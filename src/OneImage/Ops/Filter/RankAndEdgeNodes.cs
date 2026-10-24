using System.Buffers;
using System.Buffers.Binary;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Median filter over a square window of the stored samples, channel by channel. 8-bit data uses a sliding histogram, other
/// sample types sort each window as floats. An output tile reads its rectangle plus the radius on every side.
/// </summary>
internal sealed class MedianNode : Node
{
    private readonly int _radius;
    private readonly EdgeMode _edge;

    public MedianNode(Node input, int radius, EdgeMode edge)
        : base(
            input.Width,
            input.Height,
            input.Format,
            new OpDescriptor("median", new OpParameter("radius", (long)radius), new OpParameter("edge", edge.ToString().ToLowerInvariant())),
            input)
    {
        FilterEdge.Validate(edge);
        _radius = radius;
        _edge = edge;
    }

    public override ImageRect RequiredInput(int inputIndex, ImageRect output) =>
        FilterEdge.Need(output, _radius, _radius, Inputs[0].Width, Inputs[0].Height);

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        Node input = Inputs[0];
        PixelFormat fmt = Format;
        ImageRect need = RequiredInput(0, o);
        int r = _radius;
        int c = fmt.ChannelCount;
        int bps = fmt.BytesPerSample;
        int bpp = fmt.BytesPerPixel;
        int pw = o.Width + (2 * r);
        int ph = o.Height + (2 * r);
        int padL = need.X - (o.X - r);
        int padR = o.Right + r - need.Right;
        byte[] bytes = Pools<byte>.Pool.Rent(need.Width * need.Height * bpp);
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        int[] colMap = Pools<int>.Pool.Rent(pw);
        try
        {
            Render.ReadRegion(input, need, bytes, need.Width * bpp, fmt, context);
            for (int i = 0; i < pw; i++)
            {
                int m = FilterEdge.Map(o.X - r + i, input.Width, _edge);
                colMap[i] = m < 0 ? -1 : m - need.X;
                if (m >= 0 && (uint)colMap[i] >= (uint)need.Width)
                {
                    throw new ImageOperationException("A filter margin pixel fell outside the region that was read.");
                }
            }

            if (fmt.Sample == SampleType.U8)
            {
                byte[] padded = Pools<byte>.Pool.Rent(pw * ph * c);
                try
                {
                    for (int i = 0; i < ph; i++)
                    {
                        int lr = FilterEdge.LocalRow(o.Y - r + i, input.Height, need.Y, need.Height, _edge);
                        Span<byte> dst = padded.AsSpan(i * pw * c, pw * c);
                        if (lr < 0)
                        {
                            dst.Clear();
                            continue;
                        }

                        ReadOnlySpan<byte> srow = bytes.AsSpan(lr * need.Width * c, need.Width * c);
                        srow.CopyTo(dst.Slice(padL * c));
                        for (int j = 0; j < padL; j++)
                        {
                            CopyOrClear(srow, colMap[j], c, dst.Slice(j * c, c));
                        }

                        for (int j = pw - padR; j < pw; j++)
                        {
                            CopyOrClear(srow, colMap[j], c, dst.Slice(j * c, c));
                        }
                    }

                    FilterRankKernel.MedianU8(padded, pw * c, c, r, tile.Span, Tile.Size * c, o.Width, o.Height);
                }
                finally
                {
                    Pools<byte>.Pool.Return(padded);
                }

                return tile;
            }

            // 16-bit and floating point: convert to float samples, pad, sort windows, convert back.
            int sampleRow = need.Width * c;
            float[] region = Pools<float>.Pool.Rent(need.Height * sampleRow);
            float[] paddedF = Pools<float>.Pool.Rent(pw * ph * c);
            float[] result = Pools<float>.Pool.Rent(o.Width * o.Height * c);
            float[] window = Pools<float>.Pool.Rent((2 * r + 1) * (2 * r + 1));
            try
            {
                int total = need.Height * sampleRow;
                for (int i = 0; i < total; i++)
                {
                    region[i] = ReadSample(bytes, i * bps, fmt.Sample);
                }

                for (int i = 0; i < ph; i++)
                {
                    int lr = FilterEdge.LocalRow(o.Y - r + i, input.Height, need.Y, need.Height, _edge);
                    Span<float> dst = paddedF.AsSpan(i * pw * c, pw * c);
                    if (lr < 0)
                    {
                        dst.Clear();
                        continue;
                    }

                    ReadOnlySpan<float> srow = region.AsSpan(lr * sampleRow, sampleRow);
                    srow.CopyTo(dst.Slice(padL * c));
                    for (int j = 0; j < padL; j++)
                    {
                        CopyOrClear(srow, colMap[j], c, dst.Slice(j * c, c));
                    }

                    for (int j = pw - padR; j < pw; j++)
                    {
                        CopyOrClear(srow, colMap[j], c, dst.Slice(j * c, c));
                    }
                }

                FilterRankKernel.MedianFloat(paddedF, pw * c, c, r, result, o.Width * c, o.Width, o.Height, window);
                for (int y = 0; y < o.Height; y++)
                {
                    Span<byte> drow = tile.Span.Slice(y * Tile.Size * bpp, o.Width * bpp);
                    for (int i = 0; i < o.Width * c; i++)
                    {
                        WriteSample(drow, i * bps, fmt.Sample, result[(y * o.Width * c) + i]);
                    }
                }

                return tile;
            }
            finally
            {
                Pools<float>.Pool.Return(region);
                Pools<float>.Pool.Return(paddedF);
                Pools<float>.Pool.Return(result);
                Pools<float>.Pool.Return(window);
            }
        }
        catch
        {
            tile.Release();
            throw;
        }
        finally
        {
            Pools<byte>.Pool.Return(bytes);
            Pools<int>.Pool.Return(colMap);
        }
    }

    private static void CopyOrClear<T>(ReadOnlySpan<T> row, int column, int channels, Span<T> dst)
    {
        if (column < 0)
        {
            dst.Clear();
        }
        else
        {
            row.Slice(column * channels, channels).CopyTo(dst);
        }
    }

    private static float ReadSample(byte[] b, int offset, SampleType t) => t switch
    {
        SampleType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset)),
        SampleType.F16 => (float)BinaryPrimitives.ReadHalfLittleEndian(b.AsSpan(offset)),
        _ => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset)),
    };

    private static void WriteSample(Span<byte> b, int offset, SampleType t, float v)
    {
        switch (t)
        {
            case SampleType.U16:
                BinaryPrimitives.WriteUInt16LittleEndian(b[offset..], (ushort)v);
                break;
            case SampleType.F16:
                BinaryPrimitives.WriteHalfLittleEndian(b[offset..], (Half)v);
                break;
            default:
                BinaryPrimitives.WriteSingleLittleEndian(b[offset..], v);
                break;
        }
    }
}

/// <summary>
/// Sobel gradient magnitude of the luminance. The input is converted to a float gray plane the way <c>ConvertTo</c> would
/// (Rec. 709 weights on stored values, alpha dropped), the 3x3 Sobel sums are taken, and the magnitude is converted to a gray
/// image of the input's sample type.
/// </summary>
internal sealed class SobelNode : Node
{
    private readonly EdgeMode _edge;
    private readonly float _factor;

    public SobelNode(Node input, double scale, EdgeMode edge)
        : base(
            input.Width,
            input.Height,
            new PixelFormat(ColorModel.Gray, input.Format.Sample),
            new OpDescriptor("sobel", new OpParameter("scale", scale), new OpParameter("edge", edge.ToString().ToLowerInvariant())),
            input)
    {
        FilterEdge.Validate(edge);
        if (input.Format.Model is not (ColorModel.Gray or ColorModel.Rgb))
        {
            throw new ImageOperationException($"Sobel is defined for gray and RGB images, not {input.Format.Model}.");
        }

        _edge = edge;
        _factor = (float)(scale * 0.25);
    }

    public override ImageRect RequiredInput(int inputIndex, ImageRect output) =>
        FilterEdge.Need(output, 1, 1, Inputs[0].Width, Inputs[0].Height);

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        Node input = Inputs[0];
        ImageRect need = RequiredInput(0, o);
        int ibpp = input.Format.BytesPerPixel;
        int obpp = Format.BytesPerPixel;
        int padL = need.X - (o.X - 1);
        int padR = o.Right + 1 - need.Right;
        int rowF = o.Width + 2;
        byte[] bytes = Pools<byte>.Pool.Rent(need.Width * need.Height * ibpp);
        float[] gray = Pools<float>.Pool.Rent((need.Height + 1) * rowF);
        float[] mag = Pools<float>.Pool.Rent(o.Width);
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            Render.ReadRegion(input, need, bytes, need.Width * ibpp, input.Format, context);
            for (int r = 0; r < need.Height; r++)
            {
                Span<float> row = gray.AsSpan(r * rowF, rowF);
                ConvertKernel.ConvertRow(bytes.AsSpan(r * need.Width * ibpp, need.Width * ibpp), input.Format, SpanCast.AsBytes(row[padL..]), PixelFormat.GrayF32, need.Width);
                FilterEdge.FillRowPadding(row, 1, o.X - 1, padL, padR, need.X, need.Width, input.Width, _edge);
            }

            gray.AsSpan(need.Height * rowF, rowF).Clear();
            for (int y = 0; y < o.Height; y++)
            {
                int a = FilterEdge.LocalRow(o.Y + y - 1, input.Height, need.Y, need.Height, _edge);
                int m = FilterEdge.LocalRow(o.Y + y, input.Height, need.Y, need.Height, _edge);
                int b = FilterEdge.LocalRow(o.Y + y + 1, input.Height, need.Y, need.Height, _edge);
                FilterRankKernel.SobelRow(
                    gray.AsSpan((a < 0 ? need.Height : a) * rowF, rowF),
                    gray.AsSpan(m * rowF, rowF),
                    gray.AsSpan((b < 0 ? need.Height : b) * rowF, rowF),
                    _factor,
                    mag.AsSpan(0, o.Width));
                ConvertKernel.ConvertRow(SpanCast.AsBytes<float>(mag.AsSpan(0, o.Width)), PixelFormat.GrayF32, tile.Span.Slice(y * Tile.Size * obpp, o.Width * obpp), Format, o.Width);
            }

            return tile;
        }
        catch
        {
            tile.Release();
            throw;
        }
        finally
        {
            Pools<byte>.Pool.Return(bytes);
            Pools<float>.Pool.Return(gray);
            Pools<float>.Pool.Return(mag);
        }
    }
}

/// <summary>
/// The unsharp combination of an image (input 0) and a blurred copy of it (input 1, same size, any float or integer format
/// read the same way as the original). Used when the blur is too wide to compute per tile exactly.
/// </summary>
internal sealed class UnsharpCombineNode : Node
{
    private readonly ResampleLayout _layout;
    private readonly ResampleLayout _blurLayout;
    private readonly float _amount;
    private readonly float _threshold;

    public UnsharpCombineNode(Node original, Node blurred, double amount, double threshold, bool linear, OpDescriptor descriptor)
        : base(original.Width, original.Height, original.Format, descriptor, original, blurred)
    {
        _layout = new ResampleLayout(original.Format, linear);
        _blurLayout = new ResampleLayout(blurred.Format, linear);
        _amount = (float)amount;
        _threshold = (float)threshold;
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        Tile a = Inputs[0].GetTile(tx, ty, context);
        Tile? b = null;
        int p = _layout.Stride;
        float[] orig = Pools<float>.Pool.Rent(o.Width * p);
        float[] blur = Pools<float>.Pool.Rent(o.Width * p);
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            b = Inputs[1].GetTile(tx, ty, context);
            int abpp = Inputs[0].Format.BytesPerPixel;
            int bbpp = Inputs[1].Format.BytesPerPixel;
            int obpp = Format.BytesPerPixel;
            for (int y = 0; y < o.Height; y++)
            {
                ResampleKernel.UnpackRow(a.Span.Slice(y * Tile.Size * abpp, o.Width * abpp), orig, o.Width, _layout);
                ResampleKernel.UnpackRow(b.Span.Slice(y * Tile.Size * bbpp, o.Width * bbpp), blur, o.Width, _blurLayout);
                UnsharpKernel.Combine(orig, blur.AsSpan(0, o.Width * p), o.Width, _layout, _amount, _threshold);
                ResampleKernel.PackRow(blur, tile.Span.Slice(y * Tile.Size * obpp, o.Width * obpp), o.Width, _layout);
            }

            return tile;
        }
        catch
        {
            tile.Release();
            throw;
        }
        finally
        {
            a.Release();
            b?.Release();
            Pools<float>.Pool.Return(orig);
            Pools<float>.Pool.Return(blur);
        }
    }
}
