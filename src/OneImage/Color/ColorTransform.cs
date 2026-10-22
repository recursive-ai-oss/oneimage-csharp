using System.Buffers;
using System.Collections.Concurrent;
using OneImage.Kernels;

namespace OneImage;

/// <summary>A fused list of stages evaluated row by row. Identity curves vanish and adjacent matrices compose into one.</summary>
internal sealed class StagePipeline
{
    private const int Chunk = 1024;
    private readonly Stage[] _stages;

    public StagePipeline(List<Stage> stages, int inChannels)
    {
        var fused = new List<Stage>();
        int ch = inChannels;
        foreach (Stage raw in stages)
        {
            Stage s = raw is ScaleStage { In: 3 } sc ? sc.ToMatrix() : raw;
            if (s.In != ch)
            {
                throw new ImageOperationException("Color pipeline stages do not chain.");
            }

            ch = s.Out;
            if (s is CurveStage { IsIdentity: true })
            {
                continue;
            }

            if (s is MatrixStage m && fused.Count > 0 && fused[^1] is MatrixStage prev)
            {
                fused[^1] = MatrixStage.Compose(prev, m);
            }
            else
            {
                fused.Add(s);
            }
        }

        _stages = fused.ToArray();
        In = inChannels;
        Out = ch;
    }

    public int In { get; }

    public int Out { get; }

    public IReadOnlyList<Stage> Stages => _stages;

    /// <summary>True when no stage needs a lookup table or a cube root: the pipeline is curves and matrices only.</summary>
    public bool IsMatrixShaper => _stages.All(s => s is CurveStage or MatrixStage or GrayToXyzStage or XyzToGrayStage);

    public StagePipeline Tabulated()
    {
        var list = new List<Stage>();
        foreach (Stage s in _stages)
        {
            list.Add(s is CurveStage c ? c.Tabulated() : s);
        }

        return new StagePipeline(list, In);
    }

    public void Run(ReadOnlySpan<float> src, Span<float> dst, int n)
    {
        if (_stages.Length == 0)
        {
            src[..(n * In)].CopyTo(dst);
            return;
        }

        if (_stages.Length == 1)
        {
            _stages[0].Apply(src, dst, n);
            return;
        }

        float[] a = ArrayPool<float>.Shared.Rent(Chunk * 4);
        float[] b = ArrayPool<float>.Shared.Rent(Chunk * 4);
        try
        {
            for (int start = 0; start < n; start += Chunk)
            {
                int m = Math.Min(Chunk, n - start);
                ReadOnlySpan<float> cur = src.Slice(start * In, m * In);
                for (int i = 0; i < _stages.Length; i++)
                {
                    Stage s = _stages[i];
                    Span<float> target = i == _stages.Length - 1
                        ? dst.Slice(start * Out, m * Out)
                        : (i & 1) == 0 ? a.AsSpan(0, m * s.Out) : b.AsSpan(0, m * s.Out);
                    s.Apply(cur, target, m);
                    cur = target;
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(a);
            ArrayPool<float>.Shared.Return(b);
        }
    }
}

/// <summary>
/// A compiled conversion from one profile to another for an intent. Float buffers evaluate the exact pipeline per pixel;
/// 8- and 16-bit buffers run a tabulated matrix/curve pipeline or a precomputed lookup grid with tetrahedral interpolation.
/// </summary>
internal sealed class ColorTransform
{
    /// <summary>
    /// Byte budget of the transform cache: 32 MiB. Each cached transform is charged <see cref="ApproximateBytes"/> (its lookup grid, the
    /// profile bytes it keeps alive and a fixed allowance for tabulated curves), and the least recently used transforms are dropped
    /// until the total fits. A table-based CMYK or LUT RGB profile costs about 0.8 to 1.5 MiB, so a few dozen stay cached; matrix
    /// profiles cost tens of kilobytes. A transform bigger than the whole budget is built and used but not kept.
    /// A process that meets many distinct embedded profiles therefore holds at most this much, while repeated conversions with the
    /// same profile (matched by content, not by instance) stay cache hits.
    /// </summary>
    internal const long CacheBudgetBytes = 32L << 20;

    private static long s_cachedBytes;

    private static readonly object s_gate = new();
    private static readonly Dictionary<(string, string, RenderingIntent, bool), LinkedListNode<(( string, string, RenderingIntent, bool) Key, ColorTransform Value)>> s_cache = new();
    private static readonly LinkedList<((string, string, RenderingIntent, bool) Key, ColorTransform Value)> s_recent = new();
    private static readonly ConcurrentDictionary<(ColorModel, ColorModel), ColorTransform> s_defaults = new();

    private readonly StagePipeline _exact;
    private readonly StagePipeline? _fastPipeline;
    private readonly Grid? _grid;

    private ColorTransform(ColorProfile source, ColorProfile destination, StagePipeline exact)
    {
        Source = source;
        Destination = destination;
        _exact = exact;
        if (exact.IsMatrixShaper)
        {
            _fastPipeline = exact.Tabulated();
        }
        else
        {
            _grid = Bake(exact);
        }
    }

    /// <summary>Gets the bytes this transform is charged against the cache budget: grid data, the profiles' bytes and an allowance for curve tables.</summary>
    public long ApproximateBytes =>
        (_grid is null ? 0 : (long)_grid.Data.Length * sizeof(float)) + Source.IccBytes.Length + Destination.IccBytes.Length + (64L << 10);

    /// <summary>Gets the bytes currently charged to the transform cache (for tests).</summary>
    internal static long CachedBytes
    {
        get
        {
            lock (s_gate)
            {
                return s_cachedBytes;
            }
        }
    }

    public ColorProfile Source { get; }

    public ColorProfile Destination { get; }

    public int InChannels => _exact.In;

    public int OutChannels => _exact.Out;

    /// <summary>Gets a value indicating whether integer buffers run through a lookup grid (otherwise a tabulated matrix pipeline).</summary>
    public bool UsesGrid => _grid != null;

    public static ColorTransform Get(ColorProfile source, ColorProfile destination, RenderingIntent intent, bool blackPointCompensation)
    {
        if (!Enum.IsDefined(intent))
        {
            throw new ArgumentOutOfRangeException(nameof(intent));
        }

        // Keyed by profile content, not instance: the same embedded profile met in many images shares one transform, and
        // the least recently used transform is dropped once the cache is full.
        (string, string, RenderingIntent, bool) key = (source.ContentKey, destination.ContentKey, intent, blackPointCompensation);
        lock (s_gate)
        {
            if (s_cache.TryGetValue(key, out var node))
            {
                s_recent.Remove(node);
                s_recent.AddFirst(node);
                return node.Value.Value;
            }
        }

        // Built outside the lock; two threads may build the same transform once, which is harmless.
        ColorTransform built = Build(source, destination, intent, blackPointCompensation);
        lock (s_gate)
        {
            if (s_cache.TryGetValue(key, out var existing))
            {
                return existing.Value.Value;
            }

            long cost = built.ApproximateBytes;
            if (cost <= CacheBudgetBytes)
            {
                s_cache[key] = s_recent.AddFirst((key, built));
                s_cachedBytes += cost;
                while (s_cachedBytes > CacheBudgetBytes)
                {
                    s_cachedBytes -= s_recent.Last!.Value.Value.ApproximateBytes;
                    s_cache.Remove(s_recent.Last.Value.Key);
                    s_recent.RemoveLast();
                }
            }
        }

        return built;
    }

    /// <summary>The conversion used when only color models are known: default profiles, relative colorimetric with black point compensation.</summary>
    public static ColorTransform Default(ColorModel from, ColorModel to) =>
        s_defaults.GetOrAdd((from, to), static k => Get(ColorProfile.DefaultFor(k.Item1), ColorProfile.DefaultFor(k.Item2), RenderingIntent.RelativeColorimetric, true));

    private static ColorTransform Build(ColorProfile src, ColorProfile dst, RenderingIntent intent, bool bpc)
    {
        IccData s = src.Data;
        IccData d = dst.Data;
        var stages = new List<Stage>();
        if (ReferenceEquals(src, dst))
        {
            return new ColorTransform(src, dst, new StagePipeline(stages, ChannelsOf(s.Model)));
        }

        stages.AddRange(s.BuildToPcs(intent));
        if (intent == RenderingIntent.AbsoluteColorimetric)
        {
            double[] ws = s.MediaWhite;
            double[] wd = d.MediaWhite;
            stages.Add(new ScaleStage([ws[0] / wd[0], ws[1] / wd[1], ws[2] / wd[2]], [0, 0, 0]));
        }
        else if (s.Model != ColorModel.Lab && d.Model != ColorModel.Lab)
        {
            // Matches the common CMM behavior: BPC is always on for v4 destinations in the perceptual and saturation intents.
            bool effective = bpc || (d.VersionMajor >= 4 && intent is RenderingIntent.Perceptual or RenderingIntent.Saturation);
            if (effective)
            {
                double[] bin = BlackPoint(s, intent);
                double[] bout = BlackPoint(d, intent);
                if (Math.Abs(bin[0] - bout[0]) > 1e-9 || Math.Abs(bin[1] - bout[1]) > 1e-9 || Math.Abs(bin[2] - bout[2]) > 1e-9)
                {
                    double[] scale = new double[3];
                    double[] offset = new double[3];
                    double[] white = [Pcs.D50X, Pcs.D50Y, Pcs.D50Z];
                    for (int i = 0; i < 3; i++)
                    {
                        double tx = bin[i] - white[i];
                        scale[i] = (bout[i] - white[i]) / tx;
                        offset[i] = -white[i] * (bout[i] - bin[i]) / tx;
                    }

                    stages.Add(new ScaleStage(scale, offset));
                }
            }
        }

        stages.AddRange(d.BuildFromPcs(intent));
        return new ColorTransform(src, dst, new StagePipeline(stages, ChannelsOf(s.Model)));
    }

    private static int ChannelsOf(ColorModel m) => m switch { ColorModel.Gray => 1, ColorModel.Cmyk => 4, _ => 3 };

    /// <summary>Estimates the darkest PCS XYZ the profile can reproduce, following the usual CMM conventions (neutral, at most L* 50).</summary>
    private static double[] BlackPoint(IccData p, RenderingIntent intent)
    {
        if (p.Model == ColorModel.Lab)
        {
            return [0, 0, 0];
        }

        bool shaper = p.IsMatrixShaper(intent, output: false);
        if (p.VersionMajor >= 4 && intent is RenderingIntent.Perceptual or RenderingIntent.Saturation && !shaper)
        {
            return [0.00336, 0.0034731, 0.00287];
        }

        Span<float> dev = stackalloc float[4];
        dev.Clear();
        if (p.Model == ColorModel.Cmyk && p.DeviceClass == IccData.Sig("prtr") && intent == RenderingIntent.RelativeColorimetric && p.HasLut(RenderingIntent.Perceptual, output: true))
        {
            // Output CMYK: push Lab black through the table so the ink limit decides how dark it can get.
            var stages = new List<Stage> { new LabToXyzStage() };
            stages.AddRange(p.BuildFromPcs(RenderingIntent.Perceptual));
            var toDev = new StagePipeline(stages, 3);
            Span<float> lab = stackalloc float[3];
            lab.Clear();
            toDev.Run(lab, dev, 1);
        }
        else if (p.Model == ColorModel.Cmyk)
        {
            dev.Fill(1f);
        }

        var fwd = new StagePipeline(p.BuildToPcs(intent), ChannelsOf(p.Model));
        Span<float> xyz = stackalloc float[3];
        fwd.Run(dev, xyz, 1);
        Span<float> l = stackalloc float[3];
        new XyzToLabStage().Apply(xyz, l, 1);
        float lum = Math.Min(Math.Max(l[0], 0f), 50f);
        Span<float> n = stackalloc float[3];
        n[0] = lum;
        new LabToXyzStage().Apply(n, xyz, 1);
        return [xyz[0], xyz[1], xyz[2]];
    }

    private static Grid Bake(StagePipeline exact)
    {
        int ins = exact.In;
        int points = ins switch { 1 => 1024, 3 => 33, _ => 17 };
        var grid = new Grid(ins, points, exact.Out);
        const int Batch = 4096;
        float[] input = new float[Batch * ins];
        float[] output = new float[Batch * exact.Out];
        int[] idx = new int[4];
        int total = grid.Entries;
        for (int start = 0; start < total; start += Batch)
        {
            int n = Math.Min(Batch, total - start);
            for (int e = 0; e < n; e++)
            {
                int rem = start + e;
                for (int a = ins - 1; a >= 0; a--)
                {
                    idx[a] = rem % points;
                    rem /= points;
                }

                for (int a = 0; a < ins; a++)
                {
                    input[(e * ins) + a] = idx[a] / (float)(points - 1);
                }
            }

            exact.Run(input, output, n);
            for (int e = 0; e < n; e++)
            {
                for (int c = 0; c < exact.Out; c++)
                {
                    grid.Data[((start + e) * 4) + c] = output[(e * exact.Out) + c];
                }
            }
        }

        return grid;
    }

    /// <summary>Converts color channel values (no alpha), exactly. Inputs are normalized device values; Lab uses natural units.</summary>
    public void ConvertExact(ReadOnlySpan<float> src, Span<float> dst, int n) => _exact.Run(src, dst, n);

    /// <summary>Converts color channel values using the integer-oriented fast path.</summary>
    public void ConvertFast(ReadOnlySpan<float> src, Span<float> dst, int n)
    {
        if (_grid != null)
        {
            GridKernel.Eval(_grid, src, dst, n);
        }
        else
        {
            _fastPipeline!.Run(src, dst, n);
        }
    }

    /// <summary>
    /// Converts one row between pixel formats through this transform. The source and destination color models must match the
    /// profiles. Straight and premultiplied alpha is preserved (premultiplied color is divided out and multiplied back);
    /// alpha is added as opaque or dropped when only one side has it. Integer sources take the fast path, float sources the exact one.
    /// </summary>
    public void ConvertRow(ReadOnlySpan<byte> source, PixelFormat sf, Span<byte> destination, PixelFormat df, int width)
    {
        const int Px = 512;
        int scs = sf.ColorChannels, dcs = df.ColorChannels;
        int sch = sf.ChannelCount, dch = df.ChannelCount;
        if (scs != InChannels || dcs != OutChannels)
        {
            throw new ImageOperationException($"Cannot convert {sf} to {df} with profiles '{Source}' and '{Destination}'.");
        }

        bool fast = sf.Sample is SampleType.U8 or SampleType.U16;
        float[] raw = ArrayPool<float>.Shared.Rent(Px * sch);
        float[] cin = ArrayPool<float>.Shared.Rent(Px * scs);
        float[] cout = ArrayPool<float>.Shared.Rent(Px * dcs);
        float[] outRaw = ArrayPool<float>.Shared.Rent(Px * dch);
        float[] alpha = ArrayPool<float>.Shared.Rent(Px);
        try
        {
            int sbpp = sf.BytesPerPixel, dbpp = df.BytesPerPixel;
            for (int start = 0; start < width; start += Px)
            {
                int n = Math.Min(Px, width - start);
                ConvertKernel.Unpack(source.Slice(start * sbpp, n * sbpp), sf.Sample, raw.AsSpan(0, n * sch));
                bool unpre = sf.Alpha == AlphaMode.Premultiplied;
                for (int i = 0; i < n; i++)
                {
                    float a = sf.HasAlpha ? raw[(i * sch) + scs] : 1f;
                    alpha[i] = a;
                    for (int c = 0; c < scs; c++)
                    {
                        float v = raw[(i * sch) + c];
                        cin[(i * scs) + c] = unpre ? (a > 0f ? v / a : 0f) : v;
                    }
                }

                if (fast)
                {
                    ConvertFast(cin, cout, n);
                }
                else
                {
                    ConvertExact(cin, cout, n);
                }

                bool pre = df.Alpha == AlphaMode.Premultiplied;
                for (int i = 0; i < n; i++)
                {
                    float a = alpha[i];
                    for (int c = 0; c < dcs; c++)
                    {
                        float v = cout[(i * dcs) + c];
                        outRaw[(i * dch) + c] = pre ? v * a : v;
                    }

                    if (df.HasAlpha)
                    {
                        outRaw[(i * dch) + dcs] = a;
                    }
                }

                ConvertKernel.Pack(outRaw.AsSpan(0, n * dch), df.Sample, destination.Slice(start * dbpp, n * dbpp));
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(raw);
            ArrayPool<float>.Shared.Return(cin);
            ArrayPool<float>.Shared.Return(cout);
            ArrayPool<float>.Shared.Return(outRaw);
            ArrayPool<float>.Shared.Return(alpha);
        }
    }
}
