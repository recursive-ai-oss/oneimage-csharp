using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using OnePdf;

namespace OneImage.Pdf.Render;

/// <summary>
/// Everything the cells of one page share: the display list (interpreted once, on first use), and the pictures built from it that are
/// worth keeping (decoded and reduced images, shading tables, pattern tiles). All members are safe to use from several threads.
/// </summary>
internal sealed class PageRender
{
    private readonly object _gate = new();
    private readonly PdfPage _page;
    private readonly PdfRenderOptions _options;
    private readonly ConcurrentDictionary<RasterKey, Lazy<Raster?>> _rasters = new();
    private readonly ConcurrentDictionary<PdfShading, Lazy<ShadingLut>> _luts = new();
    private readonly ConcurrentDictionary<TileKey, Lazy<PatternTile?>> _tiles = new();
    private PdfDisplayList? _list;
    private PdfPageInfo _info;
    private ExceptionDispatchInfo? _failure;
    private long _cachedPixels;

    public PageRender(PdfPage page, PdfRenderOptions options)
    {
        _page = page;
        _options = options;
    }

    public PdfRenderLimits Limits => _options.Limits;

    public PdfRenderOptions Options => _options;

    public PdfDocument Document => _page.Document;

    /// <summary>The page size in points as the interpretation reports it (box, rotation and user unit applied).</summary>
    public PdfPageInfo Info
    {
        get
        {
            Prepare(CancellationToken.None);
            return _info;
        }
    }

    public PdfDisplayList List
    {
        get
        {
            Prepare(CancellationToken.None);
            return _list!;
        }
    }

    /// <summary>Interprets the page once. A failure is kept and thrown again, so a hostile page costs its work once, not once per cell.</summary>
    public void Prepare(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_list is not null)
            {
                return;
            }

            _failure?.Throw();
            try
            {
                var list = new PdfDisplayList();
                var o = new PdfInterpretOptions
                {
                    Box = _options.Box switch
                    {
                        PdfPageBox.MediaBox => OnePdf.PdfPageBox.MediaBox,
                        PdfPageBox.BleedBox => OnePdf.PdfPageBox.BleedBox,
                        PdfPageBox.TrimBox => OnePdf.PdfPageBox.TrimBox,
                        PdfPageBox.ArtBox => OnePdf.PdfPageBox.ArtBox,
                        _ => OnePdf.PdfPageBox.CropBox,
                    },
                    Annotations = _options.Annotations switch
                    {
                        PdfAnnotationRendering.Print => PdfAnnotationMode.Print,
                        PdfAnnotationRendering.None => PdfAnnotationMode.None,
                        _ => PdfAnnotationMode.View,
                    },
                    Scale = 1,
                };
                var probe = new InfoProbe(list);
                _page.Interpret(probe, o, ct);
                _info = probe.Info;
                _list = list;
            }
            catch (PdfException ex)
            {
                Exception mapped = PdfErrors.Map(ex);
                _failure = ExceptionDispatchInfo.Capture(mapped);
                throw mapped;
            }
        }
    }

    /// <summary>The finished image data (decoded through the registered image decoders), or null when the image cannot be shown.</summary>
    public PdfImageData? GetImageData(in PdfImage image, CancellationToken ct)
    {
        if (!ImageWithinLimit(image))
        {
            return null;
        }

        try
        {
            return image.GetData(null, ct);
        }
        catch (PdfLimitException)
        {
            // An image larger than the document limits is left out; the rest of the page is drawn.
            return null;
        }
    }

    private bool ImageWithinLimit(in PdfImage image)
    {
        PdfDictionary d = image.Dictionary;
        long w = d.GetInt("Width", d.GetInt("W", 0));
        long h = d.GetInt("Height", d.GetInt("H", 0));
        return w > 0 && h > 0 && w * h <= _options.Limits.MaxImagePixels;
    }

    /// <summary>
    /// The picture of an image at a device size: reduced by area averaging when the page shows it much smaller than it is, otherwise as
    /// decoded. Built once per size and shared by every cell (the full-size conversion is dropped after the reduction).
    /// </summary>
    public Raster? GetRaster(PdfImageData data, int deviceWidth, int deviceHeight, CancellationToken ct)
    {
        int tw = data.IsStencil && data.Alpha is { } sa ? sa.Width : data.Width;
        int th = data.IsStencil && data.Alpha is { } sb ? sb.Height : data.Height;
        int rw = tw > deviceWidth * 1.25 ? Math.Max(1, deviceWidth) : tw;
        int rh = th > deviceHeight * 1.25 ? Math.Max(1, deviceHeight) : th;
        var key = new RasterKey(data, rw, rh);
        if (_rasters.TryGetValue(key, out Lazy<Raster?>? hit))
        {
            return hit.Value;
        }

        Raster? Build()
        {
            Raster? full = Raster.FromData(data, ct);
            if (full is null)
            {
                return null;
            }

            int w = full.Width > deviceWidth * 1.25 ? Math.Max(1, deviceWidth) : full.Width;
            int h = full.Height > deviceHeight * 1.25 ? Math.Max(1, deviceHeight) : full.Height;
            if (w == full.Width && h == full.Height)
            {
                return full;
            }

            return new Raster(w, h, Resize.Area(full.Pixels, full.Width, full.Height, w, h, full.Stencil, ct), full.Stencil);
        }

        long cost = (long)rw * rh;
        if (Interlocked.Read(ref _cachedPixels) + cost > _options.Limits.MaxCachedPixels)
        {
            return Build();
        }

        var lazy = new Lazy<Raster?>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<Raster?> winner = _rasters.GetOrAdd(key, lazy);
        if (ReferenceEquals(winner, lazy))
        {
            Interlocked.Add(ref _cachedPixels, cost);
        }

        return winner.Value;
    }

    public ShadingLut GetLut(PdfShading shading) =>
        _luts.GetOrAdd(shading, static s => new Lazy<ShadingLut>(() => new ShadingLut(s, s.Domain.Count >= 2 ? s.Domain[0] : 0, s.Domain.Count >= 2 ? s.Domain[1] : 1))).Value;

    public PatternTile? GetTile(in TileKey key, Func<PatternTile?> build)
    {
        if (_tiles.TryGetValue(key, out Lazy<PatternTile?>? hit))
        {
            return hit.Value;
        }

        // A cell's picture can need a large tile; once the budget is spent further tiles are built per use instead of kept.
        if (Interlocked.Read(ref _cachedPixels) > _options.Limits.MaxCachedPixels)
        {
            return build();
        }

        var lazy = new Lazy<PatternTile?>(build, LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<PatternTile?> winner = _tiles.GetOrAdd(key, lazy);
        PatternTile? tile = winner.Value;
        if (ReferenceEquals(winner, lazy) && tile is not null)
        {
            Interlocked.Add(ref _cachedPixels, tile.PixelCount);
        }

        return tile;
    }

    internal readonly record struct RasterKey(PdfImageData Data, int Width, int Height);

    /// <summary>Records the page info while forwarding every call to the display list.</summary>
    private sealed class InfoProbe(PdfDisplayList list) : PdfDevice
    {
        public PdfPageInfo Info { get; private set; }

        public override void BeginPage(in PdfPageInfo page)
        {
            Info = page;
            list.BeginPage(in page);
        }

        public override void EndPage() => list.EndPage();

        public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => list.FillPath(in path, evenOdd, in state);

        public override void StrokePath(in PdfPath path, in PdfGraphicsState state) => list.StrokePath(in path, in state);

        public override void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => list.PushClip(in path, evenOdd, in state);

        public override void PushTextClip(in PdfGraphicsState state) => list.PushTextClip(in state);

        public override void PopClip() => list.PopClip();

        public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state) => list.ShowGlyphRun(in run, in state);

        public override void BeginType3Glyph(in PdfType3GlyphInfo glyph, in PdfGraphicsState state) => list.BeginType3Glyph(in glyph, in state);

        public override void EndType3Glyph() => list.EndType3Glyph();

        public override void DrawImage(in PdfImage image, in PdfGraphicsState state) => list.DrawImage(in image, in state);

        public override void PaintShading(PdfShading shading, in PdfGraphicsState state) => list.PaintShading(shading, in state);

        public override void BeginGroup(in PdfGroupInfo group, in PdfGraphicsState state) => list.BeginGroup(in group, in state);

        public override void EndGroup() => list.EndGroup();

        public override void BeginSoftMask(in PdfSoftMaskInfo mask, in PdfGraphicsState state) => list.BeginSoftMask(in mask, in state);

        public override void EndSoftMask() => list.EndSoftMask();

        public override void BeginMarkedContent(string tag, PdfDictionary? properties) => list.BeginMarkedContent(tag, properties);

        public override void EndMarkedContent() => list.EndMarkedContent();

        public override void BeginAnnotation(in PdfAnnotationInfo annotation) => list.BeginAnnotation(in annotation);

        public override void EndAnnotation() => list.EndAnnotation();
    }
}

/// <summary>Maps OnePDF failures to the typed OneImage exceptions.</summary>
internal static class PdfErrors
{
    public static Exception Map(PdfException ex) => ex switch
    {
        PdfLimitException => new ImageLimitException(ex.Message, ex),
        _ => new ImageFormatException("The PDF could not be read: " + ex.Message, ex),
    };
}
