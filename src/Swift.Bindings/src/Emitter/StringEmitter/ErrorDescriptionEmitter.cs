// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared utility for emitting Swift error description extraction and error release infrastructure.
/// Follows the same per-module singleton + per-type dedup pattern as <see cref="CancellationTaskEmitter"/>.
/// </summary>
/// <remarks>
/// Swift side: SBW_GetErrorDescription extracts String(describing:) from a Swift error pointer,
/// SBW_ReleaseError releases the error's ARC reference via Unmanaged.release().
/// C# side: P/Invoke declarations for both, deduped per C# type.
/// </remarks>
public static class ErrorDescriptionEmitter
{
    /// <summary>
    /// Tracks whether the Swift error description infrastructure has been emitted for this module.
    /// </summary>
    private static bool _infrastructureEmitted = false;

    /// <summary>
    /// The module name for the current emission context. Used for module-specific symbol names.
    /// </summary>
    private static string? _currentModuleName = null;

    /// <summary>
    /// Tracks which C# types have had the error helper P/Invokes emitted (to avoid duplicates).
    /// </summary>
    private static readonly HashSet<string> _csharpTypesWithErrorPInvoke = new();

    /// <summary>
    /// Tracks which Swift error types have had the typed error extractor function emitted (per module).
    /// Key is the Swift error type name (e.g., "SwiftBindingsTestLib.ParseError").
    /// </summary>
    private static readonly HashSet<string> _typedErrorExtractorsEmitted = new();

    /// <summary>
    /// Tracks which C# types have had the extractor P/Invoke emitted (to avoid duplicates).
    /// Key is "{typeKey}:extractor:{swiftErrorTypeName}".
    /// </summary>
    private static readonly HashSet<string> _csharpTypesWithExtractorPInvoke = new();

    /// <summary>
    /// Resets the tracking for a new module. Call at the start of each module emission.
    /// </summary>
    public static void ResetForModule()
    {
        _infrastructureEmitted = false;
        _currentModuleName = null;
        _csharpTypesWithErrorPInvoke.Clear();
        _typedErrorExtractorsEmitted.Clear();
        _csharpTypesWithExtractorPInvoke.Clear();
    }

    /// <summary>
    /// Emits the Swift error description extraction and release functions if not already emitted.
    /// SBW_GetErrorDescription converts a Swift error pointer to a Swift-allocated C string via String(describing:).
    /// The returned buffer is allocated with UnsafeMutablePointer.allocate (freed by SBW_Free / deallocate).
    /// SBW_ReleaseError releases the error's ARC reference via Unmanaged.release().
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <param name="moduleName">The module name for symbol namespacing.</param>
    /// <returns>True if the infrastructure was emitted, false if it was already emitted.</returns>
    public static bool EmitIfNeeded(SwiftWriter swiftWriter, string moduleName)
    {
        if (_infrastructureEmitted)
            return false;

        _currentModuleName = moduleName;
        var descSymbol = GetDescriptionSymbolName(moduleName);
        var releaseSymbol = GetReleaseSymbolName(moduleName);

        // P0: Allocate with Swift's allocator (not C strdup/malloc) so SBW_Free (deallocate) is correct.
        // P1: Use as? Error with fallback to avoid force-cast trap on unexpected pointer representations.
        swiftWriter.WriteLines($$"""
            // Error description extraction for sync throwing methods
            @_silgen_name("{{descSymbol}}")
            public func SBW_GetErrorDescription(_ error: UnsafeRawPointer) -> UnsafeMutablePointer<CChar>? {
                let errorObj = Unmanaged<AnyObject>.fromOpaque(error).takeUnretainedValue()
                let desc: String
                if let errorValue = errorObj as? Error {
                    desc = String(describing: errorValue)
                } else {
                    desc = String(describing: errorObj)
                }
                return desc.withCString { cStr in
                    let len = strlen(cStr) + 1
                    let buf = UnsafeMutablePointer<CChar>.allocate(capacity: len)
                    buf.initialize(from: cStr, count: len)
                    return buf
                }
            }

            // ABI assumption: SwiftError.Value from .NET CallConvSwift is a retained pointer
            // to a heap-allocated, ARC-managed Swift error box. The caller owns one reference.
            // Validated by Tier 1 runtime tests: TestDivideByZeroThrows,
            // TestThrowingStructDivideByZeroThrows, TestValidateRangeTypedCatchNullError.
            @_silgen_name("{{releaseSymbol}}")
            public func SBW_ReleaseError(_ error: UnsafeRawPointer) {
                Unmanaged<AnyObject>.fromOpaque(error).release()
            }

            """);
        _infrastructureEmitted = true;
        return true;
    }

    /// <summary>
    /// Gets the module-specific symbol name for SBW_GetErrorDescription.
    /// </summary>
    public static string GetDescriptionSymbolName(string moduleName)
    {
        return $"SBW_GetErrorDescription_{moduleName}";
    }

    /// <summary>
    /// Gets the module-specific symbol name for SBW_ReleaseError.
    /// </summary>
    public static string GetReleaseSymbolName(string moduleName)
    {
        return $"SBW_ReleaseError_{moduleName}";
    }

    /// <summary>
    /// Gets the description symbol name for the current module.
    /// </summary>
    public static string? GetCurrentDescriptionSymbolName()
    {
        return _currentModuleName != null ? GetDescriptionSymbolName(_currentModuleName) : null;
    }

    /// <summary>
    /// Gets the release symbol name for the current module.
    /// </summary>
    public static string? GetCurrentReleaseSymbolName()
    {
        return _currentModuleName != null ? GetReleaseSymbolName(_currentModuleName) : null;
    }

    /// <summary>
    /// Checks if the error helper P/Invokes have already been emitted for the specified C# type.
    /// </summary>
    public static bool HasErrorPInvokeForType(string typeName)
    {
        return _csharpTypesWithErrorPInvoke.Contains(typeName);
    }

    /// <summary>
    /// Marks the error helper P/Invokes as emitted for the specified C# type.
    /// </summary>
    public static void MarkErrorPInvokeEmittedForType(string typeName)
    {
        _csharpTypesWithErrorPInvoke.Add(typeName);
    }

    /// <summary>
    /// Checks if the error description infrastructure has already been emitted for this module.
    /// </summary>
    public static bool IsEmitted => _infrastructureEmitted;

    /// <summary>
    /// Gets the current module name, if set.
    /// </summary>
    public static string? CurrentModuleName => _currentModuleName;

    /// <summary>
    /// Emits a Swift function that extracts a typed error value from an opaque error pointer.
    /// The function uses <c>as?</c> to safely cast to the concrete type, then copies the value
    /// bytes into a new buffer. Returns null if the cast fails (defensive fallback).
    /// Deduped per Swift error type name within a module.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <param name="moduleName">The module name for symbol namespacing.</param>
    /// <param name="swiftErrorTypeName">The fully-qualified Swift error type (e.g., "SwiftBindingsTestLib.ParseError").</param>
    public static void EmitTypedErrorExtractorIfNeeded(SwiftWriter swiftWriter, string moduleName, string swiftErrorTypeName)
    {
        if (!_typedErrorExtractorsEmitted.Add(swiftErrorTypeName))
            return;

        var safeSuffix = MakeSafeSymbolSuffix(swiftErrorTypeName);
        var symbol = GetExtractorSymbolName(moduleName, swiftErrorTypeName);

        swiftWriter.WriteLines($$"""
            // Typed error extractor for {{swiftErrorTypeName}} (C2)
            @_silgen_name("{{symbol}}")
            public func SBW_ExtractTypedError_{{safeSuffix}}(_ error: UnsafeRawPointer) -> UnsafeMutableRawPointer? {
                let errorObj = Unmanaged<AnyObject>.fromOpaque(error).takeUnretainedValue()
                guard let typedError = errorObj as? {{swiftErrorTypeName}} else {
                    return nil
                }
                let size = MemoryLayout<{{swiftErrorTypeName}}>.size
                let alignment = MemoryLayout<{{swiftErrorTypeName}}>.alignment
                let buf = UnsafeMutableRawPointer.allocate(byteCount: max(size, 1), alignment: alignment)
                withUnsafePointer(to: typedError) { src in
                    buf.copyMemory(from: UnsafeRawPointer(src), byteCount: size)
                }
                return buf
            }

            """);
    }

    /// <summary>
    /// Gets the module-specific symbol name for the typed error extractor function.
    /// </summary>
    public static string GetExtractorSymbolName(string moduleName, string swiftErrorTypeName)
    {
        var safeSuffix = MakeSafeSymbolSuffix(swiftErrorTypeName);
        return $"SBW_ExtractTypedError_{moduleName}_{safeSuffix}";
    }

    /// <summary>
    /// Checks if the extractor P/Invoke has already been emitted for the specified key.
    /// </summary>
    public static bool HasExtractorPInvokeForType(string key)
    {
        return _csharpTypesWithExtractorPInvoke.Contains(key);
    }

    /// <summary>
    /// Marks the extractor P/Invoke as emitted for the specified key.
    /// </summary>
    public static void MarkExtractorPInvokeEmittedForType(string key)
    {
        _csharpTypesWithExtractorPInvoke.Add(key);
    }

    /// <summary>
    /// Replaces dots with underscores to form a valid C/Swift identifier suffix.
    /// </summary>
    public static string MakeSafeSymbolSuffix(string swiftErrorTypeName)
    {
        return swiftErrorTypeName.Replace(".", "_");
    }
}
