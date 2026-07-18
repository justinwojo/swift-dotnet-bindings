// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Centralizes the in-band wrapper-symbol contract: the single predicate that
/// decides whether a member's wrapper-targeting P/Invoke would reference a symbol
/// wrapper-emit never registered, plus the terminal skip action both the eager
/// throw (<see cref="PInvokeEmitter.EmitPInvoke"/> /
/// <see cref="PInvokeEmitHelper.FormatDeclarationLines"/>) and the constructor's
/// predict-then-skip gate route through.
/// </summary>
/// <remarks>
/// Wrapper-emit registers each SBW_…/SBSW_… symbol it produces in
/// <see cref="ModuleEmissionContext"/>. <see cref="FindUnregisteredWrapperSymbol"/>
/// checks that registry — the failure shape behind the 0.10.0 bugs where
/// binding-emit referenced a symbol that wrapper-emit silently dropped.
///
/// Two emit-time mechanisms consume that predicate, picked per site by *when* the
/// wrapper symbol becomes registered relative to the public C# body:
/// <list type="bullet">
///   <item><b>Constructor (predict-then-skip).</b> The Swift constructor wrapper
///   registers its symbol before the C# body is written, so the handler queries
///   <see cref="FindUnregisteredWrapperSymbol"/> up front and, on a hit, calls
///   <see cref="HandleSkip"/> without ever writing the orphan body.</item>
///   <item><b>Method / bridge (transactional rollback).</b> Async <c>@_cdecl</c>
///   wrappers register their symbol *inside* <c>EmitMethod</c> (after the public
///   signature is already written), so a pre-emit query cannot distinguish a valid
///   async method from a silent bail. These sites checkpoint the C# writer, emit,
///   and on the eager <see cref="WrapperSymbolContractException"/> roll the buffer
///   back to the checkpoint before calling <see cref="HandleSkip"/>.</item>
/// </list>
/// Either way the recovery is in-emission — there is no post-emit text-strip pass.
/// <see cref="HandleSkip"/> writes the <c>// Unsupported: …</c> marker, records the
/// skip on <see cref="ReportCollector"/>, and logs a structured warning.
/// </remarks>
internal static class WrapperSymbolContractGate
{
    /// <summary>
    /// The single source of truth for "would this member's wrapper-targeting P/Invoke
    /// reference a symbol wrapper-emit never registered?". Returns the unregistered
    /// wrapper entry point, or <c>null</c> when the contract holds (or does not apply —
    /// no emission context, or a non-wrapper entry point). Mirrors the pairing check in
    /// <see cref="PInvokeEmitter.EmitPInvoke"/>: Cdecl pairs with an <c>SBW_</c> prefix,
    /// Swift CC with an <c>SBSW_</c> prefix; the resolved calling convention is selected
    /// through <see cref="PInvokeEmitHelper.SelectCallingConvention"/> so the prefix and
    /// call-conv stay consistent even if a caller misspecs one half.
    /// </summary>
    public static string? FindUnregisteredWrapperSymbol(MethodEnvironment methodEnv)
    {
        ArgumentNullException.ThrowIfNull(methodEnv);

        if (methodEnv.EmissionContext is not { } emissionContext)
            return null;

        var methodDecl = (MethodDecl)methodEnv.MethodDecl;
        var (entryPoint, _) = PInvokeEmitter.ComputeEntryPoint(methodEnv);
        var declaredCallConv = WrapperValidation.GetCallingConvention(methodDecl);
        var resolvedCallConv = PInvokeEmitHelper.SelectCallingConvention(entryPoint, declaredCallConv);
        bool wrapperPair =
            (resolvedCallConv == PInvokeCallingConvention.Cdecl &&
             PInvokeEmitHelper.IsWrapperEntryPoint(entryPoint)) ||
            (resolvedCallConv == PInvokeCallingConvention.Swift &&
             PInvokeEmitHelper.IsSwiftCCWrapperEntryPoint(entryPoint));

        return wrapperPair && !emissionContext.IsWrapperSymbolRegistered(entryPoint)
            ? entryPoint
            : null;
    }

    /// <summary>
    /// Terminal skip action for a member whose wrapper symbol is unregistered: writes the
    /// <c>// Unsupported: …</c> marker, records the skip on <see cref="ReportCollector"/>,
    /// and logs a warning. Callers must <c>return</c> from their handler immediately after
    /// invoking this — the rest of the emit path (post-processors, RecordMemberEmitted, …)
    /// must not run for a member that won't compile. Predict-then-skip callers invoke this
    /// before any body is written; rollback callers must undo the member's speculative emission
    /// first — via <see cref="MemberEmissionTransaction"/>, which covers the Swift wrapper buffer
    /// as well as the C# one — so no orphan body survives.
    /// </summary>
    public static void HandleSkip(
        MethodEnvironment methodEnv,
        string missingSymbol,
        CSharpWriter csWriter,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(methodEnv);
        ArgumentNullException.ThrowIfNull(missingSymbol);
        ArgumentNullException.ThrowIfNull(csWriter);

        var methodDecl = (MethodDecl)methodEnv.MethodDecl;
        var details = $"wrapper symbol '{missingSymbol}' not registered by wrapper-emit";

        UnsupportedCommentEmitter.EmitMemberSkipped(
            csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.MissingWrapperSymbol, details, containingDecl: methodDecl.ParentDecl);
        ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.MissingWrapperSymbol, details);
        logger.LogWarning(
            "Wrapper-symbol contract: skipping member '{Member}' on '{Parent}' — wrapper symbol '{Symbol}' not registered.",
            methodDecl.Name, methodDecl.ParentDecl?.Name, missingSymbol);
    }

    /// <summary>
    /// Records a member whose Swift wrapper block had to be kept when its C# side was rolled back.
    /// <para>The block is left complete, compiling, and unreachable from managed code — dead weight
    /// in the wrapper source, deliberately preferred over the alternative. Truncating a span that
    /// also committed module-shared Swift helpers would take those helpers with it, and nothing
    /// will write them again, so every later member referring to them would fail to compile: a
    /// wrapper library that does not build at all.</para>
    /// <para>The reason is reported rather than assumed. Only
    /// <see cref="MemberEmissionTransaction.SwiftKeep.SharedHelperCommitted"/> means helper text is
    /// actually sitting in the span; the other two mean the transaction could not prove the span
    /// was member-private and kept it fail-safe. Collapsing them would send a reader hunting for a
    /// shared helper that was never committed.</para>
    /// </summary>
    public static void ReportKeptWrapperBlock(
        MemberEmissionTransaction.SwiftKeep keepReason,
        MethodEnvironment methodEnv,
        string missingSymbol,
        ILogger logger)
    {
        if (keepReason == MemberEmissionTransaction.SwiftKeep.RolledBack)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(methodEnv);

        var explanation = keepReason switch
        {
            MemberEmissionTransaction.SwiftKeep.SharedHelperCommitted =>
                "module-shared Swift helpers were committed in the same span, so truncating it would delete "
                + "definitions that cannot be re-emitted",
            MemberEmissionTransaction.SwiftKeep.NoSwiftWriter =>
                "no Swift writer took part in this member's emission, so there was no span to discard",
            MemberEmissionTransaction.SwiftKeep.NoEmissionContext =>
                "no emission context was available, so the span could not be proven member-private",
            // Deliberately generic: true of any keep reason. A future reason that falls here reads
            // vague rather than wrong, which a specific-but-mismatched fallback would not.
            _ => "the span could not be proven member-private",
        };

        // The preamble states only that the span was not discarded — asserting a block was "kept"
        // would be false for the NoSwiftWriter case, where no Swift span existed at all.
        var methodDecl = (MethodDecl)methodEnv.MethodDecl;
        logger.LogDebug(
            "Wrapper-symbol contract: the Swift wrapper span for skipped member '{Member}' on '{Parent}' "
            + "(symbol '{Symbol}') was not discarded — {Reason}.",
            methodDecl.Name, methodDecl.ParentDecl?.Name, missingSymbol, explanation);
    }
}
