namespace OneImage;

/// <summary>The kernel set <see cref="Image.DetectEdges"/> uses. Kernels are the classic unnormalized integer ones, as in ImageSharp.</summary>
public enum EdgeDetector
{
    /// <summary>3×3 Sobel: <c>sqrt(gx² + gy²)</c> with weights 1, 2, 1. Unlike <see cref="Image.Sobel"/> the result is not divided by 4.</summary>
    Sobel = 0,

    /// <summary>3×3 Prewitt: <c>sqrt(gx² + gy²)</c> with weights 1, 1, 1.</summary>
    Prewitt = 1,

    /// <summary>3×3 Scharr: <c>sqrt(gx² + gy²)</c> with weights 3, 10, 3.</summary>
    Scharr = 2,

    /// <summary>2×2 Roberts cross: <c>sqrt(gx² + gy²)</c> of the two diagonal differences. The window covers the pixel and its upper-left neighbours.</summary>
    RobertsCross = 3,

    /// <summary>3×3 Kayyali: <c>sqrt(gx² + gy²)</c> of the two diagonal kernels with weights 6.</summary>
    Kayyali = 4,

    /// <summary>3×3 Laplacian: 8 at the center, −1 around it. Negative responses become 0.</summary>
    Laplacian3x3 = 5,

    /// <summary>5×5 Laplacian: 24 at the center, −1 around it. Negative responses become 0.</summary>
    Laplacian5x5 = 6,

    /// <summary>5×5 Laplacian of Gaussian. Negative responses become 0.</summary>
    LaplacianOfGaussian = 7,

    /// <summary>Kirsch compass: the largest response of the eight rotations of the 5, 5, 5, −3 … −3 kernel.</summary>
    Kirsch = 8,

    /// <summary>Robinson compass: the largest response of the eight rotations of the 1, 2, 1, 0, −1, −2, −1, 0 kernel.</summary>
    Robinson = 9,
}
