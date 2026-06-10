// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Text.RegularExpressions;
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
        public void Props_SwiftRuntimePackageReference_SkippedForObjCFrameworkBindings()
        {
            // Pure-ObjC AppleFramework bindings (Matter, MatterSupport) must NOT pull in
            // SwiftBindings.Runtime — there are no Swift interop types in the binding.
            // The gate lives on the PackageReference itself (item-level Condition) because
            // ItemGroup conditions evaluate AFTER the body has set SwiftFrameworkType.
            // A PropertyGroup-level gate in Sdk.props (e.g. setting
            // DisableImplicitSwiftRuntimeReference based on SwiftFrameworkType) would NOT
            // work — Sdk.props evaluates before the body, so the property would be empty.
            Assert.Contains(
                "<PackageReference Include=\"SwiftBindings.Runtime\"",
                PropsContent);
            Assert.Contains(
                "AND '$(SwiftFrameworkType)' != 'ObjC'",
                PropsContent);
        }

        [Fact]
        public void Props_DoesNotSetIsBindingProjectFromBrokenPropertyGroup()
        {
            // Sdk.props is evaluated BEFORE the user csproj body — so a PropertyGroup
            // gated on $(SwiftFrameworkType) would never fire (SwiftFrameworkType is set
            // by the user's body PropertyGroup, which runs later). Earlier shapes of this
            // SDK had such a PropertyGroup at the top of Sdk.props that silently failed to
            // assign IsBindingProject=true. The fix is to require the user to declare
            // <IsBindingProject>true</IsBindingProject> explicitly in their csproj body;
            // SWIFTBIND018 / SWIFTBIND021 validate that contract at targets time. This
            // test prevents the broken-by-design PropertyGroup from creeping back in.
            Assert.DoesNotContain(
                "<PropertyGroup Condition=\"'$(SwiftFrameworkType)' == 'ObjC'\">",
                PropsContent);
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

        // Platform detection (`_SwiftBindingPlatform` and the slice/RID derivation that
        // hangs off it) lives in Sdk.targets, not Sdk.props — see the inline note at the
        // top of Sdk.props. The matching assertions are in `SdkTargetsContentTests`.

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

        // ── Slice-id resync is generalized to BOTH binding modes ─────────────────
        // The static slice-id defaults at the top of Sdk.targets assume an
        // arm64-primary sim slice (ios-arm64-simulator). A fat fold OR an
        // x86_64-primary SwiftFramework wrapper lands the sim slice at a name the
        // defaults don't predict (ios-arm64_x86_64-simulator / ios-x86_64-simulator),
        // and Guard 2a (_ValidateSwiftBindingPackSlices) then hard-fails with a FALSE
        // SWIFTBIND031. The disk-glob resync must run for SwiftFramework bindings too,
        // not just AppleFramework — so the target was renamed + its condition broadened.

        [Fact]
        public void Targets_ResyncWrapperSliceIds_TargetExists()
        {
            // Renamed from _ResyncAppleFrameworkWrapperSliceIds when generalized to both modes.
            Assert.Contains("Name=\"_ResyncWrapperSliceIds\"", TargetsContent);
            Assert.DoesNotContain("_ResyncAppleFrameworkWrapperSliceIds", TargetsContent);
        }

        [Fact]
        public void Targets_ResyncWrapperSliceIds_NotAppleFrameworkGated()
        {
            // The resync condition must NOT gate on AppleFramework mode — an
            // x86_64-primary SwiftFramework wrapper needs the disk re-read just as much.
            var condition = ExtractAttribute("_ResyncWrapperSliceIds", "Condition");
            Assert.DoesNotContain("AppleFramework", condition);
            // It gates instead on the wrapper module existing (mode-agnostic).
            Assert.Contains("_SwiftBindingWrapperModuleName", condition);
        }

        [Fact]
        public void Targets_ResyncWrapperSliceIds_RunsAfterWrapperCompileBeforePackValidation()
        {
            var openingTag = ExtractOpeningTag("_ResyncWrapperSliceIds");
            // Must run after the SwiftFramework wrapper compile (the new generalized trigger),
            // not only after the AppleFramework second-slice fold.
            Assert.Contains("AfterTargets=", openingTag);
            Assert.Contains("_CompileSwiftWrapper", openingTag);
            // Must run before pack-slice validation so the resynced ids reach Guard 2a.
            Assert.Contains("BeforeTargets=", openingTag);
            Assert.Contains("_ValidateSwiftBindingPackSlices", openingTag);
        }

        [Fact]
        public void Targets_ResyncWrapperSliceIds_GlobsBySimulatorSuffix()
        {
            // The glob must be arch-agnostic (match by the '-simulator' suffix) so it
            // resolves whether the primary arch is arm64, x86_64, or a fat fold of both.
            var body = ExtractTargetBlock("_ResyncWrapperSliceIds");
            Assert.Contains("-name '*-simulator'", body);
            Assert.Contains("! -name '*-simulator'", body);
            Assert.Contains("_SwiftBindingSimulatorSliceId", body);
            Assert.Contains("_SwiftBindingDeviceSliceId", body);
        }

        // ── The SwiftUI bridge fattens to match the wrapper ──────────────────────
        // A fat arm64+x86_64 wrapper paired with an arm64-only bridge xcframework
        // is dropped on x64-sim / Rosetta consumers (DllNotFound). The bridge compile
        // must thread the same --target-architectures the wrapper does.

        [Fact]
        public void Targets_CompileBridge_ThreadsTargetArchitectures()
        {
            var body = ExtractTargetBlock("_CompileSwiftUIBridge");
            // Scoped to the bridge block so this can't pass on the wrapper's own
            // --target-architectures line (_CompileSwiftWrapper).
            Assert.Contains("--target-architectures $(SwiftTargetArchitectures)", body);
            Assert.Contains("'$(SwiftTargetArchitectures)' != ''", body);
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
            // _GenerateSwiftBindings must depend on _ComputeSwiftFingerprint (any
            // semicolon-delimited position) so the fingerprint runs before the Exec gate.
            var generateTarget = TargetsContent.Substring(
                TargetsContent.IndexOf("Name=\"_GenerateSwiftBindings\"", StringComparison.Ordinal));
            var generateTagEnd = generateTarget.IndexOf('>', StringComparison.Ordinal);
            var generateTag = generateTarget.Substring(0, generateTagEnd);
            var dependsMatch = Regex.Match(generateTag, "DependsOnTargets=\"([^\"]*)\"");
            Assert.True(dependsMatch.Success, "_GenerateSwiftBindings must declare DependsOnTargets");
            Assert.Contains("_ComputeSwiftFingerprint",
                dependsMatch.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries));

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

            // SwiftTargetArchitectures (the x86_64 CPU-arch selector) must sit inside BOTH
            // fingerprint echoes — XCFramework-mode and Apple-framework-mode — adjacent to
            // SwiftWrapperArchitectures, so flipping it invalidates _SwiftBindingUpToDate. A bare
            // Contains would pass on the unrelated CLI-injection site, so pin the echo adjacency.
            const string fingerprintPair = "$(SwiftWrapperArchitectures) $(SwiftTargetArchitectures)";
            Assert.Equal(2, TargetsContent.Split(fingerprintPair).Length - 1);
        }

        [Fact]
        public void Targets_FingerprintIncludesGenerationInputs()
        {
            // Three generation-affecting inputs must sit inside the fingerprint echoes so
            // flipping any one invalidates _SwiftBindingUpToDate (otherwise the generator is
            // skipped as "up to date" and silently reuses stale output). $(SwiftAppleSupplementPrototypeDir)
            // is the unique tail anchor present ONLY in the two echoes, so pinning the new
            // tokens right after it ties the assertion to the echo (not an unrelated CLI site).

            // XCFramework-mode echo: SwiftFrameworkType drives --objc; SwiftAppleSupplementVersion
            // drives --apple-version. Both alter generated output for source-xcframework bindings.
            Assert.Contains(
                "$(SwiftAppleSupplementPrototypeDir) $(SwiftFrameworkType) $(SwiftAppleSupplementVersion) @(SwiftFrameworkDependency",
                TargetsContent);

            // Apple-framework-mode echo: NamespacePattern drives --namespace-pattern;
            // SwiftAppleSupplementVersion drives --apple-version. This pair closes the echo.
            Assert.Contains(
                "$(SwiftAppleSupplementPrototypeDir) %(SwiftAppleFrameworkTarget.NamespacePattern) $(SwiftAppleSupplementVersion)",
                TargetsContent);
        }

        [Fact]
        public void Targets_SecondSliceCommitIsAtomicAndRecoverable()
        {
            // The wrapper and bridge second-slice commits must NOT be the old non-atomic
            // RemoveDir(live)+mv(merged): a kill between them loses the xcframework while
            // the fingerprint stamp (written pre-swap) marks the build up-to-date, so the
            // ungated presence probe records HasWrapperXCFramework=False and drops the
            // consumer NativeReference -> DllNotFoundException. Instead, each commit parks
            // the live tree at a '.superseded' sibling, moves the merged tree in, rolls back
            // from '.superseded' on failure, then drops the aside — one shell process.
            Assert.Contains(
                "mv &quot;$(_AFW_XCF)&quot; &quot;$(_AFW_XCF).superseded&quot;",
                TargetsContent);
            Assert.Contains(
                "mv &quot;$(_AFW_XCF).superseded&quot; &quot;$(_AFW_XCF)&quot;; exit 1;",
                TargetsContent);
            Assert.Contains(
                "mv &quot;$(_AFB_XCF)&quot; &quot;$(_AFB_XCF).superseded&quot;",
                TargetsContent);
            Assert.Contains(
                "mv &quot;$(_AFB_XCF).superseded&quot; &quot;$(_AFB_XCF)&quot;; exit 1;",
                TargetsContent);

            // The old non-atomic commit comment must be gone for both targets.
            Assert.DoesNotContain("now swap: remove original, move merged", TargetsContent);

            // Stale staging 'merged.xcframework' from a prior interrupt is removed BEFORE each
            // create (xcodebuild refuses to overwrite, and the bridge's Exists()-only success
            // check would otherwise commit a stale tree).
            Assert.Contains("_merge_slices/merged.xcframework\" />", TargetsContent);
            Assert.Contains("_bridge_merge_slices/merged.xcframework\" />", TargetsContent);

            // The bridge create runs with ContinueOnError (non-fatal degrade), so its
            // success can't be inferred from Exists(merged) alone — a failed xcodebuild that
            // left a partial tree would otherwise be committed over the working bridge. Its
            // exit code is captured and the commit is gated on exit code 0 AND existence.
            Assert.Contains("TaskParameter=\"ExitCode\" PropertyName=\"_AFB_MergeExitCode\"", TargetsContent);
            Assert.Contains(
                "_AFB_MergeSucceeded Condition=\"'$(_AFB_MergeExitCode)' == '0' AND Exists(",
                TargetsContent);
        }

        [Fact]
        public void Targets_AppleFrameworkFingerprintHashesModuleDatabases()
        {
            // Apple-direct generation feeds declared cross-module databases to --module-database
            // (e.g. RealityKit consuming RealityFoundationDatabase.xml). A change to a declared
            // SwiftModuleDatabase or a SwiftFrameworkDependency's ModuleDatabasePath must
            // invalidate _SwiftBindingUpToDate, so both inputs are hashed in the Apple-framework
            // fingerprint too — not just the xcframework-mode one. Each hashing loop must now
            // appear TWICE (once per mode); before this fix the Apple echo omitted both, leaving
            // a stale-regeneration hole on cross-module DB edits.
            Assert.Equal(2, CountOccurrences(TargetsContent, "@(SwiftModuleDatabase, '%0A')"));
            Assert.Equal(2, CountOccurrences(TargetsContent,
                "@(SwiftFrameworkDependency->'%(ModuleDatabasePath)', '%0A')"));
        }

        static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                i += needle.Length;
            }
            return count;
        }

        [Fact]
        public void Targets_InterruptedSwapRecoveryIsWiredBeforePresenceProbe()
        {
            // SWIFTBIND053 surfaces the heal of an interrupted second-slice swap.
            Assert.Contains("SWIFTBIND053", TargetsContent);

            // Recovery is keyed on the asymmetric contract: a missing wrapper is catastrophic
            // (whole binding DllNotFounds) and a single-slice wrapper is valid for the current
            // platform, so it is RESTORED from '.superseded'. The bridge's parked tree is only
            // ever single-slice, which a simulator-bearing platform drops per SWIFTBIND052, so
            // restoring it would just be undone — the bridge is left missing (degrades per the
            // documented contract) and only its orphaned '.superseded' is cleaned.
            Assert.Contains("_AFW_WrapperNeedsRecovery", TargetsContent);
            Assert.Contains("_AFW_BridgeNeedsRecovery", TargetsContent);
            // Wrapper recovery does an mv-restore; bridge recovery is a RemoveDir of the aside.
            Assert.Contains(
                "if [ ! -d &quot;$(_AFW_WrapperPath)&quot; ]; then mv &quot;$(_AFW_WrapperPath).superseded&quot; &quot;$(_AFW_WrapperPath)&quot;",
                TargetsContent);
            Assert.Contains(
                "Directories=\"$(_AFW_BridgePath).superseded\" />",
                TargetsContent);
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
            // fingerprint up-to-date AND no ProjectReferences. The condition is
            // formatted across multiple lines for readability, so collapse whitespace
            // before asserting the clause order.
            Assert.Contains("_SwiftWrapperSkip", TargetsContent);
            var collapsed = System.Text.RegularExpressions.Regex.Replace(TargetsContent, @"\s+", " ");
            Assert.Contains("'$(_SwiftBindingUpToDate)' == 'true' AND '@(ProjectReference)' == ''", collapsed);
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
            // GetSwiftFrameworkSearchPaths returned relative wrapper xcframework paths.
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
            // _CompileSwiftWrapper Exec had no ContinueOnError, so wrapper compilation
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
            // ObjC ProjectReferences (e.g. Foo.ObjC.iOS.csproj) don't have
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
            // (explicit) are always included. Non-binding frameworks have no ProjectReference
            // but still need -F search paths for wrapper compilation.
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

        [Fact]
        public void Targets_AutoDetectsPlatformFromTfm()
        {
            Assert.Contains("_SwiftBindingPlatform", TargetsContent);
            Assert.Contains("maccatalyst", TargetsContent);
            Assert.Contains("tvos", TargetsContent);
            Assert.Contains("macos", TargetsContent);
            Assert.Contains("TargetFramework.Contains(", TargetsContent);
        }

        [Fact]
        public void Targets_DetectsMaccatalystBeforeMacos()
        {
            // maccatalyst must be checked before macos to avoid substring overlap
            var maccatalystIdx = TargetsContent.IndexOf("Contains('maccatalyst')", StringComparison.Ordinal);
            var macosIdx = TargetsContent.IndexOf("Contains('macos')", StringComparison.Ordinal);
            Assert.True(maccatalystIdx > 0);
            Assert.True(macosIdx > 0);
            Assert.True(maccatalystIdx < macosIdx, "maccatalyst detection must come before macos");
        }

        [Fact]
        public void Targets_DetectsIosExplicitly()
        {
            // iOS detection uses Contains('ios') — not an unconditional fallback
            Assert.Contains("TargetFramework.Contains('ios')", TargetsContent);
            Assert.Contains(">ios</_SwiftBindingPlatform>", TargetsContent);
        }

        [Fact]
        public void Targets_FlagsUnsupportedPlatform()
        {
            Assert.Contains("_SwiftBindingPlatformUnsupported", TargetsContent);
        }

        [Fact]
        public void Targets_DefinesNuGetRidPerPlatform()
        {
            Assert.Contains("_SwiftBindingNuGetRid", TargetsContent);
            Assert.Contains("osx-arm64", TargetsContent);
            Assert.Contains("tvos-arm64", TargetsContent);
            Assert.Contains("maccatalyst-arm64", TargetsContent);
            Assert.Contains("ios-arm64", TargetsContent);
        }

        [Fact]
        public void Targets_DefinesSliceIdsPerPlatform()
        {
            Assert.Contains("_SwiftBindingDeviceSliceId", TargetsContent);
            Assert.Contains("macos-arm64", TargetsContent);
            Assert.Contains("ios-arm64-maccatalyst", TargetsContent);
        }

        [Fact]
        public void Targets_DefinesSimulatorSliceForIosAndTvos()
        {
            Assert.Contains("_SwiftBindingSimulatorSliceId", TargetsContent);
            Assert.Contains("_SwiftBindingHasSimulatorSlice", TargetsContent);
            Assert.Contains("ios-arm64-simulator", TargetsContent);
            Assert.Contains("tvos-arm64-simulator", TargetsContent);
        }

        [Fact]
        public void Targets_PlatformTargetConditionalOnSimulatorSlice()
        {
            // SwiftPlatformTarget should only default to 'simulator' for platforms with simulator slices
            Assert.Contains("_SwiftBindingHasSimulatorSlice", TargetsContent);
            Assert.Contains(">simulator</SwiftPlatformTarget>", TargetsContent);
        }

        // ------------------------------------------------------------------
        // ObjC AppleFramework mode (Matter, MatterSupport, etc.):
        // these system frameworks ship with a module.modulemap but no
        // Swift interface. The SDK auto-detects the framework type from
        // the SDK layout and routes the build through bgen instead of the
        // Swift binding generator. The user must declare a THREE-property
        // contract in the csproj body:
        //   <SwiftAppleFrameworkTarget Include="Matter" />  (the framework)
        //   <SwiftFrameworkType>ObjC</SwiftFrameworkType>  (our pipeline gate)
        //   <IsBindingProject>true</IsBindingProject>      (iOS bgen pipeline gate)
        // IsBindingProject must be set in the body (not by a PropertyGroup in
        // our Sdk.props gated on SwiftFrameworkType) because Sdk.props evaluates
        // BEFORE the user csproj body — by the time SwiftFrameworkType is set,
        // Microsoft.iOS.Sdk's binding-project props have already run. These tests
        // pin the detection, validation (SWIFTBIND017/018/019/021), and
        // xcframework-synthesis machinery.
        // ------------------------------------------------------------------

        [Fact]
        public void Targets_AppleFrameworkType_DetectsSwiftFromInterface()
        {
            // Swift interface presence is the positive signal. The exact
            // assignment uses Exists() on _SwiftAppleFrameworkInterface and
            // is gated on the empty default so a user override (declared
            // SwiftFrameworkType) is respected.
            Assert.Contains(
                "<_SwiftAppleFrameworkType Condition=\"Exists('$(_SwiftAppleFrameworkInterface)')\">Swift</_SwiftAppleFrameworkType>",
                TargetsContent);
        }

        [Fact]
        public void Targets_AppleFrameworkType_DetectsObjCFromModulemap()
        {
            // ObjC detection only fires when the Swift branch already failed
            // (mixed frameworks have both — Swift wins). The empty-default
            // guard on the Condition is the load-bearing piece — without it
            // a mixed framework would silently route through bgen.
            Assert.Contains(
                "<_SwiftAppleFrameworkType Condition=\"'$(_SwiftAppleFrameworkType)' == '' AND Exists('$(_SwiftAppleFrameworkModulemap)')\">ObjC</_SwiftAppleFrameworkType>",
                TargetsContent);
        }

        [Fact]
        public void Targets_HasSwiftBind017ErrorCode_NeitherSwiftNorObjC()
        {
            Assert.Contains("SWIFTBIND017", TargetsContent);
            Assert.Contains("neither a Swift interface", TargetsContent);
        }

        [Fact]
        public void Targets_HasSwiftBind018ErrorCode_ObjcRequiresSwiftFrameworkTypeDeclaration()
        {
            // Actionable text must mention BOTH body properties the user has to set.
            // SwiftFrameworkType engages our ObjC pipeline; IsBindingProject engages
            // the .NET iOS workload's bgen pipeline at evaluation time (which is too
            // early for our props to set it conditionally — both must come from the
            // user's csproj body / Directory.Build.props / globals).
            Assert.Contains("SWIFTBIND018", TargetsContent);
            Assert.Contains("&lt;SwiftFrameworkType&gt;ObjC&lt;/SwiftFrameworkType&gt;", TargetsContent);
            Assert.Contains("&lt;IsBindingProject&gt;true&lt;/IsBindingProject&gt;", TargetsContent);
        }

        [Fact]
        public void Targets_HasSwiftBind019ErrorCode_RefuseToRouteSwiftFrameworkToBgen()
        {
            Assert.Contains("SWIFTBIND019", TargetsContent);
            Assert.Contains("has a Swift interface", TargetsContent);
        }

        [Fact]
        public void Targets_HasSwiftBind021ErrorCode_ObjcRequiresIsBindingProject()
        {
            // Companion to SWIFTBIND018: when SwiftFrameworkType=ObjC IS declared but
            // IsBindingProject is missing, surface a separate actionable error pointing
            // at the missing property. Splitting the diagnostics lets users see exactly
            // which property they forgot rather than guessing from a generic message.
            Assert.Contains("SWIFTBIND021", TargetsContent);
            Assert.Contains("&lt;IsBindingProject&gt;true&lt;/IsBindingProject&gt;", TargetsContent);
            Assert.Contains("'$(IsBindingProject)' != 'true'", TargetsContent);
        }

        [Fact]
        public void Targets_AppleFrameworkType_SwiftBindError014_GatedOnSwiftType()
        {
            // SWIFTBIND014 is the original "missing swiftinterface" error.
            // With the type-detection layer it must only fire on Swift frameworks
            // (ObjC frameworks legitimately have no swiftinterface).
            var swiftbind014 = TargetsContent.IndexOf("SWIFTBIND014", StringComparison.Ordinal);
            Assert.True(swiftbind014 > 0);
            // Walk backward to the enclosing <Error tag to inspect its Condition
            var errOpen = TargetsContent.LastIndexOf("<Error", swiftbind014, StringComparison.Ordinal);
            Assert.True(errOpen > 0);
            var errEnd = TargetsContent.IndexOf('>', swiftbind014);
            Assert.True(errEnd > swiftbind014);
            var errTag = TargetsContent.Substring(errOpen, errEnd - errOpen + 1);
            Assert.Contains("'$(_SwiftAppleFrameworkType)' == 'Swift'", errTag);
        }

        [Fact]
        public void Targets_AppleFrameworkPaths_ResolveModulemapPath()
        {
            // Modulemap path must be computed alongside Interface and Tbd paths
            // — used by both detection and fingerprinting.
            Assert.Contains(
                "<_SwiftAppleFrameworkModulemap>$(_SwiftAppleFrameworkDir)/Modules/module.modulemap</_SwiftAppleFrameworkModulemap>",
                TargetsContent);
        }

        [Fact]
        public void Targets_AppleFrameworkPaths_CatalystFallbackProbesModulemap()
        {
            // The Catalyst fallback originally only probed the .swiftmodule directory.
            // ObjC-only frameworks (Matter) have no swiftmodule, so the iOSSupport
            // overlay would short-circuit before falling back to the regular path
            // where the modulemap actually lives.
            Assert.Contains(
                "!Exists('$(_SwiftAppleFrameworkDir)/Modules/module.modulemap')",
                TargetsContent);
        }

        [Fact]
        public void Targets_SynthesizeAppleFrameworkXcframeworkTarget_Exists()
        {
            Assert.Contains("Name=\"_SynthesizeAppleFrameworkXcframework\"", TargetsContent);
        }

        [Fact]
        public void Targets_SynthesizeAppleFrameworkXcframework_RunsBeforeFingerprint()
        {
            // The synthesized xcframework path is one of the fingerprint inputs,
            // so the synthesis target must run BeforeTargets="_ComputeSwiftFingerprint".
            var synthIdx = TargetsContent.IndexOf("Name=\"_SynthesizeAppleFrameworkXcframework\"", StringComparison.Ordinal);
            Assert.True(synthIdx > 0);
            var tagEnd = TargetsContent.IndexOf('>', synthIdx);
            var tag = TargetsContent.Substring(synthIdx, tagEnd - synthIdx);
            Assert.Contains("BeforeTargets=\"_ComputeSwiftFingerprint\"", tag);
            Assert.Contains("DependsOnTargets=\"_DetectSwiftBindingTargetKind;_ResolveAppleFrameworkPaths\"", tag);
        }

        [Fact]
        public void Targets_SynthesizeAppleFrameworkXcframework_HasTaskLevelGating()
        {
            // Target-level Conditions evaluate before DependsOnTargets run, so
            // gating on $(_SwiftAppleFrameworkType) (which is set by
            // _ResolveAppleFrameworkPaths) MUST happen at task level. A Target-
            // level Condition would always evaluate to false (property still
            // empty at gate time) and the synthesis would never fire.
            var synthIdx = TargetsContent.IndexOf("Name=\"_SynthesizeAppleFrameworkXcframework\"", StringComparison.Ordinal);
            Assert.True(synthIdx > 0);
            var tagEnd = TargetsContent.IndexOf('>', synthIdx);
            var tag = TargetsContent.Substring(synthIdx, tagEnd - synthIdx);
            // No target-level Condition referencing _SwiftAppleFrameworkType
            Assert.DoesNotContain("_SwiftAppleFrameworkType", tag);
            // Each child task within the body IS gated on ObjC type.
            // Find the end of this target's body (closing </Target>).
            var bodyEnd = TargetsContent.IndexOf("</Target>", synthIdx, StringComparison.Ordinal);
            var body = TargetsContent.Substring(synthIdx, bodyEnd - synthIdx);
            // RemoveDir, MakeDir, Exec, ItemGroup, WriteLinesToFile each gated
            Assert.Contains("<RemoveDir Condition=\"'$(_SwiftBindingTargetKind)' == 'AppleFramework' AND '$(_SwiftAppleFrameworkType)' == 'ObjC'\"", body);
            Assert.Contains("<MakeDir Condition=\"'$(_SwiftBindingTargetKind)' == 'AppleFramework' AND '$(_SwiftAppleFrameworkType)' == 'ObjC'\"", body);
            Assert.Contains("<Exec Condition=\"'$(_SwiftBindingTargetKind)' == 'AppleFramework' AND '$(_SwiftAppleFrameworkType)' == 'ObjC'\"", body);
            Assert.Contains("<WriteLinesToFile Condition=\"'$(_SwiftBindingTargetKind)' == 'AppleFramework' AND '$(_SwiftAppleFrameworkType)' == 'ObjC'\"", body);
        }

        [Fact]
        public void Targets_SynthesizeAppleFrameworkXcframework_EmitsInfoPlistWithCorrectKeys()
        {
            // Info.plist must declare AvailableLibraries with LibraryIdentifier,
            // LibraryPath (=Module.framework), SupportedArchitectures (arm64),
            // SupportedPlatform, and optionally SupportedPlatformVariant.
            // XCFrameworkResolver.SelectSlice keys on these.
            Assert.Contains("&lt;key&gt;AvailableLibraries&lt;/key&gt;", TargetsContent);
            Assert.Contains("&lt;key&gt;LibraryIdentifier&lt;/key&gt;", TargetsContent);
            Assert.Contains("&lt;key&gt;LibraryPath&lt;/key&gt;", TargetsContent);
            Assert.Contains("&lt;key&gt;SupportedArchitectures&lt;/key&gt;", TargetsContent);
            Assert.Contains("&lt;key&gt;SupportedPlatform&lt;/key&gt;", TargetsContent);
            Assert.Contains("&lt;key&gt;SupportedPlatformVariant&lt;/key&gt;", TargetsContent);
            // CFBundlePackageType=XFWK + XCFrameworkFormatVersion=1.0 are
            // required by xcodebuild's xcframework spec.
            Assert.Contains("&lt;string&gt;XFWK&lt;/string&gt;", TargetsContent);
            Assert.Contains("&lt;key&gt;XCFrameworkFormatVersion&lt;/key&gt;", TargetsContent);
        }

        [Fact]
        public void Targets_SynthesizeAppleFrameworkXcframework_OmitsVariantForDevice()
        {
            // SupportedPlatformVariant must be conditionally emitted — Apple's
            // convention is to OMIT the key entirely for plain device slices.
            // XCFrameworkResolver.SelectSlice treats null/missing as device.
            // Both the <key> AND <string> lines must be gated on _AFW_SynthVariant.
            Assert.Contains(
                "Include=\"            &lt;key&gt;SupportedPlatformVariant&lt;/key&gt;\"\n                            Condition=\"'$(_AFW_SynthVariant)' != ''\"",
                TargetsContent);
        }

        [Fact]
        public void Targets_AppleFrameworkFingerprint_HashesModulemapAndHeaders()
        {
            // The fingerprint must include the modulemap and header hashes so
            // that ObjC framework changes invalidate the cache. The Swift-only
            // fingerprint (interface + tbd) is insufficient for ObjC mode.
            var fingerprintIdx = TargetsContent.IndexOf(
                "Apple-framework mode fingerprint", StringComparison.Ordinal);
            Assert.True(fingerprintIdx > 0);
            // Pull the block up to the closing </Exec> tag of the fingerprint Exec.
            var blockEnd = TargetsContent.IndexOf("</Exec>", fingerprintIdx, StringComparison.Ordinal);
            var block = TargetsContent.Substring(fingerprintIdx, blockEnd - fingerprintIdx);
            Assert.Contains("_SwiftAppleFrameworkModulemap", block);
            Assert.Contains("$(_SwiftAppleFrameworkDir)/Headers", block);
            // Type must be in the metadata string so a Swift↔ObjC switch
            // invalidates the cache even if every other input is identical.
            Assert.Contains("$(_SwiftAppleFrameworkType)", block);
            // find -L follows symlinked Headers trees so frameworks that use
            // a Versions/Current/Headers symlink layout (macOS-style) have their
            // public header surface fully covered by the fingerprint. iOS-style
            // flat Headers/ frameworks are unaffected (no symlinks to follow).
            Assert.Contains("find -L", block);
        }

        [Fact]
        public void Targets_AppleFrameworkAbiDump_GatedOnSwiftType()
        {
            // _DumpAppleFrameworkAbi runs swift-api-digester which only applies
            // to Swift frameworks. ObjC frameworks have no Swift module to dump.
            var abiDumpIdx = TargetsContent.IndexOf("_DumpAppleFrameworkAbi", StringComparison.Ordinal);
            Assert.True(abiDumpIdx > 0);
            // Find the next <Exec inside this target — that's the digester invocation.
            var execStart = TargetsContent.IndexOf("<Exec", abiDumpIdx, StringComparison.Ordinal);
            Assert.True(execStart > 0);
            var execTagEnd = TargetsContent.IndexOf('>', execStart);
            var execTag = TargetsContent.Substring(execStart, execTagEnd - execStart);
            Assert.Contains("'$(_SwiftAppleFrameworkType)' == 'Swift'", execTag);
        }

        [Fact]
        public void Targets_GenerateBindings_ObjCBranchPassesObjcFlag()
        {
            // The ObjC PropertyGroup must add --objc and point --xcframework
            // at the synthesized xcframework (NOT the SDK framework directly,
            // which would fail the XCFrameworkResolver's Info.plist validation).
            var generateIdx = TargetsContent.IndexOf(
                "Name=\"_GenerateSwiftBindingsAppleFramework\"", StringComparison.Ordinal);
            Assert.True(generateIdx > 0);
            var generateEnd = TargetsContent.IndexOf("</Target>", generateIdx, StringComparison.Ordinal);
            var generateBody = TargetsContent.Substring(generateIdx, generateEnd - generateIdx);
            // ObjC-gated PropertyGroup exists
            Assert.Contains(
                "'$(_SwiftAppleFrameworkType)' == 'ObjC' AND '$(_SwiftBindingUpToDate)' != 'true'",
                generateBody);
            // --objc flag emitted
            Assert.Contains("--objc", generateBody);
            // --xcframework points at the synthesized xcframework
            Assert.Contains("$(_SwiftAppleFrameworkSynthXcfw)", generateBody);
            // Swift PropertyGroup is itself gated on 'Swift' type (so ObjC mode
            // does NOT silently invoke the Swift binding pipeline).
            Assert.Contains(
                "'$(_SwiftAppleFrameworkType)' == 'Swift' AND '$(_SwiftBindingUpToDate)' != 'true'",
                generateBody);
        }

        [Fact]
        public void Targets_GenerateBindingsAppleFramework_DependsOnSynthesisAndMetadata()
        {
            // The generation target must chain through _SynthesizeAppleFrameworkXcframework
            // so the synth output exists before the generator runs in ObjC mode.
            var generateIdx = TargetsContent.IndexOf(
                "Name=\"_GenerateSwiftBindingsAppleFramework\"", StringComparison.Ordinal);
            Assert.True(generateIdx > 0);
            var tagEnd = TargetsContent.IndexOf('>', generateIdx);
            var tag = TargetsContent.Substring(generateIdx, tagEnd - generateIdx);
            var depsMatch = Regex.Match(tag, "DependsOnTargets=\"([^\"]*)\"");
            Assert.True(depsMatch.Success);
            var deps = depsMatch.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("_SynthesizeAppleFrameworkXcframework", deps);
            Assert.Contains("_CollectSwiftModuleDatabases", deps);
        }

        [Fact]
        public void Targets_BindingMetadataPropsSynthesis_RecordsFrameworkType()
        {
            // The metadata props synthesized for AppleFramework mode must carry
            // _SwiftBindingFrameworkType so downstream consumers (Apple supplement
            // injection, packaging) can distinguish Swift- vs ObjC-rooted bindings.
            Assert.Contains("_SwiftBindingFrameworkType&gt;$(_SwiftAppleFrameworkType)&lt;/_SwiftBindingFrameworkType", TargetsContent);
        }

        [Fact]
        public void Targets_AppleFrameworkEffectiveMinVersion_CascadesPerPlatform()
        {
            // _SwiftEffectiveMinDeploymentVersion resolves the slice-specific min:
            // starts from the legacy MinDeploymentVersion, then per-platform overrides
            // (MinIOSVersion / MinMacOSVersion / MinTvOSVersion / MinMacCatalystVersion)
            // win for the matching slice. The legacy field already falls back to
            // $(SwiftAppleFrameworkMinDeploymentVersion) via the prior ItemGroup Update.
            Assert.Contains(
                "<_SwiftEffectiveMinDeploymentVersion>%(SwiftAppleFrameworkTarget.MinDeploymentVersion)</_SwiftEffectiveMinDeploymentVersion>",
                TargetsContent);
            Assert.Contains(
                "'$(_SwiftBindingPlatform)' == 'ios' AND '%(SwiftAppleFrameworkTarget.MinIOSVersion)' != ''",
                TargetsContent);
            Assert.Contains(
                "'$(_SwiftBindingPlatform)' == 'macos' AND '%(SwiftAppleFrameworkTarget.MinMacOSVersion)' != ''",
                TargetsContent);
            Assert.Contains(
                "'$(_SwiftBindingPlatform)' == 'tvos' AND '%(SwiftAppleFrameworkTarget.MinTvOSVersion)' != ''",
                TargetsContent);
            Assert.Contains(
                "'$(_SwiftBindingPlatform)' == 'maccatalyst' AND '%(SwiftAppleFrameworkTarget.MinMacCatalystVersion)' != ''",
                TargetsContent);
        }

        [Fact]
        public void Targets_DigesterTriple_UsesEffectiveMinVersion()
        {
            // The swift-api-digester target triple must read from
            // _SwiftEffectiveMinDeploymentVersion so per-platform overrides flow
            // through. Reading %(SwiftAppleFrameworkTarget.MinDeploymentVersion)
            // directly was the bug shape: a single iOS-flavoured value (e.g. 16.1)
            // produced 'arm64-apple-macos16.1' on the macOS slice, which is invalid
            // (macOS version train is 13.x-26.x).
            Assert.Contains(
                "arm64-apple-macos$(_SwiftEffectiveMinDeploymentVersion)",
                TargetsContent);
            Assert.Contains(
                "arm64-apple-ios$(_SwiftEffectiveMinDeploymentVersion)-macabi",
                TargetsContent);
            Assert.Contains(
                "arm64-apple-ios$(_SwiftEffectiveMinDeploymentVersion)-simulator",
                TargetsContent);
            Assert.Contains(
                "arm64-apple-tvos$(_SwiftEffectiveMinDeploymentVersion)-simulator",
                TargetsContent);
            // No usage of the raw item metadata inside the triple block.
            var tripleIdx = TargetsContent.IndexOf(
                "Compute swift-api-digester target triple", StringComparison.Ordinal);
            Assert.True(tripleIdx > 0);
            var tripleEnd = TargetsContent.IndexOf("</PropertyGroup>", tripleIdx, StringComparison.Ordinal);
            var tripleBlock = TargetsContent.Substring(tripleIdx, tripleEnd - tripleIdx);
            Assert.DoesNotContain("%(SwiftAppleFrameworkTarget.MinDeploymentVersion)", tripleBlock);
        }

        [Fact]
        public void Targets_BindingMetadataProps_UsesEffectiveMinVersion()
        {
            // The per-TFM binding-metadata.props file feeds SupportedOSPlatformVersion
            // on the consumer project; it must record the slice-specific minimum, not
            // the iOS-flavoured legacy field. (Otherwise a macOS consumer would inherit
            // an invalid SupportedOSPlatformVersion that does not exist on the macOS
            // version train.)
            Assert.Contains(
                "_SwiftBindingMinimumOSVersion&gt;$(_SwiftEffectiveMinDeploymentVersion)&lt;/_SwiftBindingMinimumOSVersion",
                TargetsContent);
        }

        [Fact]
        public void Targets_SecondSliceMerge_UsesEffectiveMinVersion()
        {
            // The device/simulator second-slice compile uses its own target triple
            // (_AFW_OtherTarget) and writes the framework's Info.plist
            // MinimumOSVersion (_AFW_OtherMinOsVersion). Both must read from
            // _SwiftEffectiveMinDeploymentVersion so per-platform overrides keep
            // both slices on the same min.
            Assert.Contains(
                "<_AFW_OtherTarget Condition=\"'$(_SwiftBindingPlatform)' == 'ios' AND '$(SwiftPlatformTarget)' == 'simulator'\">arm64-apple-ios$(_SwiftEffectiveMinDeploymentVersion)</_AFW_OtherTarget>",
                TargetsContent);
            Assert.Contains(
                "<_AFW_OtherMinOsVersion>$(_SwiftEffectiveMinDeploymentVersion)</_AFW_OtherMinOsVersion>",
                TargetsContent);
        }

        [Fact]
        public void Targets_AppleFrameworkAbiDump_FailsOnDegenerateOutput()
        {
            // swift-api-digester exits 0 even when the target triple is rejected,
            // writing a placeholder abi.json whose top-level name is "NO_MODULE".
            // _DumpAppleFrameworkAbi must catch that and surface SWIFTBIND038
            // instead of letting Swift.Bindings.dll misdiagnose it later as a
            // BUILD_LIBRARY_FOR_DISTRIBUTION problem. The detection must match the
            // literal JSON field ("name": "NO_MODULE") rather than a bare substring,
            // so a Swift symbol named NO_MODULE inside a valid dump cannot trip it.
            var abiDumpIdx = TargetsContent.IndexOf(
                "Name=\"_DumpAppleFrameworkAbi\"", StringComparison.Ordinal);
            Assert.True(abiDumpIdx > 0);
            var abiDumpEnd = TargetsContent.IndexOf("</Target>", abiDumpIdx, StringComparison.Ordinal);
            var abiDumpBody = TargetsContent.Substring(abiDumpIdx, abiDumpEnd - abiDumpIdx);
            Assert.Contains("&quot;name&quot;[[:space:]]*:[[:space:]]*&quot;NO_MODULE&quot;", abiDumpBody);
            Assert.Contains("SWIFTBIND038", abiDumpBody);
            // The error must point at per-platform overrides as the user-facing fix.
            Assert.Contains("MinMacOSVersion", abiDumpBody);
        }

        [Fact]
        public void Targets_EffectiveMinVersion_SeedsFromLegacyFirst()
        {
            // The cascade must SEED _SwiftEffectiveMinDeploymentVersion from the legacy
            // %(MinDeploymentVersion) FIRST and apply per-platform overrides AFTER. If
            // the order flipped, an explicit user MinDeploymentVersion would clobber a
            // platform-specific override (because the unconditional seed would always
            // win as the last-evaluated assignment).
            var cascadeIdx = TargetsContent.IndexOf(
                "<_SwiftEffectiveMinDeploymentVersion>%(SwiftAppleFrameworkTarget.MinDeploymentVersion)</_SwiftEffectiveMinDeploymentVersion>",
                StringComparison.Ordinal);
            Assert.True(cascadeIdx > 0, "Seed line must be present.");
            var iosOverrideIdx = TargetsContent.IndexOf(
                "'$(_SwiftBindingPlatform)' == 'ios' AND '%(SwiftAppleFrameworkTarget.MinIOSVersion)' != ''",
                StringComparison.Ordinal);
            Assert.True(iosOverrideIdx > cascadeIdx, "Per-platform overrides must come AFTER the legacy seed.");
        }

        // Returns the opening <Target …> tag text (everything up to the first '>')
        // for the target whose Name attribute is `targetName`. None of the relevant
        // target attributes (AfterTargets/BeforeTargets/Condition) contain a literal
        // '>', so the first '>' reliably closes the opening element.
        private static string ExtractOpeningTag(string targetName)
        {
            var anchor = TargetsContent.IndexOf($"Name=\"{targetName}\"", StringComparison.Ordinal);
            Assert.True(anchor >= 0, $"Target '{targetName}' not found in Sdk.targets.");
            var rest = TargetsContent.Substring(anchor);
            var endOfTag = rest.IndexOf('>', StringComparison.Ordinal);
            return rest.Substring(0, endOfTag);
        }

        // Returns the value of `attribute` from the opening tag of `targetName`.
        private static string ExtractAttribute(string targetName, string attribute)
        {
            var tag = ExtractOpeningTag(targetName);
            var key = $"{attribute}=\"";
            var start = tag.IndexOf(key, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Attribute '{attribute}' not found on target '{targetName}'.");
            start += key.Length;
            var end = tag.IndexOf('"', start);
            return tag.Substring(start, end - start);
        }

        // Returns the full <Target>…</Target> block (opening tag through closing tag)
        // for `targetName`, so Contains assertions are scoped to a single target and
        // can't accidentally match an identical string in a sibling target.
        private static string ExtractTargetBlock(string targetName)
        {
            var anchor = TargetsContent.IndexOf($"Name=\"{targetName}\"", StringComparison.Ordinal);
            Assert.True(anchor >= 0, $"Target '{targetName}' not found in Sdk.targets.");
            var close = TargetsContent.IndexOf("</Target>", anchor, StringComparison.Ordinal);
            Assert.True(close >= 0, $"Closing </Target> for '{targetName}' not found.");
            return TargetsContent.Substring(anchor, close - anchor);
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
