using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OneImage.Kernels;

/// <summary>
/// The only place that reinterprets memory. Callers validate sizes before calling; these helpers never parse input.
/// </summary>
internal static class SpanCast
{
    public static Span<T> As<T>(Span<byte> bytes)
        where T : struct => MemoryMarshal.Cast<byte, T>(bytes);

    public static ReadOnlySpan<T> As<T>(ReadOnlySpan<byte> bytes)
        where T : struct => MemoryMarshal.Cast<byte, T>(bytes);

    public static Span<byte> AsBytes<T>(Span<T> items)
        where T : struct => MemoryMarshal.AsBytes(items);

    public static ReadOnlySpan<byte> AsBytes<T>(ReadOnlySpan<T> items)
        where T : struct => MemoryMarshal.AsBytes(items);

    public static int SizeOf<T>()
        where T : struct => Unsafe.SizeOf<T>();
}
