using OneImage.Codecs.Jpx;

namespace OneImage.Tests.Jpx;

/// <summary>Reduction levels, regions, palettes, channel definitions and sYCC conversion through the internal entry point.</summary>
public class JpxApiTests
{
    [Fact]
    public void ReductionLevelsHalveTheSizeAndAreClamped()
    {
        byte[] data = JpxTestStreams.Gen("gray-3res.j2k");
        JpxInfo info = JpxCodec.Identify(data);
        Assert.Equal(64, info.Width);
        Assert.Equal(48, info.Height);
        Assert.Equal(2, info.MaxReduction);

        JpxImage full = JpxCodec.Decode(data);
        JpxImage one = JpxCodec.Decode(data, new JpxDecodeOptions { ReductionLevels = 1 });
        JpxImage two = JpxCodec.Decode(data, new JpxDecodeOptions { ReductionLevels = 2 });
        JpxImage many = JpxCodec.Decode(data, new JpxDecodeOptions { ReductionLevels = 9 });
        Assert.Equal((32, 24, 1), (one.Width, one.Height, one.Reduction));
        Assert.Equal((16, 12, 2), (two.Width, two.Height, two.Reduction));
        Assert.Equal((16, 12, 2), (many.Width, many.Height, many.Reduction));

        // The low-pass band is a smaller version of the picture: same overall brightness.
        Assert.InRange(Math.Abs(Mean(one.Planes[0]) - Mean(full.Planes[0])), 0, 2.0);
        Assert.InRange(Math.Abs(Mean(two.Planes[0]) - Mean(full.Planes[0])), 0, 3.0);
    }

    [Fact]
    public void NegativeReductionIsAnArgumentError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxCodec.Decode(JpxTestStreams.Gen("gray-3res.j2k"), new JpxDecodeOptions { ReductionLevels = -1 }));
    }

    [Theory]
    [InlineData("tiles-pcrl.j2k")]
    [InlineData("tiles-rpcl-prec.j2k")]
    [InlineData("rgb-default.j2k")]
    public void RegionEqualsTheCropOfTheWholeImage(string file)
    {
        byte[] data = JpxTestStreams.Gen(file);
        JpxImage full = JpxCodec.Decode(data);
        var rect = new ImageRect(13, 7, 41, 30);
        JpxImage part = JpxCodec.Decode(data, new JpxDecodeOptions { Region = rect });
        Assert.Equal((41, 30), (part.Width, part.Height));
        for (int c = 0; c < full.ChannelCount; c++)
        {
            for (int y = 0; y < 30; y++)
            {
                for (int x = 0; x < 41; x++)
                {
                    Assert.Equal(full.Planes[c][((y + 7) * full.Width) + x + 13], part.Planes[c][(y * 41) + x]);
                }
            }
        }
    }

    [Fact]
    public void PaletteExpandsIndexesIntoColumns()
    {
        byte[] cs = JpxTestStreams.Gen("gray-1res.j2k");
        JpxImage index = JpxCodec.Decode(cs);

        // Three 8-bit columns: one ramps up, one ramps down, the last is the index squared.
        var pclr = new List<byte> { 1, 0, 3, 7, 7, 7 };
        for (int i = 0; i < 256; i++)
        {
            pclr.Add((byte)i);
            pclr.Add((byte)(255 - i));
            pclr.Add((byte)((i * i) >> 8));
        }

        byte[] pclrBox = JpxTestStreams.Box("pclr", pclr.ToArray());
        byte[] cmapBox = JpxTestStreams.Box("cmap", [0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 2]);
        byte[] jp2 = JpxTestStreams.Jp2(cs, 64, 48, 1, 8, 16, pclrBox, cmapBox);

        JpxInfo info = JpxCodec.Identify(jp2);
        Assert.True(info.HasPalette);

        JpxImage image = JpxCodec.Decode(jp2);
        Assert.Equal(3, image.ChannelCount);
        for (int i = 0; i < image.Width * image.Height; i++)
        {
            int v = index.Planes[0][i];
            Assert.Equal(v, image.Planes[0][i]);
            Assert.Equal(255 - v, image.Planes[1][i]);
            Assert.Equal((v * v) >> 8, image.Planes[2][i]);
        }

        JpxImage raw = JpxCodec.Decode(jp2, new JpxDecodeOptions { ApplyPalette = false });
        Assert.Equal(1, raw.ChannelCount);
        Assert.Equal(index.Planes[0], raw.Planes[0]);
    }

    [Fact]
    public void ChannelDefinitionsReorderColorsAndMoveOpacityLast()
    {
        byte[] cs = JpxTestStreams.Gen("rgb-default.j2k");
        JpxImage plain = JpxCodec.Decode(cs);

        // Channel 0 is opacity, channel 1 is colour 3, channel 2 is colour 2.
        byte[] cdef = JpxTestStreams.Box("cdef", JpxTestStreams.U16(3), JpxTestStreams.U16(0), JpxTestStreams.U16(1), JpxTestStreams.U16(0), JpxTestStreams.U16(1), JpxTestStreams.U16(0), JpxTestStreams.U16(3), JpxTestStreams.U16(2), JpxTestStreams.U16(0), JpxTestStreams.U16(2));
        byte[] jp2 = JpxTestStreams.Jp2(cs, 97, 61, 3, 8, 16, cdef);
        JpxImage image = JpxCodec.Decode(jp2);
        Assert.Equal(3, image.ChannelCount);
        Assert.Equal(2, image.ColorChannelCount);
        Assert.Equal(2, image.AlphaChannel);
        Assert.False(image.AlphaPremultiplied);
        Assert.Equal(plain.Planes[2], image.Planes[0]); // colour 2 came from channel 2
        Assert.Equal(plain.Planes[1], image.Planes[1]); // colour 3 from channel 1
        Assert.Equal(plain.Planes[0], image.Planes[2]); // opacity last
    }

    [Fact]
    public void SyccIsConvertedToRgbAndCanBeLeftAlone()
    {
        byte[] cs = JpxTestStreams.Gen("rgb-default.j2k");
        byte[] jp2 = JpxTestStreams.Jp2(cs, 97, 61, 3, 8, 18);
        JpxImage raw = JpxCodec.Decode(jp2, new JpxDecodeOptions { ConvertSycc = false });
        JpxImage rgb = JpxCodec.Decode(jp2);
        Assert.Equal(JpxColorSpace.Sycc, raw.ColorSpace);
        Assert.Equal(JpxColorSpace.Srgb, rgb.ColorSpace);
        Assert.True(rgb.ConvertedFromSycc);
        Assert.False(raw.ConvertedFromSycc);

        // JFIF/T.871: R = Y + 1.402 (Cr - 128), G = Y - 0.344136 (Cb - 128) - 0.714136 (Cr - 128), B = Y + 1.772 (Cb - 128).
        for (int i = 0; i < rgb.Width * rgb.Height; i++)
        {
            double y = raw.Planes[0][i];
            double cb = raw.Planes[1][i] - 128.0;
            double cr = raw.Planes[2][i] - 128.0;
            double[] expected = [y + (1.402 * cr), y - (0.344136 * cb) - (0.714136 * cr), y + (1.772 * cb)];
            for (int c = 0; c < 3; c++)
            {
                double e = Math.Clamp(expected[c], 0, 255);
                Assert.True(Math.Abs(rgb.Planes[c][i] - e) <= 1.0, $"pixel {i} channel {c}: {rgb.Planes[c][i]} vs {e}");
            }
        }
    }

    [Fact]
    public void ToInterleavedScalesBitDepths()
    {
        JpxImage image = JpxCodec.Decode(JpxTestStreams.Gen("gray16.j2k"));
        byte[] eight = image.ToInterleaved(8);
        byte[] sixteen = image.ToInterleaved(16);
        Assert.Equal(image.Width * image.Height, eight.Length);
        Assert.Equal(image.Width * image.Height * 2, sixteen.Length);
        for (int i = 0; i < eight.Length; i++)
        {
            Assert.Equal(image.Planes[0][i] >> 8, eight[i]);
            Assert.Equal(image.Planes[0][i], (sixteen[2 * i] << 8) | sixteen[(2 * i) + 1]);
        }
    }

    private static double Mean(ushort[] plane)
    {
        long sum = 0;
        foreach (ushort v in plane)
        {
            sum += v;
        }

        return (double)sum / plane.Length;
    }
}
