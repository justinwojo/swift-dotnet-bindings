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
/// <param name="UnrepresentableConcreteSameTypePins">
/// One entry per same-type (<c>==</c>) constraint that pinned this parameter to a concrete target
/// <see cref="GenericSignatureParser"/> could not represent as a nominal
/// <see cref="GenericParameterConformance"/> and therefore dropped. Two shapes reach here: a target
/// that is not a nominal type at all (<c>where RowDecoder == ()</c>) and a fully-concrete
/// constructed generic (<c>where RowDecoder == Pair&lt;Int&gt;</c>) — a constructed-generic target
/// that names one of the signature's own parameters (<c>== Pair&lt;T&gt;</c>) is a family
/// relationship, not a pin, and is not recorded. The constraint is gone from
/// <see cref="GenericConformances"/>, but the fact that the member is confined to a single
/// specialization must survive: an open-type-erasure constructor wrapper (<c>_SBW_CI_</c> / GSF)
/// emitted against the unconstrained type would not compile. The module-qualified non-constructed
/// equivalent (<c>== Swift.Int</c>) survives parsing and is caught by
/// <see cref="ConstructorAdmissibility.HasUnsatisfiableParentGenericExtensionConstraint"/> directly;
/// these entries restore parity for the dropped cases.
///
/// Each entry is the clause's own <c>subject.path==target</c> text rather than a bare flag, so a
/// pin the PARENT TYPE declares can be subtracted from the set an initializer carries. An init
/// inherits its enclosing type's whole signature, so a parent-declared pin appears on every init
/// and must not be read as an extension-added confinement — the same subtraction
/// <see cref="ConstructorAdmissibility.HasUnsatisfiableParentGenericExtensionConstraint"/> already
/// performs for representable constraints. A bare boolean cannot express that difference: it
/// cannot tell "the parent's own pin" from "a second pin an extension added on top of it".
/// That subtraction is an OPEN-erasure rule only — CSM reads the same entries through
/// <see cref="ConstructorAdmissibility.HasUnrepresentableConcreteParentPin"/>, which subtracts
/// nothing, because CSM satisfies constraints by evaluating them per conformer rather than by
/// inheriting the parent's signature.
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
    IReadOnlyList<string>? UnrepresentableConcreteSameTypePins = null,
    bool HasDroppedNominalMarkerConstraint = false
)
{
    /// <summary>
    /// True when this parameter carried at least one dropped concrete same-type pin — see
    /// <see cref="UnrepresentableConcreteSameTypePins"/>. Callers that need to know WHICH pin (to
    /// subtract the parent type's own) read the collection instead.
    /// </summary>
    public bool HasUnrepresentableConcreteSameTypePin
        => UnrepresentableConcreteSameTypePins is { Count: > 0 };
}
