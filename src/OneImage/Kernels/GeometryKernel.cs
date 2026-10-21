using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OneImage.Kernels;

[InlineArray(3)]
internal struct Bytes3
{
    private byte _e;
}

[InlineArray(6)]
internal struct Bytes6
{
    private byte _e;
}

[InlineArray(12)]
internal struct Bytes12
{
    private byte _e;
}

/// <summary>
/// Pixel moves for the exact geometry ops. Every function checks that the rows it reads and writes lie inside the spans it was
/// given before the unsafe loop runs, so the loops themselves need no per-pixel checks.
/// </summary>
internal static class GeometryKernel
{
    /// <summary>
    /// Writes one output row of <paramref name="count"/> pixels. The first source pixel is at byte offset <paramref name="first"/>
    /// of <paramref name="src"/> and each next one is <paramref name="step"/> bytes further (negative to run backwards, the row
    /// stride to run down a column).
    /// </summary>
    public static void GatherRow(ReadOnlySpan<byte> src, long first, long step, Span<byte> dst, int count, int bytesPerPixel)
    {
        if (count <= 0)
        {
            return;
        }

        long last = first + ((count - 1) * step);
        if (Math.Min(first, last) < 0 || Math.Max(first, last) + bytesPerPixel > src.Length || dst.Length < (long)count * bytesPerPixel)
        {
            throw new ArgumentException("Gather reads or writes outside its buffers.");
        }

        ref byte s = ref Unsafe.Add(ref MemoryMarshal.GetReference(src), (nint)first);
        ref byte d = ref MemoryMarshal.GetReference(dst);
        if (step == bytesPerPixel)
        {
            Unsafe.CopyBlockUnaligned(ref d, ref s, (uint)(count * bytesPerPixel));
            return;
        }

        switch (bytesPerPixel)
        {
            case 1: Gather<byte>(ref s, step, ref d, count); break;
            case 2: Gather<ushort>(ref s, step, ref d, count); break;
            case 3: Gather<Bytes3>(ref s, step, ref d, count); break;
            case 4: Gather<uint>(ref s, step, ref d, count); break;
            case 6: Gather<Bytes6>(ref s, step, ref d, count); break;
            case 8: Gather<ulong>(ref s, step, ref d, count); break;
            case 12: Gather<Bytes12>(ref s, step, ref d, count); break;
            case 16: Gather<Int128>(ref s, step, ref d, count); break;
            default:
                for (int i = 0; i < count; i++)
                {
                    for (int b = 0; b < bytesPerPixel; b++)
                    {
                        Unsafe.Add(ref d, (i * bytesPerPixel) + b) = Unsafe.Add(ref s, (nint)((i * step) + b));
                    }
                }

                break;
        }
    }

    private static void Gather<T>(ref byte s, long step, ref byte d, int count)
        where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        for (int i = 0; i < count; i++)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, i * size), Unsafe.ReadUnaligned<T>(ref Unsafe.Add(ref s, (nint)(i * step))));
        }
    }

    /// <summary>Fills <paramref name="count"/> pixels with the pixel in <paramref name="pixel"/>.</summary>
    public static void FillRow(Span<byte> dst, int count, ReadOnlySpan<byte> pixel)
    {
        int bpp = pixel.Length;
        if (dst.Length < (long)count * bpp)
        {
            throw new ArgumentException("Destination too short.");
        }

        for (int i = 0; i < count; i++)
        {
            pixel.CopyTo(dst.Slice(i * bpp, bpp));
        }
    }
}
