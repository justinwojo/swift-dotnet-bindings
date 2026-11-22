// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// A wrapper that force-loads a static primary must also link every native dependency the
    /// primary's objects call into. A library-type xcframework dependency (a bare <c>.a</c> or
    /// <c>.dylib</c> slice with a headers directory) has no <c>.framework</c> for a <c>-F</c> to find,
    /// and a static archive carries no load commands for the system frameworks it uses, so
    /// <see cref="DependencyLinkInputs"/> supplies both: the slice binary by path, and the system
    /// frameworks/libraries the dependency's own module map declares.
    /// </summary>
    public class DependencyLinkInputsTests
    {
        private static readonly byte[] ArMagic = { 0x21, 0x3C, 0x61, 0x72, 0x63, 0x68, 0x3E, 0x0A };
        private static readonly byte[] MachOMagic = { 0xCF, 0xFA, 0xED, 0xFE, 0, 0, 0, 0 };

        /// <summary>A scratch dir with a fake SDK and helpers to lay out dependency xcframeworks.</summary>
        private sealed class Layout : IDisposable
        {
            public string Root { get; }
            public string Sdk { get; }

            public Layout(string[]? sdkFrameworks = null, string[]? sdkLibraries = null)
            {
                Root = Path.Combine(Path.GetTempPath(), $"deplink_{Guid.NewGuid():N}");
                Sdk = Path.Combine(Root, "sdk");
                foreach (var fw in sdkFrameworks ?? new[] { "Metal", "OpenGLES" })
                    SdkFramework(Path.Combine(Sdk, "System", "Library", "Frameworks"), fw, linkable: true);
                Directory.CreateDirectory(Path.Combine(Sdk, "usr", "lib"));
                foreach (var lib in sdkLibraries ?? new[] { "c++abi", "z" })
                    File.WriteAllText(Path.Combine(Sdk, "usr", "lib", $"lib{lib}.tbd"), "--- !tapi-tbd\n");
            }

            /// <summary>An SDK framework: a <c>.tbd</c> stub when linkable, headers only otherwise.</summary>
            public static void SdkFramework(string frameworksDir, string name, bool linkable, string stubBody = "")
            {
                var framework = Path.Combine(frameworksDir, name + ".framework");
                Directory.CreateDirectory(Path.Combine(framework, "Headers"));
                if (linkable)
                    File.WriteAllText(Path.Combine(framework, name + ".tbd"), "--- !tapi-tbd\n" + stubBody);
            }

            /// <summary>A library-type xcframework; returns the simulator slice dir.</summary>
            public string LibraryXcframework(
                string name, string libraryPath, byte[] binary, string? modulemap,
                string? headersPath = null, string headersSubdir = "")
            {
                var xcfw = Path.Combine(Root, name + ".xcframework");
                var slice = Path.Combine(xcfw, "ios-arm64-simulator");
                Directory.CreateDirectory(slice);
                File.WriteAllBytes(Path.Combine(slice, libraryPath), binary);
                var headersDir = Path.Combine(slice, headersPath ?? "Headers", headersSubdir);
                Directory.CreateDirectory(headersDir);
                if (modulemap != null)
                    File.WriteAllText(Path.Combine(headersDir, "module.modulemap"), modulemap);
                var headersKey = headersPath != null
                    ? $"<key>HeadersPath</key><string>{headersPath}</string>"
                    : "";
                File.WriteAllText(Path.Combine(xcfw, "Info.plist"), $$"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0">
                    <dict>
                        <key>AvailableLibraries</key>
                        <array>
                            <dict>
                                {{headersKey}}
                                <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                                <key>LibraryPath</key><string>{{libraryPath}}</string>
                                <key>SupportedArchitectures</key><array><string>arm64</string></array>
                                <key>SupportedPlatform</key><string>ios</string>
                                <key>SupportedPlatformVariant</key><string>simulator</string>
                            </dict>
                        </array>
                        <key>CFBundlePackageType</key><string>XFWK</string>
                        <key>XCFrameworkFormatVersion</key><string>1.0</string>
                    </dict>
                    </plist>
                    """);
                return slice;
            }

            /// <summary>A framework-type slice dir holding one framework.</summary>
            public string FrameworkSlice(string name, byte[] binary, string? modulemap)
            {
                var slice = Path.Combine(Root, name + "-fw", "ios-arm64-simulator");
                var fw = Path.Combine(slice, name + ".framework");
                Directory.CreateDirectory(Path.Combine(fw, "Modules"));
                File.WriteAllBytes(Path.Combine(fw, name), binary);
                if (modulemap != null)
                    File.WriteAllText(Path.Combine(fw, "Modules", "module.modulemap"), modulemap);
                return slice;
            }

            public void Dispose()
            {
                try { Directory.Delete(Root, true); } catch { /* best effort */ }
            }
        }

        private const string GraphLikeModuleMap = """
            module DepCore {
              export *
              link "c++abi"
              link framework "Metal"
              // link framework "Commented"
              /* link "alsocommented" */
              link framework "NoSuchKit"
              link "nosuchlib"
            }
            """;

        private const string SimTriple = "arm64-apple-ios15.0-simulator";

        [Fact]
        public void ParseModuleMapLinks_ReadsFrameworkAndLibraryDeclarations_IgnoresComments()
        {
            var links = DependencyLinkInputs.ParseModuleMapLinks("""
                framework module Outer {
                  umbrella header "Outer.h"
                  link "c++"   // trailing comment
                  link framework "Metal"
                  /* link framework "Hidden" */
                  module Inner { link framework "OpenGLES" link "c++" }
                }
                """);

            Assert.Equal(new[] { "Metal", "OpenGLES" }, links.Frameworks);
            Assert.Equal(new[] { "c++" }, links.Libraries);
        }

        [Fact]
        public void CollectLibraryBinaries_LibraryTypeSlices_ReturnStaticAndDynamicBinaries()
        {
            using var layout = new Layout();
            var staticSlice = layout.LibraryXcframework("DepCore", "libDepCore.a", ArMagic, null);
            var dynamicSlice = layout.LibraryXcframework("DepDyn", "libDepDyn.dylib", MachOMagic, null);
            var stubSlice = layout.LibraryXcframework("DepStub", "libDepStub.a", "nope"u8.ToArray(), null);
            var frameworkSlice = layout.FrameworkSlice("FwDep", ArMagic, null);

            var binaries = DependencyLinkInputs.CollectLibraryBinaries(
                new[] { staticSlice, dynamicSlice, stubSlice, frameworkSlice, "/no/such/dir" });

            Assert.Equal(
                new[] { Path.Combine(staticSlice, "libDepCore.a"), Path.Combine(dynamicSlice, "libDepDyn.dylib") },
                binaries);
        }

        [Fact]
        public void CollectStaticArchiveAutolinks_StaticLibrarySlice_KeepsOnlyWhatTheSdkProvides()
        {
            using var layout = new Layout();
            var slice = layout.LibraryXcframework(
                "DepCore", "DepCore.a", ArMagic, GraphLikeModuleMap, headersSubdir: "DepCore");

            var links = DependencyLinkInputs.CollectStaticArchiveAutolinks(new[] { slice }, layout.Sdk, SimTriple);

            Assert.Equal(new[] { "Metal" }, links.Frameworks);
            Assert.Equal(new[] { "c++abi" }, links.Libraries);
        }

        [Fact]
        public void CollectStaticArchiveAutolinks_HonoursInfoPlistHeadersPath()
        {
            using var layout = new Layout();
            var slice = layout.LibraryXcframework(
                "DepCore", "libDepCore.a", ArMagic, "module DepCore { link framework \"OpenGLES\" }",
                headersPath: "include");

            var links = DependencyLinkInputs.CollectStaticArchiveAutolinks(new[] { slice }, layout.Sdk, SimTriple);

            Assert.Equal(new[] { "OpenGLES" }, links.Frameworks);
        }

        [Fact]
        public void CollectStaticArchiveAutolinks_DynamicDependency_ContributesNothing()
        {
            // A dylib names its own load commands; its module map's link declarations add nothing.
            using var layout = new Layout();
            var dylibSlice = layout.LibraryXcframework("DepDyn", "libDepDyn.dylib", MachOMagic, GraphLikeModuleMap);
            var dynamicFramework = layout.FrameworkSlice("FwDyn", MachOMagic, GraphLikeModuleMap);

            var links = DependencyLinkInputs.CollectStaticArchiveAutolinks(
                new[] { dylibSlice, dynamicFramework }, layout.Sdk, SimTriple);

            Assert.Empty(links.Frameworks);
            Assert.Empty(links.Libraries);
        }

        [Fact]
        public void CollectStaticArchiveAutolinks_StaticFrameworkDependency_ReadsItsModuleMap()
        {
            using var layout = new Layout();
            var slice = layout.FrameworkSlice("FwDep", ArMagic, "framework module FwDep { link framework \"Metal\" link \"z\" }");

            var links = DependencyLinkInputs.CollectStaticArchiveAutolinks(new[] { slice }, layout.Sdk, SimTriple);

            Assert.Equal(new[] { "Metal" }, links.Frameworks);
            Assert.Equal(new[] { "z" }, links.Libraries);
        }

        [Fact]
        public void CollectStaticArchiveAutolinks_HeaderOnlySdkFramework_IsNotLinked()
        {
            // Some SDK frameworks ship headers and no stub; ld rejects `-framework` for them.
            using var layout = new Layout();
            Layout.SdkFramework(Path.Combine(layout.Sdk, "System", "Library", "Frameworks"), "HeadersOnlyKit", linkable: false);
            var slice = layout.LibraryXcframework(
                "DepCore", "libDepCore.a", ArMagic,
                "module DepCore { link framework \"HeadersOnlyKit\" link framework \"Metal\" }");

            var links = DependencyLinkInputs.CollectStaticArchiveAutolinks(new[] { slice }, layout.Sdk, SimTriple);

            Assert.Equal(new[] { "Metal" }, links.Frameworks);
        }

        [Fact]
        public void CollectStaticArchiveAutolinks_ClientRestrictedSdkStub_IsNotLinked()
        {
            // ld refuses a direct link to a framework whose stub lists allowable clients. A list on a
            // re-exported sub-library (a later stub document) does not restrict the framework itself.
            using var layout = new Layout();
            var frameworks = Path.Combine(layout.Sdk, "System", "Library", "Frameworks");
            Layout.SdkFramework(frameworks, "RestrictedKit", linkable: true,
                "install-name: '/System/Library/Frameworks/RestrictedKit.framework/RestrictedKit'\n" +
                "allowable-clients:\n  - clients: [ PeerKit ]\n...\n");
            Layout.SdkFramework(frameworks, "UmbrellaKit", linkable: true,
                "install-name: '/System/Library/Frameworks/UmbrellaKit.framework/UmbrellaKit'\n" +
                "--- !tapi-tbd\ninstall-name: '/System/Library/PrivateFrameworks/Sub.framework/Sub'\n" +
                "allowable-clients:\n  - clients: [ UmbrellaKit ]\n...\n");
            File.WriteAllText(Path.Combine(layout.Sdk, "usr", "lib", "librestricted.tbd"),
                "--- !tapi-tbd\ninstall-name: '/usr/lib/librestricted.dylib'\nallowable-clients:\n  - clients: [ Peer ]\n...\n");
            var slice = layout.LibraryXcframework(
                "DepCore", "libDepCore.a", ArMagic,
                "module DepCore { link framework \"RestrictedKit\" link framework \"UmbrellaKit\" link \"restricted\" link \"z\" }");

            var links = DependencyLinkInputs.CollectStaticArchiveAutolinks(new[] { slice }, layout.Sdk, SimTriple);

            Assert.Equal(new[] { "UmbrellaKit" }, links.Frameworks);
            Assert.Equal(new[] { "z" }, links.Libraries);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CollectStaticArchiveAutolinks_UniversalArchive_ReadsItsModuleMap(bool fat64)
        {
            // A universal (fat) binary whose first architecture is an ar archive is still a static
            // archive, whether the fat header uses 32- or 64-bit offsets.
            const int memberOffset = 64;
            var binary = new byte[memberOffset + ArMagic.Length];
            new byte[] { 0xCA, 0xFE, 0xBA, fat64 ? (byte)0xBF : (byte)0xBE, 0, 0, 0, 1 }.CopyTo(binary, 0);
            // First fat_arch: cputype, cpusubtype, then the big-endian offset (8 bytes for fat64).
            if (fat64)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(binary.AsSpan(16, 8), memberOffset);
            else
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(16, 4), memberOffset);
            ArMagic.CopyTo(binary, memberOffset);
            using var layout = new Layout();
            var slice = layout.LibraryXcframework(
                "DepCore", "libDepCore.a", binary, "module DepCore { link framework \"Metal\" }");

            var links = DependencyLinkInputs.CollectStaticArchiveAutolinks(new[] { slice }, layout.Sdk, SimTriple);

            Assert.Equal(new[] { "Metal" }, links.Frameworks);
        }

        [Fact]
        public void CatalystAutolinks_UseIOSSupportFrameworks_AndBothLinkersSearchThem()
        {
            // Mac Catalyst links iOS frameworks from the SDK's iOSSupport tree; a macOS target with
            // the same SDK cannot, and the clang thunk link needs the -F that swiftc gets.
            using var layout = new Layout();
            var iOSSupport = Path.Combine(layout.Sdk, "System", "iOSSupport", "System", "Library", "Frameworks");
            Layout.SdkFramework(iOSSupport, "UIKit", linkable: true);
            var slice = layout.LibraryXcframework(
                "DepCore", "libDepCore.a", ArMagic, "module DepCore { link framework \"UIKit\" }");
            const string catalyst = "arm64-apple-ios15.0-macabi";

            Assert.Equal(new[] { "UIKit" },
                DependencyLinkInputs.CollectStaticArchiveAutolinks(new[] { slice }, layout.Sdk, catalyst).Frameworks);
            Assert.Empty(
                DependencyLinkInputs.CollectStaticArchiveAutolinks(new[] { slice }, layout.Sdk, "arm64-apple-macos12.0").Frameworks);

            var runner = new MockCommandRunner();
            NativeThunkCompiler.LinkWithClang(
                new[] { "/tmp/thunks.o" }, "/tmp/out/Binary", "TestSwiftBindings",
                catalyst, layout.Sdk, runner, NullLogger.Instance,
                transitiveFrameworkSearchPaths: new[] { slice });
            var (_, args) = runner.Invocations[0];
            Assert.Contains($"-F \"{iOSSupport}\"", args);
            Assert.Contains("-framework UIKit ", args);
        }

        [Fact]
        public void EnumerateFrameworkBinariesUnder_IncludesLibraryTypeSliceBinaries()
        {
            // The ObjC over-binding guard keeps a header-declared class or function only when some
            // probed binary defines it, so a dependency's bare archive must be among those binaries.
            using var layout = new Layout();
            var librarySlice = layout.LibraryXcframework("DepCore", "libDepCore.a", ArMagic, null);
            var frameworkSlice = layout.FrameworkSlice("FwDep", MachOMagic, null);

            var binaries = XCFrameworkResolver.EnumerateFrameworkBinariesUnder(
                new[] { librarySlice, frameworkSlice }, NullLogger.Instance);

            Assert.Contains(Path.Combine(librarySlice, "libDepCore.a"), binaries);
            Assert.Contains(Path.Combine(frameworkSlice, "FwDep.framework", "FwDep"), binaries);
        }

        [Fact]
        public void InvokeSwiftCompiler_LibraryTypeDependency_LinksBinaryAndDeclaredSystemLinks()
        {
            using var layout = new Layout();
            var slice = layout.LibraryXcframework(
                "DepCore", "DepCore.a", ArMagic, GraphLikeModuleMap, headersSubdir: "DepCore");
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            SwiftWrapperCompiler.InvokeSwiftCompiler(
                new List<string> { "/tmp/a.swift" }, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", layout.Sdk, "/fw/search",
                runner, NullLogger.Instance,
                additionalFrameworkSearchPaths: new[] { slice },
                // An author-declared framework the module map also names is linked once.
                linkFrameworks: new[] { "Metal" },
                linkLibraries: new[] { "c++" });

            var (_, args) = runner.Invocations[0];
            Assert.Contains($"-Xlinker \"{Path.Combine(slice, "DepCore.a")}\"", args);
            Assert.Equal(1, Count(args, "-Xlinker -framework -Xlinker Metal "));
            Assert.Contains("-lc++abi ", args);
            Assert.Contains("-lc++ ", args);
            Assert.DoesNotContain("NoSuchKit", args);
            Assert.DoesNotContain("nosuchlib", args);
            Assert.DoesNotContain("Commented", args);
        }

        [Fact]
        public void InvokeSwiftCompiler_ModuleResolutionOnlyPath_ContributesNoLinkInputs()
        {
            // A path passed only for module resolution (the XCTest platform path) is outside the raw
            // dependency scan set and must not link anything, library slices included.
            using var layout = new Layout();
            var slice = layout.LibraryXcframework("DepCore", "libDepCore.a", ArMagic, GraphLikeModuleMap);
            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");

            SwiftWrapperCompiler.InvokeSwiftCompiler(
                new List<string> { "/tmp/a.swift" }, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", layout.Sdk, "/fw/search",
                runner, NullLogger.Instance,
                additionalFrameworkSearchPaths: new[] { slice },
                transitiveScanSearchPaths: Array.Empty<string>());

            var (_, args) = runner.Invocations[0];
            Assert.Contains($"-F \"{slice}\"", args);
            Assert.DoesNotContain("libDepCore.a", args);
            Assert.DoesNotContain("Metal", args);
        }

        [Fact]
        public void LinkWithClang_LibraryTypeDependency_MatchesSwiftcPath()
        {
            using var layout = new Layout();
            var slice = layout.LibraryXcframework(
                "DepCore", "DepCore.a", ArMagic, GraphLikeModuleMap, headersSubdir: "DepCore");
            var runner = new MockCommandRunner();

            NativeThunkCompiler.LinkWithClang(
                new[] { "/tmp/thunks.o" }, "/tmp/out/Binary", "TestSwiftBindings",
                "arm64-apple-ios15.0-simulator", layout.Sdk, runner, NullLogger.Instance,
                originalModuleName: "OrigMod",
                linkFrameworks: new[] { "Metal" },
                transitiveFrameworkSearchPaths: new[] { slice });

            var (_, args) = runner.Invocations[0];
            Assert.Contains($"\"{Path.Combine(slice, "DepCore.a")}\"", args);
            Assert.Equal(1, Count(args, "-framework Metal "));
            Assert.Contains("-lc++abi ", args);
            Assert.DoesNotContain("NoSuchKit", args);
        }

        private static int Count(string haystack, string needle)
        {
            int count = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
            return count;
        }
    }

    /// <summary>
    /// Real toolchain: a static primary archive that calls into a static library-type xcframework
    /// dependency, whose own object uses a system framework only its module map declares. The
    /// production <see cref="SwiftWrapperCompiler.InvokeSwiftCompiler"/> force-loads the primary, as it
    /// does for a static source framework, and must produce a wrapper that defines the dependency's
    /// code and loads the declared framework. The negative control links the same inputs with the
    /// dependency slice reachable only through <c>-F</c>, which is what a library-type slice used to
    /// get, and must fail. macOS with an iOS-simulator SDK only; skipped elsewhere.
    /// </summary>
    public class DependencyLinkInputsToolchainTests
    {
        private static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        private const string DepSymbol = "sbDepLinkProbeAnswer";
        private const string PrimarySymbol = "sbDepLinkProbePrimary";

        [Fact]
        public void StaticPrimaryCallingLibraryTypeDependency_WrapperLinksDependencyAndItsDeclaredFramework()
        {
            if (!IsMacOS) return;
            var runner = new SystemCommandRunner();
            var (sdkExit, sdkOut, _) = runner.Run("xcrun", "--sdk iphonesimulator --show-sdk-path", timeoutMs: 30000);
            var sdk = sdkOut?.Trim();
            if (sdkExit != 0 || string.IsNullOrEmpty(sdk) || !Directory.Exists(sdk))
                return; // no simulator SDK — environmental skip

            var arch = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x86_64" : "arm64";
            var triple = $"{arch}-apple-ios15.0-simulator";

            // The real SDK's stubs: an umbrella whose re-exported sub-libraries restrict their clients
            // stays linkable; a client-restricted framework (present in current SDKs) does not.
            Assert.True(DependencyLinkInputs.SdkProvidesFramework(sdk, "UIKit"));
            Assert.True(DependencyLinkInputs.SdkProvidesFramework(sdk, "Metal"));
            Assert.False(DependencyLinkInputs.SdkProvidesFramework(sdk, "SwiftUICore"));
            var dir = Path.Combine(Path.GetTempPath(), $"deplink_tc_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // The dependency: a library-type xcframework slice (bare archive + headers module map).
                // Its object calls a Metal C entry point, and only its module map says to link Metal.
                var depC = Path.Combine(dir, "dep.c");
                File.WriteAllText(depC,
                    "extern void *MTLCreateSystemDefaultDevice(void);\n" +
                    $"int {DepSymbol}(void) {{ return MTLCreateSystemDefaultDevice() ? 42 : 41; }}\n");
                var xcfw = Path.Combine(dir, "DepCore.xcframework");
                var slice = Path.Combine(xcfw, $"ios-{arch}-simulator");
                Directory.CreateDirectory(Path.Combine(slice, "Headers", "DepCore"));
                File.WriteAllText(Path.Combine(slice, "Headers", "DepCore", "module.modulemap"),
                    "module DepCore {\n  export *\n  link framework \"Metal\"\n}\n");
                File.WriteAllText(Path.Combine(xcfw, "Info.plist"), $$"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0">
                    <dict>
                        <key>AvailableLibraries</key>
                        <array>
                            <dict>
                                <key>HeadersPath</key><string>Headers</string>
                                <key>LibraryIdentifier</key><string>ios-{{arch}}-simulator</string>
                                <key>LibraryPath</key><string>DepCore.a</string>
                                <key>SupportedArchitectures</key><array><string>{{arch}}</string></array>
                                <key>SupportedPlatform</key><string>ios</string>
                                <key>SupportedPlatformVariant</key><string>simulator</string>
                            </dict>
                        </array>
                        <key>CFBundlePackageType</key><string>XFWK</string>
                        <key>XCFrameworkFormatVersion</key><string>1.0</string>
                    </dict>
                    </plist>
                    """);
                BuildArchive(runner, triple, sdk, depC, Path.Combine(slice, "DepCore.a"));

                // The primary: a static archive whose object calls the dependency, force-loaded into
                // the wrapper exactly as a static source framework's binary is.
                var primaryC = Path.Combine(dir, "primary.c");
                File.WriteAllText(primaryC,
                    $"extern int {DepSymbol}(void);\nint {PrimarySymbol}(void) {{ return {DepSymbol}(); }}\n");
                var primaryArchive = Path.Combine(dir, "libPrimary.a");
                BuildArchive(runner, triple, sdk, primaryC, primaryArchive);

                var wrapperSwift = Path.Combine(dir, "Probe.swift");
                File.WriteAllText(wrapperSwift, "public func depLinkProbe() -> Int { 1 }\n");

                string Link(IReadOnlyList<string> scan, string name)
                {
                    var outBin = Path.Combine(dir, name);
                    SwiftWrapperCompiler.InvokeSwiftCompiler(
                        new List<string> { wrapperSwift }, outBin, "DepLinkProbeBindings",
                        triple, sdk, dir, runner, NullLogger.Instance,
                        additionalFrameworkSearchPaths: new[] { slice },
                        forceLoadBinaries: new[] { primaryArchive },
                        transitiveScanSearchPaths: scan);
                    return outBin;
                }

                var wrapper = Link(new[] { slice }, "with-scan.dylib");
                var (_, defined, _) = runner.Run("nm", $"-gU \"{wrapper}\"", timeoutMs: 30000);
                Assert.Contains("_" + DepSymbol, defined);
                Assert.Contains("_" + PrimarySymbol, defined);
                var (_, loads, _) = runner.Run("xcrun", $"otool -L \"{wrapper}\"", timeoutMs: 30000);
                Assert.Contains("/System/Library/Frameworks/Metal.framework/Metal", loads);

                // NEGATIVE control: the slice is only a -F, as before the fix, so nothing links the
                // dependency and the primary's call into it is undefined.
                var ex = Assert.ThrowsAny<Exception>(() => Link(Array.Empty<string>(), "without-scan.dylib"));
                Assert.Contains(DepSymbol, ex.Message);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* best effort */ }
            }
        }

        // With the SDK present a fixture that fails to build is a failure, never a silent pass.
        private static void BuildArchive(ICommandRunner runner, string triple, string sdk, string source, string archive)
        {
            var obj = Path.ChangeExtension(archive, ".o");
            var (cc, _, ccErr) = runner.Run("xcrun",
                $"clang -c -target {triple} -isysroot \"{sdk}\" \"{source}\" -o \"{obj}\"", timeoutMs: 60000);
            Assert.True(cc == 0 && File.Exists(obj), $"clang failed for {source}: {ccErr}");
            var (ar, _, arErr) = runner.Run("xcrun", $"ar rcs \"{archive}\" \"{obj}\"", timeoutMs: 30000);
            File.Delete(obj);
            Assert.True(ar == 0 && File.Exists(archive), $"ar failed for {archive}: {arErr}");
        }
    }
}
