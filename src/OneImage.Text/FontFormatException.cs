namespace OneImage.Text;

/// <summary>A font file is malformed, truncated or uses a feature this library does not read.</summary>
public class FontFormatException : ImageFormatException
{
    /// <summary>Initializes the exception.</summary>
    public FontFormatException()
    {
    }

    /// <summary>Initializes the exception with a message.</summary>
    /// <param name="message">What is wrong with the font.</param>
    public FontFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception with a message and a cause.</summary>
    /// <param name="message">What is wrong with the font.</param>
    /// <param name="innerException">The underlying error.</param>
    public FontFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
