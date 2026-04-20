using System;
using System.Collections.Generic;
using Ghostty.Core.JumpList;
using Xunit;

namespace Ghostty.Tests.JumpList;

public class JumpListBuilderTests
{
    private const string TestExe = @"C:\fake\Wintty.exe";
    private const string TestAppId = "com.deblasis.ghostty";

    [Fact]
    public void Build_sets_app_id()
    {
        var fake = new FakeCustomDestinationList();
        var builder = new JumpListBuilder(fake, () => Array.Empty<ProfileEntry>(), TestExe, TestAppId);

        builder.Build();

        Assert.Equal(TestAppId, fake.AppId);
    }

    [Fact]
    public void Build_begins_and_commits_exactly_once()
    {
        var fake = new FakeCustomDestinationList();
        var builder = new JumpListBuilder(fake, () => Array.Empty<ProfileEntry>(), TestExe, TestAppId);

        builder.Build();

        Assert.Equal(1, fake.BeginListCalls);
        Assert.Equal(1, fake.CommitCalls);
    }

    [Fact]
    public void Build_adds_static_tasks_new_window_and_new_tab()
    {
        var fake = new FakeCustomDestinationList();
        var builder = new JumpListBuilder(fake, () => Array.Empty<ProfileEntry>(), TestExe, TestAppId);

        builder.Build();

        Assert.Equal(2, fake.Tasks.Count);
        Assert.Contains(fake.Tasks, t => t.title == "New Window");
        Assert.Contains(fake.Tasks, t => t.title == "New Tab in Current Window");
        Assert.All(fake.Tasks, t => Assert.Equal(TestExe, t.exe));
    }

    [Fact]
    public void Build_with_empty_profiles_does_not_add_pinned_category()
    {
        var fake = new FakeCustomDestinationList();
        var builder = new JumpListBuilder(fake, () => Array.Empty<ProfileEntry>(), TestExe, TestAppId);

        builder.Build();

        Assert.Empty(fake.Categories);
    }

    [Fact]
    public void Build_with_nonempty_profiles_adds_pinned_category()
    {
        var fake = new FakeCustomDestinationList();
        var profiles = new List<ProfileEntry>
        {
            new("pwsh", "PowerShell", null, "pwsh.exe", null),
            new("cmd", "Command Prompt", null, "cmd.exe", null),
        };
        var builder = new JumpListBuilder(fake, () => profiles, TestExe, TestAppId);

        builder.Build();

        Assert.Single(fake.Categories);
        var (categoryName, entries) = fake.Categories[0];
        Assert.Equal("Pinned Profiles", categoryName);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.title == "PowerShell");
        Assert.Contains(entries, e => e.title == "Command Prompt");
    }
}
