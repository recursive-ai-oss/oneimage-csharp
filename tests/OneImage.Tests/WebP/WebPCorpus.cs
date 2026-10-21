using ImageMagick;

namespace OneImage.Tests.WebP;

/// <summary>Natural-looking test images generated with ImageMagick's built-in images, so no third-party files are needed.</summary>
internal static class WebPCorpus
{
    public static IEnumerable<(string Name, byte[] Rgba, int Width, int Height)> Photos(bool large = false)
    {
        yield return Load("wizard", "wizard:", 0, 0);
        yield return Load("rose-big", "rose:", 640, 420);
        yield return Plasma("plasma-a", 512, 384, 11);
        yield return Plasma("plasma-b", 400, 300, 12);
        if (large)
        {
            yield return Plasma("plasma-large", 2000, 1500, 13);
            yield return Load("wizard-large", "wizard:", 1500, 2000);
        }
    }

    private static (string, byte[], int, int) Load(string name, string spec, int w, int h)
    {
        using var img = new MagickImage(spec);
        if (w > 0)
        {
            img.FilterType = FilterType.Lanczos;
            img.Resize((uint)w, (uint)h);
        }

        return (name, ToRgba(img), (int)img.Width, (int)img.Height);
    }

    private static (string, byte[], int, int) Plasma(string name, int w, int h, int seed)
    {
        MagickNET.SetRandomSeed((ulong)seed);
        using var img = new MagickImage("plasma:fractal", (uint)w, (uint)h);
        // Add a little sensor-like noise and soft edges so it behaves like a camera image.
        img.AddNoise(NoiseType.Gaussian, 0.4);
        return (name, ToRgba(img), w, h);
    }

    private static byte[] ToRgba(MagickImage img)
    {
        img.HasAlpha = false;
        using IPixelCollection<byte> px = img.GetPixels();
        byte[] rgb = px.ToByteArray(PixelMapping.RGB) ?? throw new InvalidOperationException();
        byte[] rgba = new byte[rgb.Length / 3 * 4];
        for (int i = 0, j = 0; i < rgb.Length; i += 3, j += 4)
        {
            rgba[j] = rgb[i];
            rgba[j + 1] = rgb[i + 1];
            rgba[j + 2] = rgb[i + 2];
            rgba[j + 3] = 255;
        }

        return rgba;
    }
}
