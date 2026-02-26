// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared utility for emitting the SBW_Utf8Slice struct and SBW_Free function in Swift.
/// This struct is used for UTF-8 string marshalling between C# and Swift.
/// WitnessDispatchEmitter, EnumHandler, and WrapperEmitter (async) may need this,
/// so this class ensures each component is only emitted once per module.
///
/// State is stored on <see cref="ModuleEmissionContext"/> (per-module instance).
/// </summary>
public static class Utf8SliceEmitter
{
    /// <summary>
    /// Emits the SBW_Utf8Slice struct if not already emitted for this module.
    /// The ptr field is optional to support nil for empty strings.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <param name="ctx">The per-module emission context.</param>
    /// <returns>True if the struct was emitted, false if it was already emitted.</returns>
    public static bool EmitIfNeeded(SwiftWriter swiftWriter, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        if (ctx.Utf8SliceStructEmitted)
            return false;

        // Use non-optional pointer for @convention(c) compatibility.
        // For empty strings: ptr points to valid memory (e.g., static empty buffer), len = 0.
        // C# should check len == 0 before reading; ptr must still be valid for @convention(c).
        swiftWriter.WriteLines("""
            @frozen
            public struct SBW_Utf8Slice {
                public var ptr: UnsafeMutablePointer<UInt8>
                public var len: Int
            }

            // Static empty buffer for empty string slices (required for @convention(c) compatibility)
            fileprivate var _sbw_emptyBuffer: UInt8 = 0

            """);
        ctx.Utf8SliceStructEmitted = true;
        return true;
    }

    /// <summary>
    /// Emits the SBW_Free function if not already emitted for this module.
    /// This function deallocates memory allocated by Swift for UTF-8 buffers.
    /// Safe to call with nil pointer (no-op).
    /// Uses module-specific symbol name to avoid collisions when multiple modules
    /// are linked into the same wrapper library.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <param name="moduleName">The module name for symbol namespacing.</param>
    /// <param name="ctx">The per-module emission context.</param>
    /// <returns>True if the function was emitted, false if it was already emitted.</returns>
    public static bool EmitFreeIfNeeded(SwiftWriter swiftWriter, string moduleName, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        if (ctx.Utf8SliceFreeEmitted)
            return false;

        ctx.Utf8SliceCurrentModuleName = moduleName;
        var symbolName = GetFreeSymbolName(moduleName);

        swiftWriter.WriteLines(
            $"@_silgen_name(\"{symbolName}\")\n" +
            "public func SBW_Free(_ ptr: UnsafeMutableRawPointer?) {\n" +
            "    ptr?.deallocate()\n" +
            "}\n");
        ctx.Utf8SliceFreeEmitted = true;
        return true;
    }

    /// <summary>
    /// Gets the module-specific symbol name for SBW_Free.
    /// </summary>
    /// <param name="moduleName">The module name.</param>
    /// <returns>The symbol name in the format "SBW_Free_ModuleName".</returns>
    public static string GetFreeSymbolName(string moduleName)
    {
        return $"SBW_Free_{moduleName}";
    }

    /// <summary>
    /// Gets the symbol name for the current module's SBW_Free function.
    /// </summary>
    /// <param name="ctx">The per-module emission context.</param>
    /// <returns>The symbol name, or null if no module has been set.</returns>
    public static string? GetCurrentFreeSymbolName(ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        return ctx.Utf8SliceCurrentModuleName != null ? GetFreeSymbolName(ctx.Utf8SliceCurrentModuleName) : null;
    }

    /// <summary>
    /// Checks if the SBW_Free P/Invoke has already been emitted for the specified C# type.
    /// </summary>
    /// <param name="typeName">The fully-qualified C# type name.</param>
    /// <param name="ctx">The per-module emission context.</param>
    /// <returns>True if already emitted, false otherwise.</returns>
    public static bool HasFreePInvokeForType(string typeName, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        return ctx.HasUtf8SliceFreePInvoke(typeName);
    }

    /// <summary>
    /// Marks the SBW_Free P/Invoke as emitted for the specified C# type.
    /// </summary>
    /// <param name="typeName">The fully-qualified C# type name.</param>
    /// <param name="ctx">The per-module emission context.</param>
    public static void MarkFreePInvokeEmittedForType(string typeName, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        ctx.TryAddUtf8SliceFreePInvoke(typeName);
    }

    /// <summary>
    /// Checks if the SBW_Utf8Slice struct has already been emitted for this module.
    /// </summary>
    public static bool IsStructEmitted(ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        return ctx.Utf8SliceStructEmitted;
    }

    /// <summary>
    /// Checks if the SBW_Free function has already been emitted for this module.
    /// </summary>
    public static bool IsFreeEmitted(ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        return ctx.Utf8SliceFreeEmitted;
    }

    /// <summary>
    /// Gets the current module name, if set.
    /// </summary>
    public static string? CurrentModuleName(ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        return ctx.Utf8SliceCurrentModuleName;
    }
}
