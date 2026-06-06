// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// End-to-end tests for <see cref="WrapperXCFrameworkMerger"/>. These shell out to real
    /// <c>clang</c> + <c>lipo</c>, so they only run on macOS. They verify the compile-twice +
    /// lipo-merge mechanism that lets a single multi-arch wrapper xcframework serve both Apple
    /// Silicon and Intel (Rosetta) consumers from one <c>runtimes/&lt;rid&gt;/native/</c> tree.
    /// </summary>
    public class WrapperXCFrameworkMergerTests
    {
        private static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        // A shared, fixed LibraryIdentifier for a simulator-style slice present in BOTH passes.
        // In the real flow SliceVariant.SliceId is arch-independent (e.g. "macos-arm64"), so the
        // arm64 and x86_64 compile passes emit the same identifier and the merger can fatten them.
        private const string SimSliceId = "ios-arm64-simulator";
        private const string DeviceSliceId = "ios-arm64"; // primary-only — there is no x86_64 device

        [Fact]
        public void MergeFatSlices_FattensSharedSlice_KeepsPrimaryOnlyDeviceSlice()
        {
            if (!IsMacOS) return; // clang + lipo only

            var tmp = Path.Combine(Path.GetTempPath(), "merger-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var runner = new SystemCommandRunner();
                var primary = Path.Combine(tmp, "Primary.xcframework");
                var secondary = Path.Combine(tmp, "Secondary.xcframework");

                // Primary (arm64 pass): one simulator slice + one device slice, both arm64.
                WriteSliceBinary(runner, primary, SimSliceId, "arm64");
                WriteSliceBinary(runner, primary, DeviceSliceId, "arm64");
                WritePlist(primary, new[]
                {
                    (SimSliceId, "ios", (string?)"simulator", new[] { "arm64" }),
                    (DeviceSliceId, "ios", (string?)null, new[] { "arm64" }),
                });

                // Secondary (x86_64 pass): only the simulator slice (no Intel device exists).
                WriteSliceBinary(runner, secondary, SimSliceId, "x86_64");
                WritePlist(secondary, new[]
                {
                    (SimSliceId, "ios", (string?)"simulator", new[] { "x86_64" }),
                });

                WrapperXCFrameworkMerger.MergeFatSlices(primary, secondary, NullLogger.Instance, runner);

                // The shared simulator slice is now a fat arm64+x86_64 binary.
                var simArchs = LipoArchs(runner, BinaryPath(primary, SimSliceId));
                Assert.Contains("arm64", simArchs);
                Assert.Contains("x86_64", simArchs);

                // The primary-only device slice is untouched (still arm64-only).
                var devArchs = LipoArchs(runner, BinaryPath(primary, DeviceSliceId));
                Assert.Contains("arm64", devArchs);
                Assert.DoesNotContain("x86_64", devArchs);

                // The plist's SupportedArchitectures reflect the union for the fat slice.
                var root = PlistReader.ReadPlistDict(Path.Combine(primary, "Info.plist"), runner, NullLogger.Instance);
                Assert.NotNull(root);
                var slices = XCFrameworkResolver.ParseAvailableLibraries(root!);
                var simSlice = slices.Single(s => s.LibraryIdentifier == SimSliceId);
                Assert.Contains("arm64", simSlice.SupportedArchitectures);
                Assert.Contains("x86_64", simSlice.SupportedArchitectures);
                var devSlice = slices.Single(s => s.LibraryIdentifier == DeviceSliceId);
                Assert.Equal(new[] { "arm64" }, devSlice.SupportedArchitectures);

                // The secondary xcframework is consumed (deleted) after a successful merge.
                Assert.False(Directory.Exists(secondary));
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void MergeFatSlices_FattensSemanticallyEquivalentSlices_WithRenamedSliceIds()
        {
            // SliceVariant.WithArchitecture renames the slice id to embed the active arch:
            // the arm64 primary pass emits "ios-arm64-simulator" and the x86_64 secondary pass
            // emits "ios-x86_64-simulator". Both describe the SAME (platform, variant) target.
            // The merger must match by semantic identity, lipo the two binaries, and present a
            // single fat slice — .NET-for-Apple's NativeReference resolver requires this.
            if (!IsMacOS) return;

            const string primarySimSliceId = "ios-arm64-simulator";
            const string secondarySimSliceId = "ios-x86_64-simulator";

            var tmp = Path.Combine(Path.GetTempPath(), "merger-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var runner = new SystemCommandRunner();
                var primary = Path.Combine(tmp, "Primary.xcframework");
                var secondary = Path.Combine(tmp, "Secondary.xcframework");

                WriteSliceBinary(runner, primary, primarySimSliceId, "arm64");
                WriteSliceBinary(runner, primary, DeviceSliceId, "arm64");
                WritePlist(primary, new[]
                {
                    (primarySimSliceId, "ios", (string?)"simulator", new[] { "arm64" }),
                    (DeviceSliceId, "ios", (string?)null, new[] { "arm64" }),
                });

                WriteSliceBinary(runner, secondary, secondarySimSliceId, "x86_64");
                WritePlist(secondary, new[]
                {
                    (secondarySimSliceId, "ios", (string?)"simulator", new[] { "x86_64" }),
                });

                WrapperXCFrameworkMerger.MergeFatSlices(primary, secondary, NullLogger.Instance, runner);

                // The primary's slice directory survives intact; the secondary's renamed slice
                // directory must NOT have been copied across as a separate top-level slice.
                Assert.True(Directory.Exists(Path.Combine(primary, primarySimSliceId)),
                    "Primary sim slice directory should be retained as the surviving identifier.");
                Assert.False(Directory.Exists(Path.Combine(primary, secondarySimSliceId)),
                    "Secondary's renamed per-arch sim slice directory must not appear as a separate slice.");

                var simArchs = LipoArchs(runner, BinaryPath(primary, primarySimSliceId));
                Assert.Contains("arm64", simArchs);
                Assert.Contains("x86_64", simArchs);

                var root = PlistReader.ReadPlistDict(Path.Combine(primary, "Info.plist"), runner, NullLogger.Instance);
                Assert.NotNull(root);
                var slices = XCFrameworkResolver.ParseAvailableLibraries(root!);
                // Exactly two slices: the fat sim slice (keyed by primary id) + the arm64-only device slice.
                Assert.Equal(2, slices.Count);
                var simSlice = slices.Single(s => s.LibraryIdentifier == primarySimSliceId);
                Assert.Equal("ios", simSlice.SupportedPlatform);
                Assert.Equal("simulator", simSlice.SupportedPlatformVariant);
                Assert.Contains("arm64", simSlice.SupportedArchitectures);
                Assert.Contains("x86_64", simSlice.SupportedArchitectures);
                Assert.DoesNotContain(slices, s => s.LibraryIdentifier == secondarySimSliceId);

                Assert.False(Directory.Exists(secondary));
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void MergeFatSlices_FailureAfterLipo_LeavesPrimaryConsistent()
        {
            // Transactional invariant: if the merge throws AFTER a slice was already lipo-fattened
            // (here the secondary-only ditto fails), the primary xcframework must be left exactly as
            // it was — never a fat binary advertised by a stale single-arch Info.plist. The resolver
            // keys slice selection on the plist's SupportedArchitectures; a fat binary paired with a
            // single-arch plist is denied for the folded arch → DllNotFound for Rosetta/x64 consumers.
            if (!IsMacOS) return;

            const string secondaryOnlySliceId = "tvos-x86_64-simulator";

            var tmp = Path.Combine(Path.GetTempPath(), "merger-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var real = new SystemCommandRunner();
                var primary = Path.Combine(tmp, "Primary.xcframework");
                var secondary = Path.Combine(tmp, "Secondary.xcframework");

                // Primary: arm64 sim slice (the fold target) + arm64-only device slice.
                WriteSliceBinary(real, primary, SimSliceId, "arm64");
                WriteSliceBinary(real, primary, DeviceSliceId, "arm64");
                WritePlist(primary, new[]
                {
                    (SimSliceId, "ios", (string?)"simulator", new[] { "arm64" }),
                    (DeviceSliceId, "ios", (string?)null, new[] { "arm64" }),
                });

                // Secondary: the matching x86_64 sim slice (folds into primary's sim slice) PLUS an
                // unmatched slice that lands in the secondary-only ditto path — which we force to fail
                // so the throw happens strictly after the sim slice was already fattened.
                WriteSliceBinary(real, secondary, SimSliceId, "x86_64");
                WriteSliceBinary(real, secondary, secondaryOnlySliceId, "x86_64");
                WritePlist(secondary, new[]
                {
                    (SimSliceId, "ios", (string?)"simulator", new[] { "x86_64" }),
                    (secondaryOnlySliceId, "tvos", (string?)"simulator", new[] { "x86_64" }),
                });

                // Pass-through runner that forces the (post-lipo) ditto to fail.
                var failingRunner = new FailOnCommandRunner(real, failCommand: "ditto");

                Assert.Throws<InvalidOperationException>(() =>
                    WrapperXCFrameworkMerger.MergeFatSlices(primary, secondary, NullLogger.Instance, failingRunner));

                // The sim slice binary must remain single-arch (untouched) — the fold was rolled back.
                var simArchs = LipoArchs(real, BinaryPath(primary, SimSliceId));
                Assert.Contains("arm64", simArchs);
                Assert.DoesNotContain("x86_64", simArchs);

                // The plist must still advertise the sim slice as arm64-only, matching the binary.
                var root = PlistReader.ReadPlistDict(Path.Combine(primary, "Info.plist"), real, NullLogger.Instance);
                Assert.NotNull(root);
                var slices = XCFrameworkResolver.ParseAvailableLibraries(root!);
                var simSlice = slices.Single(s => s.LibraryIdentifier == SimSliceId);
                Assert.Equal(new[] { "arm64" }, simSlice.SupportedArchitectures);

                // Binary and plist agree — no desync.
                Assert.DoesNotContain("x86_64", string.Join(",", simSlice.SupportedArchitectures));

                // No staging/backup residue left behind, and the secondary survives (merge failed).
                Assert.False(Directory.Exists(primary + ".merge-staging"), "staging dir must be cleaned up");
                Assert.False(Directory.Exists(primary + ".superseded"), "superseded backup must not linger");
                Assert.True(Directory.Exists(secondary), "secondary must not be deleted when the merge fails");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void MergeFatSlices_RecoversFromInterruptedPriorCommit()
        {
            // Cross-run recovery: the commit phase renames the live primary to '<primary>.superseded'
            // and then moves staging into its place. A hard kill (process killed / reboot / power loss)
            // BETWEEN those two renames leaves the original intact in '.superseded' but absent at the
            // primary path. The in-process catch only restores within the same run; the next run must
            // heal it. Simulate the interrupted state and assert the merge completes correctly.
            if (!IsMacOS) return; // clang + lipo only

            var tmp = Path.Combine(Path.GetTempPath(), "merger-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var runner = new SystemCommandRunner();
                var primary = Path.Combine(tmp, "Primary.xcframework");
                var secondary = Path.Combine(tmp, "Secondary.xcframework");

                // Original (pre-merge) primary: arm64 sim slice + arm64 device slice.
                WriteSliceBinary(runner, primary, SimSliceId, "arm64");
                WriteSliceBinary(runner, primary, DeviceSliceId, "arm64");
                WritePlist(primary, new[]
                {
                    (SimSliceId, "ios", (string?)"simulator", new[] { "arm64" }),
                    (DeviceSliceId, "ios", (string?)null, new[] { "arm64" }),
                });

                // Secondary (x86_64 sim) — still on disk because the prior run was killed before it
                // reached the post-swap secondary cleanup.
                WriteSliceBinary(runner, secondary, SimSliceId, "x86_64");
                WritePlist(secondary, new[]
                {
                    (SimSliceId, "ios", (string?)"simulator", new[] { "x86_64" }),
                });

                // Simulate the kill AFTER the first commit rename (live primary moved aside) and
                // BEFORE the second (staging moved into place): primary path is gone, the original
                // survives only as '.superseded'.
                Directory.Move(primary, primary + ".superseded");
                Assert.False(Directory.Exists(primary));
                Assert.True(Directory.Exists(primary + ".superseded"));

                WrapperXCFrameworkMerger.MergeFatSlices(primary, secondary, NullLogger.Instance, runner);

                // The primary is restored AND the interrupted fold is redone: the sim slice is now fat.
                Assert.True(Directory.Exists(primary), "primary must be recovered from '.superseded'");
                var simArchs = LipoArchs(runner, BinaryPath(primary, SimSliceId));
                Assert.Contains("arm64", simArchs);
                Assert.Contains("x86_64", simArchs);

                // No residue, and the secondary is consumed by the successful re-merge.
                Assert.False(Directory.Exists(primary + ".superseded"), "recovered superseded must not linger");
                Assert.False(Directory.Exists(primary + ".merge-staging"), "staging must be cleaned up");
                Assert.False(Directory.Exists(secondary), "secondary must be consumed by the successful merge");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { /* best effort */ }
            }
        }

        // ── helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Delegates every command to a real runner except those whose program name equals
        /// <c>failCommand</c>, which return a non-zero exit so the merge throws at that step.
        /// </summary>
        private sealed class FailOnCommandRunner : ICommandRunner
        {
            private readonly ICommandRunner _inner;
            private readonly string _failCommand;
            public FailOnCommandRunner(ICommandRunner inner, string failCommand)
            {
                _inner = inner;
                _failCommand = failCommand;
            }
            public (int ExitCode, string StdOut, string StdErr) Run(
                string command, string arguments, int timeoutMs = 30000)
            {
                if (string.Equals(command, _failCommand, StringComparison.Ordinal))
                    return (1, string.Empty, $"forced failure of '{command}' for transactional-merge test");
                return _inner.Run(command, arguments, timeoutMs);
            }
        }

        private static string BinaryPath(string xcfw, string sliceId) =>
            Path.Combine(xcfw, sliceId, "Lib.framework", "Lib");

        private static void WriteSliceBinary(ICommandRunner runner, string xcfw, string sliceId, string arch)
        {
            var bin = BinaryPath(xcfw, sliceId);
            Directory.CreateDirectory(Path.GetDirectoryName(bin)!);

            // Compile a trivial single-arch dylib. The platform load command is irrelevant to
            // `lipo -create`, which merges purely on CPU arch — so plain clang targets suffice.
            var src = Path.Combine(Path.GetTempPath(), "lib-" + Guid.NewGuid().ToString("N") + ".c");
            File.WriteAllText(src, "int swift_bindings_merger_probe(void){return 0;}\n");
            try
            {
                var (exit, _, stderr) = runner.Run(
                    "xcrun",
                    $"clang -arch {arch} -dynamiclib -o \"{bin}\" \"{src}\"",
                    timeoutMs: 60_000);
                Assert.True(exit == 0, $"clang failed building {arch} dylib: {stderr}");
            }
            finally
            {
                try { File.Delete(src); } catch { /* best effort */ }
            }
        }

        private static void WritePlist(
            string xcfw,
            (string id, string platform, string? variant, string[] archs)[] slices)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<plist version=\"1.0\">");
            sb.AppendLine("<dict>");
            sb.AppendLine("  <key>AvailableLibraries</key>");
            sb.AppendLine("  <array>");
            foreach (var (id, platform, variant, archs) in slices)
            {
                sb.AppendLine("    <dict>");
                sb.AppendLine("      <key>BinaryPath</key><string>Lib.framework/Lib</string>");
                sb.AppendLine($"      <key>LibraryIdentifier</key><string>{id}</string>");
                sb.AppendLine("      <key>LibraryPath</key><string>Lib.framework</string>");
                sb.AppendLine("      <key>SupportedArchitectures</key><array>");
                foreach (var a in archs)
                    sb.AppendLine($"        <string>{a}</string>");
                sb.AppendLine("      </array>");
                sb.AppendLine($"      <key>SupportedPlatform</key><string>{platform}</string>");
                if (variant != null)
                    sb.AppendLine($"      <key>SupportedPlatformVariant</key><string>{variant}</string>");
                sb.AppendLine("    </dict>");
            }
            sb.AppendLine("  </array>");
            sb.AppendLine("  <key>CFBundlePackageType</key><string>XFWK</string>");
            sb.AppendLine("  <key>XCFrameworkFormatVersion</key><string>1.0</string>");
            sb.AppendLine("</dict>");
            sb.AppendLine("</plist>");
            File.WriteAllText(Path.Combine(xcfw, "Info.plist"), sb.ToString());
        }

        private static string LipoArchs(ICommandRunner runner, string binary)
        {
            var (exit, stdout, stderr) = runner.Run("xcrun", $"lipo -archs \"{binary}\"", timeoutMs: 30_000);
            Assert.True(exit == 0, $"lipo -archs failed: {stderr}");
            return stdout;
        }
    }
}
