using System.Numerics;
using OneImage.Kernels;

namespace OneImage.Drawing;

/// <summary>
/// Converts straight brush colors from an image's encoded values to the linear-light values a linear fill works in. Built from a
/// 4097-entry curve found by sending a gray ramp through the same profile conversion the image pixels get, so brush colors and
/// pixels agree exactly for matrix/curve profiles. Each color channel uses that one curve; for gray images the color is first
/// reduced to gray with Rec. 709 weights, as a gray fill color is.
/// </summary>
internal sealed class ColorMap
{
    private const int Steps = 4096;
    private readonly float[] _curve;
    private readonly bool _gray;

    private ColorMap(float[] curve, bool gray)
    {
        _curve = curve;
        _gray = gray;
    }

    private static readonly Lazy<ColorMap> DefaultRgb = new(() => Create(ColorProfile.DefaultFor(ColorModel.Rgb), false));
    private static readonly Lazy<ColorMap> DefaultGray = new(() => Create(ColorProfile.DefaultFor(ColorModel.Gray), true));

    /// <summary>The map of the default profile of a color model (shared; built on first use).</summary>
    public static ColorMap Default(ColorModel model) => model == ColorModel.Gray ? DefaultGray.Value : DefaultRgb.Value;

    public static ColorMap Create(Image image) => Create(image.ColorProfile, image.Format.Model == ColorModel.Gray);

    private static ColorMap Create(ColorProfile profile, bool gray)
    {
        var ramp = new RgbaFloat[Steps + 1];
        for (int i = 0; i <= Steps; i++)
        {
            float v = i / (float)Steps;
            ramp[i] = new RgbaFloat(v, v, v, 1f);
        }

        Image px = Image.FromPixels<RgbaFloat>(ramp, Steps + 1, 1);
        if (gray)
        {
            px = px.ConvertTo(PixelFormat.GrayF32);
        }

        Image lin = px.AssignProfile(profile).ToLinearLight(out _);
        var values = new float[(Steps + 1) * 4];
        Render.ReadRegion(lin.Root, new ImageRect(0, 0, Steps + 1, 1), SpanCast.AsBytes(values.AsSpan()), 0, PixelFormat.RgbaF32, RenderContext.Create(1, CancellationToken.None));
        var curve = new float[Steps + 1];
        for (int i = 0; i <= Steps; i++)
        {
            curve[i] = values[i * 4];
        }

        return new ColorMap(curve, gray);
    }

    /// <summary>Maps one encoded value in 0..1 (outside it is clamped) to linear light.</summary>
    public float Map(float v)
    {
        if (!(v > 0f))
        {
            return _curve[0];
        }

        if (v >= 1f)
        {
            return _curve[Steps];
        }

        float pos = v * Steps;
        int i = (int)pos;
        float f = pos - i;
        return _curve[i] + ((_curve[i + 1] - _curve[i]) * f);
    }

    /// <summary>Maps the color of one straight RGBA value; alpha is kept.</summary>
    public Vector4 Map(Vector4 c)
    {
        if (_gray)
        {
            float g = Map((0.2126f * c.X) + (0.7152f * c.Y) + (0.0722f * c.Z));
            return new Vector4(g, g, g, c.W);
        }

        return new Vector4(Map(c.X), Map(c.Y), Map(c.Z), c.W);
    }

    /// <summary>Maps a run of straight RGBA floats in place; alpha is kept.</summary>
    public void Apply(Span<float> rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            Vector4 m = Map(new Vector4(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]));
            rgba[i] = m.X;
            rgba[i + 1] = m.Y;
            rgba[i + 2] = m.Z;
        }
    }
}
