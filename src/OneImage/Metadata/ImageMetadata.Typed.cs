namespace OneImage;

public sealed partial class ImageMetadata
{
    private ColorProfile? _profile;
    private bool _profileRead;

    /// <summary>
    /// Gets the embedded ICC profile parsed for use in conversions, or <see langword="null"/> when there is none or it is not
    /// a supported, valid profile (a broken embedded profile never fails an image load; it is simply not used). Parsed once and cached.
    /// </summary>
    public ColorProfile? EmbeddedColorProfile
    {
        get
        {
            if (!_profileRead)
            {
                _ = ColorProfile.TryFromIcc(IccProfile.Span, out ColorProfile? p);
                _profile = p;
                _profileRead = true;
            }

            return _profile;
        }
    }

    /// <summary>Reads the typed EXIF tags (orientation, capture time, camera, size, GPS) from <see cref="Exif"/>.</summary>
    /// <returns>The tags, or <see langword="null"/> when there is no EXIF blob.</returns>
    /// <exception cref="ImageFormatException">The EXIF blob is malformed.</exception>
    public ExifData? ReadExif() => Exif.IsEmpty ? null : ExifData.Parse(Exif.Span);

    /// <summary>Reads the typed EXIF tags without throwing.</summary>
    /// <param name="data">The tags on success.</param>
    /// <returns><see langword="true"/> when the blob is present and well formed.</returns>
    public bool TryReadExif(out ExifData? data)
    {
        data = null;
        return !Exif.IsEmpty && ExifData.TryParse(Exif.Span, out data);
    }

    /// <summary>
    /// Returns the EXIF blob corrected for writing an image of the given size: the orientation tag is set to
    /// <paramref name="orientation"/> (pass 1 once the pixels are upright, the usual case after auto-orientation) and the pixel
    /// dimension tags to <paramref name="width"/> × <paramref name="height"/>. Offsets inside maker notes stay valid.
    /// </summary>
    /// <param name="width">Output width in pixels.</param>
    /// <param name="height">Output height in pixels.</param>
    /// <param name="orientation">The orientation to record, 1..8.</param>
    /// <returns>The corrected blob, or empty when there is no EXIF blob.</returns>
    /// <exception cref="ImageFormatException">The EXIF blob is malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="orientation"/> is not in 1..8.</exception>
    public ReadOnlyMemory<byte> ExifForOutput(int width, int height, int orientation = 1) =>
        Exif.IsEmpty ? default : ExifWriter.Patch(Exif.Span, width, height, orientation);

    /// <summary>Returns a copy with the ICC profile replaced; all other fields are kept.</summary>
    /// <param name="icc">The profile bytes, or empty to remove the profile.</param>
    /// <returns>The new metadata.</returns>
    public ImageMetadata WithIccProfile(ReadOnlyMemory<byte> icc) => new()
    {
        IccProfile = icc,
        Exif = Exif,
        Xmp = Xmp,
        Iptc = Iptc,
        Orientation = Orientation,
    };

    /// <summary>Returns a copy with a different orientation value; all other fields are kept.</summary>
    /// <param name="orientation">The orientation, 1..8.</param>
    /// <returns>The new metadata.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="orientation"/> is not in 1..8.</exception>
    public ImageMetadata WithOrientation(int orientation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(orientation, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(orientation, 8);
        return new ImageMetadata
        {
            IccProfile = IccProfile,
            Exif = Exif,
            Xmp = Xmp,
            Iptc = Iptc,
            Orientation = orientation,
        };
    }

    /// <summary>
    /// Returns a copy without EXIF, XMP and IPTC blobs (they can carry location and device details). The ICC profile is kept when
    /// <paramref name="keepIcc"/> is true. The orientation value is always kept, because it describes how to display the pixels.
    /// </summary>
    /// <param name="keepIcc">Whether to keep the ICC profile.</param>
    /// <returns>The stripped metadata.</returns>
    public ImageMetadata Stripped(bool keepIcc = false) => new()
    {
        IccProfile = keepIcc ? IccProfile : default,
        Orientation = Orientation,
    };
}

/// <summary>How to turn an EXIF orientation value into pixel operations. Used by auto-orientation.</summary>
internal readonly record struct OrientationOps(bool FlipHorizontal, int RotateClockwise)
{
    /// <summary>
    /// Returns the operations that make an image with the given EXIF orientation upright: first mirror left to right
    /// when <see cref="FlipHorizontal"/> is set, then rotate clockwise by <see cref="RotateClockwise"/> degrees (0, 90, 180 or 270).
    /// 1 upright; 2 mirror; 3 rotate 180; 4 mirror vertically (mirror plus rotate 180); 5 transpose (mirror, rotate 270);
    /// 6 rotate 90; 7 transverse (mirror, rotate 90); 8 rotate 270. Values outside 1..8 mean upright.
    /// </summary>
    public static OrientationOps For(int orientation) => orientation switch
    {
        2 => new(true, 0),
        3 => new(false, 180),
        4 => new(true, 180),
        5 => new(true, 270),
        6 => new(false, 90),
        7 => new(true, 90),
        8 => new(false, 270),
        _ => new(false, 0),
    };

    public bool IsIdentity => !FlipHorizontal && RotateClockwise == 0;

    /// <summary>True when the output width and height are swapped (the rotation is 90 or 270).</summary>
    public bool SwapsAxes => RotateClockwise is 90 or 270;
}
