// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Drives <see cref="BinaryDependencyAnalyzer"/> to a fixpoint instead of one pass over the
    /// primary binary.
    /// </summary>
    /// <remarks>
    /// <para>Auto-detection exists so a consumer can point the generator at one xcframework with its
    /// siblings co-located and have the run close. One pass cannot deliver that: the moment a
    /// dependency is auto-added, <em>its</em> public surface becomes part of the compile-import graph
    /// that has to close, and nothing was ever re-scanned. A framework that re-exports a sibling
    /// (<c>@_exported import</c>) therefore takes the run down at Parse with
    /// <c>SWIFTBIND119 / InputClosureUnsatisfied</c> while the missing xcframework sits in the same
    /// directory the entire time — and, being auto-added rather than requested, it does not even
    /// appear in the searched-roots list the diagnostic prints.</para>
    /// <para>Each round scans two channels per newly-added module, because neither subsumes the
    /// other: the Mach-O link list (what the binary loads) and the <c>.swiftinterface</c> import lines
    /// (what the wrapper compile must resolve). A module can be a compile-import obligation without a
    /// link entry, and a link entry can exist for a module no interface mentions — the shape that
    /// seeded the auto-detection in the first place.</para>
    /// <para>Import-derived candidates are pre-filtered to those with a co-located xcframework. That
    /// is what keeps SDK modules (<c>Foundation</c>, <c>UIKit</c>, <c>Swift</c>) out without needing
    /// an SDK oracle here: they are resolved from the SDK, not from a sibling artifact, so no sibling
    /// exists and they are never proposed. Without the filter every SDK import would enter the
    /// unresolved list and be reported as a dependency degradation.</para>
    /// <para>The <c>@_implementationOnly</c> / <c>private import</c> forms are deliberately NOT
    /// filtered out, matching <see cref="AppleFrameworkImportDetector.Detect"/>: a non-public import
    /// still means this binding's dylib links that module, so it must be present. The
    /// sibling-existence filter already bounds what that can pull in.</para>
    /// </remarks>
    public static class DependencyClosureResolver
    {
        /// <summary>
        /// Hard bound on discovery rounds. A corpus that keeps producing new modules after this many
        /// rounds is either cyclic in a way the dedup should have caught or pathologically deep; both
        /// warrant stopping with a warning and letting the closure preflight report the real gap,
        /// rather than looping. Rounds are bounded, not modules — a single round can add many.
        /// </summary>
        internal const int MaxRounds = 8;

        // One module awaiting its own dependency scan.
        private readonly record struct ScanTarget(
            string ModuleName,
            string DylibPath,
            string XCFrameworkPath,
            string? SwiftInterfacePath);

        /// <summary>
        /// Resolves the primary's dependency closure by iterating link-list and import-edge discovery
        /// until a round adds nothing new.
        /// </summary>
        /// <param name="primaryDylibPath">The primary module's binary; seeds round 1's link scan.</param>
        /// <param name="primaryXCFrameworkPath">The primary xcframework; anchors the sibling search.</param>
        /// <param name="primaryModuleName">The module being bound. Never proposed as its own dependency.</param>
        /// <param name="primarySwiftInterfacePath">
        /// The primary's public <c>.swiftinterface</c>, when one exists; seeds round 1's import scan.
        /// A run with no readable interface (ABI/TBD only) simply contributes no import edges.
        /// </param>
        /// <param name="platformTarget">Slice selection for resolved siblings.</param>
        /// <param name="wrapperArchitectures">Which slices the wrapper compile will need.</param>
        /// <param name="logger">Receives one line per newly-added module and the round summary.</param>
        /// <param name="commandRunner">Injected for tests; defaults to the real process runner.</param>
        /// <param name="platformInfo">Platform info for slice selection.</param>
        /// <param name="companionFrameworkPaths">Explicit <c>--framework-dependency</c> search paths, forwarded.</param>
        /// <param name="preResolvedDependencies">
        /// Dependencies the caller already resolved explicitly (<c>--framework-dependency</c>). Treated as
        /// satisfied AND scanned: satisfied so auto-detection never proposes the co-located artifact they
        /// override, scanned so the artifact actually in use contributes its own import edges.
        /// </param>
        /// <returns>
        /// The merged analysis across every round, or null when the PRIMARY module's link scan failed
        /// (the caller's existing "systemic analysis failure" signal). Any other module's link-scan
        /// failure degrades to a warning and costs that module only its link edges — its import edges
        /// are still scanned and resolved, since discovering those never needed the binary. The rounds
        /// already completed are real results, and discarding them would be strictly worse than
        /// returning a partial closure the preflight can still adjudicate.
        /// </returns>
        public static DependencyAnalysisResult? ResolveToFixpoint(
            string primaryDylibPath,
            string primaryXCFrameworkPath,
            string primaryModuleName,
            string? primarySwiftInterfacePath,
            XCFrameworkPlatformTarget platformTarget,
            string wrapperArchitectures,
            ILogger logger,
            ICommandRunner? commandRunner = null,
            PlatformInfo? platformInfo = null,
            IReadOnlyList<string>? companionFrameworkPaths = null,
            IReadOnlyList<FrameworkDependencyInfo>? preResolvedDependencies = null)
        {
            ArgumentNullException.ThrowIfNull(logger);

            var resolved = new List<FrameworkDependencyInfo>();
            var allDetected = new List<DetectedDependency>();
            var allDetectedNames = new HashSet<string>(StringComparer.Ordinal);

            // Reported once per name, but NOT treated as final: sibling visibility is anchor-relative
            // (FindSiblingXCFramework searches the scanned module's OWN directory first), so "no
            // sibling from here" is not "no sibling anywhere". Keyed rather than a flat list so
            // ReconcileUnresolved can drop the entries something else in the closure satisfied.
            var unresolvedByName = new Dictionary<string, DetectedDependency>(StringComparer.Ordinal);

            // Everything satisfied: the primary, every pre-resolved input, and every module resolved
            // so far. Deliberately does NOT absorb unresolved names — doing so lets whichever anchor
            // happens to be scanned first veto a resolution a differently-located anchor could make.
            var accounted = new HashSet<string>(StringComparer.Ordinal) { primaryModuleName };

            // The frontier: modules whose own dependencies have not been scanned yet. Round 1 scans the
            // primary; each later round scans exactly what the previous one added.
            var frontier = new List<ScanTarget>
            {
                new(primaryModuleName, primaryDylibPath, primaryXCFrameworkPath, primarySwiftInterfacePath),
            };

            // Explicitly-supplied dependencies are inputs, not discoveries: they are satisfied from the
            // start (so auto-detection never proposes the co-located artifact they shadow, whose own
            // transitive deps would otherwise survive the caller's override merge and pull in the
            // WRONG version's imports) and they are scanned (so the artifact actually being used
            // contributes ITS import edges to the closure).
            if (preResolvedDependencies != null)
            {
                foreach (var dep in preResolvedDependencies)
                {
                    if (!accounted.Add(dep.ModuleName))
                        continue;
                    // Same Swift-binary requirement as the discovery path below, for the same reason:
                    // an explicitly-supplied ObjC-only dependency is satisfied (so nothing re-proposes
                    // it) but contributes no scannable Swift edges.
                    if (!string.IsNullOrEmpty(dep.DylibPath) && !string.IsNullOrEmpty(dep.XCFrameworkPath))
                    {
                        frontier.Add(new ScanTarget(
                            dep.ModuleName, dep.DylibPath!, dep.XCFrameworkPath!, LocateSwiftInterface(dep)));
                    }
                }
            }

            for (var round = 1; frontier.Count > 0; round++)
            {
                if (round > MaxRounds)
                {
                    logger.LogWarning(
                        "Dependency auto-detection stopped after {Rounds} rounds with {Pending} module(s) still " +
                        "unscanned ({Modules}). The closure preflight will report any module this leaves missing.",
                        MaxRounds, frontier.Count,
                        string.Join(", ", frontier.Select(f => f.ModuleName)));
                    break;
                }

                var nextFrontier = new List<ScanTarget>();

                foreach (var scanned in frontier)
                {
                    var extra = CollectImportCandidates(
                        scanned.SwiftInterfacePath, scanned.XCFrameworkPath, accounted);

                    // The two channels are scanned independently, which is the whole point of having
                    // two: an unreadable binary costs this module its LINK edges, not its import
                    // edges. Folding both into one call that bails on the otool exit code discards
                    // `extra` — candidates already in hand, whose discovery never touched otool —
                    // and a pure `@_exported import` sibling sitting right beside the anchor then
                    // goes undiscovered and takes the run down at Parse instead.
                    var linkScan = BinaryDependencyAnalyzer.ScanLinkedDependencies(
                        scanned.DylibPath, scanned.ModuleName, logger, commandRunner);

                    if (!linkScan.Succeeded)
                    {
                        // Keyed on identity, not on `round == 1`: round 1 now also carries the
                        // pre-resolved inputs, and one of THOSE failing to scan is a degradation, not
                        // the systemic "cannot read the primary" signal the caller handles. Being
                        // unable to read the primary at all is still that signal, and still aborts —
                        // the degraded path below is for a module the closure merely passed through.
                        if (string.Equals(scanned.ModuleName, primaryModuleName, StringComparison.Ordinal))
                            return null;
                        logger.LogWarning(
                            "Dependency auto-detection could not analyze '{Module}': its link list is " +
                            "unavailable, so only its .swiftinterface import edges were scanned.",
                            scanned.ModuleName);
                    }

                    var result = BinaryDependencyAnalyzer.ResolveDetected(
                        linkScan.Dependencies, scanned.XCFrameworkPath, scanned.ModuleName,
                        platformTarget, wrapperArchitectures, logger,
                        commandRunner, platformInfo, companionFrameworkPaths,
                        additionalDetected: extra,
                        excludeModules: accounted);

                    foreach (var dep in result.AllDetected)
                    {
                        if (allDetectedNames.Add(dep.FrameworkName))
                            allDetected.Add(dep);
                    }

                    foreach (var dep in result.ResolvedDependencies)
                    {
                        // Analyze already excluded `accounted`, but two modules scanned in the SAME
                        // round can both name a third — so claim the name here, not in the loop above.
                        if (!accounted.Add(dep.ModuleName))
                            continue;
                        resolved.Add(dep);

                        if (round > 1 || !string.Equals(scanned.ModuleName, primaryModuleName, StringComparison.Ordinal))
                        {
                            // Debug, not Information: the caller already logs every resolved entry at
                            // Information. What this adds is the "via" provenance — the edge that
                            // explains why a module nobody asked for is in the closure — which is only
                            // wanted when actually diagnosing one, not on every run.
                            logger.LogDebug(
                                "Auto-detected transitive dependency: {Module} (via {Via})",
                                dep.ModuleName, scanned.ModuleName);
                        }

                        // A resolved dependency joins the frontier only if it has a Swift binary to
                        // scan. That excludes ObjC-only siblings, which BinaryDependencyAnalyzer
                        // records with IsObjCOnly and no DylibPath — so an ObjC framework's OWN link
                        // edges are not walked, and a module reachable only through one stays
                        // undiscovered. That is the closure's deliberate boundary, not an oversight:
                        // both scan channels here are Swift-shaped (the Swift dylib's link list and
                        // the .swiftinterface's imports), and the compile-import graph this exists to
                        // close is the Swift one. Widening it means resolving an ObjC framework's
                        // Mach-O binary out of its slice search path and walking that instead — a
                        // different traversal, not a missing condition on this one.
                        if (!string.IsNullOrEmpty(dep.DylibPath) && !string.IsNullOrEmpty(dep.XCFrameworkPath))
                        {
                            nextFrontier.Add(new ScanTarget(
                                dep.ModuleName,
                                dep.DylibPath!,
                                dep.XCFrameworkPath!,
                                LocateSwiftInterface(dep)));
                        }
                    }

                    foreach (var dep in result.UnresolvedDependencies)
                    {
                        // Record for reporting, but leave the name UNaccounted so a later, differently
                        // anchored scan is still allowed to resolve it. Costs a repeat sibling probe
                        // per scanned module for a genuinely-absent one — bounded by the frontier,
                        // since an unresolved module never joins it. Whether the report survives is
                        // ReconcileUnresolved's call, not this loop's: filtering here would have to
                        // compare a framework name against the module names in `accounted`, and those
                        // two identities are not interchangeable.
                        unresolvedByName.TryAdd(dep.FrameworkName, dep);
                    }
                }

                frontier = nextFrontier;
            }

            return new DependencyAnalysisResult
            {
                ResolvedDependencies = resolved,
                UnresolvedDependencies = ReconcileUnresolved(
                    unresolvedByName, resolved, preResolvedDependencies, primaryModuleName),
                AllDetected = allDetected,
            };
        }

        /// <summary>
        /// Drops from the unresolved report every entry that something else in the closure in fact
        /// satisfied, matching on BOTH identities a dependency is known by.
        /// </summary>
        /// <remarks>
        /// <para>A detected dependency is named by its <em>framework</em> (the <c>@rpath</c> install-name
        /// basename, <see cref="DetectedDependency.FrameworkName"/>), while a resolved one is named by the
        /// <em>Swift module</em> read out of the slice (<see cref="FrameworkDependencyInfo.ModuleName"/>,
        /// from <c>depResolution.ModuleName</c>). Those are the same string for most frameworks but are
        /// not required to be — one binary may vend a differently-named module. Reconciling on module
        /// name alone therefore leaves a satisfied dependency sitting in the unresolved list under its
        /// framework name, which reads as a degradation and makes a strict caller reject a closure that
        /// is actually complete.</para>
        /// <para>The framework name is recovered from the resolved artifact's own path because that is
        /// exactly what produced it: <c>BinaryDependencyAnalyzer</c> obtains the artifact via
        /// <c>FindSiblingXCFramework(anchor, dep.FrameworkName)</c>, so the <c>.xcframework</c> basename
        /// IS the framework name that was searched for. Doing the whole reconciliation once, here, rather
        /// than at each place a name is added or retracted, means the two identities are matched up in a
        /// single spot that cannot drift out of step with a second one.</para>
        /// </remarks>
        private static List<DetectedDependency> ReconcileUnresolved(
            Dictionary<string, DetectedDependency> unresolvedByName,
            IReadOnlyList<FrameworkDependencyInfo> resolved,
            IReadOnlyList<FrameworkDependencyInfo>? preResolvedDependencies,
            string primaryModuleName)
        {
            if (unresolvedByName.Count == 0)
                return new List<DetectedDependency>();

            var satisfied = new HashSet<string>(StringComparer.Ordinal) { primaryModuleName };

            void MarkSatisfied(FrameworkDependencyInfo dep)
            {
                satisfied.Add(dep.ModuleName);
                if (!string.IsNullOrEmpty(dep.XCFrameworkPath))
                    satisfied.Add(Path.GetFileNameWithoutExtension(dep.XCFrameworkPath!));
            }

            foreach (var dep in resolved)
                MarkSatisfied(dep);

            if (preResolvedDependencies != null)
            {
                foreach (var dep in preResolvedDependencies)
                    MarkSatisfied(dep);
            }

            return unresolvedByName.Values
                .Where(dep => !satisfied.Contains(dep.FrameworkName))
                .ToList();
        }

        /// <summary>
        /// Turns a supplied module's <c>import</c> lines into resolution candidates, keeping only those
        /// with a co-located sibling xcframework (see the class remarks for why that filter is the SDK
        /// exclusion). Returns an empty list when the interface is absent or unreadable — an
        /// undiscoverable edge is left to the closure preflight to report, never guessed at.
        /// </summary>
        internal static IReadOnlyList<DetectedDependency> CollectImportCandidates(
            string? swiftInterfacePath,
            string anchorXCFrameworkPath,
            IReadOnlySet<string> accounted)
        {
            if (string.IsNullOrEmpty(swiftInterfacePath) || !File.Exists(swiftInterfacePath))
                return Array.Empty<DetectedDependency>();

            List<string> imports;
            try
            {
                imports = AppleFrameworkImportDetector.ExtractImports(File.ReadAllText(swiftInterfacePath));
            }
            catch (IOException)
            {
                return Array.Empty<DetectedDependency>();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<DetectedDependency>();
            }

            var candidates = new List<DetectedDependency>();
            foreach (var module in imports)
            {
                if (accounted.Contains(module))
                    continue;
                var sibling = BinaryDependencyAnalyzer.FindSiblingXCFramework(anchorXCFrameworkPath, module);
                if (sibling == null)
                    continue;
                candidates.Add(new DetectedDependency
                {
                    FrameworkName = module,
                    // Deliberately the discovered sibling PATH, not an `@rpath/X.framework/X` install
                    // name: there may be no link edge at all here — that is what makes this an
                    // import-only candidate. Fabricating an install name would assert a load command
                    // the binary does not have. The field is diagnostic (it reaches the dependency
                    // manifest and nothing parses it back), and `Source` records the provenance.
                    InstallName = sibling,
                    Source = "swiftinterface-import",
                });
            }

            return candidates;
        }

        // A resolved dependency's own public interface, so the next round can read its import edges.
        // Prefers the slice the wrapper compile will use; falls back to the other so a device-only or
        // simulator-only sibling still contributes edges.
        private static string? LocateSwiftInterface(FrameworkDependencyInfo dep) =>
            InputInventory.LocateModuleSwiftInterface(dep.SimulatorFrameworkSearchPath, dep.ModuleName)
            ?? InputInventory.LocateModuleSwiftInterface(dep.DeviceFrameworkSearchPath, dep.ModuleName);
    }
}
