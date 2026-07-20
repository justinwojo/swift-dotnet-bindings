// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// One proven-missing module the binding cannot be built without: the module, who imports it, the
/// evidence line, the search roots that were consulted, advisory provenance, and the remediation.
/// </summary>
public sealed record MissingModuleObligation
{
    /// <summary>The module that could not be resolved.</summary>
    public required string MissingModule { get; init; }

    /// <summary>The module whose swiftinterface imports <see cref="MissingModule"/>.</summary>
    public required string ImporterModule { get; init; }

    /// <summary>The importer's <c>.swiftinterface</c> path (the evidence file).</summary>
    public required string InterfacePath { get; init; }

    /// <summary>1-based line of the import statement within <see cref="InterfacePath"/>.</summary>
    public required int Line { get; init; }

    /// <summary>The ordered <c>-F</c> roots the probe searched.</summary>
    public required IReadOnlyList<string> SearchedRoots { get; init; }

    /// <summary>Advisory provenance for the importer, or a receipt-neutral note when none is available.</summary>
    public required string Provenance { get; init; }

    /// <summary>Human-actionable remediation.</summary>
    public required string Action { get; init; }

    /// <summary>Renders the multi-line structured report body for this obligation.</summary>
    public string Format()
    {
        var roots = SearchedRoots.Count == 0
            ? "    (none)"
            : string.Join("\n", SearchedRoots.Select(r => $"    - {r}"));
        return
            $"  missing module : {MissingModule}\n" +
            $"  imported by    : {ImporterModule}\n" +
            $"  evidence       : {InterfacePath}:{Line}\n" +
            $"  searched roots :\n{roots}\n" +
            $"  provenance     : {Provenance}\n" +
            $"  action         : {Action}";
    }
}

/// <summary>The result of the closure preflight.</summary>
public sealed record ClosurePreflightVerdict
{
    /// <summary>True when every public compile-import edge resolved (no proven-missing obligations).</summary>
    public required bool IsClosed { get; init; }

    /// <summary>The proven-missing obligations (empty when <see cref="IsClosed"/>).</summary>
    public required IReadOnlyList<MissingModuleObligation> Obligations { get; init; }

    /// <summary>
    /// True when at least one candidate could not be adjudicated (no probe, or an inconclusive probe).
    /// A run may be <see cref="IsClosed"/> yet have unadjudicated candidates — those were left to the
    /// downstream wrapper compile rather than turned into a false early failure.
    /// </summary>
    public required bool HadUnadjudicatedCandidates { get; init; }
}

/// <summary>
/// Proves the primary module's compile-import graph is closed BEFORE ABI parsing, and turns a
/// genuinely-missing module into an early, structured obligation. It classifies each public
/// compile-import edge, then adjudicates the unresolved ones with an import-probe: only a probe that
/// confirms the SAME module is absent becomes a hard obligation; a resolvable probe (an SDK module the
/// registry does not catalogue) is dropped, and an inconclusive one (or no probe at all) is left to the
/// downstream compile so an incomplete registry can never manufacture a false early failure.
/// </summary>
public static class BindingInputClosurePreflight
{
    /// <summary>The diagnostic code for a proven-missing input module surfaced by the preflight.</summary>
    public const string DiagnosticCode = "SWIFTBIND119";

    // Swift/runtime modules that are always present via the standard library / SDK overlay and are never
    // author-supplied. A missing one of these is never a dependency the caller forgot.
    private static readonly HashSet<string> RuntimeBuiltins = new(StringComparer.Ordinal)
    {
        "Swift", "_Concurrency", "_StringProcessing", "_SwiftConcurrencyShims", "_Builtin_intrinsics",
        "Builtin", "simd", "ObjectiveC", "Darwin", "_math", "_RegexParser", "_RuntimeSupport",
    };

    /// <summary>Classifies an unsupplied module as a runtime-builtin or SDK module, or leaves it unresolved.</summary>
    public static InputSource? ClassifyUnsupplied(string moduleName, Func<string, bool> isSdkModuleResolved)
    {
        if (RuntimeBuiltins.Contains(moduleName))
            return InputSource.RuntimeBuiltin;
        return isSdkModuleResolved(moduleName) ? InputSource.AppleSdk : null;
    }

    /// <summary>
    /// Builds the input graph from an inventory using this preflight's own import-edge reader and
    /// unsupplied-module classifier — the single source those two rules live in, so the closure check
    /// and any other consumer (e.g. the topological-order emit) see an identically-built graph.
    /// </summary>
    public static BindingInputGraph BuildGraph(
        InputInventory inventory,
        Func<string, bool> isSdkModuleResolved,
        Func<string, string?>? compileImportSpelling = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(isSdkModuleResolved);
        return BindingInputGraph.Build(
            inventory,
            readImportEdges: ReadImportEdges,
            classifyUnsupplied: m => ClassifyUnsupplied(m, isSdkModuleResolved),
            compileImportSpelling: compileImportSpelling);
    }

    /// <summary>
    /// Runs the preflight over an inventory. <paramref name="isSdkModuleResolved"/> answers "is this
    /// unsupplied module an SDK / built-in-database module?" (cheap short-circuit); <paramref name="probe"/>
    /// adjudicates the survivors (null => no adjudication, everything left to the downstream compile).
    /// </summary>
    public static ClosurePreflightVerdict Run(
        InputInventory inventory,
        Func<string, bool> isSdkModuleResolved,
        IModuleImportProbe? probe,
        ILogger logger,
        Func<string, string?>? compileImportSpelling = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(isSdkModuleResolved);
        ArgumentNullException.ThrowIfNull(logger);

        var graph = BuildGraph(inventory, isSdkModuleResolved, compileImportSpelling);

        var candidates = graph.UnresolvedPublicCompileImports();
        if (candidates.Count == 0)
            return new ClosurePreflightVerdict { IsClosed = true, Obligations = Array.Empty<MissingModuleObligation>(), HadUnadjudicatedCandidates = false };

        var searchRoots = CollectFrameworkSearchRoots(inventory);
        var obligations = new List<MissingModuleObligation>();
        bool unadjudicated = false;

        foreach (var edge in candidates)
        {
            var missing = edge.ToModule;
            if (probe is null)
            {
                // No oracle: we cannot distinguish a forgotten dependency from an uncatalogued SDK module,
                // so we record an advisory rather than manufacture a false early failure.
                unadjudicated = true;
                RecordAdvisory(missing, edge.FromModule, logger);
                continue;
            }

            var outcome = probe.Probe(missing, searchRoots);
            switch (outcome.Status)
            {
                case ImportProbeStatus.Resolvable:
                    // An SDK module the registry did not catalogue — genuinely present, not an obligation.
                    logger.LogInformation(
                        "Closure preflight: '{Module}' (imported by '{Importer}') resolved against the SDK/search roots; not a missing input.",
                        missing, edge.FromModule);
                    break;

                case ImportProbeStatus.MissingModule when string.Equals(outcome.MissingModuleName, missing, StringComparison.Ordinal):
                    obligations.Add(BuildObligation(inventory, edge, searchRoots));
                    break;

                default:
                    // Inconclusive, or a "no such module" naming a DIFFERENT (transitive) module than the
                    // candidate — reporting the candidate would misattribute, so we defer to the compile.
                    unadjudicated = true;
                    RecordAdvisory(missing, edge.FromModule, logger);
                    break;
            }
        }

        return new ClosurePreflightVerdict
        {
            IsClosed = obligations.Count == 0,
            Obligations = obligations,
            HadUnadjudicatedCandidates = unadjudicated,
        };
    }

    /// <summary>
    /// Convenience wrapper for the generator entry point: runs the preflight and, on a proven-missing
    /// obligation, logs the <see cref="DiagnosticCode"/> structured report and returns false (the caller
    /// must then abort BEFORE parsing, emitting no binding/wrapper artifacts). Returns true otherwise.
    /// </summary>
    public static bool RunOrFail(
        InputInventory inventory,
        Func<string, bool> isSdkModuleResolved,
        IModuleImportProbe? probe,
        ILogger logger,
        Func<string, string?>? compileImportSpelling = null)
    {
        // The preflight is only meaningful when the primary's public surface is readable. An ABI/TBD-only
        // run (no primary swiftinterface) has no import edges to close — record an advisory and proceed.
        if (string.IsNullOrEmpty(inventory.Primary.SwiftInterfacePath))
        {
            InputResolutionReport.RecordInfo(
                InputResolutionCategory.SwiftInterface,
                $"Closure preflight skipped for '{inventory.Primary.ModuleName}': no primary .swiftinterface, so the compile-import graph cannot be proven closed ahead of the wrapper compile.");
            return true;
        }

        var verdict = Run(inventory, isSdkModuleResolved, probe, logger, compileImportSpelling);
        if (verdict.IsClosed)
            return true;

        logger.LogError(
            "{Code}: required module(s) not supplied — the primary module '{Module}' cannot be bound because its import graph is not closed. " +
            "The following module(s) are imported by supplied interfaces but were not found among the inputs or the SDK:\n{Report}",
            DiagnosticCode, inventory.Primary.ModuleName,
            string.Join("\n\n", verdict.Obligations.Select(o => o.Format())));
        return false;
    }

    // Reads the structured import edges for a supplied module from its swiftinterface.
    private static IReadOnlyList<ImportEdge> ReadImportEdges(InputModuleArtifacts module)
    {
        if (string.IsNullOrEmpty(module.SwiftInterfacePath) || !File.Exists(module.SwiftInterfacePath))
            return Array.Empty<ImportEdge>();
        var text = File.ReadAllText(module.SwiftInterfacePath);
        return AppleFrameworkImportDetector.ExtractImportEdges(text, module.SwiftInterfacePath);
    }

    // A generous SUPERSET of the -F roots the wrapper compile will use: the primary's framework search
    // path, every dependency's, each of those directories' parents (sibling co-location), and any nested
    // <root>/*.framework/Frameworks directories. A superset is deliberate — the probe may then resolve a
    // module the wrapper also resolves (never a false "missing"), and can only ever under-report a miss,
    // which safely defers to the downstream compile.
    internal static IReadOnlyList<string> CollectFrameworkSearchRoots(InputInventory inventory)
    {
        var roots = new List<string>();
        void Add(string? dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;
            var full = Path.GetFullPath(dir);
            if (!roots.Contains(full, StringComparer.Ordinal))
                roots.Add(full);
        }

        foreach (var module in inventory.AllModules())
        {
            var fsp = module.FrameworkSearchPath;
            Add(fsp);
            if (!string.IsNullOrEmpty(fsp))
            {
                Add(Path.GetDirectoryName(fsp)); // sibling co-location one level up
                try
                {
                    foreach (var fw in Directory.GetDirectories(fsp, "*.framework"))
                        Add(Path.Combine(fw, "Frameworks"));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return roots;
    }

    private static MissingModuleObligation BuildObligation(
        InputInventory inventory, BindingInputEdge edge, IReadOnlyList<string> searchRoots)
    {
        var import = edge.Import!;
        var importerArtifacts = inventory.FindModule(edge.FromModule);
        return new MissingModuleObligation
        {
            MissingModule = edge.ToModule,
            ImporterModule = edge.FromModule,
            InterfacePath = import.InterfacePath,
            Line = import.Line,
            SearchedRoots = searchRoots,
            Provenance = DescribeProvenance(edge.ToModule, importerArtifacts),
            Action = DescribeAction(edge.ToModule, import),
        };
    }

    // Receipt-neutral provenance phrasing: without a conversion receipt we can only say the module was
    // not supplied — never that a conversion "failed to produce it" (a manifest survives failed runs and
    // can never prove absence).
    private static string DescribeProvenance(string missingModule, InputModuleArtifacts? importer)
    {
        if (importer?.ProvenanceIdentity is { Length: > 0 } identity)
            return $"required module not supplied; importer provenance: {identity}";
        return "required module not supplied; conversion provenance unavailable";
    }

    private static string DescribeAction(string missingModule, ImportEdge import)
    {
        var reexport = import.IsExported
            ? " (re-exported via @_exported, so it is part of the bound module's public surface)"
            : string.Empty;
        return
            $"supply '{missingModule}' as an input{reexport} — pass its xcframework via --framework-dependency, " +
            $"or convert it alongside the primary so its artifacts are present.";
    }

    // An unadjudicated candidate is a DEFERRAL, not an input substitution: the preflight could not
    // prove the module absent (no probe, an inconclusive probe, or a "no such module" naming a different
    // transitive), so it hands the decision to the downstream wrapper compile / SWIFTBIND111 backstop.
    // This must be recorded as Info, NOT a degradation — a degradation escalates to a fatal SWIFTBIND027
    // under --strict-inputs (the CI compile gate), which would turn a deliberate fail-open deferral into
    // a false early failure and defeat the probe's whole reason for being generous. Only a probe that
    // confirms the SAME module absent is a hard failure, and that path is the SWIFTBIND119 obligation
    // above — never this one.
    private static void RecordAdvisory(string missingModule, string importer, ILogger logger)
    {
        logger.LogWarning(
            "Closure preflight: could not confirm module '{Module}' (imported by '{Importer}') is present; " +
            "deferring to the wrapper compile. Supply it via --framework-dependency if the binding later fails to compile.",
            missingModule, importer);
        InputResolutionReport.RecordInfo(
            InputResolutionCategory.Dependency,
            $"Closure preflight could not adjudicate module '{missingModule}' imported by '{importer}'; deferred to the wrapper compile (inputs used as supplied, conversion provenance unavailable).");
    }
}
