using System.IO.Compression;
using System.Text;

namespace OneImage.Psd.Tests;

/// <summary>Builds layered files by hand with chosen (and dishonest) sizes, to check that every limit is enforced before anything large is allocated.</summary>
internal static class Hostile
{
    public sealed class Rec
    {
        public int Left { get; init; }

        public int Top { get; init; }

        public int Right { get; set; } = 1;

        public int Bottom { get; set; } = 1;

        public List<byte[]> Channels { get; } = [];
        public List<(string Key, byte[] Data)> Blocks { get; } = [];
    }

    public static Rec Pixel(int w, int h, int channels = 3, bool honest = true)
    {
        var r = new Rec { Right = w, Bottom = h };
        for (int i = 0; i < channels; i++)
        {
            r.Channels.Add(honest ? Raw(w, h) : [0, 0]);
        }

        return r;
    }

    public static byte[] Raw(int w, int h)
    {
        byte[] b = new byte[2 + w * h];
        return b;
    }

    public static byte[] Zip(int w, int h)
    {
        var ms = new MemoryStream();
        ms.Write([0, 2]);
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(new byte[w * h]);
        }

        return ms.ToArray();
    }

    public static Rec Divider(int type)
    {
        var r = new Rec { Right = 0, Bottom = 0 };
        r.Blocks.Add(("lsct", [0, 0, 0, (byte)type, .. "8BIM"u8, .. "norm"u8]));
        return r;
    }

    public static byte[] File(int docW, int docH, List<Rec> recs, bool psb = false, int? declaredCount = null)
    {
        var ms = new MemoryStream();
        void U16(int v) => ms.Write([(byte)(v >> 8), (byte)v]);
        void U32(uint v) => ms.Write([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        void Len(long v)
        {
            if (psb)
            {
                U32((uint)(v >> 32));
            }

            U32((uint)v);
        }

        ms.Write("8BPS"u8);
        U16(psb ? 2 : 1);
        ms.Write(new byte[6]);
        U16(3);
        U32((uint)docH);
        U32((uint)docW);
        U16(8);
        U16(3);
        U32(0);
        U32(0);

        var info = new MemoryStream();
        void I16(int v) => info.Write([(byte)(v >> 8), (byte)v]);
        void I32(uint v) => info.Write([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        void ILen(long v)
        {
            if (psb)
            {
                I32((uint)(v >> 32));
            }

            I32((uint)v);
        }

        I16(declaredCount ?? recs.Count);
        foreach (Rec r in recs)
        {
            I32((uint)r.Top);
            I32((uint)r.Left);
            I32((uint)r.Bottom);
            I32((uint)r.Right);
            I16(r.Channels.Count);
            for (int c = 0; c < r.Channels.Count; c++)
            {
                I16(c);
                ILen(r.Channels[c].Length);
            }

            info.Write("8BIM"u8);
            info.Write("norm"u8);
            info.Write([255, 0, 0, 0]);
            var extra = new MemoryStream();
            extra.Write(new byte[8]); // no mask, no blending ranges
            extra.Write([1, (byte)'L', 0, 0]);
            foreach ((string key, byte[] data) in r.Blocks)
            {
                extra.Write("8BIM"u8);
                extra.Write(Encoding.ASCII.GetBytes(key));
                int n = data.Length;
                extra.Write([(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n]);
                extra.Write(data);
            }

            I32((uint)extra.Length);
            info.Write(extra.ToArray());
        }

        foreach (Rec r in recs)
        {
            foreach (byte[] ch in r.Channels)
            {
                info.Write(ch);
            }
        }

        if (info.Length % 2 != 0)
        {
            info.WriteByte(0);
        }

        Len(info.Length + (psb ? 8 : 4) + 4);
        Len(info.Length);
        ms.Write(info.ToArray());
        U32(0); // global layer mask info
        U16(0);
        return ms.ToArray();
    }

    /// <summary>A descriptor nested <paramref name="depth"/> levels deep, behind the 4-byte version of a solid color fill block.</summary>
    public static byte[] NestedDescriptor(int depth, int itemCount = 1)
    {
        var ms = new MemoryStream();
        void U32(uint v) => ms.Write([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        void Header(uint count)
        {
            U32(0); // empty name
            U32(0);
            ms.Write("null"u8);
            U32(count);
        }

        U32(16);
        Header((uint)itemCount);
        for (int i = 0; i < depth; i++)
        {
            U32(0);
            ms.Write("Clr "u8);
            ms.Write("Objc"u8);
            Header(1);
        }

        return ms.ToArray();
    }
}

public class HostileFileTests
{
    private static PsdReadOptions Opts(PsdLimits? psd = null, ImageLimits? img = null) => new() { PsdLimits = psd, Limits = img };

    private static void LoadAndRender(byte[] file, PsdReadOptions? opts = null)
    {
        using PsdFile f = PsdFile.Load(file, opts);
        f.Document.Render().ToArray<Rgba32>();
    }

    [Fact]
    public void MoreLayersThanTheLimitFailBeforeAnythingIsRead()
    {
        List<Hostile.Rec> recs = Enumerable.Range(0, 21).Select(static _ => Hostile.Pixel(1, 1)).ToList();
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Hostile.File(4, 4, recs), Opts(new PsdLimits { MaxLayers = 20 })));

        // A count field far beyond the file (and the default limit) with no records behind it.
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Hostile.File(4, 4, [], declaredCount: 30000)));
        Assert.Throws<ImageFormatException>(() => PsdFile.Load(Hostile.File(4, 4, [], declaredCount: 5000)));
    }

    [Fact]
    public void GroupsNestedDeeperThanTheLimitAreRefused()
    {
        List<Hostile.Rec> Chain(int depth)
        {
            var recs = new List<Hostile.Rec>();
            for (int i = 0; i < depth; i++)
            {
                recs.Add(Hostile.Divider(3));
            }

            recs.Add(Hostile.Pixel(2, 2));
            for (int i = 0; i < depth; i++)
            {
                recs.Add(Hostile.Divider(1));
            }

            return recs;
        }

        var limits = new PsdLimits { MaxNesting = 4 };
        LoadAndRender(Hostile.File(4, 4, Chain(4)), Opts(limits));
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Hostile.File(4, 4, Chain(5)), Opts(limits)));

        // Many thousands of levels use an explicit stack: they fail on the limit, not with a stack overflow.
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Hostile.File(4, 4, Chain(5000)), Opts(new PsdLimits { MaxLayers = 20000 })));
    }

    [Fact]
    public void BlocksOverTheBlobLimitsAreRefused()
    {
        Hostile.Rec One(int blocks, int size)
        {
            Hostile.Rec r = Hostile.Pixel(1, 1);
            for (int i = 0; i < blocks; i++)
            {
                r.Blocks.Add(("tstB", new byte[size]));
            }

            return r;
        }

        var limits = new PsdLimits { MaxBlobBytes = 1000, MaxTotalBlobBytes = 1500 };
        LoadAndRender(Hostile.File(4, 4, [One(1, 900)]), Opts(limits));
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Hostile.File(4, 4, [One(1, 1200)]), Opts(limits)));
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Hostile.File(4, 4, [One(2, 900)]), Opts(limits)));
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Hostile.File(4, 4, [One(1, 900), One(1, 900)]), Opts(limits)));
    }

    [Fact]
    public void ABlockLongerThanItsSectionIsAFormatError()
    {
        Hostile.Rec r = Hostile.Pixel(1, 1);
        r.Blocks.Add(("tstB", new byte[4]));
        byte[] file = Hostile.File(4, 4, [r]);

        // Patch the block's length field (the 4 bytes before its last 4 data bytes) to claim 2 GiB.
        int at = file.AsSpan().LastIndexOf("tstB"u8) + 4;
        file[at] = 0x7F;
        Assert.Throws<ImageFormatException>(() => PsdFile.Load(file));
    }

    [Fact]
    public void ChannelsThatDecodeToMoreThanTheTotalAreRefused()
    {
        List<Hostile.Rec> recs = Enumerable.Range(0, 3).Select(static _ => Hostile.Pixel(1000, 1000, honest: false)).ToList();
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Hostile.File(8, 8, recs), Opts(new PsdLimits { MaxDecodedChannelBytes = 8_000_000 })));

        // A rectangle of 300,000 x 300,000 in a PSB multiplies out to 360 GB of channels: refused on its header alone.
        Hostile.Rec huge = Hostile.Pixel(1, 1, honest: false);
        huge.Right = 300_000;
        huge.Bottom = 300_000;
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Hostile.File(8, 8, [huge], psb: true)));
    }

    [Fact]
    public void ALayerRectangleBeyondItsDataIsAFormatErrorWithoutALargeAllocation()
    {
        // 30,000 x 30,000 is allowed by the limits but the channel holds two bytes.
        Hostile.Rec r = Hostile.Pixel(1, 1, honest: false);
        r.Right = 30_000;
        r.Bottom = 30_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsAny<ImageException>(() => LoadAndRender(Hostile.File(8, 8, [r])));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64L << 20);
    }

    [Fact]
    public void ACompressedChannelThatWouldDecodeToMoreThanTheChannelLimitIsRefused()
    {
        Hostile.Rec r = Hostile.Pixel(1, 1, channels: 0);
        r.Right = 600;
        r.Bottom = 600;
        r.Channels.Add(Hostile.Zip(600, 600));
        byte[] file = Hostile.File(8, 8, [r]);
        LoadAndRender(file);
        Assert.Throws<ImageLimitException>(() => LoadAndRender(file, Opts(new PsdLimits { MaxChannelBytes = 1000 })));

        // The expansion ratio guard of ImageLimits applies to ZIP channels too.
        Assert.Throws<ImageLimitException>(() => LoadAndRender(file, Opts(img: new ImageLimits { MaxExpansionRatio = 4 })));
    }

    [Fact]
    public void DescriptorsAreBoundedInDepthAndItems()
    {
        Hostile.Rec Fill(byte[] descriptor)
        {
            Hostile.Rec r = Hostile.Pixel(4, 4, channels: 0);
            r.Right = 4;
            r.Bottom = 4;
            r.Blocks.Add(("SoCo", descriptor));
            return r;
        }

        var limits = new PsdLimits { MaxDescriptorDepth = 8, MaxDescriptorItems = 100 };
        using (PsdFile.Load(Hostile.File(4, 4, [Fill(Hostile.NestedDescriptor(5))]), Opts(limits)))
        {
        }

        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Hostile.File(4, 4, [Fill(Hostile.NestedDescriptor(20))]), Opts(limits)));

        // A 100,000-deep descriptor cannot overflow the stack because nesting is followed with an explicit stack and stops at the limit.
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Hostile.File(4, 4, [Fill(Hostile.NestedDescriptor(100_000))])));
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Hostile.File(4, 4, [Fill(Hostile.NestedDescriptor(1, itemCount: 1000))]), Opts(limits)));
    }

    [Fact]
    public void HeaderSizesAreCheckedAgainstTheImageLimitsBeforeAllocating()
    {
        byte[] tiny = Psdb.Flat(1, 1, 8, 3, 3, 0, new byte[3]);
        PatchSize(tiny, 20_000, 20_000);
        var cfg = new ImageConfiguration().Add(new PsdFormat());
        Assert.Throws<ImageLimitException>(() => Image.Load(tiny, new LoadOptions { Configuration = cfg }).ToArray<Rgba32>());
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(tiny));

        PatchSize(tiny, 40_000, 4); // beyond a PSD's 30,000
        Assert.ThrowsAny<ImageException>(() => Image.Load(tiny, new LoadOptions { Configuration = cfg }).ToArray<Rgba32>());

        // Dimension limit of the caller.
        byte[] wide = Psdb.Flat(1, 1, 8, 3, 3, 0, new byte[3]);
        PatchSize(wide, 3000, 3);
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(wide, Opts(img: new ImageLimits { MaxDimension = 2048 })));
    }

    private static void PatchSize(byte[] file, int width, int height)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(14), (uint)height);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(18), (uint)width);
    }

    [Fact]
    public void RunLengthLineTablesCannotClaimMoreThanTheFileHolds()
    {
        // A 1 x 1 RLE composite whose table says the row is 60,000 bytes long.
        byte[] file = Psdb.Flat(1, 1, 8, 3, 3, 1, new byte[3]);
        int table = file.Length - (3 * 2 + 3 * 2);
        file[table + 1] = 0xFF;
        file[table] = 0xEA;
        Assert.ThrowsAny<ImageException>(() => Image.Load(file, Util.Load).ToArray<Rgba32>());
    }

    [Fact]
    public void TruncatedAndCorruptedSamplesOnlyEverGiveTypedErrors()
    {
        string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "psd");
        foreach (string path in Directory.EnumerateFiles(dir).Where(static p => new FileInfo(p).Length < 100_000))
        {
            byte[] good = File.ReadAllBytes(path);
            int step = Math.Max(1, good.Length / 400);
            for (int n = 0; n < good.Length; n += step)
            {
                Try(good.AsSpan(0, n).ToArray());
            }

            var rng = new Random(12345);
            for (int i = 0; i < 300; i++)
            {
                byte[] bad = (byte[])good.Clone();
                int flips = 1 + rng.Next(4);
                for (int k = 0; k < flips; k++)
                {
                    bad[rng.Next(bad.Length)] ^= (byte)(1 << rng.Next(8));
                }

                Try(bad);
            }
        }

        static void Try(byte[] data)
        {
            var o = new PsdReadOptions { Limits = new ImageLimits { MaxPixelsPerFrame = 4_000_000, MaxDimension = 8192 }, PsdLimits = new PsdLimits { MaxDecodedChannelBytes = 64L << 20 } };
            try
            {
                LoadAndRender(data, o);
            }
            catch (ImageException)
            {
            }

            try
            {
                Image.Load(data, new LoadOptions { Configuration = new ImageConfiguration().Add(new PsdFormat()), Limits = o.Limits }).ToArray<Rgba32>();
            }
            catch (ImageException)
            {
            }
        }
    }
}
