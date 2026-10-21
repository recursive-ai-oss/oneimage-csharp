using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace OneImage;

/// <summary>Where compressed bytes come from. Opened again whenever pixels are pulled.</summary>
internal abstract class ImageSource
{
    /// <summary>Opens a seekable stream positioned at the first byte of the image. Throws <see cref="ImageSourceChangedException"/> if the source changed since load.</summary>
    public abstract Stream Open();
}

/// <summary>
/// A file opened once per image. Every <see cref="Open"/> checks size and modification time against the values recorded at
/// load (a cheap stat, no open) and then returns a private, buffered view over one shared read-only handle, so parallel tile
/// decodes do not reopen the file. The handle is closed by its finalizer when the image is unreachable; it is opened
/// with delete and write sharing so it never blocks other programs from replacing the file.
/// </summary>
internal sealed class FileImageSource : ImageSource
{
    private static long s_opens;
    private readonly string _path;
    private readonly long _length;
    private readonly DateTime _lastWriteUtc;
    private readonly object _gate = new();
    private SafeFileHandle? _handle;

    public FileImageSource(string path)
    {
        _path = Path.GetFullPath(path);
        var info = new FileInfo(_path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Image file not found.", _path);
        }

        _length = info.Length;
        _lastWriteUtc = info.LastWriteTimeUtc;
    }

    /// <summary>Gets the number of file handles opened by all file sources so far (test statistic).</summary>
    public static long OpenCount => Interlocked.Read(ref s_opens);

    public override Stream Open()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length != _length || info.LastWriteTimeUtc != _lastWriteUtc)
        {
            throw new ImageSourceChangedException($"'{_path}' changed after it was loaded.");
        }

        return new HandleStream(Volatile.Read(ref _handle) ?? OpenHandle(), _length);
    }

    private SafeFileHandle OpenHandle()
    {
        lock (_gate)
        {
            if (_handle != null)
            {
                return _handle;
            }

            SafeFileHandle h = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
            Interlocked.Increment(ref s_opens);
            if (RandomAccess.GetLength(h) != _length)
            {
                h.Dispose();
                throw new ImageSourceChangedException($"'{_path}' changed after it was loaded.");
            }

            Volatile.Write(ref _handle, h);
            return h;
        }
    }
}

/// <summary>
/// A read-only, seekable, independently positioned view of a shared file handle (positional reads, so any number of views
/// can read concurrently). Reads are buffered in small pooled blocks because decoders issue many tiny reads. Disposing a
/// view never closes the handle.
/// </summary>
internal sealed class HandleStream : Stream
{
    private const int BlockSize = 4096;
    private readonly SafeFileHandle _handle;
    private readonly long _length;
    private long _position;
    private byte[]? _block;
    private long _blockStart;
    private int _blockLength;

    public HandleStream(SafeFileHandle handle, long length)
    {
        _handle = handle;
        _length = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - _position;
        if (buffer.Length == 0 || remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > remaining)
        {
            buffer = buffer[..(int)remaining];
        }

        int n;
        if (_blockLength > 0 && _position >= _blockStart && _position < _blockStart + _blockLength)
        {
            n = (int)Math.Min(buffer.Length, _blockStart + _blockLength - _position);
            _block.AsSpan((int)(_position - _blockStart), n).CopyTo(buffer);
        }
        else if (buffer.Length >= BlockSize)
        {
            n = RandomAccess.Read(_handle, buffer, _position);
        }
        else
        {
            _block ??= ArrayPool<byte>.Shared.Rent(BlockSize);
            _blockLength = 0;
            int got = RandomAccess.Read(_handle, _block.AsSpan(0, BlockSize), _position);
            _blockStart = _position;
            _blockLength = got;
            n = Math.Min(buffer.Length, got);
            _block.AsSpan(0, n).CopyTo(buffer);
        }

        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        if (target < 0)
        {
            throw new IOException("Seek before the start of the stream.");
        }

        _position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        byte[]? b = _block;
        _block = null;
        _blockLength = 0;
        if (b != null)
        {
            ArrayPool<byte>.Shared.Return(b);
        }

        base.Dispose(disposing);
    }
}

internal sealed class MemoryImageSource : ImageSource
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly object? _keepAlive;

    public MemoryImageSource(ReadOnlyMemory<byte> data, object? keepAlive = null)
    {
        _data = data;
        _keepAlive = keepAlive;
    }

    public override Stream Open()
    {
        GC.KeepAlive(_keepAlive);
        return new ReadOnlyMemoryStream(_data);
    }
}

/// <summary>
/// A compressed copy of a caller's stream in pooled memory. <see cref="Image"/> is not disposable, so a finalizer
/// returns the buffer once nothing references the image any more.
/// </summary>
internal sealed class PooledBytes
{
    private byte[]? _buffer;

    private PooledBytes(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    public int Length { get; }

    public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, Length);

    public static PooledBytes CopyFrom(Stream stream)
    {
        long hint = stream.CanSeek ? Math.Max(0, stream.Length - stream.Position) : 0;
        int cap = (int)Math.Clamp(hint + 1, 4096, int.MaxValue / 2);
        byte[] buf = ArrayPool<byte>.Shared.Rent(cap);
        int len = 0;
        try
        {
            while (true)
            {
                if (len == buf.Length)
                {
                    byte[] bigger = ArrayPool<byte>.Shared.Rent(checked(buf.Length * 2));
                    buf.AsSpan(0, len).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(buf);
                    buf = bigger;
                }

                int n = stream.Read(buf, len, buf.Length - len);
                if (n == 0)
                {
                    break;
                }

                len += n;
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buf);
            throw;
        }

        return new PooledBytes(buf, len);
    }

    public static async Task<PooledBytes> CopyFromAsync(Stream stream, CancellationToken cancellationToken)
    {
        long hint = stream.CanSeek ? Math.Max(0, stream.Length - stream.Position) : 0;
        int cap = (int)Math.Clamp(hint + 1, 4096, int.MaxValue / 2);
        byte[] buf = ArrayPool<byte>.Shared.Rent(cap);
        int len = 0;
        try
        {
            while (true)
            {
                if (len == buf.Length)
                {
                    byte[] bigger = ArrayPool<byte>.Shared.Rent(checked(buf.Length * 2));
                    buf.AsSpan(0, len).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(buf);
                    buf = bigger;
                }

                int n = await stream.ReadAsync(buf.AsMemory(len), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                len += n;
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buf);
            throw;
        }

        return new PooledBytes(buf, len);
    }

    ~PooledBytes()
    {
        byte[]? b = Interlocked.Exchange(ref _buffer, null);
        if (b != null)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }
}

/// <summary>Read-only seekable stream over memory (safe code only).</summary>
internal sealed class ReadOnlyMemoryStream : Stream
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _position;

    public ReadOnlyMemoryStream(ReadOnlyMemory<byte> data) => _data = data;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _data.Length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = (int)Math.Min(value, _data.Length);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = Math.Min(buffer.Length, _data.Length - _position);
        _data.Span.Slice(_position, n).CopyTo(buffer);
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
        if (target < 0)
        {
            throw new IOException("Seek before the start of the stream.");
        }

        _position = (int)Math.Min(target, _data.Length);
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A write-only stream that appends to an <see cref="IBufferWriter{T}"/>.</summary>
internal sealed class BufferWriterStream : Stream
{
    private readonly IBufferWriter<byte> _writer;

    public BufferWriterStream(IBufferWriter<byte> writer) => _writer = writer;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Span<byte> dst = _writer.GetSpan(buffer.Length);
        buffer.CopyTo(dst);
        _writer.Advance(buffer.Length);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
