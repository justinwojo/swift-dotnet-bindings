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
        // P/Invoke declaration for Swift's Hashable protocol hashValue getter dispatch thunk.
        // Symbol: Swift.Hashable.hashValue.getter : Swift.Int
        // Returns nint (Swift.Int) which is 64-bit on arm64.
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSH9hashValueSivgTj")]
        private static extern nint PInvoke_SwiftHashValue(
            IntPtr value,
            SwiftSelf typeMetadataInSwiftSelf,
            TypeMetadata typeMetadata,
            ProtocolWitnessTable hashableProtocolWitnessTable);

        /// <summary>
        /// Computes the hash code of an object using Swift's Hashable protocol.
        /// Folds the 64-bit Swift hash value to a 32-bit int for .NET GetHashCode().
        /// </summary>
        /// <typeparam name="T">Type that implements ISwiftHashable</typeparam>
        /// <param name="value">The object to hash</param>
        /// <returns>A 32-bit hash code derived from Swift's hash value</returns>
        public static unsafe int GetHashCode<T>(T value)
            where T : ISwiftObject
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));

            var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
            var hashablePwt = ProtocolWitnessTable.GetOrThrowDirect<T, ISwiftHashable>();

            Span<byte> span = stackalloc byte[(int)metadata.Size];
            IntPtr payload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));

            SwiftMarshal.MarshalToSwift(value, ref span);

            nint h = PInvoke_SwiftHashValue(
                payload,
                new SwiftSelf(metadata),
                metadata,
                hashablePwt);

            // Fold 64-bit Swift hash to 32-bit .NET hash
            return unchecked((int)h ^ (int)(h >> 32));
        }
    }
}
