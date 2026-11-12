// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Routes a module's Swift-convention imports through their bridges from the lowering receipt its own
/// wrapper compile just collected (<c>--route-swift-bridges</c>). Generation cannot do it: it runs before
/// the wrapper is compiled, so it has no receipt, and feeding the receipt back into generation would make
/// the binding an input of itself. This runs inside the wrapper compile instead, once the receipt is
/// written, and works on the generated C# in place.
/// </summary>
/// <remarks>
/// <para>
/// The routing is always re-derived from generation's own C#. A journal
/// (<see cref="SwiftBridgeRoutingJournal"/>) records every import this rewrote; the C# is first restored
/// from it, then the receipt is judged (<see cref="SwiftLoweringAuthorization"/>, scoped to the slices the
/// receipt has, which are exactly the slices the wrapper ships), the plans are routed as generation would
/// route them under that judgement, and the C#, the bridges and the plan and ledger sidecars are written
/// to match. Anything the receipt does not authorize keeps its import; a contradiction in the receipt
/// refuses its plan whatever else the receipt says.
/// </para>
/// <para>
/// The bridges are linked into the wrapper, so a routing that needs a bridge the wrapper does not define
/// relinks it. The relink compiles the same wrapper sources, which the receipt records: each relinked
/// slice must have read exactly the inputs the receipt was collected from, or the relinked wrapper is
/// discarded. Then, and when anything else fails, the C# is restored to generation's own and the wrapper
/// the receipt was collected from is kept: the module is emitted as it would be without bridges, and the
/// reason is reported (<see cref="FailedCode"/>). The wrapper must define exactly the bridges the C#
/// calls, so a routing that bridges less relinks too; one whose bridges the wrapper was already linked
/// from (the next compile of an unchanged module) writes only the C# and sidecars.
/// </para>
/// <para>
/// No input of generation changes, so the binding's fingerprint never sees the receipt: the routing is
/// re-derived on every wrapper compile and is a function of generation's C#, the receipt and the
/// generator.
/// </para>
/// </remarks>
public static class SwiftBridgeFinalizer
{
    /// <summary>The warning of a routing that could not be completed, whose module keeps its imports.</summary>
    public const string FailedCode = "SWIFTBIND129";

    /// <summary>What a finalization did.</summary>
    public enum Outcome
    {
        /// <summary>Nothing to do: the module has no plans, generation routed its C# itself, or nothing is routed or asked for.</summary>
        NotApplicable,

        /// <summary>The C#, bridges and sidecars already matched the routing.</summary>
        Unchanged,

        /// <summary>The C# and sidecars were rewritten; the wrapper already defined every bridge.</summary>
        Rewritten,

        /// <summary>The bridges were written and the wrapper relinked with them.</summary>
        Relinked,

        /// <summary>The routing could not be completed: the module keeps generation's C# and the wrapper the receipt describes.</summary>
        Withdrawn,

        /// <summary>The generated C# is not in a state this can establish; the build must not ship it.</summary>
        Failed,
    }

    /// <summary>
    /// Relinks the wrapper with the bridge files now on disk, verifying each slice against
    /// <paramref name="receipt"/>. Null on success; otherwise why, with the wrapper the receipt was
    /// collected from back in place.
    /// </summary>
    public delegate string? Relink(SwiftLoweringReceipt receipt);

    /// <param name="OutputDirectory">The directory of the module's C#, sidecars, bridges and wrapper.</param>
    /// <param name="Module">The Swift module.</param>
    /// <param name="Library">The wrapper the twins import the bridges from.</param>
    /// <param name="Route">Whether routing was asked for; without it, any earlier routing is undone.</param>
    /// <param name="Receipt">The receipt this compile wrote, or null when none was.</param>
    /// <param name="Expect">What the build expects of a receipt of a scope (<see cref="SwiftToolchainProbe.Expect"/>), or why it cannot say.</param>
    public sealed record Request(
        string OutputDirectory,
        string Module,
        string Library,
        bool Route,
        SwiftLoweringEvidence? Receipt,
        Func<SwiftArtifactScope, (SwiftEvidenceExpectation? Expectation, string? Why)> Expect);

    /// <summary>Whether a finalization can have anything to do: routing is asked for, or an earlier one left a journal.</summary>
    public static bool Applies(string outputDirectory, bool route) =>
        route || (Directory.Exists(outputDirectory) && Directory.EnumerateFiles(outputDirectory, SwiftBridgeRoutingJournal.FilePattern).Any());

    private static readonly Regex BridgeDefinition = new(
        @"^\s*\.globl\s+_(" + SwiftCallPlan.ForwardPrefix + @"[A-Za-z0-9_]+)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Routes the module's imports from <paramref name="request"/>'s receipt. <paramref name="beforeRelink"/>
    /// runs once before anything a relink depends on is written, to mark the wrapper incomplete until
    /// the relink is done.
    /// </summary>
    public static Outcome Finalize(Request request, Relink relink, Action beforeRelink, ILogger logger)
    {
        var directory = request.OutputDirectory;
        var module = request.Module;

        // The module's plan sidecar names its namespace, which names its C#, ledger and bridges.
        string? sidecar = null;
        SwiftCallPlanDocument? document = null;
        foreach (var path in Directory.GetFiles(directory, "*" + SwiftCallPlanSet.FileName(string.Empty)).Order(StringComparer.Ordinal))
        {
            SwiftCallPlanDocument read;
            try
            {
                read = SwiftCallPlanSet.Read(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                logger.LogDebug("Bridge routing skips the plan sidecar {Sidecar}: {Message}", path, ex.Message);
                continue;
            }
            if (read.Module != module)
                continue;
            if (sidecar is not null)
            {
                logger.LogWarning("{Code}: {Module}'s imports are not routed through bridges: {Directory} holds two plan sidecars of the module ({First}, {Second}).",
                    FailedCode, module, directory, Path.GetFileName(sidecar), Path.GetFileName(path));
                return Outcome.NotApplicable;
            }
            (sidecar, document) = (path, read);
        }
        if (sidecar is null || document is null)
        {
            if (request.Route)
                logger.LogInformation("{Module}'s imports are not routed through bridges: {Directory} holds no readable plan sidecar of the module.", module, directory);
            return Outcome.NotApplicable;
        }

        var @namespace = Path.GetFileName(sidecar)[..^SwiftCallPlanSet.FileName(string.Empty).Length];
        var journalPath = Path.Combine(directory, SwiftBridgeRoutingJournal.FileName(@namespace));
        SwiftBridgeRoutingJournal? journal = null;
        if (File.Exists(journalPath))
        {
            try
            {
                journal = SwiftBridgeRoutingJournal.Read(journalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                logger.LogError("{Code}: the bridge routing journal {Journal} could not be read, so the routing of {Module}'s C# cannot be undone: {Message}. Regenerate the binding (a clean build).",
                    FailedCode, journalPath, module, ex.Message);
                return Outcome.Failed;
            }
            if (journal.Module != module)
            {
                logger.LogError("{Code}: the bridge routing journal {Journal} is of {Other}, not {Module}. Regenerate the binding (a clean build).",
                    FailedCode, journalPath, journal.Module, module);
                return Outcome.Failed;
            }
        }

        var generated = SwiftCallPlanSet.From(document);
        if (journal is null)
        {
            // Without a journal the C# is generation's own; a judged sidecar means generation was given
            // evidence and routed it itself, which this never second-guesses.
            if (generated.EvidenceScope is not null || generated.Coverage.Any(e => e.Bridged == true))
            {
                if (request.Route)
                    logger.LogInformation("{Module}'s imports were routed at generation from the evidence it was given; the wrapper compile leaves them as generated.", module);
                return Outcome.NotApplicable;
            }
            if (!request.Route || request.Receipt is null)
                return Outcome.NotApplicable;
        }

        // Generation's own C#.
        var files = new[] { Path.Combine(directory, $"{@namespace}.cs") }
            .Concat(Directory.GetFiles(directory, SplitFileNaming.TypeFileGlob(@namespace)).Order(StringComparer.Ordinal))
            .Where(File.Exists)
            .Select(Path.GetFileName)
            .Cast<string>()
            .ToList();
        var current = files.ToDictionary(f => f, f => File.ReadAllText(Path.Combine(directory, f)), StringComparer.Ordinal);
        Dictionary<string, string> pristine;
        try
        {
            pristine = files.ToDictionary(f => f, f => journal?.Revert(f, current[f]) ?? current[f], StringComparer.Ordinal);
            if (journal is not null && journal.Rewrites.Select(r => r.File).FirstOrDefault(f => !current.ContainsKey(f)) is { } missing)
                throw new InvalidDataException($"{missing}, which the journal records a rewrite in, is not among {@namespace}'s files");
        }
        catch (InvalidDataException ex)
        {
            logger.LogError("{Code}: {Module}'s generated C# is not the C# its bridge routing journal describes: {Message}. Regenerate the binding (a clean build).",
                FailedCode, module, ex.Message);
            return Outcome.Failed;
        }

        // The plans judged by the receipt, as generation would judge them, and routed over that C#.
        var judged = generated.WithoutEvidence();
        SwiftLoweringReceipt? receipt = null;
        if (request.Receipt is { } evidence)
        {
            var slices = evidence.ReceiptsOf(module).SelectMany(r => r.Slices.Keys).Distinct(StringComparer.Ordinal).ToList();
            if (slices.Count > 0)
            {
                // The receipt holds a slice for every slice the wrapper ships and for no other, so its
                // slices are the scope: nothing is claimed that was not observed.
                var scope = new SwiftArtifactScope(module, slices);
                var (expectation, why) = request.Expect(scope);
                // The receipt a relink is verified against: the one collected under the build's
                // compiler. An undone routing relinks against it too, to drop the bridges it linked.
                receipt = expectation is null ? null : evidence.ReceiptsOf(module).FirstOrDefault(r => r.Compiler == expectation.Compiler);
                if (request.Route)
                {
                    var judgement = expectation is null
                        ? SwiftLoweringJudgement.Refused(scope, SwiftLoweringJudgement.AuthorizationBasis, why ?? "the build's toolchain could not be identified")
                        : SwiftLoweringAuthorization.Authorize(expectation, evidence);
                    if (judgement.Refusal is { } refusal && judged.Count(SwiftPlanStatus.Planned) > 0)
                        logger.LogWarning(
                            "The swiftc lowering receipt does not authorize {Module}'s bridge plans ({Basis} of {Scope}); its Swift-convention imports are emitted as generated: {Refusal}",
                            module, judgement.Basis, judgement.Scope, refusal);
                    judged = judged.WithEvidence(judgement);
                }
            }
        }
        var sources = files.Select(f => new SwiftBridgeSource(f, pristine[f])).ToList();
        var routing = SwiftBridgeRouting.Plan(judged, sources, request.Library);
        foreach (var refused in routing.Routes.Where(r => !r.IsBridged))
            logger.LogWarning(
                "{Code}: {Member} ({EntryPoint}) has an executable bridge plan ({Symbol}) but keeps its Swift-convention import: {Reason}",
                SwiftBridgeRouting.RefusedCode, refused.Member, refused.EntryPoint, refused.Symbol, refused.Refusal);

        var assemblyPaths = SwiftBridgeRouting.Architectures.ToDictionary(a => a, a => Path.Combine(directory, SwiftBridgeRouting.AssemblyFileName(@namespace, a)));
        var linked = assemblyPaths.ToDictionary(kv => kv.Key, kv => File.Exists(kv.Value) ? File.ReadAllText(kv.Value) : null);
        Dictionary<SwiftArchitecture, string?> desired;
        Dictionary<string, string> routed;
        try
        {
            desired = SwiftBridgeRouting.Architectures.ToDictionary(a => a, a => routing.Assembly(a)?.Source);
            routed = files.ToDictionary(f => f, f => routing.Rewrite(pristine[f]), StringComparer.Ordinal);
        }
        catch (InvalidOperationException ex)
        {
            // A routing planned over these very files writes them; this is a generator defect.
            logger.LogWarning("{Code}: {Module}'s imports are not routed through bridges: {Message}", FailedCode, module, ex.Message);
            routing = routing.Withdraw(ex.Message);
            desired = SwiftBridgeRouting.Architectures.ToDictionary(a => a, _ => (string?)null);
            routed = pristine;
        }

        var nextJournal = new SwiftBridgeRoutingJournal
        {
            Schema = SwiftBridgeRoutingJournal.SchemaVersion,
            Module = module,
            Library = request.Library,
            Rewrites = routing.Rewrites,
        };
        // The wrapper must define exactly the bridges the C# calls, as the bridges file now says: one
        // it lacks fails the call, and one it defines that nothing calls is an export no route
        // accounts for. Any difference relinks it, verified against the receipt. Without a receipt
        // a relink cannot be verified: a missing bridge then withdraws the routing, while bridges
        // only left over from an earlier routing stay inert in this wrapper until it is next compiled.
        var needed = routing.Bridged.Select(p => p.Symbol).ToHashSet(StringComparer.Ordinal);
        var lacking = needed.Count > 0 && linked.Values.Any(text => !needed.IsSubsetOf(Defined(text)));
        var differs = SwiftBridgeRouting.Architectures.Any(a => !string.Equals(linked[a], desired[a], StringComparison.Ordinal));
        var relinks = lacking || (differs && receipt is not null);

        if (!relinks)
        {
            var changed = Write(directory, nextJournal, journalPath, desired, assemblyPaths, routed, current);
            changed |= WriteSidecars(directory, @namespace, routing, judged, logger);
            if (linked.Values.SelectMany(Defined).Where(s => !needed.Contains(s)).Distinct(StringComparer.Ordinal).Count() is var leftover and > 0)
                logger.LogInformation(
                    "{Module}'s wrapper keeps {Count} bridge(s) of an earlier routing that its C# no longer calls; with no receipt to verify a relink against, they go when the wrapper is next compiled.",
                    module, leftover);
            LogRouted(logger, module, routing, relinked: false);
            return changed ? Outcome.Rewritten : Outcome.Unchanged;
        }

        beforeRelink();
        Write(directory, nextJournal, journalPath, desired, assemblyPaths, routed, current);
        var failure = receipt is null ? "the receipt holds no evidence of the module" : relink(receipt);
        if (failure is null)
        {
            WriteSidecars(directory, @namespace, routing, judged, logger);
            LogRouted(logger, module, routing, relinked: true);
            return Outcome.Relinked;
        }

        // The wrapper the receipt was collected from is back in place, so the C# goes back to
        // generation's own and the bridges to what that wrapper was linked from.
        if (needed.Count > 0)
            logger.LogWarning("{Code}: {Module}'s imports are not routed through bridges, and it is emitted as generated: the wrapper could not be relinked with its bridges: {Reason}",
                FailedCode, module, failure);
        else
            logger.LogWarning("{Code}: {Module}'s wrapper keeps the bridges of an earlier routing its C# no longer calls: it could not be relinked without them: {Reason}",
                FailedCode, module, failure);
        var withdrawn = routing.Withdraw($"the wrapper could not be relinked with its bridges: {failure}");
        Write(directory, nextJournal with { Rewrites = [] }, journalPath, linked, assemblyPaths, pristine,
            files.ToDictionary(f => f, f => File.ReadAllText(Path.Combine(directory, f)), StringComparer.Ordinal));
        WriteSidecars(directory, @namespace, withdrawn, judged, logger);
        return Outcome.Withdrawn;
    }

    /// <summary>The bridges an assembly file defines.</summary>
    private static HashSet<string> Defined(string? assembly) =>
        assembly is null ? [] : BridgeDefinition.Matches(assembly).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Writes the journal, then the bridges, then the C#: whatever stops the build part-way leaves a
    /// journal that covers every rewrite on disk. Only a file whose content changes is written, so an
    /// unchanged routing leaves the managed compile incremental. Returns whether anything was written.
    /// </summary>
    private static bool Write(
        string directory, SwiftBridgeRoutingJournal journal, string journalPath,
        IReadOnlyDictionary<SwiftArchitecture, string?> assemblies, IReadOnlyDictionary<SwiftArchitecture, string> assemblyPaths,
        IReadOnlyDictionary<string, string> csharp, IReadOnlyDictionary<string, string> current)
    {
        var changed = false;
        var journalText = journal.ToJson();
        if (!File.Exists(journalPath) || File.ReadAllText(journalPath) != journalText)
        {
            SwiftBridgeRoutingJournal.WriteAtomically(journalPath, journalText);
            changed = true;
        }
        foreach (var (architecture, path) in assemblyPaths)
        {
            var text = assemblies.GetValueOrDefault(architecture);
            var existing = File.Exists(path) ? File.ReadAllText(path) : null;
            if (text == existing)
                continue;
            if (text is null)
                File.Delete(path);
            else
                SwiftBridgeRoutingJournal.WriteAtomically(path, text);
            changed = true;
        }
        foreach (var (file, text) in csharp)
        {
            if (current.TryGetValue(file, out var existing) && existing == text)
                continue;
            SwiftBridgeRoutingJournal.WriteAtomically(Path.Combine(directory, file), text);
            changed = true;
        }
        return changed;
    }

    /// <summary>The plan sidecar judged and annotated with the routes, and the ledger with the bridges. Returns whether either changed.</summary>
    private static bool WriteSidecars(string directory, string @namespace, SwiftBridgeRouting routing, SwiftCallPlanSet judged, ILogger logger)
    {
        var changed = false;
        var plansPath = Path.Combine(directory, SwiftCallPlanSet.FileName(@namespace));
        var plansText = routing.Annotate(judged).ToJson();
        if (!File.Exists(plansPath) || File.ReadAllText(plansPath) != plansText)
        {
            SwiftBridgeRoutingJournal.WriteAtomically(plansPath, plansText);
            changed = true;
        }
        var ledgerPath = Path.Combine(directory, SwiftTransitionLedger.FileName(@namespace));
        if (File.Exists(ledgerPath))
        {
            try
            {
                var ledger = File.ReadAllText(ledgerPath);
                var bridged = SwiftTransitionLedger.WithBridges(ledger, routing.Routes);
                if (bridged != ledger)
                {
                    SwiftBridgeRoutingJournal.WriteAtomically(ledgerPath, bridged);
                    changed = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
            {
                logger.LogWarning("Could not record the bridges in the Swift transition ledger {Ledger}: {Message}", ledgerPath, ex.Message);
            }
        }
        return changed;
    }

    private static void LogRouted(ILogger logger, string module, SwiftBridgeRouting routing, bool relinked)
    {
        var bridged = routing.Routes.Count(r => r.IsBridged);
        if (routing.Routes.IsEmpty)
            logger.LogInformation("Bridge routing for {Module}: no executable forward plan.", module);
        else
            logger.LogInformation(
                "Bridge routing for {Module}: {Bridged} of {Routes} executable Swift-convention import(s) call their bridge through a twin ({Bridges} bridge(s)){Relinked}.",
                module, bridged, routing.Routes.Length, routing.Bridged.Length, relinked ? "; the wrapper was relinked with them" : "");
    }
}
