// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared utility for emitting Swift Task cancellation infrastructure and C# cancel P/Invoke.
/// Follows the same per-module singleton + per-type dedup pattern as <see cref="Utf8SliceEmitter"/>.
///
/// State is stored on <see cref="ModuleEmissionContext"/> (per-module instance).
/// </summary>
/// <remarks>
/// Swift side: _SBWTaskEntry holder class, dictionary keyed by task Int64 (GCHandle), NSLock,
/// and @_cdecl cancel function that looks up and cancels the Swift Task.
/// C# side: SBW_CancelTask P/Invoke, deduped per C# type.
/// </remarks>
public static class CancellationTaskEmitter
{
    /// <summary>
    /// Emits the Swift cancel infrastructure if not already emitted for this module.
    /// Includes: _SBWTaskEntry class, _sbwActiveTasks dictionary, _sbwTaskLock, and @_cdecl cancel function.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <param name="moduleName">The module name for symbol namespacing.</param>
    /// <param name="ctx">The per-module emission context.</param>
    /// <returns>True if the infrastructure was emitted, false if it was already emitted.</returns>
    public static bool EmitIfNeeded(SwiftWriter swiftWriter, string moduleName, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        if (ctx.CancellationInfrastructureEmitted)
            return false;

        ctx.CancellationCurrentModuleName = moduleName;
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
        ctx.CancellationInfrastructureEmitted = true;
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
    /// <param name="ctx">The per-module emission context.</param>
    /// <returns>The symbol name, or null if no module has been set.</returns>
    public static string? GetCurrentCancelSymbolName(ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        return ctx.CancellationCurrentModuleName != null ? GetCancelSymbolName(ctx.CancellationCurrentModuleName) : null;
    }

    /// <summary>
    /// Checks if the SBW_CancelTask P/Invoke has already been emitted for the specified C# type.
    /// </summary>
    /// <param name="typeName">The fully-qualified C# type name.</param>
    /// <param name="ctx">The per-module emission context.</param>
    /// <returns>True if already emitted, false otherwise.</returns>
    public static bool HasCancelPInvokeForType(string typeName, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        return ctx.HasCancellationPInvoke(typeName);
    }

    /// <summary>
    /// Marks the SBW_CancelTask P/Invoke as emitted for the specified C# type.
    /// </summary>
    /// <param name="typeName">The fully-qualified C# type name.</param>
    /// <param name="ctx">The per-module emission context.</param>
    public static void MarkCancelPInvokeEmittedForType(string typeName, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        ctx.TryAddCancellationPInvoke(typeName);
    }

    /// <summary>
    /// Checks if the cancel infrastructure has already been emitted for this module.
    /// </summary>
    public static bool IsEmitted(ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        return ctx.CancellationInfrastructureEmitted;
    }

    /// <summary>
    /// Gets the current module name, if set.
    /// </summary>
    public static string? GetCurrentModuleName(ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        return ctx.CancellationCurrentModuleName;
    }
}
