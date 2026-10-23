using System.Text.Json;
using OneImage.Codecs.Jpx;

namespace OneImage.Tests.Jpx;

/// <summary>
/// The committed generated corpus (<c>testdata/jpx/gen</c>): small streams written by an independent encoder covering every
/// progression order, tiling, precincts, code-block sizes, code-block styles, SOP/EPH, PLT/TLM and 16-bit data. Lossless files must
/// decode to exactly the recorded SHA-256; lossy ones must stay within one level at 8 bits (one level of 8 bits at higher depths) of
/// the independently decoded <c>.exp</c> samples.
/// </summary>
public class JpxCorpusTests
{
    public static IEnumerable<TheoryDataRow<string>> Files()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(JpxTestStreams.GenDir, "manifest.json")));
        foreach (JsonElement e in doc.RootElement.EnumerateArray())
        {
            yield return new TheoryDataRow<string>(e.GetProperty("file").GetString()!);
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void FileDecodesAsRecorded(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(JpxTestStreams.GenDir, "manifest.json")));
        JsonElement entry = doc.RootElement.EnumerateArray().First(e => e.GetProperty("file").GetString() == file);
        bool lossless = entry.GetProperty("lossless").GetBoolean();
        int bits = entry.GetProperty("bits").GetInt32();

        JpxImage image = JpxCodec.Decode(JpxTestStreams.Gen(file));
        Assert.Equal(entry.GetProperty("width").GetInt32(), image.Width);
        Assert.Equal(entry.GetProperty("height").GetInt32(), image.Height);
        Assert.Equal(entry.GetProperty("channels").GetInt32(), image.ChannelCount);
        Assert.False(image.Damaged);

        byte[] raw = Interleave(image);
        if (lossless)
        {
            Assert.Equal(entry.GetProperty("sha256").GetString(), JpxTestStreams.Sha256(raw));
            return;
        }

        byte[] expected = File.ReadAllBytes(Path.Combine(JpxTestStreams.GenDir, Path.ChangeExtension(file, ".exp")));
        Assert.Equal(expected.Length, raw.Length);
        int tolerance = bits > 8 ? 1 << (bits - 8) : 1;
        for (int i = 0; i < raw.Length; i += 2)
        {
            int d = Math.Abs((raw[i] | (raw[i + 1] << 8)) - (expected[i] | (expected[i + 1] << 8)));
            Assert.True(d <= tolerance, $"{file}: sample {i / 2} differs by {d}");
        }
    }

    // Output hashes of every corpus file, as little-endian interleaved 16-bit samples. They pin the exact output, including the lossy
    // files, so that any change of arithmetic or of the hardware path in use shows up.
    private static readonly Dictionary<string, string> Hashes = new()
    {
        ["cblk-1024x4.j2k"] = "2db0776dff46e5d80f46d544fe5ae2e6a791e453266180356e74406fa74822d8",
        ["cblk-16x16.j2k"] = "91596151878934575261bba5ab7d1e9df4161dad0c7db8427a44386b33df80f0",
        ["cblk-4x4.j2k"] = "45103f198c583a7d48c123a080bdec44e43cc628a8574fc2ca8dae15eb809009",
        ["cblk-64x8.j2k"] = "b3b27e7f9c13183855700e98ef392b45477970129e7a2bba05e797245a1d3fd4",
        ["gray-1res.j2k"] = "51147ddc3bf51ef758a6494b167adcc8f551cc0ebfd6eec33691b6419d24e2d0",
        ["gray-3res.j2k"] = "d97ca273d12c9c303b47b93c65756fa8764f61847da932af3251a52b279c4e52",
        ["gray16.j2k"] = "c5c24dc860e54ddd984071dc70a4fc74e94d4662e01ff5ac46bdc03a88e8dc27",
        ["lossy-16bit.j2k"] = "2efce1c8717753bcbb1945c6c6d6fb811f8f648706d8119311de62b0b9d5c03f",
        ["lossy-gray.j2k"] = "03dd95fcc2838a78f0611e8124d497b33af538425d73ee3a62063fd99494894d",
        ["lossy-ict.j2k"] = "d54b4664eb60d39b1c5298bf6e8870894fbaebc7262901b75744af5225ac6532",
        ["lossy-layers-tiles.j2k"] = "1fa4336677eae9cbb1280c62e952ee0b21ed416672718a4f72f4b068a30a0650",
        ["lossy-modes.j2k"] = "9ec1271810606ba1e58f7b193cdfc6837b1ec3d00ce939992f2f7a85e014e3e2",
        ["mode-all-modes.j2k"] = "223a1a00c4d272f825948de802c84c2c97ec0ac6a66d0f0f8a79160d9c33c09a",
        ["mode-bypass-causal-reset.j2k"] = "58611a26037bd50644aed18357dc932742f24488e5e3886132adddbc9d5e7fbb",
        ["mode-bypass-termall.j2k"] = "170e60823e5a9b7de417ad6e392979563988bbad98d5fdb539fb5df8cbc69c39",
        ["mode-bypass.j2k"] = "65abaabb94f7e75971cf365bda3705bb486a9261afc64ac6a9e26ce7d1027ef4",
        ["mode-causal.j2k"] = "16a152fb3adfd8d8c76a7c577ce3fe4596a8f147be849d526fbbb103373673b6",
        ["mode-pterm.j2k"] = "a84451badfe03bb84046647babcb0c20a5f6685c6e1aefca0e02346d4b55726b",
        ["mode-reset.j2k"] = "5520183760068f34b0fdba654f9fede251aa98883db7c035aeeafe5fb63022f0",
        ["mode-segsym.j2k"] = "e7d96a491d6acf79ce0fc936038496800e6df6fae36284f7b19a6cb9e4ffe8c7",
        ["mode-termall.j2k"] = "451a4dafc8c41d5ce2e4c86608d1a5723f31c5f8d45e82443f8737199747218b",
        ["odd-1x1.j2k"] = "40f6916b78ce91e89f740a14cdc77ab0e69292daefb8e06abe4b67637ccc30c5",
        ["offset.j2k"] = "2b60ba01b1e4e067c2ded008807aa23918de2b7fbe7334afeb76f1ba55154be7",
        ["plt-tlm.j2k"] = "32cf7af52155d1f26095c816c8d83de75559b0173085f4309fe77fc31f3882a6",
        ["prog-cprl.j2k"] = "7eaca685b4059e02f788ecd3d7990127b5f60cc0a92e492bbaff7c41d0bbcc7a",
        ["prog-lrcp.j2k"] = "7eaca685b4059e02f788ecd3d7990127b5f60cc0a92e492bbaff7c41d0bbcc7a",
        ["prog-pcrl.j2k"] = "7eaca685b4059e02f788ecd3d7990127b5f60cc0a92e492bbaff7c41d0bbcc7a",
        ["prog-rlcp.j2k"] = "7eaca685b4059e02f788ecd3d7990127b5f60cc0a92e492bbaff7c41d0bbcc7a",
        ["prog-rpcl.j2k"] = "7eaca685b4059e02f788ecd3d7990127b5f60cc0a92e492bbaff7c41d0bbcc7a",
        ["rgb-default.j2k"] = "9e77976935095346e97677067578e3cfeced4c154553015bee198f224d2d1237",
        ["rgb16.j2k"] = "78404f2217a0ae03b5fffeaec7938f9f3762d182fbbaac3d054f6a7c5ea5d160",
        ["sop-eph.j2k"] = "143c3111b039d91f80cba535eab77317dea90f777c8c913e59c2b4c86b50436a",
        ["tiles-cprl.j2k"] = "a9d56f685d00f3369f3084aaf9f43c90c97fd6180bf6677b4a3946fa839f0017",
        ["tiles-pcrl.j2k"] = "a9d56f685d00f3369f3084aaf9f43c90c97fd6180bf6677b4a3946fa839f0017",
        ["tiles-rpcl-prec.j2k"] = "703d7af15234fc4ce02ac36f9cd4e6ca22dd5b9630fcfd82c050db1bcfe9eeaf",
    };

    [Fact]
    public void OutputIsIdenticalAtEveryDegreeAndMatchesThePinnedHashes()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(JpxTestStreams.GenDir, "manifest.json")));
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement e in doc.RootElement.EnumerateArray())
        {
            string file = e.GetProperty("file").GetString()!;
            byte[] data = JpxTestStreams.Gen(file);
            string serial = JpxTestStreams.Sha256(Interleave(JpxCodec.Decode(data, new JpxDecodeOptions { MaxDegreeOfParallelism = 1 })));
            string parallel = JpxTestStreams.Sha256(Interleave(JpxCodec.Decode(data, new JpxDecodeOptions { MaxDegreeOfParallelism = 8 })));
            Assert.True(serial == parallel, $"{file}: degree 1 and degree 8 differ");
            actual[file] = serial;
        }

        if (Hashes.Count == 0)
        {
            Assert.Fail(string.Join(Environment.NewLine, actual.Select(kv => $"        [\"{kv.Key}\"] = \"{kv.Value}\",")));
        }

        foreach ((string file, string hash) in actual)
        {
            Assert.True(Hashes.TryGetValue(file, out string? want) && want == hash, $"{file}: {hash}");
        }

        Assert.Equal(Hashes.Count, actual.Count);
    }

    private static byte[] Interleave(JpxImage image)
    {
        int count = image.Width * image.Height;
        var raw = new byte[count * image.ChannelCount * 2];
        for (int c = 0; c < image.ChannelCount; c++)
        {
            for (int i = 0; i < count; i++)
            {
                int o = ((i * image.ChannelCount) + c) * 2;
                raw[o] = (byte)image.Planes[c][i];
                raw[o + 1] = (byte)(image.Planes[c][i] >> 8);
            }
        }

        return raw;
    }
}
