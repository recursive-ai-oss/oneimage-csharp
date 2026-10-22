using System.Buffers;
using OneImage.Kernels;

namespace OneImage;

/// <summary>How a <see cref="PixelLock{T}"/> will be used.</summary>
public enum LockAccess
{
    /// <summary>The caller only reads. Only <see cref="PixelLock{T}.ReadRow"/> is available and nothing is copied or written back.</summary>
    Read = 0,

    /// <summary>The caller overwrites every pixel of the rectangle. Contents on entry are unspecified (zero when the lock spans several tiles).</summary>
    Write = 1,

    /// <summary>The caller reads and modifies.</summary>
    ReadWrite = 2,
}

/// <summary>
/// A mutable, tiled, sparse pixel surface. Tiles (128x128) are created on first write; unwritten pixels read as zero
/// (or as the source image's pixels for a canvas made by <see cref="Image.ToCanvas"/>). Tiles are owned by the canvas
/// and never evicted. <see cref="Snapshot"/> shares tiles with the returned <see cref="Image"/> and copies a tile only when the
/// canvas next writes it. A canvas is not thread-safe: use one writer, and hand snapshots to other threads.
/// Dispose returns pooled memory; forgetting to dispose is safe (the garbage collector reclaims the buffers).
/// </summary>
public sealed class Canvas : IDisposable
{
    private TileGrid _grid;
    private object _token = new();
    private bool _disposed;

    /// <summary>Creates an empty (all-zero) canvas.</summary>
    /// <param name="width">Width in pixels, positive.</param>
    /// <param name="height">Height in pixels, positive.</param>
    /// <param name="format">The storage format.</param>
    /// <param name="limits">Limits to check the size against; defaults to <see cref="ImageConfiguration.Default"/>'s.</param>
    /// <exception cref="ImageLimitException">The size exceeds the limits.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public Canvas(int width, int height, PixelFormat format, ImageLimits? limits = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        (limits ?? ImageConfiguration.Default.Limits).CheckFrame(width, height);
        _grid = new TileGrid(width, height, format, null);
    }

    internal Canvas(Node backing)
    {
        _grid = new TileGrid(backing.Width, backing.Height, backing.Format, backing);
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width => _grid.Width;

    /// <summary>Gets the height in pixels.</summary>
    public int Height => _grid.Height;

    /// <summary>Gets the storage format.</summary>
    public PixelFormat Format => _grid.Format;

    /// <summary>Gets the number of tiles currently materialized (a test and diagnostics aid).</summary>
    internal int AllocatedTiles => _grid.Tiles.Count(static t => t != null);

    /// <summary>Gets or sets one pixel as <see cref="Rgba32"/>, converting from/to the canvas format (straight alpha, see <see cref="CopyFrom{T}"/>).</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <returns>The pixel.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The coordinate is outside the canvas.</exception>
    public Rgba32 this[int x, int y]
    {
        get
        {
            Span<Rgba32> one = stackalloc Rgba32[1];
            CopyTo(one, new ImageRect(x, y, 1, 1));
            return one[0];
        }

        set => CopyFrom<Rgba32>([value], new ImageRect(x, y, 1, 1));
    }

    /// <summary>
    /// Returns an immutable <see cref="Image"/> of the current contents in O(1): no pixel data is copied. Later writes to the
    /// canvas copy each affected tile first, so the image never changes and may be read from any thread while the canvas is edited.
    /// </summary>
    /// <returns>The snapshot.</returns>
    /// <exception cref="ObjectDisposedException">The canvas was disposed.</exception>
    public Image Snapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _grid.Shared = true;
        _token = new object();
        return Image.FromNode(new GridSourceNode(_grid));
    }

    /// <summary>
    /// Locks a rectangle for typed row access. Inside one tile the rows are the tile's memory; across tiles the rows live in a
    /// staging buffer that is copied back on <see cref="PixelLock{T}.Dispose"/> for write access. <typeparamref name="T"/> must match the canvas format exactly.
    /// </summary>
    /// <typeparam name="T">The pixel struct.</typeparam>
    /// <param name="rect">The rectangle, inside the canvas.</param>
    /// <param name="access">The intended use.</param>
    /// <returns>The lock; dispose it to commit.</returns>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> is not the canvas format. The message names the canvas format, the matching pixel type, and the converting alternatives (<see cref="CopyTo{T}"/>, <see cref="Image.ToArray{T}"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is outside the canvas.</exception>
    public PixelLock<T> Lock<T>(ImageRect rect, LockAccess access)
        where T : unmanaged, IPixel<T>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckRect(rect);
        if (T.Format != Format)
        {
            throw new ArgumentException(
                $"This canvas is {Format}, so Lock<T> needs {MatchingPixelType(Format)}, not {typeof(T).Name} ({T.Format}). "
                + "Lock never converts; to read in another pixel type use CopyTo<T> on the canvas or Image.ToArray<T>() on a Snapshot().",
                nameof(T));
        }

        int bpp = Format.BytesPerPixel;
        bool singleTile = !rect.IsEmpty
            && (rect.X >> Tile.Shift) == ((rect.Right - 1) >> Tile.Shift)
            && (rect.Y >> Tile.Shift) == ((rect.Bottom - 1) >> Tile.Shift);
        if (singleTile)
        {
            int tx = rect.X >> Tile.Shift;
            int ty = rect.Y >> Tile.Shift;
            Tile? tile;
            if (access == LockAccess.Read)
            {
                tile = _grid.Tiles[(ty * _grid.TilesX) + tx];
            }
            else
            {
                bool whole = rect.Width == Tile.Size && rect.Height == Tile.Size;
                tile = WritableTile(tx, ty, access == LockAccess.Write && whole);
            }

            if (tile != null)
            {
                int offset = (((rect.Y & (Tile.Size - 1)) << Tile.Shift) + (rect.X & (Tile.Size - 1))) * bpp;
                return new PixelLock<T>(this, rect, access, tile, offset, null);
            }
        }

        int count = checked(rect.Width * rect.Height);
        T[] staging = ArrayPool<T>.Shared.Rent(Math.Max(count, 1));
        try
        {
            if (access == LockAccess.Write || rect.IsEmpty)
            {
                staging.AsSpan(0, count).Clear();
            }
            else
            {
                CopyTo(staging.AsSpan(0, count), rect);
            }
        }
        catch
        {
            ArrayPool<T>.Shared.Return(staging);
            throw;
        }

        return new PixelLock<T>(this, rect, access, null, 0, staging);
    }

    /// <summary>
    /// Writes pixels into a rectangle. When <typeparamref name="T"/> differs from the canvas format the values are converted
    /// (see the pixel-format conversion rules: round half up, clamped; RGB to gray uses Rec. 709 weights; alpha dropped without flattening).
    /// </summary>
    /// <typeparam name="T">The pixel struct.</typeparam>
    /// <param name="pixels">Row-major pixels, <c>rect.Width * rect.Height</c> of them, tightly packed.</param>
    /// <param name="rect">The destination rectangle, inside the canvas.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is outside the canvas.</exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> is too short.</exception>
    /// <exception cref="ImageOperationException">The formats cannot be converted.</exception>
    public void CopyFrom<T>(ReadOnlySpan<T> pixels, ImageRect rect)
        where T : unmanaged, IPixel<T>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckRect(rect);
        if ((long)rect.Width * rect.Height > pixels.Length)
        {
            throw new ArgumentException("Not enough pixels for the rectangle.", nameof(pixels));
        }

        WriteBytes(rect, SpanCast.AsBytes(pixels), T.Format);
    }

    /// <summary>Reads a rectangle, converting to <typeparamref name="T"/> when its format differs from the canvas format.</summary>
    /// <typeparam name="T">The pixel struct.</typeparam>
    /// <param name="destination">Receives <c>rect.Width * rect.Height</c> tightly packed pixels.</param>
    /// <param name="rect">The source rectangle, inside the canvas.</param>
    /// <param name="cancellationToken">Observed when the canvas is backed by a lazy image.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is outside the canvas.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public void CopyTo<T>(Span<T> destination, ImageRect rect, CancellationToken cancellationToken = default)
        where T : unmanaged, IPixel<T>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckRect(rect);
        if ((long)rect.Width * rect.Height > destination.Length)
        {
            throw new ArgumentException("Destination too short for the rectangle.", nameof(destination));
        }

        var source = new GridSourceNode(_grid);
        Render.ReadRegion(source, rect, SpanCast.AsBytes(destination), 0, T.Format, RenderContext.Create(1, cancellationToken));
    }

    /// <summary>Releases the pooled tiles this canvas owns exclusively. Tiles shared with a snapshot are left to the snapshot (and the garbage collector), so snapshots stay valid.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_grid.Shared)
        {
            // A snapshot reads this table and its tiles; leave both to the garbage collector.
            return;
        }

        Tile?[] tiles = _grid.Tiles;
        for (int i = 0; i < tiles.Length; i++)
        {
            Tile? t = tiles[i];
            if (t != null && ReferenceEquals(t.Owner, _token))
            {
                t.Release();
            }

            tiles[i] = null;
        }
    }

    internal void Commit<T>(ImageRect rect, ReadOnlySpan<T> pixels)
        where T : unmanaged, IPixel<T>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WriteBytes(rect, SpanCast.AsBytes(pixels), T.Format);
    }

    private void CheckRect(ImageRect rect)
    {
        if (!rect.IsInside(Width, Height))
        {
            throw new ArgumentOutOfRangeException(nameof(rect), "Rectangle lies outside the canvas.");
        }
    }

    /// <summary>Returns a tile this canvas may mutate: the existing one if owned, else a copy (or a fresh zeroed tile).</summary>
    private Tile WritableTile(int tx, int ty, bool willOverwriteAll)
    {
        if (_grid.Shared)
        {
            _grid = _grid.Clone();
        }

        int idx = (ty * _grid.TilesX) + tx;
        Tile? t = _grid.Tiles[idx];
        if (t != null && ReferenceEquals(t.Owner, _token))
        {
            return t;
        }

        Tile fresh;
        if (t != null)
        {
            fresh = willOverwriteAll ? Tile.Rent(t.Length, clear: false) : t.CopyForWrite(_token);
        }
        else if (_grid.Backing != null && !willOverwriteAll)
        {
            Tile src = _grid.Backing.GetTile(tx, ty, RenderContext.Create(1, CancellationToken.None));
            try
            {
                fresh = src.CopyForWrite(_token);
            }
            finally
            {
                src.Release();
            }
        }
        else
        {
            fresh = Tile.Rent(Tile.ByteSize(Format), clear: !willOverwriteAll);
        }

        fresh.Owner = _token;
        _grid.Tiles[idx] = fresh;
        return fresh;
    }

    private void WriteBytes(ImageRect rect, ReadOnlySpan<byte> source, PixelFormat sourceFormat)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        int dbpp = Format.BytesPerPixel;
        int sbpp = sourceFormat.BytesPerPixel;
        ConvertRowKernel? converter = sourceFormat != Format ? KernelRegistry.Get<ConvertRowKernel>("convert") : null;
        for (int ty = rect.Y >> Tile.Shift; ty <= (rect.Bottom - 1) >> Tile.Shift; ty++)
        {
            for (int tx = rect.X >> Tile.Shift; tx <= (rect.Right - 1) >> Tile.Shift; tx++)
            {
                ImageRect part = rect.Intersect(new ImageRect(tx << Tile.Shift, ty << Tile.Shift, Tile.Size, Tile.Size));
                bool whole = part.Width == Tile.Size && part.Height == Tile.Size;
                Tile tile = WritableTile(tx, ty, whole);
                Span<byte> dst = tile.Span;
                int tileX = part.X - (tx << Tile.Shift);
                int tileY = part.Y - (ty << Tile.Shift);
                for (int r = 0; r < part.Height; r++)
                {
                    ReadOnlySpan<byte> srow = source.Slice((((part.Y - rect.Y + r) * rect.Width) + (part.X - rect.X)) * sbpp, part.Width * sbpp);
                    Span<byte> drow = dst.Slice((((tileY + r) << Tile.Shift) + tileX) * dbpp, part.Width * dbpp);
                    if (converter == null)
                    {
                        srow.CopyTo(drow);
                    }
                    else
                    {
                        converter(srow, sourceFormat, drow, Format, part.Width);
                    }
                }
            }
        }
    }

    private static string MatchingPixelType(PixelFormat f)
    {
        if (f == PixelFormat.Gray8) return nameof(Gray8);
        if (f == PixelFormat.GrayAlpha8) return nameof(GrayAlpha8);
        if (f == PixelFormat.Gray16) return nameof(Gray16);
        if (f == PixelFormat.GrayF32) return nameof(GrayFloat);
        if (f == PixelFormat.Rgb8) return nameof(Rgb24);
        if (f == PixelFormat.Rgba8) return nameof(Rgba32);
        if (f == PixelFormat.Rgb16) return nameof(Rgb48);
        if (f == PixelFormat.Rgba16) return nameof(Rgba64);
        if (f == PixelFormat.RgbaF16) return nameof(RgbaHalf);
        if (f == PixelFormat.RgbF32) return nameof(RgbFloat);
        if (f == PixelFormat.RgbaF32) return nameof(RgbaFloat);
        if (f == PixelFormat.Cmyk8) return nameof(Cmyk32);
        if (f == PixelFormat.LabF32) return nameof(LabFloat);
        return "a pixel type of that format";
    }
}
