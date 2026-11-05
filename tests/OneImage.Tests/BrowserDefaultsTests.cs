namespace OneImage.Tests;

public class BrowserDefaultsTests
{
    [Fact]
    public void DefaultLimitsAreUnchangedOffTheBrowser()
    {
        Assert.False(OperatingSystem.IsBrowser());
        Assert.Equal(1L << 28, ImageLimits.Default.MaxPixelsPerFrame);
        Assert.Equal(1L << 30, ImageLimits.Default.MaxTotalPixels);
    }

    [Fact]
    public void BrowserLimitsAreLowerButStillHoldAScanOrAPhoto()
    {
        Assert.Equal(1L << 26, ImageLimits.DefaultMaxPixelsPerFrame(browser: true));
        Assert.Equal(1L << 27, ImageLimits.DefaultMaxTotalPixels(browser: true));
        Assert.Equal(1L << 28, ImageLimits.DefaultMaxPixelsPerFrame(browser: false));
        Assert.Equal(1L << 30, ImageLimits.DefaultMaxTotalPixels(browser: false));
        Assert.True(5100L * 7000 < ImageLimits.DefaultMaxPixelsPerFrame(browser: true)); // an A4 page at 600 dpi
        Assert.True(8192L * 8192 <= ImageLimits.DefaultMaxPixelsPerFrame(browser: true));
    }
}
