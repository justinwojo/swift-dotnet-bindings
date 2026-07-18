// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Typed outcome of a Swift wrapper compilation attempt. Carries the raw severity,
    /// the resolved exit code / diagnostic code / message, and the underlying
    /// <see cref="SwiftWrapperCompilationResult"/> so co-gating and downstream gates can
    /// read missing-symbol data without recomputing.
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
        public required int ExitCode { get; init; }
        public string? DiagnosticCode { get; init; }
        public string Message { get; init; } = string.Empty;
        public SwiftWrapperCompilationResult? CompilationResult { get; init; }
        public Exception? CompilationException { get; init; }

        public IReadOnlySet<string> StrippedSymbols
            => CompilationResult?.StrippedSymbols ?? EmptySymbols;

        public bool IsFatal => ExitCode != 0;

        /// <param name="contractualUnmetArchitectures">
        /// Architectures the caller EXPLICITLY requested (a non-auto <c>--target-architectures</c> list)
        /// that the fat-fold failed to deliver. Non-empty here means the build must fail loudly —
        /// see the contract branch in <see cref="From"/>. Auto-mode callers and additive bridge
        /// callers pass null/empty so their undelivered extras stay a best-effort degrade.
        /// </param>
        public static WrapperBuildOutcome From(
            SwiftWrapperCompilationResult? compilationResult,
            Exception? compilationException,
            IReadOnlyList<string>? contractualUnmetArchitectures = null)
        {
            // An explicitly-requested architecture that the fold failed to deliver is a CONTRACT
            // violation, not a best-effort degrade: it is fatal for a distinct reason (the primary
            // slice compiled fine; only an explicitly requested extra architecture is missing).
            // The working primary slice is still on disk and recorded present, so arm64 consumers
            // keep their NativeReference — but the build must fail loudly rather than silently ship
            // a wrapper narrower than the caller explicitly demanded. Reached only when the primary
            // itself compiled (the orchestrator only reports unmet extras after a real fold attempt),
            // so it never collides with the null-result / exception primary-failure path handled below.
            if (contractualUnmetArchitectures != null && contractualUnmetArchitectures.Count > 0)
            {
                var archs = string.Join(", ", contractualUnmetArchitectures);
                var contractMessage =
                    $"SWIFTBIND056: The Swift wrapper was explicitly requested for architecture(s) [{archs}] " +
                    "via --target-architectures, but those slices failed to build and were not folded into the " +
                    "fat wrapper. An explicit architecture request is a contract — shipping the narrower " +
                    "primary-only wrapper would silently drop the requested simulator/Intel slice, so the build " +
                    "is failed instead. Resolve the per-architecture compile failure (a swiftc timeout on a " +
                    "contended runner is relieved by raising the wrapper compile timeout) and re-run, or request " +
                    "'auto' architectures to allow a best-effort degrade.";

                return new WrapperBuildOutcome
                {
                    RawOutcome = WrapperCompilationOutcome.Fatal,
                    ExitCode = 1,
                    DiagnosticCode = "SWIFTBIND056",
                    Message = contractMessage,
                    CompilationResult = compilationResult,
                    CompilationException = compilationException,
                };
            }

            var raw = SwiftWrapperCompiler.EvaluateResult(
                compilationResult, compilationException);
            var (exitCode, diagnosticCode, message) = BindingsGenerator.HandleWrapperCompilationOutcome(
                raw, compilationException, compilationResult);

            return new WrapperBuildOutcome
            {
                RawOutcome = raw,
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
        }
    }
}
