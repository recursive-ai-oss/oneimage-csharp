using System.Security.Cryptography;

namespace OneImage.Tests.Tiff;

/// <summary>
/// A whole-frame read of a big TIFF decodes blocks in parallel straight into the caller's buffer. It must produce the bytes the
/// tile path produces, in the stored format and converted, at every degree.
/// </summary>
[Collection("TiffJpegSeam")]
public sealed class TiffDirectReadTests
{
    private static TiffBuilder Make(int seed, Action<TiffBuilder> configure)
    {
        // 1200x1000 is over the 1 MP threshold at which whole-frame reads bypass the tile cache.
        var b = new TiffBuilder { Width = 1200, Height = 1000 };
        configure(b);
        b.MakeSamples(seed);
        return b;
    }

    public static TheoryData<string> Layouts() => new()
    {
        "rgb-strips-lzw-predictor",
        "rgb-tiles-deflate",
        "rgba-premultiplied-tiles",
        "rgba-straight-strips",
        "gray-min-is-white-strips",
        "gray16-tiles",
        "rgb16-planar-strips",
        "rgb-planar-tiles",
        "rgb4-strips",
        "graya-premultiplied-strips",
        "rgb-bigendian-tiles-ragged",
    };

    private static byte[] Build(string layout) => layout switch
    {
        "rgb-strips-lzw-predictor" => Make(1, b => { b.Spp = 3; b.Photometric = 2; b.RowsPerStrip = 17; b.Compression = 5; b.Predictor = 2; }).Build(),
        "rgb-tiles-deflate" => Make(2, b => { b.Spp = 3; b.Photometric = 2; b.TileW = 256; b.TileH = 256; b.Compression = 8; }).Build(),
        "rgba-premultiplied-tiles" => Make(3, b => { b.Spp = 4; b.Photometric = 2; b.ExtraSamples = [1]; b.TileW = 128; b.TileH = 128; b.Compression = 5; }).Build(),
        "rgba-straight-strips" => Make(4, b => { b.Spp = 4; b.Photometric = 2; b.ExtraSamples = [2]; b.RowsPerStrip = 64; b.Compression = 32773; }).Build(),
        "gray-min-is-white-strips" => Make(5, b => { b.Spp = 1; b.Photometric = 0; b.RowsPerStrip = 100; b.Compression = 5; }).Build(),
        "gray16-tiles" => Make(6, b => { b.Spp = 1; b.Bits = 16; b.Photometric = 1; b.TileW = 192; b.TileH = 64; b.Compression = 8; b.Predictor = 2; }).Build(),
        "rgb16-planar-strips" => Make(7, b => { b.Spp = 3; b.Bits = 16; b.Photometric = 2; b.Planar = true; b.RowsPerStrip = 33; b.Compression = 5; }).Build(),
        "rgb-planar-tiles" => Make(8, b => { b.Spp = 3; b.Photometric = 2; b.Planar = true; b.TileW = 256; b.TileH = 128; b.Compression = 8; }).Build(),
        "rgb4-strips" => Make(9, b => { b.Spp = 3; b.Bits = 4; b.Photometric = 2; b.RowsPerStrip = 50; }).Build(),
        "graya-premultiplied-strips" => Make(10, b => { b.Spp = 2; b.Photometric = 1; b.ExtraSamples = [1]; b.RowsPerStrip = 40; b.Compression = 5; }).Build(),
        "rgb-bigendian-tiles-ragged" => Make(11, b => { b.Spp = 3; b.Bits = 16; b.Photometric = 2; b.BigEndian = true; b.TileW = 320; b.TileH = 192; b.Compression = 8; }).Build(),
        _ => throw new ArgumentException(layout),
    };

    /// <summary>Reads the frame as band rectangles, which never take the whole-frame path, and joins them.</summary>
    private static T[] ReferenceBands<T>(Image img)
        where T : unmanaged, IPixel<T>
    {
        int w = img.Width;
        int h = img.Height;
        var all = new T[w * h];
        for (int y = 0; y < h; y += 64)
        {
            int rows = Math.Min(64, h - y);
            img.Frames[0].CopyPixelsTo<T>(all.AsSpan(y * w, rows * w), new ImageRect(0, y, w, rows));
        }

        return all;
    }

    private static void Check<T>(byte[] file)
        where T : unmanaged, IPixel<T>
    {
        T[] want = ReferenceBands<T>(Image.Load(file));
        foreach (int degree in new[] { 1, 4, 16 })
        {
            Image img = Image.Load(file);
            var got = new T[want.Length];
            img.Frames[0].CopyPixelsTo<T>(got, new ImageRect(0, 0, img.Width, img.Height), degree);
            Assert.True(
                SHA256.HashData(OneImage.Kernels.SpanCast.AsBytes(want.AsSpan())).AsSpan().SequenceEqual(
                    SHA256.HashData(OneImage.Kernels.SpanCast.AsBytes(got.AsSpan()))),
                $"degree {degree} differs for {typeof(T).Name}");
        }
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void WholeFrameReadMatchesTilePath(string layout)
    {
        byte[] file = Build(layout);
        Check<Rgba32>(file);
        Check<Rgb24>(file);
        Check<Rgba64>(file);
        Check<Gray8>(file);
    }

    [Fact]
    public void BigStripFrameStillReadsThroughTiles()
    {
        // One strip holding the whole frame is over the per-worker budget, so the direct path declines and tiles serve the read.
        byte[] file = Make(12, b => { b.Spp = 4; b.Photometric = 2; b.ExtraSamples = [2]; b.RowsPerStrip = 1000; b.Compression = 8; }).Build();
        Check<Rgba32>(file);
    }

    [Fact]
    public void CorruptBlockStillFailsTyped()
    {
        byte[] file = Make(13, b => { b.Spp = 3; b.Photometric = 2; b.RowsPerStrip = 50; b.Compression = 8; }).Build();
        byte[] bad = (byte[])file.Clone();
        // Header and directory sit at the end or start depending on the builder; damaging the middle hits compressed strip data.
        for (int i = bad.Length / 2; i < (bad.Length / 2) + 64; i++)
        {
            bad[i] ^= 0xA5;
        }

        Image img = Image.Load(bad);
        Assert.ThrowsAny<ImageFormatException>(() => img.Frames[0].ToArray<Rgb24>(4));
    }
}
