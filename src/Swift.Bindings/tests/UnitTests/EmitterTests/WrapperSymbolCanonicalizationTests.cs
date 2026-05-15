// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Cross-emitter <c>@_cdecl</c> symbol dedup based on structural identity.
///
/// Two emitters can reach the same Swift method:
///  - <see cref="MethodWrapperEmitter.GetMethodSymbolName"/> builds
///    <c>SBW_&lt;Module&gt;_&lt;Type&gt;_&lt;method&gt;_&lt;hash8&gt;</c>.
///  - ProtocolExtensionEmitter's <c>BuildSymbolName</c> builds
///    <c>SBW_&lt;FlatType&gt;_&lt;method&gt;_&lt;labels&gt;</c>.
/// Same underlying method, different symbol strings — so the string-keyed
/// dedup at <c>ModuleEmissionContext._registeredWrapperSymbols</c> doesn't
/// reject the second registration. Both <c>@_cdecl</c> blocks land in the
/// wrapper file, swiftc rejects "multiple definitions of symbol", the
/// xcframework's <c>Info.plist</c> is never written, downstream consumers
/// hit <c>DllNotFoundException</c> at runtime.
///
/// The fix introduces a structural-identity registry keyed by a 3-tuple of
/// <c>(typeName, methodName, sourceKey)</c>. <c>sourceKey</c> is whatever
/// canonical string the calling emitter elects to use: the rendered
/// <c>SBW_</c> symbol itself for <see cref="MethodWrapperEmitter"/> on
/// ordinary methods, and a
/// <c>ProtocolQualifiedName::PrintedName::RawSignature</c> string for the
/// protocol-extension path (stashed on <see cref="MethodDecl.StructuralIdentityKey"/>
/// so the downstream <see cref="MethodWrapperEmitter"/> pass computes the
/// same key). The first emitter to claim the identity wins; subsequent
/// emitters for the same Swift method skip.
/// </summary>
public class WrapperSymbolCanonicalizationTests
{
    [Fact]
    public void SameMethod_DifferentEmitterSymbols_StructuralDedupKeepsOne()
    {
        // MethodWrapperEmitter and ProtocolExtensionEmitter both reach the same Swift
        // method. Today their symbol strings differ and both register. After the fix,
        // the structural identity (typeName, methodName, sourceKey) collapses them.
        var ctx = new ModuleEmissionContext();
        const string typeName = "Builder";
        const string methodName = "step";
        const string sourceKey = "$s10TestModule7Builder4stepyySbF";

        var methodEmitterSymbol = MethodWrapperEmitter.GetMethodSymbolName(
            "TestModule", typeName, methodName, sourceKey);
        var protocolExtSymbol = $"SBW_{typeName}_{methodName}_enabledBool";

        Assert.NotEqual(methodEmitterSymbol, protocolExtSymbol);

        // First emitter claims the structural identity → wins.
        Assert.True(ctx.TryClaimWrapperSymbol(typeName, methodName, sourceKey, methodEmitterSymbol),
            "First emitter for a Swift method must successfully claim the structural identity");

        // Second emitter targets the same Swift method with a different symbol string.
        // Without the structural-identity dedup, the string-keyed registry accepts it
        // and two @_cdecl blocks land in the wrapper file. After the fix, this must reject.
        Assert.False(ctx.TryClaimWrapperSymbol(typeName, methodName, sourceKey, protocolExtSymbol),
            "Second emitter for the same Swift method must be rejected even when its symbol string differs");
    }

    [Fact]
    public void DifferentMangledNames_SameTypeAndMethod_ProduceDistinctIdentities()
    {
        // Negative regression: two methods with the same type+name but different mangled names
        // (e.g., overloads with different parameter shapes) must NOT collapse. The full
        // mangled name is part of the structural key.
        var ctx = new ModuleEmissionContext();

        var s1 = MethodWrapperEmitter.GetMethodSymbolName("Mod", "Type", "step", "$s_step_Bool");
        var s2 = MethodWrapperEmitter.GetMethodSymbolName("Mod", "Type", "step", "$s_step_Int");
        Assert.NotEqual(s1, s2);

        Assert.True(ctx.TryClaimWrapperSymbol("Type", "step", "$s_step_Bool", s1));
        Assert.True(ctx.TryClaimWrapperSymbol("Type", "step", "$s_step_Int", s2),
            "Distinct mangled names must remain distinct structural identities — no over-dedup");
    }

    [Fact]
    public void DifferentTypesSameMethodName_DontCollide()
    {
        // Two distinct conforming classes (ClassA, ClassB) both inherit a protocol extension
        // method "ping". The protocol-extension emitter produces one wrapper per conforming
        // type with distinct symbols (SBW_ClassA_ping vs SBW_ClassB_ping). The structural
        // identity must keep them apart — the typeName component of the key prevents collision.
        var ctx = new ModuleEmissionContext();

        Assert.True(ctx.TryClaimWrapperSymbol("ClassA", "ping", "$s_pingA", "SBW_ClassA_ping"));
        Assert.True(ctx.TryClaimWrapperSymbol("ClassB", "ping", "$s_pingB", "SBW_ClassB_ping"),
            "Distinct conforming types must remain distinct structural identities");
    }

    [Fact]
    public void IdenticalIdentity_IdenticalSymbol_StillRejectsSecondClaim()
    {
        // Even when both emitters happen to produce the exact same symbol string,
        // the structural-identity registry must still reject the second claim — the
        // contract is "first claim wins, regardless of whether the second emitter
        // would have produced an identical string".
        var ctx = new ModuleEmissionContext();
        const string symbol = "SBW_Foo_bar_xyz";

        Assert.True(ctx.TryClaimWrapperSymbol("Foo", "bar", "$s_bar_0", symbol));
        Assert.False(ctx.TryClaimWrapperSymbol("Foo", "bar", "$s_bar_0", symbol));
    }

    [Fact]
    public void StructuralClaim_PopulatesUnifiedRegistry()
    {
        // The structural-identity claim must still register the resulting symbol in
        // the unified _registeredWrapperSymbols set. PInvokeEmitHelper consults that
        // set via IsWrapperSymbolRegistered before emitting a P/Invoke whose entry
        // point follows the SBW_ convention. If the structural claim bypassed the
        // unified set, the in-band wrapper-symbol contract would tear down a P/Invoke
        // for a wrapper that was actually emitted.
        var ctx = new ModuleEmissionContext();
        const string symbol = "SBW_Mod_Type_method_abcd1234";

        Assert.True(ctx.TryClaimWrapperSymbol("Type", "method", "$s_method", symbol));
        Assert.True(ctx.IsWrapperSymbolRegistered(symbol),
            "Structural claim must also register the surviving symbol in the unified registry");
    }

    // ====================================================================
    // S5: cross-emitter cohesion across the broader Tier-A surface.
    //
    // S2 closed cross-emitter dup-symbol on ProtocolExtensionEmitter ×
    // MethodWrapperEmitter via the structural-identity claim API. S5 widens
    // the same invariant to ForeignTypeExtensionEmitter, ExistentialBypassEmitter,
    // MetatypeArrayBridgeEmitter, and ConstrainedExtensionEmitter — each must
    // route through TryClaimWrapperSymbol so the bug class S2 closed cannot
    // recur on an adjacent emitter pair (e.g. ForeignTypeExtensionEmitter
    // synthesising a wrapper for the same Swift method as a future emitter
    // with a different rendered symbol string would silently land two
    // @_cdecl/@_silgen_name blocks → swiftc dup-symbol → xcframework drop →
    // DllNotFoundException at runtime). These tests pin each emitter's
    // structural-identity contract at the registry boundary; the per-emitter
    // rendering stays a presentation concern of the emitter itself.
    // ====================================================================

    [Fact]
    public void ForeignTypeExtension_SameForeignTypeAndMethod_DifferentSignatures_StayDistinct()
    {
        // ForeignTypeExtensionEmitter renders SBSW_<flatType>_<method>_<labels>.
        // Two extensions on the same foreign type with the same method name but
        // different parameter signatures (overloads) must remain distinct in the
        // structural-identity registry — a labels-only sourceKey would over-dedup.
        var ctx = new ModuleEmissionContext();
        const string foreignType = "SwiftBindingsTestLibDependency.DependencyService";
        const string methodName = "tagged";

        Assert.True(ctx.TryClaimWrapperSymbol(foreignType, methodName,
            "func tagged(by: Swift.Int32) -> Swift.Int32",
            "SBSW_SwiftBindingsTestLibDependency_DependencyService_tagged_byInt32"));

        Assert.True(ctx.TryClaimWrapperSymbol(foreignType, methodName,
            "func tagged(by: Swift.Double) -> Swift.Double",
            "SBSW_SwiftBindingsTestLibDependency_DependencyService_tagged_byDouble"),
            "Distinct overloads on the same foreign type must produce distinct structural identities");
    }

    [Fact]
    public void ForeignTypeExtension_SameForeignTypeAndMethod_SameSignature_DedupedToOne()
    {
        // Two foreign-type extension passes for the exact same Swift extension method
        // (same foreign type, same method, same raw signature) must collapse to a
        // single claim. Otherwise the wrapper file would land two @_silgen_name blocks
        // for the same symbol and swiftc would reject "multiple definitions of symbol".
        var ctx = new ModuleEmissionContext();
        const string foreignType = "SwiftBindingsTestLibDependency.DependencyService";
        const string methodName = "scaled";
        const string sourceKey = "func scaled(by: Swift.Double) -> SwiftBindingsTestLibDependency.DependencyPoint";
        const string symbol = "SBSW_SwiftBindingsTestLibDependency_DependencyService_scaled_byDouble";

        Assert.True(ctx.TryClaimWrapperSymbol(foreignType, methodName, sourceKey, symbol));
        Assert.False(ctx.TryClaimWrapperSymbol(foreignType, methodName, sourceKey, symbol),
            "Repeat claim for identical foreign-type extension must be rejected");
    }

    [Fact]
    public void ForeignTypeExtension_PropertyGetterAndSetter_StayDistinct()
    {
        // Property accessors share method-name-shaped identifiers but project
        // distinct Swift symbols (get_x vs set_x). The sourceKey must encode
        // the get/set role so getter and setter don't collide.
        var ctx = new ModuleEmissionContext();
        const string foreignType = "SwiftBindingsTestLibDependency.DependencyPoint";

        Assert.True(ctx.TryClaimWrapperSymbol(foreignType, "isOrigin",
            "get isOrigin",
            "SBSW_SwiftBindingsTestLibDependency_DependencyPoint_get_isOrigin"));

        Assert.True(ctx.TryClaimWrapperSymbol(foreignType, "isOrigin",
            "set isOrigin",
            "SBSW_SwiftBindingsTestLibDependency_DependencyPoint_set_isOrigin"),
            "Setter sourceKey must not collide with getter on the same property");
    }

    [Fact]
    public void ExistentialBypass_DifferentInstanceMethods_OnSameType_StayDistinct()
    {
        // ExistentialBypassEmitter hashes the underlying mangled name into the
        // SBSW_<Type>_<method>_<hash> symbol. Two distinct instance methods
        // on the same type must produce distinct structural identities so the
        // bypass for method A doesn't shadow method B.
        var ctx = new ModuleEmissionContext();
        const string typeName = "MyExistentialStruct";

        Assert.True(ctx.TryClaimWrapperSymbol(typeName, "report",
            "$sExistentialBypass_report_methodA_mangled",
            "SBSW_MyExistentialStruct_report_aaaaaaaa"));

        Assert.True(ctx.TryClaimWrapperSymbol(typeName, "annotate",
            "$sExistentialBypass_annotate_methodB_mangled",
            "SBSW_MyExistentialStruct_annotate_bbbbbbbb"),
            "Distinct methods on the same existential-bypass parent must remain distinct");
    }

    [Fact]
    public void ExistentialBypass_RepeatClaim_OnSameMethod_RejectsSecond()
    {
        // ExistentialBypassEmitter must reject a repeat structural claim for the
        // same underlying Swift method, even when the bypass emit path is reached
        // a second time (e.g., shared signature seen across two emit phases).
        var ctx = new ModuleEmissionContext();
        const string typeName = "BypassStruct";
        const string sourceKey = "$sExistentialBypass_emit_idempotent_mangled";
        const string symbol = "SBSW_BypassStruct_emit_aaaaaaaa";

        Assert.True(ctx.TryClaimWrapperSymbol(typeName, "emit", sourceKey, symbol));
        Assert.False(ctx.TryClaimWrapperSymbol(typeName, "emit", sourceKey, symbol),
            "Repeat existential-bypass claim for the same Swift method must be rejected");
    }

    [Fact]
    public void ExistentialBypass_ConstructorAndFreeFunction_DontCollideWithMethod()
    {
        // ExistentialBypassEmitter emits a (init, free) pair for constructor
        // bypass at lines 980-981, plus a method wrapper at line 753. These three
        // wrappers share the SBSW_<Type>_ prefix but have distinct symbol names
        // (init/free/method) AND distinct sourceKey strings — they must not
        // collide on the structural identity even when the type name matches.
        var ctx = new ModuleEmissionContext();
        const string typeName = "BypassStruct";

        Assert.True(ctx.TryClaimWrapperSymbol(typeName, "init",
            "$sBypassStruct_init_mangled",
            "SBSW_BypassStruct_init_aaaaaaaa"));

        Assert.True(ctx.TryClaimWrapperSymbol(typeName, "free",
            "$sBypassStruct_free_mangled",
            "SBSW_BypassStruct_free_aaaaaaaa"),
            "Free-function bypass must not collide with constructor bypass on the same type");

        Assert.True(ctx.TryClaimWrapperSymbol(typeName, "doWork",
            "$sBypassStruct_doWork_mangled",
            "SBSW_BypassStruct_doWork_bbbbbbbb"),
            "Method bypass must not collide with constructor or free-function bypass");
    }

    [Fact]
    public void MetatypeArrayBridge_RegistersStructurally_OneSymbolPerNormalizedMethod()
    {
        // MetatypeArrayBridgeEmitter wraps free functions returning [SomeProto] /
        // taking [SomeProto] in a "Free" pseudo-type SBW_<Module>_Free_<method>_<hash>
        // wrapper. The bridge bypasses MethodWrapperEmitter's normal registration so
        // its claim must register the symbol structurally — otherwise the in-band
        // wrapper-symbol contract would tear down the matching P/Invoke. A repeat
        // bridge for the same normalized method must also dedup.
        var ctx = new ModuleEmissionContext();
        const string typeName = "Free";
        const string methodName = "collect";
        const string sourceKey = "$sFreeFunctionMetaArrayBridge_collect_mangled";
        const string symbol = "SBW_TestModule_Free_collect_abcd1234";

        Assert.True(ctx.TryClaimWrapperSymbol(typeName, methodName, sourceKey, symbol));
        Assert.True(ctx.IsWrapperSymbolRegistered(symbol),
            "MetatypeArrayBridge structural claim must also register the surviving symbol in the unified registry");
        Assert.False(ctx.TryClaimWrapperSymbol(typeName, methodName, sourceKey, symbol),
            "Repeat metatype-array-bridge claim for the same normalized method must be rejected");
    }

    [Fact]
    public void ConstrainedExtension_PropertyGetters_OnDifferentConcretizations_StayDistinct()
    {
        // ConstrainedExtensionEmitter materialises one wrapper per (parent, concrete)
        // pair: SBW_CEGet_<Module>_<Parent>_<Concrete>_<property>. Two concretizations
        // of the same generic parent must not collide on the structural identity.
        var ctx = new ModuleEmissionContext();

        Assert.True(ctx.TryClaimWrapperSymbol("Container<Int32>", "first",
            "ConstrainedExtension::Container<Int32>::first",
            "SBW_CEGet_TestModule_Container_Int32_first"));

        Assert.True(ctx.TryClaimWrapperSymbol("Container<String>", "first",
            "ConstrainedExtension::Container<String>::first",
            "SBW_CEGet_TestModule_Container_String_first"),
            "Distinct concretizations of the same constrained-extension parent must produce distinct identities");
    }

    [Fact]
    public void ConstrainedExtension_PropertyAndMethod_OnSameConcretization_StayDistinct()
    {
        // SBW_CEGet_ vs SBW_CEMethod_ prefixes distinguish properties from methods at
        // the symbol level; the structural sourceKey must do the same so a property
        // named "value" doesn't shadow a method named "value".
        var ctx = new ModuleEmissionContext();
        const string parent = "Container<Int32>";

        Assert.True(ctx.TryClaimWrapperSymbol(parent, "value",
            "ConstrainedExtension::Container<Int32>::get value",
            "SBW_CEGet_TestModule_Container_Int32_value"));

        Assert.True(ctx.TryClaimWrapperSymbol(parent, "value",
            "ConstrainedExtension::Container<Int32>::method value",
            "SBW_CEMethod_TestModule_Container_Int32_value"),
            "Property getter and method named the same must not collide on the structural identity");
    }
}
