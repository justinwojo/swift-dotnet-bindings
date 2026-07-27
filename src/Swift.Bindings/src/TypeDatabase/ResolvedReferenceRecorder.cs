// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Turns a resolved <see cref="TypeRecord"/> into the package references the emitted csproj
/// needs, by observing the record's own managed projection rather than the identity that was
/// looked up.
/// </summary>
/// <remarks>
/// <para>The oracle is deliberately the <em>resolved projection</em>, not the Swift module the
/// identity came from. Swift-module ownership cannot answer the question: the four
/// <c>Foundation</c> entries in <c>FoundationDatabase.xml</c> project into four different
/// managed homes — <c>Swift.Foundation</c> (the supplement), <c>System</c> (<c>Guid</c>), the
/// ObjC workload's <c>Foundation</c> (<c>NSOperationQueue</c>) and a bare primitive
/// (<c>Date</c> → <c>double</c>) — so "the type came from Foundation" implies nothing about
/// which assembly a consumer must reference. What the emitted C# actually names is the
/// projection, so the projection is what decides the reference.</para>
/// <para>Called from the two — and only two — places a resolved record surfaces:
/// <see cref="TypeResolver.TryResolve"/> (the <see cref="NamedTypeSpec"/> strategy chain) and
/// <c>TypeDatabase.TryGetTypeRecordWithoutSupplement</c> (the raw-<see cref="SwiftTypeName"/>
/// database cascade). Hooking the chokepoints rather than the individual strategies means a
/// new strategy inherits the recording for free and cannot silently reintroduce a
/// reference-less resolution path.</para>
/// <para>Over-recording is safe and under-recording is not: a reference to a package whose
/// types the binding ends up not naming costs an unused restore edge, whereas a missing one is
/// a hard compile failure in the consumer. The recorded sets are therefore a sound superset,
/// matching the policy the in-emission verify-recover loop already relies on (a withdrawal only
/// ever shrinks the emitted surface, never grows the reference set it needs).</para>
/// </remarks>
internal static class ResolvedReferenceRecorder
{
    /// <summary>
    /// Records the package references implied by <paramref name="record"/>'s managed projection.
    /// Safe to call on every successful resolution; both collectors dedupe.
    /// </summary>
    /// <param name="record">The resolved type record, or null for a skip-style result.</param>
    /// <param name="callerHint">Provenance for the recorded reference (e.g. <c>"strategy:DatabaseLookup"</c>).</param>
    public static void Record(TypeRecord? record, string callerHint)
    {
        if (record is null)
            return;

        // Arm 1 — the Apple supplement. Fires for manifest-resolved identities (already covered
        // by AppleSupplementStrategy) and, crucially, for the hand-rolled canonicals that reach
        // the generator through the XML type databases and are excluded from the manifest by
        // design. EffectiveManagedProjection is the name the emitted C# writes.
        if (AppleSupplementResolver.IsSupplementOwnedNamespace(record.EffectiveManagedProjection.Namespace))
        {
            AppleSupplementReferences.Record(record.SwiftTypeName.ModuleQualifiedName, callerHint);
        }

        // Arm 2 — sibling binding packages. Keyed on the declaring Swift module because that is
        // what apple-frameworks.json's packageId map is keyed on; the read side drops the
        // module being generated and every module with no registered package.
        CrossModuleBindingReferences.Record(record.SwiftTypeName.Module, callerHint);
    }

    /// <summary>
    /// Records the sibling-package reference implied by naming a foreign module's type that did NOT
    /// resolve to a <see cref="TypeRecord"/> — the concrete-class fallback, which projects an
    /// unresolved Apple class straight to a <c>ClassProjection</c> bearing its module-qualified name.
    /// </summary>
    /// <remarks>
    /// The emitted C# names the foreign type either way, so the reference obligation is identical to
    /// the resolved path; only the evidence differs. This is not a redundant duplicate of
    /// <see cref="Record"/>: that one cannot fire here, because there is no record to observe.
    /// <para>Arm 1 (the Apple supplement) is deliberately absent rather than forgotten. It keys on the
    /// resolved projection's NAMESPACE, which a fallback does not produce, and it could not fire
    /// anyway — the fallback is gated on <c>AppleFrameworkRegistry.IsConcreteClassFallbackModule</c>,
    /// and no module in that set projects into a supplement-owned namespace. Should one ever be added
    /// that does, this needs the supplement arm too.</para>
    /// <para>The Swift MODULE is the right key even when the fallback renames the type: a
    /// <c>typeRemaps</c> entry changes the emitted type's spelling, never which package ships it, and
    /// <c>apple-frameworks.json</c>'s <c>packageId</c> map is itself keyed on the Swift module. This
    /// matches arm 2 of <see cref="Record"/>, which keys on the module for the same reason.</para>
    /// </remarks>
    public static void RecordUnresolvedModuleReference(string? swiftModule, string callerHint)
    {
        if (string.IsNullOrEmpty(swiftModule))
            return;

        CrossModuleBindingReferences.Record(swiftModule!, callerHint);
    }
}
