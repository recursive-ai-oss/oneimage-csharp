using OneImage.Codecs.Jpx;

namespace OneImage.Tests.Jpx;

/// <summary>
/// One hostile stream per JPEG 2000 budget. Each is a few hundred bytes whose header asks for far more than the data can justify; the
/// decoder must refuse it with the typed exception before allocating what the header describes, so every case also bounds the bytes
/// allocated by the calling thread.
/// </summary>
public class JpxLimitTests
{
    private const long AllocationBound = 8L << 20;

    // Output planes are allocated for the declared size before any tile is read (a 2048x2048 image takes 8 MiB); everything else must stay small.
    private static T Refuses<T>(byte[] data, JpxDecodeOptions? options = null, long bound = AllocationBound)
        where T : ImageException
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        T e = Assert.Throws<T>(() => JpxCodec.Decode(data, options));
        long used = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(used < bound, $"allocated {used} bytes before refusing");
        return e;
    }

    [Fact]
    public void DimensionOverTheLimitIsRefused() =>
        Refuses<ImageLimitException>(new JpxTestStreams.Stream { Width = 70000, Height = 8 }.Build());

    [Fact]
    public void PixelCountOverTheLimitIsRefused() =>
        Refuses<ImageLimitException>(new JpxTestStreams.Stream { Width = 60000, Height = 60000 }.Build());

    [Fact]
    public void CustomPixelLimitIsHonored() =>
        Refuses<ImageLimitException>(new JpxTestStreams.Stream { Width = 2000, Height = 2000 }.Build(), new JpxDecodeOptions { Limits = new ImageLimits { MaxPixelsPerFrame = 1_000_000 } });

    [Fact]
    public void TooManyComponentsAreRefused() =>
        Refuses<ImageLimitException>(new JpxTestStreams.Stream { Components = 300 }.Build());

    [Fact]
    public void ComponentLimitIsAnOption() =>
        Refuses<ImageLimitException>(new JpxTestStreams.Stream { Components = 5 }.Build(), new JpxDecodeOptions { MaxComponents = 4 });

    [Fact]
    public void MoreThan65535TilesAreRefused() =>
        Refuses<ImageFormatException>(new JpxTestStreams.Stream { Width = 300, Height = 300, TileWidth = 1, TileHeight = 1 }.Build());

    [Fact]
    public void TooManyCodeBlocksInATileAreRefused() =>
        Refuses<ImageLimitException>(
            new JpxTestStreams.Stream { Width = 1024, Height = 1024, CbWidthExp = 2, CbHeightExp = 2 }.Build(),
            new JpxDecodeOptions { MaxCodeBlocksPerTile = 1000 });

    [Fact]
    public void TooManyPrecinctsInATileAreRefused() =>
        Refuses<ImageLimitException>(
            new JpxTestStreams.Stream { Width = 1024, Height = 1024, PrecinctSizes = [0x00] }.Build(),
            new JpxDecodeOptions { MaxCodeBlocksPerTile = 1000 });

    [Fact]
    public void DefaultBudgetStopsFourMillionPrecincts() =>
        Refuses<ImageLimitException>(new JpxTestStreams.Stream { Width = 2048, Height = 2048, PrecinctSizes = [0x00] }.Build(), bound: 10L << 20);

    [Fact]
    public void WorkingMemoryOverTheLimitIsRefused() =>
        Refuses<ImageLimitException>(new JpxTestStreams.Stream { Width = 4096, Height = 4096 }.Build(), new JpxDecodeOptions { MaxWorkingBytes = 1 << 20 });

    [Fact]
    public void ImageFarLargerThanItsDataIsRefused() =>
        Refuses<ImageLimitException>(new JpxTestStreams.Stream { Width = 12000, Height = 12000 }.Build());

    [Fact]
    public void CodingPassWorkFarBeyondTheDataIsRefused()
    {
        // 1024 code-blocks of 64x64, each claiming 88 passes over 30 bit-planes with no data: 369 M sample visits from about 2 KB.
        var s = new JpxTestStreams.Stream
        {
            Width = 2048,
            Height = 2048,
            Guard = 1,
            Exponent = 30,
            Packets = JpxTestStreams.EmptyBlocksPacket(32, 32, 88, 0),
        };
        Refuses<ImageLimitException>(s.Build(), bound: 10L << 20);
    }

    [Fact]
    public void ModestPassWorkWithTheSameShapeIsNotRefused()
    {
        var s = new JpxTestStreams.Stream
        {
            Width = 256,
            Height = 256,
            Guard = 1,
            Exponent = 30,
            Packets = JpxTestStreams.EmptyBlocksPacket(4, 4, 10, 0),
        };
        JpxImage image = JpxCodec.Decode(s.Build());
        Assert.Equal(256, image.Width);
    }

    [Fact]
    public void MoreThanThirtyBitPlanesAreRefused()
    {
        var s = new JpxTestStreams.Stream { Width = 64, Height = 64, Guard = 7, Exponent = 31, Packets = JpxTestStreams.EmptyBlocksPacket(1, 1, 1, 0) };
        Refuses<ImageFormatException>(s.Build());
    }

    [Fact]
    public void TooManyProgressionOrderChangesAreRefused()
    {
        var poc = new List<byte> { 0xFF, 0x5F };
        poc.AddRange(JpxTestStreams.U16(2 + (7 * 1100)));
        for (int i = 0; i < 1100; i++)
        {
            poc.AddRange([0, 0]);
            poc.AddRange(JpxTestStreams.U16(1));
            poc.AddRange([1, 1, 0]);
        }

        byte[] data = new JpxTestStreams.Stream { Extra = poc.ToArray() }.Build();
        Refuses<ImageLimitException>(data);
        Assert.Throws<ImageLimitException>(() => JpxCodec.Identify(data));
    }

    [Theory]
    [InlineData("prog-lrcp.j2k")]
    [InlineData("tiles-pcrl.j2k")]
    [InlineData("mode-all-modes.j2k")]
    public void TruncatedPacketsAreDamagedWhenLenientAndRefusedWhenStrict(string file)
    {
        byte[] data = JpxTestStreams.Gen(file);
        byte[] cut = data.AsSpan(0, data.Length * 6 / 10).ToArray();
        JpxImage image = JpxCodec.Decode(cut);
        Assert.True(image.Damaged);
        Assert.Throws<ImageFormatException>(() => JpxCodec.Decode(cut, new JpxDecodeOptions { Strict = true }));
        Assert.False(JpxCodec.Decode(data, new JpxDecodeOptions { Strict = true }).Damaged);
    }

    [Fact]
    public void PrefixesAndCorruptionOnlyRaiseTypedExceptions()
    {
        var rng = new Random(12345);
        var options = new JpxDecodeOptions { MaxWorkingBytes = 64 << 20 };
        foreach (string path in Directory.EnumerateFiles(JpxTestStreams.GenDir, "*.j2k").OrderBy(p => p, StringComparer.Ordinal))
        {
            byte[] data = File.ReadAllBytes(path);
            for (int i = 0; i < 12; i++)
            {
                Attempt(data.AsSpan(0, rng.Next(0, data.Length)).ToArray(), options);
            }

            for (int i = 0; i < 24; i++)
            {
                byte[] copy = (byte[])data.Clone();
                int flips = 1 + rng.Next(3);
                for (int k = 0; k < flips; k++)
                {
                    copy[rng.Next(Math.Min(copy.Length, i < 12 ? 200 : copy.Length))] ^= (byte)(1 << rng.Next(8));
                }

                Attempt(copy, options);
            }
        }
    }

    [Fact]
    public void CancellationIsHonored()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => JpxCodec.Decode(JpxTestStreams.Gen("tiles-pcrl.j2k"), null, cts.Token));
    }

    private static void Attempt(byte[] data, JpxDecodeOptions options)
    {
        try
        {
            JpxCodec.Decode(data, options);
        }
        catch (ImageException)
        {
        }
    }
}
