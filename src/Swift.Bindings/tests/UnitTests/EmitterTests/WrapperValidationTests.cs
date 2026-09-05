// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the Path-3 concrete-class fallback in
/// <see cref="WrapperValidation.IsOptionalWithReferenceInner"/>.
///
/// The first two paths in the helper already cover (1) types with a TypeRecord
/// of the right Kind, and (2) the broad Apple ObjC fallback gated on
/// <see cref="MarshallingHelpers.IsOptionalObjCBridged"/> + an ObjC class
/// prefix. The gap exposed by RealityFoundation / RealityKit is the third
/// case: cross-module Swift classes that ship without an XML database AND
/// whose names do not start with an ObjC class prefix (e.g.
/// <c>RealityFoundation.Entity</c>). Both existing paths fall through and the
/// <c>@_cdecl</c> wrapper renders the parameter bare as
/// <c>Optional&lt;Entity&gt;</c> rather than <c>UnsafeMutableRawPointer?</c>,
/// which swiftc rejects with "type is not representable in Objective-C".
///
/// The fix routes these modules through a new
/// <c>concreteClassFallback</c> flag declared on the module entry in
/// <c>apple-frameworks.json</c>. The tests below pin the public contract of
/// the helper through <see cref="CdeclParamMapper.IsOptionalWithReferenceInner"/>
/// (the re-export the mapper exposes to callers).
/// </summary>
public class WrapperValidationTests
{
    [Fact]
    public void IsOptionalWithReferenceInner_ConcreteClassFallback_NoTypeRecord_NoObjCPrefix_ReturnsTrue()
    {
        // RealityFoundation.Entity: no XML/TypeRecord, name has no ObjC prefix.
        // Both Path 1 (TypeRecord lookup) and Path 2 (ObjC-prefix fallback) miss.
        // Path 3 (concrete-class fallback for known concrete-class modules)
        // must catch it so the @_cdecl wrapper renders UnsafeMutableRawPointer?.
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("RealityFoundation.Entity"));

        Assert.True(
            CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb),
            "RealityFoundation.Entity must classify as reference inner via Path 3");
    }

    [Fact]
    public void IsOptionalWithReferenceInner_ConcreteClassFallback_RealityKit_ReturnsTrue()
    {
        // RealityKit ships concrete Swift classes (ARKitSession, AnchorEntity, ...)
        // some of which do not match the "RE" objcPrefix. Path 3 must still fire.
        // Use a name that doesn't match the RE prefix so we exercise Path 3, not Path 2.
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("RealityKit.AnchorEntity"));

        Assert.True(
            CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb),
            "RealityKit.AnchorEntity must classify as reference inner via Path 3");
    }

    [Fact]
    public void IsOptionalWithReferenceInner_ConcreteClassFallback_SceneKit_ReturnsTrue()
    {
        // SceneKit ships concrete Swift classes that don't always match the "SC" prefix
        // (the framework hosts both SCN-prefixed ObjC classes and concrete Swift classes).
        // Use a name with no objcPrefix match so Path 2 doesn't fire — Path 3 must.
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("SceneKit.ProgramNode"));

        Assert.True(
            CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb),
            "SceneKit.ProgramNode must classify as reference inner via Path 3");
    }

    [Fact]
    public void IsOptionalWithReferenceInner_ConcreteClassFallback_KnownValueType_ReturnsFalse()
    {
        // SCNVector3 is in apple-frameworks.json's valueTypes list for SceneKit.
        // Path 3 must respect that exclusion — value types stay value-shaped.
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("SceneKit.SCNVector3"));

        Assert.False(
            CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb),
            "Path 3 must defer to AppleFrameworkRegistry's known-value-type list");
    }

    [Fact]
    public void IsOptionalWithReferenceInner_ConcreteClassFallback_NestedType_ReturnsFalse()
    {
        // Nested type names (two dots) are conservatively excluded — they're usually
        // value-type enums/structs scoped under a class. Matches the Path 2 guard
        // and TypeProjectionFactory.IsOptionalObjCBridged behavior.
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("RealityFoundation.Entity.HierarchyOptions"));

        Assert.False(
            CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb),
            "Nested types must not fall into Path 3 — they may be value-type enums");
    }

    [Fact]
    public void IsOptionalWithReferenceInner_ConcreteClassFallback_GenericContainer_ReturnsFalse()
    {
        // Generic specializations like RealityKit.Entity<Foo> aren't simple class
        // references — they're typically generic value types or generic specializations
        // that need their own marshalling. Path 3 must defer to the generic-container
        // handling and not over-claim them.
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        var innerGeneric = new NamedTypeSpec("RealityFoundation.Entity");
        innerGeneric.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        optionalSpec.GenericParameters.Add(innerGeneric);

        Assert.False(
            CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb),
            "Generic specializations of concrete-class-fallback modules must not fall into Path 3");
    }

    [Fact]
    public void IsOptionalWithReferenceInner_NonConcreteClassFallbackModule_NoObjCPrefix_ReturnsFalse()
    {
        // A module that is NOT in the concrete-class-fallback list and whose
        // type name doesn't match an ObjC prefix must stay rejected — Path 3
        // is opt-in per-module so we don't over-classify third-party Swift
        // modules as Apple-class shapes.
        var typeDb = new TypeDatabase();
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("ThirdParty.RandomThing"));

        Assert.False(
            CdeclParamMapper.IsOptionalWithReferenceInner(optionalSpec, typeDb),
            "Path 3 must not fire for arbitrary unrecognized modules");
    }
}

/// <summary>
/// Tests for the <c>parent_module_internal</c> guard arm (2b) of
/// <see cref="WrapperValidation.GetMemberRejectionReason"/> and the shared
/// <see cref="WrapperValidation.IsParentTypeModuleInternal"/> predicate it
/// delegates to.
///
/// A <c>public</c> member whose PARENT type is <c>@usableFromInline internal</c>
/// (<see cref="TypeDecl.IsModuleInternal"/>) slips the member-keyed
/// <c>module_internal</c> arm (the member's own flag is false), but its @_cdecl
/// wrapper body would name the parent by its module-qualified name to
/// reconstruct <c>self</c> — an internal type the separate wrapper-compilation
/// module cannot reference, so swiftc rejects the wrapper and it is stripped.
/// Arm 2b rejects the wrapper at emission instead, so the member falls back to a
/// direct CallConvSwift P/Invoke (no CS0535) and the wrapper-strip count stays 0.
///
/// Scope is sync Method / Constructor / Property / Subscript — a subscript is an
/// accessor pair like a property and shares the same clean CallConvSwift fallback,
/// so it is gated identically. The async, closure, and operator promotion sites are
/// intentionally NOT gated (no clean fallback), so those <see cref="MemberKind"/>s
/// must continue to NOT return this reason.
/// </summary>
public class ParentModuleInternalGateTests
{
    private const string WrapperLib = "TestModuleSwiftBindings";

    [Fact]
    public void GetMemberRejectionReason_PublicMethodOnInternalParentClass_ReturnsParentModuleInternal()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = InternalClass("InternalHolder", module);
        var env = Env(SyncMethod("describe", parent, module), typeDb);

        Assert.Equal("parent_module_internal",
            WrapperValidation.GetMemberRejectionReason(env, MemberKind.Method));
    }

    [Fact]
    public void GetMemberRejectionReason_PublicConstructorOnInternalParentStruct_ReturnsParentModuleInternal()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = InternalStruct("InternalValue", module);
        var ctor = SyncMethod("init", parent, module);
        ctor.IsConstructor = true;
        var env = Env(ctor, typeDb);

        Assert.Equal("parent_module_internal",
            WrapperValidation.GetMemberRejectionReason(env, MemberKind.Constructor));
    }

    [Fact]
    public void GetMemberRejectionReason_PublicPropertyOnInternalParentClass_ReturnsParentModuleInternal()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = InternalClass("InternalHolder", module);
        var env = Env(SyncMethod("get_tag", parent, module), typeDb);

        Assert.Equal("parent_module_internal",
            WrapperValidation.GetMemberRejectionReason(env, MemberKind.Property));
    }

    [Fact]
    public void CanEmitMember_PublicMethodOnInternalParent_ReturnsFalse()
    {
        // The boolean shim must agree with the diagnostic twin (Finding 12).
        var (module, typeDb) = XcframeworkEnv();
        var parent = InternalClass("InternalHolder", module);
        var env = Env(SyncMethod("describe", parent, module), typeDb);

        Assert.False(WrapperValidation.CanEmitMember(env, MemberKind.Method));
    }

    [Fact]
    public void GetMemberRejectionReason_PublicMethodOnPublicParentClass_DoesNotRejectForParentInternal()
    {
        // A plain sync method on a PUBLIC parent must NOT be rejected by arm 2b —
        // it keeps its @_cdecl wrapper exactly as before. With no other gate
        // firing for this minimal shape the overall reason is null.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("PublicHolder", module);
        var env = Env(SyncMethod("describe", parent, module), typeDb);

        var reason = WrapperValidation.GetMemberRejectionReason(env, MemberKind.Method);

        Assert.NotEqual("parent_module_internal", reason);
        Assert.Null(reason);
    }

    [Fact]
    public void GetMemberRejectionReason_PublicSubscriptOnInternalParentClass_ReturnsParentModuleInternal()
    {
        // A subscript is an accessor pair like a property — its getter/setter
        // resolve to bare-silgen / Tj symbols the dylib already exports, so it has
        // the same clean CallConvSwift fallback and is gated by arm 2b. Without the
        // gate a public subscript on an internal parent emit-then-strips and leaves
        // the C# indexer bound to a stripped @_cdecl symbol.
        var (module, typeDb) = XcframeworkEnv();
        var parent = InternalClass("InternalHolder", module);
        var env = Env(SyncMethod("subscript", parent, module), typeDb);

        Assert.Equal("parent_module_internal",
            WrapperValidation.GetMemberRejectionReason(env, MemberKind.Subscript));
    }

    [Fact]
    public void GetMemberRejectionReason_OperatorOnInternalParent_NotRejectedForParentInternal()
    {
        // Operator is out of arm 2b's scope — it has no clean CallConvSwift
        // fallback, so the wrapper MUST stay (NativeAOT ILC segfaults on a direct
        // CallConvSwift operator P/Invoke). It must never surface this reason.
        var (module, typeDb) = XcframeworkEnv();
        var parent = InternalStruct("InternalValue", module);
        var env = Env(SyncMethod("member", parent, module), typeDb);

        Assert.NotEqual("parent_module_internal",
            WrapperValidation.GetMemberRejectionReason(env, MemberKind.Operator));
    }

    [Fact]
    public void IsParentTypeModuleInternal_InternalParent_ReturnsTrue()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = InternalClass("InternalHolder", module);
        var env = Env(SyncMethod("describe", parent, module), typeDb);

        Assert.True(WrapperValidation.IsParentTypeModuleInternal(env));
    }

    [Fact]
    public void IsParentTypeModuleInternal_PublicParent_ReturnsFalse()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("PublicHolder", module);
        var env = Env(SyncMethod("describe", parent, module), typeDb);

        Assert.False(WrapperValidation.IsParentTypeModuleInternal(env));
    }

    [Fact]
    public void IsParentTypeModuleInternal_PublicParentNestedInForeignExtensionReceiver_ReturnsFalse()
    {
        // A type declared in an extension of ANOTHER module's type has that foreign type as
        // its enclosing decl, and the foreign type reads as module-internal here only because
        // it is absent from this module's public-type names — it is public where it is
        // declared and spellable from wrapper source through the import. The gate keys on the
        // IMMEDIATE parent precisely so these members keep the wrappers that compile today.
        var (module, typeDb) = XcframeworkEnv();
        var foreignReceiver = InternalClass("ForeignHost", module);
        var parent = PublicClass("HostedPayload", module);
        parent.ParentDecl = foreignReceiver;
        var env = Env(SyncMethod("describe", parent, module), typeDb);

        Assert.False(WrapperValidation.IsParentTypeModuleInternal(env));
        Assert.NotEqual("parent_module_internal",
            WrapperValidation.GetMemberRejectionReason(env, MemberKind.Method));
    }

    [Fact]
    public void IsParentTypeModuleInternal_FreeFunctionModuleParent_ReturnsFalse()
    {
        // A free function's ParentDecl is the ModuleDecl (a BaseDecl, not a
        // TypeDecl), so the predicate must return false — the gate never fires
        // for module-level functions.
        var (module, typeDb) = XcframeworkEnv();
        var freeFunc = SyncMethod("freeFunc", module, module);
        var env = Env(freeFunc, typeDb);

        Assert.False(WrapperValidation.IsParentTypeModuleInternal(env));
    }

    // ────────────────────────────────────────────────────────────────────
    // Async closure bridge eligibility (HasUnbridgeableAsyncThrowingClosure)
    //
    // A baseline-shaped async closure passes the closure-support gate on its own
    // merits, so member validation admits it and the unsupported-closure tombstone
    // never sees it. What actually decides whether the (context, startFunc) P/Invoke
    // pair gets a matching Swift adapter is the CONTAINING member's wrapper flavor:
    // the adapter is only rendered for a member promoted to an async @_cdecl method
    // wrapper. That is handler-layer knowledge, which is why the predicate lives
    // here rather than in the pre-dispatch validator — and why EVERY handler that
    // can carry a closure parameter has to consult it, not just the ordinary method
    // path. These pin the predicate's verdict per containing-member flavor.
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void HasUnbridgeableAsyncThrowingClosure_SyncMethod_ThrowingBaseline_True()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = MethodWithClosure("configure", parent, module, BaselineThrowingClosure());

        Assert.True(WrapperValidation.HasUnbridgeableAsyncThrowingClosure(Env(method, typeDb)));
    }

    [Fact]
    public void HasUnbridgeableAsyncThrowingClosure_SyncMethod_NonThrowingBaseline_True()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = MethodWithClosure("configure", parent, module, BaselineNonThrowingClosure());

        Assert.True(WrapperValidation.HasUnbridgeableAsyncThrowingClosure(Env(method, typeDb)));
    }

    [Fact]
    public void HasUnbridgeableAsyncThrowingClosure_SyncConstructor_ThrowingBaseline_True()
    {
        // The constructor factory only ever selects SYNC constructors, and the
        // constructor wrapper is a different emitter from the async method wrapper,
        // so a constructor can never satisfy the bridge's conjuncts.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var ctor = MethodWithClosure("init", parent, module, BaselineThrowingClosure());
        ctor.IsConstructor = true;
        ctor.MethodType = MethodType.Static;

        Assert.True(WrapperValidation.HasUnbridgeableAsyncThrowingClosure(Env(ctor, typeDb)));
    }

    [Fact]
    public void HasUnbridgeableAsyncThrowingClosure_StaticOperatorShapedMethod_ThrowingBaseline_True()
    {
        // An operator is emitted as a static member and is never promoted to the
        // async @_cdecl method wrapper, so it lands on the same wrong handler path.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var op = MethodWithClosure("+", parent, module, BaselineThrowingClosure());
        op.MethodType = MethodType.Static;

        Assert.True(WrapperValidation.HasUnbridgeableAsyncThrowingClosure(Env(op, typeDb)));
    }

    [Fact]
    public void HasUnbridgeableAsyncThrowingClosure_AsyncThrowsCdeclWrapper_ThrowingBaseline_False()
    {
        // Positive control: all three conjuncts hold, so the Swift adapter WILL be
        // rendered and the member must keep binding through the real bridge.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = MethodWithClosure("run", parent, module, BaselineThrowingClosure());
        method.IsAsync = true;
        method.Throws = true;
        method.UsesCdeclMethodWrapper = true;

        Assert.False(WrapperValidation.HasUnbridgeableAsyncThrowingClosure(Env(method, typeDb)));
    }

    [Fact]
    public void HasUnbridgeableAsyncThrowingClosure_AsyncCdeclWrapper_NonThrowingBaseline_False()
    {
        // Positive control for the non-throwing arm: the adapter uses `await` with
        // no `try`, so the outer member has to be async but NOT throwing.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = MethodWithClosure("run", parent, module, BaselineNonThrowingClosure());
        method.IsAsync = true;
        method.UsesCdeclMethodWrapper = true;

        Assert.False(WrapperValidation.HasUnbridgeableAsyncThrowingClosure(Env(method, typeDb)));
    }

    [Fact]
    public void HasUnbridgeableAsyncThrowingClosure_AsyncThrowsWithoutCdeclWrapper_ThrowingBaseline_True()
    {
        // Async + throws is not enough on its own: without the @_cdecl method
        // wrapper there is no Swift file for the adapter closure to be emitted into.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = MethodWithClosure("run", parent, module, BaselineThrowingClosure());
        method.IsAsync = true;
        method.Throws = true;

        Assert.True(WrapperValidation.HasUnbridgeableAsyncThrowingClosure(Env(method, typeDb)));
    }

    [Fact]
    public void HasUnbridgeableAsyncThrowingClosure_SyncClosureParameter_False()
    {
        // A plain synchronous closure never reaches the async bridge at all.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = MethodWithClosure("configure", parent, module,
            new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty));

        Assert.False(WrapperValidation.HasUnbridgeableAsyncThrowingClosure(Env(method, typeDb)));
    }

    // ────────────────────────────────────────────────────────────────────
    // Pre-emission entry point. Every test above sets UsesCdeclMethodWrapper by hand
    // because it is asking the EMISSION-time question, where the flag is already
    // settled. Callers that run BEFORE MethodHandler — the protocol-conformance
    // validator is the one that matters — see the flag still false on a method that
    // is about to be promoted, so the plain overload answers "unbridgeable" for a
    // witness that binds cleanly and the whole conformance is dropped. These pin the
    // pre-emission overload's disagreement with the plain one, and pin that the
    // disagreement is confined to the promotion conjunct.
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void BeforeEmission_AsyncThrows_PromotionNotYetRecorded_False()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = MethodWithClosure("run", parent, module, BaselineThrowingClosure());
        method.IsAsync = true;
        method.Throws = true;
        // Deliberately NOT setting UsesCdeclMethodWrapper — this is the pre-emission state.

        // The emission-time question, asked too early, gets the wrong answer …
        Assert.True(WrapperValidation.HasUnbridgeableAsyncThrowingClosure(Env(method, typeDb)));
        // … and this is the whole point of the pre-emission entry point.
        Assert.True(WrapperValidation.WillPromoteToCdeclMethodWrapper(Env(method, typeDb)));
        Assert.False(WrapperValidation.HasUnbridgeableAsyncThrowingClosureBeforeEmission(Env(method, typeDb)));
    }

    [Fact]
    public void BeforeEmission_AsyncNonThrowing_PromotionNotYetRecorded_False()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = MethodWithClosure("run", parent, module, BaselineNonThrowingClosure());
        method.IsAsync = true;

        Assert.True(WrapperValidation.HasUnbridgeableAsyncThrowingClosure(Env(method, typeDb)));
        Assert.False(WrapperValidation.HasUnbridgeableAsyncThrowingClosureBeforeEmission(Env(method, typeDb)));
    }

    [Fact]
    public void BeforeEmission_SyncMethod_StaysUnbridgeable()
    {
        // Only the PROMOTION is predicted. The outer member's async/throws facts are parser
        // facts emission never changes, so a sync member carrying an async closure has no
        // adapter and must stay unbridgeable — predicting the promotion must not be mistaken
        // for dropping the conjunct.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = MethodWithClosure("configure", parent, module, BaselineThrowingClosure());

        Assert.True(WrapperValidation.HasUnbridgeableAsyncThrowingClosureBeforeEmission(Env(method, typeDb)));
    }

    [Fact]
    public void BeforeEmission_Constructor_StaysUnbridgeable()
    {
        // A constructor never takes the method-wrapper promotion branch, so the prediction
        // must not rescue it either.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var ctor = MethodWithClosure("init", parent, module, BaselineThrowingClosure());
        ctor.IsConstructor = true;
        ctor.MethodType = MethodType.Static;
        ctor.IsAsync = true;
        ctor.Throws = true;

        Assert.False(WrapperValidation.WillPromoteToCdeclMethodWrapper(Env(ctor, typeDb)));
        Assert.True(WrapperValidation.HasUnbridgeableAsyncThrowingClosureBeforeEmission(Env(ctor, typeDb)));
    }

    [Fact]
    public void BeforeEmission_AsyncOwnedByAnotherWrapperGenerator_StaysUnbridgeable()
    {
        // The async promotion branch declines a method another wrapper generator already owns
        // (UsesWrapperLibrary), so no async adapter is emitted for it and the member stays
        // unbridgeable. Predicting the promotion must reproduce that decline, not assume every
        // async-throws member with a baseline closure gets an adapter.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = MethodWithClosure("run", parent, module, BaselineThrowingClosure());
        method.IsAsync = true;
        method.Throws = true;
        method.UsesWrapperLibrary = true;

        Assert.False(WrapperValidation.WillPromoteToCdeclMethodWrapper(Env(method, typeDb)));
        Assert.True(WrapperValidation.HasUnbridgeableAsyncThrowingClosureBeforeEmission(Env(method, typeDb)));
    }

    [Fact]
    public void BeforeEmission_AsyncOnInternalParent_StillPromotes()
    {
        // An internal parent gates the SYNC wrapper sites (they have a clean CallConvSwift
        // fallback) but deliberately NOT the async promotion site, which has none. The prediction
        // must mirror emission's actual asymmetry rather than the sync rule — treating an internal
        // parent as "no wrapper" here would drop a conformance emission keeps.
        var (module, typeDb) = XcframeworkEnv();
        var parent = InternalClass("HiddenHost", module);
        var method = MethodWithClosure("run", parent, module, BaselineThrowingClosure());
        method.IsAsync = true;
        method.Throws = true;

        Assert.True(WrapperValidation.WillPromoteToCdeclMethodWrapper(Env(method, typeDb)));
        Assert.False(WrapperValidation.HasUnbridgeableAsyncThrowingClosureBeforeEmission(Env(method, typeDb)));
    }

    [Fact]
    public void BeforeEmission_PromotionAlreadyRecorded_AgreesWithEmissionTimeAnswer()
    {
        // Asked AFTER promotion, the two overloads must not diverge — the prediction honours a
        // flag that is already set rather than re-deriving a different verdict.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = MethodWithClosure("run", parent, module, BaselineThrowingClosure());
        method.IsAsync = true;
        method.Throws = true;
        method.UsesCdeclMethodWrapper = true;

        Assert.False(WrapperValidation.HasUnbridgeableAsyncThrowingClosure(Env(method, typeDb)));
        Assert.False(WrapperValidation.HasUnbridgeableAsyncThrowingClosureBeforeEmission(Env(method, typeDb)));
    }

    [Fact]
    public void BeforeEmission_AsyncWithDebugDefaultParam_DoesNotPromote()
    {
        // Emission installs the debug-default-parameter Swift wrapper BEFORE it reaches the async
        // promotion branch, and that install sets UsesWrapperLibrary — which makes the async branch
        // decline. A prediction that reads only the CURRENT flag sees it clear and answers
        // "will promote", the inverse divergence of reading a promotion flag that is not yet set:
        // the validator keeps a conformance whose witness emission then skips, leaving the
        // interface member unimplemented.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = MethodWithClosure("run", parent, module, BaselineThrowingClosure());
        method.IsAsync = true;
        method.Throws = true;
        method.CSSignature.Add(DebugFileParameter(module));

        // Setup really is the debug-param shape — otherwise this passes vacuously.
        Assert.True(DefaultParameterOverloadEmitter.WillInstallDebugParamWrapper(method));

        Assert.False(WrapperValidation.WillPromoteToCdeclMethodWrapper(Env(method, typeDb)));
        Assert.True(WrapperValidation.HasUnbridgeableAsyncThrowingClosureBeforeEmission(Env(method, typeDb)));
    }

    [Fact]
    public void BeforeEmission_AsyncWithDebugParamWrapperAlreadyInstalled_AgreesWithPrediction()
    {
        // The post-install state emission itself sees: the wrapper is in place (UsesWrapperLibrary
        // set, debug params stripped from the signature). The predicate must reach the SAME verdict
        // from either side of the install, which is what makes it safe to share between the
        // decision site and the prediction site.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = MethodWithClosure("run", parent, module, BaselineThrowingClosure());
        method.IsAsync = true;
        method.Throws = true;
        method.UsesWrapperLibrary = true;

        Assert.False(DefaultParameterOverloadEmitter.WillInstallDebugParamWrapper(method));
        Assert.False(WrapperValidation.WillPromoteToCdeclMethodWrapper(Env(method, typeDb)));
        Assert.True(WrapperValidation.HasUnbridgeableAsyncThrowingClosureBeforeEmission(Env(method, typeDb)));
    }

    // ────────────────────────────────────────────────────────────────────
    // Non-async closure parameters on an async method (IsAsyncCdeclBridgeableSyncClosure)
    //
    // These closures are NOT the CheckedContinuation bridge's business — they are
    // ordinary callbacks (progress blocks and the like) that happen to sit on an async
    // member. They ride the same (funcPtr, context) pointer pair the synchronous @_cdecl
    // closure wrapper uses, and the predicate below is the ONE place that decides so:
    // eligibility consults it to allow the promotion, and the async Swift wrapper
    // consults it to emit the pair plus the adapter. If those two ever answered
    // differently, the C# P/Invoke and the @_cdecl signature would disagree on how many
    // C ABI words the parameter occupies — silent register corruption of every later
    // argument, not a compile error.
    //
    // The classification runs through GetClosureTypeSpec, which sees an Optional<Closure>
    // as well as a bare one; keying it on `p.SwiftTypeSpec is ClosureTypeSpec` is exactly
    // what let optional callbacks fall through to an improvised carrier.
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsAsyncCdeclBridgeableSyncClosure_OptionalClosure_True()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = AsyncMethodWithParam("run", parent, module, OptionalOf(SyncVoidClosure(escaping: false)));

        Assert.True(WrapperValidation.IsAsyncCdeclBridgeableSyncClosure(
            Env(method, typeDb), method.CSSignature[1]));
    }

    [Fact]
    public void IsAsyncCdeclEligible_AsyncMethod_OptionalClosureParam_True()
    {
        // The promotion verdict must agree with the carrier verdict: an optional callback
        // is bridgeable, so the method may take the async @_cdecl wrapper.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = AsyncMethodWithParam("run", parent, module, OptionalOf(SyncVoidClosure(escaping: false)));

        Assert.True(WrapperValidation.IsAsyncCdeclEligible(Env(method, typeDb)));
    }

    [Fact]
    public void IsAsyncCdeclBridgeableSyncClosure_BareEscapingClosure_True()
    {
        // An explicitly @escaping callback outlives the @_cdecl call the same way an
        // optional one does, so it takes the same carrier rather than a second mechanism.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = AsyncMethodWithParam("run", parent, module, SyncVoidClosure(escaping: true));

        Assert.True(WrapperValidation.IsAsyncCdeclBridgeableSyncClosure(
            Env(method, typeDb), method.CSSignature[1]));
        Assert.True(WrapperValidation.IsAsyncCdeclEligible(Env(method, typeDb)));
    }

    [Fact]
    public void IsAsyncCdeclBridgeableSyncClosure_BareNonEscapingClosure_False()
    {
        // The escaping requirement is the load-bearing half. The async wrapper hands the
        // adapter to a detached Task, so the closure runs AFTER the @_cdecl function has
        // returned; only an effectively-escaping closure gets the Swift-ARC owner token
        // that keeps the GCHandle alive that long. A non-escaping one would be freed by
        // the C# wrapper the moment the call returned and the Task would invoke a freed
        // delegate — so the member stays honestly unpromoted instead.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = AsyncMethodWithParam("run", parent, module, SyncVoidClosure(escaping: false));

        Assert.False(WrapperValidation.IsAsyncCdeclBridgeableSyncClosure(
            Env(method, typeDb), method.CSSignature[1]));
        Assert.False(WrapperValidation.IsAsyncCdeclEligible(Env(method, typeDb)));
    }

    [Fact]
    public void IsAsyncCdeclEligible_OptionalBaselineAsyncClosure_False()
    {
        // The CheckedContinuation bridge is keyed on a BARE closure on both sides — the
        // Swift wrapper's baseline filter and the (context, startFunc) P/Invoke pair. An
        // Optional wrapper around the same shape has no carrier in either mechanism, so it
        // must not claim eligibility through the baseline arms.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = AsyncMethodWithParam("run", parent, module, OptionalOf(BaselineNonThrowingClosure()));

        Assert.False(WrapperValidation.IsAsyncCdeclBridgeableSyncClosure(
            Env(method, typeDb), method.CSSignature[1]));
        Assert.False(WrapperValidation.IsAsyncCdeclEligible(Env(method, typeDb)));
    }

    [Fact]
    public void IsAsyncCdeclEligible_BareBaselineAsyncClosure_StillBridged()
    {
        // Positive control for the arm above: the bare baseline async closure keeps its
        // CheckedContinuation route, and does NOT get reclassified onto the pointer-pair
        // carrier.
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = AsyncMethodWithParam("run", parent, module, BaselineNonThrowingClosure());

        Assert.False(WrapperValidation.IsAsyncCdeclBridgeableSyncClosure(
            Env(method, typeDb), method.CSSignature[1]));
        Assert.True(WrapperValidation.IsAsyncCdeclEligible(Env(method, typeDb)));
    }

    [Fact]
    public void IsAsyncCdeclBridgeableSyncClosure_NonClosureParameter_False()
    {
        var (module, typeDb) = XcframeworkEnv();
        var parent = PublicClass("Host", module);
        var method = AsyncMethodWithParam("run", parent, module, new NamedTypeSpec("Swift.Int"));

        Assert.False(WrapperValidation.IsAsyncCdeclBridgeableSyncClosure(
            Env(method, typeDb), method.CSSignature[1]));
    }

    // --- minimal decl factories (local to keep the gate test self-contained) ---

    /// <summary>`() -&gt; ()` — an ordinary non-async callback, optionally `@escaping`.</summary>
    private static ClosureTypeSpec SyncVoidClosure(bool escaping)
    {
        var spec = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        if (escaping)
            spec.Attributes.Add(new TypeSpecAttribute("escaping"));
        return spec;
    }

    /// <summary>`T?` as the parser renders it: `Swift.Optional` with one generic argument.</summary>
    private static NamedTypeSpec OptionalOf(TypeSpec inner)
        => new NamedTypeSpec("Swift.Optional", inner);

    private static MethodDecl AsyncMethodWithParam(string name, BaseDecl parent, ModuleDecl module, TypeSpec paramType)
    {
        var method = SyncMethod(name, parent, module);
        method.IsAsync = true;
        method.CSSignature.Add(new ArgumentDecl
        {
            SwiftTypeSpec = paramType,
            Name = "handler",
            PrivateName = "handler",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = module
        });
        return method;
    }

    /// <summary>`file: Swift.StaticString = #file` — the canonical debug default parameter.</summary>
    private static ArgumentDecl DebugFileParameter(ModuleDecl module)
        => new ArgumentDecl
        {
            SwiftTypeSpec = new NamedTypeSpec("Swift.StaticString"),
            Name = "file",
            PrivateName = "file",
            IsInOut = false,
            IsGeneric = false,
            HasDefaultArg = true,
            ParentDecl = null,
            ModuleDecl = module
        };

    /// <summary>`() async throws -&gt; Swift.Int` — a blittable-primitive return with
    /// zero closure args is the canonical baseline throwing shape.</summary>
    private static ClosureTypeSpec BaselineThrowingClosure()
        => new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("Swift.Int"))
        {
            IsAsync = true,
            Throws = true
        };

    /// <summary>`() async -&gt; Swift.Int` — the non-throwing baseline twin.</summary>
    private static ClosureTypeSpec BaselineNonThrowingClosure()
        => new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("Swift.Int"))
        {
            IsAsync = true
        };

    private static MethodDecl MethodWithClosure(string name, BaseDecl parent, ModuleDecl module, ClosureTypeSpec closure)
    {
        var method = SyncMethod(name, parent, module);
        method.CSSignature.Add(new ArgumentDecl
        {
            SwiftTypeSpec = closure,
            Name = "handler",
            PrivateName = "handler",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = module
        });
        return method;
    }

    private static (ModuleDecl module, TypeDatabase typeDb) XcframeworkEnv()
    {
        // A non-empty AsyncLibraryName is what flips GenerationMode to XCFramework,
        // the prerequisite for any @_cdecl wrapper emission (gate 1).
        var typeDb = new TypeDatabase { AsyncLibraryName = WrapperLib };
        var module = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
        return (module, typeDb);
    }

    private static MethodEnvironment Env(MethodDecl method, TypeDatabase typeDb)
        => new MethodEnvironment(method, typeDb);

    private static ClassDecl InternalClass(string name, ModuleDecl module)
    {
        var decl = PublicClass(name, module);
        decl.IsModuleInternal = true;
        return decl;
    }

    private static ClassDecl PublicClass(string name, ModuleDecl module)
        => new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            IsFinal = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = module,
            ModuleDecl = module
        };

    private static StructDecl InternalStruct(string name, ModuleDecl module)
        => new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            IsFrozen = true,
            IsModuleInternal = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = module,
            ModuleDecl = module
        };

    private static MethodDecl SyncMethod(string name, BaseDecl parent, ModuleDecl module)
        => new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = module
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = module,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
}

/// <summary>
/// Pins the structural half of the plan/emit contract: a C# <c>[LibraryImport]</c> may claim an
/// <c>SBW_</c> wrapper symbol only while the Swift plane that would define that symbol is live.
/// The predicate half (<see cref="WrapperValidation.IsTypeOrEnclosingModuleInternal"/>) is what a
/// planner consults up front; this guard is the backstop at the site that would otherwise write
/// the extern, so a violation is attributed to the offending emitter instead of surfacing later
/// as a dangling-symbol failure at the end of generation.
/// </summary>
public class WrapperPlaneContractTests
{
    [Fact]
    public void RequireLiveWrapperPlane_LiveWriter_DoesNotThrow()
    {
        var writer = new SwiftWriter(new StringWriter());

        WrapperValidation.RequireLiveWrapperPlane(writer, "a test emitter");
    }

    [Fact]
    public void RequireLiveWrapperPlane_DiscardingWriter_Throws()
    {
        // A discarding writer is non-null and accepts every write, so it is indistinguishable
        // from a real one to a `writer != null` test — which is precisely how an emitter comes to
        // plan externs for wrappers that were thrown away.
        var writer = new SwiftWriter(new StringWriter()) { IsDiscarding = true };

        var ex = Assert.Throws<InvalidOperationException>(
            () => WrapperValidation.RequireLiveWrapperPlane(writer, "a test emitter"));

        Assert.Contains("a test emitter", ex.Message);
    }
}
