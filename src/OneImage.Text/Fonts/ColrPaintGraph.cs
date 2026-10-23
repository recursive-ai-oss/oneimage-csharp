using System.Collections.Concurrent;
using System.Numerics;

namespace OneImage.Text.Fonts;

internal enum ColrOpKind : byte
{
    /// <summary>Paint the clip region (or the whole layer) with a paint.</summary>
    Fill,

    /// <summary>Start a new transparent layer on top of the layer stack.</summary>
    PushLayer,

    /// <summary>Composite the top layer (the source) onto the one below it (the backdrop) with <see cref="ColrOp.Mode"/> and drop it.</summary>
    Merge,

    /// <summary>Composite the top layer onto the one below it with source-over and drop it.</summary>
    PopOver,
}

internal enum ColrFillKind : byte
{
    Solid,
    Linear,
    Radial,
    Sweep,
}

/// <summary>A color stop: position along the color line, palette index (0xFFFF is the text color) and alpha.</summary>
internal readonly record struct ColrStop(float Offset, int Palette, float Alpha);

/// <summary>A glyph outline that clips what is painted under it, with the transform that was current when it was met; chained to the clips of its ancestors.</summary>
internal sealed class ColrClip(int glyph, in Matrix3x2 matrix, ColrClip? parent)
{
    public readonly int Glyph = glyph;
    public readonly Matrix3x2 Matrix = matrix;
    public readonly ColrClip? Parent = parent;
}

/// <summary>One step of a flattened paint graph. Geometry is in font units, in the paint's own space; <see cref="Matrix"/> takes it to glyph space.</summary>
internal sealed class ColrOp
{
    public ColrOpKind Kind;
    public ColrClip? Clip;
    public Matrix3x2 Matrix = Matrix3x2.Identity;
    public ColrFillKind Fill;
    public int Palette;
    public float Alpha = 1;
    public ColrStop[] Stops = [];

    /// <summary>0 pad, 1 repeat, 2 reflect.</summary>
    public int Extend;
    public float X0, Y0, X1, Y1, X2, Y2, R0, R1;
    public float StartAngle, EndAngle;

    /// <summary>The COLR composite mode (0 to 27) of a merge.</summary>
    public int Mode;
}

/// <summary>A COLR version 1 paint graph flattened into a list of steps, with the glyph's clip box when it has one.</summary>
internal sealed class ColrProgram
{
    public readonly List<ColrOp> Ops = [];
    public bool HasClipBox;
    public float ClipXMin, ClipYMin, ClipXMax, ClipYMax;
}

internal sealed partial class ColorGlyphs
{
    /// <summary>Most paint nodes visited for one glyph; a graph that needs more (a cycle, or a graph that shares sub-graphs exponentially) is refused.</summary>
    public const int MaxPaintNodes = 4096;

    /// <summary>Deepest nesting of paints.</summary>
    public const int MaxPaintDepth = 64;

    private int _paintGraphCount;
    private int _baseListOffset;
    private int _layerListOffset;
    private int _layerListCount;
    private int _clipListOffset;
    private readonly ConcurrentDictionary<int, ColrProgram?> _programs = new();

    private void ReadGraphHeader(ReadOnlySpan<byte> c)
    {
        if (Be.U16(c, 0) < 1 || c.Length < 34)
        {
            return;
        }

        // Header fields of version 1: base glyph list, layer list and clip list offsets (the variation tables are not used: a
        // variable font draws its default instance).
        uint baseList = Be.U32(c, 14);
        uint layerList = Be.U32(c, 18);
        uint clipList = Be.U32(c, 22);
        if (baseList == 0 || baseList + 4 > c.Length)
        {
            return;
        }

        int count = (int)Be.U32(c, (int)baseList);
        if (count < 0 || (long)count * 6 > c.Length - baseList - 4)
        {
            return;
        }

        _baseListOffset = (int)baseList;
        _paintGraphCount = count;
        if (layerList != 0 && layerList + 4 <= c.Length)
        {
            int n = (int)Be.U32(c, (int)layerList);
            if (n >= 0 && (long)n * 4 <= c.Length - layerList - 4)
            {
                _layerListOffset = (int)layerList;
                _layerListCount = n;
            }
        }

        if (clipList != 0 && clipList + 6 <= c.Length)
        {
            _clipListOffset = (int)clipList;
        }
    }

    /// <summary>Whether the glyph has a version 1 paint graph.</summary>
    public bool HasPaintGraph(int glyph) => FindBasePaint(glyph) >= 0;

    private int FindBasePaint(int glyph)
    {
        if (_paintGraphCount == 0)
        {
            return -1;
        }

        ReadOnlySpan<byte> c = _colr.Span;
        int lo = 0;
        int hi = _paintGraphCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int at = _baseListOffset + 4 + (6 * mid);
            int id = Be.U16(c, at);
            if (id == glyph)
            {
                long off = (long)_baseListOffset + Be.U32(c, at + 2);
                return off + 1 <= c.Length ? (int)off : -1;
            }

            if (id < glyph)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }

    /// <summary>
    /// The flattened paint graph of a glyph, or null when it has none, it is damaged, or it exceeds the node or depth budget
    /// (which is also how a cycle is refused). Cached.
    /// </summary>
    public ColrProgram? GetProgram(int glyph)
    {
        if (_programs.TryGetValue(glyph, out ColrProgram? cached))
        {
            return cached;
        }

        ColrProgram? p = null;
        try
        {
            p = Build(glyph);
        }
        catch (FontFormatException)
        {
            // A damaged graph is ignored; the glyph falls back to its layers or its outline.
        }

        if (_programs.Count > 4096)
        {
            _programs.Clear();
        }

        _programs[glyph] = p;
        return p;
    }

    private sealed class PState(in Matrix3x2 m, ColrClip? clip, int depth)
    {
        public readonly Matrix3x2 M = m;
        public readonly ColrClip? Clip = clip;
        public readonly int Depth = depth;
    }

    private readonly struct Task(int offset, PState? state, ColrOp? emit)
    {
        public readonly int Offset = offset;
        public readonly PState? State = state;
        public readonly ColrOp? Emit = emit;
    }

    private static float F2(ReadOnlySpan<byte> c, int at) => Be.I16(c, at) / 16384f;

    private static int Off24(ReadOnlySpan<byte> c, int at) => (Be.U8(c, at) << 16) | (Be.U8(c, at + 1) << 8) | Be.U8(c, at + 2);

    private ColrProgram? Build(int glyph)
    {
        int root = FindBasePaint(glyph);
        if (root < 0)
        {
            return null;
        }

        ReadOnlySpan<byte> c = _colr.Span;
        var program = new ColrProgram();
        ReadClipBox(c, glyph, program);

        // An explicit work stack: the graph's depth never becomes call depth. Tasks either visit a paint or append a ready-made step.
        var stack = new Stack<Task>();
        stack.Push(new Task(root, new PState(Matrix3x2.Identity, null, 0), null));
        int nodes = 0;
        while (stack.Count > 0)
        {
            Task t = stack.Pop();
            if (t.Emit is not null)
            {
                program.Ops.Add(t.Emit);
                continue;
            }

            if (++nodes > MaxPaintNodes || t.State!.Depth > MaxPaintDepth)
            {
                return null;
            }

            PState st = t.State;
            int p = t.Offset;
            int format = Be.U8(c, p);
            switch (format)
            {
                case 1:
                {
                    int n = Be.U8(c, p + 1);
                    long first = Be.U32(c, p + 2);
                    if (first + n > _layerListCount)
                    {
                        return null;
                    }

                    for (int i = n - 1; i >= 0; i--)
                    {
                        long off = (long)_layerListOffset + Be.U32(c, _layerListOffset + 4 + (int)((first + i) * 4));
                        if (off >= c.Length)
                        {
                            return null;
                        }

                        stack.Push(new Task((int)off, new PState(st.M, st.Clip, st.Depth + 1), null));
                    }

                    break;
                }

                case 2:
                case 3:
                    program.Ops.Add(new ColrOp { Kind = ColrOpKind.Fill, Clip = st.Clip, Matrix = st.M, Fill = ColrFillKind.Solid, Palette = Be.U16(c, p + 1), Alpha = F2(c, p + 3) });
                    break;
                case 4:
                case 5:
                    program.Ops.Add(WithColorLine(new ColrOp
                    {
                        Kind = ColrOpKind.Fill,
                        Clip = st.Clip,
                        Matrix = st.M,
                        Fill = ColrFillKind.Linear,
                        X0 = Be.I16(c, p + 4),
                        Y0 = Be.I16(c, p + 6),
                        X1 = Be.I16(c, p + 8),
                        Y1 = Be.I16(c, p + 10),
                        X2 = Be.I16(c, p + 12),
                        Y2 = Be.I16(c, p + 14),
                    }, c, p + Off24(c, p + 1), format == 5));
                    break;
                case 6:
                case 7:
                    program.Ops.Add(WithColorLine(new ColrOp
                    {
                        Kind = ColrOpKind.Fill,
                        Clip = st.Clip,
                        Matrix = st.M,
                        Fill = ColrFillKind.Radial,
                        X0 = Be.I16(c, p + 4),
                        Y0 = Be.I16(c, p + 6),
                        R0 = Be.U16(c, p + 8),
                        X1 = Be.I16(c, p + 10),
                        Y1 = Be.I16(c, p + 12),
                        R1 = Be.U16(c, p + 14),
                    }, c, p + Off24(c, p + 1), format == 7));
                    break;
                case 8:
                case 9:
                    program.Ops.Add(WithColorLine(new ColrOp
                    {
                        Kind = ColrOpKind.Fill,
                        Clip = st.Clip,
                        Matrix = st.M,
                        Fill = ColrFillKind.Sweep,
                        X0 = Be.I16(c, p + 4),
                        Y0 = Be.I16(c, p + 6),
                        StartAngle = (F2(c, p + 8) * 180f) + 180f,
                        EndAngle = (F2(c, p + 10) * 180f) + 180f,
                    }, c, p + Off24(c, p + 1), format == 9));
                    break;
                case 10:
                    stack.Push(new Task(p + Off24(c, p + 1), new PState(st.M, new ColrClip(Be.U16(c, p + 4), st.M, st.Clip), st.Depth + 1), null));
                    break;
                case 11:
                {
                    int target = FindBasePaint(Be.U16(c, p + 1));
                    if (target < 0)
                    {
                        return null;
                    }

                    stack.Push(new Task(target, new PState(st.M, st.Clip, st.Depth + 1), null));
                    break;
                }

                case 12:
                case 13:
                {
                    int a = p + Off24(c, p + 4);
                    var local = new Matrix3x2(Be.I32(c, a) / 65536f, Be.I32(c, a + 4) / 65536f, Be.I32(c, a + 8) / 65536f, Be.I32(c, a + 12) / 65536f, Be.I32(c, a + 16) / 65536f, Be.I32(c, a + 20) / 65536f);
                    stack.Push(new Task(p + Off24(c, p + 1), new PState(local * st.M, st.Clip, st.Depth + 1), null));
                    break;
                }

                case 14:
                case 15:
                    PushChild(stack, c, p, st, Matrix3x2.CreateTranslation(Be.I16(c, p + 4), Be.I16(c, p + 6)));
                    break;
                case 16:
                case 17:
                    PushChild(stack, c, p, st, Matrix3x2.CreateScale(F2(c, p + 4), F2(c, p + 6)));
                    break;
                case 18:
                case 19:
                    PushChild(stack, c, p, st, Matrix3x2.CreateScale(F2(c, p + 4), F2(c, p + 6), new Vector2(Be.I16(c, p + 8), Be.I16(c, p + 10))));
                    break;
                case 20:
                case 21:
                    PushChild(stack, c, p, st, Matrix3x2.CreateScale(F2(c, p + 4)));
                    break;
                case 22:
                case 23:
                    PushChild(stack, c, p, st, Matrix3x2.CreateScale(F2(c, p + 4), F2(c, p + 4), new Vector2(Be.I16(c, p + 6), Be.I16(c, p + 8))));
                    break;
                case 24:
                case 25:
                    // Rotation angles are in units of half a turn, counter-clockwise (the glyph space has y up).
                    PushChild(stack, c, p, st, Matrix3x2.CreateRotation(F2(c, p + 4) * MathF.PI));
                    break;
                case 26:
                case 27:
                    PushChild(stack, c, p, st, Matrix3x2.CreateRotation(F2(c, p + 4) * MathF.PI, new Vector2(Be.I16(c, p + 6), Be.I16(c, p + 8))));
                    break;
                case 28:
                case 29:
                    PushChild(stack, c, p, st, Skew(F2(c, p + 4), F2(c, p + 6), Vector2.Zero));
                    break;
                case 30:
                case 31:
                    PushChild(stack, c, p, st, Skew(F2(c, p + 4), F2(c, p + 6), new Vector2(Be.I16(c, p + 8), Be.I16(c, p + 10))));
                    break;
                case 32:
                {
                    int mode = Be.U8(c, p + 4);
                    if (mode > 27)
                    {
                        return null;
                    }

                    // Backdrop first, then source, then merge them and put the result over what is below.
                    var next = new PState(st.M, st.Clip, st.Depth + 1);
                    stack.Push(new Task(0, null, new ColrOp { Kind = ColrOpKind.PopOver }));
                    stack.Push(new Task(0, null, new ColrOp { Kind = ColrOpKind.Merge, Mode = mode }));
                    stack.Push(new Task(p + Off24(c, p + 1), next, null));
                    stack.Push(new Task(0, null, new ColrOp { Kind = ColrOpKind.PushLayer }));
                    stack.Push(new Task(p + Off24(c, p + 5), next, null));
                    stack.Push(new Task(0, null, new ColrOp { Kind = ColrOpKind.PushLayer }));
                    break;
                }

                default:
                    // An unknown paint format: the graph cannot be drawn as the font intends.
                    return null;
            }
        }

        return program;
    }

    private static void PushChild(Stack<Task> stack, ReadOnlySpan<byte> c, int p, PState st, in Matrix3x2 local) =>
        stack.Push(new Task(p + Off24(c, p + 1), new PState(local * st.M, st.Clip, st.Depth + 1), null));

    // x skew rotates the y axis counter-clockwise by the angle (x' = x - y tan a); y skew rotates the x axis (y' = y + x tan b).
    private static Matrix3x2 Skew(float xAngle, float yAngle, Vector2 center)
    {
        var m = new Matrix3x2(1, MathF.Tan(yAngle * MathF.PI), -MathF.Tan(xAngle * MathF.PI), 1, 0, 0);
        return Matrix3x2.CreateTranslation(-center) * m * Matrix3x2.CreateTranslation(center);
    }

    private void ReadClipBox(ReadOnlySpan<byte> c, int glyph, ColrProgram program)
    {
        if (_clipListOffset == 0)
        {
            return;
        }

        int count = (int)Be.U32(c, _clipListOffset + 1);
        if (count < 0 || (long)count * 7 > c.Length - _clipListOffset - 5)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            int at = _clipListOffset + 5 + (7 * i);
            if (glyph >= Be.U16(c, at) && glyph <= Be.U16(c, at + 2))
            {
                int box = _clipListOffset + Off24(c, at + 4);
                int fmt = Be.U8(c, box);
                if (fmt is 1 or 2)
                {
                    program.HasClipBox = true;
                    program.ClipXMin = Be.I16(c, box + 1);
                    program.ClipYMin = Be.I16(c, box + 3);
                    program.ClipXMax = Be.I16(c, box + 5);
                    program.ClipYMax = Be.I16(c, box + 7);
                }

                return;
            }
        }
    }

    private static ColrOp WithColorLine(ColrOp op, ReadOnlySpan<byte> c, int line, bool variable)
    {
        op.Extend = Be.U8(c, line);
        if (op.Extend > 2)
        {
            op.Extend = 0;
        }

        int n = Be.U16(c, line + 1);
        int stride = variable ? 10 : 6;
        if (n == 0 || n > 1024 || (long)n * stride > c.Length - line - 3)
        {
            throw Be.Truncated();
        }

        var stops = new ColrStop[n];
        for (int i = 0; i < n; i++)
        {
            int at = line + 3 + (i * stride);
            stops[i] = new ColrStop(F2(c, at), Be.U16(c, at + 2), F2(c, at + 4));
        }

        // The specification wants ascending offsets; a stable sort repairs a font that did not.
        for (int i = 1; i < n; i++)
        {
            ColrStop s = stops[i];
            int j = i - 1;
            while (j >= 0 && stops[j].Offset > s.Offset)
            {
                stops[j + 1] = stops[j];
                j--;
            }

            stops[j + 1] = s;
        }

        op.Stops = stops;
        return op;
    }
}
