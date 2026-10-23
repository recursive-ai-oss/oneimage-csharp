namespace OneImage.Psd;

/// <summary>Positioned reads over a seekable stream; reading past the end is a truncated file.</summary>
internal sealed class FileIo
{
    private readonly Stream _stream;

    public FileIo(Stream stream)
    {
        _stream = stream;
        Length = stream.Length;
    }

    public long Length { get; }

    public void ReadAt(long position, Span<byte> destination)
    {
        if (position < 0 || destination.Length > Length - position)
        {
            throw new ImageFormatException("The PSD file is truncated.");
        }

        if (destination.Length == 0)
        {
            return;
        }

        _stream.Position = position;
        int read = _stream.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false);
        if (read < destination.Length)
        {
            throw new ImageFormatException("The PSD file is truncated.");
        }
    }

    /// <summary>Reads <paramref name="count"/> bytes into a new array; the count is checked against the file and <paramref name="max"/> first.</summary>
    public byte[] ReadAt(long position, long count, long max)
    {
        if (count < 0 || position < 0 || count > Length - position)
        {
            throw new ImageFormatException("A section extends past the end of the PSD file.");
        }

        if (count > max)
        {
            throw new ImageLimitException($"A section of {count} bytes exceeds the limit of {max} bytes.");
        }

        byte[] data = new byte[count];
        ReadAt(position, data);
        return data;
    }
}
