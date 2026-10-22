using System;
using OneImage;
using OneImage.Compression;

/// <summary>Drives the inflater and the LZW decoder directly with small output caps.</summary>
internal static class CompressionTargets
{
    private const int OutputCap = 1 << 20;

    /// <summary>First byte: bit 0 = zlib wrapper, bit 1 = apply an expansion ratio, bits 2-4 = output chunk size (16 B to 2 KiB).</summary>
    public static void Inflate(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        int mode = data[0];
        byte[] input = data[1..].ToArray();
        byte[] output = new byte[16 << ((mode >> 2) & 7)];
        Targets.Guard(() =>
        {
            using var inflater = new Inflater((mode & 1) != 0, OutputCap, (mode & 2) != 0 ? 50 : 0);
            Drain(input, output, (input, o) => { var s = inflater.Inflate(input, o, out int c, out int w); return (s, c, w); });
        });
    }

    /// <summary>First byte: bit 0 = TIFF (else GIF), bits 1-3 = GIF minimum code size 1-8 (invalid ones must throw ArgumentOutOfRange only from the constructor, which the target avoids), bit 4 = ratio limit.</summary>
    public static void Lzw(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        int mode = data[0];
        byte[] input = data[1..].ToArray();
        bool tiff = (mode & 1) != 0;
        int minSize = 2 + ((mode >> 1) & 7) % 7;
        byte[] output = new byte[512];
        Targets.Guard(() =>
        {
            var decoder = new LzwDecoder(tiff ? LzwVariant.Tiff : LzwVariant.Gif, minSize, OutputCap, (mode & 16) != 0 ? 50 : 0);
            Drain(input, output, (input, o) => { var s = decoder.Decode(input, o, out int c, out int w); return (s, c, w); });
        });
    }

    private static void Drain(ReadOnlySpan<byte> input, byte[] output, Func<ReadOnlySpan<byte>, byte[], (InflateStatus, int, int)> step)
    {
        long delivered = 0;
        // A decoder that reports progress must make some; bound the loop so a stuck decoder shows up as a finding, not a hang.
        for (int guard = 0; guard < (input.Length * 2) + OutputCap + 64; guard++)
        {
            (InflateStatus status, int consumed, int written) = step(input, output);
            delivered += written;
            if (consumed < 0 || consumed > input.Length || written < 0 || written > output.Length)
            {
                throw new InvalidOperationException("decoder reported impossible progress");
            }

            input = input[consumed..];
            if (status == InflateStatus.Done)
            {
                return;
            }

            if (status == InflateStatus.NeedInput && input.Length == 0)
            {
                return;
            }

            if (consumed == 0 && written == 0)
            {
                throw new InvalidOperationException("decoder made no progress");
            }
        }

        throw new InvalidOperationException("decoder did not finish within the progress bound after " + delivered + " bytes");
    }
}
