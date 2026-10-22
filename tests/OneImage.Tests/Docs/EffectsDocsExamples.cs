using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Convolution;
using Xunit;
using Image = OneImage.Image;

namespace ImageSharpEffectsSide
{
    // The ImageSharp half of the "Effects" section of docs/migrating.md, compiled against ImageSharp 3.1.12.
    internal static class Snippets
    {
        public static (int Width, int Height) Effects(string input, string output)
        {
            using SixLabors.ImageSharp.Image image = SixLabors.ImageSharp.Image.Load(input);
            image.Mutate(x => x.Pixelate(8));
            image.Mutate(x => x.OilPaint(10, 15));
            image.Mutate(x => x.Vignette(Color.Black));
            image.Mutate(x => x.Glow(Color.White));
            image.Mutate(x => x.BokehBlur(32, 2, 3f));
            image.Mutate(x => x.GaussianSharpen(1.2f));
            image.Mutate(x => x.DetectEdges(KnownEdgeDetectorKernels.Kirsch));
            image.Mutate(x => x.EntropyCrop(0.5f));
            image.Save(output);
            return (image.Width, image.Height);
        }
    }
}

namespace OneImage.Tests.Docs
{
    /// <summary>The OneImage half of the "Effects" section of docs/migrating.md and the effects block of docs/guide.md.</summary>
    public sealed class EffectsDocsExamples : IDisposable
    {
        private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("oneimage-effects-docs-");
        private readonly string _photo;

        public EffectsDocsExamples()
        {
            var px = new Rgb24[240 * 160];
            var rnd = new Random(9);
            for (int i = 0; i < px.Length; i++)
            {
                int n = rnd.Next(-8, 9);
                px[i] = new Rgb24((byte)Math.Clamp((i % 240) + n, 0, 255), (byte)Math.Clamp((i / 240 * 255 / 160) + n, 0, 255), 90);
            }

            _photo = Path.Combine(_dir.FullName, "in.png");
            Image.FromPixels<Rgb24>(px, 240, 160).Save(_photo);
        }

        public void Dispose() => _dir.Delete(recursive: true);

        [Fact]
        public void MigrationEffects()
        {
            string input = _photo;
            string output = Path.Combine(_dir.FullName, "oi.png");
            (int w, int h) = global::ImageSharpEffectsSide.Snippets.Effects(input, Path.Combine(_dir.FullName, "is.png"));
            Assert.True(w > 0 && h > 0);

            Image.Load(input)
                .Pixelate(8)
                .OilPaint(levels: 10, brushSize: 8)
                .Vignette(new Rgba32(0, 0, 0, 255))
                .Glow(new Rgba32(255, 255, 255, 255))
                .BokehBlur(radius: 32, components: 2, gamma: 3)
                .GaussianSharpen(1.2)
                .DetectEdges(EdgeDetector.Kirsch)
                .EntropyCrop(0.5f)
                .Save(output);

            ImageInfo info = Image.Identify(output);
            Assert.True(info.Width > 0 && info.Height > 0 && info.Width <= 240 && info.Height <= 160);
        }

        [Fact]
        public void GuideEffects()
        {
            Image img = Image.Load(_photo);
            Assert.Equal(240, img.Pixelate(size: 8).Width);
            Assert.Equal(240, img.OilPaint(levels: 10, brushSize: 15).Width);
            Assert.Equal(240, img.Vignette(new Rgba32(0, 0, 0, 255)).Width);
            Assert.Equal(240, img.Glow(new Rgba32(255, 255, 255, 255), radius: 200).Width);
            Assert.Equal(240, img.BokehBlur(radius: 32, components: 2, gamma: 3).Width);
            Assert.Equal(240, img.GaussianSharpen(sigma: 3).Width);
            Assert.Equal(PixelFormat.Gray8, img.DetectEdges(EdgeDetector.Kirsch).Format);
            Assert.Equal(PixelFormat.Rgb8, img.DetectEdges(EdgeDetector.Scharr, grayscale: false).Format);
            Assert.True(img.EntropyCrop(threshold: 0.5f).Width <= 240);
        }
    }
}
