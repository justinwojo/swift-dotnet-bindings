// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Unmanaged transport structs for Swift <c>Optional&lt;T&gt;</c> values that are wider than one
/// machine word, used as the parameter and return slot of a <b>direct</b> CallConvSwift P/Invoke —
/// one with no Swift-side wrapper to widen the value into a pointer or an out-buffer.
///
/// <para>Without these, such an Optional is declared as a single <c>IntPtr</c>, which carries only
/// its first word. The bytes past that word are never transferred, and for an extra-inhabitant
/// Optional (one with no separate tag byte) those missing bytes are precisely what decides Some
/// vs None. Neither compiler can see the mismatch.</para>
///
/// <para><b>Every field must be integer-typed, and that is a correctness requirement rather than a
/// style choice.</b> Swift lowers an enum payload as opaque integer storage, so the payload of an
/// <c>Optional&lt;Double&gt;</c> travels in an integer register: Swift's own code for
/// <c>(Double?) -&gt; Double</c> opens with <c>fmov d0, x0</c>, moving the payload out of x0 before
/// using it. .NET's Swift lowering, by contrast, honours declared field types and would place a
/// <c>double</c> field in a floating-point register. A carrier declaring the payload as
/// <c>double</c> therefore reads a register Swift never wrote — it returns nil for a present value
/// and passes zero for a supplied one, silently, on both runtimes.</para>
///
/// <para>These types are transport only. They collect the bytes Swift passes so the value arrives
/// complete; deciding Some vs None from those bytes belongs to the Optional's value-witness table,
/// which is the only ABI-stable reader of an extra-inhabitant tag. Nothing here encodes a spare-bit
/// layout, and callers should not add one.</para>
/// </summary>
/// <remarks>
/// Sizes are chosen so .NET's physical lowering independently reaches the same register-vs-memory
/// decision Swift did. .NET passes an aggregate of at most four lowered elements in registers and
/// spills anything wider to memory; Swift applies the same four-element threshold, so an honestly
/// sized carrier needs no direct-vs-indirect modelling of its own. Both carriers here are two
/// elements and stay well inside Mono's additional 32-byte ceiling for direct aggregates.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SwiftOptionalCarrier9
{
    /// <summary>
    /// The payload word, as raw bits. Integer-typed regardless of whether the Swift payload is an
    /// integer or a floating-point value — see the type-level remarks.
    /// </summary>
    public ulong Word0;

    /// <summary>
    /// Swift's appended enum tag byte. Interpreting it is the value-witness table's job; it is
    /// carried here so the Optional arrives whole.
    /// </summary>
    public byte Tag;
}

/// <summary>
/// Transport for a two-word Optional with no separate tag byte — the extra-inhabitant shape, of
/// which <c>Optional&lt;String&gt;</c> is the case that occurs in practice. Both words are integer
/// registers; see <see cref="SwiftOptionalCarrier9"/> for why that is load-bearing.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SwiftOptionalCarrier16
{
    /// <summary>The first payload word.</summary>
    public ulong Word0;

    /// <summary>
    /// The second payload word. For <c>Optional&lt;String&gt;</c> this is the bridge-object word,
    /// whose null value is the extra inhabitant Swift uses for nil — which is exactly why a
    /// single-word slot cannot represent this shape at all.
    /// </summary>
    public ulong Word1;
}
