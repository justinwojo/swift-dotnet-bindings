// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Phase 4 plain-throws → typed-exception bridge — Layer 1 (foundation).
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
        CollectErrorConformingTypes(moduleDecl.Types, errorTypes, parentChain: null);

        foreach (var (name, availability) in errorTypes)
            ctx.RegisterErrorTypeId(name, availability);
    }

    private static void CollectErrorConformingTypes(
        IEnumerable<TypeDecl> typeDecls,
        SortedDictionary<string, IReadOnlyList<AvailabilityAnnotation>?> sink,
        BaseDecl? parentChain)
    {
        foreach (var typeDecl in typeDecls)
        {
            if (ConformsToError(typeDecl) && IsInstantiable(typeDecl))
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
            CollectErrorConformingTypes(typeDecl.Types, sink, typeDecl);
        }
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
