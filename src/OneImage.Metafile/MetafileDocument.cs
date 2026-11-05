using System.Buffers.Binary;

namespace OneImage.Metafile;

/// <summary>
/// An interpreted metafile: the picture's header information and the list of drawing commands that reproduce it. Create one with
/// <see cref="Parse"/>, <see cref="Load(Stream, MetafileReadOptions?)"/> or <see cref="Load(string, MetafileReadOptions?)"/>, then walk <see cref="Commands"/>
/// or call <see cref="Play"/>.
/// </summary>
/// <remarks>
/// <para>
/// Interpreting is eager: the whole file is read, every record is turned into commands, and problems are collected as
/// <see cref="DiagnosticCommand"/>s (also listed in <see cref="Diagnostics"/>). A file whose header is wrong throws
/// <see cref="ImageFormatException"/>; damage after the header (a record that overruns the file, an unknown record, a missing object) never throws:
/// the interpreter does what GDI does where that is known (ignore a draw with a missing object, stop at a record whose size is impossible) and says so in a
/// diagnostic. Limits (<see cref="MetafileLimits"/>) throw <see cref="ImageLimitException"/>.
/// </para>
/// <para>
/// Enhanced metafiles (EMF) and Windows metafiles (WMF, with or without the placeable header) are read. A WMF is translated record by record to
/// the equivalent EMF records first (as Windows does), so both share one interpreter; a WMF that embeds an EMF in its comment records is
/// EMF+ (GDI+) records are interpreted (<see cref="MetafileInfo.HasEmfPlus"/>); in a dual file they win and the GDI records that GDI+ itself skips are not played.
/// </para>
/// </remarks>
public sealed class MetafileDocument
{
    private MetafileDocument(MetafileInfo info, IReadOnlyList<DrawingCommand> commands)
    {
        Info = info;
        Commands = commands;
        Diagnostics = [.. commands.OfType<DiagnosticCommand>()];
    }

    /// <summary>Gets what the header says about the picture.</summary>
    public MetafileInfo Info { get; }

    /// <summary>Gets the commands in drawing order; the first is a <see cref="BeginPictureCommand"/>.</summary>
    public IReadOnlyList<DrawingCommand> Commands { get; }

    /// <summary>Gets the diagnostics among the commands, in order.</summary>
    public IReadOnlyList<DiagnosticCommand> Diagnostics { get; }

    /// <summary>Tells whether the bytes start like an enhanced metafile (the header record and its signature).</summary>
    /// <param name="header">At least the first 44 bytes of the file.</param>
    /// <returns><see langword="true"/> when the header record type and the " EMF" signature are present.</returns>
    public static bool IsEmf(ReadOnlySpan<byte> header) =>
        header.Length >= 44 && BinaryPrimitives.ReadUInt32LittleEndian(header) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(header[40..]) == 0x464D4520;

    /// <summary>Tells whether the bytes start like a Windows metafile: the placeable header key, or a standard META_HEADER.</summary>
    /// <param name="header">At least the first 6 bytes of the file.</param>
    /// <returns><see langword="true"/> for a placeable or standard WMF header.</returns>
    public static bool IsWmf(ReadOnlySpan<byte> header) => WmfReader.IsWmf(header);

    /// <summary>Reads only the header of a metafile (EMF or WMF). For a WMF without the placeable header the records are scanned for the window it sets.</summary>
    /// <param name="data">The file bytes (for an EMF the first records are looked at to detect EMF+).</param>
    /// <param name="options">Options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The picture information.</returns>
    /// <exception cref="ImageFormatException">The data is not a metafile.</exception>
    public static MetafileInfo Identify(ReadOnlyMemory<byte> data, MetafileReadOptions? options = null)
    {
        MetafileLimits limits = (options ?? MetafileReadOptions.Default).Limits;
        return !IsEmf(data.Span) && IsWmf(data.Span) ? WmfReader.Identify(data.Span, limits) : EmfInterpreter.ReadInfo(data, limits);
    }

    /// <summary>Interprets a metafile held in memory.</summary>
    /// <param name="data">The file bytes. The commands refer to this memory for bitmaps and comments, so keep it unchanged while they are in use.</param>
    /// <param name="options">Options, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels interpretation of a large file.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ImageFormatException">The data is not a metafile.</exception>
    /// <exception cref="ImageLimitException">A limit of <paramref name="options"/> was exceeded.</exception>
    public static MetafileDocument Parse(ReadOnlyMemory<byte> data, MetafileReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= MetafileReadOptions.Default;
        if (data.Length > options.Limits.MaxFileBytes)
        {
            throw new ImageLimitException($"The metafile is larger than {options.Limits.MaxFileBytes} bytes.");
        }

        if (!IsEmf(data.Span) && IsWmf(data.Span))
        {
            return ParseWmf(data, options, cancellationToken);
        }

        var interpreter = new EmfInterpreter(data, options, cancellationToken);
        List<DrawingCommand> commands = interpreter.Run();
        return new MetafileDocument(interpreter.Info, commands);
    }

    private static MetafileDocument ParseWmf(ReadOnlyMemory<byte> data, MetafileReadOptions options, CancellationToken cancellationToken)
    {
        WmfConverter.Result converted = new WmfConverter(data, options, cancellationToken).Convert(options.PreferEmbeddedEmf);
        List<DrawingCommand> commands;
        MetafileInfo info;
        var notes = new List<DiagnosticCommand>(converted.Notes);
        EmfInterpreter? embedded = null;
        if (converted.EmbeddedEmf is { } emf)
        {
            try
            {
                embedded = new EmfInterpreter(emf, options, cancellationToken);
            }
            catch (ImageFormatException ex)
            {
                notes.Add(new DiagnosticCommand(DiagnosticSeverity.Warning, "The embedded enhanced metafile has a damaged header (" + ex.Message + "); the Windows metafile records are drawn instead.", 0x0626));
            }
        }

        if (embedded is not null)
        {
            commands = embedded.Run();
            info = embedded.Info with { Kind = MetafileKind.Wmf };
            commands[0] = new BeginPictureCommand(info) { RecordIndex = -1 };
        }
        else
        {
            var interpreter = new EmfInterpreter(converted.Emf, options, converted.Info, converted.Map, cancellationToken);
            commands = interpreter.Run();
            info = interpreter.Info;
        }

        commands.AddRange(notes);
        return new MetafileDocument(info, commands);
    }

    /// <summary>Reads a stream to its end and interprets it.</summary>
    /// <param name="stream">The stream, positioned at the start of the file.</param>
    /// <param name="options">Options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ImageFormatException">The data is not a metafile.</exception>
    /// <exception cref="ImageLimitException">A limit of <paramref name="options"/> was exceeded.</exception>
    public static MetafileDocument Load(Stream stream, MetafileReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= MetafileReadOptions.Default;
        return Parse(ReadAll(stream, options.Limits.MaxFileBytes), options);
    }

    /// <summary>Reads a file and interprets it.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="options">Options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ImageFormatException">The data is not a metafile.</exception>
    /// <exception cref="ImageLimitException">A limit of <paramref name="options"/> was exceeded.</exception>
    public static MetafileDocument Load(string path, MetafileReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream fs = File.OpenRead(path);
        return Load(fs, options);
    }

    /// <summary>Sends every command to a visitor, in order.</summary>
    /// <param name="visitor">The visitor.</param>
    public void Play(DrawingCommandVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        foreach (DrawingCommand c in Commands)
        {
            c.Accept(visitor);
        }
    }

    internal static byte[] ReadAll(Stream stream, int max)
    {
        var ms = new MemoryStream();
        byte[] buf = new byte[81920];
        int n;
        while ((n = stream.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length + n > max)
            {
                throw new ImageLimitException($"The metafile is larger than {max} bytes.");
            }

            ms.Write(buf, 0, n);
        }

        return ms.ToArray();
    }
}
