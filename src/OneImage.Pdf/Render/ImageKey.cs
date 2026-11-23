using System.Globalization;
using System.Text;
using OnePdf;

namespace OneImage.Pdf.Render;

/// <summary>
/// Identifies an image across the places a page draws it, so that one decode serves them all. An XObject is its indirect object (its
/// reference), and an inline image is its bytes plus a rendering of its dictionary: the interpreter produces a new <see cref="PdfImage"/>
/// every time a stream holding an inline image is run, so the instance says nothing, while the content does. Comparing the bytes in full
/// (not only a hash) means two different images are never mistaken for one.
/// </summary>
internal readonly struct ImageKey : IEquatable<ImageKey>
{
    private readonly object? _stream;
    private readonly int _number;
    private readonly int _generation;
    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly string? _dictionary;
    private readonly int _targetWidth;
    private readonly int _targetHeight;
    private readonly int _hash;

    private ImageKey(object? stream, int number, int generation, ReadOnlyMemory<byte> bytes, string? dictionary, int targetWidth, int targetHeight)
    {
        _stream = stream;
        _number = number;
        _generation = generation;
        _bytes = bytes;
        _dictionary = dictionary;
        _targetWidth = targetWidth;
        _targetHeight = targetHeight;
        var h = default(HashCode);
        h.Add(number);
        h.Add(generation);
        h.Add(stream);
        h.Add(dictionary);
        h.Add(targetWidth);
        h.Add(targetHeight);
        h.AddBytes(bytes.Span);
        _hash = h.ToHashCode();
    }

    /// <summary>
    /// The key of an image, or null when sharing it could give a wrong picture: a color space given by a name looked up in the resources
    /// of whatever stream is drawing it (the same bytes can mean different colors in two forms), or an image dictionary this code cannot
    /// render faithfully.
    /// </summary>
    public static ImageKey? Create(in PdfImage image)
    {
        PdfDictionary dict = image.Dictionary;
        if (!ColorSpaceIsSelfContained(dict.Get("ColorSpace") ?? dict.Get("CS")))
        {
            return null;
        }

        // A decoder may use the size the image is shown at (a hint), so only images that go through a codec decoder are told apart by it.
        int tw = 0, th = 0;
        if (UsesCodecDecoder(dict.Get("Filter") ?? dict.Get("F")))
        {
            tw = image.TargetWidth;
            th = image.TargetHeight;
        }

        if (!image.IsInline)
        {
            PdfStream? s = image.Stream;
            if (s is null)
            {
                return null;
            }

            return s.Reference is { } r
                ? new ImageKey(null, r.Number, r.Generation, default, null, tw, th)
                : new ImageKey(s, 0, 0, default, null, tw, th);
        }

        string? text = Describe(dict);
        return text is null ? null : new ImageKey(null, 0, 0, image.InlineData, text, tw, th);
    }

    public bool Equals(ImageKey other) =>
        _hash == other._hash
        && _number == other._number
        && _generation == other._generation
        && ReferenceEquals(_stream, other._stream)
        && _targetWidth == other._targetWidth
        && _targetHeight == other._targetHeight
        && string.Equals(_dictionary, other._dictionary, StringComparison.Ordinal)
        && _bytes.Span.SequenceEqual(other._bytes.Span);

    public override bool Equals(object? obj) => obj is ImageKey k && Equals(k);

    public override int GetHashCode() => _hash;

    private static bool UsesCodecDecoder(PdfObject? filter)
    {
        static bool Codec(string? n) => n is not null && (n.StartsWith("DCT", StringComparison.Ordinal) || n.StartsWith("JPX", StringComparison.Ordinal) || n.StartsWith("JBIG2", StringComparison.Ordinal));
        if (filter is PdfName name)
        {
            return Codec(name.Value);
        }

        if (filter is PdfArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (Codec(array.GetName(i)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when the color space means the same wherever the image is drawn. A bare name other than a device space is looked up in the
    /// resources of the stream being run, which differ between forms; the walk follows the one chain of base/alternate spaces.
    /// </summary>
    private static bool ColorSpaceIsSelfContained(PdfObject? space)
    {
        for (int hops = 0; hops < 8; hops++)
        {
            switch (space)
            {
                case null:
                    return true;
                case PdfName n:
                    return n.Value is "DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "G" or "RGB" or "CMYK" or "Pattern";
                case PdfArray a when a.Count > 0:
                    switch (a.GetName(0))
                    {
                        case "Indexed" or "I":
                            space = a.Count > 1 ? a[1] : null;
                            continue;
                        case "Separation" or "DeviceN":
                            space = a.Count > 2 ? a[2] : null;
                            continue;
                        case "ICCBased" or "CalGray" or "CalRGB" or "Lab" or "Pattern":
                            return true;
                        default:
                            return false;
                    }

                default:
                    return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Renders an inline image's dictionary as text (every key and value, in file order), or null when it holds something that is not a
    /// plain value. Walks with an explicit stack, so the nesting of the input does not set the depth of the code.
    /// </summary>
    private static string? Describe(PdfDictionary dict)
    {
        var sb = new StringBuilder();
        var stack = new Stack<object>();
        stack.Push(dict);
        int steps = 0;
        while (stack.Count > 0)
        {
            if (++steps > 4096 || sb.Length > 1 << 16)
            {
                return null;
            }

            switch (stack.Pop())
            {
                case string literal:
                    sb.Append(literal);
                    break;
                case PdfDictionary d:
                    sb.Append("<<");
                    stack.Push(">>");
                    var keys = new List<string>(d.Keys);
                    for (int k = keys.Count - 1; k >= 0; k--)
                    {
                        stack.Push(d.Get(keys[k]) ?? (object)"null");
                        stack.Push("/" + keys[k] + " ");
                    }

                    break;
                case PdfArray a:
                    sb.Append('[');
                    stack.Push("]");
                    for (int i = a.Count - 1; i >= 0; i--)
                    {
                        stack.Push(a[i] ?? (object)"null");
                    }

                    break;
                case PdfName n:
                    sb.Append('/').Append(n.Value).Append(' ');
                    break;
                case PdfInteger i:
                    sb.Append(i.Value.ToString(CultureInfo.InvariantCulture)).Append(' ');
                    break;
                case PdfReal r:
                    sb.Append(r.Value.ToString("R", CultureInfo.InvariantCulture)).Append(' ');
                    break;
                case PdfBoolean b:
                    sb.Append(b.Value ? "true " : "false ");
                    break;
                case PdfString s:
                    sb.Append('<').Append(Convert.ToHexString(s.Bytes.Span)).Append("> ");
                    break;
                default:
                    return null;
            }
        }

        return sb.ToString();
    }
}
