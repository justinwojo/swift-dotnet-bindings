// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Severity of a SwiftUI bridge compilation attempt. Today bridge failures are
    /// non-fatal — the C# bindings still load and bridge views throw
    /// <c>DllNotFoundException</c> at runtime. A <c>Fatal</c> severity will be added
    /// when fail-closed gating lands; until then severity is Success or Warning.
    /// </summary>
    public enum BridgeCompilationSeverity
    {
        Success,
        Warning,
    }

    /// <summary>
    /// Typed outcome of a SwiftUI bridge compilation attempt. Built only when
    /// compilation was actually attempted — the "no bridge files found" early-return
    /// in <c>RunCompileBridgeOnly</c> does not produce an outcome.
    /// </summary>
    public sealed record BridgeBuildOutcome
    {
        public required BridgeCompilationSeverity Severity { get; init; }
        public string? DiagnosticCode { get; init; }
        public string Message { get; init; } = string.Empty;
        public SwiftWrapperCompilationResult? CompilationResult { get; init; }
        public Exception? CompilationException { get; init; }

        /// <summary>
        /// True when bridge compilation produced an xcframework that exists on disk.
        /// Captured at construction; not re-checked on access.
        /// </summary>
        public bool BridgeCompiled { get; init; }

        public static BridgeBuildOutcome From(
            SwiftWrapperCompilationResult? compilationResult,
            Exception? compilationException)
        {
            if (compilationException != null)
            {
                var message = $"SWIFTBIND052: SwiftUI bridge compilation failed — bridge views will throw "
                    + $"DllNotFoundException at runtime: {compilationException.Message}";
                return new BridgeBuildOutcome
                {
                    Severity = BridgeCompilationSeverity.Warning,
                    DiagnosticCode = "SWIFTBIND052",
                    Message = message,
                    CompilationResult = null,
                    CompilationException = compilationException,
                    BridgeCompiled = false,
                };
            }

            var compiled = compilationResult?.XCFrameworkPath != null
                && Directory.Exists(compilationResult.XCFrameworkPath);

            return new BridgeBuildOutcome
            {
                Severity = BridgeCompilationSeverity.Success,
                CompilationResult = compilationResult,
                BridgeCompiled = compiled,
            };
        }

        public void LogTo(ILogger logger)
        {
            if (Severity == BridgeCompilationSeverity.Warning && !string.IsNullOrEmpty(Message))
                logger.LogWarning("{Message}", Message);
        }
    }
}
