namespace OneImage.Text.Fonts;

/// <summary>
/// Reads CFF1 outlines (Adobe TN 5176) and interprets Type 2 charstrings (TN 5177): name-keyed and CID-keyed fonts, local and global
/// subroutines, hint operators (skipped), flex, and the arithmetic operators. The interpreter bounds operand stack, subroutine
/// depth and total operator count, so a hostile charstring cannot loop or overrun. Unhinted; returns font units.
/// </summary>
internal sealed class CffGlyphs
{
    private const int MaxStack = 96;
    private const int MaxCallDepth = 10;
    private const int MaxOperators = 400_000;
    private const int MaxFontDicts = 256;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly Index _charStrings;
    private readonly Index _globalSubrs;
    private readonly FontDict[] _fontDicts;
    private readonly ReadOnlyMemory<byte> _fdSelect;

    public CffGlyphs(ReadOnlyMemory<byte> cff)
    {
        _data = cff;
        ReadOnlySpan<byte> d = cff.Span;
        if (Be.U8(d, 0) != 1)
        {
            throw new FontFormatException("Only CFF version 1 is supported.");
        }

        int hdrSize = Be.U8(d, 2);
        Index nameIndex = Index.Read(d, hdrSize, out int pos);
        _ = nameIndex;
        Index topIndex = Index.Read(d, pos, out pos);
        Index.Read(d, pos, out pos);
        _globalSubrs = Index.Read(d, pos, out _);
        if (topIndex.Count < 1)
        {
            throw new FontFormatException("The CFF font has no top dictionary.");
        }

        Dictionary<int, double[]> top = ParseDict(topIndex.Get(d, 0));
        if (!top.TryGetValue(17, out double[]? cs) || cs.Length < 1)
        {
            throw new FontFormatException("The CFF font has no CharStrings.");
        }

        _charStrings = Index.Read(d, ToOffset(cs[0]), out _);
        NumGlyphs = _charStrings.Count;

        if (top.TryGetValue(0x0C24, out double[]? fdArray) && fdArray.Length > 0)
        {
            // CID-keyed: one private dictionary per font dict, glyphs choose theirs through FDSelect.
            Index fds = Index.Read(d, ToOffset(fdArray[0]), out _);
            if (fds.Count == 0 || fds.Count > MaxFontDicts)
            {
                throw new FontFormatException("The CFF font dictionary array is invalid.");
            }

            _fontDicts = new FontDict[fds.Count];
            for (int i = 0; i < fds.Count; i++)
            {
                Dictionary<int, double[]> fd = ParseDict(fds.Get(d, i));
                _fontDicts[i] = ReadPrivate(d, fd);
            }

            if (!top.TryGetValue(0x0C25, out double[]? fdSel) || fdSel.Length < 1)
            {
                throw new FontFormatException("A CID-keyed CFF font has no FDSelect.");
            }

            int selOff = ToOffset(fdSel[0]);
            if (selOff >= cff.Length)
            {
                throw Be.Truncated();
            }

            _fdSelect = cff[selOff..];
            ValidateFdSelect(_fdSelect.Span);
        }
        else
        {
            _fontDicts = [ReadPrivate(d, top)];
            _fdSelect = default;
        }
    }

    public int NumGlyphs { get; }

    /// <summary>Runs the glyph's charstring and returns its outline. Out-of-range ids give an empty outline.</summary>
    public Outline Load(int glyphId)
    {
        if ((uint)glyphId >= (uint)NumGlyphs)
        {
            return Outline.Empty;
        }

        ReadOnlySpan<byte> d = _data.Span;
        ReadOnlyMemory<byte> code = _charStrings.GetMemory(d, _data, glyphId);
        FontDict fd = _fontDicts[SelectFd(glyphId)];
        return new Interpreter(this, fd).Run(code);
    }

    private int SelectFd(int glyph)
    {
        if (_fdSelect.IsEmpty)
        {
            return 0;
        }

        ReadOnlySpan<byte> s = _fdSelect.Span;
        int fd;
        if (s[0] == 0)
        {
            fd = Be.U8(s, 1 + glyph);
        }
        else
        {
            // Format 3: ranges of {first glyph, fd}, closed by a sentinel glyph.
            int nRanges = Be.U16(s, 1);
            int lo = 0, hi = nRanges - 1;
            fd = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int first = Be.U16(s, 3 + (mid * 3));
                if (first <= glyph)
                {
                    fd = Be.U8(s, 5 + (mid * 3));
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
        }

        return fd < _fontDicts.Length ? fd : 0;
    }

    private void ValidateFdSelect(ReadOnlySpan<byte> s)
    {
        byte fmt = Be.U8(s, 0);
        if (fmt == 0)
        {
            if (s.Length < 1 + NumGlyphs)
            {
                throw Be.Truncated();
            }
        }
        else if (fmt == 3)
        {
            int n = Be.U16(s, 1);
            if (s.Length < 5 + (3 * n))
            {
                throw Be.Truncated();
            }
        }
        else
        {
            throw new FontFormatException("Unknown FDSelect format.");
        }
    }

    private static FontDict ReadPrivate(ReadOnlySpan<byte> d, Dictionary<int, double[]> dict)
    {
        var fd = new FontDict();
        if (!dict.TryGetValue(18, out double[]? priv) || priv.Length < 2)
        {
            return fd;
        }

        int size = ToOffset(priv[0]);
        int off = ToOffset(priv[1]);
        ReadOnlySpan<byte> pd = Be.Slice(d, off, size);
        Dictionary<int, double[]> p = ParseDict(pd);
        if (p.TryGetValue(20, out double[]? dw) && dw.Length > 0)
        {
            fd.DefaultWidth = (float)dw[0];
        }

        if (p.TryGetValue(21, out double[]? nw) && nw.Length > 0)
        {
            fd.NominalWidth = (float)nw[0];
        }

        if (p.TryGetValue(19, out double[]? sub) && sub.Length > 0)
        {
            fd.LocalSubrs = Index.Read(d, checked(off + ToOffset(sub[0])), out _);
        }

        return fd;
    }

    private static int ToOffset(double v)
    {
        if (!(v >= 0 && v <= int.MaxValue))
        {
            throw new FontFormatException("A CFF offset is out of range.");
        }

        return (int)v;
    }

    // DICT data (TN 5176 section 4): operands then an operator. Keys are the operator, with escaped ones as 0x0C00 | second byte.
    private static Dictionary<int, double[]> ParseDict(ReadOnlySpan<byte> d)
    {
        var result = new Dictionary<int, double[]>();
        var operands = new List<double>(8);
        int p = 0;
        while (p < d.Length)
        {
            int b = d[p++];
            if (b <= 21)
            {
                int op = b;
                if (b == 12)
                {
                    op = 0x0C00 | Be.U8(d, p++);
                }

                result[op] = [.. operands];
                operands.Clear();
            }
            else if (b == 28)
            {
                operands.Add(Be.I16(d, p));
                p += 2;
            }
            else if (b == 29)
            {
                operands.Add(Be.I32(d, p));
                p += 4;
            }
            else if (b == 30)
            {
                operands.Add(ReadReal(d, ref p));
            }
            else if (b >= 32 && b <= 246)
            {
                operands.Add(b - 139);
            }
            else if (b >= 247 && b <= 250)
            {
                operands.Add(((b - 247) * 256) + Be.U8(d, p++) + 108);
            }
            else if (b >= 251 && b <= 254)
            {
                operands.Add((-(b - 251) * 256) - Be.U8(d, p++) - 108);
            }
            else
            {
                throw new FontFormatException("Invalid CFF dictionary byte.");
            }

            if (operands.Count > 64)
            {
                throw new FontFormatException("A CFF dictionary operand list is too long.");
            }
        }

        return result;
    }

    private static double ReadReal(ReadOnlySpan<byte> d, ref int p)
    {
        // Packed BCD, two nibbles per byte, ended by an 0xF nibble.
        Span<char> text = stackalloc char[48];
        int n = 0;
        while (true)
        {
            int b = Be.U8(d, p++);
            for (int k = 0; k < 2; k++)
            {
                int nib = k == 0 ? b >> 4 : b & 15;
                if (nib == 15)
                {
                    return double.TryParse(text[..n], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : 0;
                }

                string piece = nib switch
                {
                    < 10 => ((char)('0' + nib)).ToString(),
                    10 => ".",
                    11 => "E",
                    12 => "E-",
                    14 => "-",
                    _ => throw new FontFormatException("Invalid CFF real number."),
                };
                if (n + piece.Length > text.Length)
                {
                    throw new FontFormatException("A CFF real number is too long.");
                }

                piece.CopyTo(text[n..]);
                n += piece.Length;
            }
        }
    }

    private sealed class FontDict
    {
        public Index LocalSubrs;
        public float DefaultWidth;
        public float NominalWidth;
    }

    /// <summary>A CFF INDEX: validated header, offsets read on demand.</summary>
    private readonly struct Index
    {
        private readonly int _offsetsPos;
        private readonly int _offSize;
        private readonly int _dataStart;

        private Index(int count, int offSize, int offsetsPos, int dataStart)
        {
            Count = count;
            _offSize = offSize;
            _offsetsPos = offsetsPos;
            _dataStart = dataStart;
        }

        public int Count { get; }

        public static Index Read(ReadOnlySpan<byte> d, int pos, out int end)
        {
            int count = Be.U16(d, pos);
            if (count == 0)
            {
                end = pos + 2;
                return new Index(0, 1, pos + 2, pos + 2);
            }

            int offSize = Be.U8(d, pos + 2);
            if (offSize < 1 || offSize > 4)
            {
                throw new FontFormatException("Invalid CFF INDEX offset size.");
            }

            long offsetsPos = pos + 3L;
            long dataStart = offsetsPos + ((count + 1L) * offSize) - 1;
            if (dataStart > d.Length)
            {
                throw Be.Truncated();
            }

            uint last = Be.UN(d, (int)(offsetsPos + ((long)count * offSize)), offSize);
            long endPos = dataStart + last;
            if (last < 1 || endPos > d.Length)
            {
                throw Be.Truncated();
            }

            end = (int)endPos;
            return new Index(count, offSize, (int)offsetsPos, (int)dataStart);
        }

        public ReadOnlySpan<byte> Get(ReadOnlySpan<byte> d, int i)
        {
            (int s, int len) = Range(d, i);
            return d.Slice(s, len);
        }

        public ReadOnlyMemory<byte> GetMemory(ReadOnlySpan<byte> d, ReadOnlyMemory<byte> m, int i)
        {
            (int s, int len) = Range(d, i);
            return m.Slice(s, len);
        }

        private (int Start, int Length) Range(ReadOnlySpan<byte> d, int i)
        {
            if ((uint)i >= (uint)Count)
            {
                throw new FontFormatException("CFF INDEX entry out of range.");
            }

            uint a = Be.UN(d, _offsetsPos + (i * _offSize), _offSize);
            uint b = Be.UN(d, _offsetsPos + ((i + 1) * _offSize), _offSize);
            if (a < 1 || b < a || _dataStart + (long)b > d.Length)
            {
                throw new FontFormatException("CFF INDEX offsets are invalid.");
            }

            return (_dataStart + (int)a, (int)(b - a));
        }
    }

    private static int Bias(int count) => count < 1240 ? 107 : (count < 33900 ? 1131 : 32768);

    private sealed class Interpreter
    {
        private readonly CffGlyphs _font;
        private readonly FontDict _fd;
        private readonly OutlineBuilder _out;
        private readonly float[] _stack;
        private readonly float[] _transient;
        private int _sp;
        private float _x;
        private float _y;
        private int _hints;
        private bool _widthDone;
        private int _ops;

        public Interpreter(CffGlyphs font, FontDict fd)
        {
            _font = font;
            _fd = fd;
            _out = new OutlineBuilder();
            _stack = new float[MaxStack];
            _transient = new float[32];
            _sp = 0;
            _x = _y = 0;
            _hints = 0;
            _widthDone = false;
            _ops = 0;
        }

        public Outline Run(ReadOnlyMemory<byte> code)
        {
            var codes = new ReadOnlyMemory<byte>[MaxCallDepth + 1];
            var pos = new int[MaxCallDepth + 1];
            int depth = 0;
            codes[0] = code;
            ReadOnlySpan<byte> d = _font._data.Span;
            while (true)
            {
                ReadOnlySpan<byte> c = codes[depth].Span;
                if (pos[depth] >= c.Length)
                {
                    // Falling off the end of a subroutine is a return; off the end of the glyph ends it.
                    if (depth == 0)
                    {
                        break;
                    }

                    depth--;
                    continue;
                }

                if (++_ops > MaxOperators)
                {
                    throw new FontFormatException("A charstring runs too long.");
                }

                int p = pos[depth];
                int b0 = c[p++];
                if (b0 >= 32 || b0 == 28)
                {
                    float v;
                    if (b0 == 28)
                    {
                        v = Be.I16(c, p);
                        p += 2;
                    }
                    else if (b0 <= 246)
                    {
                        v = b0 - 139;
                    }
                    else if (b0 <= 250)
                    {
                        v = ((b0 - 247) * 256) + Be.U8(c, p++) + 108;
                    }
                    else if (b0 <= 254)
                    {
                        v = (-(b0 - 251) * 256) - Be.U8(c, p++) - 108;
                    }
                    else
                    {
                        v = Be.I32(c, p) / 65536f;
                        p += 4;
                    }

                    Push(v);
                    pos[depth] = p;
                    continue;
                }

                pos[depth] = p;
                switch (b0)
                {
                    case 1:
                    case 3:
                    case 18:
                    case 23:
                        Stems();
                        break;
                    case 19:
                    case 20:
                        // hintmask/cntrmask: leftover operands are an implicit vstem; the mask bytes follow inline.
                        Stems();
                        int maskBytes = (_hints + 7) / 8;
                        if (pos[depth] + maskBytes > c.Length)
                        {
                            throw Be.Truncated();
                        }

                        pos[depth] += maskBytes;
                        break;
                    case 21:
                        Width(_sp > 2);
                        RequireArgs(2);
                        Move(_stack[0], _stack[1]);
                        _sp = 0;
                        break;
                    case 22:
                        Width(_sp > 1);
                        RequireArgs(1);
                        Move(_stack[0], 0);
                        _sp = 0;
                        break;
                    case 4:
                        Width(_sp > 1);
                        RequireArgs(1);
                        Move(0, _stack[0]);
                        _sp = 0;
                        break;
                    case 5:
                        RLineTo();
                        break;
                    case 6:
                    case 7:
                        HVLineTo(b0 == 6);
                        break;
                    case 8:
                        RRCurveTo();
                        break;
                    case 24:
                        RCurveLine();
                        break;
                    case 25:
                        RLineCurve();
                        break;
                    case 26:
                        VVCurveTo();
                        break;
                    case 27:
                        HHCurveTo();
                        break;
                    case 30:
                    case 31:
                        HVCurveTo(b0 == 31);
                        break;
                    case 10:
                    case 29:
                    {
                        if (_sp < 1)
                        {
                            throw new FontFormatException("Subroutine call without an index.");
                        }

                        bool global = b0 == 29;
                        Index idx = global ? _font._globalSubrs : _fd.LocalSubrs;
                        int n = (int)_stack[--_sp] + Bias(idx.Count);
                        if (depth + 1 > MaxCallDepth)
                        {
                            throw new FontFormatException("Charstring subroutines are nested too deeply.");
                        }

                        if ((uint)n >= (uint)idx.Count)
                        {
                            throw new FontFormatException("Subroutine index out of range.");
                        }

                        depth++;
                        codes[depth] = idx.GetMemory(d, _font._data, n);
                        pos[depth] = 0;
                        break;
                    }

                    case 11:
                        if (depth == 0)
                        {
                            goto done;
                        }

                        depth--;
                        break;
                    case 14:
                        Width(_sp == 1 || _sp == 5);
                        goto done;
                    case 12:
                        Escape(c, ref pos[depth]);
                        break;
                    default:
                        // Reserved operators clear the stack, as the spec asks decoders to do.
                        _sp = 0;
                        break;
                }
            }

        done:
            return _out.Build();
        }

        private void Push(float v)
        {
            if (_sp >= MaxStack)
            {
                throw new FontFormatException("The charstring operand stack overflowed.");
            }

            _stack[_sp++] = v;
        }

        private void RequireArgs(int n)
        {
            if (_sp < n)
            {
                throw new FontFormatException("A charstring operator has too few operands.");
            }

            if (_sp > n)
            {
                // Extra operands on a move are leftovers (the width was dropped already); keep the last ones.
                Array.Copy(_stack, _sp - n, _stack, 0, n);
                _sp = n;
            }
        }

        // The first stack-clearing operator may carry the advance width as an extra leading operand; it is not needed here.
        private void Width(bool present)
        {
            if (!_widthDone)
            {
                _widthDone = true;
                if (present && _sp > 0)
                {
                    Array.Copy(_stack, 1, _stack, 0, _sp - 1);
                    _sp--;
                }
            }
        }

        private void Stems()
        {
            Width((_sp & 1) != 0);
            _hints += _sp / 2;
            _sp = 0;
        }

        private void Move(float dx, float dy)
        {
            _x += dx;
            _y += dy;
            _out.MoveTo(_x, _y);
        }

        private void EnsureOpen()
        {
            if (!_out.IsOpen)
            {
                _out.MoveTo(_x, _y);
            }
        }

        private void LineBy(float dx, float dy)
        {
            EnsureOpen();
            _x += dx;
            _y += dy;
            _out.LineTo(_x, _y);
        }

        private void CurveBy(float d1x, float d1y, float d2x, float d2y, float d3x, float d3y)
        {
            EnsureOpen();
            float c1x = _x + d1x, c1y = _y + d1y;
            float c2x = c1x + d2x, c2y = c1y + d2y;
            _x = c2x + d3x;
            _y = c2y + d3y;
            _out.CubicTo(c1x, c1y, c2x, c2y, _x, _y);
        }

        private void RLineTo()
        {
            for (int i = 0; i + 1 < _sp; i += 2)
            {
                LineBy(_stack[i], _stack[i + 1]);
            }

            _sp = 0;
        }

        private void HVLineTo(bool horizontal)
        {
            for (int i = 0; i < _sp; i++)
            {
                if (horizontal)
                {
                    LineBy(_stack[i], 0);
                }
                else
                {
                    LineBy(0, _stack[i]);
                }

                horizontal = !horizontal;
            }

            _sp = 0;
        }

        private void RRCurveTo()
        {
            for (int i = 0; i + 5 < _sp; i += 6)
            {
                CurveBy(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
            }

            _sp = 0;
        }

        private void RCurveLine()
        {
            int i = 0;
            for (; i + 7 < _sp; i += 6)
            {
                CurveBy(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
            }

            if (i + 1 < _sp)
            {
                LineBy(_stack[i], _stack[i + 1]);
            }

            _sp = 0;
        }

        private void RLineCurve()
        {
            int i = 0;
            for (; i + 7 < _sp; i += 2)
            {
                LineBy(_stack[i], _stack[i + 1]);
            }

            if (i + 5 < _sp)
            {
                CurveBy(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
            }

            _sp = 0;
        }

        private void VVCurveTo()
        {
            int i = 0;
            float dx1 = 0;
            if ((_sp & 1) != 0)
            {
                dx1 = _stack[i++];
            }

            for (; i + 3 < _sp; i += 4)
            {
                CurveBy(dx1, _stack[i], _stack[i + 1], _stack[i + 2], 0, _stack[i + 3]);
                dx1 = 0;
            }

            _sp = 0;
        }

        private void HHCurveTo()
        {
            int i = 0;
            float dy1 = 0;
            if ((_sp & 1) != 0)
            {
                dy1 = _stack[i++];
            }

            for (; i + 3 < _sp; i += 4)
            {
                CurveBy(_stack[i], dy1, _stack[i + 1], _stack[i + 2], _stack[i + 3], 0);
                dy1 = 0;
            }

            _sp = 0;
        }

        private void HVCurveTo(bool horizontal)
        {
            for (int i = 0; i + 3 < _sp; i += 4)
            {
                float extra = _sp - i == 5 ? _stack[i + 4] : 0;
                if (horizontal)
                {
                    CurveBy(_stack[i], 0, _stack[i + 1], _stack[i + 2], extra, _stack[i + 3]);
                }
                else
                {
                    CurveBy(0, _stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], extra);
                }

                horizontal = !horizontal;
            }

            _sp = 0;
        }

        private void Escape(ReadOnlySpan<byte> c, ref int pos)
        {
            ExecuteEscape(Be.U8(c, pos++));
        }

        private void ExecuteEscape(int op)
        {
            switch (op)
            {
                case 34: // hflex
                    if (_sp >= 7)
                    {
                        float y0 = _y;
                        CurveBy(_stack[0], 0, _stack[1], _stack[2], _stack[3], 0);
                        CurveBy(_stack[4], 0, _stack[5], y0 - _y, _stack[6], 0);
                    }

                    _sp = 0;
                    break;
                case 35: // flex
                    if (_sp >= 12)
                    {
                        CurveBy(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                        CurveBy(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], _stack[11]);
                    }

                    _sp = 0;
                    break;
                case 36: // hflex1
                    if (_sp >= 9)
                    {
                        float y0 = _y;
                        CurveBy(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], 0);
                        CurveBy(_stack[5], 0, _stack[6], _stack[7], _stack[8], y0 - (_y + _stack[7]));
                    }

                    _sp = 0;
                    break;
                case 37: // flex1
                    if (_sp >= 11)
                    {
                        float x0 = _x, y0 = _y;
                        float dx = _stack[0] + _stack[2] + _stack[4] + _stack[6] + _stack[8];
                        float dy = _stack[1] + _stack[3] + _stack[5] + _stack[7] + _stack[9];
                        CurveBy(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                        if (Math.Abs(dx) > Math.Abs(dy))
                        {
                            CurveBy(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], y0 - (_y + _stack[7] + _stack[9]));
                        }
                        else
                        {
                            CurveBy(_stack[6], _stack[7], _stack[8], _stack[9], x0 - (_x + _stack[6] + _stack[8]), _stack[10]);
                        }
                    }

                    _sp = 0;
                    break;
                default:
                    Arithmetic(op);
                    break;
            }
        }

        private void Arithmetic(int op)
        {
            switch (op)
            {
                case 3: // and
                    Need(2);
                    _stack[_sp - 2] = _stack[_sp - 2] != 0 && _stack[_sp - 1] != 0 ? 1 : 0;
                    _sp--;
                    break;
                case 4: // or
                    Need(2);
                    _stack[_sp - 2] = _stack[_sp - 2] != 0 || _stack[_sp - 1] != 0 ? 1 : 0;
                    _sp--;
                    break;
                case 5: // not
                    Need(1);
                    _stack[_sp - 1] = _stack[_sp - 1] == 0 ? 1 : 0;
                    break;
                case 9: // abs
                    Need(1);
                    _stack[_sp - 1] = Math.Abs(_stack[_sp - 1]);
                    break;
                case 10: // add
                    Need(2);
                    _stack[_sp - 2] += _stack[_sp - 1];
                    _sp--;
                    break;
                case 11: // sub
                    Need(2);
                    _stack[_sp - 2] -= _stack[_sp - 1];
                    _sp--;
                    break;
                case 12: // div
                    Need(2);
                    _stack[_sp - 2] = _stack[_sp - 1] == 0 ? 0 : _stack[_sp - 2] / _stack[_sp - 1];
                    _sp--;
                    break;
                case 14: // neg
                    Need(1);
                    _stack[_sp - 1] = -_stack[_sp - 1];
                    break;
                case 15: // eq
                    Need(2);
                    _stack[_sp - 2] = _stack[_sp - 2] == _stack[_sp - 1] ? 1 : 0;
                    _sp--;
                    break;
                case 18: // drop
                    Need(1);
                    _sp--;
                    break;
                case 20: // put
                {
                    Need(2);
                    int i = (int)_stack[_sp - 1];
                    if ((uint)i < (uint)_transient.Length)
                    {
                        _transient[i] = _stack[_sp - 2];
                    }

                    _sp -= 2;
                    break;
                }

                case 21: // get
                {
                    Need(1);
                    int i = (int)_stack[_sp - 1];
                    _stack[_sp - 1] = (uint)i < (uint)_transient.Length ? _transient[i] : 0;
                    break;
                }

                case 22: // ifelse
                    Need(4);
                    _stack[_sp - 4] = _stack[_sp - 2] <= _stack[_sp - 1] ? _stack[_sp - 4] : _stack[_sp - 3];
                    _sp -= 3;
                    break;
                case 23: // random: deterministic by design
                    Push(0.5f);
                    break;
                case 24: // mul
                    Need(2);
                    _stack[_sp - 2] *= _stack[_sp - 1];
                    _sp--;
                    break;
                case 26: // sqrt
                    Need(1);
                    _stack[_sp - 1] = MathF.Sqrt(Math.Max(0, _stack[_sp - 1]));
                    break;
                case 27: // dup
                    Need(1);
                    Push(_stack[_sp - 1]);
                    break;
                case 28: // exch
                {
                    Need(2);
                    (_stack[_sp - 1], _stack[_sp - 2]) = (_stack[_sp - 2], _stack[_sp - 1]);
                    break;
                }

                case 29: // index
                {
                    Need(1);
                    int i = (int)_stack[_sp - 1];
                    _sp--;
                    if (i < 0)
                    {
                        i = 0;
                    }

                    Need(i + 1);
                    Push(_stack[_sp - 1 - i]);
                    break;
                }

                case 30: // roll
                {
                    Need(2);
                    int j = (int)_stack[_sp - 1];
                    int n = (int)_stack[_sp - 2];
                    _sp -= 2;
                    if (n < 0 || n > _sp)
                    {
                        throw new FontFormatException("Invalid roll in a charstring.");
                    }

                    if (n > 0)
                    {
                        j = ((j % n) + n) % n;
                        Span<float> seg = _stack.AsSpan(_sp - n, n);
                        Span<float> tmp = stackalloc float[n];
                        for (int k = 0; k < n; k++)
                        {
                            tmp[(k + j) % n] = seg[k];
                        }

                        tmp.CopyTo(seg);
                    }

                    break;
                }

                default:
                    _sp = 0;
                    break;
            }
        }

        private void Need(int n)
        {
            if (_sp < n)
            {
                throw new FontFormatException("A charstring operator has too few operands.");
            }
        }
    }
}
