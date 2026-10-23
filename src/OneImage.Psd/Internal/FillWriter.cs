using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OneImage.Drawing;
using OneImage.Layers;

namespace OneImage.Psd;

/// <summary>What a brush becomes in a PSD file: the key and data of a <c>GdFl</c> or <c>PtFl</c> block and, for a pattern, the resource that holds its tile.</summary>
internal sealed class FillPlan
{
    public required string Key { get; init; }

    public required byte[] Data { get; init; }

    public PatternResource? Pattern { get; init; }
}

/// <summary>A pattern to add to the document's pattern resources: its id and the entry that stores it.</summary>
internal sealed class PatternResource
{
    public required string Id { get; init; }

    public required byte[] Entry { get; init; }
}

/// <summary>
/// Turns the brushes of an edited fill layer into the settings Photoshop keeps for gradient and pattern fills. A linear or radial gradient with
/// ordinary stops becomes a <c>GdFl</c> block, and a two-color bitmap pattern (including the hatch styles) becomes a <c>PtFl</c> block with its tile in
/// the pattern resources. Everything else (repeating or reflecting gradients, elliptic and path gradients, image brushes, translucent patterns,
/// custom brushes) has no equivalent and gives null; the layer is then written as pixels. The brush is read through its language-neutral description,
/// which is also what a port to another language would read.
/// </summary>
internal static class FillWriter
{
    // Positions in a Photoshop gradient run from 0 to 4096.
    private const int Span = 4096;

    public static FillPlan? Map(Brush brush, int width, int height, int depth)
    {
        Dictionary<string, object> p = [];
        foreach (OpParameter q in brush.Describe())
        {
            p[q.Name] = q.Value;
        }

        if (!p.TryGetValue("brush", out object? kind) || kind is not string name)
        {
            return null;
        }

        return name switch
        {
            "linear-gradient" or "radial-gradient" => MapGradient(name, p, width, height),
            "pattern" => MapPattern(p, depth),
            _ => null,
        };
    }

    private static FillPlan? MapGradient(string kind, Dictionary<string, object> p, int width, int height)
    {
        // A gradient on a box with a short side keeps its pixels (the reader does the same: Photoshop snaps such gradients to whole pixels).
        if (Math.Min(width, height) < PsdGradientBrush.MinimumSide || Str(p, "repetition") != "none" || !TryStops(Str(p, "stops"), out List<(float Pos, Rgba32 Color)>? stops))
        {
            return null;
        }

        // Photoshop blends color and opacity apart; the brush mixes premultiplied colors by default, which differs only where translucent stops differ in color.
        bool translucent = stops.Any(static s => s.Color.A < 255);
        if (translucent && p.TryGetValue("premultiplied", out object? pm) && pm is true && stops.Any(s => s.Color.R != stops[0].Color.R || s.Color.G != stops[0].Color.G || s.Color.B != stops[0].Color.B))
        {
            return null;
        }

        double angle;
        double scale;
        double cx;
        double cy;
        string type;
        if (kind == "linear-gradient")
        {
            if (!Num(p, "x1", out double x1) || !Num(p, "y1", out double y1) || !Num(p, "x2", out double x2) || !Num(p, "y2", out double y2))
            {
                return null;
            }

            double dx = x2 - x1;
            double dy = y2 - y1;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (!(length > 0))
            {
                return null;
            }

            angle = Math.Atan2(-dy / length, dx / length) * 180.0 / Math.PI;
            scale = length / Chord(angle, width, height);
            // The brush measures at pixel centers and Photoshop at integer coordinates, so the middle of the gradient moves half a pixel.
            cx = ((x1 + x2) / 2) - 0.5;
            cy = ((y1 + y2) / 2) - 0.5;
            type = "Lnr ";
        }
        else
        {
            if (!Num(p, "cx", out double ox) || !Num(p, "cy", out double oy) || !Num(p, "radius", out double radius))
            {
                return null;
            }

            angle = 0;
            scale = 2 * radius / Chord(0, width, height);
            cx = ox - 0.5;
            cy = oy - 0.5;
            type = "Rdl ";
        }

        if (!(scale >= 0.001 && scale <= 100) || !double.IsFinite(cx) || !double.IsFinite(cy) || Math.Abs(cx) > 1e7 || Math.Abs(cy) > 1e7)
        {
            return null;
        }

        DescNode colors = DescNode.ListOf();
        DescNode opacities = DescNode.ListOf();
        foreach ((float pos, Rgba32 c) in stops)
        {
            long at = Math.Clamp((long)Math.Round(pos * Span), 0, Span);
            colors.List.Add(Obj("Clrt")
                .Add("Clr ", Obj("RGBC").Add("Rd  ", DescNode.Dbl(c.R)).Add("Grn ", DescNode.Dbl(c.G)).Add("Bl  ", DescNode.Dbl(c.B)))
                .Add("Type", DescNode.Enum("Clry", "UsrS")).Add("Lctn", DescNode.Long_(at)).Add("Mdpn", DescNode.Long_(50)));
            opacities.List.Add(Obj("TrnS")
                .Add("Opct", DescNode.Unitf("#Prc", c.A / 255.0 * 100.0)).Add("Lctn", DescNode.Long_(at)).Add("Mdpn", DescNode.Long_(50)));
        }

        // The box is the document, so the offset is the gradient's center relative to the middle of the document, in percent of its size.
        DescNode root = Obj("null")
            .Add("Dthr", DescNode.Bool_(false))
            .Add("Angl", DescNode.Unitf("#Ang", angle))
            .Add("Type", DescNode.Enum("GrdT", type))
            .Add("Algn", DescNode.Bool_(false))
            .Add("Scl ", DescNode.Unitf("#Prc", scale * 100.0))
            .Add("Ofst", Obj("Pnt ")
                .Add("Hrzn", DescNode.Unitf("#Prc", (cx - (width / 2.0)) / width * 100.0))
                .Add("Vrtc", DescNode.Unitf("#Prc", (cy - (height / 2.0)) / height * 100.0)))
            .Add("Grad", Obj("Grdn")
                .Add("Nm  ", DescNode.Str("Custom"))
                .Add("GrdF", DescNode.Enum("GrdF", "CstS"))
                .Add("Intr", DescNode.Dbl(0))
                .Add("Clrs", colors)
                .Add("Trns", opacities));
        return new FillPlan { Key = "GdFl", Data = Versioned(root) };
    }

    // The length of the line through the middle of the box along the angle, as the reader lays gradients out.
    private static double Chord(double degrees, double w, double h)
    {
        double a = degrees * Math.PI / 180.0;
        double cos = Math.Cos(a);
        double sin = Math.Sin(a);
        return Math.Min(Math.Abs(cos) > 1e-9 ? w / Math.Abs(cos) : double.MaxValue, Math.Abs(sin) > 1e-9 ? h / Math.Abs(sin) : double.MaxValue);
    }

    private static FillPlan? MapPattern(Dictionary<string, object> p, int depth)
    {
        if (depth is not (8 or 16) || p.ContainsKey("m11") || !Num(p, "width", out double wd) || !Num(p, "height", out double ht) ||
            !TryColor(Str(p, "foreground"), out Rgba32 fore) || !TryColor(Str(p, "background"), out Rgba32 back) || fore.A != 255 || back.A != 255)
        {
            return null;
        }

        int w = (int)wd;
        int h = (int)ht;
        string cells = Str(p, "cells");
        if (w < 1 || h < 1 || w > 4096 || h > 4096 || cells.Length != w * h)
        {
            return null;
        }

        int bytes = depth / 8;
        byte[][] planes = [new byte[w * h * bytes], new byte[w * h * bytes], new byte[w * h * bytes]];
        for (int i = 0; i < cells.Length; i++)
        {
            Rgba32 c = cells[i] == '1' ? fore : back;
            Put(planes[0], i, c.R, bytes);
            Put(planes[1], i, c.G, bytes);
            Put(planes[2], i, c.B, bytes);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([(byte)depth, (byte)(w >> 8), (byte)w, (byte)(h >> 8), (byte)h]);
        foreach (byte[] plane in planes)
        {
            hash.AppendData(plane);
        }

        byte[] digest = hash.GetHashAndReset();
        string id = Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
        id = $"{id[..8]}-{id[8..12]}-{id[12..16]}-{id[16..20]}-{id[20..32]}";
        string title = "Pattern " + id[..8];

        DescNode root = Obj("null")
            .Add("Ptrn", Obj("Ptrn").Add("Nm  ", DescNode.Str(title)).Add("Idnt", DescNode.Str(id)))
            .Add("Angl", DescNode.Unitf("#Ang", 0))
            .Add("Scl ", DescNode.Unitf("#Prc", 100))
            .Add("Algn", DescNode.Bool_(false));
        return new FillPlan { Key = "PtFl", Data = Versioned(root), Pattern = new PatternResource { Id = id, Entry = PatternEntry(title, id, w, h, depth, planes) } };
    }

    /// <summary>The id a pattern is stored under: its own id when it has one, else a GUID-shaped hash of its pixels.</summary>
    public static string PatternId(EffectPattern pattern)
    {
        if (pattern.Id != null)
        {
            return pattern.Id;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([(byte)(pattern.Width >> 8), (byte)pattern.Width, (byte)(pattern.Height >> 8), (byte)pattern.Height]);
        hash.AppendData(pattern.Pixels);
        string id = Convert.ToHexString(hash.GetHashAndReset().AsSpan(0, 16)).ToLowerInvariant();
        return $"{id[..8]}-{id[8..12]}-{id[12..16]}-{id[16..20]}-{id[20..32]}";
    }

    /// <summary>The pattern resource entry for an effect's pattern, or null when it cannot be stored (translucent pixels, a side beyond 4096, or a depth other than 8 and 16).</summary>
    public static PatternResource? ResourceFor(EffectPattern pattern, int depth)
    {
        int w = pattern.Width;
        int h = pattern.Height;
        if (depth is not (8 or 16) || w > 4096 || h > 4096)
        {
            return null;
        }

        ReadOnlySpan<byte> px = pattern.Pixels;
        for (int i = 3; i < px.Length; i += 4)
        {
            if (px[i] != 255)
            {
                return null;
            }
        }

        int bytes = depth / 8;
        byte[][] planes = [new byte[w * h * bytes], new byte[w * h * bytes], new byte[w * h * bytes]];
        for (int i = 0; i < w * h; i++)
        {
            Put(planes[0], i, px[i * 4], bytes);
            Put(planes[1], i, px[(i * 4) + 1], bytes);
            Put(planes[2], i, px[(i * 4) + 2], bytes);
        }

        string id = PatternId(pattern);
        return new PatternResource { Id = id, Entry = PatternEntry("Pattern " + id[..8], id, w, h, depth, planes) };
    }

    private static void Put(byte[] plane, int index, byte v, int bytes)
    {
        if (bytes == 1)
        {
            plane[index] = v;
        }
        else
        {
            plane[index * 2] = v;
            plane[(index * 2) + 1] = v;
        }
    }

    // One pattern as the pattern resources store it (Adobe's file format specification, "Patterns"): the length, a header, the name and id, and a virtual
    // memory array list with one raw plane per color channel. The slot count and the unused tail follow what Photoshop writes.
    private static byte[] PatternEntry(string title, string id, int w, int h, int depth, byte[][] planes)
    {
        var arrays = new MemoryStream();
        foreach (byte[] plane in planes)
        {
            var arr = new MemoryStream();
            U32(arr, (uint)depth);
            Rect(arr, w, h);
            U16(arr, (ushort)depth);
            arr.WriteByte(0); // raw
            arr.Write(plane);
            U32(arrays, 1);
            U32(arrays, (uint)arr.Length);
            arrays.Write(arr.ToArray());
        }

        arrays.Write(new byte[92]);

        var entry = new MemoryStream();
        U32(entry, 1);
        U32(entry, 3); // RGB
        U16(entry, (ushort)h);
        U16(entry, (ushort)w);
        U32(entry, (uint)(title.Length + 1));
        entry.Write(Encoding.BigEndianUnicode.GetBytes(title));
        U16(entry, 0);
        entry.WriteByte((byte)id.Length);
        entry.Write(Encoding.Latin1.GetBytes(id));
        U32(entry, 3);
        U32(entry, (uint)(16 + 4 + arrays.Length));
        Rect(entry, w, h);
        U32(entry, 24);
        entry.Write(arrays.ToArray());

        var all = new MemoryStream();
        U32(all, (uint)entry.Length);
        all.Write(entry.ToArray());
        while (all.Length % 4 != 0)
        {
            all.WriteByte(0);
        }

        return all.ToArray();
    }

    private static void Rect(MemoryStream ms, int w, int h)
    {
        U32(ms, 0);
        U32(ms, 0);
        U32(ms, (uint)h);
        U32(ms, (uint)w);
    }

    private static void U32(MemoryStream ms, uint v)
    {
        ms.WriteByte((byte)(v >> 24));
        ms.WriteByte((byte)(v >> 16));
        ms.WriteByte((byte)(v >> 8));
        ms.WriteByte((byte)v);
    }

    private static void U16(MemoryStream ms, ushort v)
    {
        ms.WriteByte((byte)(v >> 8));
        ms.WriteByte((byte)v);
    }

    private static DescNode Obj(string cls) => new() { Type = "Objc", Name = DescNode.CountedString("", terminator: true), ClassId = cls };

    private static byte[] Versioned(DescNode root)
    {
        var ms = new MemoryStream();
        ms.Write([0, 0, 0, 16]);
        root.Write(ms);
        return ms.ToArray();
    }

    private static string Str(Dictionary<string, object> p, string key) => p.TryGetValue(key, out object? v) && v is string s ? s : "";

    private static bool Num(Dictionary<string, object> p, string key, out double value)
    {
        value = 0;
        if (p.TryGetValue(key, out object? v))
        {
            switch (v)
            {
                case double d when double.IsFinite(d):
                    value = d;
                    return true;
                case long l:
                    value = l;
                    return true;
            }
        }

        return false;
    }

    private static bool TryColor(string hex, out Rgba32 color)
    {
        color = default;
        if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v))
        {
            return false;
        }

        color = new Rgba32((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    // "position:rrggbbaa" entries separated by semicolons, as the brush describes its stops; positions must lie in 0..1.
    private static bool TryStops(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out List<(float Pos, Rgba32 Color)>? stops)
    {
        stops = null;
        if (text.Length == 0)
        {
            return false;
        }

        var list = new List<(float Pos, Rgba32 Color)>();
        foreach (string part in text.Split(';'))
        {
            int colon = part.IndexOf(':');
            if (colon < 0 || !float.TryParse(part.AsSpan(0, colon), NumberStyles.Float, CultureInfo.InvariantCulture, out float pos) ||
                !TryColor(part[(colon + 1)..], out Rgba32 c) || !(pos >= 0f && pos <= 1f) || list.Count >= 256)
            {
                return false;
            }

            list.Add((pos, c));
        }

        stops = list;
        return true;
    }
}
