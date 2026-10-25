namespace OneImage.Psd.Tests;

/// <summary>The zlib trailer of ZIP-compressed composite data: wrong is corruption, missing or cut short is tolerated.</summary>
public class ZipTrailerTests
{
    private const int W = 40;
    private const int H = 30;

    private static byte[] File(out byte[] planes)
    {
        planes = Psdb.Planes8(W, H, 3, (c, x, y) => (byte)((c * 50) + (x * 3) + (y * 5) + ((x * y) % 7)));
        return Psdb.Flat(W, H, 8, 3, 3, 2, planes);
    }

    private static Rgb24[] Read(byte[] file) => Util.LoadComposite(file).ToArray<Rgb24>();

    [Fact]
    public void MissingOrCutShortTrailerIsAccepted()
    {
        // The image data section is the end of the file, so its zlib trailer is the last four bytes.
        byte[] file = File(out _);
        Rgb24[] expected = Read(file);
        for (int present = 0; present <= 3; present++)
        {
            Assert.Equal(expected, Read(file[..^(4 - present)]));
        }
    }

    [Fact]
    public void WrongTrailerIsRejected()
    {
        byte[] file = File(out _);
        for (int i = 1; i <= 4; i++)
        {
            byte[] bad = (byte[])file.Clone();
            bad[^i] ^= 0x20;
            Assert.Throws<ImageFormatException>(() => Read(bad));
        }
    }
}
