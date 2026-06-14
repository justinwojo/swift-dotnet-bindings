// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public static partial class ClosureEmitter
{
    /// <summary>
    /// Emits the <c>catch</c> block for a NON-throwing closure
    /// <c>[UnmanagedCallersOnly]</c> callback (Void or Bool return) that invokes a managed
    /// delegate, using the line-oriented <see cref="CSharpWriter"/> at the caller's current
    /// indent. This is the single source of truth for the non-throwing-closure fault
    /// policy shared by the closure-bridge emitters (MethodClosureBridge, NestedClosureBridge,
    /// ProtocolExtensionClosureBridge); the WrapperEmitter/ClosureEmitter sibling sites emit
    /// the identical shape via string literals.
    ///
    /// A managed exception escaping the delegate must never (1) unwind into native Swift —
    /// that aborts the process with an uninformative SIGABRT — nor (2) be silently swallowed.
    /// A bare <c>catch { }</c>/<c>catch { return 0; }</c> hides the fault and hands Swift a
    /// fabricated result (e.g. <c>false</c>, or — worse — an unwritten indirect result buffer
    /// that Swift then <c>.move()</c>s as uninitialized storage). A non-throwing Swift closure
    /// has NO error channel, so the contract is a controlled
    /// <see cref="System.Environment.FailFast(string)"/> via
    /// <c>SwiftClosureMarshaller.FailFastUnhandledClosureException</c>.
    ///
    /// The trailing <c>throw;</c> is required: C# end-point reachability (CS0161) does not
    /// honor <c>[DoesNotReturn]</c>, so a value-returning callback whose catch ends in the
    /// FailFast call still trips CS0161. <c>throw;</c> is a type-agnostic terminator and is
    /// safe on the void path too (FailFast never returns, so it is never reached).
    /// </summary>
    public static void EmitNonThrowingFailFastCatch(CSharpWriter csWriter)
    {
        // Thin shim over the unified UCO guard (the FailFast policy). The unqualified marshaller
        // reference and the __ex variable name reproduce this site's historical output byte-for-byte.
        UcoGuardEmitter.EmitFailFastCatch(csWriter, exceptionVar: "__ex", fullyQualified: false);
    }
}
