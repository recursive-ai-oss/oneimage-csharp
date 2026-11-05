using System.Buffers.Binary;
using System.Text;

namespace OneImage.Metafile.Tests;

/// <summary>
/// A small EMF writer for tests: builds files from the record layouts of the specification. The reference device is 1000 x 1000 pixels over
/// 250 x 250 mm (4 pixels per millimeter), and the frame is <c>size</c> pixels square, so one logical unit in MM_TEXT is one pixel of output at natural size.
/// </summary>
internal sealed class EmfBuilder
{
    private readonly List<byte[]> _records = [];
    private readonly int _size;

    public EmfBuilder(int size = 100) => _size = size;

    public int Count => _records.Count;

    public static byte[] Int32s(params int[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 4), v[i]);
        }

        return b;
    }

    public EmfBuilder Raw(uint type, byte[] body)
    {
        int size = 8 + body.Length;
        size = (size + 3) & ~3;
        var r = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(r, type);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), (uint)size);
        body.CopyTo(r, 8);
        _records.Add(r);
        return this;
    }

    public EmfBuilder Ints(uint type, params int[] v) => Raw(type, Int32s(v));

    public static int Rgb(int r, int g, int b) => r | (g << 8) | (b << 16);

    public EmfBuilder MapMode(int mode) => Ints(17, mode);

    public EmfBuilder WindowExt(int x, int y) => Ints(9, x, y);

    public EmfBuilder ViewportExt(int x, int y) => Ints(11, x, y);

    public EmfBuilder WindowOrg(int x, int y) => Ints(10, x, y);

    public EmfBuilder ViewportOrg(int x, int y) => Ints(12, x, y);

    public EmfBuilder CreatePen(int ih, int style, int width, int color) => Ints(38, ih, style, width, 0, color);

    public EmfBuilder CreateBrush(int ih, int style, int color, int hatch = 0) => Ints(39, ih, style, color, hatch);

    public EmfBuilder Select(int ih) => Ints(37, ih);

    public EmfBuilder SelectStock(int id) => Ints(37, unchecked((int)(0x80000000 | (uint)id)));

    public EmfBuilder Delete(int ih) => Ints(40, ih);

    public EmfBuilder Rectangle(int l, int t, int r, int b) => Ints(43, l, t, r, b);

    public EmfBuilder Ellipse(int l, int t, int r, int b) => Ints(42, l, t, r, b);

    public EmfBuilder RoundRect(int l, int t, int r, int b, int w, int h) => Ints(44, l, t, r, b, w, h);

    public EmfBuilder Arc(uint type, int l, int t, int r, int b, int x1, int y1, int x2, int y2) => Ints(type, l, t, r, b, x1, y1, x2, y2);

    public EmfBuilder MoveTo(int x, int y) => Ints(27, x, y);

    public EmfBuilder LineTo(int x, int y) => Ints(54, x, y);

    public EmfBuilder SetBkMode(int m) => Ints(18, m);

    public EmfBuilder SetBkColor(int c) => Ints(25, c);

    public EmfBuilder SetTextColor(int c) => Ints(24, c);

    public EmfBuilder SetTextAlign(int a) => Ints(22, a);

    public EmfBuilder SetPolyFillMode(int m) => Ints(19, m);

    public EmfBuilder SetRop2(int m) => Ints(20, m);

    public EmfBuilder SaveDc() => Raw(33, []);

    public EmfBuilder RestoreDc(int n) => Ints(34, n);

    public EmfBuilder BeginPath() => Raw(59, []);

    public EmfBuilder EndPath() => Raw(60, []);

    public EmfBuilder CloseFigure() => Raw(61, []);

    public EmfBuilder FillPath() => Raw(62, Int32s(0, 0, 0, 0));

    public EmfBuilder StrokePath() => Raw(64, Int32s(0, 0, 0, 0));

    public EmfBuilder StrokeAndFillPath() => Raw(63, Int32s(0, 0, 0, 0));

    public EmfBuilder SelectClipPath(int mode) => Ints(67, mode);

    public EmfBuilder IntersectClipRect(int l, int t, int r, int b) => Ints(30, l, t, r, b);

    public EmfBuilder ExcludeClipRect(int l, int t, int r, int b) => Ints(29, l, t, r, b);

    public EmfBuilder SetWorldTransform(float a, float b, float c, float d, float e, float f)
    {
        var body = new byte[24];
        float[] v = [a, b, c, d, e, f];
        for (int i = 0; i < 6; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(i * 4), v[i]);
        }

        return Raw(35, body);
    }

    public EmfBuilder Polygon(uint type, bool is16, params int[] xy)
    {
        int n = xy.Length / 2;
        var body = new byte[16 + 4 + (n * (is16 ? 4 : 8))];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), n);
        int o = 20;
        for (int i = 0; i < n; i++)
        {
            if (is16)
            {
                BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(o), (short)xy[i * 2]);
                BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(o + 2), (short)xy[(i * 2) + 1]);
                o += 4;
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(o), xy[i * 2]);
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(o + 4), xy[(i * 2) + 1]);
                o += 8;
            }
        }

        return Raw(type, body);
    }

    public EmfBuilder PolyPolygon(uint type, int[] counts, params int[] xy)
    {
        int n = xy.Length / 2;
        var body = new byte[16 + 8 + (counts.Length * 4) + (n * 8)];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), counts.Length);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(20), n);
        for (int i = 0; i < counts.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(24 + (i * 4)), counts[i]);
        }

        int o = 24 + (counts.Length * 4);
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(o), xy[i * 2]);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(o + 4), xy[(i * 2) + 1]);
            o += 8;
        }

        return Raw(type, body);
    }

    /// <summary>EMR_STRETCHDIBITS of a 24-bit bottom-up bitmap given as rows of top-first (r, g, b) triples.</summary>
    public EmfBuilder StretchDibits(int xd, int yd, int cxd, int cyd, int w, int h, Func<int, int, (byte R, byte G, byte B)> pixel, uint rop = 0x00CC0020)
    {
        int stride = ((w * 3) + 3) & ~3;
        var bits = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                (byte r, byte g, byte b) = pixel(x, y);
                int o = ((h - 1 - y) * stride) + (x * 3);
                bits[o] = b;
                bits[o + 1] = g;
                bits[o + 2] = r;
            }
        }

        byte[] bmi = BitmapInfo(w, h, 24);
        // Bounds(16) xDest yDest xSrc ySrc cxSrc cySrc offBmi cbBmi offBits cbBits Usage Rop cxDest cyDest
        int fixedSize = 8 + 16 + (15 * 4);
        var body = new byte[fixedSize - 8 + bmi.Length + bits.Length];
        void W(int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at - 8), v);
        W(24, xd);
        W(28, yd);
        W(32, 0);
        W(36, 0);
        W(40, w);
        W(44, h);
        W(48, fixedSize);
        W(52, bmi.Length);
        W(56, fixedSize + bmi.Length);
        W(60, bits.Length);
        W(64, 0);
        W(68, (int)rop);
        W(72, cxd);
        W(76, cyd);
        bmi.CopyTo(body, fixedSize - 8);
        bits.CopyTo(body, fixedSize - 8 + bmi.Length);
        return Raw(81, body);
    }

    public static byte[] BitmapInfo(int w, int h, int bpp, uint[]? palette = null)
    {
        int colors = palette?.Length ?? 0;
        var b = new byte[40 + (colors * 4)];
        BinaryPrimitives.WriteInt32LittleEndian(b, 40);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), w);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), h);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(14), (short)bpp);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(32), colors);
        for (int i = 0; i < colors; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40 + (i * 4)), palette![i]);
        }

        return b;
    }

    /// <summary>EMR_BITBLT without a source bitmap (PatBlt).</summary>
    public EmfBuilder PatBlt(int x, int y, int w, int h, uint rop)
    {
        var body = new byte[100 - 8];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(24 - 8), x);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(28 - 8), y);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(32 - 8), w);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(36 - 8), h);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(40 - 8), rop);
        return Raw(76, body);
    }

    public EmfBuilder ExtTextOutW(string text, int x, int y, int options = 0, int[]? dx = null)
    {
        // Bounds(16) iGraphicsMode exScale eyScale | Reference(8) Chars offString Options Rectangle(16) offDx
        int textAt = 8 + 16 + 12 + 40;
        int strBytes = text.Length * 2;
        int dxAt = (textAt + strBytes + 3) & ~3;
        var body = new byte[dxAt + (dx is null ? 0 : dx.Length * 4) - 8];
        void W(int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at - 8), v);
        W(24, 1);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(28 - 8), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(32 - 8), 1f);
        W(36, x);
        W(40, y);
        W(44, text.Length);
        W(48, textAt);
        W(52, options);
        W(72, dx is null ? 0 : dxAt);
        Encoding.Unicode.GetBytes(text, 0, text.Length, body, textAt - 8);
        if (dx is not null)
        {
            for (int i = 0; i < dx.Length; i++)
            {
                W(dxAt + (i * 4), dx[i]);
            }
        }

        return Raw(84, body);
    }

    public EmfBuilder CreateFont(int ih, int height, string face, int weight = 400, int escapement = 0, bool underline = false)
    {
        var body = new byte[4 + 92];
        BinaryPrimitives.WriteInt32LittleEndian(body, ih);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), height);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(12), escapement);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), escapement);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(20), weight);
        body[25] = (byte)(underline ? 1 : 0);
        body[28] = 1;
        Encoding.Unicode.GetBytes(face, 0, Math.Min(face.Length, 31), body, 32);
        return Raw(82, body);
    }

    public EmfBuilder GradientRect(int x0, int y0, int x1, int y1, ushort[] c0, ushort[] c1, uint mode)
    {
        var body = new byte[16 + 12 + 32 + 8];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), 2);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), mode);
        int[] xs = [x0, x1], ys = [y0, y1];
        ushort[][] cs = [c0, c1];
        for (int i = 0; i < 2; i++)
        {
            int o = 28 + (i * 16);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(o), xs[i]);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(o + 4), ys[i]);
            for (int k = 0; k < 4; k++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o + 8 + (k * 2)), cs[i][k]);
            }
        }

        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(60), 0);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(64), 1);
        return Raw(118, body);
    }

    public EmfBuilder Comment(string id, byte[] data)
    {
        var body = new byte[4 + 4 + data.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body, 4 + data.Length);
        Encoding.ASCII.GetBytes(id, 0, 4, body, 4);
        data.CopyTo(body, 8);
        return Raw(70, body);
    }

    public byte[] Build(bool eof = true)
    {
        var all = new List<byte[]>();
        if (eof)
        {
            var e = new EmfBuilder(_size);
            e.Raw(14, Int32s(0, 16, 20));
            all.AddRange(_records);
            all.Add(e._records[0]);
        }
        else
        {
            all.AddRange(_records);
        }

        const int headerSize = 108;
        int total = headerSize + all.Sum(r => r.Length);
        var h = new byte[headerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), headerSize);
        int frame = _size * 25;
        // device bounds (inclusive), frame in 0.01 mm: 4 px per mm => 1 px = 25 units
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), 0);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(12), 0);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), _size - 1);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(20), _size - 1);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), 0);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), 0);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(32), frame);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(36), frame);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), 0x464D4520);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(44), 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(48), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(52), (uint)(all.Count + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(56), 16);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(72), 1000);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(76), 1000);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(80), 250);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(84), 250);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(100), 250_000);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(104), 250_000);
        var file = new byte[total];
        h.CopyTo(file, 0);
        int at = headerSize;
        foreach (byte[] r in all)
        {
            r.CopyTo(file, at);
            at += r.Length;
        }

        return file;
    }
}
