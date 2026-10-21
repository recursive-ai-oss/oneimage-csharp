using System.Buffers.Binary;
using System.Text;

namespace OneImage;

/// <summary>Writes small ICC profiles deterministically: fixed date, no profile ID, tags in a fixed order, shared tag data deduplicated by the caller.</summary>
internal sealed class IccWriter
{
    private readonly List<(uint Sig, byte[] Data)> _tags = new();
    private readonly Dictionary<uint, int> _shared = new();

    public IccWriter(uint deviceClass, string space, string pcs, int major, int minor, uint renderingIntent = 0)
    {
        DeviceClass = deviceClass;
        Space = space;
        PcsSig = pcs;
        Major = major;
        Minor = minor;
        RenderingIntent = renderingIntent;
    }

    public uint DeviceClass { get; }

    public string Space { get; }

    public string PcsSig { get; }

    public int Major { get; }

    public int Minor { get; }

    public uint RenderingIntent { get; }

    /// <summary>Adds a tag. A tag whose data equals an earlier tag's data shares its storage.</summary>
    public void Add(string sig, byte[] data) => _tags.Add((IccData.Sig(sig), data));

    public static byte[] Xyz(double x, double y, double z)
    {
        var b = new byte[20];
        Put(b, 0, "XYZ ");
        S15(b, 8, x);
        S15(b, 12, y);
        S15(b, 16, z);
        return b;
    }

    /// <summary>Text description: v4 profiles use 'mluc' (UTF-16BE), v2 use 'desc' (ASCII).</summary>
    public byte[] Text(string text, bool description)
    {
        if (Major >= 4)
        {
            byte[] u = Encoding.BigEndianUnicode.GetBytes(text);
            var b = new byte[28 + u.Length];
            Put(b, 0, "mluc");
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), 12);
            Put(b, 16, "enUS");
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)u.Length);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(24), 28);
            u.CopyTo(b, 28);
            return b;
        }

        byte[] a = Encoding.ASCII.GetBytes(text);
        if (description)
        {
            // 'desc': ASCII count includes the terminating NUL; Unicode and ScriptCode sections are empty.
            var b = new byte[12 + a.Length + 1 + 4 + 4 + 3 + 67];
            Put(b, 0, "desc");
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)(a.Length + 1));
            a.CopyTo(b, 12);
            return b;
        }

        var t = new byte[8 + a.Length + 1];
        Put(t, 0, "text");
        a.CopyTo(t, 8);
        return t;
    }

    public static byte[] ParametricCurve(int type, params double[] p)
    {
        var b = new byte[12 + (p.Length * 4)];
        Put(b, 0, "para");
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8), (ushort)type);
        for (int i = 0; i < p.Length; i++)
        {
            S15(b, 12 + (i * 4), p[i]);
        }

        return b;
    }

    /// <summary>A 'curv' element: no entries is the identity, one entry is a gamma (u8Fixed8), otherwise a table of 16-bit samples.</summary>
    public static byte[] SampledCurve(ReadOnlySpan<ushort> table)
    {
        var b = new byte[12 + (table.Length * 2)];
        Put(b, 0, "curv");
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)table.Length);
        for (int i = 0; i < table.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(12 + (i * 2)), table[i]);
        }

        return b;
    }

    public static byte[] IdentityCurve() => SampledCurve([]);

    public static byte[] GammaCurve(double gamma) => SampledCurve([(ushort)Math.Round(gamma * 256.0)]);

    public byte[] Build()
    {
        // Lay out tag data, sharing identical blobs.
        int count = _tags.Count;
        int pos = 128 + 4 + (count * 12);
        var offsets = new int[count];
        var sizes = new int[count];
        var placed = new List<(byte[] Data, int Offset)>();
        for (int i = 0; i < count; i++)
        {
            byte[] d = _tags[i].Data;
            int found = -1;
            foreach ((byte[] Data, int Offset) p in placed)
            {
                if (p.Data.AsSpan().SequenceEqual(d))
                {
                    found = p.Offset;
                    break;
                }
            }

            if (found >= 0)
            {
                offsets[i] = found;
            }
            else
            {
                offsets[i] = pos;
                placed.Add((d, pos));
                pos += (d.Length + 3) & ~3;
            }

            sizes[i] = d.Length;
        }

        var o = new byte[pos];
        BinaryPrimitives.WriteUInt32BigEndian(o, (uint)pos);
        o[8] = (byte)Major;
        o[9] = (byte)(Minor << 4);
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(12), DeviceClass);
        Put(o, 16, Space);
        Put(o, 20, PcsSig);

        // Fixed creation date keeps output byte-identical between runs: 2024-01-01 00:00:00.
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(24), 2024);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(28), 1);
        Put(o, 36, "acsp");
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(64), RenderingIntent);
        S15(o, 68, OneImage.Pcs.D50X);
        S15(o, 72, OneImage.Pcs.D50Y);
        S15(o, 76, OneImage.Pcs.D50Z);
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(128), (uint)count);
        for (int i = 0; i < count; i++)
        {
            int e = 132 + (i * 12);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(e), _tags[i].Sig);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(e + 4), (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(e + 8), (uint)sizes[i]);
        }

        foreach ((byte[] data, int offset) in placed)
        {
            data.CopyTo(o, offset);
        }

        return o;
    }

    public static void Put(byte[] b, int offset, string sig)
    {
        for (int i = 0; i < 4; i++)
        {
            b[offset + i] = (byte)sig[i];
        }
    }

    public static void S15(byte[] b, int offset, double v) =>
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(offset), (int)Math.Round(v * 65536.0));
}
