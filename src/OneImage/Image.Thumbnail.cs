namespace OneImage;

public sealed partial class Image
{
    /// <summary>
    /// Opens an image file and makes a thumbnail of it (lazy): <c>Image.Thumbnail("in.jpg", 400).Save("t.webp")</c>.
    /// See <see cref="Thumbnail(Image,Nullable{int},Nullable{int},Fit,Anchor,bool,ResamplingFilter,bool)"/> for exactly what is done.
    /// </summary>
    /// <param name="path">The image file.</param>
    /// <param name="width">Target width, or <see langword="null"/> to derive it from <paramref name="height"/>. At least one of the two is required.</param>
    /// <param name="height">Target height, or <see langword="null"/> to derive it from <paramref name="width"/>.</param>
    /// <param name="fit">How the image fits a width-and-height box. Defaults to <see cref="Fit.Contain"/>.</param>
    /// <param name="anchor">Where a cropped or padded image sits. Defaults to <see cref="Anchor.Center"/>.</param>
    /// <param name="upscale">Allow enlarging. The default is <see langword="false"/>: a thumbnail is never bigger than the original.</param>
    /// <param name="filter">The reconstruction filter. Defaults to <see cref="ResamplingFilter.Auto"/>.</param>
    /// <param name="keepMetadata">Keep EXIF, XMP and IPTC. The default is <see langword="false"/>: they are removed (see the overload with an <see cref="Image"/>).</param>
    /// <param name="loadOptions">Limits and configuration for loading. <see cref="LoadOptions.AutoOrient"/> is ignored: thumbnails are always upright.</param>
    /// <param name="cancellationToken">Cancels validation at load.</param>
    /// <returns>The lazy thumbnail.</returns>
    /// <exception cref="ImageFormatException">Unrecognized or malformed data.</exception>
    /// <exception cref="ImageLimitException">A limit is exceeded.</exception>
    /// <exception cref="ArgumentException">Neither width nor height is given.</exception>
    public static Image Thumbnail(string path, int? width, int? height = null, Fit fit = Fit.Contain, Anchor anchor = Anchor.Center, bool upscale = false, ResamplingFilter filter = ResamplingFilter.Auto, bool keepMetadata = false, LoadOptions? loadOptions = null, CancellationToken cancellationToken = default) =>
        Thumbnail(Load(path, loadOptions, cancellationToken), width, height, fit, anchor, upscale, filter, keepMetadata);

    /// <summary>
    /// Reads an image from a stream and makes a thumbnail of it (lazy). The compressed bytes are copied at load, so the stream
    /// is not needed afterwards. Parameters as in <see cref="Thumbnail(string,Nullable{int},Nullable{int},Fit,Anchor,bool,ResamplingFilter,bool,LoadOptions,CancellationToken)"/>.
    /// </summary>
    /// <param name="stream">The stream, positioned at the start of the image.</param>
    /// <param name="width">Target width, or <see langword="null"/>.</param>
    /// <param name="height">Target height, or <see langword="null"/>.</param>
    /// <param name="fit">How the image fits a width-and-height box.</param>
    /// <param name="anchor">Where a cropped or padded image sits.</param>
    /// <param name="upscale">Allow enlarging. The default is <see langword="false"/>.</param>
    /// <param name="filter">The reconstruction filter.</param>
    /// <param name="keepMetadata">Keep EXIF, XMP and IPTC. The default is <see langword="false"/>.</param>
    /// <param name="loadOptions">Limits and configuration for loading.</param>
    /// <param name="cancellationToken">Cancels validation at load.</param>
    /// <returns>The lazy thumbnail.</returns>
    public static Image Thumbnail(Stream stream, int? width, int? height = null, Fit fit = Fit.Contain, Anchor anchor = Anchor.Center, bool upscale = false, ResamplingFilter filter = ResamplingFilter.Auto, bool keepMetadata = false, LoadOptions? loadOptions = null, CancellationToken cancellationToken = default) =>
        Thumbnail(Load(stream, loadOptions, cancellationToken), width, height, fit, anchor, upscale, filter, keepMetadata);

    /// <summary>
    /// Decodes an image held in memory and makes a thumbnail of it (lazy). The memory is referenced, not copied, and must not
    /// change while the thumbnail is in use. Parameters as in <see cref="Thumbnail(string,Nullable{int},Nullable{int},Fit,Anchor,bool,ResamplingFilter,bool,LoadOptions,CancellationToken)"/>.
    /// </summary>
    /// <param name="data">The encoded image.</param>
    /// <param name="width">Target width, or <see langword="null"/>.</param>
    /// <param name="height">Target height, or <see langword="null"/>.</param>
    /// <param name="fit">How the image fits a width-and-height box.</param>
    /// <param name="anchor">Where a cropped or padded image sits.</param>
    /// <param name="upscale">Allow enlarging. The default is <see langword="false"/>.</param>
    /// <param name="filter">The reconstruction filter.</param>
    /// <param name="keepMetadata">Keep EXIF, XMP and IPTC. The default is <see langword="false"/>.</param>
    /// <param name="loadOptions">Limits and configuration for loading.</param>
    /// <param name="cancellationToken">Cancels validation at load.</param>
    /// <returns>The lazy thumbnail.</returns>
    public static Image Thumbnail(ReadOnlyMemory<byte> data, int? width, int? height = null, Fit fit = Fit.Contain, Anchor anchor = Anchor.Center, bool upscale = false, ResamplingFilter filter = ResamplingFilter.Auto, bool keepMetadata = false, LoadOptions? loadOptions = null, CancellationToken cancellationToken = default) =>
        Thumbnail(Load(data, loadOptions, cancellationToken), width, height, fit, anchor, upscale, filter, keepMetadata);

    /// <summary>
    /// Makes a thumbnail of an image (lazy), the quick way to a small, upright, web-ready copy. In order:
    /// <list type="number">
    /// <item><description><b>Orientation.</b> A recorded EXIF orientation is applied (<see cref="AutoOrient"/>), so the result is upright and its orientation is 1.</description></item>
    /// <item><description><b>Resize.</b> <see cref="Resize"/> with the given box, fit and filter, in linear light. When the source is a JPEG the decoder shrinks the image while decoding (DCT scaling by 2, 4 or 8) and large reductions are box-averaged before the final filter, so a 24 MP photo is never decoded at full size for a 400 px thumbnail. Unlike <see cref="Resize"/>, <paramref name="upscale"/> defaults to <see langword="false"/>.</description></item>
    /// <item><description><b>Color.</b> RGB images are converted to sRGB (relative colorimetric, black point compensation) and the compact sRGB profile is embedded, so wide-gamut and CMYK sources look right everywhere; a source that is already sRGB only gets the profile label. Untagged RGB is taken as sRGB. Gray images stay gray in the standard gray profile. The conversion runs after the resize, on the small image; the resize treats the source as sRGB-encoded, which is exact for sRGB and Display P3 and a close approximation for other curves.</description></item>
    /// <item><description><b>Metadata.</b> By default EXIF, XMP and IPTC are removed, because they can reveal location and device (GPS), and only the ICC profile and the (corrected) orientation are kept. With <paramref name="keepMetadata"/> they are carried over; on save the EXIF dimensions and orientation tags are corrected to the thumbnail, and any GPS data in the EXIF stays, so only keep them for trusted output.</description></item>
    /// </list>
    /// Nothing is computed until the thumbnail is saved or read. Animated images are thumbnailed frame by frame.
    /// </summary>
    /// <param name="source">The image.</param>
    /// <param name="width">Target width, or <see langword="null"/> to derive it from <paramref name="height"/>. At least one of the two is required.</param>
    /// <param name="height">Target height, or <see langword="null"/> to derive it from <paramref name="width"/>.</param>
    /// <param name="fit">How the image fits a width-and-height box. Defaults to <see cref="Fit.Contain"/>.</param>
    /// <param name="anchor">Where a cropped or padded image sits. Defaults to <see cref="Anchor.Center"/>.</param>
    /// <param name="upscale">Allow enlarging. The default is <see langword="false"/>.</param>
    /// <param name="filter">The reconstruction filter. Defaults to <see cref="ResamplingFilter.Auto"/>.</param>
    /// <param name="keepMetadata">Keep EXIF, XMP and IPTC. The default is <see langword="false"/>.</param>
    /// <returns>The lazy thumbnail.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentException">Neither width nor height is given.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive or an enum value is undefined.</exception>
    public static Image Thumbnail(Image source, int? width, int? height = null, Fit fit = Fit.Contain, Anchor anchor = Anchor.Center, bool upscale = false, ResamplingFilter filter = ResamplingFilter.Auto, bool keepMetadata = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        Image img = source.AutoOrient().Resize(width, height, fit, anchor, upscale, filter);
        if (img.Format.Model is ColorModel.Rgb or ColorModel.Cmyk or ColorModel.Lab)
        {
            ColorProfile target = ColorProfile.SrgbCompact;
            img = img.Format.Model == ColorModel.Rgb && SameProfile(img.ColorProfile, target) ? img.AssignProfile(target) : img.ConvertColor(target);
        }
        else if (img.Format.Model == ColorModel.Gray && !SameProfile(img.ColorProfile, ColorProfile.Gray))
        {
            img = img.ConvertColor(ColorProfile.Gray);
        }

        return keepMetadata ? img : img.WithMetadata(img.Metadata.Stripped(keepIcc: true));
    }
}
