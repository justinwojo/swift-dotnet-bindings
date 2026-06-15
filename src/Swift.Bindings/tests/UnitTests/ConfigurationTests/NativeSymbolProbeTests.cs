// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using BindingsGeneration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Tests for <see cref="NativeSymbolProbe"/>'s undefined-symbol scanning (<c>nm -u</c>),
    /// which completes the system-framework link-failure hint independently of how much of its
    /// undefined-symbol list the linker chose to print.
    /// </summary>
    public class NativeSymbolProbeTests
    {
        [Fact]
        public void ParseNmUndefinedSymbols_BareNames_ParsesAll()
        {
            // `nm -u` on a static archive prints bare names, one per line.
            var output =
                "_CVPixelBufferCreate\n" +
                "_MTLCreateSystemDefaultDevice\n" +
                "__ZNSt3__19to_stringEi\n";
            var symbols = NativeSymbolProbe.ParseNmUndefinedSymbols(output);
            Assert.Contains("_CVPixelBufferCreate", symbols);
            Assert.Contains("_MTLCreateSystemDefaultDevice", symbols);
            Assert.Contains("__ZNSt3__19to_stringEi", symbols);
        }

        [Fact]
        public void ParseNmUndefinedSymbols_SkipsMemberHeadersAndBlankLines()
        {
            // Archive output interleaves `member.o:` headers and blank lines.
            var output =
                "\n" +
                "c_api.o:\n" +
                "_glBindFramebuffer\n" +
                "\n" +
                "interpreter_utils.o:\n" +
                "_vImageConvert_AnyToAny\n";
            var symbols = NativeSymbolProbe.ParseNmUndefinedSymbols(output);
            Assert.Equal(new[] { "_glBindFramebuffer", "_vImageConvert_AnyToAny" }, symbols);
            Assert.DoesNotContain("c_api.o:", symbols);
        }

        [Fact]
        public void ParseNmUndefinedSymbols_UPrefixedFormat_ParsesName()
        {
            // Some nm builds prefix the undefined type code `U` (with a blank address column).
            // The last whitespace-delimited token is the symbol name in that shape too.
            var output =
                "                 U _CVPixelBufferGetWidth\n" +
                "                 U _OBJC_CLASS_$_EAGLContext\n";
            var symbols = NativeSymbolProbe.ParseNmUndefinedSymbols(output);
            Assert.Contains("_CVPixelBufferGetWidth", symbols);
            Assert.Contains("_OBJC_CLASS_$_EAGLContext", symbols);
        }

        [Fact]
        public void ParseNmUndefinedSymbols_Dedups()
        {
            // A symbol can be undefined across many member objects — report it once.
            var output = "_CVPixelBufferCreate\n_CVPixelBufferCreate\n_CVPixelBufferCreate\n";
            var symbols = NativeSymbolProbe.ParseNmUndefinedSymbols(output);
            Assert.Single(symbols);
            Assert.Equal("_CVPixelBufferCreate", symbols[0]);
        }

        [Fact]
        public void ParseNmUndefinedSymbols_WhitespaceAndControlCharLines_DegradeNotThrow()
        {
            // This is an error-path builder (it runs while reporting a wrapper link failure), so a
            // pathological line must degrade, not throw. A line that is non-empty before Trim but
            // collapses to zero tokens (control chars / stray whitespace runs) must be skipped, with
            // the real symbol still parsed.
            var output = "\t\v\f \n_CVPixelBufferGetWidth\n     \n";
            var symbols = NativeSymbolProbe.ParseNmUndefinedSymbols(output);
            Assert.Equal(new[] { "_CVPixelBufferGetWidth" }, symbols);
        }

        [Fact]
        public void ScanUndefinedSymbols_NonexistentPath_ReportsNothingToProbe()
        {
            var runner = new MockCommandRunner();
            runner.SetResponse("nm -u", 0, "_CVPixelBufferCreate\n");
            var (symbols, outcome) = NativeSymbolProbe.ScanUndefinedSymbols(
                new[] { "/path/does/not/exist.a" }, runner, NullLogger.Instance);
            // Nothing was read -> empty set is "no binary to probe", not "no symbols".
            Assert.Equal(NativeSymbolProbeOutcome.NothingToProbe, outcome);
            Assert.Empty(symbols);
            Assert.DoesNotContain(runner.Invocations, i => i.Command == "nm"); // skipped before invoking nm
        }

        [Fact]
        public void ScanUndefinedSymbols_BinaryExistsButNmFails_ReportsAllFailed()
        {
            // The binary is present but nm fails on it: a systemic probe failure, distinct from
            // "nothing to probe". The advisory link-hint consumer ignores it (acts only on
            // Gathered), but the outcome must be reported faithfully for callers that fail loud.
            var dir = Path.Combine(Path.GetTempPath(), "nmscan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var a = Path.Combine(dir, "a.a");
            File.WriteAllText(a, "stub");
            var runner = new MockCommandRunner();
            runner.SetResponse("nm -u", 1, ""); // non-zero exit -> read failure
            try
            {
                var (symbols, outcome) = NativeSymbolProbe.ScanUndefinedSymbols(
                    new[] { a }, runner, NullLogger.Instance);
                Assert.Equal(NativeSymbolProbeOutcome.AllFailed, outcome);
                Assert.Empty(symbols);
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
        }

        [Fact]
        public void ScanUndefinedSymbols_ZeroByteBinary_ReportsNothingToProbe_AndDoesNotInvokeNm()
        {
            // A zero-byte file is not a probeable binary: nm would always fail on it, and that
            // failure is not evidence of a systemic toolchain breakage. It must be skipped (not
            // counted as "existed"), so the outcome is NothingToProbe (fail-open), not AllFailed.
            var dir = Path.Combine(Path.GetTempPath(), "nmscan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var empty = Path.Combine(dir, "empty.a");
            File.WriteAllText(empty, ""); // 0 bytes
            var runner = new MockCommandRunner();
            runner.SetResponse("nm -u", 1, ""); // nm would fail if it were ever invoked
            try
            {
                var (symbols, outcome) = NativeSymbolProbe.ScanUndefinedSymbols(
                    new[] { empty }, runner, NullLogger.Instance);
                Assert.Equal(NativeSymbolProbeOutcome.NothingToProbe, outcome);
                Assert.Empty(symbols);
                Assert.DoesNotContain(runner.Invocations, i => i.Command == "nm");
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
        }

        [Fact]
        public void ScanObjCClassSymbols_ZeroByteBinary_ReportsNothingToProbe_AndDoesNotInvokeNm()
        {
            // Same rationale on the defined-symbol (ObjC class) probe: an empty placeholder slice
            // binary must not masquerade as a broken probe (a false-positive SWIFTBIND028). It is
            // skipped, yielding NothingToProbe so the caller fails open.
            var dir = Path.Combine(Path.GetTempPath(), "nmscan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var empty = Path.Combine(dir, "empty.framework_binary");
            File.WriteAllText(empty, ""); // 0 bytes
            var runner = new MockCommandRunner();
            runner.SetResponse("nm -gU", 1, ""); // nm would fail if it were ever invoked
            try
            {
                var scan = NativeSymbolProbe.ScanObjCClassSymbols(
                    new[] { empty }, runner, NullLogger.Instance);
                Assert.Equal(NativeSymbolProbeOutcome.NothingToProbe, scan.Outcome);
                Assert.Empty(scan.DefinedClassNames);
                Assert.DoesNotContain(runner.Invocations, i => i.Command == "nm");
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
        }

        [Fact]
        public void ScanObjCClassSymbols_NonEmptyBinaryNmFails_StillReportsAllFailed()
        {
            // The empty-file skip must NOT weaken the systemic-failure guard: a present, NON-empty
            // binary that nm genuinely cannot read still trips AllFailed.
            var dir = Path.Combine(Path.GetTempPath(), "nmscan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var a = Path.Combine(dir, "a.framework_binary");
            File.WriteAllText(a, "not-a-real-macho"); // non-empty
            var runner = new MockCommandRunner();
            runner.SetResponse("nm -gU", 1, ""); // non-zero exit -> read failure
            try
            {
                var scan = NativeSymbolProbe.ScanObjCClassSymbols(
                    new[] { a }, runner, NullLogger.Instance);
                Assert.Equal(NativeSymbolProbeOutcome.AllFailed, scan.Outcome);
                Assert.Empty(scan.DefinedClassNames);
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
        }

        [Fact]
        public void ScanUndefinedSymbols_UnionsAcrossBinaries()
        {
            var dir = Path.Combine(Path.GetTempPath(), "nmscan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var a = Path.Combine(dir, "a.a");
            var b = Path.Combine(dir, "b.a");
            File.WriteAllText(a, "stub");
            File.WriteAllText(b, "stub");
            var runner = new UnionMockRunner(a, "_CVPixelBufferCreate\n", b, "_MTLCreateSystemDefaultDevice\n");
            try
            {
                var (symbols, outcome) = NativeSymbolProbe.ScanUndefinedSymbols(
                    new[] { a, b }, runner, NullLogger.Instance);
                Assert.Equal(NativeSymbolProbeOutcome.Gathered, outcome);
                Assert.Contains("_CVPixelBufferCreate", symbols);
                Assert.Contains("_MTLCreateSystemDefaultDevice", symbols);
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
        }

        // Returns different nm output per binary path so a union across binaries can be observed.
        private sealed class UnionMockRunner : ICommandRunner
        {
            private readonly string _pathA, _outA, _pathB, _outB;
            public UnionMockRunner(string pathA, string outA, string pathB, string outB)
            {
                _pathA = pathA; _outA = outA; _pathB = pathB; _outB = outB;
            }

            public (int ExitCode, string StdOut, string StdErr) Run(string command, string arguments, int timeoutMs = 30000)
            {
                if (arguments.Contains(_pathA)) return (0, _outA, "");
                if (arguments.Contains(_pathB)) return (0, _outB, "");
                return (0, "", "");
            }
        }
    }
}
