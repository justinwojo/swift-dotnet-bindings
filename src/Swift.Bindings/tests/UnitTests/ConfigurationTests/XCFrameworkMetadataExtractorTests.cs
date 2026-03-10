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
        public void ClampMinimumOSVersion_Below16_ClampsTo16()
        {
            Assert.Equal("16.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion("13.0"));
        }

        [Fact]
        public void ClampMinimumOSVersion_Exactly15_ClampsTo16()
        {
            Assert.Equal("16.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion("15.0"));
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
            Assert.Equal("16.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion(null));
        }

        [Fact]
        public void ClampMinimumOSVersion_Empty_ReturnsFallback()
        {
            Assert.Equal("16.0", XCFrameworkMetadataExtractor.ClampMinimumOSVersion(""));
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
            var fixture = new MetadataFixture("Nuke");
            try
            {
                fixture.WriteInnerPlist("12.8.0", "13.0", "18.0");
                fixture.WriteOuterPlist();

                var metadata = XCFrameworkMetadataExtractor.Extract(
                    fixture.DylibPath, fixture.XCFrameworkPath, "Nuke", _logger, fixture.Runner);

                Assert.Equal("12.8.0", metadata.LibraryVersion);
                Assert.Equal("12.8.0", metadata.PackageVersion);
                Assert.False(metadata.IsVersionPlaceholder);
                Assert.Equal("13.0", metadata.MinimumOSVersion);
                Assert.Equal("16.0", metadata.EffectiveMinimumOSVersion); // clamped
                Assert.Equal("18.0", metadata.SdkVersion);
                Assert.Equal("Nuke", metadata.ModuleName);
            }
            finally { fixture.Dispose(); }
        }

        [Fact]
        public void Extract_PlaceholderVersion_SetsZeroZeroZero()
        {
            var fixture = new MetadataFixture("BlinkIDUX");
            try
            {
                fixture.WriteInnerPlist("1.0", "16.0", null);
                fixture.WriteOuterPlist();

                var metadata = XCFrameworkMetadataExtractor.Extract(
                    fixture.DylibPath, fixture.XCFrameworkPath, "BlinkIDUX", _logger, fixture.Runner);

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
            var fixture = new MetadataFixture("BlinkID");
            try
            {
                fixture.WriteInnerPlist("6.11.0", "16.0", "18.0");
                fixture.WriteOuterPlist();

                var metadata = XCFrameworkMetadataExtractor.Extract(
                    fixture.DylibPath, fixture.XCFrameworkPath, "BlinkID", _logger, fixture.Runner);

                Assert.Equal("16.0", metadata.MinimumOSVersion);
                Assert.Equal("16.0", metadata.EffectiveMinimumOSVersion); // no clamping needed
            }
            finally { fixture.Dispose(); }
        }

        [Fact]
        public void Extract_PlatformList_ContainsSliceInfo()
        {
            var fixture = new MetadataFixture("Nuke");
            try
            {
                fixture.WriteInnerPlist("12.8.0", "13.0", null);
                fixture.WriteOuterPlist();

                var metadata = XCFrameworkMetadataExtractor.Extract(
                    fixture.DylibPath, fixture.XCFrameworkPath, "Nuke", _logger, fixture.Runner);

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
                Assert.Equal("16.0", json["effectiveMinimumOSVersion"]?.ToString());
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
                    EffectiveMinimumOSVersion = "16.0",
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
            EffectiveMinimumOSVersion = "16.0",
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
        public string XCFrameworkPath { get; }
        public string DylibPath { get; }
        public MockCommandRunner Runner { get; }

        public MetadataFixture(string moduleName)
        {
            _rootDir = Path.Combine(Path.GetTempPath(), $"meta_test_{Guid.NewGuid():N}");
            XCFrameworkPath = Path.Combine(_rootDir, $"{moduleName}.xcframework");
            var frameworkDir = Path.Combine(XCFrameworkPath, "ios-arm64-simulator", $"{moduleName}.framework");
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
            var xml = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>LibraryIdentifier</key>
                            <string>ios-arm64-simulator</string>
                            <key>LibraryPath</key>
                            <string>{moduleName}.framework</string>
                            <key>BinaryPath</key>
                            <string>{moduleName}.framework/{moduleName}</string>
                            <key>SupportedArchitectures</key>
                            <array>
                                <string>arm64</string>
                            </array>
                            <key>SupportedPlatform</key>
                            <string>ios</string>
                            <key>SupportedPlatformVariant</key>
                            <string>simulator</string>
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
}
