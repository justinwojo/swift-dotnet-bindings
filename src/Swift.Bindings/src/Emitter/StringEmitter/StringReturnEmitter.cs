// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared utility for emitting the Swift-side SBW_Utf8Slice string return pattern.
/// Writes a String result to a resultPtr as SBW_Utf8Slice (UTF-8 bytes + length),
/// because @_cdecl can't return Swift structs directly.
///
/// Two variants:
/// - <see cref="EmitGetterBody"/>: for property/subscript getters — assigns result from an access expression.
/// - <see cref="EmitReturnBody"/>: for method returns — includes explicit <c>: String</c> type annotation
///   to disambiguate overloaded methods with different return types.
///
/// Both use the proven pattern: convert to UTF-8 array, allocate + copy for non-empty,
/// use _sbw_emptyBuffer sentinel for empty strings.
///
/// The C# side unmarshalling stays in WrapperEmitter.Return.cs (not extracted here).
/// </summary>
public static class StringReturnEmitter
{
    /// <summary>
    /// Emits the string getter body using SBW_Utf8Slice pattern.
    /// Writes result to resultPtr because @_cdecl can't return Swift structs.
    /// Used by PropertyWrapperEmitter and SubscriptWrapperEmitter for getter wrappers.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <param name="accessExpression">The Swift expression to get the string value (e.g., "obj.name").</param>
    public static void EmitGetterBody(SwiftWriter swiftWriter, string accessExpression)
    {
        swiftWriter.WriteLines($$"""
            let result = {{accessExpression}}
            let utf8 = Array(result.utf8)
            if utf8.isEmpty {
                resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: &_sbw_emptyBuffer, len: 0), as: SBW_Utf8Slice.self)
                return
            }
            let ptr = UnsafeMutablePointer<UInt8>.allocate(capacity: utf8.count)
            ptr.initialize(from: utf8, count: utf8.count)
            resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: ptr, len: utf8.count), as: SBW_Utf8Slice.self)
            """);
    }

    /// <summary>
    /// Emits the string return body for method wrappers using SBW_Utf8Slice pattern.
    /// Includes explicit <c>: String</c> type annotation to disambiguate overloaded methods
    /// with different return types (e.g., URLEncodedFormEncoder.encode(_:) returning String vs Data).
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <param name="callExpression">The Swift call expression (e.g., "obj.encode(value)").</param>
    public static void EmitReturnBody(SwiftWriter swiftWriter, string callExpression)
    {
        // Explicit `: String` annotation disambiguates overloaded methods with different return types
        swiftWriter.WriteLines($$"""
            let result: String = {{callExpression}}
            let utf8 = Array(result.utf8)
            if utf8.isEmpty {
                resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: &_sbw_emptyBuffer, len: 0), as: SBW_Utf8Slice.self)
                return
            }
            let ptr = UnsafeMutablePointer<UInt8>.allocate(capacity: utf8.count)
            ptr.initialize(from: utf8, count: utf8.count)
            resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: ptr, len: utf8.count), as: SBW_Utf8Slice.self)
            """);
    }
}
