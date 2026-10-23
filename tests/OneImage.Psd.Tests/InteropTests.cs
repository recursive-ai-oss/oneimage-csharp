using ImageMagick;
using OneImage.Layers;

namespace OneImage.Psd.Tests;

/// <summary>A PSD written here is read by ImageMagick (a separate implementation, run as a black box) and its merged image is compared with ours.</summary>
public class InteropTests
{
    private static Rgba32[] MagickPixels(byte[] file, out int width, out int height)
    {
        using var img = new MagickImage(file, new MagickReadSettings { Format = MagickFormat.Psd });
        width = (int)img.Width;
        height = (int)img.Height;
        using IPixelCollection<byte> px = img.GetPixelsUnsafe();
        byte[] raw = px.ToByteArray(PixelMapping.RGBA)!;
        var result = new Rgba32[width * height];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new Rgba32(raw[i * 4], raw[i * 4 + 1], raw[i * 4 + 2], raw[i * 4 + 3]);
        }

        return result;
    }

    [Theory]
    [InlineData(PsdCompression.Rle, false)]
    [InlineData(PsdCompression.Raw, false)]
    [InlineData(PsdCompression.Rle, true)]
    public void ImageMagickReadsTheMergedImageWeWrote(PsdCompression compression, bool psb)
    {
        using PsdFile f = TestDocuments.Build(PixelFormat.Rgba8);
        f.Document.Background = new Rgba32(250, 250, 250, 255);
        byte[] file = Util.Save(f, new PsdWriteOptions { Compression = compression, Large = psb });

        Rgba32[] theirs = MagickPixels(file, out int w, out int h);
        Rgba32[] ours = f.Document.Render().ToArray<Rgba32>();
        Assert.Equal(f.Document.Width, w);
        Assert.Equal(f.Document.Height, h);
        Assert.True(ours.AsSpan().SequenceEqual(theirs), "the merged image read by ImageMagick differs from the render");
    }

    [Fact]
    public void ImageMagickAndWeReadTheStoredCompositeOfCorpusFilesAlike()
    {
        string? report = Environment.GetEnvironmentVariable("ONEIMAGE_PSD_INTEROP_REPORT");
        if (Util.CorpusDir == null || report == null)
        {
            return;
        }

        var lines = new List<string>();
        int same = 0;
        int compared = 0;
        foreach (string path in Util.CorpusFiles().Where(static p => new FileInfo(p).Length < 3_000_000))
        {
            string rel = System.IO.Path.GetRelativePath(Util.CorpusDir, path);
            try
            {
                byte[] file = File.ReadAllBytes(path);
                Rgba32[] theirs = MagickPixels(file, out int w, out int h);
                Image img = Util.LoadComposite(file);
                if (img.Width != w || img.Height != h)
                {
                    lines.Add($"{rel}\tsize");
                    continue;
                }

                Rgba32[] ours = img.ToArray<Rgba32>();
                compared++;
                long diff = 0;
                int max = 0;
                for (int i = 0; i < ours.Length; i++)
                {
                    Rgba32 a = ours[i];
                    Rgba32 b = theirs[i];
                    int d = Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A)));
                    if (d != 0)
                    {
                        diff++;
                        max = Math.Max(max, d);
                    }
                }

                if (diff == 0)
                {
                    same++;
                }
                else
                {
                    lines.Add($"{rel}\t{img.Format}\tdiffering={diff}\tmax={max}");
                }
            }
            catch (Exception ex) when (ex is ImageException or MagickException)
            {
                lines.Add($"{rel}\tskipped\t{ex.GetType().Name}");
            }
        }

        File.WriteAllText(report, $"compared {compared}, identical {same}\n" + string.Join('\n', lines));
    }
}
