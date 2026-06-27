// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    #region A. File Collection Tests

    public class SwiftWrapperFileCollectionTests
    {
        [Fact]
        public void CollectSwiftFiles_FindsSwiftFiles()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Swift.Module.swift"), "// code");
                File.WriteAllText(Path.Combine(dir, "SwiftBindings.swift"), "// code");
                var files = SwiftWrapperCompiler.CollectSwiftFiles(dir);
                Assert.Equal(2, files.Count);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CollectSwiftFiles_ExcludesSwiftUIBridge()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Swift.Module.swift"), "// code");
                File.WriteAllText(Path.Combine(dir, "Swift.Module.SwiftUIBridge.swift"), "// bridge");
                var files = SwiftWrapperCompiler.CollectSwiftFiles(dir);
                Assert.Single(files);
                Assert.Contains("Swift.Module.swift", files[0]);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CollectSwiftFiles_EmptyDirectory_ReturnsEmpty()
        {
            var dir = CreateTempDir();
            try
            {
                var files = SwiftWrapperCompiler.CollectSwiftFiles(dir);
                Assert.Empty(files);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CollectSwiftFiles_NonexistentDirectory_ReturnsEmpty()
        {
            var files = SwiftWrapperCompiler.CollectSwiftFiles("/nonexistent/path");
            Assert.Empty(files);
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"swc_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region A2. Bridge File Collection Tests

    public class SwiftBridgeFileCollectionTests
    {
        [Fact]
        public void CollectBridgeSwiftFiles_FindsBridgeFiles()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Module.SwiftUIBridge.swift"), "// bridge");
                File.WriteAllText(Path.Combine(dir, "Module.swift"), "// wrapper");
                var files = SwiftWrapperCompiler.CollectBridgeSwiftFiles(dir);
                Assert.Single(files);
                Assert.Contains("Module.SwiftUIBridge.swift", files[0]);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CollectBridgeSwiftFiles_MultipleBridgeFiles()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "ImagePipeline.SwiftUIBridge.swift"), "// bridge1");
                File.WriteAllText(Path.Combine(dir, "VectorAnimation.SwiftUIBridge.swift"), "// bridge2");
                var files = SwiftWrapperCompiler.CollectBridgeSwiftFiles(dir);
                Assert.Equal(2, files.Count);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CollectBridgeSwiftFiles_ExcludesNonBridgeFiles()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Module.swift"), "// wrapper");
                File.WriteAllText(Path.Combine(dir, "Module.Wrappers.swift"), "// wrappers");
                var files = SwiftWrapperCompiler.CollectBridgeSwiftFiles(dir);
                Assert.Empty(files);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CollectBridgeSwiftFiles_EmptyDirectory_ReturnsEmpty()
        {
            var dir = CreateTempDir();
            try
            {
                var files = SwiftWrapperCompiler.CollectBridgeSwiftFiles(dir);
                Assert.Empty(files);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CollectBridgeSwiftFiles_NonexistentDirectory_ReturnsEmpty()
        {
            var files = SwiftWrapperCompiler.CollectBridgeSwiftFiles("/nonexistent/path");
            Assert.Empty(files);
        }

        [Fact]
        public void CollectSwiftFiles_And_CollectBridgeSwiftFiles_AreDisjoint()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Module.swift"), "// wrapper");
                File.WriteAllText(Path.Combine(dir, "Module.SwiftUIBridge.swift"), "// bridge");

                var wrapperFiles = SwiftWrapperCompiler.CollectSwiftFiles(dir);
                var bridgeFiles = SwiftWrapperCompiler.CollectBridgeSwiftFiles(dir);

                Assert.Single(wrapperFiles);
                Assert.Single(bridgeFiles);
                Assert.DoesNotContain(wrapperFiles[0], bridgeFiles);
                Assert.DoesNotContain(bridgeFiles[0], wrapperFiles);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"swb_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region B. Deployment Target Resolution Tests

    public class SwiftWrapperDeploymentTargetTests
    {
        [Fact]
        public void ResolveDeploymentTarget_ReadsMinimumOSVersion()
        {
            var dir = CreateTempDir();
            try
            {
                var plist = """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0">
                    <dict>
                        <key>MinimumOSVersion</key>
                        <string>16.0</string>
                        <key>CFBundleExecutable</key>
                        <string>DocScan</string>
                    </dict>
                    </plist>
                    """;
                File.WriteAllText(Path.Combine(dir, "Info.plist"), plist);
                var dylibPath = Path.Combine(dir, "DocScan");
                File.WriteAllText(dylibPath, "");

                var result = SwiftWrapperCompiler.ResolveDeploymentTarget(dylibPath, NullLogger.Instance);
                Assert.Equal("16.0", result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ResolveDeploymentTarget_NoPlist_FallsBackTo15()
        {
            var dir = CreateTempDir();
            try
            {
                var dylibPath = Path.Combine(dir, "SomeLib");
                File.WriteAllText(dylibPath, "");

                var result = SwiftWrapperCompiler.ResolveDeploymentTarget(dylibPath, NullLogger.Instance);
                Assert.Equal("15.0", result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ResolveDeploymentTarget_PlistMissingKey_FallsBackTo15()
        {
            var dir = CreateTempDir();
            try
            {
                var plist = """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0">
                    <dict>
                        <key>CFBundleExecutable</key>
                        <string>Lib</string>
                    </dict>
                    </plist>
                    """;
                File.WriteAllText(Path.Combine(dir, "Info.plist"), plist);
                var dylibPath = Path.Combine(dir, "Lib");
                File.WriteAllText(dylibPath, "");

                var result = SwiftWrapperCompiler.ResolveDeploymentTarget(dylibPath, NullLogger.Instance);
                Assert.Equal("15.0", result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ResolveDeploymentTarget_VendorSentinel_FallsBackToFloor()
        {
            // Some SDKs ship every framework with MinimumOSVersion=100.0
            // (a CMake/xcodebuild build-tool quirk). Without sentinel rejection the
            // value flows into `swiftc -target arm64-apple-ios100.0-simulator` and
            // the wrapper compile fails outright. Verify the value gets rejected
            // here in lockstep with the metadata extractor.
            var dir = CreateTempDir();
            try
            {
                var plist = """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0">
                    <dict>
                        <key>MinimumOSVersion</key>
                        <string>100.0</string>
                        <key>CFBundleExecutable</key>
                        <string>CloudPlatformSdkAuth</string>
                    </dict>
                    </plist>
                    """;
                File.WriteAllText(Path.Combine(dir, "Info.plist"), plist);
                var dylibPath = Path.Combine(dir, "CloudPlatformSdkAuth");
                File.WriteAllText(dylibPath, "");

                var result = SwiftWrapperCompiler.ResolveDeploymentTarget(dylibPath, NullLogger.Instance);
                Assert.Equal("15.0", result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ResolveDeploymentTarget_BelowFloor_RaisedToFloor()
        {
            var dir = CreateTempDir();
            try
            {
                var plist = """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0">
                    <dict>
                        <key>MinimumOSVersion</key>
                        <string>13.0</string>
                    </dict>
                    </plist>
                    """;
                File.WriteAllText(Path.Combine(dir, "Info.plist"), plist);
                var dylibPath = Path.Combine(dir, "OldLib");
                File.WriteAllText(dylibPath, "");

                var result = SwiftWrapperCompiler.ResolveDeploymentTarget(dylibPath, NullLogger.Instance);
                Assert.Equal("15.0", result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("16.x")]
        [InlineData("not-a-version")]
        [InlineData("v16.0")]
        public void ResolveDeploymentTarget_MalformedPlistValue_FallsBackToFloor(string malformed)
        {
            // A plist that parses cleanly but carries a non-numeric MinimumOSVersion
            // must be clamped to the floor — never written verbatim into the swiftc
            // target triple. Mirrors ClampMinimumOSVersion's malformed-input contract
            // through the plist-reader path.
            var dir = CreateTempDir();
            try
            {
                var plist = $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0">
                    <dict>
                        <key>MinimumOSVersion</key>
                        <string>{malformed}</string>
                    </dict>
                    </plist>
                    """;
                File.WriteAllText(Path.Combine(dir, "Info.plist"), plist);
                var dylibPath = Path.Combine(dir, "Lib");
                File.WriteAllText(dylibPath, "");

                var result = SwiftWrapperCompiler.ResolveDeploymentTarget(dylibPath, NullLogger.Instance);
                Assert.Equal("15.0", result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ResolveDeploymentTarget_UsesPlistReader_ViaPlutil()
        {
            var dir = CreateTempDir();
            try
            {
                // Write a binary-like Info.plist that isn't valid XML
                var plistPath = Path.Combine(dir, "Info.plist");
                File.WriteAllBytes(plistPath, new byte[] { 0x62, 0x70, 0x6C, 0x69 });
                var dylibPath = Path.Combine(dir, "SomeLib");
                File.WriteAllText(dylibPath, "");

                // Mock plutil to return XML with MinimumOSVersion
                var runner = new MockCommandRunner();
                runner.SetResponse("plutil", 0, """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0">
                    <dict>
                        <key>MinimumOSVersion</key>
                        <string>16.4</string>
                    </dict>
                    </plist>
                    """);

                var result = SwiftWrapperCompiler.ResolveDeploymentTarget(
                    dylibPath, NullLogger.Instance, runner);
                Assert.Equal("16.4", result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ResolveDeploymentTarget_PlistReaderFails_FallsBackToDefault()
        {
            var dir = CreateTempDir();
            try
            {
                // Write binary content and fail plutil
                var plistPath = Path.Combine(dir, "Info.plist");
                File.WriteAllBytes(plistPath, new byte[] { 0x62, 0x70 });
                var dylibPath = Path.Combine(dir, "SomeLib");
                File.WriteAllText(dylibPath, "");

                var runner = new MockCommandRunner();
                runner.SetResponse("plutil", 1, "", "error");

                var result = SwiftWrapperCompiler.ResolveDeploymentTarget(
                    dylibPath, NullLogger.Instance, runner);
                Assert.Equal("15.0", result);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"swc_dt_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region C. XCFramework Structure Tests

    public class SwiftWrapperStructureTests
    {
        [Fact]
        public void CreateXCFrameworkStructure_CreatesDirectoryTree()
        {
            var dir = CreateTempDir();
            try
            {
                var xcfwPath = Path.Combine(dir, "TestSwiftBindings.xcframework");
                var fwDir = Path.Combine(xcfwPath, "ios-arm64-simulator", "TestSwiftBindings.framework");

                SwiftWrapperCompiler.CreateXCFrameworkStructure(xcfwPath, fwDir, "TestSwiftBindings", "15.0");

                Assert.True(Directory.Exists(fwDir));
                Assert.True(File.Exists(Path.Combine(fwDir, "Info.plist")));
                Assert.True(File.Exists(Path.Combine(xcfwPath, "Info.plist")));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CreateXCFrameworkStructure_FrameworkPlistContainsModuleName()
        {
            var dir = CreateTempDir();
            try
            {
                var xcfwPath = Path.Combine(dir, "ImagePipelineSwiftBindings.xcframework");
                var fwDir = Path.Combine(xcfwPath, "ios-arm64-simulator", "ImagePipelineSwiftBindings.framework");

                SwiftWrapperCompiler.CreateXCFrameworkStructure(xcfwPath, fwDir, "ImagePipelineSwiftBindings", "16.0");

                var fwPlist = File.ReadAllText(Path.Combine(fwDir, "Info.plist"));
                Assert.Contains("ImagePipelineSwiftBindings", fwPlist);
                Assert.Contains("16.0", fwPlist);
                Assert.Contains("FMWK", fwPlist);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CreateXCFrameworkStructure_XCFrameworkPlistHasSimulatorSlice()
        {
            var dir = CreateTempDir();
            try
            {
                var xcfwPath = Path.Combine(dir, "TestSwiftBindings.xcframework");
                var fwDir = Path.Combine(xcfwPath, "ios-arm64-simulator", "TestSwiftBindings.framework");

                SwiftWrapperCompiler.CreateXCFrameworkStructure(xcfwPath, fwDir, "TestSwiftBindings", "15.0");

                var xcfwPlist = File.ReadAllText(Path.Combine(xcfwPath, "Info.plist"));
                Assert.Contains("ios-arm64-simulator", xcfwPlist);
                Assert.Contains("XFWK", xcfwPlist);
                Assert.Contains("simulator", xcfwPlist);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CreateXCFrameworkStructure_XCFrameworkPlistHasBinaryPath()
        {
            var dir = CreateTempDir();
            try
            {
                var xcfwPath = Path.Combine(dir, "TestSwiftBindings.xcframework");
                var fwDir = Path.Combine(xcfwPath, "ios-arm64-simulator", "TestSwiftBindings.framework");

                SwiftWrapperCompiler.CreateXCFrameworkStructure(xcfwPath, fwDir, "TestSwiftBindings", "15.0");

                var xcfwPlist = File.ReadAllText(Path.Combine(xcfwPath, "Info.plist"));
                // BinaryPath must be present and point to the dylib inside the .framework bundle
                Assert.Contains("<key>BinaryPath</key>", xcfwPlist);
                Assert.Contains("TestSwiftBindings.framework/TestSwiftBindings", xcfwPlist);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void WriteXCFrameworkPlist_DualSlice_BothSlicesHaveBinaryPath()
        {
            var dir = CreateTempDir();
            try
            {
                var xcfwPath = Path.Combine(dir, "MyLib.xcframework");
                Directory.CreateDirectory(xcfwPath);

                SwiftWrapperCompiler.WriteXCFrameworkPlist(xcfwPath, "MyLib", includeDeviceSlice: true);

                var plist = File.ReadAllText(Path.Combine(xcfwPath, "Info.plist"));
                // Both sim and device slices should contain BinaryPath
                var binaryPathCount = plist.Split("<key>BinaryPath</key>").Length - 1;
                Assert.Equal(2, binaryPathCount);
                Assert.Contains("MyLib.framework/MyLib", plist);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void WriteXCFrameworkPlist_BinaryPathParsableByResolver()
        {
            var dir = CreateTempDir();
            try
            {
                var xcfwPath = Path.Combine(dir, "Foo.xcframework");
                Directory.CreateDirectory(xcfwPath);

                SwiftWrapperCompiler.WriteXCFrameworkPlist(xcfwPath, "Foo", includeDeviceSlice: false);

                // Parse with the resolver's plist parser and verify BinaryPath is populated
                var plistPath = Path.Combine(xcfwPath, "Info.plist");
                var slices = XCFrameworkResolver.ParseInfoPlist(plistPath);
                Assert.Single(slices);
                Assert.Equal("Foo.framework/Foo", slices[0].BinaryPath);
                Assert.Equal("Foo.framework", slices[0].LibraryPath);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void WriteXCFrameworkPlist_EmitsSliceArchitecture_NotHardcodedArm64()
        {
            var dir = CreateTempDir();
            try
            {
                var xcfwPath = Path.Combine(dir, "Intel.xcframework");
                Directory.CreateDirectory(xcfwPath);

                // A macOS x86_64 device slice — the plist must reflect x86_64, never a hardcoded arm64.
                var slice = new SliceVariant
                {
                    Platform = ApplePlatform.macOS,
                    IsSimulator = false,
                    SdkName = "macosx",
                    SliceId = "macos-arm64",
                    PlistPlatformName = "MacOSX",
                    XCFrameworkPlatformString = "macos",
                    XCFrameworkPlatformVariant = null,
                    Architecture = "x86_64",
                };

                SwiftWrapperCompiler.WriteXCFrameworkPlist(xcfwPath, "Intel", includeDeviceSlice: false, slice: slice);

                var parsed = XCFrameworkResolver.ParseInfoPlist(Path.Combine(xcfwPath, "Info.plist"));
                Assert.Single(parsed);
                Assert.Contains("x86_64", parsed[0].SupportedArchitectures);
                Assert.DoesNotContain("arm64", parsed[0].SupportedArchitectures);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CreateXCFrameworkStructure_RemovesPreviousBuild()
        {
            var dir = CreateTempDir();
            try
            {
                var xcfwPath = Path.Combine(dir, "TestSwiftBindings.xcframework");
                var fwDir = Path.Combine(xcfwPath, "ios-arm64-simulator", "TestSwiftBindings.framework");

                // Create a stale file from "previous build"
                Directory.CreateDirectory(xcfwPath);
                File.WriteAllText(Path.Combine(xcfwPath, "stale.txt"), "old");

                SwiftWrapperCompiler.CreateXCFrameworkStructure(xcfwPath, fwDir, "TestSwiftBindings", "15.0");

                Assert.False(File.Exists(Path.Combine(xcfwPath, "stale.txt")));
                Assert.True(File.Exists(Path.Combine(xcfwPath, "Info.plist")));
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"swc_struct_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region D. SDK Resolution Tests

    public class SwiftWrapperSdkTests
    {
        [Fact]
        public void ResolveSdkPath_Success_ReturnsPath()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneSimulator.platform/Developer/SDKs/iPhoneSimulator.sdk");

            var result = SwiftWrapperCompiler.ResolveSdkPath(runner);
            Assert.Contains("iPhoneSimulator", result);
        }

        [Fact]
        public void ResolveSdkPath_Failure_Throws()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 1, "", "xcode-select: error");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                SwiftWrapperCompiler.ResolveSdkPath(runner));
            Assert.Contains("platform SDK", ex.Message);
        }

        [Fact]
        public void ResolveSdkPath_EmptyOutput_Throws()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                SwiftWrapperCompiler.ResolveSdkPath(runner));
            Assert.Contains("platform SDK", ex.Message);
        }
    }

    #endregion

    #region E. Compiler Invocation Tests

    public class SwiftWrapperCompilerInvocationTests
    {
        [Fact]
        public void InvokeSwiftCompiler_ConstructsCorrectArgs()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift", "/tmp/b.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                runner, NullLogger.Instance);

            Assert.Single(runner.Invocations);
            var (cmd, args) = runner.Invocations[0];
            Assert.Equal("xcrun", cmd);
            Assert.Contains("swiftc", args);
            Assert.Contains("-emit-library", args);
            Assert.Contains("arm64-apple-ios15.0-simulator", args);
            Assert.Contains("-F \"/fw/search\"", args);
            Assert.Contains("-module-name TestSwiftBindings", args);
            Assert.Contains("@rpath/TestSwiftBindings.framework/TestSwiftBindings", args);
            Assert.Contains("/tmp/a.swift", args);
            Assert.Contains("/tmp/b.swift", args);
        }

        [Fact]
        public void InvokeSwiftCompiler_FailureThrowsWithStderr()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 1, "", "error: cannot find module 'SomeModule'");

            var files = new List<string> { "/tmp/a.swift" };
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance));
            Assert.Contains("compilation failed", ex.Message);
            Assert.Contains("cannot find module", ex.Message);
        }

        [Fact]
        public void InvokeSwiftCompiler_NullAdditionalPaths_NoExtraFFlag()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                runner, NullLogger.Instance, additionalFrameworkSearchPaths: null);

            Assert.Single(runner.Invocations);
            var (_, args) = runner.Invocations[0];
            // Should have exactly one -F flag
            var fFlagCount = args.Split("-F ").Length - 1;
            Assert.Equal(1, fFlagCount);
        }

        [Fact]
        public void InvokeSwiftCompiler_OneAdditionalPath_OneExtraFFlag()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                runner, NullLogger.Instance,
                additionalFrameworkSearchPaths: new[] { "/dep1/ios-arm64-simulator" });

            Assert.Single(runner.Invocations);
            var (_, args) = runner.Invocations[0];
            Assert.Contains("-F \"/dep1/ios-arm64-simulator\"", args);
            var fFlagCount = args.Split("-F ").Length - 1;
            Assert.Equal(2, fFlagCount);
        }

        [Fact]
        public void InvokeSwiftCompiler_TwoAdditionalPaths_TwoExtraFFlags()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                runner, NullLogger.Instance,
                additionalFrameworkSearchPaths: new[] { "/dep1/slice", "/dep2/slice" });

            Assert.Single(runner.Invocations);
            var (_, args) = runner.Invocations[0];
            Assert.Contains("-F \"/dep1/slice\"", args);
            Assert.Contains("-F \"/dep2/slice\"", args);
            var fFlagCount = args.Split("-F ").Length - 1;
            Assert.Equal(3, fFlagCount);
        }

        [Fact]
        public void InvokeSwiftCompiler_AdditionalPaths_CorrectPosition()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/primary",
                runner, NullLogger.Instance,
                additionalFrameworkSearchPaths: new[] { "/fw/dep" });

            var (_, args) = runner.Invocations[0];
            // Primary -F should come before additional -F
            var primaryIdx = args.IndexOf("-F \"/fw/primary\"");
            var depIdx = args.IndexOf("-F \"/fw/dep\"");
            Assert.True(primaryIdx < depIdx, "Primary -F should precede dependency -F");
            // Both should come before -module-name
            var moduleIdx = args.IndexOf("-module-name");
            Assert.True(depIdx < moduleIdx, "Dependency -F should precede -module-name");
        }

        [Fact]
        public void InvokeSwiftCompiler_PathsWithSpaces_ProperlyQuoted()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                runner, NullLogger.Instance,
                additionalFrameworkSearchPaths: new[] { "/path with spaces/dep" });

            var (_, args) = runner.Invocations[0];
            Assert.Contains("-F \"/path with spaces/dep\"", args);
        }

        [Fact]
        public void InvokeSwiftCompiler_MacCatalyst_AddsIOSSupportFrameworkPath()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var sdkPath = Path.Combine(Path.GetTempPath(), $"maccatalyst_sdk_{Guid.NewGuid():N}");
            var iOSSupportFrameworksPath = Path.Combine(
                sdkPath, "System", "iOSSupport", "System", "Library", "Frameworks");
            Directory.CreateDirectory(iOSSupportFrameworksPath);
            try
            {
                var files = new List<string> { "/tmp/a.swift" };
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-macabi", sdkPath, "/fw/search",
                    runner, NullLogger.Instance);

                var (_, args) = runner.Invocations[0];
                Assert.Contains($"-F \"{iOSSupportFrameworksPath}\"", args);
            }
            finally
            {
                Directory.Delete(sdkPath, recursive: true);
            }
        }

        [Fact]
        public void InvokeSwiftCompiler_LongStderr_TruncatesAt4000Chars()
        {
            var runner = new MockCommandRunner();
            // Single huge line that is neither an error-line nor a linker-line — exercises the
            // raw-preview length cap. The diagnostic-line filter (preferred path) only kicks in
            // when stderr contains ` error:` / `Undefined symbols` / etc.
            var longError = new string('x', 5000);
            runner.SetResponse("swiftc", 1, "", longError);

            var files = new List<string> { "/tmp/a.swift" };
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance));
            // Raw preview caps at 4000 + "..." when no diagnostic lines are filterable
            Assert.Contains("...", ex.Message);
            // The error preview is bounded so the message can't swell with the full stderr
            Assert.True(ex.Message.Length < longError.Length);
        }

        [Fact]
        public void InvokeSwiftCompiler_StderrWithErrorLines_FiltersToErrorPreview()
        {
            var runner = new MockCommandRunner();
            // Real-world shape: many warning lines, then the actual error buried near the end.
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 50; i++)
                sb.AppendLine($"warning: nearly matches defaulted requirement #{i}");
            sb.AppendLine("/tmp/Foo.swift:42:5: error: cannot convert value of type 'Int' to 'String'");
            for (int i = 0; i < 50; i++)
                sb.AppendLine($"note: requirement from here #{i}");
            runner.SetResponse("swiftc", 1, "", sb.ToString());

            var files = new List<string> { "/tmp/a.swift" };
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance));
            // Preview must surface the real error — not bury it behind warnings.
            Assert.Contains("cannot convert value of type 'Int' to 'String'", ex.Message);
            Assert.DoesNotContain("nearly matches defaulted requirement #0", ex.Message);
        }

        [Fact]
        public void InvokeSwiftCompiler_StderrWithLinkerErrors_PreviewSurfacesLinkerLines()
        {
            var runner = new MockCommandRunner();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 30; i++)
                sb.AppendLine($"warning: existential 'any P' will require explicit 'any' #{i}");
            sb.AppendLine("Undefined symbols for architecture arm64:");
            sb.AppendLine("  \"_OBJC_CLASS_$_GULAppEnvironmentUtil\", referenced from:");
            sb.AppendLine("ld: symbol(s) not found for architecture arm64");
            sb.AppendLine("clang: error: linker command failed with exit code 1");
            runner.SetResponse("swiftc", 1, "", sb.ToString());

            var files = new List<string> { "/tmp/a.swift" };
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance));
            // Linker-failure shape (warnings + Undefined symbols + ld error) must surface the
            // linker lines, not the warning preamble.
            Assert.Contains("Undefined symbols for architecture arm64", ex.Message);
            Assert.Contains("clang: error", ex.Message);
        }

        [Fact]
        public void InvokeSwiftCompiler_ShortStderr_NoTruncation()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 1, "", "short error message");

            var files = new List<string> { "/tmp/a.swift" };
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance));
            Assert.Contains("short error message", ex.Message);
            Assert.DoesNotContain("...", ex.Message);
        }

        [Fact]
        public void InvokeSwiftCompiler_TransitiveDepFrameworks_EmittedAsXlinkerFlags()
        {
            // The wrapper `import` auto-links the bound module but not its transitive ObjC-only deps.
            // Each .framework with a Mach-O binary in additionalFrameworkSearchPaths must be
            // explicitly emitted as `-Xlinker -framework -Xlinker` so the linker resolves
            // OBJC_CLASS_$ / C / C++ symbols.
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var root = Path.Combine(Path.GetTempPath(), $"swc_link_test_{Guid.NewGuid():N}");
            var fwSearchPath = Path.Combine(root, "deps");
            var absl = Path.Combine(fwSearchPath, "absl.framework");
            var firebaseCoreInternal = Path.Combine(fwSearchPath, "CloudPlatformSdkCoreInternal.framework");
            var headerOnly = Path.Combine(fwSearchPath, "HeaderOnly.framework");
            Directory.CreateDirectory(absl);
            Directory.CreateDirectory(firebaseCoreInternal);
            Directory.CreateDirectory(headerOnly);
            // Stub Mach-O binaries (file presence is all the linker check verifies)
            File.WriteAllBytes(Path.Combine(absl, "absl"), new byte[] { 0xCF, 0xFA, 0xED, 0xFE });
            File.WriteAllBytes(Path.Combine(firebaseCoreInternal, "CloudPlatformSdkCoreInternal"),
                new byte[] { 0xCF, 0xFA, 0xED, 0xFE });
            // HeaderOnly.framework has NO binary -> must NOT be linked.

            try
            {
                var files = new List<string> { "/tmp/a.swift" };
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance,
                    additionalFrameworkSearchPaths: new[] { fwSearchPath });

                var (_, args) = runner.Invocations[0];
                Assert.Contains("-Xlinker -framework -Xlinker absl", args);
                Assert.Contains("-Xlinker -framework -Xlinker CloudPlatformSdkCoreInternal", args);
                Assert.DoesNotContain("-Xlinker -framework -Xlinker HeaderOnly", args);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void InvokeSwiftCompiler_TransitiveDepFrameworks_DedupesOriginalModuleName()
        {
            // The bound module is auto-linked by the `import` in the wrapper and is explicitly
            // re-linked via thunkLinkerFlags when thunk .o files are present. We must NOT emit
            // a duplicate -Xlinker -framework -Xlinker for it via the transitive scan.
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var root = Path.Combine(Path.GetTempPath(), $"swc_link_test_{Guid.NewGuid():N}");
            var fwSearchPath = Path.Combine(root, "deps");
            var cloudPlatformSdkFirestore = Path.Combine(fwSearchPath, "CloudPlatformSdkFirestore.framework");
            Directory.CreateDirectory(cloudPlatformSdkFirestore);
            File.WriteAllBytes(Path.Combine(cloudPlatformSdkFirestore, "CloudPlatformSdkFirestore"),
                new byte[] { 0xCF, 0xFA, 0xED, 0xFE });

            try
            {
                var files = new List<string> { "/tmp/a.swift" };
                var thunkObj = Path.Combine(root, "thunk.o");
                File.WriteAllBytes(thunkObj, new byte[] { 0xCF, 0xFA, 0xED, 0xFE });
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance,
                    additionalFrameworkSearchPaths: new[] { fwSearchPath },
                    thunkObjectFiles: new[] { thunkObj },
                    originalModuleName: "CloudPlatformSdkFirestore");

                var (_, args) = runner.Invocations[0];
                // The bound module must appear exactly once (in thunkLinkerFlags), not twice.
                var occurrences = (args.Length - args.Replace(
                    "-Xlinker -framework -Xlinker CloudPlatformSdkFirestore", "").Length)
                    / "-Xlinker -framework -Xlinker CloudPlatformSdkFirestore".Length;
                Assert.Equal(1, occurrences);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void InvokeSwiftCompiler_TransitiveDepFrameworks_DedupesSameNameAcrossPaths()
        {
            // Two search paths that each contain absl.framework (with a binary) must emit
            // -Xlinker -framework -Xlinker absl exactly once.
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var root = Path.Combine(Path.GetTempPath(), $"swc_link_test_{Guid.NewGuid():N}");
            var pathA = Path.Combine(root, "depA");
            var pathB = Path.Combine(root, "depB");
            var abslA = Path.Combine(pathA, "absl.framework");
            var abslB = Path.Combine(pathB, "absl.framework");
            Directory.CreateDirectory(abslA);
            Directory.CreateDirectory(abslB);
            File.WriteAllBytes(Path.Combine(abslA, "absl"), new byte[] { 0xCF, 0xFA, 0xED, 0xFE });
            File.WriteAllBytes(Path.Combine(abslB, "absl"), new byte[] { 0xCF, 0xFA, 0xED, 0xFE });

            try
            {
                var files = new List<string> { "/tmp/a.swift" };
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance,
                    additionalFrameworkSearchPaths: new[] { pathA, pathB });

                var (_, args) = runner.Invocations[0];
                var occurrences = (args.Length - args.Replace(
                    "-Xlinker -framework -Xlinker absl", "").Length)
                    / "-Xlinker -framework -Xlinker absl".Length;
                Assert.Equal(1, occurrences);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void InvokeSwiftCompiler_TransitiveScanPaths_ExcludesModuleResolutionOnlyPath()
        {
            // The XCTest platform `Developer/Library/Frameworks` path is folded into the -F set
            // (additionalFrameworkSearchPaths) so `import XCTest` resolves, but is deliberately
            // kept OUT of the raw scan set (transitiveScanSearchPaths). A path present only for
            // module resolution must get a -F but must NOT contribute a transitive -framework:
            // a binding library has no business pulling in test-host frameworks (XCTest, Testing).
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var root = Path.Combine(Path.GetTempPath(), $"swc_link_test_{Guid.NewGuid():N}");
            var userDeps = Path.Combine(root, "userdeps");
            var platformDir = Path.Combine(root, "platform");
            var realDep = Path.Combine(userDeps, "RealDep.framework");
            var xctest = Path.Combine(platformDir, "XCTest.framework");
            Directory.CreateDirectory(realDep);
            Directory.CreateDirectory(xctest);
            File.WriteAllBytes(Path.Combine(realDep, "RealDep"), new byte[] { 0xCF, 0xFA, 0xED, 0xFE });
            File.WriteAllBytes(Path.Combine(xctest, "XCTest"), new byte[] { 0xCF, 0xFA, 0xED, 0xFE });

            try
            {
                var files = new List<string> { "/tmp/a.swift" };
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance,
                    // Effective set (-F): both the user dep AND the platform path.
                    additionalFrameworkSearchPaths: new[] { userDeps, platformDir },
                    // Raw scan set (-framework): user dep ONLY — platform path excluded.
                    transitiveScanSearchPaths: new[] { userDeps });

                var (_, args) = runner.Invocations[0];
                // Platform path still drives module resolution via -F.
                Assert.Contains($"-F \"{platformDir}\"", args);
                // Genuine user dep is linked.
                Assert.Contains("-Xlinker -framework -Xlinker RealDep", args);
                // Module-resolution-only platform framework is NOT linked.
                Assert.DoesNotContain("-Xlinker -framework -Xlinker XCTest", args);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void InvokeSwiftCompiler_NullTransitiveScanPaths_FallsBackToAdditionalPaths()
        {
            // Backward compatibility: when transitiveScanSearchPaths is null, the scan falls back
            // to additionalFrameworkSearchPaths so existing callers (and tests) are unaffected.
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var root = Path.Combine(Path.GetTempPath(), $"swc_link_test_{Guid.NewGuid():N}");
            var fwSearchPath = Path.Combine(root, "deps");
            var dep = Path.Combine(fwSearchPath, "FallbackDep.framework");
            Directory.CreateDirectory(dep);
            File.WriteAllBytes(Path.Combine(dep, "FallbackDep"), new byte[] { 0xCF, 0xFA, 0xED, 0xFE });

            try
            {
                var files = new List<string> { "/tmp/a.swift" };
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance,
                    additionalFrameworkSearchPaths: new[] { fwSearchPath });

                var (_, args) = runner.Invocations[0];
                Assert.Contains("-Xlinker -framework -Xlinker FallbackDep", args);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void InvokeSwiftCompiler_IncludesStrictConcurrencyMinimal()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                runner, NullLogger.Instance);

            Assert.Single(runner.Invocations);
            var (_, args) = runner.Invocations[0];
            Assert.Contains("-strict-concurrency=minimal", args);
        }

        [Fact]
        public void InvokeSwiftCompiler_ShadowPath_PrependsBeforeRealF()
        {
            // Shadow precompile -F must come BEFORE the real -F so swiftc picks the
            // binary .swiftmodule (which dodges the collision) over the textual interface
            // that would re-trigger swiftinterface typecheck against the colliding name.
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/real",
                runner, NullLogger.Instance,
                precompiledShadowFrameworkPaths: new[] { "/tmp/shadow1" });

            var (_, args) = runner.Invocations[0];
            var shadowIdx = args.IndexOf("-F \"/tmp/shadow1\"");
            var realIdx = args.IndexOf("-F \"/fw/real\"");
            Assert.True(shadowIdx >= 0, "shadow -F must be present");
            Assert.True(realIdx >= 0, "real -F must be present");
            Assert.True(shadowIdx < realIdx, "shadow -F must precede real -F");
        }

        [Fact]
        public void InvokeSwiftCompiler_MultipleShadowPaths_AllPrependedInOrder()
        {
            // Multiple concurrent shadow paths (bound-module collision + dep-module collision
            // + XCTest precompile) must ALL appear before the real -F, and in input order.
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/real",
                runner, NullLogger.Instance,
                precompiledShadowFrameworkPaths: new[] { "/tmp/shadow1", "/tmp/shadow2", "/tmp/shadow3" });

            var (_, args) = runner.Invocations[0];
            var s1 = args.IndexOf("-F \"/tmp/shadow1\"");
            var s2 = args.IndexOf("-F \"/tmp/shadow2\"");
            var s3 = args.IndexOf("-F \"/tmp/shadow3\"");
            var real = args.IndexOf("-F \"/fw/real\"");
            Assert.True(s1 >= 0 && s2 >= 0 && s3 >= 0 && real >= 0,
                "all four -F flags must be present");
            Assert.True(s1 < s2, "shadow1 must precede shadow2 (input order preserved)");
            Assert.True(s2 < s3, "shadow2 must precede shadow3 (input order preserved)");
            Assert.True(s3 < real, "all shadows must precede the real -F");
        }

        [Fact]
        public void InvokeSwiftCompiler_ShadowAndAdditionalPaths_ShadowFirstRealMiddleAdditionalLast()
        {
            // Full sandwich: shadow -F first, real -F middle, additional dep -F last.
            // Validates that the dep-collision precompile and the framework-dependency
            // additionalFrameworkSearchPaths don't fight each other for position.
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/real",
                runner, NullLogger.Instance,
                additionalFrameworkSearchPaths: new[] { "/dep/slice" },
                precompiledShadowFrameworkPaths: new[] { "/tmp/shadow1" });

            var (_, args) = runner.Invocations[0];
            var shadowIdx = args.IndexOf("-F \"/tmp/shadow1\"");
            var realIdx = args.IndexOf("-F \"/fw/real\"");
            var depIdx = args.IndexOf("-F \"/dep/slice\"");
            Assert.True(shadowIdx >= 0 && realIdx >= 0 && depIdx >= 0,
                "all three -F flags must be present");
            Assert.True(shadowIdx < realIdx, "shadow -F must precede real -F");
            Assert.True(realIdx < depIdx, "real -F must precede dependency -F");
        }

        [Fact]
        public void InvokeSwiftCompiler_EmptyShadowPathInList_Skipped()
        {
            // Defensive: a null/empty entry inside the shadow list (e.g., PrecompileCollidingModule
            // returned null for one of several deps) must not emit `-F ""`.
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/real",
                runner, NullLogger.Instance,
                precompiledShadowFrameworkPaths: new[] { "/tmp/shadow1", "", "/tmp/shadow2" });

            var (_, args) = runner.Invocations[0];
            Assert.DoesNotContain("-F \"\"", args);
            Assert.Contains("-F \"/tmp/shadow1\"", args);
            Assert.Contains("-F \"/tmp/shadow2\"", args);
        }

        [Fact]
        public void InvokeSwiftCompiler_ForceLoadBinary_EmitsForceLoadLinkerFlag()
        {
            // Gap 2: a static-archive primary passed via forceLoadBinaries must be
            // force-loaded so the wrapper carries every ObjC class, not just lazily
            // referenced members. The binary must exist on disk (the guard skips ghosts).
            var archive = Path.Combine(Path.GetTempPath(), $"forceload_{Guid.NewGuid():N}.a");
            File.WriteAllBytes(archive, new byte[] { 0x21, 0x3C, 0x61, 0x72, 0x63, 0x68, 0x3E, 0x0A });
            try
            {
                var runner = new MockCommandRunner();
                runner.SetResponse("swiftc", 0, "");

                var files = new List<string> { "/tmp/a.swift" };
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance,
                    forceLoadBinaries: new[] { archive });

                var (_, args) = runner.Invocations[0];
                Assert.Contains($"-Xlinker -force_load -Xlinker \"{archive}\"", args);
            }
            finally
            {
                File.Delete(archive);
            }
        }

        [Fact]
        public void InvokeSwiftCompiler_NoForceLoadBinaries_NoForceLoadFlag()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                runner, NullLogger.Instance,
                forceLoadBinaries: null);

            var (_, args) = runner.Invocations[0];
            Assert.DoesNotContain("-force_load", args);
        }

        [Fact]
        public void InvokeSwiftCompiler_ForceLoadNonexistentBinary_Skipped()
        {
            // A force-load entry that isn't on disk must NOT reach the linker — force-loading
            // a missing path is a hard ld error. The File.Exists guard drops it silently.
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var ghost = Path.Combine(Path.GetTempPath(), $"ghost_{Guid.NewGuid():N}.a");
            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                runner, NullLogger.Instance,
                forceLoadBinaries: new[] { ghost });

            var (_, args) = runner.Invocations[0];
            Assert.DoesNotContain("-force_load", args);
        }

        [Fact]
        public void InvokeSwiftCompiler_LinkFramework_EmitsFrameworkLinkerFlag()
        {
            // Author-declared system frameworks (for a force-loaded static archive whose
            // autolink hints are absent) must reach the linker as -Xlinker -framework -Xlinker.
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                runner, NullLogger.Instance,
                linkFrameworks: new[] { "CoreVideo", "Metal" });

            var (_, args) = runner.Invocations[0];
            Assert.Contains("-Xlinker -framework -Xlinker CoreVideo", args);
            Assert.Contains("-Xlinker -framework -Xlinker Metal", args);
        }

        [Fact]
        public void InvokeSwiftCompiler_LinkLibrary_EmitsLowerLFlag()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                runner, NullLogger.Instance,
                linkLibraries: new[] { "c++" });

            var (_, args) = runner.Invocations[0];
            Assert.Contains("-lc++", args);
        }

        [Fact]
        public void InvokeSwiftCompiler_LinkLibrary_NormalizesDashLPrefix()
        {
            // An author may write either "c++" or "-lc++"; both must normalize to a single
            // -lc++ token (never -l-lc++).
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                runner, NullLogger.Instance,
                linkLibraries: new[] { "-lc++" });

            var (_, args) = runner.Invocations[0];
            Assert.Contains("-lc++", args);
            Assert.DoesNotContain("-l-lc++", args);
        }

        [Fact]
        public void InvokeSwiftCompiler_LinkFramework_DedupesDuplicateEntries()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                runner, NullLogger.Instance,
                linkFrameworks: new[] { "Metal", "Metal" });

            var (_, args) = runner.Invocations[0];
            var needle = "-Xlinker -framework -Xlinker Metal";
            var occurrences = (args.Length - args.Replace(needle, "").Length) / needle.Length;
            Assert.Equal(1, occurrences);
        }

        [Fact]
        public void InvokeSwiftCompiler_LinkFramework_EmptyAndWhitespaceEntriesSkipped()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                runner, NullLogger.Instance,
                linkFrameworks: new[] { "CoreVideo", "", "  " });

            var (_, args) = runner.Invocations[0];
            Assert.Contains("-Xlinker -framework -Xlinker CoreVideo", args);
            // No bare "-framework " with an empty name slipped through.
            Assert.DoesNotContain("-Xlinker -framework -Xlinker  ", args);
        }

        [Fact]
        public void InvokeSwiftCompiler_LinkFramework_DedupesAgainstTransitiveScan()
        {
            // If a system framework is BOTH auto-linked via the transitive dep scan and
            // declared via --link-framework, it must appear exactly once. The explicit
            // declaration shares the seenLinkedFrameworks set with the transitive scan.
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var root = Path.Combine(Path.GetTempPath(), $"swc_link_dedup_{Guid.NewGuid():N}");
            var fwSearchPath = Path.Combine(root, "deps");
            var depFw = Path.Combine(fwSearchPath, "Metal.framework");
            Directory.CreateDirectory(depFw);
            File.WriteAllBytes(Path.Combine(depFw, "Metal"), new byte[] { 0xCF, 0xFA, 0xED, 0xFE });
            try
            {
                var files = new List<string> { "/tmp/a.swift" };
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance,
                    additionalFrameworkSearchPaths: new[] { fwSearchPath },
                    linkFrameworks: new[] { "Metal" });

                var (_, args) = runner.Invocations[0];
                var needle = "-Xlinker -framework -Xlinker Metal";
                var occurrences = (args.Length - args.Replace(needle, "").Length) / needle.Length;
                Assert.Equal(1, occurrences);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void InvokeSwiftCompiler_NoLinkDependencies_NoExtraFlags()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            var files = new List<string> { "/tmp/a.swift" };
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                files, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                runner, NullLogger.Instance,
                linkFrameworks: null, linkLibraries: null);

            var (_, args) = runner.Invocations[0];
            Assert.DoesNotContain("-Xlinker -framework -Xlinker CoreVideo", args);
            Assert.DoesNotContain("-lc++", args);
        }
    }

    #endregion

    #region E2. System-Link Dependency Hint Tests

    public class SwiftWrapperSystemLinkHintTests
    {
        private const string UndefinedHeader = "Undefined symbols for architecture arm64:\n";

        [Fact]
        public void BuildSystemLinkDependencyHint_CoreVideoSymbol_NamesCoreVideo()
        {
            var stderr = UndefinedHeader +
                "  \"_CVPixelBufferGetWidth\", referenced from:\n" +
                "      _foo in libBar.a(baz.o)\n";
            var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(stderr);
            Assert.Contains("--link-framework CoreVideo", hint);
            Assert.Contains("<SwiftLinkFramework Include=\"CoreVideo\" />", hint);
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_MetalSymbol_NamesMetal()
        {
            var stderr = UndefinedHeader +
                "  \"_MTLCreateSystemDefaultDevice\", referenced from:\n";
            var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(stderr);
            Assert.Contains("--link-framework Metal", hint);
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_LibcxxSymbol_NamesCxxLibrary()
        {
            var stderr = UndefinedHeader +
                "  \"__ZNSt3__112basic_stringIcNS_11char_traitsIcEEEC1Ev\", referenced from:\n";
            var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(stderr);
            Assert.Contains("--link-library c++", hint);
            Assert.Contains("<SwiftLinkLibrary Include=\"c++\" />", hint);
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_NotALinkFailure_ReturnsEmpty()
        {
            // A missing-module compile error is not an undefined-symbol link failure — the
            // hint must stay silent rather than claim a misleading system-link cause.
            var stderr = "/tmp/Foo.swift:1:8: error: no such module 'Bar'\n";
            var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(stderr);
            Assert.Equal(string.Empty, hint);
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_UnrecognizedSymbol_ReturnsEmpty()
        {
            // Undefined symbols, but none map to a known system framework/library — don't guess.
            var stderr = UndefinedHeader +
                "  \"_SomeRandomUserSymbol\", referenced from:\n";
            var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(stderr);
            Assert.Equal(string.Empty, hint);
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_AlreadyDeclaredFramework_Suppressed()
        {
            // CoreVideo already declared by the author -> the only undefined symbol maps to an
            // already-declared framework, so there's nothing actionable left to suggest.
            var stderr = UndefinedHeader +
                "  \"_CVPixelBufferGetWidth\", referenced from:\n";
            var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(
                stderr, alreadyDeclaredFrameworks: new[] { "CoreVideo" });
            Assert.Equal(string.Empty, hint);
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_AlreadyDeclaredLibrary_Suppressed()
        {
            // Author already passed -lc++ (in either "c++" or "-lc++" spelling): suppress.
            var stderr = UndefinedHeader +
                "  \"__ZNSt3__19to_stringEi\", referenced from:\n";
            var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(
                stderr, alreadyDeclaredLibraries: new[] { "-lc++" });
            Assert.Equal(string.Empty, hint);
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_QuoteAnchored_NoMidTextMatch()
        {
            // The "_gl needle is quote-anchored: a user symbol that merely contains "gl" in the
            // middle (no leading quote+_gl) must NOT be misattributed to OpenGLES.
            var stderr = UndefinedHeader +
                "  \"_myConfigleSetting\", referenced from:\n";
            var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(stderr);
            Assert.DoesNotContain("OpenGLES", hint);
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_StderrGlStem_RequiresCamelCaseForOpenGLES()
        {
            // The linker prints undefined symbols quoted as `"_name", referenced from:`. A quoted
            // `"_global_ctors"` / `"_glue_init"` begins with quote+`_gl` but is NOT an OpenGL ES
            // entry point, so the stderr path (not just the archive scan) must not misattribute it
            // to OpenGLES; a real `"_glBindTexture"` still must be named.
            var nonGl = UndefinedHeader +
                "  \"_global_ctors\", referenced from:\n" +
                "  \"_glue_init\", referenced from:\n";
            Assert.DoesNotContain("OpenGLES", SwiftWrapperCompiler.BuildSystemLinkDependencyHint(nonGl));

            var realGl = UndefinedHeader + "  \"_glBindTexture\", referenced from:\n";
            Assert.Contains(
                "--link-framework OpenGLES", SwiftWrapperCompiler.BuildSystemLinkDependencyHint(realGl));
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_MlVisionLibShapedStderr_NamesAllRequiredDeps()
        {
            // Regression guard for issue #41: the thin Swift wrapper force-loads a C/C++ engine archive
            // that carries no autolink hints and references Accelerate, CoreVideo, Metal, OpenGLES,
            // and libc++. The linker prints these as a single quoted undefined-symbol wall; the
            // Source-1 stderr path (post-extract/match unification) must name every one of them.
            // Symbols mirror the shapes the real engine archive exposes.
            var stderr = UndefinedHeader +
                "  \"_CVPixelBufferGetWidth\", referenced from:\n" +
                "  \"_CVPixelBufferLockBaseAddress\", referenced from:\n" +
                "  \"_MTLCreateSystemDefaultDevice\", referenced from:\n" +
                "  \"_OBJC_CLASS_$_MTLCompileOptions\", referenced from:\n" +
                "  \"_glBindTexture\", referenced from:\n" +
                "  \"_OBJC_CLASS_$_EAGLContext\", referenced from:\n" +
                "  \"_vImageConvert_ARGB8888toPlanar8\", referenced from:\n" +
                "  \"_vDSP_vsmul\", referenced from:\n" +
                "  \"_cblas_sgemm\", referenced from:\n" +
                "  \"_BNNSFilterApply\", referenced from:\n" +
                "  \"__ZNSt3__112basic_stringIcNS_11char_traitsIcEEEC1Ev\", referenced from:\n";
            var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(stderr);
            Assert.Contains("--link-framework Accelerate", hint);
            Assert.Contains("--link-framework CoreVideo", hint);
            Assert.Contains("--link-framework Metal", hint);
            Assert.Contains("--link-framework OpenGLES", hint);
            Assert.Contains("--link-library c++", hint);
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_LowercaseUndefinedHeader_StillNamesFramework()
        {
            // The entry gate matches "undefined symbol" case-insensitively, so a link failure whose
            // header casing/wording drifts (lowercased here) still triggers the hint when the
            // linker's quoted symbols are present. The case-sensitive gate would have returned empty.
            var stderr = "undefined symbols for architecture arm64:\n" +
                "  \"_CVPixelBufferGetWidth\", referenced from:\n";
            var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(stderr);
            Assert.Contains("--link-framework CoreVideo", hint);
        }

        // --- Source 2: scanning the force-loaded archive's own `nm -u` symbols completes the
        //     framework set even when the linker printed only part of its undefined-symbol list. ---

        // Creates a real (stub) archive file on disk (ScanUndefinedSymbols skips paths that don't
        // exist) wired to a MockCommandRunner that returns the given `nm -u` output, runs the body,
        // and cleans up.
        private static void WithTempArchive(string nmUOutput, Action<string, MockCommandRunner> body)
        {
            var dir = Path.Combine(Path.GetTempPath(), "nmscan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "libEngine.a");
            File.WriteAllText(path, "stub");
            var runner = new MockCommandRunner();
            runner.SetResponse("nm -u", 0, nmUOutput);
            try { body(path, runner); }
            finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_ArchiveScan_CompletesFrameworkLinkerOmitted()
        {
            // The linker printed only a CoreVideo undefined symbol, but the force-loaded archive's
            // own nm -u also references an Accelerate symbol. Scanning the archive completes the set
            // so BOTH frameworks are named in one pass — no multi-round whack-a-mole.
            var stderr = UndefinedHeader + "  \"_CVPixelBufferGetWidth\", referenced from:\n";
            var nmU = "libEngine.a:\n_CVPixelBufferGetWidth\n_vImageConvert_AnyToAny\n";
            WithTempArchive(nmU, (archivePath, runner) =>
            {
                var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(
                    stderr, forceLoadedArchives: new[] { archivePath },
                    commandRunner: runner, logger: NullLogger.Instance);
                Assert.Contains("--link-framework CoreVideo", hint);
                Assert.Contains("--link-framework Accelerate", hint); // only discoverable via the scan
            });
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_ArchiveScan_AddsLibcxxFromStdSymbols()
        {
            // libc++ surfaced from the archive's std:: (__ZNSt) symbols even though the linker's
            // printed list didn't include any C++ symbol.
            var stderr = UndefinedHeader + "  \"_MTLCreateSystemDefaultDevice\", referenced from:\n";
            var nmU = "_MTLCreateSystemDefaultDevice\n__ZNSt3__112basic_stringIcNS_11char_traitsIcEEEC1Ev\n";
            WithTempArchive(nmU, (archivePath, runner) =>
            {
                var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(
                    stderr, forceLoadedArchives: new[] { archivePath },
                    commandRunner: runner, logger: NullLogger.Instance);
                Assert.Contains("--link-library c++", hint);
            });
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_ArchiveScan_RespectsDeclaredFramework()
        {
            // Accelerate already declared: even though the archive references a _vImage symbol, the
            // scan must not re-suggest it.
            var stderr = UndefinedHeader + "  \"_CVPixelBufferGetWidth\", referenced from:\n";
            var nmU = "_vImageConvert_AnyToAny\n";
            WithTempArchive(nmU, (archivePath, runner) =>
            {
                var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(
                    stderr, alreadyDeclaredFrameworks: new[] { "Accelerate" },
                    forceLoadedArchives: new[] { archivePath },
                    commandRunner: runner, logger: NullLogger.Instance);
                Assert.DoesNotContain("Accelerate", hint);
                Assert.Contains("CoreVideo", hint);
            });
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_ArchiveScan_GateHoldsForNonLinkFailure()
        {
            // No "Undefined symbol" in stderr -> not a link failure. Even with an archive full of
            // matching symbols, the hint stays empty and nm is never run.
            var stderr = "/tmp/Foo.swift:1:8: error: no such module 'Bar'\n";
            var nmU = "_vImageConvert_AnyToAny\n_MTLCreateSystemDefaultDevice\n";
            WithTempArchive(nmU, (archivePath, runner) =>
            {
                var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(
                    stderr, forceLoadedArchives: new[] { archivePath },
                    commandRunner: runner, logger: NullLogger.Instance);
                Assert.Equal(string.Empty, hint);
                Assert.DoesNotContain(runner.Invocations, i => i.Command == "nm");
            });
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_ArchiveScan_GlStem_RequiresCamelCaseForOpenGLES()
        {
            // The archive scan sees bare symbols (no quote anchor). `_gl` is an OpenGL ES stem only
            // when followed by CamelCase (glBindTexture). An unrelated `_glue_init` / `_global_ctors`
            // must NOT be misattributed to OpenGLES, while a real `_glBindTexture` must be.
            var stderr = UndefinedHeader + "  \"_CVPixelBufferGetWidth\", referenced from:\n";

            var nmUNonGl = "_glue_init\n_global_ctors\n";
            WithTempArchive(nmUNonGl, (archivePath, runner) =>
            {
                var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(
                    stderr, forceLoadedArchives: new[] { archivePath },
                    commandRunner: runner, logger: NullLogger.Instance);
                Assert.DoesNotContain("OpenGLES", hint);
            });

            var nmUGl = "_glBindTexture\n";
            WithTempArchive(nmUGl, (archivePath, runner) =>
            {
                var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(
                    stderr, forceLoadedArchives: new[] { archivePath },
                    commandRunner: runner, logger: NullLogger.Instance);
                Assert.Contains("--link-framework OpenGLES", hint);
            });
        }

        [Fact]
        public void BuildSystemLinkDependencyHint_ArchiveScan_NmFailureFallsBackToStderr()
        {
            // nm exits non-zero on the archive (no evidence gathered): the stderr-derived result
            // must still stand rather than the whole hint collapsing.
            var stderr = UndefinedHeader + "  \"_CVPixelBufferGetWidth\", referenced from:\n";
            var dir = Path.Combine(Path.GetTempPath(), "nmscan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "libEngine.a");
            File.WriteAllText(path, "stub");
            var runner = new MockCommandRunner();
            runner.SetResponse("nm -u", 1, "", "nm: bad file");
            try
            {
                var hint = SwiftWrapperCompiler.BuildSystemLinkDependencyHint(
                    stderr, forceLoadedArchives: new[] { path },
                    commandRunner: runner, logger: NullLogger.Instance);
                Assert.Contains("--link-framework CoreVideo", hint);
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
        }
    }

    #endregion

    #region E3. Thunk-Only Clang Link Path (link-framework/library forwarding)

    /// <summary>
    /// When the generated wrapper has NO Swift source (every function was thunked), the link
    /// goes through <c>NativeThunkCompiler.LinkWithClang</c> with <c>clang -shared</c> instead of
    /// swiftc. The author-declared <c>--link-framework</c> / <c>--link-library</c> inputs must
    /// reach this path too — otherwise a thunk-only binding over a force-loaded static archive
    /// would silently drop its system-framework declarations and fail to resolve symbols at load.
    /// clang takes the flags directly (<c>-framework X</c> / <c>-l&lt;name&gt;</c>), without the
    /// swiftc <c>-Xlinker</c> indirection.
    /// </summary>
    public class ThunkOnlyClangLinkPathTests
    {
        private static readonly IReadOnlyList<string> OneObject = new[] { "/tmp/thunk.arm64.o" };

        [Fact]
        public void LinkWithClang_LinkFramework_EmitsFrameworkFlag()
        {
            var runner = new MockCommandRunner();
            NativeThunkCompiler.LinkWithClang(
                OneObject, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", runner, NullLogger.Instance,
                originalModuleName: null,
                linkFrameworks: new[] { "CoreVideo", "Metal" });

            var (cmd, args) = runner.Invocations[0];
            Assert.Equal("xcrun", cmd);
            Assert.Contains("clang -shared", args);
            Assert.Contains("-framework CoreVideo", args);
            Assert.Contains("-framework Metal", args);
        }

        [Fact]
        public void LinkWithClang_LinkLibrary_EmitsLowerLFlag()
        {
            var runner = new MockCommandRunner();
            NativeThunkCompiler.LinkWithClang(
                OneObject, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", runner, NullLogger.Instance,
                originalModuleName: null,
                linkLibraries: new[] { "c++" });

            var (_, args) = runner.Invocations[0];
            Assert.Contains("-lc++", args);
        }

        [Fact]
        public void LinkWithClang_LinkLibrary_NormalizesDashLPrefix()
        {
            // "c++" or "-lc++" both normalize to a single -lc++ (never -l-lc++), matching the
            // swiftc path's normalization.
            var runner = new MockCommandRunner();
            NativeThunkCompiler.LinkWithClang(
                OneObject, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", runner, NullLogger.Instance,
                originalModuleName: null,
                linkLibraries: new[] { "-lc++" });

            var (_, args) = runner.Invocations[0];
            Assert.Contains("-lc++", args);
            Assert.DoesNotContain("-l-lc++", args);
        }

        [Fact]
        public void LinkWithClang_LinkFramework_DedupesDuplicateEntries()
        {
            var runner = new MockCommandRunner();
            NativeThunkCompiler.LinkWithClang(
                OneObject, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", runner, NullLogger.Instance,
                originalModuleName: null,
                linkFrameworks: new[] { "Metal", "Metal" });

            var (_, args) = runner.Invocations[0];
            var needle = "-framework Metal";
            var occurrences = (args.Length - args.Replace(needle, "").Length) / needle.Length;
            Assert.Equal(1, occurrences);
        }

        [Fact]
        public void LinkWithClang_EmptyAndWhitespaceEntriesSkipped()
        {
            var runner = new MockCommandRunner();
            NativeThunkCompiler.LinkWithClang(
                OneObject, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", runner, NullLogger.Instance,
                originalModuleName: null,
                linkFrameworks: new[] { "CoreVideo", "", "  " });

            var (_, args) = runner.Invocations[0];
            Assert.Contains("-framework CoreVideo", args);
            // No bare "-framework " with an empty name slipped through.
            Assert.DoesNotContain("-framework  ", args);
        }

        [Fact]
        public void LinkWithClang_NoLinkDependencies_NoFrameworkOrLibraryFlags()
        {
            // The null/default case (the overwhelming majority of thunk-only wrappers) must be
            // byte-identical to before: with no declared frameworks/libraries and no original
            // module, no -framework or -l<name> flag is injected, but the link still happens.
            var runner = new MockCommandRunner();
            NativeThunkCompiler.LinkWithClang(
                OneObject, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", runner, NullLogger.Instance,
                originalModuleName: null);

            var (_, args) = runner.Invocations[0];
            Assert.Contains("clang -shared", args);
            Assert.Contains("/tmp/thunk.arm64.o", args);
            Assert.DoesNotContain("-framework ", args);
            Assert.DoesNotContain("-lc++", args);
        }

        [Fact]
        public void LinkWithClang_PrimaryModule_NotDuplicatedByDeclaredFramework()
        {
            // Parity with the swiftc path: the primary `-framework {originalModuleName}` shares the
            // same dedup set as author-declared --link-framework, so a declared framework matching
            // the module name is emitted exactly once (swiftc seeds seenLinkedFrameworks with
            // originalModuleName before its explicit loop).
            var runner = new MockCommandRunner();
            NativeThunkCompiler.LinkWithClang(
                OneObject, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", runner, NullLogger.Instance,
                originalModuleName: "OrigMod",
                linkFrameworks: new[] { "OrigMod", "CoreVideo" });

            var (_, args) = runner.Invocations[0];
            Assert.Contains("-framework CoreVideo", args);
            Assert.Equal(1, CountOccurrences(args, "-framework OrigMod"));
        }

        [Fact]
        public void LinkWithClang_TransitiveFrameworks_EmitFrameworkFlags()
        {
            // Sibling framework-dependency names (pre-scanned by the caller) become -framework
            // flags on the clang path, just as the swiftc path emits transitiveFrameworkLinkerFlags.
            var runner = new MockCommandRunner();
            NativeThunkCompiler.LinkWithClang(
                OneObject, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", runner, NullLogger.Instance,
                originalModuleName: null,
                transitiveFrameworks: new[] { "DepKit" });

            var (_, args) = runner.Invocations[0];
            Assert.Contains("-framework DepKit", args);
        }

        [Fact]
        public void LinkWithClang_TransitiveFramework_DedupesAcrossPrimaryAndDeclared()
        {
            // A transitive name that also matches the primary module or an author-declared
            // framework is emitted once across all three sources (one shared dedup set).
            var runner = new MockCommandRunner();
            NativeThunkCompiler.LinkWithClang(
                OneObject, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", runner, NullLogger.Instance,
                originalModuleName: "OrigMod",
                linkFrameworks: new[] { "DepKit", "CoreVideo" },
                transitiveFrameworks: new[] { "DepKit", "OrigMod" });

            var (_, args) = runner.Invocations[0];
            Assert.Equal(1, CountOccurrences(args, "-framework OrigMod"));
            Assert.Equal(1, CountOccurrences(args, "-framework DepKit"));
            Assert.Equal(1, CountOccurrences(args, "-framework CoreVideo"));
        }

        [Fact]
        public void LinkWithClang_TransitiveFrameworkSearchPaths_EmitMatchingDashFForEachDir()
        {
            // The companions named by transitiveFrameworks live in the dependency search-path dirs,
            // not the primary source slice. The clang path must emit a `-F {dir}` for each so the
            // linker can locate the `-framework {name}` it also emits — without it ld fails
            // "framework not found". This is the -F parity fix mirroring the swiftc path.
            var runner = new MockCommandRunner();
            NativeThunkCompiler.LinkWithClang(
                OneObject, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", runner, NullLogger.Instance,
                frameworkSearchPath: "/primary/slice",
                originalModuleName: null,
                transitiveFrameworks: new[] { "DepKit" },
                transitiveFrameworkSearchPaths: new[] { "/deps/one", "/deps/two" });

            var (_, args) = runner.Invocations[0];
            Assert.Contains("-F \"/primary/slice\"", args);
            Assert.Contains("-F \"/deps/one\"", args);
            Assert.Contains("-F \"/deps/two\"", args);
            Assert.Contains("-framework DepKit", args);
        }

        [Fact]
        public void LinkWithClang_TransitiveFrameworkSearchPaths_DedupAgainstPrimaryAndEachOther()
        {
            // A dep search-path dir equal to the primary frameworkSearchPath, or repeated, is
            // emitted as `-F` exactly once (one shared seenSearchPaths set) — no redundant flags.
            var runner = new MockCommandRunner();
            NativeThunkCompiler.LinkWithClang(
                OneObject, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", "/sdk/path", runner, NullLogger.Instance,
                frameworkSearchPath: "/shared/dir",
                originalModuleName: null,
                transitiveFrameworkSearchPaths: new[] { "/shared/dir", "/deps/one", "/deps/one", "", "  " });

            var (_, args) = runner.Invocations[0];
            Assert.Equal(1, CountOccurrences(args, "-F \"/shared/dir\""));
            Assert.Equal(1, CountOccurrences(args, "-F \"/deps/one\""));
        }

        [Fact]
        public void LinkWithClang_LinkFailure_WithHintDelegate_SurfacesActionableGuidance()
        {
            // When the clang link fails on an undeclared system-framework symbol, the failure
            // message carries the same actionable --link-framework / <SwiftLinkFramework> guidance
            // the swiftc path emits — not an opaque "Thunk linking failed" wall.
            var runner = new MockCommandRunner();
            runner.SetResponse("clang -shared", 1, "",
                "Undefined symbols for architecture arm64:\n" +
                "  \"_CVPixelBufferGetWidth\", referenced from:\n");

            var ex = Assert.Throws<System.InvalidOperationException>(() =>
                NativeThunkCompiler.LinkWithClang(
                    OneObject, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", runner, NullLogger.Instance,
                    originalModuleName: null,
                    buildLinkFailureHint: stderr =>
                        SwiftWrapperCompiler.BuildSystemLinkDependencyHint(stderr, null, null)));

            Assert.Contains("Thunk linking failed", ex.Message);
            Assert.Contains("--link-framework CoreVideo", ex.Message);
        }

        [Fact]
        public void LinkWithClang_LinkFailure_NoHintDelegate_PlainMessage()
        {
            // Without a hint provider the failure is reported plainly — no guidance fabricated.
            var runner = new MockCommandRunner();
            runner.SetResponse("clang -shared", 1, "",
                "Undefined symbols for architecture arm64:\n" +
                "  \"_CVPixelBufferGetWidth\", referenced from:\n");

            var ex = Assert.Throws<System.InvalidOperationException>(() =>
                NativeThunkCompiler.LinkWithClang(
                    OneObject, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", runner, NullLogger.Instance,
                    originalModuleName: null));

            Assert.Contains("Thunk linking failed", ex.Message);
            Assert.DoesNotContain("--link-framework", ex.Message);
        }

        [Fact]
        public void LinkWithClang_LinkFailure_HintComputedOnFullStderr_NotTruncatedPreview()
        {
            // The hint is computed on the FULL stderr before the 2000-char preview truncation, so a
            // symbol past the preview boundary still produces guidance.
            var runner = new MockCommandRunner();
            var padding = new string('x', 2500);
            runner.SetResponse("clang -shared", 1, "",
                "Undefined symbols for architecture arm64:\n" + padding + "\n" +
                "  \"_MTLCreateSystemDefaultDevice\", referenced from:\n");

            var ex = Assert.Throws<System.InvalidOperationException>(() =>
                NativeThunkCompiler.LinkWithClang(
                    OneObject, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", runner, NullLogger.Instance,
                    originalModuleName: null,
                    buildLinkFailureHint: stderr =>
                        SwiftWrapperCompiler.BuildSystemLinkDependencyHint(stderr, null, null)));

            Assert.Contains("--link-framework Metal", ex.Message);
        }

        private static int CountOccurrences(string haystack, string needle)
            => (haystack.Length - haystack.Replace(needle, "").Length) / needle.Length;
    }

    /// <summary>
    /// <see cref="SwiftWrapperCompiler.CollectTransitiveFrameworkNames"/> is the single probe both
    /// the swiftc and clang thunk-only link paths use to discover sibling framework-dependency
    /// names from the effective search paths: only directories holding a real linker-consumable
    /// binary count, and names are de-duplicated against an already-seen set.
    /// </summary>
    public class CollectTransitiveFrameworkNamesTests
    {
        // ar(1) static-archive magic "!<arch>\n" — a linker-consumable binary per IsLinkableFrameworkBinary.
        private static readonly byte[] ArMagic =
            { 0x21, 0x3C, 0x61, 0x72, 0x63, 0x68, 0x3E, 0x0A };

        private static void MakeFrameworkDir(string root, string name, byte[]? binary)
        {
            var fwDir = System.IO.Path.Combine(root, $"{name}.framework");
            System.IO.Directory.CreateDirectory(fwDir);
            if (binary != null)
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(fwDir, name), binary);
        }

        [Fact]
        public void IncludesFrameworkWithLinkableBinary_ExcludesBinaryless()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ctfn_{System.Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(root);
            try
            {
                MakeFrameworkDir(root, "DepKit", ArMagic);                                       // linkable
                MakeFrameworkDir(root, "HeaderOnly", null);                                      // no binary
                MakeFrameworkDir(root, "TextStub", System.Text.Encoding.ASCII.GetBytes("nope")); // not a binary

                var names = SwiftWrapperCompiler.CollectTransitiveFrameworkNames(new[] { root });

                Assert.Contains("DepKit", names);
                Assert.DoesNotContain("HeaderOnly", names);
                Assert.DoesNotContain("TextStub", names);
            }
            finally
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void DedupesAgainstAlreadySeen()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ctfn_{System.Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(root);
            try
            {
                MakeFrameworkDir(root, "DepKit", ArMagic);
                MakeFrameworkDir(root, "OrigMod", ArMagic);

                var names = SwiftWrapperCompiler.CollectTransitiveFrameworkNames(
                    new[] { root }, alreadySeen: new[] { "OrigMod" });

                Assert.Contains("DepKit", names);
                Assert.DoesNotContain("OrigMod", names);
            }
            finally
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void NullOrMissingSearchPaths_ReturnEmpty()
        {
            Assert.Empty(SwiftWrapperCompiler.CollectTransitiveFrameworkNames(null));
            Assert.Empty(SwiftWrapperCompiler.CollectTransitiveFrameworkNames(
                new[] { "/no/such/path/ctfn-missing-xyz" }));
        }
    }

    #endregion

    #region F. End-to-End Compile Tests

    public class SwiftWrapperCompileEndToEndTests
    {
        [Fact]
        public void Compile_NoSwiftFiles_ReturnsNull()
        {
            var dir = CreateTempDir();
            try
            {
                var runner = new MockCommandRunner();
                var result = SwiftWrapperCompiler.Compile(
                    dir, "TestLib", "/fw/search", "/dylib/path",
                    NullLogger.Instance, runner);

                Assert.Null(result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Compile_AllCodeStripped_ReturnsZeroCompiledCount()
        {
            var dir = CreateTempDir();
            try
            {
                // Write a Swift file that will be entirely stripped (SBW_ func with EveryProtocol())
                File.WriteAllText(Path.Combine(dir, "Swift.Module.swift"), """
                    public func SBW_broken() {
                        let proxy = EveryProtocol()
                    }
                    """);

                var runner = new MockCommandRunner();
                var result = SwiftWrapperCompiler.Compile(
                    dir, "TestLib", "/fw/search", "/dylib/path",
                    NullLogger.Instance, runner);

                Assert.NotNull(result);
                Assert.Equal(0, result.CompiledFileCount);
                Assert.True(result.StrippedBlockCount > 0);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Compile_HappyPath_InvokesSwiftcAndReturnsResult()
        {
            var dir = CreateTempDir();
            try
            {
                // Create source framework dir with Info.plist for deployment target
                var fwDir = Path.Combine(dir, "source-fw");
                Directory.CreateDirectory(fwDir);
                File.WriteAllText(Path.Combine(fwDir, "Info.plist"), """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0">
                    <dict>
                        <key>MinimumOSVersion</key>
                        <string>16.0</string>
                    </dict>
                    </plist>
                    """);
                var dylibPath = Path.Combine(fwDir, "TestLib");
                File.WriteAllText(dylibPath, "");

                // Write a clean Swift file
                File.WriteAllText(Path.Combine(dir, "Swift.TestLib.swift"), """
                    import Foundation
                    @_silgen_name("good_func")
                    public func good_func(_self: UnsafeMutableRawPointer) {
                        let x = self.getValue()
                    }
                    """);

                var runner = new MockCommandRunner();
                runner.SetResponse("--show-sdk-path", 0, "/sdk/path");
                runner.SetResponse("swiftc", 0, "");
                // Simulate a successful compile so the post-compile slice-binary validation sees a real
                // Mach-O at swiftc's -o target (and lipo reports the expected arch).
                runner.SynthesizeMachOOutputs = true;

                var result = SwiftWrapperCompiler.Compile(
                    dir, "TestLib", "/fw/search", dylibPath,
                    NullLogger.Instance, runner);

                Assert.NotNull(result);
                Assert.Equal(1, result.CompiledFileCount);
                Assert.Equal(0, result.StrippedBlockCount);
                Assert.Contains("TestLibSwiftBindings.xcframework", result.XCFrameworkPath);

                // Verify swiftc was invoked with correct deployment target
                Assert.Contains(runner.Invocations, i =>
                    i.Arguments.Contains("ios16.0-simulator"));

                // Verify xcframework structure was created
                Assert.True(Directory.Exists(result.XCFrameworkPath));
                Assert.True(File.Exists(Path.Combine(result.XCFrameworkPath, "Info.plist")));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Compile_CleansUpTempDir()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Swift.Module.swift"), """
                    import Foundation
                    @_silgen_name("func1")
                    public func func1(_self: UnsafeMutableRawPointer) {
                        self.foo()
                    }
                    """);

                var runner = new MockCommandRunner();
                runner.SetResponse("--show-sdk-path", 0, "/sdk/path");
                runner.SetResponse("swiftc", 0, "");
                // Simulate a successful compile so the post-compile slice-binary validation sees a real
                // Mach-O at swiftc's -o target (and lipo reports the expected arch).
                runner.SynthesizeMachOOutputs = true;

                // Create dylib stub
                var fwDir = Path.Combine(dir, "fw");
                Directory.CreateDirectory(fwDir);
                File.WriteAllText(Path.Combine(fwDir, "Lib"), "");

                SwiftWrapperCompiler.Compile(
                    dir, "Lib", "/fw/search", Path.Combine(fwDir, "Lib"),
                    NullLogger.Instance, runner);

                Assert.False(Directory.Exists(Path.Combine(dir, ".wrapper-build")));
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"swc_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region G. XCFrameworkResolution New Properties Tests

    public class XCFrameworkResolutionPropertiesTests
    {
        [Fact]
        public void Resolve_SimulatorSlice_SetsIsSimulatorTrue()
        {
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Lib"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Lib.framework", "Lib.framework/Lib");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Lib.framework", "Lib");
            fixture.CreateAbiJson(moduleDir, "arm64-apple-ios-simulator");
            fixture.CreateTbd(moduleDir, "Lib");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = XCFrameworkResolver.Resolve(
                fixture.RootPath, fixture.OutputPath,
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);

            Assert.True(result.IsSimulatorSlice);
            Assert.Equal("ios-arm64-simulator", result.LibraryIdentifier);
            Assert.EndsWith("ios-arm64-simulator", result.FrameworkSearchPath);
        }

        [Fact]
        public void Resolve_DeviceSlice_SetsIsSimulatorFalse()
        {
            using var fixture = new XCFrameworkFixture();
            var plist = """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>BinaryPath</key><string>Lib.framework/Lib</string>
                            <key>LibraryIdentifier</key><string>ios-arm64</string>
                            <key>LibraryPath</key><string>Lib.framework</string>
                            <key>SupportedArchitectures</key><array><string>arm64</string></array>
                            <key>SupportedPlatform</key><string>ios</string>
                        </dict>
                    </array>
                </dict>
                </plist>
                """;
            fixture.WriteInfoPlist(plist);
            var sliceDir = fixture.CreateSlice("ios-arm64", "Lib.framework", "Lib.framework/Lib");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Lib.framework", "Lib");
            fixture.CreateAbiJson(moduleDir, "arm64-apple-ios");
            fixture.CreateTbd(moduleDir, "Lib");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = XCFrameworkResolver.Resolve(
                fixture.RootPath, fixture.OutputPath,
                XCFrameworkPlatformTarget.Device, NullLogger.Instance, runner);

            Assert.False(result.IsSimulatorSlice);
            Assert.Equal("ios-arm64", result.LibraryIdentifier);
        }

        [Fact]
        public void Resolve_FrameworkSearchPath_DerivedFromXCFrameworkPath()
        {
            using var fixture = new XCFrameworkFixture();
            fixture.WriteInfoPlist(XCFrameworkModuleDiscoveryTests.MakeSimplePlist("Lib"));
            var sliceDir = fixture.CreateSlice("ios-arm64-simulator", "Lib.framework", "Lib.framework/Lib");
            var moduleDir = fixture.CreateSwiftModule(sliceDir, "Lib.framework", "Lib");
            fixture.CreateAbiJson(moduleDir, "arm64-apple-ios-simulator");
            fixture.CreateTbd(moduleDir, "Lib");

            var runner = new MockCommandRunner();
            runner.SetResponse("file", 0, "dynamically linked shared library");

            var result = XCFrameworkResolver.Resolve(
                fixture.RootPath, fixture.OutputPath,
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance, runner);

            // FrameworkSearchPath should be {xcframeworkPath}/{LibraryIdentifier}
            var expected = Path.Combine(fixture.RootPath, "ios-arm64-simulator");
            Assert.Equal(expected, result.FrameworkSearchPath);
        }
    }

    #endregion

    #region H. EvaluateResult (Fatal / Warning / Success) Tests

    public class SwiftWrapperEvaluateResultTests
    {
        [Fact]
        public void EvaluateResult_NullResult_AlwaysSuccess()
        {
            Assert.Equal(WrapperCompilationOutcome.Success,
                SwiftWrapperCompiler.EvaluateResult(null, asyncLibraryAutoWired: true));
            Assert.Equal(WrapperCompilationOutcome.Success,
                SwiftWrapperCompiler.EvaluateResult(null, asyncLibraryAutoWired: false));
        }

        [Fact]
        public void EvaluateResult_SuccessfulCompilation_AlwaysSuccess()
        {
            var result = new SwiftWrapperCompilationResult
            {
                XCFrameworkPath = "/tmp/out.xcframework",
                CompiledFileCount = 2,
                StrippedBlockCount = 1
            };
            Assert.Equal(WrapperCompilationOutcome.Success,
                SwiftWrapperCompiler.EvaluateResult(result, asyncLibraryAutoWired: true));
            Assert.Equal(WrapperCompilationOutcome.Success,
                SwiftWrapperCompiler.EvaluateResult(result, asyncLibraryAutoWired: false));
        }

        [Fact]
        public void EvaluateResult_AllStripped_AutoWired_Fatal()
        {
            var result = new SwiftWrapperCompilationResult
            {
                XCFrameworkPath = "",
                CompiledFileCount = 0,
                StrippedBlockCount = 5
            };
            Assert.Equal(WrapperCompilationOutcome.Fatal,
                SwiftWrapperCompiler.EvaluateResult(result, asyncLibraryAutoWired: true));
        }

        [Fact]
        public void EvaluateResult_AllStripped_ExplicitAsyncLib_Warning()
        {
            var result = new SwiftWrapperCompilationResult
            {
                XCFrameworkPath = "",
                CompiledFileCount = 0,
                StrippedBlockCount = 5
            };
            Assert.Equal(WrapperCompilationOutcome.Warning,
                SwiftWrapperCompiler.EvaluateResult(result, asyncLibraryAutoWired: false));
        }

        [Fact]
        public void EvaluateResult_Exception_AutoWired_Fatal()
        {
            var ex = new InvalidOperationException("swiftc not found");
            Assert.Equal(WrapperCompilationOutcome.Fatal,
                SwiftWrapperCompiler.EvaluateResult(null, asyncLibraryAutoWired: true, compilationException: ex));
        }

        [Fact]
        public void EvaluateResult_Exception_ExplicitAsyncLib_Warning()
        {
            var ex = new InvalidOperationException("swiftc not found");
            Assert.Equal(WrapperCompilationOutcome.Warning,
                SwiftWrapperCompiler.EvaluateResult(null, asyncLibraryAutoWired: false, compilationException: ex));
        }

        [Fact]
        public void EvaluateResult_ZeroStrippedZeroCompiled_Success()
        {
            // Edge case: CompiledFileCount == 0 but StrippedBlockCount == 0 too
            // (shouldn't happen in practice but tests the boundary)
            var result = new SwiftWrapperCompilationResult
            {
                XCFrameworkPath = "",
                CompiledFileCount = 0,
                StrippedBlockCount = 0
            };
            Assert.Equal(WrapperCompilationOutcome.Success,
                SwiftWrapperCompiler.EvaluateResult(result, asyncLibraryAutoWired: true));
        }

        [Fact]
        public void EvaluateResult_ExceptionTakesPrecedenceOverResult()
        {
            // If both exception and a result exist, exception wins
            var result = new SwiftWrapperCompilationResult
            {
                XCFrameworkPath = "/tmp/out.xcframework",
                CompiledFileCount = 2,
                StrippedBlockCount = 0
            };
            var ex = new InvalidOperationException("SDK missing");
            Assert.Equal(WrapperCompilationOutcome.Fatal,
                SwiftWrapperCompiler.EvaluateResult(result, asyncLibraryAutoWired: true, compilationException: ex));
        }
    }

    #endregion

    #region I. EffectiveOutcome (SDK-mode downgrade) Tests

    public class SwiftWrapperEffectiveOutcomeTests
    {
        [Fact]
        public void EffectiveOutcome_Fatal_SdkMode_DowngradesToWarning()
        {
            Assert.Equal(WrapperCompilationOutcome.Warning,
                SwiftWrapperCompiler.EffectiveOutcome(WrapperCompilationOutcome.Fatal, sdkMode: true));
        }

        [Fact]
        public void EffectiveOutcome_Fatal_NonSdkMode_StaysFatal()
        {
            Assert.Equal(WrapperCompilationOutcome.Fatal,
                SwiftWrapperCompiler.EffectiveOutcome(WrapperCompilationOutcome.Fatal, sdkMode: false));
        }

        [Fact]
        public void EffectiveOutcome_Warning_SdkMode_StaysWarning()
        {
            Assert.Equal(WrapperCompilationOutcome.Warning,
                SwiftWrapperCompiler.EffectiveOutcome(WrapperCompilationOutcome.Warning, sdkMode: true));
        }

        [Fact]
        public void EffectiveOutcome_Warning_NonSdkMode_StaysWarning()
        {
            Assert.Equal(WrapperCompilationOutcome.Warning,
                SwiftWrapperCompiler.EffectiveOutcome(WrapperCompilationOutcome.Warning, sdkMode: false));
        }

        [Fact]
        public void EffectiveOutcome_Success_SdkMode_StaysSuccess()
        {
            Assert.Equal(WrapperCompilationOutcome.Success,
                SwiftWrapperCompiler.EffectiveOutcome(WrapperCompilationOutcome.Success, sdkMode: true));
        }

        [Fact]
        public void EffectiveOutcome_Success_NonSdkMode_StaysSuccess()
        {
            Assert.Equal(WrapperCompilationOutcome.Success,
                SwiftWrapperCompiler.EffectiveOutcome(WrapperCompilationOutcome.Success, sdkMode: false));
        }
    }

    #endregion

    #region I. Deployment Target Tests

    public class DeploymentTargetTests
    {
        [Fact]
        public void EnforceMinimumDeploymentTarget_LowerVersion_RaisesToMinimum()
        {
            var result = SwiftWrapperCompiler.EnforceMinimumDeploymentTarget("13.0", "15.0");
            Assert.Equal("15.0", result);
        }

        [Fact]
        public void EnforceMinimumDeploymentTarget_HigherVersion_KeepsOriginal()
        {
            // Source at 17.0 stays at 17.0
            var result = SwiftWrapperCompiler.EnforceMinimumDeploymentTarget("17.0", "15.0");
            Assert.Equal("17.0", result);
        }

        [Fact]
        public void EnforceMinimumDeploymentTarget_EqualVersion_KeepsOriginal()
        {
            var result = SwiftWrapperCompiler.EnforceMinimumDeploymentTarget("15.0", "15.0");
            Assert.Equal("15.0", result);
        }

        [Fact]
        public void EnforceMinimumDeploymentTarget_InvalidVersion_KeepsOriginal()
        {
            var result = SwiftWrapperCompiler.EnforceMinimumDeploymentTarget("invalid", "15.0");
            Assert.Equal("invalid", result);
        }

        [Fact]
        public void EnforceMinimumDeploymentTarget_14_RaisedTo15()
        {
            var result = SwiftWrapperCompiler.EnforceMinimumDeploymentTarget("14.0", "15.0");
            Assert.Equal("15.0", result);
        }
    }

    #endregion

    #region EC-1/EC-18: PatchSwiftInterface Tests

    public class PatchSwiftInterfaceTests
    {
        [Fact]
        public void PatchSwiftInterface_PreservesNestedTypes()
        {
            var dir = CreateTempDir();
            try
            {
                var source = Path.Combine(dir, "source.swiftinterface");
                var dest = Path.Combine(dir, "patched.swiftinterface");
                File.WriteAllText(source,
                    "import LoggingLib\n" +
                    "public typealias LogLevel = LoggingLib.Level\n" +
                    "public class LoggingLib {\n" +
                    "  public func setDestination(_ dest: LoggingLib.ConsoleDestination) {}\n" +
                    "}\n");

                // Combined-form regex: group 1 = module, group 2 = trailing chain.
                var pattern = new System.Text.RegularExpressions.Regex(
                    @"\b(LoggingLib)\.(\w+(?:\.\w+)*)",
                    System.Text.RegularExpressions.RegexOptions.Compiled);
                var nestedByModule = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
                {
                    ["LoggingLib"] = new HashSet<string> { "Level" },
                };

                // Use reflection to call the private static method
                var method = typeof(SwiftWrapperCompiler).GetMethod("PatchSwiftInterface",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                method.Invoke(null, new object?[] { source, dest, pattern, nestedByModule });

                var result = File.ReadAllText(dest);
                // Level is nested — should be preserved
                Assert.Contains("LoggingLib.Level", result);
                // ConsoleDestination is NOT nested — should be stripped
                Assert.Contains("dest: ConsoleDestination", result);
                Assert.DoesNotContain("LoggingLib.ConsoleDestination", result);
                // import line should be untouched
                Assert.Contains("import LoggingLib", result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void PatchSwiftInterface_NoNestedTypes_StripsAll()
        {
            var dir = CreateTempDir();
            try
            {
                var source = Path.Combine(dir, "source.swiftinterface");
                var dest = Path.Combine(dir, "patched.swiftinterface");
                File.WriteAllText(source,
                    "import NetworkMonitor\n" +
                    "public class NetworkMonitor {\n" +
                    "  public var connection: NetworkMonitor.Connection\n" +
                    "}\n");

                var pattern = new System.Text.RegularExpressions.Regex(
                    @"\b(NetworkMonitor)\.(\w+(?:\.\w+)*)",
                    System.Text.RegularExpressions.RegexOptions.Compiled);
                var emptyDict = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

                var method = typeof(SwiftWrapperCompiler).GetMethod("PatchSwiftInterface",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                method.Invoke(null, new object?[] { source, dest, pattern, emptyDict });

                var result = File.ReadAllText(dest);
                Assert.DoesNotContain("NetworkMonitor.Connection", result);
                Assert.Contains("connection: Connection", result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void PatchSwiftInterface_MultipleModulesInOneCall_PerModuleNestedCarveouts()
        {
            // The composition fix: a single PatchSwiftInterface invocation must patch
            // every collision in one pass with per-module nested-type carveouts.
            // Without this, sequential precompiles overwrote one another's shadow.
            var dir = CreateTempDir();
            try
            {
                var source = Path.Combine(dir, "source.swiftinterface");
                var dest = Path.Combine(dir, "patched.swiftinterface");
                File.WriteAllText(source,
                    "import NetworkMonitor\n" +
                    "import LoggingLib\n" +
                    "public typealias LogLevel = LoggingLib.Level\n" +
                    "public var conn: NetworkMonitor.Connection\n" +
                    "public var dest: LoggingLib.ConsoleDestination\n");

                var pattern = new System.Text.RegularExpressions.Regex(
                    @"\b(LoggingLib|NetworkMonitor)\.(\w+(?:\.\w+)*)",
                    System.Text.RegularExpressions.RegexOptions.Compiled);
                var nestedByModule = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
                {
                    ["LoggingLib"] = new HashSet<string> { "Level" },
                    // No entry for the second module — absence of key means strip all.
                };

                var method = typeof(SwiftWrapperCompiler).GetMethod("PatchSwiftInterface",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                method.Invoke(null, new object?[] { source, dest, pattern, nestedByModule });

                var result = File.ReadAllText(dest);
                // LoggingLib.Level survives because "Level" is nested.
                Assert.Contains("LoggingLib.Level", result);
                // LoggingLib.ConsoleDestination is stripped (not nested).
                Assert.Contains("dest: ConsoleDestination", result);
                Assert.DoesNotContain("LoggingLib.ConsoleDestination", result);
                // The second module's qualified reference is stripped (no nested-type carveouts for it).
                Assert.Contains("conn: Connection", result);
                Assert.DoesNotContain("NetworkMonitor.Connection", result);
                // import lines untouched.
                Assert.Contains("import NetworkMonitor", result);
                Assert.Contains("import LoggingLib", result);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"patch_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region G. Missing Module Extraction Tests

    public class ExtractMissingModulesTests
    {
        [Fact]
        public void ExtractMissingModules_SingleModule_ReturnsSingleName()
        {
            var stderr = "/tmp/wrapper.swift:1:8: error: no such module 'PaymentSdk3DS2'\nimport PaymentSdk3DS2\n       ^";
            var modules = SwiftWrapperCompiler.ExtractMissingModules(stderr);
            Assert.Single(modules);
            Assert.Equal("PaymentSdk3DS2", modules[0]);
        }

        [Fact]
        public void ExtractMissingModules_MultipleModules_ReturnsDistinctNames()
        {
            var stderr = "error: no such module 'PaymentSdkCore'\nerror: no such module 'PaymentSdk3DS2'\nerror: no such module 'PaymentSdkCore'";
            var modules = SwiftWrapperCompiler.ExtractMissingModules(stderr);
            Assert.Equal(2, modules.Count);
            Assert.Contains("PaymentSdkCore", modules);
            Assert.Contains("PaymentSdk3DS2", modules);
        }

        [Fact]
        public void ExtractMissingModules_NoMissingModules_ReturnsEmpty()
        {
            var stderr = "error: use of undeclared type 'SomeType'\nerror: cannot convert value";
            var modules = SwiftWrapperCompiler.ExtractMissingModules(stderr);
            Assert.Empty(modules);
        }

        [Fact]
        public void ExtractMissingModules_EmptyStderr_ReturnsEmpty()
        {
            var modules = SwiftWrapperCompiler.ExtractMissingModules("");
            Assert.Empty(modules);
        }

        [Fact]
        public void ExtractMissingModules_MixedErrors_ExtractsOnlyModuleNames()
        {
            var stderr = "error: no such module 'CloudPlatformSdkAuth'\n" +
                         "error: cannot find type 'CloudPlatformSdkAuth'\n" +
                         "error: no such module 'CloudPlatformSdkCore'\n" +
                         "error: value of type 'Foo' has no member 'bar'";
            var modules = SwiftWrapperCompiler.ExtractMissingModules(stderr);
            Assert.Equal(2, modules.Count);
            Assert.Equal("CloudPlatformSdkAuth", modules[0]);
            Assert.Equal("CloudPlatformSdkCore", modules[1]);
        }
    }

    public class LLVMProfileRuntimeAutoLinkTests
    {
        [Fact]
        public void IsLLVMProfileRuntimeMissing_DetectsExactSymbolToken()
        {
            var stderr =
                "Undefined symbols for architecture arm64:\n" +
                "  \"___llvm_profile_runtime\", referenced from:\n" +
                "      ___llvm_profile_runtime_user in IndoorMapsSdk[arm64][2](IndoorMapsSdk.o)\n" +
                "ld: symbol(s) not found for architecture arm64";
            Assert.True(SwiftWrapperCompiler.IsLLVMProfileRuntimeMissing(stderr));
        }

        [Fact]
        public void IsLLVMProfileRuntimeMissing_UnrelatedLinkErrors_ReturnsFalse()
        {
            var stderr =
                "Undefined symbols for architecture arm64:\n" +
                "  \"_OBJC_CLASS_$_CloudPlatformApp\", referenced from:\n" +
                "ld: symbol(s) not found for architecture arm64";
            Assert.False(SwiftWrapperCompiler.IsLLVMProfileRuntimeMissing(stderr));
        }

        [Fact]
        public void IsLLVMProfileRuntimeMissing_EmptyStderr_ReturnsFalse()
        {
            Assert.False(SwiftWrapperCompiler.IsLLVMProfileRuntimeMissing(""));
            Assert.False(SwiftWrapperCompiler.IsLLVMProfileRuntimeMissing(null!));
        }

        [Theory]
        [InlineData("arm64-apple-ios18.0-simulator", "iossim")]
        [InlineData("x86_64-apple-ios18.0-simulator", "iossim")]
        [InlineData("arm64-apple-ios18.0", "ios")]
        [InlineData("arm64-apple-ios18.0-macabi", "osx")]
        [InlineData("arm64-apple-tvos18.0-simulator", "tvossim")]
        [InlineData("arm64-apple-tvos18.0", "tvos")]
        [InlineData("arm64-apple-watchos11.0-simulator", "watchossim")]
        [InlineData("arm64-apple-watchos11.0", "watchos")]
        [InlineData("arm64-apple-xros2.0-simulator", "xrossim")]
        [InlineData("arm64-apple-xros2.0", "xros")]
        [InlineData("arm64-apple-visionos2.0-simulator", "xrossim")]
        [InlineData("arm64-apple-macos15.0", "osx")]
        public void MapTargetTripleToProfilePlatform_KnownTriples_ReturnsExpectedSuffix(
            string triple, string expected)
        {
            Assert.Equal(expected, SwiftWrapperCompiler.MapTargetTripleToProfilePlatform(triple));
        }

        [Fact]
        public void MapTargetTripleToProfilePlatform_UnknownPlatform_ReturnsNull()
        {
            Assert.Null(SwiftWrapperCompiler.MapTargetTripleToProfilePlatform("riscv64-unknown-linux"));
            Assert.Null(SwiftWrapperCompiler.MapTargetTripleToProfilePlatform(""));
            Assert.Null(SwiftWrapperCompiler.MapTargetTripleToProfilePlatform(null!));
        }

        [Fact]
        public void ResolveProfileRuntimeArchive_ArchivePresent_ReturnsExpectedPath()
        {
            var resourceDir = Path.Combine(Path.GetTempPath(), $"profile_rt_{Guid.NewGuid():N}");
            var archiveDir = Path.Combine(resourceDir, "lib", "darwin");
            Directory.CreateDirectory(archiveDir);
            var archivePath = Path.Combine(archiveDir, "libclang_rt.profile_iossim.a");
            File.WriteAllText(archivePath, "");
            try
            {
                var runner = new MockCommandRunner();
                runner.SetResponse("clang -print-resource-dir", 0, resourceDir);
                var resolved = SwiftWrapperCompiler.ResolveProfileRuntimeArchive(
                    "arm64-apple-ios18.0-simulator", runner, NullLogger.Instance);
                Assert.Equal(archivePath, resolved);
            }
            finally
            {
                Directory.Delete(resourceDir, recursive: true);
            }
        }

        [Fact]
        public void ResolveProfileRuntimeArchive_ArchiveMissing_ReturnsNull()
        {
            var resourceDir = Path.Combine(Path.GetTempPath(), $"profile_rt_{Guid.NewGuid():N}");
            Directory.CreateDirectory(resourceDir);
            try
            {
                var runner = new MockCommandRunner();
                runner.SetResponse("clang -print-resource-dir", 0, resourceDir);
                var resolved = SwiftWrapperCompiler.ResolveProfileRuntimeArchive(
                    "arm64-apple-ios18.0-simulator", runner, NullLogger.Instance);
                Assert.Null(resolved);
            }
            finally
            {
                Directory.Delete(resourceDir, recursive: true);
            }
        }

        [Fact]
        public void ResolveProfileRuntimeArchive_UnknownTriple_ReturnsNull()
        {
            var runner = new MockCommandRunner();
            var resolved = SwiftWrapperCompiler.ResolveProfileRuntimeArchive(
                "riscv64-unknown-linux", runner, NullLogger.Instance);
            Assert.Null(resolved);
            // No xcrun call should be issued for unknown triples — the early
            // platform-mapping return prevents pointless subprocess fan-out.
            Assert.Empty(runner.Invocations);
        }

        [Fact]
        public void InvokeSwiftCompiler_ProfileRuntimeMissing_RetriesWithArchive()
        {
            var resourceDir = Path.Combine(Path.GetTempPath(), $"profile_rt_{Guid.NewGuid():N}");
            var archiveDir = Path.Combine(resourceDir, "lib", "darwin");
            Directory.CreateDirectory(archiveDir);
            var archivePath = Path.Combine(archiveDir, "libclang_rt.profile_iossim.a");
            File.WriteAllText(archivePath, "");
            try
            {
                var runner = new ScriptedCommandRunner();
                // First swiftc invocation: link fails with the profile-runtime symbol error.
                runner.QueueResponse(
                    matchSubstring: "swiftc",
                    exitCode: 1,
                    stdOut: "",
                    stdErr: "Undefined symbols for architecture arm64:\n" +
                            "  \"___llvm_profile_runtime\", referenced from:\n" +
                            "      ___llvm_profile_runtime_user in IndoorMapsSdk[arm64][2](IndoorMapsSdk.o)\n" +
                            "ld: symbol(s) not found for architecture arm64");
                // xcrun clang -print-resource-dir resolution.
                runner.QueueResponse(
                    matchSubstring: "clang -print-resource-dir",
                    exitCode: 0,
                    stdOut: resourceDir);
                // Retry succeeds.
                runner.QueueResponse(matchSubstring: "swiftc", exitCode: 0, stdOut: "");

                var files = new List<string> { "/tmp/a.swift" };
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios18.0-simulator", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance);

                Assert.Equal(3, runner.Invocations.Count);
                var firstSwiftc = runner.Invocations[0].Arguments;
                var retrySwiftc = runner.Invocations[2].Arguments;
                Assert.DoesNotContain("libclang_rt.profile_", firstSwiftc);
                Assert.Contains($"-Xlinker \"{archivePath}\"", retrySwiftc);
            }
            finally
            {
                Directory.Delete(resourceDir, recursive: true);
            }
        }

        [Fact]
        public void InvokeSwiftCompiler_ProfileRuntimeMissing_RetryAlsoFails_ThrowsWithRetryStderr()
        {
            var resourceDir = Path.Combine(Path.GetTempPath(), $"profile_rt_{Guid.NewGuid():N}");
            var archiveDir = Path.Combine(resourceDir, "lib", "darwin");
            Directory.CreateDirectory(archiveDir);
            File.WriteAllText(
                Path.Combine(archiveDir, "libclang_rt.profile_iossim.a"), "");
            try
            {
                var runner = new ScriptedCommandRunner();
                runner.QueueResponse(
                    matchSubstring: "swiftc", exitCode: 1, stdOut: "",
                    stdErr: "Undefined symbols for architecture arm64:\n" +
                            "  \"___llvm_profile_runtime\", referenced from:\n" +
                            "      ___llvm_profile_runtime_user in X[arm64][2](X.o)");
                runner.QueueResponse(
                    matchSubstring: "clang -print-resource-dir", exitCode: 0, stdOut: resourceDir);
                runner.QueueResponse(
                    matchSubstring: "swiftc", exitCode: 1, stdOut: "",
                    stdErr: "ld: error: some other failure on retry");

                var files = new List<string> { "/tmp/a.swift" };
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    SwiftWrapperCompiler.InvokeSwiftCompiler(
                        files, "/tmp/out/Binary", "TestSwiftBindings",
                        "arm64-apple-ios18.0-simulator", "/sdk/path", "/fw/search",
                        runner, NullLogger.Instance));
                Assert.Contains("compilation failed", ex.Message);
                // The message should reflect the retry's stderr, not the first attempt's.
                Assert.Contains("some other failure on retry", ex.Message);
            }
            finally
            {
                Directory.Delete(resourceDir, recursive: true);
            }
        }

        [Fact]
        public void InvokeSwiftCompiler_UnrelatedLinkError_DoesNotRetry()
        {
            var runner = new ScriptedCommandRunner();
            runner.QueueResponse(
                matchSubstring: "swiftc", exitCode: 1, stdOut: "",
                stdErr: "ld: error: framework not found Foo");

            var files = new List<string> { "/tmp/a.swift" };
            Assert.Throws<InvalidOperationException>(() =>
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios18.0-simulator", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance));
            // Single invocation — no retry path exercised for unrelated failures.
            Assert.Single(runner.Invocations);
        }
    }

    /// <summary>
    /// FIFO scripted runner: each Run() consumes the next queued response whose
    /// matchSubstring appears in the command/arguments. Lets us script multi-call
    /// sequences (initial swiftc fail → resource-dir lookup → retry swiftc) where
    /// the same command name is reused with different intended outcomes.
    /// </summary>
    internal sealed class ScriptedCommandRunner : ICommandRunner
    {
        private readonly Queue<(string Match, int ExitCode, string StdOut, string StdErr)> _responses = new();
        public List<(string Command, string Arguments)> Invocations { get; } = new();

        public void QueueResponse(string matchSubstring, int exitCode, string stdOut, string stdErr = "")
        {
            _responses.Enqueue((matchSubstring, exitCode, stdOut, stdErr));
        }

        public (int ExitCode, string StdOut, string StdErr) Run(string command, string arguments, int timeoutMs = 30000)
        {
            Invocations.Add((command, arguments));
            if (_responses.Count == 0)
                return (0, "", "");
            var (match, exit, stdout, stderr) = _responses.Peek();
            if (($"{command} {arguments}").Contains(match, StringComparison.Ordinal))
            {
                _responses.Dequeue();
                return (exit, stdout, stderr);
            }
            return (0, "", "");
        }
    }

    #endregion

    #region H. Platform Path Resolution Tests

    public class SwiftWrapperPlatformPathTests
    {
        [Fact]
        public void ResolvePlatformPath_Success_ReturnsPath()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-platform-path", 0,
                "/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneSimulator.platform");

            var result = SwiftWrapperCompiler.ResolvePlatformPath("iphonesimulator", runner);
            Assert.Contains("iPhoneSimulator.platform", result);
        }

        [Fact]
        public void ResolvePlatformPath_Failure_Throws()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-platform-path", 1, "", "xcode-select: error");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                SwiftWrapperCompiler.ResolvePlatformPath("iphonesimulator", runner));
            Assert.Contains("platform path", ex.Message);
        }

        [Fact]
        public void ResolvePlatformPath_EmptyOutput_Throws()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-platform-path", 0, "");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                SwiftWrapperCompiler.ResolvePlatformPath("iphonesimulator", runner));
            Assert.Contains("platform path", ex.Message);
        }

        [Theory]
        [InlineData("iphonesimulator")]
        [InlineData("iphoneos")]
        [InlineData("macosx")]
        public void ResolvePlatformPath_UsesCorrectSdkName(string sdkName)
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-platform-path", 0, "/some/path");

            SwiftWrapperCompiler.ResolvePlatformPath(sdkName, runner);

            Assert.Single(runner.Invocations);
            Assert.Contains($"--sdk {sdkName}", runner.Invocations[0].Arguments);
            Assert.Contains("--show-sdk-platform-path", runner.Invocations[0].Arguments);
        }
    }

    #endregion

    #region I. XCTest Dependency Detection Tests

    public class XCTestDependencyDetectionTests
    {
        [Fact]
        public void DetectXCTestDependency_WithImport_ReturnsTrue()
        {
            var dir = CreateTempDir();
            try
            {
                var path = Path.Combine(dir, "Quick.swiftinterface");
                File.WriteAllText(path, "import Swift\nimport XCTest\nimport Foundation\n");

                Assert.True(SwiftWrapperCompiler.DetectXCTestDependency(path));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void DetectXCTestDependency_WithExportedImport_ReturnsTrue()
        {
            var dir = CreateTempDir();
            try
            {
                var path = Path.Combine(dir, "Quick.swiftinterface");
                File.WriteAllText(path, "import Swift\n@_exported import XCTest\n");

                Assert.True(SwiftWrapperCompiler.DetectXCTestDependency(path));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void DetectXCTestDependency_WithoutImport_ReturnsFalse()
        {
            var dir = CreateTempDir();
            try
            {
                var path = Path.Combine(dir, "ImagePipeline.swiftinterface");
                File.WriteAllText(path, "import Swift\nimport Foundation\nimport UIKit\n");

                Assert.False(SwiftWrapperCompiler.DetectXCTestDependency(path));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void DetectXCTestDependency_NullPath_ReturnsFalse()
        {
            Assert.False(SwiftWrapperCompiler.DetectXCTestDependency(null));
        }

        [Fact]
        public void DetectXCTestDependency_NonexistentFile_ReturnsFalse()
        {
            Assert.False(SwiftWrapperCompiler.DetectXCTestDependency("/nonexistent/file.swiftinterface"));
        }

        [Fact]
        public void DetectXCTestDependency_SubstringMatch_ReturnsFalse()
        {
            // "import XCTestUtils" should NOT trigger detection
            var dir = CreateTempDir();
            try
            {
                var path = Path.Combine(dir, "Lib.swiftinterface");
                File.WriteAllText(path, "import Swift\nimport XCTestUtils\n");

                Assert.False(SwiftWrapperCompiler.DetectXCTestDependency(path));
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"xctest_det_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region J. Architecture Propagation Tests

    public class ArchitecturePropagationTests
    {
        [Fact]
        public void SliceVariant_DefaultArchitecture_IsArm64()
        {
            var slice = new SliceVariant
            {
                Platform = ApplePlatform.iOS,
                IsSimulator = true,
                SdkName = "iphonesimulator",
                SliceId = "ios-arm64-simulator",
                PlistPlatformName = "iPhoneSimulator",
                XCFrameworkPlatformString = "ios",
                XCFrameworkPlatformVariant = "simulator",
            };
            Assert.Equal("arm64", slice.Architecture);
        }

        [Fact]
        public void SliceVariant_WithOverride_UsesOverriddenArchitecture()
        {
            var slice = new SliceVariant
            {
                Platform = ApplePlatform.iOS,
                IsSimulator = true,
                SdkName = "iphonesimulator",
                SliceId = "ios-x86_64-simulator",
                PlistPlatformName = "iPhoneSimulator",
                XCFrameworkPlatformString = "ios",
                XCFrameworkPlatformVariant = "simulator",
                Architecture = "x86_64"
            };
            Assert.Equal("x86_64", slice.Architecture);
        }

        [Fact]
        public void SliceVariant_WithExpression_OverridesArchitecture()
        {
            var original = new SliceVariant
            {
                Platform = ApplePlatform.iOS,
                IsSimulator = true,
                SdkName = "iphonesimulator",
                SliceId = "ios-arm64-simulator",
                PlistPlatformName = "iPhoneSimulator",
                XCFrameworkPlatformString = "ios",
                XCFrameworkPlatformVariant = "simulator",
            };
            var overridden = original with { Architecture = "x86_64" };
            Assert.Equal("x86_64", overridden.Architecture);
            Assert.Equal("arm64", original.Architecture);
        }

        // WithArchitecture must recompute SliceId so the wrapper xcframework slice
        // directory matches the produced binary's arch. A bare `with { Architecture }`
        // leaves SliceId stale, which mis-slices the wrapper (e.g. an x86_64 binary
        // under a "macos-arm64" directory) and breaks NativeReference/dlopen resolution.
        [Theory]
        [InlineData("macos", null, "x86_64", "macos-x86_64")]
        [InlineData("ios", "simulator", "x86_64", "ios-x86_64-simulator")]
        [InlineData("ios", "maccatalyst", "x86_64", "ios-x86_64-maccatalyst")]
        [InlineData("macos", null, "arm64", "macos-arm64")]
        public void WithArchitecture_RecomputesSliceId(
            string platformString, string? variant, string arch, string expectedSliceId)
        {
            var original = new SliceVariant
            {
                Platform = platformString == "macos" ? ApplePlatform.macOS : ApplePlatform.iOS,
                IsSimulator = variant == "simulator",
                SdkName = "sdk",
                SliceId = variant == null ? $"{platformString}-arm64" : $"{platformString}-arm64-{variant}",
                PlistPlatformName = "Plat",
                XCFrameworkPlatformString = platformString,
                XCFrameworkPlatformVariant = variant,
            };

            var result = original.WithArchitecture(arch);

            Assert.Equal(arch, result.Architecture);
            Assert.Equal(expectedSliceId, result.SliceId);
            // Original is unchanged (record copy semantics).
            Assert.Equal("arm64", original.Architecture);
        }

        [Theory]
        [InlineData("arm64", "arm64-apple-ios17.0-simulator")]
        [InlineData("x86_64", "x86_64-apple-ios17.0-simulator")]
        public void SliceVariant_GetTargetTriple_UsesArchitecture(string arch, string expected)
        {
            var slice = new SliceVariant
            {
                Platform = ApplePlatform.iOS,
                IsSimulator = true,
                SdkName = "iphonesimulator",
                SliceId = $"ios-{arch}-simulator",
                PlistPlatformName = "iPhoneSimulator",
                XCFrameworkPlatformString = "ios",
                XCFrameworkPlatformVariant = "simulator",
                Architecture = arch,
            };
            Assert.Equal(expected, slice.GetTargetTriple("17.0"));
        }
    }

    #endregion

    #region H. Shadow Framework Staging Tests

    public class PrecompileSanitizedShadowFrameworkTests
    {
        private static (string ModuleDir, string PublicIface, string PrivateIface) BuildSliceFixture(
            string moduleName, bool includePrivateInterface, bool includeBinary)
        {
            // .../<slice>/<Module>.framework/Modules/<Module>.swiftmodule/<arch>.swiftinterface
            var sliceRoot = Path.Combine(Path.GetTempPath(), $"shadow_test_{Guid.NewGuid():N}");
            var sliceDir = Path.Combine(sliceRoot, "ios-arm64-simulator");
            var frameworkDir = Path.Combine(sliceDir, $"{moduleName}.framework");
            var modulesDir = Path.Combine(frameworkDir, "Modules");
            var moduleDir = Path.Combine(modulesDir, $"{moduleName}.swiftmodule");
            Directory.CreateDirectory(moduleDir);

            var publicIface = Path.Combine(moduleDir, "arm64-apple-ios-simulator.swiftinterface");
            File.WriteAllText(publicIface,
                "// swift-interface-format-version: 1.0\n" +
                $"public struct Greeter {{ public init() {{}} public func hello() {{}} }}\n");

            var privateIface = Path.Combine(moduleDir, "arm64-apple-ios-simulator.private.swiftinterface");
            if (includePrivateInterface)
            {
                File.WriteAllText(privateIface,
                    "// swift-interface-format-version: 1.0\n" +
                    "@_spi(Internal) public func spiOnly() {}\n");
            }

            if (includeBinary)
            {
                File.WriteAllText(Path.Combine(frameworkDir, moduleName), "fake-macho-bytes");
            }

            return (moduleDir, publicIface, privateIface);
        }

        [Fact]
        public void PrivateInterfacePresent_NoCollisions_StagesSanitizedShadow()
        {
            var (_, publicIface, _) = BuildSliceFixture("Demo", includePrivateInterface: true, includeBinary: true);
            var buildDir = Path.Combine(Path.GetTempPath(), $"shadow_build_{Guid.NewGuid():N}");
            Directory.CreateDirectory(buildDir);
            var runner = new MockCommandRunner();

            var shadow = SwiftWrapperCompiler.PrecompileSanitizedShadowFramework(
                "Demo", publicIface, "arm64-apple-ios17.0-simulator", "/sdk",
                buildDir, runner, NullLogger.Instance,
                collisions: Array.Empty<SwiftWrapperCompiler.CollisionPatchTarget>());

            Assert.NotNull(shadow);
            // No collisions ⇒ no swift-frontend invocation (the public interface compiles
            // on its own; we just need to hide the private one from swiftc).
            Assert.Empty(runner.Invocations);

            var shadowModuleDir = Path.Combine(shadow!, "Demo.framework", "Modules", "Demo.swiftmodule");
            Assert.True(Directory.Exists(shadowModuleDir));
            Assert.True(File.Exists(Path.Combine(shadowModuleDir, "arm64-apple-ios-simulator.swiftinterface")));
            Assert.False(File.Exists(Path.Combine(shadowModuleDir, "arm64-apple-ios-simulator.private.swiftinterface")));
            Assert.True(File.Exists(Path.Combine(shadow!, "Demo.framework", "Modules", "module.modulemap")));
        }

        [Fact]
        public void PrivateInterfacePresent_BinarySymlinkedIntoShadow()
        {
            var (_, publicIface, _) = BuildSliceFixture("Demo", includePrivateInterface: true, includeBinary: true);
            var buildDir = Path.Combine(Path.GetTempPath(), $"shadow_build_{Guid.NewGuid():N}");
            Directory.CreateDirectory(buildDir);

            var shadow = SwiftWrapperCompiler.PrecompileSanitizedShadowFramework(
                "Demo", publicIface, "arm64-apple-ios17.0-simulator", "/sdk",
                buildDir, new MockCommandRunner(), NullLogger.Instance,
                collisions: Array.Empty<SwiftWrapperCompiler.CollisionPatchTarget>());

            Assert.NotNull(shadow);
            var shadowBinary = Path.Combine(shadow!, "Demo.framework", "Demo");
            var info = new FileInfo(shadowBinary);
            Assert.True(info.Exists || info.LinkTarget != null,
                "Shadow framework should expose <Module> binary (symlink or copy) so ld doesn't have to fall through to the real -F.");
        }

        [Fact]
        public void NoPrivateInterface_NoCollisions_ReturnsNull()
        {
            var (_, publicIface, _) = BuildSliceFixture("Demo", includePrivateInterface: false, includeBinary: true);
            var buildDir = Path.Combine(Path.GetTempPath(), $"shadow_build_{Guid.NewGuid():N}");
            Directory.CreateDirectory(buildDir);

            var shadow = SwiftWrapperCompiler.PrecompileSanitizedShadowFramework(
                "Demo", publicIface, "arm64-apple-ios17.0-simulator", "/sdk",
                buildDir, new MockCommandRunner(), NullLogger.Instance,
                collisions: Array.Empty<SwiftWrapperCompiler.CollisionPatchTarget>());

            Assert.Null(shadow);
        }

        [Fact]
        public void EmptySwiftInterfacePath_ReturnsNull()
        {
            var buildDir = Path.Combine(Path.GetTempPath(), $"shadow_build_{Guid.NewGuid():N}");
            Directory.CreateDirectory(buildDir);

            var shadow = SwiftWrapperCompiler.PrecompileSanitizedShadowFramework(
                "Demo", swiftInterfacePath: "", "arm64-apple-ios17.0-simulator", "/sdk",
                buildDir, new MockCommandRunner(), NullLogger.Instance,
                collisions: Array.Empty<SwiftWrapperCompiler.CollisionPatchTarget>());

            Assert.Null(shadow);
        }

        [Fact]
        public void PrivateInterface_InterfaceOnlyFramework_NoBinarySymlinkButShadowStaged()
        {
            // Interface-only xcframework slice: no <Module>.framework/<Module> binary on disk.
            // Helper should still stage the swiftinterface-filtered shadow; binary symlink is
            // best-effort and is silently skipped when there's nothing to symlink.
            var (_, publicIface, _) = BuildSliceFixture("Demo", includePrivateInterface: true, includeBinary: false);
            var buildDir = Path.Combine(Path.GetTempPath(), $"shadow_build_{Guid.NewGuid():N}");
            Directory.CreateDirectory(buildDir);

            var shadow = SwiftWrapperCompiler.PrecompileSanitizedShadowFramework(
                "Demo", publicIface, "arm64-apple-ios17.0-simulator", "/sdk",
                buildDir, new MockCommandRunner(), NullLogger.Instance,
                collisions: Array.Empty<SwiftWrapperCompiler.CollisionPatchTarget>());

            Assert.NotNull(shadow);
            var shadowModuleDir = Path.Combine(shadow!, "Demo.framework", "Modules", "Demo.swiftmodule");
            Assert.True(File.Exists(Path.Combine(shadowModuleDir, "arm64-apple-ios-simulator.swiftinterface")));
        }

        [Fact]
        public void PrivateInterface_RealModulemapAndHeadersStaged_WhenSourceHasThem()
        {
            // Regression for mixed ObjC+Swift frameworks: the bound module's
            // public swiftinterface references ObjC symbols declared in the framework's umbrella
            // header. A Swift-only modulemap drops those
            // headers and swiftc errors with "no type named '...' in module '...'". The shadow
            // must mirror the real modulemap and public Headers/ so umbrella references resolve.
            var (moduleDir, publicIface, _) = BuildSliceFixture("Demo", includePrivateInterface: true, includeBinary: true);
            var realFrameworkDir = Path.GetDirectoryName(Path.GetDirectoryName(moduleDir))!; // <Module>.framework
            Directory.CreateDirectory(Path.Combine(realFrameworkDir, "Headers"));
            File.WriteAllText(Path.Combine(realFrameworkDir, "Headers", "Demo.h"),
                "#import <Foundation/Foundation.h>\n@interface DemoObjC : NSObject @end\n");
            File.WriteAllText(Path.Combine(realFrameworkDir, "Modules", "module.modulemap"),
                "framework module Demo {\n  umbrella header \"Demo.h\"\n  export *\n}\n");

            var buildDir = Path.Combine(Path.GetTempPath(), $"shadow_build_{Guid.NewGuid():N}");
            Directory.CreateDirectory(buildDir);

            var shadow = SwiftWrapperCompiler.PrecompileSanitizedShadowFramework(
                "Demo", publicIface, "arm64-apple-ios17.0-simulator", "/sdk",
                buildDir, new MockCommandRunner(), NullLogger.Instance,
                collisions: Array.Empty<SwiftWrapperCompiler.CollisionPatchTarget>());

            Assert.NotNull(shadow);
            var shadowFrameworkDir = Path.Combine(shadow!, "Demo.framework");

            var shadowModulemap = Path.Combine(shadowFrameworkDir, "Modules", "module.modulemap");
            Assert.True(File.Exists(shadowModulemap));
            // The modulemap content must be the real one (umbrella reference preserved),
            // not the minimal Swift-only stub.
            var modulemapText = File.ReadAllText(shadowModulemap);
            Assert.Contains("umbrella header \"Demo.h\"", modulemapText);

            // Headers/ must be present so umbrella-referenced .h files resolve.
            var shadowUmbrella = Path.Combine(shadowFrameworkDir, "Headers", "Demo.h");
            Assert.True(File.Exists(shadowUmbrella));
        }

        [Fact]
        public void PrivateInterface_MinimalModulemap_WhenSourceHasNone()
        {
            // Fallback path: pure-Swift xcframework slice with no public modulemap. Helper
            // must still stage the shadow with a minimal Swift-only modulemap so swiftc
            // can find <Module>.framework via -F. Headers/ staging is skipped.
            var (moduleDir, publicIface, _) = BuildSliceFixture("Demo", includePrivateInterface: true, includeBinary: true);
            var realFrameworkDir = Path.GetDirectoryName(Path.GetDirectoryName(moduleDir))!;
            // Intentionally do NOT create a module.modulemap or Headers/ in the source.

            var buildDir = Path.Combine(Path.GetTempPath(), $"shadow_build_{Guid.NewGuid():N}");
            Directory.CreateDirectory(buildDir);

            var shadow = SwiftWrapperCompiler.PrecompileSanitizedShadowFramework(
                "Demo", publicIface, "arm64-apple-ios17.0-simulator", "/sdk",
                buildDir, new MockCommandRunner(), NullLogger.Instance,
                collisions: Array.Empty<SwiftWrapperCompiler.CollisionPatchTarget>());

            Assert.NotNull(shadow);
            var shadowModulemap = Path.Combine(shadow!, "Demo.framework", "Modules", "module.modulemap");
            Assert.True(File.Exists(shadowModulemap));
            var modulemapText = File.ReadAllText(shadowModulemap);
            Assert.Contains("framework module Demo", modulemapText);
            // No headers staged when the source had none.
            Assert.False(Directory.Exists(Path.Combine(shadow!, "Demo.framework", "Headers")));
        }

        [Fact]
        public void PrivateInterface_PrivateHeadersStaged_WhenModulemapReferencesThem()
        {
            // Regression for modules with PrivateHeaders: the public modulemap declares a header
            // which lives in PrivateHeaders/, not Headers/ (legal — swiftc searches both
            // directories for non-umbrella `header` lines). Both directories must be staged
            // so the header lookup succeeds. The @_spi surface lives in module.private.modulemap
            // (never staged), not in PrivateHeaders/.
            var (moduleDir, publicIface, _) = BuildSliceFixture("Demo", includePrivateInterface: true, includeBinary: true);
            var realFrameworkDir = Path.GetDirectoryName(Path.GetDirectoryName(moduleDir))!;
            Directory.CreateDirectory(Path.Combine(realFrameworkDir, "Headers"));
            Directory.CreateDirectory(Path.Combine(realFrameworkDir, "PrivateHeaders"));
            File.WriteAllText(Path.Combine(realFrameworkDir, "Headers", "Demo.h"),
                "#import <Foundation/Foundation.h>\n");
            File.WriteAllText(Path.Combine(realFrameworkDir, "PrivateHeaders", "demo_config.h"),
                "#define DEMO_CONFIG 1\n");
            File.WriteAllText(Path.Combine(realFrameworkDir, "Modules", "module.modulemap"),
                "framework module Demo {\n  umbrella header \"Demo.h\"\n  header \"demo_config.h\"\n  export *\n}\n");

            var buildDir = Path.Combine(Path.GetTempPath(), $"shadow_build_{Guid.NewGuid():N}");
            Directory.CreateDirectory(buildDir);

            var shadow = SwiftWrapperCompiler.PrecompileSanitizedShadowFramework(
                "Demo", publicIface, "arm64-apple-ios17.0-simulator", "/sdk",
                buildDir, new MockCommandRunner(), NullLogger.Instance,
                collisions: Array.Empty<SwiftWrapperCompiler.CollisionPatchTarget>());

            Assert.NotNull(shadow);
            var shadowFrameworkDir = Path.Combine(shadow!, "Demo.framework");
            Assert.True(File.Exists(Path.Combine(shadowFrameworkDir, "Headers", "Demo.h")));
            Assert.True(File.Exists(Path.Combine(shadowFrameworkDir, "PrivateHeaders", "demo_config.h")));
        }

        [Fact]
        public void PrivateInterface_PrivateModulemapNotStaged()
        {
            // The private modulemap exposes @_spi / private headers — that's the surface we're
            // sanitizing. Even when the source has both module.modulemap and
            // module.private.modulemap, the shadow must only carry the public one.
            var (moduleDir, publicIface, _) = BuildSliceFixture("Demo", includePrivateInterface: true, includeBinary: true);
            var realFrameworkDir = Path.GetDirectoryName(Path.GetDirectoryName(moduleDir))!;
            Directory.CreateDirectory(Path.Combine(realFrameworkDir, "Headers"));
            File.WriteAllText(Path.Combine(realFrameworkDir, "Headers", "Demo.h"),
                "#import <Foundation/Foundation.h>\n");
            File.WriteAllText(Path.Combine(realFrameworkDir, "Modules", "module.modulemap"),
                "framework module Demo {\n  umbrella header \"Demo.h\"\n  export *\n}\n");
            File.WriteAllText(Path.Combine(realFrameworkDir, "Modules", "module.private.modulemap"),
                "framework module Demo_Private {\n  header \"PrivateHeader.h\"\n  export *\n}\n");

            var buildDir = Path.Combine(Path.GetTempPath(), $"shadow_build_{Guid.NewGuid():N}");
            Directory.CreateDirectory(buildDir);

            var shadow = SwiftWrapperCompiler.PrecompileSanitizedShadowFramework(
                "Demo", publicIface, "arm64-apple-ios17.0-simulator", "/sdk",
                buildDir, new MockCommandRunner(), NullLogger.Instance,
                collisions: Array.Empty<SwiftWrapperCompiler.CollisionPatchTarget>());

            Assert.NotNull(shadow);
            var shadowModulesDir = Path.Combine(shadow!, "Demo.framework", "Modules");
            Assert.True(File.Exists(Path.Combine(shadowModulesDir, "module.modulemap")));
            Assert.False(File.Exists(Path.Combine(shadowModulesDir, "module.private.modulemap")));
        }
    }

    #endregion

    #region Force-Load Symbol Export (real toolchain — Gap 2 single-registration)

    /// <summary>
    /// Gap 2's load-bearing claim is that force-loading a static <c>ar</c> archive into the
    /// Swift wrapper makes the wrapper DEFINE and EXPORT every <c>_OBJC_CLASS_$_*</c> the
    /// archive carries — even classes the wrapper code never references — so the wrapper can be
    /// the sole runtime carrier and the consumer can drop its redundant source NativeReference
    /// without losing those classes. The arg-shape tests in
    /// <see cref="SwiftWrapperCompilerInvocationTests"/> prove the <c>-force_load</c> flag is
    /// emitted; these prove it actually changes the linked binary, which is the part the shipped
    /// fix had no end-to-end coverage for. The static-vs-dynamic <em>decision</em> that gates
    /// force_load is covered by <see cref="NativeLinkageProbeTests"/>.
    ///
    /// <para>
    /// Real toolchain: a tiny static ObjC archive is built with <c>clang</c> + <c>ar</c>, the
    /// wrapper is compiled through the production <see cref="SwiftWrapperCompiler.InvokeSwiftCompiler"/>,
    /// and the produced dylib is inspected with <c>nm -gU</c> (the same evidence the resolver's
    /// TBD synthesis relies on). macOS + an iOS-simulator SDK only; skipped elsewhere, matching
    /// the <c>XCFrameworkSlicer</c>/<c>WrapperXCFrameworkMerger</c> integration tests.
    /// </para>
    /// </summary>
    public class SwiftWrapperForceLoadSymbolExportTests
    {
        private static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        // Name is deliberately unique so a stray symbol from the SDK / Foundation can never
        // satisfy the assertion by accident.
        private const string ProbeClass = "SwiftBindGap2ForceLoadProbe";
        private const string ProbeClassSymbol = "_OBJC_CLASS_$_" + ProbeClass;

        [Fact]
        public void ForceLoad_StaticArchive_ExportsUnreferencedObjCClassFromWrapper()
        {
            if (!IsMacOS) return;

            using var env = ToolchainProbeEnv.TryCreate(ProbeClass);
            if (env is null) return; // no iphonesimulator SDK / xcrun available — environmental skip

            // POSITIVE — the production force_load path pulls the ObjC class (which the trivial
            // wrapper never references) into the wrapper and exports it.
            var withForceLoad = env.CompileWrapper(forceLoad: true);
            var exportsWith = NmDefinedGlobals(withForceLoad);
            Assert.Contains(ProbeClassSymbol, exportsWith);

            // NEGATIVE control — identical inputs, no force_load. The wrapper references nothing
            // in the archive, so `ar` lazy resolution pulls no members and the class is absent.
            // The contrast proves the export is *caused by* force_load, not by Swift auto-linking
            // or some incidental reference — i.e. the consumer can only safely drop its source
            // reference because force_load is what carries the class.
            var withoutForceLoad = env.CompileWrapper(forceLoad: false);
            var exportsWithout = NmDefinedGlobals(withoutForceLoad);
            Assert.DoesNotContain(ProbeClassSymbol, exportsWithout);
        }

        private static string NmDefinedGlobals(string binary)
        {
            var (exit, stdout, stderr) = new SystemCommandRunner().Run("nm", $"-gU \"{binary}\"", timeoutMs: 30000);
            Assert.True(exit == 0, $"nm -gU failed for {binary} (exit {exit}): {stderr}");
            return stdout;
        }

        /// <summary>
        /// A disposable scratch directory holding a real single-arch static ObjC archive plus a
        /// trivial Swift wrapper source, with the resolved simulator SDK and target triple. Returns
        /// <c>null</c> from <see cref="TryCreate"/> when the simulator SDK or a build step is
        /// unavailable, so the test skips cleanly on a toolchain-less host instead of failing.
        /// </summary>
        private sealed class ToolchainProbeEnv : IDisposable
        {
            private readonly string _dir;
            private readonly string _sdkPath;
            private readonly string _targetTriple;
            private readonly string _archivePath;
            private readonly string _wrapperSwift;
            private int _compileCounter;

            private ToolchainProbeEnv(
                string dir, string sdkPath, string targetTriple, string archivePath, string wrapperSwift)
            {
                _dir = dir;
                _sdkPath = sdkPath;
                _targetTriple = targetTriple;
                _archivePath = archivePath;
                _wrapperSwift = wrapperSwift;
            }

            public static ToolchainProbeEnv? TryCreate(string probeClass)
            {
                var runner = new SystemCommandRunner();

                // Resolve the iOS-simulator SDK so the archive and the wrapper target the same
                // platform; force_load across platforms would fail to link.
                var (sdkExit, sdkOut, _) = runner.Run("xcrun", "--sdk iphonesimulator --show-sdk-path", timeoutMs: 30000);
                var sdkPath = sdkOut?.Trim();
                if (sdkExit != 0 || string.IsNullOrEmpty(sdkPath) || !Directory.Exists(sdkPath))
                    return null;

                var arch = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x86_64" : "arm64";
                var targetTriple = $"{arch}-apple-ios15.0-simulator";

                var dir = Path.Combine(Path.GetTempPath(), $"gap2_forceload_{Guid.NewGuid():N}");
                Directory.CreateDirectory(dir);

                try
                {
                    // A static archive carrying one ObjC class that the wrapper never references.
                    var probeM = Path.Combine(dir, "probe.m");
                    File.WriteAllText(probeM,
                        "#import <Foundation/Foundation.h>\n" +
                        $"@interface {probeClass} : NSObject\n@end\n" +
                        $"@implementation {probeClass}\n@end\n");

                    var probeO = Path.Combine(dir, "probe.o");
                    var (clangExit, _, clangErr) = runner.Run("xcrun",
                        $"clang -x objective-c -c -target {targetTriple} -isysroot \"{sdkPath}\" " +
                        $"-fobjc-arc \"{probeM}\" -o \"{probeO}\"", timeoutMs: 60000);
                    if (clangExit != 0 || !File.Exists(probeO))
                    {
                        // Toolchain present but the probe didn't build — treat as environmental.
                        return CleanupAndNull(dir);
                    }

                    var archivePath = Path.Combine(dir, "libGap2Probe.a");
                    var (arExit, _, _) = runner.Run("xcrun", $"ar rcs \"{archivePath}\" \"{probeO}\"", timeoutMs: 30000);
                    if (arExit != 0 || !File.Exists(archivePath))
                        return CleanupAndNull(dir);

                    // The wrapper hard-references Foundation/NSObject so Swift auto-links Foundation,
                    // resolving the archive's NSObject superclass reference at link time. It does NOT
                    // reference the probe class — that's the whole point of the lazy-vs-force_load contrast.
                    var wrapperSwift = Path.Combine(dir, "Gap2Probe.swift");
                    File.WriteAllText(wrapperSwift,
                        "import Foundation\npublic func gap2GateProbe() -> AnyObject { return NSObject() }\n");

                    return new ToolchainProbeEnv(dir, sdkPath, targetTriple, archivePath, wrapperSwift);
                }
                catch
                {
                    return CleanupAndNull(dir);
                }
            }

            /// <summary>Compiles the wrapper dylib, with or without force-loading the probe archive.</summary>
            public string CompileWrapper(bool forceLoad)
            {
                var outBin = Path.Combine(_dir, $"wrapper{_compileCounter++}.dylib");
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    new List<string> { _wrapperSwift },
                    outBin,
                    "Gap2ProbeBindings",
                    _targetTriple,
                    _sdkPath,
                    _dir, // -F search path (real directory)
                    new SystemCommandRunner(),
                    NullLogger.Instance,
                    forceLoadBinaries: forceLoad ? new[] { _archivePath } : null);
                Assert.True(File.Exists(outBin), "wrapper dylib was not produced");
                return outBin;
            }

            private static ToolchainProbeEnv? CleanupAndNull(string dir)
            {
                try { Directory.Delete(dir, true); } catch { /* best effort */ }
                return null;
            }

            public void Dispose()
            {
                try { Directory.Delete(_dir, true); } catch { /* best effort */ }
            }
        }
    }

    #endregion

    #region N. Slice-binary validation, atomic promote, and stranded-superseded recovery

    /// <summary>
    /// Direct behavioral coverage for the timeout-hardening staging pipeline: a half-written or
    /// wrong-arch slice binary must be rejected (never promoted to the canonical xcframework), the
    /// staging→canonical promote must be a recoverable swap, and a tree stranded at <c>.superseded</c>
    /// by a crash between the two renames must be recovered on the next build. These exercise the
    /// filesystem behavior directly rather than through a happy-path compile mock.
    /// </summary>
    public class SwiftWrapperStagingPromoteTests
    {
        private static readonly byte[] MachOMagic64Le = { 0xCF, 0xFA, 0xED, 0xFE, 0x00, 0x00, 0x00, 0x00 };

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"swc_promote_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void WriteMachO(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, MachOMagic64Le);
        }

        // --- ValidateCompiledSliceBinary ---

        [Fact]
        public void ValidateCompiledSliceBinary_MissingBinary_Throws()
        {
            var dir = CreateTempDir();
            try
            {
                var binary = Path.Combine(dir, "missing.dylib");
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    SwiftWrapperCompiler.ValidateCompiledSliceBinary(
                        binary, "arm64", new MockCommandRunner(), NullLogger.Instance));
                Assert.Contains("missing or empty", ex.Message);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ValidateCompiledSliceBinary_EmptyBinary_Throws()
        {
            var dir = CreateTempDir();
            try
            {
                var binary = Path.Combine(dir, "empty.dylib");
                File.WriteAllBytes(binary, Array.Empty<byte>());
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    SwiftWrapperCompiler.ValidateCompiledSliceBinary(
                        binary, "arm64", new MockCommandRunner(), NullLogger.Instance));
                Assert.Contains("missing or empty", ex.Message);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ValidateCompiledSliceBinary_NotMachO_Throws()
        {
            var dir = CreateTempDir();
            try
            {
                var binary = Path.Combine(dir, "garbage.dylib");
                File.WriteAllText(binary, "this is not a mach-o image, it is plain text");
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    SwiftWrapperCompiler.ValidateCompiledSliceBinary(
                        binary, "arm64", new MockCommandRunner(), NullLogger.Instance));
                Assert.Contains("not a valid Mach-O", ex.Message);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ValidateCompiledSliceBinary_WrongArch_Throws()
        {
            var dir = CreateTempDir();
            try
            {
                var binary = Path.Combine(dir, "thin.dylib");
                WriteMachO(binary);
                var runner = new MockCommandRunner();
                runner.SetResponse("-archs", 0, "arm64"); // real binary is arm64-only
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    SwiftWrapperCompiler.ValidateCompiledSliceBinary(
                        binary, "x86_64", runner, NullLogger.Instance)); // but x86_64 was expected
                Assert.Contains("missing the expected architecture", ex.Message);
                Assert.Contains("x86_64", ex.Message);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ValidateCompiledSliceBinary_MachOWithExpectedArch_DoesNotThrow()
        {
            var dir = CreateTempDir();
            try
            {
                var binary = Path.Combine(dir, "thin.dylib");
                WriteMachO(binary);
                var runner = new MockCommandRunner();
                runner.SetResponse("-archs", 0, "arm64 x86_64");
                // No throw == pass.
                SwiftWrapperCompiler.ValidateCompiledSliceBinary(
                    binary, "arm64", runner, NullLogger.Instance);
            }
            finally { Directory.Delete(dir, true); }
        }

        // --- PromoteStagedXcframework ---

        [Fact]
        public void PromoteStagedXcframework_NoPriorCanonical_MovesStagingIntoPlace()
        {
            var dir = CreateTempDir();
            try
            {
                var canonical = Path.Combine(dir, "Module.xcframework");
                var staging = canonical + ".staging";
                Directory.CreateDirectory(staging);
                File.WriteAllText(Path.Combine(staging, "marker.txt"), "staged");

                SwiftWrapperCompiler.PromoteStagedXcframework(staging, canonical, NullLogger.Instance);

                Assert.True(File.Exists(Path.Combine(canonical, "marker.txt")));
                Assert.False(Directory.Exists(staging));
                Assert.False(Directory.Exists(canonical + ".superseded"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void PromoteStagedXcframework_PriorCanonical_ReplacedAndSupersededCleaned()
        {
            var dir = CreateTempDir();
            try
            {
                var canonical = Path.Combine(dir, "Module.xcframework");
                var staging = canonical + ".staging";
                Directory.CreateDirectory(canonical);
                File.WriteAllText(Path.Combine(canonical, "old.txt"), "old");
                Directory.CreateDirectory(staging);
                File.WriteAllText(Path.Combine(staging, "new.txt"), "new");

                SwiftWrapperCompiler.PromoteStagedXcframework(staging, canonical, NullLogger.Instance);

                Assert.True(File.Exists(Path.Combine(canonical, "new.txt")));
                Assert.False(File.Exists(Path.Combine(canonical, "old.txt"))); // prior tree replaced
                Assert.False(Directory.Exists(staging));
                Assert.False(Directory.Exists(canonical + ".superseded")); // parked tree cleaned up
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void PromoteStagedXcframework_StaleSuperseded_PrecleanedThenPromotes()
        {
            var dir = CreateTempDir();
            try
            {
                var canonical = Path.Combine(dir, "Module.xcframework");
                var staging = canonical + ".staging";
                var superseded = canonical + ".superseded";
                Directory.CreateDirectory(superseded); // leftover from a prior interrupted promote
                File.WriteAllText(Path.Combine(superseded, "stale.txt"), "stale");
                Directory.CreateDirectory(canonical);
                File.WriteAllText(Path.Combine(canonical, "old.txt"), "old");
                Directory.CreateDirectory(staging);
                File.WriteAllText(Path.Combine(staging, "new.txt"), "new");

                SwiftWrapperCompiler.PromoteStagedXcframework(staging, canonical, NullLogger.Instance);

                Assert.True(File.Exists(Path.Combine(canonical, "new.txt")));
                Assert.False(Directory.Exists(superseded));
                Assert.False(Directory.Exists(staging));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void PromoteStagedXcframework_MoveInFails_RollsBackPriorCanonical()
        {
            var dir = CreateTempDir();
            try
            {
                var canonical = Path.Combine(dir, "Module.xcframework");
                var staging = canonical + ".staging"; // deliberately never created → move-in fails
                Directory.CreateDirectory(canonical);
                File.WriteAllText(Path.Combine(canonical, "old.txt"), "old");

                Assert.ThrowsAny<Exception>(() =>
                    SwiftWrapperCompiler.PromoteStagedXcframework(staging, canonical, NullLogger.Instance));

                // The prior good tree is rolled back from .superseded, never destroyed.
                Assert.True(File.Exists(Path.Combine(canonical, "old.txt")));
                Assert.False(Directory.Exists(canonical + ".superseded"));
            }
            finally { Directory.Delete(dir, true); }
        }

        // --- RecoverStrandedSupersededXcframework ---

        [Fact]
        public void RecoverStrandedSupersededXcframework_CanonicalAbsent_RestoresFromSuperseded()
        {
            var dir = CreateTempDir();
            try
            {
                var canonical = Path.Combine(dir, "Module.xcframework");
                var superseded = canonical + ".superseded";
                Directory.CreateDirectory(superseded); // crash left the tree parked here
                File.WriteAllText(Path.Combine(superseded, "marker.txt"), "parked");

                SwiftWrapperCompiler.RecoverStrandedSupersededXcframework(canonical, NullLogger.Instance);

                Assert.True(File.Exists(Path.Combine(canonical, "marker.txt")));
                Assert.False(Directory.Exists(superseded));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void RecoverStrandedSupersededXcframework_CanonicalPresent_DropsStaleSuperseded()
        {
            var dir = CreateTempDir();
            try
            {
                var canonical = Path.Combine(dir, "Module.xcframework");
                var superseded = canonical + ".superseded";
                Directory.CreateDirectory(canonical);
                File.WriteAllText(Path.Combine(canonical, "current.txt"), "current");
                Directory.CreateDirectory(superseded);
                File.WriteAllText(Path.Combine(superseded, "stale.txt"), "stale");

                SwiftWrapperCompiler.RecoverStrandedSupersededXcframework(canonical, NullLogger.Instance);

                Assert.True(File.Exists(Path.Combine(canonical, "current.txt"))); // canonical untouched
                Assert.False(Directory.Exists(superseded)); // stale parked tree dropped
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void RecoverStrandedSupersededXcframework_NoSuperseded_IsNoOp()
        {
            var dir = CreateTempDir();
            try
            {
                var canonical = Path.Combine(dir, "Module.xcframework");
                Directory.CreateDirectory(canonical);
                File.WriteAllText(Path.Combine(canonical, "current.txt"), "current");

                // No throw, no mutation.
                SwiftWrapperCompiler.RecoverStrandedSupersededXcframework(canonical, NullLogger.Instance);

                Assert.True(File.Exists(Path.Combine(canonical, "current.txt")));
                Assert.False(Directory.Exists(canonical + ".superseded"));
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    #endregion
}
