// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using Swift.Runtime.InteropServices;

namespace Swift.Runtime
{
    /// <summary>
    /// Provides functionality to use Swift's Hashable protocol for hash code computation.
    /// </summary>
    public static class SwiftHashable
    {
        /// <summary>
        /// Computes the hash code of an object using Swift's Hashable protocol.
        /// Folds the 64-bit Swift hash value to a 32-bit int for .NET GetHashCode().
        /// </summary>
        /// <typeparam name="T">Type that implements ISwiftObject (and ideally ISwiftHashable).</typeparam>
        /// <param name="value">The object to hash</param>
        /// <returns>
        /// A 32-bit hash code derived from Swift's hash value when the type's Hashable witness
        /// table is registered. When the witness table cannot be resolved at runtime even
        /// though the generator opted the type in to Hashable emission (e.g., a registration
        /// hook missed it), the method falls back to identity hash for reference-shaped
        /// projections and a stable structural-byte FNV-1a hash for value-shaped ones.
        /// Returns <c>0</c> only for <c>null</c>.
        /// </returns>
        /// <remarks>
        /// The C# generator only emits this call when the Swift declaration carries an
        /// explicit (or transitively implied) <c>Hashable</c> conformance — never on
        /// Equatable-only types. Inferring Hashable from Equatable is unsafe in general:
        /// Swift's synthesized <c>==</c> compares stored properties semantically, while the
        /// structural-byte fallback hashes the marshalled representation, so equal values
        /// whose fields are non-trivial (e.g., <c>String</c>, <c>Array</c>, class storage)
        /// can hash differently and break the Equals/GetHashCode contract. The fallbacks
        /// remain as defense-in-depth for the rare runtime-registration miss.
        /// </remarks>
        public static unsafe int GetHashCode<T>(T value)
            where T : ISwiftObject
        {
            if (value == null)
                return 0;

            if (!TypeMetadata.TryGetTypeMetadata<T>(out var maybeMetadata) || !maybeMetadata.HasValue)
                return RuntimeHelpers.GetHashCode(value);
            var metadata = maybeMetadata.Value;
            bool isReferenceShaped = IsReferenceShaped(metadata.Kind);

            // For reference-shaped Swift types without a registered Hashable witness, fall back to
            // identity hash on the managed wrapper. Computing a structural hash from the marshalled
            // bytes would just hash the heap pointer (meaningless for reference equality), and the
            // marshalling itself can SIGSEGV on bridge stubs whose SafeHandle was never initialised
            // (e.g. SwiftUI bridge types). Reference-shaped types WITH a witness still go through
            // the witness path below — Equals/GetHashCode for those is value-equality, not identity.
            if (isReferenceShaped && !TryGetHashableWitnessTable<T>(out _))
                return RuntimeHelpers.GetHashCode(value);

            var size = (int)metadata.Size;

            // Marshal the Swift value into a stack buffer once. The Hashable PInvoke and the
            // structural-fallback hash both consume this representation, so we marshal exactly
            // once regardless of which path we end up on.
            Span<byte> span = size > 0 ? stackalloc byte[size] : Span<byte>.Empty;

            // Marshalling initializes the buffer with an owned value, and `hashValue` borrows its
            // receiver — it destroys nothing — so the buffer is this frame's to destroy. The
            // pointer is published only once the buffer holds a value, so a non-null pointer means
            // "live" and a marshal that throws unwinds without destroying uninitialized bytes.
            IntPtr payload = IntPtr.Zero;
            try
            {
                if (size > 0)
                {
                    SwiftMarshal.MarshalToSwift(value, ref span);
                    payload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));
                }

                if (TryGetHashableWitnessTable<T>(out var hashablePwt))
                {
                    // Cdecl-wrapped: `self` reaches the dispatch thunk as an untyped
                    // SwiftSelf, which collides with Mono's GC-safe-region cookie
                    // register; see SwiftCollectionCdeclWrappers.
                    nint h = SwiftCollectionCdeclWrappers.HashableHashValue(
                        metadata,
                        hashablePwt,
                        payload);

                    // Fold 64-bit Swift hash to 32-bit .NET hash
                    return unchecked((int)h ^ (int)(h >> 32));
                }

                // No Hashable witness — synthesize a stable hash from the marshalled Swift bytes.
                // For trivial value types this matches Swift's synthesized Equatable byte-by-byte
                // semantics. The important invariant — Equals(a, b) → GetHashCode(a) == GetHashCode(b)
                // — holds because two byte-equal Swift values produce the same hash.
                return ComputeStructuralHash(span);
            }
            finally
            {
                if (payload != IntPtr.Zero)
                    metadata.ValueWitnessTable->Destroy((void*)payload, metadata);
            }
        }

        private static bool IsReferenceShaped(TypeMetadataKind kind) =>
            kind == TypeMetadataKind.Class
            || kind == TypeMetadataKind.ForeignClass
            || kind == TypeMetadataKind.ForeignReferenceType
            || kind == TypeMetadataKind.ObjCClassWrapper;

        /// <summary>
        /// Resolves the Hashable protocol witness table without throwing on miss. Mirrors
        /// <see cref="ProtocolWitnessTable.GetOrThrowAuto{TType, TProtocol}"/>'s runtime selection
        /// (NativeAOT vs Mono) but converts the throwing path into a boolean result.
        /// </summary>
        private static bool TryGetHashableWitnessTable<T>(out ProtocolWitnessTable result)
            where T : ISwiftObject
        {
            // Pre-registered table is the fast path on both runtimes.
            if (WitnessTableDispatcher.TryGet(typeof(T), typeof(ISwiftHashable), out var cached))
            {
                result = cached;
                return true;
            }

            try
            {
                if (SwiftRuntimeInfo.IsNativeAotRuntime)
                {
                    if (!ProtocolConformanceDescriptor.TryGetDirect<T, ISwiftHashable>(out var descriptor))
                    {
                        result = ProtocolWitnessTable.Zero;
                        return false;
                    }
                    if (!TypeMetadata.TryGetTypeMetadata<T>(out var metadata) || !metadata.HasValue)
                    {
                        result = ProtocolWitnessTable.Zero;
                        return false;
                    }
                    result = ProtocolWitnessTable.GetProtocolWitnessTable(descriptor.Value, metadata.Value);
                    return true;
                }

                if (!ProtocolWitnessTable.TryGet<T, ISwiftHashable>(out var maybe) || !maybe.HasValue)
                {
                    result = ProtocolWitnessTable.Zero;
                    return false;
                }
                result = maybe.Value;
                return true;
            }
            catch
            {
                // The TryGet paths above already avoid the throwing primitives, but defend
                // against any future regressions or platform-specific lookup paths that throw.
                result = ProtocolWitnessTable.Zero;
                return false;
            }
        }

        /// <summary>
        /// Stable FNV-1a hash over the marshalled Swift bytes. Two byte-equal payloads always
        /// hash to the same value, satisfying the Equals/GetHashCode contract for synthesized
        /// Equatable value types.
        /// </summary>
        private static int ComputeStructuralHash(ReadOnlySpan<byte> bytes)
        {
            // FNV-1a 32-bit. Collision profile is acceptable for a fallback hash.
            const uint OffsetBasis = 2166136261u;
            const uint Prime = 16777619u;
            uint h = OffsetBasis;
            for (int i = 0; i < bytes.Length; i++)
            {
                h ^= bytes[i];
                h *= Prime;
            }
            return unchecked((int)h);
        }
    }
}
