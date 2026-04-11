using System.Drawing;
using System.Drawing.Imaging;

namespace Ghostty.IconGen;

internal static class IcoWriter
{
    // Standard Windows icon sizes for an app .ico. Covers taskbar (16,
    // 20, 24, 32, 40, 48), larger icon views (64), and full-size (256).
    private static readonly int[] FrameSizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    public static void Write(MasterRasters masters, string outPath)
    {
        var frames = new List<(int Px, byte[] PngBytes)>();
        foreach (var px in FrameSizes)
        {
            using var resized = PngWriter.Resize(masters, px);
            using var ms = new MemoryStream();
            resized.Save(ms, ImageFormat.Png);
            frames.Add((px, ms.ToArray()));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using var fs = File.Create(outPath);
        using var bw = new BinaryWriter(fs);

        bw.Write((ushort)0);              // reserved
        bw.Write((ushort)1);              // type: icon
        bw.Write((ushort)frames.Count);   // count

        int dataOffset = 6 + frames.Count * 16;
        foreach (var (px, png) in frames)
        {
            bw.Write((byte)(px == 256 ? 0 : px)); // width (0 means 256)
            bw.Write((byte)(px == 256 ? 0 : px)); // height
            bw.Write((byte)0);                    // color palette
            bw.Write((byte)0);                    // reserved
            bw.Write((ushort)1);                  // color planes
            bw.Write((ushort)32);                 // bits per pixel
            bw.Write((uint)png.Length);           // image size
            bw.Write((uint)dataOffset);           // image offset
            dataOffset += png.Length;
        }

        foreach (var (_, png) in frames)
            bw.Write(png);
    }
}
