namespace OneImage.Text;

/// <summary>
/// An OpenType layout feature setting: a four-letter tag such as <c>smcp</c>, <c>onum</c>, <c>dlig</c> or <c>ss01</c> and a value.
/// Add them to <see cref="TextOptions.Features"/>.
/// </summary>
/// <remarks>
/// Value 0 turns the feature off, 1 turns it on. For a feature that offers alternates (such as <c>salt</c> or <c>aalt</c>) a larger
/// value picks the alternate with that 1-based number; a value beyond the font's alternates leaves the glyph unchanged.
/// A tag the font does not have is ignored.
/// </remarks>
public readonly struct FontFeature : IEquatable<FontFeature>
{
    /// <summary>Creates a feature setting.</summary>
    /// <param name="tag">Exactly four printable ASCII characters (the OpenType feature tag).</param>
    /// <param name="value">0 for off, 1 for on, or the 1-based alternate number.</param>
    /// <exception cref="ArgumentException"><paramref name="tag"/> is not four printable ASCII characters.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public FontFeature(string tag, int value = 1)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Length != 4 || tag.Any(c => c < 0x20 || c > 0x7E))
        {
            throw new ArgumentException("A feature tag is four printable ASCII characters.", nameof(tag));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Tag = tag;
        Value = value;
    }

    /// <summary>Gets the four-character tag; null for a default-constructed value.</summary>
    public string? Tag { get; }

    /// <summary>Gets the value: 0 off, 1 on, or the 1-based alternate number.</summary>
    public int Value { get; }

    /// <summary>Turns a feature on.</summary>
    /// <param name="tag">The feature tag.</param>
    /// <returns>The setting <c>tag=1</c>.</returns>
    public static FontFeature On(string tag) => new(tag, 1);

    /// <summary>Turns a feature off, including one that is on by default (for example <c>liga</c>).</summary>
    /// <param name="tag">The feature tag.</param>
    /// <returns>The setting <c>tag=0</c>.</returns>
    public static FontFeature Off(string tag) => new(tag, 0);

    /// <inheritdoc/>
    public bool Equals(FontFeature other) => Tag == other.Tag && Value == other.Value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FontFeature f && Equals(f);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Tag, Value);

    /// <summary>Whether two settings are equal.</summary>
    /// <param name="left">The first setting.</param>
    /// <param name="right">The second setting.</param>
    /// <returns>True when tag and value match.</returns>
    public static bool operator ==(FontFeature left, FontFeature right) => left.Equals(right);

    /// <summary>Whether two settings differ.</summary>
    /// <param name="left">The first setting.</param>
    /// <param name="right">The second setting.</param>
    /// <returns>True when tag or value differ.</returns>
    public static bool operator !=(FontFeature left, FontFeature right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => Tag is null ? "" : $"{Tag}={Value}";
}
