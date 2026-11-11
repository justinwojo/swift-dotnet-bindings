// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Swift.Runtime;

namespace Swift.Runtime.Tests;

/// <summary>
/// Scoped test helper that sets <see cref="SwiftExitGuard"/>'s simulated exit flag for the
/// calling thread and resets it on disposal. The hook is thread-scoped, so a scope cannot leak
/// into a test running in parallel or into the GC finalizer thread. That leak is what used to
/// make an unrelated test's finalizer skip its release. Enter and dispose on the same thread:
/// every exit-path test releases its handle synchronously inside the scope.
/// </summary>
internal sealed class SwiftExitGuardTestScope : IDisposable
{
    private readonly int _ownerThreadId;
    private bool _disposed;

    private SwiftExitGuardTestScope()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    public static SwiftExitGuardTestScope Enter(bool processExiting)
    {
        SwiftExitGuard.SetProcessExitingForTest(processExiting);
        return new SwiftExitGuardTestScope();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException(
                "SwiftExitGuardTestScope must be disposed on the thread that entered it: the simulated "
                + "exit flag is thread-scoped, so disposing elsewhere would leave the entering thread's flag set.");
        SwiftExitGuard.SetProcessExitingForTest(false);
    }
}
