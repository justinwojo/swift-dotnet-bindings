// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Swift;
using Swift.Runtime;
using Xunit;

namespace LibraryTests;

/// <summary>
/// Coverage for <see cref="SwiftHashable.GetHashCode{T}"/>, which is the runtime's
/// only caller of Swift's <c>Hashable.hashValue</c> getter. That entry point is a
/// protocol DISPATCH THUNK (<c>$sSH9hashValueSivgTj</c>), not a concrete function:
/// it loads the requirement's function pointer out of the witness table it is handed
/// and branches to it. So a register-placement mistake there does not produce a wrong
/// hash — it produces a garbage function pointer that the thunk jumps into. Nothing
/// exercised this path before; it now runs through the C swiftcall shim
/// <c>SBW_Hashable_HashValue</c> like every other untyped-<c>SwiftSelf</c> call.
///
/// <see cref="SwiftString"/> is the deliberate choice of key: it is a non-trivial
/// <c>Hashable</c> — heap-backed, reference-counted storage rather than a POD scalar —
/// so its witness genuinely reads through <c>self</c> instead of returning a value the
/// caller already had.
/// </summary>
public class SwiftHashableTests
{
    /// <summary>
    /// Reads the marshalled Swift payload of a live <see cref="SwiftString"/> without
    /// taking ownership of anything: the handle points at the instance's own storage,
    /// and this only borrows the bytes to compare them.
    /// </summary>
    private static unsafe byte[] PayloadBytes(SwiftString s)
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<SwiftString>();
        var handle = ((ISwiftObject)s).SwiftHandle;
        var bytes = new byte[(int)metadata.Size];
        new ReadOnlySpan<byte>((void*)handle, bytes.Length).CopyTo(bytes);
        return bytes;
    }

    /// <summary>
    /// Two independently constructed strings with equal content must hash equally.
    /// The string is long enough to defeat Swift's small-string optimisation, so the
    /// two instances hold different storage pointers and therefore different marshalled
    /// bytes — the test asserts that too. That makes this a discriminating check rather
    /// than a tautology: the byte-structural fallback inside
    /// <see cref="SwiftHashable.GetHashCode{T}"/> hashes exactly those differing bytes
    /// and could not return equal values here. Only the Hashable witness can.
    /// </summary>
    [Fact]
    public void GetHashCode_AgreesForContentEqualStrings_WhoseMarshalledBytesDiffer()
    {
        const string content = "a non-trivial Hashable key, comfortably heap-backed";
        using var a = new SwiftString(content);
        using var b = new SwiftString(content);

        Assert.NotEqual(PayloadBytes(a), PayloadBytes(b));
        Assert.Equal(SwiftHashable.GetHashCode(a), SwiftHashable.GetHashCode(b));
    }

    /// <summary>
    /// The witness table the thunk dispatches through must actually resolve for
    /// <see cref="SwiftString"/>. If it did not, <see cref="SwiftHashable.GetHashCode{T}"/>
    /// would silently take its structural-byte fallback and the test above would be
    /// asserting nothing about the thunk.
    /// </summary>
    [Fact]
    public void HashableWitnessTable_ResolvesForSwiftString()
    {
        Assert.True(
            ProtocolWitnessTable.TryGet<SwiftString, ISwiftHashable>(out var pwt) && pwt.HasValue,
            "SwiftString's Hashable witness table must resolve; without it GetHashCode falls back " +
            "to a structural byte hash and never reaches the dispatch thunk.");
    }

    /// <summary>
    /// Repeated calls on the same instance are stable. Swift seeds its hasher per
    /// process, so the VALUE is not predictable across runs — but a thunk that
    /// misdispatched would not reliably return the same number twice, and a receiver
    /// destroyed or mutated by the call would change on the second read.
    /// </summary>
    [Fact]
    public void GetHashCode_IsStableAcrossRepeatedCalls()
    {
        using var s = new SwiftString("stability across repeated dispatch-thunk calls");

        int first = SwiftHashable.GetHashCode(s);
        for (int i = 0; i < 16; i++)
            Assert.Equal(first, SwiftHashable.GetHashCode(s));

        // The receiver is borrowed, not consumed: it is still usable afterwards.
        Assert.Equal("stability across repeated dispatch-thunk calls", s.ToString());
    }

    /// <summary>
    /// Distinct content must reach distinct hashes. This is the half that catches a
    /// thunk which "works" only by returning a constant — a seed, a zero, or the
    /// witness-table pointer itself.
    /// </summary>
    [Fact]
    public void GetHashCode_SeparatesDistinctStrings()
    {
        var hashes = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < 32; i++)
        {
            using var s = new SwiftString($"distinct heap-backed hashable key #{i}");
            hashes.Add(SwiftHashable.GetHashCode(s));
        }

        // 32 distinct 64-bit Swift hashes folded to 32 bits; a collision is possible but
        // vanishingly unlikely, whereas a constant-returning thunk collapses to one.
        Assert.True(hashes.Count >= 30,
            $"Only {hashes.Count} distinct hashes over 32 distinct strings — the hashValue " +
            $"dispatch is not reaching String's witness.");
    }

    /// <summary>
    /// Ties the hash to the collection that depends on it. Swift's own
    /// <c>Dictionary</c> decides key identity by hashing through the same
    /// <c>Hashable</c> conformance this runtime dispatches to by hand, so keys the
    /// dictionary treats as one key must also agree under
    /// <see cref="SwiftHashable.GetHashCode{T}"/> — and keys it keeps apart give the
    /// live collection a second, independent read of the same conformance.
    /// </summary>
    [Fact]
    public void DictionaryKeyIdentity_AgreesWithGetHashCode()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();

        using var first = new SwiftString("a heap-backed dictionary key, written once");
        using var again = new SwiftString("a heap-backed dictionary key, written once");
        using var other = new SwiftString("a different heap-backed dictionary key");

        dict[first] = 1;
        dict[again] = 2;   // same key by Swift's own hashing — overwrites
        dict[other] = 3;

        Assert.Equal(2, dict.Count);
        Assert.Equal((nint)2, dict[first]);
        Assert.Equal((nint)3, dict[other]);

        Assert.Equal(SwiftHashable.GetHashCode(first), SwiftHashable.GetHashCode(again));
        Assert.NotEqual(SwiftHashable.GetHashCode(first), SwiftHashable.GetHashCode(other));
    }

    /// <summary>
    /// The same agreement, read through <see cref="SwiftSet{Element}"/>. The set stores
    /// the element itself rather than a key/value pair, so this also exercises the
    /// hashable conformance on the insert path that <c>SBW_Set_Insert</c> serves.
    /// </summary>
    [Fact]
    public void SetMembership_AgreesWithGetHashCode()
    {
        using var set = new SwiftSet<SwiftString>();

        using var first = new SwiftString("a heap-backed set element, inserted twice");
        using var again = new SwiftString("a heap-backed set element, inserted twice");
        using var other = new SwiftString("a different heap-backed set element");

        set.Add(first);
        set.Add(again);
        set.Add(other);

        Assert.Equal(2, set.Count);
        Assert.Contains(first, set);
        Assert.Contains(again, set);
        Assert.Contains(other, set);

        Assert.Equal(SwiftHashable.GetHashCode(first), SwiftHashable.GetHashCode(again));
        Assert.NotEqual(SwiftHashable.GetHashCode(first), SwiftHashable.GetHashCode(other));
    }
}
