using System.Globalization;

namespace OneImage;

/// <summary>
/// One per-pixel color operation. A chain of steps runs as a single pass over each tile (see <see cref="ToneNode"/>). Values in
/// a row are straight (not premultiplied) floats; after every step the row is rounded back to the sample grid of that step's output
/// format, which is exactly what materializing the step into its own image would do, so a fused chain and the same ops applied
/// one by one give identical bytes.
/// </summary>
internal abstract class ToneStep
{
    /// <summary>Language-neutral op name used in the descriptor.</summary>
    public abstract string Name { get; }

    /// <summary>Serializable parameters.</summary>
    public abstract OpParameter[] Parameters();

    /// <summary>The format after this step.</summary>
    public virtual PixelFormat Output(PixelFormat input) => input;

    /// <summary>Throws <see cref="ImageOperationException"/> when the step cannot run on <paramref name="input"/>.</summary>
    public virtual void Validate(PixelFormat input)
    {
        RequireGrayOrRgb(input);
    }

    /// <summary>True when applying the step to <paramref name="input"/> changes nothing at all (identity parameters).</summary>
    public virtual bool IsNoOp(PixelFormat input) => false;

    /// <summary>True when each color channel is mapped independently from its own value (so the step can be tabulated).</summary>
    public virtual bool ChannelWise(PixelFormat input) => false;

    /// <summary>True when the step changes the alpha plane, so its result must be rounded too.</summary>
    public virtual bool ModifiesAlpha => false;

    /// <summary>Maps one color channel in place; only for channel-wise steps. Must be the same function <see cref="Apply"/> uses.</summary>
    public virtual void ApplyChannel(int channel, Span<float> values) => throw new InvalidOperationException("Not a channel-wise step.");

    /// <summary>Applies the step to a row of planes.</summary>
    public virtual void Apply(ref ToneRow row)
    {
        for (int k = 0; k < row.Colors; k++)
        {
            ApplyChannel(k, row.Plane(k));
        }
    }

    /// <summary>Optional direct 8-bit kernel for the step on its own; returns false when not applicable. Must give the same bytes as <see cref="Apply"/>.</summary>
    public virtual bool TryApplyBytes(ReadOnlySpan<byte> source, Span<byte> destination, PixelFormat input, int width) => false;

    public OpDescriptor Descriptor() => new(Name, Parameters());

    protected static void RequireGrayOrRgb(PixelFormat f)
    {
        if (f.Model is not (ColorModel.Gray or ColorModel.Rgb))
        {
            throw new ImageOperationException("Color and tone operations support gray and RGB images; convert CMYK or Lab first.");
        }
    }

    protected static OpParameter P(string name, double value) => new(name, value);

    protected static OpParameter P(string name, bool value) => new(name, value);

    protected static OpParameter P(string name, string value) => new(name, value);

    protected static OpParameter P(string name, long value) => new(name, value);

    protected static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// A channel-wise step with an optional linear-light mode: when <see cref="Linear"/> is set the encoded values are decoded with the
/// sRGB transfer curve, mapped, and encoded again. (The curve is assumed for every RGB and gray image, whatever its embedded profile.)
/// </summary>
internal abstract class ChannelStep : ToneStep
{
    protected ChannelStep(bool linear)
    {
        Linear = linear;
    }

    public bool Linear { get; }

    public sealed override bool ChannelWise(PixelFormat input) => true;

    protected virtual bool AppliesTo(int channel) => true;

    public sealed override void ApplyChannel(int channel, Span<float> values)
    {
        if (!AppliesTo(channel))
        {
            return;
        }

        if (Linear)
        {
            SrgbTransfer.Decode(values);
            Map(values);
            SrgbTransfer.EncodeExact(values);
        }
        else
        {
            Map(values);
        }
    }

    protected abstract void Map(Span<float> values);

    protected OpParameter[] With(params OpParameter[] parameters)
    {
        var all = new OpParameter[parameters.Length + 1];
        parameters.CopyTo(all, 0);
        all[^1] = P("linear", Linear);
        return all;
    }
}
