// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Opaque mirror of Swift's <c>_Concurrency.UnownedSerialExecutor</c>. Two pointer-sized
/// fields hold an unowned reference to the executor object plus the executor "witness"
/// (the conformance to <c>SerialExecutor</c>). Treated as a frozen 16-byte value type by the
/// type database — the generator marshals it through the standard frozen-struct pipeline.
/// </summary>
/// <remarks>
/// <para>
/// This struct exists so that the generator can resolve the return type of an actor's
/// implicit <c>unownedExecutor</c> accessor through the type database. Without it, the
/// accessor's return type fails resolution and the property is dropped as
/// <c>SkipReason.UnsupportedType</c>.
/// </para>
/// <para>
/// Consumers should not construct <c>UnownedSerialExecutor</c> directly. The type is
/// reachable solely as runtime metadata that the binding generator consumes when emitting
/// actor-isolated dispatch wrappers; the actor's hop into its own executor is performed in
/// Swift via <c>assumeIsolated</c>, not in C#.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly struct UnownedSerialExecutor : IEquatable<UnownedSerialExecutor>
{
    private readonly IntPtr _executor;
    private readonly IntPtr _witness;

    /// <inheritdoc />
    public bool Equals(UnownedSerialExecutor other) =>
        _executor == other._executor && _witness == other._witness;

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is UnownedSerialExecutor other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_executor, _witness);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(UnownedSerialExecutor left, UnownedSerialExecutor right) =>
        left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(UnownedSerialExecutor left, UnownedSerialExecutor right) =>
        !left.Equals(right);
}
