// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Drives the real <c>Sdk/scripts/unsafe-strong-os-loads.sh</c>, the load-safety check
    /// Sdk.targets runs on the second wrapper slice and the second SwiftUI bridge slice it links.
    /// The script applies the generator's rule (<c>OsFrameworkWeakLinking.UnsafeStrongLoads</c>):
    /// a strong /System/ load is unsafe unless it is an umbrella the linker keeps strong (its SDK
    /// stub re-exports a library restricted to allowed clients) and every import from it is weak.
    /// otool, dyld_info and the SDK are stubs, so the canned load commands and imports are exact.
    /// </summary>
    public class UnsafeStrongOsLoadsScriptTests : IDisposable
    {
        private const string SwiftUI = "/System/Library/Frameworks/SwiftUI.framework/SwiftUI";
        private const string StoreKit = "/System/Library/Frameworks/StoreKit.framework/StoreKit";
        private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

        private readonly string _dir;
        private readonly string _sdk;
        private readonly string _script;

        public UnsafeStrongOsLoadsScriptTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "swiftbind-strong-loads-" + Guid.NewGuid().ToString("N"));
            _sdk = Path.Combine(_dir, "sdk");
            Directory.CreateDirectory(_sdk);
            _script = Path.Combine(FindRepoRoot(), "src", "Swift.Bindings.Sdk", "Sdk", "scripts", "unsafe-strong-os-loads.sh");
            Assert.True(File.Exists(_script), $"script not found at {_script}");

            // SwiftUI re-exports SwiftUICore, whose stub restricts its clients; StoreKit re-exports
            // only Foundation, which restricts nobody. The SDK has no stub for CryptoKit.
            Stub(SwiftUI, $"""
                --- !tapi-tbd
                tbd-version:     4
                install-name:    '{SwiftUI}'
                reexported-libraries:
                  - targets:         [ x86_64-ios-simulator, arm64-ios-simulator ]
                    libraries:       [ '/System/Library/Frameworks/SwiftUICore.framework/SwiftUICore',
                                       '/System/Library/Frameworks/UIKit.framework/UIKit' ]
                exports:
                  - targets:         [ arm64-ios-simulator ]
                    symbols:         [ '_$s7SwiftUI4TextVMn' ]
                """);
            Stub("/System/Library/Frameworks/SwiftUICore.framework/SwiftUICore", """
                --- !tapi-tbd
                tbd-version:     4
                allowable-clients:
                  - targets:         [ arm64-ios-simulator ]
                    clients:         [ SwiftUI ]
                """);
            Stub("/System/Library/Frameworks/UIKit.framework/UIKit", "--- !tapi-tbd\ntbd-version:     4\n");
            Stub(StoreKit, $"""
                --- !tapi-tbd
                tbd-version:     4
                reexported-libraries:
                  - targets:         [ arm64-ios-simulator ]
                    libraries:       [ '{Foundation}' ]
                """);
            Stub(Foundation, "--- !tapi-tbd\ntbd-version:     4\n");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }

        private void Stub(string installName, string tbd)
        {
            var path = _sdk + installName + ".tbd";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, tbd);
        }

        private static string Load(string cmd, string name) => $"""
            Load command 9
                      cmd {cmd}
                  cmdsize 88
                     name {name} (offset 24)
               time stamp 2 Thu Jan  1 00:00:02 1970
            """;

        private static string Import(string symbol, string leaf, bool weak) =>
            $"      0x0000  {symbol}{(weak ? " [weak-import]" : "")} (from {leaf})";

        // Runs the script with otool and dyld_info stubbed to print the given text; dyld_info's
        // stub also records that it ran.
        private (string Stdout, bool ReadImports) Run(string otool, string imports)
        {
            var (exitCode, stdout, stderr, readImports) = RunRaw(otool, imports, toolExit: 0);
            Assert.True(exitCode == 0, $"script exited {exitCode}: {stderr}");
            return (stdout, readImports);
        }

        private (int ExitCode, string Stdout, string Stderr, bool ReadImports) RunRaw(
            string otool, string imports, int toolExit, bool importsHeader = true)
        {
            var otoolOut = Path.Combine(_dir, "otool.txt");
            var importsOut = Path.Combine(_dir, "imports.txt");
            var marker = Path.Combine(_dir, "dyld_info.ran");
            File.Delete(marker);
            File.WriteAllText(otoolOut, otool + "\n");
            File.WriteAllText(importsOut, importsHeader ? "/out/W.framework/W [arm64]:\n    -imports:\n" + imports + "\n" : imports);
            var otoolStub = Path.Combine(_dir, "otool");
            var dyldInfoStub = Path.Combine(_dir, "dyld_info");
            File.WriteAllText(otoolStub, $"#!/bin/bash\ncat \"{otoolOut}\"\nexit {toolExit}\n");
            File.WriteAllText(dyldInfoStub, $"#!/bin/bash\ntouch \"{marker}\"\ncat \"{importsOut}\"\nexit {toolExit}\n");

            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(_script);
            psi.ArgumentList.Add(Path.Combine(_dir, "W"));
            psi.ArgumentList.Add(_sdk + "/");
            psi.Environment["SWIFTBINDINGS_OTOOL"] = otoolStub;
            psi.Environment["SWIFTBINDINGS_DYLD_INFO"] = dyldInfoStub;
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(otoolStub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(dyldInfoStub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "script timed out");
            return (process.ExitCode, stdout, stderr, File.Exists(marker));
        }

        [Fact]
        public void AcceptsAnUmbrellaTheLinkerKeptStrong_WhenEveryImportFromItIsWeak()
        {
            var (stdout, _) = Run(
                Load("LC_LOAD_DYLIB", SwiftUI) + "\n" + Load("LC_LOAD_DYLIB", "/usr/lib/swift/libswiftCore.dylib")
                    + "\n" + Load("LC_LOAD_WEAK_DYLIB", Foundation),
                Import("_$s7SwiftUI4TextVMn", "SwiftUI", weak: true) + "\n"
                    + Import("_swift_release", "libswiftCore", weak: false));

            Assert.Equal("", stdout);
        }

        [Fact]
        public void FailsTheUmbrella_WhenOneImportFromItIsStrong()
        {
            var (stdout, _) = Run(
                Load("LC_LOAD_DYLIB", SwiftUI),
                Import("_$s7SwiftUI4TextVMn", "SwiftUI", weak: true) + "\n"
                    + Import("_$s7SwiftUI4TextVN", "SwiftUI", weak: false));

            Assert.Equal(SwiftUI, stdout);
        }

        [Fact]
        public void FailsAStrongLoad_ThatReexportsNoRestrictedLibrary_EvenWithEveryImportWeak()
        {
            var (stdout, _) = Run(
                Load("LC_LOAD_DYLIB", StoreKit) + "\n" + Load("LC_LOAD_DYLIB", SwiftUI),
                Import("_$s8StoreKit7ProductVMn", "StoreKit", weak: true) + "\n"
                    + Import("_$s7SwiftUI4TextVMn", "SwiftUI", weak: true));

            Assert.Equal(StoreKit, stdout);
        }

        [Fact]
        public void FailsAStrongLoad_TheSdkHasNoStubFor()
        {
            const string kit = "/System/Library/Frameworks/CryptoKit.framework/CryptoKit";
            var (stdout, _) = Run(Load("LC_LOAD_DYLIB", kit), Import("_$s9CryptoKit3SHAVMn", "CryptoKit", weak: true));

            Assert.Equal(kit, stdout);
        }

        [Fact]
        public void PassesAnAllWeakSlice_WithoutReadingItsImports()
        {
            var (stdout, readImports) = Run(
                Load("LC_LOAD_WEAK_DYLIB", SwiftUI) + "\n" + Load("LC_LOAD_DYLIB", "/usr/lib/libobjc.A.dylib"),
                Import("_$s7SwiftUI4TextVMn", "SwiftUI", weak: false));

            Assert.Equal("", stdout);
            Assert.False(readImports);
        }

        [Theory]
        [InlineData(1, true, "otool -l failed")]                 // a tool exits non-zero
        [InlineData(0, false, "shows no import table")]          // dyld_info prints no import table
        public void FailsClosed_WhenItCannotReadTheSlice(int toolExit, bool importsHeader, string message)
        {
            var (exitCode, stdout, stderr, _) = RunRaw(
                Load("LC_LOAD_DYLIB", SwiftUI), Import("_$s7SwiftUI4TextVMn", "SwiftUI", weak: true), toolExit, importsHeader);

            Assert.NotEqual(0, exitCode);
            Assert.Equal("", stdout);
            Assert.Contains(message, stderr);
        }

        [Fact]
        public void FailsClosed_WhenOtoolShowsNoLoadCommands()
        {
            var (exitCode, _, stderr, _) = RunRaw("", "", toolExit: 0);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("no load commands", stderr);
        }

        private static string FindRepoRoot()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("Cannot find repo root.");
        }
    }
}
