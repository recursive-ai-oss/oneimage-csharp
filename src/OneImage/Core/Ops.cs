using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace OneImage;

/// <summary>One named parameter of an operation. Values are limited to <see cref="long"/>, <see cref="double"/>, <see cref="bool"/> and <see cref="string"/> so descriptors serialize in any language.</summary>
/// <param name="Name">Language-neutral lower-case name.</param>
/// <param name="Value">The value.</param>
public readonly record struct OpParameter(string Name, object Value);

/// <summary>A language-neutral description of one graph node: the operation name and its serializable parameters.</summary>
public sealed class OpDescriptor
{
    internal OpDescriptor(string name, params OpParameter[] parameters)
    {
        Name = name;
        Parameters = parameters;
        foreach (OpParameter p in parameters)
        {
            if (p.Value is not (long or double or bool or string))
            {
                throw new ArgumentException($"Parameter '{p.Name}' of op '{name}' must be long, double, bool or string.", nameof(parameters));
            }
        }
    }

    /// <summary>Gets the operation name, for example <c>crop</c> or <c>invert</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the parameters in declaration order.</summary>
    public IReadOnlyList<OpParameter> Parameters { get; }

    /// <summary>Formats the descriptor as <c>name(a=1, b=x)</c> using the invariant culture.</summary>
    /// <returns>The text form.</returns>
    public override string ToString()
    {
        var sb = new StringBuilder(Name).Append('(');
        for (int i = 0; i < Parameters.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(Parameters[i].Name).Append('=').Append(Convert.ToString(Parameters[i].Value, CultureInfo.InvariantCulture));
        }

        return sb.Append(')').ToString();
    }
}

internal enum Backend
{
    Cpu = 0,
}

internal delegate void RowKernel(ReadOnlySpan<byte> source, Span<byte> destination, PixelFormat format);

internal delegate void ConvertRowKernel(ReadOnlySpan<byte> source, PixelFormat sourceFormat, Span<byte> destination, PixelFormat destinationFormat, int width);

/// <summary>Maps (operation name, backend) to kernel delegates. v1 registers CPU kernels only; another backend registers under its own key.</summary>
internal static class KernelRegistry
{
    private static readonly ConcurrentDictionary<(string, Backend), Delegate> s_kernels = Init();

    public static void Register(string op, Backend backend, Delegate kernel) => s_kernels[(op, backend)] = kernel;

    public static T Get<T>(string op, Backend backend = Backend.Cpu)
        where T : Delegate
    {
        if (s_kernels.TryGetValue((op, backend), out Delegate? d) && d is T typed)
        {
            return typed;
        }

        throw new ImageOperationException($"No {backend} kernel registered for '{op}'.");
    }

    private static ConcurrentDictionary<(string, Backend), Delegate> Init()
    {
        var map = new ConcurrentDictionary<(string, Backend), Delegate>();
        map[("invert", Backend.Cpu)] = new RowKernel(Kernels.InvertKernel.Invert);
        map[("convert", Backend.Cpu)] = new ConvertRowKernel(Kernels.ConvertKernel.ConvertRow);
        return map;
    }
}
