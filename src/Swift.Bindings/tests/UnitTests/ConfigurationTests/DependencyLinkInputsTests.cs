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

            /// <summary>A compiled binding wrapper xcframework with an arm64 simulator slice; returns its binary.</summary>
            public string WrapperXcframework(string outputDirectory, string moduleName)
            {
                var name = moduleName + "SwiftBindings";
                var xcfw = Path.Combine(outputDirectory, name + ".xcframework");
                var framework = Path.Combine(xcfw, "ios-arm64-simulator", name + ".framework");
                Directory.CreateDirectory(framework);
                File.WriteAllBytes(Path.Combine(framework, name), MachOMagic);
                File.WriteAllText(Path.Combine(xcfw, "Info.plist"), $$"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0">
                    <dict>
                        <key>AvailableLibraries</key>
                        <array>
                            <dict>
                                <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                                <key>LibraryPath</key><string>{{name}}.framework</string>
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
                return Path.Combine(framework, name);
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

        [Fact]
        public void BridgeLink_LibraryTypeDependency_IsKeptAndResolvedThroughTheWrapper()
        {
            // The bridge's dependency filter keeps what has a module to resolve and skips the binding
            // wrappers the SDK also passes; a library-type xcframework has no .framework at all and must
            // still be kept. Its static archive is what the wrapper carries, so the bridge links the
            // wrapper and takes none of the archive.
            using var layout = new Layout();
            var slice = layout.LibraryXcframework(
                "DepCore", "DepCore.a", ArMagic, GraphLikeModuleMap, headersSubdir: "DepCore");
            var libraryXcfw = Path.GetDirectoryName(slice)!;
            var bindingWrapper = Path.Combine(layout.Root, "DepCoreSwiftBindings.xcframework");
            var wrapperFramework = Path.Combine(bindingWrapper, "ios-arm64-simulator", "DepCoreSwiftBindings.framework");
            Directory.CreateDirectory(wrapperFramework);
            File.WriteAllBytes(Path.Combine(wrapperFramework, "DepCoreSwiftBindings"), MachOMagic);
            File.WriteAllText(Path.Combine(bindingWrapper, "Info.plist"), """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                            <key>LibraryPath</key><string>DepCoreSwiftBindings.framework</string>
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

            var selected = BindingsGenerator.SelectBridgeLinkDependencies(
                new[] { libraryXcfw, bindingWrapper, Path.Combine(layout.Root, "Missing.xcframework") },
                NullLogger.Instance);
            Assert.Equal(new[] { libraryXcfw }, selected);

            var primary = new XCFrameworkResolution
            {
                AbiJsonPath = "/abi.json",
                DylibPath = "/dylib",
                TbdPath = "/tbd",
                ModuleName = "Primary",
                XCFrameworkPath = "/path/to/Primary.xcframework",
                FrameworkSearchPath = "/path/to/Primary.xcframework/ios-arm64-simulator",
                LibraryIdentifier = "ios-arm64-simulator",
                IsSimulatorSlice = true,
                SelectedArchitecture = "arm64",
                SupportedArchitectures = new[] { "arm64" },
            };
            var resolved = BindingsGenerator.ResolveFrameworkDependencies(
                selected.ToArray(), primary, primary.XCFrameworkPath, "simulator",
                XCFrameworkPlatformTarget.Simulator, NullLogger.Instance);
            Assert.NotNull(resolved);
            var bridgeSearchPaths = resolved!
                .Select(d => d.SimulatorFrameworkSearchPath)
                .OfType<string>()
                .ToList();

            // The bridge compile passes exactly these as its additional search paths.
            var output = Path.Combine(layout.Root, "out");
            var wrapper = layout.WrapperXcframework(output, "Primary");
            var link = SwiftWrapperCompiler.ResolveBridgeStaticCodeLink(
                output, "Primary", primary.DylibPath, SimSlice, bridgeSearchPaths, NoFileTool(), NullLogger.Instance);
            Assert.Equal(new SwiftWrapperCompiler.BridgeStaticCodeLink(wrapper, null), link);

            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                new List<string> { "/tmp/Primary.SwiftUIBridge.swift" }, "/tmp/out/PrimaryBridge", "PrimaryBridge",
                SimTriple, layout.Sdk, primary.FrameworkSearchPath, runner, NullLogger.Instance,
                additionalFrameworkSearchPaths: bridgeSearchPaths, originalModuleName: "Primary",
                staticCodeThroughWrapper: link);

            var (_, args) = runner.Invocations[0];
            Assert.Contains($"-Xlinker \"{wrapper}\" ", args);
            Assert.DoesNotContain("DepCore.a", args);
            Assert.DoesNotContain("-framework -Xlinker Metal ", args);
            Assert.DoesNotContain("-lc++abi ", args);
            // The bound framework is dynamic here: the bridge still links it, as before.
            Assert.DoesNotContain("-disable-autolink-framework", args);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void BridgeLink_StaticPrimary_ResolvesThroughTheWrapperAndCopiesNoStaticCode(bool forceLoad)
        {
            // A static bound framework is force-loaded into the wrapper, and its calls into a library-type
            // dependency pull that archive into the wrapper too. The bridge is a second image: linking
            // either again there runs every static initializer twice and registers every ObjC class twice,
            // with or without ForceLoad. A dynamic library dependency is its own image and is still linked.
            using var layout = new Layout();
            var archiveSlice = layout.LibraryXcframework(
                "DepCore", "DepCore.a", ArMagic, GraphLikeModuleMap, headersSubdir: "DepCore");
            var dylibSlice = layout.LibraryXcframework("DepDyn", "libDepDyn.dylib", MachOMagic, null);
            var primaryBinary = Path.Combine(layout.Root, "Primary.xcframework", "ios-arm64-simulator", "Primary.framework", "Primary");
            Directory.CreateDirectory(Path.GetDirectoryName(primaryBinary)!);
            File.WriteAllBytes(primaryBinary, ArMagic);
            var output = Path.Combine(layout.Root, "out");
            var wrapper = layout.WrapperXcframework(output, "Primary");
            var searchPaths = new[] { archiveSlice, dylibSlice };

            var link = SwiftWrapperCompiler.ResolveBridgeStaticCodeLink(
                output, "Primary", primaryBinary, SimSlice, searchPaths, NoFileTool(), NullLogger.Instance);
            Assert.Equal(new SwiftWrapperCompiler.BridgeStaticCodeLink(wrapper, "Primary"), link);

            var runner = new MockCommandRunner();
            runner.SetResponse("swiftc", 0, "");
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                new List<string> { "/tmp/Primary.SwiftUIBridge.swift" }, "/tmp/out/PrimaryBridge", "PrimaryBridge",
                SimTriple, layout.Sdk, Path.GetDirectoryName(Path.GetDirectoryName(primaryBinary))!, runner, NullLogger.Instance,
                additionalFrameworkSearchPaths: searchPaths, originalModuleName: "Primary",
                forceLoadDependencies: forceLoad ? new[] { Path.GetDirectoryName(archiveSlice)! } : null,
                staticCodeThroughWrapper: link);

            var (_, args) = runner.Invocations[0];
            Assert.Contains("-Xfrontend -disable-autolink-framework -Xfrontend Primary ", args);
            Assert.Contains($"-Xlinker \"{wrapper}\" ", args);
            Assert.DoesNotContain("DepCore.a", args);
            Assert.DoesNotContain("-force_load", args);
            Assert.DoesNotContain(primaryBinary, args);
            Assert.Contains($"-Xlinker \"{Path.Combine(dylibSlice, "libDepDyn.dylib")}\" ", args);
        }

        [Fact]
        public void BridgeLink_NoStaticCode_LinksAsBefore()
        {
            // A dynamic bound framework and no static archive: nothing for the wrapper to carry, so the
            // bridge needs no wrapper (and none has to exist yet).
            using var layout = new Layout();
            var dylibSlice = layout.LibraryXcframework("DepDyn", "libDepDyn.dylib", MachOMagic, null);
            var primaryBinary = Path.Combine(layout.Root, "Primary");
            File.WriteAllBytes(primaryBinary, MachOMagic);

            Assert.Null(SwiftWrapperCompiler.ResolveBridgeStaticCodeLink(
                Path.Combine(layout.Root, "out"), "Primary", primaryBinary, SimSlice, new[] { dylibSlice },
                NoFileTool(), NullLogger.Instance));
        }

        [Theory]
        [InlineData(false, "arm64")]
        [InlineData(true, "x86_64")]
        public void BridgeLink_StaticCodeWithoutAMatchingWrapperSlice_IsRefused(bool wrapperExists, string bridgeArch)
        {
            // Linking the static code into the bridge instead is the duplicate, so a missing wrapper slice
            // (none at all, or none for the bridge's architecture) fails the bridge link.
            using var layout = new Layout();
            var primaryBinary = Path.Combine(layout.Root, "Primary");
            File.WriteAllBytes(primaryBinary, ArMagic);
            var output = Path.Combine(layout.Root, "out");
            if (wrapperExists)
                layout.WrapperXcframework(output, "Primary");

            var ex = Assert.Throws<InvalidOperationException>(() => SwiftWrapperCompiler.ResolveBridgeStaticCodeLink(
                output, "Primary", primaryBinary, SimSlice.WithArchitecture(bridgeArch), null, NoFileTool(), NullLogger.Instance));
            Assert.Contains("PrimarySwiftBindings", ex.Message);
            Assert.Contains(bridgeArch, ex.Message);
        }

        private static SliceVariant SimSlice => PlatformInfoFactory.Create(ApplePlatform.iOS).GetSlice(true);

        // `file` unavailable, so NativeLinkageProbe classifies by magic bytes.
        private static MockCommandRunner NoFileTool()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("file", 1, "");
            return runner;
        }

        [Fact]
        public void ForceLoadDependency_LinksOnlyThatArchiveInFull_OnBothLinkers()
        {
            using var layout = new Layout();
            var forced = layout.LibraryXcframework("DepCore", "DepCore.a", ArMagic, null);
            var lazy = layout.LibraryXcframework("DepOther", "DepOther.a", ArMagic, null);
            var forcedXcfw = Path.GetDirectoryName(forced)!;
            var forcedArchive = Path.Combine(forced, "DepCore.a");
            var lazyArchive = Path.Combine(lazy, "DepOther.a");

            var inputs = DependencyLinkInputs.CollectLibraryLinkInputs(new[] { forced, lazy }, new[] { forcedXcfw + "/" });
            Assert.Equal(
                new[] { new DependencyLinkInputs.LibraryLinkInput(forcedArchive, true), new DependencyLinkInputs.LibraryLinkInput(lazyArchive, false) },
                inputs);

            var swiftc = new MockCommandRunner();
            swiftc.SetResponse("swiftc", 0, "");
            SwiftWrapperCompiler.InvokeSwiftCompiler(
                new List<string> { "/tmp/a.swift" }, "/tmp/out/Binary", "TestSwiftBindings",
                SimTriple, layout.Sdk, "/fw/search", swiftc, NullLogger.Instance,
                additionalFrameworkSearchPaths: new[] { forced, lazy },
                forceLoadDependencies: new[] { forcedXcfw });
            var swiftcArgs = swiftc.Invocations[0].Arguments;
            Assert.Contains($"-Xlinker -force_load -Xlinker \"{forcedArchive}\" ", swiftcArgs);
            Assert.Equal(1, Count(swiftcArgs, forcedArchive));
            Assert.Contains($"-Xlinker \"{lazyArchive}\" ", swiftcArgs);
            Assert.DoesNotContain($"-force_load -Xlinker \"{lazyArchive}\"", swiftcArgs);

            var clang = new MockCommandRunner();
            NativeThunkCompiler.LinkWithClang(
                new[] { "/tmp/thunks.o" }, "/tmp/out/Binary", "TestSwiftBindings",
                SimTriple, layout.Sdk, clang, NullLogger.Instance,
                originalModuleName: "OrigMod",
                transitiveFrameworkSearchPaths: new[] { forced, lazy },
                forceLoadDependencies: new[] { forcedXcfw });
            var clangArgs = clang.Invocations[0].Arguments;
            // -Xlinker carries the path as one argument; clang would split a -Wl, value at its commas.
            Assert.Contains($"-Xlinker -force_load -Xlinker \"{forcedArchive}\" ", clangArgs);
            Assert.DoesNotContain("-Wl,-force_load", clangArgs);
            Assert.Equal(1, Count(clangArgs, forcedArchive));
            Assert.Contains($" \"{lazyArchive}\" ", clangArgs);
            Assert.DoesNotContain($"-force_load -Xlinker \"{lazyArchive}\"", clangArgs);
        }

        [Fact]
        public void ForceLoadDependency_StaticLibraryTypeDependency_IsAccepted()
        {
            using var layout = new Layout();
            var xcfw = Path.GetDirectoryName(layout.LibraryXcframework("DepCore", "DepCore.a", ArMagic, null))!;
            var logger = new CapturingLogger();

            Assert.True(DependencyLinkInputs.ValidateForceLoadDependencies(new[] { xcfw }, new[] { xcfw }, logger));
            Assert.Empty(logger.Entries);
            Assert.True(DependencyLinkInputs.ValidateForceLoadDependencies(null, new[] { xcfw }, logger));
        }

        [Theory]
        [InlineData("framework", "framework-type xcframework")]
        [InlineData("dynamic", "not a static archive")]
        [InlineData("undeclared", "not also passed as a --framework-dependency")]
        [InlineData("not-an-xcframework", "no Info.plist")]
        public void ForceLoadDependency_ItCannotApplyTo_IsRefusedWithSwiftbind142(string kind, string reason)
        {
            using var layout = new Layout();
            string xcfw = kind switch
            {
                "framework" => FrameworkXcframework(layout.Root, "FwDep"),
                "dynamic" => Path.GetDirectoryName(layout.LibraryXcframework("DepDyn", "libDepDyn.dylib", MachOMagic, null))!,
                "undeclared" => Path.GetDirectoryName(layout.LibraryXcframework("DepCore", "DepCore.a", ArMagic, null))!,
                _ => Directory.CreateDirectory(Path.Combine(layout.Root, "Loose.xcframework")).FullName,
            };
            var declared = kind == "undeclared" ? Array.Empty<string>() : new[] { xcfw };
            var logger = new CapturingLogger();

            Assert.False(DependencyLinkInputs.ValidateForceLoadDependencies(new[] { xcfw }, declared, logger));

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, entry.Level);
            Assert.StartsWith("SWIFTBIND142:", entry.Message);
            Assert.Contains(xcfw, entry.Message);
            Assert.Contains(reason, entry.Message);
        }

        // A framework-type xcframework: its slice ships a .framework bundle, as a Swift dependency's does.
        private static string FrameworkXcframework(string root, string name)
        {
            var xcfw = Path.Combine(root, name + ".xcframework");
            var fw = Path.Combine(xcfw, "ios-arm64-simulator", name + ".framework");
            Directory.CreateDirectory(fw);
            File.WriteAllBytes(Path.Combine(fw, name), ArMagic);
            File.WriteAllText(Path.Combine(xcfw, "Info.plist"), $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>LibraryIdentifier</key><string>ios-arm64-simulator</string>
                            <key>LibraryPath</key><string>{{name}}.framework</string>
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
            return xcfw;
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

        /// <summary>
        /// A library-type dependency whose component registers itself from a static initializer and is
        /// referenced by nothing else: the linker keeps only archive members that resolve a referenced
        /// symbol, so the lazily linked wrapper loses the component, and <c>ForceLoad</c> keeps it. The
        /// registry member is referenced (the wrapper's primary calls it), the component member is not.
        /// </summary>
        [Fact]
        public void SelfRegisteringComponent_IsInTheWrapperOnlyWhenItsDependencyIsForceLoaded()
        {
            if (!IsMacOS) return;
            var runner = new SystemCommandRunner();
            var (sdkExit, sdkOut, _) = runner.Run("xcrun", "--sdk iphonesimulator --show-sdk-path", timeoutMs: 30000);
            var sdk = sdkOut?.Trim();
            if (sdkExit != 0 || string.IsNullOrEmpty(sdk) || !Directory.Exists(sdk))
                return; // no simulator SDK — environmental skip

            var arch = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x86_64" : "arm64";
            var triple = $"{arch}-apple-ios15.0-simulator";
            var dir = Path.Combine(Path.GetTempPath(), $"deplink_fl_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var xcfw = Path.Combine(dir, "Registry.xcframework");
                var slice = Path.Combine(xcfw, $"ios-{arch}-simulator");
                Directory.CreateDirectory(Path.Combine(slice, "Headers"));
                File.WriteAllText(Path.Combine(xcfw, "Info.plist"), $$"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0">
                    <dict>
                        <key>AvailableLibraries</key>
                        <array>
                            <dict>
                                <key>HeadersPath</key><string>Headers</string>
                                <key>LibraryIdentifier</key><string>ios-{{arch}}-simulator</string>
                                <key>LibraryPath</key><string>Registry.a</string>
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
                var registryC = Path.Combine(dir, "registry.c");
                File.WriteAllText(registryC,
                    "static int count;\nvoid sbFlRegister(void) { count++; }\nint sbFlCount(void) { return count; }\n");
                var componentC = Path.Combine(dir, "component.c");
                File.WriteAllText(componentC,
                    "extern void sbFlRegister(void);\n" +
                    "__attribute__((constructor)) static void sbFlComponentInit(void) { sbFlRegister(); }\n" +
                    "int sbFlComponentMarker(void) { return 7; }\n");
                BuildArchive(runner, triple, sdk, new[] { registryC, componentC }, Path.Combine(slice, "Registry.a"));

                var primaryC = Path.Combine(dir, "primary.c");
                File.WriteAllText(primaryC, "extern int sbFlCount(void);\nint sbFlPrimaryCount(void) { return sbFlCount(); }\n");
                var primaryArchive = Path.Combine(dir, "libPrimary.a");
                BuildArchive(runner, triple, sdk, primaryC, primaryArchive);
                var wrapperSwift = Path.Combine(dir, "Probe.swift");
                File.WriteAllText(wrapperSwift, "public func forceLoadProbe() -> Int { 1 }\n");

                string Defined(string name, IReadOnlyCollection<string>? forceLoad)
                {
                    var outBin = Path.Combine(dir, name);
                    SwiftWrapperCompiler.InvokeSwiftCompiler(
                        new List<string> { wrapperSwift }, outBin, "ForceLoadProbeBindings",
                        triple, sdk, dir, runner, NullLogger.Instance,
                        additionalFrameworkSearchPaths: new[] { slice },
                        forceLoadBinaries: new[] { primaryArchive },
                        forceLoadDependencies: forceLoad);
                    var (_, defined, _) = runner.Run("nm", $"-gU \"{outBin}\"", timeoutMs: 30000);
                    return defined;
                }

                var lazy = Defined("lazy.dylib", null);
                Assert.Contains("_sbFlCount", lazy);
                Assert.DoesNotContain("_sbFlComponentMarker", lazy);

                var full = Defined("forced.dylib", new[] { xcfw });
                Assert.Contains("_sbFlCount", full);
                Assert.Contains("_sbFlComponentMarker", full);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* best effort */ }
            }
        }

        // One archive, one object per source, as a real multi-component library is built.
        private static void BuildArchive(ICommandRunner runner, string triple, string sdk, IReadOnlyList<string> sources, string archive)
        {
            var objects = new List<string>();
            foreach (var source in sources)
            {
                var obj = Path.ChangeExtension(source, ".o");
                var (cc, _, ccErr) = runner.Run("xcrun",
                    $"clang -c -target {triple} -isysroot \"{sdk}\" \"{source}\" -o \"{obj}\"", timeoutMs: 60000);
                Assert.True(cc == 0 && File.Exists(obj), $"clang failed for {source}: {ccErr}");
                objects.Add(obj);
            }
            var (ar, _, arErr) = runner.Run("xcrun",
                $"ar rcs \"{archive}\" {string.Join(" ", objects.Select(o => $"\"{o}\""))}", timeoutMs: 30000);
            Assert.True(ar == 0 && File.Exists(archive), $"ar failed for {archive}: {arErr}");
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
