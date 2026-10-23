namespace OneImage.Psd.Tests;

public class CompositeTests
{
    private static int Seed(int c, int x, int y) => (c * 131 + x * 31 + y * 17 + x * y) & 0xFF;

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void Rgb8ReadsBitExact(int compression, bool psb)
    {
        const int W = 37;
        const int H = 21;
        byte[] planes = Psdb.Planes8(W, H, 3, (c, x, y) => (byte)Seed(c, x, y));
        byte[] file = Psdb.Flat(W, H, 8, 3, 3, compression, planes, psb);
        Image image = Util.LoadComposite(file);
        Assert.Equal(PixelFormat.Rgb8, image.Format);
        Rgb24[] px = image.ToArray<Rgb24>();
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                Assert.Equal(new Rgb24((byte)Seed(0, x, y), (byte)Seed(1, x, y), (byte)Seed(2, x, y)), px[y * W + x]);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Rgb16ReadsBitExact(int compression)
    {
        const int W = 19;
        const int H = 11;
        byte[] planes = Psdb.Planes16(W, H, 3, (c, x, y) => (ushort)(Seed(c, x, y) * 257 + x));
        Image image = Util.LoadComposite(Psdb.Flat(W, H, 16, 3, 3, compression, planes));
        Assert.Equal(PixelFormat.Rgb16, image.Format);
        Rgb48[] px = image.ToArray<Rgb48>();
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                Assert.Equal(new Rgb48((ushort)(Seed(0, x, y) * 257 + x), (ushort)(Seed(1, x, y) * 257 + x), (ushort)(Seed(2, x, y) * 257 + x)), px[y * W + x]);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Float32ReadsBitExact(int compression)
    {
        const int W = 13;
        const int H = 9;
        byte[] planes = Psdb.PlanesF(W, H, 3, (c, x, y) => (Seed(c, x, y) / 255f) * 3f - 0.5f);
        Image image = Util.LoadComposite(Psdb.Flat(W, H, 32, 3, 3, compression, planes));
        Assert.Equal(PixelFormat.RgbF32, image.Format);
        RgbFloat[] px = image.ToArray<RgbFloat>();
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                Assert.Equal(new RgbFloat((Seed(0, x, y) / 255f) * 3f - 0.5f, (Seed(1, x, y) / 255f) * 3f - 0.5f, (Seed(2, x, y) / 255f) * 3f - 0.5f), px[y * W + x]);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void GrayReadsInItsOwnDepth(int compression)
    {
        byte[] p8 = Psdb.Planes8(9, 5, 1, (c, x, y) => (byte)Seed(c, x, y));
        Image g8 = Util.LoadComposite(Psdb.Flat(9, 5, 8, 1, 1, compression, p8));
        Assert.Equal(PixelFormat.Gray8, g8.Format);
        Assert.Equal(Seed(0, 4, 3), g8.ToArray<Gray8>()[3 * 9 + 4].V);

        byte[] p16 = Psdb.Planes16(9, 5, 1, (c, x, y) => (ushort)(Seed(c, x, y) * 200));
        Image g16 = Util.LoadComposite(Psdb.Flat(9, 5, 16, 1, 1, compression, p16));
        Assert.Equal(PixelFormat.Gray16, g16.Format);
        Assert.Equal(Seed(0, 4, 3) * 200, g16.ToArray<Gray16>()[3 * 9 + 4].V);
    }

    [Fact]
    public void BitmapAndIndexedAreExpanded()
    {
        // 1 is black in a bitmap.
        byte[] bits = [0b1010_0000, 0b0101_0000];
        Image bm = Util.LoadComposite(Psdb.Flat(4, 2, 1, 0, 1, 0, bits));
        Gray8[] g = bm.ToArray<Gray8>();
        Assert.Equal(new byte[] { 0, 255, 0, 255, 255, 0, 255, 0 }, g.Select(static p => p.V).ToArray());

        byte[] palette = new byte[768];
        for (int i = 0; i < 256; i++)
        {
            palette[i] = (byte)i;
            palette[256 + i] = (byte)(255 - i);
            palette[512 + i] = 7;
        }

        Image idx = Util.LoadComposite(Psdb.Flat(3, 1, 8, 2, 1, 1, [0, 100, 255], colorModeData: palette));
        Rgb24[] px = idx.ToArray<Rgb24>();
        Assert.Equal(new Rgb24(100, 155, 7), px[1]);
        Assert.Equal(new Rgb24(255, 0, 7), px[2]);
    }

    [Fact]
    public void CmykAndLabAreConvertedToRgb()
    {
        // PSD stores CMYK inverted: all 255 is no ink, so white.
        byte[] cmyk = Psdb.Planes8(2, 1, 4, (c, x, y) => x == 0 ? (byte)255 : (c == 3 ? (byte)0 : (byte)255));
        Image img = Util.LoadComposite(Psdb.Flat(2, 1, 8, 4, 4, 1, cmyk));
        Assert.Equal(PixelFormat.Rgb8, img.Format);
        Rgb24[] px = img.ToArray<Rgb24>();
        Assert.All([px[0].R, px[0].G, px[0].B], v => Assert.True(v >= 250));
        Assert.All([px[1].R, px[1].G, px[1].B], v => Assert.True(v <= 40));

        // Lab: L 255 is 100, a and b 128 are neutral.
        byte[] lab = Psdb.Planes8(2, 1, 3, (c, x, y) => c == 0 ? (x == 0 ? (byte)255 : (byte)0) : (byte)128);
        Rgb24[] l = Util.LoadComposite(Psdb.Flat(2, 1, 8, 9, 3, 0, lab)).ToArray<Rgb24>();
        Assert.True(l[0].R >= 250 && l[0].G >= 250 && l[0].B >= 250);
        Assert.True(l[1].R <= 5 && l[1].G <= 5 && l[1].B <= 5);
    }

    [Fact]
    public void TransparencyIsKeptAndTheWhiteMatteRemovedOnRequest()
    {
        // The merged image of a document with transparency is stored blended over white.
        byte[] planes = Psdb.Planes8(2, 1, 4, (c, x, y) =>
        {
            int a = x == 0 ? 255 : 128;
            int color = c == 0 ? 200 : c == 1 ? 100 : 0;
            return c == 3 ? (byte)a : (byte)((color * a + 255 * (255 - a) + 127) / 255);
        });
        byte[] file = Psdb.Flat(2, 1, 8, 3, 4, 0, planes, alphaLayerCount: true);
        Image raw = Util.LoadComposite(file);
        Assert.Equal(PixelFormat.Rgba8, raw.Format);
        Rgba32[] r = raw.ToArray<Rgba32>();
        Assert.Equal(128, r[1].A);
        Assert.True(r[1].R > 200);

        var cfg = new ImageConfiguration().Add(PsdFormat.Instance);
        Rgba32[] s = Image.Load(file, new LoadOptions { Configuration = cfg }).ToArray<Rgba32>();
        Assert.Equal(new Rgba32(200, 100, 0, 255), s[0]);
        Assert.InRange(s[1].R, 198, 202);
        Assert.InRange(s[1].G, 98, 102);
        Assert.InRange(s[1].B, 0, 2);
    }

    [Fact]
    public void IdentifyReadsOnlyTheHeader()
    {
        byte[] file = Psdb.Flat(5, 3, 16, 3, 3, 1, Psdb.Planes16(5, 3, 3, (c, x, y) => 1));
        ImageInfo info = Image.Identify(file, Util.Load);
        Assert.Equal(5, info.Width);
        Assert.Equal(3, info.Height);
        Assert.Equal("psd", info.Format.Name);
        Assert.Equal(PixelFormat.Rgb16, info.Frames[0].Format);
    }

    [Fact]
    public void IccProfileOfRgbFilesIsKept()
    {
        byte[] icc = ColorProfile.DisplayP3.IccBytes.ToArray();
        byte[] file = Psdb.Flat(2, 2, 8, 3, 3, 0, Psdb.Planes8(2, 2, 3, (c, x, y) => 9), icc: icc);
        Image img = Util.LoadComposite(file);
        Assert.Equal(icc, img.Metadata.IccProfile.ToArray());
    }

    [Fact]
    public void ThumbnailPipelineWorksOnAComposite()
    {
        byte[] planes = Psdb.Planes8(200, 100, 3, (c, x, y) => (byte)(c == 0 ? x : y));
        byte[] file = Psdb.Flat(200, 100, 8, 3, 3, 1, planes);
        var cfg = new ImageConfiguration().Add(PsdFormat.Instance);
        using var ms = new MemoryStream();
        Image.Load(file, new LoadOptions { Configuration = cfg }).Resize(width: 50).Save(ms, ImageConfiguration.Default.FindByName("png")!);
        Assert.True(ms.Length > 50);
    }

    [Fact]
    public void FormatIsSniffedAndWrittenThroughImageSave()
    {
        byte[] planes = Psdb.Planes8(31, 17, 3, (c, x, y) => (byte)Seed(c, x, y));
        Image src = Util.LoadComposite(Psdb.Flat(31, 17, 8, 3, 3, 0, planes));
        using var ms = new MemoryStream();
        src.Save(ms, Util.Raw);
        byte[] saved = ms.ToArray();
        Assert.True(Util.Raw.Matches(saved));
        Image again = Util.LoadComposite(saved);
        Assert.Equal(src.ToArray<Rgb24>(), again.ToArray<Rgb24>());

        // With transparency the pixels live in a layer and the merged image carries the alpha channel.
        Rgba32[] rgba = Enumerable.Range(0, 31 * 17).Select(i => new Rgba32((byte)i, (byte)(i * 3), (byte)(i * 7), (byte)(i % 5 == 0 ? 0 : 255 - i % 200))).ToArray();
        Image a = Image.FromPixels<Rgba32>(rgba, 31, 17);
        using var ms2 = new MemoryStream();
        a.Save(ms2, Util.Raw);
        using PsdFile f = PsdFile.Load(ms2.ToArray());
        Assert.Single(f.Document.Root.Children);
        Rgba32[] layerPixels = new Rgba32[31 * 17];
        ((Layers.PixelLayer)f.Document.Root.Children[0]).Pixels.CopyTo<Rgba32>(layerPixels, new ImageRect(0, 0, 31, 17));
        Assert.Equal(rgba, layerPixels);
    }
}
