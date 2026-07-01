// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ObjCInterop;

/// <summary>
/// End-to-end gate for @objc class-bound protocol existentials in Optional position.
///
/// An @objc protocol that is also class-bound (carries an AnyObject / NSObjectProtocol
/// requirement) has an existential `any P` — and `(any P)?` — that is a single 8-byte
/// Objective-C object pointer with no Swift witness-table word, and the protocol exports
/// no Swift `…Mp` descriptor. The generator used to marshal it through the 16-byte
/// `ClassExistentialContainer1` carrier and emit a module-init metadata registration
/// keyed on the (nonexistent) descriptor symbol. That registration silently failed,
/// leaving the carrier unregistered, so the first `SwiftOptional&lt;ClassExistentialContainer1&gt;`
/// static init threw "Unable to get type metadata for type ClassExistentialContainer1" —
/// even when the value was nil. This mirrors a real-world shape: a constructor taking an
/// optional class-bound `@objc` protocol existential — `init?(content: (any P)?)` where `P`
/// is an `@objc`, `NSObjectProtocol`-rooted protocol composed with a second protocol.
///
/// The fix tracks the protocol's @objc-ness and routes its existentials off the
/// ClassExistentialContainer1 carrier onto the descriptor-free opaque container path.
/// </summary>
public class ObjCClassBoundExistentialTests : TestBase
{
    public ObjCClassBoundExistentialTests(TestResults results) : base(results) { }

    /// <summary>
    /// The exact blocking shape: constructing a box whose ctor takes
    /// `(any ObjCClassBoundShape)?` with nil. Used to throw at the
    /// `SwiftOptional&lt;ClassExistentialContainer1&gt;` static initializer before any value
    /// was even marshalled.
    /// </summary>
    public void TestNullObjCExistentialCtorDoesNotThrow()
    {
        using var box = new ObjCShapeBox(null);
        AssertNotNull(box, "ObjCShapeBox(null) constructed without a metadata crash");
        AssertEqual(-1, box.GetStoredTag(), "nil content reports the empty sentinel through the witness");
    }

    /// <summary>
    /// Forward direction with a Swift-vended conformer: store and read back the
    /// class-bound @objc existential through the constructor + property getter.
    /// </summary>
    public void TestSwiftVendedObjCExistentialRoundTrips()
    {
        var shape = TestLibFunctions.MakeObjCShape(7);
        AssertNotNull(shape, "Swift vended a non-null any ObjCClassBoundShape");
        AssertEqual(7, shape.Tag, "vended conformer carries its tag");

        using var box = new ObjCShapeBox(shape);
        AssertEqual(7, box.GetStoredTag(), "stored conformer dispatches its witness through the existential");

        var read = box.Stored;
        AssertNotNull(read, "Optional class-bound @objc existential getter returned the stored value");
        AssertEqual(7, read!.Tag, "getter round-tripped the conformer identity");
    }

    /// <summary>
    /// Method param + return position for the Optional class-bound @objc existential,
    /// both nil and non-nil.
    /// </summary>
    public void TestEchoObjCExistentialOptional()
    {
        var none = TestLibFunctions.EchoObjCShape(null);
        AssertNull(none, "echo(nil) round-trips to null");

        var shape = TestLibFunctions.MakeObjCShape(99);
        var echoed = TestLibFunctions.EchoObjCShape(shape);
        AssertNotNull(echoed, "echo(value) returned a non-null existential");
        AssertEqual(99, echoed!.Tag, "echo round-tripped the conformer through param + return");
    }

    /// <summary>
    /// Settable property for the Optional class-bound @objc existential — the exact
    /// `var content: (any P)?` shape. The setter
    /// must marshal a single by-value ObjC object pointer (nil → null), not the 16-byte
    /// `ClassExistentialContainer1` decomposed (container + hasValue) carrier.
    /// </summary>
    public void TestSettableObjCExistentialProperty()
    {
        using var box = new ObjCShapeBox(null);
        AssertNull(box.MutableStored, "settable @objc existential defaults to null");
        AssertEqual(-1, box.GetMutableStoredTag(), "default settable content reports the empty sentinel");

        box.MutableStored = TestLibFunctions.MakeObjCShape(42);
        var read = box.MutableStored;
        AssertNotNull(read, "settable @objc existential round-trips a non-null value");
        AssertEqual(42, read!.Tag, "setter stored the conformer identity");
        AssertEqual(42, box.GetMutableStoredTag(), "stored conformer dispatches its witness through the existential");

        box.MutableStored = null;
        AssertNull(box.MutableStored, "settable @objc existential round-trips back to null");
        AssertEqual(-1, box.GetMutableStoredTag(), "cleared content reports the empty sentinel");
    }

    /// <summary>
    /// A plain managed conformer — a C# class implementing the generated interface with no
    /// Swift-vended backing object — flowing INTO a class-bound @objc existential parameter.
    /// The generator auto-wraps it into an EveryProtocol proxy and passes the proxy's single
    /// bare ObjC object pointer; Swift reconstructs `any ObjCClassBoundShape` and dispatches
    /// `.tag` straight back into this implementation. Reverse dispatch through the NON-optional
    /// projection path.
    /// </summary>
    public void TestPlainConformerReverseDispatchNonOptional()
    {
        var shape = new ManagedShape(31);
        AssertEqual(31, TestLibFunctions.ReadObjCShapeTag(shape),
            "Swift dispatched .tag back into the plain C# conformer through the non-optional @objc existential");

        // Second call exercises the per-(impl, protocol) proxy cache reuse.
        AssertEqual(31, TestLibFunctions.ReadObjCShapeTag(shape),
            "repeat dispatch reuses the cached proxy and still reads the conformer's witness");

        // A distinct impl instance must get its own proxy → its own tag.
        AssertEqual(58, TestLibFunctions.ReadObjCShapeTag(new ManagedShape(58)),
            "a second plain conformer dispatches its own tag");
    }

    /// <summary>
    /// The same plain managed conformer flowing INTO the Optional class-bound @objc existential
    /// parameter (nil → -1). Reverse dispatch through the Optional projection path — the shape
    /// the fail-closed guard used to reject.
    /// </summary>
    public void TestPlainConformerReverseDispatchOptional()
    {
        AssertEqual(-1, TestLibFunctions.ReadOptionalObjCShapeTag(null),
            "nil Optional @objc existential reports the empty sentinel");

        var shape = new ManagedShape(64);
        AssertEqual(64, TestLibFunctions.ReadOptionalObjCShapeTag(shape),
            "Swift dispatched .tag back into the plain C# conformer through the Optional @objc existential");
    }

    /// <summary>Plain managed conformer — no Swift-vended backing object.</summary>
    private sealed class ManagedShape : IObjCClassBoundShape
    {
        public ManagedShape(int tag) => Tag = tag;
        public int Tag { get; }
    }
}
