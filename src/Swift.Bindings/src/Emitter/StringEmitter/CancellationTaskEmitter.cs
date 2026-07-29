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
/// synchronous register/unregister helpers (async-safe in Swift 6),
/// and @_cdecl cancel function that looks up and cancels the Swift Task.
/// C# side: SBW_CancelTask P/Invoke, deduped per C# type.
/// </remarks>
public static class CancellationTaskEmitter
{
    /// <summary>
    /// Emits the Swift cancel infrastructure if not already emitted for this module.
    /// Includes: _SBWTaskEntry class, _sbwActiveTasks dictionary, _sbwTaskLock,
    /// _sbwRegisterTask/_sbwUnregisterTask helpers (async-safe), and @_cdecl cancel function.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer to emit to.</param>
    /// <param name="moduleName">The module name for symbol namespacing.</param>
    /// <param name="ctx">The per-module emission context.</param>
    /// <returns>True if the infrastructure was emitted, false if it was already emitted.</returns>
    public static bool EmitIfNeeded(SwiftWriter swiftWriter, string moduleName, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.CreateImplicitFallback();
        if (ctx.CancellationInfrastructureEmitted)
            return false;

        ctx.CancellationCurrentModuleName = moduleName;
        var symbolName = GetCancelSymbolName(moduleName);
        var unregisterSymbolName = GetUnregisterSymbolName(moduleName);

        swiftWriter.WriteLines($$"""
            // Task cancellation infrastructure for async method cancellation support
            private final class _SBWTaskEntry {
                var task: {{SwiftConcurrencyNames.Task}}<Void, Never>?
                // Replay flag: a cancel that arrives before the launching site assigns `task`
                // would otherwise be lost (nil?.cancel()). The cancel path records the intent
                // here under the lock; the launching site replays it via _sbwAssignTask.
                var wasCancelled = false
            }
            private var _sbwActiveTasks: [Int64: _SBWTaskEntry] = [:]
            private let _sbwTaskLock = NSLock()

            // Synchronous helpers — safe to call from async contexts (Swift 6).
            // NSLock.lock()/unlock() are @available(*, noasync) so direct calls
            // inside Task {} are errors in the Swift 6 language mode.
            private func _sbwRegisterTask(_ taskId: Int64, _ entry: _SBWTaskEntry) {
                _sbwTaskLock.lock()
                // WINDOW A carry-forward: _sbw_cancelTask may have run before this wrapper
                // reached registration (cancel landed between the C# token registration and
                // the P/Invoke entry). It left a tombstone marked wasCancelled under this id;
                // adopt its intent so _sbwAssignTask replays the cancel onto the launched task.
                // Recycle-safe: ids come from a process-monotonic counter, never reused.
                if let existing = _sbwActiveTasks[taskId], existing.wasCancelled {
                    entry.wasCancelled = true
                }
                _sbwActiveTasks[taskId] = entry
                _sbwTaskLock.unlock()
            }

            private func _sbwUnregisterTask(_ taskId: Int64) {
                _sbwTaskLock.lock()
                _sbwActiveTasks.removeValue(forKey: taskId)
                _sbwTaskLock.unlock()
            }

            // Assigns the launched task to the entry under the registry lock and reports
            // whether a cancel already arrived in the register→assign window. The single lock
            // gives a happens-before with _sbw_cancelTask in both directions, closing the
            // lost-cancel race that a bare unlocked `_entry.task =` would leave open.
            private func _sbwAssignTask(_ entry: _SBWTaskEntry, _ task: {{SwiftConcurrencyNames.Task}}<Void, Never>) -> Bool {
                _sbwTaskLock.lock()
                entry.task = task
                let cancelledEarly = entry.wasCancelled
                _sbwTaskLock.unlock()
                return cancelledEarly
            }

            @_cdecl("{{symbolName}}")
            public func _sbw_cancelTask(_ taskId: Int64) {
                _sbwTaskLock.lock()
                let entry = _sbwActiveTasks[taskId]
                let task = entry?.task
                if let entry {
                    // WINDOW B: the wrapper has registered but not yet assigned the task.
                    // Record the intent so _sbwAssignTask replays it.
                    if task == nil { entry.wasCancelled = true }
                } else {
                    // WINDOW A: cancel arrived before the wrapper registered at all. Leave a
                    // tombstone so the imminent _sbwRegisterTask carries the cancel forward.
                    // If the wrapper never registers (the foreground C# threw before the
                    // P/Invoke), the catch path calls _sbw_unregisterTask to reclaim it.
                    let tombstone = _SBWTaskEntry()
                    tombstone.wasCancelled = true
                    _sbwActiveTasks[taskId] = tombstone
                }
                _sbwTaskLock.unlock()
                task?.cancel()
            }

            // Reclaims a registry entry from the C# foreground catch path. Covers the WINDOW A
            // tombstone whose wrapper never launched (so the task's `defer { _sbwUnregisterTask }`
            // never runs); a no-op for any id with no entry.
            @_cdecl("{{unregisterSymbolName}}")
            public func _sbw_unregisterTask(_ taskId: Int64) {
                _sbwUnregisterTask(taskId)
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
    /// Gets the module-specific symbol name for SBW_UnregisterTask, the foreground-catch
    /// reclaim entry point that frees a WINDOW A cancellation tombstone whose wrapper never launched.
    /// </summary>
    /// <param name="moduleName">The module name.</param>
    /// <returns>The symbol name in the format "SBW_UnregisterTask_ModuleName".</returns>
    public static string GetUnregisterSymbolName(string moduleName)
    {
        return $"SBW_UnregisterTask_{moduleName}";
    }

    /// <summary>
    /// Gets the symbol name for the current module's SBW_CancelTask function.
    /// </summary>
    /// <param name="ctx">The per-module emission context.</param>
    /// <returns>The symbol name, or null if no module has been set.</returns>
    public static string? GetCurrentCancelSymbolName(ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.CreateImplicitFallback();
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
        ctx ??= ModuleEmissionContext.CreateImplicitFallback();
        return ctx.HasCancellationPInvoke(typeName);
    }

    /// <summary>
    /// Marks the SBW_CancelTask P/Invoke as emitted for the specified C# type.
    /// </summary>
    /// <param name="typeName">The fully-qualified C# type name.</param>
    /// <param name="ctx">The per-module emission context.</param>
    public static void MarkCancelPInvokeEmittedForType(string typeName, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.CreateImplicitFallback();
        ctx.TryAddCancellationPInvoke(typeName);
    }

    /// <summary>
    /// Checks if the cancel infrastructure has already been emitted for this module.
    /// </summary>
    public static bool IsEmitted(ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.CreateImplicitFallback();
        return ctx.CancellationInfrastructureEmitted;
    }

    /// <summary>
    /// Gets the current module name, if set.
    /// </summary>
    public static string? GetCurrentModuleName(ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.CreateImplicitFallback();
        return ctx.CancellationCurrentModuleName;
    }
}
