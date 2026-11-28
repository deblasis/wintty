using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Ghostty.Core.Profiles.Tracking;
using Ghostty.Core.Ssh;
using Ghostty.Interop;
using Ghostty.Ssh;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace Ghostty.Controls;

/// <summary>
/// File transfer for a pane that is inside an ssh session: files dropped
/// on it are uploaded to the host, and a file name on screen can be
/// downloaded from the pane menu. A pane that is not in ssh treats a drop
/// the way terminals usually do and types the paths.
/// </summary>
public sealed partial class TerminalControl
{
    /// <summary>What the pane menu can download: from where, and which path.</summary>
    internal readonly record struct SftpDownloadOffer(SshTarget Target, string RemotePath, bool DirectoryKnown);

    // Resolved when a drag enters, because DragOver fires continuously and
    // answering "is this pane in ssh" walks the process list.
    private SshTarget? _dragSshTarget;

    /// <summary>
    /// The ssh session this pane's shell is running, or null. A process
    /// lookup: call it on a user action, not on a timer.
    /// </summary>
    internal SshTarget? TryGetSshTarget()
        => TryGetShellPid() is int pid ? SshPaneProbe.Find(pid) : null;

    /// <summary>
    /// The remote folder the shell's title names, or null. A guess, shown
    /// for confirmation before any transfer uses it.
    /// </summary>
    internal string? RemoteDirectoryGuess => RemotePath.DirectoryFromTitle(_shellTitle);

    /// <summary>
    /// What a right-click here could download: the selection when there
    /// is a one-line one, else the word under the pointer. Null when the
    /// pane is not in ssh or nothing usable is there. Read at the moment
    /// of the click, before the pointer moves onto the menu.
    /// </summary>
    internal SftpDownloadOffer? TryGetSftpDownloadOffer(bool fromPointer)
    {
        if (_surfaceDisposed || _surface.Handle == IntPtr.Zero) return null;

        string? word = null;
        if (NativeMethods.SurfaceReadSelection(_surface) is { } selection)
        {
            var text = selection.Text.Trim();
            if (text.Length > 0 && text.IndexOfAny(['\r', '\n']) < 0) word = text;
        }
        else if (fromPointer)
        {
            word = NativeMethods.SurfaceReadWordAtPointer(_surface);
        }

        if (RemotePath.CandidateFromWord(word) is not { } candidate) return null;
        if (TryGetSshTarget() is not { } target) return null;

        var directory = RemoteDirectoryGuess;
        var known = directory is not null || candidate[0] is '/' or '~';
        return new SftpDownloadOffer(target, RemotePath.Combine(directory, candidate), known);
    }

    /// <summary>Confirm, then download what <paramref name="offer"/> names.</summary>
    internal async Task DownloadViaSftpAsync(SftpDownloadOffer offer)
    {
        if (!App.WindowsByRoot.TryGetValue(XamlRoot, out var window)) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        var answer = await SftpDialogs.AskDownloadAsync(
            XamlRoot, hwnd, offer.Target, offer.RemotePath, offer.DirectoryKnown);
        if (answer is not { } a) return;

        await SftpTransfers.DownloadAsync(offer.Target, a.RemotePath, a.LocalDirectory, window.OpenCommandTab);
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        _dragSshTarget = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? TryGetSshTarget()
            : null;
        OnDragOver(sender, e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is { } ui)
        {
            ui.Caption = _dragSshTarget is { } target
                ? $"Upload to {target.Display}"
                : "Insert path";
            ui.IsCaptionVisible = true;
            ui.IsGlyphVisible = true;
        }
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => _dragSshTarget = null;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        // async void: an event handler. Everything is contained so a
        // failed drop cannot take the UI thread down.
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            e.Handled = true;

            var deferral = e.GetDeferral();
            List<string> paths;
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                paths = new List<string>(items.Count);
                foreach (var item in items)
                    if (!string.IsNullOrEmpty(item.Path)) paths.Add(item.Path);
            }
            finally
            {
                deferral.Complete();
            }
            if (paths.Count == 0) return;

            // Ask again rather than trust the drag-enter answer: the ssh
            // session may have ended while the drag was in the air.
            _dragSshTarget = null;
            Focus(FocusState.Programmatic);
            if (TryGetSshTarget() is not { } target)
            {
                InsertDroppedPaths(paths);
                return;
            }

            // When the title names the shell's folder, upload straight
            // there: the banner says where the files went. Only ask when
            // the folder could not be found.
            var directory = RemoteDirectoryGuess
                ?? await SftpDialogs.AskUploadAsync(XamlRoot, target, paths, guessedDirectory: null);
            if (directory is null) return;
            if (!App.WindowsByRoot.TryGetValue(XamlRoot, out var window)) return;

            await SftpTransfers.UploadAsync(target, paths, directory, window.OpenCommandTab);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TerminalControl] drop failed: {ex}");
        }
    }

    // Outside ssh, a dropped file becomes its path at the prompt, quoted
    // when it has to be -- what Windows Terminal and conhost do.
    private void InsertDroppedPaths(IReadOnlyList<string> paths)
    {
        var sb = new StringBuilder();
        foreach (var path in paths)
        {
            // A path cannot contain a quote on Windows, so wrapping is
            // enough; control characters cannot appear either. Anything
            // beyond plain path characters is quoted: a file can be named
            // "a&b" or "a;b", and a shell would read those as syntax.
            if (sb.Length > 0) sb.Append(' ');
            if (NeedsQuoting(path)) sb.Append('"').Append(path).Append('"');
            else sb.Append(path);
        }
        if (_surfaceDisposed || _surface.Handle == IntPtr.Zero || sb.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        unsafe
        {
            fixed (byte* p = bytes)
                NativeMethods.SurfaceText(_surface, (IntPtr)p, (UIntPtr)bytes.Length);
        }
    }

    private static bool NeedsQuoting(string path)
    {
        foreach (var c in path)
        {
            if (!(char.IsLetterOrDigit(c) || c is '\\' or '/' or ':' or '.' or '-' or '_'))
                return true;
        }
        return false;
    }
}
