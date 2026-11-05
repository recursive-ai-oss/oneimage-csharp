using System;
using OneImage;
using OneImage.Metafile;

/// <summary>
/// EMF (with EMF+) and WMF files three ways: the interpreter on its own, a visitor walking every command, and a small render through
/// the registered format (which interprets again and draws), all under tight metafile and image limits.
/// </summary>
internal static class MetafileTarget
{
    private static readonly MetafileLimits Limits = new()
    {
        MaxFileBytes = 1 << 20,
        MaxRecords = 20_000,
        MaxCommands = 50_000,
        MaxEmbeddedEmfBytes = 1 << 20,
        MaxObjects = 1024,
        MaxPathPoints = 50_000,
        MaxBitmapPixels = 1 << 20,
        MaxTotalBitmapPixels = 1 << 22,
        MaxSaveDepth = 64,
        MaxRegionRectangles = 10_000,
        MaxTextCharacters = 5_000,
        MaxDashEntries = 64,
        MaxPlusObjectBytes = 1 << 20,
        MaxWork = 1L << 26,
    };

    private static readonly MetafileFontOptions Fonts = new() { UseSystemFonts = false };

    private sealed class Walker : DrawingCommandVisitor
    {
    }

    public static void Emf(ReadOnlySpan<byte> data) => Run(data, preferEmbedded: true);

    public static void Wmf(ReadOnlySpan<byte> data) => Run(data, preferEmbedded: false);

    private static void Run(ReadOnlySpan<byte> data, bool preferEmbedded)
    {
        if (data.Length < 8)
        {
            return;
        }

        byte[] file = data.ToArray();
        var options = new MetafileReadOptions { Limits = Limits, PreferEmbeddedEmf = preferEmbedded };
        Targets.Guard(() =>
        {
            MetafileDocument doc = MetafileDocument.Parse(file, options);
            doc.Play(new Walker());
        });
        Targets.Guard(() =>
        {
            var config = new ImageConfiguration().Add(new MetafileFormat(Limits, Fonts)).Add(new WmfFormat(Limits, Fonts));
            Image image = Image.Load(file, new LoadOptions { Limits = Targets.Limits, MaxDegree = 1, Configuration = config });
            image = image.Resize(width: 48);
            image.ToArray<Rgba32>();
        });
    }
}
