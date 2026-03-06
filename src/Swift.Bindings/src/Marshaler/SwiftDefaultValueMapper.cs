// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

[assembly: InternalsVisibleTo("Swift.Bindings.Unit.Tests")]

namespace BindingsGeneration;

/// <summary>
/// Maps Swift default value expressions from .swiftinterface to C# compile-time constants.
/// Returns null for unmappable expressions (struct constructors, static properties, arrays, etc.).
/// </summary>
public static class SwiftDefaultValueMapper
{
    // Regex to detect integer literals (with optional underscores and sign)
    private static readonly Regex IntegerRegex = new(@"^-?[0-9][0-9_]*$", RegexOptions.Compiled);

    // Regex to detect floating-point literals (with optional underscores and sign)
    private static readonly Regex FloatRegex = new(@"^-?[0-9][0-9_]*\.[0-9_]+$", RegexOptions.Compiled);

    /// <summary>
    /// Attempts to map a Swift default value expression to a C# compile-time constant.
    /// Returns null if the expression cannot be represented as a C# default parameter value.
    /// </summary>
    /// <param name="swiftExpr">The raw Swift default expression (e.g., "10", "true", ".mid", "nil").</param>
    /// <param name="paramTypeSpec">The TypeSpec of the parameter (used for nil/enum resolution).</param>
    /// <param name="typeDatabase">The type database for enum/type resolution.</param>
    public static string? TryMapToCSharpDefault(string swiftExpr, TypeSpec paramTypeSpec, ITypeDatabase typeDatabase)
    {
        if (string.IsNullOrWhiteSpace(swiftExpr))
            return null;

        var expr = swiftExpr.Trim();

        // nil → null (reference types) or default (value types / SwiftOptional)
        if (expr == "nil")
            return MapNil(paramTypeSpec, typeDatabase);

        // Bool literals
        if (expr == "true") return "true";
        if (expr == "false") return "false";

        // Integer literals (with underscore stripping)
        if (IntegerRegex.IsMatch(expr))
            return expr.Replace("_", "");

        // Float literals (with underscore stripping)
        if (FloatRegex.IsMatch(expr))
        {
            var cleaned = expr.Replace("_", "");
            // Add 'f' suffix for Swift.Float (32-bit) parameters
            if (IsSwiftFloat(paramTypeSpec))
                return cleaned + "f";
            return cleaned;
        }

        // String literals
        if (expr.StartsWith("\"") && expr.EndsWith("\""))
            return expr;

        // .none on Optional type → nil
        if (expr == ".none" && IsOptionalType(paramTypeSpec))
            return MapNil(paramTypeSpec, typeDatabase);

        // Enum dot syntax: .caseName
        if (expr.StartsWith("."))
            return MapEnumCase(expr.Substring(1), paramTypeSpec, typeDatabase);

        // Qualified enum: Type.caseName (no parens, no brackets — excludes constructors/arrays)
        if (expr.Contains('.') && !expr.Contains('(') && !expr.Contains('['))
            return MapQualifiedEnumCase(expr, paramTypeSpec, typeDatabase);

        // Everything else (struct ctors, static props, arrays, dict literals, function calls) → unmappable
        return null;
    }

    private static string? MapNil(TypeSpec paramTypeSpec, ITypeDatabase typeDatabase)
    {
        // Optional types → null
        if (IsOptionalType(paramTypeSpec))
            return "null";

        // Check if the projected C# type is a reference type (class, protocol proxy)
        if (TryLookupTypeRecord(paramTypeSpec.ToString(), typeDatabase, out var record))
        {
            if (record!.Kind == TypeRecordKind.Class || record.Kind == TypeRecordKind.Protocol ||
                record.Kind == TypeRecordKind.Existential)
                return "null";
        }

        // Default fallback for value types
        return "default";
    }

    private static bool IsOptionalType(TypeSpec typeSpec)
    {
        return typeSpec is NamedTypeSpec named && named.Name == "Swift.Optional";
    }

    private static bool IsSwiftFloat(TypeSpec typeSpec)
    {
        // Unwrap Optional
        var inner = typeSpec;
        if (inner is NamedTypeSpec opt && opt.Name == "Swift.Optional" && opt.GenericParameters.Count == 1)
            inner = opt.GenericParameters[0];

        return inner is NamedTypeSpec named && named.Name == "Swift.Float";
    }

    private static string? MapEnumCase(string caseName, TypeSpec paramTypeSpec, ITypeDatabase typeDatabase)
    {
        // Unwrap Optional if needed
        var innerType = paramTypeSpec;
        if (innerType is NamedTypeSpec opt && opt.Name == "Swift.Optional" && opt.GenericParameters.Count == 1)
            innerType = opt.GenericParameters[0];

        if (!TryLookupTypeRecord(innerType.ToString(), typeDatabase, out var record))
            return null;

        // Only map simple enums (C# value type enums)
        if (!record!.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            return null;

        var csTypeName = record.CSharpTypeName.FullyQualifiedName;
        var csCaseName = NameProvider.ToPascalCase(caseName);
        return $"{csTypeName}.{csCaseName}";
    }

    private static string? MapQualifiedEnumCase(string expr, TypeSpec paramTypeSpec, ITypeDatabase typeDatabase)
    {
        var lastDot = expr.LastIndexOf('.');
        if (lastDot <= 0) return null;

        var typePart = expr.Substring(0, lastDot);
        var casePart = expr.Substring(lastDot + 1);

        // Try direct lookup (works for fully module-qualified names like "Module.EnumType")
        if (TryLookupTypeRecord(typePart, typeDatabase, out var record))
        {
            if (record!.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            {
                var csTypeName = record.CSharpTypeName.FullyQualifiedName;
                var csCaseName = NameProvider.ToPascalCase(casePart);
                return $"{csTypeName}.{csCaseName}";
            }
            return null;
        }

        // Fallback: unqualified type name (e.g., "SVGColor.black") — resolve via paramTypeSpec.
        // Only for simple identifiers (no dots in typePart). Property chains like
        // "LottieConfiguration.shared.decodingStrategy" have dots in typePart and are not enum cases.
        if (typePart.Contains('.'))
            return null;

        var innerType = paramTypeSpec;
        if (innerType is NamedTypeSpec opt && opt.Name == "Swift.Optional" && opt.GenericParameters.Count == 1)
            innerType = opt.GenericParameters[0];

        if (TryLookupTypeRecord(innerType.ToString(), typeDatabase, out var paramRecord) &&
            paramRecord!.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
        {
            var csTypeName = paramRecord.CSharpTypeName.FullyQualifiedName;
            var csCaseName = NameProvider.ToPascalCase(casePart);
            return $"{csTypeName}.{csCaseName}";
        }

        return null;
    }

    /// <summary>
    /// Safely looks up a type record by module-qualified name string.
    /// Returns false if the name is invalid for SwiftTypeName (unqualified, generic, etc.).
    /// </summary>
    private static bool TryLookupTypeRecord(string moduleQualifiedName, ITypeDatabase typeDatabase, out TypeRecord? record)
    {
        record = null;
        if (string.IsNullOrEmpty(moduleQualifiedName) || moduleQualifiedName.Contains('<') || !moduleQualifiedName.Contains('.'))
            return false;
        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName);
            return typeDatabase.TryGetTypeRecord(swiftTypeName, out record);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
