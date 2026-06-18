// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Boolean "does this protocol member get a vtable slot?" view over the single membership oracle in
/// <see cref="VtableLayoutBuilder"/>. Each predicate is exactly <c>Classify* == Included</c>, so this
/// helper and the <see cref="VtableLayout"/> model can never disagree: they ARE the same function.
///
/// Used by the same-module struct walks and the cross-module parent emission path
/// (<see cref="ProtocolProxyEmitter.EmitCrossModuleParentScaffolding"/>,
/// <see cref="ProtocolProxyEmitter.EmitCrossModuleParentVtableInit"/>). Membership is stateless and
/// path-independent — it does NOT consult the <c>ProtocolHandler</c> skip sets (those still drive
/// INTERFACE emission, never vtable-slot membership).
/// </summary>
internal static class ProtocolVtableMembers
{
    internal static bool IncludesProperty(PropertyDecl property, ProtocolDecl protocol, ClosureHandler closureHandler)
        => VtableLayoutBuilder.ClassifyProperty(property, protocol, closureHandler) == SlotVerdict.Included;

    internal static bool IncludesSubscript(SubscriptDecl subscript, ProtocolDecl protocol)
        => VtableLayoutBuilder.ClassifySubscript(subscript, protocol) == SlotVerdict.Included;

    internal static bool IncludesMethod(MethodDecl method, ProtocolDecl protocol, ClosureHandler closureHandler)
        => VtableLayoutBuilder.ClassifyMethod(method, protocol, closureHandler) == SlotVerdict.Included;
}
