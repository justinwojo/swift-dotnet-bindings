// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Integration tests for <c>_DetectAppleFrameworkCrossModuleDeps</c> in Sdk.targets.
    /// Verifies the MSBuild target shells out to the generator's
    /// <c>--detect-apple-cross-module-deps</c> mode, parses pipe-delimited stdout,
    /// and injects bounded <c>&lt;PackageReference&gt;</c> items — all observable via
    /// MSBuild Message logging.
    ///
    /// Why these tests exist: the unit-level
    /// <see cref="AppleFrameworkImportDetectorResolveDependenciesTests"/> proves the
    /// detection logic itself is correct. These tests prove the SDK wires it up:
    /// the target gates on apple-framework mode, dedups against user-declared items,
    /// and doesn't fire in xcframework mode.
    /// </summary>
    public class AppleFrameworkCrossModuleDepsInjectionTests : IDisposable
    {
        private readonly string _tempDir;

        private static readonly Lazy<bool> MsbuildAvailable = new(() =>
        {
            try
            {
                var (exitCode, _, _) = RunProcess("dotnet", "msbuild --version");
                return exitCode == 0;
            }
            catch { return false; }
        });

        public AppleFrameworkCrossModuleDepsInjectionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"swift-cross-dep-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        [Fact]
        public void DetectsRealityFoundationDep_FromRealityKitShapedSwiftinterface()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            // Plant a swiftinterface that mirrors stock RealityKit's shape — the dep
            // edge to RealityFoundation is the flagship case this target was built for.
            var swiftInterfacePath = PlantSwiftInterface("RealityKit", """
                // swift-interface-format-version: 1.0
                import ARKit
                import Foundation
                @_exported import RealityFoundation
                import Swift
                import simd
                """);

            RunDetectTarget(swiftInterfacePath, out var output, out var exitCode);

            Assert.True(exitCode == 0, $"Target failed.\nOutput: {output}");
            // The auto-injected entry uses the bounded train range [X.Y.Z, X.(Y+1).0)
            // computed by the generator from --apple-version 26.2.1.
            Assert.Contains("AUTODEP:SwiftBindings.Apple.RealityFoundation|[26.2.1,26.3.0)", output);
            // The normal auto-inject path (no user-authored sibling reference) must not trip
            // the cross-train warning — that fires only for an authored ProjectReference.
            Assert.DoesNotContain("SWIFTBIND044", output);
        }

        [Fact]
        public void NoOpInXcframeworkMode()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            // In xcframework mode the target's task-level Conditions all evaluate false
            // (_SwiftBindingTargetKind is not "AppleFramework"). Even with a populated
            // swiftinterface that WOULD produce deps, no PackageReference may be injected.
            var swiftInterfacePath = PlantSwiftInterface("RealityKit", """
                @_exported import RealityFoundation
                """);

            RunDetectTarget(swiftInterfacePath, out var output, out var exitCode,
                targetKind: "XCFramework");

            Assert.True(exitCode == 0, $"Target failed.\nOutput: {output}");
            Assert.DoesNotContain("AUTODEP:SwiftBindings.Apple.RealityFoundation", output);
        }

        [Fact]
        public void OptOut_NoInjection()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            // Consumers with hand-rolled cross-Apple-framework refs (or who hit a bug
            // and need to disable auto-injection) opt out via
            // <SwiftAutoDetectAppleFrameworkDependencies>false</...>.
            var swiftInterfacePath = PlantSwiftInterface("RealityKit", """
                @_exported import RealityFoundation
                """);

            RunDetectTarget(swiftInterfacePath, out var output, out var exitCode,
                autoDetect: "false");

            Assert.True(exitCode == 0, $"Target failed.\nOutput: {output}");
            Assert.DoesNotContain("AUTODEP:SwiftBindings.Apple.RealityFoundation", output);
        }

        [Fact]
        public void DedupsAgainstUserDeclaredPackageReference()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            // If the user has already declared the same package, the target must NOT
            // inject a duplicate (NU1504). Their pinned version wins.
            var swiftInterfacePath = PlantSwiftInterface("RealityKit", """
                @_exported import RealityFoundation
                """);

            RunDetectTarget(swiftInterfacePath, out var output, out var exitCode,
                preDeclaredPackages: new[]
                {
                    ("SwiftBindings.Apple.RealityFoundation", "[99.0.0,99.1.0)"),
                });

            Assert.True(exitCode == 0, $"Target failed.\nOutput: {output}");
            // The user's pinned version survives — the auto-detected one does NOT
            // get added (would be a duplicate identity → NU1504 at restore time).
            Assert.Contains("AUTODEP:SwiftBindings.Apple.RealityFoundation|[99.0.0,99.1.0)", output);
            Assert.DoesNotContain("AUTODEP:SwiftBindings.Apple.RealityFoundation|[26.2.1,26.3.0)", output);
            // Exactly one item — defensive: a wildcard-prefix bug would inject a
            // second item with a similar identity and we'd see the dup count rise.
            Assert.Equal(1, CountOccurrences(output, "AUTODEP:SwiftBindings.Apple.RealityFoundation|"));
            // A user PackageReference carries its own range, so the cross-train warning
            // (which is specific to the rangeless ProjectReference case) must NOT fire.
            Assert.DoesNotContain("SWIFTBIND044", output);
        }

        [Fact]
        public void AuthoredProjectReferenceSibling_SuppressesInjection_AndWarnsSWIFTBIND044()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            // A user who wired the detected sibling as an authored <ProjectReference> (rather than
            // a PackageReference) must NOT also get the auto-injected PackageReference — that would
            // double-declare. But a ProjectReference cannot carry the bounded train range; NuGet
            // packs it as an unbounded minimum, so a shipped Apple-framework package would let
            // consumers float across Apple SDK trains. The SDK suppresses the injection AND warns
            // (SWIFTBIND044) so the maintainer switches to a bounded PackageReference.
            var swiftInterfacePath = PlantSwiftInterface("RealityKit", """
                @_exported import RealityFoundation
                """);

            RunDetectTarget(swiftInterfacePath, out var output, out var exitCode,
                preDeclaredProjectReferences: new[] { "SwiftBindings.Apple.RealityFoundation.csproj" });

            Assert.True(exitCode == 0, $"Target failed.\nOutput: {output}");
            // Dedup: the bounded auto PackageReference is suppressed (the authored PR wins,
            // so injecting would create a duplicate identity).
            Assert.DoesNotContain("AUTODEP:SwiftBindings.Apple.RealityFoundation|[26.2.1,26.3.0)", output);
            // But the cross-train-safety warning fires, naming the package and the range the
            // user would get from a bounded PackageReference.
            Assert.Contains("SWIFTBIND044", output);
            Assert.Contains("[26.2.1,26.3.0)", output);
        }

        [Fact]
        public void AuthoredProjectReferenceSibling_AlsoPinnedAsPackageReference_NoWarning()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            // When the sibling is wired BOTH ways — an authored ProjectReference AND a
            // PackageReference — the PackageReference already carries the bounded range, so the
            // ProjectReference's rangeless-pack problem is moot. SWIFTBIND044 must skip this case
            // (its condition requires the id be a ProjectReference but NOT also a PackageReference).
            var swiftInterfacePath = PlantSwiftInterface("RealityKit", """
                @_exported import RealityFoundation
                """);

            RunDetectTarget(swiftInterfacePath, out var output, out var exitCode,
                preDeclaredPackages: new[]
                {
                    ("SwiftBindings.Apple.RealityFoundation", "[26.2.1,26.3.0)"),
                },
                preDeclaredProjectReferences: new[] { "SwiftBindings.Apple.RealityFoundation.csproj" });

            Assert.True(exitCode == 0, $"Target failed.\nOutput: {output}");
            Assert.DoesNotContain("SWIFTBIND044", output);
        }

        [Fact]
        public void DepsVisibleAt_CollectPackageReferences()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            // The point of running BeforeTargets="CollectPackageReferences" is to make
            // injected items visible to NuGet's restore + pack pipeline. This test asserts
            // that contract: by the time CollectPackageReferences fires, the RealityFoundation
            // dep edge is in the PackageReference item collection.
            var swiftInterfacePath = PlantSwiftInterface("RealityKit", """
                @_exported import RealityFoundation
                """);

            RunDetectTarget(swiftInterfacePath, out var output, out var exitCode,
                downstreamTargetName: "CollectPackageReferences");

            Assert.True(exitCode == 0, $"Target failed.\nOutput: {output}");
            Assert.Contains("AUTODEP:SwiftBindings.Apple.RealityFoundation|[26.2.1,26.3.0)", output);
        }

        [Fact]
        public void NoDepsDetected_NoInjection()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            // Swiftinterface with only marker imports (Swift, _Concurrency, simd) and
            // unregistered Apple SDK modules (Foundation, UIKit, ARKit) — none have a
            // packageId, so the detector emits nothing and the target injects nothing.
            var swiftInterfacePath = PlantSwiftInterface("Foo", """
                import Swift
                import _Concurrency
                import simd
                import Foundation
                import UIKit
                import ARKit
                """);

            RunDetectTarget(swiftInterfacePath, out var output, out var exitCode);

            Assert.True(exitCode == 0, $"Target failed.\nOutput: {output}");
            Assert.DoesNotContain("AUTODEP:SwiftBindings.Apple.", output);
        }

        [Fact]
        public void InjectedCrossModuleDep_MaterializesAsNuspecDependency()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet pack not available");

            // The load-bearing Tier-1 closure: the auto-injected PackageReference must survive
            // all the way through `dotnet pack` into the packed .nuspec <dependencies> group.
            // DepsVisibleAt_CollectPackageReferences proves the item is present in the collection
            // NuGet packs from; THIS proves NuGet's per-TFM pack subgraph actually writes it as a
            // <dependency>. The Apple injection is not a static <PackageReference> in props (like
            // the supplement's Runtime dep) — it's an Exec + ItemGroup transform under AppleFramework
            // kind + per-TFM evaluation, so a regression could leave the item visible at Collect yet
            // absent (or wrong-ranged) in the nuspec the consumer restores from → a shipped package
            // whose dylib links an absent sibling (DllNotFound), observable only by unzipping the
            // nuspec. That is exactly why the supplement has the analogous AssertSupplementBoundsRuntimeRange
            // gate; this is its parity for the auto cross-Apple case.

            // A local feed carrying a stub of the sibling package so restore resolves the injected
            // dep (in production it points at the real shipped SwiftBindings.Apple.* sibling).
            var feedDir = Path.Combine(_tempDir, "feed");
            BuildStubSiblingPackage("SwiftBindings.Apple.RealityFoundation", "26.2.1", feedDir,
                out var stubOk, out var stubLog);
            SkipUnless(stubOk, $"could not build stub sibling package (offline?):\n{stubLog}");

            var swiftInterfacePath = PlantSwiftInterface("RealityKit", """
                // swift-interface-format-version: 1.0
                import ARKit
                import Foundation
                @_exported import RealityFoundation
                import Swift
                import simd
                """);

            var nuspec = PackAppleFrameworkFixtureAndReadNuspec(
                swiftInterfacePath, feedDir, out var output, out var exitCode);

            Assert.True(exitCode == 0, $"dotnet pack failed.\nOutput: {output}");
            Assert.True(nuspec != null, $"no .nuspec found in the packed nupkg.\nOutput: {output}");

            // The detected RealityFoundation edge must appear as a <dependency> carrying the bounded
            // train range the generator computed from --apple-version 26.2.1.
            var dep = nuspec!.Descendants()
                .Where(e => e.Name.LocalName == "dependency")
                .FirstOrDefault(e => (string?)e.Attribute("id") == "SwiftBindings.Apple.RealityFoundation");
            Assert.True(dep != null,
                $"packed nuspec has no <dependency id=\"SwiftBindings.Apple.RealityFoundation\">. Nuspec:\n{nuspec}");
            // Normalize the range's display spacing ("[26.2.1, 26.3.0)" → "[26.2.1,26.3.0)").
            var version = ((string?)dep!.Attribute("version") ?? "").Replace(" ", "");
            Assert.Equal("[26.2.1,26.3.0)", version);
        }

        [Fact]
        public void InTreeSiblingPresent_StillInjectsBoundedPackageReferenceDependency()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet pack not available");

            // Regression guard for the in-tree (swift-dotnet-packages mono-repo) layout, where
            // the conventional sibling csproj `../<Module>/<PackageId>.csproj` EXISTS on disk.
            // The detector must STILL inject a bounded PackageReference (the same mechanism the
            // out-of-tree case uses), NOT divert to a ProjectReference. A build-time
            // ProjectReference resolves types but never enters the restore graph NuGet builds
            // the nuspec <dependencies> group from, so it yields an EMPTY dependency group —
            // and the mono-repo IS the path that produces every shipped package, so a diverted
            // ProjectReference would ship every in-tree Apple binding linking a sibling dylib
            // its nuspec never names → DllNotFound for the consumer. The mono-repo pre-packs
            // siblings into its local feed before building dependents, so the PackageReference
            // restores in-tree; here the feed carries the stub sibling to mirror that.

            var feedDir = Path.Combine(_tempDir, "feed");
            BuildStubSiblingPackage("SwiftBindings.Apple.RealityFoundation", "26.2.1", feedDir,
                out var stubOk, out var stubLog);
            SkipUnless(stubOk, $"could not build stub sibling package (offline?):\n{stubLog}");

            var swiftInterfacePath = PlantSwiftInterface("RealityKit", """
                // swift-interface-format-version: 1.0
                import ARKit
                import Foundation
                @_exported import RealityFoundation
                import Swift
                import simd
                """);

            // inTreeSibling plants the sibling csproj on disk at the conventional path. With the
            // old code this flipped injection to a ProjectReference (→ empty nuspec); with the
            // fix it is irrelevant — a bounded PackageReference is injected regardless.
            var nuspec = PackAppleFrameworkFixtureAndReadNuspec(
                swiftInterfacePath, feedDir, out var output, out var exitCode,
                inTreeSibling: ("RealityFoundation", "SwiftBindings.Apple.RealityFoundation", "26.2.1"));

            Assert.True(exitCode == 0, $"dotnet pack failed.\nOutput: {output}");
            Assert.True(nuspec != null, $"no .nuspec found in the packed nupkg.\nOutput: {output}");

            // Even with the sibling csproj present in-tree, the dep must materialize as a
            // <dependency> carrying the bounded train range — never an empty group.
            var dep = nuspec!.Descendants()
                .Where(e => e.Name.LocalName == "dependency")
                .FirstOrDefault(e => (string?)e.Attribute("id") == "SwiftBindings.Apple.RealityFoundation");
            Assert.True(dep != null,
                $"packed nuspec has no <dependency id=\"SwiftBindings.Apple.RealityFoundation\"> despite the in-tree sibling csproj — the injection diverted to a non-propagating ProjectReference. Nuspec:\n{nuspec}");
            var version = ((string?)dep!.Attribute("version") ?? "").Replace(" ", "");
            Assert.Equal("[26.2.1,26.3.0)", version);
        }

        [Fact]
        public void RestoreOnly_InjectedCrossModuleDep_EntersRestoreGraph()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet restore not available");

            // Restore-visibility gate. The injection runs BeforeTargets="...;CollectPackageReferences",
            // which is part of the NuGet RESTORE target chain — so the bounded PackageReference must land
            // in project.assets.json from a plain `dotnet restore`, with no build or pack. This is the
            // load-bearing precondition for InjectedCrossModuleDep_MaterializesAsNuspecDependency: NuGet
            // builds the packed nuspec <dependencies> group from the restore graph (assets file), so if
            // restore alone didn't see the injected dep, a restore-then-pack CI flow (`pack --no-restore`)
            // would ship a package whose dylib links a sibling its nuspec never names → DllNotFound. That
            // test proves it transitively through a full pack; THIS proves restore-phase visibility directly.

            var feedDir = Path.Combine(_tempDir, "feed");
            BuildStubSiblingPackage("SwiftBindings.Apple.RealityFoundation", "26.2.1", feedDir,
                out var stubOk, out var stubLog);
            SkipUnless(stubOk, $"could not build stub sibling package (offline?):\n{stubLog}");

            var swiftInterfacePath = PlantSwiftInterface("RealityKit", """
                // swift-interface-format-version: 1.0
                import ARKit
                import Foundation
                @_exported import RealityFoundation
                import Swift
                import simd
                """);

            var assets = RestoreAppleFrameworkFixtureAndReadAssets(
                swiftInterfacePath, feedDir, out var output, out var exitCode);

            Assert.True(exitCode == 0, $"dotnet restore failed.\nOutput: {output}");
            Assert.True(assets != null, $"no project.assets.json after restore.\nOutput: {output}");
            // The injected sibling resolved into the restore graph at the exact stub version the
            // hermetic feed carries — proving CollectPackageReferences saw the injected item at
            // restore time, not only during a later build/pack subgraph.
            Assert.Contains("SwiftBindings.Apple.RealityFoundation/26.2.1", assets!);
        }

        // ── Helpers ──

        /// <summary>
        /// Packs a tiny stub library under <paramref name="packageId"/>/<paramref name="version"/>
        /// into <paramref name="feedDir"/> so the auto-injected sibling PackageReference resolves at
        /// restore. Targets net10.0 (the same SDK-bundled targeting pack the fixture uses) so the
        /// stub build needs no network. Sets <paramref name="ok"/> false (test skips) if the build
        /// can't run — e.g. an offline sandbox — rather than failing on an environmental cause.
        /// </summary>
        private void BuildStubSiblingPackage(
            string packageId, string version, string feedDir, out bool ok, out string log)
        {
            Directory.CreateDirectory(feedDir);
            var stubDir = Path.Combine(_tempDir, "stub");
            Directory.CreateDirectory(stubDir);
            File.WriteAllText(Path.Combine(stubDir, "Stub.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <PackageId>{packageId}</PackageId>
                    <Version>{version}</Version>
                    <Authors>test</Authors>
                  </PropertyGroup>
                </Project>
                """);
            // Insulate the stub build from any ambient Directory.Build.* up the tree.
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            var r = RunProcess("dotnet",
                $"pack \"{Path.Combine(stubDir, "Stub.csproj")}\" -c Release -o \"{feedDir}\" --nologo -v:q");
            log = r.StdOut + "\n" + r.StdErr;
            ok = r.ExitCode == 0
                 && File.Exists(Path.Combine(feedDir, $"{packageId}.{version}.nupkg"));
        }

        /// <summary>
        /// Builds a hand-rolled Apple-framework binding project that imports the REAL SwiftBindings
        /// Sdk.targets (carrying <c>_DetectAppleFrameworkCrossModuleDeps</c>), plants the AppleFramework
        /// kind + a swiftinterface, and stubs ONLY the heavy codegen/compile targets — all orthogonal to
        /// the nuspec dependency group. Runs a real <c>dotnet pack</c> against a hermetic local feed and
        /// returns the packed nuspec as an <see cref="XDocument"/> (null on pack failure / missing nuspec).
        /// </summary>
        private XDocument? PackAppleFrameworkFixtureAndReadNuspec(
            string swiftInterfacePath, string feedDir, out string output, out int exitCode,
            (string Module, string PackageId, string Version)? inTreeSibling = null)
        {
            var (mainDir, packageId, packageVersion) =
                WriteAppleFrameworkFixtureProject(swiftInterfacePath, feedDir, inTreeSibling);

            var outDir = Path.Combine(mainDir, "out");
            var r = RunProcess("dotnet",
                $"pack \"{Path.Combine(mainDir, "Main.csproj")}\" -c Release -o \"{outDir}\" --nologo -v:q");
            output = r.StdOut + "\n" + r.StdErr;
            exitCode = r.ExitCode;
            if (exitCode != 0) return null;

            var nupkg = Path.Combine(outDir, $"{packageId}.{packageVersion}.nupkg");
            if (!File.Exists(nupkg)) return null;
            using var zip = ZipFile.OpenRead(nupkg);
            var nuspecEntry = zip.Entries.FirstOrDefault(
                e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (nuspecEntry == null) return null;
            using var s = nuspecEntry.Open();
            return XDocument.Load(s);
        }

        /// <summary>
        /// Restore-visibility sibling of <see cref="PackAppleFrameworkFixtureAndReadNuspec"/>. Writes the
        /// SAME fixture but runs a plain <c>dotnet restore</c> (no build, no pack) and returns the text of
        /// the resulting <c>obj/project.assets.json</c> (null if restore fails / the file is absent). The
        /// injected cross-module PackageReference must appear in the restore graph from restore ALONE —
        /// the nuspec deps group NuGet writes at pack time is built from exactly this file.
        /// </summary>
        private string? RestoreAppleFrameworkFixtureAndReadAssets(
            string swiftInterfacePath, string feedDir, out string output, out int exitCode)
        {
            var (mainDir, _, _) =
                WriteAppleFrameworkFixtureProject(swiftInterfacePath, feedDir);

            var r = RunProcess("dotnet",
                $"restore \"{Path.Combine(mainDir, "Main.csproj")}\" --nologo -v:q");
            output = r.StdOut + "\n" + r.StdErr;
            exitCode = r.ExitCode;
            if (exitCode != 0) return null;

            var assetsPath = Path.Combine(mainDir, "obj", "project.assets.json");
            return File.Exists(assetsPath) ? File.ReadAllText(assetsPath) : null;
        }

        /// <summary>
        /// Writes the hand-rolled Apple-framework binding project (Main.csproj + hermetic NuGet.config +
        /// inert Directory.Build.*) shared by the pack and restore helpers, and returns its directory and
        /// pack identity. Imports the REAL Sdk.targets so <c>_DetectAppleFrameworkCrossModuleDeps</c> runs
        /// for real while the heavy codegen/compile targets are stubbed out.
        /// </summary>
        private (string MainDir, string PackageId, string PackageVersion) WriteAppleFrameworkFixtureProject(
            string swiftInterfacePath, string feedDir,
            (string Module, string PackageId, string Version)? inTreeSibling = null)
        {
            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");
            var generatorDir = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings", "src", "bin", "Debug", "net10.0") + "/";

            var mainDir = Path.Combine(_tempDir, "main");
            Directory.CreateDirectory(mainDir);

            // When the test exercises the in-tree layout, plant the conventional sibling
            // csproj at ../<Module>/<PackageId>.csproj relative to mainDir — exactly the path
            // the old code probed to decide whether to divert to a ProjectReference. With the
            // fix it is an inert on-disk file (the injection is always a PackageReference), so
            // it is never referenced or built; its mere presence is what the regression guard
            // asserts no longer changes the outcome. A trivial packable net10.0 lib.
            if (inTreeSibling is { } sib)
            {
                var sibDir = Path.Combine(_tempDir, sib.Module);
                Directory.CreateDirectory(sibDir);
                File.WriteAllText(Path.Combine(sibDir, $"{sib.PackageId}.csproj"), $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net10.0</TargetFramework>
                        <PackageId>{sib.PackageId}</PackageId>
                        <Version>{sib.Version}</Version>
                        <IsPackable>true</IsPackable>
                        <Authors>test</Authors>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(Path.Combine(sibDir, "Directory.Build.props"), "<Project />");
                File.WriteAllText(Path.Combine(sibDir, "Directory.Build.targets"), "<Project />");
            }

            // Hermetic feed — local only, so restore never reaches the network. The net10.0 base
            // framework refs resolve from the SDK's bundled targeting packs.
            File.WriteAllText(Path.Combine(mainDir, "NuGet.config"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="local" value="{feedDir}" />
                  </packageSources>
                </configuration>
                """);

            const string packageId = "SwiftBindings.Apple.RealityKit.Probe";
            const string packageVersion = "0.0.1";
            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <PackageId>{packageId}</PackageId>
                    <Version>{packageVersion}</Version>
                    <IsPackable>true</IsPackable>
                    <SwiftAutoDetectAppleFrameworkDependencies>true</SwiftAutoDetectAppleFrameworkDependencies>
                    <SwiftAppleSupplementVersion>26.2.1</SwiftAppleSupplementVersion>
                  </PropertyGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingTargetKind>AppleFramework</_SwiftBindingTargetKind>
                    <_SwiftBindingGeneratorDir>{generatorDir}</_SwiftBindingGeneratorDir>
                    <_SwiftAppleFrameworkInterface>{swiftInterfacePath}</_SwiftAppleFrameworkInterface>
                  </PropertyGroup>
                  <!-- Stub the kind-detect + xcrun-driven apple-path resolve (like the other injection
                       tests) and every heavy codegen/compile target — all orthogonal to the nuspec
                       dependency group. The REAL _DetectAppleFrameworkCrossModuleDeps + real pack run. -->
                  <Target Name="_DetectSwiftBindingTargetKind" />
                  <Target Name="_ResolveAppleFrameworkPaths" />
                  <Target Name="_DiscoverSwiftFrameworks" />
                  <Target Name="_ValidateSwiftPackageItems" />
                  <!-- The two generate-hook stubs MUST keep the real targets'
                       BeforeTargets="ResolveProjectReferences" anchor and set their F62 wiring
                       stamps. A bare <Target Name="..." /> override would strip the anchor, so the
                       target would never auto-run — which is precisely the "silent disconnection"
                       the late _AssertSwiftBindingHookWiring tripwire exists to catch, and the pack
                       would (correctly) fail. This fixture is AppleFramework kind, so BOTH the
                       generic hook (SWIFTBIND062) and the AppleFramework hook (SWIFTBIND065, asserted
                       in AppleFramework mode) must stamp. The real targets stamp unconditionally and
                       run before CoreCompile; the faithful stubs mirror that contract while skipping
                       the heavy codegen Exec. -->
                  <Target Name="_GenerateSwiftBindingsAppleFramework" BeforeTargets="ResolveProjectReferences">
                    <PropertyGroup>
                      <_SwiftHookRan_GenerateSwiftBindingsAppleFramework>true</_SwiftHookRan_GenerateSwiftBindingsAppleFramework>
                    </PropertyGroup>
                  </Target>
                  <Target Name="_GenerateSwiftBindings" BeforeTargets="ResolveProjectReferences">
                    <PropertyGroup>
                      <_SwiftHookRan_GenerateSwiftBindings>true</_SwiftHookRan_GenerateSwiftBindings>
                    </PropertyGroup>
                  </Target>
                  <Target Name="_CompileSwiftWrapper" />
                  <Target Name="_CollectSwiftModuleDatabases" />
                </Project>
                """;
            File.WriteAllText(Path.Combine(mainDir, "Main.csproj"), project);
            File.WriteAllText(Path.Combine(mainDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(mainDir, "Directory.Build.targets"), "<Project />");

            return (mainDir, packageId, packageVersion);
        }

        /// <summary>
        /// Writes a swiftinterface file under
        /// <c>&lt;tempDir&gt;/Foo.framework/Modules/Foo.swiftmodule/arm64-apple-ios.swiftinterface</c>
        /// — the canonical Apple SDK layout the generator's
        /// <c>DeriveModuleNameFromSwiftInterfacePath</c> helper expects.
        /// </summary>
        private string PlantSwiftInterface(string moduleName, string content)
        {
            var moduleDir = Path.Combine(_tempDir, $"{moduleName}.framework", "Modules", $"{moduleName}.swiftmodule");
            Directory.CreateDirectory(moduleDir);
            var path = Path.Combine(moduleDir, "arm64-apple-ios.swiftinterface");
            File.WriteAllText(path, content);
            return path;
        }

        private void RunDetectTarget(
            string swiftInterfacePath,
            out string output,
            out int exitCode,
            string targetKind = "AppleFramework",
            string autoDetect = "true",
            (string PackageId, string Version)[]? preDeclaredPackages = null,
            string[]? preDeclaredProjectReferences = null,
            string? downstreamTargetName = null)
        {
            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");
            var generatorDir = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings", "src", "bin", "Debug", "net10.0") + "/";

            // Plant pre-declared PackageReferences (when the test exercises dedup) and/or
            // authored ProjectReferences (when the test exercises the ProjectReference dedup
            // and its paired SWIFTBIND044 cross-train warning). The ProjectReferences are
            // inert items here: TestDump depends only on the detect target, so MSBuild never
            // runs ResolveProjectReferences and never tries to build/resolve them — the
            // dedup/warning only reads each item's %(Filename), no disk access.
            var preDeclared = preDeclaredPackages is null
                ? ""
                : "<ItemGroup>" + string.Concat(preDeclaredPackages
                    .Select(p => $"<PackageReference Include=\"{p.PackageId}\" Version=\"{p.Version}\" />"))
                  + "</ItemGroup>";
            var preDeclaredProjects = preDeclaredProjectReferences is null
                ? ""
                : "<ItemGroup>" + string.Concat(preDeclaredProjectReferences
                    .Select(p => $"<ProjectReference Include=\"{p}\" />"))
                  + "</ItemGroup>";

            // The injection target runs BeforeTargets="...;CollectPackageReferences",
            // so by depending on a downstream target the test proves the injection
            // fires as part of the same MSBuild graph the real restore pipeline uses.
            // Default path: TestDump depends directly on the injection target.
            // Downstream path: declare a stub for the named downstream target so MSBuild
            // can resolve the BeforeTargets dependency, and have TestDump depend on it.
            var dispatchTarget = downstreamTargetName is null
                ? ""
                : $"<Target Name=\"{downstreamTargetName}\" />";
            var testDependsOn = downstreamTargetName ?? "_DetectAppleFrameworkCrossModuleDeps";

            // Build a synthetic project that imports Sdk.targets with most upstream
            // targets stubbed out as no-ops. We plant _SwiftBindingTargetKind directly
            // to skip the xcrun-driven _ResolveAppleFrameworkPaths flow (which would
            // require a real Apple SDK install matching the test host).
            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <SwiftAutoDetectAppleFrameworkDependencies>{autoDetect}</SwiftAutoDetectAppleFrameworkDependencies>
                    <SwiftAppleSupplementVersion>26.2.1</SwiftAppleSupplementVersion>
                  </PropertyGroup>
                  {preDeclared}
                  {preDeclaredProjects}
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingTargetKind>{targetKind}</_SwiftBindingTargetKind>
                    <_SwiftBindingGeneratorDir>{generatorDir}</_SwiftBindingGeneratorDir>
                    <_SwiftAppleFrameworkInterface>{swiftInterfacePath}</_SwiftAppleFrameworkInterface>
                  </PropertyGroup>
                  <Target Name="_DetectSwiftBindingTargetKind" />
                  <Target Name="_ResolveAppleFrameworkPaths" />
                  {dispatchTarget}
                  <Target Name="TestDump"
                          DependsOnTargets="{testDependsOn}">
                    <Message Importance="High"
                             Text="AUTODEP:%(PackageReference.Identity)|%(PackageReference.Version)"
                             Condition="$([System.String]::Copy('%(PackageReference.Identity)').StartsWith('SwiftBindings.Apple.'))" />
                  </Target>
                </Project>
                """;

            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            var result = RunProcess("dotnet",
                $"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:TestDump -nologo -v:n");
            output = result.StdOut + "\n" + result.StdErr;
            exitCode = result.ExitCode;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return 0;
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }

        private static string FindRepoRoot()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null)
            {
                var gitPath = Path.Combine(dir, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("Cannot find repo root.");
        }

        private static void SkipUnless(bool condition, string reason)
        {
            if (!condition)
                throw Xunit.Sdk.SkipException.ForSkip(reason);
        }

        private static (int ExitCode, string StdOut, string StdErr) RunProcess(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi)!;
            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
            return (process.ExitCode, stdOut, stdErr);
        }
    }
}
