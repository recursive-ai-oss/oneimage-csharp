using System.Buffers;

namespace OneImage;

/// <summary>
/// The array pool the library rents scratch buffers from, one per element type. It is the shared pool; the indirection exists so
/// that tests can substitute a pool that poisons returned arrays and reports writes after a return and double returns.
/// </summary>
internal static class Pools<T>
{
    /// <summary>Gets or sets the pool; only tests replace it.</summary>
    public static ArrayPool<T> Pool { get; set; } = ArrayPool<T>.Shared;
}
