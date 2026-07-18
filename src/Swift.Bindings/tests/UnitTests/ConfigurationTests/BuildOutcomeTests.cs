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
        public void From_NoCompilationAttempted_IsSuccess_NotFatal()
        {
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, compilationException: null);

            Assert.Equal(WrapperCompilationOutcome.Success, outcome.RawOutcome);
            Assert.False(outcome.IsFatal);
            Assert.Equal(0, outcome.ExitCode);
        }

        [Fact]
        public void From_CompileException_IsFatal_ExitCode1()
        {
            // Shape produced by the SDK's --compile-wrapper-only path: a compile exception must
            // fail publication (exit 1), not soft-fail as a warning.
            var ex = new InvalidOperationException("swiftc failed");
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, compilationException: ex);

            Assert.Equal(WrapperCompilationOutcome.Fatal, outcome.RawOutcome);
            Assert.True(outcome.IsFatal);
            Assert.Equal(1, outcome.ExitCode);
            Assert.Null(outcome.DiagnosticCode);
            Assert.Contains("swiftc failed", outcome.Message);
        }

        [Fact]
        public void From_AllStripped_IsFatal_ExitCode1()
        {
            // The real all-stripped shape: Compile()'s give-up paths return an empty
            // XCFrameworkPath, which is what marks "no wrapper on disk".
            var result = new SwiftWrapperCompilationResult
            {
                XCFrameworkPath = "",
                CompiledFileCount = 0,
                StrippedBlockCount = 3,
            };
            var outcome = WrapperBuildOutcome.From(
                result, compilationException: null);

            Assert.Equal(WrapperCompilationOutcome.Fatal, outcome.RawOutcome);
            Assert.True(outcome.IsFatal);
            Assert.Equal(1, outcome.ExitCode);
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
                result, compilationException: null);

            Assert.Equal(2, outcome.StrippedSymbols.Count);
            Assert.Contains("$s4Test3fooyyF", outcome.StrippedSymbols);
        }

        [Fact]
        public void StrippedSymbols_NullCompilationResult_ReturnsEmptySet()
        {
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, compilationException: null);

            Assert.Empty(outcome.StrippedSymbols);
        }

        [Fact]
        public void LogTo_Success_DoesNotLog()
        {
            var logger = new CapturingLogger();
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, compilationException: null);

            outcome.LogTo(logger);

            Assert.Empty(logger.Entries);
        }

        [Fact]
        public void LogTo_Fatal_LogsError()
        {
            var logger = new CapturingLogger();
            var ex = new InvalidOperationException("swiftc failed");
            var outcome = WrapperBuildOutcome.From(
                compilationResult: null, compilationException: ex);

            outcome.LogTo(logger);

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Contains("swiftc failed", entry.Message);
        }

        [Fact]
        public void LogTo_AllStripped_LogsError()
        {
            var logger = new CapturingLogger();
            var result = new SwiftWrapperCompilationResult
            {
                XCFrameworkPath = "",
                CompiledFileCount = 0,
                StrippedBlockCount = 3,
            };
            var outcome = WrapperBuildOutcome.From(
                result, compilationException: null);

            outcome.LogTo(logger);

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Null(outcome.DiagnosticCode);
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
        public void From_ContractualUnmet_IsFatal()
        {
            // An explicitly-requested arch the fold failed to deliver is fatal: the primary
            // slice compiled, but shipping a narrower wrapper than requested would silently
            // drop the demanded architecture.
            var outcome = WrapperBuildOutcome.From(
                SucceededPrimary(),
                compilationException: null,
                contractualUnmetArchitectures: new[] { "x86_64" });

            Assert.Equal(WrapperCompilationOutcome.Fatal, outcome.RawOutcome);
            Assert.True(outcome.IsFatal);
            Assert.Equal(1, outcome.ExitCode);
            Assert.Equal("SWIFTBIND056", outcome.DiagnosticCode);
            Assert.Contains("SWIFTBIND056", outcome.Message);
            Assert.Contains("x86_64", outcome.Message);
        }

        [Fact]
        public void From_ContractualUnmet_MultipleArchs_IsFatal()
        {
            var outcome = WrapperBuildOutcome.From(
                SucceededPrimary(),
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
                SucceededPrimary(),
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
                compilationResult: null, compilationException: null,
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
                SucceededPrimary(),
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
