using System.Reflection;

namespace OneImage.Metafile.Tests;

/// <summary>
/// A deterministic mutation smoke test: damaged copies of real GDI+ recordings must parse to a command stream or fail with the typed
/// exceptions, never with anything else and never by running away. Set ONEIMAGE_MUTATIONS to raise the number of mutations per file.
/// </summary>
public class EmfPlusMutationTests
{
    private static string Dir()
    {
        string root = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "RepoRoot").Value!;
        return Path.GetFullPath(Path.Combine(root, "tests", "testdata", "metafile", "oracle-gdiplus"));
    }

    public static TheoryData<string> Files() => [.. Directory.GetFiles(Dir(), "g_*_plusonly.emf").Select(f => Path.GetFileName(f)).Order()];

    [Theory]
    [MemberData(nameof(Files))]
    public void DamagedRecordingsParseOrFailWithTypedExceptions(string file)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(Dir(), file));
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("ONEIMAGE_MUTATIONS"), out int n) ? n : 150;
        var rng = new Random(file.GetHashCode(StringComparison.Ordinal) ^ 12345);
        var limits = new MetafileLimits { MaxCommands = 200_000, MaxWork = 50_000_000 };
        for (int i = 0; i < rounds; i++)
        {
            byte[] m = (byte[])original.Clone();
            int edits = 1 + rng.Next(6);
            for (int e = 0; e < edits; e++)
            {
                int at = 108 + rng.Next(m.Length - 108);
                switch (rng.Next(3))
                {
                    case 0: m[at] = (byte)rng.Next(256); break;
                    case 1: m[at] ^= (byte)(1 << rng.Next(8)); break;
                    default:
                        if (at + 4 < m.Length)
                        {
                            BitConverter.TryWriteBytes(m.AsSpan(at), rng.Next(2) == 0 ? int.MaxValue : -1);
                        }

                        break;
                }
            }

            try
            {
                MetafileDocument doc = MetafileDocument.Parse(m, new MetafileReadOptions { Limits = limits });
                Assert.NotNull(doc.Commands);
            }
            catch (ImageFormatException)
            {
            }
            catch (ImageLimitException)
            {
            }
        }
    }
}
