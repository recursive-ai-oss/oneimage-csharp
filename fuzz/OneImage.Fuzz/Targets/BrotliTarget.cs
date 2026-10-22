using System;
using OneImage.Text.Compression;

/// <summary>The Brotli decoder behind WOFF2, fed raw streams with a small output cap and the expansion ratio WOFF2 uses.</summary>
internal static class BrotliTarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        byte[] input = data.ToArray();
        Targets.Guard(() => Brotli.Decode(input, 1 << 22, 2048, 0, out _));
    }
}
