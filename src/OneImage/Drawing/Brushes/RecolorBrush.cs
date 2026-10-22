using System.Numerics;

namespace OneImage.Drawing;

/// <summary>Paints the target color with an alpha that falls off with the backdrop's distance from the source color.</summary>
internal sealed class RecolorBrush : Brush
{
    private readonly Rgba32 _sourceColor, _targetColor;
    private readonly Vector4 _source, _target;
    private readonly float _threshold;

    public RecolorBrush(Rgba32 source, Rgba32 target, float threshold)
        : this(source, target, new Vector4(source.R, source.G, source.B, source.A) * (1f / 255f), new Vector4(target.R, target.G, target.B, target.A) * (1f / 255f), threshold)
    {
    }

    private RecolorBrush(Rgba32 sourceColor, Rgba32 targetColor, Vector4 source, Vector4 target, float threshold)
    {
        if (!float.IsFinite(threshold) || threshold < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "The threshold must be finite and not negative.");
        }

        _sourceColor = sourceColor;
        _targetColor = targetColor;
        _source = source;
        _target = target;
        _threshold = threshold;
    }

    public override bool ReadsBackdrop => true;

    internal override Brush Linearized(ColorMap map) => new RecolorBrush(_sourceColor, _targetColor, map.Map(_source), map.Map(_target), _threshold);

    // Without a backdrop there is nothing to compare: paint the target at full strength.
    public override void FillRow(int x, int y, Span<float> rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            rgba[i] = _target.X;
            rgba[i + 1] = _target.Y;
            rgba[i + 2] = _target.Z;
            rgba[i + 3] = _target.W;
        }
    }

    public override void FillRow(int x, int y, ReadOnlySpan<float> backdrop, Span<float> rgba)
    {
        float scale = _threshold > 0f ? 1f / (4f * _threshold) : 0f;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            float dr = backdrop[i] - _source.X, dg = backdrop[i + 1] - _source.Y, db = backdrop[i + 2] - _source.Z, da = backdrop[i + 3] - _source.W;
            float d2 = (dr * dr) + (dg * dg) + (db * db) + (da * da);
            float w = _threshold > 0f ? 1f - (d2 * scale) : (d2 < 1e-8f ? 1f : 0f);
            w = w > 0f ? MathF.Min(w, 1f) : 0f;
            rgba[i] = _target.X;
            rgba[i + 1] = _target.Y;
            rgba[i + 2] = _target.Z;
            rgba[i + 3] = _target.W * w;
        }
    }

    public override IReadOnlyList<OpParameter> Describe() =>
        [new("brush", "recolor"), new("source", Hex(_sourceColor)), new("target", Hex(_targetColor)), new("threshold", (double)_threshold)];
}
