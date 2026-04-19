using System;
using Ghostty.Core.Logging;
using Ghostty.Interop;

namespace Ghostty.Logging;

/// <summary>
/// Production installer for the libghostty log bridge. P/Invokes
/// <c>ghostty_log_set_callback</c> so Zig std.log output flows into the
/// process-wide <see cref="Microsoft.Extensions.Logging.ILoggerFactory"/>.
///
/// Lives in the Ghostty (WinUI) project rather than Ghostty.Core because
/// Core has no P/Invoke surface of its own and the callback target is
/// libghostty.dll, which only the shell loads. Tests substitute a fake
/// installer; see <c>LibghosttyLogBridgeTests</c>.
/// </summary>
internal sealed class LibghosttyLogInstaller : LibghosttyLogBridge.INativeInstaller
{
    public void SetCallback(IntPtr callback, IntPtr userData)
        => NativeMethods.LogSetCallback(callback, userData);
}
