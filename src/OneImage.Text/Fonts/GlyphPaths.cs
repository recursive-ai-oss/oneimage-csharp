using System.Numerics;
using OneImage.Drawing;

namespace OneImage.Text.Fonts;

/// <summary>Turns font outlines into drawing paths.</summary>
internal static class GlyphPaths
{
    /// <summary>Appends an outline to a builder after applying <paramref name="m"/> to every point.</summary>
    public static void Append(PathBuilder b, Outline o, in Matrix3x2 m)
    {
        float[] c = o.Coords;
        int p = 0;
        foreach (byte verb in o.Verbs)
        {
            switch (verb)
            {
                case Outline.Move:
                    b.MoveTo(X(m, c[p], c[p + 1]), Y(m, c[p], c[p + 1]));
                    p += 2;
                    break;
                case Outline.Line:
                    b.LineTo(X(m, c[p], c[p + 1]), Y(m, c[p], c[p + 1]));
                    p += 2;
                    break;
                case Outline.Quad:
                    b.QuadraticBezierTo(X(m, c[p], c[p + 1]), Y(m, c[p], c[p + 1]), X(m, c[p + 2], c[p + 3]), Y(m, c[p + 2], c[p + 3]));
                    p += 4;
                    break;
                case Outline.Cubic:
                    b.CubicBezierTo(X(m, c[p], c[p + 1]), Y(m, c[p], c[p + 1]), X(m, c[p + 2], c[p + 3]), Y(m, c[p + 2], c[p + 3]), X(m, c[p + 4], c[p + 5]), Y(m, c[p + 4], c[p + 5]));
                    p += 6;
                    break;
                default:
                    b.Close();
                    break;
            }
        }
    }

    private static float X(in Matrix3x2 m, float x, float y) => (m.M11 * x) + (m.M21 * y) + m.M31;

    private static float Y(in Matrix3x2 m, float x, float y) => (m.M12 * x) + (m.M22 * y) + m.M32;
}
