// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Pre-emission pass that walks all type declarations in a module and registers any
/// whose handler would emit them with <c>[OpaqueSwiftType]</c> — i.e. the class-body
/// "silent tombstone" branch — into <see cref="ModuleEmissionContext"/>.
///
/// Runs before any type/method emission so that SB0002 diagnostics on call sites
/// (which consult <see cref="ModuleEmissionContext.IsSilentTombstone"/>) fire
/// regardless of the declaration order in which types are emitted. Without this
/// pre-pass the call-site check would be declaration-order-dependent: a caller
/// emitted before its tombstoned return type would miss the annotation.
///
/// The predicate in <see cref="WouldEmitAsOpaqueTombstone"/> MUST mirror the
/// handler-time decisions exactly; a false positive pollutes <c>binding-emission-report.json</c>
/// and produces spurious SB0002 at call sites that reference a perfectly-usable type
/// (e.g., a simple C# enum or a namespace static class). It also breaks the cookie-resolution
/// invariant — a tombstoned type that no handler actually emits leaves dangling
/// <c>GetTypeMetadataOrThrow&lt;T&gt;()</c> references in the generated source.
/// <see cref="EmissionReportEmitter.AssertSilentTombstoneInvariant"/> verifies the
/// registry ⊆ actually-emitted invariant before report write and throws on divergence.
/// See the comments on each early-return for which handler it pairs with.
///
/// Registration key is <see cref="SwiftTypeName.ModuleQualifiedName"/>, so nested
/// types like <c>Module.Outer.Inner</c> match the lookup against
/// <c>NamedTypeSpec.Name</c> (which is also the full dotted path).
/// </summary>
internal static class SilentTombstoneRegistrar
{
    public static void Precompute(ModuleDecl moduleDecl, ITypeDatabase typeDatabase, ModuleEmissionContext emissionContext)
    {
        foreach (var typeDecl in moduleDecl.Types)
            Visit(typeDecl, moduleDecl, typeDatabase, emissionContext);
    }

    private static void Visit(TypeDecl typeDecl, ModuleDecl moduleDecl, ITypeDatabase typeDatabase, ModuleEmissionContext emissionContext)
    {
        if (WouldEmitAsOpaqueTombstone(typeDecl, moduleDecl, typeDatabase, emissionContext))
            emissionContext.AddSilentTombstone(typeDecl.SwiftTypeName.ModuleQualifiedName);

        foreach (var nested in typeDecl.Types)
            Visit(nested, moduleDecl, typeDatabase, emissionContext);
    }

    /// <summary>
    /// Returns true iff the matching type handler would reach the opaque-tombstone
    /// branch (<c>TypeAnnotationHelper.EmitOpaqueTypeAnnotation</c>). Every early
    /// return here corresponds to a handler-side early exit that emits the type via
    /// a non-opaque path (namespace static class, C# enum value type, cross-module
    /// extension class) or skips emission entirely.
    /// </summary>
    private static bool WouldEmitAsOpaqueTombstone(TypeDecl typeDecl, ModuleDecl moduleDecl, ITypeDatabase typeDatabase, ModuleEmissionContext emissionContext)
    {
        // ProtocolHandler has no opaque-tombstone branch — it emits an interface/proxy
        // surface. Nested protocols (protocols declared inside a struct/class/enum)
        // walk through this pre-pass as TypeDecls but would never be registered by a
        // real handler, so skip them unconditionally.
        if (typeDecl is ProtocolDecl)
            return false;

        // HandleBaseDecl (IHandler.cs): underscore-prefixed types that are not structurally
        // required are suppressed from C# output before any handler dispatches.
        if (typeDecl.SwiftTypeName != null
            && emissionContext.IsUnderscoreSuppressed(typeDecl.SwiftTypeName.ToString()))
            return false;

        // HandleBaseDecl (IHandler.cs): @_spi types are never emitted.
        if (typeDecl.IsSpiProtected)
            return false;

        // HandleBaseDecl (IHandler.cs): types owned by the Apple supplement
        // (SwiftBindings.Apple) are suppressed here so the framework package does not
        // re-emit a parallel copy of the supplement's canonical projection.
        if (typeDecl.SwiftTypeName != null
            && AppleSupplementResolver.TryResolve(typeDecl.SwiftTypeName, typeDecl.SwiftTypeName.Module, out _))
            return false;

        // HandleBaseDecl (IHandler.cs): SwiftUI View types are collected by the
        // SwiftUI bridge and never emitted through a regular struct/class handler.
        if ((typeDecl is StructDecl || typeDecl is ClassDecl)
            && SwiftUIViewDetector.IsSwiftUIView(typeDecl))
            return false;

        // All handlers: unsupported generic constraint → type is not emitted.
        if (GenericTypeEmitter.TryGetUnsupportedConstraint(typeDecl, out _))
            return false;

        // All handlers: generic type whose metadata-accessor ABI cannot be lowered
        // (TypeMetadataAccessorSkipGate) → type is not emitted. Only generic types
        // produce a non-null helper context.
        var pinvokeContext = PInvokeHelperContext.CreateIfGeneric(typeDecl, typeDatabase);
        if (pinvokeContext is not null && pinvokeContext.HasIndeterminatePwtShape)
            return false;

        // ClassHandler: cross-module extension emits as a static extension class,
        // not an opaque ISwiftObject wrapper.
        if (typeDecl is ClassDecl classDecl
            && !string.IsNullOrEmpty(classDecl.SwiftTypeName.Module)
            && classDecl.SwiftTypeName.Module != moduleDecl.Name)
            return false;

        // FrozenStructHandler / NonFrozenStructHandler: cross-module struct extensions
        // are emitted via CrossModuleExtensionEmitter (a separate static extension surface),
        // NOT via the opaque-tombstone branch. Both handlers carry the same cross-module
        // guard because the parser sets StructDecl.IsFrozen from the extension node's own
        // attributes (the extension never carries @frozen), so a foreign frozen struct
        // like Swift.Array or Foundation.Date dispatches to NonFrozenStructHandler — not
        // FrozenStructHandler — when surfaced as an extension receiver. Without this
        // guard, a foreign frozen struct gets registered as a silent tombstone here but
        // the handler exits via the cross-module path without calling AddEmittedOpaqueType,
        // leaving the invariant check in AssertSilentTombstoneInvariant to fire. Mirror the
        // ClassDecl guard above for every TypeDecl that participates in the cross-module
        // extension path.
        if (typeDecl is StructDecl structDecl
            && !string.IsNullOrEmpty(structDecl.SwiftTypeName.Module)
            && structDecl.SwiftTypeName.Module != moduleDecl.Name)
            return false;

        // EnumHandler: three early-return paths precede the opaque branch.
        if (typeDecl is EnumDecl enumDecl)
        {
            // Namespace (caseless) enum → emitted as static class.
            if (enumDecl.IsNamespaceEnum)
                return false;

            // Simple-enum path → emitted as C# enum value type. Mirror the
            // handler's "not demoted from simple" check via the TypeRecord flag.
            var wasDemotedFromSimple = typeDatabase.TryGetTypeRecord(enumDecl.SwiftTypeName, out var rec)
                && rec is not null
                && !rec.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
            if (!wasDemotedFromSimple &&
                ((enumDecl.IsSimpleEnum && EnumHandler.CanSafelyEmitAsSimpleEnum(enumDecl)) ||
                 (enumDecl.IsStringRawValueSimpleEnum && EnumHandler.CanSafelyEmitAsSimpleEnum(enumDecl))))
                return false;

            // Single-case / no-payload enum → skipped entirely (zero-size).
            if (enumDecl.Cases.Count == 1 && !enumDecl.HasAssociatedValueCases)
                return false;
        }

        // Final member-count predicate — the opaque branch only fires when the
        // type has no emittable members and at least one skipped one.
        var (emittable, skipped) = MemberEmissionValidator.CountEmittableMembers(typeDecl, typeDatabase);
        return emittable == 0 && skipped > 0;
    }
}
