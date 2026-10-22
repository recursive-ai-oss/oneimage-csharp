using System;
using OneImage;

/// <summary>ICC profile parsing and transform building, and EXIF parsing.</summary>
internal static class MetadataTargets
{
    private static PixelFormat FormatFor(ColorModel model) => model switch
    {
        ColorModel.Gray => PixelFormat.Gray8,
        ColorModel.Rgb => PixelFormat.Rgb8,
        ColorModel.Cmyk => PixelFormat.Cmyk8,
        _ => PixelFormat.LabF32,
    };

    private const long TransformHeapLimit = 400L << 20;

    public static void Icc(ReadOnlySpan<byte> data)
    {
        byte[] bytes = data.ToArray();
        Targets.Guard(() =>
        {
            ColorProfile profile = ColorProfile.FromIcc(bytes);
            _ = profile.LinearVariant;
            _ = profile.Description;
            ColorProfile target = ColorProfile.DefaultFor(profile.Model);

            // ColorTransform.Get keeps every transform it builds, keyed by profile instance and never evicted, so each distinct profile
            // leaks its tables (about 0.8 MB each). Stop building transforms once the heap is large so the run can go on parsing.
            if (GC.GetTotalMemory(false) > TransformHeapLimit)
            {
                return;
            }

            foreach (RenderingIntent intent in new[] { RenderingIntent.Perceptual, RenderingIntent.RelativeColorimetric })
            {
                // Both directions: the profile as source and as destination.
                foreach ((ColorProfile from, ColorProfile to) in new[] { (profile, target), (target, profile) })
                {
                    ColorTransform t = ColorTransform.Get(from, to, intent, blackPointCompensation: true);
                    PixelFormat sf = FormatFor(from.Model);
                    PixelFormat df = FormatFor(to.Model);
                    const int width = 33;
                    byte[] src = new byte[width * sf.BytesPerPixel];
                    for (int i = 0; i < src.Length; i++)
                    {
                        src[i] = sf.Sample == SampleType.F32 ? (byte)0 : (byte)((i * 37) + 11);
                    }

                    if (sf.Sample == SampleType.F32)
                    {
                        // Plausible Lab floats rather than reinterpreted junk.
                        Span<float> f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(src.AsSpan());
                        for (int i = 0; i < f.Length; i++)
                        {
                            f[i] = (i % 3) == 0 ? 50f + i : (i % 7) - 3f;
                        }
                    }

                    byte[] dst = new byte[width * df.BytesPerPixel];
                    t.ConvertRow(src, sf, dst, df, width);
                }
            }
        });
    }

    public static void Exif(ReadOnlySpan<byte> data)
    {
        byte[] bytes = data.ToArray();
        Targets.Guard(() =>
        {
            ExifData d = ExifData.Parse(bytes);
            Touch(d);
        });

        if (ExifData.TryParse(bytes, out ExifData? parsed) && parsed is not null)
        {
            Touch(parsed);
        }
    }

    private static void Touch(ExifData d)
    {
        _ = d.Orientation;
        _ = d.Make;
        _ = d.Model;
        _ = d.Software;
        _ = d.CaptureTime;
        _ = d.PixelWidth;
        _ = d.PixelHeight;
        _ = d.Gps;
    }
}
