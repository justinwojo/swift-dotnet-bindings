// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Helper class for handling bound generic types in Swift bindings.
/// It translates Swift generic types into their C# representations and provides
/// type information for marshalling.
/// </summary>
public class BoundGenericsHandler
{
    private static readonly HashSet<string> s_unsupportedConstraintModules = new(StringComparer.Ordinal)
    {
        "SwiftUI",
        "Combine",
    };

    private readonly ITypeDatabase _typeDatabase;
    private readonly ClosureHandler _closureHandler;
    private readonly TupleHandler _tupleHandler;
    private readonly ExistentialHandler _existentialHandler;

    public BoundGenericsHandler(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
        _closureHandler = new ClosureHandler(typeDatabase);
        _tupleHandler = new TupleHandler(typeDatabase);
        _existentialHandler = new ExistentialHandler(typeDatabase);
    }

    // Almost all generics will be projected into C# as classes.
    // This collection contains types that must be marshalled as structs.
    // We might introduce a new field in the TypeRecord to indicate this
    private static readonly HashSet<SwiftTypeName> s_structGenerics = new()
        {
            SwiftTypeName.FromModuleQualifiedName("Swift.UnsafeMutableBufferPointer"),
            SwiftTypeName.FromModuleQualifiedName("Swift.UnsafeMutablePointer"),
        };

    // TODO: Add more types as needed.
    // Mapping of Swift generic types to their corresponding buffer types.
    private static readonly Dictionary<SwiftTypeName, string> s_bufferTypeMap = new()
        {
            { SwiftTypeName.FromModuleQualifiedName("Swift.Array"), "IntPtr" },
            { SwiftTypeName.FromModuleQualifiedName("Swift.Set"), "IntPtr" },
            { SwiftTypeName.FromModuleQualifiedName("Swift.Optional"), "IntPtr" },
            { SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"), "IntPtr" }
        };

    /// <summary>
    /// Determines whether the specified property declaration represents a bound generic type.
    /// Optional closures (Optional&lt;Closure&gt;) are NOT considered bound generics - they should
    /// be handled by ClosureHandler instead.
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns><c>true</c> if the property's Swift type contains generic parameters; otherwise, <c>false</c>.</returns>
    public bool IsBoundGeneric(PropertyDecl propertyDecl) =>
        propertyDecl.SwiftTypeSpec is NamedTypeSpec namedTypeSpec &&
        namedTypeSpec.ContainsGenericParameters &&
        !_closureHandler.IsOptionalClosure(propertyDecl.SwiftTypeSpec); // TODO: Should also check that return type is not the type's own generic parameter (e.g., T in class Foo<T>)

    /// <summary>
    /// Determines whether the specified argument declaration represents a bound generic type.
    /// Optional closures (Optional&lt;Closure&gt;) are NOT considered bound generics - they should
    /// be handled by ClosureHandler instead.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns><c>true</c> if the argument's Swift type contains generic parameters; otherwise, <c>false</c>.</returns>
    public bool IsBoundGeneric(ArgumentDecl argumentDecl) =>
        !argumentDecl.IsGeneric &&
        argumentDecl.SwiftTypeSpec is NamedTypeSpec namedTypeSpec &&
        namedTypeSpec.ContainsGenericParameters &&
        !_closureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec);

    /// <summary>
    /// Determines whether the bound generic type requires special marshalling.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration to check.</param>
    /// <returns><c>true</c> if the type requires bound generic marshalling; otherwise, <c>false</c>.</returns>
    public bool RequiresBoundGenericMarshalling(ArgumentDecl argumentDecl)
    {
        if (!IsBoundGeneric(argumentDecl))
            return false;

        var namedTypeSpec = (NamedTypeSpec)argumentDecl.SwiftTypeSpec;
        var swiftTypeName = SwiftTypeName.FromTypeSpec(namedTypeSpec);
        return !s_structGenerics.Contains(swiftTypeName);
    }

    /// <summary>
    /// Translates the Swift generic type of the given property declaration into a C# type name.
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns>The C# type name with generic parameters.</returns>
    /// <exception cref="NotSupportedException">Thrown when the property is not bound generic.</exception>
    public string TranslateBoundGenericTypeToCSharp(PropertyDecl propertyDecl)
    {
        return TranslateBoundGenericTypeToCSharp(propertyDecl, GenericContext.Empty);
    }

    /// <summary>
    /// Translates the Swift generic type of the given property declaration into a C# type name,
    /// using a generic context to resolve type parameters.
    /// </summary>
    public string TranslateBoundGenericTypeToCSharp(PropertyDecl propertyDecl, GenericContext genericContext)
    {
        if (!IsBoundGeneric(propertyDecl))
            throw new NotSupportedException(
                $"Attempted to translate to C# name for a non-bound generic property {propertyDecl.Name}");
        var namedTypeSpec = (NamedTypeSpec)propertyDecl.SwiftTypeSpec;
        return TranslateBoundGenericTypeToCSharp(namedTypeSpec, genericContext);
    }

    /// <summary>
    /// Translates the Swift generic type of the given argument declaration into a C# type name.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns>The C# type name with generic parameters.</returns>
    /// <exception cref="NotSupportedException">Thrown when the argument is not bound generic.</exception>
    public string TranslateBoundGenericTypeToCSharp(ArgumentDecl argumentDecl)
    {
        return TranslateBoundGenericTypeToCSharp(argumentDecl, GenericContext.Empty);
    }

    /// <summary>
    /// Translates the Swift generic type of the given argument declaration into a C# type name,
    /// using a generic context to resolve type parameters.
    /// </summary>
    public string TranslateBoundGenericTypeToCSharp(ArgumentDecl argumentDecl, GenericContext genericContext)
    {
        if (!IsBoundGeneric(argumentDecl))
            throw new NotSupportedException(
                $"Attempted to translate to C# name for a non-bound generic argument {argumentDecl.Name}");
        var namedTypeSpec = (NamedTypeSpec)argumentDecl.SwiftTypeSpec;
        return TranslateBoundGenericTypeToCSharp(namedTypeSpec, genericContext);
    }

    /// <summary>
    /// Tries to find the first existential type argument within a bound generic type.
    /// </summary>
    /// <param name="typeSpec">The type specification to inspect.</param>
    /// <param name="existentialType">The first existential type encountered.</param>
    /// <returns><c>true</c> if an existential type argument was found; otherwise, <c>false</c>.</returns>
    public bool TryGetFirstExistentialTypeArgument(TypeSpec typeSpec, out string existentialType)
    {
        if (_existentialHandler.IsExistential(typeSpec))
        {
            existentialType = typeSpec.ToString();
            return true;
        }

        switch (typeSpec)
        {
            case NamedTypeSpec namedTypeSpec:
                foreach (var genericParameter in namedTypeSpec.GenericParameters)
                {
                    if (TryGetFirstExistentialTypeArgument(genericParameter, out existentialType))
                    {
                        return true;
                    }
                }
                break;
            case TupleTypeSpec tupleTypeSpec:
                foreach (var element in tupleTypeSpec.Elements)
                {
                    if (TryGetFirstExistentialTypeArgument(element, out existentialType))
                    {
                        return true;
                    }
                }
                break;
            case ClosureTypeSpec closureTypeSpec:
                if (TryGetFirstExistentialTypeArgument(closureTypeSpec.Arguments, out existentialType))
                {
                    return true;
                }

                if (TryGetFirstExistentialTypeArgument(closureTypeSpec.ReturnType, out existentialType))
                {
                    return true;
                }
                break;
        }

        existentialType = string.Empty;
        return false;
    }

    /// <summary>
    /// Tries to find the first bound generic argument that cannot satisfy emitted C# constraints.
    /// </summary>
    /// <param name="typeSpec">The type specification to inspect.</param>
    /// <param name="contextDecl">Declaration context used to resolve local type declarations.</param>
    /// <param name="details">Diagnostic details for the unsatisfied constraint.</param>
    /// <returns><c>true</c> if an unsatisfied constraint is found; otherwise, <c>false</c>.</returns>
    public bool TryGetFirstUnsatisfiedConstraint(TypeSpec typeSpec, BaseDecl? contextDecl, out string details)
    {
        details = string.Empty;

        var moduleDecl = contextDecl?.ModuleDecl;
        if (moduleDecl == null)
            return false;

        return TryGetFirstUnsatisfiedConstraint(typeSpec, moduleDecl, out details);
    }

    /// <summary>
    /// Helper method to convert a Swift <see cref="NamedTypeSpec"/> into its corresponding C# type name.
    /// </summary>
    /// <param name="namedTypeSpec">The named type specification.</param>
    /// <returns>The C# type name string.</returns>
    private string TranslateBoundGenericTypeToCSharp(NamedTypeSpec namedTypeSpec) =>
        TranslateBoundGenericTypeToCSharp(namedTypeSpec, GenericContext.Empty);

    /// <summary>
    /// Helper method to convert a Swift <see cref="NamedTypeSpec"/> into its corresponding C# type name,
    /// using a generic context to resolve type parameters within generic arguments.
    /// </summary>
    private string TranslateBoundGenericTypeToCSharp(NamedTypeSpec namedTypeSpec, GenericContext genericContext)
    {
        // Check if this named type is itself a generic type parameter (e.g., τ_0_0)
        if (TypeSpecHelpers.IsGenericTypeParameter(namedTypeSpec.Name) &&
            genericContext.TryResolve(namedTypeSpec.Name, out var resolvedName))
        {
            return resolvedName;
        }

        var typeReference = _typeDatabase.GetTypeRecordOrAnyType(namedTypeSpec); // TODO: consider throwing an exception instead

        // If the type falls back to AnyType, don't append generic parameters
        // since AnyType is not a generic type and adding <T1, T2> would be invalid C#
        if (typeReference == TypeDatabaseExtensions.AnyType)
        {
            return typeReference.CSharpTypeName.FullyQualifiedName;
        }

        List<string> translatedGenericParameters = new();
        foreach (var genericParameter in namedTypeSpec.GenericParameters)
        {
            translatedGenericParameters.Add(TranslateTypeSpecToCSharp(genericParameter, genericContext));
        }

        return typeReference.CSharpTypeName.FullyQualifiedName +
               (translatedGenericParameters.Count > 0
                    ? $"<{string.Join(", ", translatedGenericParameters)}>"
                    : "");
    }

    /// <summary>
    /// Translates any TypeSpec to its C# equivalent.
    /// Handles NamedTypeSpec, ClosureTypeSpec, TupleTypeSpec, and ProtocolListTypeSpec (existentials).
    /// </summary>
    /// <param name="typeSpec">The type specification to translate.</param>
    /// <returns>The C# type name string.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when the type specification is not supported.
    /// </exception>
    private string TranslateTypeSpecToCSharp(TypeSpec typeSpec) =>
        TranslateTypeSpecToCSharp(typeSpec, GenericContext.Empty);

    /// <summary>
    /// Translates any TypeSpec to its C# equivalent, using a generic context to resolve type parameters.
    /// </summary>
    private string TranslateTypeSpecToCSharp(TypeSpec typeSpec, GenericContext genericContext)
    {
        // Handle existential types (including bare 'Any' with 0 protocols and 'any Protocol' syntax)
        if (_existentialHandler.IsExistential(typeSpec))
        {
            // Bound generic arguments constrained to ISwiftObject cannot safely use existential containers.
            // Emit AnyType so callers can skip this member instead of generating invalid constraints.
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        // Check if the type is a generic type parameter before other dispatch
        if (typeSpec is NamedTypeSpec namedSpec &&
            TypeSpecHelpers.IsGenericTypeParameter(namedSpec.Name) &&
            genericContext.TryResolve(namedSpec.Name, out var csName))
        {
            return csName;
        }

        return typeSpec switch
        {
            NamedTypeSpec namedTypeSpec => TranslateBoundGenericTypeToCSharp(namedTypeSpec, genericContext),
            ClosureTypeSpec closureTypeSpec => TranslateClosureTypeToCSharp(closureTypeSpec),
            TupleTypeSpec tupleTypeSpec => _tupleHandler.GetCSharpTupleType(tupleTypeSpec,
                ts => TranslateTypeSpecToCSharp(ts, genericContext)),
            _ => throw new NotSupportedException(
                $"Type spec {typeSpec.GetType().Name} ({typeSpec}) is not supported as a generic parameter")
        };
    }

    /// <summary>
    /// Translates a closure type spec to its C# delegate type.
    /// Falls back to object for unsupported closures.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The C# delegate type name.</returns>
    private string TranslateClosureTypeToCSharp(ClosureTypeSpec closureTypeSpec)
    {
        // Check if the closure is supported
        if (!_closureHandler.IsSupportedClosure(closureTypeSpec))
        {
            // For unsupported closures (async, throwing, etc.), fall back to object
            // This allows the binding to compile, though the closure won't be directly usable
            return "object";
        }

        return _closureHandler.GetCSharpDelegateType(closureTypeSpec);
    }

    /// <summary>
    /// Gets the buffer type name used for marshalling the specified bound generic argument.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns>The buffer type name.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown if the argument is not a bound generic type.
    /// </exception>
    public string GetBufferType(ArgumentDecl argumentDecl)
    {
        if (!IsBoundGeneric(argumentDecl))
            throw new NotSupportedException(
                $"Attempted to get buffer type for a non-bound generic argument {argumentDecl.Name}");
        var namedTypeSpec = (NamedTypeSpec)argumentDecl.SwiftTypeSpec;

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedTypeSpec.Name);
        if (s_bufferTypeMap.TryGetValue(swiftTypeName, out var bufferType))
            return bufferType;

        // Fallback when no mapping is available.
        return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName; // TODO: Consider throwing an exception instead
    }

    private bool TryGetFirstUnsatisfiedConstraint(TypeSpec typeSpec, ModuleDecl moduleDecl, out string details)
    {
        details = string.Empty;

        switch (typeSpec)
        {
            case NamedTypeSpec namedTypeSpec:
                if (namedTypeSpec.ContainsGenericParameters &&
                    TryValidateGenericTypeConstraints(namedTypeSpec, moduleDecl, out details))
                {
                    return true;
                }

                foreach (var genericParameter in namedTypeSpec.GenericParameters)
                {
                    if (TryGetFirstUnsatisfiedConstraint(genericParameter, moduleDecl, out details))
                        return true;
                }
                return false;

            case TupleTypeSpec tupleTypeSpec:
                foreach (var element in tupleTypeSpec.Elements)
                {
                    if (TryGetFirstUnsatisfiedConstraint(element, moduleDecl, out details))
                        return true;
                }
                return false;

            case ClosureTypeSpec closureTypeSpec:
                if (TryGetFirstUnsatisfiedConstraint(closureTypeSpec.Arguments, moduleDecl, out details))
                    return true;
                return TryGetFirstUnsatisfiedConstraint(closureTypeSpec.ReturnType, moduleDecl, out details);

            default:
                return false;
        }
    }

    private bool TryValidateGenericTypeConstraints(NamedTypeSpec boundGenericType, ModuleDecl moduleDecl, out string details)
    {
        details = string.Empty;

        if (!boundGenericType.HasModule())
            return false;

        var genericTypeName = SwiftTypeName.FromTypeSpec(boundGenericType);
        var genericTypeDecl = FindTypeDecl(moduleDecl, genericTypeName);
        if (genericTypeDecl == null || !genericTypeDecl.IsGeneric)
            return false;

        var count = Math.Min(genericTypeDecl.GenericParameters.Count, boundGenericType.GenericParameters.Count);
        for (var i = 0; i < count; i++)
        {
            var parameterConstraint = genericTypeDecl.GenericParameters[i];
            var typeArgument = boundGenericType.GenericParameters[i];

            foreach (var conformance in parameterConstraint.GenericConformances)
            {
                if (conformance.Kind != ConformanceKind.Protocol)
                    continue;

                if (ShouldSkipConstraint(conformance.ConformanceTarget))
                    continue;

                if (SatisfiesConstraint(typeArgument, conformance.ConformanceTarget, moduleDecl))
                    continue;

                details = $"Type argument '{typeArgument}' does not satisfy constraint '{conformance.ConformanceTarget.ModuleQualifiedName}' on '{boundGenericType.NameWithoutModule}'.";
                return true;
            }
        }

        return false;
    }

    private bool ShouldSkipConstraint(SwiftTypeName protocolType)
    {
        if (protocolType.Name == "Sendable")
            return true;

        if (s_unsupportedConstraintModules.Contains(protocolType.Module))
            return true;

        if (_typeDatabase.TryGetTypeRecord(protocolType, out var protocolRecord) &&
            protocolRecord.Kind == TypeRecordKind.Protocol &&
            protocolRecord.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes))
        {
            return true;
        }

        return false;
    }

    private bool SatisfiesConstraint(TypeSpec typeArgument, SwiftTypeName protocolConstraint, ModuleDecl moduleDecl)
    {
        if (TypeSpecHelpers.IsGenericTypeParameter(typeArgument))
            return true;

        if (typeArgument is not NamedTypeSpec namedTypeArgument || !namedTypeArgument.HasModule())
            return false;

        var typeArgumentName = SwiftTypeName.FromTypeSpec(namedTypeArgument);
        var typeArgumentDecl = FindTypeDecl(moduleDecl, typeArgumentName);

        if (typeArgumentDecl == null)
        {
            // If the argument type is an external protocol type matching the constraint,
            // treat it as satisfying the self-conformance case.
            if (typeArgumentName == protocolConstraint)
                return true;

            // For external concrete types (e.g. Swift stdlib types), we can't verify
            // conformance from local declarations, so fail closed and skip the member.
            return false;
        }

        if (typeArgumentDecl is ProtocolDecl && typeArgumentName == protocolConstraint)
            return true;

        // Only Equatable is currently emitted as a concrete conformance.
        if (protocolConstraint.Name == "Equatable")
            return HasConformance(typeArgumentDecl, protocolConstraint);

        // General protocol conformance emission is handled in a later task.
        return false;
    }

    private static bool HasConformance(TypeDecl typeDecl, SwiftTypeName protocolType) =>
        typeDecl switch
        {
            StructDecl structDecl => structDecl.Conformances.Any(c => c.Protocol == protocolType),
            ClassDecl classDecl => classDecl.Conformances.Any(c => c.Protocol == protocolType),
            EnumDecl enumDecl => enumDecl.Conformances.Any(c => c.Protocol == protocolType),
            _ => false
        };

    private static TypeDecl? FindTypeDecl(ModuleDecl moduleDecl, SwiftTypeName swiftTypeName)
    {
        foreach (var type in moduleDecl.Types)
        {
            var found = FindTypeDeclRecursive(type, swiftTypeName);
            if (found != null)
                return found;
        }

        foreach (var protocol in moduleDecl.Protocols)
        {
            var found = FindTypeDeclRecursive(protocol, swiftTypeName);
            if (found != null)
                return found;
        }

        return null;
    }

    private static TypeDecl? FindTypeDeclRecursive(TypeDecl typeDecl, SwiftTypeName swiftTypeName)
    {
        if (typeDecl.SwiftTypeName == swiftTypeName)
            return typeDecl;

        foreach (var nestedType in typeDecl.Types)
        {
            var found = FindTypeDeclRecursive(nestedType, swiftTypeName);
            if (found != null)
                return found;
        }

        return null;
    }
}
