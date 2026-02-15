// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared utility for emitting Swift Task cancellation infrastructure and C# cancel P/Invoke.
/// Follows the same per-module singleton + per-type dedup pattern as <see cref="Utf8SliceEmitter"/>.
/// </summary>
/// <remarks>
/// Swift side: _SBWTaskEntry holder class, dictionary keyed by task Int64 (GCHandle), NSLock,
/// and @_cdecl cancel function that looks up and cancels the Swift Task.
/// C# side: SBW_CancelTask P/Invoke, deduped per C# type.
/// </remarks>
public static class CancellationTaskEmitter
{
    /// <summary>
    /// Tracks whether the Swift cancel infrastructure has been emitted for this module.
    /// </summary>
    private static bool _infrastructureEmitted = false;

    /// <summary>
    /// The module name for the current emission context. Used for module-specific symbol names.
    /// </summary>
    private static string? _currentModuleName = null;

    /// <summary>
    /// Tracks which C# types have had the SBW_CancelTask P/Invoke emitted (to avoid duplicates).
    /// </summary>
    private static readonly HashSet<string> _csharpTypesWithCancelPInvoke = new();

    /// <summary>
    /// Resets the tracking for a new module. Call at the start of each module emission.
    /// </summary>
    public static void ResetForModule()
    {
        _infrastructureEmitted = false;
        _currentModuleName = null;
        _csharpTypesWithCancelPInvoke.Clear();
    }

    /// <summary>
    /// Emits the Swift cancel infrastructure if not already emitted for this module.
    /// Includes: _SBWTaskEntry class, _sbwActiveTasks dictionary, _sbwTaskLock, and @_cdecl cancel function.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <param name="moduleName">The module name for symbol namespacing.</param>
    /// <returns>True if the infrastructure was emitted, false if it was already emitted.</returns>
    public static bool EmitIfNeeded(SwiftWriter swiftWriter, string moduleName)
    {
        if (_infrastructureEmitted)
            return false;

        _currentModuleName = moduleName;
        var symbolName = GetCancelSymbolName(moduleName);

        swiftWriter.WriteLines($$"""
            // Task cancellation infrastructure for async method cancellation support
            private final class _SBWTaskEntry {
                var task: Task<Void, Never>?
            }
            private var _sbwActiveTasks: [Int64: _SBWTaskEntry] = [:]
            private let _sbwTaskLock = NSLock()

            @_cdecl("{{symbolName}}")
            public func _sbw_cancelTask(_ taskId: Int64) {
                _sbwTaskLock.lock()
                let entry = _sbwActiveTasks[taskId]
                _sbwTaskLock.unlock()
                entry?.task?.cancel()
            }

            """);
        _infrastructureEmitted = true;
        return true;
    }

    /// <summary>
    /// Gets the module-specific symbol name for SBW_CancelTask.
    /// </summary>
    /// <param name="moduleName">The module name.</param>
    /// <returns>The symbol name in the format "SBW_CancelTask_ModuleName".</returns>
    public static string GetCancelSymbolName(string moduleName)
    {
        return $"SBW_CancelTask_{moduleName}";
    }

    /// <summary>
    /// Gets the symbol name for the current module's SBW_CancelTask function.
    /// </summary>
    /// <returns>The symbol name, or null if no module has been set.</returns>
    public static string? GetCurrentCancelSymbolName()
    {
        return _currentModuleName != null ? GetCancelSymbolName(_currentModuleName) : null;
    }

    /// <summary>
    /// Checks if the SBW_CancelTask P/Invoke has already been emitted for the specified C# type.
    /// </summary>
    /// <param name="typeName">The fully-qualified C# type name.</param>
    /// <returns>True if already emitted, false otherwise.</returns>
    public static bool HasCancelPInvokeForType(string typeName)
    {
        return _csharpTypesWithCancelPInvoke.Contains(typeName);
    }

    /// <summary>
    /// Marks the SBW_CancelTask P/Invoke as emitted for the specified C# type.
    /// </summary>
    /// <param name="typeName">The fully-qualified C# type name.</param>
    public static void MarkCancelPInvokeEmittedForType(string typeName)
    {
        _csharpTypesWithCancelPInvoke.Add(typeName);
    }

    /// <summary>
    /// Checks if the cancel infrastructure has already been emitted for this module.
    /// </summary>
    public static bool IsEmitted => _infrastructureEmitted;

    /// <summary>
    /// Gets the current module name, if set.
    /// </summary>
    public static string? CurrentModuleName => _currentModuleName;
}
