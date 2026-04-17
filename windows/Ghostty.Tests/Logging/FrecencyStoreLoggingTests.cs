using System.Linq;
using Ghostty.Commands;
using Ghostty.Core.Logging;
using Ghostty.Core.Logging.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ghostty.Tests.Logging;

public class FrecencyStoreLoggingTests
{
    [Fact]
    public void FromJson_WithMalformedJson_EmitsWarningWithParseFailedEventId()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));
        using var scope = CoreStaticLoggers.Install(factory);

        FrecencyStore.FromJson("this is not json");

        var warnings = capture.Entries.Where(e => e.Level == LogLevel.Warning).ToArray();
        Assert.Contains(warnings, e => e.EventId.Id == LogEvents.Frecency.ParseFailed);
    }
}
