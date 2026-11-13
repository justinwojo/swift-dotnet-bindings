// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Thrown by a generated member whose only route into Swift is a direct Swift-convention
/// transition the executing runtime does not execute correctly. The member refuses before it
/// does any native work — no retain, allocation, marshalling or call has happened — so the
/// refusal leaves nothing to clean up.
/// </summary>
/// <remarks>
/// The member works on runtimes that handle the transition (for example NativeAOT, or CoreCLR
/// with its JIT); <see cref="SwiftTransitionGuard.IsSupported"/> asks the question up front. The
/// binding report lists every guarded member, its transitions and why each one could not be
/// routed through a native bridge.
/// </remarks>
public sealed class SwiftTransitionUnsupportedException : PlatformNotSupportedException
{
    internal SwiftTransitionUnsupportedException(string member, SwiftTransitionFacts facts, SwiftTransitionFacts refusedFacts, string runtime)
        : base(BuildMessage(member, facts, refusedFacts, runtime))
    {
        Member = member;
        Facts = facts;
        RefusedFacts = refusedFacts;
        Runtime = runtime;
    }

    /// <summary>The refusing member, as spelled in the generated binding.</summary>
    public string Member { get; }

    /// <summary>Every transition shape the member reaches.</summary>
    public SwiftTransitionFacts Facts { get; }

    /// <summary>The shapes among <see cref="Facts"/> that the executing runtime refuses.</summary>
    public SwiftTransitionFacts RefusedFacts { get; }

    /// <summary>The executing runtime, as the guard identified it.</summary>
    public string Runtime { get; }

    private static string BuildMessage(string member, SwiftTransitionFacts facts, SwiftTransitionFacts refused, string runtime) =>
        $"'{member}' cannot be called on {runtime}: its only route into Swift is a direct Swift-convention " +
        $"transition of a shape this runtime does not execute correctly ({refused}). It refused before any " +
        "native work. The member works on NativeAOT and on CoreCLR with its JIT; on this runtime it needs a " +
        "native bridge, which the binding report says why it lacks.";
}
