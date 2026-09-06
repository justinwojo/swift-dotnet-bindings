// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Covers the parse-time derivation that gives a frozen struct its own inline (size, alignment),
/// which is what lets a containing frozen struct's blitted Buffer reserve a nested field's real
/// width instead of a single pointer. Every case here is judged on the derived numbers, not on
/// emitted text: a wrong width is a silent heap overflow, and declining is the only safe answer
/// when the declaration does not determine the layout.
/// </summary>
public class DeclaredLayoutDerivationTests
{
    [Fact]
    public void DeclaredLayout_TrivialFields_FollowsDeclarationOrderWithPerFieldAlignUp()
    {
        // struct Leaf { var a: Int32; var b: Int64 }
        // Swift never reorders stored fields: `a` sits at 0, `b` aligns up to 8, so the struct is
        // 16 bytes with 8-byte alignment. Accumulating strides instead of sizes would be wrong the
        // moment a trailing pad exists, so the arithmetic is asserted directly.
        var record = Derive(
            CreateStructDecl("Leaf", Spec("Leaf"),
                ("a", Int32Spec), ("b", Int64Spec)),
            "TestModule.Leaf");

        Assert.False(record.DeclaredLayoutIndeterminate);
        Assert.Equal(new DeclaredValueLayout(16, 8), record.DeclaredLayout);
    }

    [Fact]
    public void DeclaredLayout_TrailingSmallField_KeepsSizeBelowStride()
    {
        // struct Leaf { var a: Int64; var b: Int32 } — size 12, stride 16. The Buffer mirror needs
        // the SIZE; rounding to the stride here would over-state a nested field and shift whatever
        // follows it in the container.
        var record = Derive(
            CreateStructDecl("Leaf", Spec("Leaf"),
                ("a", Int64Spec), ("b", Int32Spec)),
            "TestModule.Leaf");

        Assert.Equal(new DeclaredValueLayout(12, 8), record.DeclaredLayout);
    }

    [Fact]
    public void DeclaredLayout_NestedStruct_ReservesTheNestedTypesFullWidth()
    {
        // struct Leaf { var a: Int32; var b: Int64 }   // 16 bytes
        // struct Host { var leaf: Leaf; var tail: Int32 }
        // This is the defect the whole lane exists for: sizing `leaf` as one pointer would make the
        // host 12 bytes instead of 20 and put `tail` on top of the leaf's second word.
        var leaf = CreateStructDecl("Leaf", Spec("Leaf"), ("a", Int32Spec), ("b", Int64Spec));
        var host = CreateStructDecl("Host", Spec("Host"), ("leaf", Spec("Leaf")), ("tail", Int32Spec));

        var records = DeriveAll(("TestModule.Leaf", leaf), ("TestModule.Host", host));

        Assert.Equal(new DeclaredValueLayout(16, 8), records["TestModule.Leaf"].DeclaredLayout);
        Assert.Equal(new DeclaredValueLayout(20, 8), records["TestModule.Host"].DeclaredLayout);
    }

    [Fact]
    public void DeclaredLayout_NestedStructDeclaredAfterItsContainer_StillDerives()
    {
        // Same shapes, container declared first. The walk that processes a struct's stored field
        // types before registering it is what makes the derivation order-independent; without it a
        // host would be indeterminate purely because of where the leaf appears in the module.
        var host = CreateStructDecl("Host", Spec("Host"), ("leaf", Spec("Leaf")), ("tail", Int32Spec));
        var leaf = CreateStructDecl("Leaf", Spec("Leaf"), ("a", Int32Spec), ("b", Int64Spec));

        var records = DeriveAll(("TestModule.Host", host), ("TestModule.Leaf", leaf));

        Assert.Equal(new DeclaredValueLayout(20, 8), records["TestModule.Host"].DeclaredLayout);
    }

    [Fact]
    public void DeclaredLayout_OptionalReferenceBearingPayloadDeclaredAfterItsContainer_StillDerives()
    {
        // `Leaf?` is spelled `Swift.Optional<Leaf>`, so the field's outer name belongs to the stdlib
        // and the same-module payload is only reached by unwrapping it. A container declared before
        // its optional payload must still derive: the alternative is an indeterminate layout that
        // depends on declaration order rather than on anything about the types.
        var host = CreateStructDecl("Host", Spec("Host"),
            ("maybeLeaf", new NamedTypeSpec("Swift.Optional", Spec("RefLeaf"))),
            ("tail", Int32Spec));
        var leaf = CreateStructDecl("RefLeaf", Spec("RefLeaf"), ("box", Spec("Box")));

        var records = DeriveAll(
            ("TestModule.Host", host),
            ("TestModule.RefLeaf", leaf),
            ("TestModule.Box", CreateClassDecl("Box")));

        // The leaf is one class reference wide, and Optional folds `.none` into that pointer's spare
        // inhabitants, so the field stays 8 bytes and the host ends at 12.
        Assert.Equal(new DeclaredValueLayout(8, 8), records["TestModule.RefLeaf"].DeclaredLayout);
        Assert.Equal(new DeclaredValueLayout(12, 8), records["TestModule.Host"].DeclaredLayout);
    }

    [Fact]
    public void DeclaredLayout_CustomAlignmentAttribute_IsIndeterminate()
    {
        // `@_alignment(N)` raises a struct's alignment above what its fields require. The ABI
        // descriptor records only that the attribute is present, never N, so a field walk would
        // under-state the width. Declining is the only sound answer.
        var leaf = CreateStructDecl("AlignedLeaf", Spec("AlignedLeaf"), ("a", Int64Spec));
        leaf.HasCustomAlignment = true;

        var record = Derive(leaf, "TestModule.AlignedLeaf");

        Assert.Null(record.DeclaredLayout);
        Assert.True(record.DeclaredLayoutIndeterminate);
    }

    [Fact]
    public void DeclaredLayout_ContainerOfACustomAlignedStruct_IsAlsoIndeterminate()
    {
        // Indeterminacy has to propagate: a host that embeds a struct of unknown alignment knows
        // neither the padding before that field nor its own total width.
        var leaf = CreateStructDecl("AlignedLeaf", Spec("AlignedLeaf"), ("a", Int64Spec));
        leaf.HasCustomAlignment = true;
        var host = CreateStructDecl("Host", Spec("Host"), ("leaf", Spec("AlignedLeaf")), ("tail", Int32Spec));

        var records = DeriveAll(("TestModule.AlignedLeaf", leaf), ("TestModule.Host", host));

        Assert.Null(records["TestModule.Host"].DeclaredLayout);
        Assert.True(records["TestModule.Host"].DeclaredLayoutIndeterminate);
    }

    [Fact]
    public void UnknownCustomAlignment_IsRecordedAndTravelsToContainers()
    {
        // The unknown alignment is tracked apart from the size lanes because it also invalidates a
        // size that IS known — a measured inlineSize or live value-witness size still cannot be
        // placed at an offset the Buffer's pointer words can't express. Swift takes a struct's
        // alignment as the maximum of its fields', so the flag has to travel outward: a container of
        // an over-aligned type is itself over-aligned, whatever its own attributes say.
        var leaf = CreateStructDecl("AlignedLeaf", Spec("AlignedLeaf"), ("a", Int64Spec));
        leaf.HasCustomAlignment = true;
        var host = CreateStructDecl("Host", Spec("Host"), ("leaf", Spec("AlignedLeaf")), ("tail", Int32Spec));
        var plain = CreateStructDecl("Plain", Spec("Plain"), ("a", Int64Spec));

        var records = DeriveAll(
            ("TestModule.AlignedLeaf", leaf), ("TestModule.Host", host), ("TestModule.Plain", plain));

        Assert.True(records["TestModule.AlignedLeaf"].HasUnknownCustomAlignment);
        Assert.True(records["TestModule.Host"].HasUnknownCustomAlignment);
        Assert.False(records["TestModule.Plain"].HasUnknownCustomAlignment);
    }

    [Fact]
    public void UnknownCustomAlignment_TravelsThroughAnOptionalField()
    {
        // `Optional<T>` stores T inline, so an over-aligned payload raises the container's alignment
        // exactly as a bare field of that type would.
        var leaf = CreateStructDecl("AlignedLeaf", Spec("AlignedLeaf"), ("a", Int64Spec));
        leaf.HasCustomAlignment = true;
        var host = CreateStructDecl("Host", Spec("Host"),
            ("leaf", new NamedTypeSpec("Swift.Optional", Spec("AlignedLeaf"))), ("tail", Int32Spec));

        var records = DeriveAll(("TestModule.AlignedLeaf", leaf), ("TestModule.Host", host));

        Assert.True(records["TestModule.Host"].HasUnknownCustomAlignment);
    }

    [Fact]
    public void UnknownCustomAlignment_TravelsThroughNestedOptionalLayers()
    {
        // `T??` is still one inline payload, so stopping the walk at the first Optional layer would
        // leave the container unflagged while its alignment is genuinely raised.
        var leaf = CreateStructDecl("AlignedLeaf", Spec("AlignedLeaf"), ("a", Int64Spec));
        leaf.HasCustomAlignment = true;
        var host = CreateStructDecl("Host", Spec("Host"),
            ("leaf", new NamedTypeSpec("Swift.Optional",
                new NamedTypeSpec("Swift.Optional", Spec("AlignedLeaf")))), ("tail", Int32Spec));

        var records = DeriveAll(("TestModule.AlignedLeaf", leaf), ("TestModule.Host", host));

        Assert.True(records["TestModule.Host"].HasUnknownCustomAlignment);
    }

    [Fact]
    public void UnknownCustomAlignment_TravelsThroughATupleField()
    {
        // A tuple stores its elements inline and takes the maximum of their alignments, so an
        // over-aligned element raises the container just as a bare field of that type would.
        var leaf = CreateStructDecl("AlignedLeaf", Spec("AlignedLeaf"), ("a", Int64Spec));
        leaf.HasCustomAlignment = true;
        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(Spec("AlignedLeaf"));
        tuple.Elements.Add(Int32Spec);
        var host = CreateStructDecl("Host", Spec("Host"), ("pair", tuple), ("tail", Int32Spec));

        var records = DeriveAll(("TestModule.AlignedLeaf", leaf), ("TestModule.Host", host));

        Assert.True(records["TestModule.Host"].HasUnknownCustomAlignment);
    }

    [Fact]
    public void UnknownCustomAlignment_PlainTupleField_LeavesTheContainerUnflagged()
    {
        // Positive control for the tuple walk: an ordinary tuple must not be read as over-aligned,
        // or every struct storing one loses its Buffer projection.
        var leaf = CreateStructDecl("PlainLeaf", Spec("PlainLeaf"), ("a", Int64Spec));
        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(Spec("PlainLeaf"));
        tuple.Elements.Add(Int32Spec);
        var host = CreateStructDecl("Host", Spec("Host"), ("pair", tuple), ("tail", Int32Spec));

        var records = DeriveAll(("TestModule.PlainLeaf", leaf), ("TestModule.Host", host));

        Assert.False(records["TestModule.Host"].HasUnknownCustomAlignment);
    }

    [Fact]
    public void UnknownCustomAlignment_TravelsThroughATupleField_WhenTheContainerIsDeclaredFirst()
    {
        // Declaration order must not decide soundness. The flag is read off the leaf's record, so a
        // container declared BEFORE its leaf sees no record at all unless the property walk registers
        // every inline-stored type up front — and a tuple property is exactly the shape the walk used
        // to drop whole.
        var leaf = CreateStructDecl("AlignedLeaf", Spec("AlignedLeaf"), ("a", Int64Spec));
        leaf.HasCustomAlignment = true;
        var tuple = new TupleTypeSpec();
        tuple.Elements.Add(Spec("AlignedLeaf"));
        tuple.Elements.Add(Int32Spec);
        var host = CreateStructDecl("Host", Spec("Host"), ("pair", tuple), ("tail", Int32Spec));

        var records = DeriveAll(("TestModule.Host", host), ("TestModule.AlignedLeaf", leaf));

        Assert.True(records["TestModule.Host"].HasUnknownCustomAlignment);
    }

    [Fact]
    public void UnknownCustomAlignment_TravelsThroughNestedOptionalLayers_WhenTheContainerIsDeclaredFirst()
    {
        // Same order independence for the Optional chain: `Leaf??` hides the leaf two generic layers
        // deep behind `Swift.Optional`, so only a recursive pre-walk reaches it.
        var leaf = CreateStructDecl("AlignedLeaf", Spec("AlignedLeaf"), ("a", Int64Spec));
        leaf.HasCustomAlignment = true;
        var host = CreateStructDecl("Host", Spec("Host"),
            ("leaf", new NamedTypeSpec("Swift.Optional",
                new NamedTypeSpec("Swift.Optional", Spec("AlignedLeaf")))), ("tail", Int32Spec));

        var records = DeriveAll(("TestModule.Host", host), ("TestModule.AlignedLeaf", leaf));

        Assert.True(records["TestModule.Host"].HasUnknownCustomAlignment);
    }

    [Fact]
    public void UnknownCustomAlignment_TravelsFromAnEnumPayloadToTheEnum()
    {
        // An enum stores its associated values inline and takes their alignment, but the ABI
        // descriptor spells `@_alignment(N)` out only on the type that declares it — the wrapping
        // enum looks unannotated. Reading only the enum's own attribute leaves a 16-aligned enum
        // claiming pointer alignment, and a measured size then places it at the wrong offset.
        var leaf = CreateStructDecl("AlignedLeaf", Spec("AlignedLeaf"), ("a", Int64Spec));
        leaf.HasCustomAlignment = true;
        var payloadEnum = CreateEnumDecl("Wrapper", rawValueTypeName: null, caseNames: ["a"]);
        payloadEnum.Cases[0].AssociatedValues.Add(Spec("AlignedLeaf"));

        var records = DeriveAll(("TestModule.Wrapper", payloadEnum), ("TestModule.AlignedLeaf", leaf));

        Assert.True(records["TestModule.Wrapper"].HasUnknownCustomAlignment);
    }

    [Fact]
    public void UnknownCustomAlignment_TravelsThroughAnEnumIntoItsContainer_WhateverTheDeclarationOrder()
    {
        // Two hops — leaf → enum → struct — with every type declared before the type it depends on,
        // so the flag survives only if each registration pre-walks what it stores inline.
        var leaf = CreateStructDecl("AlignedLeaf", Spec("AlignedLeaf"), ("a", Int64Spec));
        leaf.HasCustomAlignment = true;
        var payloadEnum = CreateEnumDecl("Wrapper", rawValueTypeName: null, caseNames: ["a"]);
        payloadEnum.Cases[0].AssociatedValues.Add(Spec("AlignedLeaf"));
        var host = CreateStructDecl("Host", Spec("Host"), ("wrapped", Spec("Wrapper")), ("tail", Int32Spec));

        var records = DeriveAll(
            ("TestModule.Host", host),
            ("TestModule.Wrapper", payloadEnum),
            ("TestModule.AlignedLeaf", leaf));

        Assert.True(records["TestModule.Host"].HasUnknownCustomAlignment);
    }

    [Fact]
    public void UnknownCustomAlignment_TravelsAroundARecursiveEnumCycle_WhateverTheDeclarationOrder()
    {
        // Two enums that store each other (legal in Swift once a case is indirect) have no order in
        // which both are registered before the other needs to read it, so an answer taken from the
        // type database leaves whichever was visited first unpoisoned for good — and a record that
        // later gains a measured size would then be trusted at the wrong alignment. Both reach the
        // over-aligned leaf, so both carry the flag either way round.
        var leaf = CreateStructDecl("AlignedLeaf", Spec("AlignedLeaf"), ("a", Int64Spec));
        leaf.HasCustomAlignment = true;
        var a = CreateEnumDecl("A", rawValueTypeName: null, caseNames: ["recurse", "leaf"]);
        a.Cases[0].AssociatedValues.Add(Spec("B"));
        a.Cases[1].AssociatedValues.Add(Spec("AlignedLeaf"));
        var b = CreateEnumDecl("B", rawValueTypeName: null, caseNames: ["a"]);
        b.Cases[0].AssociatedValues.Add(Spec("A"));

        var aFirst = DeriveAll(
            ("TestModule.A", a), ("TestModule.B", b), ("TestModule.AlignedLeaf", leaf));
        var bFirst = DeriveAll(
            ("TestModule.B", b), ("TestModule.A", a), ("TestModule.AlignedLeaf", leaf));

        Assert.True(aFirst["TestModule.A"].HasUnknownCustomAlignment);
        Assert.True(aFirst["TestModule.B"].HasUnknownCustomAlignment);
        Assert.True(bFirst["TestModule.A"].HasUnknownCustomAlignment);
        Assert.True(bFirst["TestModule.B"].HasUnknownCustomAlignment);
    }

    [Fact]
    public void UnknownCustomAlignment_PlainRecursiveEnumCycle_LeavesBothUnflagged()
    {
        // Scoping control for the cycle walk: cutting a cycle must not manufacture poison either.
        // Neither enum stores anything over-aligned, so neither is flagged whatever the order.
        var a = CreateEnumDecl("A", rawValueTypeName: null, caseNames: ["recurse", "leaf"]);
        a.Cases[0].AssociatedValues.Add(Spec("B"));
        a.Cases[1].AssociatedValues.Add(Int64Spec);
        var b = CreateEnumDecl("B", rawValueTypeName: null, caseNames: ["a"]);
        b.Cases[0].AssociatedValues.Add(Spec("A"));

        var records = DeriveAll(("TestModule.A", a), ("TestModule.B", b));

        Assert.False(records["TestModule.A"].HasUnknownCustomAlignment);
        Assert.False(records["TestModule.B"].HasUnknownCustomAlignment);
    }

    [Fact]
    public void UnknownCustomAlignment_TravelsThroughAGenericArgument()
    {
        // `Box<AlignedLeaf>` stores the leaf inline, but Box's declaration stores only its parameter
        // `T` — a walk that reads the declaration alone answers from the unbound parameter and never
        // reaches the leaf, so the specialization looks ordinarily aligned while Swift aligns it to
        // the leaf's width.
        var leaf = CreateStructDecl("AlignedLeaf", Spec("AlignedLeaf"), ("a", Int64Spec));
        leaf.HasCustomAlignment = true;
        var box = CreateStructDecl("Box", Spec("Box"), ("value", new NamedTypeSpec("T")));
        var host = CreateStructDecl("Host", Spec("Host"),
            ("boxed", new NamedTypeSpec("TestModule.Box", Spec("AlignedLeaf"))), ("tail", Int32Spec));

        var records = DeriveAll(
            ("TestModule.AlignedLeaf", leaf), ("TestModule.Box", box), ("TestModule.Host", host));

        Assert.True(records["TestModule.Host"].HasUnknownCustomAlignment);
    }

    [Fact]
    public void UnknownCustomAlignment_PlainGenericArgument_LeavesTheContainerUnflagged()
    {
        // Scoping control for the generic-argument walk: reading a specialization's arguments must
        // not flag every generic field, or a struct storing any generic at all loses its Buffer.
        var leaf = CreateStructDecl("PlainLeaf", Spec("PlainLeaf"), ("a", Int64Spec));
        var box = CreateStructDecl("Box", Spec("Box"), ("value", new NamedTypeSpec("T")));
        var host = CreateStructDecl("Host", Spec("Host"),
            ("boxed", new NamedTypeSpec("TestModule.Box", Spec("PlainLeaf"))), ("tail", Int32Spec));

        var records = DeriveAll(
            ("TestModule.PlainLeaf", leaf), ("TestModule.Box", box), ("TestModule.Host", host));

        Assert.False(records["TestModule.Host"].HasUnknownCustomAlignment);
    }

    [Fact]
    public void UnknownCustomAlignment_TravelsThroughAGenericArgumentOnANestedSegment()
    {
        // Generic arguments hang off each segment of a nested name, so `Outer<PlainLeaf>.Inner<AlignedLeaf>`
        // carries one argument on the outer segment and the over-aligned one on the inner. Reading only
        // the outermost segment's arguments finds the plain leaf and stops.
        var leaf = CreateStructDecl("AlignedLeaf", Spec("AlignedLeaf"), ("a", Int64Spec));
        leaf.HasCustomAlignment = true;
        var plain = CreateStructDecl("PlainLeaf", Spec("PlainLeaf"), ("a", Int64Spec));
        var inner = CreateStructDecl("Inner", new NamedTypeSpec("TestModule.Outer.Inner"), ("a", Int64Spec));
        var nested = new NamedTypeSpec("TestModule.Outer", Spec("PlainLeaf"))
        {
            InnerType = new NamedTypeSpec("Inner", Spec("AlignedLeaf")),
        };
        var host = CreateStructDecl("Host", Spec("Host"), ("nested", nested), ("tail", Int32Spec));

        var records = DeriveAll(
            ("TestModule.AlignedLeaf", leaf),
            ("TestModule.PlainLeaf", plain),
            ("TestModule.Outer.Inner", inner),
            ("TestModule.Host", host));

        Assert.True(records["TestModule.Host"].HasUnknownCustomAlignment);
    }

    [Fact]
    public void FrozenFlag_OfAStructStoringABoundGeneric_DoesNotDependOnDeclarationOrder()
    {
        // A stored field spelled `Phantom<Int64>` is looked up among the declarations, which are keyed
        // by one flat name with no generic arguments. Searching for the specialization itself finds
        // nothing, the payload is never registered ahead of its container, and an unresolved field type
        // clears the container's frozen flag — so the same Swift struct comes out frozen or not
        // depending on which type the module listed first.
        var phantom = CreateStructDecl("Phantom", Spec("Phantom"), ("value", Int64Spec));
        var host = CreateStructDecl("Host", Spec("Host"),
            ("boxed", new NamedTypeSpec("TestModule.Phantom", Int64Spec)), ("tail", Int32Spec));

        var hostFirst = DeriveAll(("TestModule.Host", host), ("TestModule.Phantom", phantom));
        var phantomFirst = DeriveAll(("TestModule.Phantom", phantom), ("TestModule.Host", host));

        Assert.Equal(
            phantomFirst["TestModule.Host"].Flags & TypeRecordFlags.Frozen,
            hostFirst["TestModule.Host"].Flags & TypeRecordFlags.Frozen);
        Assert.True((hostFirst["TestModule.Host"].Flags & TypeRecordFlags.Frozen) != 0);
    }

    [Fact]
    public void FrozenFlag_OfAStructStoringATypeDeclaredInAForeignExtension_DoesNotDependOnDeclarationOrder()
    {
        // A type this module writes inside `extension ForeignType { struct Nested { … } }` is spelled
        // under the foreign module but is declared, and emitted, here — and its record is mirrored
        // into the extended module's database so a field typed by it resolves. Comparing the field's
        // module against this one refuses to pre-register it, so a container declared before it
        // resolves nothing for that field and loses its frozen flag.
        var outerStub = CreateStructDecl("Outer", new NamedTypeSpec("Foreign.Outer"));
        var nested = CreateStructDecl("Nested", new NamedTypeSpec("Foreign.Outer.Nested"), ("value", Int64Spec));
        nested.ParentDecl = outerStub;
        nested.ModuleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        var host = CreateStructDecl("Host", Spec("Host"),
            ("nested", new NamedTypeSpec("Foreign.Outer.Nested")), ("tail", Int32Spec));

        var hostFirst = DeriveOne(
            "TestModule.Host", "Foreign", ("TestModule.Host", host), ("Foreign.Outer.Nested", nested));
        var nestedFirst = DeriveOne(
            "TestModule.Host", "Foreign", ("Foreign.Outer.Nested", nested), ("TestModule.Host", host));

        Assert.Equal(
            nestedFirst.Flags & TypeRecordFlags.Frozen,
            hostFirst.Flags & TypeRecordFlags.Frozen);
        Assert.True((hostFirst.Flags & TypeRecordFlags.Frozen) != 0);
    }

    [Fact]
    public void UnknownCustomAlignment_OfANestedType_IsNotReadFromItsEnclosingType()
    {
        // A nested type reaches the walk as an outer name carrying an inner chain (`Outer` + `.Inner`)
        // while the declarations are keyed by one flat name, so a key built from the outer name alone
        // does not miss — it HITS the enclosing type and answers from its fields. The container is
        // declared first so nothing but the declaration lookup can supply the answer.
        var outer = CreateStructDecl("Outer", Spec("Outer"), ("a", Int64Spec));
        var inner = CreateStructDecl("Inner", new NamedTypeSpec("TestModule.Outer.Inner"), ("a", Int64Spec));
        inner.HasCustomAlignment = true;
        var nested = new NamedTypeSpec("TestModule.Outer") { InnerType = new NamedTypeSpec("Inner") };
        var host = CreateStructDecl("Host", Spec("Host"), ("nested", nested), ("tail", Int32Spec));

        var records = DeriveAll(
            ("TestModule.Host", host),
            ("TestModule.Outer", outer),
            ("TestModule.Outer.Inner", inner));

        Assert.True(records["TestModule.Host"].HasUnknownCustomAlignment);
    }

    [Fact]
    public void UnknownCustomAlignment_OfAForeignTypeRetainedForItsExtensions_ComesFromItsOwnRecord()
    {
        // A foreign type is retained in this module's declarations when it hosts extension members
        // contributed from here, and that declaration carries only those members — its stored
        // properties belong to the module that owns it. Answering from the retained declaration alone
        // reads an empty field list as "not over-aligned" and overrides the owning module's record,
        // which already describes the type completely.
        var foreignStub = CreateStructDecl("Stub", new NamedTypeSpec("Foreign.Stub"));
        var host = CreateStructDecl("Host", Spec("Host"),
            ("foreign", new NamedTypeSpec("Foreign.Stub")), ("tail", Int32Spec));

        var record = DeriveWithForeignRecord(
            foreignName: "Foreign.Stub",
            foreignDecl: foreignStub,
            queried: ("TestModule.Host", host));

        Assert.True(record.HasUnknownCustomAlignment);
    }

    [Fact]
    public void FrozenFlag_OfAStructInsideAnEnumCycle_DoesNotDependOnDeclarationOrder()
    {
        // The struct is `@frozen` and stores an enum that stores it back. Registering the struct from
        // inside the enum's own processing would hand it a database in which the enum does not exist
        // yet, and an unresolved field type clears the frozen flag — which is not a skip but a
        // different projection for the same Swift type, chosen by where the module happened to list
        // it. The flag has to come out the same either way round.
        var payload = CreateStructDecl("Node", Spec("Node"), ("child", Spec("Tree")), ("extra", Int64Spec));
        var tree = CreateEnumDecl("Tree", rawValueTypeName: null, caseNames: ["node"]);
        tree.Cases[0].AssociatedValues.Add(Spec("Node"));

        var enumFirst = DeriveAll(("TestModule.Tree", tree), ("TestModule.Node", payload));
        var structFirst = DeriveAll(("TestModule.Node", payload), ("TestModule.Tree", tree));

        Assert.Equal(
            structFirst["TestModule.Node"].Flags & TypeRecordFlags.Frozen,
            enumFirst["TestModule.Node"].Flags & TypeRecordFlags.Frozen);
        Assert.True((enumFirst["TestModule.Node"].Flags & TypeRecordFlags.Frozen) != 0);
    }

    [Fact]
    public void DeclaredLayout_ComputedAndStaticProperties_DoNotOccupyLayout()
    {
        // Only stored instance properties sit in the value. A computed property has no storage and
        // a static stored one lives in type metadata; counting either inflates every offset after it.
        var leaf = CreateStructDecl("Leaf", Spec("Leaf"), ("stored", Int32Spec));
        leaf.Properties.Add(CreateProperty("computed", Int64Spec, hasStorage: false));
        leaf.Properties.Add(CreateProperty("shared", Int64Spec, isStatic: true));

        var record = Derive(leaf, "TestModule.Leaf");

        Assert.Equal(new DeclaredValueLayout(4, 4), record.DeclaredLayout);
    }

    [Fact]
    public void DeclaredLayout_NonFrozenStruct_IsIndeterminate()
    {
        // A resilient struct's layout is not knowable from its declaration, and the parser had that
        // declaration in hand — so the record says so. The unmarked state is reserved for records
        // nothing ever attempted, whose single-pointer clamp would here be a guess at a type of any
        // width.
        var leaf = CreateStructDecl("Resilient", Spec("Resilient"), ("a", Int64Spec));
        leaf.IsFrozen = false;

        var record = Derive(leaf, "TestModule.Resilient");

        Assert.Null(record.DeclaredLayout);
        Assert.True(record.DeclaredLayoutIndeterminate);
    }

    [Fact]
    public void DeclaredLayout_OptionalOverANonFrozenStruct_FailsTheContainerClosed()
    {
        // The Optional is the wrapper a container cannot see past: it stores `Swift.Optional`, not the
        // opaque struct, so the container keeps its own frozen flag and its Buffer walk asks for the
        // payload's width. There is no such width, so the container declines instead of reserving one
        // pointer for it.
        var opaque = CreateStructDecl("Opaque", Spec("Opaque"), ("a", Int64Spec), ("b", Int64Spec));
        opaque.IsFrozen = false;
        var host = CreateStructDecl("Host", Spec("Host"), ("payload", new NamedTypeSpec("Swift.Optional", Spec("Opaque"))));

        var records = DeriveAll(("TestModule.Opaque", opaque), ("TestModule.Host", host));

        Assert.True(records["TestModule.Opaque"].DeclaredLayoutIndeterminate);
        Assert.Null(records["TestModule.Host"].DeclaredLayout);
        Assert.True(records["TestModule.Host"].DeclaredLayoutIndeterminate);
    }

    [Fact]
    public void DeclaredLayout_FrozenStructDemotedByAnUnresolvedField_IsIndeterminate()
    {
        // A struct declared `@frozen` whose stored field's type is not in the database loses the flag,
        // which stops the derivation — but the declaration was still in hand and its width is still
        // unknown, so the record has to carry the fail-closed marker rather than the clamp a record
        // nothing ever attempted gets.
        var host = CreateStructDecl("Host", Spec("Host"), ("child", Spec("NotRegistered")), ("tail", Int64Spec));

        var record = Derive(host, "TestModule.Host");

        Assert.Null(record.DeclaredLayout);
        Assert.True(record.DeclaredLayoutIndeterminate);
    }

    [Fact]
    public void DeclaredLayout_EnumFirstPayloadCycle_LeavesThePayloadStructFailClosed()
    {
        // An enum whose payload struct stores the enum back (legal in Swift when the enum is
        // indirect) is a cycle. The struct's width is not derivable either way round — an enum field
        // has no declaration-derivable size — so it must come out fail-closed, not clamp-able, in
        // both orders, and so must a container that reaches it through an Optional.
        var payload = CreateStructDecl("Node", Spec("Node"), ("child", Spec("Tree")), ("extra", Int64Spec));
        var tree = CreateEnumDecl("Tree", rawValueTypeName: null, caseNames: ["node"]);
        tree.Cases[0].AssociatedValues.Add(Spec("Node"));
        var host = CreateStructDecl("Host", Spec("Host"), ("node", new NamedTypeSpec("Swift.Optional", Spec("Node"))));

        var enumFirst = DeriveAll(
            ("TestModule.Tree", tree),
            ("TestModule.Node", payload),
            ("TestModule.Host", host));
        var payloadFirst = DeriveAll(
            ("TestModule.Node", payload),
            ("TestModule.Tree", tree),
            ("TestModule.Host", host));

        Assert.True(enumFirst["TestModule.Node"].DeclaredLayoutIndeterminate);
        Assert.True(enumFirst["TestModule.Host"].DeclaredLayoutIndeterminate);
        Assert.True(payloadFirst["TestModule.Node"].DeclaredLayoutIndeterminate);
        Assert.True(payloadFirst["TestModule.Host"].DeclaredLayoutIndeterminate);
    }

    [Fact]
    public void DeclaredLayout_SimpleEnumStoredField_IsIndeterminateByDesign()
    {
        // A no-payload enum is NOT as wide as its raw-value type: `enum E: Int32 { case a, b }`
        // needs one discriminator byte, not four, and Swift is free to widen it as cases are added.
        // Nothing in the declaration determines that width, so the containing struct declines.
        // The cost is an over-skip — a parent Buffer that would embed this container is not
        // projected — which is the direction this lane is required to fail in.
        var payload = CreateEnumDecl("Flag", rawValueTypeName: "Int32", caseNames: ["a", "b"]);
        var host = CreateStructDecl("Host", Spec("Host"), ("flag", Spec("Flag")), ("tail", Int32Spec));

        var records = DeriveAll(("TestModule.Flag", payload), ("TestModule.Host", host));

        // The enum itself never attempts a derivation — it emits as a typed C# enum field rather
        // than through a Buffer — so it is neither derived nor a fail-closed signal of its own.
        Assert.Null(records["TestModule.Flag"].DeclaredLayout);
        Assert.False(records["TestModule.Flag"].DeclaredLayoutIndeterminate);

        Assert.Null(records["TestModule.Host"].DeclaredLayout);
        Assert.True(records["TestModule.Host"].DeclaredLayoutIndeterminate);
    }

    [Fact]
    public void DeclaredLayout_TupleTypedField_IsIndeterminate()
    {
        // A tuple's width is not resolved by this walk. Guessing one pointer is exactly the failure
        // mode being fixed, so the container declines instead.
        var leaf = CreateStructDecl("Leaf", Spec("Leaf"));
        leaf.Properties.Add(new PropertyDecl
        {
            Name = "pair",
            SwiftTypeSpec = new TupleTypeSpec(new[] { Int32Spec, Int32Spec }),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        });

        var record = Derive(leaf, "TestModule.Leaf");

        Assert.Null(record.DeclaredLayout);
        Assert.True(record.DeclaredLayoutIndeterminate);
    }

    // -- helpers ------------------------------------------------------------------------------

    private static NamedTypeSpec Int32Spec => new("Swift.Int32");

    private static NamedTypeSpec Int64Spec => new("Swift.Int64");

    private static NamedTypeSpec Spec(string name) => new($"TestModule.{name}");

    private static TypeRecord Derive(TypeDecl decl, string moduleQualifiedName)
        => DeriveAll((moduleQualifiedName, decl))[moduleQualifiedName];

    /// <summary>
    /// Runs the real ModuleProcessor over the given declarations (in the order supplied, which the
    /// order-dependence tests rely on) and returns every registered record by module-qualified name.
    /// </summary>
    private static Dictionary<string, TypeRecord> DeriveAll(params (string name, TypeDecl decl)[] decls)
    {
        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>();
        foreach (var (name, decl) in decls)
            typeDecls[new NamedTypeSpec(name)] = decl;

        var typeDatabase = new TypeDatabase();
        SeedPrimitives(typeDatabase);

        var processor = new ModuleProcessor(
            "TestModule",
            "/tmp/TestModule.dylib",
            "TestModule",
            typeDecls,
            typeDatabase,
            NullLogger.Instance);

        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();
        typeDatabase.AddModuleDatabase(result.ModuleDatabase);

        var records = new Dictionary<string, TypeRecord>();
        foreach (var (name, _) in decls)
        {
            Assert.True(
                typeDatabase.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName(name), out var record),
                $"Expected a registered type record for '{name}'.");
            records[name] = record!;
        }

        return records;
    }

    /// <summary>
    /// Runs the real ModuleProcessor over the given declarations and returns just one type's record,
    /// read straight out of the module database it produced. Unlike <see cref="DeriveAll"/> this makes
    /// no claim about the other declarations, so it can host a type whose Swift name puts it in another
    /// module — the shape a type declared inside an extension of a foreign type takes. Naming that
    /// module in <paramref name="extendedForeignModule"/> gives it a database, which is what the
    /// processor mirrors a locally-declared nested type into so a field typed by it resolves.
    /// </summary>
    private static TypeRecord DeriveOne(
        string queriedName, string? extendedForeignModule, params (string name, TypeDecl decl)[] decls)
    {
        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>();
        foreach (var (name, decl) in decls)
            typeDecls[new NamedTypeSpec(name)] = decl;

        var typeDatabase = new TypeDatabase();
        SeedPrimitives(typeDatabase);

        if (extendedForeignModule is not null)
            typeDatabase.AddModuleDatabase(
                new ModuleTypeDatabase(extendedForeignModule, $"/fake/{extendedForeignModule}.dylib"));

        var processor = new ModuleProcessor(
            "TestModule",
            "/tmp/TestModule.dylib",
            "TestModule",
            typeDecls,
            typeDatabase,
            NullLogger.Instance);

        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();

        Assert.True(
            result.ModuleDatabase.TryGetTypeRecord(
                SwiftTypeName.FromModuleQualifiedName(queriedName), out var record),
            $"Expected a registered type record for '{queriedName}'.");
        return record!;
    }

    /// <summary>
    /// Runs the processor over <paramref name="queried"/> with a foreign type present BOTH as a
    /// declaration retained in this module (the shape a foreign receiver hosting local extension
    /// members takes — members only, no stored properties) and as a complete record owned by its own
    /// module. Returns the queried type's record.
    /// </summary>
    private static TypeRecord DeriveWithForeignRecord(
        string foreignName, TypeDecl foreignDecl, (string name, TypeDecl decl) queried)
    {
        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            [new NamedTypeSpec(foreignName)] = foreignDecl,
            [new NamedTypeSpec(queried.name)] = queried.decl,
        };

        var typeDatabase = new TypeDatabase();
        SeedPrimitives(typeDatabase);

        var foreignTypeName = SwiftTypeName.FromModuleQualifiedName(foreignName);
        typeDatabase.AddOutOfModuleTypes(
        [
            (foreignTypeName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foreign", foreignTypeName.Name),
                SwiftTypeName = foreignTypeName,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                HasUnknownCustomAlignment = true,
            }),
        ]);

        var processor = new ModuleProcessor(
            "TestModule",
            "/tmp/TestModule.dylib",
            "TestModule",
            typeDecls,
            typeDatabase,
            NullLogger.Instance);

        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();

        Assert.True(
            result.ModuleDatabase.TryGetTypeRecord(
                SwiftTypeName.FromModuleQualifiedName(queried.name), out var record),
            $"Expected a registered type record for '{queried.name}'.");
        return record!;
    }

    /// <summary>
    /// The processor consults the type database for every stored field's record when it decides
    /// frozen-ness, so the stdlib types the fixtures use have to resolve — a field whose record is
    /// missing is conservatively treated as non-frozen, which would stop the derivation before it
    /// ran. Field sizes come from the by-name scalar table and the Optional unwrap, not from these
    /// records.
    /// </summary>
    private static void SeedPrimitives(TypeDatabase typeDatabase)
    {
        var seeded = new List<(SwiftTypeName, TypeRecord)>();
        foreach (var name in new[] { "Swift.Int32", "Swift.Int64", "Swift.Optional" })
        {
            var swiftName = SwiftTypeName.FromModuleQualifiedName(name);
            seeded.Add((swiftName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", swiftName.Name),
                SwiftTypeName = swiftName,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            }));
        }
        typeDatabase.AddOutOfModuleTypes(seeded);
    }

    private static PropertyDecl CreateProperty(
        string name, TypeSpec type, bool hasStorage = true, bool isStatic = false)
        => new()
        {
            Name = name,
            SwiftTypeSpec = type,
            IsStatic = isStatic,
            HasStorage = hasStorage,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

    private static StructDecl CreateStructDecl(
        string name, NamedTypeSpec typeSpec, params (string propName, TypeSpec propType)[] properties)
    {
        var propertyDecls = new List<PropertyDecl>();
        foreach (var (propName, propType) in properties)
            propertyDecls.Add(CreateProperty(propName, propType));

        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromTypeSpec(typeSpec),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            Properties = propertyDecls,
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static EnumDecl CreateEnumDecl(string name, string? rawValueTypeName, string[] caseNames)
        => new()
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}ON",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Cases = caseNames.Select(caseName => new EnumCaseDecl
            {
                Name = caseName,
                MangledName = $"$s10TestModule{name.Length}{name}O{caseName.Length}{caseName}yA2CmF",
                ParentDecl = null,
                ModuleDecl = null,
            }).ToList(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = "",
            RawValueTypeName = rawValueTypeName,
        };

    private static ClassDecl CreateClassDecl(string name)
        => new()
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            SuperclassUsr = null,
            SuperclassNames = new List<string>(),
        };
}
