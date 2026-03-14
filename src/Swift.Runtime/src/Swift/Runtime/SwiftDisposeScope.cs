// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Automatic disposal scope for Swift objects. All ISwiftObject instances created
/// within an active scope are tracked and disposed when the scope exits.
///
/// Threading model: async-flow-aware via AsyncLocal, but NOT safe for parallel
/// mutation. A single scope must not be shared across concurrent tasks that
/// create Swift objects simultaneously. Use one scope per sequential async flow.
/// If parallel tasks each need tracking, each should create its own scope.
/// </summary>
public sealed class SwiftDisposeScope : IDisposable
{
    private static readonly AsyncLocal<SwiftDisposeScope?> s_current = new();

    private readonly SwiftDisposeScope? _parent;
    private readonly List<IDisposable> _tracked = new();
    private bool _disposed;

    /// <summary>
    /// The currently active scope, or null if none.
    /// </summary>
    public static SwiftDisposeScope? Current => s_current.Value;

    public SwiftDisposeScope()
    {
        _parent = s_current.Value;
        s_current.Value = this;
    }

    /// <summary>
    /// Register an object for automatic disposal. Called from generated
    /// wrapper constructors and NewFromPayload (heap-backed types only).
    /// </summary>
    public static void TryRegister(IDisposable obj)
    {
        var scope = s_current.Value;
        if (scope == null || scope._disposed)
            return;
        scope._tracked.Add(obj);
    }

    /// <summary>
    /// Remove an object from automatic disposal tracking.
    /// Walks the entire scope chain to find the scope that owns this object,
    /// so it works correctly even when called from a nested inner scope.
    /// </summary>
    public static bool Detach(IDisposable obj)
    {
        var scope = s_current.Value;
        while (scope != null)
        {
            if (scope._tracked.Remove(obj))
                return true;
            scope = scope._parent;
        }
        return false;
    }

    /// <summary>
    /// Move an object from its owning scope to that scope's parent.
    /// Walks the scope chain to find the correct owning scope first.
    /// </summary>
    public static bool MoveToParent(IDisposable obj)
    {
        var scope = s_current.Value;
        while (scope != null)
        {
            if (!scope._disposed && scope._tracked.Remove(obj))
            {
                // Only add to parent if it's not disposed
                if (scope._parent != null && !scope._parent._disposed)
                    scope._parent._tracked.Add(obj);
                return true;
            }
            scope = scope._parent;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Dispose in reverse creation order (LIFO)
        for (int i = _tracked.Count - 1; i >= 0; i--)
        {
            try { _tracked[i].Dispose(); }
            catch { /* Swallow — same as SafeHandle contract */ }
        }

        _tracked.Clear();
        s_current.Value = _parent;
    }
}
