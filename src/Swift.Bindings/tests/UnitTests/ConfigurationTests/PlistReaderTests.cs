// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    public class PlistReaderTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void ReadPlistDict_PlutilSuccess_ReturnsParsedDict()
        {
            var runner = new MockCommandRunner();
            var plistXml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>CFBundleShortVersionString</key>
                    <string>12.8.0</string>
                    <key>MinimumOSVersion</key>
                    <string>13.0</string>
                </dict>
                </plist>
                """;
            runner.SetResponse("plutil", 0, plistXml);

            var dir = CreateTempDir();
            try
            {
                var plistPath = Path.Combine(dir, "Info.plist");
                File.WriteAllText(plistPath, "binary-content"); // doesn't matter, plutil mock returns XML

                var result = PlistReader.ReadPlistDict(plistPath, runner, _logger);

                Assert.NotNull(result);
                Assert.Equal("12.8.0", result["CFBundleShortVersionString"]);
                Assert.Equal("13.0", result["MinimumOSVersion"]);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ReadPlistDict_PlutilFails_FallsBackToXmlLoad()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("plutil", 1, "", "plutil error");

            var dir = CreateTempDir();
            try
            {
                var plistPath = Path.Combine(dir, "Info.plist");
                var xmlPlist = """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0">
                    <dict>
                        <key>CFBundleName</key>
                        <string>TestLib</string>
                    </dict>
                    </plist>
                    """;
                File.WriteAllText(plistPath, xmlPlist);

                var result = PlistReader.ReadPlistDict(plistPath, runner, _logger);

                Assert.NotNull(result);
                Assert.Equal("TestLib", result["CFBundleName"]);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ReadPlistDict_PlutilFails_BinaryPlist_ReturnsNull()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("plutil", 1, "", "plutil error");

            var dir = CreateTempDir();
            try
            {
                var plistPath = Path.Combine(dir, "Info.plist");
                // Write binary-like content that isn't valid XML
                File.WriteAllBytes(plistPath, new byte[] { 0x62, 0x70, 0x6C, 0x69, 0x73, 0x74, 0x30, 0x30 });

                var result = PlistReader.ReadPlistDict(plistPath, runner, _logger);

                Assert.Null(result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ReadPlistDict_MissingPlistFile_ReturnsNull()
        {
            var result = PlistReader.ReadPlistDict("/nonexistent/Info.plist", null, _logger);
            Assert.Null(result);
        }

        [Fact]
        public void ReadPlistDict_XmlPlist_ParsesAllKeyTypes()
        {
            var runner = new MockCommandRunner();
            var plistXml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>StringKey</key>
                    <string>hello</string>
                    <key>IntKey</key>
                    <integer>42</integer>
                    <key>BoolKey</key>
                    <true/>
                    <key>ArrayKey</key>
                    <array>
                        <string>item1</string>
                        <string>item2</string>
                    </array>
                </dict>
                </plist>
                """;
            runner.SetResponse("plutil", 0, plistXml);

            var dir = CreateTempDir();
            try
            {
                var plistPath = Path.Combine(dir, "Info.plist");
                File.WriteAllText(plistPath, "stub");

                var result = PlistReader.ReadPlistDict(plistPath, runner, _logger);

                Assert.NotNull(result);
                Assert.Equal("hello", result["StringKey"]);
                Assert.Equal(42, result["IntKey"]);
                Assert.Equal(true, result["BoolKey"]);
                var arr = Assert.IsType<List<object>>(result["ArrayKey"]);
                Assert.Equal(2, arr.Count);
                Assert.Equal("item1", arr[0]);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ReadPlistDict_FrameworkInnerPlist_ExtractsMinimumOSVersion()
        {
            // Integration-style test with mock plutil returning framework plist data
            var runner = new MockCommandRunner();
            var plistXml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>CFBundleShortVersionString</key>
                    <string>12.8.0</string>
                    <key>CFBundleIdentifier</key>
                    <string>com.example.imagepipeline</string>
                    <key>MinimumOSVersion</key>
                    <string>13.0</string>
                    <key>DTPlatformVersion</key>
                    <string>18.0</string>
                    <key>CFBundleSupportedPlatforms</key>
                    <array>
                        <string>iPhoneSimulator</string>
                    </array>
                </dict>
                </plist>
                """;
            runner.SetResponse("plutil", 0, plistXml);

            var dir = CreateTempDir();
            try
            {
                var plistPath = Path.Combine(dir, "Info.plist");
                File.WriteAllText(plistPath, "stub");

                var result = PlistReader.ReadPlistDict(plistPath, runner, _logger);

                Assert.NotNull(result);
                Assert.Equal("13.0", result["MinimumOSVersion"]);
                Assert.Equal("12.8.0", result["CFBundleShortVersionString"]);
                Assert.Equal("18.0", result["DTPlatformVersion"]);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ReadPlistDict_WrapperPlist_ReadsDirectXml()
        {
            // Self-generated XML plists don't need plutil
            var dir = CreateTempDir();
            try
            {
                var plistPath = Path.Combine(dir, "Info.plist");
                var xmlPlist = """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0">
                    <dict>
                        <key>CFBundleIdentifier</key>
                        <string>com.swiftbindings.ImagePipelineSwiftBindings</string>
                        <key>MinimumOSVersion</key>
                        <string>13.0</string>
                    </dict>
                    </plist>
                    """;
                File.WriteAllText(plistPath, xmlPlist);

                // Pass null runner — XML fallback should work
                var result = PlistReader.ReadPlistDict(plistPath, null, _logger);

                Assert.NotNull(result);
                Assert.Equal("com.swiftbindings.ImagePipelineSwiftBindings", result["CFBundleIdentifier"]);
                Assert.Equal("13.0", result["MinimumOSVersion"]);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ParseXmlPlistString_ValidXml_ReturnsParsedDict()
        {
            var xml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>Key1</key>
                    <string>Value1</string>
                </dict>
                </plist>
                """;

            var result = PlistReader.ParseXmlPlistString(xml);

            Assert.NotNull(result);
            Assert.Equal("Value1", result["Key1"]);
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"plist_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
