// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// The kind of dependency an edge in a <see cref="BindingInputGraph"/> represents. The four kinds are
/// modeled separately because they resolve against different inputs and fail differently:
/// </summary>
public enum BindingInputEdgeKind
{
    /// <summary>
    /// A <c>.swiftinterface</c> <c>import</c> — the module must be resolvable at wrapper-compile time.
    /// This is the kind the closure preflight adjudicates. Fully populated by the graph builder.
    /// </summary>
    ModuleCompilationImport,

    /// <summary>
    /// A public ABI/type reference (the bound surface names a type from another module). Populated
    /// after ABI parsing (cross-module fact resolution, a later session); modeled here so the graph
    /// shape is stable.
    /// </summary>
    PublicAbiTypeReference,

    /// <summary>A native runtime link dependency — the primary dylib links the target's binary.</summary>
    NativeRuntimeLink,

    /// <summary>A managed binding-package reference — the emitted package references the target's package.</summary>
    ManagedBindingPackageReference,
}

/// <summary>
/// One node in the input graph: a Swift module together with the artifacts (if any) the generator
/// holds for it and where they came from. A node with a null <see cref="Source"/> is UNRESOLVED — it
/// was referenced by an import but is not among the supplied inputs and was not recognized as an SDK
/// or runtime-builtin module.
/// </summary>
public sealed record BindingInputNode
{
    /// <summary>The Swift module name.</summary>
    public required string ModuleName { get; init; }

    /// <summary>
    /// The spelling used to <c>import</c> this module in generated Swift, when it differs from
    /// <see cref="ModuleName"/> (umbrella remaps, e.g. RealityFoundation → RealityKit). Null when identical.
    /// </summary>
    public string? CompileImportSpelling { get; init; }

    /// <summary>Where the module's artifacts came from, or null when the module is unresolved.</summary>
    public InputSource? Source { get; init; }

    /// <summary>The supplied artifacts for this module, when it is a supplied (primary/sibling/dependency) node.</summary>
    public InputModuleArtifacts? Artifacts { get; init; }

    /// <summary>The managed binding-package identity this module maps to, when known.</summary>
    public string? ManagedPackageId { get; init; }

    /// <summary>Advisory provenance identity carried from the inventory, when known.</summary>
    public string? ProvenanceIdentity { get; init; }

    /// <summary><c>true</c> when the module is accounted for in the inputs (supplied, SDK, or runtime builtin).</summary>
    public bool IsResolved => Source != null;
}

/// <summary>
/// One directed dependency edge between two modules. For a <see cref="BindingInputEdgeKind.ModuleCompilationImport"/>
/// edge, <see cref="Import"/> carries the originating swiftinterface path, line, and visibility.
/// </summary>
public sealed record BindingInputEdge
{
    /// <summary>The dependency kind this edge represents.</summary>
    public required BindingInputEdgeKind Kind { get; init; }

    /// <summary>The module that declares the dependency (the importer / referrer / linker).</summary>
    public required string FromModule { get; init; }

    /// <summary>The depended-upon module.</summary>
    public required string ToModule { get; init; }

    /// <summary>The originating import declaration, for <see cref="BindingInputEdgeKind.ModuleCompilationImport"/> edges.</summary>
    public ImportEdge? Import { get; init; }
}

/// <summary>
/// A static, pre-parse model of a binding run's input dependency graph: one node per module (supplied,
/// SDK, runtime-builtin, or unresolved) and edges of the four <see cref="BindingInputEdgeKind"/>s. Built
/// from an <see cref="InputInventory"/> before ABI parsing so the closure preflight can prove the primary
/// module's compile-import graph is closed — every <see cref="BindingInputEdgeKind.ModuleCompilationImport"/>
/// edge lands on a resolved node — and name any that does not.
/// </summary>
public sealed class BindingInputGraph
{
    private readonly Dictionary<string, BindingInputNode> _nodes;
    private readonly List<BindingInputEdge> _edges;

    private BindingInputGraph(Dictionary<string, BindingInputNode> nodes, List<BindingInputEdge> edges)
    {
        _nodes = nodes;
        _edges = edges;
    }

    /// <summary>All nodes, keyed by module name.</summary>
    public IReadOnlyDictionary<string, BindingInputNode> Nodes => _nodes;

    /// <summary>All edges, in insertion order.</summary>
    public IReadOnlyList<BindingInputEdge> Edges => _edges;

    /// <summary>The module-compilation-import edges (the ones the closure preflight adjudicates).</summary>
    public IEnumerable<BindingInputEdge> CompileImportEdges =>
        _edges.Where(e => e.Kind == BindingInputEdgeKind.ModuleCompilationImport);

    /// <summary>
    /// The compile-import edges whose target is unresolved AND public (a plain/public/@_exported import).
    /// These are the CANDIDATE missing-module obligations — non-public / @_implementationOnly imports are
    /// excluded because they are never re-emitted into the wrapper and so can never break its compile.
    /// One entry per distinct (importer, missing-module) pair, in first-seen order.
    /// </summary>
    public IReadOnlyList<BindingInputEdge> UnresolvedPublicCompileImports()
    {
        var seen = new HashSet<(string, string)>();
        var result = new List<BindingInputEdge>();
        foreach (var edge in CompileImportEdges)
        {
            if (edge.Import is null || edge.Import.IsNonPublic)
                continue;
            if (_nodes.TryGetValue(edge.ToModule, out var target) && target.IsResolved)
                continue;
            if (seen.Add((edge.FromModule, edge.ToModule)))
                result.Add(edge);
        }
        return result;
    }

    /// <summary>
    /// The supplied modules (primary + supplied dependencies) in dependency-first build order — every
    /// module appears after all supplied modules it depends on via a compile-import or managed-package
    /// reference edge. Unresolved / SDK / runtime-builtin nodes are excluded: this run does not build
    /// them, so they never gate a sibling. Ties break lexically (ordinal) for deterministic output.
    /// A dependency cycle — which a Swift module compile graph cannot legally contain — degrades to
    /// lexical order rather than throwing, so a build orchestrator can always consume the result.
    /// </summary>
    /// <remarks>
    /// A multi-module run resolves in-run siblings LOCALLY (a run-scoped package feed the verification
    /// leg restores from) rather than against a public feed the sibling has not been published to yet.
    /// This order is how an orchestrator packs those siblings feed-first: pack each module's binding into
    /// the run-scoped feed in this order, then the dependent restores against a populated feed.
    /// </remarks>
    public IReadOnlyList<string> TopologicalOrder(ILogger? logger = null)
    {
        var supplied = SuppliedModuleNames();
        if (supplied.Count == 0)
            return Array.Empty<string>();

        // Adjacency for TopologicalSort: graph[M] = the supplied modules M depends on (deps come first).
        // Both a compile-import (M's swiftinterface imports the sibling) and a managed-package reference
        // (M's emitted package references the sibling's package) mean the sibling must be built first;
        // compile-import edges also carry dep-of-dep ordering (a supplied dependency importing another).
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var moduleName in supplied)
            adjacency[moduleName] = new List<string>();

        foreach (var edge in _edges)
        {
            if (edge.Kind != BindingInputEdgeKind.ModuleCompilationImport &&
                edge.Kind != BindingInputEdgeKind.ManagedBindingPackageReference)
                continue;
            if (string.Equals(edge.FromModule, edge.ToModule, StringComparison.Ordinal))
                continue;
            if (!supplied.Contains(edge.FromModule) || !supplied.Contains(edge.ToModule))
                continue;
            var deps = adjacency[edge.FromModule];
            if (!deps.Contains(edge.ToModule))
                deps.Add(edge.ToModule);
        }

        try
        {
            return TopologicalSort.Sort(adjacency);
        }
        catch (InvalidOperationException)
        {
            // A cycle among supplied modules means no provably dependency-first order exists. Degrade
            // to a deterministic lexical order so the run stays reproducible, but warn: an orchestrator
            // packing a run-scoped feed from this order can no longer trust that a dependency is built
            // before its dependent, so a NU1101 in that leg may be an ordering artifact, not a real gap.
            var fallback = supplied.ToList();
            fallback.Sort(StringComparer.Ordinal);
            logger?.LogWarning(
                "Import graph has a dependency cycle among supplied modules ({Modules}); no dependency-first " +
                "order exists. Falling back to lexical order — run-scoped feed packing cannot guarantee a " +
                "dependency is built before its dependent.",
                string.Join(", ", fallback));
            return fallback;
        }
    }

    /// <summary>
    /// For each supplied module, the supplied modules it actually depends on via a compile-import edge
    /// (its swiftinterface imports them) — the REAL, import-derived inter-module dependencies among the
    /// modules this run builds. Managed-package-reference edges are deliberately excluded here: those
    /// are emitted primary→every supplied dependency whether or not the primary imports it, so unioning
    /// them across a corpus's per-primary runs (where each primary is handed every co-located sibling as
    /// a dependency) would fabricate a mutual-dependency cycle. Compile-import edges are pruned by nature
    /// — only a genuinely imported sibling appears — so their union is the true acyclic build graph an
    /// orchestrator topologically sorts to order a run-scoped feed. Keys and value lists are sorted
    /// (ordinal) for deterministic output.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SuppliedImportDependencies()
    {
        var supplied = SuppliedModuleNames();
        var result = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var moduleName in supplied)
            result[moduleName] = new List<string>();

        foreach (var edge in _edges)
        {
            if (edge.Kind != BindingInputEdgeKind.ModuleCompilationImport)
                continue;
            if (string.Equals(edge.FromModule, edge.ToModule, StringComparison.Ordinal))
                continue;
            if (!supplied.Contains(edge.FromModule) || !supplied.Contains(edge.ToModule))
                continue;
            var deps = result[edge.FromModule];
            if (!deps.Contains(edge.ToModule))
                deps.Add(edge.ToModule);
        }

        foreach (var deps in result.Values)
            deps.Sort(StringComparer.Ordinal);
        return result.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }

    // The modules this run actually builds: supplied (primary + supplied dependencies) nodes only.
    // SDK / runtime-builtin / unresolved nodes are never built, so they never gate a sibling's order.
    private HashSet<string> SuppliedModuleNames() => new(
        _nodes.Values.Where(n => n.Artifacts != null).Select(n => n.ModuleName),
        StringComparer.Ordinal);

    /// <summary>
    /// Builds the graph from an inventory. <paramref name="readImportEdges"/> returns the import edges
    /// for a supplied module (normally by reading its swiftinterface); <paramref name="classifyUnsupplied"/>
    /// classifies a module NOT present in the inventory as an SDK / runtime-builtin source, or returns null
    /// to leave it unresolved. <paramref name="compileImportSpelling"/> maps a module name to its Swift
    /// import spelling (identity when null-returning).
    /// </summary>
    public static BindingInputGraph Build(
        InputInventory inventory,
        Func<InputModuleArtifacts, IReadOnlyList<ImportEdge>> readImportEdges,
        Func<string, InputSource?> classifyUnsupplied,
        Func<string, string?>? compileImportSpelling = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(readImportEdges);
        ArgumentNullException.ThrowIfNull(classifyUnsupplied);

        var nodes = new Dictionary<string, BindingInputNode>(StringComparer.Ordinal);
        var edges = new List<BindingInputEdge>();

        // 1. A node for every supplied module (primary + dependencies).
        foreach (var module in inventory.AllModules())
        {
            nodes[module.ModuleName] = new BindingInputNode
            {
                ModuleName = module.ModuleName,
                CompileImportSpelling = Spelling(compileImportSpelling, module.ModuleName),
                Source = module.Source,
                Artifacts = module,
                ManagedPackageId = module.ManagedPackageId,
                ProvenanceIdentity = module.ProvenanceIdentity,
            };
        }

        // Ensures a node exists for a module referenced by an edge, classifying an unsupplied one.
        BindingInputNode EnsureNode(string moduleName)
        {
            if (nodes.TryGetValue(moduleName, out var existing))
                return existing;
            var node = new BindingInputNode
            {
                ModuleName = moduleName,
                CompileImportSpelling = Spelling(compileImportSpelling, moduleName),
                Source = classifyUnsupplied(moduleName),
            };
            nodes[moduleName] = node;
            return node;
        }

        // 2. Module-compilation-import edges from every supplied module's swiftinterface.
        foreach (var module in inventory.AllModules())
        {
            foreach (var import in readImportEdges(module))
            {
                if (string.Equals(import.ModuleName, module.ModuleName, StringComparison.Ordinal))
                    continue; // a self-import is not a dependency
                EnsureNode(import.ModuleName);
                edges.Add(new BindingInputEdge
                {
                    Kind = BindingInputEdgeKind.ModuleCompilationImport,
                    FromModule = module.ModuleName,
                    ToModule = import.ModuleName,
                    Import = import,
                });
            }
        }

        // 3. Native-runtime-link and managed-binding-package-reference edges from the primary to each
        //    supplied dependency that carries the corresponding artifact. (Public-ABI-type-reference
        //    edges are added post-parse in a later session.)
        var primaryName = inventory.Primary.ModuleName;
        foreach (var dep in inventory.Dependencies)
        {
            if (!string.IsNullOrEmpty(dep.BinaryPath))
                edges.Add(new BindingInputEdge
                {
                    Kind = BindingInputEdgeKind.NativeRuntimeLink,
                    FromModule = primaryName,
                    ToModule = dep.ModuleName,
                });

            if (!string.IsNullOrEmpty(dep.ManagedPackageId))
                edges.Add(new BindingInputEdge
                {
                    Kind = BindingInputEdgeKind.ManagedBindingPackageReference,
                    FromModule = primaryName,
                    ToModule = dep.ModuleName,
                });
        }

        return new BindingInputGraph(nodes, edges);
    }

    private static string? Spelling(Func<string, string?>? map, string moduleName)
    {
        var spelling = map?.Invoke(moduleName);
        return string.Equals(spelling, moduleName, StringComparison.Ordinal) ? null : spelling;
    }
}
