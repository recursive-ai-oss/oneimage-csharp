using System.Buffers.Binary;

namespace OneImage.Metafile.Tests;

/// <summary>
/// Writes EMF files that carry EMF+ records, from the record layouts of [MS-EMFPLUS]. EMF+ records go into EMR_COMMENT records after the
/// "EMF+" identifier; <see cref="Gdi"/> adds ordinary EMF records between them.
/// </summary>
internal sealed class EmfPlusBuilder
{
    private readonly EmfBuilder _emf;
    private readonly List<byte> _current = [];

    public EmfPlusBuilder(int size = 100, bool dual = false, uint dpi = 96)
    {
        _emf = new EmfBuilder(size);
        var data = new Data().U32(0xDBC01002).U32(1).U32(dpi).U32(dpi);
        Record(0x4001, (ushort)(dual ? 1 : 0), data);
    }

    /// <summary>Gets the underlying EMF builder, after ending the current comment, for adding GDI records.</summary>
    public EmfBuilder Gdi
    {
        get
        {
            Flush();
            return _emf;
        }
    }

    public EmfPlusBuilder Record(ushort type, ushort flags, Data data) => Record(type, flags, data.ToArray());

    public EmfPlusBuilder Record(ushort type, ushort flags, byte[] data)
    {
        int padded = (data.Length + 3) & ~3;
        var rec = new byte[12 + padded];
        BinaryPrimitives.WriteUInt16LittleEndian(rec, type);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(2), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), (uint)rec.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8), (uint)padded);
        data.CopyTo(rec, 12);
        _current.AddRange(rec);
        return this;
    }

    /// <summary>Ends the current comment so that the next EMF+ records start a new EMR_COMMENT.</summary>
    public EmfPlusBuilder Flush()
    {
        if (_current.Count > 0)
        {
            _emf.Comment("EMF+", [.. _current]);
            _current.Clear();
        }

        return this;
    }

    public byte[] Build()
    {
        Record(0x4002, 0, []);
        Flush();
        return _emf.Build();
    }

    // ---- objects -----------------------------------------------------------------------------------------------------

    public EmfPlusBuilder Object(int type, int id, Data body, bool cont = false) =>
        Record(0x4008, (ushort)((cont ? 0x8000 : 0) | (type << 8) | id), body);

    public static uint Argb(int a, int r, int g, int b) => (uint)((a << 24) | (r << 16) | (g << 8) | b);

    public EmfPlusBuilder SolidBrush(int id, uint argb) => Object(1, id, new Data().U32(0xDBC01002).U32(0).U32(argb));

    public EmfPlusBuilder HatchBrush(int id, int style, uint fore, uint back) => Object(1, id, new Data().U32(0xDBC01002).U32(1).U32((uint)style).U32(fore).U32(back));

    public EmfPlusBuilder LinearGradient(int id, float x, float y, float w, float h, uint c1, uint c2, uint wrap = 0) =>
        Object(1, id, new Data().U32(0xDBC01002).U32(4).U32(0).U32(wrap).F32(x).F32(y).F32(w).F32(h).U32(c1).U32(c2).U32(0).U32(0));

    /// <summary>A pen of a solid color. <paramref name="optional"/> is the flags word and the optional data that follows the width.</summary>
    public EmfPlusBuilder Pen(int id, float width, uint argb, uint flags = 0, Data? optional = null, uint unit = 2)
    {
        var d = new Data().U32(0xDBC01002).U32(0).U32(flags).U32(unit).F32(width);
        if (optional is not null)
        {
            d.Append(optional);
        }

        d.U32(0xDBC01002).U32(0).U32(argb);
        return Object(2, id, d);
    }

    public EmfPlusBuilder Path(int id, (float X, float Y, byte Type)[] pts, bool winding = false)
    {
        var d = new Data().U32(0xDBC01002).U32((uint)pts.Length).U32(winding ? 0x2000u : 0);
        foreach ((float x, float y, _) in pts)
        {
            d.F32(x).F32(y);
        }

        foreach ((_, _, byte t) in pts)
        {
            d.U8(t);
        }

        return Object(3, id, d);
    }

    public EmfPlusBuilder Font(int id, string family, float em, int style = 0, uint unit = 2)
    {
        var d = new Data().U32(0xDBC01002).F32(em).U32(unit).U32((uint)style).U32(0).U32((uint)family.Length);
        d.Bytes(System.Text.Encoding.Unicode.GetBytes(family));
        return Object(6, id, d);
    }

    /// <summary>A bitmap image of 32-bit ARGB pixels (blue, green, red, alpha in memory).</summary>
    public EmfPlusBuilder Bitmap(int id, int w, int h, Func<int, int, uint> pixel)
    {
        var d = new Data().U32(0xDBC01002).U32(1).U32((uint)w).U32((uint)h).U32((uint)(w * 4)).U32(0x0026200A).U32(0);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                d.U32(pixel(x, y));
            }
        }

        return Object(5, id, d);
    }

    // ---- drawing -----------------------------------------------------------------------------------------------------

    /// <summary>Compressed (16-bit) rectangle, as written when every value is a whole number.</summary>
    public static Data Rects16(params (int X, int Y, int W, int H)[] r)
    {
        var d = new Data();
        foreach ((int x, int y, int w, int h) in r)
        {
            d.I16(x).I16(y).I16(w).I16(h);
        }

        return d;
    }

    public EmfPlusBuilder FillRects(uint argb, params (int X, int Y, int W, int H)[] r) =>
        Record(0x400A, 0xC000, new Data().U32(argb).U32((uint)r.Length).Append(Rects16(r)));

    public EmfPlusBuilder FillRectsBrush(int brush, params (int X, int Y, int W, int H)[] r) =>
        Record(0x400A, 0x4000, new Data().U32((uint)brush).U32((uint)r.Length).Append(Rects16(r)));

    public EmfPlusBuilder DrawRects(int pen, params (int X, int Y, int W, int H)[] r) =>
        Record(0x400B, (ushort)(0x4000 | pen), new Data().U32((uint)r.Length).Append(Rects16(r)));

    public EmfPlusBuilder FillEllipse(uint argb, int x, int y, int w, int h) =>
        Record(0x400E, 0xC000, new Data().U32(argb).Append(Rects16((x, y, w, h))));

    public EmfPlusBuilder FillPolygon(uint argb, params (int X, int Y)[] p)
    {
        var d = new Data().U32(argb).U32((uint)p.Length);
        foreach ((int x, int y) in p)
        {
            d.I16(x).I16(y);
        }

        return Record(0x400C, 0xC000, d);
    }

    public EmfPlusBuilder DrawLines(int pen, bool closed, params (int X, int Y)[] p)
    {
        var d = new Data().U32((uint)p.Length);
        foreach ((int x, int y) in p)
        {
            d.I16(x).I16(y);
        }

        return Record(0x400D, (ushort)(0x4000 | (closed ? 0x2000 : 0) | pen), d);
    }

    public EmfPlusBuilder FillPath(uint argb, int path) => Record(0x4014, (ushort)(0x8000 | path), new Data().U32(argb));

    public EmfPlusBuilder Clear(uint argb) => Record(0x4009, 0, new Data().U32(argb));

    public EmfPlusBuilder Save(uint index) => Record(0x4025, 0, new Data().U32(index));

    public EmfPlusBuilder Restore(uint index) => Record(0x4026, 0, new Data().U32(index));

    public EmfPlusBuilder Translate(float x, float y, bool post = false) => Record(0x402D, (ushort)(post ? 0x2000 : 0), new Data().F32(x).F32(y));

    public EmfPlusBuilder Scale(float x, float y) => Record(0x402E, 0, new Data().F32(x).F32(y));

    public EmfPlusBuilder Rotate(float degrees) => Record(0x402F, 0, new Data().F32(degrees));

    public EmfPlusBuilder SetClipRect(int combine, float x, float y, float w, float h) =>
        Record(0x4032, (ushort)(combine << 8), new Data().F32(x).F32(y).F32(w).F32(h));

    public EmfPlusBuilder AntiAlias(bool on) => Record(0x401E, (ushort)(on ? 1 : 0), []);

    /// <summary>The bytes of a record's data, written field by field in little endian.</summary>
    public sealed class Data
    {
        private readonly List<byte> _b = [];

        public Data U8(byte v)
        {
            _b.Add(v);
            return this;
        }

        public Data U32(uint v)
        {
            Span<byte> s = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(s, v);
            _b.AddRange(s.ToArray());
            return this;
        }

        public Data I16(int v)
        {
            Span<byte> s = stackalloc byte[2];
            BinaryPrimitives.WriteInt16LittleEndian(s, (short)v);
            _b.AddRange(s.ToArray());
            return this;
        }

        public Data F32(float v)
        {
            Span<byte> s = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(s, v);
            _b.AddRange(s.ToArray());
            return this;
        }

        public Data Bytes(byte[] v)
        {
            _b.AddRange(v);
            return this;
        }

        public Data Append(Data d)
        {
            _b.AddRange(d._b);
            return this;
        }

        public byte[] ToArray() => [.. _b];

        public static implicit operator byte[](Data d) => d.ToArray();
    }
}
