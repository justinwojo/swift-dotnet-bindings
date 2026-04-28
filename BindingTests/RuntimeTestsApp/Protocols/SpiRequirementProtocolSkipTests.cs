// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Bug 16 regression coverage: protocols whose requirement set includes a
/// `@_spi`-protected member round-trip cleanly through the binding pipeline.
///
/// Under the current toolchain, `swift-api-digester` and the swiftinterface
/// printer both strip `@_spi` requirements from the public ABI surface, so
/// neither the parser nor the emitter ever sees `__linkSPI`. The protocol that
/// reaches `EveryProtocolEmitter` looks like a one-requirement protocol
/// (`publicLabel`), the conformance, interface, and proxy all emit normally,
/// and the wrapper compiles. The `HasSuppressedRequiredMember` gate stays in
/// place as defense-in-depth for any future toolchain that surfaces an
/// `@_spi` requirement to the parser before PropertyHandler skips it.
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

    public void TestPublicLabelThroughInterface()
    {
        IBug16SpiRequirementProtocol proto = new Bug16SpiRequirementConformer();
        AssertEqual("spi-required", proto.PublicLabel, "PublicLabel reachable through IBug16SpiRequirementProtocol");
        TestLogger.Info($"IBug16SpiRequirementProtocol.PublicLabel = \"{proto.PublicLabel}\"");
    }

    #endregion

    #region Bug 16 — Proxy emission invariant

    public void TestProtocolProxyIsEmitted()
    {
        // With the @_spi requirement filtered from both abi.json and
        // swiftinterface, neither HasSuppressedRequiredMember nor
        // HasUnsatisfiedHiddenRequirements fires for this protocol. The proxy
        // (and its IBug16SpiRequirementProtocol witness for `publicLabel`)
        // must therefore be emitted normally.
        // Proxies live in the SwiftInterop sub-namespace.
        var assembly = typeof(Bug16SpiRequirementConformer).Assembly;
        var proxyType = assembly.GetType("SwiftBindingsTestLib.SwiftInterop.Bug16SpiRequirementProtocolProxy");
        AssertTrue(proxyType is not null,
            "Bug16SpiRequirementProtocolProxy MUST be emitted — the @_spi requirement is invisible to the parser, so the gate cannot fire and the proxy is witnessed normally");
        TestLogger.Info("Bug16SpiRequirementProtocolProxy correctly present in generated bindings");
    }

    #endregion
}
