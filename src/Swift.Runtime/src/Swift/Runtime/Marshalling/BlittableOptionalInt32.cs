// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift.Runtime.Marshalling
{
    /// <summary>
    /// Blittable representation of Swift Optional&lt;Int32&gt;.
    /// Layout: 4-byte value + 1-byte discriminator = 5 bytes.
    /// On ARM64, CallConvSwift decomposes this into two registers (int + byte),
    /// matching Swift's own lowering of Optional&lt;Int32&gt;.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BlittableOptionalInt32
    {
        public int Value;
        public byte Discriminator; // 0 = Some, 1 = None
    }
}
