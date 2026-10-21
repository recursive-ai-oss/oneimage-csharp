using System.Globalization;

namespace OneImage;

public sealed partial class Image
{
    /// <summary>
    /// Gets the color profile of the pixels: the embedded ICC profile when it parses and matches the color model of the pixel
    /// format, otherwise the default for the model (sRGB for RGB, a gray profile with the sRGB curve, the generic CMYK profile, Lab D50).
    /// </summary>
    public ColorProfile ColorProfile =>
        Metadata.EmbeddedColorProfile is { } p && p.Model == Format.Model ? p : ColorProfile.DefaultFor(Format.Model);

    /// <summary>
    /// Converts colors to another profile (lazy). Source colors are read in <see cref="ColorProfile"/>. The result has the color
    /// model of <paramref name="profile"/>, the sample type of the source (Lab is always 32-bit float) and the same alpha mode; the
    /// embedded profile of the result is <paramref name="profile"/>. Alpha is not touched; premultiplied color is un-multiplied
    /// for the conversion and multiplied back. Out-of-gamut colors clip to the destination range in 8- and 16-bit output
    /// (float output is not clamped). 8- and 16-bit data converts through a precomputed 33-point (RGB), 17-point (CMYK) or
    /// 1024-point (gray) lookup grid with tetrahedral interpolation, or tabulated curves and a matrix for matrix profiles;
    /// float data uses exact math. Rounding to integers is half up. The result is identical on every hardware path.
    /// </summary>
    /// <param name="profile">The destination profile.</param>
    /// <param name="intent">The rendering intent. The default is relative colorimetric.</param>
    /// <param name="blackPointCompensation">Whether to scale the source black point to the destination's (ignored for absolute colorimetric and for Lab; always on for version 4 destinations in the perceptual and saturation intents). On by default.</param>
    /// <returns>The converted image.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="ImageOperationException">A frame's color model does not match the source profile, Lab pixels are not 32-bit float, or the profile has no tables for this direction.</exception>
    /// <exception cref="ImageFormatException">The destination profile cannot be used as a conversion target (for example CMYK without a PCS-to-device table).</exception>
    public Image ConvertColor(ColorProfile profile, RenderingIntent intent = RenderingIntent.RelativeColorimetric, bool blackPointCompensation = true) =>
        ConvertColorCore(profile, intent, blackPointCompensation, null);

    private Image ConvertColorCore(ColorProfile profile, RenderingIntent intent, bool blackPointCompensation, SampleType? outputSample)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ColorProfile source = ColorProfile;
        foreach (ImageFrame f in _frames)
        {
            if (f.Format.Model != source.Model)
            {
                throw new ImageOperationException("All frames must share the color model of the source profile.");
            }

            if (f.Format.Model == ColorModel.Lab && f.Format.Sample != SampleType.F32)
            {
                throw new ImageOperationException("Lab pixels must be 32-bit float.");
            }
        }

        // Build now so unusable profiles fail at the call, not when a tile is pulled.
        ColorTransform transform = ColorTransform.Get(source, profile, intent, blackPointCompensation);
        var descriptor = new OpDescriptor(
            "convert_color",
            new OpParameter("profile", ProfileId(profile)),
            new OpParameter("intent", intent.ToString()),
            new OpParameter("bpc", blackPointCompensation));
        Image mapped = Map(
            static (n, a) =>
            {
                PixelFormat f = n.Format;
                SampleType sample = a.Profile.Model == ColorModel.Lab ? SampleType.F32 : a.Sample ?? f.Sample;
                return new ColorConvertNode(n, a.Transform, new PixelFormat(a.Profile.Model, sample, f.Alpha), a.Descriptor);
            },
            (Profile: profile, Transform: transform, Descriptor: descriptor, Sample: outputSample));
        return mapped.WithMetadata(Metadata.WithIccProfile(profile.IccBytes));
    }

    /// <summary>Labels the pixels with a profile without changing any values (lazy; metadata only). Use it for untagged files whose space is known.</summary>
    /// <param name="profile">The profile to embed.</param>
    /// <returns>An image with the same pixels and the new embedded profile.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="ImageOperationException">The profile's color model differs from the pixel format's.</exception>
    public Image AssignProfile(ColorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Model != Format.Model)
        {
            throw new ImageOperationException("The profile's color model does not match the pixels.");
        }

        return WithMetadata(Metadata.WithIccProfile(profile.IccBytes));
    }

    /// <summary>
    /// Removes EXIF, XMP and IPTC metadata (they can reveal location and device). The ICC profile is kept when
    /// <paramref name="keepIcc"/> is true, otherwise removed (the pixels are then assumed to be in the default space for their model).
    /// The orientation value is kept, because it says how to display the pixels.
    /// </summary>
    /// <param name="keepIcc">Whether to keep the embedded ICC profile. The default is false.</param>
    /// <returns>An image with the stripped metadata; pixels are shared.</returns>
    public Image StripMetadata(bool keepIcc = false) => WithMetadata(Metadata.Stripped(keepIcc));

    /// <summary>The image with the same frames and different metadata.</summary>
    internal Image WithMetadata(ImageMetadata metadata)
    {
        var nodes = new Node[_frames.Length];
        var delays = new TimeSpan[_frames.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = _frames[i].Node;
            delays[i] = _frames[i].Delay;
        }

        return new Image(nodes, delays, LoopCount, metadata, SourceFormat);
    }

    /// <summary>
    /// Linear-light hook for resampling and blur. Returns the image converted to the linear variant of its own profile
    /// (same primaries, linear curves) as 32-bit float with the source's alpha mode; <paramref name="original"/> receives the
    /// profile to hand to <see cref="FromLinearLight"/>. CMYK, Lab and table-based RGB profiles have no linear variant and go through
    /// linear sRGB (gray profiles through linear gray).
    /// </summary>
    internal Image ToLinearLight(out ColorProfile original)
    {
        original = ColorProfile;
        ColorProfile linear = original.LinearVariant ?? (Format.Model == ColorModel.Gray ? ColorProfile.LinearGray : ColorProfile.LinearSrgb);
        return ConvertColorCore(linear, RenderingIntent.RelativeColorimetric, false, SampleType.F32);
    }

    /// <summary>Inverse of <see cref="ToLinearLight"/>: converts back into <paramref name="original"/> and the given storage format.</summary>
    internal Image FromLinearLight(ColorProfile original, PixelFormat format) =>
        ConvertColorCore(original, RenderingIntent.RelativeColorimetric, false, format.Sample).ConvertTo(format);

    private static string ProfileId(ColorProfile p)
    {
        // A short stable identity for the descriptor: the description plus an FNV-1a hash of the bytes.
        uint h = 2166136261;
        foreach (byte b in p.IccBytes.Span)
        {
            h = (h ^ b) * 16777619;
        }

        return p.Description + "#" + h.ToString("x8", CultureInfo.InvariantCulture);
    }
}
