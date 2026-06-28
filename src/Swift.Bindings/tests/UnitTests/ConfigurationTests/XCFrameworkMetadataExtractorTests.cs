// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BindingsGeneration.Tests
{
    #region A. Version Detection Tests

    public class MetadataVersionDetectionTests
    {
        [Fact]
        public void DetectVersionPlaceholder_ExactlyOnePointZero_IsPlaceholder()
        {
            Assert.True(XCFrameworkMetadataExtractor.DetectVersionPlaceholder("1.0"));
        }

        [Fact]
        public void DetectVersionPlaceholder_ExactlyOnePointZeroPointZero_IsPlaceholder()
        {
            Assert.True(XCFrameworkMetadataExtractor.DetectVersionPlaceholder("1.0.0"));
        }

        [Fact]
        public void DetectVersionPlaceholder_OnePointZeroPointOne_IsNotPlaceholder()
        {
            Assert.False(XCFrameworkMetadataExtractor.DetectVersionPlaceholder("1.0.1"));
        }

        [Fact]
        public void DetectVersionPlaceholder_TwoPointZero_IsNotPlaceholder()
        {
            Assert.False(XCFrameworkMetadataExtractor.DetectVersionPlaceholder("2.0"));
        }

        [Fact]
        public void DetectVersionPlaceholder_RealVersion_IsNotPlaceholder()
        {
            Assert.False(XCFrameworkMetadataExtractor.DetectVersionPlaceholder("12.8.0"));
        }

        [Fact]
        public void DetectVersionPlaceholder_OnePointNine_IsNotPlaceholder()
        {
            Assert.False(XCFrameworkMetadataExtractor.DetectVersionPlaceholder("1.9.0"));
        }

        [Fact]
        public void DetectVersionPlaceholder_Null_IsPlaceholder()
        {
            Assert.True(XCFrameworkMetadataExtractor.DetectVersionPlaceholder(null));
        }

        [Fact]
        public void DetectVersionPlaceholder_Empty_IsPlaceholder()
        {
            Assert.True(XCFrameworkMetadataExtractor.DetectVersionPlaceholder(""));
        }
    }

    #endregion

    #region B. MinimumOSVersion Clamping Tests

    public class MetadataMinOSClampingTests
    {
        [Fact]
        public void ClampMinimumOSVersion_Below15_ClampsTo15()
        {
            Assert.Equal("15.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion("13.0"));
        }

        [Fact]
        public void ClampMinimumOSVersion_Exactly15_Returns15()
        {
            Assert.Equal("15.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion("15.0"));
        }

        [Fact]
        public void ClampMinimumOSVersion_Exactly16_Returns16()
        {
            Assert.Equal("16.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion("16.0"));
        }

        [Fact]
        public void ClampMinimumOSVersion_HighVersion_ReturnsRaw()
        {
            Assert.Equal("18.2", XCFrameworkMetadataExtractor.ClampMinimumOSVersion("18.2"));
        }

        [Fact]
        public void ClampMinimumOSVersion_Null_ReturnsFallback()
        {
            Assert.Equal("15.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion(null));
        }

        [Fact]
        public void ClampMinimumOSVersion_Empty_ReturnsFallback()
        {
            Assert.Equal("15.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion(""));
        }

        // Some SDKs (e.g. those built via CMake) ship every
        // xcframework's inner Info.plist with MinimumOSVersion=100.0. Writing that into
        // <SupportedOSPlatformVersion> blows up the .NET SDK with NETSDK1135 once it
        // exceeds the workload's TargetPlatformVersion. The clamp must reject the
        // sentinel and fall back to the floor regardless of which Apple OS is current.
        [Fact]
        public void ClampMinimumOSVersion_VendorSentinel_FallsBackToFloor()
        {
            Assert.Equal("15.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion("100.0"));
        }

        [Fact]
        public void ClampMinimumOSVersion_AboveSentinelCeiling_FallsBackToFloor()
        {
            Assert.Equal("15.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion("999.0"));
        }

        [Fact]
        public void ClampMinimumOSVersion_JustBelowSentinelCeiling_PreservesRaw()
        {
            // Forward-compatibility check: a plausibly-real iOS version must survive the
            // sentinel filter. If Apple ever ships iOS 99 we have bigger problems.
            Assert.Equal("99.9", XCFrameworkMetadataExtractor.ClampMinimumOSVersion("99.9"));
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("16.x")]
        [InlineData("not-a-version")]
        [InlineData("16.0.0.0.0")]
        [InlineData("v16.0")]
        [InlineData("16,0")]
        [InlineData(" ")]
        public void ClampMinimumOSVersion_MalformedInput_FallsBackToFloor(string raw)
        {
            // Anything Version.TryParse rejects must fall back to the floor; otherwise
            // garbage propagates into <SupportedOSPlatformVersion> / -target ios{X}.
            Assert.Equal("15.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion(raw));
        }
    }

    #endregion

    #region B2. Platform-aware MinimumOSVersion Clamping Tests

    public class MetadataMinOSPlatformAwareClampingTests
    {
        // macOS's .NET 10 deployment floor is 12.0, not iOS's 15.0. A macOS framework that
        // deploys to macOS 12/13/14 must keep its source minimum — bumping it to 15 needlessly
        // excludes macOS 12-14 consumers the runtime fully supports (Swift.Runtime.csproj sets
        // the macOS SupportedOSPlatformVersion to 12.0).
        [Theory]
        [InlineData("12.0")]
        [InlineData("13.0")]
        [InlineData("14.0")]
        public void ClampMinimumOSVersion_macOS_AtOrAboveMacFloor_PreservesRaw(string raw)
        {
            var macOS = PlatformInfoFactory.Create(ApplePlatform.macOS);
            Assert.Equal(raw, XCFrameworkMetadataExtractor.ClampMinimumOSVersion(raw, macOS));
        }

        [Fact]
        public void ClampMinimumOSVersion_macOS_BelowMacFloor_RaisedTo12()
        {
            var macOS = PlatformInfoFactory.Create(ApplePlatform.macOS);
            Assert.Equal("12.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion("10.15", macOS));
        }

        [Fact]
        public void ClampMinimumOSVersion_macOS_Null_ReturnsMacFloor12()
        {
            var macOS = PlatformInfoFactory.Create(ApplePlatform.macOS);
            Assert.Equal("12.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion(null, macOS));
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("16.x")]
        [InlineData("v12.0")]
        [InlineData(" ")]
        public void ClampMinimumOSVersion_macOS_Malformed_ReturnsMacFloor12(string raw)
        {
            var macOS = PlatformInfoFactory.Create(ApplePlatform.macOS);
            Assert.Equal("12.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion(raw, macOS));
        }

        [Fact]
        public void ClampMinimumOSVersion_macOS_VendorSentinel_ReturnsMacFloor12()
        {
            // The sentinel ceiling (>= 100) still rejects to the floor — now the macOS floor.
            var macOS = PlatformInfoFactory.Create(ApplePlatform.macOS);
            Assert.Equal("12.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion("100.0", macOS));
        }

        [Fact]
        public void ClampMinimumOSVersion_macOS_ValidHigher_PreservesRaw()
        {
            var macOS = PlatformInfoFactory.Create(ApplePlatform.macOS);
            Assert.Equal("15.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion("15.0", macOS));
        }

        // iOS/tvOS/Catalyst keep the 15.0 floor — guards that the platform-aware change didn't
        // lower the floor for the non-macOS platforms.
        [Theory]
        [InlineData(ApplePlatform.iOS)]
        [InlineData(ApplePlatform.tvOS)]
        [InlineData(ApplePlatform.MacCatalyst)]
        public void ClampMinimumOSVersion_NonMac_Below15_StillClampsTo15(ApplePlatform platform)
        {
            var pi = PlatformInfoFactory.Create(platform);
            Assert.Equal("15.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion("13.0", pi));
        }

        [Theory]
        [InlineData(ApplePlatform.iOS)]
        [InlineData(ApplePlatform.tvOS)]
        [InlineData(ApplePlatform.MacCatalyst)]
        public void ClampMinimumOSVersion_NonMac_VendorSentinel_ClampsTo15(ApplePlatform platform)
        {
            var pi = PlatformInfoFactory.Create(platform);
            Assert.Equal("15.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion("100.0", pi));
        }

        // Parity guard: the clamp floor for EVERY platform is exactly that platform's
        // PlatformInfo.DefaultMinimumOS — a single source of truth. If a divergent literal
        // floor is ever re-introduced into the extractor, this breaks.
        [Theory]
        [InlineData(ApplePlatform.iOS)]
        [InlineData(ApplePlatform.macOS)]
        [InlineData(ApplePlatform.tvOS)]
        [InlineData(ApplePlatform.MacCatalyst)]
        public void ClampMinimumOSVersion_FloorIsPlatformDefaultMinimumOS(ApplePlatform platform)
        {
            var pi = PlatformInfoFactory.Create(platform);
            // A clearly-below-any-floor input forces the clamp to return the floor.
            Assert.Equal(pi.DefaultMinimumOS, XCFrameworkMetadataExtractor.ClampMinimumOSVersion("1.0", pi));
            // The null/fallback path returns the same floor.
            Assert.Equal(pi.DefaultMinimumOS, XCFrameworkMetadataExtractor.ClampMinimumOSVersion(null, pi));
        }

        // The no-PlatformInfo overload falls back to the conservative iOS floor (the highest
        // Apple floor), so an unknown target can never under-restrict. Ties the bare fallback to
        // PlatformInfoFactory rather than a free-standing literal.
        [Fact]
        public void ClampMinimumOSVersion_NoPlatformInfo_FallsBackToIosFloor()
        {
            var iosFloor = PlatformInfoFactory.Create(ApplePlatform.iOS).DefaultMinimumOS;
            Assert.Equal(iosFloor, XCFrameworkMetadataExtractor.ClampMinimumOSVersion("13.0"));
            Assert.Equal(iosFloor, XCFrameworkMetadataExtractor.ClampMinimumOSVersion(null));
        }
    }

    #endregion

    #region C. Full Extraction Tests

    public class MetadataExtractionTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Extract_RealVersion_ExtractsCorrectly()
        {
            var fixture = new MetadataFixture("ImagePipeline");
            try
            {
                fixture.WriteInnerPlist("12.8.0", "13.0", "18.0");
                fixture.WriteOuterPlist();

                var metadata = XCFrameworkMetadataExtractor.Extract(
                    fixture.DylibPath, fixture.XCFrameworkPath, "ImagePipeline", _logger, fixture.Runner);

                Assert.Equal("12.8.0", metadata.LibraryVersion);
                Assert.Equal("12.8.0", metadata.PackageVersion);
                Assert.False(metadata.IsVersionPlaceholder);
                Assert.Equal("13.0", metadata.MinimumOSVersion);
                Assert.Equal("15.0", metadata.EffectiveMinimumOSVersion); // clamped
                Assert.Equal("18.0", metadata.SdkVersion);
                Assert.Equal("ImagePipeline", metadata.ModuleName);
            }
            finally { fixture.Dispose(); }
        }

        [Fact]
        public void Extract_PlaceholderVersion_SetsZeroZeroZero()
        {
            var fixture = new MetadataFixture("DocScanUX");
            try
            {
                fixture.WriteInnerPlist("1.0", "16.0", null);
                fixture.WriteOuterPlist();

                var metadata = XCFrameworkMetadataExtractor.Extract(
                    fixture.DylibPath, fixture.XCFrameworkPath, "DocScanUX", _logger, fixture.Runner);

                Assert.Equal("1.0", metadata.LibraryVersion);
                Assert.Equal("0.0.0", metadata.PackageVersion);
                Assert.True(metadata.IsVersionPlaceholder);
            }
            finally { fixture.Dispose(); }
        }

        [Fact]
        public void Extract_MissingVersionKey_SetsZeroZeroZero()
        {
            var fixture = new MetadataFixture("TestLib");
            try
            {
                fixture.WriteInnerPlistRaw("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0">
                    <dict>
                        <key>MinimumOSVersion</key>
                        <string>15.0</string>
                    </dict>
                    </plist>
                    """);
                fixture.WriteOuterPlist();

                var metadata = XCFrameworkMetadataExtractor.Extract(
                    fixture.DylibPath, fixture.XCFrameworkPath, "TestLib", _logger, fixture.Runner);

                Assert.Null(metadata.LibraryVersion);
                Assert.Equal("0.0.0", metadata.PackageVersion);
                Assert.True(metadata.IsVersionPlaceholder);
            }
            finally { fixture.Dispose(); }
        }

        [Fact]
        public void Extract_HighMinOS_PreservesRawVersion()
        {
            var fixture = new MetadataFixture("DocScan");
            try
            {
                fixture.WriteInnerPlist("6.11.0", "16.0", "18.0");
                fixture.WriteOuterPlist();

                var metadata = XCFrameworkMetadataExtractor.Extract(
                    fixture.DylibPath, fixture.XCFrameworkPath, "DocScan", _logger, fixture.Runner);

                Assert.Equal("16.0", metadata.MinimumOSVersion);
                Assert.Equal("16.0", metadata.EffectiveMinimumOSVersion); // no clamping needed
            }
            finally { fixture.Dispose(); }
        }

        // End-to-end repro of the CMake-vendor sentinel plist shape (MinimumOSVersion=100.0). The raw
        // value is preserved on the metadata for diagnostics, but EffectiveMinimumOSVersion
        // — which is what every csproj/props emitter consumes — must be the floor so the
        // generated <SupportedOSPlatformVersion> never exceeds the consumer's TPV.
        [Fact]
        public void Extract_VendorSentinelMinOS_ClampsToFloor()
        {
            var fixture = new MetadataFixture("CloudPlatformSdkAuth");
            try
            {
                fixture.WriteInnerPlist("12.10.0", "100.0", "18.2");
                fixture.WriteOuterPlist();

                var metadata = XCFrameworkMetadataExtractor.Extract(
                    fixture.DylibPath, fixture.XCFrameworkPath, "CloudPlatformSdkAuth", _logger, fixture.Runner);

                Assert.Equal("100.0", metadata.MinimumOSVersion);
                Assert.Equal("15.0", metadata.EffectiveMinimumOSVersion);
            }
            finally { fixture.Dispose(); }
        }

        // End-to-end of the primary bug: a macOS framework deploying to macOS 12 must emit
        // EffectiveMinimumOSVersion=12.0 (what every csproj/props emitter writes into
        // <SupportedOSPlatformVersion>), NOT a forced 15.0 that excludes macOS 12-14 consumers.
        [Theory]
        [InlineData("12.0")]
        [InlineData("13.0")]
        [InlineData("14.0")]
        public void Extract_macOS_AtOrAboveMacFloor_KeepsSourceMin(string raw)
        {
            var fixture = new MetadataFixture("MacLib");
            try
            {
                fixture.WriteInnerPlist("3.0.0", raw, "14.0");
                fixture.WriteOuterPlist();

                var macOS = PlatformInfoFactory.Create(ApplePlatform.macOS);
                var metadata = XCFrameworkMetadataExtractor.Extract(
                    fixture.DylibPath, fixture.XCFrameworkPath, "MacLib", _logger, fixture.Runner, macOS);

                Assert.Equal(raw, metadata.MinimumOSVersion);
                Assert.Equal(raw, metadata.EffectiveMinimumOSVersion); // NOT bumped to 15
            }
            finally { fixture.Dispose(); }
        }

        // Regression guard via the full Extract path: iOS still clamps below-floor up to 15.
        [Fact]
        public void Extract_iOS_BelowFloor_StillClampsTo15()
        {
            var fixture = new MetadataFixture("IosLib");
            try
            {
                fixture.WriteInnerPlist("3.0.0", "13.0", "18.0");
                fixture.WriteOuterPlist();

                var iOS = PlatformInfoFactory.Create(ApplePlatform.iOS);
                var metadata = XCFrameworkMetadataExtractor.Extract(
                    fixture.DylibPath, fixture.XCFrameworkPath, "IosLib", _logger, fixture.Runner, iOS);

                Assert.Equal("13.0", metadata.MinimumOSVersion);
                Assert.Equal("15.0", metadata.EffectiveMinimumOSVersion);
            }
            finally { fixture.Dispose(); }
        }

        // ObjC pipeline path: ExtractFromFrameworkPath must read the slice for the generation
        // target's platform. A macOS-only framework's own slice min-OS (14.0) must be read and
        // preserved (>= macOS floor 12.0), not lost to the fallback because the lookup only
        // matched the iOS slice.
        [Fact]
        public void ExtractFromFrameworkPath_macOS_ReadsMacOsSliceMinOS()
        {
            var fixture = new MetadataFixture("MacObjCLib", "macos-arm64", "macos", supportedPlatformVariant: null);
            try
            {
                fixture.WriteInnerPlist("3.0.0", "14.0", "14.0");
                fixture.WriteOuterPlist();

                var macOS = PlatformInfoFactory.Create(ApplePlatform.macOS);
                var metadata = XCFrameworkMetadataExtractor.ExtractFromFrameworkPath(
                    fixture.XCFrameworkPath, "MacObjCLib", _logger, fixture.Runner, macOS);

                Assert.Equal("14.0", metadata.MinimumOSVersion);          // slice read, not fallback
                Assert.Equal("14.0", metadata.EffectiveMinimumOSVersion); // >= macOS floor 12, preserved
            }
            finally { fixture.Dispose(); }
        }

        // Regression guard: the iOS slice is still selected and clamped to the iOS floor.
        [Fact]
        public void ExtractFromFrameworkPath_iOS_ReadsIosSliceAndClampsTo15()
        {
            var fixture = new MetadataFixture("IosObjCLib");
            try
            {
                fixture.WriteInnerPlist("3.0.0", "13.0", "18.0");
                fixture.WriteOuterPlist();

                var iOS = PlatformInfoFactory.Create(ApplePlatform.iOS);
                var metadata = XCFrameworkMetadataExtractor.ExtractFromFrameworkPath(
                    fixture.XCFrameworkPath, "IosObjCLib", _logger, fixture.Runner, iOS);

                Assert.Equal("13.0", metadata.MinimumOSVersion);
                Assert.Equal("15.0", metadata.EffectiveMinimumOSVersion);
            }
            finally { fixture.Dispose(); }
        }

        // No generation target supplied: preserve the historical iOS-first slice default.
        [Fact]
        public void ExtractFromFrameworkPath_NullPlatform_FallsBackToIosSlice()
        {
            var fixture = new MetadataFixture("DefaultObjCLib");
            try
            {
                fixture.WriteInnerPlist("3.0.0", "13.0", "18.0");
                fixture.WriteOuterPlist();

                var metadata = XCFrameworkMetadataExtractor.ExtractFromFrameworkPath(
                    fixture.XCFrameworkPath, "DefaultObjCLib", _logger, fixture.Runner);

                Assert.Equal("13.0", metadata.MinimumOSVersion);          // iOS slice still found
                Assert.Equal("15.0", metadata.EffectiveMinimumOSVersion); // iOS fallback floor
            }
            finally { fixture.Dispose(); }
        }

        [Fact]
        public void Extract_PlatformList_ContainsSliceInfo()
        {
            var fixture = new MetadataFixture("ImagePipeline");
            try
            {
                fixture.WriteInnerPlist("12.8.0", "13.0", null);
                fixture.WriteOuterPlist();

                var metadata = XCFrameworkMetadataExtractor.Extract(
                    fixture.DylibPath, fixture.XCFrameworkPath, "ImagePipeline", _logger, fixture.Runner);

                Assert.Contains("ios-simulator", metadata.Platforms);
            }
            finally { fixture.Dispose(); }
        }
    }

    #endregion

    #region D. Metadata JSON Emission Tests

    public class MetadataJsonEmissionTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void EmitMetadataJson_CreatesFile()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = CreateTestMetadata();
                XCFrameworkMetadataExtractor.EmitMetadataJson(metadata, dir, _logger);

                Assert.True(File.Exists(Path.Combine(dir, "binding-metadata.json")));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataJson_ContainsAllFields()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = CreateTestMetadata();
                XCFrameworkMetadataExtractor.EmitMetadataJson(metadata, dir, _logger);

                var json = JObject.Parse(File.ReadAllText(Path.Combine(dir, "binding-metadata.json")));
                Assert.Equal("TestModule", json["moduleName"]?.ToString());
                Assert.Equal("3.2.1", json["libraryVersion"]?.ToString());
                Assert.Equal("3.2.1", json["packageVersion"]?.ToString());
                Assert.False(json["isVersionPlaceholder"]?.Value<bool>());
                Assert.Equal("13.0", json["minimumOSVersion"]?.ToString());
                Assert.Equal("15.0", json["effectiveMinimumOSVersion"]?.ToString());
                Assert.NotNull(json["platforms"]);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataJson_PlaceholderVersion_IncludesRawVersion()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = new XCFrameworkMetadata
                {
                    LibraryVersion = "1.0",
                    PackageVersion = "0.0.0",
                    IsVersionPlaceholder = true,
                    MinimumOSVersion = "15.0",
                    EffectiveMinimumOSVersion = "15.0",
                    SdkVersion = null,
                    ModuleName = "PlaceholderLib",
                    Platforms = new List<string>()
                };
                XCFrameworkMetadataExtractor.EmitMetadataJson(metadata, dir, _logger);

                var json = JObject.Parse(File.ReadAllText(Path.Combine(dir, "binding-metadata.json")));
                Assert.Equal("1.0", json["libraryVersion"]?.ToString());
                Assert.Equal("0.0.0", json["packageVersion"]?.ToString());
                Assert.True(json["isVersionPlaceholder"]?.Value<bool>());
            }
            finally { Directory.Delete(dir, true); }
        }

        private static XCFrameworkMetadata CreateTestMetadata() => new()
        {
            LibraryVersion = "3.2.1",
            PackageVersion = "3.2.1",
            IsVersionPlaceholder = false,
            MinimumOSVersion = "13.0",
            EffectiveMinimumOSVersion = "15.0",
            SdkVersion = "18.0",
            ModuleName = "TestModule",
            Platforms = new List<string> { "ios-simulator" }
        };

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"meta_json_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region Test Fixture

    /// <summary>
    /// Helper to build temporary xcframework structures for metadata extraction tests.
    /// </summary>
    internal sealed class MetadataFixture : IDisposable
    {
        private readonly string _rootDir;
        private readonly string _libraryIdentifier;
        private readonly string _supportedPlatform;
        private readonly string? _supportedPlatformVariant;
        public string XCFrameworkPath { get; }
        public string DylibPath { get; }
        public MockCommandRunner Runner { get; }

        // Defaults describe an iOS simulator slice so existing callers are unchanged;
        // pass a macOS/tvOS slice descriptor to exercise platform-aware slice selection.
        public MetadataFixture(
            string moduleName,
            string libraryIdentifier = "ios-arm64-simulator",
            string supportedPlatform = "ios",
            string? supportedPlatformVariant = "simulator")
        {
            _rootDir = Path.Combine(Path.GetTempPath(), $"meta_test_{Guid.NewGuid():N}");
            _libraryIdentifier = libraryIdentifier;
            _supportedPlatform = supportedPlatform;
            _supportedPlatformVariant = supportedPlatformVariant;
            XCFrameworkPath = Path.Combine(_rootDir, $"{moduleName}.xcframework");
            var frameworkDir = Path.Combine(XCFrameworkPath, libraryIdentifier, $"{moduleName}.framework");
            DylibPath = Path.Combine(frameworkDir, moduleName);
            Directory.CreateDirectory(frameworkDir);
            File.WriteAllText(DylibPath, ""); // stub
            Runner = new MockCommandRunner();
        }

        public void WriteInnerPlist(string version, string minOS, string? sdkVersion)
        {
            var sdkEntry = sdkVersion != null
                ? $"""

                        <key>DTPlatformVersion</key>
                        <string>{sdkVersion}</string>
                    """
                : "";

            var xml = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>CFBundleShortVersionString</key>
                    <string>{version}</string>
                    <key>MinimumOSVersion</key>
                    <string>{minOS}</string>{sdkEntry}
                </dict>
                </plist>
                """;
            WriteInnerPlistRaw(xml);
        }

        public void WriteInnerPlistRaw(string xml)
        {
            var frameworkDir = Path.GetDirectoryName(DylibPath)!;
            var plistPath = Path.Combine(frameworkDir, "Info.plist");
            File.WriteAllText(plistPath, xml);
            // Set up plutil mock to return the same XML
            Runner.SetResponse("plutil", 0, xml);
        }

        public void WriteOuterPlist()
        {
            var moduleName = Path.GetFileNameWithoutExtension(
                Path.GetFileNameWithoutExtension(XCFrameworkPath)); // strip .xcframework
            var variantEntry = _supportedPlatformVariant != null
                ? $"""

                            <key>SupportedPlatformVariant</key>
                            <string>{_supportedPlatformVariant}</string>
                    """
                : "";
            var xml = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>LibraryIdentifier</key>
                            <string>{_libraryIdentifier}</string>
                            <key>LibraryPath</key>
                            <string>{moduleName}.framework</string>
                            <key>BinaryPath</key>
                            <string>{moduleName}.framework/{moduleName}</string>
                            <key>SupportedArchitectures</key>
                            <array>
                                <string>arm64</string>
                            </array>
                            <key>SupportedPlatform</key>
                            <string>{_supportedPlatform}</string>{variantEntry}
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
            File.WriteAllText(Path.Combine(XCFrameworkPath, "Info.plist"), xml);
        }

        public void Dispose()
        {
            try { Directory.Delete(_rootDir, true); } catch { }
        }
    }

    #endregion

    #region E. UpdateMetadataPropsWrapperStatus Tests

    public class MetadataPropsUpdateTests : IDisposable
    {
        private static readonly ILogger _logger = NullLogger.Instance;
        private readonly string _tempDir;

        public MetadataPropsUpdateTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"metadata-update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void UpdateWrapperStatus_UpdatesExistingFalseToTrue()
        {
            WriteInitialProps(hasWrapper: false, sliceCount: 0);

            XCFrameworkMetadataExtractor.UpdateMetadataPropsWrapperStatus(
                _tempDir, hasWrapper: true, "TestSwiftBindings", 2, _logger);

            var doc = System.Xml.Linq.XDocument.Load(Path.Combine(_tempDir, "binding-metadata.props"));
            var pg = doc.Root!.Element("PropertyGroup")!;
            Assert.Equal("True", pg.Element("_SwiftBindingHasWrapperXCFramework")?.Value);
            Assert.Equal("2", pg.Element("_SwiftBindingWrapperSliceCount")?.Value);
        }

        [Fact]
        public void UpdateWrapperStatus_UpdatesExistingTrueToFalse()
        {
            WriteInitialProps(hasWrapper: true, sliceCount: 1);

            XCFrameworkMetadataExtractor.UpdateMetadataPropsWrapperStatus(
                _tempDir, hasWrapper: false, "TestSwiftBindings", 0, _logger);

            var doc = System.Xml.Linq.XDocument.Load(Path.Combine(_tempDir, "binding-metadata.props"));
            var pg = doc.Root!.Element("PropertyGroup")!;
            Assert.Equal("False", pg.Element("_SwiftBindingHasWrapperXCFramework")?.Value);
            Assert.Equal("0", pg.Element("_SwiftBindingWrapperSliceCount")?.Value);
        }

        [Fact]
        public void UpdateWrapperStatus_PreservesOtherProperties()
        {
            WriteInitialProps(hasWrapper: false, sliceCount: 0);

            XCFrameworkMetadataExtractor.UpdateMetadataPropsWrapperStatus(
                _tempDir, hasWrapper: true, "TestSwiftBindings", 1, _logger);

            var doc = System.Xml.Linq.XDocument.Load(Path.Combine(_tempDir, "binding-metadata.props"));
            var pg = doc.Root!.Element("PropertyGroup")!;
            Assert.Equal("1.2.3", pg.Element("_SwiftBindingPackageVersion")?.Value);
            Assert.Equal("TestModule", pg.Element("_SwiftBindingModuleName")?.Value);
        }

        [Fact]
        public void UpdateWrapperStatus_MissingFile_DoesNotThrow()
        {
            var emptyDir = Path.Combine(_tempDir, "empty");
            Directory.CreateDirectory(emptyDir);

            // Should log a warning but not throw
            XCFrameworkMetadataExtractor.UpdateMetadataPropsWrapperStatus(
                emptyDir, hasWrapper: true, "TestSwiftBindings", 1, _logger);
        }

        [Fact]
        public void UpdateWrapperStatus_AddsElementsIfMissing()
        {
            // Write a minimal props file without wrapper properties
            var propsPath = Path.Combine(_tempDir, "binding-metadata.props");
            File.WriteAllText(propsPath, """
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>1.0.0</_SwiftBindingPackageVersion>
                    <_SwiftBindingModuleName>Test</_SwiftBindingModuleName>
                  </PropertyGroup>
                </Project>
                """);

            XCFrameworkMetadataExtractor.UpdateMetadataPropsWrapperStatus(
                _tempDir, hasWrapper: true, "TestSwiftBindings", 2, _logger);

            var doc = System.Xml.Linq.XDocument.Load(propsPath);
            var pg = doc.Root!.Element("PropertyGroup")!;
            Assert.Equal("True", pg.Element("_SwiftBindingHasWrapperXCFramework")?.Value);
            Assert.Equal("2", pg.Element("_SwiftBindingWrapperSliceCount")?.Value);
        }

        [Fact]
        public void UpdateWrapperStatus_PersistsUnmetContractArchitectures()
        {
            // An explicit arch the fold failed to deliver is persisted so the SDK can fail the build
            // even though ContinueOnError swallows the generator exit code.
            WriteInitialProps(hasWrapper: true, sliceCount: 1);

            XCFrameworkMetadataExtractor.UpdateMetadataPropsWrapperStatus(
                _tempDir, hasWrapper: true, "TestSwiftBindings", 1, _logger,
                unmetContractArchitectures: new[] { "x86_64" });

            var doc = System.Xml.Linq.XDocument.Load(Path.Combine(_tempDir, "binding-metadata.props"));
            var pg = doc.Root!.Element("PropertyGroup")!;
            // HasWrapper stays True (primary restored) — the violation rides the separate prop.
            Assert.Equal("True", pg.Element("_SwiftBindingHasWrapperXCFramework")?.Value);
            Assert.Equal("x86_64", pg.Element("_SwiftBindingWrapperUnmetContractArchitectures")?.Value);
        }

        [Fact]
        public void UpdateWrapperStatus_MultipleUnmetArchs_JoinedWithSemicolon()
        {
            WriteInitialProps(hasWrapper: true, sliceCount: 1);

            XCFrameworkMetadataExtractor.UpdateMetadataPropsWrapperStatus(
                _tempDir, hasWrapper: true, "TestSwiftBindings", 1, _logger,
                unmetContractArchitectures: new[] { "x86_64", "arm64" });

            var doc = System.Xml.Linq.XDocument.Load(Path.Combine(_tempDir, "binding-metadata.props"));
            var pg = doc.Root!.Element("PropertyGroup")!;
            Assert.Equal("x86_64;arm64", pg.Element("_SwiftBindingWrapperUnmetContractArchitectures")?.Value);
        }

        [Fact]
        public void UpdateWrapperStatus_ClearsStaleUnmetContractWhenSatisfied()
        {
            // A prior build persisted a violation; a clean recompile (no unmet archs) must CLEAR it,
            // otherwise the SDK <Error> would fire forever even after the fat fold succeeds.
            var propsPath = Path.Combine(_tempDir, "binding-metadata.props");
            File.WriteAllText(propsPath, """
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>1.2.3</_SwiftBindingPackageVersion>
                    <_SwiftBindingModuleName>TestModule</_SwiftBindingModuleName>
                    <_SwiftBindingHasWrapperXCFramework>True</_SwiftBindingHasWrapperXCFramework>
                    <_SwiftBindingWrapperSliceCount>1</_SwiftBindingWrapperSliceCount>
                    <_SwiftBindingWrapperUnmetContractArchitectures>x86_64</_SwiftBindingWrapperUnmetContractArchitectures>
                  </PropertyGroup>
                </Project>
                """);

            XCFrameworkMetadataExtractor.UpdateMetadataPropsWrapperStatus(
                _tempDir, hasWrapper: true, "TestSwiftBindings", 2, _logger,
                unmetContractArchitectures: null);

            var doc = System.Xml.Linq.XDocument.Load(propsPath);
            var pg = doc.Root!.Element("PropertyGroup")!;
            Assert.Equal(string.Empty, pg.Element("_SwiftBindingWrapperUnmetContractArchitectures")?.Value);
        }

        private void WriteInitialProps(bool hasWrapper, int sliceCount)
        {
            var propsPath = Path.Combine(_tempDir, "binding-metadata.props");
            File.WriteAllText(propsPath, $"""
                <Project>
                  <PropertyGroup>
                    <_SwiftBindingPackageVersion>1.2.3</_SwiftBindingPackageVersion>
                    <_SwiftBindingMinimumOSVersion>15.0</_SwiftBindingMinimumOSVersion>
                    <_SwiftBindingModuleName>TestModule</_SwiftBindingModuleName>
                    <_SwiftBindingIsVersionPlaceholder>False</_SwiftBindingIsVersionPlaceholder>
                    <_SwiftBindingHasWrapperXCFramework>{hasWrapper}</_SwiftBindingHasWrapperXCFramework>
                    <_SwiftBindingWrapperModuleName>TestSwiftBindings</_SwiftBindingWrapperModuleName>
                    <_SwiftBindingWrapperSliceCount>{sliceCount}</_SwiftBindingWrapperSliceCount>
                  </PropertyGroup>
                </Project>
                """);
        }
    }

    #endregion
}
