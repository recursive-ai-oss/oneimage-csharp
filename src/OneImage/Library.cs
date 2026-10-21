namespace OneImage;

/// <summary>Identifies the library build. Placeholder until the real API lands.</summary>
public static class Library
{
    /// <summary>Gets the library name, taken from the assembly so a rename needs no code change.</summary>
    public static string Name { get; } = typeof(Library).Assembly.GetName().Name ?? string.Empty;
}
