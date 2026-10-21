using System.Buffers.Binary;
using System.Text;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// A parsed ICC profile (versions 2 and 4; device, display, output and color-space classes). Parsing is bounds-checked: every
/// offset and size comes from untrusted bytes and is validated against the buffer before use, and table sizes are checked before
/// anything is allocated. Unsupported profiles (DeviceLink, named color, abstract, exotic color spaces) throw
/// <see cref="ImageFormatException"/>.
/// </summary>
internal sealed class IccData
{
    public const int MaxProfileBytes = 1 << 24;
    private const int MaxTags = 4096;

    private static readonly uint s_acsp = Sig("acsp");

    private readonly byte[] _data;
    private readonly Dictionary<uint, (int Offset, int Size)> _tags = new();

    private IccData(byte[] data)
    {
        _data = data;
    }

    public ColorModel Model { get; private set; }

    public int VersionMajor { get; private set; }

    public int VersionMinor { get; private set; }

    public uint DeviceClass { get; private set; }

    /// <summary>True when the PCS is L*a*b*, false for XYZ.</summary>
    public bool PcsIsLab { get; private set; }

    public ReadOnlySpan<byte> Bytes => _data;

    public string Description { get; private set; } = string.Empty;

    /// <summary>Media white point in PCS XYZ (defaults to D50 when the tag is missing).</summary>
    public double[] MediaWhite { get; private set; } = [Pcs.D50X, Pcs.D50Y, Pcs.D50Z];

    public static uint Sig(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    public static IccData Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 132)
        {
            throw new ImageFormatException("ICC profile is shorter than its header.");
        }

        if (bytes.Length > MaxProfileBytes)
        {
            throw new ImageLimitException("ICC profile is larger than the supported maximum.");
        }

        uint size = U32(bytes, 0);
        if (size < 132 || size > bytes.Length)
        {
            throw new ImageFormatException("ICC profile size field is inconsistent with the data.");
        }

        var p = new IccData(bytes[..(int)size].ToArray());
        p.ParseHeader();
        return p;
    }

    private void ParseHeader()
    {
        ReadOnlySpan<byte> d = _data;
        if (U32(d, 36) != s_acsp)
        {
            throw new ImageFormatException("Not an ICC profile: signature 'acsp' missing.");
        }

        VersionMajor = d[8];
        VersionMinor = d[9] >> 4;
        if (VersionMajor is < 2 or > 4)
        {
            throw new ImageFormatException($"Unsupported ICC version {VersionMajor}.");
        }

        DeviceClass = U32(d, 12);
        if (DeviceClass != Sig("scnr") && DeviceClass != Sig("mntr") && DeviceClass != Sig("prtr") && DeviceClass != Sig("spac"))
        {
            throw new ImageFormatException("Unsupported ICC profile class (only input, display, output and color-space profiles).");
        }

        uint space = U32(d, 16);
        Model = space == Sig("RGB ") ? ColorModel.Rgb
            : space == Sig("GRAY") ? ColorModel.Gray
            : space == Sig("CMYK") ? ColorModel.Cmyk
            : space == Sig("Lab ") ? ColorModel.Lab
            : throw new ImageFormatException("Unsupported ICC data color space.");
        uint pcs = U32(d, 20);
        if (pcs == Sig("Lab "))
        {
            PcsIsLab = true;
        }
        else if (pcs != Sig("XYZ "))
        {
            throw new ImageFormatException("Unsupported ICC profile connection space.");
        }

        uint count = U32(d, 128);
        if (count > MaxTags || count > (uint)((d.Length - 132) / 12))
        {
            throw new ImageFormatException("ICC tag table does not fit in the profile.");
        }

        for (int i = 0; i < count; i++)
        {
            int e = 132 + (i * 12);
            uint sig = U32(d, e);
            uint off = U32(d, e + 4);
            uint sz = U32(d, e + 8);
            if (off > d.Length || sz > d.Length - off || sz < 8)
            {
                throw new ImageFormatException("ICC tag lies outside the profile.");
            }

            _tags.TryAdd(sig, ((int)off, (int)sz));
        }

        Description = ReadDescription();
        if (_tags.ContainsKey(Sig("wtpt")))
        {
            MediaWhite = ReadXyz(Sig("wtpt"));
        }

        // A profile must be usable: matrix/TRC, gray TRC, or at least one lookup table.
        bool usable = Model switch
        {
            ColorModel.Lab => true,
            ColorModel.Rgb => HasMatrixShaper() || HasTag("A2B0") || HasTag("A2B1"),
            ColorModel.Gray => HasTag("kTRC") || HasTag("A2B0") || HasTag("A2B1"),
            _ => HasTag("A2B0") || HasTag("A2B1"),
        };
        if (!usable)
        {
            throw new ImageFormatException("ICC profile has no usable transform tags.");
        }
    }

    public bool HasTag(string sig) => _tags.ContainsKey(Sig(sig));

    public bool HasMatrixShaper() =>
        HasTag("rXYZ") && HasTag("gXYZ") && HasTag("bXYZ") && HasTag("rTRC") && HasTag("gTRC") && HasTag("bTRC");

    /// <summary>True when the profile converts through colorants and curves only (no lookup table) for this intent and direction.</summary>
    public bool IsMatrixShaper(RenderingIntent intent, bool output) =>
        Model switch
        {
            ColorModel.Rgb => HasMatrixShaper() && !HasLut(intent, output),
            ColorModel.Gray => HasTag("kTRC") && !HasLut(intent, output),
            _ => false,
        };

    /// <summary>True for matrix/TRC (or gray TRC) profiles whose curves are all linear.</summary>
    public bool IsLinearMatrixShaper
    {
        get
        {
            try
            {
                if (IsMatrixShaper(RenderingIntent.RelativeColorimetric, output: false))
                {
                    return Model == ColorModel.Gray
                        ? ReadCurve(Tag("kTRC"), out _).IsIdentity
                        : ReadTrc3().All(c => c.IsIdentity);
                }
            }
            catch (ImageException)
            {
                // A broken curve means the profile is not usable as linear.
            }

            return false;
        }
    }

    public bool HasLut(RenderingIntent intent, bool output) => FindLut(intent, output) != 0;

    private uint FindLut(RenderingIntent intent, bool output)
    {
        string kind = output ? "B2A" : "A2B";
        int idx = intent == RenderingIntent.Saturation ? 2 : intent == RenderingIntent.Perceptual ? 0 : 1;
        foreach (int i in new[] { idx, 0 })
        {
            uint sig = Sig(kind + (char)('0' + i));
            if (_tags.ContainsKey(sig))
            {
                return sig;
            }
        }

        return 0;
    }

    private ReadOnlySpan<byte> Tag(uint sig)
    {
        if (!_tags.TryGetValue(sig, out (int Offset, int Size) t))
        {
            throw new ImageFormatException("Required ICC tag is missing.");
        }

        return _data.AsSpan(t.Offset, t.Size);
    }

    private ReadOnlySpan<byte> Tag(string sig) => Tag(Sig(sig));

    private double[] ReadXyz(uint sig)
    {
        ReadOnlySpan<byte> t = Tag(sig);
        if (t.Length < 20 || U32(t, 0) != Sig("XYZ "))
        {
            throw new ImageFormatException("Malformed XYZ tag.");
        }

        return [S15(t, 8), S15(t, 12), S15(t, 16)];
    }

    private string ReadDescription()
    {
        if (!_tags.TryGetValue(Sig("desc"), out (int Offset, int Size) t))
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> d = _data.AsSpan(t.Offset, t.Size);
        uint type = U32(d, 0);
        try
        {
            if (type == Sig("mluc") && d.Length >= 28)
            {
                uint n = U32(d, 8);
                uint rec = U32(d, 12);
                if (n >= 1 && rec >= 12)
                {
                    uint len = U32(d, 20);
                    uint off = U32(d, 24);
                    if (off <= d.Length && len <= d.Length - off)
                    {
                        return Encoding.BigEndianUnicode.GetString(d.Slice((int)off, (int)(len & ~1u)));
                    }
                }
            }
            else if (type == Sig("desc") && d.Length >= 12)
            {
                uint len = U32(d, 8);
                if (len <= d.Length - 12)
                {
                    return Encoding.Latin1.GetString(d.Slice(12, (int)len)).TrimEnd('\0');
                }
            }
        }
        catch (ArgumentException)
        {
            // A malformed description string is cosmetic; keep the profile usable.
        }

        return string.Empty;
    }

    // ---- Pipelines ------------------------------------------------------------------------------------------------

    /// <summary>Builds the device to PCS (XYZ, D50, Y of white = 1) stages for the given intent.</summary>
    public List<Stage> BuildToPcs(RenderingIntent intent)
    {
        var stages = new List<Stage>();
        if (Model == ColorModel.Lab)
        {
            stages.Add(new LabToXyzStage());
            return stages;
        }

        uint lut = FindLut(intent, output: false);
        if (lut != 0)
        {
            ReadLut(Tag(lut), toPcs: true, stages);
            return stages;
        }

        if (Model == ColorModel.Gray)
        {
            stages.Add(new CurveStage([ReadCurve(Tag("kTRC"), out _)]));
            stages.Add(new GrayToXyzStage());
            return stages;
        }

        stages.Add(new CurveStage(ReadTrc3()));
        stages.Add(new MatrixStage(ReadColorants(), []));
        return stages;
    }

    /// <summary>Builds the PCS (XYZ) to device stages for the given intent.</summary>
    public List<Stage> BuildFromPcs(RenderingIntent intent)
    {
        var stages = new List<Stage>();
        if (Model == ColorModel.Lab)
        {
            stages.Add(new XyzToLabStage());
            return stages;
        }

        uint lut = FindLut(intent, output: true);
        if (lut != 0)
        {
            ReadLut(Tag(lut), toPcs: false, stages);
            return stages;
        }

        if (Model == ColorModel.Gray)
        {
            stages.Add(new XyzToGrayStage());
            stages.Add(new CurveStage([ReadCurve(Tag("kTRC"), out _).Inverse()]));
            return stages;
        }

        if (Model != ColorModel.Rgb)
        {
            throw new ImageFormatException("This profile cannot be used as a destination: it has no PCS to device table.");
        }

        stages.Add(new MatrixStage(Invert3(ReadColorants()), []));
        ToneCurve[] trc = ReadTrc3();
        for (int i = 0; i < 3; i++)
        {
            trc[i] = trc[i].Inverse();
        }

        stages.Add(new CurveStage(trc));
        return stages;
    }

    private ToneCurve[] ReadTrc3() =>
        [ReadCurve(Tag("rTRC"), out _), ReadCurve(Tag("gTRC"), out _), ReadCurve(Tag("bTRC"), out _)];

    /// <summary>Colorant matrix with columns r, g, b (row-major 3x3).</summary>
    public double[] ReadColorants()
    {
        double[] r = ReadXyz(Sig("rXYZ"));
        double[] g = ReadXyz(Sig("gXYZ"));
        double[] b = ReadXyz(Sig("bXYZ"));
        return [r[0], g[0], b[0], r[1], g[1], b[1], r[2], g[2], b[2]];
    }

    public static double[] Invert3(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double det = (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
        if (Math.Abs(det) < 1e-12 || double.IsNaN(det))
        {
            throw new ImageFormatException("ICC colorant matrix is singular.");
        }

        double s = 1.0 / det;
        return
        [
            ((e * i) - (f * h)) * s, ((c * h) - (b * i)) * s, ((b * f) - (c * e)) * s,
            ((f * g) - (d * i)) * s, ((a * i) - (c * g)) * s, ((c * d) - (a * f)) * s,
            ((d * h) - (e * g)) * s, ((b * g) - (a * h)) * s, ((a * e) - (b * d)) * s,
        ];
    }

    // ---- Curves ---------------------------------------------------------------------------------------------------

    /// <summary>Reads a 'curv' or 'para' element at the start of <paramref name="d"/>; <paramref name="consumed"/> is its padded length.</summary>
    private static ToneCurve ReadCurve(ReadOnlySpan<byte> d, out int consumed)
    {
        uint type = U32(d, 0);
        if (type == Sig("curv"))
        {
            uint n = U32(d, 8);
            if (n > (uint)((d.Length - 12) / 2))
            {
                throw new ImageFormatException("ICC curve is longer than its tag.");
            }

            consumed = Pad4(12 + ((int)n * 2));
            if (n == 0)
            {
                return ToneCurve.Identity;
            }

            if (n == 1)
            {
                return ToneCurve.Gamma(U16(d, 12) / 256.0);
            }

            var t = new float[n];
            for (int i = 0; i < t.Length; i++)
            {
                t[i] = U16(d, 12 + (i * 2)) / 65535f;
            }

            return ToneCurve.FromTable(t);
        }

        if (type == Sig("para"))
        {
            ushort ft = U16(d, 8);
            int np = ft switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => throw new ImageFormatException("Unknown ICC parametric curve type.") };
            if (d.Length < 12 + (np * 4))
            {
                throw new ImageFormatException("ICC parametric curve is truncated.");
            }

            Span<double> p = stackalloc double[7];
            for (int i = 0; i < np; i++)
            {
                p[i] = S15(d, 12 + (i * 4));
            }

            consumed = Pad4(12 + (np * 4));
            return ToneCurve.Parametric(ft, p);
        }

        throw new ImageFormatException("Unknown ICC curve element type.");
    }

    private static ToneCurve[] ReadCurveSet(ReadOnlySpan<byte> tag, uint offset, int count)
    {
        if (offset >= tag.Length)
        {
            throw new ImageFormatException("ICC curve set lies outside its tag.");
        }

        var curves = new ToneCurve[count];
        int pos = (int)offset;
        for (int i = 0; i < count; i++)
        {
            if (pos >= tag.Length)
            {
                throw new ImageFormatException("ICC curve set is truncated.");
            }

            curves[i] = ReadCurve(tag[pos..], out int used);
            pos += used;
        }

        return curves;
    }

    // ---- Lookup tables --------------------------------------------------------------------------------------------

    private void ReadLut(ReadOnlySpan<byte> t, bool toPcs, List<Stage> stages)
    {
        uint type = U32(t, 0);
        if (type == Sig("mft1") || type == Sig("mft2"))
        {
            ReadLutLegacy(t, type == Sig("mft2"), toPcs, stages);
        }
        else if (type == Sig("mAB ") || type == Sig("mBA "))
        {
            if ((type == Sig("mAB ")) != toPcs)
            {
                throw new ImageFormatException("ICC lookup table direction does not match its tag.");
            }

            ReadLutMab(t, toPcs, stages);
        }
        else
        {
            throw new ImageFormatException("Unsupported ICC lookup table type.");
        }
    }

    private int DeviceChannels => Model switch { ColorModel.Gray => 1, ColorModel.Cmyk => 4, _ => 3 };

    private void CheckLutShape(int inCh, int outCh, bool toPcs)
    {
        int dev = DeviceChannels;
        if (toPcs ? inCh != dev || outCh != 3 : inCh != 3 || outCh != dev)
        {
            throw new ImageFormatException("ICC lookup table channel counts do not match the profile color space.");
        }
    }

    private void ReadLutLegacy(ReadOnlySpan<byte> t, bool is16, bool toPcs, List<Stage> stages)
    {
        if (t.Length < 48)
        {
            throw new ImageFormatException("ICC lookup table is truncated.");
        }

        int inCh = t[8];
        int outCh = t[9];
        int grid = t[10];
        CheckLutShape(inCh, outCh, toPcs);
        if (grid < 2)
        {
            throw new ImageFormatException("ICC lookup table grid is too small.");
        }

        int inEntries = 256, outEntries = 256, pos = 48;
        if (is16)
        {
            if (t.Length < 52)
            {
                throw new ImageFormatException("ICC lookup table is truncated.");
            }

            inEntries = U16(t, 48);
            outEntries = U16(t, 50);
            pos = 52;
            if (inEntries is < 2 or > 4096 || outEntries is < 2 or > 4096)
            {
                throw new ImageFormatException("ICC lookup table curve size is out of range.");
            }
        }

        int bps = is16 ? 2 : 1;
        long inBytes = (long)inCh * inEntries * bps;
        long clutEntries = 1;
        for (int i = 0; i < inCh; i++)
        {
            clutEntries *= grid;
            if (clutEntries > (1 << 22))
            {
                throw new ImageLimitException("ICC lookup table is too large.");
            }
        }

        long clutBytes = clutEntries * outCh * bps;
        long outBytes = (long)outCh * outEntries * bps;
        if (pos + inBytes + clutBytes + outBytes > t.Length)
        {
            throw new ImageFormatException("ICC lookup table data is truncated.");
        }

        ToneCurve[] inCurves = ReadTables(t, ref pos, inCh, inEntries, is16);
        var g = new Grid(inCh, grid, outCh);
        float scale = is16 ? 1f / 65535f : 1f / 255f;
        for (int e = 0; e < g.Entries; e++)
        {
            for (int c = 0; c < outCh; c++)
            {
                g.Data[(e * 4) + c] = (is16 ? U16(t, pos) : t[pos]) * scale;
                pos += bps;
            }
        }

        ToneCurve[] outCurves = ReadTables(t, ref pos, outCh, outEntries, is16);
        PcsEncoding enc = is16 ? PcsEncoding.LegacyLab16 : PcsEncoding.Lab;
        if (toPcs)
        {
            stages.Add(new CurveStage(inCurves));
            stages.Add(new GridStage(g));
            stages.Add(new CurveStage(outCurves));
            AddPcsDecode(stages, enc, toPcs: true);
        }
        else
        {
            AddPcsDecode(stages, enc, toPcs: false);
            if (!PcsIsLab)
            {
                // The matrix applies only to XYZ input.
                var m = new double[9];
                for (int i = 0; i < 9; i++)
                {
                    m[i] = S15(t, 12 + (i * 4));
                }

                if (!IsIdentity3(m))
                {
                    stages.Add(new MatrixStage(m, []));
                }
            }

            stages.Add(new CurveStage(inCurves));
            stages.Add(new GridStage(g));
            stages.Add(new CurveStage(outCurves));
        }
    }

    private static bool IsIdentity3(double[] m) =>
        m[0] == 1 && m[4] == 1 && m[8] == 1 && m[1] == 0 && m[2] == 0 && m[3] == 0 && m[5] == 0 && m[6] == 0 && m[7] == 0;

    private static ToneCurve[] ReadTables(ReadOnlySpan<byte> t, ref int pos, int channels, int entries, bool is16)
    {
        var curves = new ToneCurve[channels];
        for (int c = 0; c < channels; c++)
        {
            var tab = new float[entries];
            for (int i = 0; i < entries; i++)
            {
                if (is16)
                {
                    tab[i] = U16(t, pos) / 65535f;
                    pos += 2;
                }
                else
                {
                    tab[i] = t[pos++] / 255f;
                }
            }

            curves[c] = ToneCurve.FromTable(tab);
        }

        return curves;
    }

    private void ReadLutMab(ReadOnlySpan<byte> t, bool toPcs, List<Stage> stages)
    {
        if (t.Length < 32)
        {
            throw new ImageFormatException("ICC lookup table is truncated.");
        }

        int inCh = t[8];
        int outCh = t[9];
        CheckLutShape(inCh, outCh, toPcs);
        uint offB = U32(t, 12), offMat = U32(t, 16), offM = U32(t, 20), offClut = U32(t, 24), offA = U32(t, 28);

        // Stage order for A2B is A, CLUT, M, matrix, B; for B2A it is B, matrix, M, CLUT, A (ICC.1:2010 10.12 and 10.13).
        ToneCurve[]? b = offB != 0 ? ReadCurveSet(t, offB, 3) : null;
        ToneCurve[]? m = offM != 0 ? ReadCurveSet(t, offM, 3) : null;
        ToneCurve[]? a = offA != 0 ? ReadCurveSet(t, offA, toPcs ? inCh : outCh) : null;
        MatrixStage? mat = null;
        if (offMat != 0)
        {
            if (offMat > t.Length || t.Length - offMat < 48)
            {
                throw new ImageFormatException("ICC matrix lies outside its tag.");
            }

            var mm = new double[9];
            var oo = new double[3];
            for (int i = 0; i < 9; i++)
            {
                mm[i] = S15(t, (int)offMat + (i * 4));
            }

            for (int i = 0; i < 3; i++)
            {
                oo[i] = S15(t, (int)offMat + 36 + (i * 4));
            }

            mat = new MatrixStage(mm, oo);
        }

        GridStage? clut = null;
        if (offClut != 0)
        {
            if (offClut > t.Length || t.Length - offClut < 20)
            {
                throw new ImageFormatException("ICC CLUT lies outside its tag.");
            }

            int co = (int)offClut;
            var dims = new int[inCh];
            long entries = 1;
            for (int i = 0; i < inCh; i++)
            {
                dims[i] = t[co + i];
                if (dims[i] < 2)
                {
                    throw new ImageFormatException("ICC CLUT grid is too small.");
                }

                entries *= dims[i];
                if (entries > (1 << 22))
                {
                    throw new ImageLimitException("ICC lookup table is too large.");
                }
            }

            int prec = t[co + 16];
            if (prec is not (1 or 2))
            {
                throw new ImageFormatException("ICC CLUT precision must be 1 or 2 bytes.");
            }

            if (co + 20 + (entries * outCh * prec) > t.Length)
            {
                throw new ImageFormatException("ICC CLUT data is truncated.");
            }

            var g = new Grid(inCh, dims, outCh);
            int pos = co + 20;
            float scale = prec == 2 ? 1f / 65535f : 1f / 255f;
            for (int e = 0; e < g.Entries; e++)
            {
                for (int c = 0; c < outCh; c++)
                {
                    g.Data[(e * 4) + c] = (prec == 2 ? U16(t, pos) : t[pos]) * scale;
                    pos += prec;
                }
            }

            clut = new GridStage(g);
        }
        else if (inCh != outCh)
        {
            throw new ImageFormatException("ICC lookup table without CLUT must keep the channel count.");
        }

        if (toPcs)
        {
            if (a != null)
            {
                stages.Add(new CurveStage(a));
            }

            if (clut != null)
            {
                stages.Add(clut);
            }

            if (m != null)
            {
                stages.Add(new CurveStage(m));
            }

            if (mat != null)
            {
                stages.Add(mat);
            }

            if (b != null)
            {
                stages.Add(new CurveStage(b));
            }

            AddPcsDecode(stages, PcsEncoding.Lab, toPcs: true);
        }
        else
        {
            AddPcsDecode(stages, PcsEncoding.Lab, toPcs: false);
            if (b != null)
            {
                stages.Add(new CurveStage(b));
            }

            if (mat != null)
            {
                stages.Add(mat);
            }

            if (m != null)
            {
                stages.Add(new CurveStage(m));
            }

            if (clut != null)
            {
                stages.Add(clut);
            }

            if (a != null)
            {
                stages.Add(new CurveStage(a));
            }
        }
    }

    private enum PcsEncoding
    {
        Lab,
        LegacyLab16,
    }

    /// <summary>
    /// Converts between the normalized (0..1) lookup table encoding of the PCS and real XYZ. Lab uses the v4 encoding (L = 100x,
    /// a = 255x - 128) except in 16-bit lut16 tables, which keep the legacy v2 encoding (L = 100 * 65535/65280 x) in every profile version.
    /// XYZ is encoded as x * 65535/32768.
    /// </summary>
    private void AddPcsDecode(List<Stage> stages, PcsEncoding enc, bool toPcs)
    {
        if (PcsIsLab)
        {
            double[] scale, offset;
            if (enc == PcsEncoding.LegacyLab16)
            {
                scale = [100.0 * 65535 / 65280, 65535.0 / 256, 65535.0 / 256];
                offset = [0, -128, -128];
            }
            else
            {
                scale = [100, 255, 255];
                offset = [0, -128, -128];
            }

            if (toPcs)
            {
                stages.Add(new ScaleStage(scale, offset));
                stages.Add(new LabToXyzStage());
            }
            else
            {
                stages.Add(new XyzToLabStage());
                var invScale = new double[3];
                var invOffset = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    invScale[i] = 1.0 / scale[i];
                    invOffset[i] = -offset[i] / scale[i];
                }

                stages.Add(new ScaleStage(invScale, invOffset));
            }
        }
        else
        {
            double k = 65535.0 / 32768;
            stages.Add(toPcs ? new ScaleStage([k, k, k], [0, 0, 0]) : new ScaleStage([1 / k, 1 / k, 1 / k], [0, 0, 0]));
        }
    }

    // ---- Primitive readers (all bounds-checked) -------------------------------------------------------------------

    public static uint U32(ReadOnlySpan<byte> d, int offset)
    {
        if ((uint)offset > (uint)d.Length || d.Length - offset < 4)
        {
            throw new ImageFormatException("ICC data is truncated.");
        }

        return BinaryPrimitives.ReadUInt32BigEndian(d[offset..]);
    }

    public static ushort U16(ReadOnlySpan<byte> d, int offset)
    {
        if ((uint)offset > (uint)d.Length || d.Length - offset < 2)
        {
            throw new ImageFormatException("ICC data is truncated.");
        }

        return BinaryPrimitives.ReadUInt16BigEndian(d[offset..]);
    }

    public static double S15(ReadOnlySpan<byte> d, int offset) => (int)U32(d, offset) / 65536.0;

    private static int Pad4(int n) => (n + 3) & ~3;
}
