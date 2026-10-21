namespace OneImage;

/// <summary>1 - v on color channels.</summary>
internal sealed class InvertStep(bool linear) : ChannelStep(linear)
{
    public override string Name => "invert";

    public override OpParameter[] Parameters() => With();

    protected override void Map(Span<float> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = 1f - values[i];
        }
    }
}

/// <summary>v * k + offset where k = tan((contrast + 1) * pi / 4) pivots around mid-gray and brightness is added.</summary>
internal sealed class BrightnessContrastStep : ChannelStep
{
    private readonly double _brightness;
    private readonly double _contrast;
    private readonly float _k;
    private readonly float _offset;

    public BrightnessContrastStep(double brightness, double contrast, bool linear)
        : base(linear)
    {
        _brightness = brightness;
        _contrast = contrast;

        // Contrast 1 would be an infinite slope; cap just below it (a hard 0.5 threshold with a tiny soft edge).
        double c = Math.Min(contrast, 0.9999);
        double k = Math.Tan((c + 1.0) * Math.PI / 4.0);
        _k = (float)k;
        _offset = (float)(0.5 - (0.5 * k) + brightness);
    }

    public override string Name => "brightness_contrast";

    public override OpParameter[] Parameters() => With(P("brightness", _brightness), P("contrast", _contrast));

    public override bool IsNoOp(PixelFormat input) => _brightness == 0 && _contrast == 0;

    protected override void Map(Span<float> values)
    {
        float k = _k;
        float o = _offset;
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (values[i] * k) + o;
        }
    }
}

/// <summary>v * 2^stops.</summary>
internal sealed class ExposureStep(double stops, bool linear) : ChannelStep(linear)
{
    private readonly float _scale = (float)Math.Pow(2.0, stops);

    public override string Name => "exposure";

    public override OpParameter[] Parameters() => With(P("stops", stops));

    public override bool IsNoOp(PixelFormat input) => stops == 0;

    protected override void Map(Span<float> values)
    {
        float s = _scale;
        for (int i = 0; i < values.Length; i++)
        {
            values[i] *= s;
        }
    }
}

/// <summary>v^(1/gamma) for v &gt; 0, 0 otherwise.</summary>
internal sealed class GammaStep(double gamma, bool linear) : ChannelStep(linear)
{
    private readonly float _inverse = (float)(1.0 / gamma);

    public override string Name => "gamma";

    public override OpParameter[] Parameters() => With(P("gamma", gamma));

    public override bool IsNoOp(PixelFormat input) => gamma == 1.0;

    protected override void Map(Span<float> values)
    {
        float inv = _inverse;
        for (int i = 0; i < values.Length; i++)
        {
            float v = values[i];
            values[i] = v > 0f ? MathF.Pow(v, inv) : 0f;
        }
    }
}

/// <summary>Input range stretch, midtone gamma and output range, on one channel or all.</summary>
internal sealed class LevelsStep : ChannelStep
{
    private readonly double _inBlack;
    private readonly double _inWhite;
    private readonly double _gamma;
    private readonly double _outBlack;
    private readonly double _outWhite;
    private readonly ColorChannel _channel;
    private readonly float _ib;
    private readonly float _invRange;
    private readonly float _inverseGamma;
    private readonly float _ob;
    private readonly float _span;

    public LevelsStep(double inBlack, double inWhite, double gamma, double outBlack, double outWhite, ColorChannel channel, bool linear)
        : base(linear)
    {
        _inBlack = inBlack;
        _inWhite = inWhite;
        _gamma = gamma;
        _outBlack = outBlack;
        _outWhite = outWhite;
        _channel = channel;
        _ib = (float)inBlack;
        _invRange = (float)(1.0 / (inWhite - inBlack));
        _inverseGamma = (float)(1.0 / gamma);
        _ob = (float)outBlack;
        _span = (float)(outWhite - outBlack);
    }

    public override string Name => "levels";

    public override OpParameter[] Parameters() => With(
        P("in_black", _inBlack),
        P("in_white", _inWhite),
        P("gamma", _gamma),
        P("out_black", _outBlack),
        P("out_white", _outWhite),
        P("channel", _channel.ToString()));

    public override void Validate(PixelFormat input)
    {
        base.Validate(input);
        if (input.Model == ColorModel.Gray && _channel != ColorChannel.All)
        {
            throw new ImageOperationException("Gray images have only the All channel.");
        }
    }

    public override bool IsNoOp(PixelFormat input) =>
        _inBlack == 0 && _inWhite == 1 && _gamma == 1 && _outBlack == 0 && _outWhite == 1;

    protected override bool AppliesTo(int channel) => _channel == ColorChannel.All || (int)_channel - 1 == channel;

    protected override void Map(Span<float> values)
    {
        float ib = _ib;
        float inv = _invRange;
        float ig = _inverseGamma;
        float ob = _ob;
        float span = _span;
        bool curve = _gamma != 1.0;
        for (int i = 0; i < values.Length; i++)
        {
            float t = (values[i] - ib) * inv;
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            if (curve)
            {
                t = MathF.Pow(t, ig);
            }

            values[i] = ob + (t * span);
        }
    }
}

/// <summary>A monotone cubic (Fritsch-Carlson) curve through control points; flat outside the first and last point.</summary>
internal sealed class CurvesStep : ChannelStep
{
    private readonly float[] _x;
    private readonly float[] _y;
    private readonly float[] _m;
    private readonly ColorChannel _channel;
    private readonly bool _identity;

    public CurvesStep(ReadOnlySpan<CurvePoint> points, ColorChannel channel, bool linear)
        : base(linear)
    {
        if (points.Length < 2)
        {
            throw new ArgumentException("A curve needs at least two points.", nameof(points));
        }

        var p = points.ToArray();
        foreach (CurvePoint c in p)
        {
            if (!(c.X >= 0 && c.X <= 1 && c.Y >= 0 && c.Y <= 1))
            {
                throw new ArgumentOutOfRangeException(nameof(points), "Curve coordinates must lie in 0..1.");
            }
        }

        Array.Sort(p, static (a, b) => a.X.CompareTo(b.X));
        for (int i = 1; i < p.Length; i++)
        {
            if (p[i].X == p[i - 1].X)
            {
                throw new ArgumentException("Curve points must have distinct X values.", nameof(points));
            }
        }

        int n = p.Length;
        var d = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            d[i] = (p[i + 1].Y - p[i].Y) / (p[i + 1].X - p[i].X);
        }

        var m = new double[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++)
        {
            m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        }

        // Fritsch-Carlson: keep each segment monotone by limiting the tangent magnitudes.
        for (int i = 0; i < n - 1; i++)
        {
            if (d[i] == 0)
            {
                m[i] = 0;
                m[i + 1] = 0;
                continue;
            }

            double a = m[i] / d[i];
            double b = m[i + 1] / d[i];
            double s = (a * a) + (b * b);
            if (s > 9)
            {
                double tau = 3 / Math.Sqrt(s);
                m[i] = tau * a * d[i];
                m[i + 1] = tau * b * d[i];
            }
        }

        _x = new float[n];
        _y = new float[n];
        _m = new float[n];
        bool identity = true;
        for (int i = 0; i < n; i++)
        {
            _x[i] = (float)p[i].X;
            _y[i] = (float)p[i].Y;
            _m[i] = (float)m[i];
            identity &= p[i].X == p[i].Y;
        }

        // Points on y = x are the identity only if the curve spans the whole range (it is flat beyond its ends).
        _identity = identity && p[0].X == 0 && p[n - 1].X == 1;
        _channel = channel;
        Points = p;
    }

    public CurvePoint[] Points { get; }

    public override string Name => "curves";

    public override OpParameter[] Parameters() => With(
        P("points", string.Join(';', Points.Select(static c => Num(c.X) + "," + Num(c.Y)))),
        P("channel", _channel.ToString()));

    public override void Validate(PixelFormat input)
    {
        base.Validate(input);
        if (input.Model == ColorModel.Gray && _channel != ColorChannel.All)
        {
            throw new ImageOperationException("Gray images have only the All channel.");
        }
    }

    public override bool IsNoOp(PixelFormat input) => _identity;

    protected override bool AppliesTo(int channel) => _channel == ColorChannel.All || (int)_channel - 1 == channel;

    protected override void Map(Span<float> values)
    {
        float[] xs = _x;
        float[] ys = _y;
        float[] ms = _m;
        int last = xs.Length - 1;
        for (int i = 0; i < values.Length; i++)
        {
            float v = values[i];
            if (!(v > xs[0]))
            {
                values[i] = ys[0];
                continue;
            }

            if (v >= xs[last])
            {
                values[i] = ys[last];
                continue;
            }

            int s = 0;
            while (v >= xs[s + 1])
            {
                s++;
            }

            float h = xs[s + 1] - xs[s];
            float t = (v - xs[s]) / h;
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = (2f * t3) - (3f * t2) + 1f;
            float h10 = t3 - (2f * t2) + t;
            float h01 = (-2f * t3) + (3f * t2);
            float h11 = t3 - t2;
            values[i] = (h00 * ys[s]) + (h10 * h * ms[s]) + (h01 * ys[s + 1]) + (h11 * h * ms[s + 1]);
        }
    }
}

/// <summary>Maps each channel to 0 or 1 (per-channel mode), or all color channels to black or white by luminance.</summary>
internal sealed class ThresholdStep(double level, bool perChannel, bool linear) : ToneStep
{
    private readonly float _level = (float)level;

    public override string Name => "threshold";

    public override OpParameter[] Parameters() => [P("level", level), P("per_channel", perChannel), P("linear", linear)];

    public override bool ChannelWise(PixelFormat input) => perChannel || input.Model == ColorModel.Gray;

    public override void ApplyChannel(int channel, Span<float> values)
    {
        // 0 and 1 are fixed points of the transfer curve, so the result needs no re-encoding.
        if (linear)
        {
            SrgbTransfer.Decode(values);
        }

        float lv = _level;
        if (linear)
        {
            lv = SrgbTransfer.DecodeOne(lv);
        }

        for (int i = 0; i < values.Length; i++)
        {
            values[i] = values[i] >= lv ? 1f : 0f;
        }
    }

    public override void Apply(ref ToneRow row)
    {
        if (perChannel || row.Colors == 1)
        {
            base.Apply(ref row);
            return;
        }

        Span<float> r = row.Plane(0);
        Span<float> g = row.Plane(1);
        Span<float> b = row.Plane(2);
        float lv = _level;
        if (linear)
        {
            SrgbTransfer.Decode(r);
            SrgbTransfer.Decode(g);
            SrgbTransfer.Decode(b);
            lv = SrgbTransfer.DecodeOne(lv);
        }

        for (int i = 0; i < r.Length; i++)
        {
            float y = (0.2126f * r[i]) + (0.7152f * g[i]) + (0.0722f * b[i]);
            float o = y >= lv ? 1f : 0f;
            r[i] = o;
            g[i] = o;
            b[i] = o;
        }
    }
}

/// <summary>Rounds each channel to one of n evenly spaced levels.</summary>
internal sealed class PosterizeStep(int levels, bool linear) : ChannelStep(linear)
{
    public override string Name => "posterize";

    public override OpParameter[] Parameters() => With(P("levels", (long)levels));

    protected override void Map(Span<float> values)
    {
        float n = levels - 1;
        for (int i = 0; i < values.Length; i++)
        {
            float v = values[i];
            v = v < 0f ? 0f : v > 1f ? 1f : v;
            values[i] = MathF.Floor((v * n) + 0.5f) / n;
        }
    }
}
