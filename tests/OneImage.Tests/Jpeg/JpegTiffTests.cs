using OneImage.Codecs.Jpeg;
using OneImage.Codecs.Tiff;
using OneImage.Tests.Tiff;

namespace OneImage.Tests.Jpeg;

/// <summary>JPEG-compressed TIFF files decode through the JPEG codec to the pixels libjpeg produced.</summary>
[Collection("TiffJpegSeam")]
public class JpegTiffTests
{
    public static TheoryData<string> Names() => ["jpeg_gray", "jpeg_rgb", "jpeg_cmyk"];

    [Theory]
    [MemberData(nameof(Names))]
    public void JpegInTiffMatchesLibjpeg(string name)
    {
        JpegBlockDecoder? previous = TiffJpegSeam.DecodeJpeg;
        try
        {
            TiffJpegSeam.DecodeJpeg = null;
            JpegTiffBridge.Connect();
            string dir = Path.Combine(TiffTestUtil.DataDir, "jpeg");
            byte[] file = File.ReadAllBytes(Path.Combine(dir, name + ".tif"));
            byte[] expected = File.ReadAllBytes(Path.Combine(dir, name + ".raw"));
            byte[] actual = TiffTestUtil.Decode(file);
            Assert.Equal(expected.Length, actual.Length);
            int max = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                max = Math.Max(max, Math.Abs(expected[i] - actual[i]));
            }

            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "jpeg-accuracy.txt"), $"tiff-{name} max={max}\n");
            Assert.True(max <= 1, $"max error {max}");
        }
        finally
        {
            TiffJpegSeam.DecodeJpeg = previous;
        }
    }

    [Fact]
    public void ImageLoadReadsJpegInTiffWithTheDefaultConfiguration()
    {
        string dir = Path.Combine(TiffTestUtil.DataDir, "jpeg");
        Image img = Image.Load(File.ReadAllBytes(Path.Combine(dir, "jpeg_rgb.tif")));
        Rgb24[] px = img.ToArray<Rgb24>();
        Assert.Equal(53 * 37, px.Length);
    }

    [Fact]
    public void MismatchedStreamIsAFormatError()
    {
        byte[] jpeg = JpegTestSupport.Read("base-420");
        byte[] dest = new byte[64 * 64 * 3];
        Assert.Throws<ImageFormatException>(() => JpegTiffBridge.Decode(jpeg, 64, 64, 1, false, dest));
    }
}
