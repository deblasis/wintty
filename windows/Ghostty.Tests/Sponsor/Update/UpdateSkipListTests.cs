using System;
using System.IO;
using Ghostty.Core.Sponsor.Update;
using Xunit;

namespace Ghostty.Tests.Sponsor.Update;

public class UpdateSkipListTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempFile;

    public UpdateSkipListTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "skip-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _tempFile = Path.Combine(_tempDir, "update-skips.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Empty_NothingIsSkipped()
    {
        var list = new UpdateSkipList(_tempFile);
        Assert.False(list.IsSkipped("1.4.2"));
    }

    [Fact]
    public void Skip_PersistsAcrossInstances()
    {
        var a = new UpdateSkipList(_tempFile);
        a.Skip("1.4.2");

        var b = new UpdateSkipList(_tempFile);
        Assert.True(b.IsSkipped("1.4.2"));
    }

    [Fact]
    public void NewerVersion_NotSkipped_EvenIfOlderWasSkipped()
    {
        var list = new UpdateSkipList(_tempFile);
        list.Skip("1.4.2");
        Assert.False(list.IsSkipped("1.4.3"));
        Assert.False(list.IsSkipped("2.0.0"));
    }

    [Fact]
    public void CorruptedFile_LogsAndTreatsAsEmpty()
    {
        File.WriteAllText(_tempFile, "{not valid json");
        var list = new UpdateSkipList(_tempFile);
        Assert.False(list.IsSkipped("1.4.2"));
    }

    [Fact]
    public void Skip_IdempotentForSameVersion()
    {
        var list = new UpdateSkipList(_tempFile);
        list.Skip("1.4.2");
        list.Skip("1.4.2");
        Assert.True(list.IsSkipped("1.4.2"));
    }
}
