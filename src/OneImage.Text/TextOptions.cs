using System.Numerics;

namespace OneImage.Text;

/// <summary>
/// Everything that decides where text goes: the font, an origin, wrapping, spacing and alignment. A plain mutable object; copy it
/// with <see cref="Clone"/>. The drawing and measuring calls read it when they run and keep no reference.
/// </summary>
/// <remarks>
/// Units: <see cref="Font.Size"/> is in points; a point is <see cref="Dpi"/>/72 pixels, so with the default 72 dpi a size is a
/// pixel height of the em square. All positions and lengths here are pixels with y pointing down.
/// </remarks>
public sealed class TextOptions
{
    private float _lineSpacing = 1f;
    private float _tabWidth = 4f;
    private float _dpi = 72f;
    private float _wrappingLength;
    private Vector2 _origin;
    private Font _font;
    private int _colorPalette;

    /// <summary>Creates options for a font with every other setting at its default.</summary>
    /// <param name="font">The font.</param>
    /// <exception cref="ArgumentNullException"><paramref name="font"/> is null.</exception>
    public TextOptions(Font font)
    {
        ArgumentNullException.ThrowIfNull(font);
        _font = font;
    }

    /// <summary>Gets or sets the font.</summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public Font Font
    {
        get => _font;
        set => _font = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets or sets the anchor of the text block in pixels. How the block sits on it is set by <see cref="HorizontalAlignment"/> and <see cref="VerticalAlignment"/>. The default is (0, 0).</summary>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is NaN or infinite.</exception>
    public Vector2 Origin
    {
        get => _origin;
        set
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The origin must be finite.");
            }

            _origin = value;
        }
    }

    /// <summary>
    /// Gets or sets the length in pixels at which lines wrap; 0 or less means no wrapping (the default). Lines end at the break
    /// opportunities of the Unicode line breaking algorithm (see <see cref="WordBreaking"/>); by default a word longer than the
    /// length keeps its own line and overflows. Explicit
    /// line breaks (LF, CR, CRLF, U+0085, U+2028, U+2029) always break.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is NaN or infinite.</exception>
    public float WrappingLength
    {
        get => _wrappingLength;
        set
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The wrapping length must be finite.");
            }

            _wrappingLength = value;
        }
    }

    /// <summary>
    /// Gets or sets the line pitch as a multiple of the font size in pixels (not of the font's own line height). The default is 1, so
    /// lines are one em apart. The glyphs of a line are placed so any difference from the font's natural line height is split
    /// evenly above and below.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a positive finite number.</exception>
    public float LineSpacing
    {
        get => _lineSpacing;
        set
        {
            if (!(value > 0) || !float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The line spacing must be a positive finite number.");
            }

            _lineSpacing = value;
        }
    }

    /// <summary>Gets or sets the width of a tab in space characters of the font. The default is 4.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative, NaN or infinite.</exception>
    public float TabWidth
    {
        get => _tabWidth;
        set
        {
            if (!(value >= 0) || !float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The tab width must be zero or more.");
            }

            _tabWidth = value;
        }
    }

    /// <summary>Gets or sets the resolution that turns points into pixels: pixels = points × dpi / 72. The default is 72.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a positive finite number.</exception>
    public float Dpi
    {
        get => _dpi;
        set
        {
            if (!(value > 0) || !float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The dpi must be a positive finite number.");
            }

            _dpi = value;
        }
    }

    /// <summary>Gets or sets where the block sits horizontally relative to the origin. The default is <see cref="HorizontalAlignment.Left"/>. The block is as wide as the widest line (for <see cref="TextAlignment.Start"/>) or as the wrapping length (for Center and End); see <see cref="TextAlignment"/>.</summary>
    public HorizontalAlignment HorizontalAlignment { get; set; }

    /// <summary>Gets or sets where the block sits vertically relative to the origin. The default is <see cref="VerticalAlignment.Top"/>.</summary>
    public VerticalAlignment VerticalAlignment { get; set; }

    /// <summary>
    /// Gets or sets how each line lines up. The default is <see cref="TextAlignment.Start"/>: every line starts at the left edge of
    /// a box as wide as the widest line, and <see cref="HorizontalAlignment"/> puts that box on the origin. Center and End instead
    /// place each line in a box as wide as <see cref="WrappingLength"/> (the widest line when there is none), centered or against
    /// its right edge, and horizontal alignment puts that box on the origin. This is how ImageSharp lays text out; Center and End
    /// are meant for use with a wrapping length.
    /// </summary>
    public TextAlignment TextAlignment { get; set; }

    /// <summary>Gets or sets whether the font's kerning is applied. The default is <see cref="KerningMode.Normal"/>.</summary>
    public KerningMode Kerning { get; set; }

    /// <summary>
    /// Gets or sets the language of the text as a BCP 47 tag (<c>"sr"</c>, <c>"bg"</c>, <c>"tr"</c>, <c>"ro-RO"</c>), used to choose the font's
    /// language-specific forms (the <c>locl</c> feature: Serbian or Bulgarian Cyrillic letters, Romanian comma accents, Turkish dotless i
    /// and so on). The default, null, uses the font's default forms. A language the font does not know also uses them. An OpenType
    /// language tag written in capitals (<c>"SRB"</c>) is accepted as is.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets the OpenType feature settings applied on top of the defaults. For horizontal Latin, Greek and Cyrillic text the defaults are
    /// <c>ccmp</c>, <c>locl</c>, <c>rlig</c>, <c>liga</c>, <c>clig</c>, <c>calt</c>, <c>rclt</c>, <c>kern</c>, <c>mark</c>, <c>mkmk</c> and <c>curs</c>.
    /// Settings are read in order and a later setting for the same tag wins; one for <c>kern</c> overrides <see cref="Kerning"/>.
    /// </summary>
    public IList<FontFeature> Features { get; } = [];

    /// <summary>
    /// Gets or sets where wrapped lines may end: at the Unicode line break opportunities (the default), between any two characters,
    /// not within East Asian text, or with long words broken. See <see cref="Text.WordBreaking"/>.
    /// </summary>
    public WordBreaking WordBreaking { get; set; }

    /// <summary>
    /// Gets or sets how lines are stretched to <see cref="WrappingLength"/>. The default is <see cref="Text.TextJustification.None"/>.
    /// Justification needs a wrapping length and does nothing without one.
    /// </summary>
    public TextJustification TextJustification { get; set; }

    /// <summary>
    /// Gets or sets which color glyph formats are painted. The default is <see cref="ColorFontSupport.None"/>: every glyph is a
    /// plain outline filled with the brush, as in SixLabors.Fonts. With <see cref="ColorFontSupport.MicrosoftColrFormat"/>,
    /// <c>DrawText</c> paints the layers of COLR glyphs; measuring and layout do not change. Text-as-path (<see cref="TextBuilder.GeneratePath"/>)
    /// is always monochrome.
    /// </summary>
    public ColorFontSupport ColorFontSupport { get; set; }

    /// <summary>
    /// Gets or sets the CPAL palette used for color glyphs, counting from 0 (the default). A number past the font's last palette
    /// selects palette 0; see <see cref="Font.ColorPaletteCount"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int ColorPaletteIndex
    {
        get => _colorPalette;
        set => _colorPalette = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "The palette index cannot be negative.");
    }

    /// <summary>Makes a copy of these options.</summary>
    /// <returns>A new, independent object with the same settings.</returns>
    public TextOptions Clone()
    {
        var copy = CloneCore();
        foreach (FontFeature f in Features)
        {
            copy.Features.Add(f);
        }

        return copy;
    }

    private TextOptions CloneCore() => new(_font)
    {
        Language = Language,
        _origin = _origin,
        _wrappingLength = _wrappingLength,
        _lineSpacing = _lineSpacing,
        _tabWidth = _tabWidth,
        _dpi = _dpi,
        HorizontalAlignment = HorizontalAlignment,
        VerticalAlignment = VerticalAlignment,
        TextAlignment = TextAlignment,
        Kerning = Kerning,
        WordBreaking = WordBreaking,
        TextJustification = TextJustification,
        ColorFontSupport = ColorFontSupport,
        _colorPalette = _colorPalette,
    };
}
