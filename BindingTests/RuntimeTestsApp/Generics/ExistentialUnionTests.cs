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

    // The three try-cast tests below are deferred: the PAT existential `any AttributeKind` currently
    // degrades to `object` (surfaced loudly via SWIFTBIND023), so `holder.Attribute` is NOT projected
    // to ExistentialUnion yet. The real projection lands with Finding 21 in Session 12. Each test now
    // ASSERTS `union is ExistentialUnion` up front — the previous `if (union is ExistentialUnion)`
    // guard let the body be skipped, making the test vacuously green even though projection is absent.
    // With the hard assert + [Skip] the deferral is explicit; removing [Skip] in Session 12 turns the
    // assert red until the projection works, instead of silently passing.

    [Skip("PAT existential 'any AttributeKind' degrades to object (SWIFTBIND023); ExistentialUnion projection deferred to Finding 21 / Session 12.")]
    public void TestExistentialUnion_TryCast_ColorAttribute()
    {
        var holder = new AttributeHolder(color: "blue");
        var union = holder.Attribute;
        AssertNotNull(union, "Attribute should return ExistentialUnion");
        AssertTrue(union is ExistentialUnion, "Attribute should project to ExistentialUnion, not degrade to object");
        if (union is ExistentialUnion eu)
        {
            var color = eu.As<ColorAttribute>();
            AssertNotNull(color, "TryCast to ColorAttribute should succeed");
        }
    }

    [Skip("PAT existential 'any AttributeKind' degrades to object (SWIFTBIND023); ExistentialUnion projection deferred to Finding 21 / Session 12.")]
    public void TestExistentialUnion_TryCast_SizeAttribute()
    {
        var holder = new AttributeHolder(size: 100);
        var union = holder.Attribute;
        AssertNotNull(union, "Attribute should return ExistentialUnion");
        AssertTrue(union is ExistentialUnion, "Attribute should project to ExistentialUnion, not degrade to object");
        if (union is ExistentialUnion eu)
        {
            var size = eu.As<SizeAttribute>();
            AssertNotNull(size, "TryCast to SizeAttribute should succeed");
        }
    }

    [Skip("PAT existential 'any AttributeKind' degrades to object (SWIFTBIND023); ExistentialUnion projection deferred to Finding 21 / Session 12.")]
    public void TestExistentialUnion_TryCast_WrongType_ReturnsNull()
    {
        var holder = new AttributeHolder(color: "green");
        var union = holder.Attribute;
        AssertTrue(union is ExistentialUnion, "Attribute should project to ExistentialUnion, not degrade to object");
        if (union is ExistentialUnion eu)
        {
            var size = eu.As<SizeAttribute>();
            AssertNull(size, "TryCast to wrong type should return null");
        }
    }
}
