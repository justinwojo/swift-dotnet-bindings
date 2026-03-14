// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Marker interface for Swift struct types projected as C# classes.
/// Non-frozen structs and frozen structs with reference fields are projected as
/// C# classes implementing this interface. Unlike Swift classes (which have
/// finalizer-safe ARC cleanup via <see cref="SwiftClassHandle{T}"/>), struct
/// types require explicit <c>Dispose()</c> or <see cref="SwiftDisposeScope"/>
/// for reliable resource cleanup.
///
/// Used by the SB1001 analyzer to adjust diagnostic severity:
/// Warning for struct types (disposal important), Info for class types (finalizer handles it).
/// </summary>
public interface ISwiftStruct : ISwiftObject
{
}
