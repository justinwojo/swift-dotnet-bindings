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
        /// Mono: a <c>CallConvSwift</c> P/Invoke aborts with
        /// "Cannot transition thread ... from STARTING with DONE_BLOCKING" after the
        /// Swift callee has already returned.
        ///
        /// Mechanism: Mono's managed-to-native wrapper keeps the GC-safe-region cookie
        /// (a <c>MonoThreadInfo*</c>) in a callee-saved register picked by its register
        /// allocator, without excluding the registers the Swift calling convention
        /// reserves for arguments — x20 (<c>SwiftSelf</c>) and x21 (<c>SwiftError</c>).
        /// When the allocator picks one of those, the wrapper's own argument setup
        /// overwrites the cookie before the call; after the call it reloads the now
        /// clobbered register and passes it to
        /// <c>mono_threads_exit_gc_safe_region_unbalanced</c>, which reads a bogus
        /// thread record and aborts.
        ///
        /// Scope, as evidenced: any CallConvSwift P/Invoke whose self travels in x20 —
        /// that is, an untyped <c>SwiftSelf</c> parameter. Which of those members are
        /// hit is decided by Mono's register allocator, so it is neither predictable
        /// from the Swift signature nor specific to any argument or return shape.
        /// A typed <c>SwiftSelf&lt;T&gt;</c> (frozen struct self) travels in ordinary
        /// argument registers, so a cookie in x20 survives and those members are not
        /// implicated.
        /// The x21 (<c>SwiftError</c>) arm is mechanically possible but has not been
        /// observed: Mono's wrapper does write x21 during argument setup for every
        /// SwiftError-carrying call, so a cookie allocated there would be destroyed
        /// identically — but no wrapper in the surveyed corpus allocated the cookie to
        /// x21. Treat that arm as unconfirmed rather than as an established
        /// attribution.
        /// Upstream: Issue 3 — originally filed against Swift <c>Set.insert</c>, whose
        /// <c>(Bool, @out T via x0)</c> tuple return was a correlation across three
        /// samples rather than the cause.
        /// Status: Unfixed. NativeAOT confirmed NOT affected.
        /// Workaround: route the member through an <c>@_cdecl</c> Swift wrapper — a
        /// CallConvCdecl signature carries neither SwiftSelf nor SwiftError, so there
        /// is no reserved register for the cookie to collide with. For <c>Set.insert</c>
        /// specifically, Int64, Int and String elements go through <c>@_cdecl</c> Swift
        /// wrappers; every other element type goes through the C-side <c>swiftcall</c>
        /// shim <c>SBW_Set_Insert</c>.
        /// </summary>
        MonoSwiftCallDoneBlockingAbort,

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

            // Issue 3: Mono-only (GC-safe-region cookie clobbered in the Swift-reserved
            // argument registers x20/x21 by Mono's own managed-to-native wrapper)
            Limitation.MonoSwiftCallDoneBlockingAbort => isMono,

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

            Limitation.MonoSwiftCallDoneBlockingAbort =>
                "Mono CallConvSwift P/Invoke aborts with 'Cannot transition thread from STARTING " +
                "with DONE_BLOCKING' after the Swift callee returns: Mono's managed-to-native " +
                "wrapper can hold the GC-safe-region cookie in x20/x21 — the registers the Swift " +
                "calling convention reserves for SwiftSelf/SwiftError — and then overwrites it " +
                "with the call's own arguments. Observed for x20: affects any P/Invoke passing " +
                "an untyped SwiftSelf, and which of those members are hit is decided by Mono's " +
                "register allocator, not by the Swift signature. A typed SwiftSelf<T> travels in " +
                "ordinary argument registers and is not implicated. The x21/SwiftError arm is " +
                "mechanically possible but unobserved in the surveyed corpus (upstream Issue 3, " +
                "originally filed against Set.insert). NativeAOT not affected. Workaround: " +
                "@_cdecl Swift wrapper — a CallConvCdecl signature carries no SwiftSelf/SwiftError " +
                "register for the cookie to collide with.",

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
