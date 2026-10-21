using OneImage.Kernels;

namespace OneImage;

/// <summary>Rec. 709 luminance, in linear light by default. Optionally changes the format to gray.</summary>
internal sealed class GrayscaleStep(bool linear, bool toGrayFormat) : ToneStep
{
    public override string Name => "grayscale";

    public override OpParameter[] Parameters() => [P("linear", linear), P("to_gray_format", toGrayFormat)];

    public override PixelFormat Output(PixelFormat input) =>
        toGrayFormat && input.Model == ColorModel.Rgb ? new PixelFormat(ColorModel.Gray, input.Sample, input.Alpha) : input;

    public override bool IsNoOp(PixelFormat input) => input.Model == ColorModel.Gray;

    public override bool ModifiesAlpha => false;

    public override void Apply(ref ToneRow row)
    {
        Span<float> r = row.Plane(0);
        Span<float> g = row.Plane(1);
        Span<float> b = row.Plane(2);
        if (row.Sample == SampleType.U8)
        {
            // Values sit on the 8-bit grid, so the integer kernels give the same bytes as the direct row kernel.
            for (int i = 0; i < r.Length; i++)
            {
                int ri = (int)((r[i] * 255f) + 0.5f);
                int gi = (int)((g[i] * 255f) + 0.5f);
                int bi = (int)((b[i] * 255f) + 0.5f);
                r[i] = (linear ? ToneKernel.Luma8(ri, gi, bi) : ToneKernel.LumaEncoded8(ri, gi, bi)) / 255f;
            }
        }
        else
        {
            if (linear)
            {
                SrgbTransfer.Decode(r);
                SrgbTransfer.Decode(g);
                SrgbTransfer.Decode(b);
            }

            for (int i = 0; i < r.Length; i++)
            {
                r[i] = (0.2126f * r[i]) + (0.7152f * g[i]) + (0.0722f * b[i]);
            }

            if (linear)
            {
                SrgbTransfer.EncodeExact(r);
            }
        }

        if (toGrayFormat)
        {
            row.Colors = 1;
        }
        else
        {
            r.CopyTo(g);
            r.CopyTo(b);
        }
    }

    public override bool TryApplyBytes(ReadOnlySpan<byte> source, Span<byte> destination, PixelFormat input, int width)
    {
        if (input.Sample != SampleType.U8 || input.Alpha == AlphaMode.Premultiplied || input.Model != ColorModel.Rgb)
        {
            return false;
        }

        ToneKernel.GrayscaleRow8(source, destination, width, input.ChannelCount, toGrayFormat, linear);
        return true;
    }
}

/// <summary>Hue rotation, saturation and lightness in the HSL model, on values clamped to 0..1.</summary>
internal sealed class HslStep(double hue, double saturation, double lightness, bool linear) : ToneStep
{
    private readonly float _hue = (float)(hue / 360.0);
    private readonly float _sat = (float)(1.0 + saturation);
    private readonly float _light = (float)lightness;

    public override string Name => "hue_saturation_lightness";

    public override OpParameter[] Parameters() => [P("hue", hue), P("saturation", saturation), P("lightness", lightness), P("linear", linear)];

    public override bool IsNoOp(PixelFormat input) => hue == 0 && saturation == 0 && lightness == 0;

    // Gray pixels have no hue or saturation; only lightness acts, one channel at a time.
    public override bool ChannelWise(PixelFormat input) => input.Model == ColorModel.Gray;

    public override void ApplyChannel(int channel, Span<float> values)
    {
        if (linear)
        {
            SrgbTransfer.Decode(values);
        }

        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Lighten(Clamp01(values[i]));
        }

        if (linear)
        {
            SrgbTransfer.EncodeExact(values);
        }
    }

    public override void Apply(ref ToneRow row)
    {
        if (row.Colors == 1)
        {
            base.Apply(ref row);
            return;
        }

        Span<float> rs = row.Plane(0);
        Span<float> gs = row.Plane(1);
        Span<float> bs = row.Plane(2);
        if (linear)
        {
            SrgbTransfer.Decode(rs);
            SrgbTransfer.Decode(gs);
            SrgbTransfer.Decode(bs);
        }

        for (int i = 0; i < rs.Length; i++)
        {
            float r = Clamp01(rs[i]);
            float g = Clamp01(gs[i]);
            float b = Clamp01(bs[i]);
            float max = MathF.Max(r, MathF.Max(g, b));
            float min = MathF.Min(r, MathF.Min(g, b));
            float l = (max + min) / 2f;
            float d = max - min;
            float h = 0f;
            float s = 0f;
            if (d > 0f)
            {
                s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
                if (max == r)
                {
                    h = ((g - b) / d) + (g < b ? 6f : 0f);
                }
                else if (max == g)
                {
                    h = ((b - r) / d) + 2f;
                }
                else
                {
                    h = ((r - g) / d) + 4f;
                }

                h /= 6f;
            }

            h += _hue;
            h -= MathF.Floor(h);
            s = Clamp01(s * _sat);
            l = Lighten(l);
            if (s == 0f)
            {
                r = l;
                g = l;
                b = l;
            }
            else
            {
                float q = l < 0.5f ? l * (1f + s) : l + s - (l * s);
                float p = (2f * l) - q;
                r = HueToChannel(p, q, h + (1f / 3f));
                g = HueToChannel(p, q, h);
                b = HueToChannel(p, q, h - (1f / 3f));
            }

            rs[i] = r;
            gs[i] = g;
            bs[i] = b;
        }

        if (linear)
        {
            SrgbTransfer.EncodeExact(rs);
            SrgbTransfer.EncodeExact(gs);
            SrgbTransfer.EncodeExact(bs);
        }
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    private float Lighten(float l) => _light >= 0f ? l + ((1f - l) * _light) : l * (1f + _light);

    private static float HueToChannel(float p, float q, float t)
    {
        if (t < 0f)
        {
            t += 1f;
        }

        if (t > 1f)
        {
            t -= 1f;
        }

        if (t < 1f / 6f)
        {
            return p + ((q - p) * 6f * t);
        }

        if (t < 0.5f)
        {
            return q;
        }

        if (t < 2f / 3f)
        {
            return p + ((q - p) * ((2f / 3f) - t) * 6f);
        }

        return p;
    }
}

/// <summary>Each output color is a weighted sum of the input colors plus an offset.</summary>
internal sealed class ChannelMixerStep : ToneStep
{
    private readonly float[] _m;
    private readonly bool _linear;

    public ChannelMixerStep(ReadOnlySpan<double> matrix, bool linear)
    {
        if (matrix.Length is not (9 or 12))
        {
            throw new ArgumentException("The matrix needs 9 values (3x3) or 12 values (3x4 with offsets), row-major.", nameof(matrix));
        }

        _m = new float[12];
        Values = matrix.ToArray();
        for (int i = 0; i < matrix.Length; i++)
        {
            if (!double.IsFinite(matrix[i]))
            {
                throw new ArgumentOutOfRangeException(nameof(matrix), "Matrix values must be finite.");
            }
        }

        for (int row = 0; row < 3; row++)
        {
            int cols = matrix.Length / 3;
            for (int c = 0; c < cols; c++)
            {
                _m[(row * 4) + c] = (float)matrix[(row * cols) + c];
            }
        }

        _linear = linear;
    }

    public double[] Values { get; }

    public override string Name => "channel_mixer";

    public override OpParameter[] Parameters() => [P("matrix", string.Join(',', Values.Select(Num))), P("linear", _linear)];

    public override void Validate(PixelFormat input)
    {
        if (input.Model != ColorModel.Rgb)
        {
            throw new ImageOperationException("ChannelMixer needs an RGB image.");
        }
    }

    public override bool IsNoOp(PixelFormat input)
    {
        double[] id = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        for (int i = 0; i < Values.Length; i++)
        {
            double expected = i < 9 ? id[i] : 0;
            if (Values[i] != expected)
            {
                return false;
            }
        }

        return true;
    }

    public override void Apply(ref ToneRow row)
    {
        Span<float> rs = row.Plane(0);
        Span<float> gs = row.Plane(1);
        Span<float> bs = row.Plane(2);
        if (_linear)
        {
            SrgbTransfer.Decode(rs);
            SrgbTransfer.Decode(gs);
            SrgbTransfer.Decode(bs);
        }

        float[] m = _m;
        for (int i = 0; i < rs.Length; i++)
        {
            float r = rs[i];
            float g = gs[i];
            float b = bs[i];
            rs[i] = (m[0] * r) + (m[1] * g) + (m[2] * b) + m[3];
            gs[i] = (m[4] * r) + (m[5] * g) + (m[6] * b) + m[7];
            bs[i] = (m[8] * r) + (m[9] * g) + (m[10] * b) + m[11];
        }

        if (_linear)
        {
            SrgbTransfer.EncodeExact(rs);
            SrgbTransfer.EncodeExact(gs);
            SrgbTransfer.EncodeExact(bs);
        }
    }
}

/// <summary>Composites straight-alpha pixels over an opaque color and drops the alpha channel.</summary>
internal sealed class FlattenStep : ToneStep
{
    private readonly Rgba32 _background;
    private readonly bool _linear;
    private readonly float[] _rgb;
    private readonly float _gray;

    public FlattenStep(Rgba32 background, bool linear)
    {
        _background = background;
        _linear = linear;
        _rgb = [background.R / 255f, background.G / 255f, background.B / 255f];
        _gray = (0.2126f * _rgb[0]) + (0.7152f * _rgb[1]) + (0.0722f * _rgb[2]);
    }

    public override string Name => "flatten";

    public override OpParameter[] Parameters() =>
        [P("red", (long)_background.R), P("green", (long)_background.G), P("blue", (long)_background.B), P("linear", _linear)];

    public override PixelFormat Output(PixelFormat input) => new(input.Model, input.Sample, AlphaMode.None);

    public override bool IsNoOp(PixelFormat input) => !input.HasAlpha;

    public override bool ModifiesAlpha => true;

    public override void Apply(ref ToneRow row)
    {
        Span<float> a = row.Plane(3);
        for (int k = 0; k < row.Colors; k++)
        {
            Span<float> c = row.Plane(k);
            float bg = row.Colors == 1 ? _gray : _rgb[k];
            if (_linear)
            {
                SrgbTransfer.Decode(c);
                bg = SrgbTransfer.DecodeOne(bg);
            }

            for (int i = 0; i < c.Length; i++)
            {
                float al = a[i] < 0f ? 0f : a[i] > 1f ? 1f : a[i];
                c[i] = (c[i] * al) + (bg * (1f - al));
            }

            if (_linear)
            {
                SrgbTransfer.EncodeExact(c);
            }
        }

        row.HasAlpha = false;
    }
}

/// <summary>Adds an alpha channel of constant opacity (nothing happens if the image already has one).</summary>
internal sealed class AddAlphaStep(double alpha) : ToneStep
{
    public override string Name => "add_alpha";

    public override OpParameter[] Parameters() => [P("alpha", alpha)];

    public override PixelFormat Output(PixelFormat input) => input.HasAlpha ? input : new PixelFormat(input.Model, input.Sample, AlphaMode.Straight);

    public override bool IsNoOp(PixelFormat input) => input.HasAlpha;

    public override bool ModifiesAlpha => true;

    public override void Apply(ref ToneRow row)
    {
        row.Plane(3).Fill((float)alpha);
        row.HasAlpha = true;
    }
}

/// <summary>Drops the alpha channel without flattening.</summary>
internal sealed class RemoveAlphaStep : ToneStep
{
    public override string Name => "remove_alpha";

    public override OpParameter[] Parameters() => [];

    public override PixelFormat Output(PixelFormat input) => new(input.Model, input.Sample, AlphaMode.None);

    public override bool IsNoOp(PixelFormat input) => !input.HasAlpha;

    public override bool ModifiesAlpha => true;

    public override void Apply(ref ToneRow row) => row.HasAlpha = false;
}

/// <summary>
/// Pixel-format conversion between gray and RGB, alpha modes and sample types, with the arithmetic of <see cref="ConvertNode"/>
/// (so fusing it changes no bytes).
/// </summary>
internal sealed class ConvertStep(PixelFormat target) : ToneStep
{
    public override string Name => "convert";

    public override OpParameter[] Parameters() =>
        [P("model", target.Model.ToString()), P("sample", target.Sample.ToString()), P("alpha", target.Alpha.ToString())];

    public override PixelFormat Output(PixelFormat input) => target;

    public override bool IsNoOp(PixelFormat input) => input == target;

    public override bool ModifiesAlpha => true;

    public override void Validate(PixelFormat input)
    {
        RequireGrayOrRgb(input);
        RequireGrayOrRgb(target);
    }

    public override void Apply(ref ToneRow row)
    {
        if (row.Colors == 1 && target.Model == ColorModel.Rgb)
        {
            row.Plane(0).CopyTo(row.Plane(1));
            row.Plane(0).CopyTo(row.Plane(2));
            row.Colors = 3;
        }
        else if (row.Colors == 3 && target.Model == ColorModel.Gray)
        {
            Span<float> r = row.Plane(0);
            Span<float> g = row.Plane(1);
            Span<float> b = row.Plane(2);
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = (0.2126f * r[i]) + (0.7152f * g[i]) + (0.0722f * b[i]);
            }

            row.Colors = 1;
        }

        if (target.HasAlpha && !row.HasAlpha)
        {
            row.Plane(3).Fill(1f);
        }

        row.HasAlpha = target.HasAlpha;
    }
}

/// <summary>Selects one stored channel (colors first, then alpha) as a gray image.</summary>
internal sealed class ExtractChannelStep(int channel) : ToneStep
{
    public override string Name => "extract_channel";

    public override OpParameter[] Parameters() => [P("channel", (long)channel)];

    public override PixelFormat Output(PixelFormat input) => new(ColorModel.Gray, input.Sample, AlphaMode.None);

    public override bool ModifiesAlpha => false;

    public override void Validate(PixelFormat input)
    {
        RequireGrayOrRgb(input);
        if ((uint)channel >= (uint)input.ChannelCount)
        {
            throw new ImageOperationException("The image has no such channel.");
        }
    }

    public override void Apply(ref ToneRow row)
    {
        int plane = channel < row.Colors ? channel : 3;
        if (plane != 0)
        {
            row.Plane(plane).CopyTo(row.Plane(0));
        }

        row.Colors = 1;
        row.HasAlpha = false;
    }
}
