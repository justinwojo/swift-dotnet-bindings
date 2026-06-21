// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// A snapshot of how many Swift-backed objects the runtime is currently rooting.
/// Cross-heap retain cycles (a Swift object holding a C# callback that captures it back)
/// cannot be detected automatically, but a census taken at two points — before and after
/// a scope a consumer expects to fully release — makes a leak visible as counts that fail
/// to return to baseline.
/// </summary>
public readonly struct SwiftLeakCensusReport
{
    /// <summary>
    /// Registered proxy entries in the weak registry (after cleanup of expired slots). This
    /// is a <b>superset</b> of <see cref="StronglyHeldProxies"/> — a strongly-held proxy is
    /// also counted here — so the fields are deliberately not summed into a single total.
    /// </summary>
    public int RegisteredProxies { get; }

    /// <summary>
    /// The subset of registered proxies the runtime additionally holds a strong reference to.
    /// </summary>
    public int StronglyHeldProxies { get; }

    /// <summary>Count of EveryProtocol impl roots held for Swift-owned C# conformers.</summary>
    public int ProxyImplRoots { get; }

    internal SwiftLeakCensusReport(int registeredProxies, int stronglyHeldProxies, int proxyImplRoots)
    {
        RegisteredProxies = registeredProxies;
        StronglyHeldProxies = stronglyHeldProxies;
        ProxyImplRoots = proxyImplRoots;
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"[SwiftBindings] leak census: registered={RegisteredProxies}, " +
           $"stronglyHeld={StronglyHeldProxies}, implRoots={ProxyImplRoots}";
}

/// <summary>
/// Debug diagnostic for surfacing cross-heap Swift/.NET object leaks. The retain cycle a
/// self-capturing callback creates is unbreakable from the runtime side, so this does not
/// free anything — it reports the live root counts so a consumer can detect a leak by
/// comparing a before/after census around a scope. See <see cref="SwiftLeakCensusReport"/>
/// and <see cref="WeakSwiftReference{T}"/>.
/// </summary>
public static class SwiftLeakCensus
{
    /// <summary>
    /// Captures a snapshot of the runtime's current object-rooting counts. Intended to be
    /// called from a consumer's own <c>[Conditional("DEBUG")]</c> diagnostic hook; the
    /// returned report is cheap to capture and safe to log.
    /// </summary>
    /// <returns>The current census snapshot.</returns>
    public static SwiftLeakCensusReport Report()
    {
        // Drop expired weak slots first so RegisteredProxies reflects live objects rather
        // than registry entries that have not yet been swept.
        SwiftObjectRegistry.Cleanup();
        return new SwiftLeakCensusReport(
            SwiftObjectRegistry.Count,
            SwiftObjectRegistry.StrongCount,
            ProxyLifetimeTracker.ImplRootCount);
    }
}
