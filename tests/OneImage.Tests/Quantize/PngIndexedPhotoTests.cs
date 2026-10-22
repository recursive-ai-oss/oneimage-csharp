namespace OneImage.Tests.Quantize;

public class PngIndexedPhotoTests
{
    private static byte[] ToBytes(Rgba32[] px, bool alpha)
    {
        int bpp = alpha ? 4 : 3;
        var raw = new byte[px.Length * bpp];
        for (int i = 0; i < px.Length; i++)
        {
            raw[i * bpp] = px[i].R;
            raw[(i * bpp) + 1] = px[i].G;
            raw[(i * bpp) + 2] = px[i].B;
            if (alpha)
            {
                raw[(i * bpp) + 3] = px[i].A;
            }
        }

        return raw;
    }

    private static Rgba32[] From16(ushort[] rgba16)
    {
        var px = new Rgba32[rgba16.Length / 4];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba32((byte)(rgba16[i * 4] / 257), (byte)(rgba16[(i * 4) + 1] / 257), (byte)(rgba16[(i * 4) + 2] / 257), (byte)(rgba16[(i * 4) + 3] / 257));
        }

        return px;
    }

    [Fact]
    public void PhotoSavedIndexedHasAtMost256ColorsAndMatchesTheQuantizer()
    {
        const int W = 300, H = 200;
        Rgba32[] src = Gif.GifTestUtil.SyntheticPhoto(W, H, 8);
        byte[] png = Png.PngTestUtil.Encode(ToBytes(src, alpha: false), W, H, PixelFormat.Rgb8, new PngEncoderOptions { ColorMode = PngColorMode.Indexed });
        Assert.Equal(3, Png.PngGen.Header(png).ColorType);

        Rgba32[] ours = From16(Png.PngTestUtil.Decode(png).ToRgba16());
        (int vw, int vh, ushort[] libpng) = Png.PngTestUtil.NetVipsDecode(png);
        Assert.Equal((W, H), (vw, vh));
        Rgba32[] viaLibpng = From16(libpng);
        Assert.Equal(ours, viaLibpng);
        Assert.True(ours.Distinct().Count() <= 256);

        double psnr = Gif.GifTestUtil.Psnr(src, ours);
        double reference = Gif.GifTestUtil.Psnr(src, Image.FromPixels<Rgba32>(src, W, H).Quantize(256, Dither.None).ToArray<Rgba32>());
        Assert.True(Math.Abs(psnr - reference) < 0.05, $"png {psnr:F2} dB, quantizer {reference:F2} dB");
        Assert.True(psnr > 28);
    }

    [Fact]
    public void AlphaSurvivesAsTrnsEntries()
    {
        const int W = 120, H = 90;
        Rgba32[] src = Gif.GifTestUtil.SyntheticPhoto(W, H, 9);
        for (int i = 0; i < src.Length; i++)
        {
            if (i % 5 == 0)
            {
                src[i] = new Rgba32(src[i].R, src[i].G, src[i].B, 0);
            }
            else if (i % 11 == 0)
            {
                src[i] = new Rgba32(src[i].R, src[i].G, src[i].B, 128);
            }
        }

        byte[] png = Png.PngTestUtil.Encode(ToBytes(src, alpha: true), W, H, PixelFormat.Rgba8, new PngEncoderOptions { ColorMode = PngColorMode.Indexed });
        Assert.Equal(3, Png.PngGen.Header(png).ColorType);
        Rgba32[] ours = From16(Png.PngTestUtil.Decode(png).ToRgba16());
        Rgba32[] viaLibpng = From16(Png.PngTestUtil.NetVipsDecode(png).Rgba);
        Assert.Equal(ours, viaLibpng);
        Assert.True(ours.Distinct().Count() <= 256);
        for (int i = 0; i < src.Length; i++)
        {
            if (src[i].A == 0)
            {
                Assert.Equal(0, ours[i].A);
            }
            else if (src[i].A == 255)
            {
                Assert.True(ours[i].A > 200, $"pixel {i}");
            }
        }
    }
}
