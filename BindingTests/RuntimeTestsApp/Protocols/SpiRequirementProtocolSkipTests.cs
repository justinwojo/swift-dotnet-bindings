// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Bug 16 regression coverage: protocols whose requirements include any
/// `@_spi`-protected (or otherwise parser-suppressed) member must skip the
/// EveryProtocol conformance instead of emitting an unsatisfiable
/// `extension EveryProtocol: P` that Swift's type-checker rejects. Before the
/// generalized "required-but-suppressed" gate the wrapper failed to compile;
/// after the gate the conformance is dropped and the proxy class is suppressed
/// via the existing `EveryProtocolConformanceSkipped` propagation path.
///
/// The C# interface itself is still emitted, so consumers can hold typed
/// references to existential values; only the proxy (which would need a witness
/// for the SPI requirement) is suppressed.
/// </summary>
public class SpiRequirementProtocolSkipTests : TestBase
{
    public SpiRequirementProtocolSkipTests(TestResults results) : base(results) { }

    #region Bug 16 — Wrapper compile + round-trip

    public void TestConsumerConstruction()
    {
        var consumer = new Bug16SpiRequirementConsumer();
        AssertNotNull(consumer, "Bug16SpiRequirementConsumer constructed (wrapper compiled past the SPI-required protocol conformance site)");
        TestLogger.Info("Bug16SpiRequirementConsumer construction passed");
    }

    public void TestConformerConstruction()
    {
        var conformer = new Bug16SpiRequirementConformer();
        AssertNotNull(conformer, "Bug16SpiRequirementConformer constructed");
        TestLogger.Info("Bug16SpiRequirementConformer construction passed");
    }

    public void TestPublicLabelThroughInstanceWrapper()
    {
        var consumer = new Bug16SpiRequirementConsumer();
        var label = consumer.GetLabel();
        AssertEqual("spi-required", label, "GetLabel() returns the public requirement value");
        TestLogger.Info($"Bug16SpiRequirementConsumer.GetLabel() = \"{label}\"");
    }

    #endregion

    #region Bug 16 — Proxy suppression invariant

    public void TestProtocolProxyIsNotEmitted()
    {
        // The C# interface IBug16SpiRequirementProtocol must still be emitted
        // (consumers can hold typed references), but the *proxy* class — which
        // would need a witness for the SPI-protected `__linkSPI` requirement —
        // must be suppressed via the EveryProtocolConformanceSkipped path.
        var assembly = typeof(Bug16SpiRequirementConformer).Assembly;
        var proxyType = assembly.GetType("SwiftBindingsTestLib.Bug16SpiRequirementProtocolProxy");
        AssertTrue(proxyType is null,
            "Bug16SpiRequirementProtocolProxy must NOT be emitted — Bug 16 requires the proxy to be suppressed when EveryProtocol conformance is skipped");
        TestLogger.Info("Bug16SpiRequirementProtocolProxy correctly absent from generated bindings");
    }

    #endregion
}
