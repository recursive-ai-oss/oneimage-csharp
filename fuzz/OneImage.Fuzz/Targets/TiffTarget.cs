using System;
using OneImage;

/// <summary>
/// TIFF through the public load path: identify, load with validation off (so errors surface lazily as they would for a caller),
/// then pull the whole first frame, a sub-region and the last frame. The first input byte picks the sub-region.
/// </summary>
internal static class TiffTarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9)
        {
            return;
        }

        int mode = data[0];
        byte[] file = data[1..].ToArray();
        if (!TiffFormat.Instance.Matches(file))
        {
            return;
        }

        var options = new LoadOptions { Limits = Targets.Limits, MaxDegree = 1 };
        Targets.Guard(() =>
        {
            ImageInfo info = Image.Identify(file, options);
            if (info.Frames.Count == 0)
            {
                throw new InvalidOperationException("identify returned no frames");
            }
        });

        Targets.Guard(() =>
        {
            Image image = Image.Load(file, options);
            // Pixels are pulled as Rgba32, which also runs the format conversion for every stored layout.
            int w = image.Width;
            int h = image.Height;
            Rgba32[] buffer = new Rgba32[(int)Math.Min((long)w * h, 4_000_000)];
            if ((long)w * h <= buffer.Length)
            {
                image.Frames[0].CopyPixelsTo<Rgba32>(buffer, new ImageRect(0, 0, w, h), maxDegree: 1);
            }

            // A region that crosses tile and strip boundaries, chosen from the mode byte.
            int rw = Math.Max(1, w * (1 + (mode & 3)) / 5);
            int rh = Math.Max(1, h * (1 + ((mode >> 2) & 3)) / 5);
            int rx = (w - rw) * ((mode >> 4) & 3) / 3;
            int ry = (h - rh) * ((mode >> 6) & 3) / 3;
            if ((long)rw * rh <= buffer.Length)
            {
                image.Frames[0].CopyPixelsTo<Rgba32>(buffer, new ImageRect(rx, ry, rw, rh), maxDegree: 1);
            }

            if (image.FrameCount > 1)
            {
                ImageFrame last = image.Frames[image.FrameCount - 1];
                if ((long)last.Width * last.Height <= buffer.Length)
                {
                    last.CopyPixelsTo<Rgba32>(buffer, new ImageRect(0, 0, last.Width, last.Height), maxDegree: 1);
                }
            }
        });
    }
}
