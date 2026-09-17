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
        // Nested types are dispatched only from inside their parent's handler, so a type
        // whose handler never runs, or returns before its nested-type dispatch, takes its
        // whole subtree with it. Registering a descendant of such a type would name a type
        // no handler ever emits.
        if (!ReachesTypeHandler(typeDecl, typeDatabase, emissionContext))
            return;

        if (WouldEmitAsOpaqueTombstone(typeDecl, moduleDecl, typeDatabase))
            emissionContext.AddSilentTombstone(typeDecl.SwiftTypeName.ModuleQualifiedName);

        if (!DispatchesNestedTypes(typeDecl))
            return;

        foreach (var nested in typeDecl.Types)
            Visit(nested, moduleDecl, typeDatabase, emissionContext);
    }

    /// <summary>
    /// Returns true iff the type gets past every gate that stops it before its handler emits
    /// anything: the <c>HandleBaseDecl</c> gates (IHandler.cs) and the shared type-level skip
    /// conditions every handler evaluates first. A type stopped here is not emitted at all, so
    /// neither it nor anything nested in it can be a silent tombstone.
    /// </summary>
    private static bool ReachesTypeHandler(TypeDecl typeDecl, ITypeDatabase typeDatabase, ModuleEmissionContext emissionContext)
    {
        // HandleBaseDecl gate 0: a type an earlier emission attempt faulted on is denied.
        // This pre-pass runs inside the attempt, so it sees the same denylist.
        if (EmitterFaultGate.IsDenied(DeclIdFactory.ForType(typeDecl), out _))
            return false;

        // HandleBaseDecl: underscore-prefixed types that are not structurally
        // required are suppressed from C# output before any handler dispatches.
        if (typeDecl.SwiftTypeName != null
            && emissionContext.IsUnderscoreSuppressed(typeDecl.SwiftTypeName.ToString()))
            return false;

        // HandleBaseDecl: @_spi types are never emitted.
        if (typeDecl.IsSpiProtected)
            return false;

        // HandleBaseDecl: types owned by the Apple supplement (SwiftBindings.Apple) are
        // suppressed so the framework package does not re-emit a parallel copy of the
        // supplement's canonical projection.
        if (typeDecl.SwiftTypeName != null
            && AppleSupplementResolver.TryResolve(typeDecl.SwiftTypeName, typeDecl.SwiftTypeName.Module, out _))
            return false;

        // HandleBaseDecl: SwiftUI View types are collected by the SwiftUI bridge and
        // never emitted through a regular struct/class handler.
        if ((typeDecl is StructDecl || typeDecl is ClassDecl)
            && SwiftUIViewDetector.IsSwiftUIView(typeDecl))
            return false;

        // ProtocolHandler emits no type-level skip, and a protocol has no nested types;
        // leave it to WouldEmitAsOpaqueTombstone's unconditional exclusion.
        if (typeDecl is ProtocolDecl)
            return true;

        // All other handlers: any type-level skip condition (unsupported generic constraint,
        // variadic parameter pack, indeterminate struct layout, unlowerable PWT shape)
        // means the type is never emitted AT ALL — opaque branch included — so it can
        // never be a silent tombstone. Evaluating the shared condition list keeps this
        // mirror from drifting when a new skip condition lands: this registrar once
        // hand-mirrored only two of the conditions, so a fully-skipped type matching a
        // later-added one was registered here and tripped AssertSilentTombstoneInvariant.
        return TypeSkipConditions.FirstMatch(typeDecl, typeDatabase, out _) is null;
    }

    /// <summary>
    /// Returns false for the handler paths that return before dispatching the type's nested
    /// declarations. Every other path (normal emission, opaque branch, namespace enum, simple
    /// enum, cross-module extension, namespace facade) hands <c>Types</c> back to
    /// <c>HandleBaseDecl</c>.
    /// </summary>
    private static bool DispatchesNestedTypes(TypeDecl typeDecl) =>
        // EnumHandler: a single-case payload-less enum is skipped as zero-size before the
        // nested-type dispatch.
        typeDecl is not EnumDecl { IsNamespaceEnum: false, HasAssociatedValueCases: false, Cases.Count: 1 };

    /// <summary>
    /// Returns true iff the matching type handler would reach the opaque-tombstone
    /// branch (<c>TypeAnnotationHelper.EmitOpaqueTypeAnnotation</c>). Every early
    /// return here corresponds to a handler-side early exit that emits the type via
    /// a non-opaque path (namespace static class, C# enum value type, cross-module
    /// extension class) or skips emission entirely. Callers have already established
    /// <see cref="ReachesTypeHandler"/>.
    /// </summary>
    private static bool WouldEmitAsOpaqueTombstone(TypeDecl typeDecl, ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
    {
        // ProtocolHandler has no opaque-tombstone branch — it emits an interface/proxy
        // surface. Nested protocols (protocols declared inside a struct/class/enum)
        // walk through this pre-pass as TypeDecls but would never be registered by a
        // real handler, so skip them unconditionally.
        if (typeDecl is ProtocolDecl)
            return false;

        // ClassHandler: a top-level cross-module extension emits as a static extension
        // class, not an opaque ISwiftObject wrapper. The handler restricts this to
        // module-level receivers: a type nested under an extended foreign type is owned by
        // this module and emits through the normal path, opaque branch included.
        if (typeDecl is ClassDecl classDecl
            && !string.IsNullOrEmpty(classDecl.SwiftTypeName.Module)
            && classDecl.SwiftTypeName.Module != moduleDecl.Name
            && classDecl.ParentDecl is ModuleDecl)
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
        // ClassDecl guard above, including its module-level restriction.
        if (typeDecl is StructDecl structDecl
            && !string.IsNullOrEmpty(structDecl.SwiftTypeName.Module)
            && structDecl.SwiftTypeName.Module != moduleDecl.Name
            && structDecl.ParentDecl is ModuleDecl)
            return false;

        // EnumHandler: three early-return paths precede the opaque branch.
        if (typeDecl is EnumDecl enumDecl)
        {
            // Caseless enum → emitted as a static class (namespace / static-member holder) or
            // an empty value enum (uninhabited marker); either is a real emitted type, not an
            // opaque tombstone.
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
