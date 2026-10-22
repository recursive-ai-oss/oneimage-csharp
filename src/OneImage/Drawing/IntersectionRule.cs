namespace OneImage.Drawing;

/// <summary>Decides which points of a self-overlapping or multi-figure path are inside it.</summary>
public enum IntersectionRule
{
    /// <summary>Inside when the outline winds around the point a non-zero number of times (overlapping figures of the same direction merge; an opposite-direction figure cuts a hole). This is SVG's and Photoshop's default.</summary>
    NonZero = 0,

    /// <summary>Inside when a ray from the point crosses the outline an odd number of times (every overlap toggles).</summary>
    EvenOdd = 1,
}
