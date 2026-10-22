using OneImage.Text.Fonts;

namespace OneImage.Text;

/// <summary>
/// A set of loaded font families. Fonts are added from a path, a stream or memory; a TrueType Collection adds every face it holds.
/// Faces with the same family name join one <see cref="FontFamily"/>, one face per style. Thread-safe. Nothing is loaded from
/// the system unless you add it.
/// </summary>
public sealed class FontCollection : IReadOnlyCollection<FontFamily>
{
    /// <summary>The largest font file that is read, in bytes.</summary>
    public const int MaxFileBytes = 256 << 20;

    private readonly object _gate = new();
    private readonly Dictionary<string, FontFamily> _families = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the number of families added so far.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _families.Count;
            }
        }
    }

    /// <summary>Enumerates a snapshot of the families, in the order their names were first seen.</summary>
    /// <returns>An enumerator over the families.</returns>
    public IEnumerator<FontFamily> GetEnumerator()
    {
        FontFamily[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _families.Values];
        }

        return ((IEnumerable<FontFamily>)snapshot).GetEnumerator();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Adds the first (or only) face of a font file: TrueType, OpenType with TrueType or CFF outlines, or WOFF 1.0.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The family the face belongs to.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="FontFormatException">The file is not a usable font, or is larger than <see cref="MaxFileBytes"/>.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public FontFamily Add(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return AddCollection(path)[0];
    }

    /// <summary>Adds the first (or only) face of a font read from a stream, from its current position to its end.</summary>
    /// <param name="stream">The stream; it is left open.</param>
    /// <returns>The family the face belongs to.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="FontFormatException">The data is not a usable font, or is larger than <see cref="MaxFileBytes"/>.</exception>
    public FontFamily Add(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return AddCollection(stream)[0];
    }

    /// <summary>Adds the first (or only) face of a font held in memory. The bytes are copied.</summary>
    /// <param name="data">The font file.</param>
    /// <returns>The family the face belongs to.</returns>
    /// <exception cref="FontFormatException">The data is not a usable font, or is larger than <see cref="MaxFileBytes"/>.</exception>
    public FontFamily Add(ReadOnlyMemory<byte> data) => AddCollection(data)[0];

    /// <summary>Adds every face of a font file, which may be a TrueType Collection (.ttc) or a single font.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The family of each face, in file order (repeats when faces share a family).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="FontFormatException">The file is not a usable font, or is larger than <see cref="MaxFileBytes"/>.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public IReadOnlyList<FontFamily> AddCollection(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream fs = File.OpenRead(path);
        return AddCollection(fs);
    }

    /// <summary>Adds every face of a font read from a stream; see <see cref="AddCollection(string)"/>.</summary>
    /// <param name="stream">The stream, read from its current position to its end; it is left open.</param>
    /// <returns>The family of each face, in file order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="FontFormatException">The data is not a usable font, or is larger than <see cref="MaxFileBytes"/>.</exception>
    public IReadOnlyList<FontFamily> AddCollection(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + n > MaxFileBytes)
            {
                throw new FontFormatException("The font file is larger than the supported maximum.");
            }

            ms.Write(buffer, 0, n);
        }

        return AddParsed(ms.ToArray());
    }

    /// <summary>Adds every face of a font held in memory. The bytes are copied.</summary>
    /// <param name="data">The font file.</param>
    /// <returns>The family of each face, in file order.</returns>
    /// <exception cref="FontFormatException">The data is not a usable font, or is larger than <see cref="MaxFileBytes"/>.</exception>
    public IReadOnlyList<FontFamily> AddCollection(ReadOnlyMemory<byte> data)
    {
        if (data.Length > MaxFileBytes)
        {
            throw new FontFormatException("The font file is larger than the supported maximum.");
        }

        return AddParsed(data.ToArray());
    }

    /// <summary>Looks up a family by name, ignoring case.</summary>
    /// <param name="name">The family name.</param>
    /// <param name="family">The family when found.</param>
    /// <returns><see langword="true"/> when the collection has it.</returns>
    public bool TryGet(string name, out FontFamily family)
    {
        lock (_gate)
        {
            if (name is not null && _families.TryGetValue(name, out FontFamily? f))
            {
                family = f;
                return true;
            }
        }

        family = null!;
        return false;
    }

    /// <summary>Looks up a family by name, ignoring case.</summary>
    /// <param name="name">The family name.</param>
    /// <returns>The family.</returns>
    /// <exception cref="ArgumentException">The collection has no such family.</exception>
    public FontFamily Get(string name) =>
        TryGet(name, out FontFamily? f) ? f : throw new ArgumentException($"The font collection has no family named '{name}'.", nameof(name));

    private List<FontFamily> AddParsed(byte[] data)
    {
        List<FontFace> faces = FontFile.Parse(data);
        var result = new List<FontFamily>(faces.Count);
        lock (_gate)
        {
            foreach (FontFace face in faces)
            {
                var entry = new FaceEntry(face);
                if (!_families.TryGetValue(face.FamilyName, out FontFamily? family))
                {
                    family = new FontFamily(face.FamilyName);
                    _families[face.FamilyName] = family;
                }

                family.Add(entry);
                result.Add(family);
            }
        }

        return result;
    }
}
