using Xunit;

namespace Ghostty.IconGen.Tests;

public class IcoWriterTests
{
    [Fact]
    public void WritesIcoFileWithExpectedFrameSizes()
    {
        using var tempDir = new TempDir();
        using var masters = MasterRasters.Load(TempDir.FindRepoRoot());

        var icoPath = Path.Combine(tempDir.Path, "ghostty.ico");
        IcoWriter.Write(masters, icoPath);

        Assert.True(File.Exists(icoPath));

        var frameSizes = IcoReader.ReadFrameSizes(icoPath).ToHashSet();
        Assert.Contains(16, frameSizes);
        Assert.Contains(32, frameSizes);
        Assert.Contains(48, frameSizes);
        Assert.Contains(256, frameSizes);
    }
}

// Minimal ICO reader for tests. Parses the ICONDIR header and enumerates
// frame widths.
internal static class IcoReader
{
    public static IEnumerable<int> ReadFrameSizes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 6) yield break;
        ushort type = BitConverter.ToUInt16(bytes, 2);
        ushort count = BitConverter.ToUInt16(bytes, 4);
        if (type != 1) yield break;

        for (int i = 0; i < count; i++)
        {
            int offset = 6 + i * 16;
            if (offset + 16 > bytes.Length) break;
            byte w = bytes[offset];
            yield return w == 0 ? 256 : w;
        }
    }
}
