using System.Buffers;

namespace OneImage.Tests.Docs;

/// <summary>
/// The code shown in README.md and docs/*.md, compiled and run. Each test is one snippet; when an example in the
/// documentation changes, change it here too.
/// </summary>
public sealed class DocsExamples : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("oneimage-docs-");

    // Images loaded from a path keep a shared read handle on the file while they are reachable. On Windows a directory
    // cannot be removed while a handle inside it is open, so let the finalizers release them before cleaning up.
    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _dir.Delete(recursive: true);
    }

    private string P(string name) => Path.Combine(_dir.FullName, name);

    // A 600x400 gradient saved as a JPEG with an EXIF block (camera "Acme", orientation 6 = rotate 90 clockwise to view).
    private string MakePhoto(string name = "photo.jpg", int width = 600, int height = 400, bool exif = true)
    {
        var px = new Rgb24[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                px[(y * width) + x] = new Rgb24((byte)(x * 255 / width), (byte)(y * 255 / height), 128);
            }
        }

        string path = P(name);
        var bytes = new ArrayBufferWriter<byte>();
        Image.FromPixels<Rgb24>(px, width, height).Save(bytes, JpegFormat.Instance);
        byte[] jpeg = bytes.WrittenMemory.ToArray();
        if (exif)
        {
            byte[] tiff =
            [
                (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0,
                2, 0,
                0x0F, 0x01, 2, 0, 5, 0, 0, 0, 38, 0, 0, 0,   // Make, ASCII, 5 bytes at offset 38
                0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0,    // Orientation = 6
                0, 0, 0, 0,
                (byte)'A', (byte)'c', (byte)'m', (byte)'e', 0,
            ];
            byte[] app1Body = [.. "Exif\0\0"u8.ToArray(), .. tiff];
            int len = app1Body.Length + 2;
            byte[] seg = [0xFF, 0xE1, (byte)(len >> 8), (byte)len, .. app1Body];
            jpeg = [.. jpeg.AsSpan(0, 2).ToArray(), .. seg, .. jpeg.AsSpan(2).ToArray()];
        }

        File.WriteAllBytes(path, jpeg);
        return path;
    }

    [Fact]
    public void OneLiners()
    {
        string photo = MakePhoto();
        string thumbPath = P("thumb.webp");

        Image.Thumbnail(photo, 200).Save(thumbPath);

        Image thumb = Image.Load(thumbPath);
        Assert.Equal(200, thumb.Width);
        Assert.Equal(300, thumb.Height); // the stored orientation 6 was applied: 400x600 upright

        // Load, resize, save: nothing is decoded until Save.
        Image.Load(photo).Resize(width: 300).Save(P("small.png"));
        Assert.Equal(300, Image.Identify(P("small.png")).Width);

        // Crop and convert.
        Image.Load(photo).Crop(10, 10, 100, 100).Save(P("crop.bmp"));
        Assert.Equal(100, Image.Identify(P("crop.bmp")).Height);
    }

    [Fact]
    public void ResizeFits()
    {
        Image img = Image.Load(MakePhoto(exif: false)); // 600x400

        Assert.Equal((300, 200), (img.Resize(width: 300).Width, img.Resize(width: 300).Height));
        Image box = img.Resize(width: 200, height: 200, fit: Fit.Cover, anchor: Anchor.Smart);
        Assert.Equal((200, 200), (box.Width, box.Height));
        Image padded = img.Resize(width: 200, height: 200, fit: Fit.Pad, background: new Rgba32(255, 255, 255, 255));
        Assert.Equal((200, 200), (padded.Width, padded.Height));
        Image sharpEdges = img.Resize(width: 100, filter: ResamplingFilter.Mitchell);
        Assert.Equal(100, sharpEdges.Width);
    }

    [Fact]
    public void RotateFlipAndTone()
    {
        Image img = Image.Load(MakePhoto(exif: false)); // 600x400

        Image r90 = img.Rotate(90);
        Assert.Equal((400, 600), (r90.Width, r90.Height));
        Image tilted = img.Rotate(15, background: new Rgba32(255, 255, 255, 255));
        Assert.True(tilted.Width > 600);
        Assert.Equal(600, img.Flip(FlipAxis.Horizontal).Width);

        Image edited = img.Grayscale().BrightnessContrast(brightness: 0.1, contrast: 0.2).Gamma(1.1);
        Assert.Equal(600, edited.Width);
    }

    [Fact]
    public void PerspectiveAndResizeModes()
    {
        Image img = Image.Load(MakePhoto(exif: false)); // 600x400

        Image tapered = img.Perspective(new ProjectiveTransformBuilder().AppendTaper(TaperSide.Top, TaperCorner.Both, 0.7));
        Assert.Equal((600, 400), (tapered.Width, tapered.Height));
        Image pinned = img.Perspective([(30, 10), (150, 40), (120, 130), (10, 100)]);
        Assert.Equal((140, 120), (pinned.Width, pinned.Height));

        Image min = img.Resize(width: 300, height: 300, fit: Fit.Min);
        Assert.Equal((450, 300), (min.Width, min.Height));
        Image boxed = img.Resize(width: 800, height: 800, fit: Fit.BoxPad);
        Assert.Equal((800, 800), (boxed.Width, boxed.Height));
        Image manual = img.ResizeInto(200, 200, new ImageRect(10, 20, 100, 50));
        Assert.Equal((200, 200), (manual.Width, manual.Height));
        Assert.Equal(300, img.Resize(width: 300, filter: ResamplingFilter.Robidoux).Width);
    }

    [Fact]
    public void FormatsAndQuality()
    {
        Image img = Image.Load(MakePhoto(exif: false));

        img.Save(P("q.jpg"), new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 85, Subsampling = JpegSubsampling.Yuv444 } });
        img.Save(P("q.webp"), new SaveOptions { EncoderOptions = new WebPEncoderOptions { Quality = 80 } });
        img.Save(P("lossless.webp"), new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = true } });
        img.Save(P("q.png"), new SaveOptions { EncoderOptions = new PngEncoderOptions { CompressionLevel = 9 } });
        img.Save(P("q.tif"), new SaveOptions { EncoderOptions = new TiffEncoderOptions { Compression = TiffCompression.Deflate } });

        // A stream or buffer writer needs the format spelled out.
        using var ms = new MemoryStream();
        img.Save(ms, PngFormat.Instance);
        Assert.Equal("png", Image.Identify(ms.ToArray()).Format.Name);

        var writer = new ArrayBufferWriter<byte>();
        img.Save(writer, JpegFormat.Instance);
        Assert.True(writer.WrittenCount > 0);
    }

    [Fact]
    public void TransparentImageToJpeg()
    {
        var px = new Rgba32[64 * 64];
        Array.Fill(px, new Rgba32(255, 0, 0, 0)); // fully transparent red
        Image logo = Image.FromPixels<Rgba32>(px, 64, 64);

        // JPEG has no alpha. Flatten onto the color you want to see behind it, otherwise the stored color shows.
        logo.Flatten(new Rgba32(255, 255, 255, 255)).Save(P("flat.jpg"));
        Rgb24 corner = Image.Load(P("flat.jpg")).ToArray<Rgb24>()[0];
        Assert.True(corner.R > 240 && corner.G > 240 && corner.B > 240);
    }

    [Fact]
    public void GuideSnippets()
    {
        string photo = MakePhoto(exif: false);
        Image img = Image.Load(photo);

        // Catch everything the library itself throws.
        bool caught = false;
        try
        {
            Image.Load(new byte[] { 1, 2, 3 }).Resize(width: 800).Save(P("never.jpg"));
        }
        catch (ImageException ex)
        {
            caught = ex.Message.Length > 0;
        }

        Assert.True(caught);

        // Probing.
        byte[] bytes = File.ReadAllBytes(photo);
        if (Image.TryIdentify(bytes, out ImageInfo? info))
        {
            Assert.Equal("jpeg", info.Format.Name);
            Assert.Single(info.Frames);
        }
        else
        {
            Assert.Fail("not identified");
        }

        // The three source kinds.
        Image fromPath = Image.Load(photo);
        Image fromStream = Image.Load(File.OpenRead(photo));
        Image fromMemory = Image.Load(File.ReadAllBytes(photo));
        Assert.Equal(fromPath.Width, fromStream.Width + 0 * fromMemory.Width);

        // Geometry.
        Assert.Equal((620, 420), (img.Pad(left: 10, top: 10, right: 10, bottom: 10, mode: EdgeMode.Mirror).Width, img.Pad(left: 10, top: 10, right: 10, bottom: 10).Height));
        var bordered = new Rgba32[40 * 40];
        Array.Fill(bordered, new Rgba32(255, 255, 255, 255));
        for (int y = 10; y < 30; y++)
        {
            for (int x = 10; x < 30; x++)
            {
                bordered[(y * 40) + x] = new Rgba32(0, 0, 0, 255);
            }
        }

        Image trimmed = Image.FromPixels<Rgba32>(bordered, 40, 40).Trim();
        Assert.Equal((20, 20), (trimmed.Width, trimmed.Height));
        Assert.Equal(400, img.Orient(6).Width);
        Assert.Equal(600, img.AutoOrient().Width);

        // Thumbnail from a stream.
        using (FileStream fs = File.OpenRead(photo))
        {
            Assert.Equal(100, Image.Thumbnail(fs, 100).Width);
        }

        // Color: the built-in profile list.
        _ = new[] { ColorProfile.Srgb, ColorProfile.DisplayP3, ColorProfile.WideGamutRgb, ColorProfile.ProPhoto, ColorProfile.Rec2020, ColorProfile.Gray, ColorProfile.LabD50, ColorProfile.GenericCmyk };
    }

    [Fact]
    public void NewCanvas()
    {
        using var canvas = new Canvas(200, 100, PixelFormat.Rgba8);
        Assert.Equal(default, canvas[5, 5]);
        canvas[5, 5] = new Rgba32(1, 2, 3, 255);
        Assert.Equal(200, canvas.Snapshot().Width);
    }

    [Fact]
    public async Task AsyncApi()
    {
        string photo = MakePhoto();
        using var cts = new CancellationTokenSource();
        Image img = await Image.LoadAsync(photo, cancellationToken: cts.Token);
        await img.Resize(width: 100).SaveAsync(P("a.png"), cancellationToken: cts.Token);
        ImageInfo info = await Image.IdentifyAsync(P("a.png"));
        Assert.Equal(100, info.Width);
    }

    [Fact]
    public void LazyErrorsAtSave()
    {
        string png = P("good.png");
        Image.Load(MakePhoto(exif: false)).Save(png);

        // Damage the compressed pixel data but leave the header alone.
        byte[] bytes = File.ReadAllBytes(png);
        for (int i = bytes.Length / 2; i < (bytes.Length / 2) + 64; i++)
        {
            bytes[i] ^= 0x5A;
        }

        string bad = P("bad.png");
        File.WriteAllBytes(bad, bytes);

        // Loading only reads the header, so it succeeds...
        Image lazy = Image.Load(bad);
        Assert.Equal(600, lazy.Width);

        // ...and the damage shows when pixels are first pulled.
        Assert.ThrowsAny<ImageFormatException>(() => lazy.Resize(width: 100).Save(P("out.png")));
        Assert.False(File.Exists(P("out.png"))); // a failed Save removes its partial file

        // Validate: true decodes everything at load, so a damaged file fails there.
        Assert.ThrowsAny<ImageFormatException>(() => Image.Load(bad, new LoadOptions { Validate = true }));

        // TryLoad / TryIdentify turn format and limit errors into false.
        Assert.False(Image.TryLoad(bad, out _, new LoadOptions { Validate = true }));
        Assert.False(Image.TryIdentify(new byte[] { 1, 2, 3 }, out ImageInfo? none));
        Assert.Null(none);
        Assert.True(Image.TryIdentify(png, out ImageInfo? info));
        Assert.Equal((600, 400), (info!.Width, info.Height));
    }

    [Fact]
    public void ExceptionKinds()
    {
        // Not an image: ImageFormatException.
        Assert.Throws<ImageFormatException>(() => Image.Load(new byte[] { 1, 2, 3, 4 }));

        // Over a limit: ImageLimitException, raised from the header before anything is allocated.
        string photo = MakePhoto(exif: false);
        var tight = new LoadOptions { Limits = new ImageLimits { MaxPixelsPerFrame = 10_000 } };
        Assert.Throws<ImageLimitException>(() => Image.Load(photo, tight));

        // A missing file is an ordinary I/O exception.
        Assert.Throws<FileNotFoundException>(() => Image.Load(P("missing.jpg")));

        // A format without an encoder, or an extension nobody knows: ImageOperationException.
        Image img = Image.Load(photo);
        Assert.ThrowsAny<ImageOperationException>(() => img.Save(P("x.unknown")));

        // All three derive from ImageException.
        Assert.IsAssignableFrom<ImageException>(new ImageLimitException("x"));
    }

    [Fact]
    public void SourceLifetime()
    {
        string path = MakePhoto("src.jpg", exif: false);

        // Path source: reopened at Save. Changing the file in between is detected.
        Image fromPath = Image.Load(path);
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..^10]);
        Assert.Throws<ImageSourceChangedException>(() => fromPath.Save(P("o.png")));
        Assert.IsAssignableFrom<ImageOperationException>(new ImageSourceChangedException());

        // Stream source: the bytes are copied at Load, so the stream can be closed and the file deleted.
        string path2 = MakePhoto("src2.jpg", exif: false);
        Image fromStream;
        using (FileStream fs = File.OpenRead(path2))
        {
            fromStream = Image.Load(fs);
        }

        File.Delete(path2);
        fromStream.Save(P("o2.png"));

        // Memory source: referenced, not copied.
        ReadOnlyMemory<byte> data = File.ReadAllBytes(P("o2.png"));
        Image fromMemory = Image.Load(data);
        Assert.Equal(600, fromMemory.Width);
    }

    [Fact]
    public void MetadataBehavior()
    {
        string photo = MakePhoto(); // EXIF orientation 6, make "Acme"

        // Pixels come out upright; the recorded orientation is reset to 1.
        Image upright = Image.Load(photo);
        Assert.Equal((400, 600), (upright.Width, upright.Height));
        Assert.Equal(1, upright.Metadata.Orientation);

        // Opt out to keep the stored layout and the tag.
        Image stored = Image.Load(photo, new LoadOptions { AutoOrient = false });
        Assert.Equal((600, 400), (stored.Width, stored.Height));
        Assert.Equal(6, stored.Metadata.Orientation);

        // Typed EXIF reads.
        ExifData? exif = stored.Metadata.ReadExif();
        Assert.Equal("Acme", exif!.Make);
        Assert.Equal(6, exif.Orientation);

        // Save keeps EXIF (corrected to the saved size and orientation).
        upright.Save(P("kept.jpg"));
        Assert.False(Image.Identify(P("kept.jpg")).Metadata.Exif.IsEmpty);
        Assert.Equal(1, Image.Identify(P("kept.jpg")).Metadata.ReadExif()!.Orientation);

        // StripMetadata drops EXIF/XMP/IPTC (and ICC unless asked to keep it).
        upright.StripMetadata().Save(P("stripped.jpg"));
        Assert.True(Image.Identify(P("stripped.jpg")).Metadata.Exif.IsEmpty);

        // Thumbnail strips by default; keepMetadata: true carries EXIF over.
        Assert.True(Image.Thumbnail(photo, 100).Metadata.Exif.IsEmpty);
        Assert.False(Image.Thumbnail(photo, 100, keepMetadata: true).Metadata.Exif.IsEmpty);
    }

    [Fact]
    public void Color()
    {
        Image img = Image.Load(MakePhoto(exif: false));

        // Label untagged pixels, then convert to another space (relative colorimetric + black point compensation by default).
        Image p3 = img.AssignProfile(ColorProfile.DisplayP3);
        Image srgb = p3.ConvertColor(ColorProfile.Srgb);
        Assert.False(srgb.Metadata.IccProfile.IsEmpty);

        Image perceptual = p3.ConvertColor(ColorProfile.Srgb, RenderingIntent.Perceptual, blackPointCompensation: false);
        Assert.Equal(600, perceptual.Width);

        // A profile from bytes.
        ColorProfile fromBytes = ColorProfile.FromIcc(ColorProfile.Srgb.IccBytes.Span);
        Assert.Equal(ColorModel.Rgb, fromBytes.Model);
    }

    [Fact]
    public void PixelAccess()
    {
        Image img = Image.Load(MakePhoto(exif: false));

        // Whole frame, or a rectangle, converted to the pixel type you ask for.
        Rgba32[] all = img.ToArray<Rgba32>();
        Assert.Equal(600 * 400, all.Length);

        var block = new Gray8[16 * 16];
        img.CopyPixelsTo(block, new ImageRect(100, 100, 16, 16));

        // Canvas: mutable, tiled.
        using Canvas canvas = img.ToCanvas();
        canvas[0, 0] = new Rgba32(255, 0, 0, 255);
        // Lock<T> needs the canvas's exact format: a JPEG decodes to Rgb8, so T is Rgb24 (Rgba32 would throw).
        Assert.Equal(PixelFormat.Rgb8, canvas.Format);
        using (PixelLock<Rgb24> l = canvas.Lock<Rgb24>(new ImageRect(10, 10, 50, 50), LockAccess.ReadWrite))
        {
            for (int y = 0; y < l.Height; y++)
            {
                l.GetRow(y).Fill(new Rgb24(0, 0, 0));
            }
        }

        // Snapshot is O(1) and immutable: later edits don't change it.
        Image before = canvas.Snapshot();
        canvas[0, 0] = new Rgba32(0, 255, 0, 255);
        Assert.Equal(new Rgba32(255, 0, 0, 255), before.ToArray<Rgba32>()[0]);
        Assert.Equal(new Rgba32(0, 255, 0, 255), canvas[0, 0]);

        // From memory: Rgba32 here, any pixel struct works.
        var pixels = new Rgba32[4 * 4];
        Array.Fill(pixels, new Rgba32(10, 20, 30, 255));
        Image tiny = Image.FromPixels<Rgba32>(pixels, 4, 4);
        Assert.Equal(PixelFormat.Rgba8, tiny.Format);
    }

    [Fact]
    public void WatermarkComposite()
    {
        Image photo = Image.Load(MakePhoto(exif: false));
        var markPixels = new Rgba32[80 * 30];
        Array.Fill(markPixels, new Rgba32(255, 255, 255, 200));
        Image mark = Image.FromPixels<Rgba32>(markPixels, 80, 30);

        Image result = photo.Composite(mark, x: photo.Width - 80 - 10, y: photo.Height - 30 - 10, opacity: 0.8f);
        result.Save(P("marked.jpg"));
        Assert.Equal(600, Image.Identify(P("marked.jpg")).Width);
    }

    [Fact]
    public void BlendModeAndPorterDuff()
    {
        Image photo = Image.Load(MakePhoto(exif: false));
        var texturePixels = new Rgba32[80 * 30];
        Array.Fill(texturePixels, new Rgba32(200, 120, 60, 128));
        Image texture = Image.FromPixels<Rgba32>(texturePixels, 80, 30);

        Image shaded = photo.Composite(texture, opacity: 0.6f, blend: BlendMode.Multiply);
        Image cutout = photo.AddAlpha().Composite(texture, alpha: AlphaComposition.DestIn);
        Assert.Equal(photo.Width, shaded.Width);
        Rgba32[] px = cutout.ToArray<Rgba32>();
        Assert.InRange((int)px[(10 * photo.Width) + 10].A, 127, 129); // under the overlay: the base keeps the overlay's coverage
        Assert.Equal(255, px[(100 * photo.Width) + 100].A);            // elsewhere: untouched
    }

    [Fact]
    public void LimitsAndConfiguration()
    {
        string photo = MakePhoto(exif: false);

        // Per call.
        Image.Load(photo, new LoadOptions { Limits = new ImageLimits { MaxPixelsPerFrame = 1_000_000, MaxDimension = 4096 } });
        Image.Load(photo, new LoadOptions { Limits = ImageLimits.Unrestricted });

        // Global default, once at startup (restored here so other tests are unaffected).
        ImageLimits saved = ImageConfiguration.Default.Limits;
        try
        {
            ImageConfiguration.Default.Limits = new ImageLimits { MaxPixelsPerFrame = 50_000_000 };
        }
        finally
        {
            ImageConfiguration.Default.Limits = saved;
        }

        // A configuration with only the codecs you use: nothing else is linked into a trimmed app.
        var config = new ImageConfiguration().Add(JpegFormat.Instance).Add(PngFormat.Instance);
        var load = new LoadOptions { Configuration = config };
        Image img = Image.Load(photo, load);
        img.Save(P("c.png"), new SaveOptions { Configuration = config });
        img.Save(P("c.webp"));
        Assert.Throws<ImageFormatException>(() => Image.Load(P("c.webp"), load)); // WebP is not registered here
    }

    [Fact]
    public void ThreadingAndCache()
    {
        int degree = ImageEngine.DefaultCallDegree;
        long budget = ImageEngine.CacheBudgetBytes;
        try
        {
            ImageEngine.MaxDegree = Environment.ProcessorCount;
            ImageEngine.DefaultCallDegree = 2;
            ImageEngine.CacheBudgetBytes = 64L * 1024 * 1024;

            Image.Load(MakePhoto(exif: false)).Save(P("t.png"), new SaveOptions { MaxDegree = 1 });
        }
        finally
        {
            ImageEngine.DefaultCallDegree = degree;
            ImageEngine.CacheBudgetBytes = budget;
        }
    }

    // Five-frame animated GIF, 120x60, 80 ms per frame, looping twice.
    private string MakeAnimation(string name)
    {
        const int w = 120, h = 60, n = 5;
        var frames = new Rgba32[n][];
        for (int i = 0; i < n; i++)
        {
            frames[i] = new Rgba32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool inSquare = x >= i * 20 && x < (i * 20) + 20 && y >= 20 && y < 40;
                    frames[i][(y * w) + x] = inSquare ? new Rgba32(255, 0, 0, 255) : new Rgba32(0, 0, 255, 255);
                }
            }
        }

        string path = P(name);
        File.WriteAllBytes(path, Gif.GifTestUtil.Encode(Gif.GifTestUtil.Animation(w, h, frames, 80, 2)));
        return path;
    }

    [Fact]
    public void Animation()
    {
        string gif = MakeAnimation("anim.gif");

        Image anim = Image.Load(gif);
        Assert.Equal(5, anim.FrameCount);
        Assert.Equal(2, anim.LoopCount);
        Assert.Equal(TimeSpan.FromMilliseconds(80), anim.Frames[0].Delay);

        // Ops map over every frame, so the animation survives.
        anim.Resize(width: 60).Save(P("anim.webp"));
        Image back = Image.Load(P("anim.webp"));
        Assert.Equal(5, back.FrameCount);

        // One frame as its own still image; formats without animation write frame 0 only.
        Image second = anim.Frames[1].ToImage();
        Assert.Equal(1, second.FrameCount);
        anim.Save(P("first.jpg"));
        Assert.Single(Image.Identify(P("first.jpg")).Frames);
    }

    [Fact]
    public void GifAndQuantize()
    {
        Image photo = Image.Load(MakePhoto(exif: false));

        photo.Save(P("p.gif"), new SaveOptions { EncoderOptions = new GifEncoderOptions { MaxColors = 64, Dither = Dither.Ordered } });
        Assert.Equal("gif", Image.Identify(P("p.gif")).Format.Name);

        // Quantize is also an ordinary op: the result stays RGB(A) 8-bit.
        Image reduced = photo.Quantize(colors: 16, dither: Dither.FloydSteinberg);
        Assert.Equal(PixelFormat.Rgb8, reduced.Format);
        reduced.Save(P("p16.png"), new SaveOptions { EncoderOptions = new PngEncoderOptions { ColorMode = PngColorMode.Indexed } });
    }

    [Fact]
    public void Filters()
    {
        Image img = Image.Load(MakePhoto(exif: false));

        Assert.Equal(600, img.GaussianBlur(sigma: 2).Width);
        Assert.Equal(600, img.GaussianBlur(sigma: 4, sigmaY: 0).Width);
        Assert.Equal(600, img.BoxBlur(radius: 3).Width);
        Assert.Equal(600, img.Sharpen(amount: 1.5, radius: 1.2, threshold: 0.02, linear: false).Width);
        Assert.Equal(600, img.Median(radius: 1).Width);
        Image edges = img.Sobel();
        Assert.Equal(PixelFormat.Gray8, edges.Format);
        Image kernel = img.Convolve(new float[,] { { 0, -1, 0 }, { -1, 5, -1 }, { 0, -1, 0 } });
        Assert.Equal(400, kernel.Height);
        edges.Save(P("edges.png"));

        // A flat image has no edges and is unchanged by a blur.
        var flat = new Rgb24[32 * 32];
        Array.Fill(flat, new Rgb24(100, 150, 200));
        Image f = Image.FromPixels<Rgb24>(flat, 32, 32);
        Assert.All(f.GaussianBlur(3).ToArray<Rgb24>(), p => Assert.Equal(new Rgb24(100, 150, 200), p));
        Assert.All(f.Sobel().ToArray<Gray8>(), p => Assert.Equal(0, p.V));

        // Filters work on every frame.
        Image anim = Image.Load(MakeAnimation("f.gif"));
        Assert.Equal(5, anim.GaussianBlur(1).FrameCount);
    }

    [Fact]
    public void Analysis()
    {
        var px = new Rgba32[10 * 10];
        Array.Fill(px, new Rgba32(10, 20, 30, 255));
        px[0] = new Rgba32(250, 0, 0, 0); // one fully transparent pixel
        Image img = Image.FromPixels<Rgba32>(px, 10, 10);

        ImageHistogram hist = img.Histogram();
        Assert.Equal(4, hist.ChannelCount);
        Assert.Equal(99, hist[0, 10]);
        ReadOnlySpan<long> green = hist.Channel(1);
        Assert.Equal(99, green[20]);
        Assert.Equal(99, img.Histogram(skipTransparent: true)[0, 10]);
        Assert.Equal(99, img.Histogram(skipTransparent: true).PixelCount);

        ImageStatistics stats = img.Statistics();
        ChannelStatistics red = stats.Channels[0];
        Assert.Equal(250, red.Max);
        Assert.Equal(10, red.Min);
        Assert.Equal(100, red.Count);
        Assert.Equal(10, img.Statistics(skipTransparent: true).Channels[0].Mean);

        // Other frames through Frames[i].
        Image anim = Image.Load(MakeAnimation("a2.gif"));
        Assert.Equal(120 * 60, anim.Frames[2].Histogram().PixelCount);
    }

    [Fact]
    public void LockMismatchMessage()
    {
        using var canvas = new Canvas(10, 10, PixelFormat.Rgb8);
        var ex = Assert.Throws<ArgumentException>(() => canvas.Lock<Rgba32>(new ImageRect(0, 0, 4, 4), LockAccess.Read));
        Assert.Contains("Rgb24", ex.Message);
        Assert.Contains("CopyTo", ex.Message);
        Assert.Contains("ToArray", ex.Message);
    }
}
