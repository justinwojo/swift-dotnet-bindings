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
}
