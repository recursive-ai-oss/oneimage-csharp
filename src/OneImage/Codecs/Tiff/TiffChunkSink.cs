using System.Buffers;

namespace OneImage.Codecs.Tiff;

/// <summary>
/// Collects compressed strips in pooled chunks, so a frame's compressed bytes are never copied while growing and the memory
/// is reused by the next frame or call.
/// </summary>
internal sealed class TiffChunkSink : IBufferWriter<byte>, IDisposable
{
    private const int ChunkSize = 64 * 1024;
    private readonly List<(byte[] Buffer, int Used)> _full = [];
    private byte[]? _current;
    private int _used;

    public long WrittenCount { get; private set; }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_current is null || count > _current.Length - _used)
        {
            throw new InvalidOperationException("Advanced past the span that was handed out.");
        }

        _used += count;
        WrittenCount += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _current.AsMemory(_used);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _current.AsSpan(_used);
    }

    private void Ensure(int sizeHint)
    {
        int need = Math.Max(sizeHint, 1);
        if (_current is not null && _current.Length - _used >= need)
        {
            return;
        }

        if (_current is not null)
        {
            _full.Add((_current, _used));
        }

        _current = Pools<byte>.Pool.Rent(Math.Max(ChunkSize, need));
        _used = 0;
    }

    public void WriteTo(Stream destination)
    {
        foreach ((byte[] buffer, int used) in _full)
        {
            destination.Write(buffer, 0, used);
        }

        if (_current is not null)
        {
            destination.Write(_current, 0, _used);
        }
    }

    public void Dispose()
    {
        foreach ((byte[] buffer, _) in _full)
        {
            Pools<byte>.Pool.Return(buffer);
        }

        _full.Clear();
        if (_current is not null)
        {
            Pools<byte>.Pool.Return(_current);
            _current = null;
        }

        WrittenCount = 0;
    }
}
