using OneImage.Text.Fonts;

namespace OneImage.Text;

/// <summary>
/// The fonts installed in a set of directories, found without loading them: a lookup reads only each file's name table to learn its
/// family, and a family's files are parsed in full the first time that family is asked for. Thread-safe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cost.</b> The first call that needs names (<see cref="FamilyNames"/>, <see cref="TryGet"/>, <see cref="Get"/>) scans the
/// directories once: it lists the files and, for each <c>.ttf</c>, <c>.otf</c>, <c>.ttc</c> and <c>.otc</c> file, opens it and reads the table
/// directory and the <c>name</c> table (a few kilobytes), never the glyph data. That is a few hundred microseconds per file on a warm
/// disk cache, so a machine with a thousand fonts pays a fraction of a second once. The result is cached for the life of the
/// collection; fonts installed later are not noticed. WOFF files (compressed tables) are not looked at.
/// </para>
/// <para>
/// Scanning is bounded: directories are searched to a depth of <see cref="MaxDepth"/>, at most <see cref="MaxFiles"/> files are looked
/// at, and anything unreadable or malformed is skipped without an error.
/// </para>
/// </remarks>
public sealed class SystemFontSet
{
    /// <summary>The deepest directory level searched below each root.</summary>
    public const int MaxDepth = 8;

    /// <summary>The most font files looked at in one collection.</summary>
    public const int MaxFiles = 100_000;

    private const int MaxNameTableBytes = 1 << 20;

    private readonly object _gate = new();
    private readonly string[] _directories;
    private readonly FontCollection _loaded = new();
    private readonly HashSet<string> _loadedFiles = new(StringComparer.Ordinal);

    // Files parsed in full so far; the name scan never raises it.
    internal int LoadedFileCount
    {
        get
        {
            lock (_gate)
            {
                return _loadedFiles.Count;
            }
        }
    }

    private Dictionary<string, List<string>>? _index;
    private string[]? _names;

    /// <summary>Creates a collection over the standard font directories of the current operating system; see <see cref="GetStandardDirectories"/>.</summary>
    public SystemFontSet()
        : this(GetStandardDirectories())
    {
    }

    /// <summary>Creates a collection over the given directories (searched recursively). Nothing is read until a lookup needs it.</summary>
    /// <param name="directories">The directories; missing ones are ignored.</param>
    /// <exception cref="ArgumentNullException"><paramref name="directories"/> is null.</exception>
    public SystemFontSet(IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        _directories = [.. directories.Where(d => !string.IsNullOrEmpty(d))];
    }

    /// <summary>Gets the directories this collection searches.</summary>
    public IReadOnlyList<string> Directories => _directories;

    /// <summary>
    /// Gets the names of all families found, sorted, without loading any font (see the cost note above). A family is named as
    /// <see cref="FontFamily.Name"/> names it once loaded.
    /// </summary>
    public IReadOnlyList<string> FamilyNames
    {
        get
        {
            lock (_gate)
            {
                EnsureIndex();
                return _names!;
            }
        }
    }

    /// <summary>Gets the standard font directories of the current operating system, whether or not they exist.</summary>
    /// <remarks>
    /// Windows: the system fonts folder and the per-user one under <c>%LOCALAPPDATA%\Microsoft\Windows\Fonts</c>.
    /// macOS: <c>/System/Library/Fonts</c>, <c>/Library/Fonts</c>, <c>/Network/Library/Fonts</c> and <c>~/Library/Fonts</c>.
    /// Other systems: <c>/usr/share/fonts</c>, <c>/usr/local/share/fonts</c>, <c>~/.fonts</c>, <c>$XDG_DATA_HOME/fonts</c> (default <c>~/.local/share/fonts</c>) and <c>fonts</c> under each <c>$XDG_DATA_DIRS</c> entry.
    /// </remarks>
    /// <returns>The directories, most specific last.</returns>
    public static IReadOnlyList<string> GetStandardDirectories()
    {
        var dirs = new List<string>();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            AddIfSet(dirs, Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.Length > 0)
            {
                dirs.Add(System.IO.Path.Combine(local, "Microsoft", "Windows", "Fonts"));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            dirs.Add("/System/Library/Fonts");
            dirs.Add("/Library/Fonts");
            dirs.Add("/Network/Library/Fonts");
            if (home.Length > 0)
            {
                dirs.Add(System.IO.Path.Combine(home, "Library", "Fonts"));
            }
        }
        else
        {
            dirs.Add("/usr/share/fonts");
            dirs.Add("/usr/local/share/fonts");
            string? xdgDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
            if (!string.IsNullOrEmpty(xdgDirs))
            {
                foreach (string d in xdgDirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (System.IO.Path.IsPathRooted(d))
                    {
                        dirs.Add(System.IO.Path.Combine(d, "fonts"));
                    }
                }
            }

            if (home.Length > 0)
            {
                dirs.Add(System.IO.Path.Combine(home, ".fonts"));
            }

            string? xdgHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrEmpty(xdgHome) && System.IO.Path.IsPathRooted(xdgHome))
            {
                dirs.Add(System.IO.Path.Combine(xdgHome, "fonts"));
            }
            else if (home.Length > 0)
            {
                dirs.Add(System.IO.Path.Combine(home, ".local", "share", "fonts"));
            }
        }

        return [.. dirs.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Looks up an installed family by name, ignoring case, loading its font files on the first request.</summary>
    /// <param name="name">The family name.</param>
    /// <param name="family">The family when found.</param>
    /// <returns><see langword="true"/> when a font file of that family could be loaded.</returns>
    public bool TryGet(string name, out FontFamily family)
    {
        family = null!;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        lock (_gate)
        {
            EnsureIndex();
            if (!_index!.TryGetValue(name, out List<string>? files))
            {
                return false;
            }

            foreach (string file in files)
            {
                if (!_loadedFiles.Add(file))
                {
                    continue;
                }

                try
                {
                    _loaded.AddCollection(file);
                }
                catch (FontFormatException)
                {
                    // An installed file that does not parse is skipped; the other files of the family may still load.
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return _loaded.TryGet(name, out family);
        }
    }

    /// <summary>Looks up an installed family by name, ignoring case.</summary>
    /// <param name="name">The family name.</param>
    /// <returns>The family.</returns>
    /// <exception cref="ArgumentException">No installed font has that family name.</exception>
    public FontFamily Get(string name) =>
        TryGet(name, out FontFamily? f) ? f : throw new ArgumentException($"No installed font family is named '{name}'.", nameof(name));

    /// <summary>Creates a font of an installed family.</summary>
    /// <param name="name">The family name.</param>
    /// <param name="size">The size in points.</param>
    /// <param name="style">The style; the closest face is used when the family lacks it.</param>
    /// <returns>The font.</returns>
    /// <exception cref="ArgumentException">No installed font has that family name.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is not a positive finite number.</exception>
    public Font CreateFont(string name, float size, FontStyle style = FontStyle.Regular) => Get(name).CreateFont(size, style);

    private static void AddIfSet(List<string> list, string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            list.Add(value);
        }
    }

    private void EnsureIndex()
    {
        if (_index is not null)
        {
            return;
        }

        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = MaxDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var families = new List<string>(4);
        int looked = 0;
        foreach (string dir in _directories)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*", options);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            try
            {
                foreach (string file in files)
                {
                    if (!IsFontFile(file) || !seen.Add(file))
                    {
                        continue;
                    }

                    if (++looked > MaxFiles)
                    {
                        break;
                    }

                    families.Clear();
                    if (!TryReadFamilies(file, families))
                    {
                        continue;
                    }

                    foreach (string family in families)
                    {
                        if (!index.TryGetValue(family, out List<string>? list))
                        {
                            index[family] = list = [];
                        }

                        if (!list.Contains(file))
                        {
                            list.Add(file);
                        }
                    }
                }
            }
            catch (IOException)
            {
                // A directory that disappears or cannot be read mid-scan ends that root's listing.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        string[] names = [.. index.Keys];
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        _names = names;
        _index = index;
    }

    private static bool IsFontFile(string path)
    {
        ReadOnlySpan<char> ext = System.IO.Path.GetExtension(path.AsSpan());
        return ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".otf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase) || ext.Equals(".otc", StringComparison.OrdinalIgnoreCase);
    }

    // Reads the family name of every face in an sfnt or TrueType Collection file: header, table directory and name table only.
    // Anything out of range or unreadable makes the file count as "not a font".
    private static bool TryReadFamilies(string path, List<string> families)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.None);
            return TryReadFamilies(fs, families);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryReadFamilies(Stream fs, List<string> families)
    {
        try
        {
            long length = fs.Length;
            if (length < 12 || length > FontCollection.MaxFileBytes)
            {
                return false;
            }

            byte[] head = new byte[12];
            fs.ReadExactly(head);
            if (Be.U32(head, 0) == 0x74746366)
            {
                uint count = Be.U32(head, 8);
                if (count == 0 || count > FontFile.MaxFacesPerCollection || 12L + (4L * count) > length)
                {
                    return false;
                }

                byte[] offsets = new byte[4 * count];
                fs.ReadExactly(offsets);
                for (int i = 0; i < count; i++)
                {
                    ReadFace(fs, length, Be.U32(offsets, 4 * i), families);
                }
            }
            else
            {
                ReadFace(fs, length, 0, families);
            }

            return families.Count > 0;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
        catch (FontFormatException)
        {
            return false;
        }
    }

    private static void ReadFace(Stream fs, long length, uint offset, List<string> families)
    {
        if (offset + 12L > length)
        {
            return;
        }

        fs.Position = offset;
        byte[] head = new byte[12];
        fs.ReadExactly(head);
        uint version = Be.U32(head, 0);
        if (version != 0x00010000 && version != 0x4F54544F && version != 0x74727565)
        {
            return;
        }

        int numTables = Be.U16(head, 4);
        if (numTables == 0 || numTables > FontFile.MaxTablesPerFace || offset + 12L + (16L * numTables) > length)
        {
            return;
        }

        byte[] dir = new byte[16 * numTables];
        fs.ReadExactly(dir);
        uint nameTag = Be.Tag("name");
        for (int i = 0; i < numTables; i++)
        {
            if (Be.U32(dir, 16 * i) != nameTag)
            {
                continue;
            }

            uint tableOffset = Be.U32(dir, (16 * i) + 8);
            uint tableLength = Be.U32(dir, (16 * i) + 12);
            if (tableLength < 6 || tableLength > MaxNameTableBytes || tableOffset + (long)tableLength > length)
            {
                return;
            }

            fs.Position = tableOffset;
            byte[] name = new byte[tableLength];
            fs.ReadExactly(name);
            string? family = NameTable.Get(name, NameTable.TypographicFamily) ?? NameTable.Get(name, NameTable.Family);
            if (!string.IsNullOrWhiteSpace(family) && !families.Contains(family))
            {
                families.Add(family);
            }

            return;
        }
    }
}

/// <summary>
/// The fonts installed on this machine, found by scanning the standard directories (no native calls); see
/// <see cref="SystemFontSet"/> for what is read and when. The scan happens on first use and is shared by the process.
/// </summary>
public static class SystemFonts
{
    private static readonly Lazy<SystemFontSet> Shared = new(() => new SystemFontSet());

    /// <summary>Gets the process-wide collection over <see cref="SystemFontSet.GetStandardDirectories"/>.</summary>
    public static SystemFontSet Installed => Shared.Value;

    /// <summary>Gets the names of the installed families, sorted; see <see cref="SystemFontSet.FamilyNames"/>.</summary>
    public static IReadOnlyList<string> FamilyNames => Installed.FamilyNames;

    /// <summary>Looks up an installed family by name, ignoring case.</summary>
    /// <param name="name">The family name.</param>
    /// <param name="family">The family when found.</param>
    /// <returns><see langword="true"/> when found.</returns>
    public static bool TryGet(string name, out FontFamily family) => Installed.TryGet(name, out family);

    /// <summary>Looks up an installed family by name, ignoring case.</summary>
    /// <param name="name">The family name.</param>
    /// <returns>The family.</returns>
    /// <exception cref="ArgumentException">No installed font has that family name.</exception>
    public static FontFamily Get(string name) => Installed.Get(name);

    /// <summary>Creates a font of an installed family.</summary>
    /// <param name="name">The family name.</param>
    /// <param name="size">The size in points.</param>
    /// <param name="style">The style.</param>
    /// <returns>The font.</returns>
    /// <exception cref="ArgumentException">No installed font has that family name.</exception>
    public static Font CreateFont(string name, float size, FontStyle style = FontStyle.Regular) => Installed.CreateFont(name, size, style);
}
