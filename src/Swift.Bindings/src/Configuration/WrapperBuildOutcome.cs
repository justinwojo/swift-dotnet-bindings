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

        /// <param name="contractualUnmetArchitectures">
        /// Architectures the caller EXPLICITLY requested (a non-auto <c>--target-architectures</c> list)
        /// that the fat-fold failed to deliver. Non-empty here means the build must fail loudly even in
        /// SDK mode — see the contract branch in <see cref="From"/>. Auto-mode callers and additive bridge
        /// callers pass null/empty so their undelivered extras stay a best-effort degrade.
        /// </param>
        public static WrapperBuildOutcome From(
            SwiftWrapperCompilationResult? compilationResult,
            bool asyncLibraryAutoWired,
            bool sdkMode,
            Exception? compilationException,
            IReadOnlyList<string>? contractualUnmetArchitectures = null)
        {
            // An explicitly-requested architecture that the fold failed to deliver is a CONTRACT
            // violation, not a best-effort degrade: it stays fatal even in SDK mode (which otherwise
            // downgrades a wrapper Fatal to a non-fatal SWIFTBIND050 warning). The working primary slice
            // is still on disk and recorded present, so arm64 consumers keep their NativeReference — but
            // the build must fail loudly rather than silently ship a wrapper narrower than the caller
            // explicitly demanded. Reached only when the primary itself compiled (the orchestrator only
            // reports unmet extras after a real fold attempt), so it never collides with the
            // null-result / exception primary-failure path handled below.
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
                    EffectiveOutcome = WrapperCompilationOutcome.Fatal,
                    ExitCode = 1,
                    DiagnosticCode = "SWIFTBIND056",
                    Message = contractMessage,
                    CompilationResult = compilationResult,
                    CompilationException = compilationException,
                };
            }

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

        /// <summary>
        /// Writes a non-fatal wrapper-compile failure's swiftc-error preview — the filtered swiftc
        /// <c>error:</c> lines carried in <see cref="Message"/> — to <paramref name="standardError"/>.
        ///
        /// A wrapper-compile failure that is non-fatal (exit 0) is emitted by <see cref="LogTo"/> at
        /// Warning level, which the generator's console logger routes to STDOUT — its stderr threshold
        /// sits at Error. Every SDK Exec that invokes the generator captures its stdout at low importance
        /// (swallowed at <c>-v normal</c>) but stderr at high importance, so at normal verbosity only the
        /// give-up (SWIFTBIND051) surfaces and the actual swiftc cause is invisible until a rerun at
        /// <c>-v:detailed</c>. Echoing the preview to stderr makes the next wrapper regression
        /// diagnosable on the first build, without lowering the logger's stdout/stderr threshold — that
        /// threshold is a frozen contract for the resolve-auto-deps stdout grammar.
        ///
        /// The gate is <see cref="CompilationException"/>, not the SWIFTBIND050 code: two distinct
        /// classifications carry the swiftc preview and BOTH must surface. When <c>--async-library</c> was
        /// auto-wired (the inline Apple-framework generate path) a compile failure is a Fatal downgraded
        /// in SDK mode to a SWIFTBIND050 warning; when it was NOT auto-wired (the <c>--compile-wrapper-only</c>
        /// path, which always passes <c>asyncLibraryAutoWired: false</c>) the same failure is a plain,
        /// null-code Warning. Both embed the exception's capped <c>error:</c> preview in <see cref="Message"/>.
        /// Keying on the 050 code alone would leave the compile-wrapper-only path — the common third-party
        /// SDK flow — silent.
        ///
        /// Diagnostic surfacing only: <see cref="ExitCode"/> and the outcome classification are unchanged,
        /// and the same message still reaches stdout via <see cref="LogTo"/>. A no-op for any outcome that
        /// does not carry a swiftc compile exception — a fatal already reaches stderr via
        /// <see cref="LogTo"/>'s error path (and is excluded by <see cref="IsWarning"/>); a success, an
        /// all-stripped-blocks warning (no exception, no <c>error:</c> preview), and the SWIFTBIND056
        /// contract violation all stay silent.
        /// </summary>
        public void EchoWrapperFailurePreviewToStandardError(TextWriter standardError)
        {
            if (IsWarning && CompilationException != null)
                standardError.WriteLine(Message);
        }
    }
}
