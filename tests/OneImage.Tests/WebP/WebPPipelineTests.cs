using System.Runtime.InteropServices;
using Xunit;

namespace OneImage.Tests.WebP;

/// <summary>
/// Frames of 2^17 pixels or more decode with the inverse transforms (lossless) or the bitstream parsing (lossy) on a second
/// thread. The pixels must be exactly what a round trip promises, and failures on either thread must surface as typed errors.
/// </summary>
public sealed class WebPPipelineTests
{
    private const int W = 640;
    private const int H = 400;
    private static readonly WebPFormat Format = new();

    private static byte[] Photo()
    {
        var rnd = new Random(5);
        byte[] rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int o = ((y * W) + x) * 4;
                int baseV = (int)(128 + (60 * Math.Sin(x / 23.0)) + (50 * Math.Cos(y / 17.0)));
                rgba[o] = (byte)Math.Clamp(baseV + rnd.Next(-6, 7), 0, 255);
                rgba[o + 1] = (byte)Math.Clamp(baseV / 2 + (x / 8) + rnd.Next(-4, 5), 0, 255);
                rgba[o + 2] = (byte)Math.Clamp(255 - baseV + rnd.Next(-5, 6), 0, 255);
                rgba[o + 3] = 255;
            }
        }

        return rgba;
    }

    private static byte[] Encode(byte[] rgba, bool lossless)
    {
        Image img = Image.FromPixels<Rgba32>(MemoryMarshal.Cast<byte, Rgba32>(rgba), W, H);
        using var ms = new MemoryStream();
        img.Save(ms, Format, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = lossless } });
        return ms.ToArray();
    }

    [Fact]
    public void LargeLosslessFrameRoundTripsExactly()
    {
        byte[] rgba = Photo();
        byte[] file = Encode(rgba, lossless: true);
        Rgba32[] got = Image.Load(file).ToArray<Rgba32>();
        Assert.True(MemoryMarshal.AsBytes<Rgba32>(got).SequenceEqual(rgba));
    }

    [Fact]
    public void LargeFramesDecodeTheSameEveryTime()
    {
        byte[] rgba = Photo();
        foreach (bool lossless in new[] { false, true })
        {
            byte[] file = Encode(rgba, lossless);
            byte[] first = MemoryMarshal.AsBytes<Rgba32>(Image.Load(file).ToArray<Rgba32>()).ToArray();
            for (int i = 0; i < 5; i++)
            {
                Assert.True(MemoryMarshal.AsBytes<Rgba32>(Image.Load(file).ToArray<Rgba32>()).SequenceEqual(first), $"lossless {lossless} run {i}");
            }
        }
    }

    [Fact]
    public void TruncatedLargeFramesFailTyped()
    {
        byte[] rgba = Photo();
        foreach (bool lossless in new[] { false, true })
        {
            byte[] file = Encode(rgba, lossless);
            foreach (double keep in new[] { 0.3, 0.6, 0.95 })
            {
                byte[] cut = file.AsSpan(0, (int)(file.Length * keep)).ToArray();
                // The RIFF size no longer matches; the decoder must still end in a typed error or a clean decode, never hang.
                Exception? ex = Record.Exception(() => Image.Load(cut).ToArray<Rgba32>());
                Assert.True(ex is null or ImageFormatException, $"lossless {lossless} keep {keep}: {ex}");
            }
        }
    }

    [Fact]
    public void CorruptedLargeFramesNeverHangOrLeakUntypedErrors()
    {
        byte[] rgba = Photo();
        var rnd = new Random(99);
        foreach (bool lossless in new[] { false, true })
        {
            byte[] file = Encode(rgba, lossless);
            for (int i = 0; i < 40; i++)
            {
                byte[] bad = file.ToArray();
                // Damage the entropy-coded part (past the headers) in a few places.
                for (int k = 0; k < 3; k++)
                {
                    bad[rnd.Next(64, bad.Length)] ^= (byte)(1 << rnd.Next(8));
                }

                Exception? ex = Record.Exception(() => Image.Load(bad).ToArray<Rgba32>());
                Assert.True(ex is null or ImageFormatException or ImageLimitException, $"lossless {lossless} case {i}: {ex}");
            }
        }
    }

    [Fact]
    public void CancellationStopsALargeDecode()
    {
        byte[] file = Encode(Photo(), lossless: true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Image img = Image.Load(file);
        Assert.ThrowsAny<OperationCanceledException>(() => img.ToArray<Rgba32>(cancellationToken: cts.Token));
    }
}
