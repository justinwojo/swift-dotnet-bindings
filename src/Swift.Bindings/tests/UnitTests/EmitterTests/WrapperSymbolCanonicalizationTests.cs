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
/// protocol-extension path (stashed on <see cref="MethodDecl.WrapperSourceKey"/>
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
}
