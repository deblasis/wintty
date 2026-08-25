using System;
using Xunit;

namespace Ghostty.Tests.Windows;

/// <summary>
/// A Fact that skips itself when this host cannot spawn child processes.
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
        var unavailable = SpawnProbe.Unavailable;
        if (unavailable is not null)
            Skip = unavailable;
    }
}
