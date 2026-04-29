// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Typed outcome of a Swift wrapper compilation attempt. Carries the raw and
    /// SDK-adjusted severity, the resolved exit code / diagnostic code / message,
    /// and the underlying <see cref="SwiftWrapperCompilationResult"/> so co-gating
    /// and downstream gates can read missing-symbol data without recomputing.
    ///
    /// Single source of truth for severity logic across <c>RunCompileWrapperOnly</c>,
    /// the main command path, and the direct-mode (mixed-framework) path. Use
    /// <see cref="From"/> to build, then <see cref="LogTo"/> to emit, then
    /// branch on <see cref="IsFatal"/> at the call site.
    /// </summary>
    public sealed record WrapperBuildOutcome
    {
        private static readonly IReadOnlySet<string> EmptySymbols = new HashSet<string>();

        public required WrapperCompilationOutcome RawOutcome { get; init; }
        public required WrapperCompilationOutcome EffectiveOutcome { get; init; }
        public required int ExitCode { get; init; }
        public string? DiagnosticCode { get; init; }
        public string Message { get; init; } = string.Empty;
        public SwiftWrapperCompilationResult? CompilationResult { get; init; }
        public Exception? CompilationException { get; init; }

        public IReadOnlySet<string> StrippedSymbols
            => CompilationResult?.StrippedSymbols ?? EmptySymbols;

        public bool IsFatal => ExitCode != 0;

        public bool IsWarning => ExitCode == 0
            && (DiagnosticCode == "SWIFTBIND050" || RawOutcome == WrapperCompilationOutcome.Warning);

        public static WrapperBuildOutcome From(
            SwiftWrapperCompilationResult? compilationResult,
            bool asyncLibraryAutoWired,
            bool sdkMode,
            Exception? compilationException)
        {
            var raw = SwiftWrapperCompiler.EvaluateResult(
                compilationResult, asyncLibraryAutoWired, compilationException);
            var effective = SwiftWrapperCompiler.EffectiveOutcome(raw, sdkMode);
            var (exitCode, diagnosticCode, message) = BindingsGenerator.HandleWrapperCompilationOutcome(
                raw, sdkMode, compilationException, compilationResult);

            return new WrapperBuildOutcome
            {
                RawOutcome = raw,
                EffectiveOutcome = effective,
                ExitCode = exitCode,
                DiagnosticCode = diagnosticCode,
                Message = message,
                CompilationResult = compilationResult,
                CompilationException = compilationException,
            };
        }

        public void LogTo(ILogger logger)
        {
            if (IsFatal)
                logger.LogError("{Message}", Message);
            else if (IsWarning)
                logger.LogWarning("{Message}", Message);
        }
    }
}
