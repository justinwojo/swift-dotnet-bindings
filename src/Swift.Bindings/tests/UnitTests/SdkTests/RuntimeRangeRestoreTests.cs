// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// End-to-end RESTORE-time proof that the bounded <c>[X.Y.Z, X.(Y+1).0)</c> Runtime range every
    /// generated binding emits (<see cref="RuntimeVersionRange.Build"/>) fractures a cross-minor
    /// binding+runtime diamond at <c>dotnet restore</c> — NuGet <c>NU1107</c> — <em>before</em> any
    /// code runs. This is the restore-layer half of the compatibility story: because two bindings
    /// built one Runtime-minor apart can never be installed into one project, the load-time
    /// <see cref="Swift.Runtime.RuntimeContract"/> handshake can safely RELAX to a supported window
    /// (it is the backstop for the NuGet-bypassing paths, not the primary gate). No unit test
    /// previously asserted this fracture end-to-end through real package restore.
    /// </summary>
    /// <remarks>
    /// Fully hermetic: every package is a tiny net10.0 stub packed into a per-test local feed, and
    /// every restore uses a <c>&lt;clear /&gt;</c> NuGet.config so nothing reaches nuget.org. The
    /// cross-minor case must fail with NU1107; the same-minor positive control must restore cleanly,
    /// which proves the failure is the range fracture itself and not a broken harness. (A plain
    /// min-only <c>Version="0.16.0"</c> would resolve both bindings to the highest available Runtime
    /// and restore successfully — the bounded range is exactly what turns that into NU1107.)
    /// </remarks>
    public class RuntimeRangeRestoreTests : IDisposable
    {
        private readonly string _tempDir;

        private static readonly Lazy<bool> RestoreAvailable = new(() =>
        {
            try
            {
                var (exitCode, _, _) = RunProcess("dotnet", "restore --help");
                return exitCode == 0;
            }
            catch { return false; }
        });

        public RuntimeRangeRestoreTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"swift-range-restore-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        [Fact]
        public void CrossMinorBindingDiamond_FailsRestoreWithNU1107()
        {
            SkipUnless(RestoreAvailable.Value, "dotnet restore not available");
            var feedDir = Path.Combine(_tempDir, "feed");

            // Two runtimes one minor apart, plus a binding pinned to each minor via the REAL bounded
            // range the generator emits: [0.16.0,0.17.0) and [0.17.0,0.18.0). Their intersection is
            // empty, so no single Runtime version satisfies both bindings.
            PackEmptyLibrary("SwiftBindings.Runtime", "0.16.0", feedDir, out var rtLoOk, out var rtLoLog);
            SkipUnless(rtLoOk, $"could not pack runtime stub 0.16.0 (offline?):\n{rtLoLog}");
            PackEmptyLibrary("SwiftBindings.Runtime", "0.17.0", feedDir, out var rtHiOk, out var rtHiLog);
            SkipUnless(rtHiOk, $"could not pack runtime stub 0.17.0 (offline?):\n{rtHiLog}");

            PackBindingStub("Fixture.BindingLo", "1.0.0", RuntimeVersionRange.Build("0.16.0"), feedDir,
                out var loOk, out var loLog);
            SkipUnless(loOk, $"could not pack binding stub Lo (offline?):\n{loLog}");
            PackBindingStub("Fixture.BindingHi", "1.0.0", RuntimeVersionRange.Build("0.17.0"), feedDir,
                out var hiOk, out var hiLog);
            SkipUnless(hiOk, $"could not pack binding stub Hi (offline?):\n{hiLog}");

            var (exitCode, output) = RestoreConsumer(feedDir,
                ("Fixture.BindingLo", "1.0.0"), ("Fixture.BindingHi", "1.0.0"));

            Assert.True(exitCode != 0,
                $"Cross-minor diamond restored successfully — the bounded range did not fracture it.\n{output}");
            Assert.Contains("NU1107", output);
        }

        [Fact]
        public void SameMinorBindingDiamond_RestoresSuccessfully()
        {
            // Positive control: two bindings carrying the SAME-minor bounded range (different patch
            // floors, both inside [*,0.17.0)) both resolve to the one available runtime, so restore
            // succeeds. Without this, the cross-minor test above could pass for the wrong reason (a
            // harness that fails to restore anything). The runtime sits at 0.16.5 so it satisfies
            // both the 0.16.0-floor and the 0.16.5-floor binding.
            SkipUnless(RestoreAvailable.Value, "dotnet restore not available");
            var feedDir = Path.Combine(_tempDir, "feed");

            PackEmptyLibrary("SwiftBindings.Runtime", "0.16.5", feedDir, out var rtOk, out var rtLog);
            SkipUnless(rtOk, $"could not pack runtime stub 0.16.5 (offline?):\n{rtLog}");

            PackBindingStub("Fixture.BindingA", "1.0.0", RuntimeVersionRange.Build("0.16.0"), feedDir,
                out var aOk, out var aLog);
            SkipUnless(aOk, $"could not pack binding stub A (offline?):\n{aLog}");
            PackBindingStub("Fixture.BindingB", "1.0.0", RuntimeVersionRange.Build("0.16.5"), feedDir,
                out var bOk, out var bLog);
            SkipUnless(bOk, $"could not pack binding stub B (offline?):\n{bLog}");

            var (exitCode, output) = RestoreConsumer(feedDir,
                ("Fixture.BindingA", "1.0.0"), ("Fixture.BindingB", "1.0.0"));

            Assert.True(exitCode == 0,
                $"Same-minor diamond failed to restore — the harness, not the range, is at fault.\n{output}");
            Assert.DoesNotContain("NU1107", output);
        }

        // --- Hermetic stub-package + restore helpers -------------------------------------------

        /// <summary>Packs an empty net10.0 library (used for the SwiftBindings.Runtime stubs) into the feed.</summary>
        private void PackEmptyLibrary(string packageId, string version, string feedDir, out bool ok, out string log)
        {
            Directory.CreateDirectory(feedDir);
            var dir = Path.Combine(_tempDir, $"rt-{packageId}-{version}");
            Directory.CreateDirectory(dir);
            WriteInsulation(dir);
            File.WriteAllText(Path.Combine(dir, "Stub.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <PackageId>{packageId}</PackageId>
                    <Version>{version}</Version>
                    <Authors>test</Authors>
                  </PropertyGroup>
                </Project>
                """);
            var r = RunProcess("dotnet",
                $"pack \"{Path.Combine(dir, "Stub.csproj")}\" -c Release -o \"{feedDir}\" --nologo -v:q");
            log = r.StdOut + "\n" + r.StdErr;
            ok = r.ExitCode == 0 && File.Exists(Path.Combine(feedDir, $"{packageId}.{version}.nupkg"));
        }

        /// <summary>
        /// Packs a stub "binding" package that carries a bounded SwiftBindings.Runtime
        /// <c>PackageReference</c> — exactly the dependency shape a generated binding emits — so the
        /// packed nuspec records <paramref name="runtimeRange"/> as its Runtime dependency range.
        /// Restores against the hermetic feed at pack time (the Runtime stub must already be there).
        /// </summary>
        private void PackBindingStub(
            string packageId, string version, string runtimeRange, string feedDir, out bool ok, out string log)
        {
            var dir = Path.Combine(_tempDir, $"bind-{packageId}-{version}");
            Directory.CreateDirectory(dir);
            WriteInsulation(dir);
            WriteHermeticNuGetConfig(dir, feedDir);
            File.WriteAllText(Path.Combine(dir, $"{packageId}.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <PackageId>{packageId}</PackageId>
                    <Version>{version}</Version>
                    <Authors>test</Authors>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="SwiftBindings.Runtime" Version="{runtimeRange}" />
                  </ItemGroup>
                </Project>
                """);
            var r = RunProcess("dotnet",
                $"pack \"{Path.Combine(dir, $"{packageId}.csproj")}\" -c Release -o \"{feedDir}\" --nologo -v:q");
            log = r.StdOut + "\n" + r.StdErr;
            ok = r.ExitCode == 0 && File.Exists(Path.Combine(feedDir, $"{packageId}.{version}.nupkg"));
        }

        /// <summary>
        /// Writes a consumer net10.0 project referencing each binding package and runs a plain
        /// <c>dotnet restore</c> against the hermetic feed. Returns the restore exit code and the
        /// combined stdout/stderr (which carries any NU1107 the version-conflict produces).
        /// </summary>
        private (int ExitCode, string Output) RestoreConsumer(
            string feedDir, params (string Id, string Version)[] bindingRefs)
        {
            var dir = Path.Combine(_tempDir, $"consumer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            WriteInsulation(dir);
            WriteHermeticNuGetConfig(dir, feedDir);
            var refs = string.Join("\n    ",
                Array.ConvertAll(bindingRefs, b => $"<PackageReference Include=\"{b.Id}\" Version=\"{b.Version}\" />"));
            File.WriteAllText(Path.Combine(dir, "Consumer.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    {refs}
                  </ItemGroup>
                </Project>
                """);
            var r = RunProcess("dotnet",
                $"restore \"{Path.Combine(dir, "Consumer.csproj")}\" --nologo -v:q");
            return (r.ExitCode, r.StdOut + "\n" + r.StdErr);
        }

        // Insulate every stub from any ambient Directory.Build.* up the source tree.
        private static void WriteInsulation(string dir)
        {
            File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(dir, "Directory.Build.targets"), "<Project />");
        }

        // Restore sees ONLY the per-test local feed — no nuget.org, no machine sources.
        private static void WriteHermeticNuGetConfig(string dir, string feedDir)
        {
            File.WriteAllText(Path.Combine(dir, "NuGet.config"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="local" value="{feedDir}" />
                  </packageSources>
                </configuration>
                """);
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
            process.WaitForExit(120_000);
            return (process.ExitCode, stdOut, stdErr);
        }
    }
}
