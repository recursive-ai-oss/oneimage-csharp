namespace OneImage;

/// <summary>
/// Composes a perspective transform from steps, the way ImageSharp's <c>ProjectiveTransformBuilder</c> does. Steps are recorded and
/// evaluated against the size of the image when <see cref="BuildMatrix"/> runs (or when the builder is passed to
/// <see cref="Image.Perspective(ProjectiveTransformBuilder, bool, Rgba32, ResamplingFilter, EdgeMode, bool)"/>, once per frame size),
/// so a builder can be reused for images of any size.
/// <para>
/// <c>Append…</c> adds a step that runs after the steps so far; <c>Prepend…</c> adds one that runs before them. Rotation and skew
/// turn about the center of the image unless an origin is given; scale is about the top-left corner unless an origin is given;
/// tapers and quad mappings are defined by the image rectangle. The builder is not thread-safe.
/// </para>
/// </summary>
public sealed class ProjectiveTransformBuilder
{
    private readonly List<Func<int, int, ProjectiveMatrix>> _steps = [];

    /// <summary>Adds a ready-made matrix after the steps so far.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <returns>This builder.</returns>
    public ProjectiveTransformBuilder AppendMatrix(ProjectiveMatrix matrix) => Add(append: true, (_, _) => matrix);

    /// <summary>Adds a ready-made matrix before the steps so far.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <returns>This builder.</returns>
    public ProjectiveTransformBuilder PrependMatrix(ProjectiveMatrix matrix) => Add(append: false, (_, _) => matrix);

    /// <summary>Adds a taper of the image rectangle (see <see cref="ProjectiveMatrix.Taper"/>) after the steps so far.</summary>
    /// <param name="side">The edge that shrinks.</param>
    /// <param name="corner">Which end of that edge moves in.</param>
    /// <param name="fraction">The new edge length as a fraction of the old, above 0 and at most 1.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public ProjectiveTransformBuilder AppendTaper(TaperSide side, TaperCorner corner, double fraction)
    {
        CheckTaper(side, corner, fraction);
        return Add(append: true, (w, h) => ProjectiveMatrix.Taper(w, h, side, corner, fraction));
    }

    /// <summary>Adds a taper of the image rectangle before the steps so far.</summary>
    /// <param name="side">The edge that shrinks.</param>
    /// <param name="corner">Which end of that edge moves in.</param>
    /// <param name="fraction">The new edge length as a fraction of the old, above 0 and at most 1.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public ProjectiveTransformBuilder PrependTaper(TaperSide side, TaperCorner corner, double fraction)
    {
        CheckTaper(side, corner, fraction);
        return Add(append: false, (w, h) => ProjectiveMatrix.Taper(w, h, side, corner, fraction));
    }

    /// <summary>
    /// Adds the transform that pins the corners of the image rectangle to four points (top-left, top-right, bottom-right, bottom-left),
    /// after the steps so far. It is defined by the rectangle alone, so it replaces what earlier steps did to the corners rather than
    /// refining it; put it first or last on purpose.
    /// </summary>
    /// <param name="corners">The four destination points, a convex quadrilateral.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The points are not four finite points of a convex quadrilateral.</exception>
    public ProjectiveTransformBuilder AppendQuad(ReadOnlySpan<(double X, double Y)> corners)
    {
        (double X, double Y)[] copy = corners.ToArray();
        _ = ProjectiveMatrix.FromRectangle(1, 1, copy);
        return Add(append: true, (w, h) => ProjectiveMatrix.FromRectangle(w, h, copy));
    }

    /// <summary>Adds a rotation after the steps so far, clockwise as seen on screen, about the center of the image.</summary>
    /// <param name="degrees">Angle in degrees.</param>
    /// <returns>This builder.</returns>
    public ProjectiveTransformBuilder AppendRotationDegrees(double degrees) =>
        Add(append: true, (w, h) => ProjectiveMatrix.Rotation(degrees, w / 2.0, h / 2.0));

    /// <summary>Adds a rotation about a given point after the steps so far.</summary>
    /// <param name="degrees">Angle in degrees, clockwise on screen.</param>
    /// <param name="originX">x of the center of rotation, in pixel-edge coordinates.</param>
    /// <param name="originY">y of the center of rotation.</param>
    /// <returns>This builder.</returns>
    public ProjectiveTransformBuilder AppendRotationDegrees(double degrees, double originX, double originY) =>
        Add(append: true, (_, _) => ProjectiveMatrix.Rotation(degrees, originX, originY));

    /// <summary>Adds a rotation before the steps so far, about the center of the image.</summary>
    /// <param name="degrees">Angle in degrees, clockwise on screen.</param>
    /// <returns>This builder.</returns>
    public ProjectiveTransformBuilder PrependRotationDegrees(double degrees) =>
        Add(append: false, (w, h) => ProjectiveMatrix.Rotation(degrees, w / 2.0, h / 2.0));

    /// <summary>Adds a scale about the top-left corner after the steps so far.</summary>
    /// <param name="sx">Horizontal factor.</param>
    /// <param name="sy">Vertical factor.</param>
    /// <returns>This builder.</returns>
    public ProjectiveTransformBuilder AppendScale(double sx, double sy) => Add(append: true, (_, _) => ProjectiveMatrix.Scale(sx, sy));

    /// <summary>Adds a uniform scale about the top-left corner after the steps so far.</summary>
    /// <param name="factor">The factor.</param>
    /// <returns>This builder.</returns>
    public ProjectiveTransformBuilder AppendScale(double factor) => AppendScale(factor, factor);

    /// <summary>Adds a scale about the top-left corner before the steps so far.</summary>
    /// <param name="sx">Horizontal factor.</param>
    /// <param name="sy">Vertical factor.</param>
    /// <returns>This builder.</returns>
    public ProjectiveTransformBuilder PrependScale(double sx, double sy) => Add(append: false, (_, _) => ProjectiveMatrix.Scale(sx, sy));

    /// <summary>Adds a skew about the center of the image after the steps so far.</summary>
    /// <param name="degreesX">Horizontal skew angle in degrees.</param>
    /// <param name="degreesY">Vertical skew angle in degrees.</param>
    /// <returns>This builder.</returns>
    public ProjectiveTransformBuilder AppendSkewDegrees(double degreesX, double degreesY) =>
        Add(append: true, (w, h) => ProjectiveMatrix.Skew(degreesX, degreesY, w / 2.0, h / 2.0));

    /// <summary>Adds a skew about a given point after the steps so far.</summary>
    /// <param name="degreesX">Horizontal skew angle in degrees.</param>
    /// <param name="degreesY">Vertical skew angle in degrees.</param>
    /// <param name="originX">x of the fixed point.</param>
    /// <param name="originY">y of the fixed point.</param>
    /// <returns>This builder.</returns>
    public ProjectiveTransformBuilder AppendSkewDegrees(double degreesX, double degreesY, double originX, double originY) =>
        Add(append: true, (_, _) => ProjectiveMatrix.Skew(degreesX, degreesY, originX, originY));

    /// <summary>Adds a skew about the center of the image before the steps so far.</summary>
    /// <param name="degreesX">Horizontal skew angle in degrees.</param>
    /// <param name="degreesY">Vertical skew angle in degrees.</param>
    /// <returns>This builder.</returns>
    public ProjectiveTransformBuilder PrependSkewDegrees(double degreesX, double degreesY) =>
        Add(append: false, (w, h) => ProjectiveMatrix.Skew(degreesX, degreesY, w / 2.0, h / 2.0));

    /// <summary>Adds a translation after the steps so far.</summary>
    /// <param name="dx">Added to x.</param>
    /// <param name="dy">Added to y.</param>
    /// <returns>This builder.</returns>
    public ProjectiveTransformBuilder AppendTranslation(double dx, double dy) => Add(append: true, (_, _) => ProjectiveMatrix.Translation(dx, dy));

    /// <summary>Adds a translation before the steps so far.</summary>
    /// <param name="dx">Added to x.</param>
    /// <param name="dy">Added to y.</param>
    /// <returns>This builder.</returns>
    public ProjectiveTransformBuilder PrependTranslation(double dx, double dy) => Add(append: false, (_, _) => ProjectiveMatrix.Translation(dx, dy));

    /// <summary>The combined matrix for an image of a given size. With no steps this is the identity.</summary>
    /// <param name="width">Image width in pixels, above 0.</param>
    /// <param name="height">Image height in pixels, above 0.</param>
    /// <returns>The matrix mapping source pixel-edge coordinates to destination coordinates.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive.</exception>
    public ProjectiveMatrix BuildMatrix(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        ProjectiveMatrix total = ProjectiveMatrix.Identity;
        foreach (Func<int, int, ProjectiveMatrix> step in _steps)
        {
            total = total.Then(step(width, height));
        }

        return total;
    }

    /// <summary>A private copy of the steps so far, so later changes to the builder do not reach an image already built from it.</summary>
    internal Func<int, int, ProjectiveMatrix> Freeze()
    {
        Func<int, int, ProjectiveMatrix>[] steps = [.. _steps];
        return (w, h) =>
        {
            ProjectiveMatrix total = ProjectiveMatrix.Identity;
            foreach (Func<int, int, ProjectiveMatrix> step in steps)
            {
                total = total.Then(step(w, h));
            }

            return total;
        };
    }

    private static void CheckTaper(TaperSide side, TaperCorner corner, double fraction)
    {
        // Validate now with a unit size; the real size only matters for the numbers.
        _ = ProjectiveMatrix.Taper(1, 1, side, corner, fraction);
    }

    private ProjectiveTransformBuilder Add(bool append, Func<int, int, ProjectiveMatrix> step)
    {
        if (append)
        {
            _steps.Add(step);
        }
        else
        {
            _steps.Insert(0, step);
        }

        return this;
    }
}
