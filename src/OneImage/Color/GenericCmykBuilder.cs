using System.Buffers.Binary;

namespace OneImage;

/// <summary>
/// Assembles the generic CMYK profile from lookup tables stored as an embedded resource. The tables were generated from the
/// FOGRA39 characterization data by the generator in the repository's tools folder; the profile text, including the library
/// name, is added here so a rename carries through.
/// </summary>
internal static class GenericCmykBuilder
{
    public static ColorProfile Build()
    {
        using Stream? s = typeof(GenericCmykBuilder).Assembly.GetManifestResourceStream("generic-cmyk.bin");
        if (s == null)
        {
            throw new ImageOperationException("The generic CMYK tables are missing from the library.");
        }

        byte[] bin = new byte[s.Length];
        s.ReadExactly(bin);
        ReadOnlySpan<byte> d = bin;
        if (BinaryPrimitives.ReadUInt32LittleEndian(d) != 0x31504D43u)
        {
            throw new ImageOperationException("The generic CMYK tables are corrupt.");
        }

        double[] white =
        [
            BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(d[4..])),
            BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(d[12..])),
            BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(d[20..])),
        ];
        int pos = 28;
        byte[] a2b = ReadBlob(bin, ref pos);
        byte[] b2a = ReadBlob(bin, ref pos);

        var w = new IccWriter(IccData.Sig("prtr"), "CMYK", "Lab ", 2, 4);
        w.Add("desc", w.Text(ColorProfile.Name("Generic CMYK (derived from FOGRA39 characterization data)"), true));
        w.Add("cprt", w.Text("Derived from FOGRA39 characterization data published by Fogra (www.fogra.org). Not a Fogra profile; no certification, approval or endorsement by Fogra is implied.", false));
        w.Add("wtpt", IccWriter.Xyz(white[0], white[1], white[2]));
        foreach (string t in new[] { "A2B0", "A2B1", "A2B2" })
        {
            w.Add(t, a2b);
        }

        foreach (string t in new[] { "B2A0", "B2A1", "B2A2" })
        {
            w.Add(t, b2a);
        }

        return ColorProfile.FromBytes(w.Build());
    }

    private static byte[] ReadBlob(byte[] bin, ref int pos)
    {
        int len = BinaryPrimitives.ReadInt32LittleEndian(bin.AsSpan(pos));
        pos += 4;
        byte[] r = bin.AsSpan(pos, len).ToArray();
        pos += len;
        return r;
    }
}
