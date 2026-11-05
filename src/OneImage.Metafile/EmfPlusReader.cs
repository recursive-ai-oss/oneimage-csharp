using System.Buffers.Binary;

namespace OneImage.Metafile;

/// <summary>Raised inside the EMF+ reader when a record or object is shorter than its fields need or holds an impossible value. The interpreter turns it into a diagnostic for that record; it never leaves the library.</summary>
internal sealed class PlusDataException : Exception
{
    public PlusDataException(string message)
        : base(message)
    {
    }
}

/// <summary>A bounds-checked little-endian reader over the data of one EMF+ record or object.</summary>
internal struct PlusReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _pos;

    public PlusReader(ReadOnlyMemory<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public readonly int Position => _pos;

    public readonly int Remaining => _data.Length - _pos;

    public readonly ReadOnlyMemory<byte> Data => _data;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || n > _data.Length - _pos)
        {
            throw new PlusDataException("the data ends before its fields do");
        }

        ReadOnlySpan<byte> s = _data.Span.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public void Skip(int n) => Take(n);

    public byte U8() => Take(1)[0];

    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public float F32()
    {
        float f = BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        // A NaN or infinity would poison every later computation; treat it as a malformed record.
        return float.IsFinite(f) ? f : throw new PlusDataException("a coordinate is not a finite number");
    }

    public ReadOnlyMemory<byte> Bytes(int n)
    {
        if (n < 0 || n > _data.Length - _pos)
        {
            throw new PlusDataException("the data ends before its fields do");
        }

        ReadOnlyMemory<byte> m = _data.Slice(_pos, n);
        _pos += n;
        return m;
    }

    /// <summary>Checks that <paramref name="count"/> elements of <paramref name="elementSize"/> bytes fit in what is left, before anything is allocated for them.</summary>
    public readonly void Need(long count, int elementSize)
    {
        if (count < 0 || count * elementSize > _data.Length - _pos)
        {
            throw new PlusDataException("a count is larger than the data that follows");
        }
    }

    public void Align4()
    {
        int pad = (4 - (_pos & 3)) & 3;
        _pos = Math.Min(_data.Length, _pos + pad);
    }
}
