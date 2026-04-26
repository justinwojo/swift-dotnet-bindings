// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics;
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

        // ── Bug 1: find -type d discovers xcframework directories ──

        [Fact]
        public void FindTypeD_DiscoversXCFrameworkDirectory()
        {
            // Create a directory bundle (what xcframeworks actually are)
            var xcfwDir = Path.Combine(_tempDir, "Nuke.xcframework");
            Directory.CreateDirectory(xcfwDir);

            var result = RunFind(_tempDir);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Nuke.xcframework", result.StdOut);
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
            Directory.CreateDirectory(Path.Combine(_tempDir, "Nuke.xcframework"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "Lottie.xcframework"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "NotAnXCFW")); // should not match

            var result = RunFind(_tempDir);
            var lines = result.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(2, lines.Length);
            Assert.Contains(lines, l => l.Contains("Nuke.xcframework"));
            Assert.Contains(lines, l => l.Contains("Lottie.xcframework"));
        }

        // ── Bug 3: IntermediateOutputPath resolution ──

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
            var siblingDir = Path.Combine(_tempDir, "StripeCore.Swift.iOS");
            Directory.CreateDirectory(siblingDir);
            File.WriteAllText(Path.Combine(siblingDir, "StripeCore.Swift.iOS.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

            // Create fake xcframework inside a "binding project" directory
            var bindingDir = Path.Combine(_tempDir, "StripePaymentSheet.Swift.iOS");
            Directory.CreateDirectory(bindingDir);
            var fakeXcfw = Path.Combine(bindingDir, "StripePaymentSheet.xcframework");
            Directory.CreateDirectory(fakeXcfw);

            // Create binding-metadata.props with dependency pointing near the sibling
            var intermediateDir = Path.Combine(bindingDir, "obj", "swift-binding") + "/";
            Directory.CreateDirectory(intermediateDir);

            // The xcframework path is inside bindingDir, so grandparent is _tempDir,
            // and peer subdirectory search will find _tempDir/StripeCore.Swift.iOS/StripeCore.Swift.iOS.csproj
            var depsValue = $"StripeCore|StripeCore.Swift.iOS|25.6.2|{fakeXcfw}";
            var metadataProps = $"""
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>1.0.0</_SwiftBindingPackageVersion>
                    <_SwiftBindingMinimumOSVersion>15.0</_SwiftBindingMinimumOSVersion>
                    <_SwiftBindingModuleName>StripePaymentSheet</_SwiftBindingModuleName>
                    <_SwiftBindingIsVersionPlaceholder>False</_SwiftBindingIsVersionPlaceholder>
                    <_SwiftBindingHasWrapperXCFramework>False</_SwiftBindingHasWrapperXCFramework>
                    <_SwiftBindingWrapperModuleName>StripePaymentSheetSwiftBindings</_SwiftBindingWrapperModuleName>
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
            Assert.Contains("StripeCore.Swift.iOS.csproj", output);
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
            bool plantImplicitSwiftAppleReference = true)
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
            var implicitItemGroup = plantImplicitSwiftAppleReference
                ? """
                  <ItemGroup>
                    <PackageReference Include="SwiftBindings.Apple" Version="[0.0.0-placeholder,)" />
                  </ItemGroup>
                """
                : "";
            var project = $"""
                <Project>
                  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                {implicitItemGroup}
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
}
