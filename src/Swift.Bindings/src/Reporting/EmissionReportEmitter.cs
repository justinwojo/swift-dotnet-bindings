// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BindingsGeneration;

/// <summary>
/// Report model for emission-level metrics (wrapper strategies, skip reasons, conformance decisions).
/// Written to binding-emission-report.json alongside binding-report.json.
/// </summary>
public class EmissionReport
{
    [JsonProperty("module")]
    public string Module { get; set; } = "";

    [JsonProperty("wrapperStrategyCounts")]
    public Dictionary<string, int> WrapperStrategyCounts { get; set; } = new();

    [JsonProperty("skipReasons")]
    public Dictionary<string, int> SkipReasons { get; set; } = new();

    [JsonProperty("conformanceDecisions")]
    public ConformanceDecisionsSummary ConformanceDecisions { get; set; } = new();

    /// <summary>
    /// Number of distinct EveryProtocol proxy classes suppressed at emission because their
    /// conformance was not emitted. Narrower than
    /// <see cref="ConformanceDecisionsSummary.SkippedAtEmission"/>: it excludes unsupported-module
    /// proxy skips and read-only proxies (see <c>ProtocolProxyEmissionPolicy.Decide</c>), counting
    /// only the protocols whose <c>{Name}Proxy</c> class was actually withheld — the set the
    /// emit-time reference gate consumes to drop or stub <c>new {Name}Proxy(…)</c> references.
    /// </summary>
    [JsonProperty("suppressedProxyClassCount")]
    public int SuppressedProxyClassCount { get; set; }

    /// <summary>
    /// Module-qualified names of types that were emitted with [OpaqueSwiftType] but have
    /// zero usable surface (all members skipped). The type IS present as a C# class
    /// declaration — metadata-cookie references to it resolve correctly — but the surface
    /// is opaque-only (the consumer can hold an instance but cannot call any methods on it).
    /// Call sites whose return type is a silent tombstone are flagged with the SB0002
    /// diagnostic so audits can grep them out.
    ///
    /// Invariant: every name here also appears in <see cref="ModuleEmissionContext.EmittedOpaqueTypes"/>.
    /// <see cref="EmissionReportEmitter.Emit"/> throws on divergence — a break would mean
    /// the registrar predicate (<c>SilentTombstoneRegistrar.WouldEmitAsOpaqueTombstone</c>)
    /// has drifted from handler reality and a metadata-cookie reference to a tombstoned
    /// type would dangle in the generated source.
    /// </summary>
    [JsonProperty("silentTombstones")]
    public List<string> SilentTombstones { get; set; } = new();

    /// <summary>
    /// Conformer pairings the CSM engine rejected at the pairing step because the
    /// conformer fails to satisfy a non-selected protocol constraint on its generic
    /// parameter. See <see cref="ConcreteSpecializationEngine.CsmRejectedPairing"/> for
    /// the mechanism. Populated from <see cref="ConcreteSpecializationEngine.RejectedPairings"/>.
    /// Sorted for deterministic output.
    /// </summary>
    [JsonProperty("csmConformerRejections")]
    public List<CsmConformerRejectionEntry> CsmConformerRejections { get; set; } = new();

    /// <summary>
    /// Swift textual forms (e.g. <c>any AttributeKind</c>) of protocol existentials that could not
    /// be projected to a real C# type and degraded to <c>object</c> on a generated surface. Each
    /// distinct type appears once. <see cref="EmissionReportEmitter.Emit"/> raises one loud
    /// SWIFTBIND023 warning per entry so the degradation — previously visible only via the
    /// consumer-facing <c>[UnsupportedSwiftType]</c> attribute — is surfaced at generation time.
    /// Sorted for deterministic output.
    /// </summary>
    [JsonProperty("degradedExistentials")]
    public List<string> DegradedExistentials { get; set; } = new();

    /// <summary>
    /// Member descriptors (e.g. <c>Foo.bar setter</c>, <c>Foo.consume(value: any P)</c>) of EveryProtocol
    /// reverse-dispatch receivers whose existential payload referenced a protocol proxy suppressed at
    /// generation. The receiver kept its <c>[UnmanagedCallersOnly]</c> signature but degraded its body
    /// to a fail-fast stub (no C#-side proxy exists to marshal the value back across the boundary).
    /// <see cref="EmissionReportEmitter.Emit"/> raises one SWIFTBIND061 warning per entry so the
    /// reverse-dispatch degradation is visible at generation time rather than only as a runtime
    /// fail-fast. Sorted for deterministic output.
    /// </summary>
    [JsonProperty("degradedReverseDispatchReceivers")]
    public List<string> DegradedReverseDispatchReceivers { get; set; } = new();
}

/// <summary>
/// Serializable view of <see cref="ConcreteSpecializationEngine.CsmRejectedPairing"/>.
/// Mirrors the record fields one-for-one for stable JSON output.
/// </summary>
public class CsmConformerRejectionEntry
{
    [JsonProperty("parentType")]
    public string ParentType { get; set; } = "";

    [JsonProperty("genericParam")]
    public string GenericParam { get; set; } = "";

    [JsonProperty("selectedProtocol")]
    public string SelectedProtocol { get; set; } = "";

    [JsonProperty("conformer")]
    public string Conformer { get; set; } = "";

    [JsonProperty("missingConstraint")]
    public string MissingConstraint { get; set; } = "";

    [JsonProperty("reason")]
    public string Reason { get; set; } = "";
}

/// <summary>
/// Summary of EveryProtocol conformance emission decisions.
/// </summary>
public class ConformanceDecisionsSummary
{
    [JsonProperty("emittedInSource")]
    public int EmittedInSource { get; set; }

    [JsonProperty("skippedAtEmission")]
    public int SkippedAtEmission { get; set; }

    [JsonProperty("note")]
    public string Note { get; set; } = "Emitted conformances are stripped by post-processor Pattern 1 (unconditional EveryProtocol removal)";
}

/// <summary>
/// Emits the binding-emission-report.json file from ModuleEmissionContext data.
/// Follows the same pattern as ReportEmitter.
/// </summary>
public static class EmissionReportEmitter
{
    public static void Emit(ModuleEmissionContext emissionContext, string moduleName, string outputDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(emissionContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        AssertSilentTombstoneInvariant(emissionContext, moduleName);

        var report = BuildReport(emissionContext, moduleName);

        var reportPath = Path.Combine(outputDirectory, "binding-emission-report.json");
        var json = JsonConvert.SerializeObject(report, Formatting.Indented);
        File.WriteAllText(reportPath, json);

        // Log summary
        if (report.WrapperStrategyCounts.Count > 0)
        {
            var total = report.WrapperStrategyCounts.Values.Sum();
            logger.LogInformation("Emission: {Total} wrapper strategies assigned ({Breakdown})",
                total, string.Join(", ", report.WrapperStrategyCounts.Select(kv => $"{kv.Key}: {kv.Value}")));
        }

        if (report.SkipReasons.Count > 0)
        {
            var total = report.SkipReasons.Values.Sum();
            logger.LogInformation("Emission: {Total} methods skipped ({Breakdown})",
                total, string.Join(", ", report.SkipReasons.Select(kv => $"{kv.Key}: {kv.Value}")));
        }

        var decisions = report.ConformanceDecisions;
        if (decisions.EmittedInSource > 0 || decisions.SkippedAtEmission > 0)
        {
            logger.LogInformation("Emission: {Emitted} conformances emitted in source, {Skipped} skipped at emission",
                decisions.EmittedInSource, decisions.SkippedAtEmission);
        }

        if (report.SuppressedProxyClassCount > 0)
        {
            logger.LogInformation("Emission: {Count} proxy class(es) suppressed at emission (conformance not emitted)",
                report.SuppressedProxyClassCount);
        }

        if (report.SilentTombstones.Count > 0)
        {
            logger.LogInformation("Emission: {Count} silent tombstones (types emitted with [OpaqueSwiftType] but zero usable members)",
                report.SilentTombstones.Count);
        }

        if (report.CsmConformerRejections.Count > 0)
        {
            logger.LogInformation(
                "Emission: {Count} CSM conformer rejections (multi-constraint intersection filter)",
                report.CsmConformerRejections.Count);
        }

        // Defect E: turn the previously-silent existential→object degradation into a loud
        // per-type diagnostic. One SWIFTBIND023 warning per distinct existential that STILL degrades.
        // PAT (associated-type) existentials WITH known concrete conformers now project to
        // Swift.Runtime.ExistentialUnion in pure-read return positions (get-only property getters,
        // non-async/non-subscript method & free-function returns) and are NOT recorded here. What
        // remains in DegradedExistentials is the genuinely-unprojectable surface — no known conformers,
        // input/parameter/setter positions (ExistentialUnion is return-only), and the still-deferred
        // positions (optional `(any P)?`, async returns, subscripts, tuple/collection elements) — and
        // the binding author is told exactly which protocol surfaces lost type fidelity.
        foreach (var degraded in report.DegradedExistentials)
        {
            logger.LogWarning(
                "SWIFTBIND023: protocol existential '{ExistentialType}' could not be projected to a "
                + "concrete C# type and was degraded to 'object'. The member is still usable but loses "
                + "static type fidelity; this is recorded under degradedExistentials in "
                + "binding-emission-report.json.",
                degraded);
        }

        // Suppressed-proxy B3: turn the previously module-aborting receiver-channel failure into a loud
        // per-member diagnostic. The member's forward-dispatch surface stays partially usable — a produce
        // direction (getter / existential return) throws NotSupportedException because the suppressed proxy
        // can't be constructed, while a consume direction (setter / existential param) still round-trips a
        // Swift-vended value; its reverse-dispatch trampoline kept the vtable signature but fails fast if
        // Swift ever calls back into it, since there is no C#-side proxy to marshal the existential value.
        // One SWIFTBIND061 warning per affected member so the whole module still ships instead of aborting
        // with no .cs produced.
        foreach (var member in report.DegradedReverseDispatchReceivers)
        {
            logger.LogWarning(
                "SWIFTBIND061: reverse-dispatch receiver for '{Member}' degraded to a fail-fast stub "
                + "because its existential payload references a protocol proxy that was suppressed at "
                + "generation (its EveryProtocol conformance was not emitted). The member still ships: a "
                + "Swift callback into it fails fast, and on the forward surface a produce direction (a "
                + "getter or existential return) throws NotSupportedException because the proxy cannot be "
                + "constructed, while a consume direction (a setter or existential parameter) still "
                + "round-trips a Swift-vended value. Recorded under degradedReverseDispatchReceivers in "
                + "binding-emission-report.json.",
                member);
        }
    }

    /// <summary>
    /// Finding 53: emits the loud per-degradation diagnostics for the two mechanisms that were
    /// previously fully silent — <c>SWIFTBIND025</c> once per distinct <c>// Unsupported:</c>
    /// comment-drop, and <c>SWIFTBIND026</c> once per distinct Swift type that degraded to bare
    /// <c>object</c> with no <c>[UnsupportedSwiftType]</c> marker. Mirrors the SWIFTBIND023
    /// one-warning-per-distinct-entry shape above, but reads the <see cref="BindingReport"/> (where
    /// <see cref="ReportCollector"/> flowed the ambient accumulators) rather than the
    /// <see cref="ModuleEmissionContext"/>, since the emission sites have no context in scope.
    /// </summary>
    public static void EmitDegradationDiagnostics(BindingReport report, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var drop in report.UnsupportedCommentDrops)
        {
            logger.LogWarning(
                "SWIFTBIND025: {Drop} was left unbound and emitted as a `// Unsupported:` comment. "
                + "The declaration is absent from the generated bindings; this is recorded under "
                + "unsupportedCommentDrops in binding-report.json.",
                drop);
        }

        foreach (var degraded in report.ObjectDegradations)
        {
            logger.LogWarning(
                "SWIFTBIND026: Swift type '{SwiftType}' could not be projected to a concrete C# type "
                + "and degraded to bare `object` with no [UnsupportedSwiftType] marker. The member is "
                + "still usable but loses static type fidelity; this is recorded under "
                + "objectDegradations in binding-report.json.",
                degraded);
        }

        // C1: a member referenced a framework type that has no .NET binding (the type database could
        // resolve it only by synthesizing a bridged ObjC class for a value type). Emitting it would
        // dangle as a CS0234, so the member was skipped rather than abort the module. Surface one loud
        // warning per distinct skip detail so the dropped surface is visible at generation time.
        foreach (var skipDetail in report.SkippedItems
                     .Where(item => item.Reason == SkipReason.AbsentFrameworkType && item.Details is not null)
                     .Select(item => item.Details!)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(detail => detail, StringComparer.Ordinal))
        {
            logger.LogWarning(
                "SWIFTBIND049: {Detail} The member was skipped so the binding still compiles; it is "
                + "recorded under skippedItems (AbsentFrameworkType) in binding-report.json.",
                skipDetail);
        }
    }

    /// <summary>
    /// Verifies that every silent tombstone the registrar pre-pass recorded was actually
    /// emitted as a <c>[OpaqueSwiftType]</c> declaration by a handler. A break means
    /// <see cref="SilentTombstoneRegistrar.WouldEmitAsOpaqueTombstone"/> returned <c>true</c>
    /// for a type that no handler emitted — leaving any metadata-cookie reference to that
    /// type unresolved in the generated source. Throws to fail the build loudly so the
    /// drift can be diagnosed and the registrar's predicate brought back in sync.
    /// </summary>
    internal static void AssertSilentTombstoneInvariant(ModuleEmissionContext emissionContext, string moduleName)
    {
        var divergent = emissionContext.SilentTombstones
            .Where(name => !emissionContext.EmittedOpaqueTypes.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (divergent.Count == 0)
            return;

        throw new InvalidOperationException(
            $"Silent tombstone invariant violated in module '{moduleName}': "
            + $"{divergent.Count} type(s) registered by SilentTombstoneRegistrar were not emitted "
            + "with [OpaqueSwiftType] by any handler. Metadata-cookie references to these types "
            + "would dangle. Likely cause: an early-return predicate in "
            + "SilentTombstoneRegistrar.WouldEmitAsOpaqueTombstone is missing a case that the "
            + "handler-side opaque-emission gate excludes, or a handler suppressed emission via "
            + "a path the registrar does not model. "
            + $"Divergent types: {string.Join(", ", divergent)}");
    }

    internal static EmissionReport BuildReport(ModuleEmissionContext emissionContext, string moduleName)
    {
        var report = new EmissionReport { Module = moduleName };

        // Aggregate wrapper strategy counts from accumulated data
        foreach (var kv in emissionContext.WrapperStrategyCounts)
        {
            report.WrapperStrategyCounts[kv.Key] = kv.Value;
        }

        // Aggregate skip reasons
        foreach (var kv in emissionContext.WrapperSkipReasons)
        {
            report.SkipReasons[kv.Key] = kv.Value;
        }

        // Conformance decisions
        foreach (var kv in emissionContext.ConformanceDecisions)
        {
            if (kv.Value.Emitted)
                report.ConformanceDecisions.EmittedInSource++;
            else
                report.ConformanceDecisions.SkippedAtEmission++;
        }

        // Proxy classes withheld at emission (the emit-time reference gate's input set).
        report.SuppressedProxyClassCount = emissionContext.SuppressedProxyClassNames.Count;

        // Silent tombstones (sorted for deterministic output)
        report.SilentTombstones = emissionContext.SilentTombstones
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // Degraded existentials (Defect E): PAT existentials that fell back to `object`,
        // sorted for deterministic output and a deterministic SWIFTBIND023 warning order.
        report.DegradedExistentials = emissionContext.DegradedExistentials
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // Degraded reverse-dispatch receivers (suppressed-proxy B3): EveryProtocol receivers whose
        // existential payload touched a suppressed proxy and degraded to a fail-fast stub. Sorted for
        // deterministic output and a deterministic SWIFTBIND061 warning order.
        report.DegradedReverseDispatchReceivers = emissionContext.DegradedReverseDispatchReceivers
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // CSM conformer rejections (multi-constraint intersection filter). The engine
        // is the source of truth — it accumulates rejections across every
        // FindSpecializableMethods / ResolveParentSpecializableParams call within the
        // module's run. Sorted by (parentType, genericParam, conformer, missingConstraint)
        // for deterministic output.
        var engine = emissionContext.SpecializationEngine;
        if (engine is not null)
        {
            report.CsmConformerRejections = engine.RejectedPairings
                .Select(r => new CsmConformerRejectionEntry
                {
                    ParentType = r.ParentType,
                    GenericParam = r.GenericParamName,
                    SelectedProtocol = r.SelectedProtocol,
                    Conformer = r.ConformerSwiftType,
                    MissingConstraint = r.MissingConstraint,
                    Reason = r.Reason,
                })
                .OrderBy(e => e.ParentType, StringComparer.Ordinal)
                .ThenBy(e => e.GenericParam, StringComparer.Ordinal)
                .ThenBy(e => e.Conformer, StringComparer.Ordinal)
                .ThenBy(e => e.MissingConstraint, StringComparer.Ordinal)
                .ToList();
        }

        return report;
    }
}
