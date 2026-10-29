using System;
using System.Collections.Generic;

namespace Ghostty.Core.Tabs;

/// <summary>
/// The add-once rule for a control's resource overrides
/// (deblasis/wintty#1413). A NavigationViewItem's PointerOver and Pressed
/// visual states are deferred: their setters realize on the row's first
/// pointer-over from <c>{ThemeResource}</c> references captured when the
/// row's template was applied. Replacing or removing a resource entry after
/// that frees the value such a capture points at, and the next pointer-over
/// faults inside XAML with no app frame on the stack.
///
/// So each key's value is created and added exactly once, and every later
/// write for that key updates the same instance in place. There is no
/// removal: handing a key back to the theme is an in-place update to the
/// theme's value (<see cref="Clear"/>). The dictionary is reached only
/// through the add delegate the owner passes in, which is what lets the
/// rule be tested here without a XAML thread.
/// </summary>
internal sealed class NavOverrideLedger<TValue> where TValue : class
{
    private readonly Dictionary<string, TValue> _owned = new(StringComparer.Ordinal);
    private readonly Action<string, TValue> _add;
    private readonly Func<Exception, bool> _isTransientAddFailure;
    private readonly Func<string, TValue?>? _tryGetExisting;

    /// <param name="add">Adds a new entry to the owner's dictionary. Called
    /// at most once per key that lands.</param>
    /// <param name="isTransientAddFailure">An add that throws one of these
    /// (the owner is still being built, or already tearing down) is
    /// swallowed and retried on the key's next write; anything else
    /// propagates.</param>
    /// <param name="tryGetExisting">Reads the owner's dictionary. An add can
    /// commit the entry and then throw before this ledger records it; the
    /// next write adopts that entry instead of adding over it.</param>
    public NavOverrideLedger(
        Action<string, TValue> add,
        Func<Exception, bool> isTransientAddFailure,
        Func<string, TValue?>? tryGetExisting = null)
    {
        _add = add ?? throw new ArgumentNullException(nameof(add));
        _isTransientAddFailure = isTransientAddFailure ?? throw new ArgumentNullException(nameof(isTransientAddFailure));
        _tryGetExisting = tryGetExisting;
    }

    /// <summary>
    /// Write <paramref name="key"/>: update the owned value in place when
    /// one exists, otherwise create it and add it.
    /// </summary>
    /// <returns>True when this call took the add path, whether or not the
    /// add landed. Only an add needs readers to re-read the dictionary; an
    /// in-place update reaches them by itself.</returns>
    public bool Write(string key, Func<TValue> create, Action<TValue> update)
    {
        if (_owned.TryGetValue(key, out var owned))
        {
            update(owned);
            return false;
        }

        // An earlier add may have committed and then thrown before it could
        // be recorded. Adopt that entry rather than adding over a live key:
        // the add is the replacement this class exists to prevent.
        try
        {
            if (_tryGetExisting?.Invoke(key) is { } existing)
            {
                _owned[key] = existing;
                update(existing);
                return false;
            }
        }
        catch (Exception ex) when (_isTransientAddFailure(ex))
        {
            // The dictionary is not readable while the owner is being built
            // or torn down. Report an add so the caller keeps the re-read
            // pending, and retry on the next write.
            System.Diagnostics.Debug.WriteLine(
                $"nav override read failed for {key}: {ex.GetType().Name}");
            return true;
        }

        var created = create();
        try
        {
            _add(key, created);
            _owned[key] = created;
        }
        catch (Exception ex) when (_isTransientAddFailure(ex))
        {
            // The add is retried on the key's next write. Leave a trace
            // anyway: a write that keeps failing otherwise looks like a
            // stale colour with nothing to attach a report to.
            System.Diagnostics.Debug.WriteLine(
                $"nav override add failed for {key}: {ex.GetType().Name}");
        }
        return true;
    }

    /// <summary>
    /// Hand an owned key back to the theme by painting the theme's value
    /// into the same instance. A key never written is left alone, so its
    /// readers keep resolving the theme's own resource.
    /// </summary>
    /// <returns>True when the key was owned and updated.</returns>
    public bool Clear(string key, Action<TValue> paintThemeValue)
    {
        if (!_owned.TryGetValue(key, out var owned)) return false;
        try
        {
            paintThemeValue(owned);
        }
        catch (Exception ex) when (_isTransientAddFailure(ex))
        {
            // The theme walk can hit the same teardown window the add can.
            // Leave the entry in place and let the next pass repaint it.
            System.Diagnostics.Debug.WriteLine(
                $"nav override clear failed for {key}: {ex.GetType().Name}");
        }
        return true;
    }

    public bool Owns(string key) => _owned.ContainsKey(key);
}

/// <summary>
/// Which theme dictionary sets a strip reads its own colours from: the one
/// XAML picks for an element at that theme. High Contrast follows the
/// system setting, not the element's theme. Within a set, "Default" is the
/// dark dictionary's name in XamlControlsResources, so the dark list tries
/// both.
/// </summary>
internal static class StripThemeKeys
{
    private static readonly string[] HighContrast = { "HighContrast" };
    private static readonly string[] Light = { "Light" };
    private static readonly string[] Dark = { "Dark", "Default" };

    /// <param name="actualThemeIsLight">The element's ActualTheme is Light
    /// (a non-default RequestedTheme is reflected in ActualTheme).</param>
    /// <param name="systemHighContrast">The OS has a contrast theme on.</param>
    public static IReadOnlyList<string> For(bool actualThemeIsLight, bool systemHighContrast)
        => systemHighContrast ? HighContrast : actualThemeIsLight ? Light : Dark;
}
