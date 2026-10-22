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
    /// Gets or sets the length in pixels at which lines wrap; 0 or less means no wrapping (the default). Lines break after spaces
    /// and after a hyphen that is followed by a letter; a word longer than the length keeps its own line and overflows. Explicit
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

    /// <summary>Gets or sets where the block sits horizontally relative to the origin. The default is <see cref="HorizontalAlignment.Left"/>. The block is as wide as the wrapping length when there is one, else as the widest line.</summary>
    public HorizontalAlignment HorizontalAlignment { get; set; }

    /// <summary>Gets or sets where the block sits vertically relative to the origin. The default is <see cref="VerticalAlignment.Top"/>.</summary>
    public VerticalAlignment VerticalAlignment { get; set; }

    /// <summary>
    /// Gets or sets how each line lines up inside the block. The default is <see cref="TextAlignment.Start"/>, which leaves every
    /// line where <see cref="HorizontalAlignment"/> put it: lines share their left edge, and the block as a whole moves. Center and
    /// End move each line by its slack against the wrapping length (the widest line when there is none), as ImageSharp does; they
    /// are meant for use with a wrapping length.
    /// </summary>
    public TextAlignment TextAlignment { get; set; }

    /// <summary>Gets or sets whether the font's kerning is applied. The default is <see cref="KerningMode.Normal"/>.</summary>
    public KerningMode Kerning { get; set; }

    /// <summary>Makes a copy of these options.</summary>
    /// <returns>A new, independent object with the same settings.</returns>
    public TextOptions Clone() => new(_font)
    {
        _origin = _origin,
        _wrappingLength = _wrappingLength,
        _lineSpacing = _lineSpacing,
        _tabWidth = _tabWidth,
        _dpi = _dpi,
        HorizontalAlignment = HorizontalAlignment,
        VerticalAlignment = VerticalAlignment,
        TextAlignment = TextAlignment,
        Kerning = Kerning,
    };
}
