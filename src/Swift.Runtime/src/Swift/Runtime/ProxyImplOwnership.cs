// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;

namespace Swift.Runtime;

/// <summary>
/// Selects which side owns the lifetime of a generated <c>{Protocol}Proxy</c> that wraps a C#
/// implementation, and therefore how the implementation is rooted while Swift can still call
/// back into it. Passed by generated marshalling code to the proxy's implementation-taking
/// constructor.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public enum ProxyImplOwnership
{
    /// <summary>
    /// Default. The Swift sink retains the conformer box (a strong stored property, a borrowed
    /// parameter, a collection element), so Swift's own liveness is what must keep the
    /// implementation reachable: the proxy references the implementation only weakly and
    /// <see cref="ProxyLifetimeTracker.Track"/> allocates a strong root freed when the box
    /// deinitializes.
    /// </summary>
    SwiftRooted = 0,

    /// <summary>
    /// The Swift sink does NOT retain the conformer box (a <c>weak</c>/<c>unowned</c> stored
    /// property), so nothing on the Swift side keeps the carrier alive and the consumer's
    /// implementation object owns it instead. The proxy references the implementation
    /// <b>strongly</b> — which keeps it resolvable for a callback racing the proxy's finalizer —
    /// and <see cref="ProxyLifetimeTracker.TrackConsumerOwned"/> allocates a long weak root, so
    /// the implementation and its carrier stay collectable as one unit.
    /// </summary>
    ConsumerOwned = 1,
}
