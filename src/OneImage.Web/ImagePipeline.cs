namespace OneImage.Web;

/// <summary>Turns a source image and normalized commands into a lazy result image and encodes it. Mirrors what <c>Image.Thumbnail</c> does, plus the options a URL can ask for.</summary>
internal static class ImagePipeline
{
    public static Image Build(ReadOnlyMemory<byte> source, ProcessingCommands c, OneImageOptions options, ImageConfiguration config)
    {
        var load = new LoadOptions { Configuration = config, Limits = options.SourceLimits, AutoOrient = c.AutoOrient };
        Image img = Image.Load(source, load);

        if (c.Width is not null || c.Height is not null)
        {
            img = Resize(img, c);
        }

        if (img.Format.Model is ColorModel.Rgb or ColorModel.Cmyk or ColorModel.Lab)
        {
            ColorProfile target = ColorProfile.SrgbCompact;
            img = img.Format.Model == ColorModel.Rgb && SameProfile(img.ColorProfile, target) ? img.AssignProfile(target) : img.ConvertColor(target);
        }
        else if (img.Format.Model == ColorModel.Gray && !SameProfile(img.ColorProfile, ColorProfile.Gray))
        {
            img = img.ConvertColor(ColorProfile.Gray);
        }

        // The ICC profile stays; EXIF, XMP and IPTC go unless asked for, because they can reveal location and device.
        return c.KeepMetadata ? img : img.StripMetadata(keepIcc: true);
    }

    public static void CheckOutput(Image img, OneImageOptions o)
    {
        if (img.Width > o.MaxWidth || img.Height > o.MaxHeight || (long)img.Width * img.Height > o.MaxOutputPixels)
        {
            throw new RequestRejectedException(400, "The resulting image would be larger than the configured limit.");
        }
    }

    public static IImageFormat ChooseFormat(ProcessingCommands c, Image img, OneImageOptions o, ImageConfiguration config)
    {
        if (c.Format is { } f)
        {
            return f;
        }

        IImageFormat? source = img.SourceFormat;
        if (source is { Encoder: not null } && o.AllowedFormats.Contains(source.Name))
        {
            return source;
        }

        return config.FindByName("png") ?? throw new RequestRejectedException(415, "No encoder for the output format.");
    }

    public static byte[] Encode(Image img, IImageFormat format, ProcessingCommands c, OneImageOptions o, ImageConfiguration config)
    {
        // Formats without alpha get transparent pixels flattened onto a colour instead of whatever the encoder would do.
        if (img.Format.Alpha != AlphaMode.None && format.Name is "jpeg" or "bmp")
        {
            img = img.Flatten(c.FlattenColor ?? o.FlattenColor);
        }
        else if (c.FlattenColor is { } flat && img.Format.Alpha != AlphaMode.None)
        {
            img = img.Flatten(flat);
        }

        ImageEncoderOptions? enc = null;
        if (c.Quality is { } q)
        {
            enc = format.Name switch
            {
                "jpeg" => new JpegEncoderOptions { Quality = q },
                "webp" => new WebPEncoderOptions { Quality = q },
                _ => null,
            };
        }

        var writer = new System.Buffers.ArrayBufferWriter<byte>(64 * 1024);
        img.Save(writer, format, new SaveOptions { Configuration = config, EncoderOptions = enc, Limits = o.SourceLimits });
        return writer.WrittenSpan.ToArray();
    }

    private static Image Resize(Image img, ProcessingCommands c)
    {
        int sw = img.Width;
        int sh = img.Height;
        if (c is { CoverWithoutCrop: true, Width: { } mw, Height: { } mh })
        {
            // rmode=min: scale so both sides reach the box, keep the aspect ratio, crop nothing.
            double s = Math.Max((double)mw / sw, (double)mh / sh);
            if (!c.Upscale && s > 1)
            {
                return img;
            }

            int tw = Math.Max(1, (int)Math.Round(sw * s));
            int th = Math.Max(1, (int)Math.Round(sh * s));
            return img.Resize(tw, th, Fit.Stretch, Anchor.Center, upscale: true, filter: c.Filter);
        }

        if (c is { Focus: { } focus, Width: { } fw, Height: { } fh })
        {
            // Cover around a focal point: scale to cover the box, then cut the box out so the focal point is as central as the edges allow.
            double s = Math.Max((double)fw / sw, (double)fh / sh);
            if (!c.Upscale && s > 1)
            {
                s = 1;
                fw = Math.Min(fw, sw);
                fh = Math.Min(fh, sh);
            }

            int scaledW = Math.Max(fw, (int)Math.Round(sw * s));
            int scaledH = Math.Max(fh, (int)Math.Round(sh * s));
            Image scaled = scaledW == sw && scaledH == sh ? img : img.Resize(scaledW, scaledH, Fit.Stretch, Anchor.Center, upscale: true, filter: c.Filter);
            int left = Math.Clamp((int)Math.Round((focus.X * scaledW) - (fw / 2.0)), 0, scaledW - fw);
            int top = Math.Clamp((int)Math.Round((focus.Y * scaledH) - (fh / 2.0)), 0, scaledH - fh);
            return scaled.Crop(left, top, fw, fh);
        }

        return img.Resize(c.Width, c.Height, c.Fit, c.Anchor, c.Upscale, c.Filter, linear: true, background: c.PadColor ?? default);
    }

    private static bool SameProfile(ColorProfile a, ColorProfile b) =>
        ReferenceEquals(a, b) || (a.Model == b.Model && a.IccBytes.Span.SequenceEqual(b.IccBytes.Span));
}
