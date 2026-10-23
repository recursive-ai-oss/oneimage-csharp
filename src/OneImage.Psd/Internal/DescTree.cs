using System.Buffers.Binary;
using System.Text;

namespace OneImage.Psd;

/// <summary>
/// A descriptor kept exactly as it is stored: every value remembers its type code, units and identifier forms, so a descriptor that was read can be edited
/// and written back without losing the parts that were not touched. (<see cref="Descriptor"/> flattens values for reading and drops that information.)
/// </summary>
internal sealed class DescNode
{
    /// <summary>The four-character type code: <c>Objc</c>, <c>GlbO</c>, <c>VlLs</c>, <c>doub</c>, <c>UntF</c>, <c>TEXT</c>, <c>enum</c>, <c>long</c>, <c>comp</c>, <c>bool</c>, <c>type</c>, <c>GlbC</c>, <c>alis</c>, <c>tdta</c> or <c>obj </c>.</summary>
    public string Type { get; init; } = "";

    /// <summary>For objects: the class name as stored (a counted UTF-16 string, with its terminator if it had one).</summary>
    public byte[] Name { get; init; } = EmptyName;

    /// <summary>For objects and class references: the class identifier.</summary>
    public string ClassId { get; init; } = "";

    /// <summary>For objects: the items in file order.</summary>
    public List<KeyValuePair<string, DescNode>> Items { get; } = [];

    /// <summary>For lists: the values.</summary>
    public List<DescNode> List { get; } = [];

    /// <summary>For unit floats: the four-character unit (<c>#Pxl</c>, <c>#Prc</c>, <c>#Ang</c>, ...).</summary>
    public string Unit { get; init; } = "";

    public double Double { get; init; }

    public long Long { get; init; }

    public bool Bool { get; init; }

    /// <summary>For text: the string as stored (a counted UTF-16 string); for enumerations: the type identifier is in <see cref="EnumType"/>.</summary>
    public byte[] Text { get; init; } = EmptyName;

    public string EnumType { get; init; } = "";

    public string EnumValue { get; init; } = "";

    /// <summary>For <c>alis</c>, <c>tdta</c> and references: the bytes after the type code (for the first two without their length).</summary>
    public byte[] Raw { get; init; } = [];

    private static readonly byte[] EmptyName = [0, 0, 0, 0];

    // Builders for the types a written effect uses.
    public static DescNode Obj(string classId) => new() { Type = "Objc", ClassId = classId };

    public static DescNode ListOf() => new() { Type = "VlLs" };

    public static DescNode Dbl(double v) => new() { Type = "doub", Double = v };

    public static DescNode Unitf(string unit, double v) => new() { Type = "UntF", Unit = unit, Double = v };

    public static DescNode Bool_(bool v) => new() { Type = "bool", Bool = v };

    public static DescNode Long_(long v) => new() { Type = "long", Long = v };

    public static DescNode Enum(string type, string value) => new() { Type = "enum", EnumType = type, EnumValue = value };

    public static DescNode Str(string text) => new() { Type = "TEXT", Text = CountedString(text, terminator: true) };

    /// <summary>A counted UTF-16 string, the way descriptors store text (the count includes a NUL terminator when there is one).</summary>
    public static byte[] CountedString(string s, bool terminator)
    {
        int units = s.Length + (terminator ? 1 : 0);
        byte[] b = new byte[4 + (units * 2)];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)units);
        Encoding.BigEndianUnicode.GetBytes(s, 0, s.Length, b, 4);
        return b;
    }

    public DescNode Add(string key, DescNode value)
    {
        Items.Add(new(key, value));
        return this;
    }

    public DescNode? Get(string key)
    {
        foreach (KeyValuePair<string, DescNode> kv in Items)
        {
            if (kv.Key == key)
            {
                return kv.Value;
            }
        }

        return null;
    }

    public int IndexOf(string key)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Parses a descriptor (without its leading version number). Nesting is followed with an explicit stack.</summary>
    public static DescNode Parse(ReadOnlySpan<byte> data, PsdLimits limits)
    {
        var r = new SpanReader(data);
        DescNode root = ReadHeader(ref r, "Objc");
        var stack = new List<(DescNode Node, int Remaining)> { (root, ReadCount(ref r, limits)) };
        long budget = limits.MaxDescriptorItems;
        while (stack.Count > 0)
        {
            (DescNode node, int remaining) = stack[^1];
            if (remaining == 0)
            {
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            if (--budget < 0)
            {
                throw new ImageLimitException("A descriptor holds too many items.");
            }

            stack[^1] = (node, remaining - 1);
            string key = node.Type == "VlLs" ? "" : ReadId(ref r);
            string type = Fourcc.Name(r.U32());
            DescNode value;
            switch (type)
            {
                case "Objc":
                case "GlbO":
                    value = ReadHeader(ref r, type);
                    Attach(node, key, value);
                    Push(stack, value, ReadCount(ref r, limits), limits);
                    continue;
                case "VlLs":
                    value = new DescNode { Type = "VlLs" };
                    Attach(node, key, value);
                    Push(stack, value, ReadCount(ref r, limits), limits);
                    continue;
                case "doub":
                    value = new DescNode { Type = type, Double = r.F64() };
                    break;
                case "UntF":
                    value = new DescNode { Type = type, Unit = Fourcc.Name(r.U32()), Double = r.F64() };
                    break;
                case "TEXT":
                    value = new DescNode { Type = type, Text = CountedBytes(ref r) };
                    break;
                case "enum":
                    value = new DescNode { Type = type, EnumType = ReadId(ref r), EnumValue = ReadId(ref r) };
                    break;
                case "long":
                    value = new DescNode { Type = type, Long = r.I32() };
                    break;
                case "comp":
                    value = new DescNode { Type = type, Long = (long)r.U64() };
                    break;
                case "bool":
                    value = new DescNode { Type = type, Bool = r.U8() != 0 };
                    break;
                case "type":
                case "GlbC":
                    value = new DescNode { Type = type, Name = CountedBytes(ref r), ClassId = ReadId(ref r) };
                    break;
                case "alis":
                case "tdta":
                    {
                        uint n = r.U32();
                        if (n > (uint)r.Remaining)
                        {
                            throw new ImageFormatException("A descriptor data item extends past the block.");
                        }

                        value = new DescNode { Type = type, Raw = r.Bytes(n).ToArray() };
                        break;
                    }

                case "obj ":
                    {
                        int start = r.Position;
                        Descriptor.SkipReference(ref r, limits);
                        value = new DescNode { Type = type, Raw = data.Slice(start, r.Position - start).ToArray() };
                        break;
                    }

                default:
                    throw new ImageFormatException($"Unknown descriptor item type '{type}'.");
            }

            Attach(node, key, value);
        }

        return root;
    }

    /// <summary>Writes the descriptor (without a version number). Nesting is followed with an explicit stack.</summary>
    public void Write(MemoryStream ms)
    {
        WriteHeader(ms, this);
        Be32(ms, (uint)(Type == "VlLs" ? List.Count : Items.Count));
        var stack = new List<(DescNode Node, int Next)> { (this, 0) };
        while (stack.Count > 0)
        {
            (DescNode node, int next) = stack[^1];
            int count = node.Type == "VlLs" ? node.List.Count : node.Items.Count;
            if (next >= count)
            {
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack[^1] = (node, next + 1);
            DescNode v;
            if (node.Type == "VlLs")
            {
                v = node.List[next];
            }
            else
            {
                WriteId(ms, node.Items[next].Key);
                v = node.Items[next].Value;
            }

            WriteTag(ms, v.Type);
            switch (v.Type)
            {
                case "Objc":
                case "GlbO":
                    WriteHeader(ms, v);
                    Be32(ms, (uint)v.Items.Count);
                    stack.Add((v, 0));
                    break;
                case "VlLs":
                    Be32(ms, (uint)v.List.Count);
                    stack.Add((v, 0));
                    break;
                case "doub":
                    Be64(ms, BitConverter.DoubleToUInt64Bits(v.Double));
                    break;
                case "UntF":
                    WriteTag(ms, v.Unit);
                    Be64(ms, BitConverter.DoubleToUInt64Bits(v.Double));
                    break;
                case "TEXT":
                    ms.Write(v.Text);
                    break;
                case "enum":
                    WriteId(ms, v.EnumType);
                    WriteId(ms, v.EnumValue);
                    break;
                case "long":
                    Be32(ms, (uint)(int)v.Long);
                    break;
                case "comp":
                    Be64(ms, (ulong)v.Long);
                    break;
                case "bool":
                    ms.WriteByte(v.Bool ? (byte)1 : (byte)0);
                    break;
                case "type":
                case "GlbC":
                    ms.Write(v.Name);
                    WriteId(ms, v.ClassId);
                    break;
                case "alis":
                case "tdta":
                    Be32(ms, (uint)v.Raw.Length);
                    ms.Write(v.Raw);
                    break;
                default:
                    ms.Write(v.Raw);
                    break;
            }
        }
    }

    private static void Push(List<(DescNode Node, int Remaining)> stack, DescNode node, int count, PsdLimits limits)
    {
        if (stack.Count >= limits.MaxDescriptorDepth)
        {
            throw new ImageLimitException("Descriptors are nested too deeply.");
        }

        stack.Add((node, count));
    }

    private static void Attach(DescNode parent, string key, DescNode value)
    {
        if (parent.Type == "VlLs")
        {
            parent.List.Add(value);
        }
        else
        {
            parent.Items.Add(new(key, value));
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

    private static byte[] CountedBytes(ref SpanReader r)
    {
        uint count = r.U32();
        if (count > (uint)(r.Remaining / 2))
        {
            throw new ImageFormatException("A Unicode string extends past the data.");
        }

        byte[] b = new byte[4 + (count * 2)];
        BinaryPrimitives.WriteUInt32BigEndian(b, count);
        r.Bytes(count * 2L).CopyTo(b.AsSpan(4));
        return b;
    }

    private static DescNode ReadHeader(ref SpanReader r, string type)
    {
        byte[] name = CountedBytes(ref r);
        return new DescNode { Type = type, Name = name, ClassId = ReadId(ref r) };
    }

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

    private static void WriteHeader(MemoryStream ms, DescNode n)
    {
        ms.Write(n.Name);
        WriteId(ms, n.ClassId);
    }

    private static void WriteId(MemoryStream ms, string id)
    {
        Be32(ms, id.Length == 4 ? 0u : (uint)id.Length);
        ms.Write(Encoding.Latin1.GetBytes(id));
    }

    private static void WriteTag(MemoryStream ms, string tag) => ms.Write(Encoding.Latin1.GetBytes(tag));

    private static void Be32(MemoryStream ms, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        ms.Write(b);
    }

    private static void Be64(MemoryStream ms, ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        ms.Write(b);
    }
}
