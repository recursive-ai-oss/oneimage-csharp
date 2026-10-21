using System;
using System.Buffers.Binary;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace OneImage.Compression;

/// <summary>
/// CRC-32 as used by PNG, zlib and gzip (ISO 3309 / ITU-T V.42, reflected polynomial 0xEDB88320).
/// Values are the finished checksum: start from 0 and feed chunks with <see cref="Update"/>.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Tables = BuildTables();

    /// <summary>Continues a CRC-32 over <paramref name="data"/>. Pass 0 for the first chunk.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = ~crc;
        if (Pclmulqdq.IsSupported && Sse41.IsSupported && data.Length >= 64)
        {
            int n = data.Length & ~15;
            c = UpdateClmul(c, data.Slice(0, n));
            data = data.Slice(n);
        }
        else if (System.Runtime.Intrinsics.Arm.Crc32.Arm64.IsSupported)
        {
            while (data.Length >= 8)
            {
                c = System.Runtime.Intrinsics.Arm.Crc32.Arm64.ComputeCrc32(c, BinaryPrimitives.ReadUInt64LittleEndian(data));
                data = data.Slice(8);
            }

            for (int i = 0; i < data.Length; i++)
            {
                c = System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32(c, data[i]);
            }

            return ~c;
        }

        return ~UpdateTable(c, data);
    }

    /// <summary>Computes the CRC-32 of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    /// <summary>Scalar reference implementation (slice-by-8) over the raw register (no inversion).</summary>
    internal static uint UpdateTable(uint c, ReadOnlySpan<byte> data)
    {
        uint[] t = Tables;
        while (data.Length >= 8)
        {
            uint lo = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ c;
            uint hi = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
            c = t[0x700 + (lo & 0xFF)] ^ t[0x600 + ((lo >> 8) & 0xFF)] ^ t[0x500 + ((lo >> 16) & 0xFF)] ^ t[0x400 + (lo >> 24)]
              ^ t[0x300 + (hi & 0xFF)] ^ t[0x200 + ((hi >> 8) & 0xFF)] ^ t[0x100 + ((hi >> 16) & 0xFF)] ^ t[hi >> 24];
            data = data.Slice(8);
        }

        for (int i = 0; i < data.Length; i++)
        {
            c = t[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        }

        return c;
    }

    // Carry-less multiply folding (Intel "Fast CRC Computation Using PCLMULQDQ"). Length is a multiple of 16
    // and at least 64. Works on the raw register.
    private static uint UpdateClmul(uint crc, ReadOnlySpan<byte> data)
    {
        Vector128<ulong> k1k2 = Vector128.Create(0x0154442bd4UL, 0x01c6e41596UL);
        Vector128<ulong> k3k4 = Vector128.Create(0x01751997d0UL, 0x00ccaa009eUL);
        Vector128<ulong> k5 = Vector128.Create(0x0163cd6124UL, 0UL);
        Vector128<ulong> poly = Vector128.Create(0x01db710641UL, 0x01f7011641UL);
        Vector128<ulong> mask32 = Vector128.Create(0xFFFFFFFFUL, 0UL);

        Vector128<ulong> x1 = Load(data, 0) ^ Vector128.Create((ulong)crc, 0UL);
        Vector128<ulong> x2 = Load(data, 16);
        Vector128<ulong> x3 = Load(data, 32);
        Vector128<ulong> x4 = Load(data, 48);
        int pos = 64;

        while (data.Length - pos >= 64)
        {
            x1 = Fold(x1, k1k2, Load(data, pos));
            x2 = Fold(x2, k1k2, Load(data, pos + 16));
            x3 = Fold(x3, k1k2, Load(data, pos + 32));
            x4 = Fold(x4, k1k2, Load(data, pos + 48));
            pos += 64;
        }

        x1 = Fold(x1, k3k4, x2);
        x1 = Fold(x1, k3k4, x3);
        x1 = Fold(x1, k3k4, x4);

        while (data.Length - pos >= 16)
        {
            x1 = Fold(x1, k3k4, Load(data, pos));
            pos += 16;
        }

        // 128 -> 64 bits, then 64 -> 32 bits.
        Vector128<ulong> t = Pclmulqdq.CarrylessMultiply(x1, k3k4, 0x10);
        x1 = Sse2.ShiftRightLogical128BitLane(x1, 8) ^ t;
        t = Sse2.ShiftRightLogical128BitLane(x1, 4);
        x1 = Pclmulqdq.CarrylessMultiply(x1 & mask32, k5, 0x00) ^ t;

        // Barrett reduction.
        t = Pclmulqdq.CarrylessMultiply(x1 & mask32, poly, 0x10) & mask32;
        t = Pclmulqdq.CarrylessMultiply(t, poly, 0x00);
        x1 ^= t;
        return x1.AsUInt32().GetElement(1);
    }

    private static Vector128<ulong> Load(ReadOnlySpan<byte> data, int offset) =>
        Vector128.Create(data.Slice(offset, 16)).AsUInt64();

    private static Vector128<ulong> Fold(Vector128<ulong> acc, Vector128<ulong> k, Vector128<ulong> next) =>
        Pclmulqdq.CarrylessMultiply(acc, k, 0x00) ^ Pclmulqdq.CarrylessMultiply(acc, k, 0x11) ^ next;

    private static uint[] BuildTables()
    {
        var t = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            }

            t[i] = c;
        }

        for (int i = 0; i < 256; i++)
        {
            uint c = t[i];
            for (int s = 1; s < 8; s++)
            {
                c = t[c & 0xFF] ^ (c >> 8);
                t[(s * 256) + i] = c;
            }
        }

        return t;
    }
}
