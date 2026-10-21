namespace OneImage;

/// <summary>An axis-aligned pixel rectangle. The right and bottom edges are exclusive.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct ImageRect(int X, int Y, int Width, int Height)
{
    /// <summary>Gets the exclusive right edge.</summary>
    public int Right => X + Width;

    /// <summary>Gets the exclusive bottom edge.</summary>
    public int Bottom => Y + Height;

    /// <summary>Gets a value indicating whether the rectangle covers no pixels.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Returns the overlap of two rectangles (empty with zero size when they do not touch).</summary>
    /// <param name="other">The other rectangle.</param>
    /// <returns>The intersection.</returns>
    public ImageRect Intersect(ImageRect other)
    {
        int x0 = Math.Max(X, other.X);
        int y0 = Math.Max(Y, other.Y);
        int x1 = Math.Min(Right, other.Right);
        int y1 = Math.Min(Bottom, other.Bottom);
        return x1 <= x0 || y1 <= y0 ? new ImageRect(x0, y0, 0, 0) : new ImageRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Returns the rectangle shifted by an offset.</summary>
    /// <param name="dx">Horizontal shift.</param>
    /// <param name="dy">Vertical shift.</param>
    /// <returns>The shifted rectangle.</returns>
    public ImageRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    /// <summary>Returns whether this rectangle lies fully inside a <paramref name="width"/> x <paramref name="height"/> area at the origin.</summary>
    /// <param name="width">Area width.</param>
    /// <param name="height">Area height.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsInside(int width, int height) =>
        X >= 0 && Y >= 0 && Width >= 0 && Height >= 0 && (long)X + Width <= width && (long)Y + Height <= height;
}
