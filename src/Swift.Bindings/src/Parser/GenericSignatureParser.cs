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

        var constraints = ExtractConstraints(genericSignature);

        return genericParams.Select(typeName =>
            new GenericArgumentDecl(
                typeName,
                paramMap[typeName],
                constraints.Where(c => c.Path[0] == typeName && c.Path.Length == 1).ToList(),
                constraints.Where(c => c.Path[0] == typeName && c.Path.Length > 1).ToList()
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
    /// <returns>A list of constraints.</returns>
    private static List<GenericParameterConformance> ExtractConstraints(string signature)
    {
        var whereIndex = signature.IndexOf("where", StringComparison.OrdinalIgnoreCase);
        if (whereIndex == -1)
            return new List<GenericParameterConformance>();

        var constraintsSection = signature[(whereIndex + "where".Length)..];
        // Split at top-level commas only. A constructed-generic constraint target
        // (e.g. `KeyPath<Intent, Parameter>`) carries an inner comma that a naive
        // `Split(',')` would tear apart, producing fragments that throw downstream.
        var constraints = SwiftTypeListText.SplitTopLevelCommas(constraintsSection);

        // ParseConstraint returns null for constraints it cannot represent (constructed
        // generic targets); OfType drops those so a single unrepresentable constraint
        // never propagates a throw that would silently drop the whole enclosing decl.
        var parsedConstraints = constraints
            .Select(ParseConstraint)
            .OfType<GenericParameterConformance>();

        return [.. parsedConstraints];
    }

    /// <summary>
    /// Parses a constraint clause into a Conformance object.
    /// </summary>
    /// <param name="clause">The constraint clause to parse.</param>
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
    private static GenericParameterConformance? ParseConstraint(string clause)
    {
        var parts = clause.Split(new[] { ":", "==" }, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException($"Invalid constraint clause: {clause}");
        }

        var target = parts[0].Split('.');
        var conformanceTarget = parts[1];

        // A constructed-generic target carries angle brackets and is not a nominal type.
        // Drop the constraint rather than feeding it to FromModuleQualifiedName (which
        // throws on '<') or stripping it to a misleading outer-name conformance.
        if (conformanceTarget.Contains('<'))
            return null;

        ConformanceKind kind = clause.Contains(":") ? ConformanceKind.Protocol : ConformanceKind.ConcreteType;
        return new GenericParameterConformance(target, SwiftTypeName.FromModuleQualifiedName(conformanceTarget), kind);
    }
}
