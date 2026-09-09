// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using Swift.Runtime.InteropServices;

namespace Swift.Runtime
{
    /// <summary>
    /// Provides functionality to use Swift's Equatable protocol for equality comparison.
    /// </summary>
    public static class SwiftEquatable
    {
        // P/Invoke declaration for Swift's Equatable protocol equality operator
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSQ2eeoiySbx_xtFZTj")]
        private static extern bool PInvoke_SwiftEquals(
            IntPtr lhs,
            IntPtr rhs,
            SwiftSelf typeMetadataInSwiftSelf,
            TypeMetadata typeMetadata,
            ProtocolWitnessTable equatableProtocolWitnessTable);

        /// <summary>
        /// Compares two objects using Swift's Equatable protocol.
        /// </summary>
        /// <typeparam name="T">Type that implements ISwiftEquatable</typeparam>
        /// <param name="lhs">Left-hand side object</param>
        /// <param name="rhs">Right-hand side object</param>
        /// <returns>True if the objects are equal according to Swift's equality</returns>
        public static unsafe bool Equals<T>(T lhs, T rhs)
            where T : ISwiftObject
        {
            if (lhs == null)
                throw new ArgumentNullException(nameof(lhs));
            if (rhs == null)
                throw new ArgumentNullException(nameof(rhs));

            var metadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
            var equatablePwt = ProtocolWitnessTable.GetOrThrowAuto<T, IEquatable<T>>();

            Span<byte> lhsSpan = stackalloc byte[(int)metadata.Size];
            Span<byte> rhsSpan = stackalloc byte[(int)metadata.Size];

            // Marshalling initializes each buffer with an owned value (the type's value witness
            // copies into it), and Swift's `==` borrows both operands — it destroys neither. So
            // each buffer is this frame's to destroy. The pointer is published only after the
            // buffer holds a value, so a non-null pointer means "live", and a marshal that throws
            // (or an operand whose conversion fails after the first one succeeded) unwinds through
            // a finally that destroys exactly what was initialized and nothing else.
            IntPtr lhsPayload = IntPtr.Zero;
            IntPtr rhsPayload = IntPtr.Zero;
            try
            {
                SwiftMarshal.MarshalToSwift(lhs, ref lhsSpan);
                lhsPayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(lhsSpan));
                SwiftMarshal.MarshalToSwift(rhs, ref rhsSpan);
                rhsPayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(rhsSpan));

                return PInvoke_SwiftEquals(
                    lhsPayload,
                    rhsPayload,
                    new SwiftSelf(metadata),
                    metadata,
                    equatablePwt);
            }
            finally
            {
                if (rhsPayload != IntPtr.Zero)
                    metadata.ValueWitnessTable->Destroy((void*)rhsPayload, metadata);
                if (lhsPayload != IntPtr.Zero)
                    metadata.ValueWitnessTable->Destroy((void*)lhsPayload, metadata);
            }
        }
    }
}
