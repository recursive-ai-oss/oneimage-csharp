using System.Runtime.InteropServices;
using OneImage.Codecs.WebP;
using Xunit;

namespace OneImage.Tests.WebP;

/// <summary>
/// The lossy encoder takes its pixels in strips and keeps only compact per-row state; these check the pieces against the
/// whole-frame path and the decoder.
/// </summary>
public sealed class WebPLossyStreamingTests
{
    private static Rgba32[] Photo(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        var px = new Rgba32[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int v = 128 + (int)(70 * Math.Sin((x * 0.09) + (y * 0.05)));
                px[(y * w) + x] = new Rgba32((byte)Math.Clamp(v + rnd.Next(-6, 7), 0, 255), (byte)Math.Clamp(255 - v + rnd.Next(-6, 7), 0, 255), (byte)((x * 255) / w), 255);
            }
        }

        return px;
    }

    [Theory]
    [InlineData(37, 53)]
    [InlineData(16, 16)]
    [InlineData(100, 33)]
    [InlineData(1, 1)]
    [InlineData(65, 130)]
    public void StripsOfAnySizeGiveTheWholeFrameBytes(int w, int h)
    {
        Rgba32[] px = Photo(w, h, 5);
        byte[] rgba = MemoryMarshal.AsBytes<Rgba32>(px).ToArray();
        byte[] expected = VP8Encoder.Encode(rgba, w, h, 75, 4, CancellationToken.None, 2);

        // Four bytes per pixel in strips of 16 rows, then three bytes per pixel in strips of 32.
        VP8Encoder four = VP8Encoder.Create(w, h, 75, 4, CancellationToken.None, 2);
        for (int y = 0; y < h; y += 16)
        {
            int n = Math.Min(16, h - y);
            four.AddRows(rgba.AsSpan(y * w * 4, n * w * 4), 4, y, n);
        }

        Assert.True(expected.AsSpan().SequenceEqual(four.Finish()));

        byte[] rgb = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            rgb[i * 3] = rgba[i * 4];
            rgb[(i * 3) + 1] = rgba[(i * 4) + 1];
            rgb[(i * 3) + 2] = rgba[(i * 4) + 2];
        }

        VP8Encoder three = VP8Encoder.Create(w, h, 75, 4, CancellationToken.None, 1);
        for (int y = 0; y < h; y += 32)
        {
            int n = Math.Min(32, h - y);
            three.AddRows(rgb.AsSpan(y * w * 3, n * w * 3), 3, y, n);
        }

        Assert.True(expected.AsSpan().SequenceEqual(three.Finish()));
    }

    [Theory]
    [InlineData(70, 100, 64)]
    [InlineData(70, 100, 99)]
    [InlineData(48, 48, 0)]
    public void TranslucencyFirstSeenInALaterStripKeepsTheAlphaPlane(int w, int h, int firstRow)
    {
        Rgba32[] px = Photo(w, h, 9);
        for (int y = firstRow; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Rgba32 p = px[(y * w) + x];
                px[(y * w) + x] = new Rgba32(p.R, p.G, p.B, (byte)((x * 7) + (y * 3)));
            }
        }

        Image img = Image.FromPixels<Rgba32>(px, w, h);
        using var ms = new MemoryStream();
        img.Save(ms, WebPFormat.Instance, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Quality = 80 } });
        Rgba32[] back = Image.Load(ms.ToArray()).ToArray<Rgba32>();
        Assert.Equal(px.Length, back.Length);
        for (int i = 0; i < px.Length; i++)
        {
            Assert.Equal(px[i].A, back[i].A);
        }
    }

    [Fact]
    public void BoolWriterChunksRoundTripThroughTheReader()
    {
        // 1.2M bits are about 100 KB, past the first (small) chunk and the first full one, with random carries along the way.
        var rnd = new Random(11);
        int n = 1_200_000;
        int[] probs = new int[n];
        int[] bits = new int[n];
        var w = new VP8BoolWriter(100);
        for (int i = 0; i < n; i++)
        {
            probs[i] = rnd.Next(1, 256);
            bits[i] = rnd.Next(256) < probs[i] ? 0 : 1;
            w.PutBit(bits[i], probs[i]);
        }

        int length = w.Finish();
        byte[] buf = new byte[length];
        w.CopyTo(buf);
        Assert.True(length > 66_000);
        var r = default(VP8BoolReader);
        r.Init(buf, 0, buf.Length);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(bits[i], r.GetBit(probs[i]));
        }
    }
}
