// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Narrows a native-width Swift <c>Int</c>/<c>UInt</c> value onto the fixed 32-bit type the
/// generated property surface exposes, reporting the values that do not fit instead of dropping
/// their high bits.
/// </summary>
/// <remarks>
/// <para>
/// Swift's <c>Int</c> and <c>UInt</c> are pointer-width, so they cross the boundary as
/// <c>nint</c>/<c>nuint</c>. Generated properties present them as <c>int</c>/<c>uint</c>, which is
/// what C# code around them is normally written against; on a 64-bit target that projection is
/// lossy for the values above <c>Int32</c>/<c>UInt32</c> range. Routing the conversion through
/// these methods turns that loss into an <see cref="OverflowException"/> naming the member and the
/// companion accessor that reads the same storage at full width, rather than a wrapped value the
/// caller has no way to notice.
/// </para>
/// <para>
/// A plain <c>checked</c> cast would raise the same exception type; the reason this indirection
/// exists is the message, which is the only place a caller learns that a lossless read is
/// available and what it is called.
/// </para>
/// </remarks>
public static class NativeIntegerNarrowing
{
    /// <summary>
    /// Converts a native-width signed value to <see cref="int"/>.
    /// </summary>
    /// <param name="value">The native-width value read from Swift.</param>
    /// <param name="memberName">The C# member the value was read through, used in the message.</param>
    /// <param name="nativeMemberName">
    /// The companion accessor that returns the value at native width, or <see langword="null"/>
    /// when the member has none.
    /// </param>
    /// <returns>The value, when it is representable as an <see cref="int"/>.</returns>
    /// <exception cref="OverflowException">The value does not fit in an <see cref="int"/>.</exception>
    public static int ToInt32(nint value, string memberName, string? nativeMemberName = null)
    {
        if (value < int.MinValue || value > int.MaxValue)
            throw new OverflowException(BuildMessage(memberName, value.ToString(), "int", nativeMemberName));
        return (int)value;
    }

    /// <summary>
    /// Converts an optional native-width signed value to <see cref="int"/>, passing
    /// <see langword="null"/> through unchanged.
    /// </summary>
    /// <param name="value">The native-width value read from Swift, or <see langword="null"/>.</param>
    /// <param name="memberName">The C# member the value was read through, used in the message.</param>
    /// <param name="nativeMemberName">The companion accessor that returns the value at native width.</param>
    /// <returns>The value, when it is representable as an <see cref="int"/>.</returns>
    /// <exception cref="OverflowException">The value does not fit in an <see cref="int"/>.</exception>
    public static int? ToInt32(nint? value, string memberName, string? nativeMemberName = null)
        => value is null ? null : ToInt32(value.Value, memberName, nativeMemberName);

    /// <summary>
    /// Converts a native-width unsigned value to <see cref="uint"/>.
    /// </summary>
    /// <param name="value">The native-width value read from Swift.</param>
    /// <param name="memberName">The C# member the value was read through, used in the message.</param>
    /// <param name="nativeMemberName">The companion accessor that returns the value at native width.</param>
    /// <returns>The value, when it is representable as a <see cref="uint"/>.</returns>
    /// <exception cref="OverflowException">The value does not fit in a <see cref="uint"/>.</exception>
    public static uint ToUInt32(nuint value, string memberName, string? nativeMemberName = null)
    {
        if (value > uint.MaxValue)
            throw new OverflowException(BuildMessage(memberName, value.ToString(), "uint", nativeMemberName));
        return (uint)value;
    }

    /// <summary>
    /// Converts an optional native-width unsigned value to <see cref="uint"/>, passing
    /// <see langword="null"/> through unchanged.
    /// </summary>
    /// <param name="value">The native-width value read from Swift, or <see langword="null"/>.</param>
    /// <param name="memberName">The C# member the value was read through, used in the message.</param>
    /// <param name="nativeMemberName">The companion accessor that returns the value at native width.</param>
    /// <returns>The value, when it is representable as a <see cref="uint"/>.</returns>
    /// <exception cref="OverflowException">The value does not fit in a <see cref="uint"/>.</exception>
    public static uint? ToUInt32(nuint? value, string memberName, string? nativeMemberName = null)
        => value is null ? null : ToUInt32(value.Value, memberName, nativeMemberName);

    private static string BuildMessage(string memberName, string value, string targetType, string? nativeMemberName)
    {
        var suffix = nativeMemberName is { Length: > 0 }
            ? $" Read '{nativeMemberName}' for the value at its native width."
            : " The Swift declaration's type is pointer-width, so this member cannot represent every value it can hold.";
        return $"'{memberName}' holds {value}, which is outside the range of '{targetType}'.{suffix}";
    }
}
