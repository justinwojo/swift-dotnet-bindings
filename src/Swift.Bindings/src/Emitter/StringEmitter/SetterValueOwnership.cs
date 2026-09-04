// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Answers who owns the value a Swift stored-property setter is handed — an ownership
/// question, deliberately separate from the width question
/// <see cref="DirectOptionalAbi.UsesSwiftSideCarrier"/> answers.
///
/// <para>SILGen lowers <c>foo.setter</c> as <c>(@owned Value, @guaranteed self) -&gt; ()</c>
/// (a subscript setter as <c>(@owned Value, @guaranteed Index..., @guaranteed self)</c>), so
/// the new value — always the first parameter — is handed over at +1 while the indices beside
/// it are borrowed like any ordinary argument. Nothing in the ABI JSON records this: <c>@owned</c>
/// is implied by the accessor's lowering, never spelled as an ownership annotation, which is why
/// the test below is written against the parameter position rather than read off
/// <see cref="ArgumentDecl.Ownership"/>.</para>
///
/// <para>Whether that hand-over actually reaches Swift's accessor depends on what the P/Invoke
/// names. A Swift-source wrapper — a <c>@_cdecl</c> wrapper, a <c>@_silgen_name</c> free
/// function, the Optional-pointer out-buffer wrapper, an async bridge — introduces a Swift frame
/// whose own parameter is borrowed: it copies the value out and passes its own +1 on, so the
/// caller keeps ownership and must still destroy its copy. The native assembly thunk is not such
/// a frame. It shifts registers and tail-calls the real accessor symbol, owning nothing and
/// balancing nothing, so the accessor's <c>@owned</c> convention reaches the C# caller unchanged
/// and the value has to be handed across at +1 exactly as on a direct accessor call.</para>
///
/// <para>Reusing the carrier predicate for this decision conflates "does the value move through
/// memory?" with "does the callee release it?" The two sets coincide on every arm except the
/// thunk, and on that one arm the answer was wrong in the dangerous direction: the value was
/// passed borrowed to a callee that releases it, so a strong store took zero net counts and the
/// object was over-released later, on whatever thread next touched it.</para>
/// </summary>
internal static class SetterValueOwnership
{
    /// <summary>
    /// True when <paramref name="argumentDecl"/> is the new value of <paramref name="methodDecl"/>'s
    /// setter — the one argument the accessor takes <c>@owned</c>. The distinction is per-argument,
    /// not per-member: treating a whole setter as consuming would skip the Destroy on a subscript's
    /// indices and leak them, and treating none of it as consuming double-releases the value.
    /// </summary>
    internal static bool IsSetterValue(MethodDecl methodDecl, ArgumentDecl argumentDecl)
        => MarshallingHelpers.MethodIsSetter(methodDecl)
           && ReferenceEquals(methodDecl.CSSignature.ElementAtOrDefault(1), argumentDecl);

    /// <summary>
    /// True when the callee this member's P/Invoke names releases the arguments it is passed —
    /// Swift's own accessor symbol, whether reached directly or through the ownership-transparent
    /// native thunk. False for every Swift-source wrapper, which borrows.
    ///
    /// <para>The native thunk has to be tested first and positively, because the flag that says
    /// "the entry point lives in the generated wrapper library" is set for it as well as for the
    /// normalization emitters that write real Swift wrapper functions. That flag answers which
    /// dylib exports the symbol, not who owns the arguments; only the strategy separates a
    /// register-shifting thunk from a Swift frame that borrows.</para>
    /// </summary>
    internal static bool CalleeConsumesOwnedArguments(MethodDecl methodDecl)
        => !methodDecl.IsAsync
           && (methodDecl.UsesNativeThunk
               || (!methodDecl.UsesCdeclWrapper
                   && !methodDecl.UsesFreeFunctionWrapper
                   && !methodDecl.UsesWrapperLibrary
                   && !methodDecl.HasOptionalPointerWrapper));

    /// <summary>
    /// True when <paramref name="argumentDecl"/> must be handed across at +1: it is the setter's
    /// new value AND the callee consumes it. Callers that emit a hand-over (a retain, a
    /// transferring carrier accessor, a <c>MarkConsumed</c>) and callers that leave the destroy
    /// armed both ask this one question, so the two halves cannot drift into a leak or a
    /// double-release.
    /// </summary>
    internal static bool IsHandedOverToCallee(MethodDecl methodDecl, ArgumentDecl argumentDecl)
        => IsSetterValue(methodDecl, argumentDecl) && CalleeConsumesOwnedArguments(methodDecl);
}
