namespace OneImage.Codecs.Gif;

/// <summary>A buffered forward reader over a seekable stream that knows its absolute position and the stream length.</summary>
internal sealed class GifByteReader : IDisposable
{
    private readonly Stream _stream;
    private byte[]? _buf;
    private int _pos;
    private int _len;
    private long _bufStart;

    public GifByteReader(Stream stream)
    {
        _stream = stream;
        _buf = Pools<byte>.Pool.Rent(16384);
        Length = stream.Length;
        SeekTo(0);
    }

    public long Length { get; }

    public long Position => _bufStart + _pos;

    public bool AtEnd => Position >= Length;

    public void SeekTo(long position)
    {
        if (position >= _bufStart && position <= _bufStart + _len && _len > 0)
        {
            _pos = (int)(position - _bufStart);
            return;
        }

        _stream.Position = position;
        _bufStart = position;
        _pos = 0;
        _len = 0;
    }

    /// <summary>Reads one byte, or returns -1 at the end of the data.</summary>
    public int ReadByte()
    {
        if (_pos >= _len && !Fill())
        {
            return -1;
        }

        return _buf![_pos++];
    }

    /// <summary>Reads up to <paramref name="destination"/>.Length bytes; returns how many were available.</summary>
    public int Read(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            if (_pos >= _len && !Fill())
            {
                break;
            }

            int n = Math.Min(destination.Length - total, _len - _pos);
            _buf.AsSpan(_pos, n).CopyTo(destination.Slice(total));
            _pos += n;
            total += n;
        }

        return total;
    }

    /// <summary>Skips bytes; returns false when the data ends first.</summary>
    public bool Skip(long count)
    {
        long target = Position + count;
        if (target > Length)
        {
            SeekTo(Length);
            return false;
        }

        SeekTo(target);
        return true;
    }

    /// <summary>
    /// Skips a chain of data sub-blocks up to and including the zero-length terminator.
    /// Returns false when the data ends before the terminator.
    /// </summary>
    public bool SkipSubBlocks()
    {
        while (true)
        {
            int len = ReadByte();
            if (len < 0)
            {
                return false;
            }

            if (len == 0)
            {
                return true;
            }

            if (!Skip(len))
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        byte[]? b = _buf;
        _buf = null;
        if (b != null)
        {
            Pools<byte>.Pool.Return(b);
        }
    }

    private bool Fill()
    {
        _bufStart += _len;
        _pos = 0;
        _len = 0;
        int n = _stream.Read(_buf!, 0, _buf!.Length);
        if (n <= 0)
        {
            return false;
        }

        _len = n;
        return true;
    }
}
