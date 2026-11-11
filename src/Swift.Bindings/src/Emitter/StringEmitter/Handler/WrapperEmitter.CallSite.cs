// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// The ownership and error halves of an ordinary member body, for a body that writes its own
    /// P/Invoke call — the operator's.
    ///
    /// <para>Whatever writes the call, the callee is the same Swift symbol (or the ownership-
    /// transparent native thunk in front of it), so the call site owes it the same things: a +1 for
    /// every argument its lowering consumes, and a check of the error slot before the result is
    /// read. These members emit exactly those statements, from this emitter's own plan and with its
    /// own emitters, so a body that brings its own call cannot model either half differently from
    /// an ordinary member with the same signature.</para>
    ///
    /// <para>The order is the ordinary body's: <see cref="EmitCallSitePreamble"/> before the call,
    /// <see cref="EmitCallSiteEpilogue"/> straight after it and before any read of the result, and
    /// <see cref="EmitCallSiteMemberDeclarations"/> beside the member rather than inside it.</para>
    /// </summary>
    internal partial class WrapperEmitter
    {
        /// <summary>
        /// The local the call's result is stored in before the error check runs. Resolved against
        /// the member's parameter names, so it cannot shadow an operand.
        /// </summary>
        internal string CallSiteResultLocalName => _syncPlan.ReturnLocalName;

        /// <summary>
        /// True when the call site emits anything at all: an error check, or a hand-over for a
        /// consumed argument. A body that needs neither keeps its plain call.
        /// </summary>
        internal static bool CallSiteNeedsOwnershipOrErrorHalves(MethodEnvironment env)
            => (!env.MethodDecl.IsAsync && env.MethodDecl.Throws)
               || env.MethodDecl.CSSignature.Skip(1).Any(a => CalleeArgumentOwnership.IsHandedOverToCallee(env.MethodDecl, a));

        /// <summary>
        /// Writes the member-level declarations the error check calls: the error-description and
        /// error-release P/Invokes, deduplicated per type exactly as for an ordinary throwing member.
        /// The operator declines typed throws, so there is no typed-error extractor to write.
        /// </summary>
        internal void EmitCallSiteMemberDeclarations(CSharpWriter csWriter)
            => EmitErrorHelperPInvokes(csWriter);

        /// <summary>
        /// Writes what precedes the call: the zero-initialized error carrier a direct Swift-convention
        /// call passes by <c>ref</c>, and the provisional +1 lease for every argument the callee
        /// consumes. A failure before native entry rolls each lease back.
        /// </summary>
        internal void EmitCallSitePreamble(CSharpWriter csWriter)
        {
            if (_syncPlan.SwiftError?.CarrierDeclaration is { } carrier)
                csWriter.WriteLine(carrier);
            EmitConsumedArgumentHandOvers(csWriter);
        }

        /// <summary>
        /// Writes what follows the call, before its result is read: completes the hand-overs (the
        /// callee now owns those counts, whether it returned or threw), then checks the error slot and
        /// throws the member's Swift-error exception, releasing the error box exactly as an ordinary
        /// throwing member does. A thrown error leaves the result register undefined, so nothing may
        /// interpret it first.
        /// </summary>
        internal void EmitCallSiteEpilogue(CSharpWriter csWriter)
        {
            EmitConsumedArgumentTransfersCompleted(csWriter);
            EmitSwiftError(csWriter);
        }
    }
}
