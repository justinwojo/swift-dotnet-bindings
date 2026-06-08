// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace BindingsGeneration;

public class GenericSignatureParser
{
    /// <summary>
    /// Parses a generic signature and its sugared signature into a list of GenericArgumentDecl.
    /// </summary>
    /// <param name="genericSignature">The generic signature to parse. </param>
    /// <param name="sugaredSignature">The sugared signature to parse. </param>
    /// <returns>A list of GenericArgumentDecl.</returns>
    public static List<GenericArgumentDecl> ParseGenericSignature(string? genericSignature, string? sugaredSignature)
    {
        if (string.IsNullOrWhiteSpace(genericSignature))
            return [];

        genericSignature = genericSignature[1..^1];

        // Fallback: if sugared signature is missing, use the generic signature itself
        // Type parameter names will be τ_0_0 style instead of T/U, but C# uses T0/T1 anyway
        if (string.IsNullOrWhiteSpace(sugaredSignature))
            sugaredSignature = genericSignature;
        else
            sugaredSignature = sugaredSignature[1..^1];

        var genericParams = ExtractGenericParams(genericSignature);
        var sugaredParams = ExtractGenericParams(sugaredSignature);

        if (genericParams.Count != sugaredParams.Count)
            throw new InvalidOperationException("Generic and sugared parameter counts do not match.");

        var paramMap = genericParams.Zip(sugaredParams, (gen, sug) => (gen, sug)).ToDictionary(x => x.gen, x => x.sug);

        var (constraints, concretePinnedParams, markerConstrainedParams) = ExtractConstraints(genericSignature);

        return genericParams.Select(typeName =>
            new GenericArgumentDecl(
                typeName,
                paramMap[typeName],
                constraints.Where(c => c.Path[0] == typeName && c.Path.Length == 1).ToList(),
                constraints.Where(c => c.Path[0] == typeName && c.Path.Length > 1).ToList(),
                HasUnrepresentableConcreteSameTypePin: concretePinnedParams.Contains(typeName),
                HasDroppedNominalMarkerConstraint: markerConstrainedParams.Contains(typeName)
            )
        ).ToList();
    }

    /// <summary>
    /// Extracts the generic parameters from a generic signature.
    /// </summary>
    /// <param name="signature">The generic signature to extract parameters from.</param>
    /// <returns>A list of generic parameters.</returns>
    private static List<string> ExtractGenericParams(string signature)
    {
        var whereIndex = signature.IndexOf("where", StringComparison.OrdinalIgnoreCase);
        if (whereIndex >= 0)
            signature = signature[..whereIndex];

        return signature.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>
    /// Extracts the constraints from a generic signature.
    /// </summary>
    /// <param name="signature">The generic signature to extract constraints from.</param>
    /// <returns>
    /// The representable constraints, plus the set of root generic-parameter names that carried a
    /// dropped same-type concrete pin (see <see cref="ParseConstraint"/>). The pin set feeds
    /// <see cref="GenericArgumentDecl.HasUnrepresentableConcreteSameTypePin"/>.
    /// </returns>
    private static (List<GenericParameterConformance> Constraints, HashSet<string> ConcretePinnedParams, HashSet<string> MarkerConstrainedParams)
        ExtractConstraints(string signature)
    {
        var whereIndex = signature.IndexOf("where", StringComparison.OrdinalIgnoreCase);
        if (whereIndex == -1)
            return (new List<GenericParameterConformance>(), new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

        var constraintsSection = signature[(whereIndex + "where".Length)..];
        // Split at top-level commas only. A constructed-generic constraint target
        // (e.g. `KeyPath<Intent, Parameter>`) carries an inner comma that a naive
        // `Split(',')` would tear apart, producing fragments that throw downstream.
        var constraintClauses = SwiftTypeListText.SplitTopLevelCommas(constraintsSection);

        // ParseConstraint returns null for constraints it cannot represent (constructed
        // generic / non-qualified targets) so a single unrepresentable constraint never
        // propagates a throw that would silently drop the whole enclosing decl. When the
        // dropped constraint is a same-type pin to a concrete type, the root parameter is
        // recorded so downstream admissibility gates still see the single-specialization
        // confinement the dropped constraint expressed.
        var parsed = new List<GenericParameterConformance>();
        var concretePinnedParams = new HashSet<string>(StringComparer.Ordinal);
        var markerConstrainedParams = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clause in constraintClauses)
        {
            var pc = ParseConstraint(clause, out var droppedConcretePinRoot, out var droppedNominalMarkerRoot);
            if (pc != null)
                parsed.Add(pc);
            else if (droppedConcretePinRoot != null)
                concretePinnedParams.Add(droppedConcretePinRoot);
            else if (droppedNominalMarkerRoot != null)
                markerConstrainedParams.Add(droppedNominalMarkerRoot);
        }

        return (parsed, concretePinnedParams, markerConstrainedParams);
    }

    /// <summary>
    /// Parses a constraint clause into a Conformance object.
    /// </summary>
    /// <param name="clause">The constraint clause to parse.</param>
    /// <param name="droppedConcretePinRoot">
    /// Set to the root generic-parameter name when this clause is a dropped same-type (<c>==</c>)
    /// constraint pinning that parameter to a concrete, non-constructed-generic target (e.g.
    /// <c>RowDecoder == ()</c>). Such a clause confines the owning member to a single
    /// specialization; the target is unrepresentable so the constraint itself is dropped, but the
    /// confinement must survive for the open-constructor-erasure admissibility gate. Null for
    /// representable constraints, protocol/layout constraints, and constructed-generic same-type
    /// targets (family relationships, not single-specialization pins).
    /// </param>
    /// <returns>
    /// A Conformance object, or null when the clause cannot be represented as a nominal
    /// conformance — specifically when the constraint target is a constructed generic
    /// (e.g. <c>ParameterKeyPath : KeyPath&lt;Intent, Parameter&gt;</c>).
    /// <see cref="GenericParameterConformance.ConformanceTarget"/> is a non-generic
    /// <see cref="SwiftTypeName"/>; stripping the generic to its outer name would record
    /// a weaker (and wrong) constraint, so the unrepresentable constraint is dropped
    /// instead. Returning null here is preferable to throwing: the throw propagates up to
    /// <c>SwiftABIParser.HandleNode</c>, which swallows it and silently discards the entire
    /// enclosing decl rather than just the one constraint.
    /// </returns>
    private static GenericParameterConformance? ParseConstraint(string clause, out string? droppedConcretePinRoot, out string? droppedNominalMarkerRoot)
    {
        droppedConcretePinRoot = null;
        droppedNominalMarkerRoot = null;

        var parts = clause.Split(new[] { ":", "==" }, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException($"Invalid constraint clause: {clause}");
        }

        var target = parts[0].Split('.');
        var conformanceTarget = parts[1];

        // A same-type (`==`) constraint pinning a parameter to a concrete, non-constructed-generic
        // target (`RowDecoder == ()`) confines the owning member to one specialization. The target
        // is dropped as unrepresentable below, but `droppedConcretePinRoot` carries the confinement
        // forward so the open-ctor-erasure gate refuses a `_SBW_CI_`/GSF wrapper that would not
        // compile against the unconstrained type. Constructed-generic same-type targets
        // (`== Foo<τ_0_1>`) are excluded: they relate parameters (a family), and that open form
        // already compiles, so flagging them would drop currently-working constructors.
        bool isSameTypeConcretePin = clause.Contains("==") && !conformanceTarget.Contains('<');

        // A constructed-generic target carries angle brackets and is not a nominal type.
        // Drop the constraint rather than feeding it to FromModuleQualifiedName (which
        // throws on '<') or stripping it to a misleading outer-name conformance.
        if (conformanceTarget.Contains('<'))
            return null;

        // Layout/marker keyword constraints (AnyObject, Sendable, Any, Copyable, ...) are not
        // representable as a nominal SwiftTypeName. They appear EITHER unqualified (a bare keyword,
        // which would make FromModuleQualifiedName throw — it requires a '.') OR qualified by the
        // standard library module (`Swift.Sendable`). Any non-module-qualified target that reached
        // FromModuleQualifiedName would throw and propagate to SwiftABIParser.HandleNode, which
        // discards the ENTIRE enclosing decl instead of just this one constraint. Drop the
        // constraint (return null) — the soft loss of one constraint is preferable to losing the decl.
        //
        // The match is module-qualified, mirroring IsStdlibMarkerProtocol everywhere else: a
        // protocol from a NON-stdlib module that merely shares one of these names
        // (`SomeModule.Sendable`) is a real user protocol carrying a witness table, so it must NOT
        // be dropped — it falls through to the nominal-conformance path below, which both keeps the
        // conformance and (via GenericConformances.Count) feeds the enum-demotion gate naturally.
        var lastDot = conformanceTarget.LastIndexOf('.');
        var simpleTarget = lastDot >= 0 ? conformanceTarget[(lastDot + 1)..] : conformanceTarget;
        var markerModule = lastDot >= 0 ? conformanceTarget[..lastDot] : null;
        // The protocol-kind markers match IsStdlibMarkerProtocol (Sendable/Escapable/Copyable/
        // SendableMetatype/BitwiseCopyable); AnyObject and Any are added here because they are
        // unrepresentable layout/keyword constraints the parser must also drop, not nominal protocols.
        var isStdlibMarkerName = simpleTarget is "AnyObject" or "Sendable" or "Escapable"
            or "Copyable" or "SendableMetatype" or "BitwiseCopyable" or "Any";
        if (isStdlibMarkerName && markerModule is null or "Swift")
        {
            if (isSameTypeConcretePin) droppedConcretePinRoot = target[0];
            // A module-qualified protocol-kind marker (e.g. `Swift.Sendable`) was a nominal
            // GenericParameterConformance before this drop existed; the enum-demotion gate keys
            // off "param has any conformance", so record the root to preserve that signal.
            else if (clause.Contains(":") && conformanceTarget.Contains('.')) droppedNominalMarkerRoot = target[0];
            return null;
        }
        if (!conformanceTarget.Contains('.'))
        {
            if (isSameTypeConcretePin) droppedConcretePinRoot = target[0];
            return null;
        }

        ConformanceKind kind = clause.Contains(":") ? ConformanceKind.Protocol : ConformanceKind.ConcreteType;
        return new GenericParameterConformance(target, SwiftTypeName.FromModuleQualifiedName(conformanceTarget), kind);
    }
}
