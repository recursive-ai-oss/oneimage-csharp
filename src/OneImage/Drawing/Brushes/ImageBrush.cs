using System.Numerics;
using OneImage.Kernels;

namespace OneImage.Drawing;

/// <summary>Paints with the pixels of an image, nearest-neighbor, tiled or placed once.</summary>
internal sealed class ImageBrush : Brush
{
    // 2^26 pixels = 256 MiB as 8-bit RGBA. A bigger picture is not a brush; crop it with a region first.
    private const long MaxPixels = 1L << 26;
    private readonly Image _source;
    private readonly Node _node;
    private readonly ImageRect _region;
    private readonly int _ox, _oy;
    private readonly bool _tile;
    private readonly Matrix3x2? _transform;
    private readonly Matrix3x2 _inverse;
    private readonly Pixels _pixels;

    public ImageBrush(Image source, ImageRect? region, int offsetX, int offsetY, Matrix3x2? transform, bool tile)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _node = source.Root;
        ImageRect r = region ?? new ImageRect(0, 0, _node.Width, _node.Height);
        if (r.IsEmpty || !r.IsInside(_node.Width, _node.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(region), "The region must be non-empty and inside the source image.");
        }

        if ((long)r.Width * r.Height > MaxPixels)
        {
            throw new ImageLimitException("The brush source is larger than 2^26 pixels; use a smaller region.");
        }

        _region = r;
        _ox = offsetX;
        _oy = offsetY;
        _tile = tile;
        _transform = transform;
        Matrix3x2 m = (transform ?? Matrix3x2.Identity) * Matrix3x2.CreateTranslation(offsetX, offsetY);
        if (!float.IsFinite(m.M11 + m.M12 + m.M21 + m.M22 + m.M31 + m.M32) || !Matrix3x2.Invert(m, out _inverse))
        {
            throw new ArgumentException("The transform must be finite and invertible.", nameof(transform));
        }

        _pixels = new Pixels(_node, r);
    }

    internal override Node[] Sources => [_node];

    /// <summary>The region's pixels as 8-bit straight RGBA, read once on first use.</summary>
    private sealed class Pixels(Node node, ImageRect region)
    {
        private byte[]? _data;

        public byte[] Get()
        {
            byte[]? d = Volatile.Read(ref _data);
            if (d != null)
            {
                return d;
            }

            lock (this)
            {
                if (_data == null)
                {
                    var bytes = new byte[region.Width * region.Height * 4];
                    Render.ReadRegion(node, region, bytes, 0, PixelFormat.Rgba8, RenderContext.Create(1, CancellationToken.None));
                    Volatile.Write(ref _data, bytes);
                }

                return _data;
            }
        }
    }

    public override void FillRow(int x, int y, Span<float> rgba)
    {
        byte[] data = _pixels.Get();
        int w = _region.Width, h = _region.Height;
        int n = rgba.Length / 4;
        const float k = 1f / 255f;
        if (_transform == null && _tile)
        {
            int row = (int)Mod((long)y - _oy, h) * w;
            int col = (int)Mod((long)x - _ox, w);
            for (int i = 0; i < n; i++)
            {
                int s = (row + col) * 4;
                int o = i * 4;
                rgba[o] = data[s] * k;
                rgba[o + 1] = data[s + 1] * k;
                rgba[o + 2] = data[s + 2] * k;
                rgba[o + 3] = data[s + 3] * k;
                if (++col == w)
                {
                    col = 0;
                }
            }

            return;
        }

        Matrix3x2 m = _inverse;
        double py = y + 0.5;
        for (int i = 0; i < n; i++)
        {
            double px = x + i + 0.5;
            double u = (px * m.M11) + (py * m.M21) + m.M31;
            double v = (px * m.M12) + (py * m.M22) + m.M32;
            int o = i * 4;
            if (!(Math.Abs(u) < 1e15 && Math.Abs(v) < 1e15))
            {
                rgba.Slice(o, 4).Clear();
                continue;
            }

            long cu = (long)Math.Floor(u), cv = (long)Math.Floor(v);
            if (_tile)
            {
                cu = Mod(cu, w);
                cv = Mod(cv, h);
            }
            else if (cu < 0 || cu >= w || cv < 0 || cv >= h)
            {
                rgba.Slice(o, 4).Clear();
                continue;
            }

            int s = (int)((cv * w) + cu) * 4;
            rgba[o] = data[s] * k;
            rgba[o + 1] = data[s + 1] * k;
            rgba[o + 2] = data[s + 2] * k;
            rgba[o + 3] = data[s + 3] * k;
        }
    }

    private static long Mod(long a, int m)
    {
        long r = a % m;
        return r < 0 ? r + m : r;
    }

    public override IReadOnlyList<OpParameter> Describe()
    {
        Matrix3x2 m = _transform ?? Matrix3x2.Identity;
        return
        [
            new("brush", "image"),
            new("source", _node.Id),
            new("rx", (long)_region.X),
            new("ry", (long)_region.Y),
            new("rw", (long)_region.Width),
            new("rh", (long)_region.Height),
            new("ox", (long)_ox),
            new("oy", (long)_oy),
            new("tile", _tile),
            new("m11", (double)m.M11),
            new("m12", (double)m.M12),
            new("m21", (double)m.M21),
            new("m22", (double)m.M22),
            new("m31", (double)m.M31),
            new("m32", (double)m.M32),
        ];
    }
}
