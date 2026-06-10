// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// End-to-end coverage for protocol-extension methods that take an
/// Optional&lt;Class&gt; parameter (RealityFoundation Entity.setParent shape).
/// The @_cdecl wrapper must marshal the param as UnsafeMutableRawPointer? and
/// reconstruct via Unmanaged&lt;AnyObject&gt;.fromOpaque mapped over the
/// nullable pointer; non-nil and nil both must round-trip. The Swift side
/// observably mutates the (non-nil) parent so we can verify the right
/// reference made it across.
/// </summary>
public class ProtocolExtOptionalClassParamTests : TestBase
{
    public ProtocolExtOptionalClassParamTests(TestResults results) : base(results) { }

    public void TestNonNilParentReceivesChildId()
    {
        using var child = new PExtOptChild(nodeId: 42);
        using var parent = new PExtOptParent();
        var attached = child.AttachTo(parent);
        AssertTrue(attached, "AttachTo returns true for non-nil parent");
        AssertEqual(42, parent.LastAttachedChildId, "Parent observed child's nodeId");
    }

    public void TestNilParentDoesNotAttach()
    {
        using var child = new PExtOptChild(nodeId: 99);
        var attached = child.AttachTo(null);
        AssertTrue(!attached, "AttachTo returns false for nil parent");
    }

    // A C# class implementing the Swift protocol, vended back to Swift.
    private sealed class ManagedChild : IPExtOptChildProtocol
    {
        public ManagedChild(int nodeId) => NodeId = nodeId;
        public int NodeId { get; }
    }

    // Reverse direction of TestNonNilParentReceivesChildId: a C# conformer is
    // passed to Swift as `any PExtOptChildProtocol`. Constructing that existential
    // resolves the protocol witness table via
    // Get_EveryProtocol_PExtOptChildProtocol_WitnessTable; the protocol-extension
    // attachTo then reads child.nodeId back through the witness table into the
    // managed getter, landing the C# id in the Swift parent.
    public void TestCSharpChildDispatchesNodeIdToSwift()
    {
        using var parent = new PExtOptParent();
        var child = new ManagedChild(nodeId: 77);
        var attached = parent.AcceptChild(child);
        AssertTrue(attached, "AcceptChild returns true (Swift saw a non-nil parent)");
        AssertEqual(77, parent.LastAttachedChildId, "Swift read the C# child's nodeId via the witness table");
    }
}
