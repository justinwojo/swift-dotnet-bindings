// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Represents a UTF-8 string slice returned from Swift wrapper functions.
/// This is the C# counterpart of the Swift SBW_Utf8Slice struct used for
/// string marshalling between C# and Swift. The Ptr field points to a
/// heap-allocated UTF-8 buffer that must be freed after reading.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[EditorBrowsable(EditorBrowsableState.Never)]
public struct Utf8Slice
{
    /// <summary>Pointer to the UTF-8 encoded bytes.</summary>
    public IntPtr Ptr;

    /// <summary>Length of the UTF-8 encoded bytes.</summary>
    public nint Len;
}
