// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift.Runtime.Marshalling
{
    /// <summary>
    /// Blittable representation of Swift String (16 bytes = two pointer-sized words).
    /// On ARM64, CallConvSwift passes this in two registers (x0 + x1).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BlittableSwiftString
    {
        public nint Word0;
        public nint Word1;
    }
}
