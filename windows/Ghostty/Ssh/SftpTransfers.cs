using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Notifications;
using Ghostty.Core.Ssh;

namespace Ghostty.Ssh;

/// <summary>
/// Runs file transfers to and from a pane's ssh host with the system
/// <c>scp.exe</c>, so they authenticate exactly as the user's own ssh
/// does: same keys, agent, <c>~/.ssh/config</c> and <c>known_hosts</c>.
///
/// <para>
/// A transfer first runs hidden with prompts forbidden (<c>scp -B</c>),
/// reporting through an in-window notice. When that fails because the
/// host wants something only the user can give -- a password, a key
/// passphrase, a yes to a new host key -- the same transfer is handed to
/// <paramref name="runVisible"/>, which runs it in a terminal tab where
/// they can answer.
/// </para>
/// </summary>
internal static class SftpTransfers
{
    /// <summary>
    /// Windows' own OpenSSH client, by full path so a different scp earlier
    /// on PATH (Git's, Cygwin's) is not picked up by accident; falls back
    /// to PATH when OpenSSH is installed somewhere else.
    /// </summary>
    internal static string ScpPath
    {
        get
        {
            var system = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "scp.exe");
            return File.Exists(system) ? system : "scp.exe";
        }
    }

    public static Task UploadAsync(
        SshTarget target,
        IReadOnlyList<string> localPaths,
        string remoteDirectory,
        Action<string> runVisible)
    {
        var recursive = false;
        foreach (var path in localPaths)
            if (Directory.Exists(path)) { recursive = true; break; }

        var what = localPaths.Count == 1
            ? Path.GetFileName(localPaths[0].TrimEnd('\\', '/'))
            : $"{localPaths.Count} items";
        var where = $"{target.Display}:{remoteDirectory}";
        return RunAsync(
            running: $"Uploading {what} to {where}",
            done: $"Uploaded {what} to {where}",
            failed: $"Could not upload {what}",
            batchArgs: ScpCommand.Upload(target, localPaths, remoteDirectory, recursive, batch: true),
            visibleArgs: ScpCommand.Upload(target, localPaths, remoteDirectory, recursive, batch: false),
            runVisible: runVisible,
            doneActions: Array.Empty<NoticeAction>());
    }

    public static Task DownloadAsync(
        SshTarget target,
        string remotePath,
        string localDirectory,
        Action<string> runVisible)
    {
        var name = RemotePath.FileName(remotePath);
        var from = $"{target.Display}:{remotePath}";
        return RunAsync(
            running: $"Downloading {from}",
            done: $"Downloaded {name} to {localDirectory}",
            failed: $"Could not download {name}",
            batchArgs: ScpCommand.Download(target, remotePath, localDirectory, batch: true),
            visibleArgs: ScpCommand.Download(target, remotePath, localDirectory, batch: false),
            runVisible: runVisible,
            doneActions:
            [
                new NoticeAction("Show in folder", () => ShowInFolder(localDirectory, name)),
            ]);
    }

    private static async Task RunAsync(
        string running,
        string done,
        string failed,
        IReadOnlyList<string> batchArgs,
        IReadOnlyList<string> visibleArgs,
        Action<string> runVisible,
        IReadOnlyList<NoticeAction> doneActions)
    {
        var notices = App.NotificationService;
        using var cts = new CancellationTokenSource();

        var progress = new Notice
        {
            Title = running,
            IsClosable = false,
            Actions = [new NoticeAction("Cancel", cts.Cancel)],
        };
        notices?.Show(progress);

        ScpResult result;
        try
        {
            result = await RunScpAsync(batchArgs, cts.Token);
        }
        catch (Exception ex)
        {
            result = new ScpResult(-1, ex.Message, Canceled: false);
        }
        finally
        {
            notices?.Dismiss(progress);
        }

        if (result.Canceled)
        {
            notices?.Show(new Notice
            {
                Title = "Transfer canceled",
                Message = "Files already copied were left in place.",
                AutoDismissAfter = TimeSpan.FromSeconds(6),
            });
            return;
        }

        if (result.ExitCode == 0)
        {
            notices?.Show(new Notice
            {
                Title = done,
                Severity = NoticeSeverity.Success,
                Actions = doneActions,
                AutoDismissAfter = TimeSpan.FromSeconds(8),
            });
            return;
        }

        if (ScpFailure.Classify(result.Stderr) == ScpFailureKind.NeedsInteraction)
        {
            notices?.Show(new Notice
            {
                Title = "The host asked for sign-in",
                Message = "The transfer continues in a new tab, where you can answer it.",
                AutoDismissAfter = TimeSpan.FromSeconds(8),
            });
            runVisible(ScpCommand.ToCommandLine(ScpPath, visibleArgs));
            return;
        }

        notices?.Show(new Notice
        {
            Title = failed,
            Message = ScpFailure.Summarize(result.Stderr),
            Severity = NoticeSeverity.Error,
        });
    }

    private readonly record struct ScpResult(int ExitCode, string Stderr, bool Canceled);

    private static async Task<ScpResult> RunScpAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ScpPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            // An empty stdin: with -B nothing should be read, and a prompt
            // that slips through gets EOF instead of hanging the transfer.
            RedirectStandardInput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            // Bounded: a recursive copy can complain once per file.
            if (e.Data is not null && stderr.Length < 16 * 1024)
                lock (stderr) stderr.AppendLine(e.Data);
        };
        process.OutputDataReceived += static (_, _) => { };

        process.Start();
        process.StandardInput.Close();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // scp runs ssh as a child; take the whole tree down.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new ScpResult(-1, "", Canceled: true);
        }

        string text;
        lock (stderr) text = stderr.ToString();
        return new ScpResult(process.ExitCode, text, Canceled: false);
    }

    private static void ShowInFolder(string directory, string name)
    {
        try
        {
            var path = Path.Combine(directory, name);
            var psi = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
            if (File.Exists(path) || Directory.Exists(path))
                psi.ArgumentList.Add("/select," + path);
            else
                psi.ArgumentList.Add(directory);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            // Opening Explorer is a convenience; the file is already there.
        }
    }
}
