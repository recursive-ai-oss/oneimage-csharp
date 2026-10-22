using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace OneImage.Kernels;

/// <summary>
/// The VP8 loop filter (RFC 6386 section 15) for one macroblock, 16 pixels along an edge at a time. Arithmetic is done in
/// 16-bit lanes with explicit clamps, which gives exactly the integers of the scalar reference
/// (<see cref="ReferenceEdge"/> and friends, used by the tests): the results do not depend on the vector width.
/// Edges are filtered in the order the specification gives: left macroblock edge, inner vertical edges, top macroblock edge,
/// inner horizontal edges, each for luma and then the chroma planes. Buffers are the decoder's frame planes; the geometry is
/// validated once per macroblock.
/// </summary>
internal static class WebPLoopFilterKernel
{
    /// <summary>Filters the edges of one macroblock with the normal filter. Chroma planes are filtered together (U in the low lanes, V in the high lanes).</summary>
    /// <param name="y">The luma plane.</param>
    /// <param name="yo">Index of the macroblock's top-left luma pixel.</param>
    /// <param name="ys">Luma stride.</param>
    /// <param name="u">The U plane.</param>
    /// <param name="v">The V plane.</param>
    /// <param name="uvo">Index of the macroblock's top-left chroma pixel.</param>
    /// <param name="uvs">Chroma stride.</param>
    /// <param name="left">Filter the left macroblock edge (not the first column).</param>
    /// <param name="top">Filter the top macroblock edge (not the first row).</param>
    /// <param name="inner">Filter the inner edges.</param>
    /// <param name="limit">Edge limit (2 * level + interior limit).</param>
    /// <param name="ilevel">Interior limit.</param>
    /// <param name="hevThreshold">High edge variance threshold.</param>
    public static void FilterNormal(byte[] y, int yo, int ys, byte[] u, byte[] v, int uvo, int uvs, bool left, bool top, bool inner, int limit, int ilevel, int hevThreshold)
    {
        Check(y, yo, ys, 16, left, top);
        Check(u, uvo, uvs, 8, left, top);
        Check(v, uvo, uvs, 8, left, top);
        short tMb = (short)((2 * (limit + 4)) + 1);
        short tIn = (short)((2 * limit) + 1);
        short it = (short)ilevel;
        short hev = (short)hevThreshold;
        if (left)
        {
            VerticalLuma(y, yo, ys, tMb, it, hev, true);
            VerticalChroma(u, v, uvo, uvs, tMb, it, hev, true);
        }

        if (inner)
        {
            for (int k = 4; k < 16; k += 4)
            {
                VerticalLuma(y, yo + k, ys, tIn, it, hev, false);
            }

            VerticalChroma(u, v, uvo + 4, uvs, tIn, it, hev, false);
        }

        if (top)
        {
            HorizontalLuma(y, yo, ys, tMb, it, hev, true);
            HorizontalChroma(u, v, uvo, uvs, tMb, it, hev, true);
        }

        if (inner)
        {
            for (int k = 4; k < 16; k += 4)
            {
                HorizontalLuma(y, yo + (k * ys), ys, tIn, it, hev, false);
            }

            HorizontalChroma(u, v, uvo + (4 * uvs), uvs, tIn, it, hev, false);
        }
    }

    /// <summary>Filters the luma edges of one macroblock with the simple filter (chroma is left alone by the simple filter).</summary>
    public static void FilterSimple(byte[] y, int yo, int ys, bool left, bool top, bool inner, int limit)
    {
        Check(y, yo, ys, 16, left, top);
        short tMb = (short)((2 * (limit + 4)) + 1);
        short tIn = (short)((2 * limit) + 1);
        if (left)
        {
            VerticalLuma(y, yo, ys, tMb, 0, 0, true, simple: true);
        }

        if (inner)
        {
            for (int k = 4; k < 16; k += 4)
            {
                VerticalLuma(y, yo + k, ys, tIn, 0, 0, false, simple: true);
            }
        }

        if (top)
        {
            HorizontalLuma(y, yo, ys, tMb, 0, 0, true, simple: true);
        }

        if (inner)
        {
            for (int k = 4; k < 16; k += 4)
            {
                HorizontalLuma(y, yo + (k * ys), ys, tIn, 0, 0, false, simple: true);
            }
        }
    }

    private static void Check(byte[] p, int o, int stride, int size, bool left, bool top)
    {
        ArgumentNullException.ThrowIfNull(p);
        // Every access lies in [o - 4 * stride - 4, o + (size - 1) * stride + size + 3]; the filter reads four pixels beyond an
        // edge only when that edge exists.
        long first = o - (top ? 4L * stride : 0) - (left ? 4 : 0);
        long last = o + ((size - 1L) * stride) + size - 1;
        if (stride < size || first < 0 || last >= p.Length)
        {
            throw new ArgumentException("Macroblock lies outside the plane.");
        }
    }

    // ---- horizontal edges: the filter runs across rows, 16 pixels along the edge in one vector ----

    private static void HorizontalLuma(byte[] p, int o, int stride, short t2, short it, short hev, bool mbEdge, bool simple = false)
    {
        Span<byte> s = p;
        Vector128<byte> p1 = Vector128.Create(s.Slice(o - (2 * stride), 16));
        Vector128<byte> p0 = Vector128.Create(s.Slice(o - stride, 16));
        Vector128<byte> q0 = Vector128.Create(s.Slice(o, 16));
        Vector128<byte> q1 = Vector128.Create(s.Slice(o + stride, 16));
        if (simple)
        {
            Simple(p1, p0, q0, q1, t2, out Vector128<byte> np0, out Vector128<byte> nq0);
            np0.CopyTo(s.Slice(o - stride, 16));
            nq0.CopyTo(s.Slice(o, 16));
            return;
        }

        Vector128<byte> p3 = Vector128.Create(s.Slice(o - (4 * stride), 16));
        Vector128<byte> p2 = Vector128.Create(s.Slice(o - (3 * stride), 16));
        Vector128<byte> q2 = Vector128.Create(s.Slice(o + (2 * stride), 16));
        Vector128<byte> q3 = Vector128.Create(s.Slice(o + (3 * stride), 16));
        Normal(ref p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, ref q3, t2, it, hev, mbEdge);
        if (mbEdge)
        {
            p2.CopyTo(s.Slice(o - (3 * stride), 16));
            q2.CopyTo(s.Slice(o + (2 * stride), 16));
        }

        p1.CopyTo(s.Slice(o - (2 * stride), 16));
        p0.CopyTo(s.Slice(o - stride, 16));
        q0.CopyTo(s.Slice(o, 16));
        q1.CopyTo(s.Slice(o + stride, 16));
    }

    private static Vector128<byte> Pair(Span<byte> u, Span<byte> v, int o) =>
        Vector128.Create(Vector64.Create<byte>(u.Slice(o, 8)), Vector64.Create<byte>(v.Slice(o, 8)));

    private static void Split(Vector128<byte> x, Span<byte> u, Span<byte> v, int o)
    {
        x.GetLower().CopyTo(u.Slice(o, 8));
        x.GetUpper().CopyTo(v.Slice(o, 8));
    }

    private static void HorizontalChroma(byte[] pu, byte[] pv, int o, int stride, short t2, short it, short hev, bool mbEdge)
    {
        Span<byte> u = pu;
        Span<byte> v = pv;
        Vector128<byte> p3 = Pair(u, v, o - (4 * stride));
        Vector128<byte> p2 = Pair(u, v, o - (3 * stride));
        Vector128<byte> p1 = Pair(u, v, o - (2 * stride));
        Vector128<byte> p0 = Pair(u, v, o - stride);
        Vector128<byte> q0 = Pair(u, v, o);
        Vector128<byte> q1 = Pair(u, v, o + stride);
        Vector128<byte> q2 = Pair(u, v, o + (2 * stride));
        Vector128<byte> q3 = Pair(u, v, o + (3 * stride));
        Normal(ref p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, ref q3, t2, it, hev, mbEdge);
        if (mbEdge)
        {
            Split(p2, u, v, o - (3 * stride));
            Split(q2, u, v, o + (2 * stride));
        }

        Split(p1, u, v, o - (2 * stride));
        Split(p0, u, v, o - stride);
        Split(q0, u, v, o);
        Split(q1, u, v, o + stride);
    }

    // ---- vertical edges: eight columns around the edge are transposed so that one vector holds one column of 16 rows ----

    private static void VerticalLuma(byte[] p, int o, int stride, short t2, short it, short hev, bool mbEdge, bool simple = false)
    {
        Span<byte> s = p;
        Span<byte> cols = stackalloc byte[8 * 16];
        if (Sse2.IsSupported)
        {
            Span<ulong> rows = stackalloc ulong[16];
            for (int r = 0; r < 16; r++)
            {
                rows[r] = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(o - 4 + (r * stride), 8));
            }

            Transpose8Columns(rows, cols);
        }
        else
        {
            for (int r = 0; r < 16; r++)
            {
                ulong x = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(o - 4 + (r * stride), 8));
                for (int c = 0; c < 8; c++)
                {
                    cols[(c * 16) + r] = (byte)(x >> (8 * c));
                }
            }
        }

        Vector128<byte> p3 = Vector128.Create<byte>(cols.Slice(0, 16));
        Vector128<byte> p2 = Vector128.Create<byte>(cols.Slice(16, 16));
        Vector128<byte> p1 = Vector128.Create<byte>(cols.Slice(32, 16));
        Vector128<byte> p0 = Vector128.Create<byte>(cols.Slice(48, 16));
        Vector128<byte> q0 = Vector128.Create<byte>(cols.Slice(64, 16));
        Vector128<byte> q1 = Vector128.Create<byte>(cols.Slice(80, 16));
        Vector128<byte> q2 = Vector128.Create<byte>(cols.Slice(96, 16));
        Vector128<byte> q3 = Vector128.Create<byte>(cols.Slice(112, 16));
        if (simple)
        {
            Simple(p1, p0, q0, q1, t2, out p0, out q0);
        }
        else
        {
            Normal(ref p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, ref q3, t2, it, hev, mbEdge);
        }

        p2.CopyTo(cols.Slice(16, 16));
        p1.CopyTo(cols.Slice(32, 16));
        p0.CopyTo(cols.Slice(48, 16));
        q0.CopyTo(cols.Slice(64, 16));
        q1.CopyTo(cols.Slice(80, 16));
        q2.CopyTo(cols.Slice(96, 16));
        if (Sse2.IsSupported)
        {
            Span<ulong> rows = stackalloc ulong[16];
            TransposeBack(cols, rows);
            for (int r = 0; r < 16; r++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(o - 4 + (r * stride), 8), rows[r]);
            }

            return;
        }

        for (int r = 0; r < 16; r++)
        {
            ulong x = 0;
            for (int c = 0; c < 8; c++)
            {
                x |= (ulong)cols[(c * 16) + r] << (8 * c);
            }

            // The outer columns (p3, q3) were not modified; writing them back unchanged is harmless.
            BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(o - 4 + (r * stride), 8), x);
        }
    }

    // 16 rows of 8 bytes -> 8 columns of 16 bytes (column c at cols[16 * c ..]), by byte/word/dword/qword unpacking.
    private static void Transpose8Columns(ReadOnlySpan<ulong> rows, Span<byte> cols)
    {
        Vector128<byte> a0 = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(rows[0]).AsByte(), Vector128.CreateScalarUnsafe(rows[1]).AsByte());
        Vector128<byte> a1 = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(rows[2]).AsByte(), Vector128.CreateScalarUnsafe(rows[3]).AsByte());
        Vector128<byte> a2 = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(rows[4]).AsByte(), Vector128.CreateScalarUnsafe(rows[5]).AsByte());
        Vector128<byte> a3 = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(rows[6]).AsByte(), Vector128.CreateScalarUnsafe(rows[7]).AsByte());
        Vector128<byte> a4 = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(rows[8]).AsByte(), Vector128.CreateScalarUnsafe(rows[9]).AsByte());
        Vector128<byte> a5 = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(rows[10]).AsByte(), Vector128.CreateScalarUnsafe(rows[11]).AsByte());
        Vector128<byte> a6 = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(rows[12]).AsByte(), Vector128.CreateScalarUnsafe(rows[13]).AsByte());
        Vector128<byte> a7 = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(rows[14]).AsByte(), Vector128.CreateScalarUnsafe(rows[15]).AsByte());
        Vector128<ushort> b0 = Sse2.UnpackLow(a0.AsUInt16(), a1.AsUInt16());
        Vector128<ushort> b1 = Sse2.UnpackHigh(a0.AsUInt16(), a1.AsUInt16());
        Vector128<ushort> b2 = Sse2.UnpackLow(a2.AsUInt16(), a3.AsUInt16());
        Vector128<ushort> b3 = Sse2.UnpackHigh(a2.AsUInt16(), a3.AsUInt16());
        Vector128<ushort> b4 = Sse2.UnpackLow(a4.AsUInt16(), a5.AsUInt16());
        Vector128<ushort> b5 = Sse2.UnpackHigh(a4.AsUInt16(), a5.AsUInt16());
        Vector128<ushort> b6 = Sse2.UnpackLow(a6.AsUInt16(), a7.AsUInt16());
        Vector128<ushort> b7 = Sse2.UnpackHigh(a6.AsUInt16(), a7.AsUInt16());
        Vector128<uint> d0 = Sse2.UnpackLow(b0.AsUInt32(), b2.AsUInt32());
        Vector128<uint> d1 = Sse2.UnpackHigh(b0.AsUInt32(), b2.AsUInt32());
        Vector128<uint> d2 = Sse2.UnpackLow(b1.AsUInt32(), b3.AsUInt32());
        Vector128<uint> d3 = Sse2.UnpackHigh(b1.AsUInt32(), b3.AsUInt32());
        Vector128<uint> e0 = Sse2.UnpackLow(b4.AsUInt32(), b6.AsUInt32());
        Vector128<uint> e1 = Sse2.UnpackHigh(b4.AsUInt32(), b6.AsUInt32());
        Vector128<uint> e2 = Sse2.UnpackLow(b5.AsUInt32(), b7.AsUInt32());
        Vector128<uint> e3 = Sse2.UnpackHigh(b5.AsUInt32(), b7.AsUInt32());
        Sse2.UnpackLow(d0.AsUInt64(), e0.AsUInt64()).AsByte().CopyTo(cols.Slice(0, 16));
        Sse2.UnpackHigh(d0.AsUInt64(), e0.AsUInt64()).AsByte().CopyTo(cols.Slice(16, 16));
        Sse2.UnpackLow(d1.AsUInt64(), e1.AsUInt64()).AsByte().CopyTo(cols.Slice(32, 16));
        Sse2.UnpackHigh(d1.AsUInt64(), e1.AsUInt64()).AsByte().CopyTo(cols.Slice(48, 16));
        Sse2.UnpackLow(d2.AsUInt64(), e2.AsUInt64()).AsByte().CopyTo(cols.Slice(64, 16));
        Sse2.UnpackHigh(d2.AsUInt64(), e2.AsUInt64()).AsByte().CopyTo(cols.Slice(80, 16));
        Sse2.UnpackLow(d3.AsUInt64(), e3.AsUInt64()).AsByte().CopyTo(cols.Slice(96, 16));
        Sse2.UnpackHigh(d3.AsUInt64(), e3.AsUInt64()).AsByte().CopyTo(cols.Slice(112, 16));
    }

    // The inverse: 8 columns of 16 bytes -> 16 rows of 8 bytes.
    private static void TransposeBack(ReadOnlySpan<byte> cols, Span<ulong> rows)
    {
        Vector128<byte> c0 = Vector128.Create(cols.Slice(0, 16));
        Vector128<byte> c1 = Vector128.Create(cols.Slice(16, 16));
        Vector128<byte> c2 = Vector128.Create(cols.Slice(32, 16));
        Vector128<byte> c3 = Vector128.Create(cols.Slice(48, 16));
        Vector128<byte> c4 = Vector128.Create(cols.Slice(64, 16));
        Vector128<byte> c5 = Vector128.Create(cols.Slice(80, 16));
        Vector128<byte> c6 = Vector128.Create(cols.Slice(96, 16));
        Vector128<byte> c7 = Vector128.Create(cols.Slice(112, 16));
        // Pairs of columns, rows 0-7 (low) and 8-15 (high).
        Vector128<ushort> f01l = Sse2.UnpackLow(c0, c1).AsUInt16();
        Vector128<ushort> f01h = Sse2.UnpackHigh(c0, c1).AsUInt16();
        Vector128<ushort> f23l = Sse2.UnpackLow(c2, c3).AsUInt16();
        Vector128<ushort> f23h = Sse2.UnpackHigh(c2, c3).AsUInt16();
        Vector128<ushort> f45l = Sse2.UnpackLow(c4, c5).AsUInt16();
        Vector128<ushort> f45h = Sse2.UnpackHigh(c4, c5).AsUInt16();
        Vector128<ushort> f67l = Sse2.UnpackLow(c6, c7).AsUInt16();
        Vector128<ushort> f67h = Sse2.UnpackHigh(c6, c7).AsUInt16();
        // Quads of columns 0-3 and 4-7 for rows 0-3, 4-7, 8-11, 12-15 (each dword = one row).
        Vector128<uint> q0a = Sse2.UnpackLow(f01l, f23l).AsUInt32();
        Vector128<uint> q0b = Sse2.UnpackHigh(f01l, f23l).AsUInt32();
        Vector128<uint> q0c = Sse2.UnpackLow(f01h, f23h).AsUInt32();
        Vector128<uint> q0d = Sse2.UnpackHigh(f01h, f23h).AsUInt32();
        Vector128<uint> q1a = Sse2.UnpackLow(f45l, f67l).AsUInt32();
        Vector128<uint> q1b = Sse2.UnpackHigh(f45l, f67l).AsUInt32();
        Vector128<uint> q1c = Sse2.UnpackLow(f45h, f67h).AsUInt32();
        Vector128<uint> q1d = Sse2.UnpackHigh(f45h, f67h).AsUInt32();
        // Join the halves: each qword = one row.
        Vector128<ulong> r01 = Sse2.UnpackLow(q0a, q1a).AsUInt64();
        Vector128<ulong> r23 = Sse2.UnpackHigh(q0a, q1a).AsUInt64();
        Vector128<ulong> r45 = Sse2.UnpackLow(q0b, q1b).AsUInt64();
        Vector128<ulong> r67 = Sse2.UnpackHigh(q0b, q1b).AsUInt64();
        Vector128<ulong> r89 = Sse2.UnpackLow(q0c, q1c).AsUInt64();
        Vector128<ulong> r1011 = Sse2.UnpackHigh(q0c, q1c).AsUInt64();
        Vector128<ulong> r1213 = Sse2.UnpackLow(q0d, q1d).AsUInt64();
        Vector128<ulong> r1415 = Sse2.UnpackHigh(q0d, q1d).AsUInt64();
        r01.CopyTo(rows.Slice(0, 2));
        r23.CopyTo(rows.Slice(2, 2));
        r45.CopyTo(rows.Slice(4, 2));
        r67.CopyTo(rows.Slice(6, 2));
        r89.CopyTo(rows.Slice(8, 2));
        r1011.CopyTo(rows.Slice(10, 2));
        r1213.CopyTo(rows.Slice(12, 2));
        r1415.CopyTo(rows.Slice(14, 2));
    }

    private static void VerticalChroma(byte[] pu, byte[] pv, int o, int stride, short t2, short it, short hev, bool mbEdge)
    {
        Span<byte> u = pu;
        Span<byte> v = pv;
        Span<byte> cols = stackalloc byte[8 * 16];
        if (Sse2.IsSupported)
        {
            Span<ulong> rows = stackalloc ulong[16];
            for (int r = 0; r < 8; r++)
            {
                rows[r] = BinaryPrimitives.ReadUInt64LittleEndian(u.Slice(o - 4 + (r * stride), 8));
                rows[8 + r] = BinaryPrimitives.ReadUInt64LittleEndian(v.Slice(o - 4 + (r * stride), 8));
            }

            Transpose8Columns(rows, cols);
        }
        else
        for (int r = 0; r < 8; r++)
        {
            ulong xu = BinaryPrimitives.ReadUInt64LittleEndian(u.Slice(o - 4 + (r * stride), 8));
            ulong xv = BinaryPrimitives.ReadUInt64LittleEndian(v.Slice(o - 4 + (r * stride), 8));
            for (int c = 0; c < 8; c++)
            {
                cols[(c * 16) + r] = (byte)(xu >> (8 * c));
                cols[(c * 16) + 8 + r] = (byte)(xv >> (8 * c));
            }
        }

        Vector128<byte> p3 = Vector128.Create<byte>(cols.Slice(0, 16));
        Vector128<byte> p2 = Vector128.Create<byte>(cols.Slice(16, 16));
        Vector128<byte> p1 = Vector128.Create<byte>(cols.Slice(32, 16));
        Vector128<byte> p0 = Vector128.Create<byte>(cols.Slice(48, 16));
        Vector128<byte> q0 = Vector128.Create<byte>(cols.Slice(64, 16));
        Vector128<byte> q1 = Vector128.Create<byte>(cols.Slice(80, 16));
        Vector128<byte> q2 = Vector128.Create<byte>(cols.Slice(96, 16));
        Vector128<byte> q3 = Vector128.Create<byte>(cols.Slice(112, 16));
        Normal(ref p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, ref q3, t2, it, hev, mbEdge);
        p2.CopyTo(cols.Slice(16, 16));
        p1.CopyTo(cols.Slice(32, 16));
        p0.CopyTo(cols.Slice(48, 16));
        q0.CopyTo(cols.Slice(64, 16));
        q1.CopyTo(cols.Slice(80, 16));
        q2.CopyTo(cols.Slice(96, 16));
        if (Sse2.IsSupported)
        {
            Span<ulong> rows = stackalloc ulong[16];
            TransposeBack(cols, rows);
            for (int r = 0; r < 8; r++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(u.Slice(o - 4 + (r * stride), 8), rows[r]);
                BinaryPrimitives.WriteUInt64LittleEndian(v.Slice(o - 4 + (r * stride), 8), rows[8 + r]);
            }

            return;
        }

        for (int r = 0; r < 8; r++)
        {
            ulong xu = 0;
            ulong xv = 0;
            for (int c = 0; c < 8; c++)
            {
                xu |= (ulong)cols[(c * 16) + r] << (8 * c);
                xv |= (ulong)cols[(c * 16) + 8 + r] << (8 * c);
            }

            BinaryPrimitives.WriteUInt64LittleEndian(u.Slice(o - 4 + (r * stride), 8), xu);
            BinaryPrimitives.WriteUInt64LittleEndian(v.Slice(o - 4 + (r * stride), 8), xv);
        }
    }

    // ---- the filters, 16 lanes as two vectors of eight 16-bit values ----

    private static void Simple(Vector128<byte> p1, Vector128<byte> p0, Vector128<byte> q0, Vector128<byte> q1, short t2, out Vector128<byte> np0, out Vector128<byte> nq0)
    {
        (Vector128<ushort> p1l, Vector128<ushort> p1h) = Vector128.Widen(p1);
        (Vector128<ushort> p0l, Vector128<ushort> p0h) = Vector128.Widen(p0);
        (Vector128<ushort> q0l, Vector128<ushort> q0h) = Vector128.Widen(q0);
        (Vector128<ushort> q1l, Vector128<ushort> q1h) = Vector128.Widen(q1);
        SimpleHalf(p1l.AsInt16(), p0l.AsInt16(), q0l.AsInt16(), q1l.AsInt16(), t2, out Vector128<short> a0, out Vector128<short> b0);
        SimpleHalf(p1h.AsInt16(), p0h.AsInt16(), q0h.AsInt16(), q1h.AsInt16(), t2, out Vector128<short> a1, out Vector128<short> b1);
        np0 = Vector128.Narrow(a0.AsUInt16(), a1.AsUInt16());
        nq0 = Vector128.Narrow(b0.AsUInt16(), b1.AsUInt16());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SimpleHalf(Vector128<short> p1, Vector128<short> p0, Vector128<short> q0, Vector128<short> q1, short t2, out Vector128<short> np0, out Vector128<short> nq0)
    {
        Vector128<short> mask = Vector128.LessThanOrEqual((Vector128.Abs(p0 - q0) * 4) + Vector128.Abs(p1 - q1), Vector128.Create(t2));
        Filter2(p1, p0, q0, q1, out Vector128<short> f0, out Vector128<short> g0);
        np0 = Vector128.ConditionalSelect(mask, f0, p0);
        nq0 = Vector128.ConditionalSelect(mask, g0, q0);
    }

    private static void Normal(ref Vector128<byte> p3, ref Vector128<byte> p2, ref Vector128<byte> p1, ref Vector128<byte> p0, ref Vector128<byte> q0, ref Vector128<byte> q1, ref Vector128<byte> q2, ref Vector128<byte> q3, short t2, short it, short hev, bool mbEdge)
    {
        (Vector128<ushort> a3, Vector128<ushort> b3) = Vector128.Widen(p3);
        (Vector128<ushort> a2, Vector128<ushort> b2) = Vector128.Widen(p2);
        (Vector128<ushort> a1, Vector128<ushort> b1) = Vector128.Widen(p1);
        (Vector128<ushort> a0, Vector128<ushort> b0) = Vector128.Widen(p0);
        (Vector128<ushort> c0, Vector128<ushort> d0) = Vector128.Widen(q0);
        (Vector128<ushort> c1, Vector128<ushort> d1) = Vector128.Widen(q1);
        (Vector128<ushort> c2, Vector128<ushort> d2) = Vector128.Widen(q2);
        (Vector128<ushort> c3, Vector128<ushort> d3) = Vector128.Widen(q3);
        NormalHalf(a3.AsInt16(), a2.AsInt16(), a1.AsInt16(), a0.AsInt16(), c0.AsInt16(), c1.AsInt16(), c2.AsInt16(), c3.AsInt16(), t2, it, hev, mbEdge,
            out Vector128<short> lp2, out Vector128<short> lp1, out Vector128<short> lp0, out Vector128<short> lq0, out Vector128<short> lq1, out Vector128<short> lq2);
        NormalHalf(b3.AsInt16(), b2.AsInt16(), b1.AsInt16(), b0.AsInt16(), d0.AsInt16(), d1.AsInt16(), d2.AsInt16(), d3.AsInt16(), t2, it, hev, mbEdge,
            out Vector128<short> hp2, out Vector128<short> hp1, out Vector128<short> hp0, out Vector128<short> hq0, out Vector128<short> hq1, out Vector128<short> hq2);
        p2 = Vector128.Narrow(lp2.AsUInt16(), hp2.AsUInt16());
        p1 = Vector128.Narrow(lp1.AsUInt16(), hp1.AsUInt16());
        p0 = Vector128.Narrow(lp0.AsUInt16(), hp0.AsUInt16());
        q0 = Vector128.Narrow(lq0.AsUInt16(), hq0.AsUInt16());
        q1 = Vector128.Narrow(lq1.AsUInt16(), hq1.AsUInt16());
        q2 = Vector128.Narrow(lq2.AsUInt16(), hq2.AsUInt16());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Clamp(Vector128<short> v, short lo, short hi) =>
        Vector128.Max(Vector128.Min(v, Vector128.Create(hi)), Vector128.Create(lo));

    // DoFilter2: adjusts the two pixels next to the edge using the outer taps.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Filter2(Vector128<short> p1, Vector128<short> p0, Vector128<short> q0, Vector128<short> q1, out Vector128<short> np0, out Vector128<short> nq0)
    {
        Vector128<short> a = ((q0 - p0) * 3) + Clamp(p1 - q1, -128, 127);
        Vector128<short> a1 = Clamp(Vector128.ShiftRightArithmetic(a + Vector128.Create((short)4), 3), -16, 15);
        Vector128<short> a2 = Clamp(Vector128.ShiftRightArithmetic(a + Vector128.Create((short)3), 3), -16, 15);
        np0 = Clamp(p0 + a2, 0, 255);
        nq0 = Clamp(q0 - a1, 0, 255);
    }

    private static void NormalHalf(
        Vector128<short> p3, Vector128<short> p2, Vector128<short> p1, Vector128<short> p0, Vector128<short> q0, Vector128<short> q1, Vector128<short> q2, Vector128<short> q3,
        short t2, short it, short hevT, bool mbEdge,
        out Vector128<short> op2, out Vector128<short> op1, out Vector128<short> op0, out Vector128<short> oq0, out Vector128<short> oq1, out Vector128<short> oq2)
    {
        Vector128<short> vit = Vector128.Create(it);
        Vector128<short> mask = Vector128.LessThanOrEqual((Vector128.Abs(p0 - q0) * 4) + Vector128.Abs(p1 - q1), Vector128.Create(t2));
        mask &= Vector128.LessThanOrEqual(Vector128.Abs(p3 - p2), vit);
        mask &= Vector128.LessThanOrEqual(Vector128.Abs(p2 - p1), vit);
        mask &= Vector128.LessThanOrEqual(Vector128.Abs(p1 - p0), vit);
        mask &= Vector128.LessThanOrEqual(Vector128.Abs(q3 - q2), vit);
        mask &= Vector128.LessThanOrEqual(Vector128.Abs(q2 - q1), vit);
        mask &= Vector128.LessThanOrEqual(Vector128.Abs(q1 - q0), vit);
        Vector128<short> vhev = Vector128.Create(hevT);
        Vector128<short> hev = Vector128.GreaterThan(Vector128.Abs(p1 - p0), vhev) | Vector128.GreaterThan(Vector128.Abs(q1 - q0), vhev);

        Filter2(p1, p0, q0, q1, out Vector128<short> h0, out Vector128<short> hq);
        Vector128<short> round = Vector128.Create((short)63);
        if (mbEdge)
        {
            Vector128<short> a = Clamp(((q0 - p0) * 3) + Clamp(p1 - q1, -128, 127), -128, 127);
            Vector128<short> a1 = Vector128.ShiftRightArithmetic((a * 27) + round, 7);
            Vector128<short> a2 = Vector128.ShiftRightArithmetic((a * 18) + round, 7);
            Vector128<short> a3 = Vector128.ShiftRightArithmetic((a * 9) + round, 7);
            Vector128<short> np2 = Clamp(p2 + a3, 0, 255);
            Vector128<short> np1 = Clamp(p1 + a2, 0, 255);
            Vector128<short> np0 = Clamp(p0 + a1, 0, 255);
            Vector128<short> nq0 = Clamp(q0 - a1, 0, 255);
            Vector128<short> nq1 = Clamp(q1 - a2, 0, 255);
            Vector128<short> nq2 = Clamp(q2 - a3, 0, 255);
            // High edge variance: only the two pixels next to the edge change (DoFilter2); otherwise six pixels (DoFilter6).
            Vector128<short> use = mask & ~hev;
            op2 = Vector128.ConditionalSelect(use, np2, p2);
            op1 = Vector128.ConditionalSelect(use, np1, p1);
            oq1 = Vector128.ConditionalSelect(use, nq1, q1);
            oq2 = Vector128.ConditionalSelect(use, nq2, q2);
            Vector128<short> p0New = Vector128.ConditionalSelect(hev, h0, np0);
            Vector128<short> q0New = Vector128.ConditionalSelect(hev, hq, nq0);
            op0 = Vector128.ConditionalSelect(mask, p0New, p0);
            oq0 = Vector128.ConditionalSelect(mask, q0New, q0);
        }
        else
        {
            // DoFilter4: no outer-tap term.
            Vector128<short> a = (q0 - p0) * 3;
            Vector128<short> a1 = Clamp(Vector128.ShiftRightArithmetic(a + Vector128.Create((short)4), 3), -16, 15);
            Vector128<short> a2 = Clamp(Vector128.ShiftRightArithmetic(a + Vector128.Create((short)3), 3), -16, 15);
            Vector128<short> a3 = Vector128.ShiftRightArithmetic(a1 + Vector128.Create((short)1), 1);
            Vector128<short> np1 = Clamp(p1 + a3, 0, 255);
            Vector128<short> np0 = Clamp(p0 + a2, 0, 255);
            Vector128<short> nq0 = Clamp(q0 - a1, 0, 255);
            Vector128<short> nq1 = Clamp(q1 - a3, 0, 255);
            Vector128<short> use = mask & ~hev;
            op2 = p2;
            oq2 = q2;
            op1 = Vector128.ConditionalSelect(use, np1, p1);
            oq1 = Vector128.ConditionalSelect(use, nq1, q1);
            Vector128<short> p0New = Vector128.ConditionalSelect(hev, h0, np0);
            Vector128<short> q0New = Vector128.ConditionalSelect(hev, hq, nq0);
            op0 = Vector128.ConditionalSelect(mask, p0New, p0);
            oq0 = Vector128.ConditionalSelect(mask, q0New, q0);
        }
    }

    // ---- scalar reference, used by the tests and as the specification of the vector code ----

    /// <summary>Scalar reference for one run of <paramref name="count"/> edge positions (the semantics of the vector paths).</summary>
    /// <param name="p">The plane.</param>
    /// <param name="o">Index of the first q0 sample.</param>
    /// <param name="step">Distance between samples across the edge.</param>
    /// <param name="along">Distance between successive positions on the edge.</param>
    /// <param name="count">Number of positions.</param>
    /// <param name="t2">Edge threshold <c>2 * limit + 1</c>.</param>
    /// <param name="it">Interior limit; ignored when <paramref name="simple"/>.</param>
    /// <param name="hevT">High edge variance threshold; ignored when <paramref name="simple"/>.</param>
    /// <param name="mbEdge">Macroblock edge (six-tap filter) rather than inner edge.</param>
    /// <param name="simple">Use the simple filter.</param>
    internal static void ReferenceEdge(byte[] p, int o, int step, int along, int count, int t2, int it, int hevT, bool mbEdge, bool simple)
    {
        static int Abs(int v) => v < 0 ? -v : v;
        static int S1(int v) => v < -128 ? -128 : v > 127 ? 127 : v;
        static int S2(int v) => v < -16 ? -16 : v > 15 ? 15 : v;
        static byte C(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        for (int i = 0; i < count; i++, o += along)
        {
            int p3 = p[o - (4 * step)];
            int p2 = p[o - (3 * step)];
            int p1 = p[o - (2 * step)];
            int p0 = p[o - step];
            int q0 = p[o];
            int q1 = p[o + step];
            int q2 = p[o + (2 * step)];
            int q3 = p[o + (3 * step)];
            if ((4 * Abs(p0 - q0)) + Abs(p1 - q1) > t2)
            {
                continue;
            }

            if (!simple && !(Abs(p3 - p2) <= it && Abs(p2 - p1) <= it && Abs(p1 - p0) <= it && Abs(q3 - q2) <= it && Abs(q2 - q1) <= it && Abs(q1 - q0) <= it))
            {
                continue;
            }

            bool hev = !simple && (Abs(p1 - p0) > hevT || Abs(q1 - q0) > hevT);
            if (simple || hev)
            {
                int a = (3 * (q0 - p0)) + S1(p1 - q1);
                int a1 = S2((a + 4) >> 3);
                int a2 = S2((a + 3) >> 3);
                p[o - step] = C(p0 + a2);
                p[o] = C(q0 - a1);
            }
            else if (mbEdge)
            {
                int a = S1((3 * (q0 - p0)) + S1(p1 - q1));
                int a1 = ((27 * a) + 63) >> 7;
                int a2 = ((18 * a) + 63) >> 7;
                int a3 = ((9 * a) + 63) >> 7;
                p[o - (3 * step)] = C(p2 + a3);
                p[o - (2 * step)] = C(p1 + a2);
                p[o - step] = C(p0 + a1);
                p[o] = C(q0 - a1);
                p[o + step] = C(q1 - a2);
                p[o + (2 * step)] = C(q2 - a3);
            }
            else
            {
                int a = 3 * (q0 - p0);
                int a1 = S2((a + 4) >> 3);
                int a2 = S2((a + 3) >> 3);
                int a3 = (a1 + 1) >> 1;
                p[o - (2 * step)] = C(p1 + a3);
                p[o - step] = C(p0 + a2);
                p[o] = C(q0 - a1);
                p[o + step] = C(q1 - a3);
            }
        }
    }
}
