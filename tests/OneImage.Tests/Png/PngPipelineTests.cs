namespace OneImage.Tests.Png;

/// <summary>Images over 1 MiB of scanlines inflate on a second thread; results and errors must match the single-thread path.</summary>
public class PngPipelineTests
{
    private const int W = 1100;
    private const int H = 900;

    private static (byte[] File, byte[] Pixels) Big(PngEncoderOptions? options = null)
    {
        byte[] pixels = PngGen.Photo(W, H, PixelFormat.Rgb8, 7);
        return (PngTestUtil.Encode(pixels, W, H, PixelFormat.Rgb8, options), pixels);
    }

    [Theory]
    [InlineData(PngFilterMode.Adaptive)]
    [InlineData(PngFilterMode.Paeth)]
    [InlineData(PngFilterMode.Sub)]
    [InlineData(PngFilterMode.Average)]
    public void LargeImagesDecodeBitExact(PngFilterMode filter)
    {
        (byte[] file, byte[] pixels) = Big(new PngEncoderOptions { Filter = filter });
        Assert.Equal(pixels, PngTestUtil.Decode(file).Data);
    }

    [Fact]
    public void TruncatedAndCorruptLargeImagesFailWithFormatErrors()
    {
        (byte[] file, _) = Big();
        int idatEnd = PngGen.Chunks(file).Where(c => c.Name == "IDAT").Max(c => c.Data + c.Length + 4);
        foreach (int len in new[] { 100, idatEnd / 8, idatEnd / 2, idatEnd - 20 })
        {
            Assert.ThrowsAny<ImageFormatException>(() => PngTestUtil.Decode(file[..len]));
        }

        // Damage in the middle of the compressed data surfaces as a typed error, never as another exception type.
        var rnd = new Random(9);
        var idat = PngGen.Chunks(file).Where(c => c.Name == "IDAT").ToList();
        for (int round = 0; round < 20; round++)
        {
            byte[] bad = (byte[])file.Clone();
            var c = idat[rnd.Next(idat.Count)];
            for (int k = 0; k < 4; k++)
            {
                bad[c.Data + rnd.Next(c.Length)] ^= (byte)(1 + rnd.Next(255));
            }

            try
            {
                PngTestUtil.Decode(bad);
            }
            catch (ImageException)
            {
            }
        }
    }
}
