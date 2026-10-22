using System.Buffers;
using System.Buffers.Binary;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Oil-paint effect. Around every pixel a window of <c>before + after + 1</c> pixels per side is examined (clamped at the
/// image edge); each pixel in it is put into one of <c>levels</c> intensity bins by its mean of red, green and blue, and the
/// output is the mean color of the most populated bin (the highest bin wins a tie). Alpha is the center pixel's own alpha.
/// Histograms slide along each row, so the cost per pixel grows with the window height, not its area. Sums are exact integers,
/// so results do not depend on tile order or thread count. Samples are 8-bit for 8-bit images and 16-bit for everything else.
/// </summary>
internal sealed class OilPaintNode : Node
{
    private readonly int _levels;
    private readonly int _before;
    private readonly int _after;
    private readonly PixelFormat _work;
    private readonly int _scale;

    public OilPaintNode(Node input, int levels, int brushSize, int before, int after)
        : base(
            input.Width,
            input.Height,
            input.Format,
            new OpDescriptor("oil_paint", new OpParameter("levels", (long)levels), new OpParameter("brush_size", (long)brushSize)),
            input)
    {
        if (input.Format.Model is not (ColorModel.Gray or ColorModel.Rgb))
        {
            throw new ImageOperationException($"OilPaint is defined for gray and RGB images, not {input.Format.Model}.");
        }

        _levels = levels;
        _before = before;
        _after = after;
        SampleType st = input.Format.Sample == SampleType.U8 ? SampleType.U8 : SampleType.U16;
        _work = new PixelFormat(input.Format.Model, st, input.Format.HasAlpha ? AlphaMode.Straight : AlphaMode.None);
        _scale = st == SampleType.U8 ? 255 : 65535;
    }

    public override ImageRect RequiredInput(int inputIndex, ImageRect output)
    {
        Node input = Inputs[0];
        int x0 = Math.Max(0, output.X - _before);
        int y0 = Math.Max(0, output.Y - _before);
        int x1 = Math.Min(input.Width, output.Right + _after);
        int y1 = Math.Min(input.Height, output.Bottom + _after);
        return new ImageRect(x0, y0, x1 - x0, y1 - y0);
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        Node input = Inputs[0];
        ImageRect need = RequiredInput(0, o);
        int c = Format.ColorChannels;
        bool alpha = Format.HasAlpha;
        int ch = c + (alpha ? 1 : 0);
        int bps = _work.BytesPerSample;
        int wbpp = ch * bps;
        int ws = _before + _after + 1;
        int pw = o.Width + ws - 1;
        int ph = o.Height + ws - 1;
        byte[] region = ArrayPool<byte>.Shared.Rent(need.Width * need.Height * wbpp);
        int[] bins = ArrayPool<int>.Shared.Rent(pw * ph);
        int[] vals = ArrayPool<int>.Shared.Rent(pw * ph * c);
        int[] cnt = ArrayPool<int>.Shared.Rent(_levels);
        int[] sum = ArrayPool<int>.Shared.Rent(_levels * c);
        int[] colMap = ArrayPool<int>.Shared.Rent(pw);
        byte[] outRow = ArrayPool<byte>.Shared.Rent(o.Width * wbpp);
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            Render.ReadRegion(input, need, region, need.Width * wbpp, _work, context);
            for (int i = 0; i < pw; i++)
            {
                colMap[i] = Math.Clamp(o.X - _before + i, 0, input.Width - 1) - need.X;
            }

            float inv = _scale;
            for (int j = 0; j < ph; j++)
            {
                int sy = Math.Clamp(o.Y - _before + j, 0, input.Height - 1) - need.Y;
                int rowBase = sy * need.Width * wbpp;
                for (int i = 0; i < pw; i++)
                {
                    int p = rowBase + (colMap[i] * wbpp);
                    int di = (j * pw) + i;
                    // Intensity bin: the mean of the three channels as a fraction of full scale, scaled to the bin count and
                    // rounded to even, the same arithmetic in float as a straightforward floating-point implementation.
                    float intensity;
                    if (c == 3)
                    {
                        int r = Sample(region, p, 0, bps);
                        int g = Sample(region, p, 1, bps);
                        int b = Sample(region, p, 2, bps);
                        vals[di * 3] = r;
                        vals[(di * 3) + 1] = g;
                        vals[(di * 3) + 2] = b;
                        intensity = ((b / inv) + (g / inv) + (r / inv)) / 3f;
                    }
                    else
                    {
                        int v = Sample(region, p, 0, bps);
                        vals[di] = v;
                        float f = v / inv;
                        intensity = (f + f + f) / 3f;
                    }

                    bins[di] = (int)MathF.Round(intensity * (_levels - 1));
                }
            }

            int obpp = Format.BytesPerPixel;
            for (int y = 0; y < o.Height; y++)
            {
                Array.Clear(cnt, 0, _levels);
                Array.Clear(sum, 0, _levels * c);
                for (int i = 0; i < ws; i++)
                {
                    Slide(bins, vals, cnt, sum, y, ws, pw, i, c, 1);
                }

                for (int x = 0; x < o.Width; x++)
                {
                    int best = 0;
                    int bestCount = 0;
                    for (int i = _levels - 1; i >= 0; i--)
                    {
                        if (cnt[i] > bestCount)
                        {
                            bestCount = cnt[i];
                            best = i;
                        }
                    }

                    int ob = x * wbpp;
                    for (int k = 0; k < c; k++)
                    {
                        // Round half up: floor((2 * sum + n) / (2 * n)).
                        int mean = (int)(((2L * sum[(best * c) + k]) + bestCount) / (2L * bestCount));
                        Put(outRow, ob + (k * bps), mean, bps);
                    }

                    if (alpha)
                    {
                        int sx = colMap[x + _before];
                        int sy = Math.Clamp(o.Y + y, 0, input.Height - 1) - need.Y;
                        Put(outRow, ob + (c * bps), Sample(region, (sy * need.Width * wbpp) + (sx * wbpp), c, bps), bps);
                    }

                    if (x + 1 < o.Width)
                    {
                        Slide(bins, vals, cnt, sum, y, ws, pw, x, c, -1);
                        Slide(bins, vals, cnt, sum, y, ws, pw, x + ws, c, 1);
                    }
                }

                ConvertKernel.ConvertRow(outRow.AsSpan(0, o.Width * wbpp), _work, tile.Span.Slice(y * Tile.Size * obpp, o.Width * obpp), Format, o.Width);
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
            ArrayPool<byte>.Shared.Return(region);
            ArrayPool<int>.Shared.Return(bins);
            ArrayPool<int>.Shared.Return(vals);
            ArrayPool<int>.Shared.Return(cnt);
            ArrayPool<int>.Shared.Return(sum);
            ArrayPool<int>.Shared.Return(colMap);
            ArrayPool<byte>.Shared.Return(outRow);
        }
    }

    // Adds (sign 1) or removes (sign -1) one column of the window starting at padded row `top`.
    private static void Slide(int[] bins, int[] vals, int[] cnt, int[] sum, int top, int rows, int pw, int column, int c, int sign)
    {
        for (int r = 0; r < rows; r++)
        {
            int idx = ((top + r) * pw) + column;
            int bin = bins[idx];
            cnt[bin] += sign;
            int s = bin * c;
            int v = idx * c;
            for (int k = 0; k < c; k++)
            {
                sum[s + k] += sign * vals[v + k];
            }
        }
    }

    private static int Sample(byte[] b, int pixelOffset, int channel, int bps) =>
        bps == 1 ? b[pixelOffset + channel] : BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(pixelOffset + (channel * 2)));

    private static void Put(byte[] b, int offset, int value, int bps)
    {
        if (bps == 1)
        {
            b[offset] = (byte)value;
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(offset), (ushort)value);
        }
    }
}
