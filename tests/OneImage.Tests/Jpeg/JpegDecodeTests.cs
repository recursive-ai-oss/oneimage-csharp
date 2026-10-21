using System.Text;

namespace OneImage.Tests.Jpeg;

public class JpegDecodeTests
{
    public static TheoryData<string> Names()
    {
        var d = new TheoryData<string>();
        foreach (string n in JpegTestSupport.CorpusNames())
        {
            d.Add(n);
        }

        return d;
    }

    /// <summary>Reports max and mean error per file and scale against the reference decoder (libjpeg-turbo, accurate DCT, fancy upsampling).</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void MatchesReferenceDecoder(string name)
    {
        byte[] jpeg = JpegTestSupport.Read(name);
        string[] meta = File.ReadAllText(Path.Combine(JpegTestSupport.Dir, name + ".txt")).Split(' ', StringSplitOptions.TrimEntries);
        int w = int.Parse(meta[0]);
        int h = int.Parse(meta[1]);
        int comps = int.Parse(meta[2]);
        int prec = int.Parse(meta[3]);
        byte[][] refs = JpegTestSupport.ReadRef(name, w, h, comps, prec);
        bool adobeCmyk = comps == 4;
        var report = new StringBuilder();
        int i = 0;
        foreach (int scale in new[] { 1, 2, 4, 8 })
        {
            (ImageInfo info, byte[] data, PixelFormat fmt) = JpegTestSupport.Decode(jpeg, scale);
            Assert.Equal(w, info.Width);
            Assert.Equal(comps == 4 ? ColorModel.Cmyk : comps == 3 ? ColorModel.Rgb : ColorModel.Gray, fmt.Model);
            (int max, double mean) = JpegTestSupport.Compare(data, refs[i++], prec, adobeCmyk, prec == 8 ? 255 : 4095);
            report.Append(System.Globalization.CultureInfo.InvariantCulture, $"{name} 1/{scale} max={max} mean={mean:F4}\n");
            int limit = scale == 1 ? 1 : 2;
            Assert.True(max <= limit, $"{name} at 1/{scale}: max error {max} (limit {limit})");
        }

        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "jpeg-accuracy.txt"), report.ToString());
    }
}
