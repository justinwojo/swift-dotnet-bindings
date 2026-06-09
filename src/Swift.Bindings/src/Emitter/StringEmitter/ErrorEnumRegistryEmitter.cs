// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Plain-throws → typed-exception bridge — Layer 1 (foundation).
///
/// Walks every <see cref="EnumDecl"/> / <see cref="StructDecl"/> / <see cref="ClassDecl"/>
/// in the module that declares conformance to <c>Swift.Error</c>,
/// <c>Foundation.LocalizedError</c>, or any short-name <c>Error</c>/<c>LocalizedError</c>
/// alias the parser surfaces, and registers each on the per-module
/// <see cref="ModuleEmissionContext"/> with a stable id (>= 1, alphabetical-by-name
/// ordering for cross-run determinism). id 0 is reserved for "untyped" — the existing
/// stringification fallback path.
/// </summary>
/// <remarks>
/// This precompute pass is the foundation for the wire-format extension
/// (<see cref="WrapperEmitter"/>'s 5-param error callback gains an
/// <c>errorTypeId</c> discriminator), the Swift-side cascade helper
/// (<c>_dispatchSwiftError</c>) that does the ordered <c>as?</c> casts, and the C#
/// async-callback dispatcher that reconstructs <c>SwiftException&lt;TError&gt;</c>
/// from the registered id.
///
/// Layer 1 only builds the in-memory registry. Subsequent layers consume the
/// registry to drive emission of the matching C# / Swift surface.
/// </remarks>
public static class ErrorEnumRegistryEmitter
{
    /// <summary>
    /// Walks the module decl tree and registers every error-conforming concrete type
    /// (enum, struct, class — including nested) on <paramref name="ctx"/>. Idempotent:
    /// the second call on the same context is a no-op. Iteration order is stable
    /// (alphabetical by module-qualified name) so id assignment is reproducible across
    /// runs and test invocations.
    /// </summary>
    public static void Precompute(ModuleDecl moduleDecl, ModuleEmissionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(moduleDecl);
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.ErrorTypeRegistryComputed)
            return;
        ctx.ErrorTypeRegistryComputed = true;

        // Collect into an ordered map so id assignment is deterministic across runs and
        // each entry retains its source TypeDecl's availability annotations (consumed by
        // the cascade dispatcher to gate `as?` references against the strictest OS minimum
        // declared by any registered type — e.g. WeatherError = iOS 16 forces the WeatherKit
        // dispatcher to `@available(iOS 16, *)`). Cross-module error types referenced by
        // this module are registered by their own module's registry; Layer 1 stays scoped
        // to types declared inside this module.
        var errorTypes = new SortedDictionary<string, IReadOnlyList<AvailabilityAnnotation>?>(StringComparer.Ordinal);
        CollectErrorConformingTypes(moduleDecl.Types, errorTypes, ctx);

        foreach (var (name, availability) in errorTypes)
            ctx.RegisterErrorTypeId(name, availability);
    }

    private static void CollectErrorConformingTypes(
        IEnumerable<TypeDecl> typeDecls,
        SortedDictionary<string, IReadOnlyList<AvailabilityAnnotation>?> sink,
        ModuleEmissionContext ctx)
    {
        foreach (var typeDecl in typeDecls)
        {
            if (ConformsToError(typeDecl) && IsInstantiable(typeDecl) && IsRegisterable(typeDecl, ctx))
            {
                // Walk parent chain so a nested error type inherits `@available` from any
                // ancestor (e.g. `enum SomeService.FetchError` picks up SomeService's
                // iOS 16 floor). Cdecl wrappers don't inherit ancestor availability
                // automatically — same reason `MergeAvailabilityFromAncestors` exists for
                // method/property wrappers.
                var availability = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
                    typeDecl.AvailabilityAnnotations, typeDecl.ParentDecl);
                sink[typeDecl.SwiftTypeName.ModuleQualifiedName] = availability;
            }

            // Recurse into nested types — Swift allows nested error enums (e.g.,
            // `extension SomeService { public enum FetchError: Error { ... } }`).
            // Recursion is unconditional so we descend through public non-error
            // outer types (e.g. a plain namespace struct) to reach an emittable
            // nested error. Nested types whose own parent chain hits an SPI,
            // @usableFromInline internal, or underscore-suppressed link are still
            // dropped — IsRegisterable re-walks the chain per-candidate.
            CollectErrorConformingTypes(typeDecl.Types, sink, ctx);
        }
    }

    /// <summary>
    /// Two-stage check: every link in the parent chain must be name-visible to the Swift
    /// cascade dispatcher's plain <c>import {Module}</c> (per <see cref="IsTypeSkippedByEmitter"/>),
    /// and no link in the chain can be open-generic. A type failing either stage drops
    /// from the registry and the cascade falls through to id 0
    /// (untyped <see cref="SwiftException"/>) — the correct degradation.
    ///
    /// The visibility gate is STRICTER than <c>HandleBaseDecl</c>'s skip set because the
    /// cascade dispatcher operates from the wrapper module's import context, which sees
    /// only <c>public</c> declarations. C# bindings exist for some types the cascade can't
    /// reference (e.g. <c>@usableFromInline internal</c>) — those must not get a registry
    /// id even though their C# class is emitted.
    /// </summary>
    private static bool IsRegisterable(TypeDecl typeDecl, ModuleEmissionContext ctx)
    {
        // Walk self + every ancestor TypeDecl applying the same skip gates HandleBaseDecl
        // applies per-decl. If any link in the chain would be skipped from C# emission,
        // the cascade dispatcher cannot reference this type — drop the registry entry.
        for (BaseDecl? cursor = typeDecl; cursor is not null; cursor = cursor.ParentDecl)
        {
            if (cursor is not TypeDecl cursorTypeDecl)
                continue;
            if (IsTypeSkippedByEmitter(cursorTypeDecl, ctx))
                return false;
        }

        // Generic-parent gate: the cascade dispatcher renders module-qualified names
        // verbatim (`global::Module.Outer.Inner`), but a type whose parent chain contains
        // an open generic (e.g. `Outer<T>.Inner` or `Outer<T>` itself) requires either
        // a closed type argument or a fresh generic parameter on the dispatcher — neither
        // of which is available at precompute time. Drop these entries; runtime falls
        // through to untyped SwiftException.
        if (typeDecl.IsGeneric || IsNestedInGenericParent(typeDecl))
            return false;

        return true;
    }

    /// <summary>
    /// Per-decl visibility check for cascade-dispatcher purposes. A type fails this
    /// gate when the Swift wrapper module's plain <c>import {Module}</c> cannot
    /// reference it by name — i.e. when an <c>as? Module.SomeType</c> in the cascade
    /// would fail to compile.
    ///
    /// The gate is STRICTER than <c>HandleBaseDecl</c>'s skip set: HandleBaseDecl emits
    /// C# bindings for <c>@usableFromInline internal</c> types because they appear in
    /// the public signatures of <c>@inlinable</c> functions and need a referenceable
    /// C# type. The Swift cascade wrapper, by contrast, sits in a different module
    /// importing the framework and only sees <c>public</c> declarations — internal
    /// types (even with <c>@usableFromInline</c>) are name-invisible and produce
    /// "module X has no member named Y" / "no type named Y in module X" errors.
    ///
    /// Filtered types fall through to id 0 (untyped <see cref="SwiftException"/>),
    /// which is the correct degradation — a typed-throws bridge for an internal type
    /// the consumer can't name in their own code anyway buys nothing.
    /// </summary>
    private static bool IsTypeSkippedByEmitter(TypeDecl typeDecl, ModuleEmissionContext ctx)
    {
        // @_spi types are skipped from C# emission AND invisible to plain `import`.
        if (typeDecl.IsSpiProtected)
            return true;

        // `@usableFromInline internal` (and plain `internal`) types: C# binding is
        // emitted, but the wrapper module's `import` only sees `public`. The cascade
        // dispatcher's `as? Module.Type` would not resolve.
        if (typeDecl.IsModuleInternal)
            return true;

        // Underscore-prefixed types not structurally required are suppressed.
        if (typeDecl.SwiftTypeName != null
            && ctx.IsUnderscoreSuppressed(typeDecl.SwiftTypeName.ToString()))
            return true;

        // Apple-supplement-owned types: the framework-package handler skips emission
        // so consumers reach the supplement's canonical projection instead. The
        // cascade dispatcher would otherwise reference a type the framework C# never
        // declares.
        if (typeDecl.SwiftTypeName != null
            && AppleSupplementResolver.TryResolve(typeDecl.SwiftTypeName, typeDecl.SwiftTypeName.Module, out _))
            return true;

        return false;
    }

    private static bool IsNestedInGenericParent(TypeDecl typeDecl)
    {
        for (var parent = typeDecl.ParentDecl; parent is not null; parent = parent.ParentDecl)
        {
            if (parent is TypeDecl parentTypeDecl && parentTypeDecl.IsGeneric)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Filters out types that conform to Error but cannot be instantiated at runtime —
    /// most notably Swift caseless namespace enums (e.g. WeatherKit-style
    /// <c>enum WeatherErrorNamespace { static let ... }</c> with a <c>LocalizedError</c>
    /// extension). The C# emission projects a caseless enum as a <c>static class</c>,
    /// which can't be a generic type argument and can't be cast to. Registering one
    /// would produce <c>SwiftException&lt;StaticClass&gt;</c> code that fails to compile,
    /// so the cascade simply skips it and falls through to the untyped <c>SwiftException</c>.
    /// Struct and class error types are always considered instantiable.
    /// </summary>
    private static bool IsInstantiable(TypeDecl typeDecl) =>
        typeDecl is not EnumDecl enumDecl || enumDecl.Cases.Count > 0;

    /// <summary>
    /// Returns true when the decl conforms to <c>Swift.Error</c> /
    /// <c>Foundation.LocalizedError</c> directly (or via the short-name aliases
    /// the parser may surface for stdlib protocols). Concrete-type conformance only —
    /// the conformance list on <see cref="EnumDecl"/>, <see cref="StructDecl"/>, and
    /// <see cref="ClassDecl"/> is the canonical source.
    /// </summary>
    public static bool ConformsToError(TypeDecl typeDecl)
    {
        var conformances = typeDecl switch
        {
            EnumDecl e => e.Conformances,
            StructDecl s => s.Conformances,
            ClassDecl c => c.Conformances,
            _ => null,
        };
        if (conformances is null)
            return false;

        foreach (var conformance in conformances)
        {
            var protocolName = conformance.Protocol;
            var qual = protocolName.ToString();
            if (qual == "Swift.Error" || qual == "Foundation.LocalizedError")
                return true;
            // Short-name fallback: parser sometimes surfaces stdlib protocols without
            // their module prefix (mirroring the Sendable-detection precedent in
            // TypeHandlerHelpers.cs:569). LocalizedError appears with both Foundation
            // and bare-name forms in different swiftinterface flavors.
            if (string.IsNullOrEmpty(protocolName.Module) &&
                (protocolName.Name == "Error" || protocolName.Name == "LocalizedError"))
                return true;
        }
        return false;
    }
}
