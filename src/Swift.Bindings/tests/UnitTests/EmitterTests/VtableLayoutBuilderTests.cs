// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the reverse-dispatch slot semantics of <see cref="VtableLayoutBuilder"/> — the single source
/// every vtable-layout walk renders. These are the invariants a divergent hand-copied walk used to be
/// able to break silently (slot-index skew only SIGSEGVs on the NativeAOT device leg): the index axis
/// (skip-but-consume vs pre-skip vs duplicate-collapse), the property/subscript/method keying, the
/// async-effect overload split, and per-method slot width.
/// </summary>
public class VtableLayoutBuilderTests
{
    private readonly VtableLayoutBuilder _builder;

    public VtableLayoutBuilderTests()
    {
        var typeDatabase = new TypeDatabase();
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/fake/path"));
        _builder = new VtableLayoutBuilder(typeDatabase);
    }

    // ---- index allocation -------------------------------------------------------------------

    [Fact]
    public void Build_EmptyProtocol_HasNoSlots()
    {
        var layout = _builder.Build(CreateProtocol("Empty"));

        Assert.Empty(layout.Slots);
        Assert.Empty(layout.IncludedSlots);
    }

    [Fact]
    public void Build_SimpleInstanceMethod_GetsSlotZero()
    {
        var proto = CreateProtocol("P");
        proto.Methods.Add(CreateMethod("foo"));

        var layout = _builder.Build(proto);

        var slot = Assert.Single(layout.IncludedMethods);
        Assert.Equal(0, slot.SlotIndex);
        Assert.True(slot.Included);
        Assert.Equal(SlotVerdict.Included, slot.Verdict);
    }

    [Fact]
    public void Build_Constructor_IsPreSkippedAndConsumesNoIndex()
    {
        // A constructor is pre-skipped (no slot index); the following instance method must still
        // land at slot 0, proving the constructor consumed nothing.
        var proto = CreateProtocol("P");
        proto.Methods.Add(CreateMethod("init", isConstructor: true));
        proto.Methods.Add(CreateMethod("foo"));

        var layout = _builder.Build(proto);

        var ctor = layout.Slots.Single(s => s.AsMethod!.IsConstructor);
        Assert.Equal(SlotVerdict.ExcludedConstructor, ctor.Verdict);
        Assert.False(ctor.Included);
        Assert.Equal(-1, ctor.SlotIndex);

        var foo = layout.Slots.Single(s => s.AsMethod!.Name == "foo");
        Assert.Equal(0, foo.SlotIndex);
        Assert.True(foo.Included);
    }

    [Fact]
    public void Build_StaticMethod_IsPreSkippedAndConsumesNoIndex()
    {
        var proto = CreateProtocol("P");
        proto.Methods.Add(CreateMethod("staticThing", methodType: MethodType.Static));
        proto.Methods.Add(CreateMethod("foo"));

        var layout = _builder.Build(proto);

        var stat = layout.Slots.Single(s => s.AsMethod!.Name == "staticThing");
        Assert.Equal(SlotVerdict.ExcludedStatic, stat.Verdict);
        Assert.Equal(-1, stat.SlotIndex);
        Assert.Equal(0, layout.Slots.Single(s => s.AsMethod!.Name == "foo").SlotIndex);
    }

    [Fact]
    public void Build_ObjCOptionalMethod_IsPreSkippedAndConsumesNoIndex()
    {
        var proto = CreateProtocol("P");
        var optional = CreateMethod("maybe");
        optional.IsObjCOptional = true;
        proto.Methods.Add(optional);
        proto.Methods.Add(CreateMethod("foo"));

        var layout = _builder.Build(proto);

        var opt = layout.Slots.Single(s => s.AsMethod!.Name == "maybe");
        Assert.Equal(SlotVerdict.ExcludedObjCOptional, opt.Verdict);
        Assert.Equal(-1, opt.SlotIndex);
        Assert.Equal(0, layout.Slots.Single(s => s.AsMethod!.Name == "foo").SlotIndex);
    }

    [Fact]
    public void Build_SelfTypedMethod_IsSkipButConsume()
    {
        // A Self-typed member is excluded but is NOT pre-skipped: it consumes its slot index so the
        // Swift `_vtable` struct keeps a positional hole. The following method must therefore be at
        // slot 1, not slot 0 — this is the exact skew the single model exists to prevent.
        var proto = CreateProtocol("P");
        var selfTyped = CreateMethod("withSelf");
        selfTyped.CSSignature.Add(Param("other", new NamedTypeSpec("Self.Action")));
        proto.Methods.Add(selfTyped);
        proto.Methods.Add(CreateMethod("foo"));

        var layout = _builder.Build(proto);

        var skipped = layout.Slots.Single(s => s.AsMethod!.Name == "withSelf");
        Assert.Equal(SlotVerdict.ExcludedSelfTyped, skipped.Verdict);
        Assert.False(skipped.Included);
        Assert.Equal(0, skipped.SlotIndex);              // consumed the index

        var foo = layout.Slots.Single(s => s.AsMethod!.Name == "foo");
        Assert.Equal(1, foo.SlotIndex);                  // pushed past the hole
        Assert.True(foo.Included);
    }

    [Fact]
    public void Build_MethodLevelGenericMethod_IsSkipButConsume()
    {
        // A method-level-generic requirement (e.g. `func resolve<T>(_: T)`) is excluded from the vtable
        // but, like the Self-typed case, is NOT pre-skipped: it consumes its slot index. Covers a SECOND
        // skip-but-consume verdict (ExcludedMethodLevelGeneric) so the consume-an-index mechanic is pinned
        // for more than one Classify branch.
        var proto = CreateProtocol("P");
        var generic = CreateMethod("resolve");
        generic.CSSignature.Add(Param("value", new NamedTypeSpec("τ_1_0")));
        proto.Methods.Add(generic);
        proto.Methods.Add(CreateMethod("foo"));

        var layout = _builder.Build(proto);

        var skipped = layout.Slots.Single(s => s.AsMethod!.Name == "resolve");
        Assert.Equal(SlotVerdict.ExcludedMethodLevelGeneric, skipped.Verdict);
        Assert.False(skipped.Included);
        Assert.Equal(0, skipped.SlotIndex);              // consumed the index

        Assert.Equal(1, layout.Slots.Single(s => s.AsMethod!.Name == "foo").SlotIndex);
    }

    // ---- overload keying --------------------------------------------------------------------

    [Fact]
    public void Build_AsyncEffectOverload_GetsDistinctSlot()
    {
        // `func foo()` and `func foo() async` are distinct Swift witness-table requirements; the
        // async-sensitive slot key must keep them in separate slots.
        var proto = CreateProtocol("P");
        proto.Methods.Add(CreateMethod("foo"));
        proto.Methods.Add(CreateMethod("foo", isAsync: true));

        var layout = _builder.Build(proto);

        var slots = layout.IncludedMethods.OrderBy(s => s.SlotIndex).ToList();
        Assert.Equal(2, slots.Count);
        Assert.Equal(0, slots[0].SlotIndex);
        Assert.Equal(1, slots[1].SlotIndex);
        Assert.NotEqual(slots[0].IdentityKey, slots[1].IdentityKey);
    }

    [Fact]
    public void Build_RawKeyDuplicate_CollapsesOntoEarlierSlot()
    {
        // Two raw-key-identical methods (same name, params, effect): the second collapses onto the
        // first slot's index, emits no new field, and reports DuplicateOverload.
        var proto = CreateProtocol("P");
        proto.Methods.Add(CreateMethod("foo"));
        proto.Methods.Add(CreateMethod("foo"));

        var layout = _builder.Build(proto);

        Assert.Single(layout.IncludedMethods);
        var dup = layout.Slots.Last(s => s.AsMethod!.Name == "foo");
        Assert.Equal(SlotVerdict.DuplicateOverload, dup.Verdict);
        Assert.False(dup.Included);
        Assert.Equal(0, dup.SlotIndex);
    }

    // ---- properties & subscripts ------------------------------------------------------------

    [Fact]
    public void Build_RequirementProperty_IsNameKeyedWithNoNumericIndex()
    {
        var proto = CreateProtocol("P");
        proto.Properties.Add(CreateProperty("value", isRequirement: true));

        var layout = _builder.Build(proto);

        var slot = Assert.Single(layout.IncludedProperties);
        Assert.True(slot.Included);
        Assert.Equal("value", slot.IdentityKey);
        Assert.Equal(-1, slot.SlotIndex);
    }

    [Fact]
    public void Build_NonRequirementProperty_IsExcluded()
    {
        // The fixture-default trap: a property that is NOT a protocol requirement is Swift-owned and
        // gets no slot. Pair it with a genuine requirement so the test pins both verdicts.
        var proto = CreateProtocol("P");
        proto.Properties.Add(CreateProperty("defaulted", isRequirement: false));
        proto.Properties.Add(CreateProperty("required", isRequirement: true));

        var layout = _builder.Build(proto);

        var defaulted = layout.Slots.Single(s => s.AsProperty!.Name == "defaulted");
        Assert.Equal(SlotVerdict.ExcludedNonRequirement, defaulted.Verdict);
        Assert.False(defaulted.Included);

        Assert.True(layout.Slots.Single(s => s.AsProperty!.Name == "required").Included);
    }

    [Fact]
    public void Build_StaticProperty_IsExcluded()
    {
        var proto = CreateProtocol("P");
        var prop = CreateProperty("shared", isRequirement: true);
        prop.IsStatic = true;
        proto.Properties.Add(prop);

        var layout = _builder.Build(proto);

        Assert.Equal(SlotVerdict.ExcludedStatic, layout.Slots.Single().Verdict);
        Assert.Empty(layout.IncludedProperties);
    }

    [Fact]
    public void Build_StaticSubscript_ConsumesNoIndex_InstanceSubscriptStaysSlotZero()
    {
        // A static subscript is pre-skipped (no index consumed); a normal instance subscript must still
        // land at slot 0. This pins the static-subscript-consumes-no-index branch — the one asymmetry
        // vs. excluded instance subscripts, which DO consume their index (see the self-typed case below).
        var proto = CreateProtocol("P");
        proto.Subscripts.Add(CreateSubscript(isStatic: true));
        proto.Subscripts.Add(CreateSubscript());

        var layout = _builder.Build(proto);

        var stat = layout.Slots.Single(s => s.AsSubscript!.IsStatic);
        Assert.Equal(SlotVerdict.ExcludedStatic, stat.Verdict);
        Assert.False(stat.Included);
        Assert.Equal(-1, stat.SlotIndex);                // consumed nothing

        var instance = layout.Slots.Single(s => !s.AsSubscript!.IsStatic);
        Assert.True(instance.Included);
        Assert.Equal(0, instance.SlotIndex);             // not pushed past the static one
    }

    [Fact]
    public void Build_SelfTypedSubscript_IsSkipButConsume()
    {
        // A Self-typed instance subscript is excluded but NOT pre-skipped: it consumes its position-keyed
        // index, so a following normal subscript lands at slot 1. This is the subscript-axis mirror of
        // Build_SelfTypedMethod_IsSkipButConsume, and the counterpoint to the static case above.
        var proto = CreateProtocol("P");
        proto.Subscripts.Add(CreateSubscript(returnType: new NamedTypeSpec("Self.Element")));
        proto.Subscripts.Add(CreateSubscript());

        var layout = _builder.Build(proto);

        var selfTyped = layout.Slots.First(s => s.Kind == VtableMemberKind.Subscript);
        Assert.Equal(SlotVerdict.ExcludedSelfTyped, selfTyped.Verdict);
        Assert.False(selfTyped.Included);
        Assert.Equal(0, selfTyped.SlotIndex);            // consumed the index

        var normal = layout.Slots.Last(s => s.Kind == VtableMemberKind.Subscript);
        Assert.True(normal.Included);
        Assert.Equal(1, normal.SlotIndex);               // pushed past the hole
    }

    // ---- MethodSlotIndexByKey (the fillability/extension lookup surface) ---------------------

    [Fact]
    public void MethodSlotIndexByKey_MapsRawKeyToSlot_AndCollapsesDuplicateToEarlierIndex()
    {
        // The fillability walks (receivers, cctor assignments, cross-module parents) and the EveryProtocol
        // extension body look their slot index up in this map instead of running a parallel counter. It must
        // map each method's raw key to its slot, keep async overloads in distinct slots, and collapse a
        // raw-key duplicate onto the EARLIER slot's index (never a second entry).
        var proto = CreateProtocol("P");
        var foo = CreateMethod("foo");
        var fooAsync = CreateMethod("foo", isAsync: true);
        var fooDup = CreateMethod("foo");
        proto.Methods.Add(foo);
        proto.Methods.Add(fooAsync);
        proto.Methods.Add(fooDup);

        var map = _builder.Build(proto).MethodSlotIndexByKey;

        Assert.Equal(0, map[VtableLayoutBuilder.GetSlotKey(foo)]);
        Assert.Equal(1, map[VtableLayoutBuilder.GetSlotKey(fooAsync)]);
        // foo and fooDup share a raw key → one map entry, pointing at the earlier slot.
        Assert.Equal(VtableLayoutBuilder.GetSlotKey(foo), VtableLayoutBuilder.GetSlotKey(fooDup));
        Assert.Equal(0, map[VtableLayoutBuilder.GetSlotKey(fooDup)]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void MethodSlotIndexByKey_OmitsPreSkippedMethods()
    {
        // Pre-skipped methods (constructor / static / @objc-optional) carry SlotIndex -1 and MUST be absent
        // from the map. The fillability walks pre-skip them the same way BEFORE the lookup, so absence here
        // is the contract that keeps `methodSlotIndices[slotKey]` from ever throwing KeyNotFoundException.
        var proto = CreateProtocol("P");
        var ctor = CreateMethod("init", isConstructor: true);
        var stat = CreateMethod("shared", methodType: MethodType.Static);
        proto.Methods.Add(ctor);
        proto.Methods.Add(stat);
        proto.Methods.Add(CreateMethod("foo"));

        var map = _builder.Build(proto).MethodSlotIndexByKey;

        Assert.DoesNotContain(VtableLayoutBuilder.GetSlotKey(ctor), map.Keys);
        Assert.DoesNotContain(VtableLayoutBuilder.GetSlotKey(stat), map.Keys);
        Assert.Equal(0, map[VtableLayoutBuilder.GetSlotKey(CreateMethod("foo"))]);
        Assert.Single(map);
    }

    // ---- Classify* is the single membership oracle ProtocolVtableMembers delegates to --------

    [Fact]
    public void Classify_MatchesProtocolVtableMembers_OnSharedFixture()
    {
        // ProtocolVtableMembers.Includes* now delegate to VtableLayoutBuilder.Classify*; pin that the bool
        // each predicate returns equals (Classify*(...) == Included) so the struct walks, the cross-module
        // parent walks, and this builder can never decide membership differently.
        var typeDatabase = new TypeDatabase();
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/fake/path"));
        var closureHandler = new ClosureHandler(typeDatabase);
        var proto = CreateProtocol("P");

        var required = CreateProperty("required", isRequirement: true);
        var defaulted = CreateProperty("defaulted", isRequirement: false);
        Assert.Equal(
            VtableLayoutBuilder.ClassifyProperty(required, proto, closureHandler) == SlotVerdict.Included,
            ProtocolVtableMembers.IncludesProperty(required, proto, closureHandler));
        Assert.Equal(
            VtableLayoutBuilder.ClassifyProperty(defaulted, proto, closureHandler) == SlotVerdict.Included,
            ProtocolVtableMembers.IncludesProperty(defaulted, proto, closureHandler));

        var instanceSub = CreateSubscript();
        var staticSub = CreateSubscript(isStatic: true);
        Assert.Equal(
            VtableLayoutBuilder.ClassifySubscript(instanceSub, proto) == SlotVerdict.Included,
            ProtocolVtableMembers.IncludesSubscript(instanceSub, proto));
        Assert.Equal(
            VtableLayoutBuilder.ClassifySubscript(staticSub, proto) == SlotVerdict.Included,
            ProtocolVtableMembers.IncludesSubscript(staticSub, proto));

        var plain = CreateMethod("foo");
        var ctor = CreateMethod("init", isConstructor: true);
        Assert.Equal(
            VtableLayoutBuilder.ClassifyMethod(plain, proto, closureHandler) == SlotVerdict.Included,
            ProtocolVtableMembers.IncludesMethod(plain, proto, closureHandler));
        Assert.Equal(
            VtableLayoutBuilder.ClassifyMethod(ctor, proto, closureHandler) == SlotVerdict.Included,
            ProtocolVtableMembers.IncludesMethod(ctor, proto, closureHandler));
    }

    // ---- width ------------------------------------------------------------------------------

    [Fact]
    public void Build_VoidMethod_HasZeroWidth()
    {
        var proto = CreateProtocol("P");
        proto.Methods.Add(CreateMethod("foo"));

        var layout = _builder.Build(proto);

        Assert.Equal(0, Assert.Single(layout.IncludedMethods).Width);
    }

    [Fact]
    public void Build_MethodWithPlainParam_HasUnitWidthPerParam()
    {
        // A non-closure param contributes exactly one pointer slot; the closure-expansion-to-two case
        // is exercised end-to-end in BindingTests (it needs a real dispatchable closure shape).
        var proto = CreateProtocol("P");
        var method = CreateMethod("foo");
        method.CSSignature.Add(Param("a", new NamedTypeSpec("Swift.Int")));
        method.CSSignature.Add(Param("b", new NamedTypeSpec("Swift.Int")));
        proto.Methods.Add(method);

        var layout = _builder.Build(proto);

        Assert.Equal(2, Assert.Single(layout.IncludedMethods).Width);
    }

    // ---- determinism / path-independence ----------------------------------------------------

    [Fact]
    public void Build_IsStatelessAndDeterministic_AcrossRepeatedBuilds()
    {
        // The builder holds no cross-protocol state; same protocol in → identical slot list out.
        // This is the invariant that lets same-module and cross-module-parent walks share one oracle.
        var proto = CreateProtocol("P");
        proto.Properties.Add(CreateProperty("value", isRequirement: true));
        proto.Methods.Add(CreateMethod("foo"));
        proto.Methods.Add(CreateMethod("bar"));

        var first = _builder.Build(proto);
        var second = _builder.Build(proto);

        Assert.Equal(
            first.Slots.Select(s => (s.IdentityKey, s.SlotIndex, s.Included, s.Verdict)),
            second.Slots.Select(s => (s.IdentityKey, s.SlotIndex, s.Included, s.Verdict)));
    }

    // ---- fixtures ---------------------------------------------------------------------------

    private static ProtocolDecl CreateProtocol(string name) => new ProtocolDecl
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
        MangledName = $"$s10TestModule{name.Length}{name}P",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        AssociatedTypes = new List<AssociatedTypeDecl>(),
        InheritedProtocols = new List<NamedTypeSpec>(),
        HasSelfRequirement = false,
        IsClassBound = false,
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static MethodDecl CreateMethod(
        string name,
        bool isAsync = false,
        bool isConstructor = false,
        MethodType methodType = MethodType.Instance) => new MethodDecl
    {
        Name = name,
        MangledName = $"$s{name}",
        MethodType = methodType,
        IsConstructor = isConstructor,
        CSSignature = new List<ArgumentDecl> { VoidReturn() },
        GenericParameters = new List<GenericArgumentDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
        Throws = false,
        IsAsync = isAsync,
        IsSynthesizedAccessor = false,
    };

    private static PropertyDecl CreateProperty(string name, bool isRequirement) => new PropertyDecl
    {
        Name = name,
        SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
        IsStatic = false,
        HasStorage = false,
        IsProtocolRequirement = isRequirement,
        Accessors = new List<AccessorDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static SubscriptDecl CreateSubscript(bool isStatic = false, TypeSpec returnType = null) => new SubscriptDecl
    {
        Name = "subscript",
        ReturnTypeSpec = returnType ?? new NamedTypeSpec("Swift.Int"),
        IndexParameters = new List<ArgumentDecl> { Param("index", new NamedTypeSpec("Swift.Int")) },
        IsStatic = isStatic,
        Accessors = new List<AccessorDecl>(),
        MangledName = "$sSubscript",
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static ArgumentDecl VoidReturn() => new ArgumentDecl
    {
        Name = "",
        SwiftTypeSpec = TupleTypeSpec.Empty,
        PrivateName = "",
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static ArgumentDecl Param(string name, TypeSpec type) => new ArgumentDecl
    {
        Name = name,
        SwiftTypeSpec = type,
        PrivateName = "",
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = null,
    };
}
