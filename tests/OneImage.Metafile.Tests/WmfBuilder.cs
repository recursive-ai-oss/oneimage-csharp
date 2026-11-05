using System.Buffers.Binary;
using System.Text;

namespace OneImage.Metafile.Tests;

/// <summary>Writes Windows metafiles straight from the record layouts of the specification, so the reader is tested against the format and not against itself.</summary>
internal sealed class WmfBuilder
{
    private readonly List<byte[]> _records = [];
    private (int L, int T, int R, int B, int Inch)? _placeable;

    public static int Rgb(int r, int g, int b) => r | (g << 8) | (b << 16);

    public WmfBuilder Placeable(int l, int t, int r, int b, int inch = 96)
    {
        _placeable = (l, t, r, b, inch);
        return this;
    }

    public int Count => _records.Count;

    public WmfBuilder DropLast()
    {
        _records.RemoveAt(_records.Count - 1);
        return this;
    }

    /// <summary>A record whose parameters are 16-bit words.</summary>
    public WmfBuilder Rec(int function, params int[] words)
    {
        var body = new byte[words.Length * 2];
        for (int i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(i * 2), unchecked((short)words[i]));
        }

        return Raw(function, body);
    }

    public WmfBuilder Raw(int function, byte[] body)
    {
        int size = 6 + body.Length;
        size = (size + 1) & ~1;
        var r = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(r, (uint)(size / 2));
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), (ushort)function);
        body.CopyTo(r, 6);
        _records.Add(r);
        return this;
    }

    private static byte[] Words(params object[] parts)
    {
        var ms = new MemoryStream();
        foreach (object p in parts)
        {
            switch (p)
            {
                case int i: ms.Write(BitConverter.GetBytes((short)i)); break;
                case uint u: ms.Write(BitConverter.GetBytes(u)); break;
                case byte[] b: ms.Write(b); break;
                default: throw new ArgumentException("unsupported part");
            }
        }

        return ms.ToArray();
    }

    // state
    public WmfBuilder SetWindowOrg(int x, int y) => Rec(0x020B, y, x);

    public WmfBuilder SetWindowExt(int x, int y) => Rec(0x020C, y, x);

    public WmfBuilder SetViewportOrg(int x, int y) => Rec(0x020D, y, x);

    public WmfBuilder SetViewportExt(int x, int y) => Rec(0x020E, y, x);

    public WmfBuilder OffsetWindowOrg(int x, int y) => Rec(0x020F, y, x);

    public WmfBuilder OffsetViewportOrg(int x, int y) => Rec(0x0211, y, x);

    public WmfBuilder ScaleWindowExt(int xNum, int xDenom, int yNum, int yDenom) => Rec(0x0410, yDenom, yNum, xDenom, xNum);

    public WmfBuilder SetMapMode(int mode) => Rec(0x0103, mode);

    public WmfBuilder SetBkMode(int mode) => Rec(0x0102, mode, 0);

    public WmfBuilder SetBkColor(int rgb) => Raw(0x0201, BitConverter.GetBytes(rgb));

    public WmfBuilder SetTextColor(int rgb) => Raw(0x0209, BitConverter.GetBytes(rgb));

    public WmfBuilder SetTextAlign(int align) => Rec(0x012E, align, 0);

    public WmfBuilder SetTextCharExtra(int extra) => Rec(0x0108, extra);

    public WmfBuilder SetPolyFillMode(int mode) => Rec(0x0106, mode, 0);

    public WmfBuilder SetRop2(int mode) => Rec(0x0104, mode, 0);

    public WmfBuilder SetStretchBltMode(int mode) => Rec(0x0107, mode, 0);

    public WmfBuilder SaveDc() => Rec(0x001E);

    public WmfBuilder RestoreDc(int n) => Rec(0x0127, n);

    // objects
    public WmfBuilder CreatePen(int style, int width, int rgb) => Raw(0x02FA, Words(style, width, 0, (uint)rgb));

    public WmfBuilder CreateBrush(int style, int rgb, int hatch = 0) => Raw(0x02FC, Words(style, (uint)rgb, hatch));

    public WmfBuilder CreateFont(int height, string face, int weight = 400, bool underline = false, int escapement = 0)
    {
        var f = new byte[50];
        BinaryPrimitives.WriteInt16LittleEndian(f, (short)height);
        BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(4), (short)escapement);
        BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(6), (short)escapement);
        BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(8), (short)weight);
        f[11] = (byte)(underline ? 1 : 0);
        Encoding.Latin1.GetBytes(face, 0, Math.Min(face.Length, 31), f, 18);
        return Raw(0x02FB, f);
    }

    public WmfBuilder CreatePalette() => Raw(0x00F7, Words(0x0300, 1, new byte[4]));

    public WmfBuilder Select(int index) => Rec(0x012D, index);

    public WmfBuilder Delete(int index) => Rec(0x01F0, index);

    // drawing
    public WmfBuilder Rectangle(int l, int t, int r, int b) => Rec(0x041B, b, r, t, l);

    public WmfBuilder Ellipse(int l, int t, int r, int b) => Rec(0x0418, b, r, t, l);

    public WmfBuilder RoundRect(int l, int t, int r, int b, int w, int h) => Rec(0x061C, h, w, b, r, t, l);

    public WmfBuilder Arc(int function, int l, int t, int r, int b, int xStart, int yStart, int xEnd, int yEnd) => Rec(function, yEnd, xEnd, yStart, xStart, b, r, t, l);

    public WmfBuilder MoveTo(int x, int y) => Rec(0x0214, y, x);

    public WmfBuilder LineTo(int x, int y) => Rec(0x0213, y, x);

    public WmfBuilder SetPixel(int x, int y, int rgb) => Raw(0x041F, Words((uint)rgb, y, x));

    public WmfBuilder Poly(int function, params int[] xy)
    {
        var w = new List<int> { xy.Length / 2 };
        w.AddRange(xy);
        return Rec(function, [.. w]);
    }

    public WmfBuilder PolyPolygon(int[] counts, params int[] xy)
    {
        var w = new List<int> { counts.Length };
        w.AddRange(counts);
        w.AddRange(xy);
        return Rec(0x0538, [.. w]);
    }

    public WmfBuilder PatBlt(int x, int y, int w, int h, uint rop) => Raw(0x061D, Words(rop, h, w, y, x));

    public WmfBuilder TextOut(int x, int y, string text)
    {
        byte[] s = Encoding.Latin1.GetBytes(text);
        byte[] padded = new byte[(s.Length + 1) & ~1];
        s.CopyTo(padded, 0);
        return Raw(0x0521, Words(text.Length, padded, y, x));
    }

    public WmfBuilder ExtTextOut(int x, int y, int options, (int L, int T, int R, int B)? rect, string text, int[]? dx = null)
    {
        byte[] s = Encoding.Latin1.GetBytes(text);
        byte[] padded = new byte[(s.Length + 1) & ~1];
        s.CopyTo(padded, 0);
        var parts = new List<object> { y, x, text.Length, options };
        if (rect is { } r)
        {
            parts.AddRange([r.L, r.T, r.R, r.B]);
        }

        parts.Add(padded);
        if (dx is not null)
        {
            parts.AddRange(dx.Cast<object>());
        }

        return Raw(0x0A32, Words([.. parts]));
    }

    // bitmaps
    /// <summary>A packed 24-bit bottom-up DIB.</summary>
    public static byte[] Dib24(int w, int h, Func<int, int, (int R, int G, int B)> pixel)
    {
        int stride = ((w * 3) + 3) & ~3;
        var d = new byte[40 + (stride * h)];
        BinaryPrimitives.WriteUInt32LittleEndian(d, 40);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(4), w);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(8), h);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(14), 24);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                (int r, int g, int b) = pixel(x, y);
                int o = 40 + ((h - 1 - y) * stride) + (x * 3);
                d[o] = (byte)b;
                d[o + 1] = (byte)g;
                d[o + 2] = (byte)r;
            }
        }

        return d;
    }

    public WmfBuilder StretchDib(int xd, int yd, int cxd, int cyd, int xs, int ys, int cxs, int cys, uint rop, byte[] dib, int usage = 0) =>
        Raw(0x0F43, Words(rop, usage, cys, cxs, ys, xs, cyd, cxd, yd, xd, dib));

    public WmfBuilder DibBitBlt(int xd, int yd, int w, int h, int xs, int ys, uint rop, byte[] dib) =>
        Raw(0x0940, Words(rop, ys, xs, h, w, yd, xd, dib));

    public WmfBuilder DibBitBltNoBitmap(int xd, int yd, int w, int h, uint rop) =>
        Raw(0x0940, Words(rop, 0, 0, 0, h, w, yd, xd));

    public WmfBuilder DibStretchBlt(int xd, int yd, int cxd, int cyd, int xs, int ys, int cxs, int cys, uint rop, byte[] dib) =>
        Raw(0x0B41, Words(rop, cys, cxs, ys, xs, cyd, cxd, yd, xd, dib));

    public WmfBuilder SetDibToDevice(int xd, int yd, int w, int h, int xs, int ys, byte[] dib, int scanCount, int startScan = 0) =>
        Raw(0x0D33, Words(0, scanCount, startScan, ys, xs, h, w, yd, xd, dib));

    /// <summary>A Bitmap16 object holding a 1-bit-per-pixel bitmap; <paramref name="rows"/> are 16-bit-aligned scan lines.</summary>
    public static byte[] Bitmap16(int w, int h, int bpp, byte[] rows)
    {
        int widthBytes = (((w * bpp) + 15) >> 4) << 1;
        return Words(0, w, h, widthBytes, new byte[] { 1, (byte)bpp }, rows);
    }

    public WmfBuilder BitBlt(int xd, int yd, int w, int h, int xs, int ys, uint rop, byte[] bitmap16) =>
        Raw(0x0922, Words(rop, ys, xs, h, w, yd, xd, bitmap16));

    public WmfBuilder DibCreatePatternBrush(int style, int usage, byte[] data) => Raw(0x0142, Words(style, usage, data));

    public WmfBuilder CreatePatternBrush(int w, int h, byte[] rows)
    {
        // Bitmap16 header of 14 bytes (Type, Width, Height, WidthBytes, Planes, BitsPixel, 4 ignored bytes), 18 reserved bytes, the bits.
        int widthBytes = (((w * 1) + 15) >> 4) << 1;
        return Raw(0x01F9, Words(0, w, h, widthBytes, new byte[] { 1, 1 }, new byte[4], new byte[18], rows));
    }

    // regions and clipping
    /// <summary>A Region object of rectangles, one scan line each: (left, top, right, bottom).</summary>
    public static byte[] Region(params (int L, int T, int R, int B)[] rects)
    {
        var ms = new MemoryStream();
        foreach ((int l, int t, int r, int b) in rects)
        {
            ms.Write(Words(2, t, b, l, r, 2));
        }

        byte[] scans = ms.ToArray();
        return Words(0, 6, new byte[4], 20 + scans.Length, rects.Length, 2, rects.Min(x => x.L), rects.Min(x => x.T), rects.Max(x => x.R), rects.Max(x => x.B), scans);
    }

    public WmfBuilder CreateRegion(params (int L, int T, int R, int B)[] rects) => Raw(0x06FF, Region(rects));

    public WmfBuilder FillRegion(int region, int brush) => Rec(0x0228, region, brush);

    public WmfBuilder FrameRegion(int region, int brush, int h, int w) => Rec(0x0429, region, brush, h, w);

    public WmfBuilder PaintRegion(int region) => Rec(0x012B, region);

    public WmfBuilder InvertRegion(int region) => Rec(0x012A, region);

    public WmfBuilder SelectClipRegion(int region) => Rec(0x012C, region);

    public WmfBuilder IntersectClipRect(int l, int t, int r, int b) => Rec(0x0416, b, r, t, l);

    public WmfBuilder ExcludeClipRect(int l, int t, int r, int b) => Rec(0x0415, b, r, t, l);

    public WmfBuilder OffsetClipRgn(int x, int y) => Rec(0x0220, y, x);

    // escapes
    public WmfBuilder Escape(int function, byte[] data) => Raw(0x0626, Words(function, data.Length, data));

    /// <summary>Embeds an EMF in META_ESCAPE_ENHANCED_METAFILE records of at most <paramref name="chunk"/> bytes.</summary>
    public WmfBuilder EmbedEmf(byte[] emf, int chunk = 8192, int corruptRemainingAt = -1)
    {
        int count = (emf.Length + chunk - 1) / chunk;
        int done = 0;
        for (int i = 0; i < count; i++)
        {
            int n = Math.Min(chunk, emf.Length - done);
            var body = new byte[34 + n];
            BinaryPrimitives.WriteUInt32LittleEndian(body, 0x43464D57);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 0x00010000);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(18), (uint)count);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(22), (uint)n);
            uint remaining = (uint)(emf.Length - done - n);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(26), i == corruptRemainingAt ? remaining + 1 : remaining);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(30), (uint)emf.Length);
            emf.AsSpan(done, n).CopyTo(body.AsSpan(34));
            Escape(0x000F, body);
            done += n;
        }

        return this;
    }

    public byte[] Build(bool eof = true)
    {
        var body = new List<byte[]>(_records);
        if (eof)
        {
            body.Add([3, 0, 0, 0, 0, 0]);
        }

        int words = 9 + body.Sum(r => r.Length / 2);
        var file = new MemoryStream();
        if (_placeable is { } p)
        {
            var key = new byte[22];
            BinaryPrimitives.WriteUInt32LittleEndian(key, 0x9AC6CDD7);
            BinaryPrimitives.WriteInt16LittleEndian(key.AsSpan(6), (short)p.L);
            BinaryPrimitives.WriteInt16LittleEndian(key.AsSpan(8), (short)p.T);
            BinaryPrimitives.WriteInt16LittleEndian(key.AsSpan(10), (short)p.R);
            BinaryPrimitives.WriteInt16LittleEndian(key.AsSpan(12), (short)p.B);
            BinaryPrimitives.WriteUInt16LittleEndian(key.AsSpan(14), (ushort)p.Inch);
            ushort sum = 0;
            for (int i = 0; i < 20; i += 2)
            {
                sum ^= BinaryPrimitives.ReadUInt16LittleEndian(key.AsSpan(i));
            }

            BinaryPrimitives.WriteUInt16LittleEndian(key.AsSpan(20), sum);
            file.Write(key);
        }

        var header = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(header, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 0x0300);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)(words & 0xFFFF));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)(words >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 8);
        file.Write(header);
        foreach (byte[] r in body)
        {
            file.Write(r);
        }

        return file.ToArray();
    }
}
