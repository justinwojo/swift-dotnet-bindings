// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared utility for emitting the SBW_Utf8Slice struct and SBW_Free function in Swift.
/// This struct is used for UTF-8 string marshalling between C# and Swift.
/// WitnessDispatchEmitter, EnumHandler, and WrapperEmitter (async) may need this,
/// so this class ensures each component is only emitted once per module.
/// </summary>
public static class Utf8SliceEmitter
{
    /// <summary>
    /// Tracks whether the SBW_Utf8Slice struct has been emitted for this module.
    /// </summary>
    private static bool _structEmitted = false;

    /// <summary>
    /// Tracks whether the SBW_Free function has been emitted for this module.
    /// </summary>
    private static bool _freeEmitted = false;

    /// <summary>
    /// The module name for the current emission context. Used for module-specific symbol names.
    /// </summary>
    private static string? _currentModuleName = null;

    /// <summary>
    /// Tracks which C# types have had the SBW_Free P/Invoke emitted (to avoid duplicates).
    /// </summary>
    private static readonly HashSet<string> _csharpTypesWithFreePInvoke = new();

    /// <summary>
    /// Resets the tracking for a new module. Call at the start of each module emission.
    /// </summary>
    public static void ResetForModule()
    {
        _structEmitted = false;
        _freeEmitted = false;
        _currentModuleName = null;
        _csharpTypesWithFreePInvoke.Clear();
    }

    /// <summary>
    /// Emits the SBW_Utf8Slice struct if not already emitted for this module.
    /// The ptr field is optional to support nil for empty strings.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <returns>True if the struct was emitted, false if it was already emitted.</returns>
    public static bool EmitIfNeeded(SwiftWriter swiftWriter)
    {
        if (_structEmitted)
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
        _structEmitted = true;
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
    /// <returns>True if the function was emitted, false if it was already emitted.</returns>
    public static bool EmitFreeIfNeeded(SwiftWriter swiftWriter, string moduleName)
    {
        if (_freeEmitted)
            return false;

        _currentModuleName = moduleName;
        var symbolName = GetFreeSymbolName(moduleName);

        swiftWriter.WriteLines(
            $"@_silgen_name(\"{symbolName}\")\n" +
            "public func SBW_Free(_ ptr: UnsafeMutableRawPointer?) {\n" +
            "    ptr?.deallocate()\n" +
            "}\n");
        _freeEmitted = true;
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
    /// <returns>The symbol name, or null if no module has been set.</returns>
    public static string? GetCurrentFreeSymbolName()
    {
        return _currentModuleName != null ? GetFreeSymbolName(_currentModuleName) : null;
    }

    /// <summary>
    /// Checks if the SBW_Free P/Invoke has already been emitted for the specified C# type.
    /// </summary>
    /// <param name="typeName">The fully-qualified C# type name.</param>
    /// <returns>True if already emitted, false otherwise.</returns>
    public static bool HasFreePInvokeForType(string typeName)
    {
        return _csharpTypesWithFreePInvoke.Contains(typeName);
    }

    /// <summary>
    /// Marks the SBW_Free P/Invoke as emitted for the specified C# type.
    /// </summary>
    /// <param name="typeName">The fully-qualified C# type name.</param>
    public static void MarkFreePInvokeEmittedForType(string typeName)
    {
        _csharpTypesWithFreePInvoke.Add(typeName);
    }

    /// <summary>
    /// Checks if the SBW_Utf8Slice struct has already been emitted for this module.
    /// </summary>
    public static bool IsStructEmitted => _structEmitted;

    /// <summary>
    /// Checks if the SBW_Free function has already been emitted for this module.
    /// </summary>
    public static bool IsFreeEmitted => _freeEmitted;

    /// <summary>
    /// Gets the current module name, if set.
    /// </summary>
    public static string? CurrentModuleName => _currentModuleName;
}
