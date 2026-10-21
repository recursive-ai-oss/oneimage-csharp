using System.Runtime.InteropServices;

namespace OneImage;

/// <summary>8-bit gray pixel.</summary>
/// <param name="V">Luminance.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Gray8(byte V) : IPixel<Gray8>
{
    /// <inheritdoc/>
    public static PixelFormat Format => PixelFormat.Gray8;
}

/// <summary>16-bit gray pixel.</summary>
/// <param name="V">Luminance.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Gray16(ushort V) : IPixel<Gray16>
{
    /// <inheritdoc/>
    public static PixelFormat Format => PixelFormat.Gray16;
}

/// <summary>32-bit float gray pixel.</summary>
/// <param name="V">Luminance.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GrayFloat(float V) : IPixel<GrayFloat>
{
    /// <inheritdoc/>
    public static PixelFormat Format => PixelFormat.GrayF32;
}

/// <summary>8-bit gray pixel with straight alpha.</summary>
/// <param name="V">Luminance.</param>
/// <param name="A">Alpha.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GrayAlpha8(byte V, byte A) : IPixel<GrayAlpha8>
{
    /// <inheritdoc/>
    public static PixelFormat Format => PixelFormat.GrayAlpha8;
}

/// <summary>8-bit RGB pixel.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Rgb24(byte R, byte G, byte B) : IPixel<Rgb24>
{
    /// <inheritdoc/>
    public static PixelFormat Format => PixelFormat.Rgb8;
}

/// <summary>8-bit RGBA pixel with straight alpha.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="A">Alpha.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Rgba32(byte R, byte G, byte B, byte A) : IPixel<Rgba32>
{
    /// <inheritdoc/>
    public static PixelFormat Format => PixelFormat.Rgba8;
}

/// <summary>16-bit RGB pixel.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Rgb48(ushort R, ushort G, ushort B) : IPixel<Rgb48>
{
    /// <inheritdoc/>
    public static PixelFormat Format => PixelFormat.Rgb16;
}

/// <summary>16-bit RGBA pixel with straight alpha.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="A">Alpha.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Rgba64(ushort R, ushort G, ushort B, ushort A) : IPixel<Rgba64>
{
    /// <inheritdoc/>
    public static PixelFormat Format => PixelFormat.Rgba16;
}

/// <summary>Half-float RGBA pixel with straight alpha.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="A">Alpha.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct RgbaHalf(Half R, Half G, Half B, Half A) : IPixel<RgbaHalf>
{
    /// <inheritdoc/>
    public static PixelFormat Format => PixelFormat.RgbaF16;
}

/// <summary>32-bit float RGB pixel.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct RgbFloat(float R, float G, float B) : IPixel<RgbFloat>
{
    /// <inheritdoc/>
    public static PixelFormat Format => PixelFormat.RgbF32;
}

/// <summary>32-bit float RGBA pixel with straight alpha.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="A">Alpha.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct RgbaFloat(float R, float G, float B, float A) : IPixel<RgbaFloat>
{
    /// <inheritdoc/>
    public static PixelFormat Format => PixelFormat.RgbaF32;
}
