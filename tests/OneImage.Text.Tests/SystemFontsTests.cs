namespace OneImage.Text.Tests;

public sealed class SystemFontsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oneimage-fonts-" + Guid.NewGuid().ToString("N"));

    public SystemFontsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a", "deep"));
        Directory.CreateDirectory(Path.Combine(_root, "b"));
        File.Copy(TestFonts.PathOf(TestFonts.Regular), Path.Combine(_root, "a", "NotoSans-Regular.TTF"));
        File.Copy(TestFonts.PathOf(TestFonts.BoldKern), Path.Combine(_root, "a", "deep", "NotoSans-Bold.ttf"));
        File.Copy(TestFonts.PathOf(TestFonts.Cff), Path.Combine(_root, "b", "cjk.otf"));
        File.Copy(TestFonts.PathOf(TestFonts.Collection), Path.Combine(_root, "b", "set.ttc"));
        File.Copy(TestFonts.PathOf(TestFonts.Woff), Path.Combine(_root, "b", "x.woff"));
        File.WriteAllBytes(Path.Combine(_root, "b", "broken.ttf"), [0, 1, 0, 0, 0, 3, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]);
        File.WriteAllBytes(Path.Combine(_root, "b", "empty.ttf"), []);
        File.WriteAllText(Path.Combine(_root, "b", "readme.txt"), "not a font");
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void ScanFindsFamiliesWithoutLoadingAnyFont()
    {
        var set = new SystemFontSet([_root]);
        IReadOnlyList<string> names = set.FamilyNames;
        Assert.Contains("Noto Sans", names);
        Assert.Contains("Noto Sans CJK JP", names);
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names);
        Assert.Equal(0, set.LoadedFileCount);
    }

    [Fact]
    public void ALookupLoadsOnlyThatFamiliesFiles()
    {
        var set = new SystemFontSet([_root]);
        Assert.False(set.TryGet("No Such Family", out _));
        Assert.Equal(0, set.LoadedFileCount);
        FontFamily noto = set.Get("noto sans");
        Assert.Equal("Noto Sans", noto.Name);
        Assert.True(noto.IsStyleAvailable(FontStyle.Regular));
        Assert.True(noto.IsStyleAvailable(FontStyle.Bold));
        int loaded = set.LoadedFileCount;
        Assert.InRange(loaded, 2, 3);
        Assert.Same(noto, set.Get("Noto Sans"));
        Assert.Equal(loaded, set.LoadedFileCount);
        Font f = set.CreateFont("Noto Sans CJK JP", 20);
        Assert.True(f.HasGlyph('A'));
        Assert.Throws<ArgumentException>(() => set.Get("missing"));
    }

    [Fact]
    public void ACollectionFileContributesEveryFace()
    {
        var set = new SystemFontSet([Path.Combine(_root, "b")]);
        using var ms = new MemoryStream(File.ReadAllBytes(TestFonts.PathOf(TestFonts.Collection)));
        var expected = new List<string>();
        Assert.True(SystemFontSet.TryReadFamilies(ms, expected));
        foreach (string name in expected)
        {
            Assert.Contains(name, set.FamilyNames);
        }
    }

    [Fact]
    public void BrokenAndForeignFilesAreSkippedAndMissingRootsIgnored()
    {
        var set = new SystemFontSet([Path.Combine(_root, "does-not-exist"), Path.Combine(_root, "b"), ""]);
        Assert.DoesNotContain("Unknown", set.FamilyNames);
        Assert.NotEmpty(set.FamilyNames);
        Assert.Empty(new SystemFontSet([Path.Combine(_root, "nothing")]).FamilyNames);
    }

    [Fact]
    public void ScanDepthIsBounded()
    {
        string dir = _root;
        for (int i = 0; i < SystemFontSet.MaxDepth + 3; i++)
        {
            dir = Path.Combine(dir, "d");
        }

        Directory.CreateDirectory(dir);
        File.Copy(TestFonts.PathOf(TestFonts.Shaping), Path.Combine(dir, "deep.ttf"));
        var set = new SystemFontSet([Path.Combine(_root, "d")]);
        Assert.Empty(set.FamilyNames);
    }

    [Fact]
    public void TruncatedFontHeadersNeverEscapeAsExceptions()
    {
        byte[] font = TestFonts.Bytes(TestFonts.Collection);
        for (int cut = 0; cut < Math.Min(font.Length, 400); cut++)
        {
            using var ms = new MemoryStream(font, 0, cut);
            _ = SystemFontSet.TryReadFamilies(ms, []);
        }

        var rnd = new Random(7);
        for (int i = 0; i < 300; i++)
        {
            byte[] copy = (byte[])font.Clone();
            for (int k = 0; k < 8; k++)
            {
                copy[rnd.Next(0, 200)] = (byte)rnd.Next(256);
            }

            using var ms = new MemoryStream(copy);
            _ = SystemFontSet.TryReadFamilies(ms, []);
        }
    }

    [Fact]
    public void TheStandardDirectoriesOfThisMachineAreScanned()
    {
        // Only meaningful where fonts are installed; elsewhere the scan just finds nothing.
        SystemFontSet set = SystemFonts.Installed;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<string> names = set.FamilyNames;
        sw.Stop();
        int files = set.Directories.Where(Directory.Exists).Sum(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Count());
        TestReport.Line($"SYSTEMFONTS {set.Directories.Count} directories, {files} files, {names.Count} families, first scan {sw.ElapsedMilliseconds} ms");
        if (set.Directories.Any(d => Directory.Exists(d) && Directory.EnumerateFiles(d, "*.ttf", SearchOption.AllDirectories).Any()))
        {
            Assert.NotEmpty(names);
            Font f = set.CreateFont(names[0], 16);
            Assert.True(f.Size > 0);
            
        }
    }
}
