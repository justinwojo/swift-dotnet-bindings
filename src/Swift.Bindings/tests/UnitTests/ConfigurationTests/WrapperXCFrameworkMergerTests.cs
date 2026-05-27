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

        // ── helpers ──────────────────────────────────────────────────────────

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
