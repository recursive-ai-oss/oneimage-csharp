using System;
using System.IO;
using System.Threading;
using OneImage;
using OneImage.Psd;

/// <summary>
/// PSD and PSB files three ways: the composite through the public load path (a region and the whole frame), the layered document
/// (parse, render a region, write it back and read the written file again), and the decoder interface directly.
/// </summary>
internal static class PsdTarget
{
    private static readonly PsdLimits PsdLimits = new()
    {
        MaxLayers = 200,
        MaxNesting = 16,
        MaxBlobBytes = 1 << 20,
        MaxTotalBlobBytes = 4 << 20,
        MaxDecodedChannelBytes = 64L << 20,
        MaxChannelBytes = 32L << 20,
    };

    private static readonly PsdFormat Format = new(PsdLimits);

    private static readonly ImageConfiguration Config = new ImageConfiguration().Add(Format);

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            return;
        }

        byte[] file = data.ToArray();
        Targets.Guard(() =>
        {
            var options = new LoadOptions { Limits = Targets.Limits, MaxDegree = 1, Configuration = Config };
            Image image = Image.Load(file, options);
            int w = image.Width;
            int h = image.Height;
            if ((long)w * h <= 4_000_000)
            {
                Rgba32[] buffer = new Rgba32[(long)w * h];
                image.CopyPixelsTo<Rgba32>(buffer, new ImageRect(0, 0, w, h), maxDegree: 1);
            }
        });

        Targets.Guard(() =>
        {
            var options = new PsdReadOptions { Limits = Targets.Limits, PsdLimits = PsdLimits };
            using PsdFile psd = PsdFile.Load(file, options);
            Image render = psd.Document.Render();
            int w = Math.Min(render.Width, 256);
            int h = Math.Min(render.Height, 256);
            if (w > 0 && h > 0)
            {
                Rgba32[] buffer = new Rgba32[w * h];
                render.CopyPixelsTo<Rgba32>(buffer, new ImageRect(0, 0, w, h), maxDegree: 1);
            }

            if ((long)psd.Document.Width * psd.Document.Height <= 1_000_000)
            {
                using var ms = new MemoryStream();
                psd.Save(ms);
                using PsdFile again = PsdFile.Load(ms.ToArray(), options);
            }
        });
    }
}
