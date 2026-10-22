using System.Text.RegularExpressions;

namespace OneImage.Tests.Docs;

/// <summary>Runs alone: the quick start uses relative paths, so the test changes the working directory.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkingDirectoryGroup
{
    public const string Name = "WorkingDirectory";
}

/// <summary>
/// The quick start shown in README.md and src/OneImage/README.md, compiled and run, plus checks that the Markdown files
/// the repository and the NuGet page show stay consistent with it: every code line is in this file, links point at
/// files that exist, and the package readme uses only what NuGet renders.
/// </summary>
[Collection(WorkingDirectoryGroup.Name)]
public sealed class ReadmeSamples
{
    // The statements below are the README quick start, line for line. Keep them identical to the Markdown.
    private static void QuickStart()
    {
        // A 400 px wide thumbnail, upright, in sRGB, without EXIF/GPS, written as WebP.
        Image.Thumbnail("in.jpg", 400).Save("thumb.webp");

        // Load, edit, save. Nothing is decoded until Save.
        Image.Load("in.jpg")
            .Resize(width: 1200)
            .Rotate(90)
            .Save("out.png");

        // Format and quality.
        Image.Load("in.png").Save("out.jpg", new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 85 } });
    }

    [Fact]
    public void QuickStartRuns()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("oneimage-readme-");
        string old = Directory.GetCurrentDirectory();
        try
        {
            var px = new Rgb24[2000 * 1000];
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Rgb24((byte)(i % 2000 / 8), (byte)(i / 2000 / 4), 100);
            }

            Image src = Image.FromPixels<Rgb24>(px, 2000, 1000);
            src.Save(Path.Combine(dir.FullName, "in.jpg"));
            src.Save(Path.Combine(dir.FullName, "in.png"));

            Directory.SetCurrentDirectory(dir.FullName);
            QuickStart();

            Assert.Equal(400, Image.Identify("thumb.webp").Width);
            ImageInfo big = Image.Identify("out.png");
            Assert.Equal((1000 * 1200 / 2000, 1200), (big.Width, big.Height)); // 1200x600 turned 90 degrees
            Assert.True(File.Exists("out.jpg"));
        }
        finally
        {
            Directory.SetCurrentDirectory(old);
            dir.Delete(recursive: true);
        }
    }

    private static string Root()
    {
        string? root = typeof(ReadmeSamples).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "RepoRoot")?.Value;
        Assert.NotNull(root);
        return Path.GetFullPath(root);
    }

    private static IEnumerable<string> CodeBlocks(string markdown)
    {
        foreach (Match m in Regex.Matches(markdown, @"```csharp\r?\n(.*?)```", RegexOptions.Singleline))
        {
            yield return m.Groups[1].Value;
        }
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("src/OneImage/README.md")]
    public void ReadmeCodeLinesAreCompiledSamples(string file)
    {
        string root = Root();
        string self = File.ReadAllText(Path.Combine(root, "tests/OneImage.Tests/Docs/ReadmeSamples.cs"));
        var known = new HashSet<string>(self.Split('\n').Select(l => l.Trim()));
        int blocks = 0;
        foreach (string block in CodeBlocks(File.ReadAllText(Path.Combine(root, file))))
        {
            blocks++;
            foreach (string raw in block.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line == "using OneImage;")
                {
                    continue;
                }

                Assert.True(known.Contains(line), $"{file}: line not in ReadmeSamples.QuickStart: {line}");
            }
        }

        Assert.True(blocks > 0, file + " has no code block");
    }

    [Fact]
    public void PackageReadmeHasNoRelativeLinksOrHtml()
    {
        string text = File.ReadAllText(Path.Combine(Root(), "src/OneImage/README.md"));
        Assert.DoesNotMatch(@"<[a-zA-Z/!]", Regex.Replace(text, "```.*?```", "", RegexOptions.Singleline));
        foreach (Match m in Regex.Matches(text, @"\]\(([^)\s]+)"))
        {
            Assert.StartsWith("https://", m.Groups[1].Value);
        }
    }

    [Fact]
    public void RelativeLinksPointAtExistingFiles()
    {
        string root = Root();
        var files = new List<string> { "README.md", "CONTRIBUTING.md", "SECURITY.md", "CODE_OF_CONDUCT.md", "CHANGELOG.md" };
        files.AddRange(Directory.GetFiles(Path.Combine(root, "docs"), "*.md").Select(f => Path.GetRelativePath(root, f)));
        foreach (string file in files)
        {
            string dir = Path.GetDirectoryName(Path.Combine(root, file))!;
            string text = Regex.Replace(File.ReadAllText(Path.Combine(root, file)), "```.*?```", "", RegexOptions.Singleline);
            foreach (Match m in Regex.Matches(text, @"\]\(([^)\s]+)\)"))
            {
                string target = m.Groups[1].Value;
                if (target.Contains("://") || target.StartsWith('#') || target.StartsWith("mailto:", StringComparison.Ordinal))
                {
                    continue;
                }

                string path = Path.GetFullPath(Path.Combine(dir, target.Split('#')[0]));
                Assert.True(File.Exists(path) || Directory.Exists(path), $"{file}: broken link {target}");
            }
        }
    }
}
