using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Binarization;
using SixLabors.ImageSharp.Processing.Processors.Dithering;
using SixLabors.ImageSharp.Processing.Processors.Normalization;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using Xunit;
using Image = OneImage.Image;

namespace ImageSharpHistogramSide
{
    // The ImageSharp half of the "Histograms, thresholds and dithering" section of docs/migrating.md, compiled against ImageSharp 3.1.12.
    internal static class Snippets
    {
        public static (int Width, int Height) Section(string input, string output)
        {
            using SixLabors.ImageSharp.Image image = SixLabors.ImageSharp.Image.Load(input);
            image.Mutate(x => x.HistogramEqualization(new HistogramEqualizationOptions { Method = SixLabors.ImageSharp.Processing.Processors.Normalization.HistogramEqualizationMethod.AutoLevel }));
            image.Mutate(x => x.AdaptiveThreshold(Color.White, Color.Black, 0.85f));
            image.Mutate(x => x.BinaryDither(KnownDitherings.Bayer4x4));
            image.Mutate(x => x.Dither(KnownDitherings.Stucki, new[] { Color.Black, Color.White, Color.Red }));
            image.Mutate(x => x.Quantize(KnownQuantizers.Octree));
            image.Save(output);
            return (image.Width, image.Height);
        }
    }
}

namespace OneImage.Tests.Docs
{
    /// <summary>The OneImage half of the "Histograms, thresholds and dithering" section of docs/migrating.md and the matching block of docs/guide.md.</summary>
    public sealed class HistogramDocsExamples : IDisposable
    {
        private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("oneimage-histogram-docs-");
        private readonly string _photo;

        public HistogramDocsExamples()
        {
            var px = new Rgb24[240 * 160];
            var rnd = new Random(11);
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
        public void MigrationSection()
        {
            string input = _photo;
            string output = Path.Combine(_dir.FullName, "oi.png");
            (int w, int h) = global::ImageSharpHistogramSide.Snippets.Section(input, Path.Combine(_dir.FullName, "is.png"));
            Assert.True(w > 0 && h > 0);

            Image.Load(input)
                .AutoLevel()
                .AdaptiveThreshold(new Rgba32(255, 255, 255, 255), new Rgba32(0, 0, 0, 255), 0.85f)
                .BinaryDither(Dither.Bayer4x4)
                .Dither([new Rgba32(0, 0, 0, 255), new Rgba32(255, 255, 255, 255), new Rgba32(255, 0, 0, 255)], Dither.Stucki)
                .Quantize(256, quantizer: Quantizer.Octree)
                .Save(output);

            ImageInfo info = Image.Identify(output);
            Assert.Equal(240, info.Width);
            Assert.Equal(160, info.Height);
        }

        [Fact]
        public void GuideBlock()
        {
            Image img = Image.Load(_photo);
            Assert.Equal(240, img.AutoLevel(syncChannels: false).Width);
            Assert.Equal(240, img.AdaptiveThreshold().Width);
            Assert.Equal(240, img.BinaryDither(Dither.Bayer8x8).Width);
            Assert.Equal(240, img.Dither(KnownPalettes.WebSafe, Dither.FloydSteinberg, fastMatch: true).Width);
            Assert.Equal(240, img.Quantize(64, Dither.Atkinson, quantizer: Quantizer.Octree).Width);
            Assert.Equal(240, img.Quantize(quantizer: Quantizer.Werner).Width);
        }
    }
}
