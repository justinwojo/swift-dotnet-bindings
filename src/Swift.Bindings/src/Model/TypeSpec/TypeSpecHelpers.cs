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
}
