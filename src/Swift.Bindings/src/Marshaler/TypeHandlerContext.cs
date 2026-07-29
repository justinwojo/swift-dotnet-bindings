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
    /// <para>
    /// <see cref="EnumPropertyRenames"/> is a <b>property-only</b> rename channel, deliberately
    /// separate from <see cref="PropertyRenames"/>. An enum whose computed property shares a Swift
    /// identifier with one of its associated-value cases (e.g. Facebook's
    /// <c>SharePhoto.Source.image</c> property + <c>.image</c> case) projects both to the same C#
    /// name; the property side is disambiguated with a <c>Value</c> suffix
    /// (<c>Image</c> → <c>ImageValue</c>). This must NOT ride on <see cref="PropertyRenames"/>,
    /// which the case-constructor-naming path reads too — since the case and property share the
    /// literal identifier, a shared-dict entry would rename the <i>case</i> as well and silently
    /// recreate the collision one level down. Only <see cref="PropertyHandler"/> reads this channel;
    /// every case-name call site keeps reading <see cref="PropertyRenames"/> alone.
    /// </para>
    /// </remarks>
    public record TypeHandlerContext(
        PInvokeHelperContext? PInvokeHelperContext,
        List<PInvokeHelperContext> DeferredPInvokeHelperContexts,
        Dictionary<string, string>? PropertyRenames,
        SortedDictionary<string, List<string>>? CompositionCollector = null,
        Dictionary<string, List<string>>? MarkerProtocolConformances = null,
        ModuleEmissionContext? EmissionContext = null,
        Dictionary<string, string>? EnumPropertyRenames = null)
    {
        /// <summary>
        /// The context used when the caller threaded none. Scoped to this context and every
        /// <c>with</c>-derived child (the record copy constructor carries the field over), so one
        /// emission's nested handlers share one fallback while a second emission gets its own.
        /// A process-wide fallback instead put concurrent emissions in the same dedup registries
        /// and per-module accumulators, which emission enumerates while it is still adding to them.
        /// </summary>
        private readonly ModuleEmissionContext _implicitEmissionContext = ModuleEmissionContext.CreateImplicitFallback();

        /// <summary>
        /// Returns the emission context, falling back to this context's own implicit one.
        /// </summary>
        public ModuleEmissionContext GetEmissionContext() => EmissionContext ?? _implicitEmissionContext;

        public static TypeHandlerContext Empty => new(null, new(), null);
    }
}
