// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Helper class for emitting generic type declarations in C#.
/// </summary>
public static class GenericTypeEmitter
{
    private static readonly HashSet<string> UnsupportedConstraintModules = new(StringComparer.Ordinal)
    {
        "SwiftUI",
        "Combine",
    };

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
            .Select((p, i) => $"T{i}")
            .ToList();

        return $"<{string.Join(", ", typeParams)}>";
    }

    /// <summary>
    /// Gets the type name with generic parameters appended.
    /// For example, "Box" becomes "Box&lt;T0&gt;" for a generic type.
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <returns>The type name with generic parameters.</returns>
    public static string GetTypeNameWithGenerics(TypeDecl typeDecl)
    {
        return $"{typeDecl.Name}{GetGenericParameterList(typeDecl)}";
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
            var typeParamName = $"T{i}";

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

                    // Convert Swift protocol name to C# interface name
                    var interfaceName = NameProvider.GetInterfaceName(conformance.ConformanceTarget.Name);
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
        UnsupportedConstraintModules.Contains(moduleName);

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
    /// Gets the full type declaration signature including generics and where clause.
    /// For example: "Box&lt;T0&gt; where T0 : ISwiftObject"
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <param name="typeDatabase">Optional type database for checking protocol capabilities.</param>
    /// <returns>The full type signature.</returns>
    public static string GetFullTypeSignature(TypeDecl typeDecl, ITypeDatabase? typeDatabase = null)
    {
        var name = GetTypeNameWithGenerics(typeDecl);
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
            .Select((p, i) => $"TypeMetadata.GetTypeMetadataOrThrow<T{i}>()")
            .ToList();

        return string.Join(", ", typeParams);
    }
}
