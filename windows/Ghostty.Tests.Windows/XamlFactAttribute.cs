using System;
using Xunit;

namespace Ghostty.Tests.Windows;

/// <summary>
/// A Fact that skips itself when this host cannot bring the WinUI runtime
/// up for XAML objects. xunit 2.x has no dynamic skip, so the decision is
/// made where the framework does look: Skip is read off the attribute at
/// discovery. That is why the probe decides once and caches -- discovery
/// instantiates one of these per decorated test.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class XamlFactAttribute : FactAttribute
{
    public XamlFactAttribute()
    {
        var unavailable = XamlProbe.Storyboard.Unavailable;
        if (unavailable is not null)
            Skip = unavailable;
    }
}

/// <summary>
/// XamlFact plus the composition pipeline, which is a separate facet: a
/// host whose element visuals will not come up can still run Storyboards,
/// and the reason has to say which gate fired.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class XamlCompositionFactAttribute : FactAttribute
{
    public XamlCompositionFactAttribute()
    {
        var unavailable = XamlProbe.Storyboard.Unavailable ?? XamlProbe.Composition.Unavailable;
        if (unavailable is not null)
            Skip = unavailable;
    }
}
