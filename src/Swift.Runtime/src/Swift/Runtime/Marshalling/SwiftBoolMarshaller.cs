// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices.Marshalling;

namespace Swift.Runtime.Marshalling
{
    /// <summary>
    /// CustomMarshaller for a Swift <c>Bool</c> that crosses a <c>CallConvSwift</c> boundary in a
    /// register. Swift's calling convention carries a Bool as an LLVM <c>i1</c>: only bit 0 is
    /// defined, and the callee may leave the other bits set (an unoptimised <c>!(a &lt; b)</c> on
    /// x86_64 returns 0xfe for false). <c>[MarshalAs(UnmanagedType.U1)]</c> treats every nonzero
    /// byte as true, so a Swift-convention Bool is read from bit 0 instead. Values sent to Swift are
    /// always 0 or 1.
    /// </summary>
    [CustomMarshaller(typeof(bool), MarshalMode.Default, typeof(SwiftBoolMarshaller))]
    public static class SwiftBoolMarshaller
    {
        /// <summary>Converts a managed bool to the 0/1 byte Swift expects.</summary>
        public static byte ConvertToUnmanaged(bool managed) => managed ? (byte)1 : (byte)0;

        /// <summary>Reads a Swift-convention Bool from bit 0 of the register byte.</summary>
        public static bool ConvertToManaged(byte unmanaged) => (unmanaged & 1) != 0;
    }
}
