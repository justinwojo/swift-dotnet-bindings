// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    public class PlatformInfoFactoryTests
    {
        #region Create() — all 4 platforms produce correct values

        [Fact]
        public void Create_iOS_HasCorrectProperties()
        {
            var pi = PlatformInfoFactory.Create(ApplePlatform.iOS);

            Assert.Equal(ApplePlatform.iOS, pi.Platform);
            Assert.Equal("net10.0-ios", pi.Tfm);
            Assert.Equal("ios-arm64", pi.NuGetRid);
            Assert.Equal(".Swift.iOS", pi.SwiftPackageIdSuffix);
            Assert.Equal(".ObjC.iOS", pi.ObjCPackageIdSuffix);
            Assert.Equal("iOS", pi.ObjCRuntimePlatformName);
            Assert.Equal("ios", pi.PlistPlatformString);
            Assert.Equal("ios", pi.AvailabilityPlatformString);
            Assert.Equal("15.0", pi.DefaultMinimumOS);
            Assert.True(pi.HasSimulatorVariant);
            Assert.NotNull(pi.SimulatorSlice);
            Assert.NotNull(pi.DeviceSlice);
        }

        [Fact]
        public void Create_macOS_HasCorrectProperties()
        {
            var pi = PlatformInfoFactory.Create(ApplePlatform.macOS);

            Assert.Equal(ApplePlatform.macOS, pi.Platform);
            Assert.Equal("net10.0-macos", pi.Tfm);
            Assert.Equal("osx-arm64", pi.NuGetRid);
            Assert.Equal(".Swift.macOS", pi.SwiftPackageIdSuffix);
            Assert.Equal(".ObjC.macOS", pi.ObjCPackageIdSuffix);
            Assert.Equal("MacOSX", pi.ObjCRuntimePlatformName);
            Assert.Equal("macos", pi.PlistPlatformString);
            Assert.Equal("macos", pi.AvailabilityPlatformString);
            Assert.Equal("12.0", pi.DefaultMinimumOS);
            Assert.False(pi.HasSimulatorVariant);
            Assert.Null(pi.SimulatorSlice);
            Assert.NotNull(pi.DeviceSlice);
        }

        [Fact]
        public void Create_tvOS_HasCorrectProperties()
        {
            var pi = PlatformInfoFactory.Create(ApplePlatform.tvOS);

            Assert.Equal(ApplePlatform.tvOS, pi.Platform);
            Assert.Equal("net10.0-tvos", pi.Tfm);
            Assert.Equal("tvos-arm64", pi.NuGetRid);
            Assert.Equal(".Swift.tvOS", pi.SwiftPackageIdSuffix);
            Assert.Equal(".ObjC.tvOS", pi.ObjCPackageIdSuffix);
            Assert.Equal("TvOS", pi.ObjCRuntimePlatformName);
            Assert.Equal("tvos", pi.PlistPlatformString);
            Assert.Equal("tvos", pi.AvailabilityPlatformString);
            Assert.Equal("15.0", pi.DefaultMinimumOS);
            Assert.True(pi.HasSimulatorVariant);
            Assert.NotNull(pi.SimulatorSlice);
            Assert.NotNull(pi.DeviceSlice);
        }

        [Fact]
        public void Create_MacCatalyst_HasCorrectProperties()
        {
            var pi = PlatformInfoFactory.Create(ApplePlatform.MacCatalyst);

            Assert.Equal(ApplePlatform.MacCatalyst, pi.Platform);
            Assert.Equal("net10.0-maccatalyst", pi.Tfm);
            Assert.Equal("maccatalyst-arm64", pi.NuGetRid);
            Assert.Equal(".Swift.MacCatalyst", pi.SwiftPackageIdSuffix);
            Assert.Equal(".ObjC.MacCatalyst", pi.ObjCPackageIdSuffix);
            Assert.Equal("MacCatalyst", pi.ObjCRuntimePlatformName);
            Assert.Equal("ios", pi.PlistPlatformString); // Catalyst uses "ios" in plist
            Assert.Equal("maccatalyst", pi.AvailabilityPlatformString);
            Assert.Equal("15.0", pi.DefaultMinimumOS);
            Assert.False(pi.HasSimulatorVariant);
            Assert.Null(pi.SimulatorSlice);
            Assert.NotNull(pi.DeviceSlice);
        }

        #endregion

        #region SliceVariant properties per platform

        [Fact]
        public void iOS_SimulatorSlice_HasCorrectProperties()
        {
            var pi = PlatformInfoFactory.Create(ApplePlatform.iOS);
            var sim = pi.SimulatorSlice!;

            Assert.Equal(ApplePlatform.iOS, sim.Platform);
            Assert.True(sim.IsSimulator);
            Assert.Equal("iphonesimulator", sim.SdkName);
            Assert.Equal("ios-arm64-simulator", sim.SliceId);
            Assert.Equal("iPhoneSimulator", sim.PlistPlatformName);
            Assert.Equal("ios", sim.XCFrameworkPlatformString);
            Assert.Equal("simulator", sim.XCFrameworkPlatformVariant);
            Assert.Equal("arm64", sim.Architecture);
        }

        [Fact]
        public void iOS_DeviceSlice_HasCorrectProperties()
        {
            var pi = PlatformInfoFactory.Create(ApplePlatform.iOS);
            var dev = pi.DeviceSlice;

            Assert.Equal(ApplePlatform.iOS, dev.Platform);
            Assert.False(dev.IsSimulator);
            Assert.Equal("iphoneos", dev.SdkName);
            Assert.Equal("ios-arm64", dev.SliceId);
            Assert.Equal("iPhoneOS", dev.PlistPlatformName);
            Assert.Equal("ios", dev.XCFrameworkPlatformString);
            Assert.Null(dev.XCFrameworkPlatformVariant);
        }

        [Fact]
        public void macOS_DeviceSlice_HasCorrectProperties()
        {
            var pi = PlatformInfoFactory.Create(ApplePlatform.macOS);
            var dev = pi.DeviceSlice;

            Assert.Equal(ApplePlatform.macOS, dev.Platform);
            Assert.False(dev.IsSimulator);
            Assert.Equal("macosx", dev.SdkName);
            Assert.Equal("macos-arm64", dev.SliceId);
            Assert.Equal("MacOSX", dev.PlistPlatformName);
            Assert.Equal("macos", dev.XCFrameworkPlatformString);
            Assert.Null(dev.XCFrameworkPlatformVariant);
        }

        [Fact]
        public void tvOS_SimulatorSlice_HasCorrectProperties()
        {
            var pi = PlatformInfoFactory.Create(ApplePlatform.tvOS);
            var sim = pi.SimulatorSlice!;

            Assert.Equal(ApplePlatform.tvOS, sim.Platform);
            Assert.True(sim.IsSimulator);
            Assert.Equal("appletvsimulator", sim.SdkName);
            Assert.Equal("tvos-arm64-simulator", sim.SliceId);
            Assert.Equal("AppleTVSimulator", sim.PlistPlatformName);
            Assert.Equal("tvos", sim.XCFrameworkPlatformString);
            Assert.Equal("simulator", sim.XCFrameworkPlatformVariant);
        }

        [Fact]
        public void tvOS_DeviceSlice_HasCorrectProperties()
        {
            var pi = PlatformInfoFactory.Create(ApplePlatform.tvOS);
            var dev = pi.DeviceSlice;

            Assert.Equal(ApplePlatform.tvOS, dev.Platform);
            Assert.False(dev.IsSimulator);
            Assert.Equal("appletvos", dev.SdkName);
            Assert.Equal("tvos-arm64", dev.SliceId);
            Assert.Equal("AppleTVOS", dev.PlistPlatformName);
            Assert.Equal("tvos", dev.XCFrameworkPlatformString);
            Assert.Null(dev.XCFrameworkPlatformVariant);
        }

        [Fact]
        public void MacCatalyst_DeviceSlice_HasCorrectProperties()
        {
            var pi = PlatformInfoFactory.Create(ApplePlatform.MacCatalyst);
            var dev = pi.DeviceSlice;

            Assert.Equal(ApplePlatform.MacCatalyst, dev.Platform);
            Assert.False(dev.IsSimulator);
            Assert.Equal("macosx", dev.SdkName);
            Assert.Equal("ios-arm64-maccatalyst", dev.SliceId);
            Assert.Equal("MacOSX", dev.PlistPlatformName);
            Assert.Equal("ios", dev.XCFrameworkPlatformString);
            Assert.Equal("maccatalyst", dev.XCFrameworkPlatformVariant);
        }

        #endregion

        #region GetTargetTriple() — all platform × simulator combinations

        [Theory]
        [InlineData(ApplePlatform.iOS, true, "17.0", "arm64-apple-ios17.0-simulator")]
        [InlineData(ApplePlatform.iOS, false, "17.0", "arm64-apple-ios17.0")]
        [InlineData(ApplePlatform.iOS, true, "15.0", "arm64-apple-ios15.0-simulator")]
        [InlineData(ApplePlatform.iOS, false, "15.0", "arm64-apple-ios15.0")]
        [InlineData(ApplePlatform.macOS, false, "12.0", "arm64-apple-macos12.0")]
        [InlineData(ApplePlatform.macOS, false, "14.0", "arm64-apple-macos14.0")]
        [InlineData(ApplePlatform.tvOS, true, "15.0", "arm64-apple-tvos15.0-simulator")]
        [InlineData(ApplePlatform.tvOS, false, "15.0", "arm64-apple-tvos15.0")]
        [InlineData(ApplePlatform.MacCatalyst, false, "15.0", "arm64-apple-ios15.0-macabi")]
        [InlineData(ApplePlatform.MacCatalyst, false, "17.0", "arm64-apple-ios17.0-macabi")]
        public void GetTargetTriple_AllPlatforms(ApplePlatform platform, bool isSimulator, string minOS, string expected)
        {
            var pi = PlatformInfoFactory.Create(platform);
            var slice = pi.GetSlice(isSimulator);
            Assert.Equal(expected, slice.GetTargetTriple(minOS));
        }

        #endregion

        #region ParsePlatform() — valid + invalid inputs

        [Theory]
        [InlineData("ios", ApplePlatform.iOS)]
        [InlineData("iOS", ApplePlatform.iOS)]
        [InlineData("IOS", ApplePlatform.iOS)]
        [InlineData(null, ApplePlatform.iOS)] // null defaults to iOS
        [InlineData("macos", ApplePlatform.macOS)]
        [InlineData("macOS", ApplePlatform.macOS)]
        [InlineData("MACOS", ApplePlatform.macOS)]
        [InlineData("tvos", ApplePlatform.tvOS)]
        [InlineData("tvOS", ApplePlatform.tvOS)]
        [InlineData("maccatalyst", ApplePlatform.MacCatalyst)]
        [InlineData("MacCatalyst", ApplePlatform.MacCatalyst)]
        [InlineData("mac-catalyst", ApplePlatform.MacCatalyst)]
        public void ParsePlatform_ValidInputs(string? input, ApplePlatform expected)
        {
            var result = PlatformInfoFactory.ParsePlatform(input);
            Assert.NotNull(result);
            Assert.Equal(expected, result!.Value);
        }

        [Theory]
        [InlineData("windows")]
        [InlineData("android")]
        [InlineData("linux")]
        [InlineData("")]
        [InlineData("visionos")]
        public void ParsePlatform_InvalidInputs_ReturnsNull(string input)
        {
            var result = PlatformInfoFactory.ParsePlatform(input);
            Assert.Null(result);
        }

        #endregion

        #region DetectFromPlistPlatform()

        [Theory]
        [InlineData("ios", null, ApplePlatform.iOS)]
        [InlineData("ios", "simulator", ApplePlatform.iOS)]
        [InlineData("ios", "maccatalyst", ApplePlatform.MacCatalyst)]
        [InlineData("macos", null, ApplePlatform.macOS)]
        [InlineData("tvos", null, ApplePlatform.tvOS)]
        [InlineData("tvos", "simulator", ApplePlatform.tvOS)]
        public void DetectFromPlistPlatform_CorrectPlatform(
            string supportedPlatform, string? variant, ApplePlatform expected)
        {
            var result = PlatformInfoFactory.DetectFromPlistPlatform(supportedPlatform, variant);
            Assert.Equal(expected, result);
        }

        #endregion

        #region PlatformInfo helper methods

        [Theory]
        [InlineData(ApplePlatform.iOS, "Nuke", "Nuke.Swift.iOS")]
        [InlineData(ApplePlatform.macOS, "Nuke", "Nuke.Swift.macOS")]
        [InlineData(ApplePlatform.tvOS, "Nuke", "Nuke.Swift.tvOS")]
        [InlineData(ApplePlatform.MacCatalyst, "Nuke", "Nuke.Swift.MacCatalyst")]
        public void GetDefaultSwiftPackageId_PerPlatform(ApplePlatform platform, string module, string expected)
        {
            var pi = PlatformInfoFactory.Create(platform);
            Assert.Equal(expected, pi.GetDefaultSwiftPackageId(module));
        }

        [Theory]
        [InlineData(ApplePlatform.iOS, "Realm", "Realm.ObjC.iOS")]
        [InlineData(ApplePlatform.macOS, "Realm", "Realm.ObjC.macOS")]
        [InlineData(ApplePlatform.tvOS, "Realm", "Realm.ObjC.tvOS")]
        [InlineData(ApplePlatform.MacCatalyst, "Realm", "Realm.ObjC.MacCatalyst")]
        public void GetDefaultObjCPackageId_PerPlatform(ApplePlatform platform, string module, string expected)
        {
            var pi = PlatformInfoFactory.Create(platform);
            Assert.Equal(expected, pi.GetDefaultObjCPackageId(module));
        }

        [Theory]
        [InlineData(ApplePlatform.iOS, "Nuke.xcframework", "runtimes/ios-arm64/native/Nuke.xcframework/")]
        [InlineData(ApplePlatform.macOS, "Nuke.xcframework", "runtimes/osx-arm64/native/Nuke.xcframework/")]
        [InlineData(ApplePlatform.tvOS, "Nuke.xcframework", "runtimes/tvos-arm64/native/Nuke.xcframework/")]
        [InlineData(ApplePlatform.MacCatalyst, "Nuke.xcframework", "runtimes/maccatalyst-arm64/native/Nuke.xcframework/")]
        public void GetNativePackPath_PerPlatform(ApplePlatform platform, string framework, string expected)
        {
            var pi = PlatformInfoFactory.Create(platform);
            Assert.Equal(expected, pi.GetNativePackPath(framework));
        }

        [Theory]
        [InlineData(ApplePlatform.iOS, "buildTransitive/net10.0-ios26.0/")]
        [InlineData(ApplePlatform.macOS, "buildTransitive/net10.0-macos26.0/")]
        [InlineData(ApplePlatform.tvOS, "buildTransitive/net10.0-tvos26.0/")]
        [InlineData(ApplePlatform.MacCatalyst, "buildTransitive/net10.0-maccatalyst26.0/")]
        public void GetBuildTransitivePath_PerPlatform(ApplePlatform platform, string expected)
        {
            var pi = PlatformInfoFactory.Create(platform);
            Assert.Equal(expected, pi.GetBuildTransitivePath());
        }

        [Theory]
        [InlineData(ApplePlatform.iOS, "net10.0-ios", "net10.0-ios26.0")]
        [InlineData(ApplePlatform.macOS, "net10.0-macos", "net10.0-macos26.0")]
        [InlineData(ApplePlatform.tvOS, "net10.0-tvos", "net10.0-tvos26.0")]
        [InlineData(ApplePlatform.MacCatalyst, "net10.0-maccatalyst", "net10.0-maccatalyst26.0")]
        public void PackTfm_IsDerivedFromTfmAndSharedConstant(ApplePlatform platform, string expectedTfm, string expectedPackTfm)
        {
            // Pins the Codex-review rename + derive refactor: PackTfm must equal
            // Tfm + PlatformInfo.DefaultPlatformVersion exactly. If either half of the
            // derivation drifts (the workload bumps its default platform version and
            // only DefaultPlatformVersion is updated without re-running the pack, or
            // a future maintainer adds per-platform override back in), this test
            // catches it immediately rather than at `dotnet pack` time.
            var pi = PlatformInfoFactory.Create(platform);
            Assert.Equal(expectedTfm, pi.Tfm);
            Assert.Equal(expectedPackTfm, pi.PackTfm);
            Assert.Equal(pi.Tfm + PlatformInfo.DefaultPlatformVersion, pi.PackTfm);
        }

        [Fact]
        public void DefaultPlatformVersion_IsSingleSourceOfTruthAcrossAllPlatforms()
        {
            // PackTfm must be derived from the single DefaultPlatformVersion constant
            // for all four platforms — if someone accidentally reintroduces a per-
            // platform LibTfm override in PlatformInfoFactory, PackTfm will diverge
            // from the Tfm+constant formula and this assertion will fail. Pair with
            // PackTfm_IsDerivedFromTfmAndSharedConstant to lock the contract from
            // both sides.
            foreach (ApplePlatform platform in new[] {
                ApplePlatform.iOS, ApplePlatform.macOS,
                ApplePlatform.tvOS, ApplePlatform.MacCatalyst })
            {
                var pi = PlatformInfoFactory.Create(platform);
                Assert.EndsWith(PlatformInfo.DefaultPlatformVersion, pi.PackTfm);
                Assert.Equal(pi.Tfm + PlatformInfo.DefaultPlatformVersion, pi.PackTfm);
            }
        }

        [Theory]
        [InlineData(ApplePlatform.iOS, "26.2", "net10.0-ios26.2", "buildTransitive/net10.0-ios26.2/")]
        [InlineData(ApplePlatform.iOS, "27.0", "net10.0-ios27.0", "buildTransitive/net10.0-ios27.0/")]
        [InlineData(ApplePlatform.macOS, "26.4", "net10.0-macos26.4", "buildTransitive/net10.0-macos26.4/")]
        [InlineData(ApplePlatform.tvOS, "26.2", "net10.0-tvos26.2", "buildTransitive/net10.0-tvos26.2/")]
        [InlineData(ApplePlatform.MacCatalyst, "26.2", "net10.0-maccatalyst26.2", "buildTransitive/net10.0-maccatalyst26.2/")]
        public void Create_WithPlatformVersionOverride_FlowsToPackTfmAndBuildTransitive(
            ApplePlatform platform, string overrideVersion, string expectedPackTfm, string expectedBuildTransitive)
        {
            // Pin the --platform-version flag plumbing: the override must reach BOTH
            // PackTfm (used by the generator-emitted <TargetFramework>) and the
            // buildTransitive/ pack path. Both must come from the same source so they
            // cannot drift on multi-workload machines. The default DefaultPlatformVersion
            // value must NOT leak through when an override is supplied.
            var pi = PlatformInfoFactory.Create(platform, overrideVersion);
            Assert.Equal(overrideVersion, pi.PlatformVersion);
            Assert.Equal(expectedPackTfm, pi.PackTfm);
            Assert.Equal(expectedBuildTransitive, pi.GetBuildTransitivePath());
            Assert.DoesNotContain(PlatformInfo.DefaultPlatformVersion, pi.PackTfm);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void Create_WithNullEmptyOrWhitespacePlatformVersion_FallsBackToDefault(string? overrideVersion)
        {
            // Null/empty/whitespace overrides must fall back to DefaultPlatformVersion
            // so (a) callers that don't pass --platform-version see today's behavior
            // unchanged, and (b) a poorly-quoted shell invocation that passes "   " can't
            // produce <TargetFramework>net10.0-ios   </TargetFramework>. The factory uses
            // string.IsNullOrWhiteSpace specifically to handle the whitespace case.
            var pi = PlatformInfoFactory.Create(ApplePlatform.iOS, overrideVersion);
            var piDefault = PlatformInfoFactory.Create(ApplePlatform.iOS);

            Assert.Equal(PlatformInfo.DefaultPlatformVersion, pi.PlatformVersion);
            Assert.Equal(piDefault.PackTfm, pi.PackTfm);
        }

        #endregion

        #region GetSlice() + AllSlices

        [Fact]
        public void GetSlice_iOS_Simulator_ReturnsSimulatorSlice()
        {
            var pi = PlatformInfoFactory.Create(ApplePlatform.iOS);
            var slice = pi.GetSlice(true);
            Assert.True(slice.IsSimulator);
            Assert.Equal("iphonesimulator", slice.SdkName);
        }

        [Fact]
        public void GetSlice_iOS_Device_ReturnsDeviceSlice()
        {
            var pi = PlatformInfoFactory.Create(ApplePlatform.iOS);
            var slice = pi.GetSlice(false);
            Assert.False(slice.IsSimulator);
            Assert.Equal("iphoneos", slice.SdkName);
        }

        [Fact]
        public void GetSlice_macOS_SimulatorRequest_ReturnsDeviceSlice()
        {
            // macOS has no simulator — should fall back to device
            var pi = PlatformInfoFactory.Create(ApplePlatform.macOS);
            var slice = pi.GetSlice(true);
            Assert.False(slice.IsSimulator);
            Assert.Equal("macosx", slice.SdkName);
        }

        [Fact]
        public void GetSlice_MacCatalyst_SimulatorRequest_ReturnsDeviceSlice()
        {
            // Catalyst has no simulator — should fall back to device
            var pi = PlatformInfoFactory.Create(ApplePlatform.MacCatalyst);
            var slice = pi.GetSlice(true);
            Assert.False(slice.IsSimulator);
            Assert.Equal("macosx", slice.SdkName);
        }

        [Theory]
        [InlineData(ApplePlatform.iOS, 2)]
        [InlineData(ApplePlatform.macOS, 1)]
        [InlineData(ApplePlatform.tvOS, 2)]
        [InlineData(ApplePlatform.MacCatalyst, 1)]
        public void AllSlices_CorrectCount(ApplePlatform platform, int expectedCount)
        {
            var pi = PlatformInfoFactory.Create(platform);
            Assert.Equal(expectedCount, pi.AllSlices.Count);
        }

        #endregion

        #region DisplayName

        [Theory]
        [InlineData(ApplePlatform.iOS, true, "iOS Simulator")]
        [InlineData(ApplePlatform.iOS, false, "iOS Device")]
        [InlineData(ApplePlatform.macOS, false, "macOS Device")]
        [InlineData(ApplePlatform.tvOS, true, "tvOS Simulator")]
        [InlineData(ApplePlatform.MacCatalyst, false, "MacCatalyst Device")]
        public void SliceVariant_DisplayName(ApplePlatform platform, bool isSimulator, string expected)
        {
            var pi = PlatformInfoFactory.Create(platform);
            var slice = pi.GetSlice(isSimulator);
            Assert.Equal(expected, slice.DisplayName);
        }

        #endregion
    }

    public class XCFrameworkResolverPlatformTests
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        /// <summary>
        /// Builds a minimal xcframework Info.plist XML with the given slices.
        /// </summary>
        private static string BuildPlistXml(params (string platform, string? variant, string identifier)[] slices)
        {
            var entries = string.Join("\n", slices.Select(s =>
            {
                var variantEntry = s.variant != null
                    ? $@"
                <key>SupportedPlatformVariant</key>
                <string>{s.variant}</string>"
                    : "";
                return $@"
            <dict>
                <key>BinaryPath</key>
                <string>Test.framework/Test</string>
                <key>LibraryIdentifier</key>
                <string>{s.identifier}</string>
                <key>LibraryPath</key>
                <string>Test.framework</string>
                <key>SupportedArchitectures</key>
                <array><string>arm64</string></array>
                <key>SupportedPlatform</key>
                <string>{s.platform}</string>{variantEntry}
            </dict>";
            }));

            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>AvailableLibraries</key>
    <array>{entries}
    </array>
</dict>
</plist>";
        }

        [Fact]
        public void SelectSlice_macOS_FindsMacOSSlice()
        {
            var plistXml = BuildPlistXml(
                ("ios", "simulator", "ios-arm64-simulator"),
                ("ios", null, "ios-arm64"),
                ("macos", null, "macos-arm64"));

            var tempDir = Path.Combine(Path.GetTempPath(), $"plist_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var plistPath = Path.Combine(tempDir, "Info.plist");
                File.WriteAllText(plistPath, plistXml);
                var slices = XCFrameworkResolver.ParseInfoPlist(plistPath);

                var macInfo = PlatformInfoFactory.Create(ApplePlatform.macOS);
                var selected = XCFrameworkResolver.SelectSlice(slices, XCFrameworkPlatformTarget.Device, Logger, macInfo);

                Assert.Equal("macos", selected.SupportedPlatform);
                Assert.Null(selected.SupportedPlatformVariant);
                Assert.Equal("macos-arm64", selected.LibraryIdentifier);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void SelectSlice_MacCatalyst_FindsCatalystSlice()
        {
            var plistXml = BuildPlistXml(
                ("ios", "simulator", "ios-arm64-simulator"),
                ("ios", null, "ios-arm64"),
                ("ios", "maccatalyst", "ios-arm64-maccatalyst"));

            var tempDir = Path.Combine(Path.GetTempPath(), $"plist_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var plistPath = Path.Combine(tempDir, "Info.plist");
                File.WriteAllText(plistPath, plistXml);
                var slices = XCFrameworkResolver.ParseInfoPlist(plistPath);

                var catalystInfo = PlatformInfoFactory.Create(ApplePlatform.MacCatalyst);
                var selected = XCFrameworkResolver.SelectSlice(slices, XCFrameworkPlatformTarget.Device, Logger, catalystInfo);

                Assert.Equal("ios", selected.SupportedPlatform);
                Assert.Equal("maccatalyst", selected.SupportedPlatformVariant);
                Assert.Equal("ios-arm64-maccatalyst", selected.LibraryIdentifier);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void SelectSlice_tvOS_Simulator_FindsTvOSSimSlice()
        {
            var plistXml = BuildPlistXml(
                ("ios", "simulator", "ios-arm64-simulator"),
                ("tvos", "simulator", "tvos-arm64-simulator"),
                ("tvos", null, "tvos-arm64"));

            var tempDir = Path.Combine(Path.GetTempPath(), $"plist_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var plistPath = Path.Combine(tempDir, "Info.plist");
                File.WriteAllText(plistPath, plistXml);
                var slices = XCFrameworkResolver.ParseInfoPlist(plistPath);

                var tvosInfo = PlatformInfoFactory.Create(ApplePlatform.tvOS);
                var selected = XCFrameworkResolver.SelectSlice(slices, XCFrameworkPlatformTarget.Simulator, Logger, tvosInfo);

                Assert.Equal("tvos", selected.SupportedPlatform);
                Assert.Equal("simulator", selected.SupportedPlatformVariant);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void SelectSlice_NoPlatformInfo_DefaultsToiOS()
        {
            var plistXml = BuildPlistXml(
                ("ios", "simulator", "ios-arm64-simulator"),
                ("macos", null, "macos-arm64"));

            var tempDir = Path.Combine(Path.GetTempPath(), $"plist_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var plistPath = Path.Combine(tempDir, "Info.plist");
                File.WriteAllText(plistPath, plistXml);
                var slices = XCFrameworkResolver.ParseInfoPlist(plistPath);

                // No platformInfo → should select iOS
                var selected = XCFrameworkResolver.SelectSlice(slices, XCFrameworkPlatformTarget.Simulator, Logger);

                Assert.Equal("ios", selected.SupportedPlatform);
                Assert.Equal("simulator", selected.SupportedPlatformVariant);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void SelectSlice_macOS_NoMacSlice_Throws()
        {
            var plistXml = BuildPlistXml(
                ("ios", "simulator", "ios-arm64-simulator"),
                ("ios", null, "ios-arm64"));

            var tempDir = Path.Combine(Path.GetTempPath(), $"plist_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var plistPath = Path.Combine(tempDir, "Info.plist");
                File.WriteAllText(plistPath, plistXml);
                var slices = XCFrameworkResolver.ParseInfoPlist(plistPath);

                var macInfo = PlatformInfoFactory.Create(ApplePlatform.macOS);
                var ex = Assert.Throws<InvalidOperationException>(
                    () => XCFrameworkResolver.SelectSlice(slices, XCFrameworkPlatformTarget.Device, Logger, macInfo));

                Assert.Contains("macOS", ex.Message);
                Assert.Contains("No", ex.Message);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    public class BindingProjectEmitterPlatformTests
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        [Theory]
        [InlineData(ApplePlatform.iOS, "Nuke.Swift.iOS")]
        [InlineData(ApplePlatform.macOS, "Nuke.Swift.macOS")]
        [InlineData(ApplePlatform.tvOS, "Nuke.Swift.tvOS")]
        [InlineData(ApplePlatform.MacCatalyst, "Nuke.Swift.MacCatalyst")]
        public void Emit_CorrectTfmAndPackageId_PerPlatform(ApplePlatform platform, string expectedPackageId)
        {
            var pi = PlatformInfoFactory.Create(platform);
            var tempDir = Path.Combine(Path.GetTempPath(), $"emitter_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var xcfwDir = Path.Combine(tempDir, "Nuke.xcframework");
            Directory.CreateDirectory(xcfwDir);

            try
            {
                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = tempDir,
                    ModuleName = "Nuke",
                    Metadata = new XCFrameworkMetadata
                    {
                        PackageVersion = "1.0.0",
                        IsVersionPlaceholder = false,
                        EffectiveMinimumOSVersion = "15.0",
                        ModuleName = "Nuke",
                        Platforms = new List<string> { "ios" },
                    },
                    SourceXCFrameworkPath = xcfwDir,
                    PlatformInfo = pi,
                }, Logger);

                var csprojPath = Path.Combine(tempDir, $"{expectedPackageId}.csproj");
                Assert.True(File.Exists(csprojPath), $"Expected {expectedPackageId}.csproj to exist");

                var content = File.ReadAllText(csprojPath);
                // <TargetFramework> and buildTransitive/ both source from pi.PackTfm so they
                // cannot drift on multi-workload machines. Test asserts both directly off pi
                // (not a hardcoded string) so future bumps to DefaultPlatformVersion don't
                // cascade into per-platform inline-data updates.
                Assert.Contains($"<TargetFramework>{pi.PackTfm}</TargetFramework>", content);
                Assert.Contains($"<PackageId>{expectedPackageId}</PackageId>", content);
                Assert.Contains($"runtimes/{pi.NuGetRid}/native/", content);
                Assert.Contains($"buildTransitive/{pi.PackTfm}/", content);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    public class ConsumerTargetsEmitterPlatformTests
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        [Theory]
        [InlineData(ApplePlatform.iOS, "ios-arm64")]
        [InlineData(ApplePlatform.macOS, "osx-arm64")]
        [InlineData(ApplePlatform.tvOS, "tvos-arm64")]
        [InlineData(ApplePlatform.MacCatalyst, "maccatalyst-arm64")]
        public void Emit_CorrectNuGetRid_PerPlatform(ApplePlatform platform, string expectedRid)
        {
            var pi = PlatformInfoFactory.Create(platform);
            var tempDir = Path.Combine(Path.GetTempPath(), $"targets_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var packageId = pi.GetDefaultSwiftPackageId("Test");

            try
            {
                ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                {
                    OutputDirectory = tempDir,
                    ModuleName = "Test",
                    PackageId = packageId,
                    EffectiveMinimumOSVersion = "15.0",
                    HasWrapperXCFramework = true,
                    PlatformInfo = pi,
                }, Logger);

                var targetsPath = Path.Combine(tempDir, $"{packageId}.targets");
                Assert.True(File.Exists(targetsPath));

                var content = File.ReadAllText(targetsPath);
                Assert.Contains($"runtimes/{expectedRid}/native/Test.xcframework", content);
                Assert.Contains($"runtimes/{expectedRid}/native/TestSwiftBindings.xcframework", content);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    public class ObjCAvailabilityEmitterPlatformTests
    {
        [Theory]
        [InlineData(ApplePlatform.iOS, "ios", "PlatformName.iOS")]
        [InlineData(ApplePlatform.macOS, "macos", "PlatformName.MacOSX")]
        [InlineData(ApplePlatform.tvOS, "tvos", "PlatformName.TvOS")]
        [InlineData(ApplePlatform.MacCatalyst, "maccatalyst", "PlatformName.MacCatalyst")]
        public void EmitAvailability_CorrectPlatformName(ApplePlatform platform, string availPlatform, string expectedAttr)
        {
            var pi = PlatformInfoFactory.Create(platform);
            var sb = new System.Text.StringBuilder();
            var availability = new List<BindingsGeneration.ObjC.ObjCAvailability>
            {
                new() { Platform = availPlatform, IntroducedVersion = "15.0" }
            };

            var isUnavailable = BindingsGeneration.ObjC.ObjCAvailabilityEmitter.EmitAvailabilityAttributes(
                sb, availability, "    ", pi);

            Assert.False(isUnavailable);
            var result = sb.ToString();
            Assert.Contains(expectedAttr, result);
            Assert.Contains("[Introduced(", result);
        }

        [Fact]
        public void EmitAvailability_FiltersByPlatform()
        {
            var pi = PlatformInfoFactory.Create(ApplePlatform.macOS);
            var sb = new System.Text.StringBuilder();
            var availability = new List<BindingsGeneration.ObjC.ObjCAvailability>
            {
                // iOS availability should be skipped for macOS target
                new() { Platform = "ios", IntroducedVersion = "10.0" },
                new() { Platform = "macos", IntroducedVersion = "12.0" },
            };

            BindingsGeneration.ObjC.ObjCAvailabilityEmitter.EmitAvailabilityAttributes(
                sb, availability, "    ", pi);

            var result = sb.ToString();
            Assert.Contains("PlatformName.MacOSX", result);
            Assert.DoesNotContain("PlatformName.iOS", result);
        }
    }

    /// <summary>
    /// Tests that ShouldCompileWrapper is platform-aware: no-simulator platforms always compile.
    /// </summary>
    public class ShouldCompileWrapperPlatformTests
    {
        [Theory]
        [InlineData(ApplePlatform.macOS)]
        [InlineData(ApplePlatform.MacCatalyst)]
        public void NoSimulatorPlatform_SimulatorArch_ReturnsTrue(ApplePlatform platform)
        {
            // macOS and Mac Catalyst have no simulator variant.
            // Even when wrapperArchitectures is "simulator" (the default) and the slice is device-only,
            // ShouldCompileWrapper must return true so the wrapper gets compiled.
            var pi = PlatformInfoFactory.Create(platform);
            Assert.True(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: "simulator", platformInfo: pi));
        }

        [Theory]
        [InlineData(ApplePlatform.macOS)]
        [InlineData(ApplePlatform.MacCatalyst)]
        public void NoSimulatorPlatform_DeviceArch_ReturnsTrue(ApplePlatform platform)
        {
            var pi = PlatformInfoFactory.Create(platform);
            Assert.True(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: "device", platformInfo: pi));
        }

        [Fact]
        public void iOS_DeviceSlice_SimulatorArch_NoPlatformInfo_ReturnsFalse()
        {
            // Backward compat: without platformInfo, device + simulator = false (existing behavior)
            Assert.False(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: "simulator"));
        }

        [Fact]
        public void iOS_DeviceSlice_SimulatorArch_WithPlatformInfo_ReturnsFalse()
        {
            var pi = PlatformInfoFactory.Create(ApplePlatform.iOS);
            Assert.False(BindingsGenerator.ShouldCompileWrapper(
                isSimulatorSlice: false, wrapperArchitectures: "simulator", platformInfo: pi));
        }
    }

    /// <summary>
    /// Tests that BinaryDependencyAnalyzer.Analyze threads platformInfo to resolver calls.
    /// </summary>
    public class BinaryDependencyAnalyzerPlatformTests
    {
        private static readonly ILogger Logger = NullLoggerFactory.Instance.CreateLogger("test");

        [Fact]
        public void Analyze_AcceptsPlatformInfo_WithoutError()
        {
            // Verifies the method signature accepts platformInfo and doesn't throw
            // when no dependencies are detected (empty otool output).
            var runner = new MockCommandRunner();
            runner.SetResponse("-L", 0, "/path/to/lib:\n");

            var result = BinaryDependencyAnalyzer.Analyze(
                "/tmp/test.dylib", "/tmp/Test.xcframework", "Test",
                XCFrameworkPlatformTarget.Device, "simulator", Logger, runner,
                platformInfo: PlatformInfoFactory.Create(ApplePlatform.macOS));

            Assert.NotNull(result);
            Assert.Empty(result!.ResolvedDependencies);
            Assert.Empty(result.UnresolvedDependencies);
        }

        [Fact]
        public void Analyze_NullPlatformInfo_DefaultsToiOS()
        {
            // Backward compat: null platformInfo should not throw
            var runner = new MockCommandRunner();
            runner.SetResponse("-L", 0, "/path/to/lib:\n");

            var result = BinaryDependencyAnalyzer.Analyze(
                "/tmp/test.dylib", "/tmp/Test.xcframework", "Test",
                XCFrameworkPlatformTarget.Simulator, "simulator", Logger, runner);

            Assert.NotNull(result);
        }
    }

    /// <summary>
    /// Tests that SymbolGraphExtractor uses resolution.SelectedArchitecture for the target triple.
    /// </summary>
    public class SymbolGraphExtractorPlatformTests
    {
        private static readonly ILogger Logger = NullLoggerFactory.Instance.CreateLogger("test");

        [Fact]
        public void Extract_UsesSelectedArchitecture_InTargetTriple()
        {
            // When the resolved architecture differs from the SliceVariant default (arm64),
            // the target triple should use the resolved architecture.
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "/path/to/sdk");
            // ResolveDeploymentTarget uses otool -l and falls back to default "15.0"
            runner.SetResponse("otool", 0, "");
            runner.SetResponse("swift-symbolgraph-extract", 0, "");

            var resolution = new XCFrameworkResolution
            {
                AbiJsonPath = "/tmp/abi.json",
                DylibPath = "/tmp/test.dylib",
                TbdPath = "/tmp/test.tbd",
                ModuleName = "Test",
                XCFrameworkPath = "/tmp/Test.xcframework",
                FrameworkSearchPath = "/tmp/fw",
                LibraryIdentifier = "ios-arm64_x86_64-simulator",
                IsSimulatorSlice = true,
                SelectedArchitecture = "x86_64"
            };

            var pi = PlatformInfoFactory.Create(ApplePlatform.iOS);
            // Extract will return null (no .symbols.json files created), but the important
            // thing is the xcrun arguments contain x86_64 in the target triple.
            SymbolGraphExtractor.Extract(resolution, "/tmp/out", Logger, runner, pi);

            var xcrunCalls = runner.Invocations
                .Where(i => i.Arguments.Contains("swift-symbolgraph-extract"))
                .ToList();
            Assert.NotEmpty(xcrunCalls);
            Assert.Contains("x86_64-apple-ios", xcrunCalls[0].Arguments);
        }

        [Fact]
        public void Extract_DefaultArchitecture_UsesArm64()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "/path/to/sdk");
            runner.SetResponse("otool", 0, "");
            runner.SetResponse("swift-symbolgraph-extract", 0, "");

            var resolution = new XCFrameworkResolution
            {
                AbiJsonPath = "/tmp/abi.json",
                DylibPath = "/tmp/test.dylib",
                TbdPath = "/tmp/test.tbd",
                ModuleName = "Test",
                XCFrameworkPath = "/tmp/Test.xcframework",
                FrameworkSearchPath = "/tmp/fw",
                LibraryIdentifier = "ios-arm64-simulator",
                IsSimulatorSlice = true,
                SelectedArchitecture = "arm64"
            };

            var pi = PlatformInfoFactory.Create(ApplePlatform.iOS);
            SymbolGraphExtractor.Extract(resolution, "/tmp/out", Logger, runner, pi);

            var xcrunCalls = runner.Invocations
                .Where(i => i.Arguments.Contains("swift-symbolgraph-extract"))
                .ToList();
            Assert.NotEmpty(xcrunCalls);
            Assert.Contains("arm64-apple-ios", xcrunCalls[0].Arguments);
        }

        [Fact]
        public void Extract_macOS_UsesCorrectSdkAndTriple()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "/path/to/macos-sdk");
            runner.SetResponse("otool", 0, "");
            runner.SetResponse("swift-symbolgraph-extract", 0, "");

            var resolution = new XCFrameworkResolution
            {
                AbiJsonPath = "/tmp/abi.json",
                DylibPath = "/tmp/test.dylib",
                TbdPath = "/tmp/test.tbd",
                ModuleName = "Test",
                XCFrameworkPath = "/tmp/Test.xcframework",
                FrameworkSearchPath = "/tmp/fw",
                LibraryIdentifier = "macos-arm64",
                IsSimulatorSlice = false,
                SelectedArchitecture = "arm64"
            };

            var pi = PlatformInfoFactory.Create(ApplePlatform.macOS);
            SymbolGraphExtractor.Extract(resolution, "/tmp/out", Logger, runner, pi);

            // Should use macosx SDK
            var sdkCalls = runner.Invocations
                .Where(i => i.Arguments.Contains("--show-sdk-path"))
                .ToList();
            Assert.NotEmpty(sdkCalls);
            Assert.Contains("macosx", sdkCalls[0].Arguments);

            // Should use macos triple
            var extractCalls = runner.Invocations
                .Where(i => i.Arguments.Contains("swift-symbolgraph-extract"))
                .ToList();
            Assert.NotEmpty(extractCalls);
            Assert.Contains("arm64-apple-macos", extractCalls[0].Arguments);
        }
    }

    public class FrameworkDependencyInfoPlatformTests
    {
        [Theory]
        [InlineData(ApplePlatform.iOS, "Nuke.Swift.iOS")]
        [InlineData(ApplePlatform.macOS, "Nuke.Swift.macOS")]
        [InlineData(ApplePlatform.tvOS, "Nuke.Swift.tvOS")]
        [InlineData(ApplePlatform.MacCatalyst, "Nuke.Swift.MacCatalyst")]
        public void GetEffectivePackageId_PlatformAware(ApplePlatform platform, string expected)
        {
            var pi = PlatformInfoFactory.Create(platform);
            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = "/tmp/Nuke.xcframework",
                ModuleName = "Nuke",
            };

            Assert.Equal(expected, dep.GetEffectivePackageId(pi));
        }

        [Fact]
        public void EffectivePackageId_Property_DefaultsToiOS()
        {
            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = "/tmp/Nuke.xcframework",
                ModuleName = "Nuke",
            };

            Assert.Equal("Nuke.Swift.iOS", dep.EffectivePackageId);
        }

        [Fact]
        public void GetEffectivePackageId_ExplicitOverride_IgnoresPlatform()
        {
            var pi = PlatformInfoFactory.Create(ApplePlatform.macOS);
            var dep = new FrameworkDependencyInfo
            {
                XCFrameworkPath = "/tmp/Nuke.xcframework",
                ModuleName = "Nuke",
                PackageId = "MyCustom.PackageId",
            };

            Assert.Equal("MyCustom.PackageId", dep.GetEffectivePackageId(pi));
        }
    }
}
