// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Static collector that accumulates SwiftUI View types during emission.
/// Collected views are consumed by SwiftUIBridgeEmitter in later phases.
/// </summary>
public static class SwiftUIBridgeCollector
{
    private static readonly object Sync = new();
    private static readonly List<TypeDecl> CollectedViews = new();

    /// <summary>
    /// Records a View type for bridge generation.
    /// </summary>
    public static void Collect(TypeDecl viewType)
    {
        ArgumentNullException.ThrowIfNull(viewType);

        lock (Sync)
        {
            CollectedViews.Add(viewType);
        }
    }

    /// <summary>
    /// Returns all collected View types.
    /// </summary>
    public static IReadOnlyList<TypeDecl> GetCollectedViews()
    {
        lock (Sync)
        {
            return CollectedViews.ToList();
        }
    }

    /// <summary>
    /// Clears collected views. Called before and after module emission
    /// to avoid stale state from previous modules or exceptions.
    /// </summary>
    public static void Reset()
    {
        lock (Sync)
        {
            CollectedViews.Clear();
        }
    }
}
