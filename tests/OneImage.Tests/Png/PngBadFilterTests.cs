namespace OneImage.Tests.Png;

/// <summary>
/// A scanline whose filter byte is not 0..4 (what a half-written or zero/garbage-filled file can produce) is a format error on
/// every decode path: whole reads, region reads, the pipelined inflate (1 MiB and more) and the streaming window (16 MiB and more),
/// from memory and from a file.
/// </summary>
[Collection(MemoryMeasurement.Name)]
public class PngBadFilterTests
{
    public static TheoryData<int, int> Sizes => new()
    {
        { 64, 48 },
        { 1200, 1000 },
        { 4200, 4000 },
    };

    private static byte[] Png(int w, int h, int badRow, byte filter)
    {
        int stride = w + 1;
        byte[] raw = new byte[(long)stride * h];
        for (int y = 0; y < h; y++)
        {
            raw[y * stride] = 0;
            for (int x = 0; x < w; x++)
            {
                raw[(y * stride) + 1 + x] = (byte)((x * 7) + (y * 3));
            }
        }

        raw[badRow * stride] = filter;
        return MiniPng.Plain(w, h, 8, 0, raw);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void BadFilterByteIsAFormatErrorOnEveryReadPath(int w, int h)
    {
        foreach (byte filter in new byte[] { 5, 255 })
        {
            foreach (int badRow in new[] { 0, h / 2, h - 1 })
            {
                byte[] file = Png(w, h, badRow, filter);
                string path = Path.Combine(Path.GetTempPath(), $"badfilter-{Guid.NewGuid():N}.png");
                File.WriteAllBytes(path, file);
                try
                {
                    foreach (Func<Image> load in new Func<Image>[] { () => Image.Load(file), () => Image.Load(path) })
                    {
                        Assert.ThrowsAny<ImageFormatException>(() => load().ToArray<Gray8>());
                        Assert.ThrowsAny<ImageFormatException>(() => load().ToArray<Rgba32>());
                        Assert.ThrowsAny<ImageFormatException>(() =>
                        {
                            Image img = load();
                            var band = new Gray8[w * (h / 4)];
                            img.CopyPixelsTo<Gray8>(band, new ImageRect(0, h - (h / 4), w, h / 4));
                            img.CopyPixelsTo<Gray8>(band, new ImageRect(0, h - (h / 4), w, h / 4));
                        });
                        Assert.ThrowsAny<ImageFormatException>(() => Image.Load(file, new LoadOptions { Validate = true }));
                    }
                }
                finally
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public void ExceptionIsNotAnArgumentException()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Image.Load(Png(64, 48, 10, 255)).ToArray<Gray8>());
        Assert.IsType<ImageFormatException>(ex);
    }
}
