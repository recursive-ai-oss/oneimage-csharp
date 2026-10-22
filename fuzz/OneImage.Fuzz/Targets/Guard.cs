using System;
using OneImage;

/// <summary>Runs one fuzz iteration; the library's typed exceptions are expected outcomes, anything else is a finding.</summary>
internal static class Targets
{
    /// <summary>Limits tight enough that an honest-looking header cannot make an iteration slow or large.</summary>
    public static readonly ImageLimits Limits = new()
    {
        MaxPixelsPerFrame = 4_000_000,
        MaxDimension = 8192,
        MaxFrames = 8,
        MaxTotalPixels = 8_000_000,
        MaxMetadataBytes = 1_000_000,
    };

    public static void Guard(Action body)
    {
        try
        {
            body();
        }
        catch (ImageException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }
}
