// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Tests for the typed wrapper / bridge build outcomes consumed across the three
    /// wrapper compilation entry points and <c>RunCompileBridgeOnly</c>.
    /// </summary>
    public class WrapperBuildOutcomeTests
    {
        [Fact]
        public void From_NoCompilationAttempted_IsSuccess_NotFatal_NotWarning()
        {
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, asyncLibraryAutoWired: false,
                sdkMode: false, compilationException: null);

            Assert.Equal(WrapperCompilationOutcome.Success, outcome.RawOutcome);
            Assert.Equal(WrapperCompilationOutcome.Success, outcome.EffectiveOutcome);
            Assert.False(outcome.IsFatal);
            Assert.False(outcome.IsWarning);
            Assert.Equal(0, outcome.ExitCode);
        }

        [Fact]
        public void From_FatalNonSdkMode_IsFatal_HasNoSwiftbind050Code()
        {
            var ex = new InvalidOperationException("swiftc failed");
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, asyncLibraryAutoWired: true,
                sdkMode: false, compilationException: ex);

            Assert.Equal(WrapperCompilationOutcome.Fatal, outcome.RawOutcome);
            Assert.Equal(WrapperCompilationOutcome.Fatal, outcome.EffectiveOutcome);
            Assert.True(outcome.IsFatal);
            Assert.False(outcome.IsWarning);
            Assert.Equal(1, outcome.ExitCode);
            Assert.Null(outcome.DiagnosticCode);
        }

        [Fact]
        public void From_FatalSdkMode_IsWarning_HasSwiftbind050Code()
        {
            var ex = new InvalidOperationException("swiftc failed");
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, asyncLibraryAutoWired: true,
                sdkMode: true, compilationException: ex);

            Assert.Equal(WrapperCompilationOutcome.Fatal, outcome.RawOutcome);
            Assert.Equal(WrapperCompilationOutcome.Warning, outcome.EffectiveOutcome);
            Assert.False(outcome.IsFatal);
            Assert.True(outcome.IsWarning);
            Assert.Equal(0, outcome.ExitCode);
            Assert.Equal("SWIFTBIND050", outcome.DiagnosticCode);
            Assert.Contains("SWIFTBIND050", outcome.Message);
        }

        [Fact]
        public void From_WarningRaw_IsWarning_NoSwiftbind050Code()
        {
            // Stripped block triggers Warning when async library is not auto-wired.
            var result = new SwiftWrapperCompilationResult
            {
                XCFrameworkPath = "/tmp/none",
                CompiledFileCount = 0,
                StrippedBlockCount = 3,
            };
            var outcome = WrapperBuildOutcome.From(
                result, asyncLibraryAutoWired: false, sdkMode: false, compilationException: null);

            Assert.Equal(WrapperCompilationOutcome.Warning, outcome.RawOutcome);
            Assert.False(outcome.IsFatal);
            Assert.True(outcome.IsWarning);
            Assert.Null(outcome.DiagnosticCode);
        }

        [Fact]
        public void From_PreservesStrippedSymbols()
        {
            var result = new SwiftWrapperCompilationResult
            {
                XCFrameworkPath = "/tmp/none",
                CompiledFileCount = 5,
                StrippedBlockCount = 0,
                StrippedSymbols = new HashSet<string> { "$s4Test3fooyyF", "$s4Test3baryyF" },
            };
            var outcome = WrapperBuildOutcome.From(
                result, asyncLibraryAutoWired: false, sdkMode: false, compilationException: null);

            Assert.Equal(2, outcome.StrippedSymbols.Count);
            Assert.Contains("$s4Test3fooyyF", outcome.StrippedSymbols);
        }

        [Fact]
        public void StrippedSymbols_NullCompilationResult_ReturnsEmptySet()
        {
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, asyncLibraryAutoWired: false,
                sdkMode: false, compilationException: null);

            Assert.Empty(outcome.StrippedSymbols);
        }

        [Fact]
        public void LogTo_Success_DoesNotLog()
        {
            var logger = new CapturingLogger();
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, asyncLibraryAutoWired: false,
                sdkMode: false, compilationException: null);

            outcome.LogTo(logger);

            Assert.Empty(logger.Entries);
        }

        [Fact]
        public void LogTo_Fatal_LogsError()
        {
            var logger = new CapturingLogger();
            var ex = new InvalidOperationException("swiftc failed");
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, asyncLibraryAutoWired: true,
                sdkMode: false, compilationException: ex);

            outcome.LogTo(logger);

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Contains("swiftc failed", entry.Message);
        }

        [Fact]
        public void LogTo_FatalSdkMode_LogsWarning_WithSwiftbind050()
        {
            var logger = new CapturingLogger();
            var ex = new InvalidOperationException("swiftc failed");
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, asyncLibraryAutoWired: true,
                sdkMode: true, compilationException: ex);

            outcome.LogTo(logger);

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Contains("SWIFTBIND050", entry.Message);
        }

        [Fact]
        public void LogTo_WarningRaw_LogsWarning_NoSwiftbind050()
        {
            var logger = new CapturingLogger();
            var result = new SwiftWrapperCompilationResult
            {
                XCFrameworkPath = "/tmp/none",
                CompiledFileCount = 0,
                StrippedBlockCount = 3,
            };
            var outcome = WrapperBuildOutcome.From(
                result, asyncLibraryAutoWired: false, sdkMode: false, compilationException: null);

            outcome.LogTo(logger);

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.DoesNotContain("SWIFTBIND050", entry.Message);
        }

        // ── Echo of a non-fatal wrapper-compile failure's swiftc preview to stderr (visible at -v normal) ──
        // A non-fatal wrapper failure is LogTo'd at Warning → the generator's console logger sends it to
        // STDOUT; every SDK generator Exec captures stdout at low importance and swallows it at -v normal,
        // so only the SWIFTBIND051 give-up surfaces. The echo writes the same swiftc-error preview to
        // stderr (captured at high importance) so the failure is diagnosable on the first build. The gate
        // is the compilation exception (the preview carrier), NOT the SWIFTBIND050 code — because the two
        // production classifications differ by whether --async-library was auto-wired: the inline
        // Apple-framework generate path yields a SWIFTBIND050 warning, while the --compile-wrapper-only
        // path (always asyncLibraryAutoWired: false) yields a plain, null-code Warning. Both must surface.
        // These assert the mechanism and that classification is unchanged — not exact strings. A
        // StringWriter stands in for Console.Error.

        // A deliberately-broken wrapper compile: the exception message carries the filtered swiftc
        // preview exactly as SwiftWrapperCompiler throws it.
        private static InvalidOperationException BrokenWrapperCompile() =>
            new("Swift wrapper compilation failed (exit code 1): error: no such module 'DeliberatelyBroken'");

        [Fact]
        public void EchoWrapperFailurePreviewToStandardError_Swiftbind050_WritesPreviewAndCode()
        {
            // async auto-wired (the inline Apple-framework generate path): raw Fatal, downgraded in SDK
            // mode to a SWIFTBIND050 warning.
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, asyncLibraryAutoWired: true,
                sdkMode: true, compilationException: BrokenWrapperCompile());

            var stderr = new StringWriter();
            outcome.EchoWrapperFailurePreviewToStandardError(stderr);

            var written = stderr.ToString();
            Assert.Contains("SWIFTBIND050", written);
            // The swiftc-error preview reaches stderr — the whole point of the surfacing.
            Assert.Contains("no such module 'DeliberatelyBroken'", written);
            // Classification is unchanged: still a non-fatal SWIFTBIND050 warning.
            Assert.Equal(0, outcome.ExitCode);
            Assert.Equal("SWIFTBIND050", outcome.DiagnosticCode);
        }

        [Fact]
        public void EchoWrapperFailurePreviewToStandardError_CompileWrapperOnlyShape_WritesPreview()
        {
            // The exact production --compile-wrapper-only argument shape (Program.cs RunCompileWrapperOnly):
            // asyncLibraryAutoWired: false + sdkMode: true. A compile exception here is a PLAIN Warning with
            // a NULL DiagnosticCode — NOT SWIFTBIND050 — because 050 is assigned only when the raw outcome
            // is Fatal (async auto-wired). This is the wired path the feature exists to fix; a guard keyed
            // on the 050 code would leave it silent. Red without the CompilationException-based guard.
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, asyncLibraryAutoWired: false,
                sdkMode: true, compilationException: BrokenWrapperCompile());

            // Documents the real classification on this path: non-fatal warning, no 050 code.
            Assert.True(outcome.IsWarning);
            Assert.Null(outcome.DiagnosticCode);
            Assert.Equal(0, outcome.ExitCode);

            var stderr = new StringWriter();
            outcome.EchoWrapperFailurePreviewToStandardError(stderr);

            // The swiftc-error preview must still reach stderr despite the null code.
            Assert.Contains("no such module 'DeliberatelyBroken'", stderr.ToString());
        }

        [Fact]
        public void EchoWrapperFailurePreviewToStandardError_Fatal_WritesNothing()
        {
            // A fatal outcome already reaches stderr via LogTo's error path; the echo must not
            // double-surface it (and a non-SDK fatal carries no SWIFTBIND050 code).
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, asyncLibraryAutoWired: true,
                sdkMode: false, compilationException: BrokenWrapperCompile());

            var stderr = new StringWriter();
            outcome.EchoWrapperFailurePreviewToStandardError(stderr);

            Assert.True(outcome.IsFatal);
            Assert.Equal(string.Empty, stderr.ToString());
        }

        [Fact]
        public void EchoWrapperFailurePreviewToStandardError_StrippedBlocksNoException_WritesNothing()
        {
            // An all-stripped-blocks Warning carries no compilation exception, so it has no swiftc error:
            // preview to surface — its message is only a stripped-block count. The exception-based guard
            // keeps it silent even though it IS a non-fatal warning.
            var result = new SwiftWrapperCompilationResult
            {
                XCFrameworkPath = "/tmp/none",
                CompiledFileCount = 0,
                StrippedBlockCount = 3,
            };
            var outcome = WrapperBuildOutcome.From(
                result, asyncLibraryAutoWired: false, sdkMode: false, compilationException: null);

            var stderr = new StringWriter();
            outcome.EchoWrapperFailurePreviewToStandardError(stderr);

            Assert.True(outcome.IsWarning);
            Assert.Null(outcome.CompilationException);
            Assert.Equal(string.Empty, stderr.ToString());
        }

        [Fact]
        public void EchoWrapperFailurePreviewToStandardError_Success_WritesNothing()
        {
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, asyncLibraryAutoWired: false,
                sdkMode: false, compilationException: null);

            var stderr = new StringWriter();
            outcome.EchoWrapperFailurePreviewToStandardError(stderr);

            Assert.Equal(string.Empty, stderr.ToString());
        }

        [Fact]
        public void EchoWrapperFailurePreviewToStandardError_Contractual056_WritesNothing()
        {
            // A contract violation carries a non-null diagnostic code that is NOT SWIFTBIND050 and is
            // fatal — guards against a future refactor keying the echo on "DiagnosticCode != null".
            var outcome = WrapperBuildOutcome.From(
                SucceededPrimary(), asyncLibraryAutoWired: false, sdkMode: true,
                compilationException: null,
                contractualUnmetArchitectures: new[] { "x86_64" });

            var stderr = new StringWriter();
            outcome.EchoWrapperFailurePreviewToStandardError(stderr);

            Assert.Equal("SWIFTBIND056", outcome.DiagnosticCode);
            Assert.Equal(string.Empty, stderr.ToString());
        }

        // ── Architecture-contract violation (explicit --target-architectures slice undelivered) ──
        // A successful primary result is used throughout so the override — not the base evaluation —
        // is what produces the fatal outcome.
        private static SwiftWrapperCompilationResult SucceededPrimary() => new SwiftWrapperCompilationResult
        {
            XCFrameworkPath = "/tmp/primary.xcframework",
            CompiledFileCount = 4,
            StrippedBlockCount = 0,
        };

        [Fact]
        public void From_ContractualUnmet_StaysFatalEvenInSdkMode()
        {
            // The whole point: SDK mode downgrades an ordinary wrapper Fatal to a SWIFTBIND050 warning,
            // but an explicitly-requested arch the fold failed to deliver must NOT be downgraded.
            var outcome = WrapperBuildOutcome.From(
                SucceededPrimary(), asyncLibraryAutoWired: false, sdkMode: true,
                compilationException: null,
                contractualUnmetArchitectures: new[] { "x86_64" });

            Assert.Equal(WrapperCompilationOutcome.Fatal, outcome.RawOutcome);
            Assert.Equal(WrapperCompilationOutcome.Fatal, outcome.EffectiveOutcome);
            Assert.True(outcome.IsFatal);
            Assert.False(outcome.IsWarning);
            Assert.Equal(1, outcome.ExitCode);
            Assert.Equal("SWIFTBIND056", outcome.DiagnosticCode);
            Assert.Contains("SWIFTBIND056", outcome.Message);
            Assert.Contains("x86_64", outcome.Message);
        }

        [Fact]
        public void From_ContractualUnmet_NonSdkMode_IsFatal()
        {
            var outcome = WrapperBuildOutcome.From(
                SucceededPrimary(), asyncLibraryAutoWired: false, sdkMode: false,
                compilationException: null,
                contractualUnmetArchitectures: new[] { "arm64", "x86_64" });

            Assert.True(outcome.IsFatal);
            Assert.Equal(1, outcome.ExitCode);
            Assert.Equal("SWIFTBIND056", outcome.DiagnosticCode);
            Assert.Contains("arm64", outcome.Message);
            Assert.Contains("x86_64", outcome.Message);
        }

        [Fact]
        public void From_ContractualUnmetEmpty_DoesNotForceFatal()
        {
            // Auto mode / satisfied contract: callers pass an empty list, so a successful primary stays
            // a success even though the override parameter is present.
            var outcome = WrapperBuildOutcome.From(
                SucceededPrimary(), asyncLibraryAutoWired: false, sdkMode: true,
                compilationException: null,
                contractualUnmetArchitectures: System.Array.Empty<string>());

            Assert.False(outcome.IsFatal);
            Assert.NotEqual("SWIFTBIND056", outcome.DiagnosticCode);
            Assert.Equal(0, outcome.ExitCode);
        }

        [Fact]
        public void From_ContractualUnmetNull_BehavesLikeNoOverride()
        {
            // The default (null) argument must leave the historical severity path untouched.
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, asyncLibraryAutoWired: false,
                sdkMode: false, compilationException: null,
                contractualUnmetArchitectures: null);

            Assert.Equal(WrapperCompilationOutcome.Success, outcome.RawOutcome);
            Assert.False(outcome.IsFatal);
            Assert.Null(outcome.DiagnosticCode);
        }

        [Fact]
        public void LogTo_ContractualUnmet_LogsError_WithSwiftbind056AndArch()
        {
            var logger = new CapturingLogger();
            var outcome = WrapperBuildOutcome.From(
                SucceededPrimary(), asyncLibraryAutoWired: false, sdkMode: true,
                compilationException: null,
                contractualUnmetArchitectures: new[] { "x86_64" });

            outcome.LogTo(logger);

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Contains("SWIFTBIND056", entry.Message);
            Assert.Contains("x86_64", entry.Message);
        }
    }

    public class BridgeBuildOutcomeTests
    {
        [Fact]
        public void From_NoException_NoResult_IsSuccess_NotCompiled()
        {
            var outcome = BridgeBuildOutcome.From(compilationResult: null, compilationException: null);

            Assert.Equal(BridgeCompilationSeverity.Success, outcome.Severity);
            Assert.False(outcome.BridgeCompiled);
            Assert.Null(outcome.DiagnosticCode);
        }

        [Fact]
        public void From_NoException_PathDoesNotExistOnDisk_IsSuccessButNotCompiled()
        {
            var result = new SwiftWrapperCompilationResult
            {
                XCFrameworkPath = "/tmp/definitely-does-not-exist-bridge-xcfw",
                CompiledFileCount = 1,
                StrippedBlockCount = 0,
            };
            var outcome = BridgeBuildOutcome.From(result, compilationException: null);

            Assert.Equal(BridgeCompilationSeverity.Success, outcome.Severity);
            Assert.False(outcome.BridgeCompiled);
        }

        [Fact]
        public void From_NoException_PathExistsOnDisk_IsCompiled()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"bridge-outcome-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var result = new SwiftWrapperCompilationResult
                {
                    XCFrameworkPath = tempDir,
                    CompiledFileCount = 1,
                    StrippedBlockCount = 0,
                };
                var outcome = BridgeBuildOutcome.From(result, compilationException: null);

                Assert.Equal(BridgeCompilationSeverity.Success, outcome.Severity);
                Assert.True(outcome.BridgeCompiled);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void From_Exception_IsWarning_HasSwiftbind052Code()
        {
            var ex = new InvalidOperationException("bridge swiftc failed");
            var outcome = BridgeBuildOutcome.From(compilationResult: null, compilationException: ex);

            Assert.Equal(BridgeCompilationSeverity.Warning, outcome.Severity);
            Assert.False(outcome.BridgeCompiled);
            Assert.Equal("SWIFTBIND052", outcome.DiagnosticCode);
            Assert.Contains("SWIFTBIND052", outcome.Message);
            Assert.Contains("bridge swiftc failed", outcome.Message);
            Assert.Same(ex, outcome.CompilationException);
        }

        [Fact]
        public void LogTo_Success_DoesNotLog()
        {
            var logger = new CapturingLogger();
            var outcome = BridgeBuildOutcome.From(compilationResult: null, compilationException: null);

            outcome.LogTo(logger);

            Assert.Empty(logger.Entries);
        }

        [Fact]
        public void LogTo_Warning_LogsWarning()
        {
            var logger = new CapturingLogger();
            var ex = new InvalidOperationException("bridge swiftc failed");
            var outcome = BridgeBuildOutcome.From(compilationResult: null, compilationException: ex);

            outcome.LogTo(logger);

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Contains("SWIFTBIND052", entry.Message);
        }
    }

    internal sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
