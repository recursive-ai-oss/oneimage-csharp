using System;
using System.Runtime.Intrinsics;

namespace OneImage.Compression;

/// <summary>Adler-32 (RFC 1950 section 8.2). The initial value is 1.</summary>
internal static class Adler32
{
    private const uint Base = 65521;

    // Largest block (a multiple of 16) for which the 32-bit sums cannot overflow before reduction.
    private const int Chunk = 5536;

    /// <summary>The checksum of the empty input.</summary>
    public const uint Initial = 1;

    /// <summary>Continues an Adler-32 over <paramref name="data"/>.</summary>
    public static uint Update(uint adler, ReadOnlySpan<byte> data)
    {
        uint s1 = adler & 0xFFFF;
        uint s2 = adler >> 16;
        bool vector = Vector128.IsHardwareAccelerated;
        while (data.Length > 0)
        {
            int n = Math.Min(data.Length, Chunk);
            ReadOnlySpan<byte> block = data.Slice(0, n);
            data = data.Slice(n);
            if (vector && n >= 16)
            {
                int vn = n & ~15;
                Accumulate(block.Slice(0, vn), ref s1, ref s2);
                block = block.Slice(vn);
            }

            foreach (byte b in block)
            {
                s1 += b;
                s2 += s1;
            }

            s1 %= Base;
            s2 %= Base;
        }

        return (s2 << 16) | s1;
    }

    /// <summary>Scalar reference implementation.</summary>
    internal static uint UpdateScalar(uint adler, ReadOnlySpan<byte> data)
    {
        uint s1 = adler & 0xFFFF;
        uint s2 = adler >> 16;
        while (data.Length > 0)
        {
            int n = Math.Min(data.Length, 5552);
            foreach (byte b in data.Slice(0, n))
            {
                s1 += b;
                s2 += s1;
            }

            data = data.Slice(n);
            s1 %= Base;
            s2 %= Base;
        }

        return (s2 << 16) | s1;
    }

    // Processes whole 16-byte blocks. For a block with bytes b0..b15 the running sums advance by
    // s1 += sum(b) and s2 += 16*s1_before + sum((16 - i) * b_i). The per-lane vectors keep both
    // parts separate so the multiplication by 16 happens once at the end.
    private static void Accumulate(ReadOnlySpan<byte> data, ref uint s1, ref uint s2)
    {
        Vector128<ushort> wLo = Vector128.Create((ushort)16, 15, 14, 13, 12, 11, 10, 9);
        Vector128<ushort> wHi = Vector128.Create((ushort)8, 7, 6, 5, 4, 3, 2, 1);
        Vector128<uint> a = Vector128<uint>.Zero;   // running per-lane byte sums
        Vector128<uint> t = Vector128<uint>.Zero;   // sum of 'a' before each block
        Vector128<uint> w = Vector128<uint>.Zero;   // weighted sums
        int blocks = data.Length / 16;
        for (int i = 0; i < data.Length; i += 16)
        {
            Vector128<byte> v = Vector128.Create(data.Slice(i, 16));
            (Vector128<ushort> lo, Vector128<ushort> hi) = Vector128.Widen(v);
            t += a;
            Vector128<ushort> sum16 = lo + hi;
            (Vector128<uint> sl, Vector128<uint> sh) = Vector128.Widen(sum16);
            a += sl + sh;
            Vector128<ushort> w16 = (lo * wLo) + (hi * wHi);
            (Vector128<uint> wl, Vector128<uint> wh) = Vector128.Widen(w16);
            w += wl + wh;
        }

        ulong sumA = Vector128.Sum(a);
        ulong sumT = Vector128.Sum(t);
        ulong sumW = Vector128.Sum(w);
        ulong ns2 = s2 + ((ulong)s1 * (ulong)(blocks * 16)) + (16 * sumT) + sumW;
        s1 = (uint)((s1 + sumA) % Base);
        s2 = (uint)(ns2 % Base);
    }
}
