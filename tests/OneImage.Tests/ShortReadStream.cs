namespace OneImage.Tests;

/// <summary>
/// A seekable stream over bytes whose reads return fewer bytes than asked, the way pipes, network and FUSE file systems and
/// interrupted system calls do. Lengths are drawn from a seeded generator, so a failure repeats. With
/// <c>zeroEvery</c> set, an occasional read returns 0 bytes before the true end, which no caller may mistake for the end.
/// </summary>
internal sealed class ShortReadStream : Stream
{
    private readonly byte[] _data;
    private readonly Random _rnd;
    private readonly int _maxChunk;
    private readonly int _zeroEvery;
    private int _position;

    public ShortReadStream(byte[] data, int seed, int maxChunk = 3000, int zeroEvery = 0)
    {
        _data = data;
        _rnd = new Random(seed);
        _maxChunk = maxChunk;
        _zeroEvery = zeroEvery;
    }

    /// <summary>Gets how many reads returned fewer bytes than were asked for although more data remained.</summary>
    public int ShortReads { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _data.Length;

    public override long Position
    {
        get => _position;
        set => _position = (int)Math.Clamp(value, 0, _data.Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int left = _data.Length - _position;
        if (buffer.Length == 0 || left <= 0)
        {
            return 0;
        }

        if (_zeroEvery > 0 && _rnd.Next(_zeroEvery) == 0)
        {
            ShortReads++;
            return 0;
        }

        int n = Math.Min(Math.Min(buffer.Length, left), 1 + _rnd.Next(_maxChunk));
        if (n < Math.Min(buffer.Length, left))
        {
            ShortReads++;
        }

        _data.AsSpan(_position, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _data.Length + offset,
        };
        Position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
