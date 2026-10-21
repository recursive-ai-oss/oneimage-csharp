using System.Runtime.InteropServices;

namespace OneImage;

/// <summary>8-bit CMYK pixel: ink coverage, 0 is no ink and 255 is full coverage.</summary>
/// <param name="C">Cyan.</param>
/// <param name="M">Magenta.</param>
/// <param name="Y">Yellow.</param>
/// <param name="K">Black.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Cmyk32(byte C, byte M, byte Y, byte K) : IPixel<Cmyk32>
{
    /// <inheritdoc/>
    public static PixelFormat Format => PixelFormat.Cmyk8;
}

/// <summary>32-bit float CIE L*a*b* pixel (D50): L is 0..100, a and b are in natural units (about -128..127).</summary>
/// <param name="L">Lightness.</param>
/// <param name="A">Green-red axis.</param>
/// <param name="B">Blue-yellow axis.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct LabFloat(float L, float A, float B) : IPixel<LabFloat>
{
    /// <inheritdoc/>
    public static PixelFormat Format => PixelFormat.LabF32;
}
