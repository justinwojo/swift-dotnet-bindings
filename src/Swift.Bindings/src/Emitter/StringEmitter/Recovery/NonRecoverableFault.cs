// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Decides whether a caught exception may be converted into a skipped declaration, or must keep
/// propagating.
/// </summary>
/// <remarks>
/// Containment only helps when the failure is genuinely local to one declaration. Turning a process-
/// level failure into a tombstone would be strictly worse than crashing: the generator would report a
/// confident, well-attributed "this member was unsupported" for a module that actually ran out of
/// memory or had its output directory yanked, and the build would go green on a binding that is
/// silently missing arbitrary surface. Everything listed here is therefore rethrown untouched.
/// </remarks>
internal static class NonRecoverableFault
{
    /// <summary>
    /// True when <paramref name="exception"/> must propagate rather than poison a declaration.
    /// </summary>
    public static bool Test(Exception exception) => exception switch
    {
        // Resource exhaustion and stack overflow say nothing about the declaration being emitted, and
        // any retry would hit the same wall with less headroom. (StackOverflowException is not
        // catchable on CoreCLR — it is listed so the intent survives a runtime that ever makes it so.)
        OutOfMemoryException => true,
        StackOverflowException => true,
        InsufficientExecutionStackException => true,

        // Cancellation is the caller asking us to stop. Swallowing it would make the token inert and
        // leave the generator running after a build was aborted.
        OperationCanceledException => true,

        // Filesystem and output failures are environmental. They are also not attributable to any one
        // declaration, since the emitter writes per-module files after the whole render settles.
        IOException => true,
        UnauthorizedAccessException => true,

        // The generator's own fail-closed gates. These already mean "do not ship this binding"; routing
        // them through the poison list would convert a deliberate block into a silent partial success.
        AbiContractViolationException => true,

        // The ABI validation disagreement invariant. text-fail / typed-pass on a plan-backed call is a
        // generator invariant failure — one of plan population, typed comparison, or the text scan is
        // wrong — and is never auto-resolved: it must escape the verify-recover loop untouched.
        AbiValidationInvariantException => true,

        _ => false,
    };
}
