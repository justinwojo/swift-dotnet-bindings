// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Immutable context threaded through type handlers during emission.
    /// Carries both marshaler-owned state (PInvokeHelperContext, PropertyRenames)
    /// and emitter-owned state (EmissionContext) as a threading conduit.
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
    /// <para>
    /// <see cref="EmissionContext"/> is a per-module instance that replaces static mutable state
    /// in infrastructure emitters (Utf8Slice, Cancellation, ErrorDescription, etc.) with
    /// typed dedup APIs. Threaded from Program.cs → EmitModule → TypeHandlerContext → handlers.
    /// </para>
    /// </remarks>
    public record TypeHandlerContext(
        PInvokeHelperContext? PInvokeHelperContext,
        List<PInvokeHelperContext> DeferredPInvokeHelperContexts,
        Dictionary<string, string>? PropertyRenames,
        SortedDictionary<string, List<string>>? CompositionCollector = null,
        Dictionary<string, List<string>>? MarkerProtocolConformances = null,
        ModuleEmissionContext? EmissionContext = null)
    {
        /// <summary>
        /// Returns the emission context, falling back to the default singleton.
        /// </summary>
        public ModuleEmissionContext GetEmissionContext() => EmissionContext ?? ModuleEmissionContext.Default;

        public static TypeHandlerContext Empty => new(null, new(), null);
    }
}
