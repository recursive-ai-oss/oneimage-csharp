using OneImage.Ops.Quantize;

namespace OneImage;

public sealed partial class Image
{
    /// <summary>
    /// Reduces every frame to the colors of a palette you supply, spreading the error with a dithering algorithm (lazy). Each pixel becomes
    /// the palette entry nearest to it by squared distance over R, G, B and A (ties go to the earlier entry).
    /// </summary>
    /// <remarks>
    /// <para><b>Error diffusion</b> (<see cref="Dither.FloydSteinberg"/>, <see cref="Dither.Atkinson"/>, <see cref="Dither.Burkes"/>,
    /// <see cref="Dither.JarvisJudiceNinke"/>, <see cref="Dither.Sierra2"/>, <see cref="Dither.Sierra3"/>, <see cref="Dither.SierraLite"/>,
    /// <see cref="Dither.StevensonArce"/>, <see cref="Dither.Stucki"/>): pixels are visited left to right, top to bottom. The difference between
    /// a pixel and the palette color chosen for it, times <paramref name="scale"/>, is added to the not yet visited neighbours in proportion to
    /// the kernel's weights; each neighbour is rounded back to 8 bits after every addition. The pass is inherently sequential, so it runs
    /// on one thread: the first tile request computes the whole frame in one forward pass over the input, in bounded memory apart from
    /// the result, and keeps the result. The output never depends on thread count or on which tile is asked for first.</para>
    /// <para><b>Ordered</b> (<see cref="Dither.Ordered"/> = <see cref="Dither.Bayer8x8"/>, <see cref="Dither.Bayer2x2"/>, <see cref="Dither.Ordered3x3"/>,
    /// <see cref="Dither.Bayer4x4"/>, <see cref="Dither.Bayer16x16"/>): the color channels (not alpha) are first shifted by
    /// <c>S · scale · ((m + 1) / n² − 0.5)</c>, where <c>m</c> is the matrix value at (x mod n, y mod n) from 0 to n²−1 and
    /// <c>S = min(255, floor(255 / (∛(palette size) − 1)))</c>, the step of an evenly spread palette; the sum is clamped to 0..255 and truncated. Every pixel is independent, so tiles compute in parallel.</para>
    /// <para><see cref="Dither.None"/> maps to the nearest entry without spreading error. Input of any depth is first converted to 8 bits per
    /// channel, straight (not premultiplied) alpha; gray becomes RGB. The result is <see cref="PixelFormat.Rgba8"/> when the input has alpha or
    /// the palette has an entry that is not opaque, otherwise <see cref="PixelFormat.Rgb8"/>.</para>
    /// <para>Graph descriptor: <c>dither(dither, scale, palette)</c>.</para>
    /// </remarks>
    /// <param name="palette">The colors to use, 1 to 65,536 entries. See <see cref="KnownPalettes"/> for ready-made ones.</param>
    /// <param name="dither">The algorithm (default <see cref="Dither.FloydSteinberg"/>).</param>
    /// <param name="scale">How much of the error is passed on, 0 to 1 and beyond for ordered modes' amplitude (default 1). 0 turns dithering off.</param>
    /// <param name="fastMatch">Find the nearest entry for the 5-bit-per-channel cell a color falls in (its low corner, <c>c &amp; 0xF8</c> on every channel, alpha included) instead of for the color itself (default <see langword="true"/>). The first color seen in a cell decides for all later colors in it, so the result depends on raster order; this is how ImageSharp matches colors, so the default reproduces its output, and it is faster. Pass <see langword="false"/> for exact nearest-color matching of every pixel.</param>
    /// <param name="cancellationToken">Cancels the pass that error diffusion runs when the first tile is requested.</param>
    /// <returns>The dithered image.</returns>
    /// <exception cref="ArgumentException"><paramref name="palette"/> is empty or has more than 65,536 entries.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dither"/> is undefined or <paramref name="scale"/> is negative or not finite.</exception>
    /// <exception cref="ImageOperationException">The pixel format is CMYK or Lab.</exception>
    public Image Dither(IReadOnlyList<Rgba32> palette, Dither dither = OneImage.Dither.FloydSteinberg, float scale = 1f, bool fastMatch = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (palette.Count == 0 || palette.Count > 65536)
        {
            throw new ArgumentException("A palette needs 1 to 65,536 entries.", nameof(palette));
        }

        if (!Enum.IsDefined(dither))
        {
            throw new ArgumentOutOfRangeException(nameof(dither));
        }

        if (!float.IsFinite(scale) || scale < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        CheckQuantizable("Dither");
        var bytes = new byte[palette.Count * 4];
        for (int i = 0; i < palette.Count; i++)
        {
            Rgba32 c = palette[i];
            bytes[i * 4] = c.R;
            bytes[(i * 4) + 1] = c.G;
            bytes[(i * 4) + 2] = c.B;
            bytes[(i * 4) + 3] = c.A;
        }

        return Map(static (n, p) => new DitherNode(n, p.bytes, p.dither, p.scale, p.fast), (bytes, dither, scale, fast: fastMatch));
    }

    /// <summary>
    /// Reduces every frame to two colors with a dithering algorithm (lazy): <see cref="Dither(IReadOnlyList{Rgba32}, Dither, float, bool, CancellationToken)"/>
    /// with the palette <paramref name="upper"/>, <paramref name="lower"/>.
    /// </summary>
    /// <param name="dither">The algorithm (default <see cref="Dither.Bayer8x8"/>).</param>
    /// <param name="upper">The color for light pixels (default opaque white).</param>
    /// <param name="lower">The color for dark pixels (default opaque black).</param>
    /// <param name="scale">The dither amount, see <see cref="Dither(IReadOnlyList{Rgba32}, Dither, float, bool, CancellationToken)"/> (default 1).</param>
    /// <param name="fastMatch">See <see cref="Dither(IReadOnlyList{Rgba32}, Dither, float, bool, CancellationToken)"/> (default <see langword="true"/>).</param>
    /// <returns>The two-color image.</returns>
    public Image BinaryDither(Dither dither = OneImage.Dither.Bayer8x8, Rgba32? upper = null, Rgba32? lower = null, float scale = 1f, bool fastMatch = true) =>
        Dither([upper ?? new Rgba32(255, 255, 255, 255), lower ?? new Rgba32(0, 0, 0, 255)], dither, scale, fastMatch);

    private void CheckQuantizable(string op)
    {
        foreach (ImageFrame f in _frames)
        {
            if (f.Format.Model is not (ColorModel.Gray or ColorModel.Rgb))
            {
                throw new ImageOperationException($"{op} is not defined for {f.Format}.");
            }
        }
    }
}
