using System.Buffers.Binary;

namespace OneImage.Tests.Png;

/// <summary>Collects the rows a decoder delivers.</summary>
internal sealed class CollectSink : IRowSink
{
    private readonly byte[] _data;
    private readonly int _rowBytes;
    private int _next;

    public CollectSink(int width, int height, PixelFormat format)
    {
        Width = width;
        Height = height;
        Format = format;
        _rowBytes = width * format.BytesPerPixel;
        _data = new byte[(long)_rowBytes * height];
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public byte[] Data => _data;

    public int RowsWritten => _next;

    public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
    {
        Assert.Equal(_next, y);
        rows[..(rowCount * _rowBytes)].CopyTo(_data.AsSpan(y * _rowBytes));
        _next += rowCount;
    }
}

/// <summary>A decoded frame in its native pixel format.</summary>
internal sealed record Decoded(int Width, int Height, PixelFormat Format, byte[] Data)
{
    /// <summary>Straight RGBA, 16 bits per sample (8-bit values replicated, gray expanded), the common ground for oracle comparison.</summary>
    public ushort[] ToRgba16() => Canon.ToRgba16(Format, Data, Width * Height);
}

internal static class Canon
{
    public static ushort[] ToRgba16(PixelFormat f, ReadOnlySpan<byte> data, int pixels)
    {
        var o = new ushort[pixels * 4];
        int ch = f.ChannelCount;
        bool wide = f.Sample == SampleType.U16;
        bool gray = f.Model == ColorModel.Gray;
        Span<int> v = stackalloc int[4];
        for (int i = 0; i < pixels; i++)
        {
            for (int c = 0; c < ch; c++)
            {
                v[c] = wide ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(((i * ch) + c) * 2, 2)) : data[(i * ch) + c] * 257;
            }

            int r, g, b, a;
            if (gray)
            {
                r = g = b = v[0];
                a = f.HasAlpha ? v[1] : 65535;
            }
            else
            {
                r = v[0];
                g = v[1];
                b = v[2];
                a = f.HasAlpha ? v[3] : 65535;
            }

            o[i * 4] = (ushort)r;
            o[(i * 4) + 1] = (ushort)g;
            o[(i * 4) + 2] = (ushort)b;
            o[(i * 4) + 3] = (ushort)a;
        }

        return o;
    }
}

internal static class PngTestUtil
{
    public static string DataDir(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "testdata", .. parts]);

    public static Decoded DecodeWith(IImageFormat format, byte[] file, int frame = 0, ImageLimits? limits = null)
    {
        limits ??= ImageLimits.Default;
        using var ms = new MemoryStream(file);
        ImageInfo info = format.Decoder!.ReadInfo(ms, limits);
        FrameInfo fi = info.Frames[frame];
        var sink = new CollectSink(fi.Width, fi.Height, fi.Format);
        format.Decoder.DecodeFrame(ms, info, frame, new DecodeRequest(new ImageRect(0, 0, fi.Width, fi.Height)), sink, limits, CancellationToken.None);
        Assert.Equal(fi.Height, sink.RowsWritten);
        return new Decoded(fi.Width, fi.Height, fi.Format, sink.Data);
    }

    public static Decoded Decode(byte[] file, int frame = 0, ImageLimits? limits = null) => DecodeWith(PngFormat.Instance, file, frame, limits);

    /// <summary>Decodes with libvips (libpng) and returns straight RGBA16.</summary>
    public static (int W, int H, ushort[] Rgba) NetVipsDecode(byte[] file)
    {
        using var img = NetVips.Image.NewFromBuffer(file);
        int w = img.Width;
        int h = img.Height;
        int bands = img.Bands;
        bool wide = img.Format == NetVips.Enums.BandFormat.Ushort;
        Assert.True(wide || img.Format == NetVips.Enums.BandFormat.Uchar, $"unexpected vips format {img.Format}");
        byte[] raw = wide ? System.Runtime.InteropServices.MemoryMarshal.AsBytes<ushort>(img.WriteToMemory<ushort>()).ToArray() : img.WriteToMemory<byte>();
        var f = new PixelFormat(bands <= 2 ? ColorModel.Gray : ColorModel.Rgb, wide ? SampleType.U16 : SampleType.U8, bands is 2 or 4 ? AlphaMode.Straight : AlphaMode.None);
        return (w, h, Canon.ToRgba16(f, raw, w * h));
    }

    public static byte[] Encode(byte[] pixels, int w, int h, PixelFormat format, PngEncoderOptions? options = null)
    {
        Image img = FromRaw(pixels, w, h, format);
        using var ms = new MemoryStream();
        img.Save(ms, PngFormat.Instance, new SaveOptions { EncoderOptions = options });
        return ms.ToArray();
    }

    public static Image FromRaw(byte[] pixels, int w, int h, PixelFormat format)
    {
        Image img;
        if (format == PixelFormat.Rgba8)
        {
            img = Image.FromPixels<Rgba32>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgba32>(pixels), w, h);
        }
        else if (format == PixelFormat.Rgb8)
        {
            img = Image.FromPixels<Rgb24>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgb24>(pixels), w, h);
        }
        else if (format == PixelFormat.Gray8)
        {
            img = Image.FromPixels<Gray8>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Gray8>(pixels), w, h);
        }
        else if (format == PixelFormat.GrayAlpha8)
        {
            img = Image.FromPixels<GrayAlpha8>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, GrayAlpha8>(pixels), w, h);
        }
        else if (format == PixelFormat.Gray16)
        {
            img = Image.FromPixels<Gray16>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Gray16>(pixels), w, h);
        }
        else if (format == PixelFormat.Rgb16)
        {
            img = Image.FromPixels<Rgb48>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgb48>(pixels), w, h);
        }
        else if (format == PixelFormat.Rgba16)
        {
            img = Image.FromPixels<Rgba64>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgba64>(pixels), w, h);
        }
        else
        {
            throw new NotSupportedException(format.ToString());
        }

        return img;
    }

    public static ImageConfiguration Config() => new ImageConfiguration().Add(PngFormat.Instance);
}
