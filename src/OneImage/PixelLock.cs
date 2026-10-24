using System.Buffers;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Typed row access to a rectangle of a <see cref="Canvas"/>. Rows are indexed relative to the rectangle.
/// Dispose to commit: for <see cref="LockAccess.Write"/> and <see cref="LockAccess.ReadWrite"/> locks that used a staging buffer
/// (rectangles spanning several tiles) the rows are copied back on dispose; locks inside one tile edit the tile directly.
/// Do not touch the canvas other than through the lock while it is open, and do not keep spans after disposing.
/// </summary>
/// <typeparam name="T">The pixel struct, equal to the canvas format.</typeparam>
public sealed class PixelLock<T> : IDisposable
    where T : unmanaged, IPixel<T>
{
    private readonly Canvas _canvas;
    private readonly LockAccess _access;
    private readonly Tile? _tile;
    private readonly int _tileOffsetBytes;
    private T[]? _staging;

    internal PixelLock(Canvas canvas, ImageRect rect, LockAccess access, Tile? tile, int tileOffsetBytes, T[]? staging)
    {
        _canvas = canvas;
        Rect = rect;
        _access = access;
        _tile = tile;
        _tileOffsetBytes = tileOffsetBytes;
        _staging = staging;
    }

    /// <summary>Gets the locked rectangle in canvas coordinates.</summary>
    public ImageRect Rect { get; }

    /// <summary>Gets the width in pixels.</summary>
    public int Width => Rect.Width;

    /// <summary>Gets the number of rows.</summary>
    public int Height => Rect.Height;

    /// <summary>Gets a writable row. Only valid for <see cref="LockAccess.Write"/> and <see cref="LockAccess.ReadWrite"/>.</summary>
    /// <param name="row">Row index relative to the rectangle.</param>
    /// <returns><see cref="Width"/> pixels.</returns>
    /// <exception cref="ImageOperationException">The lock is read-only.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The row is outside the rectangle.</exception>
    public Span<T> GetRow(int row)
    {
        if (_access == LockAccess.Read)
        {
            throw new ImageOperationException("The lock was taken for reading; use ReadRow.");
        }

        return RowCore(row);
    }

    /// <summary>Gets a row for reading.</summary>
    /// <param name="row">Row index relative to the rectangle.</param>
    /// <returns><see cref="Width"/> pixels.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The row is outside the rectangle.</exception>
    public ReadOnlySpan<T> ReadRow(int row) => RowCore(row);

    /// <summary>Commits staged writes back into the canvas and releases the staging buffer.</summary>
    public void Dispose()
    {
        T[]? staging = _staging;
        if (staging == null)
        {
            return;
        }

        _staging = null;
        try
        {
            if (_access != LockAccess.Read)
            {
                _canvas.Commit<T>(Rect, staging.AsSpan(0, Rect.Width * Rect.Height));
            }
        }
        finally
        {
            Pools<T>.Pool.Return(staging);
        }
    }

    private Span<T> RowCore(int row)
    {
        if ((uint)row >= (uint)Rect.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        if (_tile != null)
        {
            int bpp = T.Format.BytesPerPixel;
            int stride = Tile.Size * bpp;
            return SpanCast.As<T>(_tile.Span.Slice(_tileOffsetBytes + (row * stride), Rect.Width * bpp));
        }

        return (_staging ?? throw new ObjectDisposedException(nameof(PixelLock<T>))).AsSpan(row * Rect.Width, Rect.Width);
    }
}
