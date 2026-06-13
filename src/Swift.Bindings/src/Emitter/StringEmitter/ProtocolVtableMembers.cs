// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Single source of truth for "does this protocol member get a vtable slot?"
/// Mirrors the inline filter logic in <see cref="EveryProtocolEmitter.EmitProtocolVtableStruct"/>
/// so that C# vtable population (struct layout + cctor assignment) stays in lock-step
/// with the Swift wrapper's vtable struct layout.
///
/// Used by the cross-module parent emission path in
/// <see cref="ProtocolProxyEmitter.EmitCrossModuleParentScaffolding"/>
/// and <see cref="ProtocolProxyEmitter.EmitCrossModuleParentVtableInit"/>. The same-module
/// path relies on the skip sets ProtocolHandler computes; cross-module parents have no
/// ProtocolHandler pass in the consuming module so the predicate logic must be re-applied here.
/// </summary>
internal static class ProtocolVtableMembers
{
    internal static bool IncludesProperty(PropertyDecl property, ProtocolDecl protocol, ClosureHandler closureHandler)
    {
        // Defect F: a non-requirement property (e.g. a protocol-extension default impl) has no C#
        // override — Swift owns the body — so it gets NO vtable slot. This matches the plan/fan-out
        // populators (EveryProtocolEmitter.ComputePropertyEmissionPlans / ComputeSiblingPropertyFallbacks)
        // and the struct emitter (EveryProtocolEmitter.EmitProtocolVtableStruct). Keeping the slot
        // here would diverge from the populators and shift later fields (Finding-8 positional corruption).
        if (property.IsStatic || property.IsObjCOptional || !property.IsProtocolRequirement)
            return false;
        var isMixedGeneric = EveryProtocolEmitter.IsMixedGenericProtocol(protocol);
        if (EveryProtocolEmitter.HasClosureInPropertyType(property))
        {
            if (!EveryProtocolEmitter.IsDispatchableClosureProperty(property, closureHandler))
                return false;
            if (isMixedGeneric)
                return false;
            return true;
        }
        if (EveryProtocolEmitter.ContainsSelfTypeParam(property.SwiftTypeSpec))
            return false;
        if (isMixedGeneric)
            return false;
        return true;
    }

    internal static bool IncludesSubscript(SubscriptDecl subscript, ProtocolDecl protocol)
    {
        if (subscript.IsStatic)
            return false;
        if (EveryProtocolEmitter.ContainsSelfTypeParam(subscript.ReturnTypeSpec))
            return false;
        if (subscript.IndexParameters.Any(p => EveryProtocolEmitter.ContainsSelfTypeParam(p.SwiftTypeSpec)))
            return false;
        if (EveryProtocolEmitter.IsMixedGenericProtocol(protocol))
            return false;
        return true;
    }

    internal static bool IncludesMethod(MethodDecl method, ProtocolDecl protocol, ClosureHandler closureHandler)
    {
        if (method.IsConstructor || method.MethodType == MethodType.Static)
            return false;
        if (method.IsObjCOptional)
            return false;
        if (EveryProtocolEmitter.HasClosureInMethodSignature(method)
            && !EveryProtocolEmitter.IsDispatchableClosureMethod(method, closureHandler)
            && !EveryProtocolEmitter.IsDispatchableClosureReturningMethod(method, closureHandler)
            && !EveryProtocolEmitter.IsDispatchableAsyncClosureMethod(method, closureHandler))
            return false;
        if (EveryProtocolEmitter.HasOnlyMethodLevelGenerics(method))
            return false;
        if (EveryProtocolEmitter.HasSelfTypeParamInSignature(method))
            return false;
        if (EveryProtocolEmitter.IsMixedGenericProtocol(protocol))
            return false;
        return true;
    }
}
