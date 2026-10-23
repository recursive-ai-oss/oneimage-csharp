using System.Runtime.InteropServices;

namespace OneImage.Kernels;

/// <summary>
/// A span stored as a pointer and a length, so the memory of a caller's span can be handed to pool threads that run while that
/// call is blocked. Only valid inside <see cref="Pinned.Run"/>, which keeps the memory fixed and alive.
/// </summary>
internal readonly unsafe struct RawSpan
{
    private readonly byte* _pointer;

    public RawSpan(byte* pointer, int length)
    {
        _pointer = pointer;
        Length = length;
    }

    public int Length { get; }

    public ReadOnlySpan<byte> AsReadOnly() => new(_pointer, Length);

    public Span<byte> AsSpan() => new(_pointer, Length);
}

/// <summary>Work that runs over a pinned source and destination.</summary>
internal interface IPinnedJob
{
    void Run(RawSpan source, RawSpan destination);
}

/// <summary>The one place that pins a caller's spans for work that spreads over threads. Sizes were validated by the caller.</summary>
internal static unsafe class Pinned
{
    public static void Run<TJob>(ReadOnlySpan<byte> source, Span<byte> destination, TJob job)
        where TJob : class, IPinnedJob
    {
        fixed (byte* s = &MemoryMarshal.GetReference(source))
        fixed (byte* d = &MemoryMarshal.GetReference(destination))
        {
            job.Run(new RawSpan(s, source.Length), new RawSpan(d, destination.Length));
        }
    }
}
