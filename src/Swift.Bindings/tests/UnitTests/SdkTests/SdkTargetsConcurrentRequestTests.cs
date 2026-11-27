// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// One project configuration serves a build request per requester. In a diamond of project
    /// references (an app referencing a binding directly and through a second binding) the app's
    /// request and the second binding's Build request run on the same configuration. MSBuild makes
    /// the second request wait for a target the first is executing only when it reaches that
    /// target through DependsOnTargets. An AfterTargets hook of a target that already finished is
    /// not waited for. So every SDK target that reads a product of the wrapper compile (which runs
    /// as an AfterTargets hook of ResolveProjectReferences) has to name its producer.
    /// </summary>
    public class SdkTargetsConcurrentRequestTests : IDisposable
    {
        private readonly string _tempDir;

        public SdkTargetsConcurrentRequestTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"swift-sdk-diamond-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        /// <summary>
        /// A generator stand-in. The generate pass does nothing. The wrapper pass changes what the
        /// real wrapper compile changes in the files this fixture watches: it records the wrapper
        /// in binding-metadata.props, creates the wrapper xcframework, and writes the
        /// resource-bundle manifest with one bundle.
        /// </summary>
        private static readonly Lazy<string?> WrapperStubGeneratorDir = new(() =>
        {
            try
            {
                var stubDir = Path.Combine(Path.GetTempPath(), "swift-bindings-wrapper-stub-generator-v1");
                var publishDir = Path.Combine(stubDir, "out");
                if (File.Exists(Path.Combine(publishDir, "Swift.Bindings.dll")))
                    return publishDir + "/";

                Directory.CreateDirectory(stubDir);
                File.WriteAllText(Path.Combine(stubDir, "Stub.csproj"), """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net10.0</TargetFramework>
                        <AssemblyName>Swift.Bindings</AssemblyName>
                        <ImplicitUsings>enable</ImplicitUsings>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(Path.Combine(stubDir, "Program.cs"), """
                    var output = args[Array.IndexOf(args, "-o") + 1];
                    if (!args.Contains("--compile-wrapper-only")) return;
                    var props = Path.Combine(output, "binding-metadata.props");
                    var text = File.ReadAllText(props)
                        .Replace("<_SwiftBindingHasWrapperXCFramework>False<", "<_SwiftBindingHasWrapperXCFramework>True<")
                        .Replace("<_SwiftBindingWrapperSliceCount>0<", "<_SwiftBindingWrapperSliceCount>1<");
                    var module = System.Text.RegularExpressions.Regex.Match(
                        text, "<_SwiftBindingWrapperModuleName>([^<]+)<").Groups[1].Value;
                    Directory.CreateDirectory(Path.Combine(output, module + ".xcframework", "ios-arm64"));
                    Directory.CreateDirectory(Path.Combine(output, "DiamondResources.bundle"));
                    File.WriteAllText(Path.Combine(output, "DiamondResources.bundle", "payload.txt"), "payload");
                    File.WriteAllText(Path.Combine(output, "_resource-bundles.txt"), "DiamondResources\n");
                    File.WriteAllText(props, text);
                    """);
                File.WriteAllText(Path.Combine(stubDir, "Directory.Build.props"), "<Project />");
                File.WriteAllText(Path.Combine(stubDir, "Directory.Build.targets"), "<Project />");

                var (exitCode, _) = Run("dotnet",
                    $"publish \"{Path.Combine(stubDir, "Stub.csproj")}\" -o \"{publishDir}\" --nologo -v:q",
                    stubDir, TimeSpan.FromMinutes(3));
                return exitCode == 0 && File.Exists(Path.Combine(publishDir, "Swift.Bindings.dll"))
                    ? publishDir + "/"
                    : null;
            }
            catch
            {
                return null;
            }
        });

        [Fact]
        public void DiamondSecondRequest_WaitsForTheWrapperCompile()
        {
            // App builds A and B in parallel; B builds A again, so two requests share A's
            // configuration. Two barriers fix the interleaving, so the result never depends on
            // timing:
            //   * A's ProjectReference (Dep) holds the first request inside _CompileSwiftWrapper:
            //     its GetSwiftFrameworkSearchPaths, which the wrapper compile calls, marks
            //     in-wrapper-compile and then waits, yielding its node, until the second request
            //     has arrived.
            //   * B requests A only after in-wrapper-compile exists, so the second request starts
            //     while the first is parked mid-compile with ResolveProjectReferences finished.
            // The second request marks second-request (before ResolveNativeReferences, as the
            // downstream failure did) and then reaches the two readers: the resource-bundle step
            // (through _CollectBundleResources) and _ResolveSwiftNativeReferences. Nothing yields
            // between the mark and the reads, so the first request cannot finish the compile in
            // between: each reader either waits for the compile or reads the files before it ran.
            // Without the DependsOnTargets edges the second request reads HasWrapperXCFramework=False
            // and fails with SWIFTBIND051, and no resource bundle is collected.
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            var stubDir = WrapperStubGeneratorDir.Value;
            SkipUnless(stubDir != null, "Could not build the wrapper stub generator");

            var probe = Path.Combine(_tempDir, "probe");
            WriteDiamond(stubDir!, probe);

            var (restoreExit, restoreOut) = Run("dotnet",
                $"restore \"{Path.Combine(_tempDir, "A", "A.csproj")}\" --nologo -v:q", _tempDir, TimeSpan.FromMinutes(3));
            Assert.True(restoreExit == 0, $"restore failed:\n{restoreOut}");

            var (exitCode, output) = Run("dotnet",
                $"msbuild \"{Path.Combine(_tempDir, "App.proj")}\" -nologo -m:4 -nr:false -v:n",
                _tempDir, TimeSpan.FromMinutes(5));

            Assert.False(File.Exists(Path.Combine(probe, "first-request-timeout")),
                "The first request never reached the wrapper compile's dependency query.");
            Assert.False(File.Exists(Path.Combine(probe, "barrier-timeout")),
                "The second request never reached ResolveNativeReferences while the first was compiling.");
            // The positive control for the interleaving: the second request arrived while the
            // wrapper was still uncompiled. Without it the test could pass on a serial schedule.
            Assert.Equal("HasWrapper=False", File.ReadAllText(Path.Combine(probe, "second-request")).Trim());

            Assert.DoesNotContain("SWIFTBIND051", output);
            Assert.True(exitCode == 0, $"Diamond build failed:\n{output}");
            Assert.Contains("DiamondSwiftBindings.xcframework",
                File.ReadAllLines(Path.Combine(probe, "native-references")));
            Assert.Contains("DiamondResources.bundle/payload.txt",
                File.ReadAllLines(Path.Combine(probe, "bundle-resources")));
        }

        /// <summary>
        /// Each target that reads a product of another target names that producer in
        /// DependsOnTargets, which is the edge a second request on the same configuration waits
        /// on. The behavioral diamond above observes the wrapper-metadata and resource-bundle
        /// readers; the bridge pair is covered here, since no yield point lies between the wrapper
        /// metadata step and the bridge compile for a fixture to park a request on.
        /// </summary>
        [Theory]
        [InlineData("_UpdateSwiftWrapperMetadata", "_CompileSwiftWrapper")]
        [InlineData("_CompileSwiftUIBridge", "_UpdateSwiftWrapperMetadata")]
        [InlineData("_UpdateSwiftBridgeMetadata", "_CompileSwiftUIBridge")]
        [InlineData("_IncludeSwiftResourceBundleStubs", "_CompileSwiftWrapper")]
        [InlineData("_ReferenceMixedObjCCompanion", "_BuildMixedObjCCompanion")]
        public void ProductReader_DependsOnItsProducer(string reader, string producer)
        {
            Assert.Contains(producer, DependsOn(reader));
        }

        [Fact]
        public void ResourceBundleStep_StillFollowsTheProjectDependencies()
        {
            // The wrapper compile must not move ahead of the project dependencies it links
            // against: the resource step names ResolveProjectReferences first.
            var deps = DependsOn("_IncludeSwiftResourceBundleStubs");
            Assert.True(
                Array.IndexOf(deps, "ResolveProjectReferences") >= 0
                && Array.IndexOf(deps, "ResolveProjectReferences") < Array.IndexOf(deps, "_CompileSwiftWrapper"),
                $"DependsOnTargets is '{string.Join(";", deps)}'");
        }

        private static string[] DependsOn(string target)
        {
            var doc = XDocument.Load(Path.Combine(FindRepoRoot(), "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets"));
            var element = doc.Root!.Elements()
                .Single(e => e.Name.LocalName == "Target" && (string?)e.Attribute("Name") == target);
            return ((string?)element.Attribute("DependsOnTargets") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private void WriteDiamond(string stubDir, string probe)
        {
            var repoRoot = FindRepoRoot();
            var sdkDir = Path.Combine(repoRoot, "src", "Swift.Bindings.Sdk", "Sdk");
            var root = _tempDir;
            Directory.CreateDirectory(probe);
            Directory.CreateDirectory(Path.Combine(root, "A", "Diamond.xcframework"));
            Directory.CreateDirectory(Path.Combine(root, "Dep"));
            File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(root, "Directory.Build.targets"), "<Project />");

            var intermediate = Path.Combine(root, "A", "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediate);
            File.WriteAllText(Path.Combine(intermediate, "binding-metadata.props"), """
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingModuleName>Diamond</_SwiftBindingModuleName>
                    <_SwiftBindingWrapperModuleName>DiamondSwiftBindings</_SwiftBindingWrapperModuleName>
                    <_SwiftBindingHasWrapperXCFramework>False</_SwiftBindingHasWrapperXCFramework>
                    <_SwiftBindingWrapperSliceCount>0</_SwiftBindingWrapperSliceCount>
                  </PropertyGroup>
                </Project>
                """);

            // A bounded wait (2 minutes) that marks a timeout instead of hanging the test.
            static string WaitFor(string file, string timeoutMarker) =>
                $"i=0; while [ %24i -lt 1200 ]; do [ -f '{file}' ] &amp;&amp; exit 0; sleep 0.1; i=%24((i+1)); done; touch '{timeoutMarker}'";

            var inCompile = Path.Combine(probe, "in-wrapper-compile");
            var secondRequest = Path.Combine(probe, "second-request");

            File.WriteAllText(Path.Combine(root, "Dep", "Dep.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <Target Name="GetSwiftFrameworkSearchPaths">
                    <Exec YieldDuringToolExecution="true"
                          Command="touch '{inCompile}'; {WaitFor(secondRequest, Path.Combine(probe, "barrier-timeout"))}" />
                  </Target>
                </Project>
                """);

            // The binding: the real Sdk.props/Sdk.targets with the stub generator. The probe
            // targets sit before the Sdk.targets import so they run ahead of the SDK's own
            // ResolveNativeReferences hook. _CollectBundleResources stands in for the Apple
            // workload's collection target, and ResolveNativeReferences for its native-reference
            // resolution (the desktop task refuses to run on .NET).
            File.WriteAllText(Path.Combine(root, "A", "A.csproj"), $"""
                <Project>
                  <Import Project="{sdkDir}/Sdk.props" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsPackable>false</IsPackable>
                    <DisableImplicitSwiftRuntimeReference>true</DisableImplicitSwiftRuntimeReference>
                    <DisableImplicitSwiftAppleReference>true</DisableImplicitSwiftAppleReference>
                    <_SwiftBindingGeneratorDir>{stubDir}</_SwiftBindingGeneratorDir>
                  </PropertyGroup>
                  <ItemGroup>
                    <SwiftFramework Include="{Path.Combine(root, "A", "Diamond.xcframework")}" />
                    <ProjectReference Include="../Dep/Dep.csproj" />
                  </ItemGroup>
                  <Target Name="_DiamondSecondRequest" BeforeTargets="ResolveNativeReferences">
                    <WriteLinesToFile File="{secondRequest}" Lines="HasWrapper=$(_SwiftBindingHasWrapperXCFramework)" Overwrite="true" />
                  </Target>
                  <Target Name="_DiamondCollectResources" BeforeTargets="ResolveNativeReferences" DependsOnTargets="_CollectBundleResources">
                    <WriteLinesToFile File="{Path.Combine(probe, "bundle-resources")}" Lines="@(BundleResource->'%(Link)')" Overwrite="true" />
                  </Target>
                  <Target Name="_CollectBundleResources" />
                  <Import Project="{sdkDir}/Sdk.targets" />
                  <PropertyGroup>
                    <_SwiftBindingIntermediateDir>{intermediate}</_SwiftBindingIntermediateDir>
                  </PropertyGroup>
                  <Target Name="_DiscoverSwiftFrameworks" />
                  <Target Name="_ValidateSwiftPackageItems" />
                  <Target Name="ResolveNativeReferences" DependsOnTargets="ResolveProjectReferences" />
                  <Target Name="_DiamondRecordNativeReferences" AfterTargets="_ResolveSwiftNativeReferences">
                    <WriteLinesToFile File="{Path.Combine(probe, "native-references")}" Lines="@(NativeReference->'%(Filename)%(Extension)')" Overwrite="true" />
                    <ItemGroup>
                      <NativeReference Remove="@(NativeReference)" />
                    </ItemGroup>
                  </Target>
                </Project>
                """);

            File.WriteAllText(Path.Combine(root, "B.proj"), $"""
                <Project DefaultTargets="Build">
                  <Target Name="Build">
                    <Exec YieldDuringToolExecution="true" Command="{WaitFor(inCompile, Path.Combine(probe, "first-request-timeout"))}" />
                    <MSBuild Projects="A/A.csproj" Targets="Build" />
                  </Target>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "App.proj"), """
                <Project DefaultTargets="Build">
                  <Target Name="Build">
                    <MSBuild Projects="A/A.csproj;B.proj" BuildInParallel="true" />
                  </Target>
                </Project>
                """);
        }

        private static readonly Lazy<bool> MsbuildAvailable = new(() =>
        {
            try { return Run("dotnet", "msbuild --version", Path.GetTempPath(), TimeSpan.FromMinutes(1)).ExitCode == 0; }
            catch { return false; }
        });

        private static (int ExitCode, string Output) Run(string fileName, string arguments, string workingDirectory, TimeSpan timeout)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            using var process = Process.Start(psi)!;
            var stdOut = process.StandardOutput.ReadToEndAsync();
            var stdErr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return (-1, $"timed out after {timeout}\n{stdOut.Result}\n{stdErr.Result}");
            }
            process.WaitForExit();
            return (process.ExitCode, stdOut.Result + "\n" + stdErr.Result);
        }

        private static void SkipUnless(bool condition, string reason)
        {
            if (!condition)
                throw Xunit.Sdk.SkipException.ForSkip(reason);
        }

        private static string FindRepoRoot()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("Cannot find repo root.");
        }
    }
}
