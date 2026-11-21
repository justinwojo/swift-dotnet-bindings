// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Load-safety P1: every OS-resident framework a wrapper loads is weak-linked, so a symbol an older OS
    /// lacks can never stop the wrapper loading. The parser, the flags, and the link-check-relink loop of
    /// both wrapper link paths.
    /// </summary>
    public class OsFrameworkWeakLinkingTests
    {
        // otool -l of a wrapper that autolinked Foundation strongly and was told to weak-link CryptoKit,
        // in the shape both ld 1230 and ld 27106 write it (two architectures, as a fat slice prints).
        private const string StrongFoundation = """
            /tmp/W.framework/W (architecture arm64):
            Load command 13
                      cmd LC_LOAD_WEAK_DYLIB
                  cmdsize 88
                     name /System/Library/Frameworks/CryptoKit.framework/CryptoKit (offset 24)
            Load command 14
                      cmd LC_LOAD_DYLIB
                  cmdsize 56
                     name /usr/lib/libSystem.B.dylib (offset 24)
            Load command 15
                      cmd LC_LOAD_DYLIB
                  cmdsize 88
                     name /System/Library/Frameworks/Foundation.framework/Foundation (offset 24)
            Load command 16
                      cmd LC_LOAD_DYLIB
                  cmdsize 56
                     name /usr/lib/swift/libswiftCore.dylib (offset 24)
            Load command 17
                      cmd LC_RPATH
                  cmdsize 32
                     path /usr/lib/swift (offset 12)
            /tmp/W.framework/W (architecture x86_64):
            Load command 13
                      cmd LC_LOAD_WEAK_DYLIB
                  cmdsize 88
                     name /System/Library/Frameworks/CryptoKit.framework/CryptoKit (offset 24)
            Load command 15
                      cmd LC_LOAD_DYLIB
                  cmdsize 88
                     name /System/iOSSupport/System/Library/Frameworks/UIKit.framework/Versions/A/UIKit (offset 24)
            """;

        private const string AllWeak = """
            Load command 13
                      cmd LC_LOAD_WEAK_DYLIB
                  cmdsize 88
                     name /System/Library/Frameworks/CryptoKit.framework/CryptoKit (offset 24)
            Load command 15
                      cmd LC_LOAD_WEAK_DYLIB
                  cmdsize 88
                     name /System/Library/Frameworks/Foundation.framework/Foundation (offset 24)
            Load command 16
                      cmd LC_LOAD_DYLIB
                  cmdsize 56
                     name /usr/lib/swift/libswiftCore.dylib (offset 24)
            """;

        // otool -l of a wrapper whose SwiftUI load the linker kept strong although -weak_framework SwiftUI
        // was passed: SwiftUI re-exports SwiftUICore, which only its allowed clients may link.
        private const string StrongSwiftUI = """
            Load command 10
                  cmd LC_BUILD_VERSION
              cmdsize 32
             platform 7
                minos 15.0
                  sdk 26.2
            Load command 13
                      cmd LC_LOAD_DYLIB
                  cmdsize 88
                     name /System/Library/Frameworks/SwiftUI.framework/SwiftUI (offset 24)
            Load command 14
                      cmd LC_LOAD_WEAK_DYLIB
                  cmdsize 88
                     name /System/Library/Frameworks/Foundation.framework/Foundation (offset 24)
            """;

        // dyld_info -imports of that wrapper, in the shape dyld_info 1.0 writes for a fat slice.
        private const string WeakSwiftUIImports = """
            /tmp/out/Binary [arm64]:
                -imports:
                  0x0000  _$s7SwiftUI4TextVMn [weak-import] (from SwiftUI)
                  0x0001  _$s7SwiftUI5ColorV3redACvgZ [weak-import] (from SwiftUI)
                  0x0002  _$s10Foundation4DataVMn [weak-import] (from Foundation)
                  0x0003  _swift_release (from libswiftCore)
                  0x0004  _$s7SwiftUI4TextVN [weak-import] (from <weak-def-coalesce>)
            /tmp/out/Binary [x86_64]:
                -imports:
                  0x0000  _$s7SwiftUI4TextVMn [weak-import] (from SwiftUI)
            """;

        private const string StrongSwiftUIImport = """
            /tmp/out/Binary [arm64]:
                -imports:
                  0x0000  _$s7SwiftUI4TextVMn [weak-import] (from SwiftUI)
                  0x0001  _$s7SwiftUI4FormVMn (from SwiftUI)
            """;

        /// <summary>
        /// A throwaway SDK holding the stubs the strong-umbrella rule reads: SwiftUI re-exporting a
        /// restricted SwiftUICore and a public UIKit, and Foundation re-exporting nothing.
        /// </summary>
        private sealed class StubSdk : IDisposable
        {
            internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "osfw-sdk-" + Guid.NewGuid().ToString("N"));

            internal StubSdk(bool restrictSwiftUICore = true)
            {
                Write("System/Library/Frameworks/SwiftUI.framework/SwiftUI.tbd", """
                    --- !tapi-tbd
                    tbd-version:     4
                    targets:         [ x86_64-ios-simulator, arm64-ios-simulator ]
                    install-name:    '/System/Library/Frameworks/SwiftUI.framework/SwiftUI'
                    reexported-libraries:
                      - targets:         [ x86_64-ios-simulator, arm64-ios-simulator ]
                        libraries:       [ '/System/Library/Frameworks/SwiftUICore.framework/SwiftUICore', 
                                           '/System/Library/Frameworks/UIKit.framework/UIKit' ]
                    exports:
                      - targets:         [ x86_64-ios-simulator, arm64-ios-simulator ]
                        symbols:         [ '_$s7SwiftUI4FormVMn' ]
                    ...

                    """);
                Write("System/Library/Frameworks/SwiftUICore.framework/SwiftUICore.tbd",
                    "--- !tapi-tbd\ntbd-version:     4\ninstall-name:    '/System/Library/Frameworks/SwiftUICore.framework/SwiftUICore'\n" +
                    (restrictSwiftUICore ? "allowable-clients:\n  - targets:         [ arm64-ios-simulator ]\n    clients:         [ SwiftUI ]\n" : "") +
                    "exports:\n  - targets:         [ arm64-ios-simulator ]\n    symbols:         [ '_$s7SwiftUI4TextVMn' ]\n...\n");
                Write("System/Library/Frameworks/UIKit.framework/UIKit.tbd",
                    "--- !tapi-tbd\ntbd-version:     4\ninstall-name:    '/System/Library/Frameworks/UIKit.framework/UIKit'\n...\n");
                Write("System/Library/Frameworks/Foundation.framework/Foundation.tbd",
                    "--- !tapi-tbd\ntbd-version:     4\ninstall-name:    '/System/Library/Frameworks/Foundation.framework/Foundation'\n...\n");
            }

            private void Write(string relative, string text)
            {
                var file = System.IO.Path.Combine(Path, relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                File.WriteAllText(file, text);
            }

            public void Dispose()
            {
                try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
            }
        }

        private const string SwiftUIInstallName = "/System/Library/Frameworks/SwiftUI.framework/SwiftUI";
        private const string FoundationInstallName = "/System/Library/Frameworks/Foundation.framework/Foundation";

        [Fact]
        public void ParseImports_ReadsEachArchitecturesImportsWithLeafAndWeakFlag()
        {
            var imports = OsFrameworkWeakLinking.ParseImports(WeakSwiftUIImports);

            Assert.Equal(6, imports.Count);
            Assert.Contains(new OsFrameworkWeakLinking.ImportedSymbol("arm64", "_$s7SwiftUI4TextVMn", "SwiftUI", true), imports);
            Assert.Contains(new OsFrameworkWeakLinking.ImportedSymbol("arm64", "_swift_release", "libswiftCore", false), imports);
            Assert.Contains(new OsFrameworkWeakLinking.ImportedSymbol("x86_64", "_$s7SwiftUI4TextVMn", "SwiftUI", true), imports);
            Assert.Contains(imports, i => i.From == "<weak-def-coalesce>");
        }

        [Theory]
        [InlineData("/System/Library/Frameworks/SwiftUI.framework/SwiftUI", "SwiftUI")]
        [InlineData("/System/Library/Frameworks/Foundation.framework/Versions/C/Foundation", "Foundation")]
        [InlineData("/usr/lib/swift/libswiftCore.dylib", "libswiftCore")]
        [InlineData("/usr/lib/libSystem.B.dylib", "libSystem")]
        public void ImportLeaf_IsTheInstallNamesFileUpToItsFirstDot(string installName, string expected)
            => Assert.Equal(expected, OsFrameworkWeakLinking.ImportLeaf(installName));

        [Theory]
        [InlineData("1", "macosx")]
        [InlineData("2", "iphoneos")]
        [InlineData("3", "appletvos")]
        [InlineData("6", "macosx")]
        [InlineData("7", "iphonesimulator")]
        [InlineData("8", "appletvsimulator")]
        [InlineData("IOSSIMULATOR", "iphonesimulator")]
        [InlineData("4", null)]
        public void BuildPlatformSdk_MapsTheBuildVersionPlatformToItsSdk(string platform, string? expected)
        {
            var otool = $"Load command 10\n      cmd LC_BUILD_VERSION\n  cmdsize 32\n platform {platform}\n    minos 15.0\n";
            Assert.Equal(expected, OsFrameworkWeakLinking.BuildPlatformSdk(otool));
            Assert.Null(OsFrameworkWeakLinking.BuildPlatformSdk(AllWeak));
        }

        [Fact]
        public void ReexportsRestrictedLibrary_IsTrueOnlyForAnUmbrellaReexportingAnAllowedClientsOnlyLibrary()
        {
            using var sdk = new StubSdk();
            Assert.True(OsFrameworkWeakLinking.ReexportsRestrictedLibrary(sdk.Path, SwiftUIInstallName));
            Assert.False(OsFrameworkWeakLinking.ReexportsRestrictedLibrary(sdk.Path, FoundationInstallName));
            Assert.False(OsFrameworkWeakLinking.ReexportsRestrictedLibrary(sdk.Path, "/System/Library/Frameworks/Missing.framework/Missing"));

            using var open = new StubSdk(restrictSwiftUICore: false);
            Assert.False(OsFrameworkWeakLinking.ReexportsRestrictedLibrary(open.Path, SwiftUIInstallName));
        }

        [Fact]
        public void UnsafeStrongLoads_AcceptsOnlyARestrictedUmbrellaWhoseEveryImportIsWeak()
        {
            using var sdk = new StubSdk();
            var weakImports = OsFrameworkWeakLinking.ParseImports(WeakSwiftUIImports);

            Assert.Empty(OsFrameworkWeakLinking.UnsafeStrongLoads(new[] { SwiftUIInstallName }, weakImports, sdk.Path));
            Assert.Equal(new[] { SwiftUIInstallName }, OsFrameworkWeakLinking.UnsafeStrongLoads(
                new[] { SwiftUIInstallName }, OsFrameworkWeakLinking.ParseImports(StrongSwiftUIImport), sdk.Path));
            Assert.Equal(new[] { FoundationInstallName }, OsFrameworkWeakLinking.UnsafeStrongLoads(
                new[] { SwiftUIInstallName, FoundationInstallName }, weakImports, sdk.Path));
            Assert.Equal(new[] { SwiftUIInstallName }, OsFrameworkWeakLinking.UnsafeStrongLoads(
                new[] { SwiftUIInstallName }, weakImports, sdkPath: null));
        }

        [Fact]
        public void ParseLoadCommands_ReadsEveryDylibLoadWithItsKind_AndIgnoresOtherCommands()
        {
            var loads = OsFrameworkWeakLinking.ParseLoadCommands(StrongFoundation);

            Assert.Equal(6, loads.Count);
            Assert.Contains(new OsFrameworkWeakLinking.LoadedDylib(
                "/System/Library/Frameworks/CryptoKit.framework/CryptoKit", OsFrameworkWeakLinking.LoadKind.Weak), loads);
            Assert.Contains(new OsFrameworkWeakLinking.LoadedDylib(
                "/System/Library/Frameworks/Foundation.framework/Foundation", OsFrameworkWeakLinking.LoadKind.Load), loads);
            Assert.DoesNotContain(loads, l => l.InstallName == "/usr/lib/swift");
        }

        [Fact]
        public void StrongOsResidentLoads_NamesSystemLoadsThatAreNotWeak_AndNeverUsrLib()
        {
            var strong = OsFrameworkWeakLinking.StrongOsResidentLoads(OsFrameworkWeakLinking.ParseLoadCommands(StrongFoundation));

            Assert.Equal(new[]
            {
                "/System/Library/Frameworks/Foundation.framework/Foundation",
                "/System/iOSSupport/System/Library/Frameworks/UIKit.framework/Versions/A/UIKit",
            }, strong);
            Assert.Empty(OsFrameworkWeakLinking.StrongOsResidentLoads(OsFrameworkWeakLinking.ParseLoadCommands(AllWeak)));
        }

        [Theory]
        [InlineData("/System/Library/Frameworks/CryptoKit.framework/CryptoKit", "CryptoKit")]
        [InlineData("/System/Library/Frameworks/Foundation.framework/Versions/C/Foundation", "Foundation")]
        [InlineData("/System/iOSSupport/System/Library/Frameworks/UIKit.framework/Versions/A/UIKit", "UIKit")]
        [InlineData("/System/Library/SubFrameworks/AppIntentsTypeSupport.framework/AppIntentsTypeSupport", "AppIntentsTypeSupport")]
        [InlineData("/usr/lib/swift/libswiftCore.dylib", null)]
        [InlineData("/System/Library/Frameworks/.framework/X", null)]
        public void FrameworkName_ReadsTheBundleName(string installName, string? expected)
            => Assert.Equal(expected, OsFrameworkWeakLinking.FrameworkName(installName));

        [Theory]
        [InlineData("/System/Library/Frameworks/CryptoKit.framework/CryptoKit", true)]
        [InlineData("/System/iOSSupport/System/Library/Frameworks/UIKit.framework/UIKit", true)]
        [InlineData("/usr/lib/swift/libswiftCore.dylib", false)]
        [InlineData("/usr/lib/libSystem.B.dylib", false)]
        [InlineData("@rpath/Lib.framework/Lib", false)]
        public void IsOsResident_IsTheSystemTreeOnly(string installName, bool expected)
            => Assert.Equal(expected, OsFrameworkWeakLinking.IsOsResident(installName));

        [Fact]
        public void Flags_WeakLinkEachFrameworkOnce_InOrdinalOrder()
        {
            Assert.Equal(
                "-Xlinker -weak_framework -Xlinker CryptoKit -Xlinker -weak_framework -Xlinker Foundation ",
                OsFrameworkWeakLinking.SwiftcFlags(new[] { "Foundation", "CryptoKit", "Foundation" }));
            Assert.Equal(
                "-Wl,-weak_framework,CryptoKit -Wl,-weak_framework,Foundation ",
                OsFrameworkWeakLinking.ClangFlags(new[] { "Foundation", "CryptoKit" }));
        }

        [Fact]
        public void Predict_WeakLinksTheBoundModuleOnlyWhenItIsTheSdksOwnFramework()
        {
            var module = "PredictProbe" + Guid.NewGuid().ToString("N");
            Assert.Equal(new[] { "CryptoKit" },
                OsFrameworkWeakLinking.Predict(module, "CryptoKit", "/sdk/iPhoneOS.sdk/System/Library/Frameworks", "/sdk/iPhoneOS.sdk"));
            Assert.Empty(OsFrameworkWeakLinking.Predict(module, "Lib", "/out/Lib.xcframework/ios-arm64", "/sdk/iPhoneOS.sdk"));

            OsFrameworkWeakLinking.Learn(module, new[] { "Foundation" });
            Assert.Equal(new[] { "Foundation" },
                OsFrameworkWeakLinking.Predict(module, "Lib", "/out/Lib.xcframework/ios-arm64", "/sdk/iPhoneOS.sdk"));
        }

        [Fact]
        public void InvokeSwiftCompiler_RelinksWithEveryStrongOsFrameworkWeakLinked()
        {
            var module = "RelinkProbe" + Guid.NewGuid().ToString("N");
            var runner = new ScriptedCommandRunner();
            runner.QueueResponse("otool -l", 0, StrongFoundation);
            runner.QueueResponse("swiftc -emit-library", 0, "");
            runner.QueueResponse("otool -l", 0, AllWeak);

            SwiftWrapperCompiler.InvokeSwiftCompiler(
                new List<string> { "/tmp/a.swift" }, "/tmp/out/Binary", module,
                "arm64-apple-ios15.0", "/sdk/iPhoneOS.sdk", "/sdk/iPhoneOS.sdk/System/Library/Frameworks",
                runner, NullLogger.Instance, originalModuleName: "CryptoKit");

            var links = runner.Invocations.Where(i => i.Arguments.StartsWith("swiftc -emit-library", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, links.Count);
            Assert.Contains("-Xlinker -weak_framework -Xlinker CryptoKit ", links[0].Arguments);
            Assert.DoesNotContain("-weak_framework -Xlinker Foundation", links[0].Arguments);
            Assert.Contains("-Xlinker -weak_framework -Xlinker CryptoKit ", links[1].Arguments);
            Assert.Contains("-Xlinker -weak_framework -Xlinker Foundation ", links[1].Arguments);
            Assert.Contains("-Xlinker -weak_framework -Xlinker UIKit ", links[1].Arguments);

            // The module's next slice links right the first time.
            Assert.Equal(new[] { "CryptoKit", "Foundation", "UIKit" },
                OsFrameworkWeakLinking.Predict(module, null, null, null));
        }

        [Fact]
        public void InvokeSwiftCompiler_FailsWhenAnOsFrameworkIsStillStrongAfterTheRelink()
        {
            var runner = new ScriptedCommandRunner();
            runner.QueueResponse("otool -l", 0, StrongFoundation);
            runner.QueueResponse("swiftc -emit-library", 0, "");
            runner.QueueResponse("otool -l", 0, StrongFoundation);

            var ex = Assert.Throws<InvalidOperationException>(() => SwiftWrapperCompiler.InvokeSwiftCompiler(
                new List<string> { "/tmp/a.swift" }, "/tmp/out/Binary", "StillStrong" + Guid.NewGuid().ToString("N"),
                "arm64-apple-ios15.0", "/sdk/iPhoneOS.sdk", "/fw", runner, NullLogger.Instance));
            Assert.Contains("Foundation", ex.Message);
            Assert.Contains("UIKit", ex.Message);
            Assert.Contains("strongly", ex.Message);
        }

        [Fact]
        public void InvokeSwiftCompiler_AcceptsAnUmbrellaTheLinkerKeepsStrong_WhenEveryImportFromItIsWeak()
        {
            using var sdk = new StubSdk();
            var module = "KeptStrong" + Guid.NewGuid().ToString("N");
            var runner = new ScriptedCommandRunner();
            runner.QueueResponse("otool -l", 0, StrongSwiftUI);
            runner.QueueResponse("swiftc -emit-library", 0, "");
            runner.QueueResponse("otool -l", 0, StrongSwiftUI);
            runner.QueueResponse("dyld_info -imports", 0, WeakSwiftUIImports);

            SwiftWrapperCompiler.InvokeSwiftCompiler(
                new List<string> { "/tmp/a.swift" }, "/tmp/out/Binary", module,
                "arm64-apple-ios15.0-simulator", sdk.Path, "/fw", runner, NullLogger.Instance);

            var links = runner.Invocations.Where(i => i.Arguments.StartsWith("swiftc -emit-library", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, links.Count);
            Assert.Contains("-Xlinker -weak_framework -Xlinker SwiftUI ", links[1].Arguments);
            Assert.Contains(runner.Invocations, i => i.Arguments.StartsWith("dyld_info -imports", StringComparison.Ordinal));
            Assert.Equal(new[] { "SwiftUI" }, OsFrameworkWeakLinking.Predict(module, null, null, null));
        }

        [Fact]
        public void InvokeSwiftCompiler_DoesNotRelinkForAStrongLoadItAlreadyAskedToWeakLink()
        {
            using var sdk = new StubSdk();
            var module = "NoRelink" + Guid.NewGuid().ToString("N");
            OsFrameworkWeakLinking.Learn(module, new[] { "SwiftUI" });
            var runner = new ScriptedCommandRunner();
            runner.QueueResponse("swiftc -emit-library", 0, "");
            runner.QueueResponse("otool -l", 0, StrongSwiftUI);
            runner.QueueResponse("otool -l", 0, StrongSwiftUI);
            runner.QueueResponse("dyld_info -imports", 0, WeakSwiftUIImports);

            SwiftWrapperCompiler.InvokeSwiftCompiler(
                new List<string> { "/tmp/a.swift" }, "/tmp/out/Binary", module,
                "arm64-apple-ios15.0-simulator", sdk.Path, "/fw", runner, NullLogger.Instance);

            var links = runner.Invocations.Where(i => i.Arguments.StartsWith("swiftc -emit-library", StringComparison.Ordinal)).ToList();
            Assert.Single(links);
            Assert.Contains("-Xlinker -weak_framework -Xlinker SwiftUI ", links[0].Arguments);
        }

        [Fact]
        public void InvokeSwiftCompiler_FailsAKeptStrongUmbrellaWithAStrongImport()
        {
            using var sdk = new StubSdk();
            var runner = new ScriptedCommandRunner();
            runner.QueueResponse("otool -l", 0, StrongSwiftUI);
            runner.QueueResponse("swiftc -emit-library", 0, "");
            runner.QueueResponse("otool -l", 0, StrongSwiftUI);
            runner.QueueResponse("dyld_info -imports", 0, StrongSwiftUIImport);

            var ex = Assert.Throws<InvalidOperationException>(() => SwiftWrapperCompiler.InvokeSwiftCompiler(
                new List<string> { "/tmp/a.swift" }, "/tmp/out/Binary", "StrongImport" + Guid.NewGuid().ToString("N"),
                "arm64-apple-ios15.0-simulator", sdk.Path, "/fw", runner, NullLogger.Instance));
            Assert.Contains("SwiftUI", ex.Message);
        }

        [Fact]
        public void LinkWithClang_AcceptsAnUmbrellaTheLinkerKeepsStrong_AndFailsOneWithAStrongImport()
        {
            using var sdk = new StubSdk();
            var ok = new ScriptedCommandRunner();
            ok.QueueResponse("otool -l", 0, StrongSwiftUI);
            ok.QueueResponse("clang -shared", 0, "");
            ok.QueueResponse("otool -l", 0, StrongSwiftUI);
            ok.QueueResponse("dyld_info -imports", 0, WeakSwiftUIImports);
            NativeThunkCompiler.LinkWithClang(
                new[] { "/tmp/t.o" }, "/tmp/out/Binary", "ClangKept" + Guid.NewGuid().ToString("N"),
                "arm64-apple-ios15.0-simulator", sdk.Path, ok, NullLogger.Instance, frameworkSearchPath: "/fw");
            Assert.Contains(ok.Invocations, i => i.Arguments.StartsWith("dyld_info -imports", StringComparison.Ordinal));

            var bad = new ScriptedCommandRunner();
            bad.QueueResponse("otool -l", 0, StrongSwiftUI);
            bad.QueueResponse("clang -shared", 0, "");
            bad.QueueResponse("otool -l", 0, StrongSwiftUI);
            bad.QueueResponse("dyld_info -imports", 0, StrongSwiftUIImport);
            var ex = Assert.Throws<InvalidOperationException>(() => NativeThunkCompiler.LinkWithClang(
                new[] { "/tmp/t.o" }, "/tmp/out/Binary", "ClangStrong" + Guid.NewGuid().ToString("N"),
                "arm64-apple-ios15.0-simulator", sdk.Path, bad, NullLogger.Instance, frameworkSearchPath: "/fw"));
            Assert.Contains("SwiftUI", ex.Message);
        }

        [Fact]
        public void InvokeSwiftCompiler_FailsWhenTheLoadCommandsCannotBeRead()
        {
            var runner = new ScriptedCommandRunner();
            runner.QueueResponse("otool -l", 1, "", "otool: can't open file");

            var ex = Assert.Throws<InvalidOperationException>(() => SwiftWrapperCompiler.InvokeSwiftCompiler(
                new List<string> { "/tmp/a.swift" }, "/tmp/out/Binary", "Unreadable" + Guid.NewGuid().ToString("N"),
                "arm64-apple-ios15.0", "/sdk/iPhoneOS.sdk", "/fw", runner, NullLogger.Instance));
            Assert.Contains("load commands", ex.Message);
        }

        [Fact]
        public void LinkWithClang_RelinksWithEveryStrongOsFrameworkWeakLinked()
        {
            var module = "ClangRelink" + Guid.NewGuid().ToString("N");
            var runner = new ScriptedCommandRunner();
            runner.QueueResponse("otool -l", 0, StrongFoundation);
            runner.QueueResponse("clang -shared", 0, "");
            runner.QueueResponse("otool -l", 0, AllWeak);

            NativeThunkCompiler.LinkWithClang(
                new[] { "/tmp/t.o" }, "/tmp/out/Binary", module, "arm64-apple-ios15.0", "/sdk/iPhoneOS.sdk",
                runner, NullLogger.Instance, frameworkSearchPath: "/sdk/iPhoneOS.sdk/System/Library/Frameworks",
                originalModuleName: "CryptoKit");

            var links = runner.Invocations.Where(i => i.Arguments.StartsWith("clang -shared", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, links.Count);
            Assert.Contains("-Wl,-weak_framework,CryptoKit ", links[0].Arguments);
            Assert.Contains("-Wl,-weak_framework,Foundation ", links[1].Arguments);
            Assert.Contains("-Wl,-weak_framework,UIKit ", links[1].Arguments);
        }

        [Fact]
        public void OsLoadSafetyAudit_FailsAStrongSystemLoad_AndAnEmptyRead()
        {
            var noImports = Array.Empty<OsFrameworkWeakLinking.ImportedSymbol>();
            var strong = OsLoadSafetyAudit.EvaluateLoads("W", "/tmp/W", OsFrameworkWeakLinking.ParseLoadCommands(StrongFoundation), noImports, "/sdk");
            Assert.Equal(2, strong.Failures.Count);
            Assert.All(strong.Failures, f => Assert.Contains("strongly", f));
            Assert.Empty(strong.SafeStrongLoads);

            var weak = OsLoadSafetyAudit.EvaluateLoads("W", "/tmp/W", OsFrameworkWeakLinking.ParseLoadCommands(AllWeak), noImports, "/sdk");
            Assert.Empty(weak.Failures);
            Assert.Equal(2, weak.OsResidentLoadCount);
            Assert.Equal(2, weak.WeakOsResidentLoadCount);

            var empty = OsLoadSafetyAudit.EvaluateLoads("W", "/tmp/W", Array.Empty<OsFrameworkWeakLinking.LoadedDylib>(), noImports, "/sdk");
            Assert.Single(empty.Failures);
            Assert.Contains("loads no OS-resident dylib", empty.Failures[0]);
        }

        [Fact]
        public void OsLoadSafetyAudit_AcceptsAKeptStrongRestrictedUmbrella_OnlyWhileEveryImportFromItIsWeak()
        {
            using var sdk = new StubSdk();
            var loads = OsFrameworkWeakLinking.ParseLoadCommands(StrongSwiftUI);

            var kept = OsLoadSafetyAudit.EvaluateLoads("W", "/tmp/W", loads, OsFrameworkWeakLinking.ParseImports(WeakSwiftUIImports), sdk.Path);
            Assert.Empty(kept.Failures);
            Assert.Equal(new[] { SwiftUIInstallName }, kept.SafeStrongLoads);

            var strongImport = OsLoadSafetyAudit.EvaluateLoads("W", "/tmp/W", loads, OsFrameworkWeakLinking.ParseImports(StrongSwiftUIImport), sdk.Path);
            Assert.Contains(strongImport.Failures, f => f.Contains("SwiftUI", StringComparison.Ordinal));

            using var open = new StubSdk(restrictSwiftUICore: false);
            var unrestricted = OsLoadSafetyAudit.EvaluateLoads("W", "/tmp/W", loads, OsFrameworkWeakLinking.ParseImports(WeakSwiftUIImports), open.Path);
            Assert.Single(unrestricted.Failures);
        }
    }
}
