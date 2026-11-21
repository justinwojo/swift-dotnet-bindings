// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BindingsGeneration.Tests.OsFrameworkWeakLinkingTests;

namespace BindingsGeneration.Tests
{
    /// <summary>The OS symbol inventory a run reads is process-wide, so these tests run alone.</summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class OsRunInventoryCollection
    {
        public const string Name = "OsRunInventory";
    }

    /// <summary>
    /// Load safety for the binaries the SDK links itself (its second wrapper and SwiftUI bridge slices):
    /// <c>--check-os-loads</c> answers with the generator's own rule against the run's OS symbol inventory,
    /// so an umbrella the linker keeps strong passes only when the inventory's OS has it on the binary's
    /// platform variant; and a supplied inventory decides the check of every link of the run, the
    /// generator's own included.
    /// </summary>
    [Collection(OsRunInventoryCollection.Name)]
    public sealed class OsLoadCheckCommandTests : IDisposable
    {
        private const string Binary = "/out/W.framework/W";
        private const string NewKit = "/System/Library/Frameworks/NewKit.framework/NewKit";
        private readonly OsSymbolInventory _previous = OsFrameworkWeakLinking.RunInventory;

        public void Dispose() => OsFrameworkWeakLinking.RunInventory = _previous;

        private static string StrongLoad(string installName) => $"""
            Load command 13
                      cmd LC_LOAD_DYLIB
                  cmdsize 88
                     name {installName} (offset 24)
            """;

        private static (int ExitCode, string Stdout, ScriptedCommandRunner Runner) Check(
            string loadCommands, string? imports, string? sdkPath, string? inventoryPath = null, int toolExit = 0)
        {
            var runner = new ScriptedCommandRunner();
            runner.QueueResponse("otool -l", toolExit, loadCommands, toolExit == 0 ? "" : "cannot open");
            if (imports is not null)
                runner.QueueResponse("dyld_info -imports", 0, imports);
            using var stdout = new StringWriter();
            var exit = OsLoadCheckCommand.Run(Binary, sdkPath, inventoryPath, runner, stdout, NullLogger.Instance);
            return (exit, stdout.ToString(), runner);
        }

        [Fact]
        public void AcceptsAnUmbrellaTheLinkerKeptStrong_WhenEveryImportIsWeak_AndTheInventoryOsHasIt()
        {
            using var sdk = new StubSdk();

            var (exit, stdout, _) = Check(StrongSwiftUI, WeakSwiftUIImports, sdk.Path);

            Assert.Equal(0, exit);
            Assert.Equal("", stdout);
        }

        [Fact]
        public void PrintsTheUmbrella_WhenOneImportFromItIsStrong()
        {
            using var sdk = new StubSdk();

            var (exit, stdout, _) = Check(StrongSwiftUI, StrongSwiftUIImport, sdk.Path);

            Assert.Equal(0, exit);
            Assert.Equal(SwiftUIInstallName, stdout);
        }

        /// <summary>
        /// The case the SDK's former shell check accepted: an umbrella re-exporting a restricted library,
        /// every import from it weak, on a framework the generating SDK added. dyld refuses a strong load
        /// of a framework the running OS lacks however weak its imports are.
        /// </summary>
        [Fact]
        public void PrintsAKeptUmbrella_TheInventoryOsDoesNotHave()
        {
            using var sdk = new StubSdk();

            var (exit, stdout, _) = Check(StrongLoad(NewKit), """
                /out/W.framework/W [arm64]:
                    -imports:
                      0x0000  _$s6NewKit5ThingVMn [weak-import] (from NewKit)
                """, sdk.Path);

            Assert.Equal(0, exit);
            Assert.Equal(NewKit, stdout);
        }

        [Fact]
        public void PrintsAStrongLoad_ThatReexportsNoRestrictedLibrary_AndOneTheSdkHasNoStubFor()
        {
            using var sdk = new StubSdk();
            const string foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
            const string cryptoKit = "/System/Library/Frameworks/CryptoKit.framework/CryptoKit";

            var (exit, stdout, _) = Check(StrongLoad(foundation) + "\n" + StrongLoad(cryptoKit), """
                /out/W.framework/W [arm64]:
                    -imports:
                      0x0000  _$s10Foundation4DataVMn [weak-import] (from Foundation)
                      0x0001  _$s9CryptoKit3SHAVMn [weak-import] (from CryptoKit)
                """, sdk.Path);

            Assert.Equal(0, exit);
            Assert.Equal($"{cryptoKit} {foundation}", stdout);
        }

        [Fact]
        public void PassesAnAllWeakBinary_WithoutReadingItsImports()
        {
            using var sdk = new StubSdk();

            var (exit, stdout, runner) = Check("""
                Load command 13
                          cmd LC_LOAD_WEAK_DYLIB
                      cmdsize 88
                         name /System/Library/Frameworks/SwiftUI.framework/SwiftUI (offset 24)
                Load command 14
                          cmd LC_LOAD_DYLIB
                      cmdsize 56
                         name /usr/lib/libobjc.A.dylib (offset 24)
                """, imports: null, sdk.Path);

            Assert.Equal(0, exit);
            Assert.Equal("", stdout);
            Assert.DoesNotContain(runner.Invocations, i => i.Arguments.StartsWith("dyld_info", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("tool fails")]
        [InlineData("no load commands")]
        [InlineData("no import table")]
        [InlineData("no sdk")]
        public void FailsClosed_WhenItCannotReadTheBinary(string failure)
        {
            using var sdk = new StubSdk();

            var (exit, stdout, _) = failure switch
            {
                "tool fails" => Check(StrongSwiftUI, WeakSwiftUIImports, sdk.Path, toolExit: 1),
                "no load commands" => Check("", WeakSwiftUIImports, sdk.Path),
                "no import table" => Check(StrongSwiftUI, "", sdk.Path),
                _ => Check(StrongSwiftUI, WeakSwiftUIImports, sdkPath: null),
            };

            Assert.Equal(1, exit);
            Assert.Equal("", stdout);
        }

        /// <summary>
        /// A supplied binary inventory replaces the shipped one for every check of the run: SwiftUI's
        /// kept-strong load passes against the shipped inventory and fails against one whose OS ships
        /// none of the SwiftUI symbols the binary imports.
        /// </summary>
        [Fact]
        public void ASuppliedBinaryInventory_DecidesTheCheck()
        {
            using var sdk = new StubSdk();
            using var inventory = new OsSymbolInventoryTests.TempFile(
                OsSymbolInventoryTests.BuildBinary(new Dictionary<string, int> { ["$s10Foundation4DataVMn"] = 0b11 }));

            Assert.Equal("", Check(StrongSwiftUI, WeakSwiftUIImports, sdk.Path).Stdout);
            Assert.Equal(SwiftUIInstallName, Check(StrongSwiftUI, WeakSwiftUIImports, sdk.Path, inventory.Path).Stdout);
        }

        /// <summary>A check without a supplied inventory reads the shipped one, whatever an earlier check of the process supplied.</summary>
        [Fact]
        public void ASuppliedBinaryInventory_DoesNotOutliveItsRun()
        {
            using var sdk = new StubSdk();
            using var inventory = new OsSymbolInventoryTests.TempFile(
                OsSymbolInventoryTests.BuildBinary(new Dictionary<string, int> { ["$s10Foundation4DataVMn"] = 0b11 }));

            Assert.Equal(SwiftUIInstallName, Check(StrongSwiftUI, WeakSwiftUIImports, sdk.Path, inventory.Path).Stdout);
            Assert.Equal("", Check(StrongSwiftUI, WeakSwiftUIImports, sdk.Path).Stdout);
        }

        /// <summary>A generation run that names no module leaves no inventory an earlier run of the process supplied.</summary>
        [Fact]
        public void ASuppliedInventory_DoesNotOutliveItsRun_IntoARunNamingNoModule()
        {
            using var inventory = new OsSymbolInventoryTests.TempFile(
                OsSymbolInventoryTests.BuildBinary(new Dictionary<string, int> { ["$s10Foundation4DataVMn"] = 0b11 }));
            OsSymbolFloorContext.LoadForRun(inventory.Path);
            Assert.NotSame(OsSymbolInventory.Shipped, OsFrameworkWeakLinking.RunInventory);

            Assert.Null(OsSymbolFloorContext.ResolveForGeneration(
                inventoryPath: null, moduleName: null, platformInfo: null!, isOsFramework: true,
                new ScriptedCommandRunner(), NullLogger.Instance));

            Assert.Same(OsSymbolInventory.Shipped, OsFrameworkWeakLinking.RunInventory);
        }

        [Fact]
        public void ASuppliedBinaryInventory_DecidesTheGeneratorsOwnWrapperLinkCheck()
        {
            using var sdk = new StubSdk();
            using var inventory = new OsSymbolInventoryTests.TempFile(
                OsSymbolInventoryTests.BuildBinary(new Dictionary<string, int> { ["$s10Foundation4DataVMn"] = 0b11 }));
            OsSymbolFloorContext.LoadForRun(inventory.Path);
            var runner = new ScriptedCommandRunner();
            runner.QueueResponse("otool -l", 0, StrongSwiftUI);
            runner.QueueResponse("swiftc -emit-library", 0, "");
            runner.QueueResponse("otool -l", 0, StrongSwiftUI);
            runner.QueueResponse("dyld_info -imports", 0, WeakSwiftUIImports);

            var ex = Assert.Throws<InvalidOperationException>(() => SwiftWrapperCompiler.InvokeSwiftCompiler(
                new List<string> { "/tmp/a.swift" }, "/tmp/out/Binary", "SuppliedInventory" + Guid.NewGuid().ToString("N"),
                "arm64-apple-ios15.0-simulator", sdk.Path, "/fw", runner, NullLogger.Instance));

            Assert.Contains(SwiftUIInstallName, ex.Message);
            Assert.NotSame(OsSymbolInventory.Shipped, OsFrameworkWeakLinking.RunInventory);
        }
    }
}
