// The ImageSharp half of docs/migrating.md, compiled against ImageSharp 3.1.12 so the "before" code is real too.
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageSharpSide;

internal static class Snippets
{
    public static void ResizeAndSave(string input, string output)
    {
        using Image image = Image.Load(input);
        image.Mutate(x => x.Resize(300, 0)); // 0 = keep aspect ratio
        image.Save(output);
    }

    public static void Thumbnail(string input, string output)
    {
        using Image image = Image.Load(input);
        image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(200, 200), Mode = ResizeMode.Crop, Position = AnchorPositionMode.Center }));
        image.Save(output);
    }

    public static void Crop(string input, string output)
    {
        using Image image = Image.Load(input);
        image.Mutate(x => x.Crop(new Rectangle(10, 10, 100, 100)));
        image.Save(output);
    }

    public static void Rotate(string input, string output)
    {
        using Image image = Image.Load(input);
        image.Mutate(x => x.Rotate(90).Flip(FlipMode.Horizontal));
        image.Save(output);
    }

    public static void Convert(string input, string output)
    {
        using Image image = Image.Load(input);
        image.Save(output, new JpegEncoder { Quality = 85 });
        image.SaveAsWebp(output + ".webp", new WebpEncoder { Quality = 80 });
    }

    public static byte FirstRed(string input)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(input);
        byte r = 0;
        image.ProcessPixelRows(accessor =>
        {
            Span<Rgba32> row = accessor.GetRowSpan(0);
            r = row[0].R;
            for (int x = 0; x < row.Length; x++)
            {
                row[x].G = 0;
            }
        });
        return r;
    }

    public static void Watermark(string input, string mark, string output)
    {
        using Image image = Image.Load(input);
        using Image overlay = Image.Load(mark);
        image.Mutate(x => x.DrawImage(overlay, new Point(20, 20), 0.8f));
        image.Save(output);
    }

    public static (string? Make, bool HasExif) Metadata(string input, string output)
    {
        using Image image = Image.Load(input);
        string? make = null;
        if (image.Metadata.ExifProfile != null && image.Metadata.ExifProfile.TryGetValue(ExifTag.Make, out IExifValue<string>? value))
        {
            make = value.Value;
        }

        bool has = image.Metadata.ExifProfile != null;
        image.Metadata.ExifProfile = null; // strip EXIF
        image.Save(output);
        return (make, has);
    }
}
