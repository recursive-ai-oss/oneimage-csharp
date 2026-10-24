namespace OneImage.Text.Fonts;

/// <summary>Reads strings from the <c>name</c> table.</summary>
internal static class NameTable
{
    public const int Copyright = 0;
    public const int Family = 1;
    public const int Subfamily = 2;
    public const int FullName = 4;
    public const int PostScriptName = 6;
    public const int TypographicFamily = 16;
    public const int TypographicSubfamily = 17;

    /// <summary>Finds a name, preferring Windows English, then any Windows or Unicode record, then Macintosh Roman.</summary>
    public static string? Get(ReadOnlyMemory<byte> table, int nameId)
    {
        ReadOnlySpan<byte> d = table.Span;
        if (d.Length < 6)
        {
            return null;
        }

        int count = Be.U16(d, 2);
        int strings = Be.U16(d, 4);
        if (6L + (12L * count) > d.Length)
        {
            count = (d.Length - 6) / 12;
        }

        int bestScore = -1;
        string? best = null;
        for (int i = 0; i < count; i++)
        {
            int r = 6 + (12 * i);
            if (Be.U16(d, r + 6) != nameId)
            {
                continue;
            }

            int platform = Be.U16(d, r);
            int lang = Be.U16(d, r + 4);
            int score = platform switch
            {
                3 => lang == 0x409 ? 4 : 3,
                0 => 2,
                1 => 1,
                _ => -1,
            };
            if (score <= bestScore)
            {
                continue;
            }

            int len = Be.U16(d, r + 8);
            int off = strings + Be.U16(d, r + 10);
            if (off < 0 || (long)off + len > d.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> s = d.Slice(off, len);
            best = platform == 1 ? System.Text.Encoding.Latin1.GetString(s) : System.Text.Encoding.BigEndianUnicode.GetString(s[..(len & ~1)]);
            bestScore = score;
        }

        return best;
    }
}
