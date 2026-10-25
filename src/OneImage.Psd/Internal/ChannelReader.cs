using System.Buffers;
using System.Buffers.Binary;
using OneImage.Compression;

namespace OneImage.Psd;

/// <summary>
/// Delivers the scan lines of one channel as big-endian samples, whatever the compression. Raw and run-length channels are read one
/// line at a time from the file; ZIP channels are inflated whole (bounded by <see cref="PsdLimits.MaxChannelBytes"/>).
/// </summary>
internal sealed class ChannelReader
{
    private readonly FileIo _io;
    private readonly int _height;
    private readonly long _dataPos;
    private readonly long[]? _rowStart;
    private readonly uint[]? _rowLength;
    private readonly byte[]? _decoded;
    private readonly long _decodedOffset;
    private byte[]? _scratch;

    private ChannelReader(FileIo io, int rowBytes, int height, long dataPos, long[]? rowStart, uint[]? rowLength, byte[]? decoded, long decodedOffset)
    {
        _io = io;
        RowBytes = rowBytes;
        _height = height;
        _dataPos = dataPos;
        _rowStart = rowStart;
        _rowLength = rowLength;
        _decoded = decoded;
        _decodedOffset = decodedOffset;
    }

    public int RowBytes { get; }

    public static int RowBytesFor(int width, int depth) => depth == 1 ? (width + 7) / 8 : checked(width * (depth / 8));

    /// <summary>A channel of a layer record, with its own 2-byte compression code.</summary>
    public static ChannelReader ForLayerChannel(FileIo io, long position, long length, int width, int height, int depth, bool psb, PsdLimits limits, ImageLimits imageLimits)
    {
        if (length < 2)
        {
            throw new ImageFormatException("A channel is shorter than its compression code.");
        }

        Span<byte> code = stackalloc byte[2];
        io.ReadAt(position, code);
        int compression = BinaryPrimitives.ReadUInt16BigEndian(code);
        var shared = new SharedData(io, position + 2, length - 2, compression, 1, width, height, depth, psb, limits, imageLimits);
        return shared.Channel(0);
    }

    /// <summary>Everything that all channels of a section share: the compression code and, for run-length data, one table of line lengths.</summary>
    public sealed class SharedData
    {
        private readonly FileIo _io;
        private readonly int _channels;
        private readonly int _height;
        private readonly int _rowBytes;
        private readonly long _pos;
        private readonly long[]? _rowStart;
        private readonly uint[]? _rowLength;
        private readonly byte[]? _decoded;

        public SharedData(FileIo io, long position, long length, int compression, int channels, int width, int height, int depth, bool psb, PsdLimits limits, ImageLimits imageLimits)
        {
            _io = io;
            _channels = channels;
            _height = height;
            _pos = position;
            _rowBytes = RowBytesFor(width, depth);
            long planeBytes = checked((long)_rowBytes * height);
            switch (compression)
            {
                case 0:
                    if (planeBytes * channels > length)
                    {
                        throw new ImageFormatException($"Raw channel data is shorter than the image needs ({length} bytes for {channels} channel(s) of {width}x{height} at {depth} bits).");
                    }

                    break;
                case 1:
                    {
                        int cs = psb ? 4 : 2;
                        long tableBytes = checked((long)channels * height * cs);
                        if (tableBytes > length)
                        {
                            throw new ImageFormatException("The run-length line table extends past the channel data.");
                        }

                        // The table is bounded by the data that is really in the file, so a header cannot make this allocation large.
                        byte[] table = io.ReadAt(position, tableBytes, tableBytes);
                        int lines = checked(channels * height);
                        _rowStart = new long[lines];
                        _rowLength = new uint[lines];
                        long at = position + tableBytes;
                        long end = position + length;
                        for (int i = 0; i < lines; i++)
                        {
                            uint n = psb ? BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(i * 4)) : BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(i * 2));
                            if (n > end - at || n > 2L * _rowBytes + 16)
                            {
                                throw new ImageFormatException("A run-length line extends past the channel data.");
                            }

                            _rowStart[i] = at;
                            _rowLength[i] = n;
                            at += n;
                        }

                        break;
                    }

                case 2:
                case 3:
                    {
                        long total = checked(planeBytes * channels);
                        if (total > limits.MaxChannelBytes || total > Array.MaxLength)
                        {
                            throw new ImageLimitException($"A ZIP-compressed channel would take {total} bytes decoded; the limit is {limits.MaxChannelBytes}.");
                        }

                        byte[] packed = io.ReadAt(position, length, limits.MaxChannelBytes);
                        _decoded = Zip(packed, total, compression == 3, width, height * channels, depth, imageLimits);
                        break;
                    }

                default:
                    throw new ImageFormatException($"Unknown channel compression code {compression}.");
            }
        }

        public ChannelReader Channel(int index)
        {
            if ((uint)index >= (uint)_channels)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            long plane = (long)_rowBytes * _height;
            if (_rowStart != null)
            {
                return new ChannelReader(_io, _rowBytes, _height, 0, _rowStart, _rowLength, null, (long)index * _height);
            }

            if (_decoded != null)
            {
                return new ChannelReader(_io, _rowBytes, _height, 0, null, null, _decoded, index * plane);
            }

            return new ChannelReader(_io, _rowBytes, _height, _pos + index * plane, null, null, null, 0);
        }
    }

    public void ReadRow(int y, Span<byte> destination)
    {
        if ((uint)y >= (uint)_height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        destination = destination.Slice(0, RowBytes);
        if (_decoded != null)
        {
            _decoded.AsSpan(checked((int)(_decodedOffset + (long)y * RowBytes)), RowBytes).CopyTo(destination);
        }
        else if (_rowStart != null)
        {
            int line = checked((int)_decodedOffset + y);
            int n = (int)_rowLength![line];
            _scratch ??= new byte[Math.Max(n, RowBytes + RowBytes / 128 + 2)];
            if (_scratch.Length < n)
            {
                _scratch = new byte[n];
            }

            _io.ReadAt(_rowStart[line], _scratch.AsSpan(0, n));
            Rle.Unpack(_scratch.AsSpan(0, n), destination);
        }
        else
        {
            _io.ReadAt(_dataPos + (long)y * RowBytes, destination);
        }
    }

    private static byte[] Zip(byte[] packed, long total, bool predictor, int width, int lines, int depth, ImageLimits imageLimits)
    {
        byte[] output = new byte[total];
        using var inflater = new Inflater(true, total, imageLimits.MaxExpansionRatio);
        InflateStatus status = inflater.Inflate(packed, output, out int used, out int written);
        if (status == InflateStatus.NeedInput || written < total)
        {
            throw new ImageFormatException("ZIP-compressed channel data ends early.");
        }

        // The channel is complete, but the zlib trailer may not have been read yet: run the stream to its end so its Adler-32 is checked.
        if (status != InflateStatus.Done)
        {
            try
            {
                if (inflater.Drain(packed.AsSpan(used), out _) != InflateStatus.Done)
                {
                    throw new ImageFormatException("ZIP-compressed channel data ends before its zlib trailer.");
                }
            }
            catch (ImageLimitException)
            {
                // More data than the channel holds: the channel is complete and the excess was always ignored; it stays unchecked.
            }
        }

        if (predictor)
        {
            int rowBytes = RowBytesFor(width, depth);
            byte[]? tmp = depth == 32 ? Pools<byte>.Pool.Rent(rowBytes) : null;
            try
            {
                for (int y = 0; y < lines; y++)
                {
                    Span<byte> row = output.AsSpan(checked((int)((long)y * rowBytes)), rowBytes);
                    Unpredict(row, width, depth, tmp);
                }
            }
            finally
            {
                if (tmp != null)
                {
                    Pools<byte>.Pool.Return(tmp);
                }
            }
        }

        return output;
    }

    // The ZIP predictor stores each line as differences from the sample to its left. 16-bit samples subtract as 16-bit words; 32-bit
    // samples are stored as four byte planes of the line (all first bytes, then all second bytes ...) whose bytes subtract in order.
    private static void Unpredict(Span<byte> row, int width, int depth, byte[]? tmp)
    {
        switch (depth)
        {
            case 8:
                for (int i = 1; i < row.Length; i++)
                {
                    row[i] = (byte)(row[i] + row[i - 1]);
                }

                break;
            case 16:
                for (int i = 2; i + 1 < row.Length; i += 2)
                {
                    ushort v = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(row.Slice(i)) + BinaryPrimitives.ReadUInt16BigEndian(row.Slice(i - 2)));
                    BinaryPrimitives.WriteUInt16BigEndian(row.Slice(i), v);
                }

                break;
            case 32:
                for (int i = 1; i < row.Length; i++)
                {
                    row[i] = (byte)(row[i] + row[i - 1]);
                }

                row.CopyTo(tmp);
                for (int x = 0; x < width; x++)
                {
                    row[4 * x] = tmp![x];
                    row[4 * x + 1] = tmp[width + x];
                    row[4 * x + 2] = tmp[2 * width + x];
                    row[4 * x + 3] = tmp[3 * width + x];
                }

                break;
            default:
                throw new ImageFormatException($"ZIP prediction is not defined for {depth}-bit samples.");
        }
    }
}
