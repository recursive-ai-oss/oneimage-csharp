using System.Diagnostics;

namespace OneImage.Tests.Gif;

/// <summary>Palette sharing across animation frames, and memory behavior of GIF decode and encode over many files.</summary>
[Collection(MemoryMeasurement.Name)]
public class GifAnimationTests
{
    private static Rgba32[] GradientFrame(int w, int h, int step, bool sprite = true, bool sameColors = false)
    {
        // A smooth background (far more than 256 colors) with a small sprite that moves; later frames differ only around the sprite.
        var px = new Rgba32[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                px[(y * w) + x] = new Rgba32((byte)((x * 255) / w), (byte)((y * 255) / h), (byte)(((x + y) * 255) / (w + h)), 255);
            }
        }

        int sx = 10 + (step * 7);
        int sy = 12 + (step * 3);
        for (int y = 0; y < 16 && sprite; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                // sameColors: the sprite shows colors from elsewhere in the gradient, so the first frame's palette suits it.
                px[((sy + y) * w) + sx + x] = sameColors
                    ? px[((y + 50) * w) + x + 80]
                    : new Rgba32((byte)(40 * ((x / 4) + 1)), (byte)(200 - (30 * (y / 4))), 90, 255);
            }
        }

        return px;
    }

    private static double MeanError(Rgba32[] a, Rgba32[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += Math.Abs(a[i].R - b[i].R) + Math.Abs(a[i].G - b[i].G) + Math.Abs(a[i].B - b[i].B);
        }

        return sum / (a.Length * 3.0);
    }

    /// <summary>Counts image descriptors that carry their own color table.</summary>
    private static (int Frames, int LocalTables) CountTables(byte[] gif)
    {
        int pos = 13;
        if ((gif[10] & 0x80) != 0)
        {
            pos += 3 << ((gif[10] & 7) + 1);
        }

        int frames = 0, local = 0;
        while (gif[pos] != 0x3B)
        {
            if (gif[pos] == 0x21)
            {
                pos += 2;
                while (gif[pos] != 0)
                {
                    pos += gif[pos] + 1;
                }

                pos++;
            }
            else
            {
                frames++;
                int packed = gif[pos + 9];
                pos += 10;
                if ((packed & 0x80) != 0)
                {
                    local++;
                    pos += 3 << ((packed & 7) + 1);
                }

                pos++; // minimum code size
                while (gif[pos] != 0)
                {
                    pos += gif[pos] + 1;
                }

                pos++;
            }
        }

        return (frames, local);
    }

    [Fact]
    public void AnimationWithMoreThanAPaletteOfColorsSharesOnePalette()
    {
        const int W = 160, H = 120, N = 10;
        var frames = new List<Rgba32[]>();
        for (int i = 0; i < N; i++)
        {
            frames.Add(GradientFrame(W, H, i, sameColors: true));
        }

        byte[] gif = GifTestUtil.Encode(GifTestUtil.Animation(W, H, frames));
        Assert.Equal(gif, GifTestUtil.Encode(GifTestUtil.Animation(W, H, frames))); // deterministic

        (int count, int local) = CountTables(gif);
        Assert.Equal(N, count);
        Assert.Equal(0, local); // every frame uses the global table: the changed pixels fit the first frame's palette about as well as the first frame does

        Image back = GifTestUtil.Decode(gif);
        Assert.Equal(N, back.FrameCount);
        for (int i = 0; i < N; i++)
        {
            Assert.True(MeanError(frames[i], GifTestUtil.Pixels(back, i)) < 6.0, $"frame {i}");
        }
    }

    [Fact]
    public void FrameWhoseColorsTheSharedPaletteDoesNotCoverGetsItsOwn()
    {
        const int W = 160, H = 120, N = 8;
        var frames = new List<Rgba32[]>();
        for (int i = 0; i < N; i++)
        {
            Rgba32[] f = GradientFrame(W, H, i);
            if (i == 5)
            {
                // A block of colors far from anything in the first frame's palette.
                for (int y = 60; y < 100; y++)
                {
                    for (int x = 20; x < 140; x++)
                    {
                        f[(y * W) + x] = new Rgba32((byte)(255 - ((x * 2) % 90)), (byte)((y * 5) % 256), (byte)(((x * 11) + y) % 256), 255);
                    }
                }
            }

            frames.Add(f);
        }

        byte[] gif = GifTestUtil.Encode(GifTestUtil.Animation(W, H, frames));
        Image back = GifTestUtil.Decode(gif);
        Assert.Equal(N, back.FrameCount);
        for (int i = 0; i < N; i++)
        {
            Assert.True(MeanError(frames[i], GifTestUtil.Pixels(back, i)) < 12.0, $"frame {i}");
        }

        Assert.True(CountTables(gif).LocalTables >= 1);
    }

    // A one-pixel frame in a screen that declares 8192 x 8192: the canvas must not be sized by the screen.
    private static byte[] TinyFileWithHugeScreen(int screen)
    {
        var b = new List<byte>();
        b.AddRange("GIF89a"u8.ToArray());
        b.AddRange([(byte)(screen & 255), (byte)(screen >> 8), (byte)(screen & 255), (byte)(screen >> 8), 0, 0, 0]);
        b.AddRange([0x2C, 5, 0, 7, 0, 1, 0, 1, 0, 0x80]); // frame at (5, 7), 1x1, local table of 2 entries
        b.AddRange([255, 0, 0, 0, 0, 255]);
        b.AddRange([2, 2, 0x44, 0x01, 0, 0x3B]); // min code 2, one block: clear, pixel 0, end
        return b.ToArray();
    }

    [Fact]
    public void HugeDeclaredScreenOfASmallFrameDecodesInSmallMemory()
    {
        byte[] gif = TinyFileWithHugeScreen(8192);
        GC.Collect();
        long before = GC.GetTotalMemory(true);
        var buf = new Rgba32[10 * 10];
        for (int i = 0; i < 20; i++)
        {
            Image img = Image.Load(gif, new LoadOptions { Configuration = new ImageConfiguration().Add(GifFormat.Instance) });
            Assert.Equal(8192, img.Width);
            img.Frames[0].CopyPixelsTo<Rgba32>(buf, new ImageRect(0, 4, 10, 10));
            Assert.Equal(new Rgba32(255, 0, 0, 255), buf[(3 * 10) + 5]);
            Assert.Equal(default, buf[0]);
            Assert.Equal(default, buf[(5 * 10) + 6]);
        }

        Assert.True(LiveHeap.Read(before, 8L << 20) - before < 8L << 20);
    }

    private static long Rss()
    {
        foreach (string line in File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
            {
                return long.Parse(line.AsSpan(6).Trim().TrimEnd(" kB".AsSpan()), System.Globalization.CultureInfo.InvariantCulture) * 1024;
            }
        }

        return Process.GetCurrentProcess().WorkingSet64;
    }

    /// <summary>
    /// Decodes, resizes and re-encodes many GIFs in one process (small animations, large-canvas animations, files that declare a
    /// huge screen) and checks that live heap and resident memory stop growing once warmed up. The default run is short enough
    /// for every test run (live heap and growth bounds). To also hold the resident size to a fixed band, run it alone and long:
    /// <c>ONEIMAGE_SOAK_ITERATIONS=400 dotnet test --project tests/OneImage.Tests --filter-method '*ManyDecodesAndEncodesReachASteadyResidentSize'</c>.
    /// </summary>
    [Fact]
    public void ManyDecodesAndEncodesReachASteadyResidentSize()
    {
        bool soak = int.TryParse(Environment.GetEnvironmentVariable("ONEIMAGE_SOAK_ITERATIONS"), out int n) && n >= 20;
        int iterations = soak ? n : 60;
        var config = new ImageConfiguration().Add(GifFormat.Instance);

        // Inputs: 40 frames of 200x150, 3 frames of 2048x2048 (a 16 MB canvas each), and the huge-screen file.
        var small = new List<Rgba32[]>();
        for (int i = 0; i < 40; i++)
        {
            small.Add(GradientFrame(200, 150, i % 20));
        }

        byte[] smallGif = GifTestUtil.Encode(GifTestUtil.Animation(200, 150, small));
        var large = new List<Rgba32[]>();
        for (int i = 0; i < 3; i++)
        {
            var f = new Rgba32[2048 * 2048];
            for (int p = 0; p < f.Length; p += 5)
            {
                f[p] = new Rgba32((byte)((p >> 3) + i), (byte)(p >> 11), (byte)(p >> 14), 255);
            }

            Array.Fill(f, new Rgba32(30, 60, (byte)(90 + i), 255), 1, 3);
            large.Add(f);
        }

        byte[] largeGif = GifTestUtil.Encode(GifTestUtil.Animation(2048, 2048, large), new GifEncoderOptions { Dither = Dither.None });
        byte[] hugeGif = TinyFileWithHugeScreen(16000);
        large.Clear();
        small.Clear();

        // Bounds are relative to the state before the loop: the test process also holds whatever other tests left behind.
        GC.Collect();
        long heapBefore = GC.GetTotalMemory(true);
        long rssBefore = Rss();
        var rss = new long[iterations];
        var heap = new long[iterations];
        var px = new Rgba32[200 * 150];
        for (int it = 0; it < iterations; it++)
        {
            Image a = Image.Load(smallGif, new LoadOptions { Configuration = config });
            for (int f = 0; f < a.FrameCount; f += 7)
            {
                a.Frames[f].CopyPixelsTo<Rgba32>(px, new ImageRect(0, 0, 200, 150));
            }

            using (var ms = new MemoryStream())
            {
                a.Resize(width: 100).Save(ms, GifFormat.Instance);
            }

            Image b = Image.Load(largeGif, new LoadOptions { Configuration = config });
            var row = new Rgba32[2048];
            for (int f = 0; f < b.FrameCount; f++)
            {
                b.Frames[f].CopyPixelsTo<Rgba32>(row, new ImageRect(0, 1000, 2048, 1));
            }

            if (it % 4 == 0)
            {
                using var ms = new MemoryStream();
                b.Resize(width: 256).Save(ms, GifFormat.Instance);
            }

            Image c = Image.Load(hugeGif, new LoadOptions { Configuration = config });
            c.Frames[0].CopyPixelsTo<Rgba32>(new Rgba32[64 * 64], new ImageRect(0, 0, 64, 64));

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            heap[it] = GC.GetTotalMemory(true);
            rss[it] = Rss();
        }

        int half = iterations / 2;
        long heapGrowth = heap[iterations - 1] - heap[half];
        long rssBand = rss[half..].Max() - rss[half..].Min();
        long rssGrowth = rss[half..].Max() - rss[..half].Max();
        string summary = $"iterations={iterations} start heap={heapBefore >> 20} rss={rssBefore >> 20} heap(MiB) mid={heap[half] >> 20} end={heap[^1] >> 20} rss(MiB) first-half max={rss[..half].Max() >> 20} second-half min={rss[half..].Min() >> 20} max={rss[half..].Max() >> 20}";
        Console.WriteLine(summary);
        Assert.True(heapGrowth < 16L << 20, $"live heap grew by {heapGrowth >> 20} MiB in the second half");
        Assert.True(heap.Max() - heapBefore < 192L << 20, $"live heap peaked {(heap.Max() - heapBefore) >> 20} MiB above its start");
        if (soak)
        {
            // Run on its own (filter to this test) the process holds little else, so the resident size itself must hold still. Inside the
            // full suite it also moves as the runtime gives back other tests' garbage, which says nothing about GIF decoding.
            Assert.True(rssBand < 160L << 20, $"resident size varied by {rssBand >> 20} MiB in the second half");
        }

        Assert.True(rssGrowth < 96L << 20, $"resident size grew by {rssGrowth >> 20} MiB after the first half");
        Assert.True(rss.Max() - rssBefore < 1024L << 20, $"resident size peaked {(rss.Max() - rssBefore) >> 20} MiB above its start");
    }
}
