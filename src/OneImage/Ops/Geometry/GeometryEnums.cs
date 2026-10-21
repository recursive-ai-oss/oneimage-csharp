namespace OneImage;

/// <summary>What a geometry op reads when it needs pixels beyond the image edge.</summary>
public enum EdgeMode
{
    /// <summary>Beyond the edge is the background color. This is the default.</summary>
    Constant = 0,

    /// <summary>The nearest edge pixel is repeated: <c>a b c | c c c</c>.</summary>
    Clamp = 1,

    /// <summary>The image is reflected at the edge and the edge pixel is repeated: <c>c b a | a b c | c b a</c>.</summary>
    Mirror = 2,

    /// <summary>The image tiles periodically: <c>a b c | a b c | a b c</c>.</summary>
    Wrap = 3,
}

/// <summary>Which axis <see cref="Image.Flip"/> mirrors.</summary>
public enum FlipAxis
{
    /// <summary>Left and right swap (mirror around the vertical axis).</summary>
    Horizontal = 0,

    /// <summary>Top and bottom swap (mirror around the horizontal axis).</summary>
    Vertical = 1,
}
