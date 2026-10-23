using System.Text;

namespace OneImage.Psd;

/// <summary>A parsed descriptor (the key/value structure Photoshop uses for solid colors and many other settings).</summary>
internal sealed class Descriptor
{
    public string ClassId { get; init; } = "";

    public List<KeyValuePair<string, object?>> Items { get; } = [];

    public object? Get(string key)
    {
        foreach (KeyValuePair<string, object?> kv in Items)
        {
            if (kv.Key == key)
            {
                return kv.Value;
            }
        }

        return null;
    }

    public double? Number(string key) => Get(key) switch
    {
        double d => d,
        long l => l,
        _ => null,
    };

    /// <summary>Parses a descriptor (without the 4-byte version before it). Nesting is followed with an explicit stack, never recursion.</summary>
    public static Descriptor Parse(ReadOnlySpan<byte> data, PsdLimits limits)
    {
        var r = new SpanReader(data);
        Descriptor root = ReadHeader(ref r);
        var stack = new List<Frame> { new(root, null, ReadCount(ref r, limits)) };
        long budget = limits.MaxDescriptorItems;
        while (stack.Count > 0)
        {
            Frame f = stack[^1];
            if (f.Remaining == 0)
            {
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            if (--budget < 0)
            {
                throw new ImageLimitException("A descriptor holds too many items.");
            }

            f.Remaining--;
            string key = string.Empty;
            if (f.Desc != null)
            {
                key = ReadId(ref r);
            }

            uint type = r.U32();
            object? value;
            switch (Fourcc.Name(type))
            {
                case "Objc":
                case "GlbO":
                    {
                        Descriptor child = ReadHeader(ref r);
                        int count = ReadCount(ref r, limits);
                        Attach(f, key, child);
                        Push(stack, new Frame(child, null, count), limits);
                        continue;
                    }

                case "VlLs":
                    {
                        int count = ReadCount(ref r, limits);
                        var list = new List<object?>();
                        Attach(f, key, list);
                        Push(stack, new Frame(null, list, count), limits);
                        continue;
                    }

                case "doub":
                    value = r.F64();
                    break;
                case "UntF":
                    r.Skip(4);
                    value = r.F64();
                    break;
                case "TEXT":
                    value = r.UnicodeString();
                    break;
                case "enum":
                    ReadId(ref r);
                    value = ReadId(ref r);
                    break;
                case "long":
                    value = (long)r.I32();
                    break;
                case "comp":
                    value = (long)r.U64();
                    break;
                case "bool":
                    value = r.U8() != 0;
                    break;
                case "type":
                case "GlbC":
                    r.UnicodeString();
                    value = ReadId(ref r);
                    break;
                case "alis":
                case "tdta":
                    {
                        uint n = r.U32();
                        if (n > (uint)r.Remaining)
                        {
                            throw new ImageFormatException("A descriptor data item extends past the block.");
                        }

                        r.Skip(n);
                        value = null;
                        break;
                    }

                case "obj ":
                    SkipReference(ref r, limits);
                    value = null;
                    break;
                default:
                    throw new ImageFormatException($"Unknown descriptor item type '{Fourcc.Name(type)}'.");
            }

            Attach(f, key, value);
        }

        return root;
    }

    private static void Push(List<Frame> stack, Frame f, PsdLimits limits)
    {
        if (stack.Count >= limits.MaxDescriptorDepth)
        {
            throw new ImageLimitException("Descriptors are nested too deeply.");
        }

        stack.Add(f);
    }

    private static void Attach(Frame f, string key, object? value)
    {
        if (f.Desc != null)
        {
            f.Desc.Items.Add(new(key, value));
        }
        else
        {
            f.List!.Add(value);
        }
    }

    private static int ReadCount(ref SpanReader r, PsdLimits limits)
    {
        uint n = r.U32();
        if (n > limits.MaxDescriptorItems)
        {
            throw new ImageLimitException("A descriptor holds too many items.");
        }

        return (int)n;
    }

    private static Descriptor ReadHeader(ref SpanReader r)
    {
        r.UnicodeString();
        return new Descriptor { ClassId = ReadId(ref r) };
    }

    /// <summary>An identifier: a 4-byte length (0 meaning a 4-byte code follows) and then that many characters.</summary>
    private static string ReadId(ref SpanReader r)
    {
        uint n = r.U32();
        if (n == 0)
        {
            n = 4;
        }

        if (n > (uint)r.Remaining)
        {
            throw new ImageFormatException("A descriptor identifier extends past the block.");
        }

        return Encoding.Latin1.GetString(r.Bytes(n));
    }

    internal static void SkipReference(ref SpanReader r, PsdLimits limits)
    {
        int n = ReadCount(ref r, limits);
        for (int i = 0; i < n; i++)
        {
            switch (Fourcc.Name(r.U32()))
            {
                case "prop":
                    r.UnicodeString();
                    ReadId(ref r);
                    ReadId(ref r);
                    break;
                case "Clss":
                    r.UnicodeString();
                    ReadId(ref r);
                    break;
                case "Enmr":
                    r.UnicodeString();
                    ReadId(ref r);
                    ReadId(ref r);
                    ReadId(ref r);
                    break;
                case "rele":
                    r.UnicodeString();
                    ReadId(ref r);
                    r.Skip(4);
                    break;
                case "Idnt":
                case "indx":
                    r.Skip(4);
                    break;
                case "name":
                    r.UnicodeString();
                    ReadId(ref r);
                    r.UnicodeString();
                    break;
                default:
                    throw new ImageFormatException("Unknown descriptor reference type.");
            }
        }
    }

    private sealed class Frame(Descriptor? desc, List<object?>? list, int remaining)
    {
        public Descriptor? Desc { get; } = desc;

        public List<object?>? List { get; } = list;

        public int Remaining { get; set; } = remaining;
    }

    /// <summary>Writes the data of a solid color fill block: version 16, then the <c>Clr </c> descriptor with red, green and blue from 0 to 255.</summary>
    public static byte[] WriteSolidColor(double r, double g, double b)
    {
        var ms = new MemoryStream();
        void Be32(uint v)
        {
            ms.WriteByte((byte)(v >> 24));
            ms.WriteByte((byte)(v >> 16));
            ms.WriteByte((byte)(v >> 8));
            ms.WriteByte((byte)v);
        }

        void Id(string s)
        {
            Be32(s.Length == 4 ? 0u : (uint)s.Length);
            ms.Write(Encoding.Latin1.GetBytes(s));
        }

        void Code(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        void Dbl(double d)
        {
            long bits = BitConverter.DoubleToInt64Bits(d);
            Be32((uint)(bits >> 32));
            Be32((uint)bits);
        }

        Be32(16);
        Be32(0); // empty class name
        Id("null");
        Be32(1);
        Id("Clr ");
        Code("Objc");
        Be32(0);
        Id("RGBC");
        Be32(3);
        foreach ((string key, double v) in new[] { ("Rd  ", r), ("Grn ", g), ("Bl  ", b) })
        {
            Id(key);
            Code("doub");
            Dbl(v);
        }

        return ms.ToArray();
    }
}
