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
                "_GenerateSwiftBindings",
                "_ImportSwiftBindingMetadata",
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
            Assert.Contains("buildTransitive/net10.0-ios/", TargetsContent);
            Assert.Contains("runtimes/ios-arm64/native/", TargetsContent);
            Assert.Contains("GenerateNuspec", TargetsContent);
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
}
