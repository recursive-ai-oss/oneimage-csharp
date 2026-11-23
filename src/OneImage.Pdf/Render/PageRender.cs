using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using OnePdf;

namespace OneImage.Pdf.Render;

/// <summary>
/// Everything the cells of one page share: the display list (interpreted once, on first use), and the pictures built from it that are
/// worth keeping (finished image pictures, shading tables, pattern tiles). All members are safe to use from several threads.
/// </summary>
internal sealed class PageRender
{
    private readonly object _gate = new();
    private readonly PdfPage _page;
    private readonly PdfRenderOptions _options;
    private readonly object _slotGate = new();
    private readonly Dictionary<ImageKey, ImageSlot> _slots = new();
    private readonly ConcurrentDictionary<PdfShading, Lazy<ShadingLut>> _luts = new();
    private readonly ConcurrentDictionary<TileKey, Lazy<PatternTile?>> _tiles = new();
    private PdfDisplayList? _list;
    private PdfPageInfo _info;
    private ExceptionDispatchInfo? _failure;
    private long _cachedPixels;
    private readonly object _cacheGate = new();
    private readonly LinkedList<CacheEntry> _lru = new();

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
                    GenerateAppearances = _options.GenerateAppearances,
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

    /// <summary>
    /// The picture of an image at a device size, or null when the image cannot be shown. It is reduced by area averaging when the page
    /// shows it much smaller than it is, otherwise as decoded.
    /// <para>
    /// The decoded samples are never kept: they exist only while the picture is built (OnePDF's own per-occurrence cache is bypassed,
    /// because a display list that holds every occurrence of an image holds every decoded copy of it). What is kept, within the
    /// <see cref="PdfRenderLimits.MaxCachedPixels"/> budget, is the finished picture, once per image and size, so every cell of the page
    /// and every draw of the same image share it. A picture is never larger than half of that budget (at least 1 Mpx): a larger one is
    /// reduced to that size, so it is shown a little softer but is built once and shared by every cell, instead of being decoded again
    /// by each cell that touches it. When the pictures of a page together exceed the budget, the one used least recently is dropped
    /// to make room. The size depends only on the image and the device size, so a region reads the same pixels as the whole page.
    /// An image is identified by its object for an XObject and by its bytes and dictionary for
    /// an inline image, so a stream that draws the same inline image again and again decodes it once. An image whose decoding failed is
    /// remembered as failed, so each cell does not decode it up to the limit again.
    /// </para>
    /// </summary>
    public Raster? GetPicture(in PdfImage image, int deviceWidth, int deviceHeight, ReadBudget budget, out bool interpolate, CancellationToken ct)
    {
        interpolate = false;
        if (!ImageWithinLimit(image, out long pixels))
        {
            return null;
        }

        ImageSlot? slot = SlotFor(image);
        if (slot is null)
        {
            // Not shareable (it names a color space of its resources) or too many images are tracked: build for this draw only.
            return Produce(null, image, pixels, deviceWidth, deviceHeight, budget, out interpolate, ct);
        }

        lock (slot)
        {
            if (slot.Known)
            {
                interpolate = slot.Interpolate;
                if (!slot.Drawable)
                {
                    return null;
                }

                (int rw, int rh) = PictureSize(slot.Width, slot.Height, deviceWidth, deviceHeight);
                if (TryGetCached(slot, (rw, rh), out Raster? hit))
                {
                    return hit;
                }
            }

            return Produce(slot, image, pixels, deviceWidth, deviceHeight, budget, out interpolate, ct);
        }
    }

    private static readonly PdfImageOptions s_decodeOptions = new();
    private static long s_decodeCount;

    /// <summary>How many image decodes the process has started (read by tests that check a picture is decoded once, not once per cell).</summary>
    internal static long DecodeCount => Interlocked.Read(ref s_decodeCount);

    private Raster? Produce(ImageSlot? slot, in PdfImage image, long pixels, int deviceWidth, int deviceHeight, ReadBudget budget, out bool interpolate, CancellationToken ct)
    {
        interpolate = false;
        PdfImageData? data = Decode(image, pixels, budget, ct);
        if (data is null || !data.IsDecoded)
        {
            if (slot is not null)
            {
                slot.Known = true;
                slot.Drawable = false;
            }

            return null;
        }

        interpolate = data.Interpolate;
        int tw = data.IsStencil && data.Alpha is { } sa ? sa.Width : data.Width;
        int th = data.IsStencil && data.Alpha is { } sb ? sb.Height : data.Height;
        (int rw, int rh) = PictureSize(tw, th, deviceWidth, deviceHeight);
        if (slot is not null)
        {
            slot.Known = true;
            slot.Drawable = true;
            slot.Width = tw;
            slot.Height = th;
            slot.Interpolate = interpolate;
        }

        Raster? raster = null;
        if (tw == data.Width && th == data.Height && (rw != tw || rh != th))
        {
            raster = Raster.FromDataReduced(data, rw, rh, ct);
        }

        raster ??= Raster.FromData(data, ct);
        if (raster is not null)
        {
            // A soft mask larger than the color can make the picture larger than predicted: bring it to its own target size.
            (int w, int h) = PictureSize(raster.Width, raster.Height, deviceWidth, deviceHeight);
            if (w != raster.Width || h != raster.Height)
            {
                raster = new Raster(w, h, Resize.Area(raster.Pixels, raster.Width, raster.Height, w, h, raster.Stencil, ct), raster.Stencil);
            }
        }

        if (slot is not null)
        {
            Keep(slot, (rw, rh), raster);
        }

        return raster;
    }

    private bool TryGetCached(ImageSlot slot, (int, int) size, out Raster? raster)
    {
        lock (_cacheGate)
        {
            if (slot.Rasters.TryGetValue(size, out LinkedListNode<CacheEntry>? node))
            {
                _lru.Remove(node);
                _lru.AddLast(node);
                raster = node.Value.Raster;
                return true;
            }
        }

        raster = null;
        return false;
    }

    /// <summary>
    /// Keeps a finished picture for the next cell that needs it, dropping the least recently used pictures of the page to make room.
    /// A picture that cannot fit even then (the pattern tiles hold the budget) is not kept and is built again when needed.
    /// </summary>
    private void Keep(ImageSlot slot, (int Width, int Height) size, Raster? raster)
    {
        long cost = (long)size.Width * size.Height;
        long budget = _options.Limits.MaxCachedPixels;
        lock (_cacheGate)
        {
            while (_lru.First is { } oldest && Interlocked.Read(ref _cachedPixels) + cost > budget)
            {
                _lru.RemoveFirst();
                oldest.Value.Slot.Rasters.Remove(oldest.Value.Size);
                Interlocked.Add(ref _cachedPixels, -oldest.Value.Cost);
            }

            if (Interlocked.Read(ref _cachedPixels) + cost > budget || slot.Rasters.ContainsKey(size))
            {
                return;
            }

            slot.Rasters[size] = _lru.AddLast(new CacheEntry(slot, size, cost, raster));
            Interlocked.Add(ref _cachedPixels, cost);
        }
    }

    private sealed record CacheEntry(ImageSlot Slot, (int Width, int Height) Size, long Cost, Raster? Raster);

    /// <summary>
    /// The size a picture is built at: as decoded, or reduced to the device size when the page shows it much smaller, and never more
    /// than half the cached-picture budget (at least 1 Mpx) so that it can be built once and kept.
    /// </summary>
    private (int Width, int Height) PictureSize(int width, int height, int deviceWidth, int deviceHeight)
    {
        (int w, int h) = ReducedSize(width, height, deviceWidth, deviceHeight);
        long cap = Math.Max(_options.Limits.MaxCachedPixels / 2, 1L << 20);
        long pixels = (long)w * h;
        if (pixels > cap)
        {
            double scale = Math.Sqrt((double)cap / pixels);
            w = Math.Max(1, (int)(w * scale));
            h = Math.Max(1, (int)(h * scale));
            while ((long)w * h > cap)
            {
                // The square root is inexact at the last digit: step down until the product fits.
                if (w >= h)
                {
                    w--;
                }
                else
                {
                    h--;
                }
            }
        }

        return (w, h);
    }

    private static (int Width, int Height) ReducedSize(int width, int height, int deviceWidth, int deviceHeight) =>
        (width > deviceWidth * 1.25 ? Math.Max(1, deviceWidth) : width, height > deviceHeight * 1.25 ? Math.Max(1, deviceHeight) : height);

    /// <summary>
    /// Decodes an image through the registered decoders, without OnePDF's per-occurrence cache. Counts the pixels against the read's
    /// decoded-image budget first, and refuses once it is spent. Returns null for an image beyond the document's limits (it is left out
    /// and the rest of the page is drawn).
    /// </summary>
    private PdfImageData? Decode(in PdfImage image, long pixels, ReadBudget budget, CancellationToken ct)
    {
        long total = Interlocked.Add(ref budget.DecodedPixels, pixels);
        if (total > _options.Limits.MaxDecodedImagePixels)
        {
            throw new ImageLimitException(
                $"Drawing this page decodes more than {_options.Limits.MaxDecodedImagePixels} image pixels in one read. "
                + "Raise PdfRenderLimits.MaxDecodedImagePixels to allow a page with this many or this large images.");
        }

        Interlocked.Increment(ref s_decodeCount);

        try
        {
            return image.GetData(s_decodeOptions, ct);
        }
        catch (PdfLimitException)
        {
            // An image larger than the document limits is left out; the rest of the page is drawn.
            return null;
        }
    }

    private bool ImageWithinLimit(in PdfImage image, out long pixels)
    {
        PdfDictionary d = image.Dictionary;
        long w = d.GetInt("Width", d.GetInt("W", 0));
        long h = d.GetInt("Height", d.GetInt("H", 0));
        pixels = w > 0 && h > 0 ? w * h : 0;
        return w > 0 && h > 0 && w * h <= _options.Limits.MaxImagePixels;
    }

    /// <summary>The slot that remembers one image, or null when the image cannot be identified safely or the page tracks as many images as it may.</summary>
    private ImageSlot? SlotFor(in PdfImage image)
    {
        ImageKey? key = ImageKey.Create(image);
        if (key is null)
        {
            return null;
        }

        lock (_slotGate)
        {
            if (_slots.TryGetValue(key.Value, out ImageSlot? slot))
            {
                return slot;
            }

            if (_slots.Count >= MaxTrackedImages)
            {
                return null;
            }

            slot = new ImageSlot();
            _slots.Add(key.Value, slot);
            return slot;
        }
    }

    /// <summary>Most distinct images one page remembers; more are built for the draw that needs them. Keeps a hostile page of countless distinct inline images from growing the table.</summary>
    private const int MaxTrackedImages = 4096;

    private sealed class ImageSlot
    {
        public bool Known;
        public bool Drawable;
        public int Width;
        public int Height;
        public bool Interpolate;
        public readonly Dictionary<(int, int), LinkedListNode<CacheEntry>> Rasters = new();
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

        public override void FillStrokePath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => list.FillStrokePath(in path, evenOdd, in state);

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

/// <summary>The accounting of one read of pixels (a whole render or a region): how many image pixels it has decoded so far. A read owns its own, so reads running side by side do not see each other's count.</summary>
internal sealed class ReadBudget
{
    public long DecodedPixels;
}
