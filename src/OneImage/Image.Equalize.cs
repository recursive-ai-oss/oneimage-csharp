namespace OneImage;

/// <summary>How <see cref="Image.HistogramEqualization"/> builds the luminance mapping.</summary>
public enum HistogramEqualizationMethod
{
    /// <summary>One mapping from the luminance histogram of the whole image.</summary>
    Global = 0,

    /// <summary>
    /// The image is cut into a grid of tiles; every tile gets its own mapping, and each pixel blends the mappings of the four
    /// nearest tile centers (bilinear weights). Fast, and a good local contrast enhancer, especially with histogram clipping.
    /// </summary>
    AdaptiveTileInterpolation = 1,

    /// <summary>
    /// Every pixel gets the mapping of its own window of neighboring pixels. Slower than tile interpolation, and free of tile
    /// seams.
    /// </summary>
    AdaptiveSlidingWindow = 2,
}

public sealed partial class Image
{
    /// <summary>
    /// Equalizes the luminance histogram to increase contrast (lazy), for gray and RGB images. The result matches ImageSharp's
    /// histogram equalization (within 2 levels at 8 bits on the pictures and synthetic inputs we compared).
    /// <para>
    /// <b>What happens to a pixel.</b> Its luminance is Rec. 709 luma of the stored values (0.2126 R + 0.7152 G + 0.0722 B,
    /// rounded to the nearest sample value; a gray pixel is its own luminance). The luminance picks a bin of a histogram with
    /// <paramref name="luminanceLevels"/> bins (<c>round(luminance / full scale × (levels − 1))</c>). The new luminance is
    /// <c>(cdf − cdfMin) / (n − cdfMin)</c> of the cumulative histogram, scaled to the full scale, where cdfMin is the first non-zero
    /// cumulative count and n the number of pixels the histogram stands for; it is rounded to the nearest sample value. A histogram
    /// with a single occupied bin maps to 0. <b>Color is not preserved:</b> an RGB pixel becomes the gray of its new luminance in
    /// all three channels, as ImageSharp does. Alpha is carried over unchanged and does not weigh in the histogram; the work is done
    /// on straight (not premultiplied) color. Images with 16-bit samples work on 16-bit values; floating-point images are
    /// processed at 16-bit precision.
    /// </para>
    /// <para>
    /// <b>Tile interpolation.</b> The grid has <paramref name="tiles"/> × <paramref name="tiles"/> tiles of
    /// <c>ceil(width / tiles)</c> × <c>ceil(height / tiles)</c> pixels; a tile that reaches past the right or bottom edge counts the missing
    /// pixels in n but not in any bin. Tile mappings belong to the tile centers, <c>tileSize / 2</c> into each tile. Between two
    /// centers the weight of the second grows as <c>k / (tileSize − 1)</c> with k = 0 .. tileSize − 1 starting at the first center's
    /// pixel; beyond the first and last centers the nearest tile alone is used. Where the last block of an axis runs to the image edge,
    /// the final pixel of a strip along the other edge keeps its original value (ImageSharp does the same).
    /// </para>
    /// <para>
    /// <b>Sliding window.</b> The window is <c>S</c> = <c>width / tiles</c> (integer division, at least 2) pixels wide and
    /// 2 × (S / 2) rows high, covering columns x − S/2 .. x − S/2 + S − 1 and rows y − S/2 .. y + S/2 − 1; n is S². Outside the image,
    /// the left and top reflect without repeating the edge pixel, the right edge repeats the edge pixel, and the bottom reflects
    /// with the edge pixel repeated (ImageSharp's behavior). The window size follows the width only. ImageSharp refuses an image
    /// whose height is smaller than the window; this one does not.
    /// </para>
    /// <para>
    /// <b>Clipping.</b> With <paramref name="clipHistogram"/>, bins above <paramref name="clipLimit"/> are cut to the limit and the
    /// removed counts are spread equally over all bins (excess / levels each, not rounded) before the cumulative sum, which limits
    /// contrast gain. A limit that suits a 16×16 tile is far too small for a global histogram of a large image; the
    /// limit is an absolute count per bin, not a fraction.
    /// </para>
    /// Results are identical for any thread count. The global and tile methods read the whole image once to count luminance
    /// before the first tile is produced.
    /// </summary>
    /// <param name="method">Which adaptation to use. Defaults to <see cref="HistogramEqualizationMethod.Global"/>.</param>
    /// <param name="luminanceLevels">Number of histogram bins, 2 to 65536. Defaults to 256; use 65536 to equalize 16-bit
    /// images at full precision.</param>
    /// <param name="clipHistogram">Clip histogram bins at <paramref name="clipLimit"/>. Defaults to <see langword="false"/>;
    /// recommended with tile interpolation to soften tile-border artifacts.</param>
    /// <param name="clipLimit">Bin count above which a bin is clipped, at least 1. Defaults to 350.</param>
    /// <param name="tiles">Tiles per axis (tile interpolation) or the divisor of the window size (sliding window), 2 to 1024.
    /// Ignored by the global method. Defaults to 8.</param>
    /// <returns>The equalized image. Animated images are processed frame by frame.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range, or tile interpolation was asked for with
    /// <c>tiles² × luminanceLevels</c> above 16,777,216.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image HistogramEqualization(
        HistogramEqualizationMethod method = HistogramEqualizationMethod.Global,
        int luminanceLevels = 256,
        bool clipHistogram = false,
        int clipLimit = 350,
        int tiles = 8)
    {
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(luminanceLevels, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(luminanceLevels, 65536);
        ArgumentOutOfRangeException.ThrowIfLessThan(clipLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(tiles, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tiles, 1024);
        if (method == HistogramEqualizationMethod.AdaptiveTileInterpolation && (long)tiles * tiles * luminanceLevels > EqualizeNode.MaxTableEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(tiles), "tiles² × luminanceLevels is too large.");
        }

        return Map(
            static (n, a) => new EqualizeNode(n, a.Method, a.Levels, a.Clip, a.Limit, a.Tiles),
            (Method: method, Levels: luminanceLevels, Clip: clipHistogram, Limit: clipLimit, Tiles: tiles));
    }
}
