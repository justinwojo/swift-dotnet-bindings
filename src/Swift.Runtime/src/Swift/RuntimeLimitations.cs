// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;

namespace Swift;

/// <summary>
/// Exhaustive, queryable registry of every known upstream .NET runtime limitation
/// affecting Swift interop. Each entry maps to a confirmed upstream bug with a
/// reproduction and per-issue documentation.
///
/// Key principle: this registry is exhaustive. If a runtime crash doesn't match
/// a registered limitation, it is definitively a generator bug.
/// </summary>
public static class RuntimeLimitations
{
    /// <summary>
    /// Exhaustive enum of every known upstream runtime limitation.
    /// Each value maps to a confirmed upstream bug with a reproduction and workaround.
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
        /// Mono: <c>Set.insert</c> via CallConvSwift triggers
        /// "Cannot transition thread from STARTING with DONE_BLOCKING" abort inside
        /// the Mono CallConvSwift trampoline.
        /// Upstream: Issue 3. Status: Unfixed. NativeAOT confirmed NOT affected.
        /// Workaround: never call the stdlib symbol through CallConvSwift. Int64,
        /// Int and String elements go through <c>@_cdecl</c> Swift wrappers that
        /// perform the insert on the Swift side; every other element type goes
        /// through the C-side <c>swiftcall</c> shim <c>SBW_Set_Insert</c>, which
        /// forwards to the same stdlib symbol with LLVM doing the lowering.
        /// </summary>
        MonoSetInsertDoneBlocking,

        /// <summary>
        /// Mono: SafeHandle/SwiftSelf lifetime not preserved across async P/Invoke
        /// suspension points. GC can collect the SafeHandle while Swift async operation
        /// is in flight, causing SIGSEGV.
        /// Upstream: tracking-issue comment item (no standalone bug filing).
        /// NativeAOT confirmed NOT affected.
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

            // Issue 3: Mono-only (Set.insert DONE_BLOCKING in CallConvSwift trampoline)
            Limitation.MonoSetInsertDoneBlocking => isMono,

            // Tracking-comment item: Mono-only (SafeHandle async lifetime).
            // Not a numbered upstream issue — supportability question on the Swift
            // interop tracking issue, not a confirmed runtime bug filing.
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

            Limitation.MonoSetInsertDoneBlocking =>
                "Mono CallConvSwift trampoline aborts with 'Cannot transition thread from STARTING " +
                "with DONE_BLOCKING' when calling Swift Set.insert. NativeAOT not affected " +
                "(upstream Issue 3). Workaround: @_cdecl Swift wrapper for Int64/Int/String " +
                "elements, C-side swiftcall shim SBW_Set_Insert for every other element type.",

            Limitation.MonoAsyncSafeHandleLifetime =>
                "Mono GC can collect SafeHandle across async P/Invoke suspension point, causing " +
                "SIGSEGV. NativeAOT not affected. Tracking-issue comment item — not a numbered " +
                "upstream filing. Workaround: DangerousGetHandle() + Arc.Retain/Release.",

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
