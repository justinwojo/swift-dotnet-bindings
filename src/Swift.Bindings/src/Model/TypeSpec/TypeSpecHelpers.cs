// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Helper methods for working with TypeSpec instances.
/// </summary>
public static class TypeSpecHelpers
{
    /// <summary>
    /// Checks if a type name represents a generic type parameter.
    /// Swift generic type parameters appear as:
    /// - Internal notation: τ_0_0, τ_0_1, τ_1_0, etc.
    /// - Simple names: T, U, V, Element, Key, Value, etc.
    /// </summary>
    /// <param name="typeName">The type name to check.</param>
    /// <returns>True if the type name represents a generic type parameter.</returns>
    public static bool IsGenericTypeParameter(string typeName)
    {
        // Swift internal generic parameter notation: τ_0_0, τ_0_1, τ_1_0, etc.
        // This is the canonical form in Swift ABI JSON for generic type parameters.
        if (typeName.StartsWith("τ_"))
            return true;

        // Check for simple generic parameter names without module qualifier
        if (!typeName.Contains('.') && typeName.Length <= 3)
        {
            // Common single-letter generic parameters (T, U, V, etc.)
            if (typeName is "T" or "U" or "V" or "W" or "E" or "K" or "R" or "S")
                return true;

            // Type parameters often follow naming patterns like T0, T1, T2
            // These appear in generated C# output names from GenericContext.
            if (typeName.Length >= 2 &&
                char.IsUpper(typeName[0]) && typeName.Skip(1).All(char.IsDigit))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a TypeSpec represents a generic type parameter.
    /// </summary>
    /// <param name="typeSpec">The type specification to check.</param>
    /// <returns>True if the TypeSpec represents a generic type parameter.</returns>
    public static bool IsGenericTypeParameter(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec namedType)
            return IsGenericTypeParameter(namedType.Name);
        return false;
    }

    /// <summary>
    /// Checks if a type name is a protocol-level (depth 0) generic type parameter like τ_0_0 (Self).
    /// Returns false for method-level generic parameters (τ_1_0, τ_1_1, etc.) which are independent
    /// of the conforming type and can be satisfied by EveryProtocol with stub implementations.
    /// </summary>
    public static bool IsProtocolLevelGenericParam(string typeName)
    {
        // τ_0_0, τ_0_1, etc. are depth-0 (protocol-level) params including Self
        if (typeName.StartsWith("τ_0_"))
            return true;

        // Simple names (T, U, etc.) could be either — treat as Self conservatively
        // since we can't distinguish from naming alone
        if (!typeName.Contains('.') && typeName.Length <= 3)
        {
            if (typeName is "T" or "U" or "V" or "W" or "E" or "K" or "R" or "S")
                return true;
            if (typeName.Length >= 2 &&
                char.IsUpper(typeName[0]) && typeName.Skip(1).All(char.IsDigit))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a TypeSpec tree contains any reference to the given type names.
    /// Used to detect whether a method's return type references parent generic type parameters.
    /// </summary>
    /// <param name="typeSpec">The type specification to check.</param>
    /// <param name="names">Set of type names to look for (e.g., parent generic param names like τ_0_0).</param>
    /// <returns>True if any node in the TypeSpec tree matches a name in the set.</returns>
    public static bool ContainsAnyTypeName(TypeSpec typeSpec, HashSet<string> names)
    {
        return typeSpec switch
        {
            NamedTypeSpec ns => names.Contains(ns.Name)
                || ns.GenericParameters.Any(p => ContainsAnyTypeName(p, names))
                || (ns.InnerType != null && ContainsAnyTypeName(ns.InnerType, names)),
            TupleTypeSpec ts => ts.Elements.Any(e => ContainsAnyTypeName(e, names)),
            ClosureTypeSpec cs => ContainsAnyTypeName(cs.Arguments, names)
                || ContainsAnyTypeName(cs.ReturnType, names),
            _ => false
        };
    }
}
