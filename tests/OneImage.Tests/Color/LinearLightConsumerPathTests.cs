using System.Security.Cryptography;

namespace OneImage.Tests;

/// <summary>
/// The paths a consumer takes with 16-bit and floating-point input (read to 8-bit gray, or resize in linear light) must give the same
/// bytes on every platform. The hashes are checked by CI on every operating system and architecture and equal 1.10.2's Linux x64 output.
/// </summary>
public class LinearLightConsumerPathTests
{
    private const string Gray16ToGray8Hash = "06E67C91B1E073F148982B1385C1F2F0971668AE41C7EF16F8AB1EC78EC6420D";
    private const string Gray16ResizeHash = "B5F18816E7FA50AD18CDA9210FC871739DC98A6966DA13F913A78A0A70C21613";
    private const string Rgb48ResizeHash = "43E01B985462377106E0F236F73B92CC19F458F4246552E63CF8CEA0B35E6B69";
    private const string FloatToGray8Hash = "D756D3880149BA512CE2B07668D62B9D2B0D8E75E144C239669A5572FAF9C808";
    private const string FloatResizeHash = "ACF55E66714C030D24D5131361A32D613421E086513D995A5BDD773CE97F250D";

    private static uint Next(ref uint s)
    {
        s = (s * 1664525u) + 1013904223u;
        return s >> 8;
    }

    // The failure message carries the actual hash in full, so a changed result is easy to compare across platforms.
    private static void Same(string name, string expected, string actual) => Assert.True(expected == actual, name + " actual " + actual);

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] Bytes(Gray8[] px)
    {
        var b = new byte[px.Length];
        for (int i = 0; i < b.Length; i++)
        {
            b[i] = px[i].V;
        }

        return b;
    }

    private static byte[] Bytes(Rgb24[] px)
    {
        var b = new byte[px.Length * 3];
        for (int i = 0; i < px.Length; i++)
        {
            b[3 * i] = px[i].R;
            b[(3 * i) + 1] = px[i].G;
            b[(3 * i) + 2] = px[i].B;
        }

        return b;
    }

    private static Image RoundTrip(Image img, IImageFormat format)
    {
        using var ms = new MemoryStream();
        img.Save(ms, format);
        return Image.Load(ms.ToArray());
    }

    [Fact]
    public void Gray16PngToGray8AndLinearResize()
    {
        const int W = 301, H = 203;
        var px = new Gray16[W * H];
        uint s = 12345;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int v = ((x * 65535 / (W - 1)) + (y * 7) + (int)(Next(ref s) & 0x3FF)) & 0xFFFF;
                px[(y * W) + x] = new Gray16((ushort)v);
            }
        }

        Image img = RoundTrip(Image.FromPixels<Gray16>(px, W, H), PngFormat.Instance);
        Same(nameof(Gray16ToGray8Hash), Gray16ToGray8Hash, Sha(Bytes(img.ToArray<Gray8>())));
        Same(nameof(Gray16ResizeHash), Gray16ResizeHash, Sha(Bytes(img.Resize(width: 97).ToArray<Gray8>())));
    }

    [Fact]
    public void Rgb48PngLinearResize()
    {
        const int W = 211, H = 149;
        var px = new Rgb48[W * H];
        uint s = 777;
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgb48((ushort)(Next(ref s) & 0xFFFF), (ushort)((i * 31) & 0xFFFF), (ushort)((Next(ref s) >> 3) & 0xFFFF));
        }

        Image img = RoundTrip(Image.FromPixels<Rgb48>(px, W, H), PngFormat.Instance);
        Same(nameof(Rgb48ResizeHash), Rgb48ResizeHash, Sha(Bytes(img.Resize(width: 83).ToArray<Rgb24>())));
    }

    [Fact]
    public void FloatTiffToGray8AndLinearResize()
    {
        const int W = 257, H = 131;
        var px = new GrayFloat[W * H];
        uint s = 99;
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new GrayFloat((Next(ref s) & 0xFFFF) / 65535f);
        }

        Image img = RoundTrip(Image.FromPixels<GrayFloat>(px, W, H), TiffFormat.Instance);
        Same(nameof(FloatToGray8Hash), FloatToGray8Hash, Sha(Bytes(img.ToArray<Gray8>())));
        Same(nameof(FloatResizeHash), FloatResizeHash, Sha(Bytes(img.Resize(width: 61).ToArray<Gray8>())));
    }
}
