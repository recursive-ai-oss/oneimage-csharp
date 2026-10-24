using OneImage.Text.Fonts;

namespace OneImage.Text;

/// <summary>One face together with the facts derived from it once.</summary>
internal sealed class FaceEntry
{
    public FaceEntry(FontFace data)
    {
        Data = data;
        Metrics = new FontMetrics(data.Inner.Metrics);
        Style = data.IsBold ? (data.IsItalic ? FontStyle.BoldItalic : FontStyle.Bold) : (data.IsItalic ? FontStyle.Italic : FontStyle.Regular);
    }

    public FontFace Data { get; }

    public FontMetrics Metrics { get; }

    public FontStyle Style { get; }
}

/// <summary>The faces that share a family name: up to one per <see cref="FontStyle"/>. Thread-safe.</summary>
public sealed class FontFamily
{
    private readonly object _gate = new();
    private FaceEntry?[] _faces = new FaceEntry?[4];

    internal FontFamily(string name)
    {
        Name = name;
    }

    /// <summary>Gets the family name from the fonts' name tables.</summary>
    public string Name { get; }

    /// <summary>Lists the styles this family has a face for.</summary>
    /// <returns>The available styles, in <see cref="FontStyle"/> order.</returns>
    public IReadOnlyList<FontStyle> GetAvailableStyles()
    {
        var list = new List<FontStyle>(4);
        FaceEntry?[] faces = _faces;
        for (int i = 0; i < faces.Length; i++)
        {
            if (faces[i] is not null)
            {
                list.Add((FontStyle)i);
            }
        }

        return list;
    }

    /// <summary>Tells whether the family has a face with exactly this style.</summary>
    /// <param name="style">The style.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public bool IsStyleAvailable(FontStyle style) => (uint)style < 4 && _faces[(int)style] is not null;

    /// <summary>Creates a font of this family.</summary>
    /// <param name="size">The size in points; at the default 72 dpi one point is one pixel.</param>
    /// <param name="style">The style. Without such a face the closest one is used (no synthetic bold or slant) and the font's <see cref="Font.Style"/> reports it.</param>
    /// <returns>The font.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is not a positive finite number.</exception>
    public Font CreateFont(float size, FontStyle style = FontStyle.Regular) => new(this, size, style);

    internal void Add(FaceEntry face)
    {
        lock (_gate)
        {
            var copy = (FaceEntry?[])_faces.Clone();
            copy[(int)face.Style] = face;
            _faces = copy;
        }
    }

    internal FaceEntry Select(FontStyle style, out FontStyle actual)
    {
        FaceEntry?[] faces = _faces;
        if ((uint)style < 4 && faces[(int)style] is FaceEntry exact)
        {
            actual = style;
            return exact;
        }

        // Prefer a face of the same slant, then the nearer weight.
        bool wantBold = style is FontStyle.Bold or FontStyle.BoldItalic;
        bool wantItalic = style is FontStyle.Italic or FontStyle.BoldItalic;
        FaceEntry? best = null;
        int bestScore = int.MaxValue;
        foreach (FaceEntry? f in faces)
        {
            if (f is null)
            {
                continue;
            }

            bool bold = f.Style is FontStyle.Bold or FontStyle.BoldItalic;
            bool italic = f.Style is FontStyle.Italic or FontStyle.BoldItalic;
            int score = (italic != wantItalic ? 2 : 0) + (bold != wantBold ? 1 : 0);
            if (score < bestScore)
            {
                best = f;
                bestScore = score;
            }
        }

        if (best is null)
        {
            throw new InvalidOperationException("The font family has no faces.");
        }

        actual = best.Style;
        return best;
    }
}
