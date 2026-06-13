namespace Ghostty.Core.Notifications;

/// <summary>
/// A decoded, focus-checked request to raise a single Windows toast.
/// <see cref="Title"/>/<see cref="Body"/> are display strings;
/// <see cref="SurfaceKey"/> groups toasts per surface so a newer toast
/// supersedes the older one and focus-regain can clear them.
/// </summary>
public sealed record ToastRequest(string Title, string Body, string SurfaceKey);
