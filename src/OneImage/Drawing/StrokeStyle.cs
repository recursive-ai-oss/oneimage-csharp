namespace OneImage.Drawing;

/// <summary>How two stroked segments are connected on the outside of a turn. The inside of a turn is always joined cleanly.</summary>
public enum StrokeJoin
{
    /// <summary>
    /// The outer edges are extended until they meet in a point (SVG and PostScript <c>miter</c>). When the point would lie
    /// farther from the vertex than <c>miterLimit × width / 2</c> the join is cut off flat, like <see cref="Bevel"/>.
    /// </summary>
    Miter = 0,

    /// <summary>A circular arc around the vertex, radius half the width.</summary>
    Round = 1,

    /// <summary>The two outer corners are connected by one straight line.</summary>
    Bevel = 2,

    /// <summary>
    /// The outer corner is squared off at a distance of half the width from the vertex, as the Clipper library's square join
    /// does (which is what ImageSharp.Drawing's <c>JointStyle.Square</c> uses): a corner flatter than a miter and more generous than a bevel.
    /// </summary>
    Square = 3,
}

/// <summary>How the two ends of an open stroked line (and of every dash) are finished.</summary>
public enum StrokeCap
{
    /// <summary>The line ends flat, exactly at its end point.</summary>
    Butt = 0,

    /// <summary>A half circle, radius half the width, centered on the end point.</summary>
    Round = 1,

    /// <summary>The line is extended by half the width and ends flat.</summary>
    Square = 2,
}
