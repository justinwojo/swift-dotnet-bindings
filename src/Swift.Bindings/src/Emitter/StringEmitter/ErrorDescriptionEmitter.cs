// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared utility for emitting Swift error description extraction and error release infrastructure.
/// Follows the same per-module singleton + per-type dedup pattern as <see cref="CancellationTaskEmitter"/>.
///
/// State is stored on <see cref="ModuleEmissionContext"/> (per-module instance).
/// </summary>
/// <remarks>
/// Swift side: SBW_GetErrorDescription extracts String(describing:) from a Swift error pointer,
/// SBW_ReleaseError releases the error's ARC reference via Unmanaged.release().
/// C# side: P/Invoke declarations for both, deduped per C# type.
/// </remarks>
public static class ErrorDescriptionEmitter
{
    /// <summary>
    /// Emits the Swift error description extraction and release functions if not already emitted.
    /// SBW_GetErrorDescription converts a Swift error pointer to a Swift-allocated C string via String(describing:).
    /// The returned buffer is allocated with UnsafeMutablePointer.allocate (freed by SBW_Free / deallocate).
    /// SBW_ReleaseError releases the error's ARC reference via Unmanaged.release().
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <param name="moduleName">The module name for symbol namespacing.</param>
    /// <param name="ctx">The per-module emission context.</param>
    /// <returns>True if the infrastructure was emitted, false if it was already emitted.</returns>
    public static bool EmitIfNeeded(SwiftWriter swiftWriter, string moduleName, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        if (ctx.ErrorDescInfrastructureEmitted)
            return false;

        ctx.ErrorDescCurrentModuleName = moduleName;
        var descSymbol = GetDescriptionSymbolName(moduleName);
        var releaseSymbol = GetReleaseSymbolName(moduleName);

        // P0: Allocate with Swift's allocator (not C strdup/malloc) so SBW_Free (deallocate) is correct.
        // P1: Use @_cdecl (not @_silgen_name) for C calling convention compatibility.
        // P2: NSError path uses domain+code directly (String(describing:) on NSError crashes CoreCLR).
        swiftWriter.WriteLines($$"""
            // Error description extraction for sync throwing methods.
            // SwiftError.Value from .NET CallConvSwift is the raw value from the Swift error register.
            //
            // Uses Unmanaged<AnyObject>.fromOpaque to recover the error object, then dispatches:
            //   - Swift Error (enums, structs): String(describing:) gives case name
            //   - Pure NSError: domain+code (avoid ObjC runtime operations that may crash)
            //   - Fallback: type(of:) for anything else
            @_cdecl("{{descSymbol}}")
            public func SBW_GetErrorDescription(_ error: UnsafeRawPointer) -> UnsafeMutablePointer<CChar>? {
                let errorObj = Unmanaged<AnyObject>.fromOpaque(error).takeUnretainedValue()
                let desc: String
                if let errorValue = errorObj as? Error {
                    // This function runs in the Swift/ObjC runtime (via @_cdecl P/Invoke),
                    // not in CoreCLR, so ObjC runtime operations are fully available.
                    // String(describing:) gives the case name for Swift enum errors (e.g., "divisionByZero")
                    // and the localized description for NSError/subclasses.
                    // Previous code checked NSError first, but Swift enum errors bridge to
                    // _SwiftNativeNSError (an NSError subclass), so `as? NSError` matched everything
                    // and returned "domain (code N)" instead of the case name.
                    desc = String(describing: errorValue)
                } else {
                    desc = "\(type(of: errorObj))"
                }
                return desc.withCString { cStr in
                    let len = strlen(cStr) + 1
                    let buf = UnsafeMutablePointer<CChar>.allocate(capacity: len)
                    buf.initialize(from: cStr, count: len)
                    return buf
                }
            }

            // Release the error box's ARC reference. SwiftError.Value is a retained pointer.
            @_cdecl("{{releaseSymbol}}")
            public func SBW_ReleaseError(_ error: UnsafeRawPointer) {
                Unmanaged<AnyObject>.fromOpaque(error).release()
            }

            """);
        ctx.ErrorDescInfrastructureEmitted = true;
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
    public static string? GetCurrentDescriptionSymbolName(ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        return ctx.ErrorDescCurrentModuleName != null ? GetDescriptionSymbolName(ctx.ErrorDescCurrentModuleName) : null;
    }

    /// <summary>
    /// Gets the release symbol name for the current module.
    /// </summary>
    public static string? GetCurrentReleaseSymbolName(ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        return ctx.ErrorDescCurrentModuleName != null ? GetReleaseSymbolName(ctx.ErrorDescCurrentModuleName) : null;
    }

    /// <summary>
    /// Checks if the error helper P/Invokes have already been emitted for the specified C# type.
    /// </summary>
    public static bool HasErrorPInvokeForType(string typeName, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        return ctx.HasErrorDescPInvoke(typeName);
    }

    /// <summary>
    /// Marks the error helper P/Invokes as emitted for the specified C# type.
    /// </summary>
    public static void MarkErrorPInvokeEmittedForType(string typeName, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        ctx.TryAddErrorDescPInvoke(typeName);
    }

    /// <summary>
    /// Checks if the error description infrastructure has already been emitted for this module.
    /// </summary>
    public static bool IsEmitted(ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        return ctx.ErrorDescInfrastructureEmitted;
    }

    /// <summary>
    /// Gets the current module name, if set.
    /// </summary>
    public static string? GetCurrentModuleName(ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        return ctx.ErrorDescCurrentModuleName;
    }

    /// <summary>
    /// Emits a Swift function that extracts a typed error value from an opaque error pointer.
    /// The function uses <c>as?</c> to safely cast to the concrete type, then copies the value
    /// bytes into a new buffer. Returns null if the cast fails (defensive fallback).
    /// Deduped per Swift error type name within a module.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <param name="moduleName">The module name for symbol namespacing.</param>
    /// <param name="swiftErrorTypeName">The fully-qualified Swift error type (e.g., "SwiftBindingsTestLib.ParseError").</param>
    /// <param name="ctx">The per-module emission context.</param>
    public static void EmitTypedErrorExtractorIfNeeded(SwiftWriter swiftWriter, string moduleName, string swiftErrorTypeName, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        if (!ctx.TryAddTypedErrorExtractor(swiftErrorTypeName))
            return;

        var safeSuffix = MakeSafeSymbolSuffix(swiftErrorTypeName);
        var symbol = GetExtractorSymbolName(moduleName, swiftErrorTypeName);

        swiftWriter.WriteLines($$"""
            // Typed error extractor for {{swiftErrorTypeName}} (C2)
            @_cdecl("{{symbol}}")
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
    public static bool HasExtractorPInvokeForType(string key, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        return ctx.HasExtractorPInvoke(key);
    }

    /// <summary>
    /// Marks the extractor P/Invoke as emitted for the specified key.
    /// </summary>
    public static void MarkExtractorPInvokeEmittedForType(string key, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        ctx.TryAddExtractorPInvoke(key);
    }

    /// <summary>
    /// Replaces dots with underscores to form a valid C/Swift identifier suffix.
    /// </summary>
    public static string MakeSafeSymbolSuffix(string swiftErrorTypeName)
    {
        return swiftErrorTypeName.Replace(".", "_");
    }
}
