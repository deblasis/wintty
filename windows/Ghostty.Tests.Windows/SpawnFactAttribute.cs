using System;
using Xunit;

namespace Ghostty.Tests.Windows;

/// <summary>
/// A Fact that skips itself when this host cannot spawn cmd.exe.
/// xunit 2.x has no dynamic skip, so the decision is made where the
/// framework does look: Skip is read off the attribute at discovery.
/// That is why SpawnProbe caches -- discovery instantiates one of these
/// per decorated test.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SpawnFactAttribute : FactAttribute
{
    public SpawnFactAttribute()
    {
        var unavailable = SpawnProbe.Cmd.Unavailable;
        if (unavailable is not null)
            Skip = unavailable;
    }
}

/// <summary>
/// SpawnFact for tests that spawn pwsh.exe. Separate because pwsh is not
/// in-box on Windows and, where it is installed, pays a runtime cold start
/// that a cmd.exe measurement does not predict: a host that clears the cmd
/// gate can still be far too slow for a test whose budget is spent on
/// pwsh starting up.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PwshFactAttribute : FactAttribute
{
    public PwshFactAttribute()
    {
        var unavailable = SpawnProbe.Pwsh.Unavailable;
        if (unavailable is not null)
            Skip = unavailable;
    }
}

/// <summary>
/// PwshFact plus a working wsl installation with at least one distro. The
/// two gates are separate observations and the reason says which one fired:
/// the wsl check spawns a process itself, so on a host where nothing spawns
/// it could not otherwise tell "wsl is not installed" from "wsl could not
/// be asked". Checking pwsh first removes that ambiguity.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WslFactAttribute : FactAttribute
{
    public WslFactAttribute()
    {
        var unavailable = SpawnProbe.Pwsh.Unavailable ?? WslDistro.Unavailable;
        if (unavailable is not null)
            Skip = unavailable;
    }
}
