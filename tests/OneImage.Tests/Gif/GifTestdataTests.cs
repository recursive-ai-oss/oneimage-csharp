using ImageMagick;

namespace OneImage.Tests.Gif;

/// <summary>
/// The small GIF files in testdata/gif: odd and broken inputs the decoder must handle as named by their prefix
/// (ok-: decodes; format-: ImageFormatException; limit-: ImageLimitException). They are written by <see cref="Write"/>
/// (run with ONEIMAGE_WRITE_TESTDATA=&lt;directory&gt;) and are original, generated content.
/// </summary>
public class GifTestdataTests
{
    private static readonly byte[] Pal = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255];

    private static byte[] Ramp(int n) => Enumerable.Range(0, n).Select(i => (byte)(i % 4)).ToArray();

    public static IEnumerable<(string Name, byte[] Data)> Files()
    {
        byte[] twoFrames = GifBuilder.Build(8, 8, Pal, [new GifBuilder.Frame(0, 0, 8, 8, Ramp(64)), new GifBuilder.Frame(0, 0, 8, 8, Ramp(64).Reverse().ToArray())], loop: 0);
        yield return ("ok-missing-end-code.gif", GifBuilder.Build(10, 6, Pal, [new GifBuilder.Frame(0, 0, 10, 6, Ramp(60), EndCode: false)]));
        yield return ("ok-data-ends-early.gif", GifBuilder.Build(10, 6, Pal, [new GifBuilder.Frame(0, 0, 10, 6, Ramp(20), EndCode: false)]));
        yield return ("ok-cut-inside-second-frame.gif", twoFrames.AsSpan(0, twoFrames.Length - 12).ToArray());
        yield return ("ok-frame-outside-screen.gif", GifBuilder.Build(10, 10, Pal, [new GifBuilder.Frame(5, 6, 20, 20, Enumerable.Repeat((byte)2, 400).ToArray()), new GifBuilder.Frame(40, 40, 3, 3, new byte[9])]));
        yield return ("ok-zero-screen.gif", GifBuilder.Build(0, 0, Pal, [new GifBuilder.Frame(3, 2, 5, 4, Ramp(20))]));
        yield return ("ok-huge-frame-little-data.gif", GifBuilder.Build(16, 16, Pal, [new GifBuilder.Frame(0, 0, 65535, 65535, Ramp(300), EndCode: false)]));
        yield return ("ok-xmp-interlaced-87a.gif", With87a(GifBuilder.Build(7, 19, Pal, [new GifBuilder.Frame(0, 0, 7, 19, Enumerable.Range(0, 133).Select(i => (byte)((i / 7) % 4)).ToArray(), Interlaced: true)], xmp: "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"u8.ToArray())));
        yield return ("ok-disposal-mix-magick.gif", MagickCorpus.Write(50, 40,
        [
            new(0, 0, 50, 40, MagickCorpus.Pattern(50, 40, 10, 20, true), GifDisposeMethod.None, 3),
            new(8, 8, 30, 20, MagickCorpus.Pattern(30, 20, 11, 12, true), GifDisposeMethod.Previous, 3),
            new(15, 15, 30, 20, MagickCorpus.Pattern(30, 20, 12, 12, true), GifDisposeMethod.Background, 3),
            new(0, 0, 10, 10, MagickCorpus.Pattern(10, 10, 13, 5, false), GifDisposeMethod.None, 3),
        ], 2));
        byte[] badCode = GifBuilder.Build(4, 4, Pal, [new GifBuilder.Frame(0, 0, 4, 4, Ramp(16))]);
        badCode[Array.IndexOf(badCode, (byte)0x2C) + 12] = 0x3C;
        yield return ("format-bad-lzw-code.gif", badCode);
        yield return ("format-truncated-header.gif", twoFrames.AsSpan(0, 8).ToArray());
        yield return ("format-no-image-data.gif", twoFrames.AsSpan(0, 13 + 12).ToArray());
        yield return ("limit-huge-screen.gif", GifBuilder.Build(65535, 65535, Pal, [new GifBuilder.Frame(0, 0, 2, 2, Ramp(4))]));
        yield return ("limit-too-many-frames.gif", GifBuilder.Build(2, 2, Pal, Enumerable.Range(0, 1001).Select(_ => new GifBuilder.Frame(0, 0, 1, 1, [1]))));
    }

    private static byte[] With87a(byte[] gif)
    {
        gif[4] = (byte)'7';
        return gif;
    }

    [Fact]
    public void Write()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEIMAGE_WRITE_TESTDATA");
        Assert.SkipUnless(dir != null, "set ONEIMAGE_WRITE_TESTDATA to a directory to regenerate the files");
        Directory.CreateDirectory(dir!);
        foreach ((string name, byte[] data) in Files())
        {
            File.WriteAllBytes(Path.Combine(dir!, name), data);
        }
    }

    [Fact]
    public void CheckedInFilesMatchTheGenerator()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "testdata", "gif");
        foreach ((string name, byte[] data) in Files())
        {
            Assert.True(File.Exists(Path.Combine(dir, name)), name);
            // Magick output can differ between library builds; the hand-built files are fully deterministic.
            if (!name.Contains("magick", StringComparison.Ordinal))
            {
                Assert.Equal(data, File.ReadAllBytes(Path.Combine(dir, name)));
            }
        }
    }

    [Fact]
    public void FilesBehaveAsNamed()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "testdata", "gif");
        string[] files = Directory.GetFiles(dir, "*.gif");
        Assert.True(files.Length >= 13);
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            byte[] data = File.ReadAllBytes(file);
            Action load = () =>
            {
                Image img = Image.Load(data, new LoadOptions { Configuration = new ImageConfiguration().Add(GifFormat.Instance), Validate = true });
                for (int i = 0; i < img.FrameCount; i++)
                {
                    _ = GifTestUtil.Pixels(img, i);
                }
            };
            if (name.StartsWith("ok-", StringComparison.Ordinal))
            {
                load();
            }
            else if (name.StartsWith("format-", StringComparison.Ordinal))
            {
                Assert.Throws<ImageFormatException>(load);
            }
            else
            {
                Assert.Throws<ImageLimitException>(load);
            }
        }
    }
}
