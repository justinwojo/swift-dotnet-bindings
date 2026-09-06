// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Answers who owns the arguments a Swift member is handed — an ownership question, deliberately
/// separate from the width question <see cref="DirectOptionalAbi.UsesSwiftSideCarrier"/> answers.
///
/// <para>SILGen lowers an ordinary <c>func</c> as <c>(@guaranteed Args…, @guaranteed self)</c>:
/// the caller keeps ownership and the callee borrows. Two member kinds break that pattern and take
/// their arguments <c>@owned</c> (<c>@in</c> for an address-only type), meaning the callee releases
/// them:</para>
/// <list type="bullet">
///   <item>an <b>initializer</b> — every value parameter, and multi-parameter initializers in full,
///     lower as <c>(@owned A, @owned B, …, @thin Self.Type) -&gt; @owned Self</c>;</item>
///   <item>a <b>setter</b> — the new value AND, on a subscript, the indices beside it:
///     <c>subscript(i: Idx, s: String) -&gt; Tok</c> lowers its setter as
///     <c>(@owned Tok, @owned Idx, @owned String, @inout Self) -&gt; ()</c>. The matching
///     <em>getter</em> borrows the same indices, so the convention follows the accessor, not the
///     parameter position.</item>
/// </list>
///
/// <para>Nothing in the ABI JSON records that default: it is implied by the member's lowering
/// rather than spelled out, which is why the test below is written against the member kind. What
/// the ABI JSON does record is an <em>explicit</em> specifier, and an explicit <c>borrowing</c>
/// overrides the member-kind default per parameter — so <see cref="ArgumentDecl.Ownership"/> is
/// consulted as the exception, not as the source.</para>
///
/// <para>Whether that hand-over actually reaches Swift depends on what the P/Invoke names. A
/// Swift-source wrapper — a <c>@_cdecl</c> wrapper, a <c>@_silgen_name</c> free function, the
/// Optional-pointer out-buffer wrapper, an async bridge — introduces a Swift frame whose own
/// parameter is borrowed; SILGen inserts the <c>retain_value</c> when that frame forwards the
/// value on, so the caller keeps ownership and must still destroy its copy. The native assembly
/// thunk is not such a frame. It shifts registers and tail-calls the real symbol, owning nothing
/// and balancing nothing, so the callee's <c>@owned</c> convention reaches the C# caller unchanged
/// and the value has to be handed across at +1 exactly as on a direct call.</para>
///
/// <para>Reusing the carrier predicate for this decision conflates "does the value move through
/// memory?" with "does the callee release it?" The two sets coincide on every arm except the
/// thunk, and on that one arm the answer was wrong in the dangerous direction: the value was
/// passed borrowed to a callee that releases it, so a strong store took zero net counts and the
/// object was over-released later, on whatever thread next touched it.</para>
/// </summary>
internal static class CalleeArgumentOwnership
{
    /// <summary>
    /// True when <paramref name="argumentDecl"/> is the new value of <paramref name="methodDecl"/>'s
    /// setter — the first parameter, the one a non-retaining (weak/unowned) sink stores without
    /// taking a count. This is a question about which slot the value lands in, not about ownership;
    /// use <see cref="IsConsumedByCallee"/> for the latter.
    /// </summary>
    internal static bool IsSetterNewValue(MethodDecl methodDecl, ArgumentDecl argumentDecl)
        => IsSetter(methodDecl)
           && ReferenceEquals(methodDecl.CSSignature.ElementAtOrDefault(1), argumentDecl);

    /// <summary>
    /// True for a real property or subscript setter. The name test alone matches any Swift member
    /// whose own name happens to end in the accessor suffix — an ordinary <c>func inspect_Set(…)</c>
    /// borrows its arguments like any other function, so treating it as a setter would hand its
    /// reference-bearing parameters across at +1 and strand a count on every call.
    /// </summary>
    private static bool IsSetter(MethodDecl methodDecl)
        => methodDecl.IsAccessor && MarshallingHelpers.MethodIsSetter(methodDecl);

    /// <summary>
    /// True when Swift's own lowering of <paramref name="methodDecl"/> takes
    /// <paramref name="argumentDecl"/> <c>@owned</c>. The member kind sets the default — on a setter
    /// or an initializer every value parameter is consumed, and on everything else none is — but an
    /// explicit ownership specifier on the parameter itself overrides it: SILGen lowers
    /// <c>init(w: borrowing W, n: String)</c> as <c>(@guaranteed W, @owned String, …)</c>, so the
    /// annotated parameter is borrowed while its unannotated sibling is still consumed. Handing a
    /// borrowed parameter across at +1 strands a count for the life of the process, so the
    /// annotation is honoured wherever the parser recorded one; a synthetic argument leaves it at
    /// <see cref="ParameterOwnership.Default"/> and keeps the member-kind answer.
    /// </summary>
    internal static bool IsConsumedByCallee(MethodDecl methodDecl, ArgumentDecl argumentDecl)
        => (IsSetter(methodDecl) || methodDecl.IsConstructor)
           && argumentDecl.Ownership is not (ParameterOwnership.Shared or ParameterOwnership.InOut)
           && !argumentDecl.IsInOut
           && methodDecl.CSSignature.Skip(1).Any(a => ReferenceEquals(a, argumentDecl));

    /// <summary>
    /// True when the callee this member's P/Invoke names releases the arguments it is passed —
    /// Swift's own symbol, whether reached directly or through the ownership-transparent native
    /// thunk. False for every Swift-source wrapper, which borrows.
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
    /// True when <paramref name="argumentDecl"/> must be handed across at +1: Swift's lowering takes
    /// it <c>@owned</c> AND the callee the P/Invoke names is Swift's own symbol. Callers that emit a
    /// hand-over (a retain, a value-witness copy, a transferring carrier accessor, a
    /// <c>MarkConsumed</c>) and callers that leave the destroy armed both ask this one question, so
    /// the two halves cannot drift into a leak or a double-release.
    /// </summary>
    internal static bool IsHandedOverToCallee(MethodDecl methodDecl, ArgumentDecl argumentDecl)
        => IsConsumedByCallee(methodDecl, argumentDecl) && CalleeConsumesOwnedArguments(methodDecl);
}
