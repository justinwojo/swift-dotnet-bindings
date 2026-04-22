// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Helper class for emitting generic type declarations in C#.
/// </summary>
public static class GenericTypeEmitter
{

    /// <summary>
    /// Gets the generic type parameter list for a type declaration.
    /// For example, if a type has parameters T and U, returns "&lt;T0, T1&gt;".
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <returns>The generic parameter list, or empty string if not generic.</returns>
    public static string GetGenericParameterList(TypeDecl typeDecl)
    {
        if (!typeDecl.IsGeneric)
            return string.Empty;

        var typeParams = typeDecl.GenericParameters
            .Select((p, i) => NameProvider.GetCSharpGenericParameterName(p, i))
            .ToList();

        return $"<{string.Join(", ", typeParams)}>";
    }

    /// <summary>
    /// Gets the type name with generic parameters appended.
    /// For example, "Box" becomes "Box&lt;T0&gt;" for a generic type.
    /// When a type database is provided, checks if the CSharpTypeName was renamed
    /// (e.g., nested type "Options" → "OptionsType" to avoid CS0102 collision with a property).
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <param name="typeDatabase">Optional type database for CSharpTypeName rename resolution.</param>
    /// <returns>The type name with generic parameters.</returns>
    public static string GetTypeNameWithGenerics(TypeDecl typeDecl, ITypeDatabase? typeDatabase = null)
    {
        var baseName = NameProvider.ToPascalCaseForTypeName(typeDecl.Name);

        // Check if CSharpTypeName was renamed (e.g., by ComputePropertyRenames for nested type collisions).
        // The CSharpTypeName.Name may be "Parent.OptionsType" when TypeDecl.Name is still "Options".
        if (typeDatabase != null && typeDatabase.TryGetTypeRecord(typeDecl.SwiftTypeName, out var record))
        {
            var csName = record.CSharpTypeName.Name;
            // Extract the leaf name (last dot-separated segment)
            var lastDot = csName.LastIndexOf('.');
            var leafName = lastDot >= 0 ? csName.Substring(lastDot + 1) : csName;
            if (leafName != baseName)
                baseName = leafName;
        }

        return $"{baseName}{GetGenericParameterList(typeDecl)}";
    }

    /// <summary>
    /// Gets the where clause constraints for a generic type declaration.
    /// Each generic parameter gets an ISwiftObject constraint, plus any protocol constraints.
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <param name="typeDatabase">Optional type database for checking protocol capabilities.</param>
    /// <returns>The where clause, or empty string if no constraints.</returns>
    public static string GetWhereClause(TypeDecl typeDecl, ITypeDatabase? typeDatabase = null)
    {
        if (!typeDecl.IsGeneric)
            return string.Empty;

        var constraints = new List<string>();

        for (int i = 0; i < typeDecl.GenericParameters.Count; i++)
        {
            var param = typeDecl.GenericParameters[i];
            var typeParamName = NameProvider.GetCSharpGenericParameterName(param, i);

            // Build list of constraints for this parameter
            var paramConstraints = new List<string> { "ISwiftObject" };

            // Add protocol conformance constraints
            foreach (var conformance in param.GenericConformances)
            {
                if (conformance.Kind == ConformanceKind.Protocol)
                {
                    // Skip Sendable as it doesn't have a C# equivalent
                    if (conformance.ConformanceTarget.Name == "Sendable")
                        continue;

                    // Skip constraints from unsupported framework modules (e.g. SwiftUI.View).
                    if (IsUnsupportedConstraintModule(conformance.ConformanceTarget.Module))
                        continue;

                    // Skip protocols with associated types (they generate generic interfaces
                    // which can't be used as constraints without type arguments)
                    if (typeDatabase != null && HasAssociatedTypes(typeDatabase, conformance.ConformanceTarget))
                        continue;

                    // Skip protocols whose methods use Self (τ_0_0) in parameter/return types.
                    // The interface emits AnyType for Self positions, so concrete types can't
                    // implement the interface (CS0738) and the constraint can't be satisfied.
                    if (typeDatabase != null && HasMethodSelfTypeParams(typeDatabase, conformance.ConformanceTarget))
                        continue;

                    // Skip protocols whose Self is a required associated type (Equatable,
                    // Hashable, Comparable, …). These cannot be expressed as a non-generic
                    // C# interface constraint; the PWT arg still flows via descriptor symbol
                    // through PInvokeHelperEmitter's runtime-descriptor path.
                    if (typeDatabase != null && HasSelfRequirement(typeDatabase, conformance.ConformanceTarget))
                        continue;

                    // Skip cross-module protocol constraints not registered in TypeDatabase.
                    // Same-module protocols are always registered during module processing.
                    if (typeDatabase != null
                        && conformance.ConformanceTarget.Module != (typeDecl.ModuleDecl?.Name ?? ""))
                    {
                        if (!typeDatabase.TryGetTypeRecord(conformance.ConformanceTarget, out var constraintRecord))
                            continue;
                        // Skip well-known stdlib protocols that map to runtime types (not interfaces).
                        // e.g., Swift.Error → AnyError (no IError interface is emitted)
                        if (TypeDatabaseExtensions.IsWellKnownRuntimeProtocol(constraintRecord))
                            continue;
                    }

                    // Convert Swift protocol name to C# interface name
                    var interfaceName = NameProvider.GetInterfaceName(conformance.ConformanceTarget.Name, moduleName: conformance.ConformanceTarget.Module);
                    paramConstraints.Add(interfaceName);
                }
            }

            constraints.Add($"{typeParamName} : {string.Join(", ", paramConstraints)}");
        }

        if (constraints.Count == 0)
            return string.Empty;

        // Each type parameter needs its own 'where' clause in C#
        // e.g., "where T0 : ISwiftObject where T1 : ISwiftObject"
        return string.Join(" ", constraints.Select(c => $"where {c}"));
    }

    /// <summary>
    /// Detects whether a generic type has a protocol constraint from an unsupported module
    /// (e.g. SwiftUI), which should cause the type to be skipped during emission.
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <param name="unsupportedConstraint">The first unsupported protocol constraint encountered.</param>
    /// <returns>True if an unsupported constraint was found; otherwise false.</returns>
    public static bool TryGetUnsupportedConstraint(TypeDecl typeDecl, [NotNullWhen(true)] out SwiftTypeName? unsupportedConstraint)
    {
        unsupportedConstraint = null;
        if (!typeDecl.IsGeneric)
            return false;

        foreach (var param in typeDecl.GenericParameters)
        {
            foreach (var conformance in param.GenericConformances)
            {
                if (conformance.Kind != ConformanceKind.Protocol)
                    continue;

                if (IsUnsupportedConstraintModule(conformance.ConformanceTarget.Module))
                {
                    unsupportedConstraint = conformance.ConformanceTarget;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsUnsupportedConstraintModule(string moduleName) =>
        ValidationRuleSet.IsUnsupportedConstraintModule(moduleName);

    /// <summary>
    /// Returns true if the module is unsupported for constraint and member-level filtering.
    /// Delegates to <see cref="ValidationRuleSet.IsUnsupportedConstraintModule"/> as the
    /// single source of truth.
    /// </summary>
    public static bool IsUnsupportedModule(string moduleName) =>
        ValidationRuleSet.IsUnsupportedConstraintModule(moduleName);

    /// <summary>
    /// Checks if a protocol has associated types (which would make it a generic interface in C#).
    /// </summary>
    private static bool HasAssociatedTypes(ITypeDatabase typeDatabase, SwiftTypeName protocolTypeName)
    {
        if (typeDatabase.TryGetTypeRecord(protocolTypeName, out var record))
        {
            return record.Kind == TypeRecordKind.Protocol &&
                   record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes);
        }
        return false;
    }

    /// <summary>
    /// Checks if a protocol's methods use Self (τ_0_0) in parameter/return types.
    /// Such protocols emit AnyType for Self positions in the interface, making the
    /// constraint unsatisfiable by concrete types (CS0738/CS0311).
    /// </summary>
    private static bool HasMethodSelfTypeParams(ITypeDatabase typeDatabase, SwiftTypeName protocolTypeName)
    {
        if (typeDatabase.TryGetTypeRecord(protocolTypeName, out var record))
        {
            return record.Kind == TypeRecordKind.Protocol &&
                   record.Flags.HasFlag(TypeRecordFlags.HasMethodSelfTypeParams);
        }
        return false;
    }

    /// <summary>
    /// Checks whether a protocol has <c>Self</c> as a required associated type. The Swift
    /// metadata accessor still expects a witness-table argument for these, but they cannot
    /// be projected as a usable C# interface constraint — routed through the descriptor
    /// symbol path in <see cref="PInvokeHelperEmitter"/> instead.
    /// </summary>
    private static bool HasSelfRequirement(ITypeDatabase typeDatabase, SwiftTypeName protocolTypeName)
    {
        if (typeDatabase.TryGetTypeRecord(protocolTypeName, out var record))
        {
            return record.Kind == TypeRecordKind.Protocol &&
                   record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement);
        }
        return false;
    }

    /// <summary>
    /// Gets the full type declaration signature including generics and where clause.
    /// For example: "Box&lt;T0&gt; where T0 : ISwiftObject"
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <param name="typeDatabase">Optional type database for checking protocol capabilities.</param>
    /// <returns>The full type signature.</returns>
    public static string GetFullTypeSignature(TypeDecl typeDecl, ITypeDatabase? typeDatabase = null)
    {
        var name = GetTypeNameWithGenerics(typeDecl, typeDatabase);
        var whereClause = GetWhereClause(typeDecl, typeDatabase);

        if (string.IsNullOrEmpty(whereClause))
            return name;

        return $"{name} {whereClause}";
    }

    /// <summary>
    /// Generates the GetTypeMetadata implementation for a generic type.
    /// Generic types need to pass type metadata for each type parameter to the metadata accessor.
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <returns>The GetTypeMetadata method body.</returns>
    public static string GetGenericMetadataAccessor(TypeDecl typeDecl)
    {
        if (!typeDecl.IsGeneric)
            return string.Empty;

        var typeParams = typeDecl.GenericParameters
            .Select((p, i) => $"TypeMetadata.GetTypeMetadataOrThrow<{NameProvider.GetCSharpGenericParameterName(p, i)}>()")
            .ToList();

        return string.Join(", ", typeParams);
    }
}
