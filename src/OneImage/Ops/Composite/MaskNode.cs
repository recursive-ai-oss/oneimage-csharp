using System.Buffers;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Multiplies the coverage of the base by a per-pixel mask taken from a second image (alpha, or luminance times alpha). An output tile reads the
/// base tile at the same position and only the part of the mask that lands on it; outside the mask the result is transparent.
/// The mask is already 8-bit straight RGBA.
/// </summary>
internal sealed class MaskNode : Node
{
    // Luminance weights of the CSS Masking luminance-to-alpha conversion (0.2125, 0.7154, 0.0721), in 16-bit fixed point summing to 65536.
    private const int WeightR = 13926;
    private const int WeightG = 46885;
    private const int WeightB = 4725;

    private static readonly int[] s_encoded = BuildTable(linear: false);
    private static readonly int[] s_linear = BuildTable(linear: true);

    private readonly int _x;
    private readonly int _y;
    private readonly MaskChannel _channel;
    private readonly bool _linear;
    private readonly PixelFormat _work;

    public MaskNode(Node baseNode, Node mask, int x, int y, MaskChannel channel, bool linear)
        : base(
            baseNode.Width,
            baseNode.Height,
            baseNode.Format,
            new OpDescriptor(
                "mask",
                new OpParameter("channel", channel == MaskChannel.Alpha ? "alpha" : "luminance"),
                new OpParameter("linear", linear),
                new OpParameter("x", (long)x),
                new OpParameter("y", (long)y)),
            baseNode,
            mask)
    {
        _x = x;
        _y = y;
        _channel = channel;
        _linear = linear;
        _work = new PixelFormat(baseNode.Format.Model, SampleType.F32, AlphaMode.Premultiplied);
    }

    private static int[] BuildTable(bool linear)
    {
        var t = new int[256];
        for (int i = 0; i < 256; i++)
        {
            if (!linear)
            {
                t[i] = i * 257;
                continue;
            }

            double c = i / 255.0;
            double l = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            t[i] = (int)Math.Round(l * 65535.0);
        }

        return t;
    }

    private ImageRect Hit(ImageRect output)
    {
        Node mk = Inputs[1];
        long x0 = Math.Max((long)output.X, _x);
        long y0 = Math.Max((long)output.Y, _y);
        long x1 = Math.Min((long)output.Right, (long)_x + mk.Width);
        long y1 = Math.Min((long)output.Bottom, (long)_y + mk.Height);
        return x0 >= x1 || y0 >= y1 ? new ImageRect(0, 0, 0, 0) : new ImageRect((int)(x0 - _x), (int)(y0 - _y), (int)(x1 - x0), (int)(y1 - y0));
    }

    public override ImageRect RequiredInput(int inputIndex, ImageRect output) => inputIndex == 0 ? output : Hit(output);

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        ImageRect need = Hit(o);
        Tile back = Inputs[0].GetTile(tx, ty, context);
        Tile? result = null;
        byte[]? scratch = null;
        try
        {
            result = Tile.Rent(back.Length, clear: true);
            if (need.IsEmpty)
            {
                return result;
            }

            int mstride = need.Width * 4;
            scratch = ArrayPool<byte>.Shared.Rent(mstride * need.Height);
            Render.ReadRegion(Inputs[1], need, scratch, mstride, PixelFormat.Rgba8, context);
            int bpp = Format.BytesPerPixel;
            int tileStride = Tile.Size * bpp;
            int col = (need.X + _x) - o.X;
            int row0 = (need.Y + _y) - o.Y;
            bool fast = Format == PixelFormat.Rgba8;
            int[] lut = _linear ? s_linear : s_encoded;
            int[]? cover = null;
            float[]? work = null;
            try
            {
                cover = ArrayPool<int>.Shared.Rent(need.Width);
                if (!fast)
                {
                    work = ArrayPool<float>.Shared.Rent(need.Width * _work.ChannelCount);
                }

                for (int r = 0; r < need.Height; r++)
                {
                    ReadOnlySpan<byte> mrow = scratch.AsSpan(r * mstride, mstride);
                    Coverage(mrow, cover, need.Width, lut);
                    int at = ((row0 + r) * tileStride) + (col * bpp);
                    ReadOnlySpan<byte> src = back.Span.Slice(at, need.Width * bpp);
                    Span<byte> dst = result.Span.Slice(at, need.Width * bpp);
                    if (fast)
                    {
                        for (int i = 0; i < need.Width; i++)
                        {
                            int a = src[(i * 4) + 3];
                            int a2 = Mul255(a, cover[i]);
                            if (a2 != 0)
                            {
                                dst[i * 4] = src[i * 4];
                                dst[(i * 4) + 1] = src[(i * 4) + 1];
                                dst[(i * 4) + 2] = src[(i * 4) + 2];
                                dst[(i * 4) + 3] = (byte)a2;
                            }
                        }
                    }
                    else
                    {
                        int channels = _work.ChannelCount;
                        int n = need.Width * channels;
                        float[] wf = work!;
                        Span<byte> wb = SpanCast.AsBytes(wf.AsSpan(0, n));
                        ConvertKernel.ConvertRow(src, Format, wb, _work, need.Width);
                        for (int i = 0; i < need.Width; i++)
                        {
                            float k = cover[i] / 255f;
                            for (int c = 0; c < channels; c++)
                            {
                                wf[(i * channels) + c] *= k;
                            }
                        }

                        ConvertKernel.ConvertRow(wb, _work, dst, Format, need.Width);
                    }
                }
            }
            finally
            {
                if (cover is not null)
                {
                    ArrayPool<int>.Shared.Return(cover);
                }

                if (work is not null)
                {
                    ArrayPool<float>.Shared.Return(work);
                }
            }

            return result;
        }
        catch
        {
            result?.Release();
            throw;
        }
        finally
        {
            back.Release();
            if (scratch is not null)
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }
    }

    private void Coverage(ReadOnlySpan<byte> row, int[] cover, int width, int[] lut)
    {
        if (_channel == MaskChannel.Alpha)
        {
            for (int i = 0; i < width; i++)
            {
                cover[i] = row[(i * 4) + 3];
            }

            return;
        }

        for (int i = 0; i < width; i++)
        {
            int o = i * 4;
            int lum = (int)((((long)WeightR * lut[row[o]]) + ((long)WeightG * lut[row[o + 1]]) + ((long)WeightB * lut[row[o + 2]]) + 32768) >> 16);

            // lum is 0..65535 and alpha 0..255: lum·alpha/65535 is the coverage on a 0..255 scale, rounded to nearest.
            cover[i] = ((lum * row[o + 3]) + 32767) / 65535;
        }
    }

    private static int Mul255(int a, int b)
    {
        int t = (a * b) + 128;
        return (t + (t >> 8)) >> 8;
    }
}
