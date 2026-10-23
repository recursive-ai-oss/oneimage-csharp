using System.Buffers.Binary;

namespace OneImage.Codecs.Jpx;

/// <summary>A bounds-checked big-endian cursor over header bytes.</summary>
internal ref struct JpxCursor
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public JpxCursor(ReadOnlySpan<byte> data, int pos = 0)
    {
        _data = data;
        _pos = pos;
    }

    public readonly int Position => _pos;

    public readonly int Remaining => _data.Length - _pos;

    public int U8()
    {
        if (_pos >= _data.Length)
        {
            throw Truncated();
        }

        return _data[_pos++];
    }

    public int U16()
    {
        if (_data.Length - _pos < 2)
        {
            throw Truncated();
        }

        int v = BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(_pos));
        _pos += 2;
        return v;
    }

    public uint U32()
    {
        if (_data.Length - _pos < 4)
        {
            throw Truncated();
        }

        uint v = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_pos));
        _pos += 4;
        return v;
    }

    public void Skip(int count)
    {
        if (count < 0 || _data.Length - _pos < count)
        {
            throw Truncated();
        }

        _pos += count;
    }

    private static ImageFormatException Truncated() => new("JPEG 2000 header is truncated.");
}

/// <summary>The SIZ marker segment (T.800 A.5.1).</summary>
internal sealed class JpxSiz
{
    public int Xsiz;
    public int Ysiz;
    public int XOsiz;
    public int YOsiz;
    public int XTsiz;
    public int YTsiz;
    public int XTOsiz;
    public int YTOsiz;
    public int Csiz;
    public int[] Precision = [];
    public bool[] Signed = [];
    public int[] XR = [];
    public int[] YR = [];

    public int Width => Xsiz - XOsiz;

    public int Height => Ysiz - YOsiz;

    public int TilesX { get; set; }

    public int TilesY { get; set; }

    public int TileCount => TilesX * TilesY;
}

/// <summary>Coding style of one component in one tile (COD/COC, T.800 A.6.1 and A.6.2).</summary>
internal sealed class JpxCodParams
{
    public int Levels;
    public int XcbExp;
    public int YcbExp;
    public int Style;
    public bool Reversible;
    public int[] PpX = [];
    public int[] PpY = [];

    public bool Bypass => (Style & 0x01) != 0;

    public bool ResetContexts => (Style & 0x02) != 0;

    public bool TerminateAll => (Style & 0x04) != 0;

    public bool VerticallyCausal => (Style & 0x08) != 0;

    public bool PredictableTermination => (Style & 0x10) != 0;

    public bool SegmentationSymbols => (Style & 0x20) != 0;
}

/// <summary>Quantization of one component in one tile (QCD/QCC, T.800 A.6.4 and A.6.5).</summary>
internal sealed class JpxQuant
{
    public int Style;
    public int Guard;
    public int[] Exp = [];
    public int[] Mant = [];
}

/// <summary>One progression order change entry (T.800 A.6.6).</summary>
internal readonly record struct JpxPoc(int ResStart, int CompStart, int LayerEnd, int ResEnd, int CompEnd, int Order);

/// <summary>The marker segments that can occur in the main header and again, overriding, in a tile-part header.</summary>
internal sealed class JpxMarkerSet
{
    public JpxMarkerSet(int components)
    {
        Coc = new JpxCodParams?[components];
        Qcc = new JpxQuant?[components];
        Rgn = new int[components];
        Array.Fill(Rgn, -1);
    }

    public bool HasCod;
    public int Order;
    public int Layers = 1;
    public int Mct;
    public bool Sop;
    public bool Eph;
    public JpxCodParams? Cod;
    public JpxCodParams?[] Coc;
    public JpxQuant? Qcd;
    public JpxQuant?[] Qcc;
    public int[] Rgn;
    public List<JpxPoc> Poc = [];
    public List<(int Z, int Offset, int Length)> Ppt = [];
    public List<(int Z, int Offset, int Length)> Ppm = [];
}

/// <summary>Parses the marker segments of a codestream header into the models above.</summary>
internal static class JpxHeaderParser
{
    public const int Soc = 0xFF4F;
    public const int SizM = 0xFF51;
    public const int CodM = 0xFF52;
    public const int CocM = 0xFF53;
    public const int RgnM = 0xFF5E;
    public const int QcdM = 0xFF5C;
    public const int QccM = 0xFF5D;
    public const int PocM = 0xFF5F;
    public const int Ppm = 0xFF60;
    public const int Ppt = 0xFF61;
    public const int Sot = 0xFF90;
    public const int Sod = 0xFF93;
    public const int Eoc = 0xFFD9;

    /// <summary>Parses the SIZ body (after the length field) and validates it against the limits.</summary>
    public static JpxSiz ParseSiz(ReadOnlySpan<byte> body, JpxDecodeOptions options)
    {
        var c = new JpxCursor(body);
        c.U16(); // Rsiz: capabilities. Part 2 extension bits are not interpreted; a decoder reads what Part 1 defines.
        uint xs = c.U32();
        uint ys = c.U32();
        uint xo = c.U32();
        uint yo = c.U32();
        uint xt = c.U32();
        uint yt = c.U32();
        uint xto = c.U32();
        uint yto = c.U32();
        int csiz = c.U16();
        if (csiz < 1 || csiz > 16384)
        {
            throw new ImageFormatException("Invalid component count in JPEG 2000 header.");
        }

        if (csiz > options.MaxComponents)
        {
            throw new ImageLimitException($"JPEG 2000 declares {csiz} components, over the limit of {options.MaxComponents}.");
        }

        if (xs == 0 || ys == 0 || xs > int.MaxValue || ys > int.MaxValue || xo >= xs || yo >= ys || xt == 0 || yt == 0
            || xt > int.MaxValue || yt > int.MaxValue || xto > xo || yto > yo)
        {
            throw new ImageFormatException("Invalid image or tile geometry in JPEG 2000 header.");
        }

        // The first tile must cover the image origin (T.800 A.5.1).
        if ((long)xto + xt <= xo || (long)yto + yt <= yo)
        {
            throw new ImageFormatException("The first JPEG 2000 tile does not cover the image origin.");
        }

        long width = (long)xs - xo;
        long height = (long)ys - yo;
        options.Limits.CheckFrame(width, height);

        long tilesX = ((long)xs - xto + xt - 1) / xt;
        long tilesY = ((long)ys - yto + yt - 1) / yt;
        if (tilesX * tilesY > 65535)
        {
            throw new ImageFormatException("JPEG 2000 declares more than 65535 tiles.");
        }

        var siz = new JpxSiz
        {
            Xsiz = (int)xs,
            Ysiz = (int)ys,
            XOsiz = (int)xo,
            YOsiz = (int)yo,
            XTsiz = (int)xt,
            YTsiz = (int)yt,
            XTOsiz = (int)xto,
            YTOsiz = (int)yto,
            Csiz = csiz,
            Precision = new int[csiz],
            Signed = new bool[csiz],
            XR = new int[csiz],
            YR = new int[csiz],
            TilesX = (int)tilesX,
            TilesY = (int)tilesY,
        };

        for (int i = 0; i < csiz; i++)
        {
            int s = c.U8();
            siz.Signed[i] = (s & 0x80) != 0;
            siz.Precision[i] = (s & 0x7F) + 1;
            siz.XR[i] = c.U8();
            siz.YR[i] = c.U8();
            if (siz.XR[i] == 0 || siz.YR[i] == 0)
            {
                throw new ImageFormatException("Invalid component sub-sampling in JPEG 2000 header.");
            }

            if (siz.Precision[i] > 16)
            {
                throw new ImageFormatException($"JPEG 2000 sample depth {siz.Precision[i]} is not supported (the limit is 16 bits).");
            }
        }

        return siz;
    }

    /// <summary>Parses one marker segment body into <paramref name="set"/>. Unknown markers are ignored.</summary>
    /// <param name="marker">The marker code.</param>
    /// <param name="body">The segment body after the two length bytes.</param>
    /// <param name="bodyOffset">Where the body starts in the codestream (for PPM/PPT slices).</param>
    /// <param name="set">The set to fill.</param>
    /// <param name="siz">The image header.</param>
    public static void ParseSegment(int marker, ReadOnlySpan<byte> body, int bodyOffset, JpxMarkerSet set, JpxSiz siz)
    {
        switch (marker)
        {
            case CodM:
                ParseCod(body, set);
                break;
            case CocM:
                ParseCoc(body, set, siz);
                break;
            case QcdM:
                set.Qcd = ParseQuant(body, 0, body.Length);
                break;
            case QccM:
                ParseQcc(body, set, siz);
                break;
            case RgnM:
                ParseRgn(body, set, siz);
                break;
            case PocM:
                ParsePoc(body, set, siz);
                break;
            case Ppm:
            case Ppt:
                if (body.Length < 1)
                {
                    throw new ImageFormatException("Empty PPM/PPT segment.");
                }

                (marker == Ppm ? set.Ppm : set.Ppt).Add((body[0], bodyOffset + 1, body.Length - 1));
                break;
        }
    }

    private static void ParseCod(ReadOnlySpan<byte> body, JpxMarkerSet set)
    {
        var c = new JpxCursor(body);
        int scod = c.U8();
        int order = c.U8();
        int layers = c.U16();
        int mct = c.U8();
        if (order > 4 || layers < 1 || mct > 1)
        {
            throw new ImageFormatException("Invalid COD marker segment.");
        }

        set.Cod = ParseSp(ref c, (scod & 1) != 0);
        set.HasCod = true;
        set.Order = order;
        set.Layers = layers;
        set.Mct = mct;
        set.Sop = (scod & 2) != 0;
        set.Eph = (scod & 4) != 0;
    }

    private static void ParseCoc(ReadOnlySpan<byte> body, JpxMarkerSet set, JpxSiz siz)
    {
        var c = new JpxCursor(body);
        int comp = siz.Csiz < 257 ? c.U8() : c.U16();
        if (comp >= siz.Csiz)
        {
            throw new ImageFormatException("COC names a component that does not exist.");
        }

        int scoc = c.U8();
        set.Coc[comp] = ParseSp(ref c, (scoc & 1) != 0);
    }

    private static JpxCodParams ParseSp(ref JpxCursor c, bool precincts)
    {
        int levels = c.U8();
        int xcb = c.U8() + 2;
        int ycb = c.U8() + 2;
        int style = c.U8();
        int transform = c.U8();
        if (levels > 32 || xcb > 10 || ycb > 10 || xcb + ycb > 12 || style > 0x3F || transform > 1)
        {
            throw new ImageFormatException("Invalid JPEG 2000 coding style parameters.");
        }

        var p = new JpxCodParams
        {
            Levels = levels,
            XcbExp = xcb,
            YcbExp = ycb,
            Style = style,
            Reversible = transform == 1,
            PpX = new int[levels + 1],
            PpY = new int[levels + 1],
        };

        for (int r = 0; r <= levels; r++)
        {
            if (precincts)
            {
                int b = c.U8();
                p.PpX[r] = b & 15;
                p.PpY[r] = b >> 4;
                if (r > 0 && (p.PpX[r] == 0 || p.PpY[r] == 0))
                {
                    throw new ImageFormatException("A precinct smaller than 2x2 is only valid at the lowest resolution.");
                }
            }
            else
            {
                p.PpX[r] = 15;
                p.PpY[r] = 15;
            }
        }

        return p;
    }

    private static JpxQuant ParseQuant(ReadOnlySpan<byte> body, int offset, int end)
    {
        var c = new JpxCursor(body, offset);
        int sq = c.U8();
        int style = sq & 31;
        var q = new JpxQuant { Style = style, Guard = sq >> 5 };
        if (style > 2)
        {
            throw new ImageFormatException("Invalid quantization style.");
        }

        int bytes = end - c.Position;
        if (style == 0)
        {
            if (bytes < 1 || bytes > 97)
            {
                throw new ImageFormatException("Invalid QCD/QCC length.");
            }

            q.Exp = new int[bytes];
            q.Mant = new int[bytes];
            for (int i = 0; i < bytes; i++)
            {
                q.Exp[i] = c.U8() >> 3;
            }
        }
        else
        {
            int count = style == 1 ? 1 : bytes / 2;
            if (bytes < 2 || (style == 1 && bytes != 2) || (style == 2 && (bytes % 2 != 0 || count > 97)))
            {
                throw new ImageFormatException("Invalid QCD/QCC length.");
            }

            q.Exp = new int[count];
            q.Mant = new int[count];
            for (int i = 0; i < count; i++)
            {
                int v = c.U16();
                q.Exp[i] = v >> 11;
                q.Mant[i] = v & 0x7FF;
            }
        }

        return q;
    }

    private static void ParseQcc(ReadOnlySpan<byte> body, JpxMarkerSet set, JpxSiz siz)
    {
        var c = new JpxCursor(body);
        int comp = siz.Csiz < 257 ? c.U8() : c.U16();
        if (comp >= siz.Csiz)
        {
            throw new ImageFormatException("QCC names a component that does not exist.");
        }

        set.Qcc[comp] = ParseQuant(body, c.Position, body.Length);
    }

    private static void ParseRgn(ReadOnlySpan<byte> body, JpxMarkerSet set, JpxSiz siz)
    {
        var c = new JpxCursor(body);
        int comp = siz.Csiz < 257 ? c.U8() : c.U16();
        int style = c.U8();
        int shift = c.U8();
        if (comp >= siz.Csiz || style != 0 || shift > 37)
        {
            throw new ImageFormatException("Invalid RGN marker segment.");
        }

        set.Rgn[comp] = shift;
    }

    private static void ParsePoc(ReadOnlySpan<byte> body, JpxMarkerSet set, JpxSiz siz)
    {
        var c = new JpxCursor(body);
        bool wide = siz.Csiz >= 257;
        int entry = wide ? 9 : 7;
        if (body.Length % entry != 0)
        {
            throw new ImageFormatException("Invalid POC marker segment.");
        }

        while (c.Remaining > 0)
        {
            int rs = c.U8();
            int cs = wide ? c.U16() : c.U8();
            int lye = c.U16();
            int re = c.U8();
            int ce = wide ? c.U16() : c.U8();
            int order = c.U8();
            if (ce == 0)
            {
                ce = wide ? 16384 : 256;
            }

            if (order > 4 || rs >= re || cs >= ce)
            {
                throw new ImageFormatException("Invalid progression order change.");
            }

            if (set.Poc.Count >= 1024)
            {
                throw new ImageLimitException("Too many progression order changes.");
            }

            set.Poc.Add(new JpxPoc(rs, cs, lye, re, Math.Min(ce, siz.Csiz), order));
        }
    }
}
