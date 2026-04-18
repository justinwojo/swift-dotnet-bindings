// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Content validation tests for Sdk.props and Sdk.targets.
    /// These verify the MSBuild XML files contain the expected structure
    /// without needing to invoke MSBuild itself.
    /// </summary>
    public class SdkPropsContentTests
    {
        private static readonly string SdkDir = Path.Combine(
            FindRepoRoot(), "src", "Swift.Bindings.Sdk", "Sdk");

        private static readonly string PropsContent = File.ReadAllText(
            Path.Combine(SdkDir, "Sdk.props"));

        [Fact]
        public void Props_ImportsMicrosoftNetSdk()
        {
            Assert.Contains("Sdk=\"Microsoft.NET.Sdk\"", PropsContent);
        }

        [Fact]
        public void Props_DoesNotSetDefaultTargetFramework()
        {
            // No default TFM: consumers must declare TargetFramework or TargetFrameworks.
            // A default conflicts with multi-TFM projects because Sdk.props evaluates
            // before the project body where TargetFrameworks (plural) is set.
            Assert.DoesNotContain("<TargetFramework Condition", PropsContent);
        }

        [Fact]
        public void Props_SetsAllowUnsafeBlocks()
        {
            Assert.Contains("<AllowUnsafeBlocks>true</AllowUnsafeBlocks>", PropsContent);
        }

        [Fact]
        public void Props_IncludesSwiftRuntimeReference()
        {
            Assert.Contains("SwiftBindings.Runtime", PropsContent);
            // PackageReference uses the BOUNDED range, not the bare exact version.
            // A bare "0.8.0" would be interpreted by NuGet as a minimum-only float,
            // letting consumers silently slide into 0.9.0 where compatibility is not
            // guaranteed. The range pins the minor floor instead.
            Assert.Contains("$(SwiftRuntimePackageVersionRange)", PropsContent);
            Assert.DoesNotContain(
                "Version=\"$(SwiftRuntimeVersion)\"",
                PropsContent);
        }

        [Fact]
        public void Props_DefinesSwiftRuntimePackageVersionRange()
        {
            // Must be a bracket-bounded range (e.g. "[0.0.0-dev,0.1.0)") — a bare
            // version here would defeat the whole point of splitting it from
            // SwiftRuntimeVersion.
            Assert.Contains("<SwiftRuntimePackageVersionRange Condition=", PropsContent);
            Assert.Contains("</SwiftRuntimePackageVersionRange>", PropsContent);
        }

        [Fact]
        public void Props_SupportsDisableImplicitSwiftRuntimeReference()
        {
            Assert.Contains("DisableImplicitSwiftRuntimeReference", PropsContent);
        }

        [Fact]
        public void Props_DefaultsWrapperArchitecturesToAll()
        {
            Assert.Contains("<SwiftWrapperArchitectures Condition=", PropsContent);
            Assert.Contains(">all</SwiftWrapperArchitectures>", PropsContent);
        }

        [Fact]
        public void Props_DoesNotContainAutoDiscovery()
        {
            // Auto-discovery must be in .targets, not .props
            Assert.DoesNotContain("_DiscoverSwiftFrameworks", PropsContent);
            Assert.DoesNotContain("*.xcframework", PropsContent);
        }

        [Fact]
        public void Props_DefinesGeneratorDir()
        {
            Assert.Contains("_SwiftBindingGeneratorDir", PropsContent);
            Assert.Contains("tools/net10.0/any/", PropsContent);
        }

        [Fact]
        public void Props_DefinesSdkVersion()
        {
            Assert.Contains("_SwiftBindingSdkVersion", PropsContent);
        }

        [Fact]
        public void Props_DoesNotDefineIntermediateDir()
        {
            // _SwiftBindingIntermediateDir must be in .targets, not .props
            // ($(IntermediateOutputPath) is empty at props evaluation time)
            Assert.DoesNotContain("_SwiftBindingIntermediateDir", PropsContent);
        }

        [Fact]
        public void Props_AutoDetectsPlatformFromTfm()
        {
            Assert.Contains("_SwiftBindingPlatform", PropsContent);
            Assert.Contains("maccatalyst", PropsContent);
            Assert.Contains("tvos", PropsContent);
            Assert.Contains("macos", PropsContent);
            Assert.Contains("TargetFramework.Contains(", PropsContent);
        }

        [Fact]
        public void Props_DetectsMaccatalystBeforeMacos()
        {
            // maccatalyst must be checked before macos to avoid substring overlap
            var maccatalystIdx = PropsContent.IndexOf("Contains('maccatalyst')", StringComparison.Ordinal);
            var macosIdx = PropsContent.IndexOf("Contains('macos')", StringComparison.Ordinal);
            Assert.True(maccatalystIdx > 0);
            Assert.True(macosIdx > 0);
            Assert.True(maccatalystIdx < macosIdx, "maccatalyst detection must come before macos");
        }

        [Fact]
        public void Props_DetectsIosExplicitly()
        {
            // iOS detection uses Contains('ios') — not an unconditional fallback
            Assert.Contains("TargetFramework.Contains('ios')", PropsContent);
            Assert.Contains(">ios</_SwiftBindingPlatform>", PropsContent);
        }

        [Fact]
        public void Props_FlagsUnsupportedPlatform()
        {
            // Unsupported TFMs (e.g. net10.0, net10.0-android) should be flagged
            Assert.Contains("_SwiftBindingPlatformUnsupported", PropsContent);
        }

        [Fact]
        public void Props_DefinesNuGetRidPerPlatform()
        {
            Assert.Contains("_SwiftBindingNuGetRid", PropsContent);
            Assert.Contains("osx-arm64", PropsContent);
            Assert.Contains("tvos-arm64", PropsContent);
            Assert.Contains("maccatalyst-arm64", PropsContent);
            Assert.Contains("ios-arm64", PropsContent);
        }

        [Fact]
        public void Props_DefinesSliceIdsPerPlatform()
        {
            Assert.Contains("_SwiftBindingDeviceSliceId", PropsContent);
            Assert.Contains("macos-arm64", PropsContent);
            Assert.Contains("ios-arm64-maccatalyst", PropsContent);
        }

        [Fact]
        public void Props_DefinesSimulatorSliceForIosAndTvos()
        {
            Assert.Contains("_SwiftBindingSimulatorSliceId", PropsContent);
            Assert.Contains("_SwiftBindingHasSimulatorSlice", PropsContent);
            Assert.Contains("ios-arm64-simulator", PropsContent);
            Assert.Contains("tvos-arm64-simulator", PropsContent);
        }

        [Fact]
        public void Props_PlatformTargetConditionalOnSimulatorSlice()
        {
            // SwiftPlatformTarget should only default to 'simulator' for platforms with simulator slices
            Assert.Contains("_SwiftBindingHasSimulatorSlice", PropsContent);
            Assert.Contains(">simulator</SwiftPlatformTarget>", PropsContent);
        }

        [Fact]
        public void Props_InjectsStaticPackageReferenceForDependencies()
        {
            // SwiftFrameworkDependency items with PackageId + PackageVersion
            // should generate PackageReference at evaluation time
            Assert.Contains("SwiftFrameworkDependency", PropsContent);
            Assert.Contains("%(SwiftFrameworkDependency.PackageId)", PropsContent);
            Assert.Contains("%(SwiftFrameworkDependency.PackageVersion)", PropsContent);
        }

        [Fact]
        public void Props_DependencyPackageReference_RequiresBothMetadata()
        {
            // PackageReference should only be emitted when BOTH PackageId AND PackageVersion are present
            Assert.Contains("'%(SwiftFrameworkDependency.PackageId)' != ''", PropsContent);
            Assert.Contains("'%(SwiftFrameworkDependency.PackageVersion)' != ''", PropsContent);
        }

        [Fact]
        public void Props_DefaultsSwiftRuntimeVersion()
        {
            Assert.Contains("<SwiftRuntimeVersion Condition=", PropsContent);
            Assert.Contains("</SwiftRuntimeVersion>", PropsContent);
        }

        [Fact]
        public void Props_DefaultsSwiftGenerateDocComments()
        {
            Assert.Contains("<SwiftGenerateDocComments Condition=", PropsContent);
            Assert.Contains(">true</SwiftGenerateDocComments>", PropsContent);
        }

        private static string FindRepoRoot()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null)
            {
                var gitPath = Path.Combine(dir, ".git");
                // .git is a directory in normal repos, a file in worktrees
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("Cannot find repo root.");
        }
    }

    public class SdkTargetsContentTests
    {
        private static readonly string SdkDir = Path.Combine(
            FindRepoRoot(), "src", "Swift.Bindings.Sdk", "Sdk");

        private static readonly string TargetsContent = File.ReadAllText(
            Path.Combine(SdkDir, "Sdk.targets"));

        [Fact]
        public void Targets_ImportsMicrosoftNetSdkTargets()
        {
            Assert.Contains("Sdk=\"Microsoft.NET.Sdk\"", TargetsContent);
        }

        [Fact]
        public void Targets_ContainsAllTargets()
        {
            var expectedTargets = new[]
            {
                "_ValidateSwiftPackageItems",
                "_DiscoverSwiftFrameworks",
                "_ComputeSwiftFingerprint",
                "_CollectSwiftModuleDatabases",
                "_GenerateSwiftBindings",
                "_ImportSwiftBindingMetadata",
                "_ResolveSwiftAutoDetectedDependencies",
                "_IncludeGeneratedSwiftBindings",
                "_ResolveSwiftNativeReferences",
                "_ValidateSwiftDependencyMetadata",
                "_ValidateSwiftBindingPackSlices",
                "_ConfigureSwiftBindingPack",
                "GetSwiftFrameworkSearchPaths",
                "_ReportSwiftBindingCoverage",
                "_CompileSwiftWrapper",
                "_UpdateSwiftWrapperMetadata",
            };

            foreach (var target in expectedTargets)
            {
                Assert.Contains($"Name=\"{target}\"", TargetsContent);
            }
        }

        [Fact]
        public void Targets_HasSwiftBind001ErrorCode()
        {
            Assert.Contains("SWIFTBIND001", TargetsContent);
            Assert.Contains("No xcframework found", TargetsContent);
        }

        [Fact]
        public void Targets_HasSwiftBind002ErrorCode()
        {
            Assert.Contains("SWIFTBIND002", TargetsContent);
            Assert.Contains("The SDK supports one xcframework per project", TargetsContent);
        }

        [Fact]
        public void Targets_HasSwiftBind003ErrorCode()
        {
            Assert.Contains("SWIFTBIND003", TargetsContent);
            Assert.Contains("xcframework not found", TargetsContent);
        }

        [Fact]
        public void Targets_HasSwiftBind020WarningCode()
        {
            Assert.Contains("SWIFTBIND020", TargetsContent);
            Assert.Contains("placeholder", TargetsContent);
        }

        [Fact]
        public void Targets_HasSwiftBind030ErrorCode()
        {
            Assert.Contains("SWIFTBIND030", TargetsContent);
            Assert.Contains("NuGet packages require both", TargetsContent);
        }

        [Fact]
        public void Targets_HasSwiftBind031ErrorCode()
        {
            Assert.Contains("SWIFTBIND031", TargetsContent);
            Assert.Contains("missing device or simulator slice", TargetsContent);
        }

        [Fact]
        public void Targets_HasSwiftBind010ErrorCode()
        {
            Assert.Contains("SWIFTBIND010", TargetsContent);
            Assert.Contains("Unsupported TargetFramework", TargetsContent);
        }

        [Fact]
        public void Targets_HasSwiftBind060WarningCode()
        {
            Assert.Contains("SWIFTBIND060", TargetsContent);
            Assert.Contains("types were skipped", TargetsContent);
        }

        [Fact]
        public void Targets_HasSwiftBind061WarningCode()
        {
            Assert.Contains("SWIFTBIND061", TargetsContent);
            Assert.Contains("members were skipped", TargetsContent);
        }

        [Fact]
        public void Targets_ReportCoverageTarget_ReadsBindingReportJson()
        {
            Assert.Contains("binding-report.json", TargetsContent);
            Assert.Contains("_SwiftBindingSkippedTypes", TargetsContent);
            Assert.Contains("_SwiftBindingSkippedMembers", TargetsContent);
        }

        [Fact]
        public void Targets_HasSwiftBind100ErrorCode()
        {
            Assert.Contains("SWIFTBIND100", TargetsContent);
            Assert.Contains("SwiftPackage items are not yet supported", TargetsContent);
        }

        [Fact]
        public void Targets_UsesFingerprint()
        {
            Assert.Contains("shasum", TargetsContent);
            Assert.Contains("_SwiftStampFile", TargetsContent);
            Assert.Contains("_SwiftBindingUpToDate", TargetsContent);
        }

        [Fact]
        public void Targets_UsesXmlPeekForMetadata()
        {
            Assert.Contains("<XmlPeek", TargetsContent);
            Assert.Contains("binding-metadata.props", TargetsContent);
            Assert.Contains("_SwiftBindingPackageVersion", TargetsContent);
        }

        [Fact]
        public void Targets_ConfiguresPackLayout()
        {
            // Pack paths use _SwiftBindingPackTfm (version-qualified) not raw $(TargetFramework)
            Assert.Contains("buildTransitive/$(_SwiftBindingPackTfm)/", TargetsContent);
            Assert.Contains("runtimes/$(_SwiftBindingNuGetRid)/native/", TargetsContent);
            // Pack target runs before _GetPackageFiles (not GenerateNuspec) so items are
            // collected before NuGet freezes the file list
            Assert.Contains("_GetPackageFiles", TargetsContent);
        }

        [Fact]
        public void Targets_PackTargetUsesPerTfmContentMechanism()
        {
            // _ConfigureSwiftBindingPack is invoked per-TFM via TargetsForTfmSpecificContentInPackage
            // (set in Sdk.props). It returns TfmSpecificPackageFile items which the .NET SDK's
            // _WalkEachTargetPerFramework collects during multi-TFM pack. This replaces the
            // old BeforeTargets="_GetPackageFiles" approach which only worked for single-TFM.
            Assert.Contains("Returns=\"@(TfmSpecificPackageFile)\"", TargetsContent);
            Assert.DoesNotContain("BeforeTargets=\"_GetPackageFiles\"", TargetsContent);
        }

        [Fact]
        public void Targets_PackTfmResolvesVersionFromWorkload()
        {
            // _SwiftBindingPackTfm is computed inside _ConfigureSwiftBindingPack
            // to handle versionless TFMs (net10.0-ios -> net10.0-ios26.0).
            // NuGet NU1012 requires platform-versioned paths for framework-specific content.
            Assert.Contains("_SwiftBindingPackTfm", TargetsContent);
            Assert.Contains("TargetPlatformVersion", TargetsContent);
            // SWIFTBIND035 fires if version can't be resolved
            Assert.Contains("SWIFTBIND035", TargetsContent);
        }

        [Fact]
        public void Targets_AutoDiscoveryUsesShellFind()
        {
            // Auto-discovery uses find -type d because xcframeworks are directories
            Assert.Contains("find", TargetsContent);
            Assert.Contains("-type d", TargetsContent);
            Assert.Contains("*.xcframework", TargetsContent);
            Assert.Contains("_DiscoverSwiftFrameworks", TargetsContent);
            Assert.Contains("ConsoleToMSBuild", TargetsContent);
        }

        [Fact]
        public void Targets_GeneratorInvokesSdkMode()
        {
            Assert.Contains("--sdk-mode", TargetsContent);
            Assert.Contains("--wrapper-architectures", TargetsContent);
            Assert.Contains("--xcframework", TargetsContent);
        }

        [Fact]
        public void Targets_FingerprintGatesExecNotTarget()
        {
            // MSBuild evaluates Target Condition with evaluation-phase property values,
            // so _SwiftBindingUpToDate (set at execution time in _ComputeSwiftFingerprint)
            // can't gate the Target. Instead, the fingerprint gates the Exec task.
            Assert.Contains("DependsOnTargets=\"_ComputeSwiftFingerprint\"", TargetsContent);

            // _ComputeSwiftFingerprint target should not declare BeforeTargets
            var fingerprintTarget = TargetsContent.Substring(
                TargetsContent.IndexOf("Name=\"_ComputeSwiftFingerprint\"", StringComparison.Ordinal));
            var endOfTag = fingerprintTarget.IndexOf('>', StringComparison.Ordinal);
            var targetTag = fingerprintTarget.Substring(0, endOfTag);
            Assert.DoesNotContain("BeforeTargets", targetTag);

            // The Exec task must have both SwiftFramework and fingerprint conditions
            Assert.Contains("Exec Condition=\"'@(SwiftFramework)' != '' AND '$(_SwiftBindingUpToDate)' != 'true'\"", TargetsContent);
        }

        [Fact]
        public void Targets_GenerateHasNoTargetLevelSwiftFrameworkCondition()
        {
            // MSBuild evaluates Target Conditions at evaluation time, but SwiftFramework
            // items may only exist at execution time (populated by _DiscoverSwiftFrameworks).
            // A Target-level Condition would prevent the DependsOnTargets chain from firing.
            var generateTarget = TargetsContent.Substring(
                TargetsContent.IndexOf("Name=\"_GenerateSwiftBindings\"", StringComparison.Ordinal));
            var endOfTag = generateTarget.IndexOf('>', StringComparison.Ordinal);
            var targetTag = generateTarget.Substring(0, endOfTag);
            Assert.DoesNotContain("@(SwiftFramework)", targetTag);
        }

        [Fact]
        public void Targets_NativeReferenceDependsOnDiscovery()
        {
            // _ResolveSwiftNativeReferences must depend on _DiscoverSwiftFrameworks
            // so auto-discovered items are available before ResolveNativeReferences
            var nativeRefTarget = TargetsContent.Substring(
                TargetsContent.IndexOf("Name=\"_ResolveSwiftNativeReferences\"", StringComparison.Ordinal));
            var endOfTag = nativeRefTarget.IndexOf('>', StringComparison.Ordinal);
            var targetTag = nativeRefTarget.Substring(0, endOfTag);
            Assert.Contains("_DiscoverSwiftFrameworks", targetTag);
            Assert.DoesNotContain("@(SwiftFramework)", targetTag);
        }

        [Fact]
        public void Targets_DefinesIntermediateDir()
        {
            // _SwiftBindingIntermediateDir must be in .targets (not .props) so
            // $(IntermediateOutputPath) resolves to obj/ correctly
            Assert.Contains("_SwiftBindingIntermediateDir", TargetsContent);
            Assert.Contains("$(IntermediateOutputPath)swift-binding/", TargetsContent);
        }

        [Fact]
        public void Targets_FingerprintIncludesProperties()
        {
            // Verify that generation-affecting properties are part of the fingerprint
            Assert.Contains("_SwiftBindingSdkVersion", TargetsContent);
            Assert.Contains("SwiftPlatformTarget", TargetsContent);
            Assert.Contains("SwiftWrapperArchitectures", TargetsContent);
            Assert.Contains("PackageId", TargetsContent);
        }

        [Fact]
        public void Targets_FingerprintIncludesDocCommentsProperty()
        {
            Assert.Contains("SwiftGenerateDocComments", TargetsContent);
        }

        [Fact]
        public void Targets_NoDocsFlag_AppendsWhenNotTrue()
        {
            // --no-docs appended when SwiftGenerateDocComments != 'true'
            // MSBuild Condition string comparisons are case-insensitive by default.
            Assert.Contains("--no-docs", TargetsContent);
            Assert.Contains("SwiftGenerateDocComments", TargetsContent);
        }

        [Fact]
        public void Targets_GeneratorAppendsFrameworkDependencyArgs()
        {
            Assert.Contains("--framework-dependency", TargetsContent);
            Assert.Contains("SwiftFrameworkDependency", TargetsContent);
        }

        [Fact]
        public void Targets_NativeReferenceIncludesDependencies()
        {
            // Dependency xcframeworks should be injected as NativeReference for local build
            Assert.Contains("%(SwiftFrameworkDependency.Identity)", TargetsContent);
        }

        [Fact]
        public void Targets_FingerprintIncludesDependencies()
        {
            // Fingerprint hash should include SwiftFrameworkDependency items in property string
            Assert.Contains("@(SwiftFrameworkDependency", TargetsContent);
            // Fingerprint should also hash dependency xcframework contents (not just item text)
            // Uses newline-delimited 'while read' loop (space-safe) to hash each dependency
            Assert.Contains("while IFS= read -r dep", TargetsContent);
        }

        [Fact]
        public void Targets_HasSwiftBind040WarningCode()
        {
            Assert.Contains("SWIFTBIND040", TargetsContent);
            Assert.Contains("PackageId", TargetsContent);
            Assert.Contains("PackageVersion", TargetsContent);
        }

        [Fact]
        public void Targets_ContainsValidateDependencyMetadataTarget()
        {
            Assert.Contains("Name=\"_ValidateSwiftDependencyMetadata\"", TargetsContent);
        }

        [Fact]
        public void Targets_ValidateDependencyMetadata_BeforePackConfig()
        {
            var validateTarget = TargetsContent.Substring(
                TargetsContent.IndexOf("Name=\"_ValidateSwiftDependencyMetadata\"", StringComparison.Ordinal));
            var endOfTag = validateTarget.IndexOf('>', StringComparison.Ordinal);
            var targetTag = validateTarget.Substring(0, endOfTag);
            Assert.Contains("BeforeTargets=\"_ConfigureSwiftBindingPack\"", targetTag);
        }

        [Fact]
        public void Targets_ContainsCollectModuleDatabasesTarget()
        {
            Assert.Contains("Name=\"_CollectSwiftModuleDatabases\"", TargetsContent);
        }

        [Fact]
        public void Targets_CollectModuleDatabases_BeforeGenerateBindings()
        {
            var collectTarget = TargetsContent.Substring(
                TargetsContent.IndexOf("Name=\"_CollectSwiftModuleDatabases\"", StringComparison.Ordinal));
            var endOfTag = collectTarget.IndexOf('>', StringComparison.Ordinal);
            var targetTag = collectTarget.Substring(0, endOfTag);
            Assert.Contains("BeforeTargets=\"_GenerateSwiftBindings\"", targetTag);
        }

        [Fact]
        public void Targets_GeneratorAppendsModuleDatabaseArgs()
        {
            Assert.Contains("--module-database", TargetsContent);
            Assert.Contains("_SwiftModuleDatabaseFile", TargetsContent);
        }

        [Fact]
        public void Targets_PackLayoutIncludesModuleDatabase()
        {
            Assert.Contains("Database.xml", TargetsContent);
            // Pack paths use _SwiftBindingPackTfm (version-qualified) not raw $(TargetFramework)
            Assert.Contains("buildTransitive/$(_SwiftBindingPackTfm)/", TargetsContent);
        }

        [Fact]
        public void Targets_FingerprintIncludesModuleDatabases()
        {
            Assert.Contains("SwiftModuleDatabase", TargetsContent);
        }

        [Fact]
        public void Targets_HasSwiftBind073WarningCode()
        {
            Assert.Contains("SWIFTBIND073", TargetsContent);
            Assert.Contains("Module database not found", TargetsContent);
        }

        [Fact]
        public void Targets_CollectModuleDatabases_SupportsLocalModuleDatabasePath()
        {
            Assert.Contains("%(SwiftFrameworkDependency.ModuleDatabasePath)", TargetsContent);
        }

        [Fact]
        public void Targets_ContainsResolveAutoDetectedDependenciesTarget()
        {
            Assert.Contains("Name=\"_ResolveSwiftAutoDetectedDependencies\"", TargetsContent);
        }

        [Fact]
        public void Targets_AutoDetectedDeps_BeforeResolveProjectReferences()
        {
            var target = TargetsContent.Substring(
                TargetsContent.IndexOf("Name=\"_ResolveSwiftAutoDetectedDependencies\"", StringComparison.Ordinal));
            var endOfTag = target.IndexOf('>', StringComparison.Ordinal);
            var targetTag = target.Substring(0, endOfTag);
            Assert.Contains("BeforeTargets=\"ResolveProjectReferences\"", targetTag);
        }

        [Fact]
        public void Targets_HasSwiftBind080WarningCode()
        {
            Assert.Contains("SWIFTBIND080", TargetsContent);
            Assert.Contains("Cross-module dependency detected", TargetsContent);
        }

        [Fact]
        public void Targets_GeneratorPassesPlatformArg()
        {
            Assert.Contains("--platform $(_SwiftBindingPlatform)", TargetsContent);
        }

        [Fact]
        public void Targets_PlatformTargetConditionalOnNonEmpty()
        {
            // --platform-target should only be passed when SwiftPlatformTarget has a value
            // (macOS/Catalyst have no simulator, so it's empty)
            Assert.Contains("Condition=\"'$(SwiftPlatformTarget)' != ''\"", TargetsContent);
            Assert.Contains("--platform-target $(SwiftPlatformTarget)", TargetsContent);
        }

        [Fact]
        public void Targets_FingerprintIncludesPlatform()
        {
            Assert.Contains("$(_SwiftBindingPlatform)", TargetsContent);
        }

        [Fact]
        public void Targets_SliceValidationIsPlatformAware()
        {
            // SWIFTBIND030 should only fire for platforms with simulator slices
            Assert.Contains("_SwiftBindingHasSimulatorSlice", TargetsContent);
            // SWIFTBIND031 should use dynamic slice IDs
            Assert.Contains("_SwiftBindingSimulatorSliceId", TargetsContent);
            Assert.Contains("_SwiftBindingDeviceSliceId", TargetsContent);
        }

        [Fact]
        public void Targets_HasSingleSlicePlatformValidation()
        {
            // For macOS/Catalyst, only device slice is validated (no simulator)
            // There should be a guard checking _SwiftBindingHasSimulatorSlice != 'true'
            Assert.Contains("'$(_SwiftBindingHasSimulatorSlice)' != 'true'", TargetsContent);
        }

        [Fact]
        public void Targets_GenerateSwiftBindings_BeforeResolveProjectReferences()
        {
            var generateTarget = TargetsContent.Substring(
                TargetsContent.IndexOf("Name=\"_GenerateSwiftBindings\"", StringComparison.Ordinal));
            var endOfTag = generateTarget.IndexOf('>', StringComparison.Ordinal);
            var targetTag = generateTarget.Substring(0, endOfTag);
            Assert.Contains("BeforeTargets=\"ResolveProjectReferences\"", targetTag);
        }

        // ── Two-pass build (wrapper compilation deferred to after ResolveProjectReferences) ──

        [Fact]
        public void Targets_GeneratorPassesSkipWrapperCompilation()
        {
            Assert.Contains("--skip-wrapper-compilation", TargetsContent);
        }

        [Fact]
        public void Targets_CompileSwiftWrapperRunsAfterResolveProjectReferences()
        {
            var target = TargetsContent.Substring(
                TargetsContent.IndexOf("Name=\"_CompileSwiftWrapper\"", StringComparison.Ordinal));
            var endOfTag = target.IndexOf('>', StringComparison.Ordinal);
            var targetTag = target.Substring(0, endOfTag);
            Assert.Contains("AfterTargets=\"ResolveProjectReferences\"", targetTag);
        }

        [Fact]
        public void Targets_CompileSwiftWrapperUsesCompileWrapperOnlyFlag()
        {
            Assert.Contains("--compile-wrapper-only", TargetsContent);
        }

        [Fact]
        public void Targets_CompileSwiftWrapperCollectsDependencyPaths()
        {
            Assert.Contains("Targets=\"GetSwiftFrameworkSearchPaths\"", TargetsContent);
            Assert.Contains("_ResolvedDepXCFramework", TargetsContent);
        }

        [Fact]
        public void Targets_GetSwiftFrameworkSearchPaths_ReturnsPaths()
        {
            var target = TargetsContent.Substring(
                TargetsContent.IndexOf("Name=\"GetSwiftFrameworkSearchPaths\"", StringComparison.Ordinal));
            var endOfTag = target.IndexOf('>', StringComparison.Ordinal);
            var targetTag = target.Substring(0, endOfTag);
            Assert.Contains("Returns=\"@(_SwiftBindingFrameworkSearchPath)\"", targetTag);
        }

        [Fact]
        public void Targets_UpdateSwiftWrapperMetadataRunsAfterCompile()
        {
            var target = TargetsContent.Substring(
                TargetsContent.IndexOf("Name=\"_UpdateSwiftWrapperMetadata\"", StringComparison.Ordinal));
            var endOfTag = target.IndexOf('>', StringComparison.Ordinal);
            var targetTag = target.Substring(0, endOfTag);
            Assert.Contains("AfterTargets=\"_CompileSwiftWrapper\"", targetTag);
        }

        [Fact]
        public void Targets_ValidateWrapperRunsAfterMetadataUpdate()
        {
            var target = TargetsContent.Substring(
                TargetsContent.IndexOf("Name=\"_ValidateSwiftWrapperCompilation\"", StringComparison.Ordinal));
            var endOfTag = target.IndexOf('>', StringComparison.Ordinal);
            var targetTag = target.Substring(0, endOfTag);
            Assert.Contains("AfterTargets=\"_UpdateSwiftWrapperMetadata\"", targetTag);
        }

        [Fact]
        public void Targets_NativeReferenceDependsOnWrapperMetadataUpdate()
        {
            var target = TargetsContent.Substring(
                TargetsContent.IndexOf("Name=\"_ResolveSwiftNativeReferences\"", StringComparison.Ordinal));
            var endOfTag = target.IndexOf('>', StringComparison.Ordinal);
            var targetTag = target.Substring(0, endOfTag);
            Assert.Contains("_UpdateSwiftWrapperMetadata", targetTag);
        }

        [Fact]
        public void Targets_WrapperSkipOnlyWhenUpToDateAndNoProjectReferences()
        {
            // _SwiftWrapperSkip should require BOTH conditions:
            // fingerprint up-to-date AND no ProjectReferences
            Assert.Contains("_SwiftWrapperSkip", TargetsContent);
            Assert.Contains("'$(_SwiftBindingUpToDate)' == 'true' AND '@(ProjectReference)' == ''", TargetsContent);
        }

        [Fact]
        public void Targets_CompileWrapperUsesWrapperSkipNotUpToDate()
        {
            // _CompileSwiftWrapper tasks should gate on _SwiftWrapperSkip, not _SwiftBindingUpToDate directly
            // Find the _CompileSwiftWrapper target and check its Exec condition
            var targetStart = TargetsContent.IndexOf("Name=\"_CompileSwiftWrapper\"", StringComparison.Ordinal);
            var targetEnd = TargetsContent.IndexOf("</Target>", targetStart, StringComparison.Ordinal);
            var targetBody = TargetsContent.Substring(targetStart, targetEnd - targetStart);
            Assert.Contains("_SwiftWrapperSkip", targetBody);
            // Should NOT directly use _SwiftBindingUpToDate in task conditions
            Assert.DoesNotContain("'$(_SwiftBindingUpToDate)' != 'true'", targetBody);
        }

        // ── Bug fix regression tests (SDK 0.2.0) ──

        [Fact]
        public void Targets_GetSwiftFrameworkSearchPaths_WrapperPathIsAbsolute()
        {
            // Bug 1: GetSwiftFrameworkSearchPaths returned relative wrapper xcframework paths.
            // When project B queries project A via MSBuild task, relative paths resolve
            // against the consumer (B), not the producer (A). Fix: prefix with $(MSBuildProjectDirectory)/.
            var target = TargetsContent.Substring(
                TargetsContent.IndexOf("Name=\"GetSwiftFrameworkSearchPaths\"", StringComparison.Ordinal));
            var endOfTarget = target.IndexOf("</Target>", StringComparison.Ordinal);
            var targetBody = target.Substring(0, endOfTarget);
            Assert.Contains("$(MSBuildProjectDirectory)/$(_SwiftBindingIntermediateDir)$(_SwiftBindingWrapperModuleName).xcframework", targetBody);
        }

        [Fact]
        public void Targets_CompileSwiftWrapperExec_HasContinueOnError()
        {
            // Bug 2: _CompileSwiftWrapper Exec had no ContinueOnError, so wrapper compilation
            // failure killed the entire build before _ValidateSwiftWrapperCompilation could run.
            // Fix: add ContinueOnError="WarnAndContinue" so downstream validation handles the result.
            var targetStart = TargetsContent.IndexOf("Name=\"_CompileSwiftWrapper\"", StringComparison.Ordinal);
            var targetEnd = TargetsContent.IndexOf("</Target>", targetStart, StringComparison.Ordinal);
            var targetBody = TargetsContent.Substring(targetStart, targetEnd - targetStart);

            // Find the Exec element within the target
            var execStart = targetBody.IndexOf("<Exec", StringComparison.Ordinal);
            Assert.True(execStart >= 0, "_CompileSwiftWrapper should contain an Exec task");
            var execEnd = targetBody.IndexOf("/>", execStart, StringComparison.Ordinal);
            var execElement = targetBody.Substring(execStart, execEnd - execStart + 2);
            Assert.Contains("ContinueOnError=\"WarnAndContinue\"", execElement);
        }

        [Fact]
        public void Targets_CompileSwiftWrapper_FiltersObjCProjectReferences()
        {
            // Bug 3: ObjC ProjectReferences (e.g. BlinkID.ObjC.iOS.csproj) don't have
            // GetSwiftFrameworkSearchPaths target, causing MSB4057 errors.
            // Fix: filter into _SwiftBindingProjectReference excluding .ObjC. items.
            var targetStart = TargetsContent.IndexOf("Name=\"_CompileSwiftWrapper\"", StringComparison.Ordinal);
            var targetEnd = TargetsContent.IndexOf("</Target>", targetStart, StringComparison.Ordinal);
            var targetBody = TargetsContent.Substring(targetStart, targetEnd - targetStart);

            // Must define _SwiftBindingProjectReference that filters out .ObjC.
            Assert.Contains("_SwiftBindingProjectReference", targetBody);
            Assert.Contains(".ObjC.", targetBody);
            // MSBuild task must use filtered list, not raw @(ProjectReference)
            var msbuildTask = targetBody.Substring(targetBody.IndexOf("<MSBuild", StringComparison.Ordinal));
            msbuildTask = msbuildTask.Substring(0, msbuildTask.IndexOf("/>", StringComparison.Ordinal) + 2);
            Assert.Contains("@(_SwiftBindingProjectReference)", msbuildTask);
            Assert.DoesNotContain("@(ProjectReference)", msbuildTask);
        }

        [Fact]
        public void Targets_CompileSwiftWrapper_IncludesBothResolvedAndExplicitDeps()
        {
            // Both _ResolvedDepXCFramework (from ProjectReference) and SwiftFrameworkDependency
            // (explicit) are always included. Non-binding frameworks (e.g., Stripe3DS2) have no
            // ProjectReference but still need -F search paths for wrapper compilation.
            // Duplicate modules are handled by the generator (skip, not error).
            var targetStart = TargetsContent.IndexOf("Name=\"_CompileSwiftWrapper\"", StringComparison.Ordinal);
            var targetEnd = TargetsContent.IndexOf("</Target>", targetStart, StringComparison.Ordinal);
            var targetBody = TargetsContent.Substring(targetStart, targetEnd - targetStart);

            // Both should be present
            var resolvedIdx = targetBody.IndexOf("@(_ResolvedDepXCFramework->' --framework-dependency", StringComparison.Ordinal);
            var explicitIdx = targetBody.IndexOf("@(SwiftFrameworkDependency->' --framework-dependency", StringComparison.Ordinal);
            Assert.True(resolvedIdx >= 0, "Should have _ResolvedDepXCFramework framework-dependency line");
            Assert.True(explicitIdx >= 0, "Should have SwiftFrameworkDependency framework-dependency line");

            // SwiftFrameworkDependency should NOT be gated on _ResolvedDepXCFramework being empty
            var explicitLine = targetBody.Substring(
                targetBody.LastIndexOf("<_SwiftWrapperCmd", explicitIdx, StringComparison.Ordinal));
            explicitLine = explicitLine.Substring(0, explicitLine.IndexOf("</_SwiftWrapperCmd>", StringComparison.Ordinal));
            Assert.DoesNotContain("'@(_ResolvedDepXCFramework)' == ''", explicitLine);
        }

        // ------------------------------------------------------------------
        // Mac Catalyst framework resolver fallback
        //
        // Catalyst frameworks that ship only a regular macOS slice — no
        // iOSSupport/ variant — must still resolve at compile time. Sdk.targets
        // probes System/iOSSupport/System/Library/Frameworks first and, when
        // the .swiftmodule is missing there, falls back to the regular
        // System/Library/Frameworks path. Both paths are pure MSBuild XML, so
        // the cheap, deterministic gate is string assertions on Sdk.targets.
        // ------------------------------------------------------------------

        [Fact]
        public void Targets_CatalystFrameworkResolver_PrimaryPathIsIosSupport()
        {
            Assert.Contains(
                "'$(_SwiftBindingPlatform)' == 'maccatalyst'",
                TargetsContent);
            Assert.Contains(
                "System/iOSSupport/System/Library/Frameworks",
                TargetsContent);
        }

        [Fact]
        public void Targets_CatalystFrameworkResolver_FallbackGuardedOnMissingSwiftmodule()
        {
            Assert.Contains(
                "!Exists('$(_SwiftAppleFrameworkDir)/Modules/$(_SwiftAppleFrameworkModule).swiftmodule')",
                TargetsContent);
        }

        [Fact]
        public void Targets_CatalystFrameworkResolver_FallbackReassignsToRegularMacosPath()
        {
            var primaryIdx = TargetsContent.IndexOf(
                "System/iOSSupport/System/Library/Frameworks",
                StringComparison.Ordinal);
            Assert.True(primaryIdx >= 0);

            var fallbackIdx = TargetsContent.IndexOf(
                "<_SwiftAppleFrameworkSdkSubpath>System/Library/Frameworks</_SwiftAppleFrameworkSdkSubpath>",
                primaryIdx,
                StringComparison.Ordinal);
            Assert.True(fallbackIdx > primaryIdx,
                "Catalyst fallback reassignment must appear AFTER the iOSSupport primary " +
                "assignment so the regular macOS path is used only when the iOSSupport " +
                "variant is missing.");
        }

        private static string FindRepoRoot()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null)
            {
                var gitPath = Path.Combine(dir, ".git");
                // .git is a directory in normal repos, a file in worktrees
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("Cannot find repo root.");
        }
    }

    /// <summary>
    /// Content validation tests for Swift.Runtime.csproj and Swift.Runtime.targets
    /// to ensure platform-specific native dylib conditions don't overlap.
    /// </summary>
    public class RuntimeNativeAssetConditionTests
    {
        private static readonly string RuntimeDir = Path.Combine(
            FindRepoRoot(), "src", "Swift.Runtime", "src");

        private static readonly string CsprojContent = File.ReadAllText(
            Path.Combine(RuntimeDir, "Swift.Runtime.csproj"));

        private static readonly string TargetsContent = File.ReadAllText(
            Path.Combine(RuntimeDir, "build", "SwiftBindings.Runtime.targets"));

        [Fact]
        public void Csproj_MacOsDylibCondition_ExcludesTvos()
        {
            // The macOS dylib must NOT match net10.0-tvos. The condition must use a positive
            // 'macos' check (not just exclude 'ios' and 'maccatalyst'), otherwise tvOS picks
            // up the wrong dylib.
            var macosBlock = ExtractDylibBlock(CsprojContent, "native/macos/");
            Assert.NotNull(macosBlock);
            Assert.Contains("Contains('macos')", macosBlock);
            // Must not use the old exclusion-only pattern
            Assert.DoesNotContain("!$(TargetFramework.Contains('ios')) AND !$(TargetFramework.Contains('maccatalyst'))", macosBlock);
        }

        [Fact]
        public void Targets_MacOsDylibCondition_ExcludesTvos()
        {
            var macosBlock = ExtractDylibBlock(TargetsContent, "native/macos/");
            Assert.NotNull(macosBlock);
            Assert.Contains("Contains('macos')", macosBlock);
            Assert.DoesNotContain("!$(TargetFramework.Contains('ios')) AND !$(TargetFramework.Contains('maccatalyst'))", macosBlock);
        }

        [Fact]
        public void Csproj_HasTvosTargetFramework()
        {
            Assert.Contains("net10.0-tvos", CsprojContent);
        }

        [Fact]
        public void Csproj_HasTvosDylibContentItems()
        {
            Assert.Contains("native/tvos/libSwiftBindingsRuntime.dylib", CsprojContent);
            Assert.Contains("native/tvossimulator/libSwiftBindingsRuntime.dylib", CsprojContent);
        }

        [Fact]
        public void Targets_HasTvosDylibBlocks()
        {
            Assert.Contains("native/tvos/libSwiftBindingsRuntime.dylib", TargetsContent);
            Assert.Contains("native/tvossimulator/libSwiftBindingsRuntime.dylib", TargetsContent);
        }

        private static string? ExtractDylibBlock(string content, string dylib)
        {
            var idx = content.IndexOf(dylib, StringComparison.Ordinal);
            if (idx < 0) return null;
            // Walk backward to find the enclosing <ItemGroup
            var start = content.LastIndexOf("<ItemGroup", idx, StringComparison.Ordinal);
            if (start < 0) return null;
            var end = content.IndexOf("</ItemGroup>", idx, StringComparison.Ordinal);
            if (end < 0) return null;
            return content.Substring(start, end - start + "</ItemGroup>".Length);
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
    }
}
