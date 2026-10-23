using OneImage.Codecs.Jpeg;

namespace OneImage.Tests.Jpeg;

/// <summary>
/// The 12-bit lookahead tables and the branch-free refill must decode exactly like the plain table path, for good files and for damaged ones.
/// </summary>
[Collection("JpegLookahead")]
public class JpegEntropyTests
{
    private static readonly string[] s_samples =
    [
        "base-420", "base-444", "base-422", "base-420-q98", "base-420-q30", "base-420-optimized", "base-444-restart-blocks", "base-420-restart-rows",
        "prog-420", "prog-444", "prog-422-restart", "enc-ref-q75-420-prog", "enc-ref-q90-444-prog", "enc-ref-q1-420", "enc-ref-q100-444", "gray", "prog-gray",
        "cmyk", "p12-420", "p12-prog-420", "enc-ref-q85-420-b-prog",
    ];

    public static TheoryData<string> Samples()
    {
        var d = new TheoryData<string>();
        foreach (string s in s_samples)
        {
            d.Add(s);
        }

        return d;
    }

    private static string Outcome(byte[] jpeg, int scale)
    {
        try
        {
            (_, byte[] data, _) = JpegTestSupport.Decode(jpeg, scale);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        }
        catch (ImageFormatException e)
        {
            return "format: " + e.Message;
        }
        catch (ImageLimitException e)
        {
            return "limit: " + e.Message;
        }
    }

    private static string WithLookahead(byte[] jpeg, int scale, bool on)
    {
        int saved = JpegScanDecoder.LookMinPixels;
        JpegScanDecoder.LookMinPixels = on ? 0 : int.MaxValue;
        try
        {
            return Outcome(jpeg, scale);
        }
        finally
        {
            JpegScanDecoder.LookMinPixels = saved;
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void LookaheadDecodesLikeThePlainTables(string name)
    {
        byte[] jpeg = JpegTestSupport.Read(name);
        foreach (int scale in new[] { 1, 2, 4, 8 })
        {
            Assert.Equal(WithLookahead(jpeg, scale, on: false), WithLookahead(jpeg, scale, on: true));
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void LookaheadFailsLikeThePlainTablesOnDamagedData(string name)
    {
        byte[] jpeg = JpegTestSupport.Read(name);
        var rnd = new Random(name.GetHashCode(StringComparison.Ordinal) ^ 12345);
        for (int round = 0; round < 40; round++)
        {
            byte[] damaged = (byte[])jpeg.Clone();
            int flips = 1 + rnd.Next(4);
            for (int i = 0; i < flips; i++)
            {
                damaged[rnd.Next(damaged.Length / 4, damaged.Length)] ^= (byte)(1 << rnd.Next(8));
            }

            byte[] input = round % 5 == 0 ? damaged[..rnd.Next(damaged.Length / 2, damaged.Length)] : damaged;
            int scale = 1 << rnd.Next(4);
            Assert.Equal(WithLookahead(input, scale, on: false), WithLookahead(input, scale, on: true));
        }
    }
}
