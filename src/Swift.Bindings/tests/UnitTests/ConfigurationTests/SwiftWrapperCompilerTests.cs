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
            Assert.Contains("Xcode and iOS SDK", ex.Message);
        }

        [Fact]
        public void ResolveSdkPath_EmptyOutput_Throws()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("--show-sdk-path", 0, "");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                SwiftWrapperCompiler.ResolveSdkPath(runner));
            Assert.Contains("Xcode and iOS SDK", ex.Message);
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
                "15.0", "/sdk/path", "/fw/search",
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
                    "15.0", "/sdk/path", "/fw/search",
                    runner, NullLogger.Instance));
            Assert.Contains("compilation failed", ex.Message);
            Assert.Contains("cannot find module", ex.Message);
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
                // Write a Swift file that will be entirely stripped
                File.WriteAllText(Path.Combine(dir, "Swift.Module.swift"), """
                    class EveryProtocol {
                        var x: Int = 0
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
}
