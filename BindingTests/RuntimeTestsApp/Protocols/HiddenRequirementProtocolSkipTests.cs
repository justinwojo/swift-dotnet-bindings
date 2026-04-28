// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Bug 17 regression coverage: when a protocol declares a `__`-prefixed
/// requirement that DOES survive into the ABI JSON, the gate that skips
/// EveryProtocol conformance for digester-stripped requirements must NOT fire.
/// The proxy class, the C# interface, and the EveryProtocol witness for the
/// `__`-prefixed member must all emit normally.
///
/// The complementary digester-stripped shape (where `abi.json` lacks the
/// requirement) is exercised by `RealityFoundation.MaterialFunction` in
/// `nuke validate`: there the gate fires and the proxy is suppressed. Together
/// these two cover the gate from both sides.
/// </summary>
public class HiddenRequirementProtocolSkipTests : TestBase
{
    public HiddenRequirementProtocolSkipTests(TestResults results) : base(results) { }

    #region Bug 17 — Wrapper compile + round-trip

    public void TestConsumerConstruction()
    {
        var consumer = new Bug17HiddenRequirementConsumer();
        AssertNotNull(consumer, "Bug17HiddenRequirementConsumer constructed (wrapper compiled past the __-prefixed requirement)");
        TestLogger.Info("Bug17HiddenRequirementConsumer construction passed");
    }

    public void TestConformerConstruction()
    {
        var conformer = new Bug17HiddenRequirementConformer();
        AssertNotNull(conformer, "Bug17HiddenRequirementConformer constructed");
        TestLogger.Info("Bug17HiddenRequirementConformer construction passed");
    }

    public void TestPublicLabelThroughInstanceWrapper()
    {
        var consumer = new Bug17HiddenRequirementConsumer();
        var label = consumer.GetLabel();
        AssertEqual("hidden-required", label, "GetLabel() returns the public requirement value");
        TestLogger.Info($"Bug17HiddenRequirementConsumer.GetLabel() = \"{label}\"");
    }

    public void TestUnderscoredRequirementReachableThroughInterface()
    {
        // The whole point of the gate-scope fix: when the ABI carries the
        // __-prefixed requirement, the C# interface AND the conformer must
        // expose a working witness. Reading it through the interface proves
        // the proxy/witness path is wired up end-to-end.
        IBug17HiddenRequirementProtocol proto = new Bug17HiddenRequirementConformer();
        AssertEqual(true, proto.__linkSPI, "__linkSPI getter returns the conformer's value through IBug17HiddenRequirementProtocol");
        TestLogger.Info($"IBug17HiddenRequirementProtocol.__linkSPI = {proto.__linkSPI}");
    }

    #endregion

    #region Bug 17 — Proxy emission invariant

    public void TestProtocolProxyIsEmitted()
    {
        // The gate must NOT fire here: the ABI carries `__linkSPI`, so the
        // proxy can be witnessed normally. (The MaterialFunction case where the
        // proxy IS suppressed is covered by `nuke validate` against the
        // RealityFoundation framework.)
        // Proxies live in the SwiftInterop sub-namespace.
        var assembly = typeof(Bug17HiddenRequirementConformer).Assembly;
        var proxyType = assembly.GetType("SwiftBindingsTestLib.SwiftInterop.Bug17HiddenRequirementProtocolProxy");
        AssertTrue(proxyType is not null,
            "Bug17HiddenRequirementProtocolProxy MUST be emitted — when the __-prefixed requirement is present in the ABI, the gate must not suppress the proxy");
        TestLogger.Info("Bug17HiddenRequirementProtocolProxy correctly present in generated bindings");
    }

    #endregion
}
