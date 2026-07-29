// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Collector that accumulates SwiftUI View types during emission.
/// Collected views are consumed by SwiftUIBridgeEmitter in later phases.
///
/// State lives on <see cref="ModuleEmissionContext"/> (a per-module instance), so it can no
/// longer bleed across emission runs the way the former process-global static collections did.
/// This class is a thin facade over that context, mirroring <see cref="Utf8SliceEmitter"/>.
/// </summary>
public static class SwiftUIBridgeCollector
{
    /// <summary>
    /// Records a View type for bridge generation on the given emission context.
    /// </summary>
    public static void Collect(TypeDecl viewType, ModuleEmissionContext? ctx = null)
    {
        (ctx ?? ModuleEmissionContext.CreateImplicitFallback()).CollectSwiftUIView(viewType);
    }

    /// <summary>
    /// Returns all collected View types for the given emission context.
    /// </summary>
    public static IReadOnlyList<TypeDecl> GetCollectedViews(ModuleEmissionContext? ctx = null)
    {
        return (ctx ?? ModuleEmissionContext.CreateImplicitFallback()).GetCollectedSwiftUIViews();
    }
}
