using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Crops a window out of a covering image, choosing the window by detail. The choice is made once, on first use, from a
/// reduced copy of the input (see <see cref="Anchor.Smart"/>).
/// </summary>
internal sealed class SmartCropNode : Node
{
    private const int AnalysisSize = 128;
    private readonly object _gate = new();
    private (int X, int Y)? _offset;

    public SmartCropNode(Node input, int width, int height)
        : base(
            width,
            height,
            input.Format,
            new OpDescriptor("crop_smart", new OpParameter("width", (long)width), new OpParameter("height", (long)height)),
            input)
    {
    }

    /// <summary>Before the window is chosen the whole input is reported as needed; afterwards just the window.</summary>
    public override ImageRect RequiredInput(int inputIndex, ImageRect output) =>
        _offset is { } off ? output.Offset(off.X, off.Y) : new ImageRect(0, 0, Inputs[0].Width, Inputs[0].Height);

    /// <summary>Picks the window offset for a covering image: the one maximizing summed gradient magnitude.</summary>
    internal static int ChooseOffset(ReadOnlySpan<byte> gray, int w, int h, bool alongX, int window)
    {
        int length = alongX ? w : h;
        int cross = alongX ? h : w;
        if (window >= length)
        {
            return 0;
        }

        // Line energy: sum over the cross axis of |Sobel gx| + |Sobel gy| of each pixel.
        var energy = new long[length];
        for (int y = 0; y < h; y++)
        {
            int ym = Math.Max(y - 1, 0) * w;
            int y0 = y * w;
            int yp = Math.Min(y + 1, h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                int xm = Math.Max(x - 1, 0);
                int xp = Math.Min(x + 1, w - 1);
                int gx = (gray[ym + xp] + (2 * gray[y0 + xp]) + gray[yp + xp]) - (gray[ym + xm] + (2 * gray[y0 + xm]) + gray[yp + xm]);
                int gy = (gray[yp + xm] + (2 * gray[yp + x]) + gray[yp + xp]) - (gray[ym + xm] + (2 * gray[ym + x]) + gray[ym + xp]);
                energy[alongX ? x : y] += Math.Abs(gx) + Math.Abs(gy);
            }
        }

        _ = cross;
        long sum = 0;
        for (int i = 0; i < window; i++)
        {
            sum += energy[i];
        }

        int best = 0;
        long bestSum = sum;
        int centerStart = (length - window) / 2;
        for (int s = 1; s + window <= length; s++)
        {
            sum += energy[s + window - 1] - energy[s - 1];
            if (sum > bestSum || (sum == bestSum && Math.Abs(s - centerStart) < Math.Abs(best - centerStart)))
            {
                best = s;
                bestSum = sum;
            }
        }

        return best;
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        (int ox, int oy) = Offset(context);
        Node input = Inputs[0];
        ImageRect o = TileRect(tx, ty);
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            Render.ReadRegion(input, o.Offset(ox, oy), tile.Span, Tile.Size * Format.BytesPerPixel, Format, context);
            return tile;
        }
        catch
        {
            tile.Release();
            throw;
        }
    }

    private (int X, int Y) Offset(RenderContext context)
    {
        if (_offset is { } done)
        {
            return done;
        }

        lock (_gate)
        {
            if (_offset is { } again)
            {
                return again;
            }

            Node input = Inputs[0];
            bool alongX = input.Width > Width;
            int length = alongX ? input.Width : input.Height;
            int target = alongX ? Width : Height;
            int offset = 0;
            if (input.Format.Model is ColorModel.Rgb or ColorModel.Gray)
            {
                double shrink = Math.Max(1.0, (double)Math.Max(input.Width, input.Height) / AnalysisSize);
                int sw = Math.Max(1, (int)Math.Round(input.Width / shrink));
                int sh = Math.Max(1, (int)Math.Round(input.Height / shrink));
                Node small = sw == input.Width && sh == input.Height ? input : ResampleNode.Create(input, sw, sh, ResamplingFilter.Box, linear: false);
                byte[] gray = new byte[sw * sh];
                Render.ReadRegion(small, new ImageRect(0, 0, sw, sh), gray, sw, PixelFormat.Gray8, context);
                int smallLength = alongX ? sw : sh;
                int window = Math.Clamp((int)Math.Round((double)target * smallLength / length), 1, smallLength);
                int start = ChooseOffset(gray, sw, sh, alongX, window);
                offset = (int)Math.Round((double)start * length / smallLength);
            }
            else
            {
                offset = (length - target) / 2;
            }

            offset = Math.Clamp(offset, 0, length - target);
            _offset = alongX ? (offset, 0) : (0, offset);
            return _offset.Value;
        }
    }
}
