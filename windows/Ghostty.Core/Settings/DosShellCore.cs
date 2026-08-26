using System;

namespace Ghostty.Core.Settings;

/// <summary>
/// Stub: implemented after the tests in Ghostty.Tests.Settings.DosShellCoreTests.
/// </summary>
internal sealed class DosShellCore
{
    public DosShellCore(Func<DateTime>? clock = null) => throw new NotImplementedException();

    public string Boot() => throw new NotImplementedException();

    public string NewPrompt() => throw new NotImplementedException();

    public string SendChar(char ch) => throw new NotImplementedException();

    public string SendKey(DosShellKey key) => throw new NotImplementedException();
}
