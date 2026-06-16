// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Tests for existential union projection — PAT protocols with known conformers
/// are returned as ExistentialUnion with try-cast to each concrete conformer.
/// </summary>
public class ExistentialUnionTests : TestBase
{
    public ExistentialUnionTests(TestResults results) : base(results) { }

    public void TestAttributeHolder_ColorAttribute_Label()
    {
        var holder = new AttributeHolder(color: "red");
        AssertEqual("color", holder.AttributeLabel, "ColorAttribute label");
    }

    public void TestAttributeHolder_SizeAttribute_Label()
    {
        var holder = new AttributeHolder(size: 42);
        AssertEqual("size", holder.AttributeLabel, "SizeAttribute label");
    }

    public void TestAttributeHolder_FlagAttribute_Label()
    {
        var holder = new AttributeHolder(flag: true);
        AssertEqual("flag", holder.AttributeLabel, "FlagAttribute label");
    }

    // Finding 21 / Session 12: the PAT existential `any AttributeKind` has known conformers
    // (ColorAttribute / SizeAttribute / FlagAttribute), so `holder.Attribute` (a get-only property in a
    // pure-read position) projects to Swift.Runtime.ExistentialUnion — the read-only forward try-cast
    // wrapper — instead of degrading to `object` with a SWIFTBIND023 marker. The strongly-typed
    // `ExistentialUnion union = holder.Attribute;` is itself a compile-time projection assertion: if the
    // property ever regressed to `object`, this would fail to convert and the compile gate would go red.

    public void TestExistentialUnion_TryCast_ColorAttribute()
    {
        var holder = new AttributeHolder(color: "blue");
        ExistentialUnion union = holder.Attribute;
        AssertNotNull(union, "Attribute should project to ExistentialUnion");
        var color = union.As<ColorAttribute>();
        AssertNotNull(color, "TryCast to ColorAttribute should succeed");
    }

    public void TestExistentialUnion_TryCast_SizeAttribute()
    {
        var holder = new AttributeHolder(size: 100);
        ExistentialUnion union = holder.Attribute;
        AssertNotNull(union, "Attribute should project to ExistentialUnion");
        var size = union.As<SizeAttribute>();
        AssertNotNull(size, "TryCast to SizeAttribute should succeed");
    }

    public void TestExistentialUnion_TryCast_WrongType_ReturnsNull()
    {
        var holder = new AttributeHolder(color: "green");
        ExistentialUnion union = holder.Attribute;
        var size = union.As<SizeAttribute>();
        AssertNull(size, "TryCast to wrong type should return null");
    }

    // Finding 21 / Session 12 finding #1: the SETTABLE PAT property MutableAttributeHolder.Current
    // keeps BOTH its public type and its backing getter at `object` (ExistentialUnion is return-only,
    // no input marshalling). The strongly-typed `object current = holder.Current;` plus the
    // `holder.Current = current;` round-trip is the runtime hazard the review flagged: if the getter
    // had projected to ExistentialUnion under an `object` property, this round-trip would feed an
    // ExistentialUnion back into the setter's input marshalling. Assert it round-trips without crashing.
    public void TestMutableAttributeHolder_ObjectRoundTrip_DoesNotCrash()
    {
        var holder = new MutableAttributeHolder(color: "blue");
        object current = holder.Current;
        AssertNotNull(current, "Settable PAT property getter should return a non-null object");
        holder.Current = current;   // round-trip back through the setter — must not crash
        object again = holder.Current;
        AssertNotNull(again, "Settable PAT property should still read back after a round-trip set");
    }
}
