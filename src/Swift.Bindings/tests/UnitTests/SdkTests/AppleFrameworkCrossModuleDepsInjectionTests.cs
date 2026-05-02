// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics;
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

        // ── Helpers ──

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
            string? downstreamTargetName = null)
        {
            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");
            var generatorDir = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings", "src", "bin", "Debug", "net10.0") + "/";

            // Plant pre-declared PackageReferences (when the test exercises dedup).
            var preDeclared = preDeclaredPackages is null
                ? ""
                : "<ItemGroup>" + string.Concat(preDeclaredPackages
                    .Select(p => $"<PackageReference Include=\"{p.PackageId}\" Version=\"{p.Version}\" />"))
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
