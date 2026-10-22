namespace OneImage.Drawing;

/// <summary>Small path constructors for the drawing helpers.</summary>
internal static class StrokeShapes
{
    /// <summary>One figure: the first point, then a cubic per three points.</summary>
    public static Path Beziers(ReadOnlySpan<System.Numerics.Vector2> p)
    {
        if (p.Length < 4 || (p.Length - 1) % 3 != 0)
        {
            throw new ArgumentException("A chain of cubic Béziers needs 1 + 3 × n points, n at least 1.", nameof(p));
        }

        var b = new PathBuilder().MoveTo(p[0]);
        for (int i = 1; i < p.Length; i += 3)
        {
            b.CubicBezierTo(p[i].X, p[i].Y, p[i + 1].X, p[i + 1].Y, p[i + 2].X, p[i + 2].Y);
        }

        return b.Build();
    }
}
