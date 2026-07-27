// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Threading;
using CoreFoundation;
using Foundation;

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// A <see cref="SynchronizationContext"/> that returns <c>await</c> continuations to the
/// platform main thread by posting them to the main dispatch queue.
/// </summary>
/// <remarks>
/// The iOS and tvOS hosts get an equivalent context for free — <c>UIApplication.Main</c>
/// installs one — so a suite kicked off on the main thread stays on the main thread across
/// every suspension point. The macOS and Mac Catalyst hosts are plain executables with no
/// NSApplication/UIApplication, so nothing installs a context and every continuation would
/// resume on a threadpool thread. That matters because the suite deliberately exercises
/// Swift <c>@MainActor</c>-isolated declarations, and the runtime's main-actor guard
/// (correctly) refuses those anywhere but the main thread.
///
/// Posting to <c>DispatchQueue.MainQueue</c> is the same mechanism Swift's own main-actor
/// hops use, so a single main-thread run-loop pump drains both the suite's continuations
/// and Swift's GCD callbacks.
/// </remarks>
internal sealed class MainQueueSynchronizationContext : SynchronizationContext
{
    public override SynchronizationContext CreateCopy() => new MainQueueSynchronizationContext();

    public override void Post(SendOrPostCallback d, object? state)
        => DispatchQueue.MainQueue.DispatchAsync(() => d(state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        // Run inline when already on the main thread: dispatching synchronously onto the
        // queue this thread is currently draining would deadlock.
        if (NSThread.IsMain)
            d(state);
        else
            DispatchQueue.MainQueue.DispatchSync(() => d(state));
    }
}
