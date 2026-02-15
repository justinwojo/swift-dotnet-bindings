// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices.Marshalling;

namespace Swift.Runtime.Marshalling
{
    /// <summary>
    /// CustomMarshaller that lowers SwiftOptional&lt;int&gt; to BlittableOptionalInt32.
    /// Used with [LibraryImport] + [MarshalUsing] to produce a blittable intermediate
    /// for CallConvSwift.
    /// </summary>
    [CustomMarshaller(typeof(SwiftOptional<int>), MarshalMode.Default, typeof(SwiftOptionalInt32Marshaller))]
    public static class SwiftOptionalInt32Marshaller
    {
        public static BlittableOptionalInt32 ConvertToUnmanaged(SwiftOptional<int> managed)
        {
            if (managed == null || managed.Case == SwiftOptionalCases.None)
                return new BlittableOptionalInt32 { Value = 0, Discriminator = 1 };
            return new BlittableOptionalInt32 { Value = managed.Value, Discriminator = 0 };
        }

        public static SwiftOptional<int> ConvertToManaged(BlittableOptionalInt32 unmanaged)
        {
            return unmanaged.Discriminator == 0
                ? SwiftOptional<int>.NewSome(unmanaged.Value)
                : SwiftOptional<int>.NewNone();
        }

        public static void Free(BlittableOptionalInt32 _) { }
    }
}
