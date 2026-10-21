namespace OneImage.Tests.Png;

/// <summary>Decodes every PngSuite image and compares with libpng (through libvips); the x* files must be rejected.</summary>
public class PngSuiteTests
{
    private static readonly string Dir = PngTestUtil.DataDir("png", "pngsuite");

    public static IEnumerable<TheoryDataRow<string>> ValidFiles() =>
        Directory.GetFiles(Dir, "*.png").Select(Path.GetFileName).Where(n => !n!.StartsWith('x')).Order().Select(n => new TheoryDataRow<string>(n!));

    public static IEnumerable<TheoryDataRow<string>> CorruptFiles() =>
        Directory.GetFiles(Dir, "x*.png").Select(Path.GetFileName).Order().Select(n => new TheoryDataRow<string>(n!));

    [Theory]
    [MemberData(nameof(ValidFiles))]
    public void DecodesBitExactAgainstLibpng(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Dir, name));
        Decoded ours = PngTestUtil.Decode(file);
        (int w, int h, ushort[] reference) = PngTestUtil.NetVipsDecode(file);
        Assert.Equal(w, ours.Width);
        Assert.Equal(h, ours.Height);
        Assert.Equal(reference, ours.ToRgba16());
    }

    [Theory]
    [MemberData(nameof(CorruptFiles))]
    public void CorruptFilesAreRejected(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Dir, name));
        Assert.ThrowsAny<ImageFormatException>(() => PngTestUtil.Decode(file));
    }
}
