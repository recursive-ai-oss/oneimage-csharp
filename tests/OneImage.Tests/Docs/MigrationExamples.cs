namespace OneImage.Tests.Docs;

/// <summary>The OneImage half of docs/migrating.md, run next to the ImageSharp half in <c>ImageSharpSide.cs</c>.</summary>
public sealed class MigrationExamples : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("oneimage-migrate-");
    private string _photo = "";
    private string _mark = "";

    public MigrationExamples()
    {
        // A 600x300 photo with EXIF and a small PNG watermark, written by OneImage itself.
        var px = new Rgb24[600 * 300];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgb24((byte)(i % 600 / 3), (byte)(i / 600), 90);
        }

        _photo = P("in.jpg");
        using var ms = new MemoryStream();
        Image.FromPixels<Rgb24>(px, 600, 300).Save(ms, JpegFormat.Instance);
        byte[] jpeg = ms.ToArray();
        byte[] tiff = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x0F, 0x01, 2, 0, 5, 0, 0, 0, 26, 0, 0, 0, 0, 0, 0, 0, (byte)'A', (byte)'c', (byte)'m', (byte)'e', 0];
        byte[] body = [.. "Exif\0\0"u8.ToArray(), .. tiff];
        int len = body.Length + 2;
        File.WriteAllBytes(_photo, [.. jpeg.AsSpan(0, 2).ToArray(), 0xFF, 0xE1, (byte)(len >> 8), (byte)len, .. body, .. jpeg.AsSpan(2).ToArray()]);

        var mp = new Rgba32[40 * 20];
        Array.Fill(mp, new Rgba32(255, 255, 255, 200));
        _mark = P("mark.png");
        Image.FromPixels<Rgba32>(mp, 40, 20).Save(_mark);
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private string P(string name) => Path.Combine(_dir.FullName, name);

    [Fact]
    public void LoadResizeSave()
    {
        global::ImageSharpSide.Snippets.ResizeAndSave(_photo, P("is.png"));

        Image.Load(_photo).Resize(width: 300).Save(P("oi.png"));

        Assert.Equal(Image.Identify(P("is.png")).Height, Image.Identify(P("oi.png")).Height);
    }

    [Fact]
    public void CoverThumbnail()
    {
        global::ImageSharpSide.Snippets.Thumbnail(_photo, P("is.png"));

        Image.Load(_photo).Resize(width: 200, height: 200, fit: Fit.Cover, anchor: Anchor.Center).Save(P("oi.png"));
        // or, with orientation handling, no metadata and sRGB output:
        Image.Thumbnail(_photo, 200, 200, fit: Fit.Cover).Save(P("oi2.png"));

        Assert.Equal((200, 200), (Image.Identify(P("oi.png")).Width, Image.Identify(P("oi.png")).Height));
        Assert.Equal((200, 200), (Image.Identify(P("oi2.png")).Width, Image.Identify(P("oi2.png")).Height));
        Assert.Equal(200, Image.Identify(P("is.png")).Width);
    }

    [Fact]
    public void CropRotateFlip()
    {
        global::ImageSharpSide.Snippets.Crop(_photo, P("is-c.png"));
        global::ImageSharpSide.Snippets.Rotate(_photo, P("is-r.png"));

        Image.Load(_photo).Crop(10, 10, 100, 100).Save(P("oi-c.png"));
        Image.Load(_photo).Rotate(90).Flip(FlipAxis.Horizontal).Save(P("oi-r.png"));

        Assert.Equal(Image.Identify(P("is-c.png")).Width, Image.Identify(P("oi-c.png")).Width);
        Assert.Equal(Image.Identify(P("is-r.png")).Width, Image.Identify(P("oi-r.png")).Width);
        Assert.Equal(Image.Identify(P("is-r.png")).Height, Image.Identify(P("oi-r.png")).Height);
    }

    [Fact]
    public void ConvertAndQuality()
    {
        global::ImageSharpSide.Snippets.Convert(_photo, P("is.jpg"));

        Image image = Image.Load(_photo);
        image.Save(P("oi.jpg"), new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 85 } });
        image.Save(P("oi.webp"), new SaveOptions { EncoderOptions = new WebPEncoderOptions { Quality = 80 } });

        Assert.Equal("webp", Image.Identify(P("oi.webp")).Format.Name);
    }

    [Fact]
    public void PixelAccess()
    {
        byte isRed = global::ImageSharpSide.Snippets.FirstRed(_photo);

        Image image = Image.Load(_photo);
        Rgba32[] pixels = image.ToArray<Rgba32>();   // read
        using Canvas canvas = image.ToCanvas();      // edit
        using (PixelLock<Rgb24> l = canvas.Lock<Rgb24>(new ImageRect(0, 0, canvas.Width, 1), LockAccess.ReadWrite))
        {
            Span<Rgb24> row = l.GetRow(0);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = row[x] with { G = 0 };
            }
        }

        Image edited = canvas.Snapshot();
        Assert.Equal(0, edited.ToArray<Rgba32>()[0].G);
        Assert.True(Math.Abs(isRed - pixels[0].R) <= 2); // two different JPEG decoders
    }

    [Fact]
    public void Watermark()
    {
        global::ImageSharpSide.Snippets.Watermark(_photo, _mark, P("is.png"));

        Image.Load(_photo).Composite(Image.Load(_mark), x: 20, y: 20, opacity: 0.8f).Save(P("oi.png"));

        Assert.Equal(Image.Identify(P("is.png")).Width, Image.Identify(P("oi.png")).Width);
    }

    [Fact]
    public void MetadataRead()
    {
        (string? make, bool has) = global::ImageSharpSide.Snippets.Metadata(_photo, P("is.jpg"));

        Image image = Image.Load(_photo);
        string? ourMake = image.Metadata.ReadExif()?.Make;
        bool ourHas = !image.Metadata.Exif.IsEmpty;
        image.StripMetadata().Save(P("oi.jpg"));

        Assert.Equal(make, ourMake);
        Assert.True(has && ourHas);
        Assert.True(Image.Identify(P("oi.jpg")).Metadata.Exif.IsEmpty);
    }
}
