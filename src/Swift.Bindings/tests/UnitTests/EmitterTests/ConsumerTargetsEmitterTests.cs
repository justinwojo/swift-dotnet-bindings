// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

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
                EmitTargets(dir, "Nuke", "Nuke.Swift.iOS", "15.0", hasWrapper: true);
                Assert.True(File.Exists(Path.Combine(dir, "Nuke.Swift.iOS.targets")));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SourceNativeRef_Present()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", "Nuke.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("Nuke.xcframework", content);
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
                var content = EmitAndRead(dir, "Nuke", "Nuke.Swift.iOS", "15.0", hasWrapper: true);
                Assert.Contains("NukeSwiftBindings.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WrapperNativeRef_AbsentWhenNoWrapper()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", "Nuke.Swift.iOS", "15.0", hasWrapper: false);
                Assert.DoesNotContain("NukeSwiftBindings.xcframework", content);
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
                var content = EmitAndRead(dir, "Nuke", "Nuke.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("_SwiftBinding_Nuke_Injected", content);
                Assert.Contains("Condition=\"'$(_SwiftBinding_Nuke_Injected)' != 'true'\"", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SwiftBindingFramework_Registration()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", "Nuke.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("<SwiftBindingFramework Include=\"Nuke\">", content);
                Assert.Contains("<SourcePackage>Nuke.Swift.iOS</SourcePackage>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_PlatformVersionWarning_Present()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", "Nuke.Swift.iOS", "15.0", hasWrapper: false);
                Assert.Contains("SWIFTBIND010", content);
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
                var content = EmitAndRead(dir, "BlinkID", "BlinkID.Swift.iOS", "16.0", hasWrapper: false);
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
                var content = EmitAndRead(dir, "Nuke", "Nuke.Swift.iOS", "15.0", hasWrapper: false);
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
                var content = EmitAndRead(dir, "Nuke", "Nuke.Swift.iOS", "15.0", hasWrapper: true);
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
                var content = EmitAndRead(dir, "Nuke", "Nuke.Swift.iOS", "15.0", hasWrapper: false);
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
            Assert.Equal("Nuke", ConsumerTargetsEmitter.SanitizeModuleName("Nuke"));
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
