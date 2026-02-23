// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Immutable context threaded through type handlers during emission.
    /// Replaces mutable save/set/restore patterns on Conductor for:
    /// - P/Invoke helper context (generic types → CS7042 avoidance)
    /// - Deferred P/Invoke helper contexts (nested generic types)
    /// - Property renames (property/nested-type name collision resolution)
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="DeferredPInvokeHelperContexts"/> is intentionally a shared mutable
    /// <see cref="List{T}"/>. The outermost type handler (where <c>PInvokeHelperContext == null</c>)
    /// creates the list, and nested generic type handlers add to it via reference sharing
    /// through <c>context with { ... }</c> expressions. After the outermost handler finishes
    /// emitting its body, it drains and emits all deferred helper classes. This collector
    /// pattern is required because nested generic types can't emit their P/Invoke helper
    /// classes inline (they'd still be inside the outer generic type → CS7042).
    /// </para>
    /// </remarks>
    public record TypeHandlerContext(
        PInvokeHelperContext? PInvokeHelperContext,
        List<PInvokeHelperContext> DeferredPInvokeHelperContexts,
        Dictionary<string, string>? PropertyRenames,
        SortedDictionary<string, List<string>>? CompositionCollector = null)
    {
        public static TypeHandlerContext Empty => new(null, new(), null);
    }
}
