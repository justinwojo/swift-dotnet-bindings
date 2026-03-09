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
        public void Props_SetsDefaultTargetFramework()
        {
            Assert.Contains("net10.0-ios", PropsContent);
        }

        [Fact]
        public void Props_SetsAllowUnsafeBlocks()
        {
            Assert.Contains("<AllowUnsafeBlocks>true</AllowUnsafeBlocks>", PropsContent);
        }

        [Fact]
        public void Props_IncludesSwiftRuntimeReference()
        {
            Assert.Contains("Swift.Runtime", PropsContent);
            Assert.Contains("$(SwiftRuntimeVersion)", PropsContent);
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
                "_ConfigureSwiftBindingPack"
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
            Assert.Contains("Multiple xcframeworks found", TargetsContent);
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
        public void Targets_PackTargetRunsBeforeGetPackageFiles()
        {
            // Critical: _ConfigureSwiftBindingPack must run before _GetPackageFiles,
            // not GenerateNuspec. NuGet's pack pipeline collects None items during
            // _GetPackageFiles (a DependsOnTargets of GenerateNuspec), which runs
            // before any BeforeTargets="GenerateNuspec" targets fire. Without this,
            // native xcframeworks and .targets files are added too late and missing
            // from the .nupkg.
            Assert.Contains("BeforeTargets=\"_GetPackageFiles\"", TargetsContent);
            Assert.DoesNotContain("BeforeTargets=\"GenerateNuspec\"", TargetsContent);
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
            Assert.Contains("DependsOnTargets=\"_DiscoverSwiftFrameworks\"", targetTag);
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
            Path.Combine(RuntimeDir, "build", "Swift.Runtime.targets"));

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
