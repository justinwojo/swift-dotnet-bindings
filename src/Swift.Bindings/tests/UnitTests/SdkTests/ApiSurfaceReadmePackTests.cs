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
    /// Integration tests for the SDK's package-readme wiring: the generated
    /// <c>{namespace}.api-surface.md</c> — the member table derived from what the generator actually
    /// emitted — is staged and packed as the binding package's <c>PackageReadmeFile</c>, so a
    /// consumer landing on nuget.org sees the real shipped surface instead of nothing.
    ///
    /// <para>Why this layer: the wiring is two MSBuild targets straddling the inner/outer pack
    /// boundary (the doc is produced per-TFM, but <c>PackageReadmeFile</c> is only read by the outer
    /// build's <c>GenerateNuspec</c>). Nothing below a real <c>dotnet pack</c> can observe that
    /// hand-off — an XML-shape assertion over Sdk.targets would pass just as happily on a version
    /// that sets the property in the wrong build and silently packs no readme. So each test packs a
    /// fixture and reads the resulting nupkg.</para>
    ///
    /// <para>The negative cases matter as much as the positive one: naming a
    /// <c>PackageReadmeFile</c> that is not packed is a hard NU5039 pack failure, so a binding with
    /// no surface doc, or one whose author brought their own readme, must pack cleanly.</para>
    /// </summary>
    public class ApiSurfaceReadmePackTests : IDisposable
    {
        private readonly string _tempDir;

        private static readonly Lazy<bool> PackAvailable = new(() =>
        {
            try
            {
                var (exitCode, _, _) = RunProcess("dotnet", "msbuild --version");
                return exitCode == 0;
            }
            catch { return false; }
        });

        public ApiSurfaceReadmePackTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"swift-readme-pack-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        [Fact]
        public void GeneratedApiSurfaceDoc_IsPackedAsThePackageReadme()
        {
            SkipUnless(PackAvailable.Value, "dotnet pack not available");

            // Two docs, because a binding project can bind several Swift modules and the generator
            // writes one doc per module. The readme has to describe the whole package, so they are
            // concatenated rather than one being picked arbitrarily.
            var (exitCode, output, entries, nuspec) = PackFixture(SurfaceDocs.Present);

            Assert.True(exitCode == 0, $"dotnet pack failed.\nOutput: {output}");

            // The nuspec must NAME the readme and the package must CONTAIN it at the root. Either
            // half alone is a broken package: a named-but-absent readme is NU5039, and a packed but
            // unnamed file is just an inert extra entry nuget.org never renders.
            var readme = nuspec!.Descendants().FirstOrDefault(e => e.Name.LocalName == "readme");
            Assert.True(readme != null, $"packed nuspec declares no <readme>. Nuspec:\n{nuspec}");
            Assert.Equal("README.md", readme!.Value);
            Assert.Contains("README.md", entries);

            var packedReadme = ReadPackedEntry("README.md");
            Assert.Contains("Present(nint)", packedReadme);
            Assert.Contains("Configure(string)", packedReadme);
        }

        [Fact]
        public void NoApiSurfaceDoc_PacksCleanlyWithNoReadme()
        {
            SkipUnless(PackAvailable.Value, "dotnet pack not available");

            // A binding whose generator emitted no surface doc (nothing bound, or an older
            // generator) must still pack. Setting PackageReadmeFile unconditionally would turn that
            // case into a hard NU5039 failure — the readme is a nicety, never a pack precondition.
            var (exitCode, output, entries, nuspec) = PackFixture(SurfaceDocs.None);

            Assert.True(exitCode == 0, $"dotnet pack failed.\nOutput: {output}");
            Assert.DoesNotContain("NU5039", output);
            Assert.DoesNotContain(nuspec!.Descendants(), e => e.Name.LocalName == "readme");
            Assert.DoesNotContain("README.md", entries);
        }

        [Fact]
        public void ProjectWithItsOwnReadme_KeepsIt()
        {
            SkipUnless(PackAvailable.Value, "dotnet pack not available");

            // An author who wrote a README.md meant that one to ship. Taking it over would be
            // surprising, and packing both to the package root collides (NU5118) — so the generated
            // doc stands down whenever the project brought its own.
            var (exitCode, output, _, nuspec) = PackFixture(
                SurfaceDocs.Present,
                projectReadme: "# Hand-written\n\nThis is the author's own readme.\n");

            Assert.True(exitCode == 0, $"dotnet pack failed.\nOutput: {output}");

            var readme = nuspec!.Descendants().FirstOrDefault(e => e.Name.LocalName == "readme");
            Assert.True(readme != null, $"packed nuspec declares no <readme>. Nuspec:\n{nuspec}");
            var packedReadme = ReadPackedEntry("README.md");
            Assert.Contains("Hand-written", packedReadme);
            Assert.DoesNotContain("Present(nint)", packedReadme);
        }

        [Fact]
        public void OptOutProperty_PacksWithoutTheGeneratedReadme()
        {
            SkipUnless(PackAvailable.Value, "dotnet pack not available");

            // The escape hatch for a package that deliberately ships no readme (or arranges its own
            // by some other route) — it must switch BOTH halves off, staging and naming alike.
            var (exitCode, output, entries, nuspec) = PackFixture(
                SurfaceDocs.Present, extraProperties: "<SwiftBindingPackApiSurfaceReadme>false</SwiftBindingPackApiSurfaceReadme>");

            Assert.True(exitCode == 0, $"dotnet pack failed.\nOutput: {output}");
            Assert.DoesNotContain(nuspec!.Descendants(), e => e.Name.LocalName == "readme");
            Assert.DoesNotContain("README.md", entries);
        }

        [Fact]
        public void RepackAfterTheSurfaceDisappears_ShipsNoStaleReadme()
        {
            SkipUnless(PackAvailable.Value, "dotnet pack not available");

            // obj/ survives between packs, so the staged readme is the one piece of state that can
            // outlive the surface it describes: bind a module, pack, then stop binding it, and a
            // stage step that only ever WRITES would leave the previous run's file for the pack to
            // pick up — shipping a readme documenting an API the package no longer contains.
            var first = PackFixture(SurfaceDocs.Present);
            Assert.True(first.ExitCode == 0, $"first pack failed.\nOutput: {first.Output}");
            Assert.Contains("README.md", first.Entries);

            var second = PackFixture(SurfaceDocs.Removed);
            Assert.True(second.ExitCode == 0, $"second pack failed.\nOutput: {second.Output}");
            Assert.DoesNotContain("NU5039", second.Output);
            Assert.DoesNotContain(second.Nuspec!.Descendants(), e => e.Name.LocalName == "readme");
            Assert.DoesNotContain("README.md", second.Entries);
        }

        [Fact]
        public void CrossTargetingBuild_CombinesTheDeclaredFrameworksFragmentsInOrder()
        {
            SkipUnless(PackAvailable.Value, "dotnet msbuild not available");

            // A cross-targeting pack runs one inner build per TFM, each with its own generated
            // surface, and produces ONE package with ONE readme. Each inner build therefore stages
            // its own fragment (they run concurrently — a single shared file would be a race with a
            // last-writer-wins outcome) and the outer build combines them.
            //
            // This exercises the outer half directly rather than through a real cross-targeting
            // pack: a second plain TFM would need a targeting pack the offline restore may not have,
            // and an Apple TFM would drag Xcode into a unit test. The inner half — a build staging
            // its own TFM's fragment — is what every pack test above already runs.
            var mainDir = WriteFixtureProject(SurfaceDocs.None, projectReadme: null, extraProperties: "",
                tfmElement: "<TargetFrameworks>net10.0-ios;net10.0</TargetFrameworks>");

            var stageDir = Path.Combine(mainDir, "obj", "swift-binding-readme");
            Directory.CreateDirectory(stageDir);
            File.WriteAllText(Path.Combine(stageDir, "net10.0-ios.api-surface.md"), "IOS-SURFACE\n");
            File.WriteAllText(Path.Combine(stageDir, "net10.0.api-surface.md"), "PLAIN-SURFACE\n");
            // A TFM the project no longer declares, and a combined file from that older pack.
            File.WriteAllText(Path.Combine(stageDir, "net9.0.api-surface.md"), "DROPPED-SURFACE\n");
            File.WriteAllText(Path.Combine(stageDir, "README.md"), "STALE-COMBINED\n");

            var r = RunProcess("dotnet",
                $"msbuild \"{Path.Combine(mainDir, "Main.csproj")}\" -t:_SetSwiftBindingPackageReadme -nologo -v:n");
            Assert.True(r.ExitCode == 0, $"combine target failed.\nOutput: {r.StdOut}{r.StdErr}");

            var combined = File.ReadAllText(Path.Combine(stageDir, "README.md"));
            Assert.Contains("IOS-SURFACE", combined);
            Assert.Contains("PLAIN-SURFACE", combined);
            // Declared order, not the alphabetical order a directory glob would have produced.
            Assert.True(combined.IndexOf("IOS-SURFACE", StringComparison.Ordinal)
                        < combined.IndexOf("PLAIN-SURFACE", StringComparison.Ordinal),
                $"fragments were not combined in declared TFM order:\n{combined}");
            Assert.DoesNotContain("DROPPED-SURFACE", combined);
            Assert.DoesNotContain("STALE-COMBINED", combined);
        }

        // ── harness ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What the fixture's stand-in generator does to the binding intermediate directory:
        /// writes the surface docs, leaves it alone, or clears docs a previous pack left there.
        /// </summary>
        private enum SurfaceDocs { None, Present, Removed }

        private const string PackageId = "SwiftBindings.ReadmeProbe";
        private const string PackageVersion = "0.0.1";

        /// <summary>
        /// Packs the fixture binding project and returns the pack result together with the nupkg's
        /// entry list and parsed nuspec.
        /// </summary>
        private (int ExitCode, string Output, string[] Entries, XDocument? Nuspec) PackFixture(
            SurfaceDocs docs, string? projectReadme = null, string extraProperties = "")
        {
            var mainDir = WriteFixtureProject(docs, projectReadme, extraProperties);
            var outDir = Path.Combine(mainDir, "out");

            var r = RunProcess("dotnet",
                $"pack \"{Path.Combine(mainDir, "Main.csproj")}\" -c Release -o \"{outDir}\" --nologo -v:n");
            var output = r.StdOut + r.StdErr;

            var nupkg = Path.Combine(outDir, $"{PackageId}.{PackageVersion}.nupkg");
            if (!File.Exists(nupkg))
                return (r.ExitCode, output, Array.Empty<string>(), null);

            using var zip = ZipFile.OpenRead(nupkg);
            var entries = zip.Entries.Select(e => e.FullName).ToArray();
            var nuspecEntry = zip.Entries.FirstOrDefault(
                e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            XDocument? nuspec = null;
            if (nuspecEntry != null)
            {
                using var stream = nuspecEntry.Open();
                nuspec = XDocument.Load(stream);
            }
            return (r.ExitCode, output, entries, nuspec);
        }

        private string ReadPackedEntry(string entryName)
        {
            var nupkg = Path.Combine(_tempDir, "main", "out", $"{PackageId}.{PackageVersion}.nupkg");
            using var zip = ZipFile.OpenRead(nupkg);
            var entry = zip.Entries.First(e => e.FullName == entryName);
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// Writes a minimal packable binding project that imports the REAL Sdk.props and Sdk.targets,
        /// so the readme targets are reached through the SDK's own pack registration rather than by
        /// being invoked directly.
        /// </summary>
        private string WriteFixtureProject(SurfaceDocs docs, string? projectReadme, string extraProperties,
            string tfmElement = "<TargetFramework>net10.0</TargetFramework>")
        {
            var repoRoot = FindRepoRoot();
            var sdkPropsPath = Path.Combine(repoRoot, "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.props");
            var sdkTargetsPath = Path.Combine(repoRoot, "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            var mainDir = Path.Combine(_tempDir, "main");
            Directory.CreateDirectory(mainDir);

            // Hermetic feed — cleared sources, so restore never reaches the network. The implicit
            // Runtime/Apple package references are switched off in Directory.Build.props below for
            // the same reason; neither has any bearing on readme packing.
            File.WriteAllText(Path.Combine(mainDir, "NuGet.config"), """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                  </packageSources>
                </configuration>
                """);

            File.WriteAllText(Path.Combine(mainDir, "Placeholder.cs"),
                "namespace ReadmeProbe { internal static class Placeholder { } }\n");

            if (projectReadme != null)
            {
                File.WriteAllText(Path.Combine(mainDir, "README.md"), projectReadme);
            }

            // Stands in for the generator writing {namespace}.api-surface.md into the binding
            // intermediate directory — the same path and glob the staging target reads. The Removed
            // arm is what a repack looks like once the project stops binding those modules: obj/
            // still holds the previous run's docs unless the generation clears them.
            var plantTarget = docs switch
            {
                SurfaceDocs.Present => """
                  <Target Name="_PlantApiSurfaceDocs" BeforeTargets="_StageSwiftBindingApiSurfaceReadme">
                    <MakeDir Directories="$(_SwiftBindingIntermediateDir)" />
                    <WriteLinesToFile File="$(_SwiftBindingIntermediateDir)Alpha.api-surface.md" Overwrite="true"
                                      Lines="# Alpha - AUTO-GENERATED public API surface;## Widget;- `Present(nint)`" />
                    <WriteLinesToFile File="$(_SwiftBindingIntermediateDir)Beta.api-surface.md" Overwrite="true"
                                      Lines="# Beta - AUTO-GENERATED public API surface;## Gadget;- `Configure(string)`" />
                  </Target>
                  """,
                SurfaceDocs.Removed => """
                  <Target Name="_PlantApiSurfaceDocs" BeforeTargets="_StageSwiftBindingApiSurfaceReadme">
                    <ItemGroup>
                      <_StaleApiSurfaceDoc Include="$(_SwiftBindingIntermediateDir)*.api-surface.md" />
                    </ItemGroup>
                    <Delete Files="@(_StaleApiSurfaceDoc)" />
                  </Target>
                  """,
                _ => "",
            };

            // The project's own README.md has to be packed by the project for the "author brought
            // their own" case to be a real one — that is what an author who writes a readme does.
            var ownReadmeItems = projectReadme != null
                ? """
                  <PropertyGroup>
                    <PackageReadmeFile>README.md</PackageReadmeFile>
                  </PropertyGroup>
                  <ItemGroup>
                    <None Include="README.md" Pack="true" PackagePath="" />
                  </ItemGroup>
                  """
                : "";

            var project = $"""
                <Project>
                  <Import Project="{sdkPropsPath}" />
                  <PropertyGroup>
                    {tfmElement}
                    <PackageId>{PackageId}</PackageId>
                    <Version>{PackageVersion}</Version>
                    <Authors>test</Authors>
                    <Description>readme pack probe</Description>
                    <IsPackable>true</IsPackable>
                    {extraProperties}
                  </PropertyGroup>
                  <Import Project="{sdkTargetsPath}" />
                  {ownReadmeItems}
                  <!-- Stub the codegen/native half: it needs a real xcframework, an Apple platform TFM
                       and Xcode, none of which the readme wiring touches. The generate hook keeps its
                       BeforeTargets anchor and stamps its wiring flag, because a bare override would
                       strip the anchor and trip the late hook-wiring tripwire. -->
                  <Target Name="_DetectSwiftBindingTargetKind" />
                  <Target Name="_ResolveAppleFrameworkPaths" />
                  <Target Name="_DiscoverSwiftFrameworks" />
                  <Target Name="_ValidateSwiftPackageItems" />
                  <Target Name="_GenerateSwiftBindings" BeforeTargets="ResolveProjectReferences">
                    <PropertyGroup>
                      <_SwiftHookRan_GenerateSwiftBindings>true</_SwiftHookRan_GenerateSwiftBindings>
                    </PropertyGroup>
                  </Target>
                  <Target Name="_CompileSwiftWrapper" />
                  <Target Name="_CollectSwiftModuleDatabases" />
                  <!-- The pack anchor is kept (the staging target hangs off it, and the SDK's own
                       TargetsForTfmSpecificContentInPackage registration is what schedules it) but
                       emptied: its real body arranges native slices for an Apple TFM. -->
                  <Target Name="_ConfigureSwiftBindingPack" Returns="@(TfmSpecificPackageFile)" />
                  {plantTarget}
                </Project>
                """;

            File.WriteAllText(Path.Combine(mainDir, "Main.csproj"), project);
            File.WriteAllText(Path.Combine(mainDir, "Directory.Build.props"), """
                <Project>
                  <PropertyGroup>
                    <DisableImplicitSwiftRuntimeReference>true</DisableImplicitSwiftRuntimeReference>
                    <DisableImplicitSwiftAppleReference>true</DisableImplicitSwiftAppleReference>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(mainDir, "Directory.Build.targets"), "<Project />");

            return mainDir;
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
            process.WaitForExit(180_000);
            return (process.ExitCode, stdOut, stdErr);
        }
    }
}
