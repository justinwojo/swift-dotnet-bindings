// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared helpers for the wrapper emitters (Method, Constructor, Property, Subscript).
/// Contains code-emission utilities that are identical across all wrapper types.
/// </summary>
public static class WrapperEmitterHelpers
{
    /// <summary>
    /// Emits the @MainActor (if needed) and @_cdecl annotations for a Swift wrapper function.
    /// Consolidates the identical annotation pattern used by MethodWrapperEmitter,
    /// PropertyWrapperEmitter, and ConstructorWrapperEmitter.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer for the wrapper .swift file.</param>
    /// <param name="symbolName">The @_cdecl symbol name string.</param>
    /// <param name="needsMainActor">Whether to prepend @MainActor before @_cdecl.</param>
    public static void EmitCdeclAnnotation(SwiftWriter swiftWriter, string symbolName, bool needsMainActor)
    {
        if (needsMainActor)
        {
            swiftWriter.WriteLine("@MainActor");
        }

        swiftWriter.WriteLines($$"""
            @_cdecl("{{symbolName}}")
            """);
    }

    /// <summary>
    /// Emits a safe tag-only enum return for @_cdecl wrappers.
    /// Tag-only enums (no RawRepresentable conformance) have a memory layout smaller than
    /// the cdecl return type (e.g., a 4-case enum is 1 byte, but the return type is Int/8 bytes).
    /// Using <c>UnsafeRawPointer.load(as: Int.self)</c> reads past the enum's allocation,
    /// causing "load from misaligned raw pointer" crashes on ARM64.
    /// Instead, zero-initialize an Int and copy only the enum's actual bytes into it.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer for the wrapper .swift file.</param>
    /// <param name="callExpr">The Swift expression that produces the enum value.</param>
    /// <param name="cdeclReturnType">The cdecl return type name (e.g., "Int").</param>
    public static void EmitTagOnlyEnumReturn(SwiftWriter swiftWriter, string callExpr, string cdeclReturnType)
    {
        // Compute size BEFORE the closures to avoid Swift exclusivity checker error
        // ("overlapping accesses to 'result'") — MemoryLayout.size(ofValue:) reads result
        // while withUnsafePointer(to: &result) takes exclusive access.
        swiftWriter.WriteLine($"var result = {callExpr}");
        swiftWriter.WriteLine("let resultSize = MemoryLayout.size(ofValue: result)");
        swiftWriter.WriteLine($"var tag: {cdeclReturnType} = 0");
        swiftWriter.WriteLine("withUnsafeMutablePointer(to: &tag) { tagPtr in withUnsafePointer(to: &result) { resultPtr in UnsafeMutableRawPointer(tagPtr).copyMemory(from: UnsafeRawPointer(resultPtr), byteCount: resultSize) } }");
        swiftWriter.WriteLine("return tag");
    }

    /// <summary>
    /// Returns the Swift code lines for a tag-only enum return as a list of strings,
    /// for use in extension body lines (e.g., generic extension method emission).
    /// See <see cref="EmitTagOnlyEnumReturn"/> for the rationale.
    /// </summary>
    /// <param name="callExpr">The Swift expression that produces the enum value.</param>
    /// <param name="cdeclReturnType">The cdecl return type name (e.g., "Int").</param>
    public static List<string> GetTagOnlyEnumReturnLines(string callExpr, string cdeclReturnType)
    {
        return new List<string>
        {
            $"var result = {callExpr}",
            "let resultSize = MemoryLayout.size(ofValue: result)",
            $"var tag: {cdeclReturnType} = 0",
            "withUnsafeMutablePointer(to: &tag) { tagPtr in withUnsafePointer(to: &result) { resultPtr in UnsafeMutableRawPointer(tagPtr).copyMemory(from: UnsafeRawPointer(resultPtr), byteCount: resultSize) } }",
            "return tag"
        };
    }
}
