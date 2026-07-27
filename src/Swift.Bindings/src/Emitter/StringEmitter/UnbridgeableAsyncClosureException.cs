// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Signals that binding-emit reached the P/Invoke write site for a member carrying a
/// baseline-shaped async closure parameter that never reached the async
/// (context, startFunc) bridge, so the parameter degraded to the AnyType placeholder.
/// The placeholder is not a marshallable type: the resulting declaration is rejected by
/// the source generator that expands it, and every call site referencing it fails to
/// compile.
///
/// This is a backstop, not the gate. The member is supposed to be skipped whole at the
/// handler layer — the closure shape itself is supported, so neither the pre-dispatch
/// validator nor the unsupported-closure tombstone absorbs it, and only the handler
/// knows whether the containing member was promoted to an async @_cdecl wrapper. The
/// throw exists so that a handler path missing that check fails loudly at generation
/// time instead of shipping a binding that cannot compile. Handler sites with a
/// transactional checkpoint convert it into an honest member skip; anywhere else it
/// propagates, because a silently broken P/Invoke is worse than a hard stop.
/// </summary>
public sealed class UnbridgeableAsyncClosureException : Exception
{
    /// <summary>The C# P/Invoke method name that was about to be emitted.</summary>
    public string MethodName { get; }

    public UnbridgeableAsyncClosureException(string methodName)
        : base($"P/Invoke '{methodName}' carries a baseline async closure parameter that could not " +
               "be bridged: the containing member is not an async @_cdecl wrapper, so the closure " +
               "degraded to the unsupported-type placeholder. The member should have been skipped " +
               "at the handler layer.")
    {
        MethodName = methodName;
    }
}
