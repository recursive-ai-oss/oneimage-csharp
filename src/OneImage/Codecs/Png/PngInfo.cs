using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// PNG-specific facts read from the headers, available as <see cref="ImageInfo.DecoderState"/> for images decoded as PNG.
/// The color-space chunks are reported as stored; the library does not apply them to pixels (an embedded ICC profile
/// is carried in <see cref="ImageInfo.Metadata"/>).
/// </summary>
public sealed class PngInfo
{
    internal PngInfo()
    {
    }

    /// <summary>Gets the IHDR color type (0 gray, 2 RGB, 3 palette, 4 gray+alpha, 6 RGBA).</summary>
    public int ColorType { get; internal set; }

    /// <summary>Gets the IHDR bit depth (1, 2, 4, 8 or 16).</summary>
    public int BitDepth { get; internal set; }

    /// <summary>Gets whether the image uses Adam7 interlacing.</summary>
    public bool Interlaced { get; internal set; }

    /// <summary>Gets whether the file is an APNG whose frames are the animation (<see cref="ImageInfo.Frames"/> are then full-canvas composites).</summary>
    public bool IsAnimated { get; internal set; }

    /// <summary>Gets the gAMA value in units of 1/100000 (45455 means gamma 0.45455), or <see langword="null"/>.</summary>
    public uint? Gamma { get; internal set; }

    /// <summary>Gets the cHRM values (white x/y, red x/y, green x/y, blue x/y, each in units of 1/100000), or <see langword="null"/>.</summary>
    public IReadOnlyList<int>? Chromaticities { get; internal set; }

    /// <summary>Gets the sRGB rendering intent (0 perceptual, 1 relative, 2 saturation, 3 absolute), or <see langword="null"/>.</summary>
    public int? SrgbIntent { get; internal set; }

    /// <summary>Gets the cICP code points (primaries, transfer, matrix, full-range flag), or <see langword="null"/>.</summary>
    public IReadOnlyList<int>? CicP { get; internal set; }

    internal int Width;
    internal int Height;
    internal int Channels;
    internal PixelFormat Natural;
    internal PixelFormat CanvasFormat;
    internal PngUnpackContext Unpack = new();
    internal PngFrameDesc[] Frames = [];
    internal ApngCache Cache = new();
}

/// <summary>One APNG frame (or the single still image) as described by the headers.</summary>
internal struct PngFrameDesc
{
    public int X;
    public int Y;
    public int Width;
    public int Height;
    public ushort DelayNum;
    public ushort DelayDen;
    public byte Dispose;
    public byte Blend;

    /// <summary>Stream offset of the first data chunk header (IDAT for the still image, fdAT otherwise).</summary>
    public long DataOffset;
    public bool IsFdat;
}

/// <summary>The most recently composited APNG frame, kept so playing frames in order does not recomposite from the start.</summary>
internal sealed class ApngCache
{
    private ApngWork? _work;

    public ApngWork? Take() => Interlocked.Exchange(ref _work, null);

    public void Put(ApngWork work) => Interlocked.Exchange(ref _work, work);
}

internal sealed class ApngWork
{
    /// <summary>Index of the frame the canvas currently shows (state after drawing it, before its disposal).</summary>
    public int Index = -1;
    public byte[] Canvas = [];

    /// <summary>Canvas as it was before the frame at <see cref="Index"/> was drawn; only kept when that frame disposes to previous.</summary>
    public byte[]? Snapshot;
}
