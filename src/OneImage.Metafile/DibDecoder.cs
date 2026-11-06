using System.Buffers.Binary;

namespace OneImage.Metafile;

/// <summary>Reads and decodes the device-independent bitmaps that records carry. All offsets and sizes come from the file and are checked.</summary>
internal static class DibDecoder
{
    /// <summary>
    /// Parses a bitmap info header (with its color table) and the pixel bytes. Offsets are relative to <paramref name="record"/>.
    /// </summary>
    public static DeviceIndependentBitmap? TryParse(ReadOnlyMemory<byte> record, uint offBmi, uint cbBmi, uint offBits, uint cbBits, MetafileLimits limits, out string? error)
    {
        error = null;
        if (cbBmi == 0 || cbBits == 0)
        {
            error = "the record has no bitmap data";
            return null;
        }

        if ((ulong)offBmi + cbBmi > (ulong)record.Length || (ulong)offBits + cbBits > (ulong)record.Length)
        {
            error = "the bitmap lies outside the record";
            return null;
        }

        ReadOnlySpan<byte> h = record.Span.Slice((int)offBmi, (int)cbBmi);
        if (h.Length < 12)
        {
            error = "the bitmap header is too short";
            return null;
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(h);
        int width, height, bitCount, compression = 0, colorsUsed = 0;
        int paletteOffset;
        int entrySize = 4;
        if (size == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]);
            paletteOffset = 12;
            entrySize = 3;
        }
        else
        {
            if (size < 40 || size > h.Length)
            {
                error = "the bitmap header has an unsupported size";
                return null;
            }

            width = BinaryPrimitives.ReadInt32LittleEndian(h[4..]);
            height = BinaryPrimitives.ReadInt32LittleEndian(h[8..]);
            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
            compression = BinaryPrimitives.ReadInt32LittleEndian(h[16..]);
            colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(h[32..]);
            paletteOffset = (int)size;
        }

        bool topDown = height < 0;
        long rows = Math.Abs((long)height);
        if (width <= 0 || rows == 0 || width > 1 << 20 || rows > 1 << 20 || (long)width * rows > limits.MaxBitmapPixels)
        {
            error = $"the bitmap size {width}x{height} is not usable or exceeds the limit";
            return null;
        }

        if (bitCount is not (1 or 2 or 4 or 8 or 16 or 24 or 32))
        {
            error = $"{bitCount} bits per pixel is not supported";
            return null;
        }

        if (compression is not (0 or 1 or 2 or 3))
        {
            error = $"bitmap compression {compression} is not supported";
            return null;
        }

        if ((compression == 1 && bitCount != 8) || (compression == 2 && bitCount != 4) || (compression == 3 && bitCount is not (16 or 32)))
        {
            error = "the bitmap compression does not fit its bit count";
            return null;
        }

        uint[] masks = [];
        if (compression == 3)
        {
            // Bit masks follow a 40-byte header; later headers carry them inside.
            const int at = 40;
            if (h.Length < at + 12)
            {
                error = "the bit masks are missing";
                return null;
            }

            masks = [BinaryPrimitives.ReadUInt32LittleEndian(h[at..]), BinaryPrimitives.ReadUInt32LittleEndian(h[(at + 4)..]), BinaryPrimitives.ReadUInt32LittleEndian(h[(at + 8)..])];
            if (size == 40)
            {
                paletteOffset = 52;
            }
        }

        uint[] palette = [];
        if (bitCount <= 8)
        {
            int count = colorsUsed > 0 ? Math.Min(colorsUsed, 1 << bitCount) : 1 << bitCount;
            int available = Math.Max(0, (h.Length - paletteOffset) / entrySize);
            count = Math.Min(count, available);
            palette = new uint[count];
            for (int i = 0; i < count; i++)
            {
                int o = paletteOffset + (i * entrySize);
                palette[i] = (uint)(h[o + 2] << 16 | h[o + 1] << 8 | h[o]);
            }
        }

        ReadOnlyMemory<byte> bits = record.Slice((int)offBits, (int)cbBits);
        if (compression is 0 or 3)
        {
            long stride = (((long)width * bitCount) + 31) / 32 * 4;
            if (stride * rows > bits.Length)
            {
                error = "the pixel data is shorter than the bitmap size says";
                return null;
            }
        }

        return new DeviceIndependentBitmap(width, (int)rows, topDown, bitCount, compression, palette, masks, bits);
    }

    public static byte[] Decode(DeviceIndependentBitmap dib, bool useAlpha, uint[] palette, uint[] masks)
    {
        int w = dib.Width, h = dib.Height;
        if ((long)w * h * 4 > int.MaxValue)
        {
            throw new ImageLimitException("The bitmap is too large to decode.");
        }

        var rgba = new byte[w * h * 4];
        ReadOnlySpan<byte> bits = dib.Bits.Span;
        if (dib.Compression is 1 or 2)
        {
            DecodeRle(dib, rgba, palette);
            return rgba;
        }

        int stride = (((w * dib.BitCount) + 31) / 32) * 4;
        for (int y = 0; y < h; y++)
        {
            int srcRow = dib.IsTopDown ? y : h - 1 - y;
            ReadOnlySpan<byte> row = bits.Slice(srcRow * stride, stride);
            Span<byte> dst = rgba.AsSpan(y * w * 4, w * 4);
            switch (dib.BitCount)
            {
                case 1:
                case 2:
                case 4:
                case 8:
                    {
                        int bpp = dib.BitCount;
                        int mask = (1 << bpp) - 1;
                        for (int x = 0; x < w; x++)
                        {
                            int bit = x * bpp;
                            int idx = (row[bit >> 3] >> (8 - bpp - (bit & 7))) & mask;
                            PutPalette(dst, x, palette, idx);
                        }

                        break;
                    }

                case 16:
                    {
                        uint rm = 0x7C00, gm = 0x03E0, bm = 0x001F;
                        if (masks.Length == 3)
                        {
                            (rm, gm, bm) = (masks[0], masks[1], masks[2]);
                        }

                        for (int x = 0; x < w; x++)
                        {
                            uint v = BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..]);
                            Put(dst, x, Scale(v, rm), Scale(v, gm), Scale(v, bm), 255);
                        }

                        break;
                    }

                case 24:
                    for (int x = 0; x < w; x++)
                    {
                        Put(dst, x, row[(x * 3) + 2], row[(x * 3) + 1], row[x * 3], 255);
                    }

                    break;
                default:
                    {
                        uint rm = 0x00FF0000, gm = 0x0000FF00, bm = 0x000000FF;
                        if (masks.Length == 3)
                        {
                            (rm, gm, bm) = (masks[0], masks[1], masks[2]);
                        }

                        for (int x = 0; x < w; x++)
                        {
                            uint v = BinaryPrimitives.ReadUInt32LittleEndian(row[(x * 4)..]);
                            byte a = 255;
                            byte r = Scale(v, rm), g = Scale(v, gm), b = Scale(v, bm);
                            if (useAlpha)
                            {
                                a = (byte)(v >> 24);
                                if (a is not (0 or 255))
                                {
                                    r = (byte)Math.Min(255, (r * 255 + (a / 2)) / a);
                                    g = (byte)Math.Min(255, (g * 255 + (a / 2)) / a);
                                    b = (byte)Math.Min(255, (b * 255 + (a / 2)) / a);
                                }
                            }

                            Put(dst, x, r, g, b, a);
                        }

                        break;
                    }
            }
        }

        return rgba;
    }

    private static byte Scale(uint v, uint mask)
    {
        if (mask == 0)
        {
            return 0;
        }

        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint field = (v & mask) >> shift;
        uint max = mask >> shift;
        return (byte)((field * 255UL + (max / 2)) / max);
    }

    private static void Put(Span<byte> dst, int x, byte r, byte g, byte b, byte a)
    {
        int o = x * 4;
        dst[o] = r;
        dst[o + 1] = g;
        dst[o + 2] = b;
        dst[o + 3] = a;
    }

    private static void PutPalette(Span<byte> dst, int x, uint[] palette, int idx)
    {
        uint c = idx < palette.Length ? palette[idx] : 0;
        Put(dst, x, (byte)(c >> 16), (byte)(c >> 8), (byte)c, 255);
    }

    private static void DecodeRle(DeviceIndependentBitmap dib, byte[] rgba, uint[] palette)
    {
        int w = dib.Width, h = dib.Height;
        ReadOnlySpan<byte> d = dib.Bits.Span;
        bool four = dib.Compression == 2;
        int x = 0, y = 0, p = 0;

        void SetPixel(int px, int py, int idx)
        {
            if ((uint)px >= (uint)w || (uint)py >= (uint)h)
            {
                return;
            }

            int row = dib.IsTopDown ? py : h - 1 - py;
            uint c = idx < palette.Length ? palette[idx] : 0;
            int o = ((row * w) + px) * 4;
            rgba[o] = (byte)(c >> 16);
            rgba[o + 1] = (byte)(c >> 8);
            rgba[o + 2] = (byte)c;
            rgba[o + 3] = 255;
        }

        while (p + 1 < d.Length && y < h)
        {
            int count = d[p];
            int val = d[p + 1];
            p += 2;
            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    SetPixel(x++, y, four ? ((i & 1) == 0 ? val >> 4 : val & 15) : val);
                }

                continue;
            }

            switch (val)
            {
                case 0:
                    x = 0;
                    y++;
                    break;
                case 1:
                    return;
                case 2:
                    if (p + 1 >= d.Length)
                    {
                        return;
                    }

                    x += d[p];
                    y += d[p + 1];
                    p += 2;
                    break;
                default:
                    {
                        int n = val;
                        int bytes = four ? (n + 1) / 2 : n;
                        if (p + bytes > d.Length)
                        {
                            return;
                        }

                        for (int i = 0; i < n; i++)
                        {
                            int b = d[p + (four ? i / 2 : i)];
                            SetPixel(x++, y, four ? ((i & 1) == 0 ? b >> 4 : b & 15) : b);
                        }

                        p += (bytes + 1) & ~1;
                        break;
                    }
            }
        }
    }
}
