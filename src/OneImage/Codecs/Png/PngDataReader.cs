using System.Buffers;
using OneImage.Compression;

namespace OneImage;

/// <summary>
/// Presents the concatenated payload of consecutive IDAT (or fdAT) chunks as one inflated byte stream, checking each
/// chunk's CRC as it is consumed. Memory use is a fixed input buffer plus the inflater window.
/// </summary>
internal sealed class PngDataReader : IDisposable
{
    private readonly Stream _s;
    private readonly uint _type;
    private readonly bool _fdat;
    private readonly long _streamLength;
    private Inflater? _inflater;
    private byte[]? _in;
    private int _inPos;
    private int _inLen;
    private long _remaining;
    private uint _crc;
    private bool _chunkOpen;
    private bool _ended;
    private bool _inputDone;

    public PngDataReader(Stream s, long dataOffset, bool fdat, long maxOutput, double maxRatio)
    {
        _s = s;
        _fdat = fdat;
        _type = fdat ? PngChunk.fdAT : PngChunk.IDAT;
        _streamLength = s.Length;
        s.Position = dataOffset;
        _in = Pools<byte>.Pool.Rent(32768);
        _inflater = new Inflater(true, maxOutput, maxRatio);
    }

    /// <summary>Fills <paramref name="dst"/> with inflated bytes.</summary>
    /// <exception cref="ImageFormatException">The data is corrupt or ends before enough bytes were produced.</exception>
    public void ReadExactly(Span<byte> dst)
    {
        Inflater inflater = _inflater ?? throw new ObjectDisposedException(nameof(PngDataReader));
        byte[] input = _in!;
        int filled = 0;
        while (filled < dst.Length)
        {
            if (_inPos == _inLen && !_inputDone && !Fill())
            {
                _inputDone = true;
                _inPos = _inLen = 0;
            }

            InflateStatus status = inflater.Inflate(input.AsSpan(_inPos, _inLen - _inPos), dst[filled..], out int consumed, out int written);
            _inPos += consumed;
            filled += written;
            if (filled == dst.Length)
            {
                break;
            }

            if (status == InflateStatus.Done || (status == InflateStatus.NeedInput && _inputDone))
            {
                throw new ImageFormatException("The image data ends before the last row.");
            }
        }
    }

    /// <summary>
    /// Reads the rest of the chunk the last byte came from and checks its CRC, so a damaged chunk is noticed even when
    /// every row was complete before its end.
    /// </summary>
    public void VerifyCurrentChunk()
    {
        byte[]? buf = _in;
        if (buf == null)
        {
            return;
        }

        while (_chunkOpen && _remaining > 0)
        {
            int n = (int)Math.Min(_remaining, buf.Length);
            ContainerIo.ReadFully(_s, buf.AsSpan(0, n));
            _crc = Crc32.Update(_crc, buf.AsSpan(0, n));
            _remaining -= n;
        }

        if (_chunkOpen)
        {
            Span<byte> tail = stackalloc byte[4];
            ContainerIo.ReadFully(_s, tail);
            if (ContainerIo.U32Be(tail) != _crc)
            {
                throw new ImageFormatException("CRC mismatch in image data.");
            }

            _chunkOpen = false;
        }
    }

    public void Dispose()
    {
        _inflater?.Dispose();
        _inflater = null;
        ContainerIo.Return(ref _in);
    }

    private bool Fill()
    {
        Span<byte> hdr = stackalloc byte[8];
        while (_remaining == 0)
        {
            if (_chunkOpen)
            {
                ContainerIo.ReadFully(_s, hdr[..4]);
                if (ContainerIo.U32Be(hdr) != _crc)
                {
                    throw new ImageFormatException("CRC mismatch in image data.");
                }

                _chunkOpen = false;
            }

            if (_ended)
            {
                return false;
            }

            if (ContainerIo.ReadUpTo(_s, hdr) < 8 || ContainerIo.U32Be(hdr[4..]) != _type)
            {
                _ended = true;
                return false;
            }

            uint len = ContainerIo.U32Be(hdr);
            if (len > _streamLength - _s.Position)
            {
                throw new ImageFormatException("A chunk runs past the end of the file.");
            }

            _crc = Crc32.Compute(hdr[4..8]);
            _remaining = len;
            _chunkOpen = true;
            if (_fdat)
            {
                if (len < 4)
                {
                    throw new ImageFormatException("fdAT chunk is too short.");
                }

                ContainerIo.ReadFully(_s, hdr[..4]);
                _crc = Crc32.Update(_crc, hdr[..4]);
                _remaining -= 4;
            }
        }

        int n = (int)Math.Min(_remaining, _in!.Length);
        ContainerIo.ReadFully(_s, _in.AsSpan(0, n));
        _crc = Crc32.Update(_crc, _in.AsSpan(0, n));
        _remaining -= n;
        _inPos = 0;
        _inLen = n;
        return true;
    }
}

/// <summary>PNG chunk type codes (four ASCII letters as a big-endian integer).</summary>
internal static class PngChunk
{
    public const uint IHDR = 0x49484452;
    public const uint PLTE = 0x504C5445;
    public const uint IDAT = 0x49444154;
    public const uint IEND = 0x49454E44;
    public const uint tRNS = 0x74524E53;
    public const uint gAMA = 0x67414D41;
    public const uint cHRM = 0x6348524D;
    public const uint sRGB = 0x73524742;
    public const uint iCCP = 0x69434350;
    public const uint cICP = 0x63494350;
    public const uint eXIf = 0x65584966;
    public const uint iTXt = 0x69545874;
    public const uint tEXt = 0x74455874;
    public const uint acTL = 0x6163544C;
    public const uint fcTL = 0x6663544C;
    public const uint fdAT = 0x66644154;

    public static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Critical chunks have an upper-case first letter (bit 5 of the first byte clear).</summary>
    public static bool IsCritical(uint type) => (type & 0x20000000) == 0;

    public static bool IsValidType(uint type)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            uint b = (type >> shift) & 0xFF;
            if (!((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z')))
            {
                return false;
            }
        }

        return true;
    }
}
