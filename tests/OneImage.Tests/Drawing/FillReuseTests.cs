using System.Numerics;
using OneImage.Drawing;
using Path = OneImage.Drawing.Path;

namespace OneImage.Tests.Drawing;

/// <summary>
/// Canvas fills reuse the rasterizer's buffers from one fill to the next on a thread. A long run of very different fills (sizes,
/// shapes, clips, empty and far-away paths, canvases of different heights) must still give the bytes of the lazy image fill, which
/// builds everything fresh.
/// </summary>
public class FillReuseTests
{
    [Fact]
    public void ManyDifferentFillsMatchTheImageFill()
    {
        var rnd = new Random(31);
        (int W, int H)[] sizes = [(300, 40), (17, 500), (128, 128), (1, 1), (260, 130), (65, 700), (500, 20)];
        foreach ((int w, int h) in sizes)
        {
            using var canvas = new Canvas(w, h, PixelFormat.Rgba8);
            Image reference = canvas.Snapshot();
            for (int i = 0; i < 60; i++)
            {
                Path path = RandomPath(rnd, w, h);
                Path? clip = i % 5 == 0 ? RandomPath(rnd, w, h) : null;
                var color = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)(80 + rnd.Next(176)));
                IntersectionRule rule = i % 3 == 0 ? IntersectionRule.EvenOdd : IntersectionRule.NonZero;
                bool aa = i % 7 != 0;
                canvas.Fill(color, path, aa, rule, clip: clip);
                reference = reference.Fill(color, path, aa, rule, clip: clip);
            }

            Assert.Equal(reference.ToArray<Rgba32>(), canvas.Snapshot().ToArray<Rgba32>());
        }
    }

    private static Path RandomPath(Random rnd, int w, int h)
    {
        var b = new PathBuilder();
        int figures = 1 + rnd.Next(3);
        for (int f = 0; f < figures; f++)
        {
            // Mostly small shapes inside the canvas, sometimes huge ones, sometimes ones that miss the canvas completely.
            float scale = rnd.Next(10) switch { 0 => 10 * Math.Max(w, h), 1 => 0.5f, _ => Math.Max(4, Math.Min(w, h) / 3f) };
            float cx = rnd.Next(12) == 0 ? w + 4000 : rnd.NextSingle() * w;
            float cy = rnd.NextSingle() * h;
            b.MoveTo(new Vector2(cx, cy));
            int points = 2 + rnd.Next(5);
            for (int p = 0; p < points; p++)
            {
                var to = new Vector2(cx + ((rnd.NextSingle() - 0.5f) * scale), cy + ((rnd.NextSingle() - 0.5f) * scale));
                if (rnd.Next(3) == 0)
                {
                    b.QuadraticBezierTo(cx + ((rnd.NextSingle() - 0.5f) * scale), cy + ((rnd.NextSingle() - 0.5f) * scale), to.X, to.Y);
                }
                else
                {
                    b.LineTo(to);
                }
            }

            b.Close();
        }

        return b.Build();
    }
}
