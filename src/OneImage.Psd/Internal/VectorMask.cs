using System.Buffers.Binary;
using DPath = OneImage.Drawing.Path;
using OneImage.Drawing;

namespace OneImage.Psd;

/// <summary>Reads the path records of a vector mask (the PSD path resource format: 26-byte records, points as 8.24 fixed-point fractions of the image size).</summary>
internal static class VectorMask
{
    /// <summary>Parses a <c>vmsk</c> or <c>vsms</c> block into a path in pixel coordinates, offset by (-originX, -originY). Returns null if there is no usable path.</summary>
    public static DPath? Parse(ReadOnlySpan<byte> data, int width, int height, int originX, int originY, out bool invert, out bool disabled)
    {
        invert = false;
        disabled = false;
        if (data.Length < 8)
        {
            return null;
        }

        uint flags = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4));
        invert = (flags & 1) != 0;
        disabled = (flags & 4) != 0;
        ReadOnlySpan<byte> rec = data.Slice(8);
        int count = rec.Length / 26;
        var b = new PathBuilder();
        int i = 0;
        bool any = false;
        bool initialFill = false;
        while (i < count)
        {
            ReadOnlySpan<byte> r = rec.Slice(i * 26, 26);
            int sel = BinaryPrimitives.ReadUInt16BigEndian(r);
            i++;
            if (sel == 8)
            {
                initialFill = BinaryPrimitives.ReadUInt16BigEndian(r.Slice(2)) == 1;
                continue;
            }

            if (sel is not (0 or 3))
            {
                continue;
            }

            int knots = BinaryPrimitives.ReadUInt16BigEndian(r.Slice(2));
            if (knots > count - i)
            {
                throw new ImageFormatException("A vector mask subpath has more knots than the block holds.");
            }

            if (knots >= 2)
            {
                // Each knot is (control before, anchor, control after); a segment runs from the anchor of one knot
                // through its "after" control and the next knot's "before" control to the next anchor.
                (float X, float Y)[] pre = new (float, float)[knots];
                (float X, float Y)[] anchor = new (float, float)[knots];
                (float X, float Y)[] post = new (float, float)[knots];
                for (int k = 0; k < knots; k++)
                {
                    ReadOnlySpan<byte> kr = rec.Slice((i + k) * 26, 26);
                    pre[k] = Point(kr.Slice(2), width, height, originX, originY);
                    anchor[k] = Point(kr.Slice(10), width, height, originX, originY);
                    post[k] = Point(kr.Slice(18), width, height, originX, originY);
                }

                b.MoveTo(anchor[0].X, anchor[0].Y);
                for (int k = 1; k < knots; k++)
                {
                    b.CubicBezierTo(post[k - 1].X, post[k - 1].Y, pre[k].X, pre[k].Y, anchor[k].X, anchor[k].Y);
                }

                if (sel == 0)
                {
                    b.CubicBezierTo(post[knots - 1].X, post[knots - 1].Y, pre[0].X, pre[0].Y, anchor[0].X, anchor[0].Y);
                }

                b.Close();
                any = true;
            }

            i += knots;
        }

        if (any)
        {
            return b.Build();
        }

        // A vector mask with no subpaths is all or nothing: the initial fill rule says which.
        invert ^= initialFill;
        return null;
    }

    private static (float, float) Point(ReadOnlySpan<byte> p, int width, int height, int originX, int originY)
    {
        // Vertical component first, then horizontal, each a signed 8.24 fixed-point fraction of the image height or width.
        double y = BinaryPrimitives.ReadInt32BigEndian(p) / 16777216.0;
        double x = BinaryPrimitives.ReadInt32BigEndian(p.Slice(4)) / 16777216.0;
        return ((float)(x * width - originX), (float)(y * height - originY));
    }
}
