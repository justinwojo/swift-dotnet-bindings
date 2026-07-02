// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    #region A. Basic Targets Emission Tests

    public class ConsumerTargetsBasicTests
    {
        [Fact]
        public void Emit_CreatesTargetsFile()
        {
            var dir = CreateTempDir();
            try
            {
                EmitTargets(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: true);
                Assert.True(File.Exists(Path.Combine(dir, "ImagePipeline.Swift.iOS.targets")));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SourceNativeRef_Present()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("ImagePipeline.xcframework", content);
                Assert.Contains("<Kind>Framework</Kind>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WrapperNativeRef_PresentWhenHasWrapper()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: true);
                Assert.Contains("ImagePipelineSwiftBindings.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WrapperNativeRef_AbsentWhenNoWrapper()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.DoesNotContain("ImagePipelineSwiftBindings.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir() => ConsumerTargetsTestHelper.CreateTempDir();
        private static void EmitTargets(string dir, string module, string packageId, string minOS, bool hasWrapper)
            => ConsumerTargetsTestHelper.EmitTargets(dir, module, packageId, minOS, hasWrapper);
        private static string EmitAndRead(string dir, string module, string packageId, string minOS, bool hasWrapper)
            => ConsumerTargetsTestHelper.EmitAndRead(dir, module, packageId, minOS, hasWrapper);
    }

    #endregion

    #region B. Target Structure Tests

    public class ConsumerTargetsStructureTests
    {
        [Fact]
        public void Emit_IdempotencyGuard_Present()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("_SwiftBinding_ImagePipeline_Injected", content);
                Assert.Contains("Condition=\"'$(_SwiftBinding_ImagePipeline_Injected)' != 'true'\"", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SwiftBindingFramework_Registration()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("<SwiftBindingFramework Include=\"ImagePipeline\">", content);
                Assert.Contains("<SourcePackage>ImagePipeline.Swift.iOS</SourcePackage>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_PlatformVersionWarning_Present()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("SWIFTBIND011", content);
                Assert.Contains("System.Version", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_PlatformVersionWarning_UsesEffectiveMinOS()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "DocScan", "DocScan.Swift.iOS", "16.0", hasWrapper: false);
                Assert.Contains("16.0", content);
                Assert.Contains("requires iOS 16.0+", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_BeforeTargets_Correct()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("BeforeTargets=\"ResolveNativeReferences\"", content);
                Assert.Contains("BeforeTargets=\"Build\"", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_MSBuildThisFileDirectory_InPaths()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: true);
                Assert.Contains("$(MSBuildThisFileDirectory)", content);
                Assert.Contains("../../runtimes/ios-arm64/native/", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_VersionCompare_UsesCompareToNotLessThan()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                // Must use CompareTo(...) < 0 (numeric), NOT < between Version objects (lexicographic)
                Assert.Contains(".CompareTo(", content);
                Assert.Contains("&lt; 0", content);
                Assert.Contains("$([System.Version]::Parse('$(SupportedOSPlatformVersion)').CompareTo(", content);
                Assert.Contains("$([System.Version]::Parse('15.0')", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        /// <summary>
        /// Validates that the Version.CompareTo logic the emitted condition relies on
        /// produces correct results for edge-case version pairs. This catches the
        /// lexicographic-vs-numeric ordering bug (e.g., "15.10" vs "15.2").
        /// </summary>
        [Theory]
        [InlineData("13.0", "15.0", true)]   // below minimum → should warn
        [InlineData("15.0", "15.0", false)]   // exactly at minimum → no warn
        [InlineData("16.0", "15.0", false)]   // above minimum → no warn
        [InlineData("15.2", "15.10", true)]   // 15.2 < 15.10 numerically → should warn
        [InlineData("15.10", "15.2", false)]  // 15.10 > 15.2 numerically → no warn
        [InlineData("14.99", "15.0", true)]   // just below → should warn
        [InlineData("15.0", "16.4", true)]    // below higher minimum → should warn
        [InlineData("18.0", "16.4", false)]   // well above → no warn
        public void VersionCompareTo_ProducesCorrectOrdering(
            string consumerVersion, string minimumVersion, bool shouldWarn)
        {
            // This mirrors the MSBuild condition:
            // Version.Parse(consumer).CompareTo(Version.Parse(minimum)) < 0
            var consumer = Version.Parse(consumerVersion);
            var minimum = Version.Parse(minimumVersion);
            var wouldWarn = consumer.CompareTo(minimum) < 0;

            Assert.Equal(shouldWarn, wouldWarn);
        }

        private static string CreateTempDir() => ConsumerTargetsTestHelper.CreateTempDir();
        private static string EmitAndRead(string dir, string module, string packageId, string minOS, bool hasWrapper)
            => ConsumerTargetsTestHelper.EmitAndRead(dir, module, packageId, minOS, hasWrapper);
    }

    #endregion

    #region C. Module Name Sanitization Tests

    public class ConsumerTargetsSanitizationTests
    {
        [Fact]
        public void SanitizeModuleName_DotsReplacedWithUnderscores()
        {
            Assert.Equal("My_Module", ConsumerTargetsEmitter.SanitizeModuleName("My.Module"));
        }

        [Fact]
        public void SanitizeModuleName_HyphensReplacedWithUnderscores()
        {
            Assert.Equal("My_Module", ConsumerTargetsEmitter.SanitizeModuleName("My-Module"));
        }

        [Fact]
        public void SanitizeModuleName_SimpleNameUnchanged()
        {
            Assert.Equal("ImagePipeline", ConsumerTargetsEmitter.SanitizeModuleName("ImagePipeline"));
        }

        [Fact]
        public void Emit_SanitizedModuleName_InTargetNames()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "My.Module", "My.Module.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("_ResolveMy_ModuleNativeReferences", content);
                Assert.Contains("_ValidateMy_ModulePlatformVersion", content);
                Assert.Contains("_SwiftBinding_My_Module_Injected", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir() => ConsumerTargetsTestHelper.CreateTempDir();
        private static string EmitAndRead(string dir, string module, string packageId, string minOS, bool hasWrapper)
            => ConsumerTargetsTestHelper.EmitAndRead(dir, module, packageId, minOS, hasWrapper);
    }

    #endregion

    #region C2. SwiftBindingsInteropMode Tests

    public class ConsumerTargetsInteropModeTests
    {
        [Fact]
        public void Emit_InteropMode_DefaultAutoProperty()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("SwiftBindingsInteropMode", content);
                Assert.Contains("Auto", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_InteropMode_PublishAotCondition()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("$(PublishAot)", content);
                Assert.Contains("'true'", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_InteropMode_DirectSuppressesSB0001()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("SB0001", content);
                Assert.Contains("<NoWarn>$(NoWarn);SB0001</NoWarn>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_InteropMode_DoesNotSuppressSB0002()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                // SB0002 (missing symbol) should never be suppressed — always relevant regardless of runtime
                Assert.DoesNotContain("SB0002", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_InteropMode_DirectModeCondition()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("'$(SwiftBindingsInteropMode)' == 'Direct'", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_InteropMode_AutoResolvesToSafeByDefault()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                // Second PropertyGroup: when still Auto (PublishAot != true), resolves to Safe
                Assert.Contains("'$(SwiftBindingsInteropMode)' == 'Auto'", content);
                Assert.Contains(">Safe<", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir() => ConsumerTargetsTestHelper.CreateTempDir();
        private static string EmitAndRead(string dir, string module, string packageId, string minOS, bool hasWrapper)
            => ConsumerTargetsTestHelper.EmitAndRead(dir, module, packageId, minOS, hasWrapper);
    }

    #endregion

    #region D. NativeReference Exists() Guard Tests

    public class ConsumerTargetsExistsGuardTests
    {
        [Fact]
        public void Emit_SourceNativeRef_HasExistsCondition()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("Condition=\"Exists('$(MSBuildThisFileDirectory)../../runtimes/ios-arm64/native/ImagePipeline.xcframework')\"", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WrapperNativeRef_HasExistsCondition()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: true);
                Assert.Contains("Condition=\"Exists('$(MSBuildThisFileDirectory)../../runtimes/ios-arm64/native/ImagePipelineSwiftBindings.xcframework')\"", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_NativeRefPaths_UseRelativeFromBuildTransitive()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "TestLib", "TestLib.Swift.iOS", "15.0", hasWrapper: true);
                Assert.Contains("../../runtimes/ios-arm64/native/TestLib.xcframework", content);
                Assert.Contains("../../runtimes/ios-arm64/native/TestLibSwiftBindings.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SourceNativeRef_ContainsModuleName()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "DocScan", "DocScan.Swift.iOS", "16.0", hasWrapper: false);
                Assert.Contains("DocScan.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WrapperNativeRef_ContainsSwiftBindingsSuffix()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "VectorAnimation", "VectorAnimation.Swift.iOS", "15.0", hasWrapper: true);
                Assert.Contains("VectorAnimationSwiftBindings.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir() => ConsumerTargetsTestHelper.CreateTempDir();
        private static string EmitAndRead(string dir, string module, string packageId, string minOS, bool hasWrapper)
            => ConsumerTargetsTestHelper.EmitAndRead(dir, module, packageId, minOS, hasWrapper);
    }

    #endregion

    #region E. SwiftModuleDatabase Tests

    public class ConsumerTargetsModuleDatabaseTests
    {
        [Fact]
        public void Emit_SwiftModuleDatabase_ItemPresent()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("<SwiftModuleDatabase Include=", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SwiftModuleDatabase_HasModuleNameMetadata()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("<ModuleName>ImagePipeline</ModuleName>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SwiftModuleDatabase_HasSourcePackageMetadata()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("<SourcePackage>ImagePipeline.Swift.iOS</SourcePackage>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SwiftModuleDatabase_HasExistsCondition()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("Condition=\"Exists('$(MSBuildThisFileDirectory)ImagePipelineDatabase.xml')\"", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SwiftModuleDatabase_DatabaseFilename()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("ImagePipelineDatabase.xml", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir() => ConsumerTargetsTestHelper.CreateTempDir();
        private static string EmitAndRead(string dir, string module, string packageId, string minOS, bool hasWrapper)
            => ConsumerTargetsTestHelper.EmitAndRead(dir, module, packageId, minOS, hasWrapper);
    }

    #endregion

    #region F. Bridge NativeReference Tests

    public class ConsumerTargetsBridgeTests
    {
        [Fact]
        public void Emit_BridgeNativeRef_PresentWhenHasBridge()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0",
                    hasWrapper: true, hasBridge: true);
                Assert.Contains("ImagePipelineBridge.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_BridgeNativeRef_AbsentWhenNoBridge()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0",
                    hasWrapper: true, hasBridge: false);
                Assert.DoesNotContain("ImagePipelineBridge.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_BridgeAndWrapper_BothPresent()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "VectorAnimation", "VectorAnimation.Swift.iOS", "15.0",
                    hasWrapper: true, hasBridge: true);
                Assert.Contains("VectorAnimationSwiftBindings.xcframework", content);
                Assert.Contains("VectorAnimationBridge.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_ProjectReferenceTargets_ContainsBridgeRef()
        {
            var dir = CreateTempDir();
            try
            {
                ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    PackageId = "ImagePipeline.Swift.iOS",
                    EffectiveMinimumOSVersion = "15.0",
                    HasWrapperXCFramework = true,
                    HasBridgeXCFramework = true,
                }, NullLogger.Instance);

                var localContent = File.ReadAllText(
                    Path.Combine(dir, "ImagePipeline.Swift.iOS.ProjectReference.targets"));
                Assert.Contains("ImagePipelineBridge.xcframework", localContent);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_BridgeNativeRef_ExcludedOnNativeMacOS()
        {
            // The SwiftUI bridge is UIKit-only: its macOS slice is an empty Mach-O
            // that fails a native-macOS consumer with Xamarin MT158. The bridge
            // NativeReference must carry the !Contains('-macos') gate so a native-macOS
            // consumer never references it. Mac Catalyst keeps it (TFM lacks "-macos").
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0",
                    hasWrapper: true, hasBridge: true);
                Assert.Contains("ImagePipelineBridge.xcframework') AND !$(TargetFramework.Contains('-macos'))", content);
                // The wrapper carries the real ABI and is required on macOS — it must
                // NOT inherit the bridge's macOS exclusion.
                Assert.DoesNotContain("ImagePipelineSwiftBindings.xcframework') AND !$(TargetFramework.Contains('-macos'))", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_ProjectReferenceTargets_BridgeRefExcludedOnNativeMacOS()
        {
            var dir = CreateTempDir();
            try
            {
                ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    PackageId = "ImagePipeline.Swift.iOS",
                    EffectiveMinimumOSVersion = "15.0",
                    HasWrapperXCFramework = true,
                    HasBridgeXCFramework = true,
                }, NullLogger.Instance);

                var localContent = File.ReadAllText(
                    Path.Combine(dir, "ImagePipeline.Swift.iOS.ProjectReference.targets"));
                Assert.Contains("ImagePipelineBridge.xcframework') AND !$(TargetFramework.Contains('-macos'))", localContent);
                Assert.DoesNotContain("ImagePipelineSwiftBindings.xcframework') AND !$(TargetFramework.Contains('-macos'))", localContent);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir() => ConsumerTargetsTestHelper.CreateTempDir();

        private static string EmitAndRead(string dir, string module, string packageId,
            string minOS, bool hasWrapper, bool hasBridge)
        {
            ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
            {
                OutputDirectory = dir,
                ModuleName = module,
                PackageId = packageId,
                EffectiveMinimumOSVersion = minOS,
                HasWrapperXCFramework = hasWrapper,
                HasBridgeXCFramework = hasBridge,
            }, NullLogger.Instance);
            return File.ReadAllText(Path.Combine(dir, $"{packageId}.targets"));
        }
    }

    #endregion

    #region G. MacCatalyst-x64 Mono Interpreter Auto-Workaround (upstream-issue-04)

    public class ConsumerTargetsMacCatalystX64WorkaroundTests
    {
        [Fact]
        public void Emit_MacCatalystX64Workaround_PropertyGroupPresent()
        {
            var dir = ConsumerTargetsTestHelper.CreateTempDir();
            try
            {
                var content = ConsumerTargetsTestHelper.EmitAndRead(
                    dir, "ImagePipeline", "ImagePipeline.Swift.MacCatalyst", "15.0", hasWrapper: true);
                Assert.Contains("'$(RuntimeIdentifier)' == 'maccatalyst-x64'", content);
                Assert.Contains("MtouchInterpreter", content);
                Assert.Contains("UseInterpreter", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_MacCatalystX64Workaround_HasOptOutGate()
        {
            var dir = ConsumerTargetsTestHelper.CreateTempDir();
            try
            {
                var content = ConsumerTargetsTestHelper.EmitAndRead(
                    dir, "ImagePipeline", "ImagePipeline.Swift.MacCatalyst", "15.0", hasWrapper: true);
                Assert.Contains("SwiftBindingsMacCatalystX64UseJit", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_MacCatalystX64Workaround_RespectsConsumerOverride()
        {
            var dir = ConsumerTargetsTestHelper.CreateTempDir();
            try
            {
                var content = ConsumerTargetsTestHelper.EmitAndRead(
                    dir, "ImagePipeline", "ImagePipeline.Swift.MacCatalyst", "15.0", hasWrapper: true);
                Assert.Contains("<MtouchInterpreter Condition=\"'$(MtouchInterpreter)' == ''\">all</MtouchInterpreter>", content);
                Assert.Contains("<UseInterpreter Condition=\"'$(UseInterpreter)' == ''\">true</UseInterpreter>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_MacCatalystX64Workaround_AnnounceMessagePresent()
        {
            var dir = ConsumerTargetsTestHelper.CreateTempDir();
            try
            {
                var content = ConsumerTargetsTestHelper.EmitAndRead(
                    dir, "ImagePipeline", "ImagePipeline.Swift.MacCatalyst", "15.0", hasWrapper: true);
                Assert.Contains("_SwiftBindingsAnnounceMacCatalystX64Workaround", content);
                Assert.Contains("Importance=\"high\"", content);
                Assert.Contains("upstream-issue-04", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_MacCatalystX64Workaround_AnnouncementIsAccurate_NotMisleading()
        {
            // The message must not claim we *forced* MtouchInterpreter / UseInterpreter
            // to specific values — the inner Condition guards make the assignments
            // default-only, so a consumer who pre-set UseInterpreter=false keeps their
            // value, and a "forcing UseInterpreter=true" message would be a lie.
            // Instead it must describe the action as defaulting and expand the live
            // values so the build log shows what the consumer actually ended up with.
            var dir = ConsumerTargetsTestHelper.CreateTempDir();
            try
            {
                var content = ConsumerTargetsTestHelper.EmitAndRead(
                    dir, "ImagePipeline", "ImagePipeline.Swift.MacCatalyst", "15.0", hasWrapper: true);
                Assert.DoesNotContain("forcing Mono interpreter", content);
                Assert.Contains("defaults MtouchInterpreter and UseInterpreter", content);
                Assert.Contains("$(MtouchInterpreter)", content);
                Assert.Contains("$(UseInterpreter)", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_MacCatalystX64Workaround_AnnouncementPointsAtPublicDocs()
        {
            // The message must include a pointer the end-user can actually follow.
            // The source-tree path lives in a comment; the user-facing Text needs the wiki URL.
            var dir = ConsumerTargetsTestHelper.CreateTempDir();
            try
            {
                var content = ConsumerTargetsTestHelper.EmitAndRead(
                    dir, "ImagePipeline", "ImagePipeline.Swift.MacCatalyst", "15.0", hasWrapper: true);
                Assert.Contains("https://github.com/justinwojo/swift-dotnet-bindings/wiki/Known-Limitations", content);
                // The wiki URL must appear inside the <Message Text="..."> attribute, not just in a comment.
                var messageIdx = content.IndexOf("_SwiftBindingsAnnounceMacCatalystX64Workaround", StringComparison.Ordinal);
                Assert.True(messageIdx > 0);
                var afterTarget = content[messageIdx..];
                Assert.Contains("https://github.com/justinwojo/swift-dotnet-bindings/wiki/Known-Limitations", afterTarget);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_MacCatalystX64Workaround_AnnounceIdempotencyGuardShared()
        {
            var dir = ConsumerTargetsTestHelper.CreateTempDir();
            try
            {
                var contentA = ConsumerTargetsTestHelper.EmitAndRead(
                    dir, "ImagePipeline", "ImagePipeline.Swift.MacCatalyst", "15.0", hasWrapper: true);
                var contentB = ConsumerTargetsTestHelper.EmitAndRead(
                    dir, "VectorAnimation", "VectorAnimation.Swift.MacCatalyst", "15.0", hasWrapper: true);
                Assert.Contains("_SwiftBindingsMacCatalystX64WorkaroundAnnounced", contentA);
                Assert.Contains("_SwiftBindingsMacCatalystX64WorkaroundAnnounced", contentB);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_MacCatalystX64Workaround_AppliesToProjectReferenceTargets()
        {
            // The workaround must also ship in the .ProjectReference.targets file
            // used by local <ProjectReference> consumers; if it only lived in the
            // nupkg buildTransitive .targets, local-dev PR workflows on maccatalyst-x64
            // would silently get the crashing JIT path.
            var dir = ConsumerTargetsTestHelper.CreateTempDir();
            try
            {
                ConsumerTargetsTestHelper.EmitTargets(
                    dir, "ImagePipeline", "ImagePipeline.Swift.MacCatalyst", "15.0", hasWrapper: true);
                var projRefPath = Path.Combine(dir, "ImagePipeline.Swift.MacCatalyst.ProjectReference.targets");
                Assert.True(File.Exists(projRefPath));
                var content = File.ReadAllText(projRefPath);
                Assert.Contains("'$(RuntimeIdentifier)' == 'maccatalyst-x64'", content);
                Assert.Contains("SwiftBindingsMacCatalystX64UseJit", content);
                Assert.Contains("_SwiftBindingsAnnounceMacCatalystX64Workaround", content);
                Assert.Contains("<MtouchInterpreter Condition=\"'$(MtouchInterpreter)' == ''\">all</MtouchInterpreter>", content);
                Assert.Contains("<UseInterpreter Condition=\"'$(UseInterpreter)' == ''\">true</UseInterpreter>", content);
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    #endregion

    #region H. Static-Linkage Source Wrapper-Absent Fallback (Gap 2)

    /// <summary>
    /// When the source framework's native binary is a static <c>ar</c> archive, the wrapper
    /// force-loads it and becomes the sole carrier of its ObjC classes — so referencing the
    /// source xcframework while the wrapper is present would duplicate-register every class.
    /// But these consumer targets are frozen before the wrapper is compiled and evaluated later
    /// on the consumer's machine, so they must NOT bake a drop decision (a soft-failed/skipped
    /// wrapper compile would then leave no carrier at all). Instead the source is referenced as a
    /// wrapper-absent fallback (<c>!Exists(wrapper) AND Exists(source)</c>): inert while the
    /// wrapper is present, the sole carrier when it is not. Dynamic sources and a static source
    /// with no wrapper are referenced unconditionally (<c>Exists(source)</c>).
    /// </summary>
    public class ConsumerTargetsStaticLinkageTests
    {
        [Fact]
        public void Emit_NupkgTargets_StaticSource_ReferencesSourceAsWrapperAbsentFallback()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitNupkg(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", NativeLinkage.Static, hasWrapper: true);
                // Wrapper (force-load carrier) is referenced...
                Assert.Contains("native/ImagePipelineSwiftBindings.xcframework", content);
                // ...and the source is kept, but only as a fallback gated on the wrapper's absence,
                // so it self-heals to the sole carrier if the wrapper compile soft-failed.
                Assert.Contains("native/ImagePipeline.xcframework", content);
                Assert.Contains(
                    "!Exists('$(MSBuildThisFileDirectory)../../runtimes/ios-arm64/native/ImagePipelineSwiftBindings.xcframework') "
                    + "AND Exists('$(MSBuildThisFileDirectory)../../runtimes/ios-arm64/native/ImagePipeline.xcframework')",
                    content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_NupkgTargets_DynamicSource_ReferencesSourceUnconditionally()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitNupkg(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", NativeLinkage.Dynamic, hasWrapper: true);
                // Dynamic source is always carried by the source xcframework — plain Exists, no
                // wrapper-absent fallback (the wrapper and source coexist).
                Assert.Contains(
                    "Condition=\"Exists('$(MSBuildThisFileDirectory)../../runtimes/ios-arm64/native/ImagePipeline.xcframework')\"",
                    content);
                Assert.DoesNotContain("!Exists(", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_NupkgTargets_StaticSourceNoWrapper_ReferencesSoleCarrierUnconditionally()
        {
            var dir = CreateTempDir();
            try
            {
                // Static source but no wrapper to carry it: the source IS the sole carrier and
                // must be referenced unconditionally — dropping it would link no native at all.
                var content = EmitNupkg(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", NativeLinkage.Static, hasWrapper: false);
                Assert.Contains(
                    "Condition=\"Exists('$(MSBuildThisFileDirectory)../../runtimes/ios-arm64/native/ImagePipeline.xcframework')\"",
                    content);
                Assert.DoesNotContain("ImagePipelineSwiftBindings.xcframework", content);
                Assert.DoesNotContain("!Exists(", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_ProjectReferenceTargets_StaticSource_ReferencesSourceAsWrapperAbsentFallback()
        {
            var dir = CreateTempDir();
            try
            {
                var sourceXcfw = Path.Combine(dir, "ImagePipeline.xcframework");
                Directory.CreateDirectory(sourceXcfw);

                ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    PackageId = "ImagePipeline.Swift.iOS",
                    EffectiveMinimumOSVersion = "15.0",
                    HasWrapperXCFramework = true,
                    XcframeworkPath = sourceXcfw,
                    SourceNativeLinkage = NativeLinkage.Static,
                }, NullLogger.Instance);

                var prContent = File.ReadAllText(
                    Path.Combine(dir, "ImagePipeline.Swift.iOS.ProjectReference.targets"));
                // The local source archive always exists on disk here, so the !Exists(wrapper)
                // guard is what keeps it inert while the wrapper is present and active when not.
                Assert.Contains("ImagePipelineSwiftBindings.xcframework", prContent);
                Assert.Contains(
                    "!Exists('$(MSBuildThisFileDirectory)ImagePipelineSwiftBindings.xcframework') "
                    + "AND Exists('$(MSBuildThisFileDirectory)ImagePipeline.xcframework')",
                    prContent);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_ProjectReferenceTargets_DynamicSource_ReferencesSourceUnconditionally()
        {
            var dir = CreateTempDir();
            try
            {
                var sourceXcfw = Path.Combine(dir, "ImagePipeline.xcframework");
                Directory.CreateDirectory(sourceXcfw);

                ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    PackageId = "ImagePipeline.Swift.iOS",
                    EffectiveMinimumOSVersion = "15.0",
                    HasWrapperXCFramework = true,
                    XcframeworkPath = sourceXcfw,
                    SourceNativeLinkage = NativeLinkage.Dynamic,
                }, NullLogger.Instance);

                var prContent = File.ReadAllText(
                    Path.Combine(dir, "ImagePipeline.Swift.iOS.ProjectReference.targets"));
                Assert.Contains("ImagePipeline.xcframework\"", prContent);
                Assert.DoesNotContain("!Exists(", prContent);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string EmitNupkg(
            string dir, string module, string packageId, NativeLinkage linkage, bool hasWrapper)
        {
            ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
            {
                OutputDirectory = dir,
                ModuleName = module,
                PackageId = packageId,
                EffectiveMinimumOSVersion = "15.0",
                HasWrapperXCFramework = hasWrapper,
                SourceNativeLinkage = linkage,
            }, NullLogger.Instance);
            return File.ReadAllText(Path.Combine(dir, $"{packageId}.targets"));
        }

        private static string CreateTempDir() => ConsumerTargetsTestHelper.CreateTempDir();
    }

    /// <summary>
    /// A mixed (ObjC+Swift) framework ships ONE xcframework as ONE NuGet package: the ObjC
    /// companion's managed assembly is embedded in the Swift binding's lib/, never packed as a
    /// separate package. The standalone Swift csproj references that companion with
    /// PrivateAssets="all" (to block both nuspec promotion and transitive compile-asset flow), so a
    /// local ProjectReference consumer's own C# can't see the ObjC types unless the
    /// .ProjectReference.targets injects an explicit assembly <c>&lt;Reference&gt;</c> to the
    /// companion's built output. These tests pin that injection on/off by
    /// <see cref="ConsumerTargetsEmitterOptions.ObjCCompanionProjectFileName"/>.
    /// </summary>
    public class ConsumerTargetsMixedCompanionTests
    {
        [Fact]
        public void Emit_ProjectReferenceTargets_MixedFramework_InjectsCompanionReference()
        {
            var dir = CreateTempDir();
            try
            {
                ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    PackageId = "ImagePipeline.Swift.iOS",
                    EffectiveMinimumOSVersion = "15.0",
                    HasWrapperXCFramework = true,
                    ObjCCompanionProjectFileName = "ImagePipeline.ObjC.iOS.csproj",
                }, NullLogger.Instance);

                var prContent = File.ReadAllText(
                    Path.Combine(dir, "ImagePipeline.Swift.iOS.ProjectReference.targets"));

                // A dedicated target resolves the companion's built output via GetTargetPath and
                // injects it as a plain <Reference> (which never promotes to a nuspec <dependency>),
                // running before RAR so the consumer's C# compile sees the ObjC types.
                Assert.Contains("_ResolveLocalImagePipelineObjCCompanionReference", prContent);
                Assert.Contains("$(MSBuildThisFileDirectory)ImagePipeline.ObjC.iOS.csproj", prContent);
                Assert.Contains("Targets=\"GetTargetPath\"", prContent);
                Assert.Contains("BeforeTargets=\"ResolveAssemblyReferences\"", prContent);
                Assert.Contains("<Reference Include=\"@(_SwiftBindingImagePipelineObjCCompanionAssembly)\">", prContent);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_ProjectReferenceTargets_MixedFramework_CompanionGetTargetPathStripsRuntimeIdentifier()
        {
            var dir = CreateTempDir();
            try
            {
                ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    PackageId = "ImagePipeline.Swift.iOS",
                    EffectiveMinimumOSVersion = "15.0",
                    HasWrapperXCFramework = true,
                    ObjCCompanionProjectFileName = "ImagePipeline.ObjC.iOS.csproj",
                }, NullLogger.Instance);

                var prContent = File.ReadAllText(
                    Path.Combine(dir, "ImagePipeline.Swift.iOS.ProjectReference.targets"));

                // The companion is a managed-only library with a single TFM and no <RuntimeIdentifiers>,
                // so it always builds RID-agnostic (…/<tfm>/<name>.dll). A consumer app builds
                // RID-specific, and when its RID arrives as a GLOBAL property (a command-line
                // -p:RuntimeIdentifier=…, e.g. the CI harness) it propagates through this <MSBuild>
                // GetTargetPath call unless removed — making the query report a RID-qualified path
                // (…/<tfm>/<rid>/<name>.dll) that does not exist, which RAR silently drops (CS0012/CS0246
                // on the ObjC types). The RemoveProperties MUST strip RuntimeIdentifier alongside
                // TargetFramework so the query resolves the actual RID-agnostic output. Asserting the
                // literal keeps the two names paired: dropping RuntimeIdentifier here re-opens the bug.
                Assert.Contains("RemoveProperties=\"TargetFramework;RuntimeIdentifier\"", prContent);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_ProjectReferenceTargets_MixedFramework_CompanionResolveFailsClosedSWIFTBIND042()
        {
            var dir = CreateTempDir();
            try
            {
                ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    PackageId = "ImagePipeline.Swift.iOS",
                    EffectiveMinimumOSVersion = "15.0",
                    HasWrapperXCFramework = true,
                    ObjCCompanionProjectFileName = "ImagePipeline.ObjC.iOS.csproj",
                }, NullLogger.Instance);

                var prContent = File.ReadAllText(
                    Path.Combine(dir, "ImagePipeline.Swift.iOS.ProjectReference.targets"));

                // The companion-resolve target only runs once the companion csproj Exists (path c local
                // dev), so an empty GetTargetPath result there is a genuine failure: the consumer's C#
                // would not see the ObjC types (CS0246). The emitted target must fail closed with
                // SWIFTBIND042 on the empty resolve rather than silently inject nothing — the path-c
                // sibling of SWIFTBIND041 (SDK-direct) and SWIFTBIND039 (pack).
                Assert.Contains("Code=\"SWIFTBIND042\"", prContent);
                Assert.Contains(
                    "'@(_SwiftBindingImagePipelineObjCCompanionAssembly)' == ''", prContent);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_ProjectReferenceTargets_PureSwift_OmitsCompanionReference()
        {
            var dir = CreateTempDir();
            try
            {
                // No companion: a pure-Swift binding has no ObjC companion to surface.
                ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    PackageId = "ImagePipeline.Swift.iOS",
                    EffectiveMinimumOSVersion = "15.0",
                    HasWrapperXCFramework = true,
                    ObjCCompanionProjectFileName = null,
                }, NullLogger.Instance);

                var prContent = File.ReadAllText(
                    Path.Combine(dir, "ImagePipeline.Swift.iOS.ProjectReference.targets"));

                Assert.DoesNotContain("ObjCCompanionReference", prContent);
                Assert.DoesNotContain("GetTargetPath", prContent);
                Assert.DoesNotContain("ObjCCompanionAssembly", prContent);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir() => ConsumerTargetsTestHelper.CreateTempDir();
    }

    #endregion

    #region I. NativeAOT Trimmer-Descriptor Delivery (Defect J)

    /// <summary>
    /// Defect J: a binding package that emits an open-generic ISwiftObject ships
    /// <c>ILLink.Descriptors.xml</c>, but ILC does NOT auto-discover a descriptor embedded in
    /// a referenced assembly — it must be rooted explicitly on the consumer. So the consumer
    /// <c>.targets</c> (packed in buildTransitive/, imported by a downstream PackageReference)
    /// must root the loose descriptor packed beside it via BOTH <c>TrimmerRootDescriptor</c>
    /// (the IL-trimmer path) and <c>IlcArg --descriptor</c> (the NativeAOT/ILC path), gated on
    /// PublishAot and Exists() so non-AOT builds and descriptor-less bindings no-op. The
    /// .ProjectReference.targets (local path-c consumers) must do the same, but from INSIDE
    /// the deferred resolve target because the descriptor is generated late.
    /// </summary>
    public class ConsumerTargetsDescriptorDeliveryTests
    {
        [Fact]
        public void Emit_NupkgTargets_RootsDescriptorForNativeAot()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: true);
                // Both rooting mechanisms must be present — TrimmerRootDescriptor alone does
                // nothing under NativeAOT (it is the PublishTrimmed-without-AOT path); IlcArg
                // --descriptor is what ILC actually reads.
                Assert.Contains(
                    "<TrimmerRootDescriptor Include=\"$(MSBuildThisFileDirectory)ILLink.Descriptors.xml\" />",
                    content);
                Assert.Contains(
                    "<IlcArg Include=\"--descriptor:$(MSBuildThisFileDirectory)ILLink.Descriptors.xml\" />",
                    content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_NupkgTargets_DescriptorRootsGatedOnPublishAotAndExists()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "ImagePipeline.Swift.iOS", "15.0", hasWrapper: true);
                // A binding with no open generics packs no descriptor, and a non-AOT consumer
                // has no ILC item collection — both must no-op. The ItemGroup carries the
                // PublishAot + Exists guard so the rooting is inert in those cases.
                Assert.Contains(
                    "<ItemGroup Condition=\"'$(PublishAot)' == 'true' AND Exists('$(MSBuildThisFileDirectory)ILLink.Descriptors.xml')\">",
                    content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_ProjectReferenceTargets_RootsDescriptorInsideDeferredResolveTarget()
        {
            // Path c (local ProjectReference): the descriptor is GENERATED next to this file by
            // the referenced binding build and does NOT exist at the consumer's outer evaluation
            // on a clean build. So the roots must live INSIDE the deferred
            // _ResolveLocal{sanitized}NativeReferences target (which DependsOn ResolveProjectReferences),
            // where Exists() re-evaluates after the binding builds — NOT in a static top-level
            // ItemGroup that would evaluate Exists() too early and drop the descriptor.
            var dir = CreateTempDir();
            try
            {
                ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    PackageId = "ImagePipeline.Swift.iOS",
                    EffectiveMinimumOSVersion = "15.0",
                    HasWrapperXCFramework = true,
                }, NullLogger.Instance);

                var prContent = File.ReadAllText(
                    Path.Combine(dir, "ImagePipeline.Swift.iOS.ProjectReference.targets"));

                Assert.Contains(
                    "<TrimmerRootDescriptor Include=\"$(MSBuildThisFileDirectory)ILLink.Descriptors.xml\"",
                    prContent);
                Assert.Contains(
                    "<IlcArg Include=\"--descriptor:$(MSBuildThisFileDirectory)ILLink.Descriptors.xml\"",
                    prContent);
                // Each item carries its own per-item PublishAot + Exists guard (an ItemGroup-level
                // Condition can't be used inside a Target that also injects unconditional native refs).
                Assert.Contains(
                    "Condition=\"'$(PublishAot)' == 'true' AND Exists('$(MSBuildThisFileDirectory)ILLink.Descriptors.xml')\"",
                    prContent);

                // Structural: the descriptor roots must sit INSIDE the deferred resolve target,
                // not before it. Assert the roots appear after the target's opening tag.
                var targetIdx = prContent.IndexOf(
                    "_ResolveLocalImagePipelineNativeReferences", StringComparison.Ordinal);
                var rootIdx = prContent.IndexOf("--descriptor:$(MSBuildThisFileDirectory)", StringComparison.Ordinal);
                Assert.True(targetIdx >= 0 && rootIdx > targetIdx,
                    "descriptor roots must be emitted inside the deferred resolve target");
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir() => ConsumerTargetsTestHelper.CreateTempDir();
        private static string EmitAndRead(string dir, string module, string packageId, string minOS, bool hasWrapper)
            => ConsumerTargetsTestHelper.EmitAndRead(dir, module, packageId, minOS, hasWrapper);
    }

    #endregion

    #region H. Emitted-targets XML well-formedness (--in-comment guard)

    /// <summary>
    /// Both emitted consumer-targets files are imported by a downstream consumer's build,
    /// so any malformed XML in them breaks every consumer with MSB4024. The recurring hazard
    /// is a CLI-flag token (e.g. "--descriptor") written into an XML COMMENT: a "--" inside
    /// "&lt;!-- ... --&gt;" is illegal per the XML spec. XDocument.Parse throws on such a defect,
    /// so parsing the emitted output across the wrapper/bridge matrix locks the class out at the
    /// emitter unit layer — far earlier than the PackGate consumer build that would otherwise
    /// be the first to surface it.
    /// </summary>
    public class ConsumerTargetsWellFormednessTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void EmittedTargets_AreWellFormedXml(bool hasWrapper, bool hasBridge)
        {
            var dir = ConsumerTargetsTestHelper.CreateTempDir();
            try
            {
                ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    PackageId = "ImagePipeline.Swift.iOS",
                    EffectiveMinimumOSVersion = "15.0",
                    HasWrapperXCFramework = hasWrapper,
                    HasBridgeXCFramework = hasBridge,
                }, NullLogger.Instance);

                // The PackageReference-path targets (carries the descriptor-delivery comment).
                AssertWellFormed(Path.Combine(dir, "ImagePipeline.Swift.iOS.targets"));
                // The ProjectReference-path targets.
                AssertWellFormed(Path.Combine(dir, "ImagePipeline.Swift.iOS.ProjectReference.targets"));
            }
            finally { Directory.Delete(dir, true); }
        }

        private static void AssertWellFormed(string path)
        {
            Assert.True(File.Exists(path), $"emitter did not produce {path}");
            var ex = Record.Exception(() => XDocument.Parse(File.ReadAllText(path)));
            Assert.True(ex == null, $"emitted targets is not well-formed XML ({Path.GetFileName(path)}): {ex?.Message}");
        }
    }

    #endregion

    #region Test Helper

    internal static class ConsumerTargetsTestHelper
    {
        public static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"cte_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void EmitTargets(string dir, string module, string packageId, string minOS, bool hasWrapper)
        {
            ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
            {
                OutputDirectory = dir,
                ModuleName = module,
                PackageId = packageId,
                EffectiveMinimumOSVersion = minOS,
                HasWrapperXCFramework = hasWrapper
            }, NullLogger.Instance);
        }

        public static string EmitAndRead(string dir, string module, string packageId, string minOS, bool hasWrapper)
        {
            EmitTargets(dir, module, packageId, minOS, hasWrapper);
            return File.ReadAllText(Path.Combine(dir, $"{packageId}.targets"));
        }
    }

    #endregion
}
