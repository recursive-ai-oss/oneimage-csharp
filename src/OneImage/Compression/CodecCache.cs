using System.Buffers;

namespace OneImage.Compression;

/// <summary>
/// Per-thread cache of coders for callers that decode or encode many small blocks (TIFF strips and tiles). Reusing an
/// instance saves its tables and buffers, which are larger than a typical block. A rented coder is not shared: it
/// belongs to the renting call until it is returned, and an exception simply skips the return.
/// </summary>
internal static class CodecCache
{
    [ThreadStatic]
    private static LzwDecoder? t_gifDecoder;

    [ThreadStatic]
    private static LzwDecoder? t_tiffDecoder;

    [ThreadStatic]
    private static LzwEncoder? t_tiffEncoder;

    [ThreadStatic]
    private static Inflater? t_inflater;

    [ThreadStatic]
    private static Deflater? t_deflater;

    public static LzwDecoder RentLzwDecoder(LzwVariant variant, int minCodeSize, long maxOutputBytes, double maxExpansionRatio)
    {
        LzwDecoder? d = variant == LzwVariant.Tiff ? t_tiffDecoder : t_gifDecoder;
        if (variant == LzwVariant.Tiff)
        {
            t_tiffDecoder = null;
        }
        else
        {
            t_gifDecoder = null;
        }

        if (d is null)
        {
            return new LzwDecoder(variant, minCodeSize, maxOutputBytes, maxExpansionRatio);
        }

        d.Reset(minCodeSize, maxOutputBytes, maxExpansionRatio);
        return d;
    }

    public static void Return(LzwDecoder d, LzwVariant variant)
    {
        if (variant == LzwVariant.Tiff)
        {
            t_tiffDecoder = d;
        }
        else
        {
            t_gifDecoder = d;
        }
    }

    public static LzwEncoder RentTiffLzwEncoder(IBufferWriter<byte> sink)
    {
        LzwEncoder? e = t_tiffEncoder;
        t_tiffEncoder = null;
        if (e is null)
        {
            return new LzwEncoder(sink, LzwVariant.Tiff);
        }

        e.Reset(sink);
        return e;
    }

    public static void Return(LzwEncoder e) => t_tiffEncoder = e;

    public static Inflater RentInflater(bool zlib, long maxOutputBytes, double maxExpansionRatio)
    {
        Inflater? i = t_inflater;
        t_inflater = null;
        if (i is null)
        {
            return new Inflater(zlib, maxOutputBytes, maxExpansionRatio);
        }

        i.Reset(zlib, maxOutputBytes, maxExpansionRatio);
        return i;
    }

    public static void Return(Inflater i) => t_inflater = i;

    public static Deflater RentDeflater(IBufferWriter<byte> sink, int level, bool zlib)
    {
        Deflater? d = t_deflater;
        t_deflater = null;
        if (d is null)
        {
            return new Deflater(sink, level, zlib);
        }

        d.Reset(sink, level, zlib);
        return d;
    }

    public static void Return(Deflater d) => t_deflater = d;
}
