// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;

namespace Swift.Runtime;

/// <summary>
/// Reads and writes one simple Swift enum in Swift memory by case identity. A binding registers
/// one for each simple enum whose C# constants are not its in-memory tags
/// (<see cref="SimpleEnumCaseCodecs.Register"/>): its constants count the cases its interface
/// publishes, while the running binary may hold more cases, or other ones, in another order. The
/// codec's functions live in the binding's Swift wrapper and switch on the case, so they are right
/// whatever the binary's layout is.
/// </summary>
/// <remarks>
/// Values are the C# constants widened to <see cref="long"/>, including the unnamed value Swift
/// hands out for a case the binding does not name. Destinations are uninitialized Swift memory of
/// the enum's (or its Optional's) size.
/// </remarks>
public interface ISimpleEnumCaseCodec
{
    /// <summary>The C# constant of the enum value at <paramref name="source"/>.</summary>
    long Load(IntPtr source);

    /// <summary>
    /// Initializes <paramref name="destination"/> with the case <paramref name="value"/> names;
    /// false, writing nothing, when it names none.
    /// </summary>
    bool Store(long value, IntPtr destination);

    /// <summary>
    /// Reads the <c>Optional</c> of the enum at <paramref name="source"/>: false for <c>nil</c>,
    /// otherwise true with the case's C# constant in <paramref name="value"/>.
    /// </summary>
    bool LoadOptional(IntPtr source, out long value);

    /// <summary>
    /// Initializes the <c>Optional</c> of the enum at <paramref name="destination"/>: <c>nil</c>
    /// when <paramref name="hasValue"/> is false, otherwise the case <paramref name="value"/>
    /// names. False, writing nothing, when it names none.
    /// </summary>
    bool StoreOptional(bool hasValue, long value, IntPtr destination);
}

/// <summary>
/// The case codecs of simple enums (<see cref="ISimpleEnumCaseCodec"/>), registered by each
/// binding's module initializer beside the enum's metadata. Every runtime site that moves such an
/// enum through Swift memory (container elements, tuple elements, slots, <see cref="SwiftOptional{T}"/>
/// payloads) asks here first; an enum with no codec keeps reading its in-memory tag as its constant.
/// </summary>
public static class SimpleEnumCaseCodecs
{
    private static readonly ConcurrentDictionary<Type, ISimpleEnumCaseCodec> s_codecs = new();

    /// <summary>Registers the case codec of the C# enum <paramref name="enumType"/>. The first registration wins.</summary>
    public static void Register(Type enumType, ISimpleEnumCaseCodec codec)
    {
        ArgumentNullException.ThrowIfNull(enumType);
        ArgumentNullException.ThrowIfNull(codec);
        if (!enumType.IsEnum)
            throw new ArgumentException($"{enumType} is not an enum.", nameof(enumType));
        s_codecs.TryAdd(enumType, codec);
    }

    /// <summary>The case codec registered for <paramref name="enumType"/>, if any.</summary>
    public static bool TryGet(Type enumType, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISimpleEnumCaseCodec? codec)
        => s_codecs.TryGetValue(enumType, out codec);

    /// <summary>The C# enum value of a constant widened to <see cref="long"/> (truncated to the underlying width).</summary>
    internal static object ToEnum(Type enumType, long value) => Enum.ToObject(enumType, value);

    /// <summary>A C# enum value widened to <see cref="long"/>, as the codec takes it.</summary>
    internal static long ToInt64(object value)
        => Enum.GetUnderlyingType(value.GetType()) == typeof(ulong)
            ? unchecked((long)Convert.ToUInt64(value))
            : Convert.ToInt64(value);

    /// <summary>Initializes <paramref name="destination"/> with <paramref name="value"/>, refusing a value that names no case.</summary>
    internal static void StoreOrThrow(ISimpleEnumCaseCodec codec, object value, IntPtr destination)
    {
        if (!codec.Store(ToInt64(value), destination))
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{value.GetType().Name} value {value} names no case of the Swift enum.");
    }
}
