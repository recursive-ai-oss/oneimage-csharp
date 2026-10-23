using OneImage.Codecs.Jpeg;

namespace OneImage.Tests.Jpeg;

/// <summary>The decoder's helper-thread stages must not change a single output byte or the way failures surface.</summary>
[Collection(MemoryMeasurement.Name)]
public class JpegStagingTests
{
    public static TheoryData<string> Names() => new() { "base-420", "base-422", "base-440", "base-444", "base-411", "prog-420", "prog-444", "arith-420", "gray", "cmyk", "ycck", "p12-420", "base-420-restart-rows", "narrow-3x40-420", "tiny-9x17-420", "wide-70x2-420" };

    private static T WithStages<T>(int mode, Func<T> body)
    {
        int old = JpegDecoderCore.StageOverride;
        JpegDecoderCore.StageOverride = mode;
        try
        {
            return body();
        }
        finally
        {
            JpegDecoderCore.StageOverride = old;
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryStageSplitGivesTheSingleThreadedBytes(string name)
    {
        byte[] jpeg = JpegTestSupport.Read(name);
        foreach (int scale in new[] { 1, 2, 4, 8 })
        {
            byte[] expected = WithStages(0, () => JpegTestSupport.Decode(jpeg, scale).Data);
            foreach (int mode in new[] { 1, 2 })
            {
                byte[] got = WithStages(mode, () => JpegTestSupport.Decode(jpeg, scale).Data);
                Assert.True(expected.AsSpan().SequenceEqual(got), $"{name} 1/{scale} stages {mode}");
            }
        }
    }

    [Theory]
    [InlineData("base-420")]
    [InlineData("prog-420")]
    [InlineData("base-444")]
    public void RegionsMatchUnderEveryStageSplit(string name)
    {
        byte[] jpeg = JpegTestSupport.Read(name);
        var rnd = new Random(name.Length);
        (ImageInfo info, _, PixelFormat fmt) = JpegTestSupport.Decode(jpeg);
        for (int k = 0; k < 6; k++)
        {
            int w = 1 + rnd.Next(info.Width);
            int h = 1 + rnd.Next(info.Height);
            var r = new ImageRect(rnd.Next(info.Width - w + 1), rnd.Next(info.Height - h + 1), w, h);
            byte[] expected = WithStages(0, () => JpegTestSupport.Decode(jpeg, 1, r).Data);
            foreach (int mode in new[] { 1, 2 })
            {
                byte[] got = WithStages(mode, () => JpegTestSupport.Decode(jpeg, 1, r).Data);
                Assert.True(expected.AsSpan().SequenceEqual(got), $"{name} region {r} stages {mode} ({fmt})");
            }
        }
    }

    [Theory]
    [InlineData("base-420")]
    [InlineData("prog-420")]
    public void TruncatedDataFailsTheSameWayAndDoesNotHang(string name)
    {
        byte[] jpeg = JpegTestSupport.Read(name);
        foreach (int cut in new[] { jpeg.Length / 2, jpeg.Length - 40 })
        {
            byte[] part = jpeg.AsSpan(0, cut).ToArray();
            Type? expected = WithStages(0, () => Record(part));
            foreach (int mode in new[] { 1, 2 })
            {
                Task<Type?> t = Task.Run(() => WithStages(mode, () => Record(part)));
                Assert.True(t.Wait(TimeSpan.FromSeconds(30)), $"{name} cut {cut} stages {mode} hung");
                Assert.Equal(expected, t.Result);
            }
        }
    }

    [Fact]
    public void DamageBeyondTheRequestedRowsIsNotAnError()
    {
        // The entropy decoder runs ahead of the output; a request satisfied by the rows before the damage must still succeed.
        var px = new Rgb24[64 * 400];
        var rnd = new Random(5);
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgb24((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
        }

        byte[] jpeg = JpegEncodeTests.Encode(Image.FromPixels<Rgb24>(px, 64, 400), new JpegEncoderOptions { Quality = 80 });
        byte[] part = jpeg.AsSpan(0, jpeg.Length * 3 / 4).ToArray();
        var top = new ImageRect(0, 0, 16, 8);
        byte[] expected = WithStages(0, () => JpegTestSupport.Decode(part, 1, top).Data);
        foreach (int mode in new[] { 1, 2 })
        {
            for (int i = 0; i < 40; i++)
            {
                byte[] got = WithStages(mode, () => JpegTestSupport.Decode(part, 1, top).Data);
                Assert.True(expected.AsSpan().SequenceEqual(got), $"stages {mode} run {i}");
            }
        }
    }

    [Fact]
    public void FuzzReproTailDamageGivesTheSameOutcomeInEveryStageSplit()
    {
        // First byte is the fuzz target's mode (scale and region flag); the rest is the file.
        byte[] all = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "jpeg-staged-tail-damage.bin"));
        int mode = all[0];
        int scale = 1 << (mode & 3);
        byte[] file = all[1..];
        using var ms = new MemoryStream(file);
        ImageInfo info = JpegFormat.Instance.ReadInfo(ms, ImageLimits.Default);
        int w = (info.Width + scale - 1) / scale;
        int h = (info.Height + scale - 1) / scale;
        ImageRect r = (mode & 4) != 0 ? new ImageRect(w / 3, h / 4, Math.Max(1, w / 2), Math.Max(1, h / 2)) : new ImageRect(0, 0, w, h);
        string Run(int stages) => WithStages(stages, () =>
        {
            try
            {
                return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JpegTestSupport.Decode(file, scale, r).Data));
            }
            catch (ImageFormatException)
            {
                return "format";
            }
        });
        string expected = Run(0);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(expected, Run(1));
            Assert.Equal(expected, Run(2));
        }
    }

    private static Type? Record(byte[] data)
    {
        try
        {
            JpegTestSupport.Decode(data);
            return null;
        }
        catch (Exception ex)
        {
            return ex.GetType();
        }
    }

    [Fact]
    public void BigFramesUseTheStagesByDefaultAndRepeatIdentically()
    {
        const int W = 1100, H = 1000;
        var px = new Rgb24[W * H];
        var rnd = new Random(3);
        for (int i = 0; i < px.Length; i++)
        {
            int n = rnd.Next(12);
            px[i] = new Rgb24((byte)((i % W / 5) + n), (byte)((i / W / 4) + n), (byte)(n * 9));
        }

        byte[] jpeg = JpegEncodeTests.Encode(Image.FromPixels<Rgb24>(px, W, H), new JpegEncoderOptions { Quality = 80 });
        byte[] expected = WithStages(0, () => JpegTestSupport.Decode(jpeg).Data);
        for (int i = 0; i < 5; i++)
        {
            byte[] got = WithStages(-1, () => JpegTestSupport.Decode(jpeg).Data);
            Assert.True(expected.AsSpan().SequenceEqual(got));
        }
    }
}
