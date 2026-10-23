using System.Drawing;
using OneImage.Drawing;

namespace OneImage.Psd;

/// <summary>Reads the descriptors of gradient fill (<c>GdFl</c>) and pattern fill (<c>PtFl</c>) layers. Anything the renderer cannot reproduce gives null, and the layer then keeps its stored pixels.</summary>
internal static class FillDescriptors
{
    /// <summary>Parses the data of a <c>GdFl</c> block (a version number and a descriptor).</summary>
    public static GradientFillSpec? ParseGradient(ReadOnlySpan<byte> data, PsdLimits limits, ColorConverter colors)
    {
        Descriptor? d = ParseDescriptor(data, limits);
        if (d == null || d.Get("Grad") is not Descriptor grad)
        {
            return null;
        }

        // Noise gradients are random numbers from Photoshop's own generator, which is not documented.
        if (grad.Get("GrdF") is not string form || form != "CstS")
        {
            return null;
        }

        // The interpolation method: absent or classic blends encoded values, linear blends light. Perceptual is a different curve that is not reproduced.
        bool linear = false;
        if (d.Get("gradientsInterpolationMethod") is string method)
        {
            if (method == "Lnr ")
            {
                linear = true;
            }
            else if (method != "Gcls")
            {
                return null;
            }
        }

        GradientStyle style;
        switch (d.Get("Type") as string)
        {
            case "Lnr ":
                style = GradientStyle.Linear;
                break;
            case "Rdl ":
                style = GradientStyle.Radial;
                break;
            case "Angl":
                style = GradientStyle.Angle;
                break;
            case "Rflc":
                style = GradientStyle.Reflected;
                break;
            case "Dmnd":
                style = GradientStyle.Diamond;
                break;
            default:
                return null;
        }

        if (grad.Get("Clrs") is not List<object?> clrs || clrs.Count == 0 || clrs.Count > 256 ||
            grad.Get("Trns") is not List<object?> trns || trns.Count == 0 || trns.Count > 256)
        {
            return null;
        }

        var colorStops = new List<PsdColorStop>(clrs.Count);
        foreach (object? o in clrs)
        {
            if (o is not Descriptor stop || stop.Get("Type") is not string type || type != "UsrS" || stop.Get("Clr ") is not Descriptor clr ||
                stop.Number("Lctn") is not { } loc || colors.ToRgb(clr) is not { } rgb)
            {
                return null;
            }

            colorStops.Add(new PsdColorStop(Fraction(loc), (float)Math.Clamp((stop.Number("Mdpn") ?? 50) / 100.0, 0, 1), rgb.R, rgb.G, rgb.B));
        }

        var opacityStops = new List<PsdOpacityStop>(trns.Count);
        foreach (object? o in trns)
        {
            if (o is not Descriptor stop || stop.Number("Opct") is not { } opct || stop.Number("Lctn") is not { } loc)
            {
                return null;
            }

            opacityStops.Add(new PsdOpacityStop(Fraction(loc), (float)Math.Clamp((stop.Number("Mdpn") ?? 50) / 100.0, 0, 1), (float)Math.Clamp(opct / 100.0, 0, 1)));
        }

        double dx = 0, dy = 0;
        if (d.Get("Ofst") is Descriptor ofst)
        {
            dx = ofst.Number("Hrzn") ?? 0;
            dy = ofst.Number("Vrtc") ?? 0;
        }

        return new GradientFillSpec
        {
            Style = style,
            Angle = d.Number("Angl") ?? 0,
            Reverse = d.Get("Rvrs") is true,
            Align = d.Get("Algn") is not false,
            Scale = (d.Number("Scl ") ?? 100) / 100.0,
            OffsetX = dx / 100.0,
            OffsetY = dy / 100.0,
            LinearLight = linear,
            Smoothness = Math.Clamp((grad.Number("Intr") ?? 4096) / 4096.0, 0, 1),
            Colors = [.. colorStops.OrderBy(static s => s.Location)],
            Opacities = [.. opacityStops.OrderBy(static s => s.Location)],
        };
    }

    /// <summary>Whether a <c>GdFl</c> block lays its gradient out on the layer's bounds ("align with layer", the default) rather than on the document.</summary>
    public static bool IsAligned(ReadOnlySpan<byte> data, PsdLimits limits) => ParseDescriptor(data, limits)?.Get("Algn") is not false;

    /// <summary>Builds the brush of a <c>PtFl</c> block, or null when the pattern is not in the document or a setting is not reproduced.</summary>
    public static Brush? MakePatternBrush(ReadOnlySpan<byte> data, PsdLimits limits, PatternCache patterns, RectangleF box, bool shape)
    {
        Descriptor? d = ParseDescriptor(data, limits);
        if (d == null || d.Get("Ptrn") is not Descriptor ptrn || ptrn.Get("Idnt") is not string id)
        {
            return null;
        }

        // Scaled, rotated or phase-shifted patterns resample the tile in ways the sample files do not show, so they keep their stored pixels.
        if ((d.Number("Scl ") is { } scl && Math.Abs(scl - 100) > 1e-6) || (d.Number("Angl") is { } ang && Math.Abs(ang) > 1e-9) || d.Get("phase") is Descriptor)
        {
            return null;
        }

        id = id.TrimEnd('\0');
        PatternImage? image = patterns.Find(id);
        if (image == null)
        {
            return null;
        }

        bool align = d.Get("Algn") is not false;
        // In a shape layer the tile starts one pixel right of the shape's left edge and at its top edge (measured on the sample files, to the pixel);
        // in a fill layer it starts at the document's corner.
        return new PsdPatternBrush(image, id, align && shape ? (int)Math.Floor(box.X) + 1 : 0, align && shape ? (int)Math.Floor(box.Y) : 0);
    }

    // Stop locations are 0..4096.
    private static float Fraction(double location) => (float)Math.Clamp(location / 4096.0, 0, 1);

    internal static Descriptor? ParseDescriptor(ReadOnlySpan<byte> data, PsdLimits limits)
    {
        try
        {
            return data.Length < 8 ? null : Descriptor.Parse(data.Slice(4), limits);
        }
        catch (ImageFormatException)
        {
            return null;
        }
    }
}

/// <summary>Turns the color descriptors of fill settings into RGB in the document's space, through the color engine for CMYK and Lab.</summary>
internal sealed class ColorConverter(ColorProfile docProfile, byte[]? sourceIcc)
{
    public (float R, float G, float B)? ToRgb(Descriptor clr)
    {
        switch (clr.ClassId)
        {
            case "RGBC":
                if (clr.Number("Rd  ") is { } r && clr.Number("Grn ") is { } g && clr.Number("Bl  ") is { } b)
                {
                    return (Unit(r / 255.0), Unit(g / 255.0), Unit(b / 255.0));
                }

                return null;
            case "Grsc":
                // Gray is stored as percent ink: 0 is white.
                if (clr.Number("Gry ") is { } gry)
                {
                    float v = Unit(1.0 - (gry / 100.0));
                    return (v, v, v);
                }

                return null;
            case "CMYC":
                if (clr.Number("Cyn ") is { } c && clr.Number("Mgnt") is { } m && clr.Number("Ylw ") is { } y && clr.Number("Blck") is { } k)
                {
                    // Percent ink, 8-bit like the pixel path; the color engine takes 0 as no ink.
                    var px = new Cmyk32[] { new(Ink(c), Ink(m), Ink(y), Ink(k)) };
                    Image img = Image.FromPixels<Cmyk32>(px, 1, 1);
                    if (sourceIcc is { Length: > 0 })
                    {
                        img = img.WithMetadata(ImageMetadata.Empty.WithIccProfile(sourceIcc));
                    }

                    return Convert(img);
                }

                return null;
            case "LbCl":
                if (clr.Number("Lmnc") is { } l && clr.Number("A   ") is { } a && clr.Number("B   ") is { } bb)
                {
                    return Convert(Image.FromPixels<LabFloat>([new LabFloat((float)Math.Clamp(l, 0, 100), (float)Math.Clamp(a, -128, 127), (float)Math.Clamp(bb, -128, 127))], 1, 1));
                }

                return null;
            default:
                return null;
        }
    }

    private (float R, float G, float B)? Convert(Image img)
    {
        var rgb = new RgbFloat[1];
        img.ConvertColor(docProfile).CopyPixelsTo<RgbFloat>(rgb, new ImageRect(0, 0, 1, 1));
        return (Unit(rgb[0].R), Unit(rgb[0].G), Unit(rgb[0].B));
    }

    private static byte Ink(double percent) => (byte)Math.Clamp((int)Math.Round(percent * 2.55), 0, 255);

    private static float Unit(double v) => (float)Math.Clamp(v, 0, 1);
}
