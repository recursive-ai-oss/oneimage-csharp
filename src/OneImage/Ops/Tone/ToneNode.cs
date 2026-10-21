using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// A chain of per-pixel color steps computed in one pass per tile. Building a step on top of another <see cref="ToneNode"/> (or on a gray/RGB
/// <see cref="ConvertNode"/>) extends the chain instead of adding a node, so N adjacent color ops materialize one tile set, not N.
/// <para>
/// Execution picks the cheapest path that gives the same bytes as running the steps one by one:
/// a tabulated path (one 256- or 65536-entry table per color channel) when every step maps channels independently and keeps the
/// pixel format; a direct kernel for a lone step that has one (8-bit grayscale); otherwise a float path over planes that rounds
/// to the sample grid after every step. Because rounding happens between steps in every path, fusing never changes a byte.
/// </para>
/// </summary>
internal sealed class ToneNode : Node
{
    private readonly ToneStep[] _steps;
    private readonly PixelFormat[] _formats;
    private volatile Compiled? _compiled;

    private ToneNode(Node source, ToneStep[] steps)
        : base(source.Width, source.Height, Chain(source.Format, steps, out PixelFormat[] formats), Describe(steps), source)
    {
        _steps = steps;
        _formats = formats;
    }

    /// <summary>Gets the number of fused steps.</summary>
    public int StepCount => _steps.Length;

    /// <summary>Applies <paramref name="step"/> on top of <paramref name="input"/>, fusing with an adjacent color chain. Identity steps return the input.</summary>
    public static Node Build(Node input, ToneStep step)
    {
        if (step.IsNoOp(input.Format))
        {
            return input;
        }

        var chain = new List<ToneStep> { step };
        Node source = input;
        while (true)
        {
            if (source is ToneNode t)
            {
                chain.InsertRange(0, t._steps);
                source = t.Inputs[0];
            }
            else if (source is ConvertNode c && IsGrayOrRgb(c.Format) && IsGrayOrRgb(c.Inputs[0].Format))
            {
                chain.Insert(0, new ConvertStep(c.Format));
                source = c.Inputs[0];
            }
            else
            {
                break;
            }
        }

        return new ToneNode(source, chain.ToArray());
    }

    /// <summary>Gets the descriptors of the fused steps, in order.</summary>
    public IEnumerable<OpDescriptor> StepDescriptors() => _steps.Select(static s => s.Descriptor());

    private static bool IsGrayOrRgb(PixelFormat f) => f.Model is ColorModel.Gray or ColorModel.Rgb;

    private static PixelFormat Chain(PixelFormat input, ToneStep[] steps, out PixelFormat[] formats)
    {
        formats = new PixelFormat[steps.Length];
        PixelFormat f = input;
        for (int i = 0; i < steps.Length; i++)
        {
            steps[i].Validate(f);
            f = steps[i].Output(f);
            formats[i] = f;
        }

        return f;
    }

    private static OpDescriptor Describe(ToneStep[] steps)
    {
        if (steps.Length == 1)
        {
            return steps[0].Descriptor();
        }

        var parameters = new OpParameter[steps.Length + 1];
        parameters[0] = new OpParameter("count", (long)steps.Length);
        for (int i = 0; i < steps.Length; i++)
        {
            parameters[i + 1] = new OpParameter("step" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), steps[i].Descriptor().ToString());
        }

        return new OpDescriptor("fused", parameters);
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        Compiled plan = _compiled ??= Compile();
        Tile src = Inputs[0].GetTile(tx, ty, context);
        try
        {
            Tile dst = Tile.Rent(Tile.ByteSize(Format), clear: false);
            try
            {
                Run(plan, src.Span, dst.Span, TileRect(tx, ty));
                return dst;
            }
            catch
            {
                dst.Release();
                throw;
            }
        }
        finally
        {
            src.Release();
        }
    }

    private void Run(Compiled plan, ReadOnlySpan<byte> src, Span<byte> dst, ImageRect rect)
    {
        PixelFormat inF = Inputs[0].Format;
        PixelFormat outF = Format;
        int sbpp = inF.BytesPerPixel;
        int dbpp = outF.BytesPerPixel;
        int w = rect.Width;
        Span<float> buf = stackalloc float[4 * Tile.Size];
        for (int y = 0; y < rect.Height; y++)
        {
            ReadOnlySpan<byte> s = src.Slice(y * Tile.Size * sbpp, w * sbpp);
            Span<byte> d = dst.Slice(y * Tile.Size * dbpp, w * dbpp);
            if (plan.Lut8 != null)
            {
                ToneKernel.ApplyLut8(s, d, w, inF.ChannelCount, inF.ColorChannels, plan.Lut8);
                continue;
            }

            if (plan.Lut16 != null)
            {
                ToneKernel.ApplyLut16(s, d, w, inF.ChannelCount, inF.ColorChannels, plan.Lut16);
                continue;
            }

            if (_steps.Length == 1 && _steps[0].TryApplyBytes(s, d, inF, w))
            {
                continue;
            }

            var row = new ToneRow { Buf = buf, Width = w };
            ToneKernel.UnpackRow(s, inF, ref row);
            for (int i = 0; i < _steps.Length; i++)
            {
                _steps[i].Apply(ref row);
                row.Sample = _formats[i].Sample;
                if (i < _steps.Length - 1)
                {
                    ToneKernel.QuantizeRow(ref row, _formats[i], _steps[i].ModifiesAlpha);
                }
            }

            ToneKernel.PackRow(ref row, outF, d);
        }
    }

    private Compiled Compile()
    {
        PixelFormat f = Inputs[0].Format;
        bool tabulate = f.Sample is SampleType.U8 or SampleType.U16 && f.Alpha != AlphaMode.Premultiplied;
        for (int i = 0; i < _steps.Length && tabulate; i++)
        {
            tabulate = _steps[i].ChannelWise(f) && _formats[i] == f;
        }

        if (!tabulate)
        {
            return new Compiled(null, null);
        }

        int colors = f.ColorChannels;
        if (f.Sample == SampleType.U8)
        {
            var tables = new byte[colors][];
            var ramp = new float[256];
            for (int k = 0; k < colors; k++)
            {
                for (int i = 0; i < 256; i++)
                {
                    ramp[i] = i / 255f;
                }

                ApplyAll(k, ramp, SampleType.U8);
                tables[k] = new byte[256];
                for (int i = 0; i < 256; i++)
                {
                    tables[k][i] = ToneKernel.StoreU8(ramp[i]);
                }
            }

            return new Compiled(tables, null);
        }
        else
        {
            var tables = new ushort[colors][];
            var ramp = new float[65536];
            for (int k = 0; k < colors; k++)
            {
                for (int i = 0; i < 65536; i++)
                {
                    ramp[i] = i / 65535f;
                }

                ApplyAll(k, ramp, SampleType.U16);
                tables[k] = new ushort[65536];
                for (int i = 0; i < 65536; i++)
                {
                    tables[k][i] = ToneKernel.StoreU16(ramp[i]);
                }
            }

            return new Compiled(null, tables);
        }
    }

    private void ApplyAll(int channel, float[] ramp, SampleType sample)
    {
        foreach (ToneStep s in _steps)
        {
            s.ApplyChannel(channel, ramp);
            ToneKernel.RoundSpan(ramp, sample);
        }
    }

    private sealed record Compiled(byte[][]? Lut8, ushort[][]? Lut16);
}
