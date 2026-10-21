using System.Runtime.InteropServices;

namespace OneImage.Kernels;

/// <summary>
/// Lets several threads write disjoint parts of one caller-owned span. A span cannot be captured by a lambda, so the span is
/// pinned for the duration of <see cref="Run"/> and workers get bounds-checked slices of it through <see cref="Slice"/>.
/// Callers guarantee that concurrent slices do not overlap (tile copy-out writes disjoint rectangles).
/// </summary>
internal sealed unsafe class PinnedDestination
{
    private byte* _base;
    private int _length;

    private PinnedDestination()
    {
    }

    public int Length => _length;

    public static void Run<TState>(Span<byte> destination, TState state, Action<PinnedDestination, TState> body)
    {
        fixed (byte* p = &MemoryMarshal.GetReference(destination))
        {
            body(new PinnedDestination { _base = p, _length = destination.Length }, state);
        }
    }

    /// <summary>Returns <c>length</c> bytes starting at <c>offset</c>; throws if they are not inside the pinned span.</summary>
    public Span<byte> Slice(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > _length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return new Span<byte>(_base + offset, length);
    }
}
