using System.Globalization;

namespace OneImage;

/// <summary>
/// A 5x4 affine color transform on RGBA, in the layout ImageSharp uses so matrices carry over unchanged. Values are row-major
/// (<c>M11</c> is row 1, column 1) and the pixel is a row vector, so rows are inputs and columns are outputs:
/// <code>
/// R' = R*M11 + G*M21 + B*M31 + A*M41 + M51
/// G' = R*M12 + G*M22 + B*M32 + A*M42 + M52
/// B' = R*M13 + G*M23 + B*M33 + A*M43 + M53
/// A' = R*M14 + G*M24 + B*M34 + A*M44 + M54
/// </code>
/// Rows 1 to 4 weigh the input red, green, blue and alpha; row 5 holds the offsets. All values are on a 0..1 scale and the color is
/// straight (not premultiplied), so an offset of 0.1 adds 25.5 to an 8-bit channel. The sums are evaluated in the order shown in
/// float32 without fused multiply-add, which keeps every hardware path identical. The presets are the ones ImageSharp ships.
/// </summary>
public readonly struct ColorMatrix : IEquatable<ColorMatrix>
{
    /// <summary>Row 1, column 1: red into red.</summary>
    public float M11 { get; }

    /// <summary>Row 1, column 2: red into green.</summary>
    public float M12 { get; }

    /// <summary>Row 1, column 3: red into blue.</summary>
    public float M13 { get; }

    /// <summary>Row 1, column 4: red into alpha.</summary>
    public float M14 { get; }

    /// <summary>Row 2, column 1: green into red.</summary>
    public float M21 { get; }

    /// <summary>Row 2, column 2: green into green.</summary>
    public float M22 { get; }

    /// <summary>Row 2, column 3: green into blue.</summary>
    public float M23 { get; }

    /// <summary>Row 2, column 4: green into alpha.</summary>
    public float M24 { get; }

    /// <summary>Row 3, column 1: blue into red.</summary>
    public float M31 { get; }

    /// <summary>Row 3, column 2: blue into green.</summary>
    public float M32 { get; }

    /// <summary>Row 3, column 3: blue into blue.</summary>
    public float M33 { get; }

    /// <summary>Row 3, column 4: blue into alpha.</summary>
    public float M34 { get; }

    /// <summary>Row 4, column 1: alpha into red.</summary>
    public float M41 { get; }

    /// <summary>Row 4, column 2: alpha into green.</summary>
    public float M42 { get; }

    /// <summary>Row 4, column 3: alpha into blue.</summary>
    public float M43 { get; }

    /// <summary>Row 4, column 4: alpha into alpha.</summary>
    public float M44 { get; }

    /// <summary>Row 5, column 1: offset added to red.</summary>
    public float M51 { get; }

    /// <summary>Row 5, column 2: offset added to green.</summary>
    public float M52 { get; }

    /// <summary>Row 5, column 3: offset added to blue.</summary>
    public float M53 { get; }

    /// <summary>Row 5, column 4: offset added to alpha.</summary>
    public float M54 { get; }

    /// <summary>Creates a matrix from its twenty values, row by row.</summary>
    /// <param name="m11">Row 1, column 1.</param>
    /// <param name="m12">Row 1, column 2.</param>
    /// <param name="m13">Row 1, column 3.</param>
    /// <param name="m14">Row 1, column 4.</param>
    /// <param name="m21">Row 2, column 1.</param>
    /// <param name="m22">Row 2, column 2.</param>
    /// <param name="m23">Row 2, column 3.</param>
    /// <param name="m24">Row 2, column 4.</param>
    /// <param name="m31">Row 3, column 1.</param>
    /// <param name="m32">Row 3, column 2.</param>
    /// <param name="m33">Row 3, column 3.</param>
    /// <param name="m34">Row 3, column 4.</param>
    /// <param name="m41">Row 4, column 1.</param>
    /// <param name="m42">Row 4, column 2.</param>
    /// <param name="m43">Row 4, column 3.</param>
    /// <param name="m44">Row 4, column 4.</param>
    /// <param name="m51">Row 5, column 1.</param>
    /// <param name="m52">Row 5, column 2.</param>
    /// <param name="m53">Row 5, column 3.</param>
    /// <param name="m54">Row 5, column 4.</param>
    public ColorMatrix(
        float m11, float m12, float m13, float m14,
        float m21, float m22, float m23, float m24,
        float m31, float m32, float m33, float m34,
        float m41, float m42, float m43, float m44,
        float m51, float m52, float m53, float m54)
    {
        M11 = m11;
        M12 = m12;
        M13 = m13;
        M14 = m14;
        M21 = m21;
        M22 = m22;
        M23 = m23;
        M24 = m24;
        M31 = m31;
        M32 = m32;
        M33 = m33;
        M34 = m34;
        M41 = m41;
        M42 = m42;
        M43 = m43;
        M44 = m44;
        M51 = m51;
        M52 = m52;
        M53 = m53;
        M54 = m54;
    }

    /// <summary>Gets the matrix that leaves every pixel unchanged.</summary>
    public static ColorMatrix Identity => new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
        0, 0, 0, 0);

    /// <summary>Gets the black-and-white matrix: 1.5 times the sum of red, green and blue, minus 1, so it also raises contrast.</summary>
    public static ColorMatrix BlackWhite => new(
        1.5f, 1.5f, 1.5f, 0,
        1.5f, 1.5f, 1.5f, 0,
        1.5f, 1.5f, 1.5f, 0,
        0, 0, 0, 1,
        -1, -1, -1, 0);

    /// <summary>Gets the Kodachrome film look.</summary>
    public static ColorMatrix Kodachrome => new(
        1.1401526f, -0.041965187f, -0.041965187f, 0,
        -0.11794539f, 0.871806f, -0.11794539f, 0,
        -0.011609921f, -0.011609826f, 0.9558832f, 0,
        0, 0, 0, 1,
        -0.041944027f, 0.022855982f, 0.039056003f, 0);

    /// <summary>Gets the Lomograph color shift (the matrix part of the look: no vignette).</summary>
    public static ColorMatrix Lomograph => new(
        2.1520066f, -0.042493507f, -0.042493507f, 0,
        -0.13788776f, 1.9834623f, -0.13788776f, 0,
        -0.011108165f, -0.011108165f, 1.6859719f, 0,
        0, 0, 0, 1,
        -0.3058949f, -0.1888549f, -0.2620049f, 0);

    /// <summary>Gets the Polaroid color shift (the matrix part of the look: no vignette).</summary>
    public static ColorMatrix Polaroid => new(
        1.538f, -0.062f, -0.262f, 0,
        -0.022f, 1.578f, -0.022f, 0,
        0.216f, -0.16f, 1.5831f, 0,
        0, 0, 0, 1,
        0.02f, -0.05f, -0.05f, 0);

    /// <summary>Gets one value by row (0..4) and column (0..3), zero-based.</summary>
    /// <param name="row">Row 0..4 (red, green, blue, alpha inputs, then offsets).</param>
    /// <param name="column">Column 0..3 (red, green, blue, alpha outputs).</param>
    /// <exception cref="ArgumentOutOfRangeException">An index is out of range.</exception>
    public float this[int row, int column]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)row, 5u);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)column, 4u);
            Span<float> all = stackalloc float[20];
            CopyTo(all);
            return all[(row * 4) + column];
        }
    }

    /// <summary>Gets a value indicating whether this is the identity matrix.</summary>
    public bool IsIdentity => Equals(Identity);

    /// <summary>Creates the sepia matrix.</summary>
    /// <param name="amount">0 (no change) to 1 (full sepia). Default 1.</param>
    /// <returns>The matrix.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1 or not a number.</exception>
    public static ColorMatrix Sepia(float amount = 1f)
    {
        if (!(amount >= 0f && amount <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Must be between 0 and 1.");
        }

        // The weights are ImageSharp's: full sepia at amount 1, a linear blend toward the identity below that.
        amount = 1f - amount;
        return new ColorMatrix(
            .393f + (.607f * amount), .349f - (.349f * amount), .272f - (.272f * amount), 0,
            .769f - (.769f * amount), .686f + (.314f * amount), .534f - (.534f * amount), 0,
            .189f - (.189f * amount), .168f - (.168f * amount), .131f + (.869f * amount), 0,
            0, 0, 0, 1,
            0, 0, 0, 0);
    }

    /// <summary>Creates a matrix that multiplies alpha by <paramref name="amount"/> and leaves color alone.</summary>
    /// <param name="amount">0 (fully transparent) to 1 (unchanged).</param>
    /// <returns>The matrix.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1 or not a number.</exception>
    public static ColorMatrix Opacity(float amount)
    {
        if (!(amount >= 0f && amount <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Must be between 0 and 1.");
        }

        return new ColorMatrix(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, amount,
            0, 0, 0, 0);
    }

    /// <summary>Creates the matrix that simulates one kind of color vision deficiency.</summary>
    /// <param name="mode">The deficiency to simulate.</param>
    /// <returns>The matrix.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined value.</exception>
    public static ColorMatrix ColorBlindness(ColorBlindnessMode mode) => mode switch
    {
        ColorBlindnessMode.Achromatomaly => Simulation(0.618f, 0.163f, 0.163f, 0.32f, 0.775f, 0.32f, 0.062f, 0.062f, 0.516f),
        ColorBlindnessMode.Achromatopsia => Simulation(0.299f, 0.299f, 0.299f, 0.587f, 0.587f, 0.587f, 0.114f, 0.114f, 0.114f),
        ColorBlindnessMode.Deuteranomaly => Simulation(0.8f, 0.258f, 0f, 0.2f, 0.742f, 0.142f, 0f, 0f, 0.858f),
        ColorBlindnessMode.Deuteranopia => Simulation(0.625f, 0.7f, 0f, 0.375f, 0.3f, 0.3f, 0f, 0f, 0.7f),
        ColorBlindnessMode.Protanomaly => Simulation(0.817f, 0.333f, 0f, 0.183f, 0.667f, 0.125f, 0f, 0f, 0.875f),
        ColorBlindnessMode.Protanopia => Simulation(0.567f, 0.558f, 0f, 0.433f, 0.442f, 0.242f, 0f, 0f, 0.758f),
        ColorBlindnessMode.Tritanomaly => Simulation(0.967f, 0f, 0f, 0.33f, 0.733f, 0.183f, 0f, 0.267f, 0.817f),
        ColorBlindnessMode.Tritanopia => Simulation(0.95f, 0f, 0f, 0.05f, 0.433f, 0.475f, 0f, 0.567f, 0.525f),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>Concatenates two transforms: the result applies <paramref name="first"/>, then <paramref name="second"/>, on unclamped float values.</summary>
    /// <param name="first">Applied first.</param>
    /// <param name="second">Applied second.</param>
    /// <returns>The combined matrix.</returns>
    public static ColorMatrix Multiply(ColorMatrix first, ColorMatrix second)
    {
        Span<float> a = stackalloc float[20];
        Span<float> b = stackalloc float[20];
        Span<float> r = stackalloc float[20];
        first.CopyTo(a);
        second.CopyTo(b);
        for (int row = 0; row < 5; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                float sum = row == 4 ? b[16 + col] : 0f;
                for (int k = 0; k < 4; k++)
                {
                    sum += a[(row * 4) + k] * b[(k * 4) + col];
                }

                // The first matrix's offset row is a pixel with an implicit weight of one.
                r[(row * 4) + col] = sum;
            }
        }

        return FromSpan(r);
    }

    /// <summary>Concatenates two transforms; see <see cref="Multiply"/>.</summary>
    /// <param name="left">Applied first.</param>
    /// <param name="right">Applied second.</param>
    /// <returns>The combined matrix.</returns>
    public static ColorMatrix operator *(ColorMatrix left, ColorMatrix right) => Multiply(left, right);

    /// <summary>Compares two matrices value by value.</summary>
    /// <param name="left">First matrix.</param>
    /// <param name="right">Second matrix.</param>
    /// <returns><see langword="true"/> when all twenty values are equal.</returns>
    public static bool operator ==(ColorMatrix left, ColorMatrix right) => left.Equals(right);

    /// <summary>Compares two matrices value by value.</summary>
    /// <param name="left">First matrix.</param>
    /// <param name="right">Second matrix.</param>
    /// <returns><see langword="true"/> when any value differs.</returns>
    public static bool operator !=(ColorMatrix left, ColorMatrix right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(ColorMatrix other)
    {
        Span<float> a = stackalloc float[20];
        Span<float> b = stackalloc float[20];
        CopyTo(a);
        other.CopyTo(b);
        return a.SequenceEqual(b);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ColorMatrix m && Equals(m);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        Span<float> a = stackalloc float[20];
        CopyTo(a);
        var h = default(HashCode);
        foreach (float v in a)
        {
            h.Add(v);
        }

        return h.ToHashCode();
    }

    internal void CopyTo(Span<float> destination)
    {
        destination[0] = M11;
        destination[1] = M12;
        destination[2] = M13;
        destination[3] = M14;
        destination[4] = M21;
        destination[5] = M22;
        destination[6] = M23;
        destination[7] = M24;
        destination[8] = M31;
        destination[9] = M32;
        destination[10] = M33;
        destination[11] = M34;
        destination[12] = M41;
        destination[13] = M42;
        destination[14] = M43;
        destination[15] = M44;
        destination[16] = M51;
        destination[17] = M52;
        destination[18] = M53;
        destination[19] = M54;
    }

    internal static ColorMatrix FromSpan(ReadOnlySpan<float> v) => new(
        v[0], v[1], v[2], v[3],
        v[4], v[5], v[6], v[7],
        v[8], v[9], v[10], v[11],
        v[12], v[13], v[14], v[15],
        v[16], v[17], v[18], v[19]);

    internal string ToParameter()
    {
        Span<float> v = stackalloc float[20];
        CopyTo(v);
        var parts = new string[20];
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = v[i].ToString("R", CultureInfo.InvariantCulture);
        }

        return string.Join(',', parts);
    }

    private static ColorMatrix Simulation(float m11, float m12, float m13, float m21, float m22, float m23, float m31, float m32, float m33) => new(
        m11, m12, m13, 0,
        m21, m22, m23, 0,
        m31, m32, m33, 0,
        0, 0, 0, 1,
        0, 0, 0, 0);
}

/// <summary>A color vision deficiency to simulate with <see cref="Image.ColorBlindness"/>. The "-opsia" modes lack one cone type; the "-omaly" modes have it weakened.</summary>
public enum ColorBlindnessMode
{
    /// <summary>Weak color sensitivity overall (blue-cone monochromacy, partial).</summary>
    Achromatomaly = 0,

    /// <summary>No color vision; gray by the BT.601 weights 0.299, 0.587 and 0.114.</summary>
    Achromatopsia = 1,

    /// <summary>Weak green sensitivity.</summary>
    Deuteranomaly = 2,

    /// <summary>No green sensitivity.</summary>
    Deuteranopia = 3,

    /// <summary>Weak red sensitivity.</summary>
    Protanomaly = 4,

    /// <summary>No red sensitivity.</summary>
    Protanopia = 5,

    /// <summary>Weak blue sensitivity.</summary>
    Tritanomaly = 6,

    /// <summary>No blue sensitivity.</summary>
    Tritanopia = 7,
}
