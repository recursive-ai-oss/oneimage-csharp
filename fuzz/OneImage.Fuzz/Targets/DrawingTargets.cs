using System;
using System.Text;
using OneImage;
using OneImage.Drawing;

/// <summary>SVG path-data parsing, then everything a parsed path can be asked to do: measure, flatten, reverse, write back, and fill.</summary>
internal static class DrawingTargets
{
    public static void Svg(ReadOnlySpan<byte> data)
    {
        string text = Encoding.UTF8.GetString(data);
        Targets.Guard(() =>
        {
            if (!OneImage.Drawing.Path.TryParseSvg(text, out OneImage.Drawing.Path? path) || path is null)
            {
                return;
            }

            _ = path.Bounds;
            _ = path.Length;
            _ = path.Flatten(0.5f);
            OneImage.Drawing.Path reversed = path.Reverse();
            if (!OneImage.Drawing.Path.TryParseSvg(path.ToSvg(), out OneImage.Drawing.Path? again) || again is null || again.ToSvg() != path.ToSvg())
            {
                throw new InvalidOperationException("ToSvg output does not read back to the same path");
            }

            var image = Image.FromPixels<Rgba32>(new Rgba32[96 * 96], 96, 96);
            FillBoth(image, path);
            FillBoth(image, reversed);
        });
    }

    private static void FillBoth(Image image, OneImage.Drawing.Path p)
    {
        for (int k = 0; k < 2; k++)
        {
            bool aa = k == 0;
            _ = image.Fill(new Rgba32(255, 255, 255, 255), p, aa, aa ? IntersectionRule.NonZero : IntersectionRule.EvenOdd).ToArray<Rgba32>(maxDegree: 1);
        }
    }
}
