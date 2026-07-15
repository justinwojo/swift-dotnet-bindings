// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Fail-closed coverage for `@objc` protocol existentials in out-of-scope positions.
///
/// An `@objc` protocol's existential (`any P` / `Optional<any P>`) has a single 8-byte ObjC
/// object-pointer ABI (AnyObject-shaped, no witness table). Only the bare and single-Optional
/// forms in a synchronous parameter/return/property position marshal correctly. Every other
/// position — nested inside a container/tuple/closure, or ANY position in an async method —
/// would route the existential through the ExistentialContainer1 carrier whose descriptor
/// registration silently fails for `@objc` (no `…Mp` descriptor), producing a wrong-ABI
/// mis-emission or a buffer over-read crash. Those members must be DROPPED (fail-closed).
///
/// These tests pin (a) the recursive shape predicates that classify a position and (b) the
/// validator wiring that turns an unsupported position into a member drop, while confirming the
/// gate is surgical — the supported bare/optional forms survive and a non-`@objc` existential
/// container is untouched.
/// </summary>
public class ObjCExistentialFailClosedTests
{
    private const string ObjCProto = "TestModule.ObjCProto";
    private const string SwiftProto = "TestModule.SwiftProto";

    private static ITypeDatabase BuildDatabase()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        // @objc protocols are ClassBound and carry the ObjCProtocol flag; the predicate keys on
        // the ObjCProtocol bit specifically.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(ObjCProto),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IObjCProto"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(ObjCProto),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCProtocol | TypeRecordFlags.ClassBound,
                Kind = TypeRecordKind.Protocol,
            });
        // A plain Swift protocol — used to prove the gate is surgical (only @objc is dropped).
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(SwiftProto),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ISwiftProto"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(SwiftProto),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol,
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    private static ProtocolListTypeSpec Any(string protocol) =>
        new ProtocolListTypeSpec(new[] { new NamedTypeSpec(protocol) });

    private static NamedTypeSpec Optional(TypeSpec inner) => new NamedTypeSpec("Swift.Optional", inner);
    private static NamedTypeSpec Array(TypeSpec element) => new NamedTypeSpec("Swift.Array", element);
    private static NamedTypeSpec Dictionary(TypeSpec key, TypeSpec value) => new NamedTypeSpec("Swift.Dictionary", key, value);
    private static NamedTypeSpec Int() => new NamedTypeSpec("Swift.Int");
    private static NamedTypeSpec String() => new NamedTypeSpec("Swift.String");
    private static TupleTypeSpec Void() => TupleTypeSpec.Empty;

    #region IsObjCProtocolExistentialSpec — supported top-level shapes

    [Fact]
    public void IsObjCProtocolExistentialSpec_BareAnyObjC_ReturnsTrue()
    {
        var db = BuildDatabase();
        Assert.True(ExistentialHandler.IsObjCProtocolExistentialSpec(Any(ObjCProto), db));
    }

    [Fact]
    public void IsObjCProtocolExistentialSpec_OptionalAnyObjC_ReturnsTrue()
    {
        var db = BuildDatabase();
        Assert.True(ExistentialHandler.IsObjCProtocolExistentialSpec(Optional(Any(ObjCProto)), db, out var isOptional));
        Assert.True(isOptional);
    }

    [Fact]
    public void IsObjCProtocolExistentialSpec_NonObjCProtocol_ReturnsFalse()
    {
        var db = BuildDatabase();
        Assert.False(ExistentialHandler.IsObjCProtocolExistentialSpec(Any(SwiftProto), db));
    }

    [Fact]
    public void IsObjCProtocolExistentialSpec_ArrayOfAnyObjC_ReturnsFalse()
    {
        // A container is never a top-level @objc existential — that is what ContainsObjCProtocolExistential is for.
        var db = BuildDatabase();
        Assert.False(ExistentialHandler.IsObjCProtocolExistentialSpec(Array(Any(ObjCProto)), db));
    }

    #endregion

    #region ContainsObjCProtocolExistential — recursive occurrence

    public static IEnumerable<object[]> NestedObjCShapes()
    {
        yield return new object[] { "array", Array(Any(ObjCProto)) };
        yield return new object[] { "optional-array", Optional(Array(Any(ObjCProto))) };
        yield return new object[] { "dictionary-value", Dictionary(String(), Any(ObjCProto)) };
        yield return new object[] { "tuple", new TupleTypeSpec(new TypeSpec[] { Any(ObjCProto), Int() }) };
        yield return new object[] { "closure-param", new ClosureTypeSpec(new TupleTypeSpec(new TypeSpec[] { Any(ObjCProto) }), Void()) };
        yield return new object[] { "closure-return", new ClosureTypeSpec(Void(), Any(ObjCProto)) };
        yield return new object[] { "closure-optional-return", new ClosureTypeSpec(Void(), Optional(Any(ObjCProto))) };
    }

    [Theory]
    [MemberData(nameof(NestedObjCShapes))]
    public void ContainsObjCProtocolExistential_NestedObjC_ReturnsTrue(string label, TypeSpec spec)
    {
        Assert.NotNull(label);
        var db = BuildDatabase();
        Assert.True(ExistentialHandler.ContainsObjCProtocolExistential(spec, db));
    }

    [Fact]
    public void ContainsObjCProtocolExistential_BareAnyObjC_ReturnsTrue()
    {
        var db = BuildDatabase();
        Assert.True(ExistentialHandler.ContainsObjCProtocolExistential(Any(ObjCProto), db));
    }

    [Fact]
    public void ContainsObjCProtocolExistential_ArrayOfNonObjC_ReturnsFalse()
    {
        // Surgical: a non-@objc existential container is not an @objc occurrence.
        var db = BuildDatabase();
        Assert.False(ExistentialHandler.ContainsObjCProtocolExistential(Array(Any(SwiftProto)), db));
    }

    [Fact]
    public void ContainsObjCProtocolExistential_PlainContainer_ReturnsFalse()
    {
        var db = BuildDatabase();
        Assert.False(ExistentialHandler.ContainsObjCProtocolExistential(Array(Int()), db));
    }

    #endregion

    #region HasUnsupportedObjCProtocolExistentialPosition — the drop predicate

    [Fact]
    public void HasUnsupportedPosition_BareAnyObjC_ReturnsFalse()
    {
        // Supported: bare `any P` marshals as a single object pointer.
        var db = BuildDatabase();
        Assert.False(ExistentialHandler.HasUnsupportedObjCProtocolExistentialPosition(Any(ObjCProto), db));
    }

    [Fact]
    public void HasUnsupportedPosition_OptionalAnyObjC_ReturnsFalse()
    {
        // Supported: single `Optional<any P>` marshals as a nullable object pointer.
        var db = BuildDatabase();
        Assert.False(ExistentialHandler.HasUnsupportedObjCProtocolExistentialPosition(Optional(Any(ObjCProto)), db));
    }

    [Theory]
    [MemberData(nameof(NestedObjCShapes))]
    public void HasUnsupportedPosition_NestedObjC_ReturnsTrue(string label, TypeSpec spec)
    {
        Assert.NotNull(label);
        var db = BuildDatabase();
        Assert.True(ExistentialHandler.HasUnsupportedObjCProtocolExistentialPosition(spec, db));
    }

    [Fact]
    public void HasUnsupportedPosition_NonObjCContainer_ReturnsFalse()
    {
        // Surgical: a non-@objc existential container keeps its existing (supported) behavior.
        var db = BuildDatabase();
        Assert.False(ExistentialHandler.HasUnsupportedObjCProtocolExistentialPosition(Array(Any(SwiftProto)), db));
    }

    #endregion

    #region ShouldSkipMethodEmission — sync nested + async drop

    private static MethodDecl Method(string name, bool isAsync, params ArgumentDecl[] signature) =>
        new MethodDecl
        {
            Name = name,
            MangledName = "$s10TestModule" + name,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = signature.ToList(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = new ModuleDecl
            {
                Name = "TestModule",
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Dependencies = new List<string>(),
                Protocols = new List<ProtocolDecl>(),
                ParentDecl = null,
                ModuleDecl = null,
            },
            Throws = false,
            IsAsync = isAsync,
            IsSynthesizedAccessor = false,
        };

    private static ArgumentDecl Arg(string name, TypeSpec spec) =>
        new ArgumentDecl
        {
            SwiftTypeSpec = spec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null,
        };

    [Fact]
    public void ShouldSkipMethodEmission_ArrayOfAnyObjCParam_DropsUnsupportedExistential()
    {
        var db = BuildDatabase();
        var method = Method("takesArray", isAsync: false,
            Arg(string.Empty, Void()),
            Arg("xs", Array(Any(ObjCProto))));

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, db, out var details);

        Assert.Equal(SkipReason.UnsupportedExistential, result);
        Assert.Contains("@objc", details);
    }

    [Fact]
    public void ShouldSkipMethodEmission_TupleReturnWithAnyObjC_DropsUnsupportedExistential()
    {
        var db = BuildDatabase();
        var method = Method("returnsTuple", isAsync: false,
            Arg(string.Empty, new TupleTypeSpec(new TypeSpec[] { Any(ObjCProto), Int() })));

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, db, out var details);

        Assert.Equal(SkipReason.UnsupportedExistential, result);
        Assert.Contains("@objc", details);
    }

    [Fact]
    public void ShouldSkipMethodEmission_AsyncReturnsAnyObjC_DropsUnsupportedExistential()
    {
        // Even the bare `any P` form is unsupported in an async RETURN — the async harness reads the
        // return through the ExistentialContainer1 carrier (40 bytes) over the 8-byte @objc cell.
        var db = BuildDatabase();
        var method = Method("asyncReturns", isAsync: true,
            Arg(string.Empty, Any(ObjCProto)));

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, db, out var details);

        Assert.Equal(SkipReason.UnsupportedExistential, result);
        Assert.Contains("Async", details);
    }

    [Fact]
    public void ShouldSkipMethodEmission_BareAnyObjCParam_NotDroppedForObjCReason()
    {
        // Regression guard: the supported bare form must NOT be dropped by the @objc position gate.
        var db = BuildDatabase();
        var method = Method("takesBare", isAsync: false,
            Arg(string.Empty, Void()),
            Arg("x", Any(ObjCProto)));

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, db, out _);

        Assert.NotEqual(SkipReason.UnsupportedExistential, result);
    }

    #endregion

    #region CanEmitProperty — nested container property drop

    [Fact]
    public void CanEmitProperty_ArrayOfAnyObjC_DropsUnsupportedExistential()
    {
        var db = BuildDatabase();
        var property = new PropertyDecl
        {
            Name = "shapes",
            IsStatic = false,
            HasStorage = true,
            SwiftTypeSpec = Array(Any(ObjCProto)),
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var result = MemberEmissionValidator.CanEmitProperty(property, db, out var details, out _);

        Assert.Equal(SkipReason.UnsupportedExistential, result);
        Assert.Contains("@objc", details);
    }

    #endregion

    #region Reverse-path lockstep — MemberGateEvaluator (interface) + VtableLayoutBuilder (vtable slot)

    // The concrete-witness path (MemberEmissionValidator, above) was the ONLY enforcer of the @objc
    // nested-existential gate. The protocol reverse-dispatch path is a SEPARATE pair of oracles — the
    // member gate that decides interface membership and the vtable classifier that decides slot
    // membership — and both must drop the same shape or the C# vtable desyncs from the Swift `{P}_vtable`
    // struct (→ SIGSEGV on the NativeAOT device leg). These tests pin that both fire on a nested @objc
    // existential, that neither touches the supported bare form, and — critically — that they agree.

    private static ProtocolDecl Protocol(string name) => new ProtocolDecl
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
        IsClassBound = true,
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static PropertyDecl RequirementProperty(TypeSpec spec) => new PropertyDecl
    {
        Name = "shapes",
        IsStatic = false,
        HasStorage = false,
        IsProtocolRequirement = true,
        SwiftTypeSpec = spec,
        Accessors = new List<AccessorDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static SubscriptDecl RequirementSubscript(TypeSpec returnSpec) => new SubscriptDecl
    {
        Name = "subscript",
        ReturnTypeSpec = returnSpec,
        IndexParameters = new List<ArgumentDecl> { Arg("index", Int()) },
        IsStatic = false,
        Accessors = new List<AccessorDecl>(),
        MangledName = "$sSubscript",
        ParentDecl = null,
        ModuleDecl = null,
    };

    [Fact]
    public void MemberGate_MethodWithNestedObjCParam_DropsFromInterface()
    {
        var db = BuildDatabase();
        var proto = Protocol("P");
        var method = Method("takesArray", isAsync: false,
            Arg(string.Empty, Void()),
            Arg("xs", Array(Any(ObjCProto))));

        var result = new MemberGateEvaluator(db).EvaluateMethod(method, null, proto);

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.UnsupportedExistential, result.Reason);
    }

    [Fact]
    public void MemberGate_PropertyWithNestedObjC_DropsFromInterface()
    {
        var db = BuildDatabase();
        var proto = Protocol("P");
        var result = new MemberGateEvaluator(db).EvaluateProperty(RequirementProperty(Array(Any(ObjCProto))), null, proto);

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.UnsupportedExistential, result.Reason);
    }

    [Fact]
    public void MemberGate_SubscriptWithNestedObjCReturn_DropsFromInterface()
    {
        var db = BuildDatabase();
        var proto = Protocol("P");
        var result = new MemberGateEvaluator(db).EvaluateSubscript(RequirementSubscript(Array(Any(ObjCProto))), null, proto);

        Assert.True(result.IsSkipped);
        Assert.Equal(SkipReason.UnsupportedExistential, result.Reason);
    }

    [Fact]
    public void MemberGate_MethodWithBareObjCParam_NotDroppedForExistentialReason()
    {
        // Surgical: the supported bare `any P` form is NOT hard-dropped by the @objc gate (it may still
        // route through the interface as a soft-gated existential, but never as UnsupportedExistential).
        var db = BuildDatabase();
        var proto = Protocol("P");
        var method = Method("takesBare", isAsync: false,
            Arg(string.Empty, Void()),
            Arg("x", Any(ObjCProto)));

        var result = new MemberGateEvaluator(db).EvaluateMethod(method, null, proto);

        Assert.False(result.IsSkipped && result.Reason == SkipReason.UnsupportedExistential);
    }

    [Fact]
    public void VtableClassify_MethodWithNestedObjCParam_ExcludesSlot()
    {
        var db = BuildDatabase();
        var closureHandler = new ClosureHandler(db);
        var proto = Protocol("P");
        var method = Method("takesArray", isAsync: false,
            Arg(string.Empty, Void()),
            Arg("xs", Array(Any(ObjCProto))));

        Assert.Equal(SlotVerdict.ExcludedUnsupportedObjCExistential,
            VtableLayoutBuilder.ClassifyMethod(method, proto, closureHandler));
    }

    [Fact]
    public void VtableClassify_PropertyWithNestedObjC_ExcludesSlot()
    {
        var db = BuildDatabase();
        var closureHandler = new ClosureHandler(db);
        var proto = Protocol("P");

        Assert.Equal(SlotVerdict.ExcludedUnsupportedObjCExistential,
            VtableLayoutBuilder.ClassifyProperty(RequirementProperty(Array(Any(ObjCProto))), proto, closureHandler));
    }

    [Fact]
    public void VtableClassify_SubscriptWithNestedObjCReturn_ExcludesSlot()
    {
        var db = BuildDatabase();
        var closureHandler = new ClosureHandler(db);
        var proto = Protocol("P");

        Assert.Equal(SlotVerdict.ExcludedUnsupportedObjCExistential,
            VtableLayoutBuilder.ClassifySubscript(RequirementSubscript(Array(Any(ObjCProto))), proto, closureHandler));
    }

    [Fact]
    public void VtableClassify_MethodWithBareObjCParam_KeepsSlot()
    {
        // The supported bare form still occupies a slot — the gate is surgical, only nested drops.
        var db = BuildDatabase();
        var closureHandler = new ClosureHandler(db);
        var proto = Protocol("P");
        var method = Method("takesBare", isAsync: false,
            Arg(string.Empty, Void()),
            Arg("x", Any(ObjCProto)));

        Assert.Equal(SlotVerdict.Included,
            VtableLayoutBuilder.ClassifyMethod(method, proto, closureHandler));
    }

    [Theory]
    [MemberData(nameof(NestedObjCShapes))]
    public void MemberGateAndVtableClassify_AgreeOnNestedObjCMethod(string label, TypeSpec spec)
    {
        // THE lockstep invariant: interface-drop ⇔ vtable-slot-drop. If these ever diverge for a shape,
        // the C# vtable buffer and the Swift `{P}_vtable` struct desync → device SIGSEGV.
        Assert.NotNull(label);
        var db = BuildDatabase();
        var closureHandler = new ClosureHandler(db);
        var proto = Protocol("P");
        var method = Method("m", isAsync: false, Arg(string.Empty, Void()), Arg("p", spec));

        bool droppedFromInterface = new MemberGateEvaluator(db).EvaluateMethod(method, null, proto).IsSkipped;
        bool droppedFromVtable =
            VtableLayoutBuilder.ClassifyMethod(method, proto, closureHandler) != SlotVerdict.Included;

        Assert.Equal(droppedFromInterface, droppedFromVtable);
    }

    [Fact]
    public void VtableBuild_NestedObjCMethodFirst_ConsumesIndexAndPushesLaterMethod()
    {
        // Skip-but-consume, asserted at the Build() layer: a dropped nested-@objc-existential method still
        // consumes its slot index, so a later SUPPORTED method lands at the NEXT slot — never at the dropped
        // member's index, and never double-counted. This is the exact device-only slot skew the
        // ObjCExistentialReverseVtableLockstep runtime fixture guards end-to-end (hazard declared FIRST, a
        // supported scalar LAST); pinning the index arithmetic here catches a regression at the unit layer
        // before it reaches the NativeAOT leg.
        var db = BuildDatabase();
        var proto = Protocol("P");
        proto.Methods.Add(Method("absorb", isAsync: false,
            Arg(string.Empty, Void()), Arg("xs", Array(Any(ObjCProto)))));
        proto.Methods.Add(Method("count", isAsync: false, Arg(string.Empty, Int())));

        var layout = new VtableLayoutBuilder(db).Build(proto);

        var hazard = layout.Slots.Single(s => s.AsMethod!.Name == "absorb");
        Assert.Equal(SlotVerdict.ExcludedUnsupportedObjCExistential, hazard.Verdict);
        Assert.False(hazard.Included);
        Assert.Equal(0, hazard.SlotIndex);    // consumed the index

        var supported = layout.Slots.Single(s => s.AsMethod!.Name == "count");
        Assert.True(supported.Included);
        Assert.Equal(1, supported.SlotIndex); // pushed past the hole, not sitting on the dropped index
    }

    #endregion

    #region Fan-out branch filter — same layout oracle as the vtable struct

    // The same-signature owner/peer fan-out (ComputeMethodEmissionPlans) filters its branch list to
    // siblings that actually emit a vtable FUNC field, then emits a branch reading
    // `branchVtable.func_{name}_{idx}` for each surviving sibling. That field is emitted ONLY for a
    // layout-included slot (EmitProtocolVtableStruct walks VtableLayout.IncludedSlots). If the fan-out
    // filter re-derived membership with a predicate that disagreed with the layout on a nested @objc
    // protocol existential — keeping the sibling the layout dropped — it would emit a branch over a
    // `func_...` member the struct never declared, failing Swift wrapper compilation for the whole
    // package. This pins the two on one oracle: a nested-@objc method is dropped from the fan-out
    // branch list exactly as it is dropped from the layout slot.

    [Fact]
    public void FanOut_NestedObjCMethodSharedAcrossProtocols_ExcludedFromBranchList()
    {
        var db = BuildDatabase();
        var emitter = new EveryProtocolEmitter(db, NullLogger.Instance, "TestModule");

        // Two plain protocols (same carrier) declaring one IDENTICAL nested-@objc-existential method,
        // so they form a single same-signature fan-out group.
        var first = Protocol("FirstCollector");
        first.Methods.Add(Method("absorb", isAsync: false,
            Arg(string.Empty, Void()), Arg("xs", Array(Any(ObjCProto)))));
        var second = Protocol("SecondCollector");
        second.Methods.Add(Method("absorb", isAsync: false,
            Arg(string.Empty, Void()), Arg("xs", Array(Any(ObjCProto)))));

        var plans = emitter.ComputeMethodEmissionPlans(new[] { first, second });
        var plan = Assert.Single(new HashSet<EveryProtocolEmitter.MethodEmissionPlan>(plans.Values));

        // The layout drops the nested-@objc slot for BOTH protocols, so no branch may reference a
        // `func_absorb_*` field: the branch list is empty and the drop forces the guarded fan-out.
        Assert.Empty(plan.Siblings);
        Assert.True(plan.HasFilteredPeers);
    }

    [Fact]
    public void FanOut_SupportedMethodSharedAcrossProtocols_KeepsBothBranches()
    {
        // Regression guard: a SUPPORTED method (bare `any P`) still occupies a slot on both protocols,
        // so the fan-out keeps both branches — the filter is surgical, only the nested form drops.
        var db = BuildDatabase();
        var emitter = new EveryProtocolEmitter(db, NullLogger.Instance, "TestModule");

        var first = Protocol("FirstCollector");
        first.Methods.Add(Method("absorb", isAsync: false,
            Arg(string.Empty, Void()), Arg("x", Any(ObjCProto))));
        var second = Protocol("SecondCollector");
        second.Methods.Add(Method("absorb", isAsync: false,
            Arg(string.Empty, Void()), Arg("x", Any(ObjCProto))));

        var plans = emitter.ComputeMethodEmissionPlans(new[] { first, second });
        var plan = Assert.Single(new HashSet<EveryProtocolEmitter.MethodEmissionPlan>(plans.Values));

        Assert.Equal(2, plan.Siblings.Count);
        Assert.False(plan.HasFilteredPeers);
    }

    #endregion
}
