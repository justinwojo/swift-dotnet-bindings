// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    #region A. Assembly File Collection Tests

    public class NativeThunkFileCollectionTests
    {
        [Fact]
        public void CollectAssemblyFiles_FindsArm64SFiles()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Nuke.arm64.s"), ".globl _thunk_test\n_thunk_test:\n  ret");
                File.WriteAllText(Path.Combine(dir, "Nuke2.arm64.s"), ".globl _thunk_test2\n_thunk_test2:\n  ret");
                var files = NativeThunkCompiler.CollectAssemblyFiles(dir);
                Assert.Equal(2, files.Count);
                Assert.All(files, f => Assert.EndsWith(".arm64.s", f));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CollectAssemblyFiles_IgnoresNonArm64SFiles()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Nuke.arm64.s"), "thunk code");
                File.WriteAllText(Path.Combine(dir, "Nuke.swift"), "swift code");
                File.WriteAllText(Path.Combine(dir, "Nuke.cs"), "csharp code");
                File.WriteAllText(Path.Combine(dir, "Nuke.s"), "other asm");
                var files = NativeThunkCompiler.CollectAssemblyFiles(dir);
                Assert.Single(files);
                Assert.Contains("Nuke.arm64.s", files[0]);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CollectAssemblyFiles_EmptyDirectory_ReturnsEmpty()
        {
            var dir = CreateTempDir();
            try
            {
                var files = NativeThunkCompiler.CollectAssemblyFiles(dir);
                Assert.Empty(files);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CollectAssemblyFiles_NonexistentDirectory_ReturnsEmpty()
        {
            var files = NativeThunkCompiler.CollectAssemblyFiles("/nonexistent/path");
            Assert.Empty(files);
        }

        [Fact]
        public void CollectAssemblyFiles_ReturnsSorted()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Zebra.arm64.s"), "code");
                File.WriteAllText(Path.Combine(dir, "Alpha.arm64.s"), "code");
                File.WriteAllText(Path.Combine(dir, "Middle.arm64.s"), "code");
                var files = NativeThunkCompiler.CollectAssemblyFiles(dir);
                Assert.Equal(3, files.Count);
                Assert.Contains("Alpha.arm64.s", files[0]);
                Assert.Contains("Middle.arm64.s", files[1]);
                Assert.Contains("Zebra.arm64.s", files[2]);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"thunk_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region B. Thunk Compilation Tests

    public class NativeThunkCompilationTests
    {
        [Fact]
        public void CompileThunkObjects_NoAssemblyFiles_ReturnsNull()
        {
            var dir = CreateTempDir();
            try
            {
                var result = NativeThunkCompiler.CompileThunkObjects(
                    dir, "arm64-apple-ios17.0-simulator", "/sdk/path",
                    NullLogger.Instance);
                Assert.Null(result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CompileThunkObjects_InvokesClangPerFile()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Module.arm64.s"), ".globl _thunk\n_thunk:\n  ret");
                File.WriteAllText(Path.Combine(dir, "Module2.arm64.s"), ".globl _thunk2\n_thunk2:\n  ret");

                var mockRunner = new MockCommandRunner();
                mockRunner.SetResponse("clang -c", 0, "", "");

                var result = NativeThunkCompiler.CompileThunkObjects(
                    dir, "arm64-apple-ios17.0-simulator", "/sdk/path",
                    NullLogger.Instance, mockRunner);

                Assert.NotNull(result);
                Assert.Equal(2, result!.CompiledFileCount);

                // Verify clang was invoked for each file
                var clangCalls = mockRunner.Invocations.Where(
                    i => i.Arguments.Contains("clang -c")).ToList();
                Assert.Equal(2, clangCalls.Count);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CompileThunkObjects_PassesCorrectTargetTriple()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Test.arm64.s"), ".globl _thunk\n_thunk:\n  ret");

                var mockRunner = new MockCommandRunner();
                mockRunner.SetResponse("clang -c", 0, "", "");

                var targetTriple = "arm64-apple-ios17.0-simulator";
                NativeThunkCompiler.CompileThunkObjects(
                    dir, targetTriple, "/sdk/path",
                    NullLogger.Instance, mockRunner);

                var call = mockRunner.Invocations.First(i => i.Arguments.Contains("clang -c"));
                Assert.Contains($"-target {targetTriple}", call.Arguments);
                Assert.Contains("-isysroot \"/sdk/path\"", call.Arguments);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CompileThunkObjects_OutputsCorrectObjectFilePaths()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Module.arm64.s"), "asm");

                var mockRunner = new MockCommandRunner();
                mockRunner.SetResponse("clang -c", 0, "", "");

                var result = NativeThunkCompiler.CompileThunkObjects(
                    dir, "arm64-apple-ios17.0-simulator", "/sdk/path",
                    NullLogger.Instance, mockRunner);

                Assert.NotNull(result);
                Assert.Single(result!.ObjectFiles);
                Assert.EndsWith(".o", result.ObjectFiles[0]);
                // Object file should be derived from source: Module.arm64.o
                Assert.Contains("Module.arm64.o", result.ObjectFiles[0]);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CompileThunkObjects_ThrowsOnClangFailure()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Bad.arm64.s"), "invalid asm");

                var mockRunner = new MockCommandRunner();
                mockRunner.SetResponse("clang -c", 1, "", "error: invalid instruction");

                var ex = Assert.Throws<InvalidOperationException>(() =>
                    NativeThunkCompiler.CompileThunkObjects(
                        dir, "arm64-apple-ios17.0-simulator", "/sdk/path",
                        NullLogger.Instance, mockRunner));

                Assert.Contains("Thunk assembly compilation failed", ex.Message);
                Assert.Contains("Bad.arm64.s", ex.Message);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"thunk_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region C. Clang Linking Tests

    public class NativeThunkLinkingTests
    {
        [Fact]
        public void LinkWithClang_InvokesClangShared()
        {
            var mockRunner = new MockCommandRunner();
            mockRunner.SetResponse("clang -shared", 0, "", "");

            NativeThunkCompiler.LinkWithClang(
                new[] { "/tmp/thunk1.o", "/tmp/thunk2.o" },
                "/tmp/output/binary",
                "TestSwiftBindings",
                "arm64-apple-ios17.0-simulator",
                "/sdk/path",
                mockRunner,
                NullLogger.Instance);

            var call = mockRunner.Invocations.First(i => i.Arguments.Contains("clang -shared"));
            Assert.Contains("-target arm64-apple-ios17.0-simulator", call.Arguments);
            Assert.Contains("-isysroot \"/sdk/path\"", call.Arguments);
            Assert.Contains("-install_name @rpath/TestSwiftBindings.framework/TestSwiftBindings", call.Arguments);
            Assert.Contains("-o \"/tmp/output/binary\"", call.Arguments);
            Assert.Contains("/tmp/thunk1.o", call.Arguments);
            Assert.Contains("/tmp/thunk2.o", call.Arguments);
        }

        [Fact]
        public void LinkWithClang_ThrowsOnFailure()
        {
            var mockRunner = new MockCommandRunner();
            mockRunner.SetResponse("clang -shared", 1, "", "ld: error: undefined symbols");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                NativeThunkCompiler.LinkWithClang(
                    new[] { "/tmp/thunk.o" },
                    "/tmp/output/binary",
                    "TestSwiftBindings",
                    "arm64-apple-ios17.0-simulator",
                    "/sdk/path",
                    mockRunner,
                    NullLogger.Instance));

            Assert.Contains("Thunk linking failed", ex.Message);
        }
    }

    #endregion

    #region D. SwiftWrapperCompiler Thunk Integration Tests

    public class SwiftWrapperThunkIntegrationTests
    {
        [Fact]
        public void CompileSlice_WithThunkAssembly_CompilesThunksBeforeSwiftc()
        {
            // Verify that when .arm64.s files exist alongside .swift files,
            // both clang (thunk compile) and swiftc (wrapper compile) are invoked
            var dir = CreateTempDir();
            try
            {
                // Create Swift file + assembly file
                File.WriteAllText(Path.Combine(dir, "Wrapper.swift"), "import Foundation\n@_cdecl(\"test\") func test() {}");
                File.WriteAllText(Path.Combine(dir, "Module.arm64.s"), ".globl _thunk_test\n_thunk_test:\n  ret");

                // Create dylib with Info.plist for deployment target
                var dylibPath = Path.Combine(dir, "source.framework", "Source");
                Directory.CreateDirectory(Path.GetDirectoryName(dylibPath)!);
                File.WriteAllText(dylibPath, "");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(dylibPath)!, "Info.plist"),
                    "<?xml version=\"1.0\"?><plist><dict><key>MinimumOSVersion</key><string>16.0</string></dict></plist>");

                var mockRunner = new MockCommandRunner();
                mockRunner.SetResponse("--show-sdk-path", 0, "/Applications/Xcode.app/Contents/Developer/Platforms/iPhoneSimulator.platform/Developer/SDKs/iPhoneSimulator.sdk");
                mockRunner.SetResponse("clang -c", 0, "", "");
                mockRunner.SetResponse("swiftc", 0, "", "");
                mockRunner.SetResponse("plutil", 0, "{\"MinimumOSVersion\":\"16.0\"}", "");

                var slice = new SliceVariant
                {
                    Platform = ApplePlatform.iOS,
                    IsSimulator = true,
                    SdkName = "iphonesimulator",
                    SliceId = "ios-arm64-simulator",
                    PlistPlatformName = "iPhoneSimulator",
                    XCFrameworkPlatformString = "ios",
                    XCFrameworkPlatformVariant = "simulator"
                };

                var result = SwiftWrapperCompiler.CompileSlice(
                    dir, "Test",
                    dir, dylibPath,
                    slice, NullLogger.Instance,
                    commandRunner: mockRunner);

                // Should have invoked clang for thunk compilation
                Assert.Contains(mockRunner.Invocations, i => i.Arguments.Contains("clang -c"));
                // Should have invoked swiftc for wrapper compilation
                Assert.Contains(mockRunner.Invocations, i => i.Arguments.Contains("swiftc"));
                // The swiftc call should include the .o file
                var swiftcCall = mockRunner.Invocations.First(i => i.Arguments.Contains("swiftc"));
                Assert.Contains(".o", swiftcCall.Arguments);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CompileSlice_NoSwiftNoThunks_ReturnsNull()
        {
            var dir = CreateTempDir();
            try
            {
                var result = SwiftWrapperCompiler.CompileSlice(
                    dir, "Test", dir, Path.Combine(dir, "dylib"),
                    new SliceVariant
                    {
                        Platform = ApplePlatform.iOS, IsSimulator = true,
                        SdkName = "iphonesimulator", SliceId = "ios-arm64-simulator",
                        PlistPlatformName = "iPhoneSimulator",
                        XCFrameworkPlatformString = "ios", XCFrameworkPlatformVariant = "simulator"
                    },
                    NullLogger.Instance);

                Assert.Null(result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CompileSlice_SkipThunkCompilation_IgnoresAssemblyFiles()
        {
            var dir = CreateTempDir();
            try
            {
                // Only assembly files, no Swift — with skipThunkCompilation=true, should return null
                File.WriteAllText(Path.Combine(dir, "Module.arm64.s"), ".globl _thunk\n_thunk:\n  ret");

                var result = SwiftWrapperCompiler.CompileSlice(
                    dir, "Test", dir, Path.Combine(dir, "dylib"),
                    new SliceVariant
                    {
                        Platform = ApplePlatform.iOS, IsSimulator = true,
                        SdkName = "iphonesimulator", SliceId = "ios-arm64-simulator",
                        PlistPlatformName = "iPhoneSimulator",
                        XCFrameworkPlatformString = "ios", XCFrameworkPlatformVariant = "simulator"
                    },
                    NullLogger.Instance,
                    skipThunkCompilation: true);

                Assert.Null(result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void InvokeSwiftCompiler_WithThunkObjects_AppendsToFileArgs()
        {
            var mockRunner = new MockCommandRunner();
            mockRunner.SetResponse("swiftc", 0, "", "");

            var swiftFiles = new List<string> { "/tmp/wrapper.swift" };
            var thunkObjects = new List<string> { "/tmp/thunk1.o", "/tmp/thunk2.o" };

            SwiftWrapperCompiler.InvokeSwiftCompiler(
                swiftFiles, "/tmp/output/binary", "TestSwiftBindings",
                "arm64-apple-ios17.0-simulator",
                "/Applications/Xcode.app/SDKs/iPhoneSimulator.sdk",
                "/tmp/framework",
                mockRunner, NullLogger.Instance,
                thunkObjectFiles: thunkObjects);

            var call = mockRunner.Invocations.First(i => i.Arguments.Contains("swiftc"));
            Assert.Contains("/tmp/thunk1.o", call.Arguments);
            Assert.Contains("/tmp/thunk2.o", call.Arguments);
            Assert.Contains("/tmp/wrapper.swift", call.Arguments);
        }

        [Fact]
        public void InvokeSwiftCompiler_NoThunkObjects_WorksNormally()
        {
            var mockRunner = new MockCommandRunner();
            mockRunner.SetResponse("swiftc", 0, "", "");

            var swiftFiles = new List<string> { "/tmp/wrapper.swift" };

            SwiftWrapperCompiler.InvokeSwiftCompiler(
                swiftFiles, "/tmp/output/binary", "TestSwiftBindings",
                "arm64-apple-ios17.0-simulator",
                "/Applications/Xcode.app/SDKs/iPhoneSimulator.sdk",
                "/tmp/framework",
                mockRunner, NullLogger.Instance);

            var call = mockRunner.Invocations.First(i => i.Arguments.Contains("swiftc"));
            Assert.DoesNotContain(".o", call.Arguments);
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"thunk_integ_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion
}
