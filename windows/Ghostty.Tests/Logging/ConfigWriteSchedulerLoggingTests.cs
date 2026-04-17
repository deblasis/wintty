using System;
using System.Linq;
using Ghostty.Core.Config;
using Ghostty.Core.Logging;
using Ghostty.Core.Logging.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ghostty.Tests.Logging;

public class ConfigWriteSchedulerLoggingTests
{
    [Fact]
    public void WriteBatch_ItemThatThrows_EmitsWarningWithWriteSchedulerErrEventId()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));

        var editor = new ThrowingEditor();
        var timer = new ManualTimer();
        using var scheduler = new ConfigWriteScheduler(
            editor, timer, TimeSpan.FromMilliseconds(1),
            onFlushed: () => { },
            logger: factory.CreateLogger<ConfigWriteScheduler>());

        scheduler.Schedule("vertical-tabs", "true");
        scheduler.Flush();

        var warnings = capture.Entries.Where(e => e.Level == LogLevel.Warning).ToArray();
        Assert.Contains(warnings, e => e.EventId.Id == LogEvents.Config.WriteSchedulerErr);
    }

    private sealed class ThrowingEditor : IConfigFileEditor
    {
        public string FilePath => "throwing";
        public string ReadAll() => string.Empty;
        public void SetValue(string key, string value)
            => throw new System.IO.IOException($"injected: {key}");
        public void RemoveValue(string key) { }
        public void WriteRaw(string content) { }
        public void SetRepeatableValues(string key, string[] values) { }
    }

    private sealed class ManualTimer : ISchedulerTimer
    {
        public Action? Callback { get; set; }
        public void Schedule(TimeSpan delay) { }
        public void Cancel() { }
        public void Dispose() { }
    }
}
