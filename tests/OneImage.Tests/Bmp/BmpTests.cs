using System.Buffers.Binary;
using ImageMagick;
using OneImage.Tests.Png;

namespace OneImage.Tests.Bmp;

public class BmpTests
{
    private sealed record Case(string Name, byte[] File, int ExpectBpp, int ExpectCompression);

    private static IEnumerable<Case> MagickCorpus()
    {
        const int W = 37, H = 23;
        byte[] two = BmpTestUtil.Colorful(W, H, 2, false, 1);
        byte[] sixteen = BmpTestUtil.Colorful(W, H, 13, false, 2);
        byte[] many = BmpTestUtil.Colorful(W, H, 200, false, 3);
        byte[] noise = PngNoiseRgba(W, H, 4, opaque: true);
        byte[] alpha = PngNoiseRgba(W, H, 5, opaque: false);

        yield return new("magick-1bpp", BmpTestUtil.MagickWrite(two, W, H, MagickFormat.Bmp3, i => { i.Quantize(new QuantizeSettings { Colors = 2, DitherMethod = DitherMethod.No }); i.ColorType = ColorType.Bilevel; }), 1, 0);
        yield return new("magick-4bpp", BmpTestUtil.MagickWrite(sixteen, W, H, MagickFormat.Bmp3, i => { i.Quantize(new QuantizeSettings { Colors = 16, DitherMethod = DitherMethod.No }); i.ColorType = ColorType.Palette; }), 4, 0);
        yield return new("magick-8bpp", BmpTestUtil.MagickWrite(many, W, H, MagickFormat.Bmp3, i => { i.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.No }); i.ColorType = ColorType.Palette; i.Settings.Compression = CompressionMethod.NoCompression; }), 8, 0);
        yield return new("magick-8bpp-rle", BmpTestUtil.MagickWrite(many, W, H, MagickFormat.Bmp3, i =>
        {
            i.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.No });
            i.ColorType = ColorType.Palette;
            i.Settings.Compression = CompressionMethod.RLE;
        }), 8, 1);
        yield return new("magick-24bpp", BmpTestUtil.MagickWrite(noise, W, H, MagickFormat.Bmp3), 24, 0);
        yield return new("magick-24bpp-bmp2", BmpTestUtil.MagickWrite(noise, W, H, MagickFormat.Bmp2), 24, 0);
        yield return new("magick-32bpp-alpha", BmpTestUtil.MagickWrite(alpha, W, H, MagickFormat.Bmp), 32, -1);
    }

    private static byte[] PngNoiseRgba(int w, int h, int seed, bool opaque)
    {
        byte[] b = new byte[w * h * 4];
        new Random(seed).NextBytes(b);
        if (opaque)
        {
            for (int i = 3; i < b.Length; i += 4)
            {
                b[i] = 255;
            }
        }

        return b;
    }

    public static IEnumerable<TheoryDataRow<string>> CorpusNames() => MagickCorpus().Select(c => new TheoryDataRow<string>(c.Name));

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void MagickWrittenFilesDecodeLikeMagick(string name)
    {
        Case c = MagickCorpus().First(x => x.Name == name);
        bool core = BinaryPrimitives.ReadUInt32LittleEndian(c.File.AsSpan(14)) == 12;
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian(c.File.AsSpan(core ? 24 : 28));
        int comp = core ? 0 : BinaryPrimitives.ReadInt32LittleEndian(c.File.AsSpan(30));
        TestContext.Current.SendDiagnosticMessage($"{name}: header {BinaryPrimitives.ReadUInt32LittleEndian(c.File.AsSpan(14))}, {bpp} bpp, compression {comp}, {c.File.Length} bytes");
        Assert.Equal(c.ExpectBpp, bpp);
        if (c.ExpectCompression >= 0)
        {
            Assert.Equal(c.ExpectCompression, comp);
        }

        Decoded ours = BmpTestUtil.DecodeBmp(c.File);
        byte[] reference = BmpTestUtil.MagickRgba(c.File, out int w, out int h);
        Assert.Equal((w, h), (ours.Width, ours.Height));
        Assert.Equal(reference, BmpTestUtil.ToRgba8(ours));

        string? dir = Environment.GetEnvironmentVariable("ONEIMAGE_WRITE_CORPUS");
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name + ".bmp"), c.File);
        }
    }

    [Fact]
    public void CommittedCorpusDecodesLikeMagick()
    {
        string dir = PngTestUtil.DataDir("bmp");
        string[] files = Directory.GetFiles(dir, "*.bmp");
        Assert.NotEmpty(files);
        foreach (string path in files)
        {
            byte[] file = File.ReadAllBytes(path);
            Assert.Equal(BmpTestUtil.MagickRgba(file, out _, out _), BmpTestUtil.ToRgba8(BmpTestUtil.DecodeBmp(file)));
        }
    }
}
