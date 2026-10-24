using System.Buffers;
using System.Buffers.Binary;

namespace OneImage;

/// <summary>Stream helpers for container parsers: short reads become format errors instead of silent partial data.</summary>
internal static class ContainerIo
{
    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes.</summary>
    /// <exception cref="ImageFormatException">The stream ends first.</exception>
    public static void ReadFully(Stream s, Span<byte> buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int n = s.Read(buffer[done..]);
            if (n <= 0)
            {
                throw new ImageFormatException("The file ends unexpectedly.");
            }

            done += n;
        }
    }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes and returns how many were available.</summary>
    public static int ReadUpTo(Stream s, Span<byte> buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int n = s.Read(buffer[done..]);
            if (n <= 0)
            {
                break;
            }

            done += n;
        }

        return done;
    }

    /// <summary>Reads <paramref name="count"/> bytes into a new array after checking that the stream has that many left.</summary>
    public static byte[] ReadArray(Stream s, long count)
    {
        if (count < 0 || count > s.Length - s.Position)
        {
            throw new ImageFormatException("A size field runs past the end of the file.");
        }

        byte[] data = new byte[count];
        ReadFully(s, data);
        return data;
    }

    /// <summary>Returns a pooled buffer and clears the reference.</summary>
    public static void Return(ref byte[]? buffer)
    {
        byte[]? b = buffer;
        buffer = null;
        if (b != null)
        {
            Pools<byte>.Pool.Return(b);
        }
    }

    public static ushort U16Be(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt16BigEndian(s);

    public static uint U32Be(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt32BigEndian(s);
}
