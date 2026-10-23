using System;
using OneImage;
using OneImage.Codecs.Jpx;

/// <summary>
/// JPEG 2000 codestreams and JP2 files through identify and decode under small limits. The first byte selects the mode: bit 0 strict,
/// bit 1 one reduction level, bit 2 a window of the image, bit 3 identify only, bit 4 leave sYCC alone and keep palettes unexpanded.
/// </summary>
internal static class JpxTarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        int mode = data[0];
        byte[] body = data[1..].ToArray();
        var options = new JpxDecodeOptions
        {
            Limits = Targets.Limits,
            Strict = (mode & 1) != 0,
            ReductionLevels = (mode & 2) != 0 ? 1 : 0,
            Region = (mode & 4) != 0 ? new ImageRect(3, 2, 40, 30) : null,
            ConvertSycc = (mode & 16) == 0,
            ApplyPalette = (mode & 16) == 0,
            MaxDegreeOfParallelism = 2,
            MaxWorkingBytes = 64 << 20,
            MaxCodeBlocksPerTile = 1 << 18,
        };
        Targets.Guard(() =>
        {
            if ((mode & 8) != 0)
            {
                JpxCodec.Identify(body, options);
                return;
            }

            try
            {
                JpxImage image = JpxCodec.Decode(body, options);
                if (image.ChannelCount > 0)
                {
                    image.ToInterleaved(8);
                }
            }
            catch (ArgumentException) when (options.Region is not null)
            {
                // A window outside the image is the caller's error, reported as an argument exception.
            }
        });
    }
}
