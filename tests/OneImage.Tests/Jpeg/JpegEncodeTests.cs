namespace OneImage.Tests.Jpeg;

public class JpegEncodeTests
{
    public static TheoryData<string> Cases()
    {
        var d = new TheoryData<string>();
        foreach (string line in File.ReadAllLines(Path.Combine(JpegTestSupport.Dir, "enc-cases.txt")))
        {
            d.Add(line);
        }

        return d;
    }

    internal static (int W, int H, int Channels, byte[] Data) ReadPnm(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int pos = 2;
        int[] v = new int[3];
        for (int i = 0; i < 3; i++)
        {
            while (b[pos] is (byte)' ' or (byte)'\n')
            {
                pos++;
            }

            int n = 0;
            while (b[pos] >= (byte)'0')
            {
                n = (n * 10) + (b[pos++] - '0');
            }

            v[i] = n;
        }

        pos++;
        return (v[0], v[1], b[1] == (byte)'6' ? 3 : 1, b[pos..]);
    }

    internal static Image ToImage(string path)
    {
        (int w, int h, int ch, byte[] data) = ReadPnm(path);
        if (ch == 1)
        {
            var px = new Gray8[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Gray8(data[i]);
            }

            return Image.FromPixels<Gray8>(px, w, h);
        }

        var rgb = new Rgb24[w * h];
        for (int i = 0; i < rgb.Length; i++)
        {
            rgb[i] = new Rgb24(data[3 * i], data[(3 * i) + 1], data[(3 * i) + 2]);
        }

        return Image.FromPixels<Rgb24>(rgb, w, h);
    }

    internal static JpegEncoderOptions Options(int quality, string sampling, bool optimize, bool progressive, int restart) => new()
    {
        Quality = quality,
        Subsampling = sampling switch
        {
            "1x1" => JpegSubsampling.Yuv444,
            "2x1" => JpegSubsampling.Yuv422,
            "1x2" => JpegSubsampling.Yuv440,
            _ => JpegSubsampling.Yuv420,
        },
        OptimizeHuffman = optimize,
        Progressive = progressive,
        RestartInterval = restart,
    };

    internal static byte[] Encode(Image image, JpegEncoderOptions o)
    {
        using var ms = new MemoryStream();
        image.Save(ms, JpegFormat.Instance, new SaveOptions { EncoderOptions = o });
        return ms.ToArray();
    }

    /// <summary>The encoder writes exactly the bytes the reference encoder writes for the same pixels and settings.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesReferenceEncoderBytes(string line)
    {
        string[] f = line.Split(' ');
        Image image = ToImage(Path.Combine(JpegTestSupport.Dir, f[1]));
        byte[] ours = Encode(image, Options(int.Parse(f[2]), f[3], f[4] == "1", f[5] == "1", int.Parse(f[6])));
        byte[] expected = File.ReadAllBytes(Path.Combine(JpegTestSupport.Dir, "enc-ref-" + f[0] + ".jpg"));
        int firstDiff = -1;
        for (int i = 0; i < Math.Min(ours.Length, expected.Length); i++)
        {
            if (ours[i] != expected[i])
            {
                firstDiff = i;
                break;
            }
        }

        Assert.True(firstDiff < 0 && ours.Length == expected.Length, $"{f[0]}: sizes {ours.Length} vs {expected.Length}, first difference at {firstDiff}");
    }
}
