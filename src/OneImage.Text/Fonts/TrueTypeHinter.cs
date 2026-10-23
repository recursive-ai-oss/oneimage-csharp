namespace OneImage.Text.Fonts;

/// <summary>Raised inside the interpreter when a program cannot go on (a limit is hit or the font's code does something invalid). Never leaves the hinter.</summary>
internal sealed class HintAbortException : Exception
{
    public HintAbortException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The TrueType bytecode interpreter (the instruction set of the OpenType specification's "TrueType instruction set" chapter) in safe
/// code, run in the backward compatible mode that DirectWrite-style rasterizers use: movements along x are ignored, after both IUP
/// instructions have run no point moves again, and GETINFO reports interpreter version 40 with grayscale-and-subpixel positioning
/// flags. Everything here is untrusted font code, so it runs under hard budgets: instructions, stack depth, call depth, loop
/// counts, storage, CVT, twilight points and functions. Any violation aborts the program; the caller then draws the glyph unhinted.
/// </summary>
internal sealed class TrueTypeHinter
{
    private const int MaxInstructions = 1 << 20;
    private const int MaxGlyphInstructions = 1 << 18;
    private const int MaxCallDepth = 32;
    private const int MaxStack = 1 << 14;
    private const int MaxStorage = 1 << 14;
    private const int MaxFunctions = 1 << 14;
    private const int MaxTwilight = 1 << 14;
    private const int MaxCvt = 1 << 15;
    private const int MaxPoints = 1 << 14;

    private const byte OnCurve = 1;
    private const byte TouchX = 2;
    private const byte TouchY = 4;

    private readonly int _upem;
    private readonly int _scale;      // 16.16: 26.6 pixels per font unit
    private readonly int _ppem;
    private readonly byte[][] _codes; // 0 fpgm, 1 prep; the glyph program (2) belongs to each run
    private readonly Function[] _functions;
    private readonly Function[] _idefs;
    private readonly int[] _cvt;
    private readonly int[] _storage;
    private readonly int _maxStack;
    private readonly int _twilightCount;
    private GraphicsState _defaults;
    private bool _usable;

    private struct Function
    {
        public int Code;
        public int Start;
        public int End;
        public bool Defined;
    }

    private struct GraphicsState
    {
        public int Rp0, Rp1, Rp2;
        public short PvX, PvY, FvX, FvY, DvX, DvY;
        public int Loop;
        public int MinDist;
        public int Period, Phase, Threshold;
        public bool RoundOff;
        public bool AutoFlip;
        public int CvtCutIn;
        public int SwCutIn;
        public int SwValue;
        public int DeltaBase;
        public int DeltaShift;
        public int InstructControl;
        public int Zp0, Zp1, Zp2;
    }

    private sealed class Zone
    {
        public int[] OrgX = [], OrgY = [], CurX = [], CurY = [], OrusX = [], OrusY = [];
        public byte[] Tags = [];
        public int[] Ends = [];
        public int Count;
        public int Contours;

        public Zone(int n)
        {
            OrgX = new int[n];
            OrgY = new int[n];
            CurX = new int[n];
            CurY = new int[n];
            OrusX = new int[n];
            OrusY = new int[n];
            Tags = new byte[n];
            Count = n;
        }
    }

    private TrueTypeHinter(int upem, double pixelSize, ReadOnlyMemory<byte> fpgm, ReadOnlyMemory<byte> prep, ReadOnlyMemory<byte> cvt, int maxStorage, int maxFunctions, int maxStack, int maxTwilight)
    {
        _upem = upem;
        _ppem = Math.Max(1, (int)Math.Round(pixelSize));
        _scale = (int)Math.Round(pixelSize * 64.0 * 65536.0 / upem);
        _codes = [fpgm.ToArray(), prep.ToArray()];
        _functions = new Function[Math.Clamp(maxFunctions, 0, MaxFunctions) + 1];
        _idefs = new Function[256];
        _maxStack = Math.Clamp(maxStack, 0, MaxStack) + 32;
        _twilightCount = Math.Clamp(maxTwilight, 0, MaxTwilight) + 4;
        _storage = new int[Math.Clamp(maxStorage, 0, MaxStorage)];
        int n = Math.Min(cvt.Length / 2, MaxCvt);
        _cvt = new int[n];
        ReadOnlySpan<byte> c = cvt.Span;
        for (int i = 0; i < n; i++)
        {
            _cvt[i] = MulFix((short)Be.U16(c, 2 * i), _scale);
        }
    }

    /// <summary>
    /// Prepares hinting at a pixel size: runs the font program and the control value program. Null when the font has no usable
    /// programs, the size is out of range, or either program fails (the font is then drawn unhinted at that size).
    /// </summary>
    public static TrueTypeHinter? Create(int unitsPerEm, double pixelSize, ReadOnlyMemory<byte> fpgm, ReadOnlyMemory<byte> prep, ReadOnlyMemory<byte> cvt, ReadOnlyMemory<byte> maxp)
    {
        if (!(pixelSize >= 4 && pixelSize <= 400) || maxp.Length < 32 || (fpgm.Length == 0 && prep.Length == 0))
        {
            return null;
        }

        var h = new TrueTypeHinter(unitsPerEm, pixelSize, fpgm, prep, cvt, Be.U16(maxp.Span, 18), Be.U16(maxp.Span, 20), Be.U16(maxp.Span, 24), Be.U16(maxp.Span, 16));
        try
        {
            var ctx = new Context(h, h._storage, h._cvt);
            ctx.Reset();
            if (h._codes[0].Length > 0)
            {
                ctx.Run(0, MaxInstructions);
            }

            ctx.Reset();
            ctx.ProgramIsPrep = true;
            if (h._codes[1].Length > 0)
            {
                ctx.Run(1, MaxInstructions);
            }

            // What the control value program leaves in the CVT and storage, and in the graphics state variables that persist, is
            // the starting point of every glyph program.
            h._defaults = ctx.Gs;
            h._defaultsStored = true;
            h._usable = (ctx.Gs.InstructControl & 1) == 0;
        }
        catch (HintAbortException)
        {
            return null;
        }

        return h._usable ? h : null;
    }

    private bool _defaultsStored;

    /// <summary>
    /// Hints one simple glyph in place. <paramref name="x"/> and <paramref name="y"/> hold font units on entry and, on success, the
    /// grid-fitted outline in the same units (so the caller's usual scale gives pixels). Returns false, leaving the arrays alone,
    /// when the glyph has no instructions or its program fails.
    /// </summary>
    public bool TryHint(float[] x, float[] y, bool[] onCurve, int first, int count, ReadOnlySpan<int> contourEnds, ReadOnlySpan<byte> instructions)
    {
        if (instructions.Length == 0 || count <= 0 || count > MaxPoints || !_defaultsStored)
        {
            return false;
        }

        var glyph = new Zone(count + 4);
        for (int i = 0; i < count; i++)
        {
            int fx = (int)x[first + i];
            int fy = (int)y[first + i];
            glyph.OrusX[i] = fx;
            glyph.OrusY[i] = fy;
            glyph.OrgX[i] = glyph.CurX[i] = MulFix(fx, _scale);
            glyph.OrgY[i] = glyph.CurY[i] = MulFix(fy, _scale);
            glyph.Tags[i] = onCurve[first + i] ? OnCurve : (byte)0;
        }

        glyph.Ends = contourEnds.ToArray();
        glyph.Contours = glyph.Ends.Length;
        glyph.Count = count + 4;
        var ctx = new Context(this, (int[])_storage.Clone(), (int[])_cvt.Clone());
        try
        {
            ctx.Glyph = glyph;
            ctx.Reset();

            // A glyph program starts from the default graphics state, except for the variables the control value program may set.
            ctx.Gs.AutoFlip = _defaults.AutoFlip;
            ctx.Gs.CvtCutIn = _defaults.CvtCutIn;
            ctx.Gs.DeltaBase = _defaults.DeltaBase;
            ctx.Gs.DeltaShift = _defaults.DeltaShift;
            ctx.Gs.InstructControl = _defaults.InstructControl;
            ctx.Gs.MinDist = _defaults.MinDist;
            ctx.Gs.SwCutIn = _defaults.SwCutIn;
            ctx.Gs.SwValue = _defaults.SwValue;
            ctx.BackwardCompatible = (ctx.Gs.InstructControl & 4) == 0;
            ctx.RunGlyph(MaxGlyphInstructions, instructions.ToArray());
        }
        catch (HintAbortException)
        {
            return false;
        }

        float back = 65536f / _scale;
        for (int i = 0; i < count; i++)
        {
            if (glyph.CurY[i] != glyph.OrgY[i])
            {
                y[first + i] = glyph.CurY[i] * back;
            }

            if (glyph.CurX[i] != glyph.OrgX[i])
            {
                x[first + i] = glyph.CurX[i] * back;
            }
        }

        return true;
    }

    // ---- fixed point helpers ----

    private static int MulFix(int a, int b)
    {
        long t = (long)a * b;
        return (int)((t + 0x8000 + (t >> 63)) >> 16);
    }

    private static int Mul14(int a, int b)
    {
        long t = (long)a * b;
        return (int)((t + 0x2000 + (t >> 63)) >> 14);
    }

    private static int MulDiv(int a, int b, int c)
    {
        if (c == 0)
        {
            return a == 0 || b == 0 ? 0 : int.MaxValue;
        }

        long t = (long)a * b;
        long d = c;
        bool neg = (t < 0) != (d < 0);
        t = Math.Abs(t);
        d = Math.Abs(d);
        long r = (t + (d / 2)) / d;
        r = neg ? -r : r;
        return (int)Math.Clamp(r, int.MinValue, int.MaxValue);
    }

    private static int MulDivNoRound(int a, int b, int c)
    {
        if (c == 0)
        {
            return a == 0 || b == 0 ? 0 : int.MaxValue;
        }

        long t = (long)a * b;
        long d = c;
        bool neg = (t < 0) != (d < 0);
        t = Math.Abs(t);
        d = Math.Abs(d);
        long r = t / d;
        r = neg ? -r : r;
        return (int)Math.Clamp(r, int.MinValue, int.MaxValue);
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    // ---- the interpreter ----

    private sealed class Context(TrueTypeHinter h, int[] storage, int[] cvt)
    {
        private readonly TrueTypeHinter _h = h;
        private byte[] _glyphCode = [];
        private int[] _stack = [];
        private int _sp;
        private Frame[] _frames = new Frame[MaxCallDepth];
        private int _fp;
        private int _steps;
        private int _budget;
        private byte[] _code = [];
        private int _codeId;
        private int _ip;
        private Zone _twilight = null!;
        private Zone _zp0 = null!, _zp1 = null!, _zp2 = null!;
        private bool _iupX, _iupY;

        public int[] Storage = storage;
        public int[] Cvt = cvt;
        public GraphicsState Gs;
        public Zone? Glyph;
        public bool BackwardCompatible = true;
        public bool ProgramIsPrep;

        private struct Frame
        {
            public int Code;
            public int RetIp;
            public int Count;
            public int Start;
            public int FuncCode;
        }

        public void Reset()
        {
            _stack = new int[_h._maxStack];
            _sp = 0;
            _fp = 0;
            _steps = 0;
            _twilight = new Zone(_h._twilightCount);
            _iupX = _iupY = false;
            Gs = new GraphicsState
            {
                PvX = 0x4000,
                FvX = 0x4000,
                DvX = 0x4000,
                Loop = 1,
                MinDist = 64,
                Period = 64,
                Threshold = 32,
                AutoFlip = true,
                CvtCutIn = 68,
                SwCutIn = 0,
                DeltaBase = 9,
                DeltaShift = 3,
                Zp0 = 1,
                Zp1 = 1,
                Zp2 = 1,
            };
            if (Glyph is null)
            {
                // No glyph zone during setup programs: the glyph zone selector then refers to an empty zone.
                Glyph = new Zone(4);
            }

            SelectZones();
        }

        public void RunGlyph(int budget, byte[] code)
        {
            // Glyph programs may not define functions: the function table is shared and read-only here.
            _glyphCode = code;
            _fp = 0;
            _steps = 0;
            Execute(2, budget);
        }

        public void Run(int codeId, int budget) => Execute(codeId, budget);

        private static HintAbortException Abort(string why) => new(why);

        private byte[] CodeOf(int id) => id == 2 ? _glyphCode : _h._codes[id];

        private void SelectZones()
        {
            _zp0 = Gs.Zp0 == 0 ? _twilight : Glyph!;
            _zp1 = Gs.Zp1 == 0 ? _twilight : Glyph!;
            _zp2 = Gs.Zp2 == 0 ? _twilight : Glyph!;
        }

        private void Push(int v)
        {
            if (_sp >= _stack.Length)
            {
                throw Abort("stack overflow");
            }

            _stack[_sp++] = v;
        }

        private int Pop()
        {
            if (_sp <= 0)
            {
                throw Abort("stack underflow");
            }

            return _stack[--_sp];
        }

        private void Need(int n)
        {
            if (_sp < n)
            {
                throw Abort("stack underflow");
            }
        }

        private static int Pt(Zone z, int p)
        {
            if ((uint)p >= (uint)z.Count)
            {
                throw Abort("point out of range");
            }

            return p;
        }

        private int Project(int dx, int dy) => Mul14(dx, Gs.PvX) + Mul14(dy, Gs.PvY);

        private int DualProject(int dx, int dy) => Mul14(dx, Gs.DvX) + Mul14(dy, Gs.DvY);

        private int FdotP()
        {
            int d = ((Gs.FvX * Gs.PvX) + (Gs.FvY * Gs.PvY)) >> 14;
            return Math.Abs(d) < 0x400 ? 0x4000 : d;
        }

        private bool YMoves => !(BackwardCompatible && _iupX && _iupY);

        private void MoveDelta(Zone z, int p, int dx, int dy, bool touch)
        {
            if (Gs.FvX != 0)
            {
                if (!BackwardCompatible)
                {
                    z.CurX[p] += dx;
                }

                if (touch)
                {
                    z.Tags[p] |= TouchX;
                }
            }

            if (Gs.FvY != 0)
            {
                if (YMoves)
                {
                    z.CurY[p] += dy;
                }

                if (touch)
                {
                    z.Tags[p] |= TouchY;
                }
            }
        }

        // Moves a point along the freedom vector so that its projection changes by the distance.
        private void Move(Zone z, int p, int distance)
        {
            int fp = FdotP();
            MoveDelta(z, p, MulDiv(distance, Gs.FvX, fp), MulDiv(distance, Gs.FvY, fp), true);
        }

        private void MoveOrig(Zone z, int p, int distance)
        {
            int fp = FdotP();
            z.OrgX[p] += MulDiv(distance, Gs.FvX, fp);
            z.OrgY[p] += MulDiv(distance, Gs.FvY, fp);
        }

        private int Round(int d)
        {
            if (Gs.RoundOff)
            {
                return d;
            }

            int period = Gs.Period;
            int phase = Gs.Phase;
            int thr = Gs.Threshold;
            if (d >= 0)
            {
                int v = (FloorDiv(d - phase + thr, period) * period) + phase;
                return v < 0 ? phase : v;
            }

            int w = -(FloorDiv(thr - phase - d, period) * period) - phase;
            return w > 0 ? -phase : w;
        }

        private static void Normalize(int a, int b, out short x, out short y)
        {
            double len = Math.Sqrt(((double)a * a) + ((double)b * b));
            if (len == 0)
            {
                x = 0x4000;
                y = 0;
                return;
            }

            x = (short)Math.Round(a / len * 16384.0);
            y = (short)Math.Round(b / len * 16384.0);
        }

        // Opcode lengths, for skipping code.
        private static int OpLen(byte[] c, int ip)
        {
            byte op = c[ip];
            int len;
            if (op == 0x40)
            {
                len = ip + 1 < c.Length ? 2 + c[ip + 1] : int.MaxValue;
            }
            else if (op == 0x41)
            {
                len = ip + 1 < c.Length ? 2 + (2 * c[ip + 1]) : int.MaxValue;
            }
            else if (op >= 0xB0 && op <= 0xB7)
            {
                len = 2 + (op - 0xB0);
            }
            else if (op >= 0xB8 && op <= 0xBF)
            {
                len = 1 + (2 * (op - 0xB8 + 1));
            }
            else
            {
                len = 1;
            }

            if (len > c.Length - ip)
            {
                throw Abort("instruction runs past the program");
            }

            return len;
        }

        private void SkipTo(bool stopAtElse)
        {
            // Skips from just after an IF (or ELSE) to its ELSE or EIF, counting nested IFs.
            int depth = 1;
            while (true)
            {
                _ip += OpLen(_code, _ip);
                if (_ip >= _code.Length)
                {
                    throw Abort("unterminated IF");
                }

                byte op = _code[_ip];
                if (op == 0x58)
                {
                    depth++;
                }
                else if (op == 0x1B && depth == 1 && stopAtElse)
                {
                    return;
                }
                else if (op == 0x59)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return;
                    }
                }

                if (++_steps > _budget)
                {
                    throw Abort("instruction budget");
                }
            }
        }

        private void Execute(int codeId, int budget)
        {
            _budget = budget;
            _codeId = codeId;
            _code = CodeOf(codeId);
            _ip = 0;
            _fp = 0;
            while (true)
            {
                if (_ip >= _code.Length)
                {
                    if (_fp == 0)
                    {
                        return;
                    }

                    throw Abort("function ran off the end of its program");
                }

                if (++_steps > _budget)
                {
                    throw Abort("instruction budget");
                }

                byte op = _code[_ip];
                int len = OpLen(_code, _ip);
                int next = _ip + len;
                Step(op, ref next);
                _ip = next;
            }
        }

        private void Step(byte op, ref int next)
        {
            switch (op)
            {
                case 0x00:
                case 0x01:
                    SetAxis(op & 1, true, true, true);
                    break;
                case 0x02:
                case 0x03:
                    SetAxis(op & 1, true, false, true);
                    break;
                case 0x04:
                case 0x05:
                    SetAxis(op & 1, false, true, false);
                    break;
                case 0x06:
                case 0x07:
                    LineVector(op, projection: true, dual: false);
                    break;
                case 0x08:
                case 0x09:
                    LineVector(op, projection: false, dual: false);
                    break;
                case 0x0A:
                {
                    int y = (short)Pop();
                    int x = (short)Pop();
                    Normalize(x, y, out Gs.PvX, out Gs.PvY);
                    Gs.DvX = Gs.PvX;
                    Gs.DvY = Gs.PvY;
                    break;
                }

                case 0x0B:
                {
                    int y = (short)Pop();
                    int x = (short)Pop();
                    Normalize(x, y, out Gs.FvX, out Gs.FvY);
                    break;
                }

                case 0x0C:
                    Push(Gs.PvX);
                    Push(Gs.PvY);
                    break;
                case 0x0D:
                    Push(Gs.FvX);
                    Push(Gs.FvY);
                    break;
                case 0x0E:
                    Gs.FvX = Gs.PvX;
                    Gs.FvY = Gs.PvY;
                    break;
                case 0x0F:
                    Isect();
                    break;
                case 0x10:
                    Gs.Rp0 = Pop();
                    break;
                case 0x11:
                    Gs.Rp1 = Pop();
                    break;
                case 0x12:
                    Gs.Rp2 = Pop();
                    break;
                case 0x13:
                    Gs.Zp0 = ZoneId(Pop());
                    SelectZones();
                    break;
                case 0x14:
                    Gs.Zp1 = ZoneId(Pop());
                    SelectZones();
                    break;
                case 0x15:
                    Gs.Zp2 = ZoneId(Pop());
                    SelectZones();
                    break;
                case 0x16:
                    Gs.Zp0 = Gs.Zp1 = Gs.Zp2 = ZoneId(Pop());
                    SelectZones();
                    break;
                case 0x17:
                {
                    int n = Pop();
                    if (n < 0 || n > 0xFFFF)
                    {
                        throw Abort("bad loop count");
                    }

                    Gs.Loop = n;
                    break;
                }

                case 0x18:
                    SetRound(64, 0, 32);
                    break;
                case 0x19:
                    SetRound(64, 32, 32);
                    break;
                case 0x1A:
                    Gs.MinDist = Pop();
                    break;
                case 0x1B:
                    // ELSE reached while running the true branch: skip to the matching EIF.
                    SkipTo(false);
                    next = _ip + 1;
                    break;
                case 0x1C:
                    Jump(ref next, Pop());
                    break;
                case 0x1D:
                    Gs.CvtCutIn = Pop();
                    break;
                case 0x1E:
                    Gs.SwCutIn = Pop();
                    break;
                case 0x1F:
                    Gs.SwValue = MulFix(Pop(), _h._scale);
                    break;
                case 0x20:
                    Need(1);
                    Push(_stack[_sp - 1]);
                    break;
                case 0x21:
                    Pop();
                    break;
                case 0x22:
                    _sp = 0;
                    break;
                case 0x23:
                {
                    Need(2);
                    (_stack[_sp - 1], _stack[_sp - 2]) = (_stack[_sp - 2], _stack[_sp - 1]);
                    break;
                }

                case 0x24:
                    Push(_sp);
                    break;
                case 0x25:
                {
                    int k = Pop();
                    if (k <= 0 || k > _sp)
                    {
                        throw Abort("bad CINDEX");
                    }

                    Push(_stack[_sp - k]);
                    break;
                }

                case 0x26:
                {
                    int k = Pop();
                    if (k <= 0 || k > _sp)
                    {
                        throw Abort("bad MINDEX");
                    }

                    int v = _stack[_sp - k];
                    Array.Copy(_stack, _sp - k + 1, _stack, _sp - k, k - 1);
                    _stack[_sp - 1] = v;
                    break;
                }

                case 0x27:
                {
                    int p2 = Pop();
                    int p1 = Pop();
                    Pt(_zp1, p1);
                    Pt(_zp0, p2);
                    int d = Project(_zp0.CurX[p2] - _zp1.CurX[p1], _zp0.CurY[p2] - _zp1.CurY[p1]) / 2;
                    Move(_zp1, p1, d);
                    Move(_zp0, p2, -d);
                    break;
                }

                case 0x29:
                {
                    int p = Pt(_zp0, Pop());
                    if (Gs.FvX != 0)
                    {
                        _zp0.Tags[p] &= unchecked((byte)~TouchX);
                    }

                    if (Gs.FvY != 0)
                    {
                        _zp0.Tags[p] &= unchecked((byte)~TouchY);
                    }

                    break;
                }

                case 0x2A:
                case 0x2B:
                    Call(op == 0x2A, ref next);
                    break;
                case 0x2C:
                    DefineFunction(ref next, instruction: false);
                    break;
                case 0x2D:
                    EndFunction(ref next);
                    break;
                case 0x2E:
                case 0x2F:
                    Mdap(op);
                    break;
                case 0x30:
                case 0x31:
                    Iup(op);
                    break;
                case 0x32:
                case 0x33:
                    Shp(op);
                    break;
                case 0x34:
                case 0x35:
                    Shc(op);
                    break;
                case 0x36:
                case 0x37:
                    Shz(op);
                    break;
                case 0x38:
                    Shpix();
                    break;
                case 0x39:
                    Ip();
                    break;
                case 0x3A:
                case 0x3B:
                    Msirp(op);
                    break;
                case 0x3C:
                    AlignRp();
                    break;
                case 0x3D:
                    SetRound(32, 0, 16);
                    break;
                case 0x3E:
                case 0x3F:
                    Miap(op);
                    break;
                case 0x40:
                {
                    int n = _code[_ip + 1];
                    for (int i = 0; i < n; i++)
                    {
                        Push(_code[_ip + 2 + i]);
                    }

                    break;
                }

                case 0x41:
                {
                    int n = _code[_ip + 1];
                    for (int i = 0; i < n; i++)
                    {
                        Push((short)((_code[_ip + 2 + (2 * i)] << 8) | _code[_ip + 3 + (2 * i)]));
                    }

                    break;
                }

                case 0x42:
                {
                    int v = Pop();
                    int i = Pop();
                    if ((uint)i >= (uint)Storage.Length)
                    {
                        throw Abort("storage index");
                    }

                    Storage[i] = v;
                    break;
                }

                case 0x43:
                {
                    int i = Pop();
                    if ((uint)i >= (uint)Storage.Length)
                    {
                        throw Abort("storage index");
                    }

                    Push(Storage[i]);
                    break;
                }

                case 0x44:
                {
                    int v = Pop();
                    SetCvt(Pop(), v);
                    break;
                }

                case 0x45:
                    Push(GetCvt(Pop()));
                    break;
                case 0x46:
                case 0x47:
                {
                    int p = Pt(_zp2, Pop());
                    Push(op == 0x46 ? Project(_zp2.CurX[p], _zp2.CurY[p]) : DualProject(_zp2.OrgX[p], _zp2.OrgY[p]));
                    break;
                }

                case 0x48:
                {
                    int v = Pop();
                    int p = Pt(_zp2, Pop());
                    int k = Project(_zp2.CurX[p], _zp2.CurY[p]);
                    Move(_zp2, p, v - k);
                    if (Gs.Zp2 == 0)
                    {
                        _zp2.OrgX[p] = _zp2.CurX[p];
                        _zp2.OrgY[p] = _zp2.CurY[p];
                    }

                    break;
                }

                case 0x49:
                case 0x4A:
                    Md(op);
                    break;
                case 0x4B:
                case 0x4C:
                    Push(_h._ppem);
                    break;
                case 0x4D:
                    Gs.AutoFlip = true;
                    break;
                case 0x4E:
                    Gs.AutoFlip = false;
                    break;
                case 0x4F:
                    Pop();
                    break;
                case 0x50:
                {
                    int b = Pop();
                    Push(Pop() < b ? 1 : 0);
                    break;
                }

                case 0x51:
                {
                    int b = Pop();
                    Push(Pop() <= b ? 1 : 0);
                    break;
                }

                case 0x52:
                {
                    int b = Pop();
                    Push(Pop() > b ? 1 : 0);
                    break;
                }

                case 0x53:
                {
                    int b = Pop();
                    Push(Pop() >= b ? 1 : 0);
                    break;
                }

                case 0x54:
                {
                    int b = Pop();
                    Push(Pop() == b ? 1 : 0);
                    break;
                }

                case 0x55:
                {
                    int b = Pop();
                    Push(Pop() != b ? 1 : 0);
                    break;
                }

                case 0x56:
                {
                    int saved = Round(Pop());
                    Push((saved & 127) == 64 ? 1 : 0);
                    break;
                }

                case 0x57:
                {
                    int saved = Round(Pop());
                    Push((saved & 127) == 0 ? 1 : 0);
                    break;
                }

                case 0x58:
                    if (Pop() == 0)
                    {
                        SkipTo(true);
                        next = _ip + 1;
                    }

                    break;
                case 0x59:
                    break;
                case 0x5A:
                {
                    int b = Pop();
                    Push(Pop() != 0 && b != 0 ? 1 : 0);
                    break;
                }

                case 0x5B:
                {
                    int b = Pop();
                    Push(Pop() != 0 || b != 0 ? 1 : 0);
                    break;
                }

                case 0x5C:
                    Push(Pop() == 0 ? 1 : 0);
                    break;
                case 0x5D:
                    DeltaP(0);
                    break;
                case 0x5E:
                {
                    int v = Pop();
                    if (v < 0 || v > 0xFFFF)
                    {
                        throw Abort("bad delta base");
                    }

                    Gs.DeltaBase = v;
                    break;
                }

                case 0x5F:
                {
                    int v = Pop();
                    if (v < 0 || v > 6)
                    {
                        throw Abort("bad delta shift");
                    }

                    Gs.DeltaShift = v;
                    break;
                }

                case 0x60:
                {
                    int b = Pop();
                    Push(unchecked(Pop() + b));
                    break;
                }

                case 0x61:
                {
                    int b = Pop();
                    Push(unchecked(Pop() - b));
                    break;
                }

                case 0x62:
                {
                    int b = Pop();
                    if (b == 0)
                    {
                        throw Abort("division by zero");
                    }

                    Push(MulDivNoRound(Pop(), 64, b));
                    break;
                }

                case 0x63:
                {
                    int b = Pop();
                    Push(MulDiv(Pop(), b, 64));
                    break;
                }

                case 0x64:
                    Push(Math.Abs(Pop()));
                    break;
                case 0x65:
                    Push(unchecked(-Pop()));
                    break;
                case 0x66:
                    Push(Pop() & -64);
                    break;
                case 0x67:
                    Push(unchecked(Pop() + 63) & -64);
                    break;
                case 0x68:
                case 0x69:
                case 0x6A:
                case 0x6B:
                    Push(Round(Pop()));
                    break;
                case 0x6C:
                case 0x6D:
                case 0x6E:
                case 0x6F:
                    break;
                case 0x70:
                {
                    int v = Pop();
                    SetCvt(Pop(), MulFix(v, _h._scale));
                    break;
                }

                case 0x71:
                    DeltaP(16);
                    break;
                case 0x72:
                    DeltaP(32);
                    break;
                case 0x73:
                    DeltaC(0);
                    break;
                case 0x74:
                    DeltaC(16);
                    break;
                case 0x75:
                    DeltaC(32);
                    break;
                case 0x76:
                    SRound(false);
                    break;
                case 0x77:
                    SRound(true);
                    break;
                case 0x78:
                {
                    int cond = Pop();
                    int off = Pop();
                    if (cond != 0)
                    {
                        Jump(ref next, off);
                    }

                    break;
                }

                case 0x79:
                {
                    int cond = Pop();
                    int off = Pop();
                    if (cond == 0)
                    {
                        Jump(ref next, off);
                    }

                    break;
                }

                case 0x7A:
                    Gs.RoundOff = true;
                    break;
                case 0x7C:
                    SetRound(64, 0, 63);
                    break;
                case 0x7D:
                    SetRound(64, 0, 0);
                    break;
                case 0x7E:
                case 0x7F:
                    Pop();
                    break;
                case 0x80:
                {
                    int n = LoopCount();
                    for (int i = 0; i < n; i++)
                    {
                        int p = Pt(Glyph!, Pop());
                        Glyph!.Tags[p] ^= OnCurve;
                    }

                    break;
                }

                case 0x81:
                case 0x82:
                {
                    int hi = Pop();
                    int lo = Pop();
                    if (lo < 0 || hi >= Glyph!.Count || lo > hi)
                    {
                        throw Abort("bad point range");
                    }

                    for (int p = lo; p <= hi; p++)
                    {
                        if (op == 0x81)
                        {
                            Glyph.Tags[p] |= OnCurve;
                        }
                        else
                        {
                            Glyph.Tags[p] &= unchecked((byte)~OnCurve);
                        }
                    }

                    break;
                }

                case 0x85:
                case 0x8D:
                    Pop();
                    break;
                case 0x86:
                case 0x87:
                    LineVector(op, projection: true, dual: true);
                    break;
                case 0x88:
                    GetInfo();
                    break;
                case 0x89:
                    DefineFunction(ref next, instruction: true);
                    break;
                case 0x8A:
                {
                    Need(3);
                    int a = _stack[_sp - 3];
                    _stack[_sp - 3] = _stack[_sp - 2];
                    _stack[_sp - 2] = _stack[_sp - 1];
                    _stack[_sp - 1] = a;
                    break;
                }

                case 0x8B:
                {
                    int b = Pop();
                    Push(Math.Max(Pop(), b));
                    break;
                }

                case 0x8C:
                {
                    int b = Pop();
                    Push(Math.Min(Pop(), b));
                    break;
                }

                case 0x8E:
                    InstructControl();
                    break;
                default:
                    if (op >= 0xB0 && op <= 0xB7)
                    {
                        int n = op - 0xB0 + 1;
                        for (int i = 0; i < n; i++)
                        {
                            Push(_code[_ip + 1 + i]);
                        }
                    }
                    else if (op >= 0xB8 && op <= 0xBF)
                    {
                        int n = op - 0xB8 + 1;
                        for (int i = 0; i < n; i++)
                        {
                            Push((short)((_code[_ip + 1 + (2 * i)] << 8) | _code[_ip + 2 + (2 * i)]));
                        }
                    }
                    else if (op >= 0xC0 && op <= 0xDF)
                    {
                        Mdrp(op);
                    }
                    else if (op >= 0xE0)
                    {
                        Mirp(op);
                    }
                    else
                    {
                        CallInstructionDefinition(op, ref next);
                    }

                    break;
            }
        }

        private static int ZoneId(int v) => v is 0 or 1 ? v : throw Abort("bad zone");

        private void SetRound(int period, int phase, int threshold)
        {
            Gs.Period = period;
            Gs.Phase = phase;
            Gs.Threshold = threshold;
            Gs.RoundOff = false;
        }

        private void SRound(bool d45)
        {
            int n = Pop();
            int period = (n >> 6) & 3;
            int p = d45
                ? period switch { 0 => 23, 1 => 45, 2 => 90, _ => 45 }
                : period switch { 0 => 32, 1 => 64, 2 => 128, _ => 64 };
            int phase = ((n >> 4) & 3) switch { 0 => 0, 1 => p / 4, 2 => p / 2, _ => p * 3 / 4 };
            int t = n & 15;
            int threshold = t == 0 ? p - 1 : (t - 4) * p / 8;
            SetRound(p, phase, threshold);
        }

        private void SetAxis(int axisIsX, bool pv, bool fv, bool both)
        {
            short x = (short)(axisIsX != 0 ? 0x4000 : 0);
            short y = (short)(axisIsX != 0 ? 0 : 0x4000);
            if (pv)
            {
                Gs.PvX = x;
                Gs.PvY = y;
                Gs.DvX = x;
                Gs.DvY = y;
            }

            if (fv)
            {
                Gs.FvX = x;
                Gs.FvY = y;
            }
        }

        private void LineVector(byte op, bool projection, bool dual)
        {
            int idx2 = Pop();
            int idx1 = Pop();
            Pt(_zp1, idx1);
            Pt(_zp2, idx2);
            int dx = _zp1.CurX[idx1] - _zp2.CurX[idx2];
            int dy = _zp1.CurY[idx1] - _zp2.CurY[idx2];
            int ox = _zp1.OrgX[idx1] - _zp2.OrgX[idx2];
            int oy = _zp1.OrgY[idx1] - _zp2.OrgY[idx2];
            if (dx == 0 && dy == 0)
            {
                dx = 0x4000;
                dy = 0;
                op = (byte)(op & ~1);
            }

            if (ox == 0 && oy == 0)
            {
                ox = 0x4000;
                oy = 0;
            }

            if ((op & 1) != 0)
            {
                (dx, dy) = (-dy, dx);
                (ox, oy) = (-oy, ox);
            }

            if (projection)
            {
                Normalize(dx, dy, out Gs.PvX, out Gs.PvY);
                if (dual)
                {
                    Normalize(ox, oy, out Gs.DvX, out Gs.DvY);
                }
                else
                {
                    Gs.DvX = Gs.PvX;
                    Gs.DvY = Gs.PvY;
                }
            }
            else
            {
                Normalize(dx, dy, out Gs.FvX, out Gs.FvY);
            }
        }

        private void Jump(ref int next, int offset)
        {
            if (offset == 0)
            {
                throw Abort("jump to self");
            }

            long target = (long)_ip + offset;
            if (target < 0 || target > _code.Length)
            {
                throw Abort("jump out of the program");
            }

            next = (int)target;
        }

        private int GetCvt(int i)
        {
            if ((uint)i >= (uint)Cvt.Length)
            {
                throw Abort("cvt index");
            }

            return Cvt[i];
        }

        private void SetCvt(int i, int v)
        {
            if ((uint)i >= (uint)Cvt.Length)
            {
                throw Abort("cvt index");
            }

            Cvt[i] = v;
        }

        private int LoopCount()
        {
            int n = Gs.Loop;
            Gs.Loop = 1;
            _steps += n;
            if (_steps > _budget)
            {
                throw Abort("instruction budget");
            }

            return n;
        }

        // ---- functions ----

        private void DefineFunction(ref int next, bool instruction)
        {
            int id = Pop();
            if (_codeId == 2)
            {
                throw Abort("function definition in a glyph program");
            }

            Function[] table = instruction ? _h._idefs : _h._functions;
            if ((uint)id >= (uint)table.Length)
            {
                throw Abort("function number");
            }

            int start = _ip + 1;
            int p = start;
            while (true)
            {
                if (p >= _code.Length)
                {
                    throw Abort("unterminated function");
                }

                byte o = _code[p];
                if (o == 0x2D)
                {
                    break;
                }

                if (o is 0x2C or 0x89)
                {
                    throw Abort("nested function definition");
                }

                p += OpLen(_code, p);
                if (++_steps > _budget)
                {
                    throw Abort("instruction budget");
                }
            }

            table[id] = new Function { Code = _codeId, Start = start, End = p, Defined = true };
            next = p + 1;
        }

        private void Call(bool loop, ref int next)
        {
            int id = Pop();
            int count = 1;
            if (loop)
            {
                count = Pop();
                if (count <= 0)
                {
                    return;
                }

                if (count > 0xFFFF)
                {
                    throw Abort("loop call count");
                }

                _steps += count;
                if (_steps > _budget)
                {
                    throw Abort("instruction budget");
                }
            }

            if ((uint)id >= (uint)_h._functions.Length || !_h._functions[id].Defined)
            {
                throw Abort("undefined function");
            }

            Enter(_h._functions[id], count, ref next);
        }

        private void Enter(Function f, int count, ref int next)
        {
            if (_fp >= MaxCallDepth)
            {
                throw Abort("call depth");
            }

            _frames[_fp++] = new Frame { Code = _codeId, RetIp = next, Count = count, Start = f.Start, FuncCode = f.Code };
            _codeId = f.Code;
            _code = CodeOf(f.Code);
            next = f.Start;
        }

        private void EndFunction(ref int next)
        {
            if (_fp == 0)
            {
                throw Abort("ENDF outside a function");
            }

            ref Frame fr = ref _frames[_fp - 1];
            if (--fr.Count > 0)
            {
                next = fr.Start;
                return;
            }

            _codeId = fr.Code;
            _code = CodeOf(fr.Code);
            next = fr.RetIp;
            _fp--;
        }

        private void CallInstructionDefinition(byte op, ref int next)
        {
            Function f = _h._idefs[op];
            if (!f.Defined)
            {
                throw Abort("invalid instruction");
            }

            Enter(f, 1, ref next);
        }

        // ---- point instructions ----

        private void Mdap(byte op)
        {
            int p = Pt(_zp0, Pop());
            int d = 0;
            if ((op & 1) != 0)
            {
                int cur = Project(_zp0.CurX[p], _zp0.CurY[p]);
                d = Round(cur) - cur;
            }

            Move(_zp0, p, d);
            Gs.Rp0 = Gs.Rp1 = p;
        }

        private void Miap(byte op)
        {
            int n = Pop();
            int p = Pt(_zp0, Pop());
            int distance = GetCvt(n);
            if (Gs.Zp0 == 0)
            {
                _zp0.OrgX[p] = MulDiv(distance, Gs.FvX, 0x4000);
                _zp0.OrgY[p] = MulDiv(distance, Gs.FvY, 0x4000);
                _zp0.CurX[p] = _zp0.OrgX[p];
                _zp0.CurY[p] = _zp0.OrgY[p];
            }

            int org = Project(_zp0.CurX[p], _zp0.CurY[p]);
            if ((op & 1) != 0)
            {
                if (Math.Abs(distance - org) > Gs.CvtCutIn)
                {
                    distance = org;
                }

                distance = Round(distance);
            }

            Move(_zp0, p, distance - org);
            Gs.Rp0 = Gs.Rp1 = p;
        }

        private void Mdrp(byte op)
        {
            int p = Pt(_zp1, Pop());
            Pt(_zp0, Gs.Rp0);
            int orgDist;
            if (Gs.Zp0 == 0 || Gs.Zp1 == 0)
            {
                orgDist = DualProject(_zp1.OrgX[p] - _zp0.OrgX[Gs.Rp0], _zp1.OrgY[p] - _zp0.OrgY[Gs.Rp0]);
            }
            else
            {
                orgDist = DualProject(_zp1.OrusX[p] - _zp0.OrusX[Gs.Rp0], _zp1.OrusY[p] - _zp0.OrusY[Gs.Rp0]);
                orgDist = MulFix(orgDist, _h._scale);
            }

            if (Math.Abs(orgDist - Gs.SwValue) < Gs.SwCutIn)
            {
                orgDist = orgDist >= 0 ? Gs.SwValue : -Gs.SwValue;
            }

            int distance = (op & 4) != 0 ? Round(orgDist) : orgDist;
            if ((op & 8) != 0)
            {
                if (orgDist >= 0)
                {
                    distance = Math.Max(distance, Gs.MinDist);
                }
                else
                {
                    distance = Math.Min(distance, -Gs.MinDist);
                }
            }

            int cur = Project(_zp1.CurX[p] - _zp0.CurX[Gs.Rp0], _zp1.CurY[p] - _zp0.CurY[Gs.Rp0]);
            Move(_zp1, p, distance - cur);
            Gs.Rp1 = Gs.Rp0;
            Gs.Rp2 = p;
            if ((op & 0x10) != 0)
            {
                Gs.Rp0 = p;
            }
        }

        private void Mirp(byte op)
        {
            int n = Pop();
            int p = Pt(_zp1, Pop());
            Pt(_zp0, Gs.Rp0);
            int cvtDist = n == -1 ? 0 : GetCvt(n);
            if (Math.Abs(cvtDist - Gs.SwValue) < Gs.SwCutIn)
            {
                cvtDist = cvtDist >= 0 ? Gs.SwValue : -Gs.SwValue;
            }

            if (Gs.Zp1 == 0)
            {
                _zp1.OrgX[p] = _zp0.OrgX[Gs.Rp0] + MulDiv(cvtDist, Gs.FvX, 0x4000);
                _zp1.OrgY[p] = _zp0.OrgY[Gs.Rp0] + MulDiv(cvtDist, Gs.FvY, 0x4000);
                _zp1.CurX[p] = _zp1.OrgX[p];
                _zp1.CurY[p] = _zp1.OrgY[p];
            }

            int orgDist = DualProject(_zp1.OrgX[p] - _zp0.OrgX[Gs.Rp0], _zp1.OrgY[p] - _zp0.OrgY[Gs.Rp0]);
            int curDist = Project(_zp1.CurX[p] - _zp0.CurX[Gs.Rp0], _zp1.CurY[p] - _zp0.CurY[Gs.Rp0]);
            if (Gs.AutoFlip && (orgDist ^ cvtDist) < 0)
            {
                cvtDist = -cvtDist;
            }

            int distance;
            if ((op & 4) != 0)
            {
                if (Gs.Zp0 == Gs.Zp1 && Math.Abs(cvtDist - orgDist) > Gs.CvtCutIn)
                {
                    cvtDist = orgDist;
                }

                distance = Round(cvtDist);
            }
            else
            {
                distance = cvtDist;
            }

            if ((op & 8) != 0)
            {
                if (orgDist >= 0)
                {
                    distance = Math.Max(distance, Gs.MinDist);
                }
                else
                {
                    distance = Math.Min(distance, -Gs.MinDist);
                }
            }

            Move(_zp1, p, distance - curDist);
            Gs.Rp1 = Gs.Rp0;
            if ((op & 0x10) != 0)
            {
                Gs.Rp0 = p;
            }

            Gs.Rp2 = p;
        }

        private void Msirp(byte op)
        {
            int distance = Pop();
            int p = Pt(_zp1, Pop());
            Pt(_zp0, Gs.Rp0);
            if (Gs.Zp1 == 0)
            {
                _zp1.OrgX[p] = _zp0.OrgX[Gs.Rp0];
                _zp1.OrgY[p] = _zp0.OrgY[Gs.Rp0];
                MoveOrig(_zp1, p, distance);
                _zp1.CurX[p] = _zp1.OrgX[p];
                _zp1.CurY[p] = _zp1.OrgY[p];
            }

            int cur = Project(_zp1.CurX[p] - _zp0.CurX[Gs.Rp0], _zp1.CurY[p] - _zp0.CurY[Gs.Rp0]);
            Move(_zp1, p, distance - cur);
            Gs.Rp1 = Gs.Rp0;
            Gs.Rp2 = p;
            if ((op & 1) != 0)
            {
                Gs.Rp0 = p;
            }
        }

        private void AlignRp()
        {
            int n = LoopCount();
            Pt(_zp0, Gs.Rp0);
            for (int i = 0; i < n; i++)
            {
                int p = Pt(_zp1, Pop());
                int d = Project(_zp1.CurX[p] - _zp0.CurX[Gs.Rp0], _zp1.CurY[p] - _zp0.CurY[Gs.Rp0]);
                Move(_zp1, p, -d);
            }
        }

        private void Md(byte op)
        {
            int k = Pop();
            int l = Pop();
            Pt(_zp0, l);
            Pt(_zp1, k);
            int d;
            if (op == 0x49)
            {
                d = Project(_zp0.CurX[l] - _zp1.CurX[k], _zp0.CurY[l] - _zp1.CurY[k]);
            }
            else if (Gs.Zp0 == 0 || Gs.Zp1 == 0)
            {
                d = DualProject(_zp0.OrgX[l] - _zp1.OrgX[k], _zp0.OrgY[l] - _zp1.OrgY[k]);
            }
            else
            {
                d = MulFix(DualProject(_zp0.OrusX[l] - _zp1.OrusX[k], _zp0.OrusY[l] - _zp1.OrusY[k]), _h._scale);
            }

            Push(d);
        }

        // The displacement of the reference point of SHP, SHC and SHZ along the freedom vector.
        private void RefDisplacement(byte op, out int dx, out int dy, out Zone zone, out int refPoint)
        {
            if ((op & 1) != 0)
            {
                zone = _zp0;
                refPoint = Pt(_zp0, Gs.Rp1);
            }
            else
            {
                zone = _zp1;
                refPoint = Pt(_zp1, Gs.Rp2);
            }

            int d = Project(zone.CurX[refPoint] - zone.OrgX[refPoint], zone.CurY[refPoint] - zone.OrgY[refPoint]);
            int fp = FdotP();
            dx = MulDiv(d, Gs.FvX, fp);
            dy = MulDiv(d, Gs.FvY, fp);
        }

        private void Shp(byte op)
        {
            int n = LoopCount();
            RefDisplacement(op, out int dx, out int dy, out _, out _);
            for (int i = 0; i < n; i++)
            {
                int p = Pt(_zp2, Pop());
                MoveDelta(_zp2, p, dx, dy, true);
            }
        }

        private void Shc(byte op)
        {
            int c = Pop();
            RefDisplacement(op, out int dx, out int dy, out Zone refZone, out int refPoint);
            Zone z = _zp2;
            int contours = z == Glyph ? z.Contours : 1;
            if ((uint)c >= (uint)contours && !(z != Glyph && c == 0))
            {
                throw Abort("contour index");
            }

            int start;
            int end;
            if (z == Glyph)
            {
                start = c == 0 ? 0 : z.Ends[c - 1] + 1;
                end = z.Ends[c];
            }
            else
            {
                start = 0;
                end = z.Count - 1;
            }

            for (int p = start; p <= end && p < z.Count; p++)
            {
                if (!(z == refZone && p == refPoint))
                {
                    MoveDelta(z, p, dx, dy, false);
                }
            }
        }

        private void Shz(byte op)
        {
            int zid = ZoneId(Pop());
            RefDisplacement(op, out int dx, out int dy, out Zone refZone, out int refPoint);
            Zone z = zid == 0 ? _twilight : Glyph!;
            int limit = zid == 0 ? z.Count : Math.Max(0, z.Count - 4);
            for (int p = 0; p < limit; p++)
            {
                if (!(z == refZone && p == refPoint))
                {
                    MoveDelta(z, p, dx, dy, false);
                }
            }
        }

        private void Shpix()
        {
            int amount = Pop();
            int dx = Mul14(amount, Gs.FvX);
            int dy = Mul14(amount, Gs.FvY);
            int n = LoopCount();
            for (int i = 0; i < n; i++)
            {
                int p = Pt(_zp2, Pop());
                if (BackwardCompatible && Gs.Zp2 != 0)
                {
                    // Backward compatible: only a point already touched in y is moved, and only in y.
                    if ((_zp2.Tags[p] & TouchY) != 0 && Gs.FvY != 0)
                    {
                        MoveDelta(_zp2, p, 0, dy, true);
                    }

                    continue;
                }

                MoveDelta(_zp2, p, dx, dy, true);
            }
        }

        private void Ip()
        {
            int n = LoopCount();
            Pt(_zp0, Gs.Rp1);
            Pt(_zp1, Gs.Rp2);
            bool twilight = Gs.Zp0 == 0 || Gs.Zp1 == 0 || Gs.Zp2 == 0;
            int baseX = twilight ? _zp0.OrgX[Gs.Rp1] : _zp0.OrusX[Gs.Rp1];
            int baseY = twilight ? _zp0.OrgY[Gs.Rp1] : _zp0.OrusY[Gs.Rp1];
            int oldRange = twilight
                ? DualProject(_zp1.OrgX[Gs.Rp2] - baseX, _zp1.OrgY[Gs.Rp2] - baseY)
                : DualProject(_zp1.OrusX[Gs.Rp2] - baseX, _zp1.OrusY[Gs.Rp2] - baseY);
            int curRange = Project(_zp1.CurX[Gs.Rp2] - _zp0.CurX[Gs.Rp1], _zp1.CurY[Gs.Rp2] - _zp0.CurY[Gs.Rp1]);
            for (int i = 0; i < n; i++)
            {
                int p = Pt(_zp2, Pop());
                int orgDist = twilight
                    ? DualProject(_zp2.OrgX[p] - baseX, _zp2.OrgY[p] - baseY)
                    : DualProject(_zp2.OrusX[p] - baseX, _zp2.OrusY[p] - baseY);
                int curDist = Project(_zp2.CurX[p] - _zp0.CurX[Gs.Rp1], _zp2.CurY[p] - _zp0.CurY[Gs.Rp1]);
                int newDist = orgDist != 0 ? (oldRange != 0 ? MulDiv(orgDist, curRange, oldRange) : orgDist) : 0;
                Move(_zp2, p, newDist - curDist);
            }
        }

        private void Isect()
        {
            int b1 = Pop();
            int b0 = Pop();
            int a1 = Pop();
            int a0 = Pop();
            int p = Pop();
            Pt(_zp2, p);
            Pt(_zp1, a0);
            Pt(_zp1, a1);
            Pt(_zp0, b0);
            Pt(_zp0, b1);
            long dbx = _zp0.CurX[b1] - _zp0.CurX[b0];
            long dby = _zp0.CurY[b1] - _zp0.CurY[b0];
            long dax = _zp1.CurX[a1] - _zp1.CurX[a0];
            long day = _zp1.CurY[a1] - _zp1.CurY[a0];
            long dx = _zp0.CurX[b0] - _zp1.CurX[a0];
            long dy = _zp0.CurY[b0] - _zp1.CurY[a0];
            _zp2.Tags[p] |= TouchX | TouchY;
            long discriminant = (dax * -dby) - (day * -dbx);
            if (discriminant != 0)
            {
                long val = (dx * -dby) - (dy * -dbx);
                _zp2.CurX[p] = (int)(_zp1.CurX[a0] + (val * dax / discriminant));
                _zp2.CurY[p] = (int)(_zp1.CurY[a0] + (val * day / discriminant));
            }
            else
            {
                _zp2.CurX[p] = (_zp1.CurX[a0] + _zp1.CurX[a1] + _zp0.CurX[b0] + _zp0.CurX[b1]) / 4;
                _zp2.CurY[p] = (_zp1.CurY[a0] + _zp1.CurY[a1] + _zp0.CurY[b0] + _zp0.CurY[b1]) / 4;
            }
        }

        private void DeltaP(int bias)
        {
            int n = Pop();
            if (n < 0 || n > 0xFFFF)
            {
                throw Abort("bad delta count");
            }

            for (int i = 0; i < n; i++)
            {
                int p = Pop();
                int arg = Pop();
                Pt(_zp0, p);
                int c = ((arg & 0xF0) >> 4) + Gs.DeltaBase + bias;
                if (c != _h._ppem)
                {
                    continue;
                }

                int b = (arg & 0xF) - 8;
                if (b >= 0)
                {
                    b++;
                }

                b *= 1 << (6 - Gs.DeltaShift);
                if (BackwardCompatible && Gs.Zp0 != 0 && !((_zp0.Tags[p] & TouchY) != 0 && !(_iupX && _iupY)))
                {
                    continue;
                }

                Move(_zp0, p, b);
            }
        }

        private void DeltaC(int bias)
        {
            int n = Pop();
            if (n < 0 || n > 0xFFFF)
            {
                throw Abort("bad delta count");
            }

            for (int i = 0; i < n; i++)
            {
                int c = Pop();
                int arg = Pop();
                int ppem = ((arg & 0xF0) >> 4) + Gs.DeltaBase + bias;
                if (ppem != _h._ppem)
                {
                    continue;
                }

                int b = (arg & 0xF) - 8;
                if (b >= 0)
                {
                    b++;
                }

                b *= 1 << (6 - Gs.DeltaShift);
                SetCvt(c, GetCvt(c) + b);
            }
        }

        // Interpolate untouched points between touched ones, per contour, along one axis (rule: SHAPE of the original outline).
        private void Iup(byte op)
        {
            bool x = (op & 1) != 0;
            if (x)
            {
                _iupX = true;
            }
            else
            {
                _iupY = true;
            }

            if (Glyph is null || (x && BackwardCompatible))
            {
                return;
            }

            Zone z = Glyph;
            byte mask = x ? TouchX : TouchY;
            int[] cur = x ? z.CurX : z.CurY;
            int[] org = x ? z.OrgX : z.OrgY;
            int[] orus = x ? z.OrusX : z.OrusY;
            int start = 0;
            for (int c = 0; c < z.Contours; c++)
            {
                int end = z.Ends[c];
                if (end >= z.Count - 4 || end < start)
                {
                    break;
                }

                int firstTouched = -1;
                int prev = -1;
                for (int p = start; p <= end; p++)
                {
                    if ((z.Tags[p] & mask) == 0)
                    {
                        continue;
                    }

                    if (firstTouched < 0)
                    {
                        firstTouched = p;
                    }

                    if (prev >= 0)
                    {
                        Interpolate(start, end, prev, p, cur, org, orus);
                    }

                    prev = p;
                }

                if (firstTouched >= 0)
                {
                    if (prev == firstTouched)
                    {
                        int delta = cur[firstTouched] - org[firstTouched];
                        if (delta != 0)
                        {
                            for (int p = start; p <= end; p++)
                            {
                                if (p != firstTouched)
                                {
                                    cur[p] += delta;
                                }
                            }
                        }
                    }
                    else
                    {
                        // The stretch from the last touched point around the end of the contour to the first.
                        Interpolate(start, end, prev, firstTouched, cur, org, orus);
                    }
                }

                start = end + 1;
            }
        }

        // Points strictly between touched points a and b (going forward, wrapping at the contour's end) move in proportion to the
        // original (font unit) distances between the references.
        private static void Interpolate(int start, int end, int a, int b, int[] cur, int[] org, int[] orus)
        {
            int r1 = a;
            int r2 = b;
            if (orus[r1] > orus[r2])
            {
                (r1, r2) = (r2, r1);
            }

            int orus1 = orus[r1];
            int orus2 = orus[r2];
            int org1 = org[r1];
            int org2 = org[r2];
            int cur1 = cur[r1];
            int cur2 = cur[r2];
            int delta1 = cur1 - org1;
            int delta2 = cur2 - org2;
            int scale = 0;
            bool same = orus1 == orus2;
            if (!same)
            {
                scale = MulDiv(cur2 - cur1, 0x10000, orus2 - orus1);
            }

            int p = a == end ? start : a + 1;
            int guard = end - start + 1;
            while (p != b && guard-- > 0)
            {
                int v = orus[p];
                int n;
                if (same || v <= orus1)
                {
                    n = org[p] + delta1;
                }
                else if (v >= orus2)
                {
                    n = org[p] + delta2;
                }
                else
                {
                    n = cur1 + MulFix(v - orus1, scale);
                }

                cur[p] = n;
                p = p == end ? start : p + 1;
            }
        }

        private void GetInfo()
        {
            int sel = Pop();
            int result = 0;
            if ((sel & 1) != 0)
            {
                result = 40;
            }

            // Bit 5 asks whether the rendering is grayscale (bit 12 of the result); bits 6, 10, 11 and 12 ask about subpixel
            // (ClearType) rendering: report the DirectWrite-style combination of subpixel positioning with symmetric smoothing
            // and gray output.
            if ((sel & 32) != 0)
            {
                result |= 1 << 12;
            }

            if ((sel & 64) != 0)
            {
                result |= 1 << 13;
            }

            if ((sel & 1024) != 0)
            {
                result |= 1 << 17;
            }

            if ((sel & 2048) != 0)
            {
                result |= 1 << 18;
            }

            if ((sel & 4096) != 0)
            {
                result |= 1 << 19;
            }

            Push(result);
        }

        private void InstructControl()
        {
            int selector = Pop();
            int value = Pop();
            if (selector < 1 || selector > 3)
            {
                return;
            }

            int bit = 1 << (selector - 1);
            if (selector == 3)
            {
                // Native ClearType mode (selector 3) is only honored from the control value program.
                if (!ProgramIsPrep)
                {
                    return;
                }

                if (value == 4)
                {
                    Gs.InstructControl |= 4;
                }
                else if (value == 0)
                {
                    Gs.InstructControl &= ~4;
                }

                return;
            }

            if (!ProgramIsPrep)
            {
                return;
            }

            if (value == bit)
            {
                Gs.InstructControl |= bit;
            }
            else if (value == 0)
            {
                Gs.InstructControl &= ~bit;
            }
        }
    }
}
