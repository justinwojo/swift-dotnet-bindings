// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Threading;
using Swift.Runtime;

namespace Swift.Runtime.Tests;

/// <summary>
/// Scoped test helper that mutates <see cref="SwiftExitGuard"/>'s process-global
/// exit flag under a Monitor lock and resets it on disposal. This provides
/// belt-and-suspenders mutual exclusion on top of xunit's
/// <c>[Collection(SwiftExitGuardCollection.Name)]</c> serialization: if xunit
/// collection isolation is ever imperfect (observed as a rare flake under
/// full-suite runs), the Monitor lock still guarantees that only one test
/// holds the flag at a time.
/// </summary>
internal sealed class SwiftExitGuardTestScope : IDisposable
{
    private static readonly object s_flagLock = new();
    private bool _disposed;

    private SwiftExitGuardTestScope() { }

    public static SwiftExitGuardTestScope Enter(bool processExiting)
    {
        Monitor.Enter(s_flagLock);
        try
        {
            SwiftExitGuard.SetProcessExitingForTest(processExiting);
            return new SwiftExitGuardTestScope();
        }
        catch
        {
            Monitor.Exit(s_flagLock);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            SwiftExitGuard.SetProcessExitingForTest(false);
        }
        finally
        {
            Monitor.Exit(s_flagLock);
        }
    }
}
