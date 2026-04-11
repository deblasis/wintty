using Xunit;

namespace Ghostty.IconGen.Tests;

public class SmokeTest
{
    [Fact]
    public void ProgramRunWithValidArgsReturnsZero()
    {
        using var tempDir = new TempDir();
        var repoRoot = TempDir.FindRepoRoot();

        var exitCode = Program.Run(
            new[] { "--channel", "stable", "--out", tempDir.Path },
            repoRoot);

        Assert.Equal(0, exitCode);
    }
}
