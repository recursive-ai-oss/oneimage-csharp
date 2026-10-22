using SixLabors.ImageSharp.Processing;
using Xunit;
using IsColorMatrix = SixLabors.ImageSharp.ColorMatrix;
using IsImage = SixLabors.ImageSharp.Image;

namespace OneImage.Tests.Docs;

/// <summary>The snippets of the color-matrix section of docs/migrating.md: the ImageSharp half and the OneImage half, run side by side.</summary>
public sealed class ColorMatrixDocsExamples : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("oneimage-colormatrix-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void SepiaPresetsBlindnessOpacityAndMatrix()
    {
        string input = Path.Combine(_dir.FullName, "in.png");
        string isOut = Path.Combine(_dir.FullName, "is.png");
        string output = Path.Combine(_dir.FullName, "oi.png");
        var px = new Rgba32[64 * 48];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba32((byte)(i % 64 * 4), (byte)(i / 64 * 5), 90, 255);
        }

        Image.FromPixels<Rgba32>(px, 64, 48).Save(input);

        using (IsImage image = IsImage.Load(input))
        {
            image.Mutate(x => x.Sepia(0.8f));
            image.Mutate(x => x.Kodachrome());
            image.Mutate(x => x.ColorBlindness(SixLabors.ImageSharp.Processing.ColorBlindnessMode.Deuteranopia));
            image.Mutate(x => x.Opacity(0.5f));
            image.Mutate(x => x.Filter(new IsColorMatrix(
                1f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f,
                0f, 0f, 1f, 0f,
                0f, 0f, 0f, 1f,
                0.1f, 0f, 0f, 0f)));
            SixLabors.ImageSharp.ImageExtensions.Save(image, isOut);
        }

        Image.Load(input)
            .Sepia(0.8f)
            .Kodachrome()
            .ColorBlindness(ColorBlindnessMode.Deuteranopia)
            .Opacity(0.5f)
            .ApplyColorMatrix(new ColorMatrix(
                1f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f,
                0f, 0f, 1f, 0f,
                0f, 0f, 0f, 1f,
                0.1f, 0f, 0f, 0f))
            .Save(output);

        // Both chains round to 8 bits after every step, so they agree closely but not necessarily to the bit.
        Rgba32[] ours = Image.Load(output).ToArray<Rgba32>();
        Rgba32[] theirs = Image.Load(isOut).ToArray<Rgba32>();
        int max = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            max = Math.Max(max, Math.Max(Math.Abs(ours[i].R - theirs[i].R), Math.Max(Math.Abs(ours[i].G - theirs[i].G), Math.Max(Math.Abs(ours[i].B - theirs[i].B), Math.Abs(ours[i].A - theirs[i].A)))));
        }

        Assert.True(max <= 4, $"max difference {max}");
    }

    [Fact]
    public void GuideSnippets()
    {
        Image photo = Image.FromPixels<Rgba32>(new Rgba32[16 * 16], 16, 16);
        Image warm = photo.Sepia(0.6f).BrightnessContrast(contrast: 0.1).Opacity(0.9f);
        Image seen = photo.ColorBlindness(ColorBlindnessMode.Protanopia);
        Assert.Equal(16, warm.Width);
        Assert.Equal(16, seen.Height);
    }
}
