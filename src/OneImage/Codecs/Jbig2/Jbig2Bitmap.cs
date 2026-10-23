namespace OneImage.Codecs.Jbig2;

/// <summary>How a bitmap is drawn onto another one (T.88 7.4.1.5 and 6.4.5).</summary>
internal enum Jbig2CombinationOperator
{
    /// <summary>Destination OR source.</summary>
    Or = 0,

    /// <summary>Destination AND source.</summary>
    And = 1,

    /// <summary>Destination XOR source.</summary>
    Xor = 2,

    /// <summary>Destination XNOR source.</summary>
    Xnor = 3,

    /// <summary>Source replaces the destination.</summary>
    Replace = 4,
}

/// <summary>
/// A bi-level bitmap, one bit per pixel, rows packed most significant bit first and padded to a byte boundary
/// with zero bits. A set bit is the foreground (black) value. Reads outside the bitmap return 0, as the
/// standard requires for template pixels (T.88 6.2.5.2).
/// </summary>
internal sealed class Jbig2Bitmap
{
    /// <summary>Creates a zero-filled bitmap. Callers check the size against their limits first.</summary>
    public Jbig2Bitmap(int width, int height)
    {
        if (width < 0 || height < 0)
        {
            throw new ImageFormatException("A JBIG2 bitmap has a negative size.");
        }

        Width = width;
        Height = height;
        Stride = (width + 7) >> 3;
        long bytes = (long)Stride * height;
        if (bytes > int.MaxValue)
        {
            throw new ImageLimitException("A JBIG2 bitmap is too large.");
        }

        Data = new byte[bytes];
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the bytes per row.</summary>
    public int Stride { get; }

    /// <summary>Gets the packed pixels.</summary>
    public byte[] Data { get; internal set; }

    /// <summary>Returns the pixel at (x, y), or 0 outside the bitmap.</summary>
    public int Get(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return 0;
        }

        return (Data[(y * Stride) + (x >> 3)] >> (7 - (x & 7))) & 1;
    }

    /// <summary>Sets the pixel at (x, y); coordinates outside the bitmap are ignored.</summary>
    public void Set(int x, int y, int value)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return;
        }

        int i = (y * Stride) + (x >> 3);
        byte mask = (byte)(0x80 >> (x & 7));
        if (value != 0)
        {
            Data[i] |= mask;
        }
        else
        {
            Data[i] &= (byte)~mask;
        }
    }

    /// <summary>Fills every pixel with <paramref name="value"/> (0 or 1).</summary>
    public void Fill(int value)
    {
        if (value == 0)
        {
            Array.Clear(Data);
            return;
        }

        Array.Fill(Data, (byte)0xFF);
        int pad = (Stride * 8) - Width;
        if (pad > 0 && Stride > 0)
        {
            byte keep = (byte)(0xFF << pad);
            for (int y = 0; y < Height; y++)
            {
                Data[(y * Stride) + Stride - 1] = keep;
            }
        }
    }

    /// <summary>Copies the rectangle (x, y, w, h) into a new bitmap; parts outside this bitmap read as 0.</summary>
    public Jbig2Bitmap Extract(int x, int y, int w, int h)
    {
        var r = new Jbig2Bitmap(w, h);
        r.Compose(this, -x, -y, Jbig2CombinationOperator.Replace);
        return r;
    }

    /// <summary>
    /// Draws <paramref name="src"/> with its top left pixel at (dx, dy), combining with the operator. Parts of the
    /// source that fall outside this bitmap are ignored (T.88 6.4.5 step 3 c viii).
    /// </summary>
    public void Compose(Jbig2Bitmap src, long dx, long dy, Jbig2CombinationOperator op)
    {
        long x0 = Math.Max(dx, 0);
        long y0 = Math.Max(dy, 0);
        long x1 = Math.Min(dx + src.Width, Width);
        long y1 = Math.Min(dy + src.Height, Height);
        if (x0 >= x1 || y0 >= y1)
        {
            return;
        }

        int ix0 = (int)x0;
        int ix1 = (int)x1;
        int firstByte = ix0 >> 3;
        int lastByte = (ix1 - 1) >> 3;
        for (long y = y0; y < y1; y++)
        {
            int drow = (int)y * Stride;
            int srow = (int)(y - dy) * src.Stride;
            for (int b = firstByte; b <= lastByte; b++)
            {
                int mask = 0xFF;
                int lo = b << 3;
                if (lo < ix0)
                {
                    mask &= 0xFF >> (ix0 - lo);
                }

                if (lo + 8 > ix1)
                {
                    mask &= 0xFF << (lo + 8 - ix1);
                }

                int s = src.FetchByte(srow, lo - (int)dx);
                int d = Data[drow + b];
                int r = op switch
                {
                    Jbig2CombinationOperator.Or => d | (s & mask),
                    Jbig2CombinationOperator.And => d & (s | ~mask),
                    Jbig2CombinationOperator.Xor => d ^ (s & mask),
                    Jbig2CombinationOperator.Xnor => (d & ~mask) | (~(d ^ s) & mask),
                    _ => (d & ~mask) | (s & mask),
                };
                Data[drow + b] = (byte)r;
            }
        }
    }

    /// <summary>Returns the eight bits of a row starting at pixel column <paramref name="sx"/> (which may be negative); bytes outside the row read as 0.</summary>
    private int FetchByte(int rowOffset, int sx)
    {
        int i = sx >> 3;
        int r = sx & 7;
        int hi = (uint)i < (uint)Stride ? Data[rowOffset + i] : 0;
        if (r == 0)
        {
            return hi;
        }

        int lo = (uint)(i + 1) < (uint)Stride ? Data[rowOffset + i + 1] : 0;
        return (((hi << 8) | lo) >> (8 - r)) & 0xFF;
    }

    /// <summary>Returns a copy whose height is <paramref name="newHeight"/>; added rows are filled with <paramref name="fill"/>.</summary>
    public Jbig2Bitmap Resized(int newHeight, int fill)
    {
        var r = new Jbig2Bitmap(Width, newHeight);
        Array.Copy(Data, r.Data, Math.Min(Data.Length, r.Data.Length));
        if (fill != 0 && newHeight > Height)
        {
            for (int y = Height; y < newHeight; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    r.Set(x, y, 1);
                }
            }
        }

        return r;
    }
}
