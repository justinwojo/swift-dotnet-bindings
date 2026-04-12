// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Canonical source of truth for all shared validation gate predicates.
/// All validators (MemberEmissionValidator, MemberGateEvaluator, MethodValidationGates,
/// GenericTypeEmitter, BoundGenericsHandler) delegate to this class instead of reimplementing
/// checks. Eliminates divergence risk from multiple copies of the same logic.
/// </summary>
public static class ValidationRuleSet
{
    #region Module-Level Gates

    /// <summary>
    /// Modules whose types are unsupported for generic constraints and member-level filtering.
    /// These are the modules that the generator cannot yet bind — their protocol conformances
    /// are skipped in GenericTypeEmitter and BoundGenericsHandler, and their types are filtered
    /// in MemberEmissionValidator.
    /// </summary>
    private static readonly HashSet<string> UnsupportedConstraintModules = new(StringComparer.Ordinal)
    {
        "SwiftUI",
        "Combine",
    };

    /// <summary>
    /// Returns true if the module is unsupported for constraint and member-level filtering
    /// (SwiftUI, Combine).
    /// Used by: GenericTypeEmitter (type-level constraint filtering), BoundGenericsHandler
    /// (constraint skipping), MemberEmissionValidator (member-level type reference filtering).
    /// </summary>
    public static bool IsUnsupportedConstraintModule(string moduleName)
        => UnsupportedConstraintModules.Contains(moduleName);

    #endregion

    #region TypeSpec-Level Gates

    /// <summary>
    /// Returns true if the TypeSpec references a type from an unsupported module (SwiftUI, Combine, etc.)
    /// that is NOT registered in the type database. Types registered in the database (e.g., SwiftUI.Color,
    /// SwiftUI.Font from SwiftUIDatabase.xml) are considered supported and pass through.
    /// Recursively checks generic parameters, tuple elements, and closure args/return.
    /// Also rejects types that are .NET static classes (cannot be used as variables/parameters).
    /// </summary>
    public static bool ReferencesUnsupportedModule(TypeSpec? typeSpec, ITypeDatabase? typeDatabase = null)
    {
        if (typeSpec == null)
            return false;

        switch (typeSpec)
        {
            case NamedTypeSpec namedType:
                // Types that are static classes in .NET — cannot be used as variables,
                // parameters, return types, or generic type arguments (CS0718/CS0723).
                if (IsNetStaticClassType(namedType.Name))
                    return true;
                // Types that are auto-bridged but not yet present in the .NET Foundation
                // (or similar) assembly. Referencing them would produce CS0234.
                if (IsNetUnavailableBridgedType(namedType.Name))
                    return true;
                // Types the emitter will never produce (e.g., single-case no-payload
                // enums marked Unemittable). Skip anything referencing them so we don't
                // leave dangling references to a type that will never exist.
                if (typeDatabase != null && namedType.HasModule() &&
                    typeDatabase.TryGetTypeRecord(
                        SwiftTypeName.FromModuleQualifiedName(namedType.Name), out var unemittableRecord) &&
                    unemittableRecord.Flags.HasFlag(TypeRecordFlags.Unemittable))
                {
                    return true;
                }
                if (namedType.HasModule() && IsUnsupportedConstraintModule(namedType.Module))
                {
                    // Registered non-generic types (with C# ISwiftObject stubs) pass through.
                    // Generic usages, null DB, and unregistered types still rejected.
                    if (typeDatabase == null ||
                        namedType.ContainsGenericParameters ||
                        !typeDatabase.TryGetTypeRecord(
                            SwiftTypeName.FromModuleQualifiedName(namedType.Name), out _))
                    {
                        return true;
                    }
                    // Registered non-generic type — fall through to generic parameter check
                }
                foreach (var genericParam in namedType.GenericParameters)
                {
                    if (ReferencesUnsupportedModule(genericParam, typeDatabase))
                        return true;
                }
                return false;

            case TupleTypeSpec tupleType:
                foreach (var element in tupleType.Elements)
                {
                    if (ReferencesUnsupportedModule(element, typeDatabase))
                        return true;
                }
                return false;

            case ClosureTypeSpec closureType:
                if (ReferencesUnsupportedModule(closureType.Arguments, typeDatabase))
                    return true;
                if (ReferencesUnsupportedModule(closureType.ReturnType, typeDatabase))
                    return true;
                return false;

            case ProtocolListTypeSpec protocolList:
                foreach (var protocol in protocolList.Protocols.Keys)
                {
                    if (ReferencesUnsupportedModule(protocol, typeDatabase))
                        return true;
                }
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Returns true if the TypeSpec contains an associated type reference (e.g., Self.Element,
    /// τ_0_0.ID) that would produce an unresolvable C# type like TElement or TRowDecoder.ID.
    /// These are protocol-scoped type parameters that leak into interface/proxy member signatures
    /// when the protocol doesn't declare them as generic parameters.
    /// </summary>
    public static bool ContainsAssociatedTypeReference(TypeSpec? typeSpec)
    {
        if (typeSpec == null)
            return false;

        switch (typeSpec)
        {
            case AssociatedTypeReferenceSpec:
                return true;

            case NamedTypeSpec namedType:
                // Optional<AssocType> etc.
                foreach (var genericParam in namedType.GenericParameters)
                {
                    if (ContainsAssociatedTypeReference(genericParam))
                        return true;
                }
                return false;

            case TupleTypeSpec tupleType:
                foreach (var element in tupleType.Elements)
                {
                    if (ContainsAssociatedTypeReference(element))
                        return true;
                }
                return false;

            case ClosureTypeSpec closureType:
                if (ContainsAssociatedTypeReference(closureType.Arguments))
                    return true;
                if (ContainsAssociatedTypeReference(closureType.ReturnType))
                    return true;
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Recursively checks if a TypeSpec references a type that is internal to the given module.
    /// A type is considered internal if it belongs to the module (qualified with the module name)
    /// but is not found in the type database — meaning it wasn't part of the public API surface.
    /// </summary>
    public static bool ReferencesInternalModuleType(TypeSpec? typeSpec, ITypeDatabase typeDatabase, string moduleName)
    {
        if (typeSpec == null)
            return false;

        switch (typeSpec)
        {
            case NamedTypeSpec namedType:
                // Skip existential types (any Protocol) — these are protocol usages,
                // not concrete type references. A protocol may validly not be in the
                // type database while still being publicly accessible.
                if (namedType.IsAny)
                    return false;
                if (namedType.HasModule())
                {
                    var typeModule = namedType.Module;
                    if (typeModule == moduleName)
                    {
                        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                        if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out _))
                            return true;
                    }
                }
                foreach (var genericParam in namedType.GenericParameters)
                {
                    if (ReferencesInternalModuleType(genericParam, typeDatabase, moduleName))
                        return true;
                }
                return false;

            case TupleTypeSpec tupleType:
                foreach (var element in tupleType.Elements)
                {
                    if (ReferencesInternalModuleType(element, typeDatabase, moduleName))
                        return true;
                }
                return false;

            case ClosureTypeSpec closureType:
                if (ReferencesInternalModuleType(closureType.Arguments, typeDatabase, moduleName))
                    return true;
                if (ReferencesInternalModuleType(closureType.ReturnType, typeDatabase, moduleName))
                    return true;
                return false;

            case ProtocolListTypeSpec:
                // Protocol compositions (any P1 & P2) are existential references.
                // Constituent protocols may legitimately be absent from the type database
                // while still being public — protocol TypeRecords are only created when
                // the protocol has enough infrastructure for proxy/conformance emission.
                // Internal protocol compositions are caught downstream by the wrapper
                // post-processor safety net.
                return false;

            default:
                return false;
        }
    }

    #endregion

    #region Method-Level Gates

    /// <summary>
    /// Checks if the method has constraints on protocols with associated types or self requirements.
    /// Delegates to MethodValidationGates for the implementation.
    /// </summary>
    public static bool HasUnsupportedProtocolConstraints(MethodDecl methodDecl, ITypeDatabase typeDatabase)
        => MethodValidationGates.HasUnsupportedProtocolConstraints(methodDecl, typeDatabase);

    /// <summary>
    /// Checks whether a protocol is unsupported as a constraint — has associated types
    /// or HasSelfRequirement. Delegates to MethodValidationGates for the implementation.
    /// </summary>
    public static bool IsUnsupportedProtocolConstraint(SwiftTypeName protocolTypeName, ITypeDatabase typeDatabase)
        => MethodValidationGates.IsUnsupportedProtocolConstraint(protocolTypeName, typeDatabase);

    #endregion

    #region C# Type Name Gates

    /// <summary>
    /// Checks if a resolved C# type name contains AnyType as a generic type argument
    /// (inside angle brackets). Plain AnyType as a standalone type is NOT flagged —
    /// it's degraded but compilable. Only AnyType within a bound generic (e.g.,
    /// BatchedCollection&lt;Swift.AnyType&gt;) is problematic because it violates
    /// generic constraints.
    /// </summary>
    public static bool ContainsAnyTypeGenericArg(string csharpTypeName)
    {
        int angleBracketStart = csharpTypeName.IndexOf('<');
        if (angleBracketStart < 0) return false;
        var genericPart = csharpTypeName.Substring(angleBracketStart);
        // Token-aware match: ensure "AnyType" is a standalone type identifier,
        // not part of a larger name (e.g., reject "MyAnyTypeModel")
        int idx = 0;
        while ((idx = genericPart.IndexOf("AnyType", idx, StringComparison.Ordinal)) >= 0)
        {
            bool startOk = idx == 0 || !IsIdentifierChar(genericPart[idx - 1]);
            int end = idx + "AnyType".Length;
            bool endOk = end >= genericPart.Length || !IsIdentifierChar(genericPart[end]);
            if (startOk && endOk) return true;
            idx++;
        }
        return false;
    }

    #endregion

    #region Static Class Type Detection

    /// <summary>
    /// Known .NET types that are static classes and cannot be used as variables, parameters,
    /// return types, or generic type arguments (CS0718/CS0723).
    /// </summary>
    private static readonly HashSet<string> NetStaticClassTypes = new(StringComparer.Ordinal)
    {
        "UIKit.UITextContentType",
    };

    /// <summary>
    /// Returns true if the given module-qualified type name is a known .NET static class.
    /// </summary>
    public static bool IsNetStaticClassType(string moduleQualifiedName)
        => NetStaticClassTypes.Contains(moduleQualifiedName);

    /// <summary>
    /// Returns true if the type name refers to an auto-bridged Swift type that does not exist
    /// in the .NET Foundation (or similar) assembly. Such references must be suppressed before
    /// they reach the C# compiler to avoid CS0234. Data lives in <c>apple-frameworks.json</c>
    /// under each framework's <c>netUnavailableTypes</c> entry — add new exclusions there so
    /// the registry stays the single source of truth for Apple framework facts.
    /// </summary>
    public static bool IsNetUnavailableBridgedType(string moduleQualifiedName)
        => AppleFrameworkRegistry.IsNetUnavailableType(moduleQualifiedName);

    #endregion

    #region Private Helpers

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    #endregion
}
