namespace OneImage.Svg.Render;

/// <summary>Pixel operations of the filter primitives over premultiplied float RGBA arrays of equal size.</summary>
internal static class FilterOps
{
    /// <summary>Copies <paramref name="src"/> shifted right by <paramref name="dx"/> and down by <paramref name="dy"/> pixels; what moves in from outside is transparent.</summary>
    public static void Offset(float[] src, float[] dst, int w, int h, long dx, long dy)
    {
        Array.Clear(dst, 0, w * h * 4);
        if (Math.Abs(dx) >= w || Math.Abs(dy) >= h)
        {
            return;
        }

        int ix = (int)dx;
        int iy = (int)dy;
        int x0 = Math.Max(0, ix);
        int x1 = Math.Min(w, w + ix);
        for (int y = Math.Max(0, iy); y < Math.Min(h, h + iy); y++)
        {
            Array.Copy(src, (((y - iy) * w) + (x0 - ix)) * 4, dst, ((y * w) + x0) * 4, (x1 - x0) * 4);
        }
    }

    /// <summary>The input's alpha with black color.</summary>
    public static void AlphaOnly(float[] src, float[] dst, int n)
    {
        for (int i = 0; i < n; i++)
        {
            dst[(i * 4) + 3] = src[(i * 4) + 3];
        }
    }

    /// <summary>Source-over of <paramref name="top"/> onto <paramref name="acc"/>, in place on <paramref name="acc"/>.</summary>
    public static void Over(float[] top, float[] acc, int n)
    {
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            float inv = 1 - top[p + 3];
            acc[p] = top[p] + (acc[p] * inv);
            acc[p + 1] = top[p + 1] + (acc[p + 1] * inv);
            acc[p + 2] = top[p + 2] + (acc[p + 2] * inv);
            acc[p + 3] = top[p + 3] + (acc[p + 3] * inv);
        }
    }

    /// <summary>The Porter-Duff operators and arithmetic of <c>feComposite</c>: <paramref name="a"/> is <c>in</c>, <paramref name="b"/> is <c>in2</c>.</summary>
    public static void Composite(float[] a, float[] b, float[] dst, int n, FeCompositeOp op, float k1, float k2, float k3, float k4)
    {
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            float aa = a[p + 3];
            float ab = b[p + 3];
            float fa, fb;
            switch (op)
            {
                case FeCompositeOp.Over: fa = 1; fb = 1 - aa; break;
                case FeCompositeOp.In: fa = ab; fb = 0; break;
                case FeCompositeOp.Out: fa = 1 - ab; fb = 0; break;
                case FeCompositeOp.Atop: fa = ab; fb = 1 - aa; break;
                case FeCompositeOp.Xor: fa = 1 - ab; fb = 1 - aa; break;
                case FeCompositeOp.Lighter: fa = 1; fb = 1; break;
                default:
                {
                    float ra = Math.Clamp((k1 * aa * ab) + (k2 * aa) + (k3 * ab) + k4, 0f, 1f);
                    for (int c = 0; c < 3; c++)
                    {
                        dst[p + c] = Math.Clamp((k1 * a[p + c] * b[p + c]) + (k2 * a[p + c]) + (k3 * b[p + c]) + k4, 0f, ra);
                    }

                    dst[p + 3] = ra;
                    continue;
                }
            }

            for (int c = 0; c < 4; c++)
            {
                float v = (a[p + c] * fa) + (b[p + c] * fb);
                dst[p + c] = op == FeCompositeOp.Lighter ? Math.Min(v, 1f) : v;
            }
        }
    }

    /// <summary>The 4x5 matrix on straight color, results clamped to 0..1, in the buffer's own color space.</summary>
    public static void ColorMatrix(float[] src, float[] dst, int n, double[] m)
    {
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            float a = src[p + 3];
            float r = 0, g = 0, b = 0;
            if (a > 0)
            {
                float inv = 1 / a;
                r = Math.Min(src[p] * inv, 1f);
                g = Math.Min(src[p + 1] * inv, 1f);
                b = Math.Min(src[p + 2] * inv, 1f);
            }

            float nr = Row(m, 0, r, g, b, a);
            float ng = Row(m, 5, r, g, b, a);
            float nb = Row(m, 10, r, g, b, a);
            float na = Row(m, 15, r, g, b, a);
            dst[p] = nr * na;
            dst[p + 1] = ng * na;
            dst[p + 2] = nb * na;
            dst[p + 3] = na;
        }
    }

    private static float Row(double[] m, int o, float r, float g, float b, float a) =>
        Math.Clamp((float)((m[o] * r) + (m[o + 1] * g) + (m[o + 2] * b) + (m[o + 3] * a) + m[o + 4]), 0f, 1f);

    /// <summary>Blends <paramref name="top"/> over <paramref name="back"/> with a blend mode (compositing specification, general formula).</summary>
    public static void Blend(float[] top, float[] back, float[] dst, int n, FeBlendMode mode)
    {
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            float qa = top[p + 3];
            float qb = back[p + 3];
            float ra = qa + qb - (qa * qb);
            if (qa <= 0 || qb <= 0 || mode == FeBlendMode.Normal)
            {
                for (int c = 0; c < 3; c++)
                {
                    dst[p + c] = top[p + c] + (back[p + c] * (1 - qa));
                }

                dst[p + 3] = ra;
                continue;
            }

            float s0 = Math.Min(top[p] / qa, 1f), s1 = Math.Min(top[p + 1] / qa, 1f), s2 = Math.Min(top[p + 2] / qa, 1f);
            float b0 = Math.Min(back[p] / qb, 1f), b1 = Math.Min(back[p + 1] / qb, 1f), b2 = Math.Min(back[p + 2] / qb, 1f);
            float m0, m1, m2;
            if (mode >= FeBlendMode.Hue)
            {
                (m0, m1, m2) = NonSeparable(mode, b0, b1, b2, s0, s1, s2);
            }
            else
            {
                m0 = Separable(mode, b0, s0);
                m1 = Separable(mode, b1, s1);
                m2 = Separable(mode, b2, s2);
            }

            float both = qa * qb;
            dst[p] = (top[p] * (1 - qb)) + (back[p] * (1 - qa)) + (both * m0);
            dst[p + 1] = (top[p + 1] * (1 - qb)) + (back[p + 1] * (1 - qa)) + (both * m1);
            dst[p + 2] = (top[p + 2] * (1 - qb)) + (back[p + 2] * (1 - qa)) + (both * m2);
            dst[p + 3] = ra;
        }
    }

    private static float Separable(FeBlendMode mode, float cb, float cs)
    {
        switch (mode)
        {
            case FeBlendMode.Multiply: return cb * cs;
            case FeBlendMode.Screen: return cb + cs - (cb * cs);
            case FeBlendMode.Overlay: return HardLight(cs, cb);
            case FeBlendMode.Darken: return Math.Min(cb, cs);
            case FeBlendMode.Lighten: return Math.Max(cb, cs);
            case FeBlendMode.ColorDodge: return cb <= 0 ? 0 : cs >= 1 ? 1 : Math.Min(1, cb / (1 - cs));
            case FeBlendMode.ColorBurn: return cb >= 1 ? 1 : cs <= 0 ? 0 : 1 - Math.Min(1, (1 - cb) / cs);
            case FeBlendMode.HardLight: return HardLight(cb, cs);
            case FeBlendMode.SoftLight:
            {
                if (cs <= 0.5f)
                {
                    return cb - ((1 - (2 * cs)) * cb * (1 - cb));
                }

                float d = cb <= 0.25f ? ((((16 * cb) - 12) * cb) + 4) * cb : MathF.Sqrt(cb);
                return cb + (((2 * cs) - 1) * (d - cb));
            }

            case FeBlendMode.Difference: return Math.Abs(cb - cs);
            case FeBlendMode.Exclusion: return cb + cs - (2 * cb * cs);
            default: return cs;
        }
    }

    private static float HardLight(float cb, float cs) =>
        cs <= 0.5f ? cb * 2 * cs : cb + ((2 * cs) - 1) - (cb * ((2 * cs) - 1));

    private static float Lum(float r, float g, float b) => (0.3f * r) + (0.59f * g) + (0.11f * b);

    private static (float, float, float) ClipColor(float r, float g, float b)
    {
        float l = Lum(r, g, b);
        float n = Math.Min(r, Math.Min(g, b));
        float x = Math.Max(r, Math.Max(g, b));
        if (n < 0)
        {
            float d = l - n;
            r = l + ((r - l) * l / d);
            g = l + ((g - l) * l / d);
            b = l + ((b - l) * l / d);
        }

        if (x > 1)
        {
            float d = x - l;
            r = l + ((r - l) * (1 - l) / d);
            g = l + ((g - l) * (1 - l) / d);
            b = l + ((b - l) * (1 - l) / d);
        }

        return (r, g, b);
    }

    private static (float, float, float) SetLum(float r, float g, float b, float l)
    {
        float d = l - Lum(r, g, b);
        return ClipColor(r + d, g + d, b + d);
    }

    private static float Sat(float r, float g, float b) => Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));

    private static (float, float, float) SetSat(float r, float g, float b, float s)
    {
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        if (max <= min)
        {
            return (0, 0, 0);
        }

        float k = s / (max - min);
        return ((r - min) * k, (g - min) * k, (b - min) * k);
    }

    private static (float, float, float) NonSeparable(FeBlendMode mode, float b0, float b1, float b2, float s0, float s1, float s2)
    {
        switch (mode)
        {
            case FeBlendMode.Hue:
            {
                (float r, float g, float b) = SetSat(s0, s1, s2, Sat(b0, b1, b2));
                return SetLum(r, g, b, Lum(b0, b1, b2));
            }

            case FeBlendMode.Saturation:
            {
                (float r, float g, float b) = SetSat(b0, b1, b2, Sat(s0, s1, s2));
                return SetLum(r, g, b, Lum(b0, b1, b2));
            }

            case FeBlendMode.Color:
                return SetLum(s0, s1, s2, Lum(b0, b1, b2));
            default:
                return SetLum(b0, b1, b2, Lum(s0, s1, s2));
        }
    }

    /// <summary>
    /// The convolution of <c>feConvolveMatrix</c> over <paramref name="src"/>, which covers the pixel rectangle at (<paramref name="ox"/>, <paramref name="oy"/>)
    /// of the filter's space; <paramref name="edgeRect"/> is the rectangle of the filter region that edge modes refer to.
    /// </summary>
    public static void Convolve(float[] src, float[] dst, int w, int h, int ox, int oy, PxRect edgeRect, FeConvolve c)
    {
        double bias = c.Bias;
        double inv = 1 / c.Divisor;
        float[] px = new float[4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                for (int j = 0; j < c.OrderY; j++)
                {
                    for (int i = 0; i < c.OrderX; i++)
                    {
                        // Kernel entries run in reverse over the source (the specification's flipped sum).
                        double k = c.Kernel[((c.OrderY - 1 - j) * c.OrderX) + (c.OrderX - 1 - i)];
                        if (k == 0)
                        {
                            continue;
                        }

                        int gx = ox + x - c.TargetX + i;
                        int gy = oy + y - c.TargetY + j;
                        if (!Fetch(src, w, h, ox, oy, edgeRect, c.Edge, gx, gy, px))
                        {
                            continue;
                        }

                        if (c.PreserveAlpha)
                        {
                            float a = px[3];
                            if (a > 0)
                            {
                                s0 += k * Math.Min(px[0] / a, 1f);
                                s1 += k * Math.Min(px[1] / a, 1f);
                                s2 += k * Math.Min(px[2] / a, 1f);
                            }
                        }
                        else
                        {
                            s0 += k * px[0];
                            s1 += k * px[1];
                            s2 += k * px[2];
                            s3 += k * px[3];
                        }
                    }
                }

                int p = ((y * w) + x) * 4;
                float srcA = src[p + 3];
                if (c.PreserveAlpha)
                {
                    float r = (float)Math.Clamp((s0 * inv) + bias, 0, 1);
                    float g = (float)Math.Clamp((s1 * inv) + bias, 0, 1);
                    float b = (float)Math.Clamp((s2 * inv) + bias, 0, 1);
                    dst[p] = r * srcA;
                    dst[p + 1] = g * srcA;
                    dst[p + 2] = b * srcA;
                    dst[p + 3] = srcA;
                }
                else
                {
                    float a = (float)Math.Clamp((s3 * inv) + bias, 0, 1);
                    dst[p] = (float)Math.Clamp((s0 * inv) + (bias * a), 0, a);
                    dst[p + 1] = (float)Math.Clamp((s1 * inv) + (bias * a), 0, a);
                    dst[p + 2] = (float)Math.Clamp((s2 * inv) + (bias * a), 0, a);
                    dst[p + 3] = a;
                }
            }
        }
    }

    // One source pixel by absolute coordinates under the edge mode; false for transparent (outside under "none", or outside the data).
    private static bool Fetch(float[] src, int w, int h, int ox, int oy, PxRect edge, int mode, int gx, int gy, float[] px)
    {
        if (gx < edge.X || gx >= edge.Right || gy < edge.Y || gy >= edge.Bottom)
        {
            switch (mode)
            {
                case 0:
                    gx = Math.Clamp(gx, edge.X, edge.Right - 1);
                    gy = Math.Clamp(gy, edge.Y, edge.Bottom - 1);
                    break;
                case 1:
                    gx = edge.X + (((gx - edge.X) % edge.W) + edge.W) % edge.W;
                    gy = edge.Y + (((gy - edge.Y) % edge.H) + edge.H) % edge.H;
                    break;
                default:
                    return false;
            }
        }

        int lx = gx - ox;
        int ly = gy - oy;
        if (lx < 0 || lx >= w || ly < 0 || ly >= h)
        {
            return false;
        }

        int p = ((ly * w) + lx) * 4;
        px[0] = src[p];
        px[1] = src[p + 1];
        px[2] = src[p + 2];
        px[3] = src[p + 3];
        return true;
    }
}
