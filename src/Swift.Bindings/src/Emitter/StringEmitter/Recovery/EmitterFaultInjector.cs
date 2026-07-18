// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Test-only hook that makes an emission seam throw for a chosen declaration.
/// </summary>
/// <remarks>
/// Containment is only worth as much as the proof that it works, and there is no honest way to prove
/// it against real faults: every emitter defect we know about gets fixed, so a test written against
/// one stops exercising the recovery path the moment it is repaired. Injecting the fault keeps the
/// proof permanent. When no hook is installed every member here short-circuits on a null field read,
/// so production behavior is unchanged.
/// </remarks>
internal static class EmitterFaultInjector
{
    private static readonly AsyncLocal<Func<DeclId, Exception?>?> Hook = new();

    /// <summary>
    /// Installs <paramref name="hook"/> for the lifetime of the returned scope. The hook returns the
    /// exception to throw for a given declaration, or null to let it emit normally.
    /// </summary>
    internal static IDisposable Install(Func<DeclId, Exception?> hook) => new Scope(hook);

    /// <summary>Throws whatever the installed hook chose for <paramref name="subject"/>, if anything.</summary>
    internal static void MaybeThrow(in DeclId subject)
    {
        var hook = Hook.Value;
        if (hook is null)
        {
            return;
        }

        var injected = hook(subject);
        if (injected is not null)
        {
            throw injected;
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly Func<DeclId, Exception?>? _previous;
        private bool _disposed;

        public Scope(Func<DeclId, Exception?> hook)
        {
            _previous = Hook.Value;
            Hook.Value = hook;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Hook.Value = _previous;
        }
    }
}
