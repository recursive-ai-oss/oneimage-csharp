using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace OneImage.Codecs.Jpeg;

/// <summary>
/// Buffered access to the JPEG byte stream: marker-level reads, and (in the other partial files) the bit-level
/// entropy readers. Everything here is bounds-checked; running out of data is an <see cref="ImageFormatException"/>.
/// </summary>
internal sealed partial class JpegReader : IDisposable
{
    private const int BufferSize = 1 << 16;

    private readonly Stream _stream;
    private byte[] _buf;
    private int _pos;
    private int _end;
    private bool _eof;
    private long _totalRead;
    private bool _ownsBuffer = true;

    public JpegReader(Stream stream)
    {
        _stream = stream;
        _buf = ArrayPool<byte>.Shared.Rent(BufferSize);
    }

    private JpegReader()
    {
        _stream = Stream.Null;
        _buf = [];
        _eof = true;
        _ownsBuffer = false;
    }

    /// <summary>Creates a reader with no stream, to be pointed at in-memory entropy-coded data with <see cref="Attach"/>.</summary>
    public static JpegReader CreateDetached() => new();

    /// <summary>
    /// Makes the reader serve <paramref name="length"/> bytes of <paramref name="data"/> from <paramref name="start"/> as if they were the
    /// whole rest of the file: past them it behaves exactly as at the end of a file (zero padding, no further reads).
    /// </summary>
    public void Attach(byte[] data, int start, int length)
    {
        _buf = data;
        _pos = start;
        _end = start + length;
        _eof = true;
        _marker = 0;
        ResetBits();
    }

    /// <summary>Gets the number of bytes pulled from the stream so far.</summary>
    public long BytesRead => _totalRead;

    public void Dispose()
    {
        byte[] b = _buf;
        _buf = [];
        if (b.Length > 0 && _ownsBuffer)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }

    /// <summary>Reads more bytes after the unread ones; returns false when the stream is exhausted.</summary>
    private bool Refill()
    {
        if (_eof)
        {
            return false;
        }

        if (_pos > 0)
        {
            Buffer.BlockCopy(_buf, _pos, _buf, 0, _end - _pos);
            _end -= _pos;
            _pos = 0;
        }

        while (_end < _buf.Length)
        {
            int n = _stream.Read(_buf, _end, _buf.Length - _end);
            if (n <= 0)
            {
                _eof = true;
                break;
            }

            _end += n;
            _totalRead += n;
            if (_end >= 16)
            {
                break;
            }
        }

        return _end > _pos;
    }

    private static ImageFormatException Truncated() => new("Unexpected end of JPEG data.");

    public int ReadByte()
    {
        if (_pos >= _end && !Refill())
        {
            throw Truncated();
        }

        return _buf[_pos++];
    }

    /// <summary>Reads a byte, or returns -1 at the end of the data.</summary>
    public int TryReadByte()
    {
        if (_pos >= _end && !Refill())
        {
            return -1;
        }

        return _buf[_pos++];
    }

    public int ReadUInt16() => (ReadByte() << 8) | ReadByte();

    public void ReadExactly(Span<byte> dst)
    {
        while (dst.Length > 0)
        {
            if (_pos >= _end && !Refill())
            {
                throw Truncated();
            }

            int n = Math.Min(dst.Length, _end - _pos);
            _buf.AsSpan(_pos, n).CopyTo(dst);
            _pos += n;
            dst = dst[n..];
        }
    }

    public void Skip(int count)
    {
        while (count > 0)
        {
            if (_pos >= _end && !Refill())
            {
                throw Truncated();
            }

            int n = Math.Min(count, _end - _pos);
            _pos += n;
            count -= n;
        }
    }

    /// <summary>
    /// Finds the next marker: skips any bytes up to a 0xFF followed by a code that is neither 0 nor 0xFF.
    /// A marker already hit by the entropy reader is returned first.
    /// </summary>
    /// <returns>The marker code (the byte after 0xFF), or -1 at the end of the data.</returns>
    public int NextMarker()
    {
        if (_marker > 0)
        {
            int m = _marker;
            _marker = 0;
            return m;
        }

        if (_marker < 0)
        {
            return -1;
        }

        while (true)
        {
            int b = TryReadByte();
            if (b < 0)
            {
                return -1;
            }

            if (b != 0xFF)
            {
                continue;
            }

            int c;
            do
            {
                c = TryReadByte();
            }
            while (c == 0xFF);
            if (c < 0)
            {
                return -1;
            }

            if (c != 0)
            {
                return c;
            }
        }
    }

    /// <summary>Skips entropy-coded data up to (not including) the next marker, without decoding.</summary>
    public void SkipEntropyData()
    {
        ResetBits();
        if (_marker != 0)
        {
            return;
        }

        while (true)
        {
            if (_pos >= _end && !Refill())
            {
                _marker = -1;
                return;
            }

            int i = _buf.AsSpan(_pos, _end - _pos).IndexOf((byte)0xFF);
            if (i < 0)
            {
                _pos = _end;
                continue;
            }

            _pos += i;
            if (_end - _pos < 2 && !Refill())
            {
                _marker = -1;
                return;
            }

            if (_end - _pos < 2)
            {
                continue;
            }

            byte next = _buf[_pos + 1];
            if (next == 0 || next == 0xFF)
            {
                _pos += next == 0 ? 2 : 1;
                continue;
            }

            if (next is >= 0xD0 and <= 0xD7)
            {
                _pos += 2;
                continue;
            }

            // Leave the marker unconsumed for NextMarker.
            return;
        }
    }

    /// <summary>
    /// Copies the raw entropy-coded bytes of the next restart intervals, byte stuffing untouched, up to and including the
    /// marker that ends the <paramref name="intervals"/>-th one. Every marker ends exactly one interval, which is also how the
    /// sequential decoder walks the data, so a worker decoding the copy sees what the sequential decoder would see.
    /// Copying stops after a marker that is not a restart marker, and at the end of the data.
    /// </summary>
    /// <param name="intervals">How many markers to copy through.</param>
    /// <param name="dst">The destination; replaced by a larger pooled array when it is too small.</param>
    /// <param name="len">The number of bytes in <paramref name="dst"/>; advanced.</param>
    /// <param name="ended">Set when the data ended or a non-restart marker was copied, so no more intervals follow.</param>
    /// <returns>The number of markers copied.</returns>
    public int CopyEntropyIntervals(int intervals, ref byte[] dst, ref int len, out bool ended)
    {
        ended = false;
        int done = 0;
        Span<byte> one = stackalloc byte[1];
        while (done < intervals)
        {
            if (_pos >= _end && !Refill())
            {
                ended = true;
                return done;
            }

            ReadOnlySpan<byte> avail = _buf.AsSpan(_pos, _end - _pos);
            int i = avail.IndexOf((byte)0xFF);
            if (i < 0)
            {
                Append(ref dst, ref len, avail);
                _pos = _end;
                continue;
            }

            Append(ref dst, ref len, avail[..i]);
            _pos += i;

            // A run of 0xFF fill bytes followed by 0 is a stuffed data byte; followed by anything else it is a marker.
            int c;
            one[0] = 0xFF;
            Append(ref dst, ref len, one);
            _pos++;
            do
            {
                c = TryReadByte();
                if (c < 0)
                {
                    ended = true;
                    return done;
                }

                one[0] = (byte)c;
                Append(ref dst, ref len, one);
            }
            while (c == 0xFF);

            if (c == 0)
            {
                continue;
            }

            done++;
            if (c is < 0xD0 or > 0xD7)
            {
                ended = true;
                return done;
            }
        }

        return done;
    }

    private static void Append(ref byte[] dst, ref int len, ReadOnlySpan<byte> src)
    {
        if (src.Length > dst.Length - len)
        {
            long need = (long)len + src.Length;
            if (need > Array.MaxLength)
            {
                throw new ImageLimitException("JPEG restart interval data is too large.");
            }

            byte[] bigger = ArrayPool<byte>.Shared.Rent((int)Math.Max(need, Math.Min((long)dst.Length * 2, Array.MaxLength)));
            dst.AsSpan(0, len).CopyTo(bigger);
            if (dst.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(dst);
            }

            dst = bigger;
        }

        src.CopyTo(dst.AsSpan(len));
        len += src.Length;
    }

    /// <summary>Reads a segment body: the 16-bit length (which counts itself) followed by that many bytes minus two.</summary>
    /// <returns>The length of the body in bytes.</returns>
    public int ReadSegmentLength()
    {
        int len = ReadUInt16();
        if (len < 2)
        {
            throw new ImageFormatException("Invalid JPEG segment length.");
        }

        return len - 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasFF(ulong v)
    {
        ulong x = ~v;
        return ((x - 0x0101010101010101UL) & ~x & 0x8080808080808080UL) != 0;
    }
}
