namespace OneImage.Tests;

/// <summary>A whole-frame read of a small sequential decode goes straight into the caller's buffer.</summary>
[Collection(MemoryMeasurement.Name)]
public class DirectDecodeTests
{
    private static byte[] MakePng(int w, int h)
    {
        var px = new Rgb24[w * h];
        var rnd = new Random(3);
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgb24((byte)(i / w), (byte)(i % w), (byte)rnd.Next(8));
        }

        using var ms = new MemoryStream();
        Image.FromPixels<Rgb24>(px, w, h).Save(ms, PngFormat.Instance);
        return ms.ToArray();
    }

    [Fact]
    public void FirstAndLaterReadsAgree()
    {
        byte[] file = MakePng(333, 257);
        Image img = Image.Load(file);
        Rgba32[] first = img.ToArray<Rgba32>();
        Rgba32[] second = img.ToArray<Rgba32>();
        Rgb24[] third = img.ToArray<Rgb24>();
        Assert.Equal(first, second);
        Assert.Equal(first.Length, third.Length);
        for (int i = 0; i < first.Length; i++)
        {
            Assert.Equal(third[i].R, first[i].R);
            Assert.Equal(third[i].B, first[i].B);
            Assert.Equal(255, first[i].A);
        }

        // A region read after the whole-frame reads still works.
        var part = new Rgba32[10 * 10];
        img.CopyPixelsTo<Rgba32>(part, new ImageRect(5, 7, 10, 10));
        Assert.Equal(first[(7 * 333) + 5], part[0]);
    }

    [Fact]
    public void FirstWholeFrameReadAllocatesNoTileStorage()
    {
        byte[] file = MakePng(1200, 1000);
        var dest = new Rgba32[1200 * 1000];
        Image.Load(file).CopyPixelsTo<Rgba32>(dest, new ImageRect(0, 0, 1200, 1000));

        Image img = Image.Load(file);
        long before = GC.GetAllocatedBytesForCurrentThread();
        img.CopyPixelsTo<Rgba32>(dest, new ImageRect(0, 0, 1200, 1000));
        long used = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(used < 1_000_000, $"allocated {used} bytes for a 3.6 MB frame");
    }

    [Fact]
    public void TruncatedDataStillFailsWithTheTypedException()
    {
        byte[] file = MakePng(200, 200);
        byte[] cut = file.AsSpan(0, file.Length / 2).ToArray();
        Assert.ThrowsAny<ImageFormatException>(() => Image.Load(cut).ToArray<Rgba32>());
    }
}
