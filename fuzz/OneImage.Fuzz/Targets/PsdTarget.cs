using System;
using System.IO;
using System.Threading;
using OneImage;
using OneImage.Drawing;
using OneImage.Layers;
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

                // Edit what was read (a style around the same effects plus a new one, a new brush on every fill) and write it again: the writer works from
                // the blocks it kept, which are the hostile part.
                Edit(psd.Document.Root, 0);
                using var edited = new MemoryStream();
                psd.Save(edited);
                using PsdFile back = PsdFile.Load(edited.ToArray(), options);
            }
        });
    }

    private static void Edit(GroupLayer group, int depth)
    {
        if (depth > 16)
        {
            return;
        }

        foreach (Layer l in group.Children)
        {
            LayerEffect[] kept = l.Style == null ? [] : [.. l.Style.Effects];
            if (kept.Length < LayerStyle.MaxEffects - 1)
            {
                l.Style = new LayerStyle([.. kept, new StrokeEffect { Size = 2 }]);
            }

            switch (l)
            {
                case FillLayer fill when !fill.Brush.TryGetSolid(out _):
                    fill.Brush = Brush.LinearGradient(new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(40, 30), [new ColorStop(0, new Rgba32(255, 0, 0, 255)), new ColorStop(1, new Rgba32(0, 0, 255, 128))]);
                    break;
                case GroupLayer sub:
                    Edit(sub, depth + 1);
                    break;
            }
        }
    }
}
