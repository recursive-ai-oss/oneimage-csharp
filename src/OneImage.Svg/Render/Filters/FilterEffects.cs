using System.Buffers;

namespace OneImage.Svg.Render;

/// <summary>
/// Pixel operations of the primitives that are not plain per-pixel maps: component transfer, morphology, tiling, turbulence,
/// displacement and lighting. All work on premultiplied float RGBA over the work rectangle and read only positions they can name
/// from global pixel coordinates, so any rectangle computes the same values as the same pixels of a larger one.
/// </summary>
internal static class FilterEffects
{
    /// <summary>Applies one transfer function per channel to straight color, then premultiplies again.</summary>
    public static void ComponentTransfer(float[] src, float[] dst, int n, FeTransfer[] f)
    {
        FeTransfer fr = f[0], fg = f[1], fb = f[2], fa = f[3];
        bool colorId = fr.Type == FeTransfer.Kind.Identity && fg.Type == FeTransfer.Kind.Identity && fb.Type == FeTransfer.Kind.Identity;
        bool alphaId = fa.Type == FeTransfer.Kind.Identity;
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            float a = src[p + 3];
            if (colorId && alphaId)
            {
                dst[p] = src[p];
                dst[p + 1] = src[p + 1];
                dst[p + 2] = src[p + 2];
                dst[p + 3] = a;
                continue;
            }

            float r = 0, g = 0, b = 0;
            if (a > 0)
            {
                float inv = 1 / a;
                r = Math.Min(src[p] * inv, 1f);
                g = Math.Min(src[p + 1] * inv, 1f);
                b = Math.Min(src[p + 2] * inv, 1f);
            }

            float na = fa.Apply(a);
            dst[p] = fr.Apply(r) * na;
            dst[p + 1] = fg.Apply(g) * na;
            dst[p + 2] = fb.Apply(b) * na;
            dst[p + 3] = na;
        }
    }

    // ---- morphology ----

    /// <summary>
    /// Erodes (minimum) or dilates (maximum) each channel over a window of (2rx+1) by (2ry+1) pixels, separably. What lies outside the
    /// rectangle is transparent. The time does not depend on the radius: a radius beyond the rectangle is cut to it, which gives the
    /// same result because everything past the edge is transparent.
    /// </summary>
    public static void Morphology(float[] src, float[] dst, int w, int h, int rx, int ry, bool dilate)
    {
        int count = w * h * 4;
        float[]? mid = null;
        try
        {
            float[] from = src;
            if (rx > 0)
            {
                mid = Pools<float>.Pool.Rent(Math.Max(count, 1));
                Pass(from, ry > 0 ? mid : dst, w, h, Math.Min(rx, w), dilate, horizontal: true);
                from = ry > 0 ? mid : dst;
            }

            if (ry > 0)
            {
                Pass(from, dst, w, h, Math.Min(ry, h), dilate, horizontal: false);
            }
            else if (rx <= 0)
            {
                Array.Copy(src, dst, count);
            }
        }
        finally
        {
            if (mid is not null)
            {
                Pools<float>.Pool.Return(mid);
            }
        }
    }

    // van Herk / Gil-Werman: blocks of the window size, prefix and suffix extrema, so each output is the extremum of two table entries.
    private static void Pass(float[] src, float[] dst, int w, int h, int r, bool dilate, bool horizontal)
    {
        int len = horizontal ? w : h;
        int lines = horizontal ? h : w;
        int win = (2 * r) + 1;
        int padded = len + (2 * r);
        float[] line = Pools<float>.Pool.Rent(padded);
        float[] pre = Pools<float>.Pool.Rent(padded);
        float[] suf = Pools<float>.Pool.Rent(padded);
        try
        {
            for (int c = 0; c < 4; c++)
            {
                for (int l = 0; l < lines; l++)
                {
                    int start = horizontal ? (l * w * 4) + c : (l * 4) + c;
                    int step = horizontal ? 4 : w * 4;
                    Array.Clear(line, 0, padded);
                    for (int i = 0; i < len; i++)
                    {
                        line[r + i] = src[start + (i * step)];
                    }

                    for (int i = 0; i < padded; i++)
                    {
                        pre[i] = i % win == 0 ? line[i] : Extreme(pre[i - 1], line[i], dilate);
                    }

                    for (int i = padded - 1; i >= 0; i--)
                    {
                        suf[i] = i == padded - 1 || (i + 1) % win == 0 ? line[i] : Extreme(suf[i + 1], line[i], dilate);
                    }

                    // The window of output x covers padded indices x .. x + 2r.
                    for (int x = 0; x < len; x++)
                    {
                        dst[start + (x * step)] = Extreme(suf[x], pre[x + win - 1], dilate);
                    }
                }
            }
        }
        finally
        {
            Pools<float>.Pool.Return(line);
            Pools<float>.Pool.Return(pre);
            Pools<float>.Pool.Return(suf);
        }
    }

    private static float Extreme(float a, float b, bool max) => max ? Math.Max(a, b) : Math.Min(a, b);

    // ---- tile ----

    /// <summary>Fills <paramref name="sub"/> of the work rectangle with copies of <paramref name="tile"/> of the input, anchored at the tile's corner.</summary>
    public static void Tile(float[] src, float[] dst, PxRect work, PxRect tile, PxRect sub)
    {
        PxRect s = sub.Intersect(work);
        if (s.IsEmpty || tile.IsEmpty)
        {
            return;
        }

        for (int y = s.Y; y < s.Bottom; y++)
        {
            int sy = tile.Y + Mod(y - tile.Y, tile.H);
            for (int x = s.X; x < s.Right; x++)
            {
                int sx = tile.X + Mod(x - tile.X, tile.W);
                if (sx < work.X || sx >= work.Right || sy < work.Y || sy >= work.Bottom)
                {
                    continue;
                }

                int o = (((y - work.Y) * work.W) + (x - work.X)) * 4;
                int i = (((sy - work.Y) * work.W) + (sx - work.X)) * 4;
                dst[o] = src[i];
                dst[o + 1] = src[i + 1];
                dst[o + 2] = src[i + 2];
                dst[o + 3] = src[i + 3];
            }
        }
    }

    private static int Mod(int a, int m) => ((a % m) + m) % m;

    // ---- displacement ----

    /// <summary>
    /// Moves the pixels of <paramref name="src"/> by the channels of <paramref name="map"/> (read as straight color): the pixel at p takes
    /// the source pixel at p + scale * (channel - 0.5), nearest, and transparent where that lies outside the rectangle.
    /// </summary>
    public static void Displace(float[] src, float[] map, float[] dst, PxRect work, double scaleX, double scaleY, int xc, int yc)
    {
        int w = work.W, h = work.H;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = ((y * w) + x) * 4;
                float a = map[p + 3];
                float vx = Channel(map, p, xc, a);
                float vy = Channel(map, p, yc, a);
                double fx = x + 0.5 + (scaleX * (vx - 0.5));
                double fy = y + 0.5 + (scaleY * (vy - 0.5));
                int sx = (int)Math.Floor(fx);
                int sy = (int)Math.Floor(fy);
                if (sx < 0 || sx >= w || sy < 0 || sy >= h)
                {
                    continue;
                }

                int s = ((sy * w) + sx) * 4;
                dst[p] = src[s];
                dst[p + 1] = src[s + 1];
                dst[p + 2] = src[s + 2];
                dst[p + 3] = src[s + 3];
            }
        }
    }

    private static float Channel(float[] map, int p, int channel, float alpha)
    {
        if (channel == 3)
        {
            return alpha;
        }

        return alpha > 0 ? Math.Min(map[p + channel] / alpha, 1f) : 0;
    }

    // ---- turbulence ----

    private const int BSize = 0x100;
    private const int BM = 0xff;
    private const int PerlinN = 0x1000;

    /// <summary>The Perlin noise tables of one seed, after the reference algorithm of the specification.</summary>
    internal sealed class Noise
    {
        private readonly int[] _lattice = new int[BSize + BSize + 2];
        private readonly double[] _grad = new double[4 * (BSize + BSize + 2) * 2];

        public Noise(long seed)
        {
            Init(seed);
        }

        private static long SetupSeed(long s)
        {
            if (s <= 0)
            {
                s = -(s % (2147483647 - 1)) + 1;
            }

            return s > 2147483647 - 1 ? 2147483647 - 1 : s;
        }

        // Park and Miller's minimal standard generator: r = a * r mod (2^31 - 1), by Schrage's method.
        private static long Random(long seed)
        {
            long result = (16807 * (seed % 127773)) - (2836 * (seed / 127773));
            if (result <= 0)
            {
                result += 2147483647;
            }

            return result;
        }

        private static int G(int k, int i, int j) => ((k * (BSize + BSize + 2)) + i) * 2 + j;

        private void Init(long seed)
        {
            seed = SetupSeed(seed);
            int i;
            for (int k = 0; k < 4; k++)
            {
                for (i = 0; i < BSize; i++)
                {
                    _lattice[i] = i;
                    seed = Random(seed);
                    double g0 = (double)((seed % (BSize + BSize)) - BSize) / BSize;
                    seed = Random(seed);
                    double g1 = (double)((seed % (BSize + BSize)) - BSize) / BSize;

                    // Every vector is normalized, none is redrawn: this is the original SVG 1.1 generator, which every renderer follows
                    // (the Filter Effects text adds a redraw of zero and over-long vectors that none of them implements).
                    double s = Math.Sqrt((g0 * g0) + (g1 * g1));
                    _grad[G(k, i, 0)] = s > 0 ? g0 / s : 0;
                    _grad[G(k, i, 1)] = s > 0 ? g1 / s : 0;
                }
            }

            for (i = BSize - 1; i > 0; i--)
            {
                int k = _lattice[i];
                seed = Random(seed);
                int j = (int)(seed % BSize);
                _lattice[i] = _lattice[j];
                _lattice[j] = k;
            }

            for (i = 0; i < BSize + 2; i++)
            {
                _lattice[BSize + i] = _lattice[i];
                for (int k = 0; k < 4; k++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        _grad[G(k, BSize + i, j)] = _grad[G(k, i, j)];
                    }
                }
            }
        }

        private static double SCurve(double t) => t * t * (3.0 - (2.0 * t));

        private static double Lerp(double t, double a, double b) => a + (t * (b - a));

        private static int Trunc(double t) => !(Math.Abs(t) < 1e15) ? 0 : (int)(long)t;

        private double Noise2(int channel, double vx, double vy, bool stitch, int wrapX, int wrapY, int width, int height)
        {
            double t = vx + PerlinN;
            int bx0 = Trunc(t);
            int bx1 = bx0 + 1;
            double rx0 = t - Trunc(t);
            double rx1 = rx0 - 1.0;
            t = vy + PerlinN;
            int by0 = Trunc(t);
            int by1 = by0 + 1;
            double ry0 = t - Trunc(t);
            double ry1 = ry0 - 1.0;
            if (stitch)
            {
                if (bx0 >= wrapX)
                {
                    bx0 -= width;
                }

                if (bx1 >= wrapX)
                {
                    bx1 -= width;
                }

                if (by0 >= wrapY)
                {
                    by0 -= height;
                }

                if (by1 >= wrapY)
                {
                    by1 -= height;
                }
            }

            bx0 &= BM;
            bx1 &= BM;
            by0 &= BM;
            by1 &= BM;
            int i = _lattice[bx0];
            int j = _lattice[bx1];
            int b00 = _lattice[i + by0];
            int b10 = _lattice[j + by0];
            int b01 = _lattice[i + by1];
            int b11 = _lattice[j + by1];
            double sx = SCurve(rx0);
            double sy = SCurve(ry0);
            double u = (rx0 * _grad[G(channel, b00, 0)]) + (ry0 * _grad[G(channel, b00, 1)]);
            double v = (rx1 * _grad[G(channel, b10, 0)]) + (ry0 * _grad[G(channel, b10, 1)]);
            double a = Lerp(sx, u, v);
            u = (rx0 * _grad[G(channel, b01, 0)]) + (ry1 * _grad[G(channel, b01, 1)]);
            v = (rx1 * _grad[G(channel, b11, 0)]) + (ry1 * _grad[G(channel, b11, 1)]);
            double b = Lerp(sx, u, v);
            return Lerp(sy, a, b);
        }

        /// <summary>The turbulence or fractal-sum value at a point of the user space, for one color channel (0 red .. 3 alpha).</summary>
        public double Turbulence(int channel, double px, double py, double fx, double fy, int octaves, bool fractal, bool stitch, double tileX, double tileY, double tileW, double tileH)
        {
            int width = 0, height = 0, wrapX = 0, wrapY = 0;
            if (stitch)
            {
                // The frequencies are nudged so the subregion holds a whole number of lattice cells (specification, stitchTiles).
                if (fx != 0.0)
                {
                    double lo = Math.Floor(tileW * fx) / tileW;
                    double hi = Math.Ceiling(tileW * fx) / tileW;
                    fx = fx / lo < hi / fx ? lo : hi;
                }

                if (fy != 0.0)
                {
                    double lo = Math.Floor(tileH * fy) / tileH;
                    double hi = Math.Ceiling(tileH * fy) / tileH;
                    fy = fy / lo < hi / fy ? lo : hi;
                }

                width = Trunc((tileW * fx) + 0.5);
                wrapX = Trunc((tileX * fx) + PerlinN + width);
                height = Trunc((tileH * fy) + 0.5);
                wrapY = Trunc((tileY * fy) + PerlinN + height);
            }

            double sum = 0;
            double vx = px * fx;
            double vy = py * fy;
            double ratio = 1;
            for (int o = 0; o < octaves; o++)
            {
                double n = Noise2(channel, vx, vy, stitch, wrapX, wrapY, width, height);
                sum += (fractal ? n : Math.Abs(n)) / ratio;
                vx *= 2;
                vy *= 2;
                ratio *= 2;
                if (stitch)
                {
                    width *= 2;
                    wrapX = (2 * wrapX) - PerlinN;
                    height *= 2;
                    wrapY = (2 * wrapY) - PerlinN;
                }
            }

            return sum;
        }
    }

    /// <summary>Fills the work rectangle with turbulence; pixel (x, y) samples the user-space point of its top-left corner.</summary>
    public static void Turbulence(float[] dst, PxRect work, double a, double d, double e, double f, FeTurbulence t, double tileX, double tileY, double tileW, double tileH)
    {
        var noise = new Noise(t.Seed);

        for (int y = 0; y < work.H; y++)
        {
            double uy = (work.Y + y - f) / d;
            for (int x = 0; x < work.W; x++)
            {
                double ux = (work.X + x - e) / a;
                int p = ((y * work.W) + x) * 4;
                float c0 = Value(noise, 0, ux, uy, t, tileX, tileY, tileW, tileH);
                float c1 = Value(noise, 1, ux, uy, t, tileX, tileY, tileW, tileH);
                float c2 = Value(noise, 2, ux, uy, t, tileX, tileY, tileW, tileH);
                float c3 = Value(noise, 3, ux, uy, t, tileX, tileY, tileW, tileH);
                dst[p] = c0 * c3;
                dst[p + 1] = c1 * c3;
                dst[p + 2] = c2 * c3;
                dst[p + 3] = c3;
            }
        }
    }

    private static float Value(Noise noise, int channel, double ux, double uy, FeTurbulence t, double tx, double ty, double tw, double th)
    {
        double v = noise.Turbulence(channel, ux, uy, t.BaseX, t.BaseY, t.Octaves, t.Fractal, t.Stitch, tx, ty, tw, th);
        if (t.Fractal)
        {
            v = (v + 1) / 2;
        }

        return double.IsNaN(v) ? 0 : (float)Math.Clamp(v, 0, 1);
    }

    // ---- lighting ----

    /// <summary>Everything a lighting pass needs, in pixel units of the filter grid.</summary>
    internal readonly record struct LightSetup(
        bool Specular, double SurfaceScale, double Constant, double Exponent, double DxPx, double DyPx, bool Unit,
        LightKind Kind, double Lx, double Ly, double Lz, double Sx, double Sy, double Sz, double SpotExp, double CosCone,
        float R, float G, float B);

    /// <summary>
    /// Lights the alpha channel of the input as a height map. <paramref name="region"/> is the filter region: its border pixels use
    /// the edge and corner gradient kernels of the specification.
    /// </summary>
    public static void Lighting(float[] src, float[] dst, PxRect work, PxRect region, in LightSetup s)
    {
        int w = work.W, h = work.H;
        double ex = 0, ey = 0, ez = 1;
        if (s.Kind == LightKind.Distant)
        {
            ex = s.Lx;
            ey = s.Ly;
            ez = s.Lz;
        }

        for (int y = 0; y < h; y++)
        {
            int gy = work.Y + y;
            for (int x = 0; x < w; x++)
            {
                int gx = work.X + x;
                (double nx, double ny) = Gradient(src, work, region, gx, gy, s);
                double nl = Math.Sqrt((nx * nx) + (ny * ny) + 1);
                nx /= nl;
                ny /= nl;
                double nz = 1 / nl;

                double lx, ly, lz;
                double cr = s.R, cg = s.G, cb = s.B;
                if (s.Kind == LightKind.Distant)
                {
                    lx = ex;
                    ly = ey;
                    lz = ez;
                }
                else
                {
                    double z = s.SurfaceScale * Alpha(src, work, gx, gy);
                    lx = s.Lx - (gx + 0.5);
                    ly = s.Ly - (gy + 0.5);
                    lz = s.Lz - z;
                    double ln = Math.Sqrt((lx * lx) + (ly * ly) + (lz * lz));
                    if (ln > 0)
                    {
                        lx /= ln;
                        ly /= ln;
                        lz /= ln;
                    }

                    if (s.Kind == LightKind.Spot)
                    {
                        double minusLS = -((lx * s.Sx) + (ly * s.Sy) + (lz * s.Sz));
                        double k = 0;
                        if (minusLS > 0 && !(minusLS < s.CosCone))
                        {
                            k = Math.Pow(minusLS, s.SpotExp);
                        }

                        cr *= k;
                        cg *= k;
                        cb *= k;
                    }
                }

                int p = ((y * w) + x) * 4;
                if (!s.Specular)
                {
                    double f = s.Constant * Math.Max(0, (nx * lx) + (ny * ly) + (nz * lz));
                    dst[p] = Clamp01(f * cr);
                    dst[p + 1] = Clamp01(f * cg);
                    dst[p + 2] = Clamp01(f * cb);
                    dst[p + 3] = 1;
                }
                else
                {
                    double hx = lx, hy = ly, hz = lz + 1;
                    double hn = Math.Sqrt((hx * hx) + (hy * hy) + (hz * hz));
                    double f = 0;
                    if (hn > 0)
                    {
                        double nh = ((nx * hx) + (ny * hy) + (nz * hz)) / hn;
                        f = s.Constant * (nh > 0 ? Math.Pow(nh, s.Exponent) : 0);
                    }

                    float r = Clamp01(f * cr), g = Clamp01(f * cg), b = Clamp01(f * cb);
                    dst[p] = r;
                    dst[p + 1] = g;
                    dst[p + 2] = b;
                    dst[p + 3] = Math.Max(r, Math.Max(g, b));
                }
            }
        }
    }

    private static float Clamp01(double v) => double.IsNaN(v) ? 0 : (float)Math.Clamp(v, 0, 1);

    private static float Alpha(float[] src, PxRect work, int gx, int gy)
    {
        if (gx < work.X || gx >= work.Right || gy < work.Y || gy >= work.Bottom)
        {
            return 0;
        }

        return src[((((gy - work.Y) * work.W) + (gx - work.X)) * 4) + 3];
    }

    // Bilinear alpha at a fractional position (pixel centers at integers plus a half), clamped to the region so its border extends.
    private static double Sample(float[] src, PxRect work, PxRect region, double fx, double fy)
    {
        double x = Math.Clamp(fx, region.X, region.Right - 1);
        double y = Math.Clamp(fy, region.Y, region.Bottom - 1);
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        double tx = x - x0;
        double ty = y - y0;
        double a = Alpha(src, work, x0, y0);
        double b = Alpha(src, work, x0 + 1, y0);
        double c = Alpha(src, work, x0, y0 + 1);
        double d = Alpha(src, work, x0 + 1, y0 + 1);
        return ((a + ((b - a) * tx)) * (1 - ty)) + ((c + ((d - c) * tx)) * ty);
    }

    // The surface slope (the specification's Nx and Ny before normalizing), with the interior kernel, or an edge or corner kernel on the region's border.
    private static (double Nx, double Ny) Gradient(float[] src, PxRect work, PxRect region, int gx, int gy, in LightSetup s)
    {
        double scale = s.SurfaceScale;
        if (s.Unit)
        {
            double dx = s.DxPx, dy = s.DyPx;
            double fx = gx, fy = gy;
            double Sm(double ox, double oy) => Sample(src, work, region, fx + ox, fy + oy);
            double sx = (Sm(dx, -dy) + (2 * Sm(dx, 0)) + Sm(dx, dy)) - (Sm(-dx, -dy) + (2 * Sm(-dx, 0)) + Sm(-dx, dy));
            double sy = (Sm(-dx, dy) + (2 * Sm(0, dy)) + Sm(dx, dy)) - (Sm(-dx, -dy) + (2 * Sm(0, -dy)) + Sm(dx, -dy));
            return (-scale * sx / (4 * dx), -scale * sy / (4 * dy));
        }

        bool left = gx <= region.X, right = gx >= region.Right - 1, top = gy <= region.Y, bottom = gy >= region.Bottom - 1;
        double I(int ox, int oy) => Alpha(src, work, gx + ox, gy + oy);
        int x0 = left ? 0 : -1, x1 = right ? 0 : 1, y0 = top ? 0 : -1, y1 = bottom ? 0 : 1;
        if (x0 == x1 || y0 == y1)
        {
            // A one-pixel-wide region has no slope along that axis.
            return (0, 0);
        }

        // The Sobel kernels with weights 1, 2, 1 across the lines that exist. The specification's factors all equal 2 / (sum of weights * span in pixels).
        double gxs = 0, wxs = 0;
        for (int oy = y0; oy <= y1; oy++)
        {
            double wgt = oy == 0 ? 2 : 1;
            gxs += wgt * (I(x1, oy) - I(x0, oy));
            wxs += wgt;
        }

        double gys = 0, wys = 0;
        for (int ox = x0; ox <= x1; ox++)
        {
            double wgt = ox == 0 ? 2 : 1;
            gys += wgt * (I(ox, y1) - I(ox, y0));
            wys += wgt;
        }

        double nx = 2 * gxs / (wxs * (x1 - x0));
        double ny = 2 * gys / (wys * (y1 - y0));
        return (-scale * nx, -scale * ny);
    }
}
