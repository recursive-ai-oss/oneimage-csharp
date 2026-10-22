using System.IO.Compression;
using OneImage.Text.Compression;

namespace OneImage.Text.Tests;

public class BrotliTests
{
    private static string Dir => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "brotli");

    public static IEnumerable<TheoryDataRow<string>> Vectors() =>
        Directory.GetFiles(Dir, "*.compressed").Select(f => new TheoryDataRow<string>(System.IO.Path.GetFileName(f)[..^".compressed".Length]));

    private static byte[] Decode(byte[] input, int max = 1 << 28, int ratio = 1 << 20)
    {
        byte[] buf = Brotli.Decode(input, max, ratio, 0, out int n);
        return buf.AsSpan(0, n).ToArray();
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void ReferenceVectorsDecodeToTheirOriginals(string name)
    {
        byte[] compressed = File.ReadAllBytes(System.IO.Path.Combine(Dir, name + ".compressed"));
        byte[] expected = File.ReadAllBytes(System.IO.Path.Combine(Dir, name));
        Assert.Equal(expected, Decode(compressed));
    }

    public static IEnumerable<TheoryDataRow<string, int, int>> Shapes()
    {
        foreach (string shape in new[] { "text", "random", "zeros", "records", "mixed", "words" })
        {
            foreach (int quality in new[] { 0, 1, 4, 6, 9, 11 })
            {
                foreach (int window in new[] { 10, 16, 22, 24 })
                {
                    yield return new TheoryDataRow<string, int, int>(shape, quality, window);
                }
            }
        }
    }

    private static byte[] Make(string shape, int size)
    {
        var rng = new Random(shape.GetHashCode(StringComparison.Ordinal) ^ size);
        var d = new byte[size];
        switch (shape)
        {
            case "random":
                rng.NextBytes(d);
                break;
            case "zeros":
                break;
            case "records":
                for (int i = 0; i < size; i++)
                {
                    d[i] = (byte)((i % 24) * 7 + (i / 24 % 5));
                }

                break;
            case "mixed":
                rng.NextBytes(d.AsSpan(0, size / 3));
                for (int i = size / 3; i < size; i++)
                {
                    d[i] = (byte)(i * 31 >> 3);
                }

                break;
            case "words":
            {
                // Common English and markup words, so the encoder reaches for its static dictionary.
                string[] w = ["the", "of", "and", "information", "<div class=\"", "</a>", "University", "because", " http://www.", "Government", "between", "Wikipedia", "\u0431\u044b\u043b\u043e"];
                var sb = new System.Text.StringBuilder();
                while (sb.Length < size)
                {
                    sb.Append(w[rng.Next(w.Length)]).Append(rng.Next(3) == 0 ? ". " : " ");
                }

                System.Text.Encoding.UTF8.GetBytes(sb.ToString()).AsSpan(0, size).CopyTo(d);
                break;
            }

            default:
                {
                    string[] w = ["alpha ", "beta, ", "Gamma ", "delta.\n", "epsilon ", "the ", "of "];
                    var sb = new System.Text.StringBuilder();
                    while (sb.Length < size)
                    {
                        sb.Append(w[rng.Next(w.Length)]);
                    }

                    System.Text.Encoding.ASCII.GetBytes(sb.ToString().AsSpan(0, size), d);
                    break;
                }
        }

        return d;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void MatchesTheFrameworkDecoderOnEncodedData(string shape, int quality, int window)
    {
        foreach (int size in new[] { 0, 1, 17, 1000, 70000, 600000 })
        {
            byte[] original = Make(shape, size);
            byte[] compressed = new byte[BrotliEncoder.GetMaxCompressedLength(Math.Max(size, 1))];
            Assert.True(BrotliEncoder.TryCompress(original, compressed, out int written, quality, window));
            byte[] stream = compressed.AsSpan(0, written).ToArray();

            byte[] ours = Decode(stream);
            Assert.True(original.AsSpan().SequenceEqual(ours), $"{shape} q{quality} w{window} size {size}");

            // And the framework decoder agrees with both.
            byte[] theirs = new byte[size];
            Assert.True(BrotliDecoder.TryDecompress(stream, theirs, out int n));
            Assert.Equal(size, n);
            Assert.True(theirs.AsSpan().SequenceEqual(ours));
        }
    }

    [Fact]
    public void TruncatedAndCorruptedStreamsFailTyped()
    {
        byte[] original = Make("words", 50000);
        byte[] buf = new byte[BrotliEncoder.GetMaxCompressedLength(original.Length)];
        Assert.True(BrotliEncoder.TryCompress(original, buf, out int written, 5, 22));
        byte[] stream = buf.AsSpan(0, written).ToArray();

        // Every truncation either fails with FontFormatException or (for a cut inside trailing padding) cannot succeed with the full output.
        for (int cut = 0; cut < stream.Length; cut += Math.Max(1, stream.Length / 400))
        {
            try
            {
                byte[] r = Decode(stream.AsSpan(0, cut).ToArray());
                Assert.False(r.AsSpan().SequenceEqual(original));
            }
            catch (FontFormatException)
            {
            }
        }

        var rng = new Random(7);
        for (int i = 0; i < 3000; i++)
        {
            byte[] bad = (byte[])stream.Clone();
            for (int k = 0; k < 1 + (i % 4); k++)
            {
                bad[rng.Next(bad.Length)] ^= (byte)(1 << rng.Next(8));
            }

            try
            {
                Decode(bad, 1 << 24);
            }
            catch (FontFormatException)
            {
            }
        }

        for (int i = 0; i < 2000; i++)
        {
            byte[] noise = new byte[rng.Next(1, 200)];
            rng.NextBytes(noise);
            try
            {
                Decode(noise, 1 << 24);
            }
            catch (FontFormatException)
            {
            }
        }
    }

    [Fact]
    public void OutputCapAndExpansionRatioStopHostileStreamsCheaply()
    {
        // 64 MiB of zeros compresses to a few dozen bytes.
        byte[] zeros = new byte[64 << 20];
        byte[] buf = new byte[1 << 20];
        Assert.True(BrotliEncoder.TryCompress(zeros, buf, out int written, 5, 24));
        byte[] stream = buf.AsSpan(0, written).ToArray();
        Assert.True(stream.Length < 200);

        // Within the caps it decodes in full.
        byte[] ok = Brotli.Decode(stream, zeros.Length, 1 << 24, 0, out int n);
        Assert.Equal(zeros.Length, n);
        Assert.Equal(0, ok[n - 1]);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<FontFormatException>(() => Brotli.Decode(stream, 1 << 20, 1 << 24, 1 << 30, out _));
        Assert.Throws<FontFormatException>(() => Brotli.Decode(stream, zeros.Length, 2048, zeros.Length, out _));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 4 << 20, $"hostile decodes allocated {allocated} bytes");
    }
}
