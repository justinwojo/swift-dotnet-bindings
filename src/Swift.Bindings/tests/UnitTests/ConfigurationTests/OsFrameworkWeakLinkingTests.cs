// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
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
            var strong = OsLoadSafetyAudit.EvaluateLoads("W", "/tmp/W", OsFrameworkWeakLinking.ParseLoadCommands(StrongFoundation));
            Assert.Equal(2, strong.Failures.Count);
            Assert.All(strong.Failures, f => Assert.Contains("strongly", f));

            var weak = OsLoadSafetyAudit.EvaluateLoads("W", "/tmp/W", OsFrameworkWeakLinking.ParseLoadCommands(AllWeak));
            Assert.Empty(weak.Failures);
            Assert.Equal(2, weak.OsResidentLoadCount);
            Assert.Equal(2, weak.WeakOsResidentLoadCount);

            var empty = OsLoadSafetyAudit.EvaluateLoads("W", "/tmp/W", Array.Empty<OsFrameworkWeakLinking.LoadedDylib>());
            Assert.Single(empty.Failures);
            Assert.Contains("loads no OS-resident dylib", empty.Failures[0]);
        }
    }
}
