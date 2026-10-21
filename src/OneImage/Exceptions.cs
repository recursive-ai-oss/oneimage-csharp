namespace OneImage;

/// <summary>Base type of every error the library raises about image data or image operations.</summary>
public abstract class ImageException : Exception
{
    /// <summary>Initializes the exception.</summary>
    protected ImageException()
    {
    }

    /// <summary>Initializes the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    protected ImageException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception with a message and a cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying error.</param>
    protected ImageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The bytes being decoded are not a valid image of the claimed or detected format.</summary>
public class ImageFormatException : ImageException
{
    /// <summary>Initializes the exception.</summary>
    public ImageFormatException()
    {
    }

    /// <summary>Initializes the exception with a message.</summary>
    /// <param name="message">What is wrong with the data.</param>
    public ImageFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception with a message and a cause.</summary>
    /// <param name="message">What is wrong with the data.</param>
    /// <param name="innerException">The underlying error.</param>
    public ImageFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An <see cref="ImageLimits"/> bound (or a hard engine bound) would be exceeded. Raised before the guarded allocation.</summary>
public class ImageLimitException : ImageException
{
    /// <summary>Initializes the exception.</summary>
    public ImageLimitException()
    {
    }

    /// <summary>Initializes the exception with a message.</summary>
    /// <param name="message">Which limit was exceeded.</param>
    public ImageLimitException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception with a message and a cause.</summary>
    /// <param name="message">Which limit was exceeded.</param>
    /// <param name="innerException">The underlying error.</param>
    public ImageLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An operation cannot be carried out on the given image (unsupported pixel format, missing encoder, misuse of a lock, ...).</summary>
public class ImageOperationException : ImageException
{
    /// <summary>Initializes the exception.</summary>
    public ImageOperationException()
    {
    }

    /// <summary>Initializes the exception with a message.</summary>
    /// <param name="message">Why the operation cannot run.</param>
    public ImageOperationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception with a message and a cause.</summary>
    /// <param name="message">Why the operation cannot run.</param>
    /// <param name="innerException">The underlying error.</param>
    public ImageOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The file an <see cref="Image"/> was loaded from changed (size or modification time) before its pixels were pulled.</summary>
public sealed class ImageSourceChangedException : ImageOperationException
{
    /// <summary>Initializes the exception.</summary>
    public ImageSourceChangedException()
    {
    }

    /// <summary>Initializes the exception with a message.</summary>
    /// <param name="message">Which source changed.</param>
    public ImageSourceChangedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception with a message and a cause.</summary>
    /// <param name="message">Which source changed.</param>
    /// <param name="innerException">The underlying error.</param>
    public ImageSourceChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
