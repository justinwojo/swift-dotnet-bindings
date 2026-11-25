// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Builds equality comparers that compare and hash a projected key the way Swift does: through the
/// <c>Hashable</c> conformance of the Swift key type it crosses as.
/// </summary>
/// <remarks>
/// A binding reads a Swift dictionary or set nested inside another collection into a .NET
/// <see cref="Dictionary{TKey, TValue}"/>, <see cref="HashSet{T}"/> or <see cref="NilKeyDictionary{TKey, TValue}"/>.
/// When the projected key type's own equality is not Swift's (a Swift class whose wrapper sees no
/// equality, because Swift's conformance is conditional on the class's generic arguments, or is the
/// identity of the wrapper), the binding gives the collection one of these comparers, so a key looked
/// up with a fresh, equal object is found as Swift would find it.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SwiftKeyComparer
{
    /// <summary>
    /// A comparer of <typeparamref name="TKey"/> values that converts each to its Swift key carrier
    /// <typeparamref name="TCarrier"/> through <paramref name="toCarrier"/> and compares and hashes
    /// the carriers by <typeparamref name="TCarrier"/>'s <c>Hashable</c> conformance. A
    /// <see langword="null"/> key equals only another <see langword="null"/> key.
    /// </summary>
    /// <param name="toCarrier">Converts a non-null key to the carrier it crosses into Swift as,
    /// borrowing from the key.</param>
    public static IEqualityComparer<TKey> Create<TKey, TCarrier>(Func<TKey, TCarrier> toCarrier)
    {
        ArgumentNullException.ThrowIfNull(toCarrier);
        return new HashedKeyComparer<TKey, TCarrier>(toCarrier);
    }

    private sealed class HashedKeyComparer<TKey, TCarrier> : IEqualityComparer<TKey>
    {
        private readonly Func<TKey, TCarrier> _toCarrier;

        public HashedKeyComparer(Func<TKey, TCarrier> toCarrier) => _toCarrier = toCarrier;

        public bool Equals(TKey? x, TKey? y)
        {
            if (x is null || y is null)
                return x is null && y is null;
            if (ReferenceEquals(x, y))
                return true;
            try
            {
                return SwiftKeyComparerCore.CarriersEqual(_toCarrier(x), _toCarrier(y));
            }
            finally
            {
                // Each carrier borrows from its key.
                GC.KeepAlive(x);
                GC.KeepAlive(y);
            }
        }

        public int GetHashCode(TKey key)
        {
            if (key is null)
                return 0;
            try
            {
                return SwiftKeyComparerCore.CarrierHash(_toCarrier(key));
            }
            finally
            {
                GC.KeepAlive(key);
            }
        }
    }
}

/// <summary>
/// Swift's <c>==</c> and <c>hashValue</c> on carriers of a Swift <c>Hashable</c> type, through the
/// witness table every Swift collection over that type is instantiated with.
/// </summary>
internal static class SwiftKeyComparerCore
{
    /// <summary>Whether Swift's <c>==</c> holds between the two carriers' values.</summary>
    internal static unsafe bool CarriersEqual<TCarrier>(TCarrier lhs, TCarrier rhs)
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<TCarrier>();
        var equatable = EquatableWitness<TCarrier>.Table;
        int size = (int)metadata.Size;
        Span<byte> lhsSpan = size > 0 ? stackalloc byte[size] : Span<byte>.Empty;
        Span<byte> rhsSpan = size > 0 ? stackalloc byte[size] : Span<byte>.Empty;

        // Marshalling initializes each buffer (an owned copy for a wrapper or object carrier), and
        // Swift's `==` borrows both operands, so a buffer is this frame's to destroy once it holds a
        // value. The pointer is published only then, so an operand whose conversion throws unwinds
        // without destroying uninitialized bytes.
        IntPtr lhsPayload = IntPtr.Zero;
        IntPtr rhsPayload = IntPtr.Zero;
        try
        {
            SwiftMarshal.MarshalToSwift(lhs, ref lhsSpan);
            lhsPayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(lhsSpan));
            SwiftMarshal.MarshalToSwift(rhs, ref rhsSpan);
            rhsPayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(rhsSpan));
            return SwiftEquatable.EqualsRaw(lhsPayload, rhsPayload, metadata, equatable);
        }
        finally
        {
            if (rhsPayload != IntPtr.Zero && SwiftMarshal.MarshalsOwnedCopy(rhs))
                metadata.ValueWitnessTable->Destroy((void*)rhsPayload, metadata);
            if (lhsPayload != IntPtr.Zero && SwiftMarshal.MarshalsOwnedCopy(lhs))
                metadata.ValueWitnessTable->Destroy((void*)lhsPayload, metadata);
        }
    }

    /// <summary>Swift's <c>hashValue</c> of the carrier's value, folded to 32 bits.</summary>
    internal static unsafe int CarrierHash<TCarrier>(TCarrier value)
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<TCarrier>();
        var hashable = HashableConformanceRegistry.GetHashableWitnessTable<TCarrier>();
        int size = (int)metadata.Size;
        Span<byte> span = size > 0 ? stackalloc byte[size] : Span<byte>.Empty;
        IntPtr payload = IntPtr.Zero;
        try
        {
            SwiftMarshal.MarshalToSwift(value, ref span);
            payload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));
            // `hashValue` borrows its receiver.
            nint hash = SwiftCollectionCdeclWrappers.HashableHashValue(metadata, hashable, payload);
            return unchecked((int)hash ^ (int)(hash >> 32));
        }
        finally
        {
            if (payload != IntPtr.Zero && SwiftMarshal.MarshalsOwnedCopy(value))
                metadata.ValueWitnessTable->Destroy((void*)payload, metadata);
        }
    }

    /// <summary>
    /// The <c>Equatable</c> witness table of <typeparamref name="TCarrier"/>'s <c>Hashable</c>
    /// conformance: the one Swift's <c>Set</c> and <c>Dictionary</c> compare keys by. A
    /// <c>Hashable</c> witness table holds its conformance descriptor, then its requirements in
    /// declaration order, the first of which is the inherited <c>Equatable</c> conformance. The table
    /// read there is checked to be a conformance to <c>Equatable</c> before it is used.
    /// </summary>
    private static class EquatableWitness<TCarrier>
    {
        internal static readonly ProtocolWitnessTable Table = Resolve();

        private static unsafe ProtocolWitnessTable Resolve()
        {
            var hashable = HashableConformanceRegistry.GetHashableWitnessTable<TCarrier>();
            var equatable = ProtocolWitnessTable.FromHandle(((IntPtr*)hashable.Handle)[1]);
            if (!equatable.IsValid || ConformedProtocol(equatable) != EquatableDescriptor.Handle)
            {
                throw new SwiftRuntimeException(
                    $"The Hashable witness table of {typeof(TCarrier)} does not hold its Equatable conformance where Swift's ABI places it.");
            }
            return equatable;
        }
    }

    private static readonly ProtocolDescriptor EquatableDescriptor =
        ProtocolDescriptor.LoadFromSymbol(KnownLibraries.SwiftCore, "$sSQMp");

    /// <summary>
    /// The protocol descriptor a witness table's conformance names. A witness table's first word is
    /// its conformance descriptor, whose first field is a relative pointer to the protocol
    /// descriptor, indirect (through a pointer-sized slot) when its low bit is set.
    /// </summary>
    private static unsafe IntPtr ConformedProtocol(ProtocolWitnessTable table)
    {
        IntPtr conformance = *(IntPtr*)table.Handle;
        if (conformance == IntPtr.Zero)
            return IntPtr.Zero;
        int offset = *(int*)conformance;
        IntPtr target = conformance + (offset & ~1);
        return (offset & 1) != 0 ? *(IntPtr*)target : target;
    }
}
