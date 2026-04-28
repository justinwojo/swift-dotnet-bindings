// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;

namespace Swift;

/// <summary>
/// Exhaustive, queryable registry of every known upstream .NET runtime limitation
/// affecting Swift interop. Each entry maps to a confirmed upstream bug documented
/// in src/docs/Future/upstream-bug-reports-draft.md.
///
/// Key principle: this registry is exhaustive. If a runtime crash doesn't match
/// a registered limitation, it is definitively a generator bug.
/// MONO-JIT-FINDINGS.md proved 100% of suspected "Mono bugs" were generator bugs.
/// This registry codifies that lesson.
/// </summary>
public static class RuntimeLimitations
{
    /// <summary>
    /// Exhaustive enum of every known upstream runtime limitation.
    /// Each value maps to a confirmed upstream bug with a reproduction
    /// and workaround documented in upstream-bug-reports-draft.md.
    /// </summary>
    internal enum Limitation
    {
        /// <summary>
        /// Mono: CallConvSwift JIT assertion failure (!ji->async) when calling
        /// Swift runtime functions like swift_getExistentialTypeMetadata.
        /// Upstream: Issue 1 (mono/metadata/jit-info.c:918). Status: Unfixed.
        /// Workaround: @_silgen_name Swift wrapper performs metadata lookup on Swift side.
        /// </summary>
        MonoCallConvSwiftJitAssertion,

        /// <summary>
        /// Both runtimes: Non-blittable types (SafeHandle, managed strings, SwiftOptional,
        /// managed delegates) rejected in CallConvSwift P/Invoke signatures.
        /// Upstream: Issue 2 (Mono: marshal.c:3729, NativeAOT: SwiftPhysicalLowering.cs:215).
        /// Status: Unfixed. Impact: ~67% of P/Invokes need @_cdecl wrappers because of this.
        /// Workaround: @_cdecl Swift wrapper presents C-compatible signature via CallConvCdecl.
        /// </summary>
        NonBlittableCallConvSwiftRejection,

        /// <summary>
        /// NativeAOT: Custom struct float/double fields placed in GPR (x0-x7) instead of
        /// FPR (d0-d7) when passed as parameters via CallConvSwift on ARM64.
        /// System framework types (CGRect, CGPoint) are exempt.
        /// Upstream: Issue 5. Status: Unfixed.
        /// Workaround: @_cdecl wrapper decomposes struct into individual scalar parameters.
        /// </summary>
        NativeAotFloatStructParam,

        /// <summary>
        /// NativeAOT: Custom struct float/double return values read from GPR instead of FPR.
        /// On Mono, float struct returns may SIGSEGV.
        /// Upstream: Issue 6. Status: Unfixed.
        /// Workaround: @_cdecl wrapper returns scalar, or SwiftIndirectResult bypasses registers.
        /// </summary>
        NativeAotFloatStructReturn,

        /// <summary>
        /// Mono: SafeHandle/SwiftSelf lifetime not preserved across async P/Invoke
        /// suspension points. GC can collect the SafeHandle while Swift async operation
        /// is in flight, causing SIGSEGV.
        /// Upstream: Issue 3. Status: Unfixed. NativeAOT confirmed NOT affected.
        /// Workaround: DangerousGetHandle() + explicit Arc.Retain/Release, or
        /// @_cdecl wrapper accepting UnsafeMutableRawPointer.
        /// </summary>
        MonoAsyncSafeHandleLifetime,
    }

    // Cached array of all limitation values for completeness checks.
    private static readonly Limitation[] AllLimitations = Enum.GetValues<Limitation>();

    /// <summary>
    /// Returns true if the given limitation affects the current runtime.
    /// Uses three-way runtime detection: Mono (iOS simulator), NativeAOT (iOS device),
    /// and CoreCLR (desktop macOS). Desktop CoreCLR is not affected by any
    /// runtime-specific limitation since it doesn't execute Swift interop P/Invokes.
    /// </summary>
    internal static bool IsAffected(Limitation limitation)
    {
        bool isMono = SwiftRuntimeInfo.IsMonoRuntime;
        bool isNativeAot = SwiftRuntimeInfo.IsNativeAotRuntime;

        return limitation switch
        {
            // Issue 1: Mono-only (JIT assertion)
            Limitation.MonoCallConvSwiftJitAssertion => isMono,

            // Issue 2: Both Mono and NativeAOT reject non-blittable in CallConvSwift.
            // Not affected on desktop CoreCLR (no Swift interop P/Invokes).
            Limitation.NonBlittableCallConvSwiftRejection => isMono || isNativeAot,

            // Issue 5: NativeAOT-only (float struct params in GPR)
            Limitation.NativeAotFloatStructParam => isNativeAot,

            // Issue 6: NativeAOT-only (float struct return from GPR)
            // Note: Mono has the inverse bug (SIGSEGV on float struct return)
            // but the workaround (@_cdecl) handles both, so we only register
            // the NativeAOT side as the canonical limitation.
            Limitation.NativeAotFloatStructReturn => isNativeAot,

            // Issue 3: Mono-only (SafeHandle async lifetime)
            Limitation.MonoAsyncSafeHandleLifetime => isMono,

            _ => false,
        };
    }

    /// <summary>
    /// Returns a human-readable description of the limitation suitable for
    /// skip messages, diagnostics, and test output.
    /// </summary>
    internal static string Describe(Limitation limitation)
    {
        return limitation switch
        {
            Limitation.MonoCallConvSwiftJitAssertion =>
                "Mono JIT assertion '!ji->async' at jit-info.c:918 when calling Swift runtime " +
                "functions via CallConvSwift (upstream Issue 1). Workaround: @_silgen_name wrapper.",

            Limitation.NonBlittableCallConvSwiftRejection =>
                "Non-blittable types (SafeHandle, String, Optional, delegates) rejected in " +
                "CallConvSwift P/Invoke on both Mono (marshal.c:3729) and NativeAOT " +
                "(SwiftPhysicalLowering.cs:215) (upstream Issue 2). Workaround: @_cdecl wrapper.",

            Limitation.NativeAotFloatStructParam =>
                "NativeAOT places custom struct float/double param fields in GPR instead of FPR " +
                "on ARM64. System types (CGRect) exempt (upstream Issue 5). " +
                "Workaround: @_cdecl wrapper with scalar decomposition.",

            Limitation.NativeAotFloatStructReturn =>
                "NativeAOT reads custom struct float/double return from GPR instead of FPR on ARM64. " +
                "On Mono, float struct returns may SIGSEGV (upstream Issue 6). " +
                "Workaround: @_cdecl wrapper or SwiftIndirectResult.",

            Limitation.MonoAsyncSafeHandleLifetime =>
                "Mono GC can collect SafeHandle across async P/Invoke suspension point, causing " +
                "SIGSEGV. NativeAOT not affected (upstream Issue 3). " +
                "Workaround: DangerousGetHandle() + Arc.Retain/Release.",

            _ => $"Unknown runtime limitation: {limitation}",
        };
    }

    /// <summary>
    /// Returns all registered limitations. Used for completeness validation
    /// in unit tests and diagnostic tools.
    /// </summary>
    internal static IReadOnlyList<Limitation> GetAllLimitations() => AllLimitations;

    /// <summary>
    /// Returns all limitations that affect the current runtime.
    /// </summary>
    internal static IReadOnlyList<Limitation> GetAffectedLimitations()
    {
        var affected = new List<Limitation>();
        foreach (var limitation in AllLimitations)
        {
            if (IsAffected(limitation))
                affected.Add(limitation);
        }
        return affected;
    }
}
