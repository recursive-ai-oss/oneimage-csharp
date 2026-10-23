using System.Buffers.Binary;
using System.Drawing;
using System.Text;
using OneImage.Drawing;

namespace OneImage.Psd;

/// <summary>A pattern from the document's pattern resources, as straight 8-bit RGBA.</summary>
internal sealed class PatternImage
{
    public PatternImage(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Rgba { get; }
}

/// <summary>The patterns decoded so far for one document, so layers that share a pattern decode it once; the decoded bytes are capped.</summary>
internal sealed class PatternCache(IReadOnlyList<PsdBlock> documentBlocks)
{
    private const long Budget = 256L << 20;
    private readonly Dictionary<string, PatternImage?> _found = new(StringComparer.OrdinalIgnoreCase);
    private long _bytes;

    public PatternImage? Find(string id)
    {
        if (_found.TryGetValue(id, out PatternImage? image))
        {
            return image;
        }

        image = PatternLibrary.Find(documentBlocks, id);
        if (image != null)
        {
            _bytes += image.Rgba.Length;
            if (_bytes > Budget)
            {
                image = null;
            }
        }

        _found[id] = image;
        return image;
    }
}

/// <summary>
/// Finds a pattern by its unique id in the document's pattern resources (<c>Patt</c>, <c>Pat2</c>, <c>Pat3</c>; Adobe's file format
/// specification, "Patterns"). Each pattern is a header and a virtual memory array list with one array per channel, stored raw or run-length coded.
/// Only RGB and grayscale patterns of 8 or 16 bits without transparency are read; anything else gives null.
/// </summary>
internal static class PatternLibrary
{
    private const int MaxSide = 1 << 14;
    private const long MaxPixels = 1L << 24;

    public static PatternImage? Find(IReadOnlyList<PsdBlock> documentBlocks, string id)
    {
        foreach (PsdBlock b in documentBlocks)
        {
            if (b.Key is not ("Patt" or "Pat2" or "Pat3"))
            {
                continue;
            }

            ReadOnlySpan<byte> data = b.Data.Span;
            int p = 0;
            while (data.Length - p >= 16)
            {
                long length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(p));
                long end = p + 4 + length;
                if (length < 12 || end > data.Length)
                {
                    break;
                }

                ReadOnlySpan<byte> entry = data.Slice(p + 4, (int)length);
                if (TryRead(entry, id, out PatternImage? image, out bool matched))
                {
                    return image;
                }

                if (matched)
                {
                    return null;
                }

                p = (int)Math.Min(int.MaxValue, (end + 3) & ~3L);
            }
        }

        return null;
    }

    // Returns true with the image when the pattern has the id and can be read; matched says the id was this pattern's (even if unreadable).
    private static bool TryRead(ReadOnlySpan<byte> e, string id, out PatternImage? image, out bool matched)
    {
        image = null;
        matched = false;
        try
        {
            var r = new SpanReader(e);
            uint version = r.U32();
            uint mode = r.U32();
            r.Skip(4); // size as vertical and horizontal 16-bit values
            string name = r.UnicodeString();
            _ = name;
            int idLen = r.U8();
            string patternId = Encoding.Latin1.GetString(r.Bytes((uint)idLen)).TrimEnd('\0');
            if (!string.Equals(patternId, id, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            matched = true;
            if (version != 1 || mode is not (1 or 3))
            {
                return false;
            }

            uint vmaVersion = r.U32();
            uint vmaLength = r.U32();
            if (vmaVersion != 3 || vmaLength > (uint)r.Remaining)
            {
                return false;
            }

            r.Skip(16); // rectangle of the whole pattern
            uint channelCount = r.U32();
            int colorChannels = mode == 3 ? 3 : 1;
            if (channelCount < colorChannels || channelCount > 56)
            {
                return false;
            }

            byte[][]? planes = new byte[colorChannels][];
            int width = 0, height = 0;
            for (int c = 0; c < colorChannels + 1 && c < channelCount + 2; c++)
            {
                uint written = r.U32();
                if (written == 0)
                {
                    if (c < colorChannels)
                    {
                        return false;
                    }

                    break;
                }

                uint length = r.U32();
                if (length > (uint)r.Remaining || length < 23)
                {
                    return false;
                }

                if (c == colorChannels)
                {
                    // A channel after the color ones is transparency, which patterns drawn here do not have.
                    return false;
                }

                ReadOnlySpan<byte> arr = r.Bytes(length);
                var ar = new SpanReader(arr);
                ar.Skip(4); // pixel depth
                int top = ar.I32(), left = ar.I32(), bottom = ar.I32(), right = ar.I32();
                int depth = ar.U16();
                int compression = ar.U8();
                long w = (long)right - left;
                long h = (long)bottom - top;
                if (w < 1 || h < 1 || w > MaxSide || h > MaxSide || w * h > MaxPixels || depth is not (8 or 16) || (c > 0 && (w != width || h != height)))
                {
                    return false;
                }

                width = (int)w;
                height = (int)h;
                planes[c] = Decode(ar.Bytes((uint)ar.Remaining), compression, width, height, depth);
            }

            byte[] rgba = new byte[checked(width * height * 4)];
            for (int i = 0; i < width * height; i++)
            {
                if (colorChannels == 3)
                {
                    rgba[i * 4] = planes[0][i];
                    rgba[(i * 4) + 1] = planes[1][i];
                    rgba[(i * 4) + 2] = planes[2][i];
                }
                else
                {
                    rgba[i * 4] = rgba[(i * 4) + 1] = rgba[(i * 4) + 2] = planes[0][i];
                }

                rgba[(i * 4) + 3] = 255;
            }

            image = new PatternImage(width, height, rgba);
            return true;
        }
        catch (ImageFormatException)
        {
            return false;
        }
    }

    // Decodes one channel to 8-bit samples. Compression 0 is raw and 1 is run-length coded with a 16-bit byte count per row (as in layer channels).
    private static byte[] Decode(ReadOnlySpan<byte> data, int compression, int width, int height, int depth)
    {
        int bps = depth / 8;
        int rowBytes = checked(width * bps);

        // The size comes from the file: the data has to be there (raw), or at least the row table and a byte per row (run-length coded), before anything that size is allocated.
        if (compression == 0 ? data.Length < (long)rowBytes * height : compression == 1 && data.Length < 3L * height)
        {
            throw new ImageFormatException("A pattern channel is shorter than its size says.");
        }

        byte[] raw = new byte[checked(rowBytes * height)];
        switch (compression)
        {
            case 0:

                data.Slice(0, raw.Length).CopyTo(raw);
                break;
            case 1:
                {
                    long tableBytes = 2L * height;
                    if (data.Length < tableBytes)
                    {
                        throw new ImageFormatException("A pattern channel ends inside its row table.");
                    }

                    long pos = tableBytes;
                    for (int y = 0; y < height; y++)
                    {
                        int count = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(y * 2));
                        if (pos + count > data.Length)
                        {
                            throw new ImageFormatException("A pattern row extends past its channel.");
                        }

                        Rle.Unpack(data.Slice((int)pos, count), raw.AsSpan(y * rowBytes, rowBytes));
                        pos += count;
                    }

                    break;
                }

            default:
                throw new ImageFormatException("A pattern channel uses an unsupported compression.");
        }

        if (bps == 1)
        {
            return raw;
        }

        byte[] samples = new byte[width * height];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (byte)((BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(i * 2)) * 255 + 32767) / 65535);
        }

        return samples;
    }
}

/// <summary>Tiles a pattern over the document from an origin, one pattern pixel per document pixel.</summary>
internal sealed class PsdPatternBrush : Brush
{
    private readonly PatternImage _image;
    private readonly int _ox, _oy;
    private readonly string _id;

    public PatternImage Image => _image;

    public PsdPatternBrush(PatternImage image, string id, int originX, int originY)
    {
        _image = image;
        _id = id;
        _ox = originX;
        _oy = originY;
    }

    public override IReadOnlyList<OpParameter> Describe() =>
    [
        new("brush", "psd-pattern"),
        new("pattern", _id),
        new("origin-x", (long)_ox),
        new("origin-y", (long)_oy),
    ];

    public override void FillRow(int x, int y, Span<float> rgba)
    {
        int w = _image.Width;
        int h = _image.Height;
        int iy = (int)(((long)y - _oy) % h);
        if (iy < 0)
        {
            iy += h;
        }

        ReadOnlySpan<byte> row = _image.Rgba.AsSpan(iy * w * 4, w * 4);
        int n = rgba.Length / 4;
        int ix = (int)(((long)x - _ox) % w);
        if (ix < 0)
        {
            ix += w;
        }

        for (int i = 0; i < n; i++)
        {
            int s = ix * 4;
            rgba[i * 4] = row[s] * (1f / 255f);
            rgba[(i * 4) + 1] = row[s + 1] * (1f / 255f);
            rgba[(i * 4) + 2] = row[s + 2] * (1f / 255f);
            rgba[(i * 4) + 3] = 1f;
            if (++ix == w)
            {
                ix = 0;
            }
        }
    }
}

/// <summary>
/// A fill brush that draws from the fill's settings and, where Photoshop stored pixels with the layer, uses their colors instead. Photoshop's
/// own raster has its edge antialiasing and dither, which the settings cannot reproduce, so a file keeps looking as Photoshop rendered it
/// while the fill is still an editable brush wherever the stored raster is missing (a stub of 1 pixel, or none).
/// </summary>
internal sealed class PsdFillBrush : Brush
{
    private readonly Brush _drawn;
    private readonly Canvas? _stored;
    private readonly ImageRect _rect;

    public PsdFillBrush(Brush drawn, Canvas? stored, ImageRect storedRect)
    {
        _drawn = drawn;
        _stored = stored;
        _rect = storedRect;
    }

    /// <summary>Whether Photoshop's stored pixels take part.</summary>
    public bool UsesStoredPixels => _stored != null;

    public override IReadOnlyList<OpParameter> Describe() =>
        [.. _drawn.Describe(), new OpParameter("stored-pixels", _stored != null)];

    public override void FillRow(int x, int y, Span<float> rgba)
    {
        _drawn.FillRow(x, y, rgba);
        if (_stored == null || y < _rect.Y || y >= _rect.Bottom)
        {
            return;
        }

        int n = rgba.Length / 4;
        int x0 = Math.Max(x, _rect.X);
        int x1 = Math.Min(x + n, _rect.Right);
        if (x1 <= x0)
        {
            return;
        }

        RgbaFloat[] row = new RgbaFloat[x1 - x0];
        _stored.CopyTo<RgbaFloat>(row, new ImageRect(x0 - _rect.X, y - _rect.Y, x1 - x0, 1));
        for (int i = 0; i < row.Length; i++)
        {
            RgbaFloat p = row[i];
            if (p.A <= 0f)
            {
                continue; // Photoshop stored nothing here; the settings stand
            }

            int o = (x0 - x + i) * 4;
            rgba[o] = p.R;
            rgba[o + 1] = p.G;
            rgba[o + 2] = p.B;
            // The alpha stays the drawn one: the stored alpha already holds the shape's edge coverage, which the layer's mask applies again.
        }
    }
}
