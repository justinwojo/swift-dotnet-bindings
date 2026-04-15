// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

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
                File.WriteAllText(Path.Combine(dir, "Nuke.SwiftUIBridge.swift"), "// bridge1");
                File.WriteAllText(Path.Combine(dir, "Lottie.SwiftUIBridge.swift"), "// bridge2");
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
                        <string>BlinkID</string>
                    </dict>
                    </plist>
                    """;
                File.WriteAllText(Path.Combine(dir, "Info.plist"), plist);
                var dylibPath = Path.Combine(dir, "BlinkID");
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
                var xcfwPath = Path.Combine(dir, "NukeSwiftBindings.xcframework");
                var fwDir = Path.Combine(xcfwPath, "ios-arm64-simulator", "NukeSwiftBindings.framework");

                SwiftWrapperCompiler.CreateXCFrameworkStructure(xcfwPath, fwDir, "NukeSwiftBindings", "16.0");

                var fwPlist = File.ReadAllText(Path.Combine(fwDir, "Info.plist"));
                Assert.Contains("NukeSwiftBindings", fwPlist);
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
        public void InvokeSwiftCompiler_LongStderr_TruncatesAt2000Chars()
        {
            var runner = new MockCommandRunner();
            var longError = new string('x', 3000);
            runner.SetResponse("swiftc", 1, "", longError);

            var files = new List<string> { "/tmp/a.swift" };
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SwiftWrapperCompiler.InvokeSwiftCompiler(
                    files, "/tmp/out/Binary", "TestSwiftBindings",
                    "arm64-apple-ios15.0-simulator", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance));
            // Should truncate at 2000 + "..."
            Assert.Contains("...", ex.Message);
            // The error preview in the message should be at most 2003 chars (2000 + "...")
            // but the full message includes the prefix text too
            Assert.True(ex.Message.Length < longError.Length + 100);
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
                    "import SwiftyBeaver\n" +
                    "public typealias LogLevel = SwiftyBeaver.Level\n" +
                    "public class SwiftyBeaver {\n" +
                    "  public func setDestination(_ dest: SwiftyBeaver.ConsoleDestination) {}\n" +
                    "}\n");

                var pattern = new System.Text.RegularExpressions.Regex(
                    @"\bSwiftyBeaver\.(\w+(?:\.\w+)*)",
                    System.Text.RegularExpressions.RegexOptions.Compiled);
                var nested = new HashSet<string> { "Level" };

                // Use reflection to call the private static method
                var method = typeof(SwiftWrapperCompiler).GetMethod("PatchSwiftInterface",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                method.Invoke(null, new object?[] { source, dest, pattern, nested });

                var result = File.ReadAllText(dest);
                // Level is nested — should be preserved
                Assert.Contains("SwiftyBeaver.Level", result);
                // ConsoleDestination is NOT nested — should be stripped
                Assert.Contains("dest: ConsoleDestination", result);
                Assert.DoesNotContain("SwiftyBeaver.ConsoleDestination", result);
                // import line should be untouched
                Assert.Contains("import SwiftyBeaver", result);
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
                    "import Reachability\n" +
                    "public class Reachability {\n" +
                    "  public var connection: Reachability.Connection\n" +
                    "}\n");

                var pattern = new System.Text.RegularExpressions.Regex(
                    @"\bReachability\.(\w+(?:\.\w+)*)",
                    System.Text.RegularExpressions.RegexOptions.Compiled);

                var method = typeof(SwiftWrapperCompiler).GetMethod("PatchSwiftInterface",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                method.Invoke(null, new object?[] { source, dest, pattern, null });

                var result = File.ReadAllText(dest);
                Assert.DoesNotContain("Reachability.Connection", result);
                Assert.Contains("connection: Connection", result);
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
            var stderr = "/tmp/wrapper.swift:1:8: error: no such module 'Stripe3DS2'\nimport Stripe3DS2\n       ^";
            var modules = SwiftWrapperCompiler.ExtractMissingModules(stderr);
            Assert.Single(modules);
            Assert.Equal("Stripe3DS2", modules[0]);
        }

        [Fact]
        public void ExtractMissingModules_MultipleModules_ReturnsDistinctNames()
        {
            var stderr = "error: no such module 'StripeCore'\nerror: no such module 'Stripe3DS2'\nerror: no such module 'StripeCore'";
            var modules = SwiftWrapperCompiler.ExtractMissingModules(stderr);
            Assert.Equal(2, modules.Count);
            Assert.Contains("StripeCore", modules);
            Assert.Contains("Stripe3DS2", modules);
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
            var stderr = "error: no such module 'FirebaseAuth'\n" +
                         "error: cannot find type 'FIRAuth'\n" +
                         "error: no such module 'FirebaseCore'\n" +
                         "error: value of type 'Foo' has no member 'bar'";
            var modules = SwiftWrapperCompiler.ExtractMissingModules(stderr);
            Assert.Equal(2, modules.Count);
            Assert.Equal("FirebaseAuth", modules[0]);
            Assert.Equal("FirebaseCore", modules[1]);
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
                var path = Path.Combine(dir, "Nuke.swiftinterface");
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
}
