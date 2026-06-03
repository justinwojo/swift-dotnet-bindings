// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Tests for <see cref="NativeLinkageProbe"/>, the static-vs-dynamic classifier that
    /// drives Gap 2's force_load decision. The load-bearing asymmetry: only a genuine
    /// <c>ar</c> archive is <see cref="NativeLinkage.Static"/>; a dylib AND any non-binary
    /// (a TBD/JSON stub, a missing path) must be <see cref="NativeLinkage.Dynamic"/> so the
    /// wrapper never force-loads something that isn't an archive.
    /// </summary>
    public class NativeLinkageProbeTests
    {
        private static readonly NullLogger Logger = NullLogger.Instance;

        /// <summary>Creates a real temp file (so Detect's File.Exists passes) with given bytes.</summary>
        private sealed class TempFile : IDisposable
        {
            public string Path { get; }
            public TempFile(byte[]? content = null)
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), $"linkageprobe_{Guid.NewGuid():N}.bin");
                File.WriteAllBytes(Path, content ?? new byte[] { 0, 1, 2, 3 });
            }
            public void Dispose() { try { File.Delete(Path); } catch { /* best effort */ } }
        }

        [Fact]
        public void Detect_Dynamic_ForDylibFileOutput()
        {
            using var f = new TempFile();
            var runner = new MockCommandRunner();
            runner.SetResponse(f.Path, 0, "Mach-O 64-bit dynamically linked shared library arm64");

            Assert.Equal(NativeLinkage.Dynamic, NativeLinkageProbe.Detect(f.Path, runner, Logger));
        }

        [Fact]
        public void Detect_Static_ForThinArArchive()
        {
            using var f = new TempFile();
            var runner = new MockCommandRunner();
            runner.SetResponse(f.Path, 0, $"{f.Path}: current ar archive");

            Assert.Equal(NativeLinkage.Static, NativeLinkageProbe.Detect(f.Path, runner, Logger));
        }

        [Fact]
        public void Detect_Static_ForFatArArchive()
        {
            // The universal-binary form must still classify as Static (each member is an archive).
            using var f = new TempFile();
            var runner = new MockCommandRunner();
            runner.SetResponse(f.Path, 0,
                "Mach-O universal binary with 2 architectures: " +
                "[x86_64:current ar archive] [arm64:current ar archive]");

            Assert.Equal(NativeLinkage.Static, NativeLinkageProbe.Detect(f.Path, runner, Logger));
        }

        [Fact]
        public void Detect_Dynamic_ForTbdJsonStub()
        {
            // The Apple/direct path passes a .tbd (JSON text) where a binary would go. It must
            // NOT be force-loaded — "JSON data" is neither dylib nor archive → Dynamic.
            using var f = new TempFile();
            var runner = new MockCommandRunner();
            runner.SetResponse(f.Path, 0, $"{f.Path}: JSON data");

            Assert.Equal(NativeLinkage.Dynamic, NativeLinkageProbe.Detect(f.Path, runner, Logger));
        }

        [Fact]
        public void Detect_Dynamic_ForMissingFile()
        {
            var missing = Path.Combine(Path.GetTempPath(), $"nope_{Guid.NewGuid():N}");
            Assert.Equal(NativeLinkage.Dynamic, NativeLinkageProbe.Detect(missing, new MockCommandRunner(), Logger));
        }

        [Fact]
        public void Detect_Dynamic_ForNullOrEmptyPath()
        {
            Assert.Equal(NativeLinkage.Dynamic, NativeLinkageProbe.Detect(null, new MockCommandRunner(), Logger));
            Assert.Equal(NativeLinkage.Dynamic, NativeLinkageProbe.Detect("", new MockCommandRunner(), Logger));
        }

        [Fact]
        public void Detect_Static_ByMagicFallback_WhenFileCommandFails()
        {
            // file(1) unavailable (non-zero exit) → magic-byte sniff. "!<arch>\n" → Static.
            var arMagic = new byte[] { 0x21, 0x3C, 0x61, 0x72, 0x63, 0x68, 0x3E, 0x0A, 0xFF };
            using var f = new TempFile(arMagic);
            var runner = new MockCommandRunner();
            runner.SetResponse(f.Path, 1, "", "file: command failed");

            Assert.Equal(NativeLinkage.Static, NativeLinkageProbe.Detect(f.Path, runner, Logger));
        }

        [Fact]
        public void Detect_Dynamic_ByMagicFallback_ForNonArchive()
        {
            // file(1) fails AND the bytes are not the ar magic → Dynamic (don't force_load).
            var machOMagic = new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 0x0C, 0x00, 0x00, 0x01, 0x00 };
            using var f = new TempFile(machOMagic);
            var runner = new MockCommandRunner();
            runner.SetResponse(f.Path, 1, "");

            Assert.Equal(NativeLinkage.Dynamic, NativeLinkageProbe.Detect(f.Path, runner, Logger));
        }
    }
}
