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
    /// <param name="visibleGenericNames">
    /// Optional set of generic-parameter names visible in the surrounding method's scope —
    /// both ABI-canonical (<c>τ_0_0</c>) and source-level sugared (<c>Value</c>, <c>Element</c>).
    /// When supplied, an unconstrained-T <c>nil</c> default with a sugared name maps to
    /// <c>default</c> instead of falling through to <c>null</c> (which would be CS1750).
    /// Callers that have a <see cref="MethodDecl"/> should pass
    /// <c>BaseHandler.CollectVisibleGenericParamNames(methodDecl)</c>; the heuristic
    /// <see cref="TypeSpecHelpers.IsGenericTypeParameter"/> still catches the canonical
    /// names when this is null (which keeps the unit-test surface and detached fixtures
    /// working without a parent decl).
    /// </param>
    public static string? TryMapToCSharpDefault(
        string swiftExpr,
        TypeSpec paramTypeSpec,
        ITypeDatabase typeDatabase,
        IReadOnlySet<string>? visibleGenericNames = null)
    {
        if (string.IsNullOrWhiteSpace(swiftExpr))
            return null;

        var expr = swiftExpr.Trim();

        // nil → null (reference types) or default (value types / SwiftOptional)
        if (expr == "nil")
            return MapNil(paramTypeSpec, typeDatabase, visibleGenericNames);

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
            return MapNil(paramTypeSpec, typeDatabase, visibleGenericNames);

        // Enum dot syntax: .caseName
        if (expr.StartsWith("."))
            return MapEnumCase(expr.Substring(1), paramTypeSpec, typeDatabase);

        // Qualified enum: Type.caseName (no parens, no brackets — excludes constructors/arrays)
        if (expr.Contains('.') && !expr.Contains('(') && !expr.Contains('['))
            return MapQualifiedEnumCase(expr, paramTypeSpec, typeDatabase);

        // Everything else (struct ctors, static props, arrays, dict literals, function calls) → unmappable
        return null;
    }

    private static string? MapNil(
        TypeSpec paramTypeSpec,
        ITypeDatabase typeDatabase,
        IReadOnlySet<string>? visibleGenericNames)
    {
        // Unconstrained generic parameters: `null` is illegal as a default for an unconstrained
        // C# type parameter (CS1750). The `default` literal infers the right zero value from
        // context (T? → null when T is reference; default(Nullable<T>) → no value when T is
        // value). Detect both bare generic-param refs (`T`, `τ_0_0`, sugared `Value`) and
        // Optional<GenericParam>. The sugared-name path requires the caller-supplied
        // visibleGenericNames set; without it we fall back to the heuristic-only recogniser
        // which still catches τ_*_* and the single-letter conventions.
        if (IsGenericParameterRef(paramTypeSpec, visibleGenericNames))
            return "default";
        if (paramTypeSpec is NamedTypeSpec optNamed && optNamed.Name == "Swift.Optional" &&
            optNamed.GenericParameters.Count == 1 &&
            IsGenericParameterRef(optNamed.GenericParameters[0], visibleGenericNames))
            return "default";

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

    private static bool IsGenericParameterRef(TypeSpec typeSpec, IReadOnlySet<string>? visibleGenericNames)
    {
        if (typeSpec is NamedTypeSpec named && visibleGenericNames is not null &&
            visibleGenericNames.Contains(named.Name))
            return true;
        return TypeSpecHelpers.IsGenericTypeParameter(typeSpec);
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

        return QualifyEnumCase(record, caseName);
    }

    /// <summary>
    /// Names <paramref name="caseName"/> as a member of <paramref name="record"/>'s C# enum.
    ///
    /// An ObjC-bridged NS_ENUM carries the member names its companion declared, because the companion
    /// strips a shared prefix off every case (the enum's own name, or the module's acronym tag) that
    /// Swift's importer does not necessarily strip the same way — PascalCasing the Swift spelling of a
    /// stripped case names a member that was never declared. Every other enum keeps the PascalCase
    /// transform, which is what its own declaration site uses.
    /// </summary>
    private static string QualifyEnumCase(TypeRecord record, string caseName)
    {
        var csTypeName = record.CSharpTypeName.FullyQualifiedName;
        var csCaseName = ObjCEnumCaseNames.TryResolveEmittedName(record.ObjCEnumCaseNames, caseName, out var emitted)
            ? emitted
            : NameProvider.ToPascalCase(caseName);
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
                return QualifyEnumCase(record, casePart);
            return null;
        }

        // Fallback: unqualified type name (e.g., "SVGColor.black") — resolve via paramTypeSpec.
        // Only for simple identifiers (no dots in typePart). Property chains like
        // "TypeName.propertyName.nestedProperty" have dots in typePart and are not enum cases.
        if (typePart.Contains('.'))
            return null;

        var innerType = paramTypeSpec;
        if (innerType is NamedTypeSpec opt && opt.Name == "Swift.Optional" && opt.GenericParameters.Count == 1)
            innerType = opt.GenericParameters[0];

        if (TryLookupTypeRecord(innerType.ToString(), typeDatabase, out var paramRecord) &&
            paramRecord!.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
        {
            return QualifyEnumCase(paramRecord, casePart);
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
