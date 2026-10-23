using OneImage.Codecs.Jbig2;

namespace OneImage.Tests.Jbig2;

/// <summary>
/// One hostile stream per JBIG2 budget: a header that asks for far more than the data can justify must be refused
/// with a limit exception before the allocation it describes, so every case also checks the bytes allocated.
/// </summary>
public class Jbig2LimitTests
{
    private const long AllocationBound = 6L << 20;

    [Fact]
    public void TooManySegmentsAreRefused()
    {
        var s = new List<byte>();
        s.AddRange(Page(8, 8));
        for (uint i = 1; i <= 6; i++)
        {
            s.AddRange(Segment(i, 62, []));
        }

        Refuses(s.ToArray(), new Jbig2Limits { MaxSegments = 4 });
    }

    [Fact]
    public void TooManyReferredSegmentsAreRefused()
    {
        var s = new List<byte>();
        s.AddRange(Page(8, 8));
        s.AddRange(Segment(1, 62, [], 1, 2, 3));
        Refuses(s.ToArray(), new Jbig2Limits { MaxReferredSegments = 2 });
    }

    [Fact]
    public void SymbolDictionaryWithTooManySymbolsIsRefused()
    {
        var sd = new List<byte> { 0, 0 };
        sd.AddRange(new byte[8]);
        sd.AddRange(U32(1_000_000)); // exported
        sd.AddRange(U32(1_000_000)); // new
        sd.AddRange(new byte[8]);
        var s = new List<byte>();
        s.AddRange(Page(8, 8));
        s.AddRange(Segment(1, 0, sd.ToArray()));
        Refuses(s.ToArray(), new Jbig2Limits { MaxSymbolsPerDictionary = 1000 });
    }

    [Fact]
    public void SymbolDictionaryDeclaringImpossibleCountsIsRefused()
    {
        var sd = new List<byte> { 0, 0 };
        sd.AddRange(new byte[8]);
        sd.AddRange(U32(0xFFFFFFFF));
        sd.AddRange(U32(0xFFFFFFFF));
        var s = new List<byte>();
        s.AddRange(Page(8, 8));
        s.AddRange(Segment(1, 0, sd.ToArray()));
        Refuses(s.ToArray(), null);
    }

    [Fact]
    public void PatternDictionaryWithTooManyPatternsIsRefused()
    {
        Refuses(PatternDictionaryStream(2, 2, 100_000), new Jbig2Limits { MaxPatterns = 1000 });
    }

    [Fact]
    public void PatternDictionaryBeyondTheRetainedBudgetIsRefused()
    {
        // 255 x 255 patterns, 1001 of them: about 8 MB of bitmaps from a handful of bytes.
        Refuses(PatternDictionaryStream(255, 255, 1000), new Jbig2Limits { MaxRetainedBytes = 1 << 20 });
    }

    [Fact]
    public void HalftoneGridBeyondTheCellLimitIsRefused()
    {
        var s = new List<byte>();
        s.AddRange(Page(8, 8));
        s.AddRange(Segment(1, 16, PatternDictionaryData(2, 2, 1)));
        var h = new List<byte>();
        h.AddRange(RegionInfo(8, 8));
        h.Add(0);
        h.AddRange(U32(8192)); // grid width
        h.AddRange(U32(8192)); // grid height
        h.AddRange(new byte[8]);
        h.AddRange(new byte[4]);
        h.AddRange(new byte[8]);
        s.AddRange(Segment(2, 22, h.ToArray(), 1));
        Refuses(s.ToArray(), new Jbig2Limits { MaxHalftoneCells = 1 << 20 });
    }

    [Fact]
    public void GenericRegionDeclaringFarMorePixelsThanItsDataIsRefused()
    {
        var g = new List<byte>();
        g.AddRange(RegionInfo(16000, 16000));
        g.Add(0); // arithmetic, template 0
        g.AddRange(new byte[8]);
        g.AddRange(new byte[] { 1, 2, 3 });
        var s = new List<byte>();
        s.AddRange(Page(8, 8));
        s.AddRange(Segment(1, 38, g.ToArray()));
        Refuses(s.ToArray(), null);
    }

    [Fact]
    public void HugePageIsRefusedBeforeItIsAllocated()
    {
        Refuses(Page(1 << 30, 1 << 30), null);
    }

    [Fact]
    public void PageBeyondTheDecodedPixelBudgetIsRefused()
    {
        Refuses(Page(1000, 1000), new Jbig2Limits { MaxDecodedPixels = 500_000 });
    }

    [Fact]
    public void UnknownHeightPageGrowingPastTheLimitIsRefused()
    {
        var s = new List<byte>();
        s.AddRange(U32(60000));
        s.AddRange(U32(0xFFFFFFFF));
        s.AddRange(new byte[8]);
        s.Add(0);
        s.AddRange([0x80, 0x10]);
        var all = new List<byte>(Segment(0, 48, s.ToArray()));
        all.AddRange(Segment(1, 50, U32(1_000_000)));
        Refuses(all.ToArray(), null);
    }

    [Fact]
    public void HuffmanTableWithTooManyLinesIsRefused()
    {
        var t = new List<byte> { 0 };
        t.AddRange(U32(0));
        t.AddRange(U32(1_000_000));
        t.AddRange(new byte[64]);
        var s = new List<byte>();
        s.AddRange(Page(8, 8));
        s.AddRange(Segment(1, 53, t.ToArray()));
        Refuses(s.ToArray(), new Jbig2Limits { MaxHuffmanTableLines = 4 });
    }

    [Fact]
    public void WorkCountersEnforceTheirBudgets()
    {
        var work = new Jbig2Work(ImageLimits.Default, new Jbig2Limits { MaxDecodedPixels = 100, MaxInstances = 10, MaxRetainedBytes = 50, MaxSymbolPixels = 64 }, default);
        work.ChargePixels(10, 10);
        Assert.Throws<ImageLimitException>(() => work.ChargePixels(1, 1));
        work.ChargeInstances(10);
        Assert.Throws<ImageLimitException>(() => work.ChargeInstances(1));
        work.ChargeRetained(50);
        Assert.Throws<ImageLimitException>(() => work.ChargeRetained(1));
        work.CheckSymbol(8, 8);
        Assert.Throws<ImageLimitException>(() => work.CheckSymbol(9, 8));
        Assert.Throws<ImageLimitException>(() => work.CheckSymbol(1 << 20, 1));
        Assert.Throws<ImageFormatException>(() => work.CheckSymbol(-1, 1));
        work.CheckDensity(1000, 1000, 1);
        Assert.Throws<ImageLimitException>(() => work.CheckDensity(1 << 20, 2, 1));
    }

    private static void Refuses(byte[] stream, Jbig2Limits? limits)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ImageLimitException>(() => Jbig2Codec.Decode(stream, ReadOnlyMemory<byte>.Empty, null, limits));
        long used = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(used < AllocationBound, $"allocated {used} bytes before refusing");
    }

    private static byte[] PatternDictionaryStream(int w, int h, uint grayMax)
    {
        var s = new List<byte>();
        s.AddRange(Page(8, 8));
        s.AddRange(Segment(1, 16, PatternDictionaryData(w, h, grayMax)));
        return s.ToArray();
    }

    private static byte[] PatternDictionaryData(int w, int h, uint grayMax)
    {
        var d = new List<byte> { 0, (byte)w, (byte)h };
        d.AddRange(U32(grayMax));
        d.AddRange(new byte[] { 1, 2, 3, 4 });
        return d.ToArray();
    }

    private static byte[] Page(uint width, uint height)
    {
        var d = new List<byte>();
        d.AddRange(U32(width));
        d.AddRange(U32(height));
        d.AddRange(new byte[8]);
        d.Add(0);
        d.AddRange(new byte[2]);
        return Segment(0, 48, d.ToArray());
    }

    private static byte[] RegionInfo(uint width, uint height)
    {
        var d = new List<byte>();
        d.AddRange(U32(width));
        d.AddRange(U32(height));
        d.AddRange(new byte[8]);
        d.Add(0);
        return d.ToArray();
    }

    // T.88 7.2: segment number, flags (type), referred-to count and numbers (up to four, one byte each), page, length.
    private static byte[] Segment(uint number, int type, byte[] data, params byte[] referred)
    {
        var s = new List<byte>();
        s.AddRange(U32(number));
        s.Add((byte)type);
        s.Add((byte)(referred.Length << 5));
        s.AddRange(referred);
        s.Add(1);
        s.AddRange(U32((uint)data.Length));
        s.AddRange(data);
        return s.ToArray();
    }

    private static byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
}
