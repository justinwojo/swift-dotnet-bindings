// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Represents a generic argument declaration.
/// </summary>
/// <param name="TypeName">The name of the generic argument type</param>
/// <param name="SugaredTypeName">The sugared name of the generic argument type</param>
/// <param name="GenericConformances">The conformances of the generic argument type</param>
/// <param name="AssosiatedTypeConformances">The conformances of the associated types of the generic argument type</param>
/// <param name="HasUnrepresentableConcreteSameTypePin">
/// True when this parameter carried a same-type (<c>==</c>) constraint pinning it to a concrete,
/// non-module-qualified target (e.g. <c>where RowDecoder == ()</c>) that <see cref="GenericSignatureParser"/>
/// could not represent as a nominal <see cref="GenericParameterConformance"/> and therefore dropped.
/// The constraint is gone from <see cref="GenericConformances"/>, but the fact that the member is
/// confined to a single specialization must survive: an open-type-erasure constructor wrapper
/// (<c>_SBW_CI_</c> / GSF) emitted against the unconstrained type would not compile. The
/// module-qualified equivalent (<c>== Swift.Int</c>) survives parsing and is caught by
/// <see cref="ConstructorAdmissibility.HasUnsatisfiableParentGenericExtensionConstraint"/> directly;
/// this flag restores parity for the dropped non-qualified case.
/// </param>
/// <param name="HasDroppedNominalMarkerConstraint">
/// True when this parameter carried a module-qualified protocol-kind marker constraint
/// (e.g. <c>where U : Swift.Sendable</c>) that the parser drops as an unrepresentable nominal
/// conformance. Before that drop existed, such a constraint surfaced as a real
/// <see cref="GenericParameterConformance"/>, so it counted toward the enum-demotion gate
/// (<c>ModuleProcessor.HasProtocolConstraintAtPosition</c>), which keys off "param has any
/// conformance". This flag preserves that signal: a simple enum used at a position whose Swift
/// parameter is constrained must still demote to a class, since the dropped marker does not make
/// the position constraint-free.
/// </param>
public record GenericArgumentDecl(
    string TypeName,
    string SugaredTypeName,
    List<GenericParameterConformance> GenericConformances,
    List<GenericParameterConformance> AssosiatedTypeConformances,
    bool HasUnrepresentableConcreteSameTypePin = false,
    bool HasDroppedNominalMarkerConstraint = false
);
