using System.Globalization;
using OneImage.Text.Unicode;

namespace OneImage.Text.Tests;

/// <summary>The official BidiTest.txt and BidiCharacterTest.txt of the Unicode release the tables come from, run in full.</summary>
public class BidiConformanceTests
{
    private static string DataPath(string file) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "unicode", file);

    private static int[] Cps(string s)
    {
        List<int> r = [];
        for (int i = 0; i < s.Length; i++)
        {
            int cp = char.ConvertToUtf32(s, i);
            if (cp > 0xFFFF)
            {
                i++;
            }

            r.Add(cp);
        }

        return [.. r];
    }

    private static string Reorder(string logical, int para)
    {
        int[] c = Cps(logical);
        byte[] lev = new byte[c.Length];
        Bidi.ResolveLevels(c, para, lev);
        int[] ord = new int[c.Length];
        int m = Bidi.VisualOrder(lev, ord);
        return string.Concat(Enumerable.Range(0, m).Select(i => char.ConvertFromUtf32(c[ord[i]])));
    }

    [Fact]
    public void BasicReordering()
    {
        Assert.Equal("abc", Reorder("abc", 0));
        Assert.Equal("\u05DD\u05D5\u05DC\u05E9", Reorder("\u05E9\u05DC\u05D5\u05DD", 0));
        Assert.Equal("a \u05DD\u05D5\u05DC\u05E9 b", Reorder("a \u05E9\u05DC\u05D5\u05DD b", 0));
        Assert.Equal("123 \u05DD\u05D5\u05DC\u05E9", Reorder("\u05E9\u05DC\u05D5\u05DD 123", -1));
    }

    [Fact]
    public void TablesAreTheExpectedRelease() => Assert.Equal("17.0.0", BidiData.Version);

    [Fact]
    public void BidiCharacterTestPassesInFull()
    {
        int run = 0;
        var failures = new List<string>();
        foreach (string line in File.ReadLines(DataPath("BidiCharacterTest.txt")))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] f = line.Split(';');
            int[] cps = [.. f[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
            int direction = int.Parse(f[1], CultureInfo.InvariantCulture);
            int paraExpected = int.Parse(f[2], CultureInfo.InvariantCulture);
            string[] levelTokens = f[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int[] orderExpected = [.. f[4].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture))];

            byte[] lev = new byte[cps.Length];
            int para = Bidi.ResolveLevels(cps, direction == 2 ? -1 : direction, lev);
            bool ok = para == paraExpected;
            for (int i = 0; ok && i < cps.Length; i++)
            {
                ok = levelTokens[i] == "x" ? lev[i] == Bidi.Removed : lev[i] != Bidi.Removed && lev[i].ToString(CultureInfo.InvariantCulture) == levelTokens[i];
            }

            if (ok)
            {
                int[] ord = new int[cps.Length];
                int m = Bidi.VisualOrder(lev, ord);
                ok = ord.AsSpan(0, m).SequenceEqual(orderExpected);
            }

            run++;
            if (!ok && failures.Count < 5)
            {
                failures.Add(line);
            }
            else if (!ok)
            {
                failures.Add(null!);
            }
        }

        Assert.True(run > 90000, $"only {run} cases ran");
        Assert.True(failures.Count == 0, $"{failures.Count} of {run} failed, e.g.\n{string.Join('\n', failures.Where(x => x is not null))}");
    }

    // One representative code point per Bidi_Class, in the order of BidiClass.
    private static readonly Dictionary<string, int> Representative = new()
    {
        ["L"] = 0x41, ["R"] = 0x5D0, ["AL"] = 0x627, ["EN"] = 0x30, ["ES"] = 0x2B, ["ET"] = 0x24, ["AN"] = 0x660, ["CS"] = 0x2C,
        ["NSM"] = 0x300, ["BN"] = 0xAD, ["B"] = 0x2029, ["S"] = 0x09, ["WS"] = 0x20, ["ON"] = 0x21, ["LRE"] = 0x202A,
        ["LRO"] = 0x202D, ["RLE"] = 0x202B, ["RLO"] = 0x202E, ["PDF"] = 0x202C, ["LRI"] = 0x2066, ["RLI"] = 0x2067,
        ["FSI"] = 0x2068, ["PDI"] = 0x2069,
    };

    [Fact]
    public void RepresentativesHaveTheirClass()
    {
        foreach ((string name, int cp) in Representative)
        {
            Assert.Equal(name, Bidi.GetClass(cp).ToString());
        }
    }

    [Fact]
    public void BidiTestPassesInFull()
    {
        int[] levelsExpected = [];
        int[] orderExpected = [];
        long cases = 0;
        var failures = new List<string>();
        int failed = 0;
        foreach (string raw in File.ReadLines(DataPath("BidiTest.txt")))
        {
            if (raw.Length == 0 || raw[0] == '#')
            {
                continue;
            }

            if (raw.StartsWith("@Levels:", StringComparison.Ordinal))
            {
                levelsExpected = [.. raw[8..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(t => t == "x" ? -1 : int.Parse(t, CultureInfo.InvariantCulture))];
                continue;
            }

            if (raw.StartsWith("@Reorder:", StringComparison.Ordinal))
            {
                orderExpected = [.. raw[9..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(t => int.Parse(t, CultureInfo.InvariantCulture))];
                continue;
            }

            if (raw[0] == '@')
            {
                continue;
            }

            int semi = raw.IndexOf(';', StringComparison.Ordinal);
            int[] cps = [.. raw[..semi].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n => Representative[n])];
            int bits = int.Parse(raw[(semi + 1)..].Trim(), CultureInfo.InvariantCulture);
            byte[] lev = new byte[cps.Length];
            int[] ord = new int[cps.Length];
            foreach ((int bit, int request) in new[] { (1, -1), (2, 0), (4, 1) })
            {
                if ((bits & bit) == 0)
                {
                    continue;
                }

                cases++;
                Bidi.ResolveLevels(cps, request, lev);
                bool ok = lev.Length == levelsExpected.Length;
                for (int i = 0; ok && i < lev.Length; i++)
                {
                    ok = levelsExpected[i] < 0 ? lev[i] == Bidi.Removed : lev[i] == levelsExpected[i];
                }

                if (ok)
                {
                    int m = Bidi.VisualOrder(lev, ord);
                    ok = ord.AsSpan(0, m).SequenceEqual(orderExpected);
                }

                if (!ok && failed++ < 5)
                {
                    failures.Add($"{raw} (request {request})");
                }
            }
        }

        Assert.True(cases > 400000, $"only {cases} cases ran");
        Assert.True(failed == 0, $"{failed} of {cases} failed, e.g.\n{string.Join('\n', failures)}");
    }
}
