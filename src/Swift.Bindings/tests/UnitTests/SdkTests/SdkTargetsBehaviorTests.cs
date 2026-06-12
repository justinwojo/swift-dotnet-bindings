// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Diagnostics;
using System.Linq;
using Xunit;
namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Behavioral tests that verify the actual shell commands and MSBuild patterns
    /// used by Sdk.targets work correctly at runtime — not just that the XML is present.
    /// </summary>
    public class SdkTargetsBehaviorTests : IDisposable
    {
        private readonly string _tempDir;

        /// <summary>
        /// Lazy one-time check: can we invoke <c>dotnet msbuild</c>?
        /// Tests that require MSBuild use <c>Assert.SkipUnless</c> when this is false,
        /// but assert non-zero exit as a real failure when MSBuild IS available.
        /// </summary>
        private static readonly Lazy<bool> MsbuildAvailable = new(() =>
        {
            try
            {
                var (exitCode, _, _) = RunProcess("dotnet", "msbuild --version");
                return exitCode == 0;
            }
            catch { return false; }
        });

        /// <summary>
        /// Lazy one-time build of a stub Swift.Bindings.dll that writes received args
        /// to stderr and exits 0. Used by tests that exercise the REAL _GenerateSwiftBindings
        /// target (which invokes <c>dotnet exec Swift.Bindings.dll</c>).
        /// </summary>
        private static readonly Lazy<string?> StubGeneratorDir = new(() =>
        {
            if (!MsbuildAvailable.Value) return null;

            try
            {
                var stubDir = Path.Combine(Path.GetTempPath(), "swift-bindings-stub-generator");
                var publishDir = Path.Combine(stubDir, "out");

                // Reuse if already built (persists across test runs)
                if (File.Exists(Path.Combine(publishDir, "Swift.Bindings.dll")))
                    return publishDir + "/";

                Directory.CreateDirectory(stubDir);

                File.WriteAllText(Path.Combine(stubDir, "Stub.csproj"), """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net10.0</TargetFramework>
                        <AssemblyName>Swift.Bindings</AssemblyName>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(Path.Combine(stubDir, "Program.cs"), """
                    System.Console.Error.WriteLine("STUB_RECEIVED_ARGS:" + string.Join(" ", args));
                    """);
                // Prevent repo-level build files from interfering
                File.WriteAllText(Path.Combine(stubDir, "Directory.Build.props"), "<Project />");
                File.WriteAllText(Path.Combine(stubDir, "Directory.Build.targets"), "<Project />");

                var (exitCode, _, _) = RunProcess("dotnet",
                    $"publish \"{Path.Combine(stubDir, "Stub.csproj")}\" -o \"{publishDir}\" --nologo -v:q");

                if (exitCode != 0 || !File.Exists(Path.Combine(publishDir, "Swift.Bindings.dll")))
                    return null;

                return publishDir + "/";
            }
            catch { return null; }
        });

        public SdkTargetsBehaviorTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"swift-sdk-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        // ── find -type d discovers xcframework directories ──

        [Fact]
        public void FindTypeD_DiscoversXCFrameworkDirectory()
        {
            // Create a directory bundle (what xcframeworks actually are)
            var xcfwDir = Path.Combine(_tempDir, "ImagePipeline.xcframework");
            Directory.CreateDirectory(xcfwDir);

            var result = RunFind(_tempDir);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("ImagePipeline.xcframework", result.StdOut);
        }

        [Fact]
        public void FindTypeD_IgnoresXCFrameworkFiles()
        {
            // Create a regular file with .xcframework extension (should NOT be found)
            File.WriteAllText(Path.Combine(_tempDir, "Fake.xcframework"), "not a directory");

            var result = RunFind(_tempDir);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("Fake.xcframework", result.StdOut);
        }

        [Fact]
        public void FindTypeD_ReturnsEmptyForNoXCFrameworks()
        {
            // Empty directory — find should return nothing (not an error)
            var result = RunFind(_tempDir);

            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.StdOut.Trim());
        }

        [Fact]
        public void FindTypeD_MaxDepth1_IgnoresNestedXCFrameworks()
        {
            // Nested xcframework directory should NOT be found (maxdepth 1)
            var nested = Path.Combine(_tempDir, "subdir", "Nested.xcframework");
            Directory.CreateDirectory(nested);

            var result = RunFind(_tempDir);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("Nested.xcframework", result.StdOut);
        }

        [Fact]
        public void FindTypeD_ReturnsFullPathsForConsoleToMSBuild()
        {
            // MSBuild's ConsoleToMSBuild expects full paths to populate ItemName
            var xcfwDir = Path.Combine(_tempDir, "Library.xcframework");
            Directory.CreateDirectory(xcfwDir);

            var result = RunFind(_tempDir);

            // find returns absolute paths when given an absolute search dir
            Assert.StartsWith("/", result.StdOut.Trim());
            Assert.Contains(_tempDir, result.StdOut);
        }

        [Fact]
        public void FindTypeD_DiscoversMultipleXCFrameworks()
        {
            Directory.CreateDirectory(Path.Combine(_tempDir, "ImagePipeline.xcframework"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "VectorAnimation.xcframework"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "NotAnXCFW")); // should not match

            var result = RunFind(_tempDir);
            var lines = result.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(2, lines.Length);
            Assert.Contains(lines, l => l.Contains("ImagePipeline.xcframework"));
            Assert.Contains(lines, l => l.Contains("VectorAnimation.xcframework"));
        }

        // ── Second-slice park-aside swap + interrupted-swap recovery ──
        //
        // These exercise the EXACT shell commands Sdk.targets uses to commit a merged
        // fat xcframework and to heal a swap that a SIGKILL interrupted mid-commit. The
        // commit parks the live tree at a '.superseded' sibling, moves the merged tree
        // in, rolls back on failure, then drops the aside; recovery (run every build,
        // before the presence probe) restores a wrapper or cleans a bridge aside.

        // The wrapper/bridge commit Exec, verbatim from Sdk.targets (X = live xcframework
        // path, M = staging merged.xcframework path).
        private static string SwapCommand(string x, string m) =>
            $"set -e; rm -rf \"{x}.superseded\"; mv \"{x}\" \"{x}.superseded\"; " +
            $"mv \"{m}\" \"{x}\" || {{ mv \"{x}.superseded\" \"{x}\"; exit 1; }}; rm -rf \"{x}.superseded\"";

        // The wrapper recovery Exec, verbatim from Sdk.targets: restore if the primary is
        // missing, else clear a stale aside.
        private static string WrapperRecoveryCommand(string x) =>
            $"if [ ! -d \"{x}\" ]; then mv \"{x}.superseded\" \"{x}\"; else rm -rf \"{x}.superseded\"; fi";

        private static string MarkerOf(string dir) =>
            File.Exists(Path.Combine(dir, "marker")) ? File.ReadAllText(Path.Combine(dir, "marker")) : "<none>";

        private static void MakeTree(string dir, string marker)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "marker"), marker);
        }

        [Fact]
        public void SecondSliceSwap_ParkAsideCommit_ReplacesLiveTreeAndLeavesNoAside()
        {
            var live = Path.Combine(_tempDir, "Foo.xcframework");
            var merged = Path.Combine(_tempDir, "_merge_slices", "merged.xcframework");
            MakeTree(live, "ORIGINAL");
            MakeTree(merged, "MERGED");

            var result = RunShell(SwapCommand(live, merged));

            Assert.Equal(0, result.ExitCode);
            Assert.True(Directory.Exists(live), "live tree must exist after commit");
            Assert.Equal("MERGED", MarkerOf(live));               // merged content swapped in
            Assert.False(Directory.Exists(live + ".superseded"), "aside must be dropped");
            Assert.False(Directory.Exists(merged), "staging merged must be consumed");
        }

        [Fact]
        public void SecondSliceSwap_KillBetweenMoves_LeavesCompleteOriginalAtSuperseded()
        {
            // Simulate a SIGKILL after the park-aside mv but before the move-in: only the
            // first two steps of the swap ran.
            var live = Path.Combine(_tempDir, "Foo.xcframework");
            MakeTree(live, "ORIGINAL");

            var interrupted = RunShell($"set -e; rm -rf \"{live}.superseded\"; mv \"{live}\" \"{live}.superseded\"");

            Assert.Equal(0, interrupted.ExitCode);
            Assert.False(Directory.Exists(live), "primary path is empty in the interrupted window");
            Assert.True(Directory.Exists(live + ".superseded"), "the complete original survives at .superseded");
            Assert.Equal("ORIGINAL", MarkerOf(live + ".superseded"));

            // Next build's recovery restores it before the presence probe.
            var recovered = RunShell(WrapperRecoveryCommand(live));

            Assert.Equal(0, recovered.ExitCode);
            Assert.True(Directory.Exists(live), "wrapper restored");
            Assert.Equal("ORIGINAL", MarkerOf(live));
            Assert.False(Directory.Exists(live + ".superseded"), "aside cleared after restore");
        }

        [Fact]
        public void WrapperRecovery_PrimaryAlreadyPresent_ClearsStaleAsideOnly()
        {
            // The swap completed (primary present) but a stale aside lingers (e.g. a kill
            // after move-in, before the final rm). Recovery must NOT clobber the live tree.
            var live = Path.Combine(_tempDir, "Foo.xcframework");
            MakeTree(live, "MERGED");
            MakeTree(live + ".superseded", "ORIGINAL");

            var result = RunShell(WrapperRecoveryCommand(live));

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("MERGED", MarkerOf(live));                  // live tree untouched
            Assert.False(Directory.Exists(live + ".superseded"), "stale aside cleared");
        }

        [Fact]
        public void SecondSliceSwap_MoveInFails_RollsBackOriginalWithoutDataLoss()
        {
            // The staging merged tree does not exist, so the move-in fails; the rollback
            // arm must restore the original from .superseded and fail non-zero (no data loss).
            var live = Path.Combine(_tempDir, "Foo.xcframework");
            var missingMerged = Path.Combine(_tempDir, "_merge_slices", "merged.xcframework");
            MakeTree(live, "ORIGINAL");

            var result = RunShell(SwapCommand(live, missingMerged));

            Assert.NotEqual(0, result.ExitCode);                    // surfaces the failure
            Assert.True(Directory.Exists(live), "original rolled back into place");
            Assert.Equal("ORIGINAL", MarkerOf(live));
            Assert.False(Directory.Exists(live + ".superseded"), "no aside left after rollback");
        }

        [Fact]
        public void BridgeRecovery_RemovesSupersededWithoutRestoring()
        {
            // The bridge recovery is a RemoveDir of the '.superseded' aside (NOT an mv-back):
            // a single-slice bridge would be dropped by SWIFTBIND052 anyway, so the bridge is
            // left missing to degrade per contract and only the orphan is cleaned. Encodes the
            // wrapper/bridge asymmetry as a `rm -rf` of the aside.
            var live = Path.Combine(_tempDir, "FooBridge.xcframework");
            MakeTree(live + ".superseded", "ORIGINAL");             // primary missing, aside present

            var result = RunShell($"rm -rf \"{live}.superseded\"");

            Assert.Equal(0, result.ExitCode);
            Assert.False(Directory.Exists(live), "bridge stays missing — NOT restored");
            Assert.False(Directory.Exists(live + ".superseded"), "orphan aside cleaned");
        }

        // ── IntermediateOutputPath resolution ──

        [Fact]
        public void IntermediateOutputPath_EmptyInPropsContext_PopulatedInTargetsContext()
        {
            // Demonstrates WHY _SwiftBindingIntermediateDir must be in .targets, not .props:
            // $(IntermediateOutputPath) is set by Microsoft.NET.Sdk's targets, so it's empty
            // when .props files and the project body are evaluated, but populated when
            // .targets files (like Directory.Build.targets) are evaluated.
            //
            // _PropsDir is defined in the project body (same timing as .props) → empty prefix.
            // _TargetsDir is defined in Directory.Build.targets (after SDK targets) → has obj/.
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var projectContent = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <_PropsDir>$(IntermediateOutputPath)swift-binding/</_PropsDir>
                  </PropertyGroup>
                </Project>
                """;
            var targetsContent = """
                <Project>
                  <PropertyGroup>
                    <_TargetsDir>$(IntermediateOutputPath)swift-binding/</_TargetsDir>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), projectContent);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), targetsContent);
            // Prevent inheriting repo-level Directory.Build.props/targets
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");

            var propsResult = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -getProperty:_PropsDir -nologo");
            var targetsResult = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -getProperty:_TargetsDir -nologo");

            Assert.True(propsResult.ExitCode == 0,
                $"MSBuild -getProperty:_PropsDir failed.\nStdErr: {propsResult.StdErr}");
            Assert.True(targetsResult.ExitCode == 0,
                $"MSBuild -getProperty:_TargetsDir failed.\nStdErr: {targetsResult.StdErr}");

            var propsDir = propsResult.StdOut.Trim();
            var targetsDir = targetsResult.StdOut.Trim();

            // Props context: $(IntermediateOutputPath) is empty → just "swift-binding/"
            Assert.Equal("swift-binding/", propsDir);

            // Targets context: $(IntermediateOutputPath) resolved → contains "obj/"
            Assert.Contains("obj", targetsDir);
            Assert.EndsWith("swift-binding/", targetsDir);
        }

        // ── Module database collection behavioral tests ──
        // These verify _CollectSwiftModuleDatabases actually collects items at MSBuild
        // execution time (not just that the XML looks correct).

        [Fact]
        public void CollectModuleDatabases_CollectsFromNuGetSource()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var dbPath = Path.Combine(_tempDir, "DepModuleDatabase.xml");
            File.WriteAllText(dbPath, "<TypeDatabase />");

            var project = CreateCollectionTestProject(
                swiftModuleDatabases: $"""
                    <SwiftModuleDatabase Include="{dbPath}">
                      <ModuleName>DepModule</ModuleName>
                      <SourcePackage>DepModule.Swift.iOS</SourcePackage>
                    </SwiftModuleDatabase>
                    """);
            WriteTestProject(project);

            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:TestDump -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"_CollectSwiftModuleDatabases target failed (NuGet source).\nStdErr: {result.StdErr}\nStdOut: {result.StdOut}");

            Assert.Contains("DepModuleDatabase.xml", result.StdOut);
        }

        [Fact]
        public void CollectModuleDatabases_CollectsFromLocalModuleDatabasePath()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var dbPath = Path.Combine(_tempDir, "LocalDatabase.xml");
            File.WriteAllText(dbPath, "<TypeDatabase />");

            var project = CreateCollectionTestProject(
                swiftFrameworkDependencies: $"""
                    <SwiftFrameworkDependency Include="/fake.xcframework"
                                              ModuleDatabasePath="{dbPath}" />
                    """);
            WriteTestProject(project);

            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:TestDump -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"_CollectSwiftModuleDatabases target failed (local path).\nStdErr: {result.StdErr}\nStdOut: {result.StdOut}");

            Assert.Contains("LocalDatabase.xml", result.StdOut);
        }

        [Fact]
        public void CollectModuleDatabases_EmitsSWIFTBIND073ForMissingPath()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var project = CreateCollectionTestProject(
                swiftFrameworkDependencies: """
                    <SwiftFrameworkDependency Include="/fake.xcframework"
                                              ModuleDatabasePath="/nonexistent/path/Database.xml" />
                    """);
            WriteTestProject(project);

            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:TestDump -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"_CollectSwiftModuleDatabases target failed (SWIFTBIND073 test).\nStdErr: {result.StdErr}\nStdOut: {result.StdOut}");

            var output = result.StdOut + "\n" + result.StdErr;
            Assert.Contains("SWIFTBIND073", output);
            Assert.Contains("Module database not found", output);
        }

        [Fact]
        public void CollectModuleDatabases_FeedsModuleDatabaseArgsToGenerator()
        {
            // Exercises the REAL _GenerateSwiftBindings target from Sdk.targets by pointing
            // _SwiftBindingGeneratorDir at a stub DLL. The stub writes received args to stderr
            // (which MSBuild surfaces at StandardErrorImportance="high"). This validates:
            // 1. _CollectSwiftModuleDatabases fires via BeforeTargets="_GenerateSwiftBindings"
            // 2. The real PropertyGroup conditions (@(SwiftFramework), up-to-date gate) pass
            // 3. The real @(_SwiftModuleDatabaseFile) item transform produces --module-database args
            // 4. The Exec actually runs and receives the constructed command
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var stubDir = StubGeneratorDir.Value;
            SkipUnless(stubDir != null, "Could not build stub generator DLL");

            var dbPath = Path.Combine(_tempDir, "CoreDatabase.xml");
            File.WriteAllText(dbPath, "<TypeDatabase />");

            // Fake xcframework directory (gates the PropertyGroup in _GenerateSwiftBindings:
            // Condition="'@(SwiftFramework)' != '' AND '$(_SwiftBindingUpToDate)' != 'true'")
            var fakeXcfw = Path.Combine(_tempDir, "Fake.xcframework");
            Directory.CreateDirectory(fakeXcfw);

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            // Import real Sdk.targets. Override only targets that require real xcframework
            // content. The REAL _CollectSwiftModuleDatabases and _GenerateSwiftBindings run.
            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <_SwiftBindingGeneratorDir>{stubDir}</_SwiftBindingGeneratorDir>
                  </PropertyGroup>
                  <ItemGroup>
                    <SwiftFramework Include="{fakeXcfw}" />
                  </ItemGroup>
                  <ItemGroup>
                    <SwiftModuleDatabase Include="{dbPath}">
                      <ModuleName>Core</ModuleName>
                    </SwiftModuleDatabase>
                  </ItemGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <Target Name="_ComputeSwiftFingerprint" />
                  <Target Name="_DiscoverSwiftFrameworks" />
                  <Target Name="_ValidateSwiftPackageItems" />
                </Project>
                """;

            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:_GenerateSwiftBindings -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"_GenerateSwiftBindings with stub generator failed.\nStdErr: {result.StdErr}\nStdOut: {result.StdOut}");

            // Stub writes "STUB_RECEIVED_ARGS:..." to stderr; MSBuild surfaces it at
            // StandardErrorImportance="high" in build output (stdout of dotnet msbuild process)
            var output = result.StdOut + "\n" + result.StdErr;
            Assert.Contains("STUB_RECEIVED_ARGS:", output);
            Assert.Contains("--module-database", output);
            Assert.Contains("CoreDatabase.xml", output);
        }

        // ── Auto-detected dependency resolution behavioral tests ──

        [Fact]
        public void ResolveAutoDetectedDeps_FindsSiblingProject_InjectsProjectReference()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            // Create a "dependency" sibling project directory
            var siblingDir = Path.Combine(_tempDir, "PaymentSdkCore.Swift.iOS");
            Directory.CreateDirectory(siblingDir);
            File.WriteAllText(Path.Combine(siblingDir, "PaymentSdkCore.Swift.iOS.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

            // Create fake xcframework inside a "binding project" directory
            var bindingDir = Path.Combine(_tempDir, "PaymentSdkSheet.Swift.iOS");
            Directory.CreateDirectory(bindingDir);
            var fakeXcfw = Path.Combine(bindingDir, "PaymentSdkSheet.xcframework");
            Directory.CreateDirectory(fakeXcfw);

            // Create binding-metadata.props with dependency pointing near the sibling
            var intermediateDir = Path.Combine(bindingDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            // The xcframework path is inside bindingDir, so grandparent is _tempDir,
            // and peer subdirectory search will find _tempDir/PaymentSdkCore.Swift.iOS/PaymentSdkCore.Swift.iOS.csproj
            var depsValue = $"PaymentSdkCore|PaymentSdkCore.Swift.iOS|25.6.2|{fakeXcfw}";
            var metadataProps = $"""
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>1.0.0</_SwiftBindingPackageVersion>
                    <_SwiftBindingMinimumOSVersion>15.0</_SwiftBindingMinimumOSVersion>
                    <_SwiftBindingModuleName>PaymentSdkSheet</_SwiftBindingModuleName>
                    <_SwiftBindingIsVersionPlaceholder>False</_SwiftBindingIsVersionPlaceholder>
                    <_SwiftBindingHasWrapperXCFramework>False</_SwiftBindingHasWrapperXCFramework>
                    <_SwiftBindingWrapperModuleName>PaymentSdkSheetSwiftBindings</_SwiftBindingWrapperModuleName>
                    <_SwiftBindingWrapperSliceCount>0</_SwiftBindingWrapperSliceCount>
                    <_SwiftBindingDependencies>{depsValue}</_SwiftBindingDependencies>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(intermediateDir, "binding-metadata.props"), metadataProps);

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            // Project imports real Sdk.targets, overrides targets that need real xcframework,
            // and has a TestDump target that outputs ProjectReference items.
            // _SwiftBindingIntermediateDir must be set AFTER the Sdk.targets import because
            // Sdk.targets redefines it from $(IntermediateOutputPath) which would overwrite our value.
            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingIntermediateDir>{intermediateDir}</_SwiftBindingIntermediateDir>
                  </PropertyGroup>
                  <Target Name="_ComputeSwiftFingerprint" />
                  <Target Name="_DiscoverSwiftFrameworks" />
                  <Target Name="_ValidateSwiftPackageItems" />
                  <Target Name="_GenerateSwiftBindings" />
                  <Target Name="TestDump"
                          DependsOnTargets="_ImportSwiftBindingMetadata;_ResolveSwiftAutoDetectedDependencies">
                    <Message Importance="High" Text="PROJREF_ITEM:%(ProjectReference.Identity)" />
                  </Target>
                </Project>
                """;

            File.WriteAllText(Path.Combine(bindingDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(bindingDir, "Test.csproj")}\" -t:TestDump -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"_ResolveSwiftAutoDetectedDependencies test failed.\nStdErr: {result.StdErr}\nStdOut: {result.StdOut}");

            var output = result.StdOut + "\n" + result.StdErr;
            Assert.Contains("PaymentSdkCore.Swift.iOS.csproj", output);
        }

        // ── Multi-framework first-build ordering (Gap #7): _BuildSiblingSwiftBindingDeps
        //    pre-builds user-declared sibling ProjectReferences before the database scan +
        //    generate. (Sibling Apple-framework deps need no in-tree pre-build — they are
        //    always a restored PackageReference feeding _CollectSwiftModuleDatabases Source 1.) ──

        [Fact]
        public void BuildSiblingSwiftBindingDeps_PreBuildsSiblingProjectReference()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            var (output, exitCode) = RunSiblingPreBuildDump();
            Assert.True(exitCode == 0, $"_CollectSwiftModuleDatabases (sibling pre-build) failed.\nOutput: {output}");
            Assert.Contains("SIBLING_PREBUILT", output);
        }

        [Fact]
        public void BuildSiblingSwiftBindingDeps_OuterNoBuild_SiblingPreBuildDoesNotInheritNoBuild()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // Same global-NoBuild hazard as the companion (see
            // BuildMixedObjCCompanion_OuterNoBuild_CompanionBuildDoesNotInheritNoBuild): a
            // `dotnet pack --no-build` outer sets NoBuild=true as a GLOBAL property which the
            // <MSBuild> task forwards to this out-of-band sibling pre-build. Here ContinueOnError
            // would SWALLOW the resulting NETSDK1085 into a skipped pre-build — silently leaving a
            // stale module database (the very failure mode _BuildSiblingSwiftBindingDeps exists to
            // prevent) instead of a hard error. _BuildSiblingSwiftBindingDeps must therefore
            // neutralize the inherited NoBuild (Properties NoBuild=false) so the sibling actually
            // pre-builds. Assert on the value the sibling's Build receives — true would trip the
            // guard in a real SDK sibling.
            var (output, exitCode) = RunSiblingPreBuildDump(noBuild: true);
            Assert.True(exitCode == 0, $"_CollectSwiftModuleDatabases (sibling pre-build) failed under outer NoBuild.\nOutput: {output}");
            Assert.Contains("SIBLING_PREBUILT", output);
            Assert.DoesNotContain("SIBLING_PREBUILT_NOBUILD:[true]", output);
            Assert.Contains("SIBLING_PREBUILT_NOBUILD:[false]", output);
        }

        /// <summary>
        /// Drives the REAL _CollectSwiftModuleDatabases chain so its DependsOn
        /// _BuildSiblingSwiftBindingDeps pre-builds a stub sibling ProjectReference. The stub's
        /// Build target echoes <c>SIBLING_PREBUILT</c> and the inherited <c>$(NoBuild)</c> so a
        /// caller can assert both that the pre-build fired and the value it received. When
        /// <paramref name="noBuild"/> is true, <c>-p:NoBuild=true</c> models a `dotnet pack --no-build`
        /// outer (NoBuild as a forwarded GLOBAL property).
        /// </summary>
        private (string Output, int ExitCode) RunSiblingPreBuildDump(bool noBuild = false)
        {
            // On a CLEAN first build a multi-framework library's lower binding (a sibling
            // ProjectReference) hasn't been built yet when _CollectSwiftModuleDatabases /
            // _GenerateSwiftBindings run — ResolveProjectReferences builds siblings AFTER
            // generate — so cross-module type resolution degrades to a CS0234 and the wrapper
            // -F path misses the sibling, self-healing only on the 2nd build.
            // _BuildSiblingSwiftBindingDeps closes that by pre-building the sibling first.
            //
            // Drive the REAL _CollectSwiftModuleDatabases chain (not the pre-build target in
            // isolation): _BuildSiblingSwiftBindingDeps gates its Condition on
            // @(_UserProjectReference), which is populated by _DiscoverProjectReferenceDependencies
            // running early via _ComputeSwiftFingerprint's BeforeTargets — exactly the ordering
            // the pre-build target needs. Invoking the pre-build target alone would evaluate its
            // Condition before that runs and skip it.

            // A sibling "binding" project: a plain MSBuild project whose Build target echoes
            // a marker (a non-SDK <Project> so Targets="Build" hits our marker, not the MS default).
            // It also echoes the forwarded $(NoBuild); a real SDK sibling would trip NETSDK1085 here
            // if NoBuild=true rode in, so the SDK-less stub asserts on the propagated value instead.
            var siblingDir = Path.Combine(_tempDir, "Sibling.Swift.iOS");
            Directory.CreateDirectory(siblingDir);
            var siblingCsproj = Path.Combine(siblingDir, "Sibling.Swift.iOS.csproj");
            File.WriteAllText(siblingCsproj, """
                <Project>
                  <Target Name="Restore" />
                  <Target Name="Build">
                    <Message Importance="High" Text="SIBLING_PREBUILT" />
                    <Message Importance="High" Text="SIBLING_PREBUILT_NOBUILD:[$(NoBuild)]" />
                  </Target>
                </Project>
                """);

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            // Keep the REAL _DiscoverProjectReferenceDependencies (populates _UserProjectReference),
            // _DetectSwiftBindingTargetKind (sets the non-Apple kind), and _BuildSiblingSwiftBindingDeps.
            // Override the targets whose real bodies need an xcframework or reject the test TFM:
            //   _ComputeSwiftFingerprint — empty body, but its BeforeTargets hooks
            //     (_DiscoverProjectReferenceDependencies, _DetectSwiftBindingTargetKind) STILL fire,
            //     which is exactly what populates _UserProjectReference + the kind before
            //     _BuildSiblingSwiftBindingDeps's Condition is evaluated.
            //   _DiscoverSwiftFrameworks — would error SWIFTBIND001 with no xcframework.
            //   _ValidateSwiftPackageItems — fires SWIFTBIND010 on the net10.0 test TFM.
            //   _DetectAppleFrameworkCrossModuleDeps — a _CollectSwiftModuleDatabases dep that
            //     would otherwise resolve Apple framework paths / shell the generator.
            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{siblingCsproj}" />
                  </ItemGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <Target Name="_ComputeSwiftFingerprint" />
                  <Target Name="_DiscoverSwiftFrameworks" />
                  <Target Name="_ValidateSwiftPackageItems" />
                  <Target Name="_DetectAppleFrameworkCrossModuleDeps" />
                </Project>
                """;

            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            // Drive the real _CollectSwiftModuleDatabases entry: it DependsOn
            // _BuildSiblingSwiftBindingDeps, and its earlier _ComputeSwiftFingerprint dep pulls
            // in the BeforeTargets-scheduled discovery that populates _UserProjectReference first.
            var noBuildArg = noBuild ? " -p:NoBuild=true" : "";
            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:_CollectSwiftModuleDatabases -nologo -v:n{noBuildArg}");
            return (result.StdOut + "\n" + result.StdErr, result.ExitCode);
        }

        [Fact]
        public void BuildSiblingSwiftBindingDeps_ForwardsPinnedConfigurationAndTargetFramework()
        {
            // When a sibling ProjectReference pins a CROSS-config/cross-TFM slice via
            // SetConfiguration/SetTargetFramework, _BuildSiblingSwiftBindingDeps must pre-build it
            // under THAT slice — the same one _CollectSwiftModuleDatabases Source 3 and the wrapper
            // -F query later scan (obj/<cfg>/<tfm>/). Forwarding only the parent Configuration (as
            // the pre-build did before this fix) builds the sibling into obj/Debug/ while a
            // Release-pinned discovery scans obj/Release/ → the database + wrapper xcframework are
            // missed on a clean first build. SetConfiguration is MSBuild's canonical
            // `Configuration=<cfg>` form, passed verbatim and appended AFTER the parent default so
            // the pin wins. This mirrors the reported repro shape (both pins set).
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            // Sibling echoes the active Configuration/TargetFramework it was actually built under.
            var siblingDir = Path.Combine(_tempDir, "Sibling.Swift.iOS");
            Directory.CreateDirectory(siblingDir);
            var siblingCsproj = Path.Combine(siblingDir, "Sibling.Swift.iOS.csproj");
            File.WriteAllText(siblingCsproj, """
                <Project>
                  <Target Name="Restore" />
                  <Target Name="Build">
                    <Message Importance="High" Text="SIBLING_CFG=$(Configuration)" />
                    <Message Importance="High" Text="SIBLING_TFM=$(TargetFramework)" />
                  </Target>
                </Project>
                """);

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            // Parent defaults to Debug (Microsoft.NET.Sdk's Configuration default). The pin forces
            // the sibling onto Release, so a leaked-parent-config bug shows as SIBLING_CFG=Debug.
            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{siblingCsproj}"
                                      SetConfiguration="Configuration=Release"
                                      SetTargetFramework="TargetFramework=net10.0" />
                  </ItemGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <Target Name="_ComputeSwiftFingerprint" />
                  <Target Name="_DiscoverSwiftFrameworks" />
                  <Target Name="_ValidateSwiftPackageItems" />
                  <Target Name="_DetectAppleFrameworkCrossModuleDeps" />
                </Project>
                """;

            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:_CollectSwiftModuleDatabases -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"_CollectSwiftModuleDatabases (pinned sibling pre-build) failed.\nStdErr: {result.StdErr}\nStdOut: {result.StdOut}");

            var output = result.StdOut + "\n" + result.StdErr;
            // The pin won: the sibling built under Release/net10.0, not the parent's Debug.
            Assert.Contains("SIBLING_CFG=Release", output);
            Assert.Contains("SIBLING_TFM=net10.0", output);
            Assert.DoesNotContain("SIBLING_CFG=Debug", output);
        }

        // ── SwiftUI bridge -F search path must mirror the wrapper's (include BOTH the resolved
        //    ProjectReference xcframeworks AND every explicit SwiftFrameworkDependency). ──

        [Fact]
        public void CompileSwiftUIBridge_IncludesFrameworkDependencyAlongsideProjectRefDep()
        {
            // A bare SwiftFrameworkDependency (a framework with no binding project) must still
            // reach the bridge compile even when a ProjectReference dep (_ResolvedDepXCFramework)
            // is ALSO present. Previously it was dropped whenever _ResolvedDepXCFramework was
            // non-empty, failing bridge compilation (SWIFTBIND052) so bridge views threw
            // DllNotFound. The bridge now mirrors _CompileSwiftWrapper exactly.
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            var stubDir = StubGeneratorDir.Value;
            SkipUnless(stubDir != null, "Could not build stub generator DLL");

            var bindingDir = Path.Combine(_tempDir, "Bridge.Swift.iOS");
            Directory.CreateDirectory(bindingDir);
            var intermediateDir = Path.Combine(bindingDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);
            var sourceXcfw = Path.Combine(bindingDir, "Bridged.xcframework");
            Directory.CreateDirectory(sourceXcfw);

            // Bridge skip is keyed on _SwiftBindingHasBridgeSwift (peeked from props by the target).
            var metadataProps = """
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingHasBridgeSwift>True</_SwiftBindingHasBridgeSwift>
                    <_SwiftBindingBridgeModuleName>BridgedBridge</_SwiftBindingBridgeModuleName>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(intermediateDir, "binding-metadata.props"), metadataProps);

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            var resolvedDep = Path.Combine(_tempDir, "ResolvedSibling.xcframework");
            var explicitDep = Path.Combine(_tempDir, "PaymentSdk3DS2.xcframework");

            // _ResolvedDepXCFramework is normally produced by _CompileSwiftWrapper; inject it
            // directly since we invoke the bridge target in isolation. SwiftFrameworkDependency is
            // the bare (no binding project) dep that the buggy condition used to drop.
            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <_SwiftBindingGeneratorDir>{stubDir}</_SwiftBindingGeneratorDir>
                  </PropertyGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingIntermediateDir>{intermediateDir}</_SwiftBindingIntermediateDir>
                  </PropertyGroup>
                  <ItemGroup>
                    <SwiftFramework Include="{sourceXcfw}" />
                    <SwiftFrameworkDependency Include="{explicitDep}" />
                    <_ResolvedDepXCFramework Include="{resolvedDep}" />
                  </ItemGroup>
                  <Target Name="_DiscoverSwiftFrameworks" />
                  <Target Name="_ImportSwiftBindingMetadata" />
                  <Target Name="_UpdateSwiftWrapperMetadata" />
                </Project>
                """;

            File.WriteAllText(Path.Combine(bindingDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(bindingDir, "Test.csproj")}\" -t:_CompileSwiftUIBridge -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"_CompileSwiftUIBridge failed.\nStdErr: {result.StdErr}\nStdOut: {result.StdOut}");

            var output = result.StdOut + "\n" + result.StdErr;
            Assert.Contains("STUB_RECEIVED_ARGS:", output);
            Assert.Contains("--compile-bridge-only", output);
            // Both deps must be on the bridge -F path simultaneously.
            Assert.Contains("ResolvedSibling.xcframework", output);
            Assert.Contains("PaymentSdk3DS2.xcframework", output);
        }

        // ── Author-declared SwiftLinkFramework/SwiftLinkLibrary must reach the WRAPPER compile
        //    (the pass that actually links), so a force-loaded static-archive source that depends
        //    on an autolink-hint-free system framework (static-archive-no-autolink shape) resolves. ──

        [Fact]
        public void CompileSwiftWrapper_ForwardsLinkFrameworkAndLibraryToGenerator()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            var stubDir = StubGeneratorDir.Value;
            SkipUnless(stubDir != null, "Could not build stub generator DLL");

            var bindingDir = Path.Combine(_tempDir, "Linked.Swift.iOS");
            Directory.CreateDirectory(bindingDir);
            var intermediateDir = Path.Combine(bindingDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);
            var sourceXcfw = Path.Combine(bindingDir, "Linked.xcframework");
            Directory.CreateDirectory(sourceXcfw);

            // The wrapper target's Condition keys off _SwiftBindingWrapperModuleName. MSBuild
            // evaluates a target Condition BEFORE its DependsOnTargets (_ImportSwiftBindingMetadata)
            // run, so in a full build the property is already project-global by the time the wrapper
            // target is reached. In this isolated invocation we set it as a project property (and in
            // binding-metadata.props, so the dependency's XmlPeek re-affirms the same value rather
            // than clobbering it to empty). No xcframework on disk => _SwiftWrapperSkip stays false,
            // so the wrapper Exec actually fires against the stub generator.
            var metadataProps = """
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingWrapperModuleName>LinkedSwiftBindings</_SwiftBindingWrapperModuleName>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(intermediateDir, "binding-metadata.props"), metadataProps);

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <_SwiftBindingGeneratorDir>{stubDir}</_SwiftBindingGeneratorDir>
                  </PropertyGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingIntermediateDir>{intermediateDir}</_SwiftBindingIntermediateDir>
                    <_SwiftBindingWrapperModuleName>LinkedSwiftBindings</_SwiftBindingWrapperModuleName>
                  </PropertyGroup>
                  <ItemGroup>
                    <SwiftFramework Include="{sourceXcfw}" />
                    <SwiftLinkFramework Include="CoreVideo" />
                    <SwiftLinkLibrary Include="c++" />
                  </ItemGroup>
                  <Target Name="_DiscoverSwiftFrameworks" />
                </Project>
                """;

            File.WriteAllText(Path.Combine(bindingDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(bindingDir, "Test.csproj")}\" -t:_CompileSwiftWrapper -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"_CompileSwiftWrapper failed.\nStdErr: {result.StdErr}\nStdOut: {result.StdOut}");

            var output = result.StdOut + "\n" + result.StdErr;
            // End-to-end: the wrapper compile actually executed and reached the generator. The
            // compile now runs under the obj-dir lock (scripts/compile-wrapper-locked.sh), which
            // executes the generator via a persisted command file rather than inline in the Exec.
            Assert.Contains("STUB_RECEIVED_ARGS:", output);
            Assert.Contains("--compile-wrapper-only", output);
            // The author-declared link flags reach that real wrapper-compile invocation. The stub
            // echoes its received argv (STUB_RECEIVED_ARGS: + space-joined args), so asserting on the
            // shell-parsed tokens proves the generator was actually CALLED with the flags — stronger
            // than inspecting the persisted command file (which is now per-context GUID-named and
            // removed by the lock script's exit trap).
            Assert.Contains("--link-framework CoreVideo", output);
            Assert.Contains("--link-library c++", output);
        }

        // ── Source-native-linkage is read solely by _ComputeSwiftBindingSourceXcframeworkInclusion
        //    (it self-peeks binding-metadata.props so the generator-free GetNativeManifest path can
        //    depend on it). Its read + absent→Dynamic default are covered by the
        //    ComputeSourceXcframeworkInclusion_* tests below; there is no separate import-side peek. ──

        // ── Source-xcframework inclusion decision (Gap 2): the single
        //    _ComputeSwiftBindingSourceXcframeworkInclusion target derives
        //    $(_SwiftBindingIncludeSourceXcframework) once, read by all three SDK consumers.
        //    The decision must be Static-AND-wrapper-on-disk for a drop; everything else keeps. ──

        [Fact]
        public void ComputeSourceXcframeworkInclusion_StaticWithWrapperOnDisk_DropsSource()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            var output = RunComputeInclusionDump(
                "<_SwiftBindingSourceNativeLinkage>Static</_SwiftBindingSourceNativeLinkage>",
                wrapperOnDisk: true);
            Assert.Contains("INCLUDE:false", output);
        }

        [Fact]
        public void ComputeSourceXcframeworkInclusion_StaticWithoutWrapper_KeepsSoleCarrier()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // No wrapper carrier on disk (compile failure / skipped pass): the static source is
            // the only native, so it must be kept or the binding ships with no carrier at all.
            var output = RunComputeInclusionDump(
                "<_SwiftBindingSourceNativeLinkage>Static</_SwiftBindingSourceNativeLinkage>",
                wrapperOnDisk: false);
            Assert.Contains("INCLUDE:true", output);
        }

        [Fact]
        public void ComputeSourceXcframeworkInclusion_DynamicWithWrapper_KeepsSource()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // A dynamic source is never force-loaded into the wrapper, so it is always referenced
            // even when a wrapper carrier exists.
            var output = RunComputeInclusionDump(
                "<_SwiftBindingSourceNativeLinkage>Dynamic</_SwiftBindingSourceNativeLinkage>",
                wrapperOnDisk: true);
            Assert.Contains("INCLUDE:true", output);
        }

        [Fact]
        public void ComputeSourceXcframeworkInclusion_AbsentLinkage_DefaultsToKeep()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // Pre-Gap-2 metadata omits the linkage property; the compute target must default it
            // to Dynamic (keep the source) — the conservative, never-drop default.
            var output = RunComputeInclusionDump(linkageNode: "", wrapperOnDisk: true);
            Assert.Contains("INCLUDE:true", output);
        }

        // ── End-to-end wiring: the compute tests above verify the PROPERTY value; these verify
        //    _ResolveSwiftNativeReferences actually GATES the source NativeReference item on it,
        //    so the source ref drops/keeps in lockstep with the decision (not just the property). ──

        [Fact]
        public void ResolveNativeReferences_StaticWithWrapperOnDisk_DropsSourceNativeReference()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // Static archive + wrapper carrier on disk: the source NativeReference must NOT be
            // injected (the wrapper force-loads it and is the sole carrier).
            var (nref, exitCode) = RunResolveNativeReferencesDump(wrapperOnDisk: true);
            Assert.True(exitCode == 0, $"_ResolveSwiftNativeReferences failed.\nOutput: {nref}");
            Assert.DoesNotContain("Mixed.xcframework", nref);
            Assert.Contains("MixedSwiftBindings.xcframework", nref);
        }

        [Fact]
        public void ResolveNativeReferences_StaticWithoutWrapper_KeepsSourceNativeReference()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // No wrapper on disk (compile soft-failed / skipped): the static source is the sole
            // carrier and the source NativeReference must be injected, or the consumer links no
            // native at all.
            var (nref, exitCode) = RunResolveNativeReferencesDump(wrapperOnDisk: false);
            Assert.True(exitCode == 0, $"_ResolveSwiftNativeReferences failed.\nOutput: {nref}");
            Assert.Contains("Mixed.xcframework", nref);
            Assert.DoesNotContain("MixedSwiftBindings.xcframework", nref);
        }

        [Fact]
        public void ResolveNativeReferences_SourceDroppedButWrapperMetadataFalse_FailsClosedSWIFTBIND040()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // The source-drop/wrapper-ref divergence: _ComputeSwiftBindingSourceXcframeworkInclusion
            // drops the source NativeReference off live disk (Static linkage + a wrapper dir present),
            // but the wrapper NativeReference is gated on the PERSISTED _SwiftBindingHasWrapperXCFramework
            // metadata — recorded False here (a metadata-refresh gap against the stale wrapper dir). The
            // two readers disagree, so BOTH the source and the wrapper are skipped and no native carrier
            // is referenced. _ResolveSwiftNativeReferences must fail closed with SWIFTBIND040 rather than
            // build an SDK-direct app that DllNotFoundExceptions at runtime (the non-pack sibling of the
            // pack-time SWIFTBIND040).
            var (output, exitCode) = RunResolveNativeReferencesDump(
                wrapperOnDisk: true, hasWrapperMetadata: "False");

            Assert.True(exitCode != 0, $"Expected SWIFTBIND040 to fail the build.\nOutput: {output}");
            Assert.Contains("SWIFTBIND040", output);
        }

        [Fact]
        public void GetNativeManifest_SourceDroppedButWrapperMetadataFalse_FailsClosedSWIFTBIND040()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // The path-c (ProjectReference) sibling of the _ResolveSwiftNativeReferences divergence:
            // GetNativeManifest drops the source off live disk (Static + wrapper dir present) but reads
            // the persisted _GNM_HasWrapper=False, so it flows NEITHER source NOR wrapper through the
            // manifest — a ProjectReference consumer would build with no native carrier and
            // DllNotFoundException at runtime. GetNativeManifest must fail closed with SWIFTBIND040.
            var (output, exitCode) = RunGetNativeManifestDump(
                wrapperOnDisk: true, hasWrapperMetadata: "False");

            Assert.True(exitCode != 0, $"Expected SWIFTBIND040 to fail GetNativeManifest.\nOutput: {output}");
            Assert.Contains("SWIFTBIND040", output);
        }

        [Fact]
        public void GetNativeManifest_SourceDroppedWithWrapperMetadataTrue_FlowsWrapperNoError()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // Good state (no divergence): source dropped (Static + wrapper on disk) AND the persisted
            // _GNM_HasWrapper=True, so the wrapper xcframework flows through the manifest as the sole
            // native carrier and the guard stays inert. Confirms SWIFTBIND040 does not misfire on the
            // healthy path-c case the manifest is built for.
            var (output, exitCode) = RunGetNativeManifestDump(
                wrapperOnDisk: true, hasWrapperMetadata: "True");

            Assert.True(exitCode == 0, $"GetNativeManifest should succeed in the good state.\nOutput: {output}");
            Assert.DoesNotContain("SWIFTBIND040", output);
            Assert.Contains("MixedSwiftBindings.xcframework", output);
            Assert.DoesNotContain("Mixed.xcframework\n", output.Replace("MixedSwiftBindings.xcframework", "WRAPPER"));
        }

        // ── _BuildMixedObjCCompanion: when the source framework is Mixed, the SDK builds the
        //    emitted ObjC companion (Restore → Build → GetTargetPath) so its managed assembly
        //    can be EMBEDDED into the Swift binding's single nupkg (one xcframework → one
        //    package; no separate companion package, no nuspec <dependency>). These run the
        //    REAL target with a stub companion whose Restore/Build/GetTargetPath are markers
        //    (so no real NuGet restore/build is needed). ──

        [Fact]
        public void BuildMixedObjCCompanion_MixedFramework_RestoresThenBuildsCompanion()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // Mixed + companion present: the target must Restore the companion (it is generated
            // during build, so it is not in the parent's restore graph) BEFORE Build, then
            // GetTargetPath to capture the assembly to embed.
            var (output, exitCode) = RunBuildMixedObjCCompanionDump(
                frameworkType: "Mixed", companionPresent: true);

            Assert.True(exitCode == 0, $"_BuildMixedObjCCompanion failed.\nOutput: {output}");
            Assert.Contains("COMPANION_RESTORE", output);
            Assert.Contains("COMPANION_BUILD", output);
            Assert.Contains("COMPANION_GETTARGETPATH", output);
            // Restore must precede Build — the whole point of the explicit restore.
            Assert.True(
                output.IndexOf("COMPANION_RESTORE", StringComparison.Ordinal)
                    < output.IndexOf("COMPANION_BUILD", StringComparison.Ordinal),
                $"Restore must run before Build.\nOutput: {output}");
        }

        [Fact]
        public void BuildMixedObjCCompanion_NonMixedFramework_DoesNotBuildCompanion()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // A pure-Swift binding has no companion. Even with a companion csproj present on
            // disk (defensive — stale artifact), the FrameworkType gate must keep the build
            // from firing so a non-mixed binding never builds a spurious companion.
            var (output, exitCode) = RunBuildMixedObjCCompanionDump(
                frameworkType: "Swift", companionPresent: true);

            Assert.True(exitCode == 0, $"_BuildMixedObjCCompanion failed.\nOutput: {output}");
            Assert.DoesNotContain("COMPANION_RESTORE", output);
            Assert.DoesNotContain("COMPANION_BUILD", output);
        }

        [Fact]
        public void BuildMixedObjCCompanion_MixedButCompanionMissing_IsCleanNoOp()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // Metadata says Mixed and names a companion, but the csproj is absent: this build
            // hook is a clean no-op (the fail-closed guard lives at pack time in
            // _ConfigureSwiftBindingPack as SWIFTBIND039, where a missing companion would
            // otherwise ship an ObjC-less package). It must not hard-fail an ordinary build.
            var (output, exitCode) = RunBuildMixedObjCCompanionDump(
                frameworkType: "Mixed", companionPresent: false);

            Assert.True(exitCode == 0, $"_BuildMixedObjCCompanion should no-op, not fail.\nOutput: {output}");
            Assert.DoesNotContain("COMPANION_RESTORE", output);
            Assert.DoesNotContain("COMPANION_BUILD", output);
        }

        [Fact]
        public void BuildMixedObjCCompanion_OuterNoBuild_CompanionBuildDoesNotInheritNoBuild()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // Regression (surfaced in SDK 0.14.0): `dotnet pack --no-build` sets NoBuild=true as a
            // GLOBAL property, which the <MSBuild> task forwards by default to the out-of-band
            // companion build. With Targets="Build", a real SDK companion's _CheckForBuildWithNoBuild
            // guard then raises NETSDK1085 and the package is never produced. _BuildMixedObjCCompanion
            // must neutralize the inherited NoBuild (Properties NoBuild=false) so the companion builds
            // regardless of how the outer command was launched. We assert on the value the companion's
            // Build actually receives — true here would trip NETSDK1085 in a real companion.
            var (output, exitCode) = RunBuildMixedObjCCompanionDump(
                frameworkType: "Mixed", companionPresent: true, noBuild: true);

            Assert.True(exitCode == 0, $"_BuildMixedObjCCompanion failed under outer NoBuild.\nOutput: {output}");
            Assert.Contains("COMPANION_BUILD:Config=", output);
            Assert.DoesNotContain("COMPANION_BUILD_NOBUILD:[true]", output);
            Assert.Contains("COMPANION_BUILD_NOBUILD:[false]", output);
        }

        [Fact]
        public void ConfigureSwiftBindingPack_MixedButNoCompanionCaptured_FailsClosedSWIFTBIND039()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // Pack-time fail-closed (the partner to _BuildMixedObjCCompanion's clean no-op at build):
            // metadata says Mixed and names a companion, but the companion csproj is absent so nothing
            // was captured to embed. _ConfigureSwiftBindingPack MUST raise SWIFTBIND039 rather than
            // silently ship a Swift-only package whose consumers hit TypeLoadException on the ObjC types.
            var (output, exitCode) = RunConfigurePackDump(
                frameworkType: "Mixed", companionPresent: false);

            Assert.True(exitCode != 0, $"Expected SWIFTBIND039 to fail the pack.\nOutput: {output}");
            Assert.Contains("SWIFTBIND039", output);
        }

        [Fact]
        public void ConfigureSwiftBindingPack_MixedWithCompanionCaptured_DoesNotFailClosed()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // Happy path: a companion csproj is present, so _BuildMixedObjCCompanion captures its
            // assembly (GetTargetPath) and SWIFTBIND039's "nothing captured" condition is false.
            // Pack proceeds — the guard must not misfire when the companion was embedded.
            var (output, exitCode) = RunConfigurePackDump(
                frameworkType: "Mixed", companionPresent: true);

            Assert.True(exitCode == 0, $"Pack should not fail when the companion was captured.\nOutput: {output}");
            Assert.DoesNotContain("SWIFTBIND039", output);
        }

        [Fact]
        public void ConfigureSwiftBindingPack_SwiftOnly_DoesNotFailClosed()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // A pure-Swift binding has no companion and FrameworkType != Mixed, so SWIFTBIND039's
            // guard is inert. Pack must proceed even though no companion was captured.
            var (output, exitCode) = RunConfigurePackDump(
                frameworkType: "Swift", companionPresent: false);

            Assert.True(exitCode == 0, $"Swift-only pack must not trip the mixed guard.\nOutput: {output}");
            Assert.DoesNotContain("SWIFTBIND039", output);
        }

        [Fact]
        public void ConfigureSwiftBindingPack_SourceDroppedButWrapperMetadataFalse_FailsClosedSWIFTBIND040()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // Pack-time source-drop/wrapper divergence: the source xcframework was dropped
            // (_SwiftBindingIncludeSourceXcframework=false, the static-source-with-wrapper decision)
            // but the persisted _SwiftBindingHasWrapperXCFramework is False, so the wrapper is NOT
            // packed either — the nupkg would ship with no native payload. SWIFTBIND038 cannot catch
            // this (it is gated on HasWrapper=='True'), so _ConfigureSwiftBindingPack must fail closed
            // with SWIFTBIND040. Swift-only + no companion keeps SWIFTBIND039 inert so the failure is
            // unambiguously the native-carrier guard.
            var (output, exitCode) = RunConfigurePackDump(
                frameworkType: "Swift", companionPresent: false, includeSourceXcframework: "false");

            Assert.True(exitCode != 0, $"Expected SWIFTBIND040 to fail the pack.\nOutput: {output}");
            Assert.Contains("SWIFTBIND040", output);
            Assert.DoesNotContain("SWIFTBIND039", output);
        }

        // ── _ReferenceMixedObjCCompanion: an SDK-direct consumer (path b) IS the binding and
        //    compiles its own C# against the ObjC types, so it needs an explicit assembly
        //    Reference to the companion _BuildMixedObjCCompanion built out-of-band. A <Reference>
        //    (not a <ProjectReference>) never promotes to a nuspec <dependency>, so the
        //    one-xcframework → one-package contract is preserved. ──

        [Fact]
        public void ReferenceMixedObjCCompanion_MixedFramework_InjectsCompanionReference()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // Mixed + companion present: _BuildMixedObjCCompanion captures the companion assembly
            // and _ReferenceMixedObjCCompanion must inject it as a <Reference> so the SDK-direct
            // consumer's own C# sees the ObjC types (otherwise CS0246 / TypeLoadException).
            var (output, exitCode) = RunReferenceMixedObjCCompanionDump(
                frameworkType: "Mixed", companionPresent: true);

            Assert.True(exitCode == 0, $"_ReferenceMixedObjCCompanion failed.\nOutput: {output}");
            Assert.Contains("REF:", output);
            Assert.Contains("stub-companion.dll", output);
        }

        [Fact]
        public void ReferenceMixedObjCCompanion_NonMixedFramework_InjectsNothing()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // A pure-Swift binding has no companion: nothing was captured, so the gated ItemGroup
            // must stay inert and inject no companion Reference.
            var (output, exitCode) = RunReferenceMixedObjCCompanionDump(
                frameworkType: "Swift", companionPresent: true);

            Assert.True(exitCode == 0, $"_ReferenceMixedObjCCompanion failed.\nOutput: {output}");
            Assert.DoesNotContain("stub-companion.dll", output);
        }

        [Fact]
        public void ReferenceMixedObjCCompanion_NonPackableMixedNoCompanion_FailsClosedSWIFTBIND041()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // SDK-direct shape (path b): a non-packable project IS the binding and compiles its own C#
            // against the ObjC types, but the companion was not captured (csproj absent). There is no
            // later pack step to fail closed (SWIFTBIND039 only runs at pack), so the silent no-op would
            // surface as a confusing CS0246. _ReferenceMixedObjCCompanion must fail closed with
            // SWIFTBIND041 instead.
            var (output, exitCode) = RunReferenceMixedObjCCompanionDump(
                frameworkType: "Mixed", companionPresent: false, isPackable: "false");

            Assert.True(exitCode != 0, $"Expected SWIFTBIND041 to fail the build.\nOutput: {output}");
            Assert.Contains("SWIFTBIND041", output);
        }

        [Fact]
        public void ReferenceMixedObjCCompanion_PackableMixedNoCompanion_DefersToPack()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");
            // Packable binding (path a) with the same missing-companion state: SWIFTBIND041 is scoped to
            // non-packable projects, so the build must stay a clean no-op and defer the fail-closed to
            // pack time (SWIFTBIND039). This locks in that the new path-b guard does NOT regress the
            // documented build-no-op behavior for a packable mixed binding.
            var (output, exitCode) = RunReferenceMixedObjCCompanionDump(
                frameworkType: "Mixed", companionPresent: false, isPackable: "true");

            Assert.True(exitCode == 0, $"Packable build should no-op, not fail.\nOutput: {output}");
            Assert.DoesNotContain("SWIFTBIND041", output);
        }

        /// <summary>
        /// Runs the REAL _ComputeSwiftBindingSourceXcframeworkInclusion target against a
        /// binding-metadata.props carrying <paramref name="linkageNode"/>, optionally with the
        /// wrapper xcframework present on disk, and returns build output containing
        /// <c>INCLUDE:$(_SwiftBindingIncludeSourceXcframework)</c>.
        /// </summary>
        private string RunComputeInclusionDump(string linkageNode, bool wrapperOnDisk)
        {
            var bindingDir = Path.Combine(_tempDir, "Mixed.Swift.iOS");
            Directory.CreateDirectory(bindingDir);
            var intermediateDir = Path.Combine(bindingDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            // The compute target checks Exists($(_SwiftBindingIntermediateDir)<WrapperModule>.xcframework).
            if (wrapperOnDisk)
                Directory.CreateDirectory(Path.Combine(intermediateDir, "MixedSwiftBindings.xcframework"));

            var metadataProps = $"""
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>1.0.0</_SwiftBindingPackageVersion>
                    <_SwiftBindingMinimumOSVersion>15.0</_SwiftBindingMinimumOSVersion>
                    <_SwiftBindingModuleName>Mixed</_SwiftBindingModuleName>
                    <_SwiftBindingIsVersionPlaceholder>False</_SwiftBindingIsVersionPlaceholder>
                    <_SwiftBindingHasWrapperXCFramework>True</_SwiftBindingHasWrapperXCFramework>
                    <_SwiftBindingWrapperModuleName>MixedSwiftBindings</_SwiftBindingWrapperModuleName>
                    <_SwiftBindingWrapperSliceCount>1</_SwiftBindingWrapperSliceCount>
                    {linkageNode}
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(intermediateDir, "binding-metadata.props"), metadataProps);

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingIntermediateDir>{intermediateDir}</_SwiftBindingIntermediateDir>
                  </PropertyGroup>
                  <Target Name="TestDump" DependsOnTargets="_ComputeSwiftBindingSourceXcframeworkInclusion">
                    <Message Importance="High" Text="INCLUDE:$(_SwiftBindingIncludeSourceXcframework)" />
                  </Target>
                </Project>
                """;

            File.WriteAllText(Path.Combine(bindingDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(bindingDir, "Test.csproj")}\" -t:TestDump -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"_ComputeSwiftBindingSourceXcframeworkInclusion test failed.\nStdErr: {result.StdErr}\nStdOut: {result.StdOut}");
            return result.StdOut + "\n" + result.StdErr;
        }

        /// <summary>
        /// Runs the REAL _ResolveSwiftNativeReferences target (the live-disk SDK consumer of the
        /// inclusion decision) over a static-linkage binding with a single source xcframework, and
        /// returns the dumped <c>@(NativeReference)</c> identities. The generator/wrapper-compile
        /// dependency targets are stubbed empty so only _ComputeSwiftBindingSourceXcframeworkInclusion
        /// does real work; <paramref name="wrapperOnDisk"/> drives the carrier's presence.
        /// </summary>
        private (string Output, int ExitCode) RunResolveNativeReferencesDump(
            bool wrapperOnDisk,
            string hasWrapperMetadata = "True")
        {
            var bindingDir = Path.Combine(_tempDir, "MixedResolve.Swift.iOS");
            Directory.CreateDirectory(bindingDir);
            var intermediateDir = Path.Combine(bindingDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            var sourceXcfw = Path.Combine(bindingDir, "Mixed.xcframework");
            Directory.CreateDirectory(sourceXcfw);
            if (wrapperOnDisk)
                Directory.CreateDirectory(Path.Combine(intermediateDir, "MixedSwiftBindings.xcframework"));

            // The compute target self-peeks linkage + wrapper module name from this props file.
            var metadataProps = """
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingSourceNativeLinkage>Static</_SwiftBindingSourceNativeLinkage>
                    <_SwiftBindingWrapperModuleName>MixedSwiftBindings</_SwiftBindingWrapperModuleName>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(intermediateDir, "binding-metadata.props"), metadataProps);

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            // Stub the heavy DependsOnTargets of _ResolveSwiftNativeReferences so it runs without the
            // generator/wrapper compile; the source/wrapper metadata it reads is set directly here.
            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingIntermediateDir>{intermediateDir}</_SwiftBindingIntermediateDir>
                    <_SwiftBindingWrapperModuleName>MixedSwiftBindings</_SwiftBindingWrapperModuleName>
                    <_SwiftBindingHasWrapperXCFramework>{hasWrapperMetadata}</_SwiftBindingHasWrapperXCFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <SwiftFramework Include="{sourceXcfw}" />
                  </ItemGroup>
                  <!-- Stub the generator/validation deps so only _ResolveSwiftNativeReferences and
                       _ComputeSwiftBindingSourceXcframeworkInclusion do real work. Overriding
                       _ValidateSwiftPackageItems also drops its BeforeTargets hook, so the net10.0
                       TFM guard (SWIFTBIND010) does not fire in this non-Apple-TFM harness. -->
                  <Target Name="_ComputeSwiftFingerprint" />
                  <Target Name="_DiscoverSwiftFrameworks" />
                  <Target Name="_ValidateSwiftPackageItems" />
                  <Target Name="_GenerateSwiftBindings" />
                  <Target Name="_ImportSwiftBindingMetadata" />
                  <Target Name="_UpdateSwiftWrapperMetadata" />
                  <Target Name="_UpdateSwiftBridgeMetadata" />
                  <Target Name="TestDump" DependsOnTargets="_ResolveSwiftNativeReferences">
                    <Message Importance="High" Text="NREF:@(NativeReference)" />
                  </Target>
                </Project>
                """;

            File.WriteAllText(Path.Combine(bindingDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(bindingDir, "Test.csproj")}\" -t:TestDump -nologo -v:n");
            return (result.StdOut + "\n" + result.StdErr, result.ExitCode);
        }

        /// <summary>
        /// Runs the REAL GetNativeManifest target (the ProjectReference-consumer path, path c) via a
        /// TestDump that depends on it, then dumps the resolved <c>@(_SwiftBindingNativeManifest)</c>
        /// as <c>NMAN:</c> lines. Mirrors <see cref="RunResolveNativeReferencesDump"/> but exercises the
        /// distinct manifest path: GetNativeManifest peeks <c>_GNM_HasWrapper</c> from the PROPS file
        /// (not the project property), so <paramref name="hasWrapperMetadata"/> is written into
        /// binding-metadata.props here. Static linkage + <paramref name="wrapperOnDisk"/> drive
        /// _ComputeSwiftBindingSourceXcframeworkInclusion (the real DependsOnTarget) to drop the source.
        /// </summary>
        private (string Output, int ExitCode) RunGetNativeManifestDump(
            bool wrapperOnDisk,
            string hasWrapperMetadata = "True")
        {
            var bindingDir = Path.Combine(_tempDir, "GetManifest.Swift.iOS");
            Directory.CreateDirectory(bindingDir);
            var intermediateDir = Path.Combine(bindingDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            var sourceXcfw = Path.Combine(bindingDir, "Mixed.xcframework");
            Directory.CreateDirectory(sourceXcfw);
            if (wrapperOnDisk)
                Directory.CreateDirectory(Path.Combine(intermediateDir, "MixedSwiftBindings.xcframework"));

            // GetNativeManifest self-peeks linkage (via _ComputeSwiftBindingSourceXcframeworkInclusion),
            // wrapper module name, AND _SwiftBindingHasWrapperXCFramework all from this props file —
            // hasWrapperMetadata is the persisted signal the manifest's _GNM_HasWrapper reads, which is
            // exactly the half of the source-drop/wrapper divergence that the project property cannot fix.
            var metadataProps = $"""
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingSourceNativeLinkage>Static</_SwiftBindingSourceNativeLinkage>
                    <_SwiftBindingWrapperModuleName>MixedSwiftBindings</_SwiftBindingWrapperModuleName>
                    <_SwiftBindingHasWrapperXCFramework>{hasWrapperMetadata}</_SwiftBindingHasWrapperXCFramework>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(intermediateDir, "binding-metadata.props"), metadataProps);

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            // Stub _DiscoverSwiftFrameworks (GetNativeManifest's other DependsOnTarget) but let
            // _ComputeSwiftBindingSourceXcframeworkInclusion run for real so the source-drop decision
            // is the genuine on-disk one.
            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingIntermediateDir>{intermediateDir}</_SwiftBindingIntermediateDir>
                  </PropertyGroup>
                  <ItemGroup>
                    <SwiftFramework Include="{sourceXcfw}" />
                  </ItemGroup>
                  <Target Name="_ComputeSwiftFingerprint" />
                  <Target Name="_DiscoverSwiftFrameworks" />
                  <Target Name="_ValidateSwiftPackageItems" />
                  <Target Name="TestDump" DependsOnTargets="GetNativeManifest">
                    <Message Importance="High" Text="NMAN:@(_SwiftBindingNativeManifest)" />
                  </Target>
                </Project>
                """;

            File.WriteAllText(Path.Combine(bindingDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(bindingDir, "Test.csproj")}\" -t:TestDump -nologo -v:n");
            return (result.StdOut + "\n" + result.StdErr, result.ExitCode);
        }

        /// <summary>
        /// Runs the REAL _BuildMixedObjCCompanion target directly. A binding-metadata.props is
        /// planted with the given <paramref name="frameworkType"/> and an ObjC companion project
        /// name. When <paramref name="companionPresent"/> is true a STUB companion csproj is
        /// written at that name: a plain (SDK-less) project exposing marker <c>Restore</c>,
        /// <c>Build</c>, and <c>GetTargetPath</c> targets, so the <c>&lt;MSBuild&gt;</c> calls
        /// under test run without a real NuGet restore/build. Each marker echoes a token so the
        /// test can assert Restore-then-Build fired (and the assembly was captured). Returns
        /// build output + exit code.
        /// </summary>
        private (string Output, int ExitCode) RunBuildMixedObjCCompanionDump(
            string frameworkType,
            bool companionPresent,
            string objCProjectName = "Mixed.ObjC.iOS.csproj",
            bool noBuild = false)
        {
            var bindingDir = Path.Combine(_tempDir, "Mixed.Swift.iOS");
            Directory.CreateDirectory(bindingDir);
            var intermediateDir = Path.Combine(bindingDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            var metadataProps = $"""
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>1.0.0</_SwiftBindingPackageVersion>
                    <_SwiftBindingModuleName>Mixed</_SwiftBindingModuleName>
                    <_SwiftBindingFrameworkType>{frameworkType}</_SwiftBindingFrameworkType>
                    <_SwiftBindingObjCProjectName>{objCProjectName}</_SwiftBindingObjCProjectName>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(intermediateDir, "binding-metadata.props"), metadataProps);

            if (companionPresent)
            {
                // Stub companion: a plain (SDK-less) project exposing marker Restore/Build/
                // GetTargetPath. The real companion would `<Import>` Microsoft.NET.Sdk and run
                // NuGet restore + a real build; here we only need to prove the SDK target invokes
                // them (Restore before Build) and captures the assembly path via GetTargetPath, so
                // the markers echo back and GetTargetPath returns a stand-in assembly path.
                File.WriteAllText(Path.Combine(intermediateDir, objCProjectName), """
                    <Project>
                      <Target Name="Restore">
                        <Message Importance="high" Text="COMPANION_RESTORE:Config=$(Configuration)" />
                      </Target>
                      <Target Name="Build">
                        <Message Importance="high" Text="COMPANION_BUILD:Config=$(Configuration)" />
                        <!-- Echo the NoBuild the parent <MSBuild> task forwarded. A real SDK companion
                             would trip NETSDK1085 here if NoBuild=true rode in; this SDK-less stub can't
                             import that guard, so the test asserts on the propagated value instead. -->
                        <Message Importance="high" Text="COMPANION_BUILD_NOBUILD:[$(NoBuild)]" />
                      </Target>
                      <Target Name="GetTargetPath" Returns="@(_StubCompanionOutput)">
                        <Message Importance="high" Text="COMPANION_GETTARGETPATH" />
                        <ItemGroup>
                          <_StubCompanionOutput Include="$(MSBuildProjectDirectory)/stub-companion.dll" />
                        </ItemGroup>
                      </Target>
                    </Project>
                    """);
            }

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            // _SwiftBindingIntermediateDir is set AFTER the Sdk.targets import because
            // Sdk.targets redefines it from $(IntermediateOutputPath), which would overwrite us.
            // IsPackable=true models a packable Library binding (path a): _ReferenceMixedObjCCompanion
            // fires via AfterTargets even under a bare -t:_BuildMixedObjCCompanion, and its SWIFTBIND041
            // guard is scoped to non-packable (path b) projects. Setting it true here keeps the
            // "Mixed + companion absent" build a clean no-op that defers to pack-time SWIFTBIND039.
            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsPackable>true</IsPackable>
                  </PropertyGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingIntermediateDir>{intermediateDir}</_SwiftBindingIntermediateDir>
                  </PropertyGroup>
                </Project>
                """;

            File.WriteAllText(Path.Combine(bindingDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.targets"), "<Project />");

            // -p:NoBuild=true models `dotnet pack --no-build`, which sets NoBuild as a GLOBAL
            // property that the MSBuild task forwards to the out-of-band companion build.
            var noBuildArg = noBuild ? " -p:NoBuild=true" : "";
            var result = RunDotnet(
                $"msbuild \"{Path.Combine(bindingDir, "Test.csproj")}\" -t:_BuildMixedObjCCompanion -nologo -v:n{noBuildArg}");
            return (result.StdOut + "\n" + result.StdErr, result.ExitCode);
        }

        /// <summary>
        /// Runs the REAL _ReferenceMixedObjCCompanion target via a TestDump target that depends on
        /// _BuildMixedObjCCompanion (so the companion is built/captured first and the AfterTargets
        /// hook fires), then dumps the project-wide <c>@(Reference)</c> items as <c>REF:</c> lines.
        /// Uses the same stub companion as <see cref="RunBuildMixedObjCCompanionDump"/> (GetTargetPath
        /// returns stub-companion.dll), so a Mixed binding's dump contains the companion path and a
        /// non-mixed one does not. Returns build output + exit code.
        /// </summary>
        private (string Output, int ExitCode) RunReferenceMixedObjCCompanionDump(
            string frameworkType,
            bool companionPresent,
            string objCProjectName = "Mixed.ObjC.iOS.csproj",
            string isPackable = "true")
        {
            var bindingDir = Path.Combine(_tempDir, "RefMixed.Swift.iOS");
            Directory.CreateDirectory(bindingDir);
            var intermediateDir = Path.Combine(bindingDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            var metadataProps = $"""
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>1.0.0</_SwiftBindingPackageVersion>
                    <_SwiftBindingModuleName>Mixed</_SwiftBindingModuleName>
                    <_SwiftBindingFrameworkType>{frameworkType}</_SwiftBindingFrameworkType>
                    <_SwiftBindingObjCProjectName>{objCProjectName}</_SwiftBindingObjCProjectName>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(intermediateDir, "binding-metadata.props"), metadataProps);

            if (companionPresent)
            {
                // Same stub as the build-companion dump: marker Restore/Build and a GetTargetPath
                // that returns a stand-in assembly path captured into _SwiftBindingCompanionBuildOutput.
                File.WriteAllText(Path.Combine(intermediateDir, objCProjectName), """
                    <Project>
                      <Target Name="Restore" />
                      <Target Name="Build" />
                      <Target Name="GetTargetPath" Returns="@(_StubCompanionOutput)">
                        <ItemGroup>
                          <_StubCompanionOutput Include="$(MSBuildProjectDirectory)/stub-companion.dll" />
                        </ItemGroup>
                      </Target>
                    </Project>
                    """);
            }

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            // TestDump depends on _BuildMixedObjCCompanion so it runs first; its AfterTargets hook
            // (_ReferenceMixedObjCCompanion) then injects @(Reference) before TestDump's body dumps it.
            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsPackable>{isPackable}</IsPackable>
                  </PropertyGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingIntermediateDir>{intermediateDir}</_SwiftBindingIntermediateDir>
                  </PropertyGroup>
                  <Target Name="TestDump" DependsOnTargets="_BuildMixedObjCCompanion">
                    <Message Importance="high" Text="REF:%(Reference.Identity)" Condition="'@(Reference)' != ''" />
                  </Target>
                </Project>
                """;

            File.WriteAllText(Path.Combine(bindingDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet(
                $"msbuild \"{Path.Combine(bindingDir, "Test.csproj")}\" -t:TestDump -nologo -v:n");
            return (result.StdOut + "\n" + result.StdErr, result.ExitCode);
        }

        /// <summary>
        /// Runs the REAL _ConfigureSwiftBindingPack target (the pack-time inner-graph target that
        /// stages TfmSpecificPackageFile entries and hosts the SWIFTBIND039 fail-closed guard) with
        /// Mixed metadata. The heavy DependsOnTargets and BeforeTargets hooks (framework discovery,
        /// slicing, validation, generation) are stubbed empty so only _BuildMixedObjCCompanion —
        /// which captures the companion assembly, or not — and the guard logic do real work. When
        /// <paramref name="companionPresent"/> is false NO companion csproj is on disk, so nothing is
        /// captured to embed and SWIFTBIND039 must fire. _SwiftBindingHasWrapperXCFramework=False
        /// keeps the sibling SWIFTBIND038 guard from firing first and masking SWIFTBIND039.
        /// </summary>
        private (string Output, int ExitCode) RunConfigurePackDump(
            string frameworkType,
            bool companionPresent,
            string objCProjectName = "Mixed.ObjC.iOS.csproj",
            string includeSourceXcframework = "")
        {
            var bindingDir = Path.Combine(_tempDir, "MixedPack.Swift.iOS");
            Directory.CreateDirectory(bindingDir);
            var intermediateDir = Path.Combine(bindingDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            var metadataProps = $"""
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>1.0.0</_SwiftBindingPackageVersion>
                    <_SwiftBindingModuleName>Mixed</_SwiftBindingModuleName>
                    <_SwiftBindingFrameworkType>{frameworkType}</_SwiftBindingFrameworkType>
                    <_SwiftBindingObjCProjectName>{objCProjectName}</_SwiftBindingObjCProjectName>
                  </PropertyGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(intermediateDir, "binding-metadata.props"), metadataProps);

            if (companionPresent)
            {
                // Stub companion exposing the marker Restore/Build/GetTargetPath targets
                // _BuildMixedObjCCompanion drives; GetTargetPath returns a stand-in assembly path so
                // _SwiftBindingCompanionBuildOutput is populated (SWIFTBIND039's guard sees a capture).
                File.WriteAllText(Path.Combine(intermediateDir, objCProjectName), """
                    <Project>
                      <Target Name="Restore" />
                      <Target Name="Build" />
                      <Target Name="GetTargetPath" Returns="@(_StubCompanionOutput)">
                        <ItemGroup>
                          <_StubCompanionOutput Include="$(MSBuildProjectDirectory)/stub-companion.dll" />
                        </ItemGroup>
                      </Target>
                    </Project>
                    """);
            }

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            // _ConfigureSwiftBindingPack has three guards ahead of SWIFTBIND039 that this harness
            // must satisfy so the test reaches (and isolates) the mixed-companion guard:
            //  • SWIFTBIND035 (platform version): set _SwiftBindingPlatform to a value net10.0 does
            //    NOT end with, so TargetFramework.EndsWith(platform) is false and the guard is inert
            //    (keeps the harness on a plain net10.0 TFM — no Apple workload needed).
            //  • SWIFTBIND037 (managed output present): point TargetPath at a real file on disk.
            //  • SWIFTBIND038 (sibling native guard): _SwiftBindingHasWrapperXCFramework=False.
            var dummyTargetPath = Path.Combine(bindingDir, "dummy-output.dll");
            File.WriteAllText(dummyTargetPath, "stub-managed-assembly");

            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsPackable>true</IsPackable>
                  </PropertyGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingIntermediateDir>{intermediateDir}</_SwiftBindingIntermediateDir>
                    <_SwiftBindingHasWrapperXCFramework>False</_SwiftBindingHasWrapperXCFramework>
                    <_SwiftBindingPlatform>ios</_SwiftBindingPlatform>
                    <_SwiftBindingIncludeSourceXcframework>{includeSourceXcframework}</_SwiftBindingIncludeSourceXcframework>
                    <TargetPath>{dummyTargetPath}</TargetPath>
                  </PropertyGroup>
                  <!-- Stub everything in _ConfigureSwiftBindingPack's DependsOnTargets and BeforeTargets
                       graph EXCEPT _BuildMixedObjCCompanion, which must run to (not) capture the companion. -->
                  <Target Name="_ComputeSwiftFingerprint" />
                  <Target Name="_DiscoverSwiftFrameworks" />
                  <Target Name="_ImportSwiftBindingMetadata" />
                  <Target Name="_SetSlicePaths" />
                  <Target Name="_ComputeSwiftBindingSourceXcframeworkInclusion" />
                  <Target Name="_ValidateSwiftPackageItems" />
                  <Target Name="_ValidateSwiftDependencyMetadata" />
                  <Target Name="_ValidateSwiftBindingPackSlices" />
                  <Target Name="_SliceSourceXcframework" />
                  <Target Name="_GenerateSwiftBindings" />
                </Project>
                """;

            File.WriteAllText(Path.Combine(bindingDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(bindingDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet(
                $"msbuild \"{Path.Combine(bindingDir, "Test.csproj")}\" -t:_ConfigureSwiftBindingPack -nologo -v:n");
            return (result.StdOut + "\n" + result.StdErr, result.ExitCode);
        }

        /// <summary>
        /// Creates a minimal project that imports the real Sdk.targets and overrides
        /// targets that would fail without a real xcframework. The TestDump target
        /// dumps collected _SwiftModuleDatabaseFile items via Message tasks.
        /// </summary>
        private string CreateCollectionTestProject(
            string? swiftModuleDatabases = null,
            string? swiftFrameworkDependencies = null)
        {
            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            var smdItems = swiftModuleDatabases != null
                ? $"<ItemGroup>{swiftModuleDatabases}</ItemGroup>"
                : "";
            var sfdItems = swiftFrameworkDependencies != null
                ? $"<ItemGroup>{swiftFrameworkDependencies}</ItemGroup>"
                : "";

            return $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  {smdItems}
                  {sfdItems}
                  <Import Project="{sdkTargetsPath}" />
                  <Target Name="_ComputeSwiftFingerprint" />
                  <Target Name="_DiscoverSwiftFrameworks" />
                  <Target Name="_ValidateSwiftPackageItems" />
                  <Target Name="TestDump" DependsOnTargets="_CollectSwiftModuleDatabases">
                    <Message Importance="High" Text="DB_ITEM:%(_SwiftModuleDatabaseFile.Identity)" />
                  </Target>
                </Project>
                """;
        }

        private void WriteTestProject(string projectContent)
        {
            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), projectContent);
            // Prevent repo-level Directory.Build files from interfering
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");
        }

        // ── _InjectAppleSupplementPrototype: supplement signals reach the target via
        //    both binding-metadata.props (xcframework path, inlined) and apple-supplement.props
        //    (direct Apple-framework path, sibling file). XmlPeek reads raw XML and does NOT
        //    evaluate `<Import>`, so the target must query both files.

        [Fact]
        public void InjectAppleSupplementPrototype_ReadsFromBindingMetadataProps_XcframeworkPath()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var intermediateDir = Path.Combine(_tempDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            // Xcframework path: supplement signals are inlined directly into binding-metadata.props.
            File.WriteAllText(Path.Combine(intermediateDir, "binding-metadata.props"), """
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>1.0.0</_SwiftBindingPackageVersion>
                    <_SwiftBindingMinimumOSVersion>15.0</_SwiftBindingMinimumOSVersion>
                    <_SwiftBindingModuleName>TestModule</_SwiftBindingModuleName>
                    <_SwiftBindingIsVersionPlaceholder>False</_SwiftBindingIsVersionPlaceholder>
                    <_SwiftBindingHasWrapperXCFramework>False</_SwiftBindingHasWrapperXCFramework>
                    <_SwiftBindingWrapperModuleName>TestModuleSwiftBindings</_SwiftBindingWrapperModuleName>
                    <_SwiftBindingWrapperSliceCount>0</_SwiftBindingWrapperSliceCount>
                    <_SwiftBindingNeedsAppleSupplement>True</_SwiftBindingNeedsAppleSupplement>
                    <_SwiftBindingAppleSupplementVersion>26.0.0</_SwiftBindingAppleSupplementVersion>
                  </PropertyGroup>
                </Project>
                """);

            RunInjectSupplementTarget(intermediateDir, out var output, out var exitCode);

            Assert.True(exitCode == 0, $"Target failed.\nOutput: {output}");
            // Open-ended floor range: the supplement is cross-major additive-only so
            // diamond graphs across iOS majors unify at the higher supplement version.
            Assert.Contains("SUPPLEMENT_PKG:SwiftBindings.Apple|[26.0.0,)", output);
            // The target uses `Update=` to refine the props-side reference rather than
            // adding a second `Include=` (that would trip NU1504). Confirm exactly one
            // PackageReference for SwiftBindings.Apple survives.
            Assert.Equal(1, CountOccurrences(output, "SUPPLEMENT_PKG:SwiftBindings.Apple|"));
            // The placeholder version planted by RunInjectSupplementTarget (mirroring the
            // Sdk.props default) must have been overwritten by Update.
            Assert.DoesNotContain("SUPPLEMENT_PKG:SwiftBindings.Apple|[0.0.0-placeholder,)", output);
        }

        [Fact]
        public void InjectAppleSupplementPrototype_ReadsFromAppleSupplementProps_DirectPath()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var intermediateDir = Path.Combine(_tempDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            // Direct Apple-framework path: binding-metadata.props lacks supplement signals
            // (the heredoc in Sdk.targets has no visibility into generator state). Signals
            // live in a sibling apple-supplement.props. XmlPeek must fall back to it.
            File.WriteAllText(Path.Combine(intermediateDir, "binding-metadata.props"), """
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>1.0.0</_SwiftBindingPackageVersion>
                    <_SwiftBindingMinimumOSVersion>15.0</_SwiftBindingMinimumOSVersion>
                    <_SwiftBindingModuleName>TestModule</_SwiftBindingModuleName>
                    <_SwiftBindingIsVersionPlaceholder>False</_SwiftBindingIsVersionPlaceholder>
                    <_SwiftBindingHasWrapperXCFramework>False</_SwiftBindingHasWrapperXCFramework>
                    <_SwiftBindingWrapperModuleName>TestModuleSwiftBindings</_SwiftBindingWrapperModuleName>
                    <_SwiftBindingWrapperSliceCount>0</_SwiftBindingWrapperSliceCount>
                    <Import Project="apple-supplement.props" Condition="Exists('apple-supplement.props')" />
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(intermediateDir, "apple-supplement.props"), """
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingNeedsAppleSupplement>True</_SwiftBindingNeedsAppleSupplement>
                    <_SwiftBindingAppleSupplementVersion>26.0.0</_SwiftBindingAppleSupplementVersion>
                  </PropertyGroup>
                </Project>
                """);

            RunInjectSupplementTarget(intermediateDir, out var output, out var exitCode);

            Assert.True(exitCode == 0, $"Target failed.\nOutput: {output}");
            // Open-ended floor range: the supplement is cross-major additive-only so
            // diamond graphs across iOS majors unify at the higher supplement version.
            Assert.Contains("SUPPLEMENT_PKG:SwiftBindings.Apple|[26.0.0,)", output);
            // Same NU1504 guard as the xcframework-path test.
            Assert.Equal(1, CountOccurrences(output, "SUPPLEMENT_PKG:SwiftBindings.Apple|"));
            Assert.DoesNotContain("SUPPLEMENT_PKG:SwiftBindings.Apple|[0.0.0-placeholder,)", output);
        }

        [Fact]
        public void InjectAppleSupplementPrototype_NoSupplement_EmitsNoReference()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var intermediateDir = Path.Combine(_tempDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            // Consumer that resolved no supplement types: binding-metadata.props has no
            // supplement signals and apple-supplement.props either is absent or records
            // a comment body. Target must not inject any reference.
            File.WriteAllText(Path.Combine(intermediateDir, "binding-metadata.props"), """
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>1.0.0</_SwiftBindingPackageVersion>
                    <_SwiftBindingMinimumOSVersion>15.0</_SwiftBindingMinimumOSVersion>
                    <_SwiftBindingModuleName>TestModule</_SwiftBindingModuleName>
                    <_SwiftBindingIsVersionPlaceholder>False</_SwiftBindingIsVersionPlaceholder>
                    <_SwiftBindingHasWrapperXCFramework>False</_SwiftBindingHasWrapperXCFramework>
                    <_SwiftBindingWrapperModuleName>TestModuleSwiftBindings</_SwiftBindingWrapperModuleName>
                    <_SwiftBindingWrapperSliceCount>0</_SwiftBindingWrapperSliceCount>
                  </PropertyGroup>
                </Project>
                """);

            RunInjectSupplementTarget(intermediateDir, out var output, out var exitCode);

            Assert.True(exitCode == 0, $"Target failed.\nOutput: {output}");
            // No supplement signals: the target's Update guard fails, so the placeholder
            // PackageReference planted by RunInjectSupplementTarget (mirroring the props-side
            // default) survives untouched. We assert the placeholder version remains AND that
            // no refined "[26.0.0,)" reference appears.
            Assert.Contains("SUPPLEMENT_PKG:SwiftBindings.Apple|[0.0.0-placeholder,)", output);
            Assert.DoesNotContain("SUPPLEMENT_PKG:SwiftBindings.Apple|[26.0.0,)", output);
            // Still exactly one item — Update did not run, but it also did not duplicate.
            Assert.Equal(1, CountOccurrences(output, "SUPPLEMENT_PKG:SwiftBindings.Apple|"));
            Assert.DoesNotContain("SUPPLEMENT_PROJ:", output);
        }

        [Fact]
        public void InjectAppleSupplementPrototype_OptOut_NoReferenceInjected()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var intermediateDir = Path.Combine(_tempDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            // The supplement IS needed (generator detected types), but the consumer opted
            // out of the implicit SwiftBindings.Apple PackageReference (e.g. by setting
            // DisableImplicitSwiftAppleReference=true, or because SwiftFrameworkType=ObjC).
            // Because the targets-side now uses `Update=`, it must NOT re-add the reference
            // — opt-out wins by design. This locks in the behavior change vs. the previous
            // `Include=` form, which would have injected the reference regardless of opt-out.
            File.WriteAllText(Path.Combine(intermediateDir, "binding-metadata.props"), """
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>1.0.0</_SwiftBindingPackageVersion>
                    <_SwiftBindingMinimumOSVersion>15.0</_SwiftBindingMinimumOSVersion>
                    <_SwiftBindingModuleName>TestModule</_SwiftBindingModuleName>
                    <_SwiftBindingIsVersionPlaceholder>False</_SwiftBindingIsVersionPlaceholder>
                    <_SwiftBindingHasWrapperXCFramework>False</_SwiftBindingHasWrapperXCFramework>
                    <_SwiftBindingWrapperModuleName>TestModuleSwiftBindings</_SwiftBindingWrapperModuleName>
                    <_SwiftBindingWrapperSliceCount>0</_SwiftBindingWrapperSliceCount>
                    <_SwiftBindingNeedsAppleSupplement>True</_SwiftBindingNeedsAppleSupplement>
                    <_SwiftBindingAppleSupplementVersion>26.0.0</_SwiftBindingAppleSupplementVersion>
                  </PropertyGroup>
                </Project>
                """);

            // plantImplicitSwiftAppleReference: false models the props-side skip caused by
            // an opt-out; the target's `Update=` should be a no-op against an absent item.
            RunInjectSupplementTarget(intermediateDir, out var output, out var exitCode,
                plantImplicitSwiftAppleReference: false);

            Assert.True(exitCode == 0, $"Target failed.\nOutput: {output}");
            Assert.DoesNotContain("SUPPLEMENT_PKG:SwiftBindings.Apple", output);
            Assert.DoesNotContain("SUPPLEMENT_PROJ:", output);
        }

        [Fact]
        public void InjectAppleSupplementPrototype_OptOutWithManualReference_PreservesUserVersion()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var intermediateDir = Path.Combine(_tempDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            // Consumer set DisableImplicitSwiftAppleReference=true (so Sdk.props skipped its
            // implicit reference) and then manually pinned `SwiftBindings.Apple` to a sentinel
            // version. The targets-side `Update=` must respect the same opt-out conditions
            // as the props-side `Include=` — otherwise it would silently rewrite the user's
            // pinned version to the generator-detected floor.
            File.WriteAllText(Path.Combine(intermediateDir, "binding-metadata.props"), """
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>1.0.0</_SwiftBindingPackageVersion>
                    <_SwiftBindingMinimumOSVersion>15.0</_SwiftBindingMinimumOSVersion>
                    <_SwiftBindingModuleName>TestModule</_SwiftBindingModuleName>
                    <_SwiftBindingIsVersionPlaceholder>False</_SwiftBindingIsVersionPlaceholder>
                    <_SwiftBindingHasWrapperXCFramework>False</_SwiftBindingHasWrapperXCFramework>
                    <_SwiftBindingWrapperModuleName>TestModuleSwiftBindings</_SwiftBindingWrapperModuleName>
                    <_SwiftBindingWrapperSliceCount>0</_SwiftBindingWrapperSliceCount>
                    <_SwiftBindingNeedsAppleSupplement>True</_SwiftBindingNeedsAppleSupplement>
                    <_SwiftBindingAppleSupplementVersion>26.0.0</_SwiftBindingAppleSupplementVersion>
                  </PropertyGroup>
                </Project>
                """);

            RunInjectSupplementTarget(intermediateDir, out var output, out var exitCode,
                plantImplicitSwiftAppleReference: true,
                disableImplicitSwiftAppleReference: true,
                plantedReferenceVersion: "[5.0.0,)");

            Assert.True(exitCode == 0, $"Target failed.\nOutput: {output}");
            // The user's manually-pinned version must survive — Update was suppressed by the
            // opt-out condition. The generator-detected floor "[26.0.0,)" must NOT appear.
            Assert.Contains("SUPPLEMENT_PKG:SwiftBindings.Apple|[5.0.0,)", output);
            Assert.DoesNotContain("SUPPLEMENT_PKG:SwiftBindings.Apple|[26.0.0,)", output);
            Assert.Equal(1, CountOccurrences(output, "SUPPLEMENT_PKG:SwiftBindings.Apple|"));
        }

        // Regression: <PackageReference Update="X" Version="Y" /> inside a target body
        // (execution-time) does NOT actually filter to identity X — MSBuild applies the
        // Version metadata to every PackageReference in scope, including unrelated ones
        // (SwiftBindings.Runtime, the SDK-injected Microsoft.NET.ILLink.Tasks). The
        // resulting nuspec / dgspec then declares the wrong version range for those deps,
        // surfacing downstream as NU1605 package-downgrade restore failures (e.g. the
        // SwiftWrapperRequired=false libraries hit this once the
        // generator's binding-metadata.props existed on disk from a prior build).
        //
        // The fix is the per-item `Condition="'%(Identity)' == 'SwiftBindings.Apple'"` on
        // the Update line in `_InjectAppleSupplementPrototype`. This test asserts that an
        // unrelated sibling PackageReference (SwiftBindings.Runtime here, modelling the
        // implicit reference Sdk.props injects at evaluation time) survives the supplement
        // Update with its sentinel version intact.
        [Fact]
        public void InjectAppleSupplementPrototype_UpdateDoesNotStompUnrelatedPackageReferences()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var intermediateDir = Path.Combine(_tempDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            File.WriteAllText(Path.Combine(intermediateDir, "binding-metadata.props"), """
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>1.0.0</_SwiftBindingPackageVersion>
                    <_SwiftBindingMinimumOSVersion>15.0</_SwiftBindingMinimumOSVersion>
                    <_SwiftBindingModuleName>TestModule</_SwiftBindingModuleName>
                    <_SwiftBindingIsVersionPlaceholder>False</_SwiftBindingIsVersionPlaceholder>
                    <_SwiftBindingHasWrapperXCFramework>False</_SwiftBindingHasWrapperXCFramework>
                    <_SwiftBindingWrapperModuleName>TestModuleSwiftBindings</_SwiftBindingWrapperModuleName>
                    <_SwiftBindingWrapperSliceCount>0</_SwiftBindingWrapperSliceCount>
                    <_SwiftBindingNeedsAppleSupplement>True</_SwiftBindingNeedsAppleSupplement>
                    <_SwiftBindingAppleSupplementVersion>26.0.0</_SwiftBindingAppleSupplementVersion>
                  </PropertyGroup>
                </Project>
                """);

            // Plant a sibling SwiftBindings.Runtime PackageReference with a sentinel version
            // distinct from the supplement's [26.0.0,). If the Update bug regresses, the
            // sibling's Version will be rewritten to [26.0.0,) and the assertion below will
            // catch it.
            const string runtimeSentinel = "[0.0.0-runtime-placeholder,0.1.0)";
            RunInjectSupplementTarget(intermediateDir, out var output, out var exitCode,
                plantSiblingPackageReferenceWithVersion: runtimeSentinel);

            Assert.True(exitCode == 0, $"Target failed.\nOutput: {output}");
            // Apple ref refined as expected.
            Assert.Contains("SUPPLEMENT_PKG:SwiftBindings.Apple|[26.0.0,)", output);
            Assert.Equal(1, CountOccurrences(output, "SUPPLEMENT_PKG:SwiftBindings.Apple|"));
            // Runtime ref must keep its sentinel version — Update must not stomp it.
            Assert.Contains($"SIBLING_PKG:SwiftBindings.Runtime|{runtimeSentinel}", output);
            Assert.DoesNotContain("SIBLING_PKG:SwiftBindings.Runtime|[26.0.0,)", output);
            Assert.Equal(1, CountOccurrences(output, "SIBLING_PKG:SwiftBindings.Runtime|"));
        }

        // ── ObjC AppleFramework synthesis: _SynthesizeAppleFrameworkXcframework ──
        // Apple system frameworks like Matter ship as a single .framework per slice
        // under the SDK with a .tbd link stub and no Mach-O binary. xcodebuild
        // -create-xcframework refuses to package those, so the SDK hand-builds a
        // single-slice xcframework into $(IntermediateOutputPath). These tests
        // override _ResolveAppleFrameworkPaths to plant fake state and assert
        // the synthesis target produces the exact directory + plist layout that
        // XCFrameworkResolver later consumes. The contract is load-bearing for
        // issue #38 (SwiftBindings.Apple.Matter, SwiftBindings.Apple.MatterSupport).

        [Fact]
        public void SynthesizeAppleFrameworkXcframework_ObjcMode_BuildsSliceWithInfoPlist()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            // Plant a fake SDK framework dir: Foo.framework/Modules/module.modulemap + Foo.tbd.
            // This models the layout cp -R will copy from the real Xcode SDK.
            var fakeSdk = Path.Combine(_tempDir, "fake-sdk");
            var fakeFrameworkDir = Path.Combine(fakeSdk, "Foo.framework");
            Directory.CreateDirectory(Path.Combine(fakeFrameworkDir, "Modules"));
            Directory.CreateDirectory(Path.Combine(fakeFrameworkDir, "Headers"));
            File.WriteAllText(Path.Combine(fakeFrameworkDir, "Modules", "module.modulemap"),
                "framework module Foo {\n  umbrella header \"Foo.h\"\n}\n");
            File.WriteAllText(Path.Combine(fakeFrameworkDir, "Foo.tbd"), "--- !tapi-tbd");
            File.WriteAllText(Path.Combine(fakeFrameworkDir, "Headers", "Foo.h"), "// public header");

            var intermediateDir = Path.Combine(_tempDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            // The strategy: import real Sdk.targets, then override _ResolveAppleFrameworkPaths
            // and _DetectSwiftBindingTargetKind with stubs that bypass xcrun and plant the
            // resolved state directly (last-Target-wins replaces the SDK's definitions).
            // This isolates the synthesis target so we can test its directory/plist output
            // without needing a real Xcode SDK with the Matter framework installed.
            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <SwiftAppleFrameworkTarget Include="Foo" />
                  </ItemGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingIntermediateDir>{intermediateDir}</_SwiftBindingIntermediateDir>
                  </PropertyGroup>
                  <!-- Override the SDK's xcrun-dependent path resolution. We plant the
                       AppleFramework state directly so the synthesis target can run in
                       isolation against our fake framework dir. -->
                  <Target Name="_DetectSwiftBindingTargetKind">
                    <PropertyGroup>
                      <_SwiftBindingTargetKind>AppleFramework</_SwiftBindingTargetKind>
                      <_SwiftBindingPlatform>ios</_SwiftBindingPlatform>
                      <_SwiftBindingDeviceSliceId>ios-arm64</_SwiftBindingDeviceSliceId>
                      <_SwiftBindingSimulatorSliceId>ios-arm64-simulator</_SwiftBindingSimulatorSliceId>
                    </PropertyGroup>
                  </Target>
                  <Target Name="_ResolveAppleFrameworkPaths" DependsOnTargets="_DetectSwiftBindingTargetKind">
                    <PropertyGroup>
                      <_SwiftAppleFrameworkModule>Foo</_SwiftAppleFrameworkModule>
                      <_SwiftAppleFrameworkDir>{fakeFrameworkDir}</_SwiftAppleFrameworkDir>
                      <_SwiftAppleFrameworkModulemap>{fakeFrameworkDir}/Modules/module.modulemap</_SwiftAppleFrameworkModulemap>
                      <_SwiftAppleFrameworkType>ObjC</_SwiftAppleFrameworkType>
                    </PropertyGroup>
                  </Target>
                  <Target Name="_ComputeSwiftFingerprint" />
                </Project>
                """;

            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:_SynthesizeAppleFrameworkXcframework -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"_SynthesizeAppleFrameworkXcframework failed.\nStdErr: {result.StdErr}\nStdOut: {result.StdOut}");

            // The synthesized xcframework should appear under the intermediate dir
            // with the exact name convention the SDK's downstream targets expect.
            var synthXcfw = Path.Combine(intermediateDir, "Foo.xcframework");
            Assert.True(Directory.Exists(synthXcfw),
                $"Synthesized xcframework not created at {synthXcfw}");

            // Device slice dir (ios-arm64) must be present with the copied framework.
            // SDK uses _SwiftBindingDeviceSliceId="ios-arm64" for iOS device.
            var sliceDir = Path.Combine(synthXcfw, "ios-arm64");
            Assert.True(Directory.Exists(sliceDir), $"Slice dir not found at {sliceDir}");
            var copiedFramework = Path.Combine(sliceDir, "Foo.framework");
            Assert.True(Directory.Exists(copiedFramework),
                $"Copied Foo.framework missing under slice dir {sliceDir}");
            Assert.True(File.Exists(Path.Combine(copiedFramework, "Modules", "module.modulemap")),
                "modulemap not copied into synthesized slice");
            Assert.True(File.Exists(Path.Combine(copiedFramework, "Foo.tbd")),
                ".tbd link stub not copied into synthesized slice");

            // Info.plist must validate as an XCFramework Info.plist and parse via
            // XCFrameworkResolver — that's the actual downstream contract.
            var plistPath = Path.Combine(synthXcfw, "Info.plist");
            Assert.True(File.Exists(plistPath), "Synthesized Info.plist missing");
            var plistContent = File.ReadAllText(plistPath);
            Assert.Contains("AvailableLibraries", plistContent);
            Assert.Contains("<string>ios-arm64</string>", plistContent);
            Assert.Contains("<string>Foo.framework</string>", plistContent);
            Assert.Contains("<string>arm64</string>", plistContent);
            Assert.Contains("<string>ios</string>", plistContent);
            Assert.Contains("XFWK", plistContent);
            // SupportedPlatformVariant MUST be omitted for a plain device slice
            // — Apple's convention, and XCFrameworkResolver.SelectSlice treats
            // missing as device. A stray variant key would cause the device
            // path to be misclassified.
            Assert.DoesNotContain("SupportedPlatformVariant", plistContent);
        }

        [Fact]
        public void SynthesizeAppleFrameworkXcframework_ObjcMode_SimulatorEmitsVariant()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            // Mirror of the device test, but with SwiftPlatformTarget=simulator.
            // The plist MUST carry SupportedPlatformVariant=simulator so
            // XCFrameworkResolver routes through the simulator path.
            var fakeFrameworkDir = Path.Combine(_tempDir, "fake-sdk", "Foo.framework");
            Directory.CreateDirectory(Path.Combine(fakeFrameworkDir, "Modules"));
            File.WriteAllText(Path.Combine(fakeFrameworkDir, "Modules", "module.modulemap"),
                "framework module Foo {}\n");
            File.WriteAllText(Path.Combine(fakeFrameworkDir, "Foo.tbd"), "--- !tapi-tbd");

            var intermediateDir = Path.Combine(_tempDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <SwiftPlatformTarget>simulator</SwiftPlatformTarget>
                  </PropertyGroup>
                  <ItemGroup>
                    <SwiftAppleFrameworkTarget Include="Foo" />
                  </ItemGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingIntermediateDir>{intermediateDir}</_SwiftBindingIntermediateDir>
                  </PropertyGroup>
                  <Target Name="_DetectSwiftBindingTargetKind">
                    <PropertyGroup>
                      <_SwiftBindingTargetKind>AppleFramework</_SwiftBindingTargetKind>
                      <_SwiftBindingPlatform>ios</_SwiftBindingPlatform>
                      <_SwiftBindingDeviceSliceId>ios-arm64</_SwiftBindingDeviceSliceId>
                      <_SwiftBindingSimulatorSliceId>ios-arm64-simulator</_SwiftBindingSimulatorSliceId>
                    </PropertyGroup>
                  </Target>
                  <Target Name="_ResolveAppleFrameworkPaths" DependsOnTargets="_DetectSwiftBindingTargetKind">
                    <PropertyGroup>
                      <_SwiftAppleFrameworkModule>Foo</_SwiftAppleFrameworkModule>
                      <_SwiftAppleFrameworkDir>{fakeFrameworkDir}</_SwiftAppleFrameworkDir>
                      <_SwiftAppleFrameworkModulemap>{fakeFrameworkDir}/Modules/module.modulemap</_SwiftAppleFrameworkModulemap>
                      <_SwiftAppleFrameworkType>ObjC</_SwiftAppleFrameworkType>
                    </PropertyGroup>
                  </Target>
                  <Target Name="_ComputeSwiftFingerprint" />
                </Project>
                """;

            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:_SynthesizeAppleFrameworkXcframework -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"_SynthesizeAppleFrameworkXcframework (simulator) failed.\nStdErr: {result.StdErr}\nStdOut: {result.StdOut}");

            var synthXcfw = Path.Combine(intermediateDir, "Foo.xcframework");
            var sliceDir = Path.Combine(synthXcfw, "ios-arm64-simulator");
            Assert.True(Directory.Exists(sliceDir),
                $"Simulator slice dir not found at {sliceDir}");

            var plistContent = File.ReadAllText(Path.Combine(synthXcfw, "Info.plist"));
            Assert.Contains("<string>ios-arm64-simulator</string>", plistContent);
            // Simulator MUST emit SupportedPlatformVariant=simulator so
            // XCFrameworkResolver picks the simulator slice when requested.
            Assert.Contains("SupportedPlatformVariant", plistContent);
            Assert.Contains("<string>simulator</string>", plistContent);
        }

        [Fact]
        public void SynthesizeAppleFrameworkXcframework_SwiftMode_NoOps()
        {
            // When the framework is a Swift framework (e.g. CryptoKit), the
            // synthesis target's task-level conditions must all evaluate false
            // and produce NO xcframework. The Swift binding pipeline uses the
            // direct .swiftinterface path, not a synthesized xcframework.
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var intermediateDir = Path.Combine(_tempDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <SwiftAppleFrameworkTarget Include="Foo" />
                  </ItemGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingIntermediateDir>{intermediateDir}</_SwiftBindingIntermediateDir>
                  </PropertyGroup>
                  <Target Name="_DetectSwiftBindingTargetKind">
                    <PropertyGroup>
                      <_SwiftBindingTargetKind>AppleFramework</_SwiftBindingTargetKind>
                      <_SwiftBindingPlatform>ios</_SwiftBindingPlatform>
                      <_SwiftBindingDeviceSliceId>ios-arm64</_SwiftBindingDeviceSliceId>
                      <_SwiftBindingSimulatorSliceId>ios-arm64-simulator</_SwiftBindingSimulatorSliceId>
                    </PropertyGroup>
                  </Target>
                  <Target Name="_ResolveAppleFrameworkPaths" DependsOnTargets="_DetectSwiftBindingTargetKind">
                    <PropertyGroup>
                      <_SwiftAppleFrameworkModule>Foo</_SwiftAppleFrameworkModule>
                      <!-- Swift type — synthesis must be a no-op -->
                      <_SwiftAppleFrameworkType>Swift</_SwiftAppleFrameworkType>
                    </PropertyGroup>
                  </Target>
                  <Target Name="_ComputeSwiftFingerprint" />
                </Project>
                """;

            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:_SynthesizeAppleFrameworkXcframework -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"_SynthesizeAppleFrameworkXcframework (Swift no-op) failed.\nStdErr: {result.StdErr}\nStdOut: {result.StdOut}");

            // No synthesized xcframework should have been created.
            Assert.False(Directory.Exists(Path.Combine(intermediateDir, "Foo.xcframework")),
                "Synthesis target must NOT produce an xcframework for Swift-type AppleFramework mode");
        }

        [Fact]
        public void SynthesizeAppleFrameworkXcframework_ObjcMode_TvosSimulatorEmitsCorrectPlatform()
        {
            // Synthesis must emit the tvOS platform string in the plist when
            // _SwiftBindingPlatform=tvos, plus SupportedPlatformVariant=simulator
            // for SwiftPlatformTarget=simulator. The platform mapping at
            // Sdk.targets:365-368 has three branches (ios/maccatalyst, tvos, macos)
            // and the iOS path was the only one covered by behavioral tests until now.
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var fakeFrameworkDir = Path.Combine(_tempDir, "fake-sdk", "Foo.framework");
            Directory.CreateDirectory(Path.Combine(fakeFrameworkDir, "Modules"));
            File.WriteAllText(Path.Combine(fakeFrameworkDir, "Modules", "module.modulemap"),
                "framework module Foo {}\n");
            File.WriteAllText(Path.Combine(fakeFrameworkDir, "Foo.tbd"), "--- !tapi-tbd");

            var intermediateDir = Path.Combine(_tempDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <SwiftPlatformTarget>simulator</SwiftPlatformTarget>
                  </PropertyGroup>
                  <ItemGroup>
                    <SwiftAppleFrameworkTarget Include="Foo" />
                  </ItemGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingIntermediateDir>{intermediateDir}</_SwiftBindingIntermediateDir>
                  </PropertyGroup>
                  <Target Name="_DetectSwiftBindingTargetKind">
                    <PropertyGroup>
                      <_SwiftBindingTargetKind>AppleFramework</_SwiftBindingTargetKind>
                      <_SwiftBindingPlatform>tvos</_SwiftBindingPlatform>
                      <_SwiftBindingDeviceSliceId>tvos-arm64</_SwiftBindingDeviceSliceId>
                      <_SwiftBindingSimulatorSliceId>tvos-arm64-simulator</_SwiftBindingSimulatorSliceId>
                    </PropertyGroup>
                  </Target>
                  <Target Name="_ResolveAppleFrameworkPaths" DependsOnTargets="_DetectSwiftBindingTargetKind">
                    <PropertyGroup>
                      <_SwiftAppleFrameworkModule>Foo</_SwiftAppleFrameworkModule>
                      <_SwiftAppleFrameworkDir>{fakeFrameworkDir}</_SwiftAppleFrameworkDir>
                      <_SwiftAppleFrameworkModulemap>{fakeFrameworkDir}/Modules/module.modulemap</_SwiftAppleFrameworkModulemap>
                      <_SwiftAppleFrameworkType>ObjC</_SwiftAppleFrameworkType>
                    </PropertyGroup>
                  </Target>
                  <Target Name="_ComputeSwiftFingerprint" />
                </Project>
                """;

            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:_SynthesizeAppleFrameworkXcframework -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"_SynthesizeAppleFrameworkXcframework (tvOS simulator) failed.\nStdErr: {result.StdErr}\nStdOut: {result.StdOut}");

            var synthXcfw = Path.Combine(intermediateDir, "Foo.xcframework");
            var sliceDir = Path.Combine(synthXcfw, "tvos-arm64-simulator");
            Assert.True(Directory.Exists(sliceDir),
                $"tvOS simulator slice dir not found at {sliceDir}");

            var plistContent = File.ReadAllText(Path.Combine(synthXcfw, "Info.plist"));
            // tvos platform string (not ios) — checks the SwiftBindingPlatform mapping branch
            Assert.Contains("<string>tvos</string>", plistContent);
            Assert.DoesNotContain("<string>ios</string>", plistContent);
            // Simulator variant must still be emitted on tvOS
            Assert.Contains("SupportedPlatformVariant", plistContent);
            Assert.Contains("<string>simulator</string>", plistContent);
            Assert.Contains("<string>tvos-arm64-simulator</string>", plistContent);
        }

        // ── _EmitObjCAppleFrameworkLinkWith ──
        // ObjC AppleFramework mode has no Swift wrapper xcframework Mach-O to carry the
        // framework dependency through LC_LINKER_OPTION load commands. Without an explicit
        // signal, the consumer's mtouch/mlaunch native link step never adds `-framework Foo`
        // and the app build fails with "Undefined symbols: _MTRAttributePathKey, ...".
        // The SDK fix emits `[assembly: ObjCRuntime.LinkWithAttribute(Frameworks="Foo")]`
        // into the binding DLL so every consumer (direct or transitive) inherits the flag.
        //
        // A companion AssemblyMetadata("SwiftBindings.LinkWith.Module", "<Module>") is also
        // emitted as a cache-invalidation sentinel. The upstream
        // CreateGeneratedAssemblyInfoInputsCacheFile only hashes _Parameter1..8 of each
        // AssemblyAttribute, not named metadata like Frameworks — so without the sentinel
        // putting Module into _Parameter2, renaming Module on an existing project would
        // leave the previous Frameworks value baked into the generated AssemblyInfo on
        // incremental rebuilds. These tests assert both attributes are emitted, only for
        // ObjC mode, and carrying the right Module name.

        [Fact]
        public void EmitObjCAppleFrameworkLinkWith_ObjcMode_AddsLinkWithAttribute()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            // Plant ObjC AppleFramework state directly via stubbed resolver targets
            // (mirrors SynthesizeAppleFrameworkXcframework_ObjcMode_* tests), then run
            // a TestDump target that depends on _EmitObjCAppleFrameworkLinkWith and
            // echoes back @(AssemblyAttribute) items batched by Frameworks metadata.
            // The real _ResolveAppleFrameworkPaths defaults Module to %(Identity), so
            // the stub must do the same to model the real flow.
            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <SwiftAppleFrameworkTarget Include="Matter" />
                  </ItemGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <Target Name="_DetectSwiftBindingTargetKind">
                    <PropertyGroup>
                      <_SwiftBindingTargetKind>AppleFramework</_SwiftBindingTargetKind>
                    </PropertyGroup>
                  </Target>
                  <Target Name="_ResolveAppleFrameworkPaths" DependsOnTargets="_DetectSwiftBindingTargetKind">
                    <ItemGroup>
                      <SwiftAppleFrameworkTarget Update="@(SwiftAppleFrameworkTarget)">
                        <Module Condition="'%(SwiftAppleFrameworkTarget.Module)' == ''">%(Identity)</Module>
                      </SwiftAppleFrameworkTarget>
                    </ItemGroup>
                    <PropertyGroup>
                      <_SwiftAppleFrameworkType>ObjC</_SwiftAppleFrameworkType>
                    </PropertyGroup>
                  </Target>
                  <Target Name="TestDump" DependsOnTargets="_EmitObjCAppleFrameworkLinkWith">
                    <Message Importance="High" Text="LINKWITH:%(AssemblyAttribute.Identity)|Frameworks=%(AssemblyAttribute.Frameworks)" />
                    <Message Importance="High" Text="SENTINEL:%(AssemblyAttribute.Identity)|P1=%(AssemblyAttribute._Parameter1)|P2=%(AssemblyAttribute._Parameter2)" />
                  </Target>
                </Project>
                """;

            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:TestDump -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"TestDump failed.\nStdOut: {result.StdOut}\nStdErr: {result.StdErr}");

            // The SDK target must add the LinkWithAttribute item with Frameworks==module name…
            Assert.Contains("LINKWITH:ObjCRuntime.LinkWithAttribute|Frameworks=Matter", result.StdOut);
            // …and the cache-invalidation sentinel (AssemblyMetadata) carrying Module in _Parameter2,
            // so the upstream GenerateAssemblyInfo hash changes when Module changes.
            Assert.Contains("SENTINEL:System.Reflection.AssemblyMetadataAttribute|P1=SwiftBindings.LinkWith.Module|P2=Matter", result.StdOut);
        }

        [Fact]
        public void EmitObjCAppleFrameworkLinkWith_ObjcMode_HonorsCustomModuleMetadata()
        {
            // When the user explicitly overrides <Module> on SwiftAppleFrameworkTarget,
            // the LinkWith attribute must use the override, not Identity. This matters
            // for the rare case where Identity is a logical alias and Module is the actual
            // framework name on disk.
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <SwiftAppleFrameworkTarget Include="LogicalAlias">
                      <Module>RealFramework</Module>
                    </SwiftAppleFrameworkTarget>
                  </ItemGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <Target Name="_DetectSwiftBindingTargetKind">
                    <PropertyGroup>
                      <_SwiftBindingTargetKind>AppleFramework</_SwiftBindingTargetKind>
                    </PropertyGroup>
                  </Target>
                  <Target Name="_ResolveAppleFrameworkPaths" DependsOnTargets="_DetectSwiftBindingTargetKind">
                    <PropertyGroup>
                      <_SwiftAppleFrameworkType>ObjC</_SwiftAppleFrameworkType>
                    </PropertyGroup>
                  </Target>
                  <Target Name="TestDump" DependsOnTargets="_EmitObjCAppleFrameworkLinkWith">
                    <Message Importance="High" Text="LINKWITH:%(AssemblyAttribute.Identity)|Frameworks=%(AssemblyAttribute.Frameworks)" />
                    <Message Importance="High" Text="SENTINEL:%(AssemblyAttribute.Identity)|P1=%(AssemblyAttribute._Parameter1)|P2=%(AssemblyAttribute._Parameter2)" />
                  </Target>
                </Project>
                """;

            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:TestDump -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"TestDump failed.\nStdOut: {result.StdOut}\nStdErr: {result.StdErr}");

            Assert.Contains("LINKWITH:ObjCRuntime.LinkWithAttribute|Frameworks=RealFramework", result.StdOut);
            // Negative: Identity must NOT leak into Frameworks when an explicit Module is set.
            Assert.DoesNotContain("Frameworks=LogicalAlias", result.StdOut);
            // The cache-invalidation sentinel must also pick up the override, not Identity —
            // otherwise the upstream hash would be wrong-but-stable for projects that use
            // an Identity != Module pattern.
            Assert.Contains("SENTINEL:System.Reflection.AssemblyMetadataAttribute|P1=SwiftBindings.LinkWith.Module|P2=RealFramework", result.StdOut);
            Assert.DoesNotContain("P2=LogicalAlias", result.StdOut);
        }

        [Fact]
        public void EmitObjCAppleFrameworkLinkWith_SwiftMode_NoOps()
        {
            // Swift AppleFramework mode produces a wrapper xcframework whose Mach-O carries
            // the framework dependency via LC_LINKER_OPTION load commands. Emitting LinkWith
            // here would double-link the framework — at best harmless, at worst a duplicate
            // dependency warning. The target must early-out for Swift type.
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <SwiftAppleFrameworkTarget Include="CryptoKit" />
                  </ItemGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <Target Name="_DetectSwiftBindingTargetKind">
                    <PropertyGroup>
                      <_SwiftBindingTargetKind>AppleFramework</_SwiftBindingTargetKind>
                    </PropertyGroup>
                  </Target>
                  <Target Name="_ResolveAppleFrameworkPaths" DependsOnTargets="_DetectSwiftBindingTargetKind">
                    <PropertyGroup>
                      <_SwiftAppleFrameworkType>Swift</_SwiftAppleFrameworkType>
                    </PropertyGroup>
                  </Target>
                  <Target Name="TestDump" DependsOnTargets="_EmitObjCAppleFrameworkLinkWith">
                    <Message Importance="High" Text="LINKWITH:%(AssemblyAttribute.Identity)|Frameworks=%(AssemblyAttribute.Frameworks)" />
                    <Message Importance="High" Text="SENTINEL:%(AssemblyAttribute.Identity)|P1=%(AssemblyAttribute._Parameter1)|P2=%(AssemblyAttribute._Parameter2)" />
                  </Target>
                </Project>
                """;

            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:TestDump -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"TestDump failed.\nStdOut: {result.StdOut}\nStdErr: {result.StdErr}");

            // Neither the LinkWithAttribute nor its cache-invalidation sentinel may be emitted
            // in Swift AppleFramework mode — both belong to the ObjC-only fix.
            Assert.DoesNotContain("LinkWithAttribute", result.StdOut);
            Assert.DoesNotContain("SwiftBindings.LinkWith.Module", result.StdOut);
        }

        [Fact]
        public void EmitObjCAppleFrameworkLinkWith_XCFrameworkMode_NoOps()
        {
            // Non-AppleFramework projects (the normal xcframework binding flow) already get
            // their native dependencies via NativeReference items pointing at the wrapper
            // xcframework. Adding LinkWith there would be wrong — there's no system framework
            // to link, just the user's library inside the xcframework.
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <Import Project="{sdkTargetsPath}" />
                  <Target Name="_DetectSwiftBindingTargetKind" />
                  <Target Name="_ResolveAppleFrameworkPaths" DependsOnTargets="_DetectSwiftBindingTargetKind" />
                  <Target Name="TestDump" DependsOnTargets="_EmitObjCAppleFrameworkLinkWith">
                    <Message Importance="High" Text="LINKWITH:%(AssemblyAttribute.Identity)|Frameworks=%(AssemblyAttribute.Frameworks)" />
                    <Message Importance="High" Text="SENTINEL:%(AssemblyAttribute.Identity)|P1=%(AssemblyAttribute._Parameter1)|P2=%(AssemblyAttribute._Parameter2)" />
                  </Target>
                </Project>
                """;

            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:TestDump -nologo -v:n");
            Assert.True(result.ExitCode == 0,
                $"TestDump failed.\nStdOut: {result.StdOut}\nStdErr: {result.StdErr}");

            Assert.DoesNotContain("LinkWithAttribute", result.StdOut);
            // The cache-invalidation sentinel is also ObjC-only — must not appear here.
            Assert.DoesNotContain("SwiftBindings.LinkWith.Module", result.StdOut);
        }

        [Fact]
        public void EmitObjCAppleFrameworkLinkWith_CacheHashVariesWithModule()
        {
            // Upstream CreateGeneratedAssemblyInfoInputsCacheFile hashes @(AssemblyAttribute)
            // by `%(Identity)%(_Parameter1)…%(_Parameter8)` only — named metadata like
            // `Frameworks` is NOT in the hash. The cache-invalidation sentinel (an
            // AssemblyMetadata attribute with Module in _Parameter2) is what makes the
            // hash actually change when Module changes. This test runs MSBuild's Hash task
            // — the same task GenerateAssemblyInfo uses to build the cache key — against
            // the @(AssemblyAttribute) set produced for two different Module values, and
            // asserts the two hashes are not equal. If the sentinel were ever removed (or
            // the upstream cache contract changed and we updated the SDK to match), this
            // test would catch the silent reversion to "incremental rebuilds keep stale
            // Frameworks after a Module rename".
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            string MakeProject(string moduleName) => $$"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <SwiftAppleFrameworkTarget Include="{{moduleName}}" />
                  </ItemGroup>
                  <Import Project="{{sdkTargetsPath}}" />
                  <Target Name="_DetectSwiftBindingTargetKind">
                    <PropertyGroup>
                      <_SwiftBindingTargetKind>AppleFramework</_SwiftBindingTargetKind>
                    </PropertyGroup>
                  </Target>
                  <Target Name="_ResolveAppleFrameworkPaths" DependsOnTargets="_DetectSwiftBindingTargetKind">
                    <ItemGroup>
                      <SwiftAppleFrameworkTarget Update="@(SwiftAppleFrameworkTarget)">
                        <Module Condition="'%(SwiftAppleFrameworkTarget.Module)' == ''">%(Identity)</Module>
                      </SwiftAppleFrameworkTarget>
                    </ItemGroup>
                    <PropertyGroup>
                      <_SwiftAppleFrameworkType>ObjC</_SwiftAppleFrameworkType>
                    </PropertyGroup>
                  </Target>
                  <Target Name="HashAttrs" DependsOnTargets="_EmitObjCAppleFrameworkLinkWith">
                    <!-- Identical to the upstream CreateGeneratedAssemblyInfoInputsCacheFile expression. -->
                    <Hash ItemsToHash="@(AssemblyAttribute->'%(Identity)%(_Parameter1)%(_Parameter2)%(_Parameter3)%(_Parameter4)%(_Parameter5)%(_Parameter6)%(_Parameter7)%(_Parameter8)')">
                      <Output TaskParameter="HashResult" PropertyName="_AttrsHash" />
                    </Hash>
                    <Message Importance="High" Text="HASH:$(_AttrsHash)" />
                  </Target>
                </Project>
                """;

            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            // Build A: Module=Matter
            var projectA = Path.Combine(_tempDir, "TestA.csproj");
            File.WriteAllText(projectA, MakeProject("Matter"));
            var resultA = RunDotnet($"msbuild \"{projectA}\" -t:HashAttrs -nologo -v:n");
            Assert.True(resultA.ExitCode == 0, $"Build A failed: {resultA.StdOut}\n{resultA.StdErr}");

            // Build B: Module=MatterRenamed
            var projectB = Path.Combine(_tempDir, "TestB.csproj");
            File.WriteAllText(projectB, MakeProject("MatterRenamed"));
            var resultB = RunDotnet($"msbuild \"{projectB}\" -t:HashAttrs -nologo -v:n");
            Assert.True(resultB.ExitCode == 0, $"Build B failed: {resultB.StdOut}\n{resultB.StdErr}");

            // Extract the hash lines.
            string ExtractHash(string stdout)
            {
                var line = stdout.Split('\n').FirstOrDefault(l => l.Contains("HASH:"))
                    ?? throw new Xunit.Sdk.XunitException($"No HASH: line in output:\n{stdout}");
                return line.Substring(line.IndexOf("HASH:", StringComparison.Ordinal) + "HASH:".Length).Trim();
            }
            var hashA = ExtractHash(resultA.StdOut);
            var hashB = ExtractHash(resultB.StdOut);

            Assert.False(string.IsNullOrEmpty(hashA), "Empty hash for build A");
            Assert.False(string.IsNullOrEmpty(hashB), "Empty hash for build B");
            // The hashes must differ — proves the upstream cache key tracks Module changes.
            Assert.NotEqual(hashA, hashB);
        }

        // Regression guard against re-introducing custom-metadata-in-Condition patterns
        // anywhere in Sdk.props. The original failure: an ItemGroup at evaluation time
        // declared a PackageReference with a Condition that referenced qualified custom
        // metadata
        //   `Condition="'%(SwiftFrameworkDependency.PackageId)' != '' AND ..."`
        // which passes during normal restore but fails MSB4191 ("custom metadata not
        // allowed in this condition") under introspection paths like
        // `dotnet msbuild -getItem:PackageReference`, which IDE/CI tools invoke for
        // project introspection. The failure surfaces even when the project declares
        // no SwiftFrameworkDependency items (the inner Condition is parsed regardless
        // of the outer ItemGroup Condition's evaluation), which is why pure-binding
        // csprojs hit it. The buggy block has been removed entirely (it was dead code
        // for the typical `<Project Sdk="SwiftBindings.Sdk/...">` flow because Sdk.props
        // auto-imports before the project body); this test plants SwiftFrameworkDependency
        // items and runs `-getItem:PackageReference` to ensure no equivalent pattern
        // creeps back into Sdk.props or its imports.
        [Fact]
        public void SdkProps_GetItemPackageReference_DoesNotEmitMsb4191()
        {
            SkipUnless(MsbuildAvailable.Value, "dotnet msbuild not available");

            var sdkPropsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.props");

            // Manual import order: Microsoft.NET.Sdk first establishes evaluation
            // context, user SwiftFrameworkDependency items are declared in the project
            // body, then the repo Sdk.props is imported by path so it sees those items
            // (the inverse of real `Sdk="SwiftBindings.Sdk/..."` order, but irrelevant
            // for what this test asserts: MSB4191 fires regardless of whether the items
            // exist or are visible at the import point, so any ordering exercises the
            // bug. The benign MSB4011 "Microsoft.NET.Sdk Sdk.props re-imported" warning
            // is expected and ignored.)
            var project = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <SwiftFrameworkDependency Include="/path/A.xcframework"
                                              PackageId="My.PackageA"
                                              PackageVersion="1.2.3" />
                  </ItemGroup>
                  <Import Project="{sdkPropsPath}" />
                </Project>
                """;

            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -getItem:PackageReference -nologo");
            var combined = result.StdOut + "\n" + result.StdErr;

            Assert.True(result.ExitCode == 0,
                $"-getItem:PackageReference failed.\nStdOut: {result.StdOut}\nStdErr: {result.StdErr}");
            Assert.DoesNotContain("MSB4191", combined);
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

        private void RunInjectSupplementTarget(string intermediateDir, out string output, out int exitCode,
            bool plantImplicitSwiftAppleReference = true,
            bool disableImplicitSwiftAppleReference = false,
            string plantedReferenceVersion = "[0.0.0-placeholder,)",
            string? plantSiblingPackageReferenceWithVersion = null)
        {
            var sdkTargetsPath = Path.Combine(FindRepoRoot(),
                "src", "Swift.Bindings.Sdk", "Sdk", "Sdk.targets");

            // The placeholder PackageReference (when planted) mirrors the implicit
            // declaration in Sdk.props (line ~140): the real consumer flow declares
            // `PackageReference Include="SwiftBindings.Apple"` at evaluation time so NuGet
            // restore picks it up before targets run, and `_InjectAppleSupplementPrototype`
            // then refines its version metadata via `PackageReference Update=` once
            // generator metadata is available. This test fixture imports only the
            // Microsoft.NET.Sdk Sdk.props (not the Swift Sdk.props), so we plant the
            // placeholder here to model that pre-existing item — without it, the Update
            // would be a no-op and we'd be testing a non-representative scenario. The
            // sentinel version `[0.0.0-placeholder,)` lets assertions distinguish "Update
            // refined the version" (becomes `[26.0.0,)`) from "Update did not fire" (stays
            // `[0.0.0-placeholder,)`). Set plantImplicitSwiftAppleReference=false to model
            // the opt-out path (DisableImplicitSwiftAppleReference=true or
            // SwiftFrameworkType=ObjC) where Sdk.props skips the implicit Include.
            // To model "consumer opted out AND manually pinned a version", combine
            // plantImplicitSwiftAppleReference=true (their hand-rolled item),
            // disableImplicitSwiftAppleReference=true (suppresses the props-side Include),
            // and a distinct plantedReferenceVersion to detect rewrites.
            var implicitItemGroup = plantImplicitSwiftAppleReference
                ? $"""
                  <ItemGroup>
                    <PackageReference Include="SwiftBindings.Apple" Version="{plantedReferenceVersion}" />
                  </ItemGroup>
                """
                : "";
            // Optional sibling PackageReference. Used by tests that need to assert the
            // supplement Update only modifies SwiftBindings.Apple and leaves unrelated
            // PackageReferences (Runtime, ILLink.Tasks, …) untouched. The siblingmust
            // have a distinct sentinel version so a wildcard-matching Update — the
            // exact bug being regression-tested — would visibly stomp it.
            var siblingItemGroup = plantSiblingPackageReferenceWithVersion is { } siblingVersion
                ? $"""
                  <ItemGroup>
                    <PackageReference Include="SwiftBindings.Runtime" Version="{siblingVersion}" />
                  </ItemGroup>
                """
                : "";
            var optOutPropertyGroup = disableImplicitSwiftAppleReference
                ? """
                  <PropertyGroup>
                    <DisableImplicitSwiftAppleReference>true</DisableImplicitSwiftAppleReference>
                  </PropertyGroup>
                """
                : "";
            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                {optOutPropertyGroup}
                {implicitItemGroup}
                {siblingItemGroup}
                  <Import Project="{sdkTargetsPath}" />
                  <PropertyGroup>
                    <_SwiftBindingIntermediateDir>{intermediateDir}</_SwiftBindingIntermediateDir>
                  </PropertyGroup>
                  <Target Name="_ComputeSwiftFingerprint" />
                  <Target Name="_DiscoverSwiftFrameworks" />
                  <Target Name="_ValidateSwiftPackageItems" />
                  <Target Name="_GenerateSwiftBindings" />
                  <Target Name="_GenerateSwiftBindingsAppleFramework" />
                  <Target Name="TestDump" DependsOnTargets="_InjectAppleSupplementPrototype">
                    <Message Importance="High" Text="SUPPLEMENT_PKG:%(PackageReference.Identity)|%(PackageReference.Version)"
                             Condition="'%(PackageReference.Identity)' == 'SwiftBindings.Apple'" />
                    <Message Importance="High" Text="SIBLING_PKG:%(PackageReference.Identity)|%(PackageReference.Version)"
                             Condition="'%(PackageReference.Identity)' == 'SwiftBindings.Runtime'" />
                    <Message Importance="High" Text="SUPPLEMENT_PROJ:%(ProjectReference.Identity)"
                             Condition="$([System.String]::Copy('%(ProjectReference.Identity)').Contains('SwiftBindings.Apple'))" />
                  </Target>
                </Project>
                """;

            File.WriteAllText(Path.Combine(_tempDir, "Test.csproj"), project);
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.targets"), "<Project />");

            var result = RunDotnet($"msbuild \"{Path.Combine(_tempDir, "Test.csproj")}\" -t:TestDump -nologo -v:n");
            output = result.StdOut + "\n" + result.StdErr;
            exitCode = result.ExitCode;
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

        // ── Helpers ──

        /// <summary>
        /// Marks the test as skipped (via <see cref="Xunit.Sdk.SkipException"/>) when the
        /// condition is false. The xUnit runner (2.8+) reports these as "Skipped" rather than "Passed".
        /// </summary>
        private static void SkipUnless(bool condition, string reason)
        {
            if (!condition)
                throw Xunit.Sdk.SkipException.ForSkip(reason);
        }

        /// <summary>
        /// Runs the exact same find command used in Sdk.targets _DiscoverSwiftFrameworks.
        /// </summary>
        private static (int ExitCode, string StdOut, string StdErr) RunFind(string searchDir)
        {
            return RunShell($"find \"{searchDir}\" -maxdepth 1 -type d -name '*.xcframework' 2>/dev/null || true");
        }

        private static (int ExitCode, string StdOut, string StdErr) RunDotnet(string args)
        {
            return RunProcess("dotnet", args);
        }

        private static (int ExitCode, string StdOut, string StdErr) RunShell(string command)
        {
            return RunProcess("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"");
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
            process.WaitForExit(30_000);
            return (process.ExitCode, stdOut, stdErr);
        }
    }

    /// <summary>
    /// XML well-formedness smoke test for the shipped SDK files. MSBuild fails imports on
    /// malformed XML (e.g. "--" in a comment) with a generic error at the consumer end —
    /// an easy trap when hand-editing props/targets. This test loads both files through
    /// <see cref="System.Xml.Linq.XDocument.Load(string)"/> so the regression surfaces at
    /// unit-test time instead of inside a user's build.
    /// </summary>
    public class SdkFileWellFormednessTests
    {
        [Theory]
        [InlineData("Sdk.props")]
        [InlineData("Sdk.targets")]
        public void SdkFile_IsWellFormedXml(string fileName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SwiftBindings.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            var path = Path.Combine(
                dir!.FullName, "src", "Swift.Bindings.Sdk", "Sdk", fileName);
            Assert.True(File.Exists(path), $"SDK file not found at {path}");
            System.Xml.Linq.XDocument.Load(path);
        }
    }

    /// <summary>
    /// Drives the real <c>Sdk/scripts/compile-wrapper-locked.sh</c> — the obj-dir mutex that
    /// serializes wrapper-xcframework compilation across concurrent MSBuild ProjectInstances
    /// sharing one obj/.../swift-binding/ tree (the parallel fan-in "Stripe" shape). The bug it
    /// fixes: a second context observes the producer's EARLY-created partial .xcframework dir,
    /// skips its own compile, and validates the still-False binding-metadata.props → spurious
    /// SWIFTBIND051. These tests assert the lock's two guarantees — mutual exclusion, and an
    /// in-lock completeness recheck that makes followers no-op once a peer has published.
    /// </summary>
    public class CompileWrapperLockTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _script;

        public CompileWrapperLockTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "swiftbind-lock-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _script = Path.Combine(FindRepoRoot(), "src", "Swift.Bindings.Sdk", "Sdk", "scripts", "compile-wrapper-locked.sh");
            Assert.True(File.Exists(_script), $"lock script not found at {_script}");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
        }

        private string WriteProps(bool hasWrapper)
        {
            var props = Path.Combine(_tempDir, "binding-metadata.props");
            File.WriteAllText(props, $"""
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingHasWrapperXCFramework>{hasWrapper}</_SwiftBindingHasWrapperXCFramework>
                  </PropertyGroup>
                </Project>
                """);
            return props;
        }

        // A synthetic stand-in for the generator's --compile-wrapper-only invocation. Appends its
        // PID to a runs-log so concurrency is observable; optionally simulates a SUCCESSFUL compile
        // by creating the xcframework dir and flipping the props to True (what the real generator's
        // UpdateMetadataPropsWrapperStatus does), or a NON-completing one to exercise pure mutex.
        private string WriteCmdFile(string runsLog, string props, string xcfw, bool markComplete, double sleepSeconds)
        {
            var cmd = Path.Combine(_tempDir, "compile-wrapper-cmd-" + Guid.NewGuid().ToString("N") + ".sh");
            var complete = markComplete
                ? $"""
                   mkdir -p "{xcfw}"
                   printf '%s' '<Project><PropertyGroup><_SwiftBindingHasWrapperXCFramework>True</_SwiftBindingHasWrapperXCFramework></PropertyGroup></Project>' > "{props}"
                   """
                : "";
            File.WriteAllText(cmd, $"""
                #!/bin/bash
                echo "START $$" >> "{runsLog}"
                sleep {sleepSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}
                {complete}
                echo "END $$" >> "{runsLog}"
                """);
            return cmd;
        }

        private Process StartLock(string props, string xcfw, string cmdFile)
        {
            var lockBase = Path.Combine(_tempDir, "wrapper-compile.lock");
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(_script);
            psi.ArgumentList.Add(lockBase);
            psi.ArgumentList.Add(props);
            psi.ArgumentList.Add(xcfw);
            psi.ArgumentList.Add(cmdFile);
            return Process.Start(psi)!;
        }

        [Fact]
        public void ConcurrentContexts_OnlyOneCompiles_RestSkipAfterPeerPublishes()
        {
            // Fresh obj dir: no wrapper, props False. The first context to take the lock "compiles"
            // (marks complete); the others must observe the published wrapper and no-op.
            var props = WriteProps(hasWrapper: false);
            var xcfw = Path.Combine(_tempDir, "FooSwiftBindings.xcframework");
            var runs = Path.Combine(_tempDir, "runs.log");
            File.WriteAllText(runs, "");

            const int N = 5;
            var procs = Enumerable.Range(0, N)
                .Select(_ => StartLock(props, xcfw, WriteCmdFile(runs, props, xcfw, markComplete: true, sleepSeconds: 0.3)))
                .ToList();
            foreach (var p in procs) { p.WaitForExit(30_000); Assert.Equal(0, p.ExitCode); }

            // Exactly one context ran the (completing) compile; the rest skipped via the in-lock recheck.
            var startLines = File.ReadAllLines(runs).Count(l => l.StartsWith("START"));
            Assert.Equal(1, startLines);
            Assert.True(Directory.Exists(xcfw), "the single compile must have produced the wrapper");
        }

        [Fact]
        public void ConcurrentContexts_NonCompletingCompile_RunSerially_NeverOverlap()
        {
            // The compile never marks complete, so every context that takes the lock runs it. The
            // lock must still serialize them — no two run at once (asserted by non-interleaved
            // START/END pairs in the shared log).
            var props = WriteProps(hasWrapper: false);
            var xcfw = Path.Combine(_tempDir, "FooSwiftBindings.xcframework");
            var runs = Path.Combine(_tempDir, "runs.log");
            File.WriteAllText(runs, "");

            const int N = 4;
            var procs = Enumerable.Range(0, N)
                .Select(_ => StartLock(props, xcfw, WriteCmdFile(runs, props, xcfw, markComplete: false, sleepSeconds: 0.2)))
                .ToList();
            foreach (var p in procs) { p.WaitForExit(30_000); Assert.Equal(0, p.ExitCode); }

            var lines = File.ReadAllLines(runs).Where(l => l.Length > 0).ToList();
            Assert.Equal(N * 2, lines.Count);                 // every context ran (no recheck-skip)
            // Strict serialization: lines must alternate START,END,START,END … with each END
            // matching the immediately-preceding START's PID. Any overlap interleaves a second
            // START before the first END.
            for (int i = 0; i < lines.Count; i += 2)
            {
                Assert.StartsWith("START ", lines[i]);
                Assert.StartsWith("END ", lines[i + 1]);
                Assert.Equal(lines[i].Substring("START ".Length), lines[i + 1].Substring("END ".Length));
            }
        }

        [Fact]
        public void AlreadyComplete_SkipsCompile_WithoutRunningGenerator()
        {
            // Pre-published wrapper: props True + xcframework on disk. The lock body must no-op.
            var props = WriteProps(hasWrapper: true);
            var xcfw = Path.Combine(_tempDir, "FooSwiftBindings.xcframework");
            Directory.CreateDirectory(xcfw);
            var runs = Path.Combine(_tempDir, "runs.log");
            File.WriteAllText(runs, "");

            using var p = StartLock(props, xcfw, WriteCmdFile(runs, props, xcfw, markComplete: true, sleepSeconds: 0.0));
            p.WaitForExit(30_000);

            Assert.Equal(0, p.ExitCode);
            Assert.Equal("", File.ReadAllText(runs).Trim());   // generator never ran
        }

        [Fact]
        public void Incomplete_RunsGeneratorOnce_AndPropagatesExitCode()
        {
            // props False, no xcframework → not complete → the generator runs. A non-zero generator
            // exit must propagate (so MSBuild's ContinueOnError=WarnAndContinue + the downstream
            // SWIFTBIND051 validation see a real failure).
            var props = WriteProps(hasWrapper: false);
            var xcfw = Path.Combine(_tempDir, "FooSwiftBindings.xcframework");
            var cmd = Path.Combine(_tempDir, "failing-cmd.sh");
            File.WriteAllText(cmd, "#!/bin/bash\nexit 7\n");

            using var p = StartLock(props, xcfw, cmd);
            p.WaitForExit(30_000);

            Assert.Equal(7, p.ExitCode);
        }

        [Fact]
        public void WrongArgCount_FailsFast_DoesNotProceed()
        {
            // The Exec site hardcodes exactly four args; a drift to fewer must fail loudly rather
            // than run with empty positionals (LOCKDIR=".d", bash "" …) and silently misbehave.
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(_script);
            psi.ArgumentList.Add(Path.Combine(_tempDir, "wrapper-compile.lock"));
            psi.ArgumentList.Add(WriteProps(hasWrapper: false));   // only 2 of the 4 required args
            using var p = Process.Start(psi)!;
            p.WaitForExit(30_000);

            Assert.Equal(2, p.ExitCode);
        }

        [Fact]
        public void Release_DoesNotRemoveLock_WhenOwnershipChangedUnderUs()
        {
            // Ownership fence: if the lock is stolen and handed to a different live holder while we
            // were compiling, our cleanup() exit trap must NOT remove it (doing so would drop that
            // holder's lock and let a third context enter the critical section concurrently — Codex/Grok
            // review). Modelled by externally rewriting the lock's pid to a foreign value before we exit;
            // cleanup() compares on-disk pid to $$ and must leave the lockdir intact.
            var props = WriteProps(hasWrapper: false);
            var xcfw = Path.Combine(_tempDir, "FooSwiftBindings.xcframework");
            var runs = Path.Combine(_tempDir, "runs.log");
            File.WriteAllText(runs, "");
            var lockDir = Path.Combine(_tempDir, "wrapper-compile.lock") + ".d";
            var pidFile = Path.Combine(lockDir, "pid");

            // Non-completing compile with enough runtime to overwrite the pid mid-flight.
            using var p = StartLock(props, xcfw, WriteCmdFile(runs, props, xcfw, markComplete: false, sleepSeconds: 1.0));

            // Wait for the lock to be acquired (pid file present), then simulate the steal handing
            // ownership to a foreign holder by rewriting the pid to a value that is not our process.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(pidFile) && DateTime.UtcNow < deadline) { Thread.Sleep(20); }
            Assert.True(File.Exists(pidFile), "lock was never acquired");
            File.WriteAllText(pidFile, "2147483647");   // a pid the lock script's process never has

            p.WaitForExit(30_000);

            // The exit trap saw pid != $$ and left the (now foreign-owned) lockdir in place.
            Assert.True(Directory.Exists(lockDir),
                "ownership fence must not delete a lock that was stolen out from under us");
        }

        [Fact]
        public void DeadHolderLock_IsStolenAtomically_AndRecovers()
        {
            // A prior build was killed (SIGKILL) and left a lockdir whose stamped pid no longer
            // refers to a live process. A fresh context must reclaim it (via the atomic capture
            // steal) and go on to compile — not deadlock waiting on the abandoned holder.
            var props = WriteProps(hasWrapper: false);
            var xcfw = Path.Combine(_tempDir, "FooSwiftBindings.xcframework");
            var runs = Path.Combine(_tempDir, "runs.log");
            File.WriteAllText(runs, "");

            // Pre-seed an abandoned lock: pid that refers to no live process (out of macOS's
            // default pid range → kill -0 yields ESRCH → treated as dead).
            var lockDir = Path.Combine(_tempDir, "wrapper-compile.lock") + ".d";
            Directory.CreateDirectory(lockDir);
            File.WriteAllText(Path.Combine(lockDir, "pid"), "999999");

            using var p = StartLock(props, xcfw, WriteCmdFile(runs, props, xcfw, markComplete: true, sleepSeconds: 0.1));
            p.WaitForExit(30_000);

            Assert.Equal(0, p.ExitCode);
            Assert.Equal(1, File.ReadAllLines(runs).Count(l => l.StartsWith("START")));   // it compiled
            Assert.True(Directory.Exists(xcfw), "the recovered context must have produced the wrapper");
        }

        [Fact]
        public void StaleLockWithNoStampedPid_IsStolenAfterThreshold_AndRecovers()
        {
            // A holder died in the mkdir→pid-write window: a lockdir exists but no pid was ever
            // stamped. Once it ages past the staleness threshold, a fresh context must reclaim it
            // (the gated mtime steal) and compile. Backdate the dir's mtime so `find -mmin +20`
            // fires immediately rather than waiting 20 real minutes.
            var props = WriteProps(hasWrapper: false);
            var xcfw = Path.Combine(_tempDir, "FooSwiftBindings.xcframework");
            var runs = Path.Combine(_tempDir, "runs.log");
            File.WriteAllText(runs, "");

            var lockDir = Path.Combine(_tempDir, "wrapper-compile.lock") + ".d";
            Directory.CreateDirectory(lockDir);   // NO pid file written → empty holder
            Backdate(lockDir, "200001010000");    // long past STALE_MINUTES

            using var p = StartLock(props, xcfw, WriteCmdFile(runs, props, xcfw, markComplete: true, sleepSeconds: 0.1));
            p.WaitForExit(30_000);

            Assert.Equal(0, p.ExitCode);
            Assert.Equal(1, File.ReadAllLines(runs).Count(l => l.StartsWith("START")));
            Assert.True(Directory.Exists(xcfw), "the recovered context must have produced the wrapper");
        }

        [Fact]
        public void StaleLockWithLivePid_IsNotPreempted_EvenPastThreshold()
        {
            // The mtime gate must NEVER preempt a genuinely-live holder: an old lockdir whose stamped
            // pid is still alive is a slow compile, not an abandoned lock. Seed an old lockdir owned
            // by a real live process; a fresh context must keep waiting (no compile) until that holder
            // goes away — proving the stale steal does not fire on a non-empty live pid.
            var props = WriteProps(hasWrapper: false);
            var xcfw = Path.Combine(_tempDir, "FooSwiftBindings.xcframework");
            var runs = Path.Combine(_tempDir, "runs.log");
            File.WriteAllText(runs, "");

            // A real, live holder process (sleeps well past the test window).
            var holder = Process.Start(new ProcessStartInfo { FileName = "/bin/sleep", ArgumentList = { "30" }, UseShellExecute = false })!;
            try
            {
                var lockDir = Path.Combine(_tempDir, "wrapper-compile.lock") + ".d";
                Directory.CreateDirectory(lockDir);
                File.WriteAllText(Path.Combine(lockDir, "pid"), holder.Id.ToString());
                Backdate(lockDir, "200001010000");   // old mtime, but holder is alive

                using var p = StartLock(props, xcfw, WriteCmdFile(runs, props, xcfw, markComplete: true, sleepSeconds: 0.1));
                // Give the waiter ample time to (wrongly) steal if the gate were broken.
                bool exited = p.WaitForExit(4_000);

                Assert.False(exited, "the live holder must not be preempted — the waiter should still be blocking");
                Assert.Equal("", File.ReadAllText(runs).Trim());   // no compile ran

                // Now release the live holder; the waiter must then reclaim (dead-pid path) and finish.
                holder.Kill();
                holder.WaitForExit(5_000);
                Assert.True(p.WaitForExit(30_000), "waiter must proceed once the holder is gone");
                Assert.Equal(0, p.ExitCode);
                Assert.Equal(1, File.ReadAllLines(runs).Count(l => l.StartsWith("START")));
            }
            finally
            {
                try { if (!holder.HasExited) holder.Kill(); } catch { /* best effort */ }
            }
        }

        // Backdate a path's mtime via `touch -t [[CC]YY]MMDDhhmm` so `find -mmin +N` treats the lock
        // as stale without waiting real minutes.
        private static void Backdate(string path, string stamp)
        {
            using var t = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/touch",
                ArgumentList = { "-t", stamp, path },
                UseShellExecute = false,
            })!;
            t.WaitForExit(5_000);
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
