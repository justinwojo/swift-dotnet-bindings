// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Covers the dependency-closure fixpoint: auto-detection must keep scanning newly-added modules
    /// until a round adds nothing, because an auto-added dependency brings its own public surface into
    /// the compile-import graph that has to close. A single pass over the primary leaves a re-exporting
    /// sibling undiscovered and takes the whole run down at Parse with an unsatisfiable import edge —
    /// while the missing xcframework sits in the same directory the entire time.
    /// </summary>
    public class DependencyClosureResolverTests : IDisposable
    {
        private readonly string _tempDir;

        public DependencyClosureResolverTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"depclosure_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }

        private const string SliceId = "ios-arm64-simulator";

        private static string SimOnlyPlist(string name) => $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
            <dict>
                <key>AvailableLibraries</key>
                <array>
                    <dict>
                        <key>BinaryPath</key><string>{{name}}.framework/{{name}}</string>
                        <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                        <key>LibraryPath</key><string>{{name}}.framework</string>
                        <key>SupportedArchitectures</key><array><string>arm64</string></array>
                        <key>SupportedPlatform</key><string>ios</string>
                        <key>SupportedPlatformVariant</key><string>simulator</string>
                    </dict>
                </array>
            </dict>
            </plist>
            """;

        /// <summary>
        /// Creates a simulator-only xcframework carrying enough Swift evidence (abi.json + tbd) that
        /// <see cref="XCFrameworkResolver.Resolve"/> succeeds without a toolchain. Optionally writes a
        /// public <c>.swiftinterface</c> so the module contributes import edges.
        /// </summary>
        /// <param name="moduleName">
        /// The Swift module the framework vends, when it differs from the framework's own name — the
        /// binary is found by framework name (the <c>@rpath</c> install-name basename) but resolves to
        /// whatever module the <c>.swiftmodule</c> directory declares.
        /// </param>
        private (string XCFrameworkPath, string DylibPath) CreateModule(
            string name, string? swiftInterfaceText = null, string? subdir = null,
            string? moduleName = null)
        {
            var root = subdir == null ? _tempDir : Path.Combine(_tempDir, subdir);
            Directory.CreateDirectory(root);
            var xcfwPath = Path.Combine(root, $"{name}.xcframework");
            Directory.CreateDirectory(xcfwPath);
            File.WriteAllText(Path.Combine(xcfwPath, "Info.plist"), SimOnlyPlist(name));

            var fwDir = Path.Combine(xcfwPath, SliceId, $"{name}.framework");
            Directory.CreateDirectory(fwDir);
            var dylibPath = Path.Combine(fwDir, name);
            File.WriteAllText(dylibPath, "");

            var vendedModule = moduleName ?? name;
            var moduleDir = Path.Combine(fwDir, "Modules", $"{vendedModule}.swiftmodule");
            Directory.CreateDirectory(moduleDir);
            File.WriteAllText(Path.Combine(moduleDir, "arm64-apple-ios-simulator.abi.json"), "{}");
            File.WriteAllText(Path.Combine(moduleDir, $"{vendedModule}.tbd"), "--- !tapi-tbd");

            if (swiftInterfaceText != null)
            {
                File.WriteAllText(
                    Path.Combine(moduleDir, "arm64-apple-ios-simulator.swiftinterface"), swiftInterfaceText);
            }

            return (xcfwPath, dylibPath);
        }

        /// <summary>
        /// Mocks <c>otool -L</c> for one binary. Keyed on the <c>-L "&lt;path&gt;"</c> fragment rather
        /// than the bare path so the response cannot also satisfy the other probes
        /// (<c>otool -l</c>, metadata extraction) that mention the same binary.
        /// </summary>
        private static void SetLinkList(MockCommandRunner runner, string dylibPath, params string[] frameworkNames)
        {
            var lines = string.Join("\n", frameworkNames.Select(
                n => $"\t@rpath/{n}.framework/{n} (compatibility version 0.0.0, current version 0.0.0)"));
            runner.SetResponse($"-L \"{dylibPath}\"", 0, $"{dylibPath}:\n{lines}\n");
        }

        private static MockCommandRunner NewRunner()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");
            return runner;
        }

        private static DependencyAnalysisResult? Run(
            MockCommandRunner runner, ILogger logger,
            string primaryXcfw, string primaryDylib, string primaryModule,
            string? primaryInterface = null,
            IReadOnlyList<FrameworkDependencyInfo>? preResolved = null) =>
            DependencyClosureResolver.ResolveToFixpoint(
                primaryDylib, primaryXcfw, primaryModule, primaryInterface,
                XCFrameworkPlatformTarget.Simulator, "simulator", logger, runner,
                preResolvedDependencies: preResolved);

        // Mirrors what ResolveFrameworkDependencies hands back for an explicit --framework-dependency:
        // the chosen artifact plus the slice search path the interface locator reads.
        private static FrameworkDependencyInfo ManualDep(
            string name, string xcfwPath, string dylibPath) =>
            new()
            {
                ModuleName = name,
                XCFrameworkPath = xcfwPath,
                DylibPath = dylibPath,
                SimulatorFrameworkSearchPath = Path.Combine(xcfwPath, SliceId),
            };

        // ── Link-list channel ────────────────────────────────────────────────

        [Fact]
        public void ResolveToFixpoint_TransitiveLinkDependency_DiscoveredInLaterRound()
        {
            var (primaryXcfw, primaryDylib) = CreateModule("Primary");
            var (_, middleDylib) = CreateModule("Middle");
            CreateModule("Leaf");

            var runner = NewRunner();
            SetLinkList(runner, primaryDylib, "Middle");
            SetLinkList(runner, middleDylib, "Leaf");

            var result = Run(runner, new CapturingLogger(), primaryXcfw, primaryDylib, "Primary");

            Assert.NotNull(result);
            Assert.Equal(
                new[] { "Leaf", "Middle" },
                result!.ResolvedDependencies.Select(d => d.ModuleName).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Empty(result.UnresolvedDependencies);
            Assert.Equal(
                new[] { "Leaf", "Middle" },
                result.AllDetected.Select(d => d.FrameworkName).OrderBy(n => n, StringComparer.Ordinal));
        }

        /// <summary>
        /// The control for the test above: one pass over the primary genuinely cannot see Leaf. This is
        /// the defect shape, pinned so a future "simplification" back to a single Analyze call fails here
        /// rather than only in a corpus run.
        /// </summary>
        [Fact]
        public void Analyze_SinglePass_DoesNotSeeTheTransitiveDependency()
        {
            var (primaryXcfw, primaryDylib) = CreateModule("Primary");
            var (_, middleDylib) = CreateModule("Middle");
            CreateModule("Leaf");

            var runner = NewRunner();
            SetLinkList(runner, primaryDylib, "Middle");
            SetLinkList(runner, middleDylib, "Leaf");

            var result = BinaryDependencyAnalyzer.Analyze(
                primaryDylib, primaryXcfw, "Primary",
                XCFrameworkPlatformTarget.Simulator, "simulator", new CapturingLogger(), runner);

            Assert.NotNull(result);
            Assert.Equal("Middle", Assert.Single(result!.ResolvedDependencies).ModuleName);
        }

        [Fact]
        public void ResolveToFixpoint_DiamondDependency_ResolvesSharedModuleOnce()
        {
            var (primaryXcfw, primaryDylib) = CreateModule("Primary");
            var (_, leftDylib) = CreateModule("Left");
            var (_, rightDylib) = CreateModule("Right");
            CreateModule("Shared");

            var runner = NewRunner();
            SetLinkList(runner, primaryDylib, "Left", "Right");
            SetLinkList(runner, leftDylib, "Shared");
            SetLinkList(runner, rightDylib, "Shared");

            var result = Run(runner, new CapturingLogger(), primaryXcfw, primaryDylib, "Primary");

            Assert.NotNull(result);
            Assert.Equal(
                new[] { "Left", "Right", "Shared" },
                result!.ResolvedDependencies.Select(d => d.ModuleName).OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void ResolveToFixpoint_DependencyCycle_Terminates()
        {
            var (primaryXcfw, primaryDylib) = CreateModule("Primary");
            var (_, aDylib) = CreateModule("Alpha");
            var (_, bDylib) = CreateModule("Beta");

            var runner = NewRunner();
            SetLinkList(runner, primaryDylib, "Alpha");
            SetLinkList(runner, aDylib, "Beta", "Primary");
            SetLinkList(runner, bDylib, "Alpha", "Primary");

            var result = Run(runner, new CapturingLogger(), primaryXcfw, primaryDylib, "Primary");

            Assert.NotNull(result);
            Assert.Equal(
                new[] { "Alpha", "Beta" },
                result!.ResolvedDependencies.Select(d => d.ModuleName).OrderBy(n => n, StringComparer.Ordinal));
            Assert.DoesNotContain("Primary", result.ResolvedDependencies.Select(d => d.ModuleName));
        }

        [Fact]
        public void ResolveToFixpoint_ChainDeeperThanMaxRounds_StopsWithWarning()
        {
            // M0 (primary) → M1 → … → M9. Round N scans M(N-1), so the bound admits M1…M{MaxRounds}
            // and the tail is left to the closure preflight to report.
            var modules = Enumerable.Range(0, DependencyClosureResolver.MaxRounds + 2)
                .Select(i => (Name: $"M{i}", Fixture: CreateModule($"M{i}")))
                .ToList();

            var runner = NewRunner();
            for (var i = 0; i < modules.Count - 1; i++)
                SetLinkList(runner, modules[i].Fixture.DylibPath, modules[i + 1].Name);

            var logger = new CapturingLogger();
            var result = Run(
                runner, logger, modules[0].Fixture.XCFrameworkPath, modules[0].Fixture.DylibPath, "M0");

            Assert.NotNull(result);
            var resolvedNames = result!.ResolvedDependencies.Select(d => d.ModuleName).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(DependencyClosureResolver.MaxRounds, resolvedNames.Count);
            Assert.Contains($"M{DependencyClosureResolver.MaxRounds}", resolvedNames);
            Assert.DoesNotContain($"M{DependencyClosureResolver.MaxRounds + 1}", resolvedNames);

            var warning = Assert.Single(logger.Entries, e =>
                e.Level == LogLevel.Warning && e.Message.Contains("stopped after", StringComparison.Ordinal));
            Assert.Contains($"M{DependencyClosureResolver.MaxRounds}", warning.Message, StringComparison.Ordinal);
        }

        // ── Import-edge channel ──────────────────────────────────────────────

        [Fact]
        public void ResolveToFixpoint_ImportOnlyDependency_DiscoveredFromSwiftInterface()
        {
            // No link entry at all — the module is a compile-import obligation only, which is exactly
            // the shape the link list cannot see.
            var (primaryXcfw, primaryDylib) = CreateModule("Primary", """
                // swift-interface-format-version: 1.0
                @_exported import ReExported
                public struct Thing {}
                """);
            CreateModule("ReExported");

            var runner = NewRunner();
            SetLinkList(runner, primaryDylib);

            var interfacePath = Path.Combine(
                primaryXcfw, SliceId, "Primary.framework", "Modules", "Primary.swiftmodule",
                "arm64-apple-ios-simulator.swiftinterface");

            var result = Run(runner, new CapturingLogger(), primaryXcfw, primaryDylib, "Primary", interfacePath);

            Assert.NotNull(result);
            Assert.Equal("ReExported", Assert.Single(result!.ResolvedDependencies).ModuleName);
            Assert.Equal("swiftinterface-import", Assert.Single(result.AllDetected).Source);
        }

        [Fact]
        public void ResolveToFixpoint_TransitiveImportOnlyDependency_DiscoveredFromDependencyInterface()
        {
            // The B11 shape end to end: the primary links a sibling, and that sibling re-exports a third
            // module that appears in neither binary's link list.
            var (primaryXcfw, primaryDylib) = CreateModule("Primary");
            var (_, hubDylib) = CreateModule("Hub", """
                // swift-interface-format-version: 1.0
                @_exported import Spoke
                """);
            CreateModule("Spoke");

            var runner = NewRunner();
            SetLinkList(runner, primaryDylib, "Hub");
            SetLinkList(runner, hubDylib);

            var result = Run(runner, new CapturingLogger(), primaryXcfw, primaryDylib, "Primary");

            Assert.NotNull(result);
            Assert.Equal(
                new[] { "Hub", "Spoke" },
                result!.ResolvedDependencies.Select(d => d.ModuleName).OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void ResolveToFixpoint_SdkImports_AreNotProposedAsDependencies()
        {
            // Foundation/UIKit/Swift resolve from the SDK, not from a co-located artifact — so no sibling
            // exists and they must never enter the unresolved list as a dependency degradation.
            var (primaryXcfw, primaryDylib) = CreateModule("Primary", """
                // swift-interface-format-version: 1.0
                import Swift
                import Foundation
                import UIKit
                """);

            var runner = NewRunner();
            SetLinkList(runner, primaryDylib);

            var interfacePath = Path.Combine(
                primaryXcfw, SliceId, "Primary.framework", "Modules", "Primary.swiftmodule",
                "arm64-apple-ios-simulator.swiftinterface");

            var result = Run(runner, new CapturingLogger(), primaryXcfw, primaryDylib, "Primary", interfacePath);

            Assert.NotNull(result);
            Assert.Empty(result!.ResolvedDependencies);
            Assert.Empty(result.UnresolvedDependencies);
            Assert.Empty(result.AllDetected);
        }

        [Fact]
        public void ResolveToFixpoint_NonPublicImportWithSibling_IsStillProposed()
        {
            // A non-public import still means this binding's dylib links the module, so it must be
            // present — matching AppleFrameworkImportDetector.Detect rather than the wrapper-re-emission
            // filter, which drops the same edges for a different reason.
            var (primaryXcfw, primaryDylib) = CreateModule("Primary", """
                // swift-interface-format-version: 1.0
                @_implementationOnly import Hidden
                """);
            CreateModule("Hidden");

            var runner = NewRunner();
            SetLinkList(runner, primaryDylib);

            var interfacePath = Path.Combine(
                primaryXcfw, SliceId, "Primary.framework", "Modules", "Primary.swiftmodule",
                "arm64-apple-ios-simulator.swiftinterface");

            var result = Run(runner, new CapturingLogger(), primaryXcfw, primaryDylib, "Primary", interfacePath);

            Assert.NotNull(result);
            Assert.Equal("Hidden", Assert.Single(result!.ResolvedDependencies).ModuleName);
        }

        // ── Failure handling ─────────────────────────────────────────────────

        [Fact]
        public void ResolveToFixpoint_PrimaryAnalysisFails_ReturnsNull()
        {
            var (primaryXcfw, primaryDylib) = CreateModule("Primary");

            var runner = NewRunner();
            runner.SetResponse($"-L \"{primaryDylib}\"", 1, "", "otool: can't open file");

            Assert.Null(Run(runner, new CapturingLogger(), primaryXcfw, primaryDylib, "Primary"));
        }

        /// <summary>
        /// The two discovery channels are independent by design — "neither subsumes the other" — so a
        /// module whose binary cannot be read must still contribute its <c>.swiftinterface</c> import
        /// edges. Those candidates never needed <c>otool</c> to be discovered, and dropping them along
        /// with the link list loses a co-located <c>@_exported import</c> sibling entirely: the run
        /// then dies at Parse with SWIFTBIND119 naming a module that was sitting beside the anchor the
        /// whole time. Scanning both channels through one call that bails on the otool exit code is
        /// exactly what produced that, so this pins the degradation as PARTIAL.
        /// </summary>
        [Fact]
        public void ResolveToFixpoint_LinkScanFails_StillDiscoversThatModulesImportEdges()
        {
            var (primaryXcfw, primaryDylib) = CreateModule("Primary");
            var (_, hubDylib) = CreateModule("Hub", """
                // swift-interface-format-version: 1.0
                @_exported import Spoke
                """);
            CreateModule("Spoke");

            var runner = NewRunner();
            SetLinkList(runner, primaryDylib, "Hub");
            // Hub resolves fine; only its own link scan is unreadable.
            runner.SetResponse($"-L \"{hubDylib}\"", 1, "", "otool: can't open file");

            var logger = new CapturingLogger();
            var result = Run(runner, logger, primaryXcfw, primaryDylib, "Primary");

            Assert.NotNull(result);
            Assert.Equal(
                new[] { "Hub", "Spoke" },
                result!.ResolvedDependencies.Select(d => d.ModuleName).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Empty(result.UnresolvedDependencies);

            // The lost channel is still reported — degraded, not silent.
            Assert.Contains(logger.Entries, e =>
                e.Level == LogLevel.Warning &&
                e.Message.Contains("could not analyze 'Hub'", StringComparison.Ordinal));
        }

        /// <summary>
        /// The companion to the test above, and the reason the fix could not simply stop treating a
        /// failed link scan as fatal: an unreadable PRIMARY is still the caller's systemic
        /// "cannot read the inputs at all" signal (<c>RecordSystemicDependencyAnalysisFailure</c>),
        /// and must abort even when the primary has a perfectly readable interface whose imports
        /// would otherwise resolve. Collapsing the two meanings turns a fail-closed diagnostic into a
        /// partial closure that looks complete.
        /// </summary>
        [Fact]
        public void ResolveToFixpoint_PrimaryLinkScanFails_StillReturnsNull_EvenWithResolvableImports()
        {
            var (primaryXcfw, primaryDylib) = CreateModule("Primary", """
                // swift-interface-format-version: 1.0
                @_exported import Spoke
                """);
            CreateModule("Spoke");

            var runner = NewRunner();
            runner.SetResponse($"-L \"{primaryDylib}\"", 1, "", "otool: can't open file");

            var interfacePath = Path.Combine(
                primaryXcfw, SliceId, "Primary.framework", "Modules", "Primary.swiftmodule",
                "arm64-apple-ios-simulator.swiftinterface");

            Assert.Null(Run(
                runner, new CapturingLogger(), primaryXcfw, primaryDylib, "Primary", interfacePath));
        }

        [Fact]
        public void ResolveToFixpoint_TransitiveAnalysisFails_KeepsEarlierRoundsAndWarns()
        {
            var (primaryXcfw, primaryDylib) = CreateModule("Primary");
            var (_, middleDylib) = CreateModule("Middle");

            var runner = NewRunner();
            SetLinkList(runner, primaryDylib, "Middle");
            runner.SetResponse($"-L \"{middleDylib}\"", 1, "", "otool: can't open file");

            var logger = new CapturingLogger();
            var result = Run(runner, logger, primaryXcfw, primaryDylib, "Primary");

            Assert.NotNull(result);
            Assert.Equal("Middle", Assert.Single(result!.ResolvedDependencies).ModuleName);
            Assert.Contains(logger.Entries, e =>
                e.Level == LogLevel.Warning &&
                e.Message.Contains("could not analyze 'Middle'", StringComparison.Ordinal));
        }

        [Fact]
        public void ResolveToFixpoint_UnresolvableDependency_ReportedOnce()
        {
            var (primaryXcfw, primaryDylib) = CreateModule("Primary");
            var (_, middleDylib) = CreateModule("Middle");
            // No Ghost.xcframework anywhere — and both scanned binaries name it.

            var runner = NewRunner();
            SetLinkList(runner, primaryDylib, "Middle", "Ghost");
            SetLinkList(runner, middleDylib, "Ghost");

            var result = Run(runner, new CapturingLogger(), primaryXcfw, primaryDylib, "Primary");

            Assert.NotNull(result);
            Assert.Equal("Middle", Assert.Single(result!.ResolvedDependencies).ModuleName);
            var ghost = Assert.Single(result.UnresolvedDependencies);
            Assert.Equal("Ghost", ghost.FrameworkName);
            Assert.Equal("no-xcframework", ghost.UnresolvedReason);
        }

        /// <summary>
        /// Sibling lookup is anchor-RELATIVE — <c>FindSiblingXCFramework</c> searches the scanned
        /// module's own directory first — so "not found from here" is not "not found anywhere".
        /// Primary (in <c>Primary/</c>) cannot see <c>Middle/Ghost.xcframework</c>, but Middle can.
        /// Recording Primary's miss as final would let whichever anchor happens to be scanned first
        /// veto a resolution a later one can make, and the run would then fail closure on a module
        /// that is present on disk.
        /// </summary>
        [Fact]
        public void ResolveToFixpoint_UnresolvableFromOneAnchor_StillResolvedFromAnother()
        {
            var (primaryXcfw, primaryDylib) = CreateModule("Primary", subdir: "Primary");
            var (middleXcfw, middleDylib) = CreateModule("Middle", subdir: "Middle");
            CreateModule("Ghost", subdir: "Middle");

            var runner = NewRunner();
            // Primary names both, but from Primary/ only Middle is reachable (via the peer-subdir arm).
            SetLinkList(runner, primaryDylib, "Middle", "Ghost");
            SetLinkList(runner, middleDylib, "Ghost");

            var logger = new CapturingLogger();
            var result = Run(runner, logger, primaryXcfw, primaryDylib, "Primary");

            Assert.NotNull(result);
            var names = result!.ResolvedDependencies.Select(d => d.ModuleName).ToList();
            Assert.Contains("Middle", names);
            Assert.Single(names, n => n == "Ghost");
            // And the earlier miss must be retracted, not merely outvoted.
            Assert.DoesNotContain("Ghost", result.UnresolvedDependencies.Select(d => d.FrameworkName));
            Assert.Equal(
                middleXcfw,
                result.ResolvedDependencies.First(d => d.ModuleName == "Middle").XCFrameworkPath);
        }

        /// <summary>
        /// A dependency is known by two identities that are not required to agree: the FRAMEWORK name
        /// (the <c>@rpath</c> install-name basename, which is what a miss is reported under) and the
        /// SWIFT MODULE the resolved slice turns out to vend (which is what a hit is recorded under).
        /// Reconciling the unresolved report on module name alone leaves a dependency that WAS found
        /// sitting in the unresolved list under its framework name — a phantom degradation that makes a
        /// strict caller reject a closure which is in fact complete.
        /// </summary>
        [Fact]
        public void ResolveToFixpoint_ResolvedUnderADifferentModuleName_RetractsTheFrameworkNameMiss()
        {
            var (primaryXcfw, primaryDylib) = CreateModule("Primary", subdir: "Primary");
            var (_, middleDylib) = CreateModule("Middle", subdir: "Middle");
            // Ghost.framework vends module GhostCore — reachable only from Middle's anchor, so Primary
            // reports the miss under "Ghost" and Middle records the hit under "GhostCore".
            CreateModule("Ghost", subdir: "Middle", moduleName: "GhostCore");

            var runner = NewRunner();
            SetLinkList(runner, primaryDylib, "Middle", "Ghost");
            SetLinkList(runner, middleDylib, "Ghost");

            var result = Run(runner, new CapturingLogger(), primaryXcfw, primaryDylib, "Primary");

            Assert.NotNull(result);
            Assert.Contains("GhostCore", result!.ResolvedDependencies.Select(d => d.ModuleName));
            Assert.DoesNotContain("Ghost", result.UnresolvedDependencies.Select(d => d.FrameworkName));
        }

        /// <summary>
        /// An explicit <c>--framework-dependency</c> overrides the co-located artifact auto-detection
        /// would otherwise pick. The closure must be seeded with the OVERRIDING artifact: scanning the
        /// one about to be discarded pulls in its transitive imports (which survive the caller's merge,
        /// since that only drops the overridden module itself) while the chosen artifact's own imports
        /// are never discovered at all — the closure then fails on a module sitting beside the manual
        /// input.
        /// </summary>
        [Fact]
        public void ResolveToFixpoint_ManualOverride_ScansChosenArtifactNotShadowedOne()
        {
            var (primaryXcfw, primaryDylib) = CreateModule("Primary");

            // The co-located Foo auto-detection would find, importing Baz.
            var (_, shadowedFooDylib) = CreateModule("Foo", "import Swift\nimport Baz\n");
            CreateModule("Baz");

            // The manually supplied Foo actually in use, importing Bar beside it.
            var (manualFooXcfw, manualFooDylib) = CreateModule(
                "Foo", "import Swift\nimport Bar\n", subdir: "manual");
            CreateModule("Bar", subdir: "manual");

            var runner = NewRunner();
            SetLinkList(runner, primaryDylib, "Foo");
            SetLinkList(runner, shadowedFooDylib);
            SetLinkList(runner, manualFooDylib);

            var logger = new CapturingLogger();
            var result = Run(runner, logger, primaryXcfw, primaryDylib, "Primary",
                preResolved: new[] { ManualDep("Foo", manualFooXcfw, manualFooDylib) });

            Assert.NotNull(result);
            var names = result!.ResolvedDependencies.Select(d => d.ModuleName).ToList();
            Assert.Contains("Bar", names);          // the chosen artifact's edge was followed
            Assert.DoesNotContain("Baz", names);    // the shadowed artifact was never scanned
            // Foo is a caller-supplied input, not a discovery — it must not be re-proposed.
            Assert.DoesNotContain("Foo", names);
        }

        // ── Import-candidate collection ──────────────────────────────────────

        [Fact]
        public void CollectImportCandidates_NoInterface_ReturnsEmpty()
        {
            var (primaryXcfw, _) = CreateModule("Primary");

            Assert.Empty(DependencyClosureResolver.CollectImportCandidates(
                null, primaryXcfw, new HashSet<string>(StringComparer.Ordinal)));
            Assert.Empty(DependencyClosureResolver.CollectImportCandidates(
                Path.Combine(_tempDir, "does-not-exist.swiftinterface"), primaryXcfw,
                new HashSet<string>(StringComparer.Ordinal)));
        }

        [Fact]
        public void CollectImportCandidates_SkipsAccountedAndSiblinglessModules()
        {
            var (primaryXcfw, _) = CreateModule("Primary");
            CreateModule("Present");
            CreateModule("Already");

            var interfacePath = Path.Combine(_tempDir, "probe.swiftinterface");
            File.WriteAllText(interfacePath, """
                import Present
                import Already
                import Absent
                """);

            var candidates = DependencyClosureResolver.CollectImportCandidates(
                interfacePath, primaryXcfw, new HashSet<string>(StringComparer.Ordinal) { "Already" });

            var candidate = Assert.Single(candidates);
            Assert.Equal("Present", candidate.FrameworkName);
            Assert.Equal("swiftinterface-import", candidate.Source);
        }
    }
}
