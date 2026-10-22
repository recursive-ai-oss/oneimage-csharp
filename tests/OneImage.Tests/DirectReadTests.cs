namespace OneImage.Tests;

/// <summary>Whole-frame reads of big (streaming) decodes go straight into the caller's buffer, optionally through a color conversion.</summary>
[Collection(MemoryMeasurement.Name)]
public sealed class DirectReadTests : IDisposable
{
    private readonly long _savedThreshold = DecodeSourceNode.StreamingMinBytes;

    public void Dispose() => DecodeSourceNode.StreamingMinBytes = _savedThreshold;

    private static byte[] CmykJpeg(int w, int h)
    {
        var rnd = new Random(5);
        var px = new Cmyk32[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            int x = i % w;
            int y = i / w;
            px[i] = new Cmyk32((byte)((x * 200 / w) + rnd.Next(8)), (byte)((y * 200 / h) + rnd.Next(8)), (byte)rnd.Next(60), (byte)((x + y) % 90));
        }

        using var ms = new MemoryStream();
        Image.FromPixels<Cmyk32>(px, w, h).Save(ms, JpegFormat.Instance, new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 90 } });
        return ms.ToArray();
    }

    [Fact]
    public void StreamingFrameReadsStraightIntoTheBufferWithoutADecoderThread()
    {
        byte[] jpeg = CmykJpeg(700, 300);
        DecodeSourceNode.StreamingMinBytes = 0;
        Image img = Image.Load(jpeg);
        Assert.True(img.Frames[0].Node.Streaming);
        int threads = StreamingSession.LiveThreads;

        Cmyk32[] direct = img.ToArray<Cmyk32>();
        Assert.True(StreamingSession.LiveThreads <= threads);

        // A region read goes through the window and must agree with the direct whole-frame read.
        var part = new Cmyk32[100 * 50];
        img.Frames[0].CopyPixelsTo<Cmyk32>(part, new ImageRect(20, 30, 100, 50));
        for (int y = 0; y < 50; y++)
        {
            Assert.True(direct.AsSpan(((30 + y) * 700) + 20, 100).SequenceEqual(part.AsSpan(y * 100, 100)));
        }

        // Reading again decodes again and gives the same pixels.
        Assert.Equal(direct, img.ToArray<Cmyk32>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ColorConvertOverAStreamingDecodeMatchesTheTilePath(int degree)
    {
        byte[] jpeg = CmykJpeg(3000, 150);
        DecodeSourceNode.StreamingMinBytes = 0;
        Cmyk32[] decoded = Image.Load(jpeg).ToArray<Cmyk32>();

        Rgb24[] expected = Image.FromPixels<Cmyk32>(decoded, 3000, 150).ConvertColor(ColorProfile.Srgb).ToArray<Rgb24>();
        Rgb24[] direct = Image.Load(jpeg).ConvertColor(ColorProfile.Srgb).ToArray<Rgb24>(maxDegree: degree);
        Assert.Equal(expected, direct);

        // A destination other than the converted format goes through the format kernel.
        Rgba32[] direct32 = Image.Load(jpeg).ConvertColor(ColorProfile.Srgb).ToArray<Rgba32>(maxDegree: degree);
        Rgba32[] expected32 = Image.FromPixels<Cmyk32>(decoded, 3000, 150).ConvertColor(ColorProfile.Srgb).ToArray<Rgba32>();
        Assert.Equal(expected32, direct32);
    }

    [Fact]
    public void TruncatedDataFailsTypedThroughTheConvertingPath()
    {
        byte[] jpeg = CmykJpeg(3000, 150);
        DecodeSourceNode.StreamingMinBytes = 0;
        Image img = Image.Load(jpeg.AsMemory(0, jpeg.Length / 2)).ConvertColor(ColorProfile.Srgb);
        Assert.Throws<ImageFormatException>(() => img.ToArray<Rgb24>(maxDegree: 4));
    }
}
