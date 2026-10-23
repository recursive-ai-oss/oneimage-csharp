using Xunit;

namespace OneImage.Tests.Docs;

/// <summary>The PixelResizer example of docs/guide.md, compiled and run.</summary>
public class ResizeDocsExamples
{
    [Fact]
    public void GuidePixelResizerLoop()
    {
        var crops = new List<(Gray8[] Pixels, int Width, int Height)>();
        foreach (int width in new[] { 2300, 1800, 2411 })
        {
            var pixels = new Gray8[width * 60];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Gray8((byte)((i * 7) ^ (i >> 5)));
            }

            crops.Add((pixels, width, 60));
        }

        using var resizer = new PixelResizer();
        var buffer = new Gray8[4000 * 32];
        foreach ((Gray8[] pixels, int width, int height) in crops)
        {
            (int w, int h) = PixelResizer.GetSize(width, height, height: 32);
            resizer.Resize<Gray8>(pixels, width, height, buffer.AsSpan(0, w * h), w, h);

            Image lazy = Image.FromPixels<Gray8>(pixels, width, height).Resize(height: 32);
            Assert.Equal((lazy.Width, lazy.Height), (w, h));
            Assert.True(lazy.ToArray<Gray8>().AsSpan().SequenceEqual(buffer.AsSpan(0, w * h)));
        }
    }
}
