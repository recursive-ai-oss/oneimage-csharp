using System;
using OneImage.Codecs.Jbig2;

/// <summary>
/// JBIG2 streams through the embedded-stream decoder (with the first half optionally supplied as the shared globals) and
/// the standalone file decoder, under small limits. First byte: bit 0 = standalone file, bit 1 = the rest starts with a big-endian
/// 16-bit globals length, then the globals, then the page data, bits 2-3 = page number minus one for files.
/// </summary>
internal static class Jbig2Target
{
    private static readonly Jbig2Limits SmallLimits = new()
    {
        MaxSegments = 4096,
        MaxReferredSegments = 256,
        MaxSymbolsPerDictionary = 1 << 14,
        MaxSymbolPixels = 1 << 18,
        MaxRetainedBytes = 1 << 24,
        MaxInstances = 1 << 18,
        MaxDecodedPixels = 1 << 24,
        MaxHuffmanTableLines = 1 << 12,
        MaxPatterns = 1 << 12,
        MaxHalftoneCells = 1 << 18,
    };

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        int mode = data[0];
        byte[] body = data[1..].ToArray();
        Targets.Guard(() =>
        {
            Jbig2Page page;
            if ((mode & 1) != 0)
            {
                page = Jbig2Codec.DecodeFile(body, 1 + ((mode >> 2) & 3), Targets.Limits, SmallLimits);
            }
            else if ((mode & 2) != 0)
            {
                if (body.Length < 2)
                {
                    return;
                }

                int cut = Math.Min((body[0] << 8) | body[1], body.Length - 2);
                page = Jbig2Codec.Decode(body.AsMemory(2 + cut), body.AsMemory(2, cut), Targets.Limits, SmallLimits);
            }
            else
            {
                page = Jbig2Codec.Decode(body, ReadOnlyMemory<byte>.Empty, Targets.Limits, SmallLimits);
            }

            if (page.Data.Length != page.Stride * page.Height || page.Stride != (page.Width + 7) / 8)
            {
                throw new InvalidOperationException("inconsistent JBIG2 page");
            }
        });
    }
}
