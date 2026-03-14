// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Extension methods for ergonomic DisposeScope interaction.
/// </summary>
public static class SwiftDisposeScopeExtensions
{
    /// <summary>
    /// Detach this object from automatic scope disposal.
    /// Returns the object for fluent chaining.
    /// </summary>
    public static T DetachFromScope<T>(this T obj) where T : ISwiftObject
    {
        SwiftDisposeScope.Detach(obj);
        return obj;
    }

    /// <summary>
    /// Move this object to the parent scope.
    /// Returns the object for fluent chaining.
    /// </summary>
    public static T MoveToParentScope<T>(this T obj) where T : ISwiftObject
    {
        SwiftDisposeScope.MoveToParent(obj);
        return obj;
    }
}
