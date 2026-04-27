// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Bug #5 regression coverage: protocols whose requirements are all `static var`
/// must skip the EveryProtocol conformance instead of emitting a `fatalError`
/// stub that fails Swift's type-checker. The fixture's protocol, conformer, and
/// consumer build only when the wrapper compiles cleanly — before the fix the
/// generated `extension EveryProtocol: Bug5StaticOnlyProtocol { ... }` block
/// rejected the conformance and the dylib never linked.
///
/// The proxy class for the protocol is intentionally suppressed via the existing
/// `EveryProtocolConformanceSkipped` propagation path. The protocol interface
/// itself is still emitted, so consumers can pass conformers around and call
/// instance methods that surface the static values.
/// </summary>
public class StaticOnlyProtocolSkipTests : TestBase
{
    public StaticOnlyProtocolSkipTests(TestResults results) : base(results) { }

    #region Bug #5 — Wrapper compile + round-trip (Tier 1)

    public void TestConsumerConstruction()
    {
        var consumer = new Bug5StaticOnlyConsumer();
        AssertNotNull(consumer, "Bug5StaticOnlyConsumer constructed (wrapper compiled past the static-var conformance site)");
        TestLogger.Info("Bug5StaticOnlyConsumer construction passed");
    }

    public void TestConformerConstruction()
    {
        var conformer = new Bug5StaticOnlyConformer();
        AssertNotNull(conformer, "Bug5StaticOnlyConformer constructed");
        TestLogger.Info("Bug5StaticOnlyConformer construction passed");
    }

    public void TestStaticIdentifierThroughInstanceWrapper()
    {
        var consumer = new Bug5StaticOnlyConsumer();
        // The C# generator emits zero-arg instance methods with a `Get` prefix,
        // so Swift's `identifier()` projects to `GetIdentifier()`.
        var identifier = consumer.GetIdentifier();
        AssertEqual("static-only-default", identifier, "GetIdentifier() returns the protocol's static defaultIdentifier value");
        TestLogger.Info($"Bug5StaticOnlyConsumer.GetIdentifier() = \"{identifier}\"");
    }

    public void TestStaticRankThroughInstanceWrapper()
    {
        var consumer = new Bug5StaticOnlyConsumer();
        var rank = consumer.GetRank();
        AssertEqual(7, rank, "GetRank() returns the protocol's static defaultRank value");
        TestLogger.Info($"Bug5StaticOnlyConsumer.GetRank() = {rank}");
    }

    #endregion

    #region Bug #5 — Proxy suppression invariant (Tier 2)

    public void TestProtocolProxyIsNotEmitted()
    {
        // The C# interface IBug5StaticOnlyProtocol must still be emitted (consumers
        // can hold typed references to existential values), but the *proxy* class
        // — which would need the witness table getter Swift refused to compile —
        // must be suppressed via the EveryProtocolConformanceSkipped path.
        var assembly = typeof(Bug5StaticOnlyConformer).Assembly;
        var proxyType = assembly.GetType("SwiftBindingsTestLib.Bug5StaticOnlyProtocolProxy");
        AssertTrue(proxyType is null,
            "Bug5StaticOnlyProtocolProxy must NOT be emitted — Bug #5 requires the proxy to be suppressed when EveryProtocol conformance is skipped");
        TestLogger.Info("Bug5StaticOnlyProtocolProxy correctly absent from generated bindings");
    }

    #endregion
}
