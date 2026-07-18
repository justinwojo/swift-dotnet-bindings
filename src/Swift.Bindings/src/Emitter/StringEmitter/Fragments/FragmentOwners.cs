// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;

namespace BindingsGeneration;

/// <summary>
/// Builds the <see cref="FragmentOwner"/> for a declaration being emitted.
/// </summary>
/// <remarks>
/// The recovery scope is not chosen here — it comes from
/// <see cref="RecoveryUnitClassifier"/>, the same table the recovery graph is built from, so a
/// fragment and the graph node it will be matched against cannot disagree about how withdrawable the
/// thing is. Only the two scopes whose ids need a qualifier the classifier does not carry (an
/// accessor group must normalize to its property, a shared-helper bundle must name its bundle) are
/// resolved here.
/// </remarks>
public static class FragmentOwners
{
    /// <summary>The owner for a declaration's public C# surface / Swift wrapper pair.</summary>
    public static FragmentOwner ForDecl(BaseDecl decl)
    {
        ArgumentNullException.ThrowIfNull(decl);
        var declId = DeclIdFactory.ForDecl(decl) ?? DeclIdFactory.ForMember(BindingItemKind.Method, decl.Name, decl.ParentDecl);
        return ForDeclId(declId);
    }

    /// <summary>
    /// The owner for the Swift <c>@_cdecl</c> wrapper a declaration emits, as distinct from its C#
    /// surface.
    /// </summary>
    /// <remarks>
    /// One declaration writes into both planes, and the two are separately withdrawable — a wrapper
    /// can be stripped while its C# surface stands, and the recovery classifier scopes them
    /// differently. Sharing one owner across both planes would make an interval in the wrapper
    /// indistinguishable from one in the generated C#, which is exactly the question a diagnostic on
    /// the wrapper is asking.
    /// </remarks>
    public static FragmentOwner ForDeclWrapper(BaseDecl decl)
    {
        ArgumentNullException.ThrowIfNull(decl);
        var declId = DeclIdFactory.ForDecl(decl) ?? DeclIdFactory.ForMember(BindingItemKind.Method, decl.Name, decl.ParentDecl);

        // A type or module shell has no wrapper of its own — the Swift plane's outer scopes are the
        // same containers as the C# plane's, and re-roling them would invent an artifact that is
        // never emitted.
        return declId.Kind is BindingItemKind.Module or BindingItemKind.Type
            ? ForDeclId(declId)
            : ForDeclId(declId, ArtifactRole.SwiftWrapper);
    }

    /// <summary>The owner for an already-resolved declaration identity.</summary>
    public static FragmentOwner ForDeclId(DeclId declId) => ForDeclId(declId, RoleFor(declId.Kind));

    /// <summary>The owner for a declaration identity in a specific artifact role.</summary>
    public static FragmentOwner ForDeclId(DeclId declId, ArtifactRole role)
    {
        var artifact = ArtifactId.Create(declId, role);
        var artifactKind = RecoveryUnitClassifier.FromArtifact(role, declId.Kind);
        var scope = RecoveryUnitClassifier.ScopeOf(artifactKind);
        return new FragmentOwner(artifact, UnitFor(declId, scope));
    }

    /// <summary>The owner for the module-level scope everything else nests inside.</summary>
    public static FragmentOwner ForModule(ModuleDecl moduleDecl)
    {
        ArgumentNullException.ThrowIfNull(moduleDecl);
        return ForModule(DeclIdFactory.ForModule(moduleDecl));
    }

    /// <summary>The owner for a module identity.</summary>
    public static FragmentOwner ForModule(DeclId moduleId) =>
        new(
            ArtifactId.Create(moduleId, ArtifactRole.CSharpPublic),
            RecoveryUnitId.Create(moduleId, RecoveryScope.Module));

    /// <summary>
    /// The owner for a shared helper bundle on a module. <paramref name="bundleKey"/> separates the
    /// independent bundles a module owns; without it every helper collapses onto one unit.
    /// </summary>
    public static FragmentOwner ForSharedHelper(DeclId moduleId, string bundleKey) =>
        new(
            ArtifactId.Create(moduleId, ArtifactRole.MetadataHelper),
            RecoveryUnitId.ForSharedHelper(moduleId, bundleKey));

    /// <summary>
    /// Maps a declaration kind to the artifact role its emission renders. Members render their
    /// public C# surface (the P/Invoke and Swift wrapper hang off the same declaration and are
    /// distinguished by role, not by a separate id), types render their shell.
    /// </summary>
    private static ArtifactRole RoleFor(BindingItemKind kind) => kind switch
    {
        BindingItemKind.Module => ArtifactRole.ModuleInitializer,
        _ => ArtifactRole.CSharpPublic,
    };

    /// <summary>
    /// Builds the unit id for a scope, routing the two scopes whose ids need normalizing or
    /// qualifying through their dedicated factories.
    /// </summary>
    private static RecoveryUnitId UnitFor(DeclId declId, RecoveryScope scope) => scope switch
    {
        RecoveryScope.AccessorGroup => RecoveryUnitId.ForAccessorGroup(declId),
        // A conformance edge names two declarations and a shared-helper bundle names a bundle key;
        // neither is derivable from the declaration being emitted, and the classifier never returns
        // them for a declaration-rooted artifact. Fall back to the declaration's own surface rather
        // than fabricating a qualifier that would collide with every other instance.
        RecoveryScope.ConformanceEdge or RecoveryScope.SharedHelperBundle =>
            RecoveryUnitId.Create(declId, RecoveryScope.LeafApi),
        _ => RecoveryUnitId.Create(declId, scope),
    };
}
