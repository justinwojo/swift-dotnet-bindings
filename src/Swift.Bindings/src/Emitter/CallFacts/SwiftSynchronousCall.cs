// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Whether a callee is an ordinary synchronous <c>swiftcc</c> call, the only kind a direct Swift
/// transition can make. Every other kind has a different calling convention or a different
/// protocol on top of it, so it is refused by name rather than lowered as if it were synchronous.
/// </summary>
public static class SwiftSynchronousCall
{
    /// <summary>Why the callee is not a synchronous <c>swiftcc</c> call, or null when it is.</summary>
    public static string? Refusal(SwiftCallFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var effects = facts.Effects;
        if (effects.IsAsync)
            return "async: the callee is swifttailcc with a swiftasync context and resumes through a continuation, not a synchronous swiftcc call";
        if (effects.TypedThrows is { } thrown && !IsUntypedError(thrown))
            return $"typed throws({thrown}): the error is returned through an indirect error result, not the swifterror register";
        if (effects.IsCoroutine)
            return "a read or modify coroutine accessor yields to its caller and must be resumed, not called once";
        if (effects.HasParameterPacks)
            return "parameter packs: the callee takes a pack of values and metadata whose length is not fixed by its signature";
        if (facts.Target.Kind == SwiftCallTargetKind.ClosureInvocation && !facts.IsComplete
            && facts.Unknowns.Any(u => u.Contains("@convention(c)", StringComparison.Ordinal)))
            return "a @convention(c) function value is called with the C convention, not as a Swift closure";

        if (IsObjCProtocolRequirement(facts))
            return $"a requirement of the @objc protocol '{facts.Target.DeclaringType}' is sent as an Objective-C message and has no Swift entry point";

        if (facts.Target.Entry != SwiftEntryKind.FunctionPointer && facts.Target.Symbol is { Length: > 0 } symbol)
        {
            if (!IsSwiftSymbol(symbol) && facts.Target.Synthesizer is null)
                return $"'{symbol}' is not a Swift symbol: a @_cdecl or C function uses the C convention";
            if (IsObjCEntryPoint(symbol))
                return $"'{symbol}' is an @objc entry point, which uses the Objective-C convention";
        }
        return null;
    }

    /// <summary>
    /// A requirement of an <c>@objc</c> protocol: its signature binds Self to the declaring protocol,
    /// and that protocol is an Objective-C one.
    /// </summary>
    private static bool IsObjCProtocolRequirement(SwiftCallFacts facts) =>
        facts.Self.Abstraction == SwiftSelfAbstraction.ProtocolRequirement
        && facts.Target.DeclaringType is { } protocol
        && !facts.Generics.Requirements.IsDefaultOrEmpty
        && facts.Generics.Requirements.Any(r => r.Target == protocol && r.TargetKind == SwiftRequirementTargetKind.ObjCProtocol);

    /// <summary><c>throws(any Error)</c> is the untyped <c>throws</c>: the error is in the swifterror register.</summary>
    private static bool IsUntypedError(string thrown) =>
        thrown is "Swift.Error" or "Error" or "any Swift.Error" or "any Error";

    private static bool IsSwiftSymbol(string symbol) =>
        symbol.StartsWith("$s", StringComparison.Ordinal) || symbol.StartsWith("_$s", StringComparison.Ordinal);

    /// <summary>The Objective-C entry point of an <c>@objc</c> member: its symbol ends in <c>To</c>.</summary>
    private static bool IsObjCEntryPoint(string symbol) => symbol.EndsWith("To", StringComparison.Ordinal);
}
