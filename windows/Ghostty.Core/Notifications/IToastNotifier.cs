namespace Ghostty.Core.Notifications;

/// <summary>
/// Shell-side sink that turns a <see cref="ToastRequest"/> into an actual OS
/// toast. Declared in Core so <c>GhosttyHost</c> depends on the abstraction
/// while the WinAppSDK implementation stays in the shell project. All
/// implementations must swallow their own failures -- a toast error must
/// never propagate back through the libghostty action callback.
/// </summary>
public interface IToastNotifier
{
    /// <summary>Raise (or replace) the toast described by <paramref name="request"/>.</summary>
    void Show(ToastRequest request);

    /// <summary>
    /// Remove any outstanding toast for a surface. Called when the surface
    /// regains focus so a stale notification disappears.
    /// </summary>
    void ClearForSurface(string surfaceKey);
}
