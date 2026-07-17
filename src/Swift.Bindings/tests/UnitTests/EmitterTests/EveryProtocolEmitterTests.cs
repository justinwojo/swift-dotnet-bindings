// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for EveryProtocolEmitter Swift code generation.
/// </summary>
public class EveryProtocolEmitterTests
{
    private readonly TypeDatabase _typeDatabase;
    private readonly EveryProtocolEmitter _emitter;

    public EveryProtocolEmitterTests()
    {
        _typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        _typeDatabase.AddModuleDatabase(module);
        _emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
    }

    #region EveryProtocol Class Emission Tests

    [Fact]
    public void EmitEveryProtocolClass_GeneratesClassDeclaration()
    {
        var output = EmitEveryProtocolClass();

        Assert.Contains("public final class EveryProtocol", output);
    }

    [Fact]
    public void EmitEveryProtocolClass_GeneratesHandleProperty()
    {
        var output = EmitEveryProtocolClass();

        Assert.Contains("let handle: UnsafeRawPointer?", output);
    }

    [Fact]
    public void EmitEveryProtocolClass_GeneratesDefaultInit()
    {
        var output = EmitEveryProtocolClass();

        Assert.Contains("public init()", output);
    }

    [Fact]
    public void EmitEveryProtocolClass_GeneratesHandleInit()
    {
        var output = EmitEveryProtocolClass();

        Assert.Contains("public init(handle: UnsafeRawPointer)", output);
    }

    #endregion

    #region Protocol Vtable Struct Emission Tests

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesStructDeclaration()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("fileprivate struct TestProtocol_vtable", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesCsVTHandle()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("var csVTHandle: OpaquePointer?", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesPropertyGetterField()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("var func_value_get:", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesPropertySetterField()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: true);
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("var func_value_set:", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesMethodField()
    {
        var protocolDecl = CreateProtocolWithMethod("TestProtocol", "doSomething");
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("var func_doSomething_0:", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_MethodField_SlotCountMatchesLayoutWidth()
    {
        // The Swift `_vtable` method field's @convention(c) parameter arity is driven by the SAME
        // width oracle (VtableLayout.GetWidth) that sizes the C# LocalVTable mirror. A debug or
        // empty-tuple param contributes NO ABI slot, so hand-counting CSSignature (which the field
        // once did) over-produces a slot the mirror never allocates — shifting every later field and
        // corrupting reverse dispatch. This pins the emitted field arity to 2 fixed leading slots
        // (vtable handle + self) plus the oracle width.
        var protocol = CreateSimpleProtocol("TestProtocol");
        var method = CreateMethodDecl("doWork");
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "value",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            PrivateName = "value",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        });
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "unit",
            SwiftTypeSpec = TupleTypeSpec.Empty, // empty tuple → no ABI slot (skipped by GetWidth)
            PrivateName = "unit",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(method);

        var width = Assert.Single(new VtableLayoutBuilder(_typeDatabase).Build(protocol).IncludedMethods).Width;
        var output = EmitVtableStruct(protocol);

        var fieldLine = output.Split('\n').Single(l => l.Contains("func_doWork_0:"));
        int open = fieldLine.IndexOf("@convention(c)(", StringComparison.Ordinal) + "@convention(c)(".Length;
        int close = fieldLine.IndexOf(") ->", open, StringComparison.Ordinal);
        var slots = fieldLine.Substring(open, close - open).Split(',');

        Assert.Equal(1, width);                  // one Int param; the empty tuple adds nothing
        Assert.Equal(2 + width, slots.Length);   // vtable handle + self + oracle width
    }

    [Fact]
    public void EmitProtocolVtableStruct_GeneratesVtableInstance()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitVtableStruct(protocolDecl);

        Assert.Contains("private var _testProtocol_vtable = TestProtocol_vtable()", output);
    }

    #endregion

    #region Protocol Extension Emission Tests

    [Fact]
    public void EmitProtocolExtension_GeneratesExtensionDeclaration()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("extension EveryProtocol: TestModule.TestProtocol", output);
    }

    [Fact]
    public void EmitProtocolExtension_GeneratesPropertyGetter()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public var value:", output);
        Assert.Contains("get {", output);
    }

    [Fact]
    public void EmitProtocolExtension_GeneratesPropertySetter()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: true);
        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("set {", output);
    }

    [Fact]
    public void EmitProtocolExtension_GeneratesMethodImplementation()
    {
        var protocolDecl = CreateProtocolWithMethod("TestProtocol", "doSomething");
        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public func doSomething()", output);
    }

    [Fact]
    public void EmitProtocolExtension_CollapsedExistentialOverload_SecondWitnessTrapsInsteadOfNilForceUnwrap()
    {
        // Two raw-DISTINCT existential overloads of the same method name whose raw signature keys both
        // erase to Swift.AnyType (the FirebaseFirestore add(any Expression)/add(any Sendable) shape)
        // collapse onto ONE C# interface method. The reverse-dispatch layout still gives each its own
        // vtable slot (GetMethodKey keys off the raw Swift type), but the C# fillability walk fills only
        // the FIRST overload's slot — the second's is left null. The second's Swift witness must trap
        // rather than force-unwrap the nil slot at the point of dispatch (the G8 crash). The first
        // overload's real reverse-dispatch body must be untouched.
        var protocolDecl = CreateSimpleProtocol("OverloadCollapse");
        // Unregistered existential-ish params: their raw signature key erases to Swift.AnyType and
        // collapses, while GetMethodKey (raw type string) keeps them in distinct slots.
        protocolDecl.Methods.Add(CreateMethodDeclWithParam("record", "value", "TestModule.CollapsePrimary"));
        protocolDecl.Methods.Add(CreateMethodDeclWithParam("record", "value", "TestModule.CollapseSecondary"));

        var output = EmitProtocolExtension(protocolDecl);

        // First (surviving) overload keeps its real reverse-dispatch body: force-unwraps slot 0.
        Assert.Contains("func_record_0!", output);
        // Second overload is a branded fatalError stub; it must NOT reference/force-unwrap its null slot.
        Assert.DoesNotContain("func_record_1", output);
        Assert.Contains("[SwiftBindings] EveryProtocol: collapsed existential overload 'record'", output);
    }

    [Fact]
    public void EmitProtocolExtension_EscapesSwiftKeywordPropertyName()
    {
        // A protocol requirement whose member NAME is a Swift keyword (e.g. `repeat`)
        // must be emitted as a backtick-escaped identifier in the conformance, or the
        // generated Swift fails to compile (`public var repeat:` is a parse error).
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "repeat", hasGetter: true, hasSetter: false);
        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public var `repeat`:", output);
        Assert.DoesNotContain("public var repeat:", output);
    }

    [Fact]
    public void EmitProtocolExtension_EscapesSwiftKeywordMethodName()
    {
        // A method requirement whose NAME is a Swift keyword (e.g. `class`) must be
        // backtick-escaped in the conformance declaration.
        var protocolDecl = CreateProtocolWithMethod("TestProtocol", "class");
        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public func `class`(", output);
        Assert.DoesNotContain("public func class(", output);
    }

    [Fact]
    public void EmitProtocolExtension_AsyncMethod_PureSwiftBase_OmitsAsyncModifier()
    {
        // Pure-Swift protocol conformances (EveryProtocol base, not EveryObjCProtocol)
        // satisfy async requirements with sync witnesses: in Swift, sync trivially
        // satisfies async ("never suspends"). Emitting `async` on the witness is not
        // just unnecessary — it breaks any sibling/child protocol whose SYNC requirement
        // was being member-inherited from this conformance, e.g. a sync `modified(for:)` requirement
        // inheriting from an async protocol sibling.
        var protocolDecl = CreateSimpleProtocol("AsyncProvider");
        var method = CreateMethodDecl("fetchValue", throws: true);
        method.IsAsync = true;
        protocolDecl.Methods.Add(method);

        var output = EmitProtocolExtension(protocolDecl);

        // Sync witness for an async requirement is valid Swift on the EveryProtocol base.
        Assert.Contains("public func fetchValue() throws", output);
        Assert.DoesNotContain("public func fetchValue() async", output);
    }

    [Fact]
    public void EmitProtocolExtension_AsyncMethod_ObjCBase_EmitsAsyncModifier()
    {
        // Regression: @objc protocol requirements declared `async throws` bridge to ObjC
        // `:completion:`-suffixed selectors. swiftc rejects sync candidates on
        // EveryObjCProtocol with "candidate is not 'async', but '@objc' protocol
        // requirement is" — so the async modifier MUST appear on the witness when the
        // conformance is routed through the NSObject-rooted EveryObjCProtocol base.
        var protocolDecl = CreateSimpleProtocol("AsyncThrowingObjCProvider");
        var method = CreateMethodDecl("fetchValue", throws: true);
        method.IsAsync = true;
        protocolDecl.Methods.Add(method);

        // Flip the per-protocol routing flag to the @objc base, as
        // EmitProtocolConformance would for an NSObjectProtocol-only protocol.
        var useObjCBaseField = typeof(EveryProtocolEmitter)
            .GetField("_useObjCBase", BindingFlags.Instance | BindingFlags.NonPublic);
        useObjCBaseField!.SetValue(_emitter, true);
        try
        {
            var output = EmitProtocolExtension(protocolDecl);
            Assert.Contains("public func fetchValue() async throws", output);
        }
        finally
        {
            useObjCBaseField.SetValue(_emitter, false);
        }
    }

    #endregion

    #region SetVtable Function Emission Tests

    [Fact]
    public void EmitSetVtableFunction_GeneratesCdeclExport()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitSetVtableFunction(protocolDecl);

        // @_cdecl (not @_silgen_name): the symbol must be a C-exported entry point so it is a
        // linker root that survives dead-stripping on NativeAOT/device builds. The C# P/Invoke
        // calls it with CallConvCdecl.
        Assert.Contains("@_cdecl(\"SetTestProtocol_vtable\")", output);
    }

    [Fact]
    public void EmitSetVtableFunction_GeneratesPublicFunction()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitSetVtableFunction(protocolDecl);

        Assert.Contains("public func setTestProtocol_vtable(uvt: UnsafeRawPointer)", output);
    }

    [Fact]
    public void EmitSetVtableFunction_CopiesVtable()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var output = EmitSetVtableFunction(protocolDecl);

        Assert.Contains("_testProtocol_vtable = vt.pointee", output);
    }

    #endregion

    #region Protocol Conformance Filtering Tests

    [Fact]
    public void EmitProtocolConformance_SkipsProtocolsWithSelfRequirement()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        protocolDecl.HasSelfRequirement = true;

        var output = EmitFullConformance(protocolDecl);

        Assert.DoesNotContain("extension EveryProtocol:", output);
    }

    [Fact]
    public void EmitProtocolConformance_GeneratesTypealiasForAssociatedTypes()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });

        var output = EmitFullConformance(protocolDecl);

        Assert.Contains("extension EveryProtocol: TestModule.TestProtocol", output);
        Assert.Contains("public typealias Element = Any", output);
    }

    [Fact]
    public void EmitProtocolConformance_GeneratesMultipleTypealiases()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Key" });
        protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Value" });

        var output = EmitFullConformance(protocolDecl);

        Assert.Contains("public typealias Key = Any", output);
        Assert.Contains("public typealias Value = Any", output);
    }

    [Fact]
    public void EmitProtocolConformance_EmptyMarkerProtocol_EmitsTrivialConformance()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        // Empty marker protocol (no members at all) — gets trivial conformance
        // for existential container creation (e.g., Taggable)

        var output = EmitFullConformance(protocolDecl);

        Assert.Contains("extension EveryProtocol: TestModule.TestProtocol", output);
    }

    [Fact]
    public void EmitProtocolConformance_WithGlobalSignatureConflict_SkipsSecondMethodImplementation()
    {
        var firstProtocol = CreateProtocolWithMethod("FirstProtocol", "conflict");
        var secondProtocol = CreateProtocolWithMethod("SecondProtocol", "conflict");
        var globalSignatures = new HashSet<string>();

        var firstOutput = EmitFullConformance(firstProtocol, globalSignatures);
        var secondOutput = EmitFullConformance(secondProtocol, globalSignatures);

        Assert.Contains("public func conflict()", firstOutput);
        Assert.DoesNotContain("public func conflict()", secondOutput);
        Assert.Contains("extension EveryProtocol: TestModule.SecondProtocol", secondOutput);
    }

    // -- Same-signature method fan-out. When method plans
    //    are supplied, the lex-min protocol OWNS the shared body and fans out
    //    across every sibling vtable, instead of the legacy first-seen-wins dedup
    //    above (which left the non-owner's witness routed to the owner's OWN
    //    vtable and crashed when only the non-owner's proxy was populated). --

    [Fact]
    public void ComputeMethodEmissionPlans_SameSignatureAcrossTwoProtocols_PicksLexMinOwnerWithBothSiblings()
    {
        var first = CreateProtocolWithMethod("FirstProtocol", "conflict");
        var second = CreateProtocolWithMethod("SecondProtocol", "conflict");

        var plans = _emitter.ComputeMethodEmissionPlans(new[] { second, first });

        // One group → one plan, referenced by an entry for each protocol.
        var plan = Assert.Single(new HashSet<EveryProtocolEmitter.MethodEmissionPlan>(plans.Values));
        Assert.Equal("FirstProtocol", plan.Owner.Name); // lex-min owner, independent of input order
        Assert.Equal(2, plan.Siblings.Count);
        Assert.Equal("FirstProtocol", plan.Siblings[0].Proto.Name); // owner first
        Assert.Equal("SecondProtocol", plan.Siblings[1].Proto.Name);
    }

    [Fact]
    public void ComputeMethodEmissionPlans_SoloMethod_OwnerIsSelfWithSingleBranch()
    {
        var solo = CreateProtocolWithMethod("SoloProtocol", "only");

        var plans = _emitter.ComputeMethodEmissionPlans(new[] { solo });

        var plan = Assert.Single(plans.Values);
        Assert.Same(solo, plan.Owner);
        Assert.Single(plan.Siblings); // no siblings → single-branch (byte-identical legacy output)
        Assert.False(plan.HasFilteredPeers);
    }

    [Fact]
    public void EmitProtocolConformance_WithMethodPlans_OwnerEmitsFanOutAcrossSiblingVtables()
    {
        var first = CreateProtocolWithMethod("FirstProtocol", "conflict");
        var second = CreateProtocolWithMethod("SecondProtocol", "conflict");
        var plans = _emitter.ComputeMethodEmissionPlans(new[] { first, second });

        var ownerOutput = EmitFullConformanceWithMethodPlans(first, plans);

        Assert.Contains("public func conflict()", ownerOutput);
        // Fan-out shape: a nil-checked branch per sibling vtable + a fatalError tail.
        Assert.Contains("else if", ownerOutput);
        Assert.Contains("no sibling vtable populated for method conflict", ownerOutput);
    }

    [Fact]
    public void EmitProtocolConformance_WithMethodPlans_NonOwnerEmitsEmptyExtension()
    {
        var first = CreateProtocolWithMethod("FirstProtocol", "conflict");
        var second = CreateProtocolWithMethod("SecondProtocol", "conflict");
        var plans = _emitter.ComputeMethodEmissionPlans(new[] { first, second });

        var loserOutput = EmitFullConformanceWithMethodPlans(second, plans);

        // The non-owner conforms via an empty extension; Swift cross-extension
        // resolution routes its requirement into the owner's body.
        Assert.Contains("extension EveryProtocol: TestModule.SecondProtocol", loserOutput);
        Assert.DoesNotContain("public func conflict()", loserOutput);
    }

    [Fact]
    public void EmitProtocolConformance_WithSoloMethodPlan_KeepsSingleBranchBody()
    {
        var solo = CreateProtocolWithMethod("SoloProtocol", "only");
        var plans = _emitter.ComputeMethodEmissionPlans(new[] { solo });

        var output = EmitFullConformanceWithMethodPlans(solo, plans);

        // Zero-regression: a solo method must keep the historic single-branch
        // force-unwrap shape — no fan-out scaffolding.
        Assert.Contains("public func only()", output);
        Assert.DoesNotContain("else if", output);
        Assert.DoesNotContain("no sibling vtable populated", output);
    }

    [Fact]
    public void ComputeMethodEmissionPlans_FieldFilteredPeerLeavesSingleBranch_ForcesSafeFanOut()
    {
        // A sync closure-param method emits a vtable field (IsDispatchableClosureMethod admits it);
        // the SAME selector declared `async` does NOT (every closure-dispatch predicate bails on
        // IsAsync), so EntryEmitsVtableField filters the async peer out of the branch list. The
        // async-insensitive grouping key co-groups the two, so the surviving branch set is a single
        // entry. A C# impl conforming ONLY to the async peer dispatches through the sync sibling's
        // vtable — nil for that instance — so the plan must force the guarded nil-check fan-out
        // (HasFilteredPeers) rather than the bare single-branch force-unwrap, which would SIGSEGV.
        var syncProto = CreateSimpleProtocol("SyncFactory");
        syncProto.Methods.Add(CreateMethodWithClosureParam("applyFactory", "factory"));
        var asyncProto = CreateSimpleProtocol("AsyncFactory");
        var asyncMethod = CreateMethodWithClosureParam("applyFactory", "factory");
        asyncMethod.IsAsync = true;
        asyncProto.Methods.Add(asyncMethod);

        var plans = _emitter.ComputeMethodEmissionPlans(new[] { syncProto, asyncProto });

        var plan = Assert.Single(new HashSet<EveryProtocolEmitter.MethodEmissionPlan>(plans.Values));
        Assert.Equal("SyncFactory", plan.Owner.Name);   // sync, non-throwing wins owner selection
        Assert.Single(plan.Siblings);                   // async peer filtered out of the branch list
        Assert.True(plan.HasFilteredPeers);             // the field-filter drop must force safe fan-out
    }

    [Fact]
    public void EmitProtocolConformance_FieldFilteredPeer_EmitsGuardedFanOutNotForceUnwrap()
    {
        // Emission counterpart of the plan test above: even with a single surviving branch, the
        // dropped async peer forces the nil-check shape — a guarded `if let fn` + fatalError
        // fallback, never the bare `!(` force-unwrap that SIGSEGVs when dispatched through the
        // filtered peer's existential.
        var syncProto = CreateSimpleProtocol("SyncFactory");
        syncProto.Methods.Add(CreateMethodWithClosureParam("applyFactory", "factory"));
        var asyncProto = CreateSimpleProtocol("AsyncFactory");
        var asyncMethod = CreateMethodWithClosureParam("applyFactory", "factory");
        asyncMethod.IsAsync = true;
        asyncProto.Methods.Add(asyncMethod);
        var plans = _emitter.ComputeMethodEmissionPlans(new[] { syncProto, asyncProto });

        var output = EmitFullConformanceWithMethodPlans(syncProto, plans);

        Assert.Contains("if let fn", output);
        Assert.Contains("no sibling vtable populated for closure method applyFactory", output);
    }

    [Fact]
    public void ComputeSiblingMethodFallbacks_SameSignatureGroup_RecordsCrossProtocolSibling()
    {
        var first = CreateProtocolWithMethod("FirstProtocol", "conflict");
        var second = CreateProtocolWithMethod("SecondProtocol", "conflict");

        var fallbacks = _emitter.ComputeSiblingMethodFallbacks(new[] { first, second });

        // The owner's receiver must be able to fall back to the sibling interface.
        var firstKey = (EveryProtocolEmitter.GetProtocolFallbackKey(first),
                        EveryProtocolEmitter.GetMethodSiblingMapKey(first.Methods[0]));
        Assert.True(fallbacks.TryGetValue(firstKey, out var firstSiblings));
        Assert.Single(firstSiblings!);
        Assert.Equal("SecondProtocol", firstSiblings![0].Proto.Name);
    }

    [Fact]
    public void ComputeSiblingMethodFallbacks_ValueAndInoutSameSignature_AreNotSiblings()
    {
        // A value-param method and an otherwise-identical inout-param method project to DIFFERENT C#
        // members (`T arg` vs `ref T arg`), so they are NOT siblings. The grouping key's renderer
        // (GetSwiftTypeName) drops the `inout` annotation, so without the inout discriminator the two
        // collapse into one group — and a value receiver would then fall back into the inout sibling's
        // `F(ref T)` member (CS1620), or the real-async fan-out would mis-dispatch its widened slot.
        // They must land in SEPARATE groups, so neither records a cross-protocol sibling.
        var valueProto = CreateProtocolWithRealAsyncMethod("ValueProto", "compute");
        var inoutProto = CreateProtocolWithInoutAsyncMethod("InoutProto", "compute");

        var fallbacks = _emitter.ComputeSiblingMethodFallbacks(new[] { valueProto, inoutProto });

        var valueKey = (EveryProtocolEmitter.GetProtocolFallbackKey(valueProto),
                        EveryProtocolEmitter.GetMethodSiblingMapKey(valueProto.Methods[0]));
        var inoutKey = (EveryProtocolEmitter.GetProtocolFallbackKey(inoutProto),
                        EveryProtocolEmitter.GetMethodSiblingMapKey(inoutProto.Methods[0]));
        Assert.False(fallbacks.ContainsKey(valueKey),
            "a value-param method must not list an inout-param method as a sibling");
        Assert.False(fallbacks.ContainsKey(inoutKey),
            "an inout-param method must not list a value-param method as a sibling");
    }

    [Fact]
    public void ComputeSiblingMethodFallbacks_TwoValueParamMethods_RemainSiblings()
    {
        // Control: the inout discriminator must NOT over-split — two genuinely same-shape value-param
        // methods across two protocols still form one sibling group (an all-value group has an
        // identical inout shape across members, so its grouping key is unchanged by the discriminator).
        var first = CreateProtocolWithRealAsyncMethod("FirstValueProto", "compute");
        var second = CreateProtocolWithRealAsyncMethod("SecondValueProto", "compute");

        var fallbacks = _emitter.ComputeSiblingMethodFallbacks(new[] { first, second });

        var firstKey = (EveryProtocolEmitter.GetProtocolFallbackKey(first),
                        EveryProtocolEmitter.GetMethodSiblingMapKey(first.Methods[0]));
        Assert.True(fallbacks.TryGetValue(firstKey, out var firstSiblings));
        Assert.Single(firstSiblings!);
        Assert.Equal("SecondValueProto", firstSiblings![0].Proto.Name);
    }

    [Fact]
    public void GetMethodSiblingMapKey_IsDeterministicAndNameDiscriminating()
    {
        var describe = CreateMethodDecl("describe");
        var other = CreateMethodDecl("other");

        // Deterministic: same method shape → identical key (so emitter and receiver agree).
        Assert.Equal(
            EveryProtocolEmitter.GetMethodSiblingMapKey(describe),
            EveryProtocolEmitter.GetMethodSiblingMapKey(CreateMethodDecl("describe")));
        // Name-discriminating: distinct method names → distinct keys.
        Assert.NotEqual(
            EveryProtocolEmitter.GetMethodSiblingMapKey(describe),
            EveryProtocolEmitter.GetMethodSiblingMapKey(other));
        Assert.StartsWith("describe(", EveryProtocolEmitter.GetMethodSiblingMapKey(describe));
    }

    #region Cross-carrier same-signature partitioning

    // A plain Swift protocol routes its EveryProtocol conformance through the plain `EveryProtocol`
    // carrier; an @objc/NSObjectProtocol protocol routes through the NSObject-rooted
    // `EveryObjCProtocol` carrier. When both declare the SAME member signature, Swift's
    // cross-extension witness resolution cannot satisfy a conformance on one concrete carrier from a
    // witness body emitted on the other — so each carrier must own (emit) its own witness. A
    // carrier-blind single-owner plan emits the body on one carrier only and leaves the other
    // carrier's `extension ...: P {}` empty and unsatisfiable, which swiftc rejects ("type
    // 'EveryObjCProtocol' does not conform to protocol ...") and fails wrapper compilation. These
    // tests pin the per-carrier partition for methods, properties, and the sibling-fallback maps.

    [Fact]
    public void ComputeMethodEmissionPlans_SameSignatureAcrossCarriers_EachCarrierOwnsItsOwnWitness()
    {
        var plain = CreateProtocolWithMethod("PlainGreeter", "greet");
        var objc = CreateObjCProtocolWithMethod("ObjCGreeter", "greet");
        _emitter.PreScanProtocols(new[] { plain, objc });

        var plans = _emitter.ComputeMethodEmissionPlans(new[] { plain, objc });

        // Two distinct plans — one per carrier — not a single shared owner.
        Assert.Equal(2, new HashSet<EveryProtocolEmitter.MethodEmissionPlan>(plans.Values).Count);
        Assert.Same(plain, PlanFor(plans, plain).Owner);
        Assert.Same(objc, PlanFor(plans, objc).Owner);
    }

    [Fact]
    public void ComputeMethodEmissionPlans_SameSignatureSameCarrier_StillSharesOneOwner()
    {
        // Guard against over-partitioning: two plain protocols (same carrier) sharing a signature
        // must still collapse to ONE owner with both as siblings — the legacy same-carrier dedup.
        var first = CreateProtocolWithMethod("AlphaGreeter", "greet");
        var second = CreateProtocolWithMethod("BetaGreeter", "greet");
        _emitter.PreScanProtocols(new[] { first, second });

        var plans = _emitter.ComputeMethodEmissionPlans(new[] { first, second });

        var plan = Assert.Single(new HashSet<EveryProtocolEmitter.MethodEmissionPlan>(plans.Values));
        Assert.Equal("AlphaGreeter", plan.Owner.Name); // lex-min owner within the shared carrier
        Assert.Equal(2, plan.Siblings.Count);
    }

    [Fact]
    public void EmitProtocolConformance_SameSignatureAcrossCarriers_BothCarriersEmitSatisfyingWitness()
    {
        var plain = CreateProtocolWithMethod("PlainGreeter", "greet");
        var objc = CreateObjCProtocolWithMethod("ObjCGreeter", "greet");
        _emitter.PreScanProtocols(new[] { plain, objc });
        var plans = _emitter.ComputeMethodEmissionPlans(new[] { plain, objc });

        var plainOutput = EmitFullConformanceWithMethodPlans(plain, plans);
        var objcOutput = EmitFullConformanceWithMethodPlans(objc, plans);

        // Each carrier emits its OWN witness body; neither is an empty, unsatisfiable extension
        // relying on a cross-carrier witness that cannot exist. Pre-fix, the lex-min owner
        // (ObjCGreeter, EveryObjCProtocol) owned the sole body and the plain protocol's
        // EveryProtocol extension emitted empty — so plainOutput lacked the witness.
        Assert.Contains("extension EveryProtocol: TestModule.PlainGreeter", plainOutput);
        Assert.Contains("public func greet()", plainOutput);
        Assert.Contains("extension EveryObjCProtocol: TestModule.ObjCGreeter", objcOutput);
        Assert.Contains("public func greet()", objcOutput);
    }

    [Fact]
    public void ComputePropertyEmissionPlans_SameSignatureAcrossCarriers_EachCarrierOwnsItsOwnProperty()
    {
        var plain = CreateProtocolWithProperty("PlainHolder", "value", hasGetter: true, hasSetter: false);
        var objc = CreateObjCProtocolWithProperty("ObjCHolder", "value", hasGetter: true, hasSetter: false);
        _emitter.PreScanProtocols(new[] { plain, objc });

        var plans = _emitter.ComputePropertyEmissionPlans(new[] { plain, objc });

        Assert.Equal(2, new HashSet<EveryProtocolEmitter.PropertyEmissionPlan>(plans.Values).Count);
        // Keys are carrier-prefixed: the shared property name appears once per carrier, each owned
        // by its own declaring protocol.
        var plainKey = $"EveryProtocol\u0001value|Swift.Int";
        var objcKey = $"EveryObjCProtocol\u0001value|Swift.Int";
        Assert.True(plans.ContainsKey(plainKey));
        Assert.True(plans.ContainsKey(objcKey));
        Assert.Same(plain, plans[plainKey].Owner);
        Assert.Same(objc, plans[objcKey].Owner);
    }

    [Fact]
    public void ComputeSiblingMethodFallbacks_SameSignatureAcrossCarriers_AreNotSiblings()
    {
        var plain = CreateProtocolWithMethod("PlainGreeter", "greet");
        var objc = CreateObjCProtocolWithMethod("ObjCGreeter", "greet");
        _emitter.PreScanProtocols(new[] { plain, objc });

        var fallbacks = _emitter.ComputeSiblingMethodFallbacks(new[] { plain, objc });

        // Cross-carrier protocols are NOT mutual fallback siblings: each carrier's witness fan-out
        // only reaches same-carrier receivers, so each group is solo (count < 2) and no fallback
        // entry is recorded. Pre-fix they shared one group and each got a sibling entry.
        Assert.Empty(fallbacks);
    }

    [Fact]
    public void ComputeSiblingPropertyFallbacks_SameSignatureAcrossCarriers_AreNotSiblings()
    {
        var plain = CreateProtocolWithProperty("PlainHolder", "value", hasGetter: true, hasSetter: false);
        var objc = CreateObjCProtocolWithProperty("ObjCHolder", "value", hasGetter: true, hasSetter: false);
        _emitter.PreScanProtocols(new[] { plain, objc });

        var fallbacks = _emitter.ComputeSiblingPropertyFallbacks(new[] { plain, objc });

        Assert.Empty(fallbacks);
    }

    [Fact]
    public void ComputeSubscriptEmissionPlans_SameSignatureAcrossCarriers_EachCarrierOwnsItsOwnSubscript()
    {
        var plain = CreateProtocolWithSubscript("PlainIndexable");
        var objc = CreateObjCProtocolWithSubscript("ObjCIndexable");
        _emitter.PreScanProtocols(new[] { plain, objc });

        var plans = _emitter.ComputeSubscriptEmissionPlans(new[] { plain, objc });

        // Two distinct plans — one per carrier — not a single shared owner. Pre-fix the carrier-blind
        // group key collapsed both into one plan owned by the lex-min protocol, leaving the other
        // carrier's subscript extension empty and unsatisfiable.
        Assert.Equal(2, new HashSet<EveryProtocolEmitter.SubscriptEmissionPlan>(plans.Values).Count);
        Assert.Same(plain, SubscriptPlanFor(plans, plain).Owner);
        Assert.Same(objc, SubscriptPlanFor(plans, objc).Owner);
    }

    [Fact]
    public void ComputeSiblingSubscriptFallbacks_SameSignatureAcrossCarriers_AreNotSiblings()
    {
        var plain = CreateProtocolWithSubscript("PlainIndexable");
        var objc = CreateObjCProtocolWithSubscript("ObjCIndexable");
        _emitter.PreScanProtocols(new[] { plain, objc });

        var fallbacks = _emitter.ComputeSiblingSubscriptFallbacks(new[] { plain, objc });

        // Each carrier's subscript group is solo, so no mutual fallback siblings are recorded.
        // Pre-fix they shared one group and each got a sibling entry.
        Assert.Empty(fallbacks);
    }

    #region Cross-carrier inherited-requirement suppression (refined @objc protocol)

    [Fact]
    public void CrossCarrierInheritedRequirement_ObjCChildRefiningPlainObjCParent_ChildSuppressed()
    {
        // Shape: `@objc protocol Parent { func validateThing() }` — no NSObjectProtocol, so it
        // routes to the plain EveryProtocol carrier — and `@objc protocol Child : Parent,
        // NSObjectProtocol { var flag: Int { get } }` — lists NSObjectProtocol, so it routes to the
        // NSObject-rooted EveryObjCProtocol carrier. Swift then requires EveryObjCProtocol to also
        // satisfy Parent, but Parent's witness body was emitted on EveryProtocol, so
        // `extension EveryObjCProtocol: Child` fails to compile ("type 'EveryObjCProtocol' does not
        // conform to protocol 'Parent'"). The child conformance must be suppressed fail-closed while
        // the parent's own plain-carrier conformance is left intact.
        var parent = CreateProtocolWithMethod("CarrierParent", "validateThing");
        var child = CreateProtocolWithProperty("CarrierChild", "flag", hasGetter: true, hasSetter: false);
        child.InheritedProtocols.Add(new NamedTypeSpec("TestModule.CarrierParent"));
        child.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));

        _emitter.PreScanProtocols(new[] { parent, child });

        // Pre-scan seeds the skip so IsConformanceSkipped removes the child from the sibling-plan
        // owner input (and Pass-2 genericSig propagation sees it); the parent is unaffected.
        Assert.True(_emitter.IsConformanceSkipped(child));
        Assert.False(_emitter.IsConformanceSkipped(parent));

        // Emission ladder suppresses the wrapper conformance on either carrier.
        var childOutput = EmitFullConformance(child);
        Assert.DoesNotContain("extension EveryObjCProtocol: TestModule.CarrierChild", childOutput);
        Assert.DoesNotContain("extension EveryProtocol: TestModule.CarrierChild", childOutput);

        // The parent still gets its plain-carrier conformance — the fix is surgical.
        Assert.Contains("extension EveryProtocol: TestModule.CarrierParent", EmitFullConformance(parent));
    }

    [Fact]
    public void CrossCarrierInheritedRequirement_ConsistentObjCChain_NotSuppressed()
    {
        // Both parent and child list NSObjectProtocol → both route to EveryObjCProtocol. Same carrier
        // on both, so the child's conformance is satisfiable and must NOT be over-suppressed.
        var parent = CreateObjCProtocolWithMethod("ObjCChainParent", "validateThing");
        var child = CreateObjCProtocolWithProperty("ObjCChainChild", "flag", hasGetter: true, hasSetter: false);
        child.InheritedProtocols.Add(new NamedTypeSpec("TestModule.ObjCChainParent"));

        _emitter.PreScanProtocols(new[] { parent, child });

        Assert.False(_emitter.IsConformanceSkipped(child));
        Assert.False(_emitter.IsConformanceSkipped(parent));
    }

    [Fact]
    public void CrossCarrierInheritedRequirement_PlainRefinement_NotSuppressed()
    {
        // Both parent and child are plain Swift protocols → both route to EveryProtocol. No carrier
        // split, so the gate leaves the common refinement case untouched.
        var parent = CreateProtocolWithMethod("PlainChainParent", "validateThing");
        var child = CreateProtocolWithProperty("PlainChainChild", "flag", hasGetter: true, hasSetter: false);
        child.InheritedProtocols.Add(new NamedTypeSpec("TestModule.PlainChainParent"));

        _emitter.PreScanProtocols(new[] { parent, child });

        Assert.False(_emitter.IsConformanceSkipped(child));
        Assert.False(_emitter.IsConformanceSkipped(parent));
        Assert.Contains("extension EveryProtocol: TestModule.PlainChainChild", EmitFullConformance(child));
    }

    [Fact]
    public void CrossCarrierInheritedRequirement_CrossModuleParentRefinedByObjCChild_ChildSuppressed()
    {
        // Cross-module variant: the parent lives in a --framework-dependency module (DepModule), so it
        // is supplied via the crossModuleParents argument, NOT the module-local list. The parent is a
        // plain protocol → routes to EveryProtocol; the local child refines it AND NSObjectProtocol →
        // routes to EveryObjCProtocol. Same carrier split as the same-module case, but the parent is
        // only resolvable through the cross-module list. The gate must resolve it there and suppress
        // the child fail-closed — otherwise the local wrapper emits an unsatisfiable
        // `extension EveryObjCProtocol: DepChild`.
        var parent = CreateCrossModuleParentWithMethod("DepModule", "DepCarrierParent", "validateThing");
        var child = CreateProtocolWithProperty("DepCarrierChild", "flag", hasGetter: true, hasSetter: false);
        child.InheritedProtocols.Add(new NamedTypeSpec("DepModule.DepCarrierParent"));
        child.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));

        _emitter.PreScanProtocols(new[] { child }, new[] { parent });

        Assert.True(_emitter.IsConformanceSkipped(child));
        // Emission ladder suppresses the wrapper conformance on either carrier.
        var childOutput = EmitFullConformance(child);
        Assert.DoesNotContain("extension EveryObjCProtocol: TestModule.DepCarrierChild", childOutput);
        Assert.DoesNotContain("extension EveryProtocol: TestModule.DepCarrierChild", childOutput);
    }

    [Fact]
    public void CrossCarrierInheritedRequirement_CrossModuleConsistentObjCChain_NotSuppressed()
    {
        // The cross-module parent ALSO lists NSObjectProtocol → routes to EveryObjCProtocol, same as the
        // child. No carrier split, so the child must NOT be over-suppressed even though the parent is
        // cross-module.
        var parent = CreateCrossModuleParentWithMethod("DepModule", "DepObjCParent", "validateThing");
        parent.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));
        var child = CreateProtocolWithProperty("DepObjCChild", "flag", hasGetter: true, hasSetter: false);
        child.InheritedProtocols.Add(new NamedTypeSpec("DepModule.DepObjCParent"));
        child.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));

        _emitter.PreScanProtocols(new[] { child }, new[] { parent });

        Assert.False(_emitter.IsConformanceSkipped(child));
    }

    [Fact]
    public void CrossCarrierInheritedRequirement_CrossModuleParentNotSupplied_SplitNotDetected()
    {
        // Guards the load-bearing crossModuleParents argument: without it the gate cannot resolve the
        // cross-module parent's carrier, so the split is silently missed and the child is NOT suppressed.
        // ModuleHandler MUST pass the parents (which it does) for the cross-module split to be caught —
        // this asserts the mechanism is the parent list, not incidental behaviour.
        var child = CreateProtocolWithProperty("UnresolvedParentChild", "flag", hasGetter: true, hasSetter: false);
        child.InheritedProtocols.Add(new NamedTypeSpec("DepModule.DepCarrierParent"));
        child.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));

        // No crossModuleParents supplied — the dep parent is unresolvable.
        _emitter.PreScanProtocols(new[] { child });

        Assert.False(_emitter.IsConformanceSkipped(child));
    }

    [Fact]
    public void CrossCarrierInheritedRequirement_SameSimpleNameCrossModuleParents_NoFalseSplitOnObjCNamesake()
    {
        // Two --framework-dependency modules each export a protocol with the SAME simple name
        // "Delegate": DepA.Delegate is plain (→ EveryProtocol) and DepB.Delegate lists
        // NSObjectProtocol (→ EveryObjCProtocol). The local child refines the ObjC one
        // (DepB.Delegate) AND NSObjectProtocol, so it routes to EveryObjCProtocol — the SAME
        // carrier as its true parent → NO split → it must NOT be suppressed. Resolving the
        // inherited "DepB.Delegate" by bare simple name binds the FIRST-seen namesake
        // (DepA.Delegate, plain → EveryProtocol), fabricates a carrier split, and over-suppresses
        // a child that compiles fine. The resolver must disambiguate by module-qualified name.
        var plainNamesake = CreateCrossModuleParentWithMethod("DepA", "Delegate", "doA");
        var objcNamesake = CreateCrossModuleParentWithMethod("DepB", "Delegate", "doB");
        objcNamesake.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));

        var child = CreateProtocolWithProperty("SameNameDelegateChild", "flag", hasGetter: true, hasSetter: false);
        child.InheritedProtocols.Add(new NamedTypeSpec("DepB.Delegate"));
        child.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));

        // plainNamesake FIRST so a bare simple-name FirstOrDefault resolves "DepB.Delegate" to it.
        _emitter.PreScanProtocols(new[] { child }, new[] { plainNamesake, objcNamesake });

        Assert.False(_emitter.IsConformanceSkipped(child));
    }

    [Fact]
    public void CrossCarrierInheritedRequirement_SameSimpleNameCrossModuleParents_StillDetectsRealSplit()
    {
        // Same two same-simple-name namesakes, but the ObjC child now refines the PLAIN one
        // (DepA.Delegate → EveryProtocol). That IS a real carrier split (child EveryObjCProtocol,
        // parent EveryProtocol), so the child MUST be suppressed — proving the qualified resolver
        // binds DepA.Delegate specifically and does not simply always latch the ObjC namesake.
        // objcNamesake is listed FIRST so a first-match resolver would pick it and MISS the split.
        var plainNamesake = CreateCrossModuleParentWithMethod("DepA", "Delegate", "doA");
        var objcNamesake = CreateCrossModuleParentWithMethod("DepB", "Delegate", "doB");
        objcNamesake.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));

        var child = CreateProtocolWithProperty("SplitDelegateChild", "flag", hasGetter: true, hasSetter: false);
        child.InheritedProtocols.Add(new NamedTypeSpec("DepA.Delegate"));
        child.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));

        // objcNamesake FIRST so a bare simple-name FirstOrDefault resolves "DepA.Delegate" to it.
        _emitter.PreScanProtocols(new[] { child }, new[] { objcNamesake, plainNamesake });

        Assert.True(_emitter.IsConformanceSkipped(child));
    }

    #endregion

    private static EveryProtocolEmitter.MethodEmissionPlan PlanFor(
        IReadOnlyDictionary<(string ProtoQName, string CarrierAndSignature), EveryProtocolEmitter.MethodEmissionPlan> plans,
        ProtocolDecl proto)
    {
        var qname = EveryProtocolEmitter.GetProtocolFallbackKey(proto);
        return plans.Where(kv => kv.Key.ProtoQName == qname).Select(kv => kv.Value).Distinct().Single();
    }

    private ProtocolDecl CreateObjCProtocolWithMethod(string name, string methodName)
    {
        var protocol = CreateProtocolWithMethod(name, methodName);
        // NSObjectProtocol inheritance routes the conformance through the NSObject-rooted
        // EveryObjCProtocol carrier (see EveryProtocolEmitter.GetCarrierClassName).
        protocol.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));
        return protocol;
    }

    private ProtocolDecl CreateObjCProtocolWithProperty(string name, string propertyName, bool hasGetter, bool hasSetter)
    {
        var protocol = CreateProtocolWithProperty(name, propertyName, hasGetter, hasSetter);
        protocol.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));
        return protocol;
    }

    private static EveryProtocolEmitter.SubscriptEmissionPlan SubscriptPlanFor(
        IReadOnlyDictionary<(string ProtoQName, string SubscriptKey), EveryProtocolEmitter.SubscriptEmissionPlan> plans,
        ProtocolDecl proto)
    {
        var qname = EveryProtocolEmitter.GetProtocolFallbackKey(proto);
        return plans.Where(kv => kv.Key.ProtoQName == qname).Select(kv => kv.Value).Distinct().Single();
    }

    private ProtocolDecl CreateProtocolWithSubscript(string name)
    {
        var protocol = CreateSimpleProtocol(name);
        protocol.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s_test_subscript",
            ReturnTypeSpec = new NamedTypeSpec("Swift.String"),
            IndexParameters = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "index",
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        });
        return protocol;
    }

    private ProtocolDecl CreateObjCProtocolWithSubscript(string name)
    {
        var protocol = CreateProtocolWithSubscript(name);
        // NSObjectProtocol inheritance routes the conformance through the NSObject-rooted
        // EveryObjCProtocol carrier (see EveryProtocolEmitter.GetCarrierClassName).
        protocol.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));
        return protocol;
    }

    #endregion

    #region S13 Pillar C — real-async reverse-dispatch witness

    // -- The classifier oracle EmitsRealAsyncWitness is method-shape-ONLY: it consults nothing but the
    //    method's own signature, so every emission site (Swift `_vtable` field, VtableLayoutBuilder.GetWidth,
    //    the C# local delegate field, the receiver, and this extension's witness body) reaches the IDENTICAL
    //    verdict and cannot drift the slot's width or effect. These boundary rows pin the accept/reject edge. --

    public static IEnumerable<object[]> RealAsyncWitnessCases()
    {
        // Accepts: the narrow supported shape — async instance requirement, blittable-primitive return,
        // blittable-primitive non-inout value params, arity ≤ 4, throwing OR non-throwing.
        yield return Row(RealAsyncEligible(arity: 1), true, "async, Int32 -> Int32, arity 1");
        yield return Row(RealAsyncEligible(arity: 0), true, "async, () -> Int32 (no value params)");
        yield return Row(RealAsyncEligible(arity: 4), true, "async, arity 4 (at the cap)");
        yield return Row(RealAsyncEligible(arity: 1, throws: true), true, "async throws is still real-async");

        // Rejects: every shape that takes a different emit path or escapes the supported corner.
        yield return Row(Mutate(RealAsyncEligible(), m => m.IsAsync = false), false, "not async → legacy sync witness");
        yield return Row(Mutate(RealAsyncEligible(), m => m.IsConstructor = true), false, "constructor");
        yield return Row(Mutate(RealAsyncEligible(), m => m.MethodType = MethodType.Static), false, "static");
        yield return Row(Mutate(RealAsyncEligible(), m => m.IsObjCOptional = true), false, "@objc optional");
        yield return Row(RealAsyncEligible(arity: 5), false, "arity 5 (over the cap)");
        yield return Row(Mutate(RealAsyncEligible(), m => m.CSSignature[1].IsInOut = true), false, "inout value param");
        yield return Row(Mutate(RealAsyncEligible(), m => m.CSSignature[0].SwiftTypeSpec = TupleTypeSpec.Empty),
            false, "Void (empty-tuple) return");
        yield return Row(Mutate(RealAsyncEligible(), m => m.CSSignature[0].SwiftTypeSpec = new NamedTypeSpec("TestModule.Widget")),
            false, "non-primitive return");
        yield return Row(Mutate(RealAsyncEligible(), m => m.CSSignature[1].SwiftTypeSpec = new NamedTypeSpec("TestModule.Widget")),
            false, "non-primitive value param");
        yield return Row(RealAsyncWithClosureParam(), false, "closure param (dedicated closure emit path)");
        yield return Row(Mutate(RealAsyncEligible(), m => m.CSSignature[1].SwiftTypeSpec =
                new ProtocolListTypeSpec(new[] { new NamedTypeSpec("ObjectiveC.NSObjectProtocol") })),
            false, "protocol existential param — no @objc-existential arm needed; the primitive whitelist subsumes it");
    }

    [Theory]
    [MemberData(nameof(RealAsyncWitnessCases))]
    public void EmitsRealAsyncWitness_ClassifiesMethodShapeAtTheBoundary(MethodDecl method, bool expected, string because)
    {
        Assert.Equal(expected, EveryProtocolEmitter.EmitsRealAsyncWitness(method));
        _ = because; // documents the row; asserted via the xUnit display name
    }

    [Fact]
    public void EmitProtocolConformance_RealAsyncSoloMethod_EmitsContinuationHandoffSingleBranch()
    {
        // A solo (no same-signature sibling) NON-throwing real-async requirement emits a genuine
        // continuation handoff — withCheckedContinuation over CheckedContinuation<T, Never> — and keeps the
        // byte-identical single force-unwrap of THIS protocol's own widened slot (no fan-out scaffolding).
        var solo = CreateProtocolWithRealAsyncMethod("AsyncSolo", "compute");
        var plans = _emitter.ComputeMethodEmissionPlans(new[] { solo });

        var output = EmitFullConformanceWithMethodPlans(solo, plans);

        Assert.Contains("public func compute(", output);
        Assert.Contains("async -> Swift.Int32", output);                  // non-throwing effect clause
        Assert.Contains("withCheckedContinuation", output);
        Assert.Contains("CheckedContinuation<Swift.Int32, Never>", output);
        // Widened slot: the trailing continuation box + success/error FPs handed to the C# vtable thunk (+3).
        Assert.Contains("__boxPtr", output);
        Assert.Contains("__successFP", output);
        Assert.Contains("__errorFP", output);
        // Solo → single force-unwrap, never the sibling fan-out.
        Assert.DoesNotContain("else if", output);
        Assert.DoesNotContain("no sibling vtable populated for method compute", output);
    }

    [Fact]
    public void EmitProtocolConformance_RealAsyncThrowingSoloMethod_UsesThrowingContinuation()
    {
        // The throwing variant carries a real Swift error channel: withCheckedThrowingContinuation over
        // CheckedContinuation<T, Error>, and the witness is itself `async throws`. The slot width is
        // unchanged (+3, throwing-agnostic); only the box/continuation variant differs.
        var solo = CreateProtocolWithRealAsyncMethod("AsyncThrowingSolo", "compute", throws: true);
        var plans = _emitter.ComputeMethodEmissionPlans(new[] { solo });

        var output = EmitFullConformanceWithMethodPlans(solo, plans);

        Assert.Contains("async throws -> Swift.Int32", output);
        Assert.Contains("withCheckedThrowingContinuation", output);
        Assert.Contains("CheckedContinuation<Swift.Int32, Swift.Error>", output);
        Assert.DoesNotContain("else if", output);
    }

    [Fact]
    public void EmitProtocolConformance_RealAsyncSiblingGroup_OwnerFansOutAcrossSiblingWidenedSlots()
    {
        // The Codex-High regression: two class-bound protocols declare the SAME real-async signature, so
        // they form one owner group whose witness is a continuation handoff. The lex-min owner's body must
        // FAN OUT across both siblings' widened vtable slots (dispatch through the first non-nil one,
        // fatalError if none) — exactly the sync witness's fan-out, on the +3 widened slot — so a C# impl
        // conforming to only the non-owner peer lands on ITS populated slot instead of the owner's nil
        // global vtable (which the solo force-unwrap would SIGSEGV on).
        var owner = CreateProtocolWithRealAsyncMethod("AsyncFanOwner", "compute");
        var peer = CreateProtocolWithRealAsyncMethod("AsyncFanPeer", "compute");
        var plans = _emitter.ComputeMethodEmissionPlans(new[] { owner, peer });

        // Owner selection is lex-min and order-independent, and the group carries both siblings.
        var plan = Assert.Single(new HashSet<EveryProtocolEmitter.MethodEmissionPlan>(plans.Values));
        Assert.Equal("AsyncFanOwner", plan.Owner.Name);
        Assert.Equal(2, plan.Siblings.Count);

        var ownerOutput = EmitFullConformanceWithMethodPlans(owner, plans);

        // Still a real continuation handoff...
        Assert.Contains("withCheckedContinuation", ownerOutput);
        // ...but now a nil-checked branch per sibling widened slot + a fatalError tail.
        Assert.Contains("else if", ownerOutput);
        Assert.Contains("let fn = ", ownerOutput);
        Assert.Contains("__boxPtr, __successFP, __errorFP", ownerOutput); // +3 widened dispatch per branch
        Assert.Contains("no sibling vtable populated for method compute", ownerOutput);

        // The non-owner peer conforms via an empty extension; Swift cross-extension resolution routes its
        // requirement into the owner's fanned-out body.
        var peerOutput = EmitFullConformanceWithMethodPlans(peer, plans);
        Assert.Contains("extension EveryProtocol: TestModule.AsyncFanPeer", peerOutput);
        Assert.DoesNotContain("public func compute(", peerOutput);
    }

    #endregion

    [Fact]
    public void EmitProtocolConformance_SkipsProtocolWithConstructorRequirements()
    {
        var protocolDecl = CreateSimpleProtocol("MixedProtocol");
        protocolDecl.Methods.Add(CreateMethodDecl("instanceMethod"));
        protocolDecl.Methods.Add(CreateMethodDecl("utility", methodType: MethodType.Static));
        protocolDecl.Methods.Add(CreateMethodDecl("init", isConstructor: true));

        var output = EmitFullConformance(protocolDecl);

        // Entire conformance skipped because protocol has constructor requirements
        Assert.Empty(output);
    }

    [Fact]
    public void EmitProtocolExtension_WithProtocolCompositionProperty_UsesAnyCompositionSyntax()
    {
        var protocolDecl = CreateSimpleProtocol("Composable");
        protocolDecl.Properties.Add(new PropertyDecl
        {
            Name = "delegate",
            SwiftTypeSpec = new ProtocolListTypeSpec(new[]
            {
                new NamedTypeSpec("TestModule.P1"),
                new NamedTypeSpec("TestModule.P2")
            }),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("delegate_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public var delegate: any TestModule.P1 & TestModule.P2", output);
    }

    [Fact]
    public void EmitProtocolExtension_WithGenericTypeParameter_EmitsSelfTypedStub()
    {
        // "T" is treated as protocol-level generic param (Self) by TypeSpecHelpers.
        // Self-typed properties get fatalError() stubs with EveryProtocol substitution.
        var protocolDecl = CreateSimpleProtocol("GenericLike");
        protocolDecl.Properties.Add(new PropertyDecl
        {
            Name = "item",
            SwiftTypeSpec = new NamedTypeSpec("T"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("item_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public var item: EveryProtocol", output);
        Assert.Contains("Self-typed property 'item'", output);
    }

    [Fact]
    public void EmitProtocolExtension_WithEscapingAsyncThrowingClosure_FormatsClosureType()
    {
        var protocolDecl = CreateSimpleProtocol("ClosureLike");
        var closure = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: new NamedTypeSpec("Swift.String"));
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));
        closure.IsAsync = true;
        closure.Throws = true;

        protocolDecl.Properties.Add(new PropertyDecl
        {
            Name = "callback",
            SwiftTypeSpec = closure,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("callback_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProtocolExtension(protocolDecl);

        // @escaping is excluded from property type declarations (only valid on function parameters).
        // Other attributes like @MainActor, @Sendable would be preserved.
        Assert.Contains("public var callback: (Swift.Int) async throws -> Swift.String", output);
    }

    [Fact]
    public void EmitProtocolExtension_ThrowingMethod_EmitsThrowsKeyword()
    {
        var protocolDecl = CreateSimpleProtocol("Throwable");
        protocolDecl.Methods.Add(CreateMethodDecl("doWork", throws: true));

        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public func doWork() throws", output);
    }

    [Fact]
    public void EmitProtocolExtension_ThrowingMethodWithReturn_EmitsThrowsBeforeArrow()
    {
        var protocolDecl = CreateSimpleProtocol("Throwable");
        var method = CreateMethodDecl("makeItem", throws: true);
        // Replace return type with a non-void type
        method.CSSignature[0] = new ArgumentDecl
        {
            Name = "",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
        protocolDecl.Methods.Add(method);

        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public func makeItem() throws -> Swift.Int", output);
    }

    [Fact]
    public void EmitProtocolExtension_NonThrowingMethod_NoThrowsKeyword()
    {
        var protocolDecl = CreateProtocolWithMethod("NonThrowing", "doWork");

        var output = EmitProtocolExtension(protocolDecl);

        Assert.Contains("public func doWork()", output);
        Assert.DoesNotContain("throws", output);
    }

    #endregion

    #region Witness Table Getter Tests

    [Fact]
    public void EmitWitnessTableGetter_GeneratesCdeclExport()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitWitnessTableGetter(protocolDecl);

        // @_cdecl (not @_silgen_name): nothing in the Swift wrapper references this getter — it is
        // reached only from C# via P/Invoke (CallConvCdecl). As a C-exported entry point it becomes
        // a linker root and survives dead-stripping on NativeAOT/device builds; an unreferenced
        // @_silgen_name free function is dropped there.
        Assert.Contains("@_cdecl(\"Get_EveryProtocol_TestProtocol_WitnessTable\")", output);
    }

    [Fact]
    public void EmitWitnessTableGetter_GeneratesPublicFunction()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitWitnessTableGetter(protocolDecl);

        Assert.Contains("public func getEveryProtocolTestProtocolWitnessTable() -> UnsafeRawPointer", output);
    }

    [Fact]
    public void EmitWitnessTableGetter_UsesCorrectProtocolName()
    {
        var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
        var output = EmitWitnessTableGetter(protocolDecl);

        Assert.Contains("any TestModule.TestProtocol = instance", output);
    }

    [Fact]
    public void EmitTypeMetadataGetter_GeneratesCdeclExport()
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitTypeMetadataGetter(writer);
        var output = stringWriter.ToString();

        // @_cdecl (not @_silgen_name) for the same dead-strip-survival reason as the witness getters.
        Assert.Contains("@_cdecl(\"Get_EveryProtocol_TypeMetadata\")", output);
    }

    [Fact]
    public void EmitTypeMetadataGetter_ReturnsUnsafeRawPointer()
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitTypeMetadataGetter(writer);
        var output = stringWriter.ToString();

        Assert.Contains("public func getEveryProtocolTypeMetadata() -> UnsafeRawPointer", output);
    }

    #endregion

    #region Vtable Index Integrity Tests (Bug #21)

    [Fact]
    public void EmitProtocolConformance_WithGlobalSkip_VtableFieldIndicesMatchExtensionReferences()
    {
        // Protocol has 3 methods: start, update, finish
        // "update" is globally skipped (already emitted by another protocol)
        // Vtable declares: func_start_0, func_update_1, func_finish_2
        // Extension must reference func_finish_2 (not func_finish_1)
        var firstProtocol = CreateProtocolWithMethod("FirstProtocol", "update");
        var secondProtocol = CreateSimpleProtocol("SecondProtocol");
        secondProtocol.Methods.Add(CreateMethodDecl("start"));
        secondProtocol.Methods.Add(CreateMethodDecl("update"));
        secondProtocol.Methods.Add(CreateMethodDecl("finish"));

        var globalSignatures = new HashSet<string>();

        // First protocol claims "update()"
        EmitFullConformance(firstProtocol, globalSignatures);

        // Second protocol emits with global dedup — "update" skipped
        var vtableOutput = EmitVtableStruct(secondProtocol);
        var extensionOutput = EmitFullConformance(secondProtocol, globalSignatures);

        // Vtable struct must declare all 3 fields sequentially
        Assert.Contains("func_start_0", vtableOutput);
        Assert.Contains("func_update_1", vtableOutput);
        Assert.Contains("func_finish_2", vtableOutput);

        // Extension must use matching indices for emitted methods
        Assert.Contains("func_start_0", extensionOutput);
        Assert.DoesNotContain("public func update()", extensionOutput); // globally skipped
        Assert.Contains("func_finish_2", extensionOutput);
        // Must NOT have func_finish_1 (the drifted index from the old bug)
        Assert.DoesNotContain("func_finish_1", extensionOutput);
    }

    [Fact]
    public void EmitProtocolConformance_WithMultipleGlobalSkips_IndicesStillMatch()
    {
        // Protocol has 4 methods: a, b, c, d
        // "b" and "c" are globally skipped
        // Vtable declares: func_a_0, func_b_1, func_c_2, func_d_3
        // Extension must reference func_d_3 (not func_d_1)
        var proto1 = CreateProtocolWithMethod("Proto1", "b");
        var proto2 = CreateProtocolWithMethod("Proto2", "c");
        var proto3 = CreateSimpleProtocol("Proto3");
        proto3.Methods.Add(CreateMethodDecl("a"));
        proto3.Methods.Add(CreateMethodDecl("b"));
        proto3.Methods.Add(CreateMethodDecl("c"));
        proto3.Methods.Add(CreateMethodDecl("d"));

        var globalSignatures = new HashSet<string>();
        EmitFullConformance(proto1, globalSignatures);
        EmitFullConformance(proto2, globalSignatures);

        var vtableOutput = EmitVtableStruct(proto3);
        var extensionOutput = EmitFullConformance(proto3, globalSignatures);

        // Vtable declares all 4 fields
        Assert.Contains("func_a_0", vtableOutput);
        Assert.Contains("func_b_1", vtableOutput);
        Assert.Contains("func_c_2", vtableOutput);
        Assert.Contains("func_d_3", vtableOutput);

        // Extension emits a and d, skips b and c
        Assert.Contains("func_a_0", extensionOutput);
        Assert.DoesNotContain("public func b()", extensionOutput);
        Assert.DoesNotContain("public func c()", extensionOutput);
        Assert.Contains("func_d_3", extensionOutput);
        Assert.DoesNotContain("func_d_1", extensionOutput);
        Assert.DoesNotContain("func_d_2", extensionOutput);
    }

    [Fact]
    public void EmitProtocolConformance_NoGlobalSkips_IndicesSequential()
    {
        // Baseline: without any global conflicts, indices are simply sequential
        var protocol = CreateSimpleProtocol("Sequential");
        protocol.Methods.Add(CreateMethodDecl("alpha"));
        protocol.Methods.Add(CreateMethodDecl("beta"));
        protocol.Methods.Add(CreateMethodDecl("gamma"));

        var vtableOutput = EmitVtableStruct(protocol);
        var extensionOutput = EmitFullConformance(protocol);

        Assert.Contains("func_alpha_0", vtableOutput);
        Assert.Contains("func_beta_1", vtableOutput);
        Assert.Contains("func_gamma_2", vtableOutput);

        Assert.Contains("func_alpha_0", extensionOutput);
        Assert.Contains("func_beta_1", extensionOutput);
        Assert.Contains("func_gamma_2", extensionOutput);
    }

    [Fact]
    public void EmitProtocolConformance_ThrowingFirstThenNonThrowing_EmitsNonThrowingWithOverride()
    {
        // Protocol A has "process()" as throwing, Protocol B has it as non-throwing.
        // In Swift, a non-throwing func satisfies both requirements, but a throwing func
        // does NOT satisfy a non-throwing requirement. The non-throwing variant must win.
        var throwingProtocol = CreateSimpleProtocol("ThrowingProto");
        throwingProtocol.Methods.Add(CreateMethodDecl("process", throws: true));

        var nonThrowingProtocol = CreateSimpleProtocol("NonThrowingProto");
        nonThrowingProtocol.Methods.Add(CreateMethodDecl("process", throws: false));

        // Build the non-throwing overrides set (simulates what ModuleHandler.ComputeNonThrowingOverrides does)
        // Uses full signature format: name(params)->ReturnType
        var nonThrowingOverrides = new HashSet<string> { "process()->Void" };
        var globalSignatures = new HashSet<string>();

        // Throwing protocol emitted first — but the override forces non-throwing
        var throwingOutput = EmitFullConformance(throwingProtocol, globalSignatures, nonThrowingOverrides);

        // The emitted method should NOT have "throws" because the override suppresses it
        Assert.Contains("public func process()", throwingOutput);
        Assert.DoesNotContain("throws", throwingOutput);
    }

    [Fact]
    public void EmitProtocolConformance_NonThrowingOnlySignature_NoOverrideNeeded()
    {
        // When a method is only non-throwing (no conflict), it should emit normally
        var protocol = CreateSimpleProtocol("SimpleProto");
        protocol.Methods.Add(CreateMethodDecl("process", throws: false));

        var nonThrowingOverrides = new HashSet<string>(); // empty — no conflicts
        var globalSignatures = new HashSet<string>();

        var output = EmitFullConformance(protocol, globalSignatures, nonThrowingOverrides);

        Assert.Contains("public func process()", output);
        Assert.DoesNotContain("throws", output);
    }

    [Fact]
    public void EmitProtocolConformance_ThrowingOnlySignature_EmitsThrowsNormally()
    {
        // When a method is only throwing (no conflict), it should emit with throws
        var protocol = CreateSimpleProtocol("ThrowingOnly");
        protocol.Methods.Add(CreateMethodDecl("process", throws: true));

        var nonThrowingOverrides = new HashSet<string>(); // empty — no conflicts
        var globalSignatures = new HashSet<string>();

        var output = EmitFullConformance(protocol, globalSignatures, nonThrowingOverrides);

        Assert.Contains("public func process() throws", output);
    }

    #endregion

    #region Underscore Parameter Name Tests (Issue I)

    [Fact]
    public void EmitProtocolExtension_UnderscorePrivateName_DoesNotReferenceUnderscore()
    {
        // When a protocol method has _ as the internal parameter name (PrivateName),
        // the emitter should NOT generate "var _Copy = _" (which is illegal in Swift).
        // Instead, it should fall back to using the public Name as the internal name.
        var protocol = CreateSimpleProtocol("ImageProvider");
        var method = CreateMethodDecl("contentsGravity", MethodType.Instance, false, false);
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "for",
            PrivateName = "_",
            SwiftTypeSpec = new NamedTypeSpec("TestModule.ImageAsset"),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(method);

        var output = EmitProtocolExtension(protocol);

        // Should NOT contain "var _Copy = _" (the broken pattern)
        Assert.DoesNotContain("var _Copy = _", output);
        // Should NOT try to use _ as a value
        Assert.DoesNotContain("= _\n", output);
    }

    [Fact]
    public void EmitProtocolExtension_BothNamesUnderscore_UsesSyntheticName()
    {
        // When both Name and PrivateName are _, use synthetic arg0
        var protocol = CreateSimpleProtocol("ImageProvider");
        var method = CreateMethodDecl("contentsGravity", MethodType.Instance, false, false);
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "_",
            PrivateName = "_",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(method);

        var output = EmitProtocolExtension(protocol);

        // Should use synthetic name, not _
        Assert.DoesNotContain("var _Copy", output);
        // Index starts at 1 (skipping return type at index 0)
        Assert.Contains("arg1", output);
    }

    [Fact]
    public void EmitProtocolExtension_NormalParamName_PreservesName()
    {
        // Normal parameter names should still work as before
        var protocol = CreateSimpleProtocol("ImageProvider");
        var method = CreateMethodDecl("contentsGravity", MethodType.Instance, false, false);
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "for",
            PrivateName = "asset",
            SwiftTypeSpec = new NamedTypeSpec("TestModule.ImageAsset"),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(method);

        var output = EmitProtocolExtension(protocol);

        Assert.Contains("asset", output);
    }

    #endregion

    #region Optional Closure Property Tests (EC-15)

    [Fact]
    public void EmitProtocolExtension_OptionalClosureProperty_EmitsCorrectType()
    {
        // Protocol with optional closure property: var onDismiss: ((SomeType) -> Void)?
        var protocolDecl = CreateSimpleProtocol("DismissDelegate");
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("TestModule.SomeType") }),
            returnType: TupleTypeSpec.Empty);
        var optionalClosureType = new NamedTypeSpec("Swift.Optional");
        optionalClosureType.GenericParameters.Add(closureType);

        protocolDecl.Properties.Add(new PropertyDecl
        {
            Name = "onDismiss",
            SwiftTypeSpec = optionalClosureType,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("onDismiss_get") },
                new SetAccessorDecl { Method = CreateMethodDecl("onDismiss_set") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProtocolExtension(protocolDecl);

        // Optional closure properties get fatalError() stubs (can't dispatch through vtable)
        Assert.Contains("public var onDismiss:", output);
        Assert.Contains("fatalError", output);
        Assert.Contains("closure property 'onDismiss' cannot be dispatched", output);
    }

    [Fact]
    public void EmitProtocolExtension_MainActorOptionalClosureProperty_PreservesMainActor()
    {
        // Protocol with @MainActor optional closure property:
        // var callback: (@MainActor (SomeType) -> Void)?
        var protocolDecl = CreateSimpleProtocol("CallbackDelegate");
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("TestModule.SomeType") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("MainActor"));
        var optionalClosureType = new NamedTypeSpec("Swift.Optional");
        optionalClosureType.GenericParameters.Add(closureType);

        protocolDecl.Properties.Add(new PropertyDecl
        {
            Name = "callback",
            SwiftTypeSpec = optionalClosureType,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("callback_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProtocolExtension(protocolDecl);

        // @MainActor should be preserved in the property type
        Assert.Contains("@MainActor", output);
        // @escaping should NOT appear
        Assert.DoesNotContain("@escaping", output);
    }

    [Fact]
    public void EmitProtocolExtension_EscapingOptionalClosureProperty_ExcludesEscaping()
    {
        // Protocol with @escaping optional closure property:
        // var handler: (@escaping (Int) -> Void)?
        // @escaping should be stripped since it's invalid on property declarations
        var protocolDecl = CreateSimpleProtocol("HandlerDelegate");
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));
        var optionalClosureType = new NamedTypeSpec("Swift.Optional");
        optionalClosureType.GenericParameters.Add(closureType);

        protocolDecl.Properties.Add(new PropertyDecl
        {
            Name = "handler",
            SwiftTypeSpec = optionalClosureType,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("handler_get") },
                new SetAccessorDecl { Method = CreateMethodDecl("handler_set") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitProtocolExtension(protocolDecl);

        Assert.DoesNotContain("@escaping", output);
        Assert.Contains("public var handler:", output);
    }

    #endregion

    #region Method-Level Generic Protocol Gate Tests

    [Fact]
    public void EmitConformance_AllMethodsHaveMethodLevelGenerics_EmitsConformance()
    {
        // All methods have method-level generics only (τ_1_0), no properties —
        // should emit EveryProtocol conformance with stubs
        var protocol = CreateSimpleProtocol("Resolver");
        protocol.Methods.Add(CreateMethodWithMethodLevelGeneric("resolve"));
        protocol.Methods.Add(CreateMethodWithMethodLevelGeneric("resolveWithArg"));

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.Resolver", output);
    }

    [Fact]
    public void EmitConformance_MethodLevelGenericsWithNonGenericProperty_EmitsConformance()
    {
        // Mixed-generic protocol: method-level generics AND a non-generic property.
        // ALL members get fatalError() stubs because the type projection pipeline
        // generates incorrect types for non-generic members.
        var protocol = CreateSimpleProtocol("SchedulerType");
        protocol.Methods.Add(CreateMethodWithMethodLevelGeneric("scheduleRelative"));
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "now",
            SwiftTypeSpec = new NamedTypeSpec("Foundation.Date"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("now_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.SchedulerType", output);
        // Generic method gets a fatalError() stub
        Assert.Contains("fatalError", output);
        // Property also gets a stub (mixed-generic protocol — no vtable dispatch)
        Assert.DoesNotContain("func_now_get", output);
        Assert.Contains("public var now: Foundation.Date", output);
    }

    [Fact]
    public void EmitConformance_MethodLevelGenericsWithNonGenericMethod_EmitsConformance()
    {
        // Protocol has both generic and non-generic methods.
        // Mixed-generic protocol: ALL members get fatalError() stubs.
        var protocol = CreateSimpleProtocol("MixedProtocol");
        protocol.Methods.Add(CreateMethodWithMethodLevelGeneric("genericMethod"));
        protocol.Methods.Add(CreateMethodDecl("nonGenericMethod"));

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.MixedProtocol", output);
        // Generic method gets a fatalError() stub
        Assert.Contains("method-level generic method 'genericMethod'", output);
        // Non-generic method also gets a stub (mixed-generic protocol — no vtable dispatch)
        Assert.DoesNotContain("func_nonGenericMethod_1", output);
        Assert.Contains("func nonGenericMethod()", output);
        Assert.Contains("fatalError", output);
    }

    [Fact]
    public void HasOnlyMethodLevelGenerics_MethodWithTau1_ReturnsTrue()
    {
        var method = CreateMethodWithMethodLevelGeneric("resolve");

        Assert.True(EveryProtocolEmitter.HasOnlyMethodLevelGenerics(method));
    }

    [Fact]
    public void HasOnlyMethodLevelGenerics_MethodWithTau0_ReturnsFalse()
    {
        // τ_0_0 is Self (protocol-level) — not method-level only
        var method = CreateMethodDecl("selfMethod");
        method.CSSignature[0] = new ArgumentDecl
        {
            Name = "",
            SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };

        Assert.False(EveryProtocolEmitter.HasOnlyMethodLevelGenerics(method));
    }

    [Fact]
    public void HasOnlyMethodLevelGenerics_MethodWithNoGenerics_ReturnsFalse()
    {
        var method = CreateMethodDecl("plainMethod");

        Assert.False(EveryProtocolEmitter.HasOnlyMethodLevelGenerics(method));
    }

    [Fact]
    public void EmitConformance_GenericStubWithAsyncClosure_EmitsAsyncInSignature()
    {
        // Protocol with method-level generic that takes an async closure:
        // func process<T>(_ body: @Sendable (T) async -> Void)
        var protocol = CreateSimpleProtocol("AsyncProcessor");
        var closureType = new ClosureTypeSpec(
            new NamedTypeSpec("τ_1_0"),
            TupleTypeSpec.Empty)
        {
            IsAsync = true
        };
        closureType.Attributes.Add(new TypeSpecAttribute("Sendable"));
        var method = CreateMethodWithSignature("process", closureType);
        protocol.Methods.Add(method);

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.AsyncProcessor", output);
        Assert.Contains("async", output);
        Assert.Contains("@Sendable", output);
    }

    [Fact]
    public void EmitConformance_GenericStubWithLabeledTuple_PreservesLabels()
    {
        // func transform<T>(_ value: (item: T, count: Int))
        var elem0 = new NamedTypeSpec("τ_1_0") { TypeLabel = "item" };
        var elem1 = new NamedTypeSpec("Swift.Int") { TypeLabel = "count" };
        var tupleType = new TupleTypeSpec(new TypeSpec[] { elem0, elem1 });
        var method = CreateMethodWithSignature("transform", tupleType);
        var protocol = CreateSimpleProtocol("LabeledTupleProto");
        protocol.Methods.Add(method);

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.LabeledTupleProto", output);
        Assert.Contains("item: _G0", output);
        Assert.Contains("count: Swift.Int", output);
    }

    [Fact]
    public void EmitConformance_GenericStubWithTupleParam_EmitsParenthesizedTuple()
    {
        // Protocol with method-level generic that takes a tuple:
        // func transform<T>(_ value: (T, Int)) -> (T, Int)
        var tupleType = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("τ_1_0"),
            new NamedTypeSpec("Swift.Int")
        });
        var method = CreateMethodWithSignatureAndReturn("transform", tupleType, tupleType);
        var protocol = CreateSimpleProtocol("TupleTransformer");
        protocol.Methods.Add(method);

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.TupleTransformer", output);
        // Tuple must be parenthesized: (T, Int), not bare T, Int
        Assert.Matches(@"\(_G0,\s*Swift\.Int\)", output);
    }

    [Fact]
    public void EmitConformance_SelfTypedMethodReturn_EmitsConformanceWithStub()
    {
        // Protocol with a Self-typed (τ_0_0) return method and a normal method.
        // Self-typed method gets fatalError() stub; normal method gets vtable dispatch.
        var protocol = CreateSimpleProtocol("ImageLoaderOptionSetter");
        // Self-returning method: func setOption() -> Self (τ_0_0)
        var selfMethod = CreateMethodDecl("setOption");
        selfMethod.CSSignature[0] = new ArgumentDecl
        {
            Name = "",
            SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
        protocol.Methods.Add(selfMethod);
        // Normal method
        protocol.Methods.Add(CreateMethodDecl("reset"));

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.ImageLoaderOptionSetter", output);
        // Self-typed method gets stub with EveryProtocol substitution
        Assert.Contains("-> EveryProtocol", output);
        Assert.Contains("Self-typed method 'setOption'", output);
        // Normal method gets vtable dispatch
        Assert.Contains("func_reset_1", output);
    }

    [Fact]
    public void EmitConformance_SelfTypedProperty_EmitsConformanceWithStub()
    {
        // Protocol with a Self-typed property and a normal property
        var protocol = CreateSimpleProtocol("ContentEquatable");
        // Self-typed property: var copy: Self (τ_0_0)
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "copy",
            SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("copy_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });
        // Normal property
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("name_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.ContentEquatable", output);
        // Self-typed property gets stub with EveryProtocol type
        Assert.Contains("public var copy: EveryProtocol", output);
        Assert.Contains("Self-typed property 'copy'", output);
        // Normal property gets vtable dispatch
        Assert.Contains("func_name_get", output);
    }

    [Fact]
    public void EmitConformance_SelfTypedMethodParam_EmitsStubWithEveryProtocol()
    {
        // Protocol with a method that takes Self as a parameter
        var protocol = CreateSimpleProtocol("Comparable");
        var method = CreateMethodDecl("compare");
        method.CSSignature.Add(new ArgumentDecl
        {
            Name = "other",
            SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
            PrivateName = "other",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(method);

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.Comparable", output);
        Assert.Contains("other: EveryProtocol", output);
        Assert.Contains("Self-typed method 'compare'", output);
    }

    [Fact]
    public void EmitConformance_SelfAssocTypeProperty_EmitsStubInsteadOfVtableDispatch()
    {
        // Regression: TipKit.Tip has associatedtype Action, and a protocol requirement
        // `var actions: Swift.Array<Self.Action> { get }`. The Self.Action reference
        // can't be dispatched through EveryProtocol's vtable (EveryProtocol has no
        // matching associated type), so it must route to the fatalError() stub path
        // rather than emit a vtable-dispatched implementation that fails to compile.
        var protocol = CreateSimpleProtocol("TipLike");
        protocol.AssociatedTypes.Add(new AssociatedTypeDecl
        {
            Name = "Action"
        });
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "actions",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Array")
            {
                GenericParameters = { new NamedTypeSpec("Self.Action") }
            },
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("actions_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitConformance(protocol);

        // The extension is still emitted with the PAT typealias.
        Assert.Contains("extension EveryProtocol: TestModule.TipLike", output);
        Assert.Contains("public typealias Action = Any", output);
        // Self.Action routes through the stub path — Swift.Array<Any> is the rendered
        // stub signature. No vtable dispatch (func_actions_get) is emitted because the
        // Self-typed gate triggers on the Self.Action associated-type reference.
        Assert.Contains("Swift.Array<Any>", output);
        Assert.DoesNotContain("func_actions_get!", output);
    }

    [Fact]
    public void EmitVtableStruct_SkipsSelfAssocTypeProperty()
    {
        // Self.Action references must not generate vtable fields — the C# side has
        // no way to produce a value for the associated type, and the Swift side can't
        // cast a raw pointer to Self.Action in EveryProtocol's context.
        var protocol = CreateSimpleProtocol("TipLike");
        protocol.AssociatedTypes.Add(new AssociatedTypeDecl
        {
            Name = "Action"
        });
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "actions",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Array")
            {
                GenericParameters = { new NamedTypeSpec("Self.Action") }
            },
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("actions_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var output = EmitVtableStruct(protocol);

        Assert.DoesNotContain("func_actions_get", output);
    }

    [Fact]
    public void EmitConformance_SelfTypedOptionalReturn_EmitsStubWithOptionalEveryProtocol()
    {
        // Protocol with a method returning Optional<Self>: func find() -> Self?
        var protocol = CreateSimpleProtocol("Findable");
        var method = CreateMethodDecl("find");
        method.CSSignature[0] = new ArgumentDecl
        {
            Name = "",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Optional")
            {
                GenericParameters = { new NamedTypeSpec("τ_0_0") }
            },
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
        protocol.Methods.Add(method);

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.Findable", output);
        Assert.Contains("Swift.Optional<EveryProtocol>", output);
    }

    [Fact]
    public void EmitVtableStruct_SkipsSelfTypedAndMethodLevelGenericFields()
    {
        // Protocol with Self-typed members only (no method-level generics → NOT mixed-generic).
        // Self-typed members should not get vtable fields; normal members should.
        var protocol = CreateSimpleProtocol("SelfTypedProtocol");
        // Normal property — should have vtable field
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            IsProtocolRequirement = true,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("count_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });
        // Self-typed property — should NOT have vtable field. Marked a requirement so the ONLY
        // reason it is skipped is the Self-type (τ_0_0), not the requirement gate.
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "self_prop",
            SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
            IsStatic = false,
            HasStorage = false,
            IsProtocolRequirement = true,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("self_prop_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });
        // Normal method — should have vtable field
        protocol.Methods.Add(CreateMethodDecl("normalOp"));

        var output = EmitVtableStruct(protocol);

        // Normal property and method get vtable fields
        Assert.Contains("func_count_get", output);
        Assert.Contains("func_normalOp_0", output);
        // Self-typed property does NOT get vtable field
        Assert.DoesNotContain("func_self_prop", output);
    }

    [Fact]
    public void EmitVtableStruct_MixedGenericProtocol_SkipsAllFields()
    {
        // Mixed-generic protocol (has both method-level generic and non-generic members).
        // ALL members get stubs — vtable has only csVTHandle, no member fields.
        var protocol = CreateSimpleProtocol("MixedGenericProtocol");
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            // Requirement so the DoesNotContain below attributes the skip to the mixed-generic
            // protocol shape, not the requirement gate.
            IsProtocolRequirement = true,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("count_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(CreateMethodWithMethodLevelGeneric("genericOp"));
        protocol.Methods.Add(CreateMethodDecl("normalOp"));

        var output = EmitVtableStruct(protocol);

        // Only csVTHandle — no member vtable fields
        Assert.Contains("csVTHandle", output);
        Assert.DoesNotContain("func_count_get", output);
        Assert.DoesNotContain("func_normalOp", output);
    }

    #endregion

    #region Helper Methods

    private string EmitEveryProtocolClass()
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitEveryProtocolClass(writer);
        return stringWriter.ToString();
    }

    private string EmitVtableStruct(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolVtableStruct(writer, protocolDecl);
        return stringWriter.ToString();
    }

    private string EmitProtocolExtension(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolExtension(writer, protocolDecl);
        return stringWriter.ToString();
    }

    private string EmitSetVtableFunction(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitSetVtableFunction(writer, protocolDecl);
        return stringWriter.ToString();
    }

    private string EmitFullConformance(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocolDecl);
        return stringWriter.ToString();
    }

    private string EmitFullConformance(ProtocolDecl protocolDecl, HashSet<string> globalSignatures)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocolDecl, globalSignatures);
        return stringWriter.ToString();
    }

    private string EmitFullConformance(ProtocolDecl protocolDecl, HashSet<string> globalSignatures, HashSet<string> nonThrowingOverrides)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocolDecl, globalSignatures, nonThrowingOverrides);
        return stringWriter.ToString();
    }

    private string EmitFullConformanceWithMethodPlans(ProtocolDecl protocolDecl,
        IReadOnlyDictionary<(string ProtoQName, string CarrierAndSignature), EveryProtocolEmitter.MethodEmissionPlan> methodPlans)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocolDecl, null, null, null, null, methodPlans);
        return stringWriter.ToString();
    }

    private string EmitWitnessTableGetter(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitWitnessTableGetter(writer, protocolDecl);
        return stringWriter.ToString();
    }

    #region EC-3: Class-Bound and CaseIterable Gate Tests

    [Fact]
    public void IsClassBoundProtocol_WithIsClassBound_ReturnsFalse()
    {
        // Pure AnyObject class-bound protocols are allowed — EveryProtocol is a class
        // and satisfies the AnyObject constraint. Only NSObjectProtocol is blocked.
        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.IsClassBound = true;
        Assert.False(EveryProtocolEmitter.IsClassBoundProtocol(protocol));
    }

    [Fact]
    public void IsClassBoundProtocol_WithNSObjectProtocolInheritance_ReturnsTrue()
    {
        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));
        Assert.True(EveryProtocolEmitter.IsClassBoundProtocol(protocol));
    }

    [Fact]
    public void IsClassBoundProtocol_WithAnyObjectInheritance_ReturnsFalse()
    {
        // AnyObject inheritance is allowed — EveryProtocol is a class.
        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Swift.AnyObject"));
        Assert.False(EveryProtocolEmitter.IsClassBoundProtocol(protocol));
    }

    [Fact]
    public void IsClassBoundProtocol_NormalProtocol_ReturnsFalse()
    {
        var protocol = CreateSimpleProtocol("MyProtocol");
        Assert.False(EveryProtocolEmitter.IsClassBoundProtocol(protocol));
    }

    [Fact]
    public void EmitProtocolConformance_ClassBoundProtocol_EmitsConformance()
    {
        // Class-bound (AnyObject) protocols now emit conformances —
        // EveryProtocol is a class and satisfies AnyObject.
        var protocol = CreateProtocolWithMethod("ClassBoundProto", "doSomething");
        protocol.IsClassBound = true;

        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocol);
        var output = stringWriter.ToString();

        Assert.Contains("EveryProtocol", output);
        Assert.Contains("vtable", output);
    }

    [Fact]
    public void EmitProtocolConformance_NSObjectProtocolInheritor_RoutesThroughEveryObjCProtocol()
    {
        // @objc protocols inheriting only NSObjectProtocol (no NSCoding et al.)
        // route through the NSObject-rooted EveryObjCProtocol helper class instead of
        // skipping. The emitted extension hangs off EveryObjCProtocol so Swift's
        // type-checker accepts the conformance, and the witness-table getter / vtable
        // setter use the same class.
        var protocol = CreateProtocolWithMethod("PaymentSdkFormEncodable", "encode");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));

        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocol);
        var output = stringWriter.ToString();

        // The extension hangs off EveryObjCProtocol (NSObject-rooted) so the
        // synthesized conformance type-checks against an @objc protocol that
        // inherits NSObjectProtocol. The protocol name is emitted module-qualified.
        Assert.Contains("extension EveryObjCProtocol: TestModule.PaymentSdkFormEncodable", output);
        Assert.DoesNotContain("extension EveryProtocol: TestModule.PaymentSdkFormEncodable", output);
    }

    [Fact]
    public void EmitProtocolConformance_EntityRooted_RoutesThroughEveryEntityProtocol()
    {
        // Failure B: a protocol whose only class-superclass requirement is
        // RealityFoundation.Entity (e.g. HasAnchoring) reroutes through the
        // Entity-rooted EveryEntityProtocol helper class instead of skipping via
        // HasClassSuperclassRequirement. The emitted extension hangs off
        // EveryEntityProtocol so Swift's type-checker accepts the conformance.
        var realityFoundation = new ModuleTypeDatabase("RealityFoundation", "/fake/RealityFoundation.framework/RealityFoundation");
        var entityName = SwiftTypeName.FromModuleQualifiedName("RealityFoundation.Entity");
        realityFoundation.RegisterType(entityName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RealityFoundation", "Entity"),
            SwiftTypeName = entityName,
            MetadataAccessor = "$s17RealityFoundation6EntityCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        _typeDatabase.AddModuleDatabase(realityFoundation);

        var protocolDecl = CreateProtocolWithMethod("HasAnchoring", "doSomething");
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("RealityFoundation.Entity"));

        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocolDecl);
        var output = stringWriter.ToString();

        // The extension hangs off EveryEntityProtocol (Entity-rooted) so the
        // synthesized conformance type-checks against a protocol that constrains
        // Self to be an Entity subclass.
        Assert.Contains("extension EveryEntityProtocol: TestModule.HasAnchoring", output);
        Assert.DoesNotContain("extension EveryProtocol: TestModule.HasAnchoring", output);
        Assert.DoesNotContain("extension EveryObjCProtocol: TestModule.HasAnchoring", output);
    }

    [Fact]
    public void EmitProtocolConformance_NonEntityClassSuperclass_StillSkipsEmission()
    {
        // Negative for Failure B: a class-superclass requirement on anything other
        // than RealityFoundation.Entity (e.g. UIGestureRecognizer) still skips —
        // the EveryEntityProtocol helper inherits Entity, not arbitrary classes.
        var uikit = new ModuleTypeDatabase("UIKit", "/fake/UIKit.framework/UIKit");
        var gestureName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIGestureRecognizer");
        uikit.RegisterType(gestureName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "UIGestureRecognizer"),
            SwiftTypeName = gestureName,
            MetadataAccessor = "$sSo19UIGestureRecognizerCMa",
            Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        _typeDatabase.AddModuleDatabase(uikit);

        var protocolDecl = CreateProtocolWithMethod("EntityGestureRecognizer", "doSomething");
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("UIKit.UIGestureRecognizer"));

        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocolDecl);
        var output = stringWriter.ToString();

        Assert.DoesNotContain("extension EveryProtocol: TestModule.EntityGestureRecognizer", output);
        Assert.DoesNotContain("extension EveryObjCProtocol: TestModule.EntityGestureRecognizer", output);
        Assert.DoesNotContain("extension EveryEntityProtocol: TestModule.EntityGestureRecognizer", output);
    }

    [Fact]
    public void IsEntityRootedProtocol_RealityFoundationEntity_ReturnsTrue()
    {
        // Direct unit test for the new helper: a protocol whose only class-superclass
        // requirement is RealityFoundation.Entity returns true.
        var realityFoundation = new ModuleTypeDatabase("RealityFoundation", "/fake/RealityFoundation.framework/RealityFoundation");
        var entityName = SwiftTypeName.FromModuleQualifiedName("RealityFoundation.Entity");
        realityFoundation.RegisterType(entityName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RealityFoundation", "Entity"),
            SwiftTypeName = entityName,
            MetadataAccessor = "$s17RealityFoundation6EntityCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        _typeDatabase.AddModuleDatabase(realityFoundation);

        var protocolDecl = CreateProtocolWithMethod("HasAnchoring", "doSomething");
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("RealityFoundation.Entity"));

        Assert.True(EveryProtocolEmitter.IsEntityRootedProtocol(protocolDecl, _typeDatabase));
    }

    [Fact]
    public void IsEntityRootedProtocol_RealityKitUmbrellaEntity_ReturnsTrue()
    {
        // The Entity type can appear in ABI JSON under either the RealityFoundation
        // declaring module or the RealityKit umbrella spelling depending on how it
        // is surfaced. Both must be recognized as Entity-rooted.
        var realityKit = new ModuleTypeDatabase("RealityKit", "/fake/RealityKit.framework/RealityKit");
        var entityName = SwiftTypeName.FromModuleQualifiedName("RealityKit.Entity");
        realityKit.RegisterType(entityName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RealityKit", "Entity"),
            SwiftTypeName = entityName,
            MetadataAccessor = "$s10RealityKit6EntityCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        _typeDatabase.AddModuleDatabase(realityKit);

        var protocolDecl = CreateProtocolWithMethod("HasAnchoring", "doSomething");
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("RealityKit.Entity"));

        Assert.True(EveryProtocolEmitter.IsEntityRootedProtocol(protocolDecl, _typeDatabase));
    }

    [Fact]
    public void IsEntityRootedProtocol_NonEntityClass_ReturnsFalse()
    {
        // Defensive: a non-Entity class superclass (e.g. UIKit.UIGestureRecognizer)
        // must NOT be classified as Entity-rooted — the helper only models Entity.
        var uikit = new ModuleTypeDatabase("UIKit", "/fake/UIKit.framework/UIKit");
        var gestureName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIGestureRecognizer");
        uikit.RegisterType(gestureName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "UIGestureRecognizer"),
            SwiftTypeName = gestureName,
            MetadataAccessor = "$sSo19UIGestureRecognizerCMa",
            Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        _typeDatabase.AddModuleDatabase(uikit);

        var protocolDecl = CreateProtocolWithMethod("EntityGestureRecognizer", "doSomething");
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("UIKit.UIGestureRecognizer"));

        Assert.False(EveryProtocolEmitter.IsEntityRootedProtocol(protocolDecl, _typeDatabase));
    }

    [Fact]
    public void EmitEveryProtocolClass_WithEntityRootedProtocol_EmitsEveryEntityProtocolClass()
    {
        // When EmitEveryProtocolClass is given a protocol list containing an
        // Entity-rooted protocol AND a non-null ModuleEmissionContext, it emits
        // the EveryEntityProtocol Swift class + its four @_cdecl wrappers and
        // records MarkEntityBase so per-protocol routing in EmitProtocolConformance
        // picks up the right base class.
        var realityFoundation = new ModuleTypeDatabase("RealityFoundation", "/fake/RealityFoundation.framework/RealityFoundation");
        var entityName = SwiftTypeName.FromModuleQualifiedName("RealityFoundation.Entity");
        realityFoundation.RegisterType(entityName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RealityFoundation", "Entity"),
            SwiftTypeName = entityName,
            MetadataAccessor = "$s17RealityFoundation6EntityCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        _typeDatabase.AddModuleDatabase(realityFoundation);

        var protocolDecl = CreateProtocolWithMethod("HasAnchoring", "doSomething");
        protocolDecl.InheritedProtocols.Add(new NamedTypeSpec("RealityFoundation.Entity"));

        var emissionContext = new ModuleEmissionContext();
        var emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule", emissionContext);

        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        emitter.EmitEveryProtocolClass(writer, new[] { protocolDecl });
        var output = stringWriter.ToString();

        Assert.Contains("public final class EveryEntityProtocol: Entity", output);
        Assert.Contains("SBW_CreateEveryEntityProtocol", output);
        Assert.Contains("SBW_ReleaseEveryEntityProtocol", output);
        Assert.Contains("SBW_GetMetadata_EveryEntityProtocol", output);
        Assert.Contains("SBW_SetEveryEntityProtocolDeinitCallback", output);
        Assert.True(emissionContext.AnyEntityBaseUsed);
        // EntityBase marker is keyed on the module-qualified name (T2.6), so a same-simple-name
        // cross-module protocol cannot mis-select the carrier class. The bare simple name is never a key.
        Assert.True(emissionContext.UsesEntityBase("TestModule.HasAnchoring"));
        Assert.False(emissionContext.UsesEntityBase("HasAnchoring"));
    }

    [Fact]
    public void EmitEveryProtocolClass_WithoutEntityRootedProtocol_OmitsEveryEntityProtocolClass()
    {
        // The Entity-rooted class is conditional: a wrapper module whose suitable
        // protocols include no Entity-rooted protocol must NOT emit EveryEntityProtocol
        // (Entity lives in RealityFoundation and is not a universal dependency —
        // emitting the class in a wrapper that does not import RealityFoundation
        // would fail to compile).
        var emissionContext = new ModuleEmissionContext();
        var emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule", emissionContext);

        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        emitter.EmitEveryProtocolClass(writer, Array.Empty<ProtocolDecl>());
        var output = stringWriter.ToString();

        Assert.Contains("public final class EveryProtocol", output);
        Assert.Contains("public final class EveryObjCProtocol", output);
        Assert.DoesNotContain("EveryEntityProtocol", output);
        Assert.False(emissionContext.AnyEntityBaseUsed);
    }

    [Fact]
    public void IsEntityRootedProtocol_EntityInGenericSig_ReturnsTrue()
    {
        // The class-superclass root is recorded in the protocol's genericSig
        // (`<Self : RealityKit.Entity>`), not in InheritedProtocols — this is the
        // real ABI shape for RealityFoundation.HasTransform / HasAnchoring. Entity
        // detection must read genericSig, not only the inherited-protocol list.
        var realityKit = new ModuleTypeDatabase("RealityKit", "/fake/RealityKit.framework/RealityKit");
        var entityName = SwiftTypeName.FromModuleQualifiedName("RealityKit.Entity");
        realityKit.RegisterType(entityName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RealityKit", "Entity"),
            SwiftTypeName = entityName,
            MetadataAccessor = "$s10RealityKit6EntityCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        _typeDatabase.AddModuleDatabase(realityKit);

        var protocolDecl = CreateProtocolWithMethod("HasTransform", "doSomething");
        protocolDecl.GenericSignature = "<Self : RealityKit.Entity>";

        Assert.True(EveryProtocolEmitter.IsEntityRootedProtocol(protocolDecl, _typeDatabase));
    }

    [Fact]
    public void IsEntityRootedProtocol_TransitiveEntityViaGenericSig_ReturnsTrue()
    {
        // ABI transitivity: HasCollision's genericSig constrains Self to HasTransform,
        // whose own genericSig constrains Self to Entity. The Entity root is reachable
        // only by following the protocol-typed genericSig constraint transitively
        // through the supplied module protocol list.
        var realityKit = new ModuleTypeDatabase("RealityKit", "/fake/RealityKit.framework/RealityKit");
        var entityName = SwiftTypeName.FromModuleQualifiedName("RealityKit.Entity");
        realityKit.RegisterType(entityName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RealityKit", "Entity"),
            SwiftTypeName = entityName,
            MetadataAccessor = "$s10RealityKit6EntityCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        _typeDatabase.AddModuleDatabase(realityKit);

        var hasTransform = CreateProtocolWithMethod("HasTransform", "doSomething");
        hasTransform.GenericSignature = "<Self : RealityKit.Entity>";

        var hasCollision = CreateProtocolWithMethod("HasCollision", "collide");
        hasCollision.GenericSignature = "<Self : RealityKit.HasTransform>";

        var all = new[] { hasCollision, hasTransform };
        Assert.True(EveryProtocolEmitter.IsEntityRootedProtocol(hasCollision, _typeDatabase, all));
    }

    [Fact]
    public void EmitProtocolConformance_EntityRootedViaGenericSig_RoutesThroughEveryEntityProtocol()
    {
        // End-to-end emission: a protocol whose only class-superclass requirement
        // appears in genericSig (`<Self : RealityKit.Entity>`) routes through the
        // Entity-rooted EveryEntityProtocol helper instead of being dropped as an
        // unsatisfied genericSig constraint. RealityKit is an autoBridge module, so
        // the umbrella `RealityKit.Entity` spelling otherwise trips the Apple-module
        // gate in HasUnsatisfiedProtocolConstraintInGenericSig and skips the proxy.
        var realityKit = new ModuleTypeDatabase("RealityKit", "/fake/RealityKit.framework/RealityKit");
        var entityName = SwiftTypeName.FromModuleQualifiedName("RealityKit.Entity");
        realityKit.RegisterType(entityName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RealityKit", "Entity"),
            SwiftTypeName = entityName,
            MetadataAccessor = "$s10RealityKit6EntityCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        _typeDatabase.AddModuleDatabase(realityKit);

        var protocolDecl = CreateProtocolWithMethod("HasTransform", "doSomething");
        protocolDecl.GenericSignature = "<Self : RealityKit.Entity>";

        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocolDecl);
        var output = stringWriter.ToString();

        Assert.Contains("extension EveryEntityProtocol: TestModule.HasTransform", output);
        Assert.DoesNotContain("extension EveryProtocol: TestModule.HasTransform", output);
        Assert.DoesNotContain("extension EveryObjCProtocol: TestModule.HasTransform", output);
    }

    [Fact]
    public void EmitProtocolConformance_EntityInheritedProtocol_SkipsRedundantExtension()
    {
        // The Entity-rooted carrier subclasses RealityFoundation.Entity, so it inherits
        // every protocol Entity itself conforms to (HasTransform / HasHierarchy /
        // HasSynchronization). Re-declaring `extension EveryEntityProtocol: HasTransform`
        // is a redundant-conformance error in swiftc. A subclass-only protocol Entity does
        // NOT conform to (HasModel) adds requirements Entity can't satisfy and must still
        // emit its full extension. Both protocols are directly Entity-rooted via genericSig;
        // the ONLY discriminator is membership in Entity's ProtocolConformances.
        var realityKit = new ModuleTypeDatabase("RealityKit", "/fake/RealityKit.framework/RealityKit");
        var entityName = SwiftTypeName.FromModuleQualifiedName("RealityKit.Entity");
        realityKit.RegisterType(entityName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RealityKit", "Entity"),
            SwiftTypeName = entityName,
            MetadataAccessor = "$s10RealityKit6EntityCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
            // Entity's direct ABI conformances — HasTransform is here, HasModel is not.
            ProtocolConformances = new[]
            {
                SwiftTypeName.FromModuleQualifiedName("RealityFoundation.HasTransform"),
            },
        });
        _typeDatabase.AddModuleDatabase(realityKit);

        var inherited = CreateProtocolWithMethod("HasTransform", "doSomething");
        inherited.GenericSignature = "<Self : RealityKit.Entity>";

        var subclassOnly = CreateProtocolWithMethod("HasModel", "doSomething");
        subclassOnly.GenericSignature = "<Self : RealityKit.Entity>";

        var inheritedWriter = new StringWriter();
        _emitter.EmitProtocolConformance(new SwiftWriter(inheritedWriter), inherited);
        var inheritedOutput = inheritedWriter.ToString();

        var subclassWriter = new StringWriter();
        _emitter.EmitProtocolConformance(new SwiftWriter(subclassWriter), subclassOnly);
        var subclassOutput = subclassWriter.ToString();

        // Inherited protocol: no redundant extension, but the forward-only witness-table
        // getter (and the C# proxy it backs) is still emitted via the inherited conformance.
        Assert.DoesNotContain("extension EveryEntityProtocol: TestModule.HasTransform", inheritedOutput);
        Assert.Contains("any TestModule.HasTransform", inheritedOutput);

        // Subclass-only protocol: full vtable-backed extension still emitted.
        Assert.Contains("extension EveryEntityProtocol: TestModule.HasModel", subclassOutput);
    }

    [Fact]
    public void EmitProtocolConformance_NSCodingInheritor_StillSkipsEmission()
    {
        // An NSCoding requirement arriving ONLY via InheritedProtocols (no genericSig
        // `<Self : NSCoding>` entry) routes to skip: this bare-conformance shape carries
        // no witnessing carrier stub, so EmitProtocolConformance emits nothing. The
        // real @objc-delegate rescue keys off ParsedGenericSignature.Requirements (where
        // digested `@objc protocol X: NSCoding` actually lands) and is covered end-to-end
        // by the RoomPlan / RenderProgressDelegate fixtures. NSSecureCoding / NSCopying /
        // NSMutableCopying always skip — they need real encoding/copying surfaces the
        // EveryObjCProtocol synthesis can't supply.
        var protocol = CreateProtocolWithMethod("EncodableThing", "doIt");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Foundation.NSCoding"));

        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocol);
        var output = stringWriter.ToString();

        Assert.DoesNotContain("extension EveryProtocol: TestModule.EncodableThing", output);
        Assert.DoesNotContain("extension EveryObjCProtocol: TestModule.EncodableThing", output);
    }

    [Fact]
    public void EmitProtocolConformance_CaseIterable_SkipsEmission()
    {
        var protocol = CreateProtocolWithMethod("LoadingIndicatorType", "allCases");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Swift.CaseIterable"));

        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitProtocolConformance(writer, protocol);
        var output = stringWriter.ToString();

        Assert.DoesNotContain("EveryProtocol", output);
    }

    [Fact]
    public void IsClassBoundProtocol_TransitiveNSObjectProtocol_ReturnsTrue()
    {
        // BaseProto inherits NSObjectProtocol; DerivedProto inherits BaseProto.
        // DerivedProto should be detected as class-bound transitively.
        var baseProto = CreateSimpleProtocol("BaseProto");
        baseProto.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));

        var derivedProto = CreateSimpleProtocol("DerivedProto");
        derivedProto.InheritedProtocols.Add(new NamedTypeSpec("TestModule.BaseProto"));

        var allProtocols = new List<ProtocolDecl> { baseProto, derivedProto };

        Assert.True(EveryProtocolEmitter.IsClassBoundProtocol(derivedProto, allProtocols));
    }

    [Fact]
    public void IsClassBoundProtocol_TransitiveAnyObject_ReturnsFalse()
    {
        // Transitive AnyObject class-binding is NOT a blocker.
        // EveryProtocol is a class and satisfies AnyObject at any depth.
        var baseProto = CreateSimpleProtocol("Connectable");
        baseProto.IsClassBound = true;

        var derivedProto = CreateSimpleProtocol("StreamConnectable");
        derivedProto.InheritedProtocols.Add(new NamedTypeSpec("TestModule.Connectable"));

        var allProtocols = new List<ProtocolDecl> { baseProto, derivedProto };

        Assert.False(EveryProtocolEmitter.IsClassBoundProtocol(derivedProto, allProtocols));
    }

    [Fact]
    public void IsClassBoundProtocol_TransitiveNonClassBound_ReturnsFalse()
    {
        var baseProto = CreateSimpleProtocol("Processable");

        var derivedProto = CreateSimpleProtocol("FastProcessable");
        derivedProto.InheritedProtocols.Add(new NamedTypeSpec("TestModule.Processable"));

        var allProtocols = new List<ProtocolDecl> { baseProto, derivedProto };

        Assert.False(EveryProtocolEmitter.IsClassBoundProtocol(derivedProto, allProtocols));
    }

    [Fact]
    public void IsClassBoundProtocol_TransitiveNSObjectProtocol_StillBlocked()
    {
        // Even though AnyObject is now allowed, NSObjectProtocol is still blocked.
        // Transitive NSObjectProtocol inheritance should return true.
        var baseProto = CreateSimpleProtocol("ObjCDelegate");
        baseProto.InheritedProtocols.Add(new NamedTypeSpec("ObjectiveC.NSObjectProtocol"));

        var derivedProto = CreateSimpleProtocol("AppDelegate");
        derivedProto.InheritedProtocols.Add(new NamedTypeSpec("TestModule.ObjCDelegate"));

        var allProtocols = new List<ProtocolDecl> { baseProto, derivedProto };

        Assert.True(EveryProtocolEmitter.IsClassBoundProtocol(derivedProto, allProtocols));
    }

    [Fact]
    public void WitnessTableGetter_ClassBoundProtocol_UsesDynamicOffset()
    {
        // Class-bound protocols have a 2-word existential container,
        // so the witness table offset should be computed dynamically using
        // MemoryLayout<any Protocol>.size - MemoryLayout<Int>.size
        var protocol = CreateProtocolWithMethod("ClassBoundDelegate", "didReceive");
        protocol.IsClassBound = true;

        var output = EmitWitnessTableGetter(protocol);

        Assert.Contains("MemoryLayout<any", output);
        Assert.Contains(">.size - MemoryLayout<Int>.size", output);
        // Should NOT use hardcoded 4 * MemoryLayout<Int>.size
        Assert.DoesNotContain("4 * MemoryLayout<Int>.size", output);
    }

    [Fact]
    public void WitnessTableGetter_NonClassBoundProtocol_UsesDynamicOffset()
    {
        // Non-class-bound protocols should also use the dynamic formula
        var protocol = CreateProtocolWithMethod("ValueDelegate", "process");

        var output = EmitWitnessTableGetter(protocol);

        Assert.Contains("MemoryLayout<any", output);
        Assert.Contains(">.size - MemoryLayout<Int>.size", output);
    }

    [Fact]
    public void InheritsCaseIterable_Transitive_ReturnsTrue()
    {
        var baseProto = CreateSimpleProtocol("EnumType");
        baseProto.InheritedProtocols.Add(new NamedTypeSpec("Swift.CaseIterable"));

        var derivedProto = CreateSimpleProtocol("SpecificEnumType");
        derivedProto.InheritedProtocols.Add(new NamedTypeSpec("TestModule.EnumType"));

        var allProtocols = new List<ProtocolDecl> { baseProto, derivedProto };

        Assert.True(EveryProtocolEmitter.InheritsCaseIterable(derivedProto, allProtocols));
    }

    [Fact]
    public void InheritsCaseIterable_NoCaseIterable_ReturnsFalse()
    {
        var proto = CreateSimpleProtocol("RegularProto");

        Assert.False(EveryProtocolEmitter.InheritsCaseIterable(proto));
    }

    [Fact]
    public void InheritsProtocolWithAssociatedTypes_DirectInheritance_ReturnsTrue()
    {
        // ParentProto has an associated type; ChildProto inherits it via GenericSignature.
        var parentProto = CreateSimpleProtocol("DataSerializer");
        parentProto.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "SerializedObject" });

        var childProto = CreateSimpleProtocol("ResponseSerializer");
        childProto.GenericSignature = "<Self : TestModule.DataSerializer>";

        var allProtocols = new List<ProtocolDecl> { parentProto, childProto };

        Assert.True(ModuleHandler.InheritsProtocolWithAssociatedTypes(childProto, allProtocols));
    }

    [Fact]
    public void InheritsProtocolWithAssociatedTypes_NoAssociatedTypes_ReturnsFalse()
    {
        var parentProto = CreateSimpleProtocol("Describable");
        var childProto = CreateSimpleProtocol("ExtendedDescribable");
        childProto.GenericSignature = "<Self : TestModule.Describable>";

        var allProtocols = new List<ProtocolDecl> { parentProto, childProto };

        Assert.False(ModuleHandler.InheritsProtocolWithAssociatedTypes(childProto, allProtocols));
    }

    [Fact]
    public void InheritsProtocolWithAssociatedTypes_TransitiveInheritance_ReturnsTrue()
    {
        // Root has associated type, Middle inherits Root, Child inherits Middle.
        var rootProto = CreateSimpleProtocol("Cursor");
        rootProto.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });

        var middleProto = CreateSimpleProtocol("BidirectionalCursor");
        middleProto.GenericSignature = "<Self : TestModule.Cursor>";

        var childProto = CreateSimpleProtocol("RandomAccessCursor");
        childProto.GenericSignature = "<Self : TestModule.BidirectionalCursor>";

        var allProtocols = new List<ProtocolDecl> { rootProto, middleProto, childProto };

        Assert.True(ModuleHandler.InheritsProtocolWithAssociatedTypes(childProto, allProtocols));
    }

    [Fact]
    public void InheritsProtocolWithAssociatedTypes_SelfRequirement_ReturnsTrue()
    {
        var parentProto = CreateSimpleProtocol("Comparable");
        parentProto.HasSelfRequirement = true;

        var childProto = CreateSimpleProtocol("Sortable");
        childProto.GenericSignature = "<Self : TestModule.Comparable>";

        var allProtocols = new List<ProtocolDecl> { parentProto, childProto };

        Assert.True(ModuleHandler.InheritsProtocolWithAssociatedTypes(childProto, allProtocols));
    }

    [Fact]
    public void InheritsProtocolWithAssociatedTypes_SelfRequirementOnly_DoesNotBlockWhenSelfRequirementBlocksFalse()
    {
        // A Self-requirement-ONLY inherited protocol (no associated types) — e.g. Equatable/
        // Hashable/Comparable — is the forward-only READ proxy's Population-B case: `any P` is a
        // valid existential and the inherited Self requirement is never dispatched through the
        // forward proxy. With selfRequirementBlocks:false it must NOT block (Joint admission fix);
        // with the default (true) it still blocks (suitableProtocols reverse-conformance path).
        var parentProto = CreateSimpleProtocol("Comparable");
        parentProto.HasSelfRequirement = true; // no AssociatedTypes

        var childProto = CreateSimpleProtocol("Sortable");
        childProto.GenericSignature = "<Self : TestModule.Comparable>";

        var allProtocols = new List<ProtocolDecl> { parentProto, childProto };

        Assert.True(ModuleHandler.InheritsProtocolWithAssociatedTypes(childProto, allProtocols, selfRequirementBlocks: true));
        Assert.False(ModuleHandler.InheritsProtocolWithAssociatedTypes(childProto, allProtocols, selfRequirementBlocks: false));
    }

    [Fact]
    public void InheritsProtocolWithAssociatedTypes_GenuineAssociatedTypes_BlocksRegardlessOfSelfRequirementBlocks()
    {
        // Genuine associated types make `any P` an invalid existential, so the read-only forward
        // path must STILL reject — selfRequirementBlocks:false relaxes ONLY the Self-requirement
        // half, never the associated-type half.
        var parentProto = CreateSimpleProtocol("DataSerializer");
        parentProto.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "SerializedObject" });

        var childProto = CreateSimpleProtocol("ResponseSerializer");
        childProto.GenericSignature = "<Self : TestModule.DataSerializer>";

        var allProtocols = new List<ProtocolDecl> { parentProto, childProto };

        Assert.True(ModuleHandler.InheritsProtocolWithAssociatedTypes(childProto, allProtocols, selfRequirementBlocks: true));
        Assert.True(ModuleHandler.InheritsProtocolWithAssociatedTypes(childProto, allProtocols, selfRequirementBlocks: false));
    }

    #endregion

    private static ProtocolDecl CreateSimpleProtocol(string name)
    {
        return new ProtocolDecl
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
            ModuleDecl = null
        };
    }

    private ProtocolDecl CreateProtocolWithProperty(string name, string propertyName, bool hasGetter, bool hasSetter)
    {
        var protocol = CreateSimpleProtocol(name);

        var getterMethod = CreateMethodDecl($"{propertyName}_get");
        var setterMethod = CreateMethodDecl($"{propertyName}_set");

        var accessors = new List<AccessorDecl>();
        if (hasGetter)
            accessors.Add(new GetAccessorDecl { Method = getterMethod });
        if (hasSetter)
            accessors.Add(new SetAccessorDecl { Method = setterMethod });

        protocol.Properties.Add(new PropertyDecl
        {
            Name = propertyName,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            // A genuine protocol property requirement — the vtable struct only allots a slot to
            // requirements (a !IsProtocolRequirement default-impl property is Swift-owned, no slot;
            // the struct layout MUST match ComputePropertyEmissionPlans, else slots land in the
            // wrong positions — Defect F / Finding-8 positional corruption).
            IsProtocolRequirement = true,
            Accessors = accessors,
            ParentDecl = null,
            ModuleDecl = null
        });

        return protocol;
    }

    private ProtocolDecl CreateProtocolWithMethod(string name, string methodName)
    {
        var protocol = CreateSimpleProtocol(name);

        protocol.Methods.Add(CreateMethodDecl(methodName));

        return protocol;
    }

    // A parent protocol homed in a --framework-dependency module: same shape as
    // CreateProtocolWithMethod but its SwiftTypeName is qualified with the dependency module so the
    // cross-carrier gate treats it as a cross-module parent (supplied via PreScanProtocols'
    // crossModuleParents argument rather than the module-local list).
    private ProtocolDecl CreateCrossModuleParentWithMethod(string module, string name, string methodName)
    {
        var protocol = CreateProtocolWithMethod(name, methodName);
        protocol.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}");
        return protocol;
    }

    private ProtocolDecl CreateProtocolWithRealAsyncMethod(string name, string methodName, bool throws = false)
    {
        var protocol = CreateSimpleProtocol(name);
        protocol.Methods.Add(RealAsyncEligible(methodName, arity: 1, throws: throws));
        return protocol;
    }

    // Same async shape as CreateProtocolWithRealAsyncMethod but the single param is `inout Int32` rather
    // than a value `Int32`. EmitsRealAsyncWitness rejects it (inout), yet it is still enumerated for
    // sibling grouping — the exact case the grouping key's inout discriminator must split from the
    // value-param variant (the renderer GetSwiftTypeName drops `inout`, so without it they collapse).
    private ProtocolDecl CreateProtocolWithInoutAsyncMethod(string name, string methodName)
    {
        var protocol = CreateSimpleProtocol(name);
        var method = RealAsyncEligible(methodName, arity: 1);
        method.CSSignature[1].IsInOut = true;
        method.CSSignature[1].SwiftTypeSpec.IsInOut = true;
        protocol.Methods.Add(method);
        return protocol;
    }

    // A real-async-eligible witness: `func name(_ a0: Int32, ...) async [throws] -> Int32`. CSSignature[0]
    // is the Int32 return slot; [1..arity] are Int32 value params — the exact shape EmitsRealAsyncWitness
    // accepts. Mutate the returned decl to walk off the boundary (see RealAsyncWitnessCases).
    private static MethodDecl RealAsyncEligible(string name = "compute", int arity = 1, bool throws = false)
    {
        var sig = new List<ArgumentDecl> { Int32Slot() }; // return slot
        for (int i = 0; i < arity; i++)
            sig.Add(Int32Slot($"a{i}"));
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = sig,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = throws,
            IsAsync = true,
            IsSynthesizedAccessor = false
        };
    }

    // A real-async-shaped method (Int32 return) but with a closure value param — the dedicated closure
    // emit path, which EmitsRealAsyncWitness must reject even though the return/effect qualify.
    private static MethodDecl RealAsyncWithClosureParam()
    {
        var m = RealAsyncEligible(arity: 0);
        m.CSSignature.Add(new ArgumentDecl
        {
            Name = "factory",
            PrivateName = "factory",
            SwiftTypeSpec = CreateEscapingClosure(),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        });
        return m;
    }

    private static ArgumentDecl Int32Slot(string label = "") => new()
    {
        Name = label,
        PrivateName = label,
        SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = null
    };

    // Applies an in-place mutation to a freshly-built decl and returns it (for one-line boundary rows).
    private static MethodDecl Mutate(MethodDecl method, Action<MethodDecl> mutate)
    {
        mutate(method);
        return method;
    }

    private static object[] Row(MethodDecl method, bool expected, string because) =>
        new object[] { method, expected, because };

    private static MethodDecl CreateMethodDecl(string name, MethodType methodType = MethodType.Instance, bool isConstructor = false, bool throws = false)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = methodType,
            IsConstructor = isConstructor,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = throws,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    /// <summary>
    /// Creates a non-generic instance method whose return type is the named type
    /// (CSSignature[0] is the return slot). Used to give a protocol a member that
    /// touches a registered noncopyable type so the noncopyable skip gate fires.
    /// </summary>
    private static MethodDecl CreateMethodReturning(string name, string returnTypeName)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = new NamedTypeSpec(returnTypeName),
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    /// <summary>
    /// Creates a method with a method-level generic parameter (τ_1_0) in its signature.
    /// This represents methods like resolve&lt;Service&gt;() where the generic is method-level,
    /// not protocol-level (Self).
    /// </summary>
    private static MethodDecl CreateMethodWithMethodLevelGeneric(string name)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type referencing method-level generic param
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = new NamedTypeSpec("τ_1_0"),
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                // Parameter using method-level generic
                new ArgumentDecl
                {
                    Name = "serviceType",
                    SwiftTypeSpec = new NamedTypeSpec("τ_1_0"),
                    PrivateName = "serviceType",
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "Service", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    /// <summary>
    /// Creates a method with method-level generics where the parameter uses the given TypeSpec.
    /// </summary>
    private static MethodDecl CreateMethodWithSignature(string name, TypeSpec paramType)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new ArgumentDecl
                {
                    Name = "body",
                    SwiftTypeSpec = paramType,
                    PrivateName = "body",
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    /// <summary>
    /// Creates a method with method-level generics with both param and return TypeSpec.
    /// </summary>
    private static MethodDecl CreateMethodWithSignatureAndReturn(string name, TypeSpec paramType, TypeSpec returnType)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = returnType,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new ArgumentDecl
                {
                    Name = "value",
                    SwiftTypeSpec = paramType,
                    PrivateName = "value",
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    #endregion

    #region Codable Stub Tests

    [Fact]
    public void EmitCodableStubs_ProtocolInheritsDecodable_EmitsDecodableStub()
    {
        var protocol = CreateProtocolWithInheritance("MyDecodableProto", "Swift.Decodable");

        var output = EmitCodableStubs(new[] { protocol }, new[] { protocol });

        Assert.Contains("extension EveryProtocol: Decodable", output);
        Assert.Contains("init(from decoder: Decoder)", output);
    }

    [Fact]
    public void EmitCodableStubs_ProtocolInheritsEncodable_EmitsEncodableStub()
    {
        var protocol = CreateProtocolWithInheritance("MyEncodableProto", "Swift.Encodable");

        var output = EmitCodableStubs(new[] { protocol }, new[] { protocol });

        Assert.Contains("extension EveryProtocol: Encodable", output);
        Assert.Contains("encode(to encoder: Encoder)", output);
    }

    [Fact]
    public void EmitCodableStubs_ProtocolInheritsCodable_EmitsBothStubs()
    {
        var protocol = CreateProtocolWithInheritance("MyCodableProto", "Swift.Codable");

        var output = EmitCodableStubs(new[] { protocol }, new[] { protocol });

        Assert.Contains("extension EveryProtocol: Decodable", output);
        Assert.Contains("extension EveryProtocol: Encodable", output);
    }

    [Fact]
    public void EmitCodableStubs_ProtocolInheritsError_EmitsErrorStub()
    {
        var protocol = CreateProtocolWithInheritance("MyErrorProto", "Swift.Error");

        var output = EmitCodableStubs(new[] { protocol }, new[] { protocol });

        Assert.Contains("extension EveryProtocol: Swift.Error", output);
    }

    [Fact]
    public void EmitCodableStubs_NoInheritance_EmitsNothing()
    {
        var protocol = CreateProtocolWithInheritance("PlainProto");

        var output = EmitCodableStubs(new[] { protocol }, new[] { protocol });

        Assert.DoesNotContain("Decodable", output);
        Assert.DoesNotContain("Encodable", output);
        Assert.DoesNotContain("Error", output);
    }

    [Fact]
    public void EmitCodableStubs_TransitiveDecodable_EmitsDecodableStub()
    {
        // ChildProto inherits ParentProto which inherits Decodable
        var parentProto = CreateProtocolWithInheritance("ParentProto", "Swift.Decodable");
        var childProto = CreateProtocolWithInheritance("ChildProto", "TestModule.ParentProto");

        var allProtocols = new[] { parentProto, childProto };
        var output = EmitCodableStubs(new[] { childProto }, allProtocols);

        Assert.Contains("extension EveryProtocol: Decodable", output);
    }

    #endregion

    #region Static/Composition Protocol Conformance Tests

    [Fact]
    public void EmitProtocolConformance_StaticOnlyProtocol_SkipsConformance()
    {
        // Bug #5: protocols whose only requirements are `static var` properties cannot
        // be reliably satisfied by `fatalError()` stub bodies — Swift's type-checker
        // rejects the conformance for protocols with constrained static-var requirements
        // (e.g. RealityFoundation.RealityCoordinateSpace, MaterialFunction). Skip the
        // conformance entirely; ProtocolHandler propagates the suppression to the C#
        // proxy via the existing EveryProtocolConformanceSkipped path.
        var protocol = CreateStaticOnlyProtocol("SafeEnumDecodable");

        var output = EmitConformance(protocol);

        Assert.DoesNotContain("extension EveryProtocol", output);
        Assert.DoesNotContain("fatalError", output);
    }

    [Fact]
    public void EmitProtocolConformance_CompositionProtocol_EmitsEmptyConformance()
    {
        // Composition protocol: inherits from non-trivial protocols but has no own members
        var protocol = CreateCompositionProtocol("SafeEnumCodable",
            "TestModule.SafeEnumDecodable", "Swift.Encodable");

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.SafeEnumCodable", output);
    }

    #endregion

    #region GenericSig Constraint Gate Tests

    [Fact]
    public void EmitProtocolConformance_ObjCModuleConstraint_SkipsConformance()
    {
        // Protocol with genericSig "<τ_0_0 : UIKit.UICollectionViewDataSource>"
        // should be skipped because UIKit is an ObjC module
        var protocol = CreateProtocolWithMethod("SkeletonDataSource", "numSections");
        protocol.GenericSignature = "<τ_0_0 : UIKit.UICollectionViewDataSource>";

        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void EmitProtocolConformance_UnderscorePrefixedExternalConstraint_SkipsConformance()
    {
        // Protocol with constraint to an underscore-prefixed protocol from an external module
        var protocol = CreateProtocolWithMethod("MyDelegate", "didComplete");
        protocol.GenericSignature = "<τ_0_0 : ExternalModule._internalBase>";

        // Module name for emitter is "TestModule", so "ExternalModule" is external
        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void EmitProtocolConformance_SameModuleSkippedConstraint_SkipsConformance()
    {
        // If ParentProtocol is skipped (has static method requirements)
        // then ChildProtocol with genericSig "<τ_0_0 : TestModule.ParentProtocol>" should also be skipped
        var parentProtocol = CreateSimpleProtocol("ParentProtocol");
        parentProtocol.Methods.Add(CreateMethodDecl("doStuff"));
        parentProtocol.Methods.Add(CreateStaticMethodDecl("staticReq")); // causes skip

        var childProtocol = CreateProtocolWithMethod("ChildProtocol", "childMethod");
        childProtocol.GenericSignature = "<τ_0_0 : TestModule.ParentProtocol>";

        var globalSignatures = new HashSet<string>();

        // First, emit parent — it will be skipped due to static method requirements
        var parentOutput = EmitFullConformance(parentProtocol, globalSignatures);
        Assert.DoesNotContain("extension EveryProtocol", parentOutput);

        // Now emit child — should be skipped because parent was skipped
        var childOutput = EmitFullConformance(childProtocol, globalSignatures);
        Assert.DoesNotContain("extension EveryProtocol", childOutput);
    }

    [Fact]
    public void EmitProtocolConformance_TrivialConstraints_DoesNotSkip()
    {
        // Protocol with only trivial constraints (Sendable, Copyable) should not be skipped
        var protocol = CreateProtocolWithMethod("SimpleProto", "doSomething");
        protocol.GenericSignature = "<τ_0_0 : Swift.Sendable>";

        var output = EmitConformance(protocol);
        Assert.Contains("extension EveryProtocol: TestModule.SimpleProto", output);
    }

    [Fact]
    public void PreScan_ChildBeforeParent_StillSkipsChild()
    {
        // If ChildProtocol (with genericSig referencing ParentProtocol) appears BEFORE
        // ParentProtocol in the list, PreScanProtocols should still detect the dependency.
        var parentProtocol = CreateSimpleProtocol("ParentProtocol");
        parentProtocol.Methods.Add(CreateMethodDecl("doStuff"));
        parentProtocol.Methods.Add(CreateStaticMethodDecl("staticReq")); // causes skip

        var childProtocol = CreateProtocolWithMethod("ChildProtocol", "childMethod");
        childProtocol.GenericSignature = "<τ_0_0 : TestModule.ParentProtocol>";

        // Child appears BEFORE parent in the list (reverse order)
        var protocols = new List<ProtocolDecl> { childProtocol, parentProtocol };

        // Create a fresh emitter to test PreScan
        var emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
        emitter.PreScanProtocols(protocols);

        var globalSignatures = new HashSet<string>();

        // Child should be skipped even though it appeared first
        var childOutput = new StringWriter();
        var childWriter = new SwiftWriter(childOutput);
        emitter.EmitProtocolConformance(childWriter, childProtocol, globalSignatures);
        Assert.DoesNotContain("extension EveryProtocol", childOutput.ToString());
    }

    [Fact]
    public void PreScan_ChildBeforeNoncopyableParent_StillSkipsChild()
    {
        // Order-independence for the noncopyable-member skip gate. The emission ladder
        // (EmitProtocolConformance) skips a protocol whose member signatures touch a
        // ~Copyable type because the inout trampoline can't copy the value across the
        // boundary. The pre-scan's WillSkipConformance MUST record the SAME skip, or
        // Pass-2 genericSig propagation won't see a noncopyable parent as unsatisfied —
        // and a genericSig-constrained child declared BEFORE its parent would emit a
        // dangling `extension EveryProtocol: Child` that references a parent conformance
        // the ladder then refuses to produce (order-dependent swiftc failure).
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        // A noncopyable value type the parent protocol returns. Only the NonCopyable
        // flag matters; TryGetTypeRecord routes through the resolver by module-qualified name.
        var resourceName = SwiftTypeName.FromModuleQualifiedName("TestModule.Resource");
        module.RegisterType(
            resourceName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Resource"),
                SwiftTypeName = resourceName,
                MetadataAccessor = "$s10TestModule8ResourceVMa",
                Flags = TypeRecordFlags.NonCopyable,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(module);

        // Parent protocol whose ONLY skip cause is a noncopyable return type. Without the
        // WillSkipConformance noncopyable gate it survives the pre-scan unseeded.
        var parentProtocol = CreateSimpleProtocol("NoncopyableParent");
        parentProtocol.Methods.Add(CreateMethodReturning("makeResource", "TestModule.Resource"));

        var childProtocol = CreateProtocolWithMethod("ChildProtocol", "childMethod");
        childProtocol.GenericSignature = "<τ_0_0 : TestModule.NoncopyableParent>";

        // Child appears BEFORE parent in the list (reverse order).
        var protocols = new List<ProtocolDecl> { childProtocol, parentProtocol };

        var emitter = new EveryProtocolEmitter(typeDatabase, NullLogger.Instance, "TestModule");
        emitter.PreScanProtocols(protocols);

        var globalSignatures = new HashSet<string>();
        var childOutput = new StringWriter();
        var childWriter = new SwiftWriter(childOutput);
        emitter.EmitProtocolConformance(childWriter, childProtocol, globalSignatures);
        Assert.DoesNotContain("extension EveryProtocol", childOutput.ToString());
    }

    [Fact]
    public void PreScan_ParentDroppedFromCandidacy_SkipsChild()
    {
        // A parent dropped by the MODULE-LEVEL candidacy filter never reaches the emitter at all,
        // so no `extension EveryProtocol: Parent` exists — but the pre-scan's own passes iterate
        // only the protocols they are handed and cannot discover that. The child must still be
        // skipped: `extension EveryProtocol: Child` makes Swift demand Parent's witnesses too, and
        // swiftc names the PARENT in the error, not the child.
        var childProtocol = CreateProtocolWithMethod("ChildProtocol", "childMethod");
        childProtocol.GenericSignature = "<τ_0_0 : TestModule.DroppedParent>";

        // Only the child is offered for emission — the parent lost candidacy upstream.
        var protocols = new List<ProtocolDecl> { childProtocol };

        var emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
        emitter.PreScanProtocols(
            protocols,
            crossModuleParents: null,
            unavailableConformances: new[] { "DroppedParent", "TestModule.DroppedParent" });

        var output = new StringWriter();
        emitter.EmitProtocolConformance(new SwiftWriter(output), childProtocol, new HashSet<string>());
        Assert.DoesNotContain("extension EveryProtocol", output.ToString());
    }

    [Fact]
    public void PreScan_ParentDroppedFromCandidacy_InheritedProtocolsEdgeOnly_SkipsChild()
    {
        // Same defect reached through the OTHER channel. The parser fills InheritedProtocols from
        // ABI `Conformances` and GenericSignature from `GenericSig` — different fields, populated
        // independently. A child that records its parent ONLY as an inherited protocol (empty
        // genericSig) must be skipped just the same; a genericSig-only check short-circuits on the
        // empty string and lets the broken conformance through.
        var childProtocol = CreateProtocolWithMethod("ChildProtocol", "childMethod");
        childProtocol.InheritedProtocols.Add(new NamedTypeSpec("TestModule.DroppedParent"));
        Assert.True(string.IsNullOrEmpty(childProtocol.GenericSignature));

        var protocols = new List<ProtocolDecl> { childProtocol };

        var emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
        emitter.PreScanProtocols(
            protocols,
            crossModuleParents: null,
            unavailableConformances: new[] { "DroppedParent", "TestModule.DroppedParent" });

        var output = new StringWriter();
        emitter.EmitProtocolConformance(new SwiftWriter(output), childProtocol, new HashSet<string>());
        Assert.DoesNotContain("extension EveryProtocol", output.ToString());
    }

    [Fact]
    public void PreScan_ParentSkippedByStructuralGate_InheritedProtocolsEdgeOnly_SkipsChild()
    {
        // The InheritedProtocols edge must also carry Pass-1 skips, not just seeded drops: a parent
        // that IS offered for emission but loses on a structural gate (here, a static requirement)
        // leaves the same unwitnessed hole.
        var parentProtocol = CreateSimpleProtocol("ParentProtocol");
        parentProtocol.Methods.Add(CreateMethodDecl("doStuff"));
        parentProtocol.Methods.Add(CreateStaticMethodDecl("staticReq")); // causes skip

        var childProtocol = CreateProtocolWithMethod("ChildProtocol", "childMethod");
        childProtocol.InheritedProtocols.Add(new NamedTypeSpec("TestModule.ParentProtocol"));

        var protocols = new List<ProtocolDecl> { childProtocol, parentProtocol };

        var emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
        emitter.PreScanProtocols(protocols);

        var output = new StringWriter();
        emitter.EmitProtocolConformance(new SwiftWriter(output), childProtocol, new HashSet<string>());
        Assert.DoesNotContain("extension EveryProtocol", output.ToString());
    }

    [Fact]
    public void PreScan_DroppedParentSkip_PropagatesTransitively()
    {
        // The seed must feed the existing fixpoint, not just a one-level check: Grandchild inherits
        // Child inherits the dropped Parent. Seeding after the passes (or checking only direct
        // edges) leaves Grandchild emitting against a Child that never conforms.
        var childProtocol = CreateProtocolWithMethod("ChildProtocol", "childMethod");
        childProtocol.InheritedProtocols.Add(new NamedTypeSpec("TestModule.DroppedParent"));

        var grandchild = CreateProtocolWithMethod("GrandchildProtocol", "grandchildMethod");
        grandchild.InheritedProtocols.Add(new NamedTypeSpec("TestModule.ChildProtocol"));

        // Grandchild first, so the fixpoint has to re-visit it after Child is marked.
        var protocols = new List<ProtocolDecl> { grandchild, childProtocol };

        var emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
        emitter.PreScanProtocols(
            protocols,
            crossModuleParents: null,
            unavailableConformances: new[] { "DroppedParent", "TestModule.DroppedParent" });

        var output = new StringWriter();
        emitter.EmitProtocolConformance(new SwiftWriter(output), grandchild, new HashSet<string>());
        Assert.DoesNotContain("extension EveryProtocol", output.ToString());
    }

    [Fact]
    public void PreScan_UnrelatedProtocol_StillEmitsWhenSiblingDropped()
    {
        // Guard the other direction: seeding dropped candidates must not suppress a protocol that
        // doesn't inherit any of them. Over-skipping is as much a defect as under-skipping.
        var loneProtocol = CreateProtocolWithMethod("LoneProtocol", "loneMethod");

        var emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
        emitter.PreScanProtocols(
            new List<ProtocolDecl> { loneProtocol },
            crossModuleParents: null,
            unavailableConformances: new[] { "DroppedParent", "TestModule.DroppedParent" });

        var output = new StringWriter();
        emitter.EmitProtocolConformance(new SwiftWriter(output), loneProtocol, new HashSet<string>());
        Assert.Contains("extension EveryProtocol", output.ToString());
    }

    [Fact]
    public void PreScan_InheritsStubbedCodable_StillEmits()
    {
        // Codable and its halves get explicit stub conformances on EveryProtocol
        // (EmitCodableStubsIfNeeded), so the inherited edge is witnessed and must NOT read as
        // unavailable. This is the exemption most at risk from the new InheritedProtocols arm:
        // the genericSig arm never had to rule on it.
        var codableChild = CreateProtocolWithMethod("CodableChild", "childMethod");
        codableChild.InheritedProtocols.Add(new NamedTypeSpec("Swift.Codable"));

        var emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
        emitter.PreScanProtocols(new List<ProtocolDecl> { codableChild });

        var output = new StringWriter();
        emitter.EmitProtocolConformance(new SwiftWriter(output), codableChild, new HashSet<string>());
        Assert.Contains("extension EveryProtocol", output.ToString());
    }

    [Theory]
    [InlineData(true, "public var thing: (Swift.Int)! {")]
    [InlineData(false, "public var thing: (Swift.Int)? {")]
    public void EmitProtocolConformance_OptionalProperty_WitnessSpellingFollowsTheRequirement(
        bool iuo, string expectedDecl)
    {
        // Swift rejects a `T?` witness for a `T!` requirement ("candidate has non-matching type"),
        // so the emitted declaration has to carry the spelling the protocol used. Asserted through
        // real emission rather than the renderer alone: the renderer was already correct while the
        // emitter called it from the wrong seam, which is exactly what this locks down.
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        optional.IsImplicitlyUnwrappedOptional = iuo;

        var protocol = CreateProtocolWithProperty("OptionalProto", "thing", hasGetter: true, hasSetter: false);
        protocol.Properties[0].SwiftTypeSpec = optional;

        var emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
        emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        var output = new StringWriter();
        emitter.EmitProtocolConformance(new SwiftWriter(output), protocol, new HashSet<string>());
        var emitted = output.ToString();

        Assert.Contains(expectedDecl, emitted);
        // The `!` spelling is legal ONLY in the declaration. The witness body reads the value back
        // through a metatype, which must stay on the plain form or swiftc rejects the file outright
        // with "using '!' is not allowed here".
        Assert.DoesNotContain("!.self", emitted);
    }

    [Fact]
    public void PreScan_DroppedForeignParent_SpelledUnderReexportingModule_StillSkipsChild()
    {
        // The ChartView shape, and the reason Case 2's simple-name arm cannot be narrowed to our own
        // module. A module's ABI lists the foreign protocols it extends, so `View` arrives declared
        // by moduleName "SwiftUICore" while mangled `$s7SwiftUI4ViewP`; it fails module candidacy and
        // is seeded unwitnessed under BOTH its bare name and its declaring module. The child spells
        // the constraint with the re-exporting module instead — "SwiftUI.View" — so the qualified
        // seed never matches and only the bare name connects them. Skip the child, or it emits an
        // extension against a `View` requirement nothing satisfies.
        var child = CreateProtocolWithMethod("ChartBase", "chartMethod");
        child.InheritedProtocols.Add(new NamedTypeSpec("SwiftUI.View"));

        var emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "SwiftUICharts");
        emitter.PreScanProtocols(
            new List<ProtocolDecl> { child },
            crossModuleParents: null,
            unavailableConformances: new[] { "View", "SwiftUICore.View" });

        var output = new StringWriter();
        emitter.EmitProtocolConformance(new SwiftWriter(output), child, new HashSet<string>());
        Assert.DoesNotContain("extension EveryProtocol", output.ToString());
    }

    [Fact]
    public void PreScan_DroppedLocalParent_QualifiedWithOwnModule_StillSkipsChild()
    {
        // The companion to the guard above: when the constraint names OUR module, the simple-name
        // arm must still fire, or the analytics-swift shape (`EventPlugin : Plugin` where Plugin
        // lost candidacy) regresses back to emitting a conformance Swift cannot satisfy.
        var child = CreateProtocolWithMethod("Child", "childMethod");
        child.InheritedProtocols.Add(new NamedTypeSpec("TestModule.Plugin"));

        var emitter = new EveryProtocolEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
        emitter.PreScanProtocols(
            new List<ProtocolDecl> { child },
            crossModuleParents: null,
            unavailableConformances: new[] { "Plugin" });

        var output = new StringWriter();
        emitter.EmitProtocolConformance(new SwiftWriter(output), child, new HashSet<string>());
        Assert.DoesNotContain("extension EveryProtocol", output.ToString());
    }

    #endregion

    #region Method Type Conflict Gate Tests

    [Fact]
    public void EmitProtocolConformance_SameLabelDifferentTypes_EmitsBothAsOverloads()
    {
        // Two protocols with same method label but different parameter types
        // Both should emit because Swift supports method overloading by parameter type.
        // EveryProtocol can implement both register(delegate: FirstDelegate) and
        // register(delegate: SecondDelegate) as distinct overloads.
        var firstProtocol = CreateSimpleProtocol("FirstHandler");
        firstProtocol.Methods.Add(CreateMethodDeclWithParam("register", "delegate", "FirstDelegate"));
        firstProtocol.Methods.Add(CreateMethodDeclWithParam("parse", "data", "Foundation.Data"));

        var secondProtocol = CreateSimpleProtocol("SecondHandler");
        secondProtocol.Methods.Add(CreateMethodDeclWithParam("register", "delegate", "SecondDelegate"));
        secondProtocol.Methods.Add(CreateMethodDecl("createResponse"));

        var globalSignatures = new HashSet<string>();

        // First protocol emits normally
        var firstOutput = EmitFullConformance(firstProtocol, globalSignatures);
        Assert.Contains("extension EveryProtocol: TestModule.FirstHandler", firstOutput);
        Assert.Contains("public func register", firstOutput);

        // Second protocol also emits — different param types are valid overloads
        var secondOutput = EmitFullConformance(secondProtocol, globalSignatures);
        Assert.Contains("extension EveryProtocol: TestModule.SecondHandler", secondOutput);
        Assert.Contains("public func register", secondOutput);
        Assert.Contains("public func createResponse", secondOutput);
    }

    [Fact]
    public void EmitProtocolConformance_SameParamsDifferentReturn_BothEmitted()
    {
        // Two protocols with same method name and parameter types but different return types.
        // Both are intentionally emitted — the resulting invalid Swift is handled by the
        // wrapper strip/retry mechanism (strips the duplicate function, then the unsatisfiable
        // conformance on retry). Using call-signature dedup to skip the second would leave an
        // empty conformance that the strip script can't recover from.
        var firstProtocol = CreateSimpleProtocol("IntValueProvider");
        var intMethod = CreateMethodDecl("value");
        intMethod.CSSignature[0] = new ArgumentDecl
        {
            Name = "", SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
            PrivateName = "", IsInOut = false, IsGeneric = false,
            ParentDecl = null, ModuleDecl = null
        };
        firstProtocol.Methods.Add(intMethod);

        var secondProtocol = CreateSimpleProtocol("StringValueProvider");
        var stringMethod = CreateMethodDecl("value");
        stringMethod.CSSignature[0] = new ArgumentDecl
        {
            Name = "", SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            PrivateName = "", IsInOut = false, IsGeneric = false,
            ParentDecl = null, ModuleDecl = null
        };
        secondProtocol.Methods.Add(stringMethod);

        var globalSignatures = new HashSet<string>();

        // First protocol emits value() -> Int
        var firstOutput = EmitFullConformance(firstProtocol, globalSignatures);
        Assert.Contains("public func value", firstOutput);

        // Second protocol also emits value() -> String (different full signature).
        // This produces invalid Swift that the strip/retry mechanism will resolve.
        var secondOutput = EmitFullConformance(secondProtocol, globalSignatures);
        Assert.Contains("public func value", secondOutput);
    }

    [Fact]
    public void EmitProtocolConformance_SameSignatureNoConflict_EmitsConformance()
    {
        // Two protocols with same method (same name, same types) — no conflict
        var firstProtocol = CreateProtocolWithMethod("ProtoA", "update");
        var secondProtocol = CreateSimpleProtocol("ProtoB");
        secondProtocol.Methods.Add(CreateMethodDecl("update"));
        secondProtocol.Methods.Add(CreateMethodDecl("finish"));

        var globalSignatures = new HashSet<string>();
        EmitFullConformance(firstProtocol, globalSignatures);

        // Second protocol should still emit (update is satisfied by first, finish is new)
        var output = EmitFullConformance(secondProtocol, globalSignatures);
        Assert.Contains("extension EveryProtocol: TestModule.ProtoB", output);
        Assert.Contains("public func finish", output);
    }

    [Fact]
    public void EmitProtocolConformance_OverloadedMethodsSameNameDifferentLabels_EmitsBothMethods()
    {
        // Protocol has two methods with the same base name and same parameter types
        // but different argument labels (e.g. pageViewController(_:viewControllerBefore:)
        // and pageViewController(_:viewControllerAfter:)). Both must be emitted.
        var protocol = CreateSimpleProtocol("PageDataSource");
        protocol.Methods.Add(CreateMethodDeclWithParam("pageVC", "before", "UIKit.UIViewController"));
        protocol.Methods.Add(CreateMethodDeclWithParam("pageVC", "after", "UIKit.UIViewController"));

        var vtableOutput = EmitVtableStruct(protocol);
        var extensionOutput = EmitConformance(protocol);

        // Vtable must have TWO distinct entries
        Assert.Contains("func_pageVC_0", vtableOutput);
        Assert.Contains("func_pageVC_1", vtableOutput);

        // Extension must implement BOTH overloads
        Assert.Contains("func pageVC(before", extensionOutput);
        Assert.Contains("func pageVC(after", extensionOutput);
    }

    [Fact]
    public void EmitProtocolConformance_SameLabelDifferentTypes_ThrowsPreservedPerOverload()
    {
        // Non-throwing validate(input: String) from one protocol must NOT suppress
        // throws on validate(input: Int32) throws from a different protocol.
        // These are distinct Swift overloads; the non-throwing override should only
        // apply when the full signature (name + types) matches.
        var nonThrowingProto = CreateSimpleProtocol("StringValidator");
        nonThrowingProto.Methods.Add(CreateMethodDeclWithParam("validate", "input", "Swift.String"));

        var throwingProto = CreateSimpleProtocol("IntValidator");
        var throwingMethod = CreateMethodDeclWithParam("validate", "input", "Swift.Int32");
        throwingMethod.Throws = true;
        throwingProto.Methods.Add(throwingMethod);

        var globalSignatures = new HashSet<string>();

        // First protocol: non-throwing validate(input: String)
        var firstOutput = EmitFullConformance(nonThrowingProto, globalSignatures);
        Assert.Contains("extension EveryProtocol: TestModule.StringValidator", firstOutput);
        Assert.Contains("public func validate", firstOutput);
        Assert.DoesNotContain("throws", firstOutput);

        // Second protocol: throwing validate(input: Int32)
        // Must retain "throws" — different parameter type means different overload
        var secondOutput = EmitFullConformance(throwingProto, globalSignatures);
        Assert.Contains("extension EveryProtocol: TestModule.IntValidator", secondOutput);
        Assert.Contains("throws", secondOutput);
    }

    #endregion

    #region Method Type Conflict Helper Methods

    private static MethodDecl CreateMethodDeclWithParam(string name, string label, string typeName)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type (void)
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                // Parameter
                new ArgumentDecl
                {
                    Name = label,
                    SwiftTypeSpec = new NamedTypeSpec(typeName),
                    PrivateName = label,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static MethodDecl CreateStaticMethodDecl(string name)
    {
        return CreateMethodDecl(name, MethodType.Static);
    }

    #endregion

    #region Codable/Composition Helper Methods

    private static ProtocolDecl CreateProtocolWithInheritance(string name, params string[] inheritedNames)
    {
        var inherited = inheritedNames.Select(n => new NamedTypeSpec(n)).ToList();
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            HasSelfRequirement = false,
            IsClassBound = false,
            Properties = new List<PropertyDecl>
            {
                new PropertyDecl
                {
                    Name = "value",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    HasStorage = false,
                    IsStatic = false,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = inherited,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ProtocolDecl CreateStaticOnlyProtocol(string name)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            HasSelfRequirement = false,
            IsClassBound = false,
            Properties = new List<PropertyDecl>
            {
                new PropertyDecl
                {
                    Name = "unparsable",
                    SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
                    HasStorage = false,
                    IsStatic = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ProtocolDecl CreateCompositionProtocol(string name, params string[] inheritedNames)
    {
        var inherited = inheritedNames.Select(n => new NamedTypeSpec(n)).ToList();
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            HasSelfRequirement = false,
            IsClassBound = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = inherited,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private string EmitCodableStubs(IReadOnlyList<ProtocolDecl> suitableProtocols,
        IReadOnlyList<ProtocolDecl> allProtocols)
    {
        var output = new System.IO.StringWriter();
        var writer = new SwiftWriter(output);
        _emitter.EmitCodableStubsIfNeeded(writer, suitableProtocols, allProtocols, _typeDatabase);
        return output.ToString();
    }

    private string EmitConformance(ProtocolDecl protocol)
    {
        var output = new System.IO.StringWriter();
        var writer = new SwiftWriter(output);
        _emitter.EmitProtocolConformance(writer, protocol);
        return output.ToString();
    }

    #endregion

    #region Closure Method Stub Tests

    [Fact]
    public void EmitProtocolExtension_DispatchableClosureMethod_EmitsRealDispatch()
    {
        // `@escaping () -> Void` is the dispatchable closure shape.
        // It gets a real vtable-dispatching implementation, not a fatalError stub.
        var protocol = CreateSimpleProtocol("EventDelegate");
        protocol.Methods.Add(CreateMethodDeclWithParam("didReceiveEvent", "name", "Swift.String"));
        protocol.Methods.Add(CreateMethodWithClosureParam("onComplete", "handler"));

        var output = EmitProtocolExtension(protocol);

        Assert.Contains("public func onComplete(handler: @escaping () -> Void)", output);
        Assert.DoesNotContain("@escaping @escaping", output);
        // Real dispatch, not the fatalError stub.
        Assert.DoesNotContain("closure method 'onComplete' cannot be dispatched", output);
        Assert.Contains("func_onComplete_1", output);
        Assert.Contains("public func didReceiveEvent(", output);
    }

    [Fact]
    public void EmitProtocolExtension_IntArgClosureMethod_Dispatches()
    {
        // A closure with a non-Void argument (here Int -> Void) is dispatchable: the
        // invoke-thunk machinery already handles multi-arg shapes via EachArgument().
        var protocol = CreateSimpleProtocol("EventDelegate");
        protocol.Methods.Add(CreateMethodWithIntArgClosureParam("onValue", "handler"));

        var output = EmitProtocolExtension(protocol);

        Assert.Contains("public func onValue(handler: @escaping (Swift.Int) -> Void)", output);
        Assert.DoesNotContain("@escaping @escaping", output);
        Assert.DoesNotContain("closure method 'onValue' cannot be dispatched", output);
        Assert.Contains("func_onValue_0", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_DispatchableClosureMethod_IncludesVtableField()
    {
        // Dispatchable closure methods get a vtable field that expands
        // the closure into a (fnPtr, ctxPtr) pair.
        var protocol = CreateSimpleProtocol("EventDelegate");
        protocol.Methods.Add(CreateMethodDeclWithParam("didReceiveEvent", "name", "Swift.String"));
        protocol.Methods.Add(CreateMethodWithClosureParam("onComplete", "handler"));

        var output = EmitVtableStruct(protocol);

        Assert.Contains("func_didReceiveEvent_0", output);
        Assert.Contains("func_onComplete_1", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_IntArgClosureMethod_IncludesVtableField()
    {
        // Non-Void closure arg shapes are dispatched in 4b — vtable field with
        // (fnPtr, ctxPtr) slot pair is emitted alongside other methods.
        var protocol = CreateSimpleProtocol("EventDelegate");
        protocol.Methods.Add(CreateMethodDeclWithParam("didReceiveEvent", "name", "Swift.String"));
        protocol.Methods.Add(CreateMethodWithIntArgClosureParam("onValue", "handler"));

        var output = EmitVtableStruct(protocol);

        Assert.Contains("func_didReceiveEvent_0", output);
        Assert.Contains("func_onValue_1", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_AsyncClosureMethod_SkipsVtableField()
    {
        // Async closure methods remain non-dispatchable — no vtable field.
        var protocol = CreateSimpleProtocol("AsyncDelegate");
        protocol.Methods.Add(CreateMethodDeclWithParam("didReceiveEvent", "name", "Swift.String"));
        protocol.Methods.Add(CreateAsyncMethodWithClosureParam("onComplete", "handler"));

        var output = EmitVtableStruct(protocol);

        Assert.Contains("func_didReceiveEvent_0", output);
        Assert.DoesNotContain("func_onComplete_1", output);
    }

    [Fact]
    public void EmitProtocolConformance_ProtocolWithClosureAndNonClosure_EmitsConformance()
    {
        var protocol = CreateSimpleProtocol("EventDelegate");
        protocol.Methods.Add(CreateMethodDeclWithParam("didReceiveEvent", "name", "Swift.String"));
        protocol.Methods.Add(CreateMethodWithClosureParam("onComplete", "handler"));

        var output = EmitFullConformance(protocol);

        // Conformance IS emitted (not skipped entirely)
        Assert.Contains("extension EveryProtocol: TestModule.EventDelegate", output);
        // Witness table getter IS emitted
        Assert.Contains("Get_EveryProtocol_EventDelegate_WitnessTable", output);
    }

    [Fact]
    public void EmitProtocolExtension_ReturnTypedClosureMethod_Dispatches()
    {
        // () -> Int closure — return-typed closure shape (4b widening). The invoke-thunk
        // machinery already passes the return value back via @_cdecl.
        var protocol = CreateSimpleProtocol("Factory");
        protocol.Methods.Add(CreateMethodWithReturnTypedClosureParam("make", "factory", "Swift.Int"));

        var output = EmitProtocolExtension(protocol);

        Assert.Contains("public func make(factory: @escaping () -> Swift.Int)", output);
        Assert.DoesNotContain("closure method 'make' cannot be dispatched", output);
        Assert.Contains("func_make_0", output);
    }

    [Fact]
    public void EmitProtocolExtension_MultiArg_DispatchablePlusArrayOfClosure_SkipsDispatch()
    {
        // One dispatchable @escaping () -> Void plus a value param that nests a closure
        // (Array<() -> Void>) must NOT dispatch — the receiver path can only marshal one
        // (fnPtr, ctx) pair per method, and an Array<Closure> would silently emit a vtable
        // slot the proxy cannot wire.
        var protocol = CreateSimpleProtocol("FanOut");
        var arrayOfClosure = new NamedTypeSpec(
            "Swift.Array",
            CreateEscapingClosure());
        protocol.Methods.Add(CreateMethodWithDispatchableClosureAndNonClosureValueParam(
            "register", "completion", arrayOfClosure, "fallbacks"));

        var output = EmitProtocolExtension(protocol);

        Assert.Contains("closure method 'register' cannot be dispatched", output);
    }

    [Fact]
    public void EmitProtocolExtension_MultiArg_DispatchablePlusTupleContainingClosure_SkipsDispatch()
    {
        // One dispatchable @escaping () -> Void plus a tuple value param containing a
        // closure ((Int, () -> Void)). Tuples are TupleTypeSpec, so the rejection takes
        // a different recursion path through ContainsClosureType than the Array case.
        var protocol = CreateSimpleProtocol("PairedHandler");
        var tupleWithClosure = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            CreateEscapingClosure()
        });
        protocol.Methods.Add(CreateMethodWithDispatchableClosureAndNonClosureValueParam(
            "schedule", "primary", tupleWithClosure, "secondary"));

        var output = EmitProtocolExtension(protocol);

        Assert.Contains("closure method 'schedule' cannot be dispatched", output);
    }

    [Fact]
    public void EmitProtocolExtension_DispatchableClosurePlusObjCBridgeableValueParam_BridgesViaAnyObject()
    {
        // A dispatchable @escaping () -> Void closure alongside an ObjC-bridgeable value
        // param (e.g. Foundation.Decimal) must marshal that param through the ObjC bridge
        // (`as AnyObject` / passUnretained → opaque pointer), NOT as `var amountCopy = amount`
        // raw Swift struct bytes. The C# receiver materializes the param via GetNSObject<T>
        // (expecting an ObjC pointer); passing raw struct bytes corrupts it. This mirrors the
        // ObjC-bridgeable arm in the non-closure method fan-out (EmitMethodImplementation).
        var foundation = new ModuleTypeDatabase("Foundation", "/fake/Foundation.framework/Foundation");
        var decimalName = SwiftTypeName.FromModuleQualifiedName("Foundation.Decimal");
        foundation.RegisterType(decimalName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSDecimalNumber"),
            SwiftTypeName = decimalName,
            MetadataAccessor = "$s10Foundation7DecimalVMa",
            Flags = TypeRecordFlags.ObjCBridgeable | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct,
        });
        _typeDatabase.AddModuleDatabase(foundation);

        var protocol = CreateSimpleProtocol("AmountProcessor");
        protocol.Methods.Add(CreateMethodWithDispatchableClosureAndNonClosureValueParam(
            "process", "completion", new NamedTypeSpec("Foundation.Decimal"), "amount"));

        var output = EmitProtocolExtension(protocol);

        // Dispatched (not rejected) and bridged through the ObjC arm, not the raw-bytes path.
        Assert.DoesNotContain("closure method 'process' cannot be dispatched", output);
        Assert.Contains("let amountNS = amount as AnyObject", output);
        Assert.Contains("Unmanaged.passUnretained(amountNS).toOpaque()", output);
        Assert.DoesNotContain("var amountCopy = amount", output);
    }

    [Fact]
    public void EmitProtocolExtension_InOutObjCBridgeableParam_EmitsTrapStubNotDispatch()
    {
        // An inout ObjC-bridgeable param (inout Decimal) cannot round-trip the mutated value
        // back across the ObjC bridge — neither the Swift caller arm nor the C# receiver wires
        // the writeback. The method must route to a fatalError trap stub (requirement satisfied,
        // dispatch refused) rather than EmitMethodImplementation's writeback, which would
        // reference an `amountCopy` the ObjC arm never declares and fail to compile.
        var foundation = new ModuleTypeDatabase("Foundation", "/fake/Foundation.framework/Foundation");
        var decimalName = SwiftTypeName.FromModuleQualifiedName("Foundation.Decimal");
        foundation.RegisterType(decimalName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSDecimalNumber"),
            SwiftTypeName = decimalName,
            MetadataAccessor = "$s10Foundation7DecimalVMa",
            Flags = TypeRecordFlags.ObjCBridgeable | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct,
        });
        _typeDatabase.AddModuleDatabase(foundation);

        var protocol = CreateSimpleProtocol("AmountMutator");
        protocol.Methods.Add(CreateMethodWithInOutParam("update", "amount", new NamedTypeSpec("Foundation.Decimal")));

        var output = EmitProtocolExtension(protocol);

        // Routed to the trap stub: inout signature rendered + fatalError, NOT the broken dispatch
        // (no dangling amountCopy/amountRef writeback).
        Assert.Contains("inout ObjC-bridgeable parameter cannot be dispatched", output);
        Assert.Contains("amount: inout", output);
        Assert.DoesNotContain("amountCopy", output);
        Assert.DoesNotContain("amountRef", output);
    }

    [Fact]
    public void EmitProtocolExtension_InOutOptionalObjCBridgeableValueParam_EmitsTrapStubNotDispatch()
    {
        // An inout Optional<ObjC-bridgeable VALUE> param (inout Decimal?) has the same limitation as
        // the non-optional inout case: the mutated value cannot be bridged back across the ObjC bridge.
        // The param-in optional arm binds `var amountRef = amountNS.map { passUnretained($0).toOpaque() }`
        // (an Optional<UnsafeMutableRawPointer>) as its writeback source, so a dispatched body would emit
        // `amount = amountRef` — assigning UnsafeMutableRawPointer? to Decimal? → the generated wrapper
        // fails to compile. The method must route to the SAME fatalError trap stub as the non-optional
        // inout case, not EmitMethodImplementation's optional-value param arm.
        var foundation = new ModuleTypeDatabase("Foundation", "/fake/Foundation.framework/Foundation");
        var decimalName = SwiftTypeName.FromModuleQualifiedName("Foundation.Decimal");
        foundation.RegisterType(decimalName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSDecimalNumber"),
            SwiftTypeName = decimalName,
            MetadataAccessor = "$s10Foundation7DecimalVMa",
            Flags = TypeRecordFlags.ObjCBridgeable | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct,
        });
        _typeDatabase.AddModuleDatabase(foundation);

        var protocol = CreateSimpleProtocol("AmountMutator");
        protocol.Methods.Add(CreateMethodWithInOutParam(
            "update", "amount", new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Foundation.Decimal"))));

        var output = EmitProtocolExtension(protocol);

        // Routed to the trap stub, NOT the optional-value dispatch arm (no dangling amountRef writeback).
        Assert.Contains("inout ObjC-bridgeable parameter cannot be dispatched", output);
        Assert.Contains("amount: inout", output);
        Assert.DoesNotContain("amountRef", output);
        Assert.DoesNotContain("amountNS", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_InOutObjCBridgeableParam_RetainsVtableSlot()
    {
        // The inout ObjC-bridgeable method routes its witness body to a fatalError trap stub, but
        // — unlike the Self-typed / generic / non-dispatchable-closure stub categories that skip
        // their slot — it deliberately KEEPS its vtable slot. The trapping witness never reads the
        // slot and the C# receiver compiles via the ordinary objc-param path, so retaining it keeps
        // the Swift vtable struct and the C# vtable buffer in lock-step. Pin that decision so a
        // future "tidy-up" that adds a slot skip here without the matching C#-side skip can't
        // silently desync the two layouts.
        var foundation = new ModuleTypeDatabase("Foundation", "/fake/Foundation.framework/Foundation");
        var decimalName = SwiftTypeName.FromModuleQualifiedName("Foundation.Decimal");
        foundation.RegisterType(decimalName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSDecimalNumber"),
            SwiftTypeName = decimalName,
            MetadataAccessor = "$s10Foundation7DecimalVMa",
            Flags = TypeRecordFlags.ObjCBridgeable | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct,
        });
        _typeDatabase.AddModuleDatabase(foundation);

        var protocol = CreateSimpleProtocol("AmountMutator");
        protocol.Methods.Add(CreateMethodWithInOutParam("update", "amount", new NamedTypeSpec("Foundation.Decimal")));

        var output = EmitVtableStruct(protocol);

        Assert.Contains("func_update_0", output);
    }

    private static MethodDecl CreateMethodWithInOutParam(string name, string paramLabel, TypeSpec paramType)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new ArgumentDecl
                {
                    Name = paramLabel,
                    SwiftTypeSpec = paramType,
                    PrivateName = paramLabel,
                    IsInOut = true,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    [Fact]
    public void EmitProtocolVtableStruct_OptionalClosureParam_IncludesVtableField()
    {
        // Optional<Closure> is dispatchable in 4b — vtable field emitted as a closure slot.
        var protocol = CreateSimpleProtocol("Notifier");
        protocol.Methods.Add(CreateMethodWithOptionalClosureParam("notify", "handler"));

        var output = EmitVtableStruct(protocol);

        Assert.Contains("func_notify_0", output);
    }

    [Fact]
    public void EmitProtocolVtableStruct_OptionalClosureParam_UsesOptionalRawPointerSlot()
    {
        // Optional<Closure> nil round-trips via UnsafeRawPointer? slot type (not a sentinel).
        // Nil → 0 at C# boundary.
        var protocol = CreateSimpleProtocol("Notifier");
        protocol.Methods.Add(CreateMethodWithOptionalClosureParam("notify", "handler"));

        var output = EmitVtableStruct(protocol);

        Assert.Contains("UnsafeRawPointer?", output);
    }

    private static MethodDecl CreateMethodWithReturnTypedClosureParam(string name, string paramLabel, string returnTypeName)
    {
        var closure = new ClosureTypeSpec
        {
            Arguments = TupleTypeSpec.Empty,
            ReturnType = new NamedTypeSpec(returnTypeName)
        };
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new ArgumentDecl
                {
                    Name = paramLabel,
                    SwiftTypeSpec = closure,
                    PrivateName = paramLabel,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static MethodDecl CreateMethodWithClosureParam(string name, string paramLabel)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type (void)
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                // Closure parameter
                new ArgumentDecl
                {
                    Name = paramLabel,
                    SwiftTypeSpec = CreateEscapingClosure(),
                    PrivateName = paramLabel,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    [Fact]
    public void EmitProtocolExtension_OptionalClosureParam_DispatchesWithoutEscapingAnnotation()
    {
        // Optional<Closure> is always escaping in Swift — @escaping annotation is invalid
        // syntax on Optional types. 4b dispatches Optional<Closure> shapes; the rendered
        // signature must NOT carry @escaping, and the method must NOT route through fatalError.
        var protocol = CreateSimpleProtocol("Notifier");
        protocol.Methods.Add(CreateMethodWithOptionalClosureParam("notify", "handler"));

        var output = EmitProtocolExtension(protocol);

        Assert.Contains("public func notify(", output);
        Assert.DoesNotContain("@escaping", output);
        Assert.DoesNotContain("closure method 'notify' cannot be dispatched", output);
    }

    [Fact]
    public void EmitProtocolExtension_OptionalClosureParam_UsesInoutBytesNotUnwrap()
    {
        // Regression sentinel for the Optional<Closure> reabstraction trap.
        //
        // The Optional's payload stores closures in Swift's generic memory
        // abstraction (`Iegr_`, @out-Void). Unwrapping via `if var localVar =
        // param { ... }` materializes a concrete `() -> Void` local — Swift
        // inserts a `$sIeg_ytIegr_TR` reabstraction thunk + partial-application
        // context. The temporary partial-app ctx is freed on scope exit, so
        // captured (fn, ctx) bytes dangle. The fix reads the Optional's raw
        // bytes directly via inout binding, since Optional<Closure> shares the
        // closure's 2-word layout via the function-pointer extra-inhabitant.
        //
        // This test asserts the emitter NEVER regresses to the `if var ... =`
        // unwrap pattern for Optional<Closure> protocol method params.
        var protocol = CreateSimpleProtocol("Notifier");
        protocol.Methods.Add(CreateMethodWithOptionalClosureParam("notify", "handler"));

        var output = EmitProtocolExtension(protocol);

        // Inout-binding pattern: shadow Optional value then read bytes through it.
        Assert.Contains("var handlerLocal = handler", output);
        Assert.Contains("withUnsafeBytes(of: &handlerLocal)", output);

        // Optional-typed pointer slots so `.none` round-trips as nil to the
        // C# trampoline (NOT non-optional UnsafeRawPointer, which would store
        // an arbitrary nil-sentinel value).
        Assert.Contains("UnsafeRawPointer?.self", output);

        // Regression trap shapes — none of these may reappear.
        Assert.DoesNotContain("if var handlerLocal = handler", output);
        Assert.DoesNotContain("if let handlerLocal = handler", output);
    }

    [Fact]
    public void EmitProtocolExtension_AsyncClosureMethod_IncludesAsync()
    {
        var protocol = CreateSimpleProtocol("AsyncDelegate");
        protocol.Methods.Add(CreateAsyncMethodWithClosureParam("onComplete", "handler"));

        var output = EmitProtocolExtension(protocol);

        Assert.Contains("public func onComplete(", output);
        Assert.Contains(" async", output);
        Assert.Contains("fatalError", output);
    }

    [Fact]
    public void EmitProtocolExtension_GenericClosureMethod_IncludesGenericClause()
    {
        var protocol = CreateSimpleProtocol("GenericDelegate");
        protocol.Methods.Add(CreateGenericMethodWithClosureParam("transform", "handler"));

        var output = EmitProtocolExtension(protocol);

        Assert.Contains("public func transform<_G0>(", output);
        Assert.Contains("fatalError", output);
    }

    [Fact]
    public void EmitProtocolExtension_AsyncGenericMethod_IncludesAsync()
    {
        // Non-closure method with method-level generics + async — tests EmitMethodLevelGenericStub
        var protocol = CreateSimpleProtocol("AsyncResolver");
        var method = CreateMethodWithMethodLevelGeneric("resolve");
        method.IsAsync = true;
        protocol.Methods.Add(method);

        var output = EmitProtocolExtension(protocol);

        Assert.Contains(" async", output);
        Assert.Contains("_G0", output);
    }

    [Fact]
    public void EmitProtocolExtension_ClosureMethodWithMetatype_RewritesGenericParam()
    {
        // Closure method with a generic metatype param: func register<T>(_ type: T.Type, handler: () -> T)
        // Tests that the closure stub's RenderTypeSpec rewrites τ_1_0.Type → _G0.Type
        var protocol = CreateSimpleProtocol("Registry");
        var method = CreateMethodWithClosureParam("register", "handler");
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        // Add a metatype parameter before the closure
        method.CSSignature.Insert(1, new ArgumentDecl
        {
            Name = "type",
            SwiftTypeSpec = new NamedTypeSpec("τ_1_0.Type"),
            PrivateName = "type",
            IsInOut = false,
            IsGeneric = true,
            ParentDecl = null,
            ModuleDecl = null
        });
        protocol.Methods.Add(method);

        var output = EmitProtocolExtension(protocol);

        // Should rewrite τ_1_0.Type → _G0.Type
        Assert.Contains("_G0.Type", output);
        Assert.DoesNotContain("τ_1_0", output);
    }

    private static MethodDecl CreateMethodWithOptionalClosureParam(string name, string paramLabel)
    {
        var closureSpec = CreateEscapingClosure();
        var optionalSpec = new NamedTypeSpec("Swift.Optional", closureSpec);

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new ArgumentDecl
                {
                    Name = paramLabel,
                    SwiftTypeSpec = optionalSpec,
                    PrivateName = paramLabel,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static MethodDecl CreateAsyncMethodWithClosureParam(string name, string paramLabel)
    {
        var method = CreateMethodWithClosureParam(name, paramLabel);
        method.IsAsync = true;
        return method;
    }

    private static MethodDecl CreateGenericMethodWithClosureParam(string name, string paramLabel)
    {
        // Generic-typed closure parameter (τ_1_0) -> Void. Two reasons for the
        // generic-in-signature shape:
        //   1. HasOnlyMethodLevelGenerics is detected by walking signature types,
        //      not method.GenericParameters — without τ_1_0 in the signature, the
        //      method would slip through the generic gate.
        //   2. The closure shape (τ_1_0) -> Void is off-surface for dispatch
        //      (only () -> Void dispatches), so it still routes through
        //      the fatalError stub path the test asserts on.
        var method = CreateMethodWithClosureParam(name, paramLabel);
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("τ_1_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var genericClosure = new ClosureTypeSpec
        {
            Arguments = new NamedTypeSpec("τ_1_0"),
            ReturnType = TupleTypeSpec.Empty
        };
        genericClosure.Attributes.Add(new TypeSpecAttribute("escaping"));
        method.CSSignature[1] = new ArgumentDecl
        {
            Name = method.CSSignature[1].Name,
            SwiftTypeSpec = genericClosure,
            PrivateName = method.CSSignature[1].PrivateName,
            IsInOut = false,
            IsGeneric = true,
            ParentDecl = null,
            ModuleDecl = null
        };
        return method;
    }

    private static MethodDecl CreateMethodWithIntArgClosureParam(string name, string paramLabel)
    {
        var closure = new ClosureTypeSpec
        {
            Arguments = new NamedTypeSpec("Swift.Int"),
            ReturnType = TupleTypeSpec.Empty
        };
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new ArgumentDecl
                {
                    Name = paramLabel,
                    SwiftTypeSpec = closure,
                    PrivateName = paramLabel,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static MethodDecl CreateMethodWithDispatchableClosureAndNonClosureValueParam(
        string name,
        string closureLabel,
        TypeSpec extraParamType,
        string extraParamLabel)
    {
        var closure = CreateEscapingClosure();
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new ArgumentDecl
                {
                    Name = closureLabel,
                    SwiftTypeSpec = closure,
                    PrivateName = closureLabel,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                },
                new ArgumentDecl
                {
                    Name = extraParamLabel,
                    SwiftTypeSpec = extraParamType,
                    PrivateName = extraParamLabel,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static ClosureTypeSpec CreateEscapingClosure()
    {
        var closure = new ClosureTypeSpec
        {
            Arguments = TupleTypeSpec.Empty,
            ReturnType = TupleTypeSpec.Empty
        };
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));
        return closure;
    }

    #endregion

    #region Missing Requirements / Convention-C Skip Tests

    [Fact]
    public void EmitProtocolConformance_HasMissingRequirements_SkipsConformance()
    {
        // Protocol with a requirement that failed ABI parsing (e.g., `some` parameter
        // causes GenericSignatureParser count mismatch). The conformance should be
        // skipped entirely because the emitter can't generate stubs for unknown requirements.
        var protocol = CreateProtocolWithMethod("RowAdapter", "addingScopes");
        protocol.HasMissingRequirements = true;

        var output = EmitConformance(protocol);

        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void EmitProtocolConformance_HasConventionCClosureParameters_SkipsConformance()
    {
        // Protocol with @convention(c) closure parameters. ABI JSON lacks convention
        // info, so the closure stub emits @escaping instead of @convention(c).
        var protocol = CreateProtocolWithMethod("FTS5Tokenizer", "tokenize");
        protocol.HasConventionCClosureParameters = true;

        var output = EmitConformance(protocol);

        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void EmitProtocolConformance_NoMissingRequirements_EmitsConformance()
    {
        // Normal protocol with all requirements present — should emit conformance normally.
        var protocol = CreateProtocolWithMethod("ValidProtocol", "doWork");
        protocol.HasMissingRequirements = false;
        protocol.HasConventionCClosureParameters = false;

        var output = EmitConformance(protocol);

        Assert.Contains("extension EveryProtocol: TestModule.ValidProtocol", output);
    }

    [Fact]
    public void WillSkipConformance_HasMissingRequirements_ReturnsTrue()
    {
        // WillSkipConformance should detect missing requirements during pre-scan
        var protocol = CreateProtocolWithMethod("RowAdapter", "addingScopes");
        protocol.HasMissingRequirements = true;

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        // Verify the protocol was added to skipped set by emitting — should be empty
        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void WillSkipConformance_HasConventionCClosureParameters_ReturnsTrue()
    {
        // WillSkipConformance should detect convention-C protocols during pre-scan
        var protocol = CreateProtocolWithMethod("FTS5Tokenizer", "tokenize");
        protocol.HasConventionCClosureParameters = true;

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void WillSkipConformance_RequiredSpiProperty_ReturnsTrue()
    {
        // A protocol whose required Var is `@_spi`-protected loses its
        // witness in the EveryProtocol extension (PropertyHandler skips SPI
        // properties). The conformance must be skipped to keep the wrapper buildable.
        var protocol = CreateProtocolWithProperty("MaterialFunction", "__linkSPI",
            hasGetter: true, hasSetter: false);
        protocol.Properties[0].IsProtocolRequirement = true;
        protocol.Properties[0].IsSpiProtected = true;

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void WillSkipConformance_RequiredModuleInternalProperty_DoesNotSkip()
    {
        // Narrowing: IsModuleInternal must NOT trigger the gate. The parser's
        // negative-space heuristic (IsInternalFromPublicMemberNames) flags protocol
        // requirement vars as internal because the swiftinterface body lists them
        // without a leading `public` keyword. Those conformances already emit working
        // witnesses on baseline; treating
        // IsModuleInternal as a suppression signal regresses them. Only IsSpiProtected
        // is consulted by the gate.
        var protocol = CreateProtocolWithProperty("InternalShape", "value",
            hasGetter: true, hasSetter: false);
        protocol.Properties[0].IsProtocolRequirement = true;
        protocol.Properties[0].IsModuleInternal = true;

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        var output = EmitConformance(protocol);
        Assert.Contains("extension EveryProtocol: TestModule.InternalShape", output);
    }

    [Fact]
    public void WillSkipConformance_RequiredSpiMethod_ReturnsTrue()
    {
        // Same gate over methods. A required Function with IsSpiProtected
        // would also yield an unsatisfiable extension.
        var protocol = CreateProtocolWithMethod("Logger", "log");
        protocol.Methods[0].IsProtocolRequirement = true;
        protocol.Methods[0].IsSpiProtected = true;

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void WillSkipConformance_SubscriptHasMethodLevelGenericDependentMember_ReturnsTrue()
    {
        // Mirrors RealityFoundation.MeshBufferContainer's
        //   subscript<S: MeshBufferSemantic>(_: S) -> MeshBuffer<S.Element>?
        // The Self-typed stub path substitutes S → EveryProtocol and S.Element → Any,
        // producing `subscript(_: EveryProtocol) -> MeshBuffer<Any>?` which does not
        // satisfy the protocol's generic requirement. The conformance must be skipped.
        var protocol = CreateSimpleProtocol("MeshBufferContainer");
        // Add a non-generic property so the protocol has implementable members and
        // would otherwise pass the empty-protocol gate.
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("name_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        // MeshBuffer<S.Element>? — Optional<MeshBuffer<assoc("S","Element")>>.
        var elementRef = new AssociatedTypeReferenceSpec("S", "Element");
        var meshBuffer = new NamedTypeSpec("RealityFoundation.MeshBuffer");
        meshBuffer.GenericParameters.Add(elementRef);
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(meshBuffer);

        protocol.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s_test",
            ReturnTypeSpec = optional,
            IndexParameters = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "index0",
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("S"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        });

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void WillSkipConformance_SubscriptDottedNamedTypeSpec_ReturnsTrue()
    {
        // The parser materializes nested DependentMember nodes via TypeSpecParser.Parse
        // on the outer PrintedName, which produces NamedTypeSpec("S.Element") rather than
        // an AssociatedTypeReferenceSpec. Verify the gate also catches the dotted-name
        // shape so MeshBufferContainer's
        //   subscript<S: MeshBufferSemantic>(_: S) -> MeshBuffer<S.Element>?
        // is skipped after the Optional<TypeNameAlias> parser fix unblocks emission.
        var protocol = CreateSimpleProtocol("DottedContainer");
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("name_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var dottedAssoc = new NamedTypeSpec("S.Element");
        var meshBuffer = new NamedTypeSpec("RealityFoundation.MeshBuffer");
        meshBuffer.GenericParameters.Add(dottedAssoc);
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(meshBuffer);

        protocol.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s_test",
            ReturnTypeSpec = optional,
            IndexParameters = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "index0",
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("S"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        });

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void WillSkipConformance_SubscriptBareGenericReturnType_ReturnsTrue()
    {
        // Bare subscript-scoped generic: subscript<T>(key: String) -> T?
        // The return type is Swift.Optional<T> where T is a NamedTypeSpec("T") —
        // a bare protocol-level-looking single letter that is actually bound at
        // the subscript itself. SubscriptDecl carries no generic clause, so the
        // self-typed stub would render T as EveryProtocol and emit a non-generic
        // subscript that cannot satisfy the original requirement. Skip instead.
        var protocol = CreateSimpleProtocol("BareGenericSubscriptProto");
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("name_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var bareT = new NamedTypeSpec("T");
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(bareT);

        protocol.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s_test",
            ReturnTypeSpec = optional,
            IndexParameters = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "key",
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        });

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void WillSkipConformance_SubscriptCanonicalTauReturnType_ReturnsTrue()
    {
        // Canonical method/subscript-scope generic spelling: τ_1_0 surfaces directly
        // when CreateTypeSpec walks ABI nodes that already use the τ form. The gate
        // must NOT rely on the sugar (T/S) — it must trip on τ_1_* too, otherwise the
        // same broken non-generic witness emits.
        var protocol = CreateSimpleProtocol("CanonicalTauProto");
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("name_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var tau = new NamedTypeSpec("τ_1_0");
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(tau);

        protocol.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s_test",
            ReturnTypeSpec = optional,
            IndexParameters = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "key",
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        });

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void WillSkipConformance_SubscriptCanonicalTauDottedMember_ReturnsTrue()
    {
        // τ_1_0.Element form — canonical dotted spelling for a dependent member on a
        // subscript-scope generic. Must trip the gate the same as the S.Element shape.
        var protocol = CreateSimpleProtocol("CanonicalTauDottedProto");
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("name_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var dottedTau = new NamedTypeSpec("τ_1_0.Element");
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(dottedTau);

        protocol.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s_test",
            ReturnTypeSpec = optional,
            IndexParameters = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "index0",
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("τ_1_0"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        });

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void WillSkipConformance_SubscriptSelfTypedDependentMember_StillEmits()
    {
        // Counterpart: a subscript whose dependent member is on Self (Self.Element)
        // is the existing Self-typed-stub path — substitution produces a valid witness.
        // The new gate must NOT trip on Self/τ_0_* bases.
        var protocol = CreateSimpleProtocol("SelfElementContainer");
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("name_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });

        var selfElement = new AssociatedTypeReferenceSpec("Self", "Element");
        protocol.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s_test",
            ReturnTypeSpec = selfElement,
            IndexParameters = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "index0",
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            IsStatic = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        });

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        var output = EmitConformance(protocol);
        Assert.Contains("extension EveryProtocol: TestModule.SelfElementContainer", output);
    }

    [Fact]
    public void WillSkipConformance_HasUnsatisfiedHiddenRequirements_ReturnsTrue()
    {
        // Supplement: swift-api-digester strips __-prefixed protocol requirements
        // (e.g. RealityFoundation.MaterialFunction.__linkSPI) from the ABI JSON, so the
        // parser never sees them. The Swift compiler still enforces them at conformance
        // type-check time. The SwiftSyntax host surfaces these from the swiftinterface;
        // the emitter must skip the conformance.
        var protocol = CreateProtocolWithMethod("MaterialFunction", "name");
        protocol.HasUnsatisfiedHiddenRequirements = true;

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void EmitProtocolConformance_HasUnsatisfiedHiddenRequirements_SkipsConformance()
    {
        // Mirror of the WillSkipConformance test through the direct emission path —
        // verifies the same gate fires when the pre-scan was bypassed (e.g. tests that
        // construct a ProtocolDecl ad-hoc and invoke EmitProtocolConformance).
        var protocol = CreateProtocolWithMethod("MaterialFunction", "name");
        protocol.HasUnsatisfiedHiddenRequirements = true;

        var output = EmitConformance(protocol);

        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void WillSkipConformance_HasMissingTbdMethodDescriptors_ReturnsTrue()
    {
        // Mac Catalyst Apple-bug pattern: the macCatalyst swiftinterface declares a
        // protocol requirement whose method-descriptor ($mangled+Tq) symbol isn't
        // exported in the framework's TBD. Apple's
        // LiveCommunicationKit.ConversationManagerDelegate.didActivate / didDeactivate
        // are the canonical example. The synthesized EveryProtocol witness table would
        // reference an unresolved descriptor and the wrapper link would fail. The
        // parser sets HasMissingTbdMethodDescriptors during ABI parsing; the emitter
        // must skip the conformance so the wrapper links.
        var protocol = CreateProtocolWithMethod("ConversationManagerDelegate", "didActivate");
        protocol.HasMissingTbdMethodDescriptors = true;

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        var output = EmitConformance(protocol);
        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void EmitProtocolConformance_HasMissingTbdMethodDescriptors_SkipsConformance()
    {
        // Mirror of the WillSkipConformance test through the direct emission path —
        // verifies the same gate fires when the pre-scan was bypassed.
        var protocol = CreateProtocolWithMethod("ConversationManagerDelegate", "didActivate");
        protocol.HasMissingTbdMethodDescriptors = true;

        var output = EmitConformance(protocol);

        Assert.DoesNotContain("extension EveryProtocol", output);
    }

    [Fact]
    public void WillSkipConformance_NonRequiredSpiProperty_StillEmits()
    {
        // Extension defaults (protocolReq=false) that happen to be SPI must NOT
        // trigger the gate — they don't need a witness; Swift provides them via
        // the default implementation. Adds a non-SPI required getter so the
        // conformance has implementable members.
        var protocol = CreateProtocolWithProperty("NormalProto", "value",
            hasGetter: true, hasSetter: false);
        protocol.Properties[0].IsProtocolRequirement = true;
        // Add a second SPI property that is NOT a requirement (extension default).
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "_spiHelper",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("_spiHelper_get") }
            },
            ParentDecl = null,
            ModuleDecl = null,
            IsProtocolRequirement = false,
            IsSpiProtected = true
        });

        _emitter.PreScanProtocols(new List<ProtocolDecl> { protocol });

        var output = EmitConformance(protocol);
        Assert.Contains("extension EveryProtocol: TestModule.NormalProto", output);
    }

    #endregion

    #region Swift Keyword Escaping Tests

    [Theory]
    [InlineData("extension")]
    [InlineData("class")]
    [InlineData("func")]
    [InlineData("var")]
    [InlineData("import")]
    public void EmitMethodImplementation_SwiftKeywordParamName_EscapesWithBackticks(string keyword)
    {
        // A protocol method whose parameter name is a Swift keyword should
        // generate backtick-escaped references in the function body.
        var protocol = CreateSimpleProtocol("FileResolver");
        protocol.Methods.Add(CreateMethodDeclWithParam("resolve", keyword, "Swift.String"));

        var output = EmitConformance(protocol);

        // The function body should use backtick escaping for the value reference. A Swift.String
        // param is stored through an explicitly-typed pointer (RequiresExplicitValuePointer — avoids
        // the implicit string-to-pointer conversion), so the escaped reference appears as the
        // initialize(to:) source rather than a `var xCopy = x` assignment.
        Assert.Contains($"initialize(to: `{keyword}`)", output);
        // The function signature should use the keyword as a parameter label (valid Swift)
        Assert.Contains($"{keyword}: Swift.String", output);
    }

    [Fact]
    public void EmitMethodImplementation_NonKeywordParamName_NoBackticks()
    {
        var protocol = CreateSimpleProtocol("TestProto");
        protocol.Methods.Add(CreateMethodDeclWithParam("process", "fileName", "Swift.String"));

        var output = EmitConformance(protocol);

        // A non-keyword Swift.String param flows unescaped through the explicit-pointer storage.
        Assert.Contains("initialize(to: fileName)", output);
        Assert.DoesNotContain("`fileName`", output);
    }

    #endregion

    #region Unsatisfied Stdlib Protocol Gate Tests

    [Theory]
    [InlineData("Hashable")]
    [InlineData("Equatable")]
    [InlineData("Comparable")]
    public void InheritsUnsatisfiedStdlibProtocol_DirectInheritance_ReturnsTrue(string protocolName)
    {
        var protocol = CreateProtocolWithInheritance("MyProto", $"Swift.{protocolName}");

        Assert.True(EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(protocol));
    }

    [Fact]
    public void InheritsUnsatisfiedStdlibProtocol_IndirectHashableViaCustomProtocol_ReturnsTrue()
    {
        // Protocol chain: RichTextInsertable -> Swift.Hashable (via allProtocols lookup)
        var hashableProto = CreateSwiftStdlibProtocol("Hashable");
        var customProto = CreateProtocolWithInheritance("RichTextInsertable", "Swift.Hashable");

        var allProtocols = new List<ProtocolDecl> { hashableProto, customProto };

        Assert.True(EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(customProto, allProtocols));
    }

    [Fact]
    public void InheritsUnsatisfiedStdlibProtocol_NoUnsatisfiedInheritance_ReturnsFalse()
    {
        var protocol = CreateProtocolWithInheritance("MyProto", "Swift.Sendable");

        Assert.False(EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(protocol));
    }

    [Theory]
    [InlineData("Hashable")]
    [InlineData("Equatable")]
    [InlineData("Comparable")]
    public void InheritsUnsatisfiedStdlibProtocol_LibraryDefinedSameName_ReturnsFalse(string protocolName)
    {
        // A library-defined protocol named "Hashable" (module != Swift) should NOT
        // be treated as an unsatisfied stdlib protocol.
        var protocol = CreateProtocolWithInheritance(protocolName);

        Assert.False(EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(protocol));
    }

    [Fact]
    public void InheritsUnsatisfiedStdlibProtocol_InheritsLibraryDefinedHashable_ReturnsFalse()
    {
        // A protocol that inherits from a library-local "Hashable" (not Swift.Hashable)
        // should NOT be treated as inheriting an unsatisfied stdlib protocol.
        // The unqualified name "Hashable" in InheritedProtocols must be resolved
        // via allProtocols to check the actual module.
        var libraryHashable = CreateProtocolWithInheritance("Hashable");
        var myProto = CreateProtocolWithInheritance("MyProto", "Hashable");

        var allProtocols = new List<ProtocolDecl> { libraryHashable, myProto };

        Assert.False(EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(myProto, allProtocols));
    }

    [Fact]
    public void InheritsUnsatisfiedStdlibProtocol_InheritsSwiftQualifiedHashable_ReturnsTrue()
    {
        // A protocol that inherits from "Swift.Hashable" (explicitly qualified)
        // should be correctly identified without needing allProtocols lookup.
        var myProto = CreateProtocolWithInheritance("MyProto", "Swift.Hashable");

        Assert.True(EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(myProto));
    }

    #endregion

    #region Forward-Safe Reverse-Impossible Reason Gate Tests

    [Fact]
    public void HasForwardSafeReverseImpossibleReason_HiddenRequirementNonSuperclass_ReturnsTrue()
    {
        // Population A (the RealityFoundation Material shape): a non-class-superclass protocol
        // whose reverse conformance is blocked ONLY by a stripped `__`-prefixed hidden
        // requirement. The existential is still a valid read target, so it earns a forward-only
        // proxy. (Not reproducible in BindingTests — the test toolchain keeps `__` names — so
        // this unit test is its coverage.)
        var protocol = CreateProtocolWithMethod("MaterialFunction", "name");
        protocol.HasUnsatisfiedHiddenRequirements = true;

        Assert.True(EveryProtocolEmitter.HasForwardSafeReverseImpossibleReason(protocol, _typeDatabase));
    }

    [Theory]
    [InlineData("Equatable")]
    [InlineData("Hashable")]
    [InlineData("CustomStringConvertible")]
    public void HasForwardSafeReverseImpossibleReason_StdlibInheritanceNonSuperclass_ReturnsTrue(string stdlibProtocol)
    {
        // Population B (the RealityFoundation PhysicsJoint shape, and the deterministic
        // BindingTests repro): a non-class-superclass protocol that inherits a stdlib protocol
        // EveryProtocol can't witness. Reverse-impossible, forward-readable for the non-Self
        // members.
        var protocol = CreateProtocolWithInheritance("ForwardReadable", $"Swift.{stdlibProtocol}");

        Assert.True(EveryProtocolEmitter.HasForwardSafeReverseImpossibleReason(protocol, _typeDatabase));
    }

    [Fact]
    public void HasForwardSafeReverseImpossibleReason_ClassSuperclassEvenWithHiddenRequirement_ReturnsFalse()
    {
        // Disjointness: a class-superclass-constrained protocol is the ORIGINAL read-only
        // population, admitted by ModuleHandler's superclass arm — NOT this forward-safe arm.
        // The `!HasClassSuperclassRequirement` guard short-circuits even when a forward-safe
        // reason (hidden requirement) is also present, so the two arms never both fire.
        var uikit = new ModuleTypeDatabase("UIKit", "/fake/UIKit.framework/UIKit");
        var gestureName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIGestureRecognizer");
        uikit.RegisterType(gestureName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "UIGestureRecognizer"),
            SwiftTypeName = gestureName,
            MetadataAccessor = "$sSo19UIGestureRecognizerCMa",
            Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        _typeDatabase.AddModuleDatabase(uikit);

        var protocol = CreateProtocolWithMethod("GestureBackedDelegate", "doSomething");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("UIKit.UIGestureRecognizer"));
        protocol.HasUnsatisfiedHiddenRequirements = true;

        Assert.False(EveryProtocolEmitter.HasForwardSafeReverseImpossibleReason(protocol, _typeDatabase));
    }

    [Fact]
    public void HasForwardSafeReverseImpossibleReason_ForwardUnsafeReasonOnly_ReturnsFalse()
    {
        // A forward-UNSAFE skip reason (here: missing TBD method descriptors) must NOT admit a
        // forward-only proxy. The descriptor is genuinely absent from the framework, so a forward
        // read would move the failure to wrapper link / runtime rather than projecting cleanly.
        var protocol = CreateProtocolWithMethod("ConversationManagerDelegate", "didActivate");
        protocol.HasMissingTbdMethodDescriptors = true;

        Assert.False(EveryProtocolEmitter.HasForwardSafeReverseImpossibleReason(protocol, _typeDatabase));
    }

    [Fact]
    public void HasForwardSafeReverseImpossibleReason_NoBlockingReason_ReturnsFalse()
    {
        // A protocol with no reverse-impossible reason at all is not a read-only proxy — it gets
        // a normal EveryProtocol conformance, so the forward-safe arm must not claim it.
        var protocol = CreateProtocolWithMethod("PlainProtocol", "doSomething");

        Assert.False(EveryProtocolEmitter.HasForwardSafeReverseImpossibleReason(protocol, _typeDatabase));
    }

    [Fact]
    public void HasForwardSafeReverseImpossibleReason_StdlibInheritancePlusForwardUnsafeReason_ReturnsFalse()
    {
        // Mixed case: a forward-SAFE reason (inherits Swift.CustomStringConvertible) coexists with
        // a forward-UNSAFE one (a required method's TBD descriptor is absent). The forward read of
        // that member would move the failure to wrapper link / runtime, so the predicate must fail
        // closed and keep the throwing-stub suppression — the forward-safe reason alone is NOT
        // enough to admit when an unsafe reason is also present.
        var protocol = CreateProtocolWithInheritance("ForwardReadable", "Swift.CustomStringConvertible");
        protocol.HasMissingTbdMethodDescriptors = true;

        Assert.False(EveryProtocolEmitter.HasForwardSafeReverseImpossibleReason(protocol, _typeDatabase));
    }

    [Fact]
    public void HasForwardSafeReverseImpossibleReason_HiddenRequirementPlusConventionCClosure_ReturnsFalse()
    {
        // Mixed case, other axis: the forward-safe hidden-requirement reason (Population A) coexists
        // with a @convention(c) closure parameter (forward-unsafe). Still fail closed.
        var protocol = CreateProtocolWithMethod("MaterialFunction", "name");
        protocol.HasUnsatisfiedHiddenRequirements = true;
        protocol.HasConventionCClosureParameters = true;

        Assert.False(EveryProtocolEmitter.HasForwardSafeReverseImpossibleReason(protocol, _typeDatabase));
    }

    [Fact]
    public void HasForwardSafeReverseImpossibleReason_StdlibInheritancePlusMixedGenericMethod_ReturnsFalse()
    {
        // Mixed case, method-generic axis. The base protocol is admissible: it inherits
        // Swift.CustomStringConvertible (forward-safe) and carries a plain `value: Int` property.
        // Adding a method-level-generic requirement alongside that non-generic member makes it
        // IsMixedGenericProtocol, for which the Swift wrapper emits NO witness-dispatch accessors
        // for the WHOLE protocol (EmitWitnessDispatchFunctions is gated protocol-wide). The C#
        // forward-read proxy gates per-member, so it would still emit the plain property's
        // NativeMethods P/Invoke into a never-generated @_cdecl symbol -> EntryPointNotFoundException.
        // The predicate must fail closed once the generic requirement is present.
        var protocol = CreateProtocolWithInheritance("ForwardReadableMixedGeneric", "Swift.CustomStringConvertible");
        Assert.True(EveryProtocolEmitter.HasForwardSafeReverseImpossibleReason(protocol, _typeDatabase));

        protocol.Methods.Add(CreateMethodWithMethodLevelGeneric("transform"));
        Assert.False(EveryProtocolEmitter.HasForwardSafeReverseImpossibleReason(protocol, _typeDatabase));
    }

    #endregion

    /// <summary>
    /// Creates a protocol declaration as if it's from the Swift standard library.
    /// </summary>
    private static ProtocolDecl CreateSwiftStdlibProtocol(string name)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"Swift.{name}"),
            MangledName = $"$ss{name.Length}{name}P",
            HasSelfRequirement = false,
            IsClassBound = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }
}
