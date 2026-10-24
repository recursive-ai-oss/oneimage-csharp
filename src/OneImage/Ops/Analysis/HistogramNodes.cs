using System.Buffers;

namespace OneImage;

/// <summary>Shared helpers of the histogram-based nodes.</summary>
internal static class Luminance
{
    // Rec. 709 weights applied to channel/255 in float, in the order x*a + y*b + z*c, so the result does not depend on the hardware.
    private static readonly float[] s_r = Table(0.2126f);
    private static readonly float[] s_g = Table(0.7152f);
    private static readonly float[] s_b = Table(0.0722f);

    private static float[] Table(float weight)
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
        {
            t[i] = (i / 255f) * weight;
        }

        return t;
    }

    /// <summary>The luminance level of a pixel: round(Y * (levels - 1)), ties to even.</summary>
    public static int Level(byte r, byte g, byte b, int levels) =>
        (int)MathF.Round((s_r[r] + s_g[g] + s_b[b]) * (levels - 1));

    /// <summary>
    /// Turns a histogram, plus <paramref name="add"/> in every bin, into the output fraction for every level: (cdf - cdfMin) / (pixels - cdfMin),
    /// where cdfMin is the cumulative count at the first occupied level. A histogram with one occupied level (or none) maps everything to 0.
    /// </summary>
    public static void BuildMap(ReadOnlySpan<int> hist, long pixels, double add, float[] map)
    {
        double cum = 0;
        double cdfMin = 0;
        bool found = false;
        for (int i = 0; i < hist.Length; i++)
        {
            cum += hist[i] + add;
            if (!found && hist[i] + add != 0)
            {
                cdfMin = cum;
                found = true;
            }

            map[i] = (float)(cum - cdfMin);
        }

        double denom = pixels - cdfMin;
        for (int i = 0; i < hist.Length; i++)
        {
            map[i] = denom <= 0 ? 0f : Math.Clamp((float)(map[i] / denom), 0f, 1f);
        }
    }

    /// <summary>The luminance of a pixel as a fraction of full scale.</summary>
    public static float Value(byte r, byte g, byte b) => s_r[r] + s_g[g] + s_b[b];

    public static byte ToByte(float v) => (byte)Math.Clamp((v * 255f) + 0.5f, 0f, 255f);
}

/// <summary>
/// Applies the equalization curve of the luminance histogram to the color channels as a tone curve, either to each channel value on
/// its own or, synchronized, as a gain on the pixel that moves its luminance to the curve (keeping hue). The luminance histogram is
/// counted once, exactly, on first use.
/// </summary>
internal sealed class AutoLevelNode : Node
{
    private readonly bool _sync;
    private readonly object _gate = new();
    private float[]? _map;
    private byte[]? _lut;

    public AutoLevelNode(Node input, bool syncChannels)
        : base(input.Width, input.Height, input.Format, new OpDescriptor("autoLevel", new OpParameter("syncChannels", syncChannels)), input)
    {
        _sync = syncChannels;
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        if (_map == null)
        {
            Prepare(context);
        }

        float[] map = _map!;
        byte[] lut = _lut!;
        ImageRect rect = TileRect(tx, ty);
        int bpp = Format.BytesPerPixel;
        Tile src = Inputs[0].GetTile(tx, ty, context);
        try
        {
            Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
            ReadOnlySpan<byte> s = src.Span;
            Span<byte> d = tile.Span;
            for (int y = 0; y < rect.Height; y++)
            {
                for (int x = 0; x < rect.Width; x++)
                {
                    int p = ((y * Tile.Size) + x) * bpp;
                    if (_sync)
                    {
                        // Gain = curve(Y) / Y, so a gray pixel lands exactly on the curve and a color keeps its channel ratios.
                        float lum = Luminance.Value(s[p], s[p + 1], s[p + 2]) * 255f;
                        float target = map[Luminance.Level(s[p], s[p + 1], s[p + 2], 256)] * 255f;
                        float gain = lum > 0f ? target / lum : 0f;
                        d[p] = Luminance.ToByte(s[p] * gain / 255f);
                        d[p + 1] = Luminance.ToByte(s[p + 1] * gain / 255f);
                        d[p + 2] = Luminance.ToByte(s[p + 2] * gain / 255f);
                    }
                    else
                    {
                        d[p] = lut[s[p]];
                        d[p + 1] = lut[s[p + 1]];
                        d[p + 2] = lut[s[p + 2]];
                    }

                    if (bpp == 4)
                    {
                        d[p + 3] = s[p + 3];
                    }
                }
            }

            return tile;
        }
        finally
        {
            src.Release();
        }
    }

    private void Prepare(RenderContext context)
    {
        lock (_gate)
        {
            if (_map != null)
            {
                return;
            }

            int bpp = Format.BytesPerPixel;
            var hist = new int[256];
            long pixels = 0;
            TileReducer.Run<int[]>(
                Inputs[0],
                context,
                (rect, tile) =>
                {
                    var h = new int[256];
                    for (int y = 0; y < rect.Height; y++)
                    {
                        for (int x = 0; x < rect.Width; x++)
                        {
                            int p = ((y * Tile.Size) + x) * bpp;
                            h[Luminance.Level(tile[p], tile[p + 1], tile[p + 2], 256)]++;
                        }
                    }

                    return h;
                },
                part =>
                {
                    for (int i = 0; i < 256; i++)
                    {
                        hist[i] += part[i];
                        pixels += part[i];
                    }
                });

            var map = new float[256];
            Luminance.BuildMap(hist, pixels, 0.0, map);
            var lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                lut[i] = Luminance.ToByte(map[i]);
            }

            _lut = lut;
            Volatile.Write(ref _map, map);
        }
    }
}

/// <summary>Bradley adaptive thresholding: a pixel is dark when it is more than a fraction below the mean of the window around it.</summary>
internal sealed class AdaptiveThresholdNode : Node
{
    private readonly uint _upper;
    private readonly uint _lower;
    private readonly float _limit;
    private readonly int _before;
    private readonly int _after;

    public AdaptiveThresholdNode(Node input, uint upper, uint lower, float limit)
        : base(
            input.Width,
            input.Height,
            PixelFormat.Rgba8,
            new OpDescriptor(
                "adaptiveThreshold",
                new OpParameter("upper", (long)upper),
                new OpParameter("lower", (long)lower),
                new OpParameter("limit", (double)limit)),
            input)
    {
        _upper = upper;
        _lower = lower;
        _limit = limit;
        int s = Math.Max(input.Width / 16, 1);
        // The window spans s - 2 pixels before and s + 1 after the pixel (clipped to the image) in both directions.
        _before = Math.Max(s - 2, 0);
        _after = s + 1;
    }

    public override ImageRect RequiredInput(int inputIndex, ImageRect output)
    {
        int x0 = Math.Max(0, output.X - _before);
        int y0 = Math.Max(0, output.Y - _before);
        return new ImageRect(x0, y0, Math.Min(Width, output.Right + _after) - x0, Math.Min(Height, output.Bottom + _after) - y0);
    }

    // Brightness: Rec. 709 luma (0.2126 R + 0.7152 G + 0.0722 B) of the 8-bit sample, rounded to a whole number (ties to even).
    private static int Brightness(byte r, byte g, byte b) => (int)MathF.Round((0.2126f * r) + (0.7152f * g) + (0.0722f * b));

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect rect = TileRect(tx, ty);
        ImageRect need = RequiredInput(0, rect);
        int w = need.Width;
        int h = need.Height;
        byte[] px = Pools<byte>.Pool.Rent(w * h * 4);
        int[] lum = Pools<int>.Pool.Rent(w * h);
        int[] column = Pools<int>.Pool.Rent(w);
        try
        {
            Render.ReadRegion(Inputs[0], need, px, w * 4, PixelFormat.Rgba8, context);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int q = ((y * w) + x) * 4;
                    lum[(y * w) + x] = Brightness(px[q], px[q + 1], px[q + 2]);
                }
            }

            // Window sums by sliding: column[x] holds the brightness total of the window's rows in column x; moving down one pixel adds
            // the row that enters and removes the row that leaves, and along a row the horizontal total adds and removes one column.
            Array.Clear(column, 0, w);
            Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
            Span<byte> d = tile.Span;
            int prevY1 = 0;
            int prevY2 = -1;
            for (int y = 0; y < rect.Height; y++)
            {
                int gy = rect.Y + y;
                int y1 = Math.Max(gy - _before, 0) - need.Y;
                int y2 = Math.Min(gy + _after, Height - 1) - need.Y;
                for (int r = prevY2 + 1; r <= y2; r++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        column[x] += lum[(r * w) + x];
                    }
                }

                for (int r = prevY1; r < y1; r++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        column[x] -= lum[(r * w) + x];
                    }
                }

                prevY1 = y1;
                prevY2 = y2;
                int rowsInWindow = y2 - y1 + 1;
                long sum = 0;
                int x1 = 0;
                int x2 = -1;
                for (int x = 0; x < rect.Width; x++)
                {
                    int gx = rect.X + x;
                    int nx1 = Math.Max(gx - _before, 0) - need.X;
                    int nx2 = Math.Min(gx + _after, Width - 1) - need.X;
                    for (; x2 < nx2; x2++)
                    {
                        sum += column[x2 + 1];
                    }

                    for (; x1 < nx1; x1++)
                    {
                        sum -= column[x1];
                    }

                    long count = (long)(x2 - x1 + 1) * rowsInWindow;
                    long value = lum[((gy - need.Y) * w) + gx - need.X];
                    uint c = (double)value * count <= sum * (double)_limit ? _lower : _upper;
                    int o = ((y * Tile.Size) + x) * 4;
                    d[o] = (byte)c;
                    d[o + 1] = (byte)(c >> 8);
                    d[o + 2] = (byte)(c >> 16);
                    d[o + 3] = (byte)(c >> 24);
                }
            }

            return tile;
        }
        finally
        {
            Pools<byte>.Pool.Return(px);
            Pools<int>.Pool.Return(lum);
            Pools<int>.Pool.Return(column);
        }
    }
}
