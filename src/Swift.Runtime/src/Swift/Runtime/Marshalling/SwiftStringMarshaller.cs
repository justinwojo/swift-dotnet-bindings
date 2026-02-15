// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Swift.Runtime.InteropServices;

namespace Swift.Runtime.Marshalling
{
    /// <summary>
    /// CustomMarshaller: SwiftString to BlittableSwiftString (16-byte raw copy).
    /// Input: copies raw payload words from SwiftString's SafeHandle buffer.
    /// Output: allocates temp buffer, writes words, creates SwiftString via VWT InitializeWithCopy.
    /// </summary>
    [CustomMarshaller(typeof(SwiftString), MarshalMode.Default, typeof(SwiftStringMarshaller))]
    public static class SwiftStringMarshaller
    {
        public static unsafe BlittableSwiftString ConvertToUnmanaged(SwiftString managed)
        {
            if (managed == null)
                return default;
            bool success = false;
            managed.Payload.DangerousAddRef(ref success);
            try
            {
                var ptr = (nint*)managed.Payload.DangerousGetHandle();
                return new BlittableSwiftString { Word0 = ptr[0], Word1 = ptr[1] };
            }
            finally
            {
                if (success) managed.Payload.DangerousRelease();
            }
        }

        public static unsafe SwiftString ConvertToManaged(BlittableSwiftString unmanaged)
        {
            var temp = (nint*)NativeMemory.Alloc((nuint)(2 * sizeof(nint)));
            try
            {
                temp[0] = unmanaged.Word0;
                temp[1] = unmanaged.Word1;
                // SwiftString(IntPtr) does a raw byte copy (ownership transfer), NOT
                // InitializeWithCopy. The +1 refcount from the Swift return value is
                // transferred to the new SwiftString. We must NOT call VWT.Destroy here —
                // that would decrement the refcount on heap-backed strings, leaving the
                // new SwiftString with a dangling pointer. NativeMemory.Free only frees
                // the 16-byte temp buffer without touching the Swift refcount.
                return SwiftMarshal.MarshalFromSwift<SwiftString>(new IntPtr(temp));
            }
            finally
            {
                NativeMemory.Free(temp);
            }
        }

        public static void Free(BlittableSwiftString _) { }
    }
}
