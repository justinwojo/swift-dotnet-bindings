// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    public class XCFrameworkSlicerTests : IDisposable
    {
        private static readonly ILogger _logger = NullLogger.Instance;
        private readonly List<string> _tempDirs = new();

        public void Dispose()
        {
            foreach (var dir in _tempDirs)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        private string MakeTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"slicer_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);
            return dir;
        }

        // Synthetic 7-slice xcframework matching the design doc's Nuke baseline:
        // ios-arm64, ios-arm64-simulator, ios-arm64-maccatalyst, tvos-arm64,
        // tvos-arm64-simulator, macos-arm64, watchos-arm64
        private static readonly (string id, string platform, string? variant)[] SevenSlices = new (string, string, string?)[]
        {
            ("ios-arm64",                "ios",   null),
            ("ios-arm64-simulator",      "ios",   "simulator"),
            ("ios-arm64-maccatalyst",    "ios",   "maccatalyst"),
            ("tvos-arm64",               "tvos",  null),
            ("tvos-arm64-simulator",     "tvos",  "simulator"),
            ("macos-arm64",              "macos", null),
            ("watchos-arm64",            "watchos", null),
        };

        private string CreateFakeXcframework(string rootDir, string moduleName = "Lib", IEnumerable<(string id, string platform, string? variant)>? slices = null)
        {
            slices ??= SevenSlices;
            var xcfwPath = Path.Combine(rootDir, $"{moduleName}.xcframework");
            Directory.CreateDirectory(xcfwPath);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<plist version=\"1.0\">");
            sb.AppendLine("<dict>");
            sb.AppendLine("  <key>AvailableLibraries</key>");
            sb.AppendLine("  <array>");
            foreach (var (id, platform, variant) in slices)
            {
                sb.AppendLine("    <dict>");
                sb.AppendLine($"      <key>BinaryPath</key><string>{moduleName}.framework/{moduleName}</string>");
                sb.AppendLine($"      <key>LibraryIdentifier</key><string>{id}</string>");
                sb.AppendLine($"      <key>LibraryPath</key><string>{moduleName}.framework</string>");
                sb.AppendLine("      <key>SupportedArchitectures</key><array><string>arm64</string></array>");
                sb.AppendLine($"      <key>SupportedPlatform</key><string>{platform}</string>");
                if (variant != null)
                    sb.AppendLine($"      <key>SupportedPlatformVariant</key><string>{variant}</string>");
                sb.AppendLine("    </dict>");

                // Stub slice contents on disk
                var sliceFx = Path.Combine(xcfwPath, id, $"{moduleName}.framework");
                Directory.CreateDirectory(sliceFx);
                File.WriteAllText(Path.Combine(sliceFx, moduleName), "stub-mach-o");
                File.WriteAllText(Path.Combine(sliceFx, $"{id}-marker.txt"), id);
            }
            sb.AppendLine("  </array>");
            sb.AppendLine("  <key>CFBundlePackageType</key><string>XFWK</string>");
            sb.AppendLine("  <key>XCFrameworkFormatVersion</key><string>1.0</string>");
            sb.AppendLine("</dict>");
            sb.AppendLine("</plist>");
            File.WriteAllText(Path.Combine(xcfwPath, "Info.plist"), sb.ToString());
            return xcfwPath;
        }

        private static List<string> ReadSliceIdentifiers(string slicedXcfwPath)
        {
            var slices = XCFrameworkResolver.ParseInfoPlist(Path.Combine(slicedXcfwPath, "Info.plist"));
            return slices.Select(s => s.LibraryIdentifier).OrderBy(s => s).ToList();
        }

        private static List<string> ListSliceDirs(string xcfwPath)
        {
            return Directory.EnumerateDirectories(xcfwPath)
                .Select(Path.GetFileName)
                .Cast<string>()
                .OrderBy(s => s)
                .ToList();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Predicate tests (pure, no I/O)
        // ─────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("ios-arm64",          "ios",   null,           true)]
        [InlineData("ios-arm64",          "ios",   "simulator",    true)]
        [InlineData("ios-arm64",          "ios",   "maccatalyst",  false)]
        [InlineData("ios-arm64",          "tvos",  null,           false)]
        [InlineData("ios-arm64",          "macos", null,           false)]
        [InlineData("tvos-arm64",         "tvos",  null,           true)]
        [InlineData("tvos-arm64",         "tvos",  "simulator",    true)]
        [InlineData("tvos-arm64",         "ios",   null,           false)]
        [InlineData("osx-arm64",          "macos", null,           true)]
        [InlineData("osx-arm64",          "ios",   null,           false)]
        [InlineData("osx-arm64",          "macos", "simulator",    false)]
        [InlineData("maccatalyst-arm64",  "ios",   "maccatalyst",  true)]
        [InlineData("maccatalyst-arm64",  "ios",   null,           false)]
        [InlineData("maccatalyst-arm64",  "macos", null,           false)]
        public void MatchesRid_PredicateTable(string rid, string platform, string? variant, bool expected)
        {
            var slice = new XCFrameworkSlice
            {
                BinaryPath = "Lib.framework/Lib",
                LibraryIdentifier = "test",
                LibraryPath = "Lib.framework",
                SupportedArchitectures = new List<string> { "arm64" },
                SupportedPlatform = platform,
                SupportedPlatformVariant = variant,
            };
            Assert.Equal(expected, XCFrameworkSlicer.MatchesRid(slice, rid));
        }

        [Fact]
        public void MatchesRid_UnsupportedRid_Throws()
        {
            var slice = new XCFrameworkSlice
            {
                BinaryPath = "", LibraryIdentifier = "x", LibraryPath = "x",
                SupportedArchitectures = new List<string>(), SupportedPlatform = "ios"
            };
            Assert.Throws<ArgumentException>(() => XCFrameworkSlicer.MatchesRid(slice, "linux-x64"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // End-to-end Slice() tests (require real ditto on macOS)
        // ─────────────────────────────────────────────────────────────────────

        private static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        [Theory]
        [InlineData("ios-arm64",          new[] { "ios-arm64", "ios-arm64-simulator" })]
        [InlineData("tvos-arm64",         new[] { "tvos-arm64", "tvos-arm64-simulator" })]
        [InlineData("osx-arm64",          new[] { "macos-arm64" })]
        [InlineData("maccatalyst-arm64",  new[] { "ios-arm64-maccatalyst" })]
        public void Slice_FilteredCorrectlyPerRid(string rid, string[] expectedSliceIds)
        {
            if (!IsMacOS) return; // ditto-only

            var root = MakeTempDir();
            var src = CreateFakeXcframework(root);
            var dst = Path.Combine(root, $"sliced-{rid}", "Lib.xcframework");

            XCFrameworkSlicer.Slice(src, rid, dst, _logger);

            Assert.Equal(expectedSliceIds.OrderBy(s => s).ToList(), ListSliceDirs(dst));
            Assert.Equal(expectedSliceIds.OrderBy(s => s).ToList(), ReadSliceIdentifiers(dst));

            // Pruned Info.plist preserves CFBundlePackageType and XCFrameworkFormatVersion.
            var doc = new XmlDocument();
            doc.Load(Path.Combine(dst, "Info.plist"));
            Assert.NotNull(doc.SelectSingleNode("/plist/dict/key[text()='CFBundlePackageType']"));
            Assert.NotNull(doc.SelectSingleNode("/plist/dict/key[text()='XCFrameworkFormatVersion']"));
        }

        [Fact]
        public void Slice_ZeroMatch_ThrowsSwiftBind050()
        {
            if (!IsMacOS) return;

            var root = MakeTempDir();
            // Build an xcframework with only watchOS slices — none of our 4 RIDs match.
            var src = CreateFakeXcframework(root, slices: new (string, string, string?)[]
            {
                ("watchos-arm64",         "watchos", null),
                ("watchos-arm64-simulator", "watchos", "simulator"),
            });
            var dst = Path.Combine(root, "sliced", "Lib.xcframework");

            var ex = Assert.Throws<InvalidOperationException>(
                () => XCFrameworkSlicer.Slice(src, "ios-arm64", dst, _logger));
            Assert.Contains("SWIFTBIND050", ex.Message);
            Assert.Contains("ios-arm64", ex.Message);
        }

        [Fact]
        public void Slice_IdempotentOnAlreadySliced()
        {
            if (!IsMacOS) return;

            var root = MakeTempDir();
            // Pre-sliced source: only contains the slices ios-arm64 expects.
            var src = CreateFakeXcframework(root, slices: new (string, string, string?)[]
            {
                ("ios-arm64",           "ios", null),
                ("ios-arm64-simulator", "ios", "simulator"),
            });
            var dst = Path.Combine(root, "sliced", "Lib.xcframework");

            XCFrameworkSlicer.Slice(src, "ios-arm64", dst, _logger);
            Assert.Equal(new[] { "ios-arm64", "ios-arm64-simulator" }.OrderBy(s => s).ToList(), ListSliceDirs(dst));
            Assert.Equal(new[] { "ios-arm64", "ios-arm64-simulator" }.OrderBy(s => s).ToList(), ReadSliceIdentifiers(dst));

            // Slice second time → same result, still valid.
            XCFrameworkSlicer.Slice(src, "ios-arm64", dst, _logger);
            Assert.Equal(new[] { "ios-arm64", "ios-arm64-simulator" }.OrderBy(s => s).ToList(), ListSliceDirs(dst));
        }

        [Fact]
        public void Slice_PreservesSymlinksAndExecBits()
        {
            if (!IsMacOS) return;

            var root = MakeTempDir();
            var src = CreateFakeXcframework(root);

            // Inside macos-arm64/Lib.framework/, add Versions/A/Lib + Lib symlink to Versions/A/Lib.
            var fxDir = Path.Combine(src, "macos-arm64", "Lib.framework");
            // wipe stub binary — we'll rebuild with a Versions structure
            File.Delete(Path.Combine(fxDir, "Lib"));
            var versionsA = Path.Combine(fxDir, "Versions", "A");
            Directory.CreateDirectory(versionsA);
            var realBinary = Path.Combine(versionsA, "Lib");
            File.WriteAllText(realBinary, "stub-mach-o");
            // exec bit
            Run("chmod", $"+x \"{realBinary}\"");
            // top-level symlinks: Versions/Current -> A, Lib -> Versions/Current/Lib
            Run("ln", $"-s A \"{Path.Combine(fxDir, "Versions", "Current")}\"");
            Run("ln", $"-s Versions/Current/Lib \"{Path.Combine(fxDir, "Lib")}\"");

            var dst = Path.Combine(root, "sliced-osx", "Lib.xcframework");
            XCFrameworkSlicer.Slice(src, "osx-arm64", dst, _logger);

            var dstFx = Path.Combine(dst, "macos-arm64", "Lib.framework");
            // exec bit preserved
            var (statCode, statOut, _) = RunCapture("stat", $"-f %p \"{Path.Combine(dstFx, "Versions", "A", "Lib")}\"");
            Assert.Equal(0, statCode);
            // octal mode contains an exec bit (last digit odd or has 1/3/5/7)
            var modeDigits = statOut.Trim();
            var lastDigit = modeDigits[modeDigits.Length - 1] - '0';
            Assert.True((lastDigit & 1) == 1, $"Expected exec bit on copied binary; mode = {modeDigits}");

            // symlinks preserved
            var (lsCode, lsOut, _) = RunCapture("ls", $"-l \"{Path.Combine(dstFx, "Lib")}\"");
            Assert.Equal(0, lsCode);
            Assert.Contains("->", lsOut);
        }

        [Fact]
        public void Slice_BinaryPlistInput_ParsesViaPlutil()
        {
            if (!IsMacOS) return;

            var root = MakeTempDir();
            var src = CreateFakeXcframework(root);
            // Convert the XML Info.plist to binary in-place via plutil
            var plistPath = Path.Combine(src, "Info.plist");
            var (code, _, err) = RunCapture("plutil", $"-convert binary1 \"{plistPath}\"");
            Assert.True(code == 0, $"plutil convert failed: {err}");


            var dst = Path.Combine(root, "sliced-bin", "Lib.xcframework");
            XCFrameworkSlicer.Slice(src, "ios-arm64", dst, _logger);

            Assert.Equal(new[] { "ios-arm64", "ios-arm64-simulator" }.OrderBy(s => s).ToList(),
                ListSliceDirs(dst));
        }

        [Fact]
        public void Slice_MissingSourceXcframework_Throws()
        {
            var ex = Assert.Throws<DirectoryNotFoundException>(
                () => XCFrameworkSlicer.Slice("/nonexistent/Lib.xcframework", "ios-arm64",
                    Path.Combine(MakeTempDir(), "out"), _logger));
            Assert.Contains("Lib.xcframework", ex.Message);
        }

        [Fact]
        public void Slice_MissingInfoPlist_Throws()
        {
            var root = MakeTempDir();
            var emptyXcfw = Path.Combine(root, "Lib.xcframework");
            Directory.CreateDirectory(emptyXcfw);
            Assert.Throws<FileNotFoundException>(
                () => XCFrameworkSlicer.Slice(emptyXcfw, "ios-arm64",
                    Path.Combine(root, "out"), _logger));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private static void Run(string cmd, string args)
        {
            var (code, _, err) = RunCapture(cmd, args);
            if (code != 0)
                throw new InvalidOperationException($"{cmd} failed: {err}");
        }

        private static (int Code, string Out, string Err) RunCapture(string cmd, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = cmd,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, o, e);
        }
    }
}
