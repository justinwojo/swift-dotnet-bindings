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
