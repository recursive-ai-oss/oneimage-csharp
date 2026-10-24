using System.Buffers;
using System.Numerics;
using OneImage.Kernels;

namespace OneImage;

/// <summary>Which radial overlay a <see cref="RadialOverlayNode"/> draws.</summary>
internal enum RadialKind
{
    /// <summary>Darkens (or tints) towards the corners.</summary>
    Vignette,

    /// <summary>Tints the center and fades out towards the edge.</summary>
    Glow,
}

/// <summary>
/// Blends a color over the image with an amount that depends only on the distance from a center pixel, inside an optional
/// rectangle. The blend is ordinary source-over of the color (its own alpha times the amount) on the straight pixel, in the
/// stored (encoded) values. Pixels outside the rectangle are untouched.
/// <para>
/// Vignette: amount = clamp(0.9 × d / sqrt(rx² + ry²)). Glow: amount = clamp(1 − 0.95 × d / radius). <c>d</c> is the Euclidean
/// distance in pixels from the integer center (rectangle origin + size / 2).
/// </para>
/// </summary>
internal sealed class RadialOverlayNode : Node
{
    private readonly RadialKind _kind;
    private readonly ImageRect _area;
    private readonly float _a;
    private readonly int _cx;
    private readonly int _cy;
    private readonly float[] _color;
    private readonly PixelFormat _work;

    public RadialOverlayNode(Node input, RadialKind kind, Rgba32 color, double radiusX, double radiusY, ImageRect? area)
        : base(input.Width, input.Height, input.Format, Describe(kind, color, radiusX, radiusY, area), input)
    {
        if (input.Format.Model is not (ColorModel.Gray or ColorModel.Rgb))
        {
            throw new ImageOperationException($"{(kind == RadialKind.Vignette ? "Vignette" : "Glow")} is defined for gray and RGB images, not {input.Format.Model}.");
        }

        _kind = kind;
        _area = area ?? new ImageRect(0, 0, input.Width, input.Height);
        if (!_area.IsInside(input.Width, input.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(area), "The area must lie inside the image.");
        }

        _cx = _area.X + (_area.Width / 2);
        _cy = _area.Y + (_area.Height / 2);
        if (kind == RadialKind.Vignette)
        {
            double rx = radiusX > 0 ? radiusX : _area.Width * 0.5;
            double ry = radiusY > 0 ? radiusY : _area.Height * 0.5;
            _a = (float)(0.9 / Math.Sqrt((rx * rx) + (ry * ry)));
        }
        else
        {
            double half = _area.Width * 0.5;
            double radius = radiusX > 0 ? Math.Min(radiusX, half) : half;
            _a = (float)(0.95 / radius);
        }

        float r = color.R / 255f;
        float g = color.G / 255f;
        float bl = color.B / 255f;
        float alpha = color.A / 255f;
        _color = input.Format.Model == ColorModel.Gray ? [(0.2126f * r) + (0.7152f * g) + (0.0722f * bl), alpha] : [r, g, bl, alpha];
        _work = new PixelFormat(input.Format.Model, SampleType.F32, input.Format.HasAlpha ? AlphaMode.Straight : AlphaMode.None);
    }

    private static OpDescriptor Describe(RadialKind kind, Rgba32 color, double radiusX, double radiusY, ImageRect? area)
    {
        string hex = $"#{color.R:x2}{color.G:x2}{color.B:x2}{color.A:x2}";
        var list = new List<OpParameter> { new("color", hex), new("radius_x", radiusX) };
        if (kind == RadialKind.Vignette)
        {
            list.Add(new OpParameter("radius_y", radiusY));
        }

        if (area is { } a)
        {
            list.Add(new OpParameter("area", $"{a.X},{a.Y},{a.Width},{a.Height}"));
        }

        return new OpDescriptor(kind == RadialKind.Vignette ? "vignette" : "glow", [.. list]);
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        Node input = Inputs[0];
        int ibpp = input.Format.BytesPerPixel;
        if (o.X >= _area.Right || o.Right <= _area.X || o.Y >= _area.Bottom || o.Bottom <= _area.Y)
        {
            // Nothing of the effect reaches this tile: share the input tile as it is.
            return input.GetTile(tx, ty, context);
        }

        int c = _work.ChannelCount;
        int colors = _work.ColorChannels;
        bool hasAlpha = _work.HasAlpha;
        // 8-bit gray and RGB with opaque or no alpha are the common case; they blend on the bytes without a float round trip.
        bool bytePath = input.Format.Sample == SampleType.U8 && input.Format.Alpha != AlphaMode.Premultiplied;
        Tile src = input.GetTile(tx, ty, context);
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        float[] row = Pools<float>.Pool.Rent(bytePath ? 1 : o.Width * c);
        float[] amounts = Pools<float>.Pool.Rent(o.Width);
        try
        {
            int x0 = Math.Max(o.X, _area.X);
            int x1 = Math.Min(o.Right, _area.Right);
            for (int y = 0; y < o.Height; y++)
            {
                ReadOnlySpan<byte> srow = src.Span.Slice(y * Tile.Size * ibpp, o.Width * ibpp);
                Span<byte> drow = tile.Span.Slice(y * Tile.Size * ibpp, o.Width * ibpp);
                int py = o.Y + y;
                if (py < _area.Y || py >= _area.Bottom)
                {
                    srow.CopyTo(drow);
                    continue;
                }

                float dy = py - _cy;
                if (bytePath)
                {
                    srow.CopyTo(drow);
                    BlendRowU8(drow, o.X, x0, x1, dy, amounts);
                    continue;
                }

                ConvertKernel.ConvertRow(srow, input.Format, SpanCast.AsBytes<float>(row.AsSpan(0, o.Width * c)), _work, o.Width);
                for (int x = x0; x < x1; x++)
                {
                    float dx = x - _cx;
                    float d = MathF.Sqrt((dx * dx) + (dy * dy));
                    float amount = _kind == RadialKind.Vignette ? d * _a : 1f - (d * _a);
                    amount = Math.Clamp(amount, 0f, 1f) * _color[^1];
                    int i = (x - o.X) * c;
                    float da = hasAlpha ? row[i + colors] : 1f;
                    float outA = amount + (da * (1f - amount));
                    if (outA > 0f)
                    {
                        float k = da * (1f - amount);
                        for (int ch = 0; ch < colors; ch++)
                        {
                            row[i + ch] = ((_color[ch] * amount) + (row[i + ch] * k)) / outA;
                        }
                    }

                    if (hasAlpha)
                    {
                        row[i + colors] = outA;
                    }
                }

                ConvertKernel.ConvertRow(SpanCast.AsBytes<float>(row.AsSpan(0, o.Width * c)), _work, drow, Format, o.Width);
                if (x0 > o.X || x1 < o.Right)
                {
                    // Columns outside the area keep their exact stored bytes (the float round trip is only exact for some formats).
                    srow[..((x0 - o.X) * ibpp)].CopyTo(drow);
                    srow[((x1 - o.X) * ibpp)..].CopyTo(drow[((x1 - o.X) * ibpp)..]);
                }
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
            src.Release();
            Pools<float>.Pool.Return(row);
            Pools<float>.Pool.Return(amounts);
        }
    }

    // Blends one row of 8-bit pixels in place for columns x0..x1 (image coordinates; the row starts at image column rowX). The
    // amounts are computed a vector at a time; pixels with partial alpha take the general source-over formula.
    private void BlendRowU8(Span<byte> row, int rowX, int x0, int x1, float dy, float[] amounts)
    {
        int channels = Format.ChannelCount;
        int colors = Format.ColorChannels;
        bool hasAlpha = Format.HasAlpha;
        float colorAlpha = _color[^1];
        bool vignette = _kind == RadialKind.Vignette;
        float dy2 = dy * dy;
        if (!vignette)
        {
            // The glow reaches 1 / _a pixels from the center and nothing beyond: skip the rest of the row.
            float reach = 1f / _a;
            if (Math.Abs(dy) >= reach)
            {
                return;
            }

            int half = (int)MathF.Sqrt((reach * reach) - dy2) + 1;
            x0 = Math.Max(x0, _cx - half);
            x1 = Math.Min(x1, _cx + half + 1);
        }

        int n = x1 - x0;
        if (n <= 0)
        {
            return;
        }

        int i = 0;
        int vc = Vector<float>.Count;
        var scale = new Vector<float>(_a);
        var one = Vector<float>.One;
        var alphaScale = new Vector<float>(colorAlpha);
        var dy2v = new Vector<float>(dy2);
        for (; i + vc <= n; i += vc)
        {
            Vector<float> dx = Vector.ConvertToSingle(new Vector<int>(x0 - _cx + i) + Vector<int>.Indices);
            Vector<float> d = Vector.SquareRoot((dx * dx) + dy2v);
            Vector<float> amount = vignette ? d * scale : one - (d * scale);
            (Vector.Min(Vector.Max(amount, Vector<float>.Zero), one) * alphaScale).CopyTo(amounts.AsSpan(i, vc));
        }

        for (; i < n; i++)
        {
            float dx = x0 - _cx + i;
            float d = MathF.Sqrt((dx * dx) + dy2);
            amounts[i] = Math.Clamp(vignette ? d * _a : 1f - (d * _a), 0f, 1f) * colorAlpha;
        }

        int first = (x0 - rowX) * channels;
        if (colors == 3)
        {
            // RGB: the straight-line case of the blend, with the color and loop invariants hoisted.
            float c0 = _color[0] * 255f;
            float c1 = _color[1] * 255f;
            float c2 = _color[2] * 255f;
            Span<byte> px = row.Slice(first, n * channels);
            for (int k = 0; k < n; k++)
            {
                float amount = amounts[k];
                if (amount <= 0f)
                {
                    continue;
                }

                int o = k * channels;
                if (hasAlpha && px[o + 3] != 255)
                {
                    BlendGeneral(px.Slice(o, channels), amount, colors, hasAlpha);
                    continue;
                }

                float s0 = px[o];
                float s1 = px[o + 1];
                float s2 = px[o + 2];
                px[o] = (byte)(s0 + (((c0 - s0) * amount) + 0.5f));
                px[o + 1] = (byte)(s1 + (((c1 - s1) * amount) + 0.5f));
                px[o + 2] = (byte)(s2 + (((c2 - s2) * amount) + 0.5f));
            }

            return;
        }

        for (int k = 0; k < n; k++)
        {
            float amount = amounts[k];
            if (amount <= 0f)
            {
                continue;
            }

            Span<byte> p = row.Slice((x0 - rowX + k) * channels, channels);
            float da = hasAlpha ? p[colors] * (1f / 255f) : 1f;
            if (da == 1f)
            {
                for (int ch = 0; ch < colors; ch++)
                {
                    float s = p[ch];
                    p[ch] = (byte)(s + ((((_color[ch] * 255f) - s) * amount) + 0.5f));
                }

                continue;
            }

            BlendGeneral(p, amount, colors, hasAlpha);
        }
    }

    // Source-over of the color (with the given amount as its alpha) on one pixel whose alpha may be below 1.
    private void BlendGeneral(Span<byte> p, float amount, int colors, bool hasAlpha)
    {
        float da = hasAlpha ? p[colors] * (1f / 255f) : 1f;
        float outA = amount + (da * (1f - amount));
        if (outA > 0f)
        {
            float keep = da * (1f - amount);
            for (int ch = 0; ch < colors; ch++)
            {
                float v = (((_color[ch] * amount) + ((p[ch] * (1f / 255f)) * keep)) / outA) * 255f;
                p[ch] = (byte)(v + 0.5f);
            }
        }

        if (hasAlpha)
        {
            p[colors] = (byte)((outA * 255f) + 0.5f);
        }
    }
}
