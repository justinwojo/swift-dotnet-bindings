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
    private static readonly HashSet<string> s_stdlibGenerics = new(StringComparer.Ordinal)
    {
        "Swift.Dictionary", "Swift.Array", "Swift.Set", "Swift.Optional", "Swift.Result",
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
    /// Pointer types (UnsafePointer&lt;T&gt;, etc.) are NOT bound generics — they map to IntPtr.
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns><c>true</c> if the property's Swift type contains generic parameters; otherwise, <c>false</c>.</returns>
    public bool IsBoundGeneric(PropertyDecl propertyDecl) =>
        propertyDecl.SwiftTypeSpec is NamedTypeSpec namedTypeSpec &&
        namedTypeSpec.ContainsGenericParameters &&
        !_closureHandler.IsOptionalClosure(propertyDecl.SwiftTypeSpec) &&
        !IsPointerType(namedTypeSpec); // TODO: Should also check that return type is not the type's own generic parameter (e.g., T in class Foo<T>)

    /// <summary>
    /// Determines whether the specified argument declaration represents a bound generic type.
    /// Optional closures (Optional&lt;Closure&gt;) are NOT considered bound generics - they should
    /// be handled by ClosureHandler instead.
    /// Pointer types (UnsafePointer&lt;T&gt;, etc.) are NOT bound generics — they map to IntPtr.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns><c>true</c> if the argument's Swift type contains generic parameters; otherwise, <c>false</c>.</returns>
    public bool IsBoundGeneric(ArgumentDecl argumentDecl) =>
        !argumentDecl.IsGeneric &&
        argumentDecl.SwiftTypeSpec is NamedTypeSpec namedTypeSpec &&
        namedTypeSpec.ContainsGenericParameters &&
        !_closureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec) &&
        !IsPointerType(namedTypeSpec);

    /// <summary>
    /// Checks whether a NamedTypeSpec is a Swift pointer type (UnsafePointer, UnsafeMutablePointer, etc.).
    /// Pointer types map to IntPtr and must NOT be treated as bound generics.
    /// </summary>
    private static bool IsPointerType(NamedTypeSpec typeSpec) =>
        typeSpec.Name is "Swift.OpaquePointer" or "Swift.UnsafePointer"
            or "Swift.UnsafeMutablePointer" or "Swift.UnsafeRawPointer"
            or "Swift.UnsafeMutableRawPointer" or "Builtin.RawPointer";

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
        return TranslateBoundGenericTypeToCSharp(namedTypeSpec, genericContext, propertyDecl.ModuleDecl);
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
        return TranslateBoundGenericTypeToCSharp(namedTypeSpec, genericContext, argumentDecl.ModuleDecl);
    }

    /// <summary>
    /// Translates a bound generic TypeSpec into a C# type name.
    /// Used by protocol/proxy emission paths that have a TypeSpec but no parent declaration.
    /// </summary>
    public string TranslateBoundGenericTypeToCSharp(TypeSpec typeSpec, GenericContext genericContext)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec || !namedTypeSpec.ContainsGenericParameters)
            throw new NotSupportedException(
                $"Attempted to translate non-bound-generic TypeSpec: {typeSpec}");
        return TranslateBoundGenericTypeToCSharp(namedTypeSpec, genericContext, moduleDecl: null);
    }

    /// <summary>
    /// Returns true when the type is a supported container with existential elements that can
    /// be marshalled. Handles direct containers (Array, Dictionary) and Optional-wrapped containers
    /// (Optional&lt;Array&lt;any P&gt;&gt;, Optional&lt;Dictionary&lt;K, any P&gt;&gt;). Also handles
    /// direct Optional&lt;any P&gt; as a supported pattern. All existential elements are validated
    /// for TypeRecord availability, non-object public type, and ObjC filter parity.
    /// </summary>
    public bool IsContainerWithSupportedDirectExistential(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec outerNamedType || !outerNamedType.ContainsGenericParameters)
            return false;

        // Optional wrapping: unwrap one layer and check if inner is a supported container
        if (MarshallingHelpers.IsSwiftOptional(outerNamedType) &&
            outerNamedType.GenericParameters.Count > 0)
        {
            var inner = outerNamedType.GenericParameters[0];
            // Optional<any P> — direct existential in optional
            if (_existentialHandler.IsExistential(inner))
                return IsValidExistentialForContainer(inner);
            // Optional<Array<any P>> or Optional<Dictionary<K, any P>>
            return IsContainerWithSupportedDirectExistential(inner);
        }

        // Array<any P> — element must be directly existential
        if (MarshallingHelpers.IsSwiftArray(outerNamedType) &&
            outerNamedType.GenericParameters.Count > 0 &&
            _existentialHandler.IsExistential(outerNamedType.GenericParameters[0]))
        {
            return IsValidExistentialForContainer(outerNamedType.GenericParameters[0]);
        }

        // Dictionary<K, any P> — only VALUE position (GenericParameters[1]) may be existential.
        // Key position (GenericParameters[0]) is not allowed (Swift requires Hashable,
        // ExistentialContainer is not Hashable). Reject if key is also existential.
        if (MarshallingHelpers.IsSwiftDictionary(outerNamedType) &&
            outerNamedType.GenericParameters.Count > 1 &&
            _existentialHandler.IsExistential(outerNamedType.GenericParameters[1]) &&
            !_existentialHandler.IsExistential(outerNamedType.GenericParameters[0]))
        {
            return IsValidExistentialForContainer(outerNamedType.GenericParameters[1]);
        }

        return false;
    }

    /// <summary>
    /// Validates that an existential TypeSpec is supported for use inside a container:
    /// resolvable protocols, supported count (≤8), non-object public type, and ObjC filter parity.
    /// </summary>
    private bool IsValidExistentialForContainer(TypeSpec existentialTypeSpec)
    {
        var protocolList = _existentialHandler.ToProtocolListTypeSpec(existentialTypeSpec);
        if (protocolList == null || !_existentialHandler.IsSupportedExistential(protocolList))
            return false;
        if (!_existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
            return false;
        if (_existentialHandler.GetPublicExistentialType(protocolList) == "object")
            return false;
        // P1 fix: Mixed compositions where ObjC filtering drops protocols
        // would produce proxy/container size mismatch at runtime.
        var filteredCount = protocolList.Protocols.Keys
            .Count(p => !TypeDatabaseExtensions.IsObjCModuleType(p));
        if (filteredCount != protocolList.Protocols.Count)
            return false;
        return true;
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
    /// Tries to find the first existential type argument within a bound generic type
    /// that is NOT supported (i.e., has more than 8 protocols).
    /// Supported existentials (0-8 protocols) are skipped so they can be emitted as ExistentialContainer types.
    /// </summary>
    /// <param name="typeSpec">The type specification to inspect.</param>
    /// <param name="existentialType">The first unsupported existential type encountered.</param>
    /// <returns><c>true</c> if an unsupported existential type argument was found; otherwise, <c>false</c>.</returns>
    public bool TryGetFirstUnsupportedExistentialTypeArgument(TypeSpec typeSpec, out string existentialType)
    {
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList == null || !_existentialHandler.IsSupportedExistential(protocolList))
            {
                existentialType = typeSpec.ToString();
                return true;
            }
            existentialType = string.Empty;
            return false;
        }

        switch (typeSpec)
        {
            case NamedTypeSpec namedTypeSpec:
                foreach (var genericParameter in namedTypeSpec.GenericParameters)
                {
                    if (TryGetFirstUnsupportedExistentialTypeArgument(genericParameter, out existentialType))
                    {
                        return true;
                    }
                }
                break;
            case TupleTypeSpec tupleTypeSpec:
                foreach (var element in tupleTypeSpec.Elements)
                {
                    if (TryGetFirstUnsupportedExistentialTypeArgument(element, out existentialType))
                    {
                        return true;
                    }
                }
                break;
            case ClosureTypeSpec closureTypeSpec:
                if (TryGetFirstUnsupportedExistentialTypeArgument(closureTypeSpec.Arguments, out existentialType))
                {
                    return true;
                }

                if (TryGetFirstUnsupportedExistentialTypeArgument(closureTypeSpec.ReturnType, out existentialType))
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

        // Collect the parent type's generic parameters — these represent the constraints
        // actually emitted in C#. Methods from conditional extensions have additional
        // constraints in their genericSig that are NOT on the parent type, so a generic
        // type parameter (e.g., T0) only satisfies a bound generic constraint if the
        // parent type declares that constraint.
        var parentTypeGenericParams = contextDecl?.ParentDecl is TypeDecl parentType
            ? parentType.GenericParameters
            : null;

        return TryGetFirstUnsatisfiedConstraint(typeSpec, moduleDecl, parentTypeGenericParams, out details);
    }

    /// <summary>
    /// Returns true when a named type is a generic declaration used without type arguments.
    /// Uses module-local declaration lookup first, then stdlib fallback names.
    /// </summary>
    public bool IsBareGenericUsage(TypeSpec typeSpec, ModuleDecl? moduleDecl)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return false;

        if (namedTypeSpec.ContainsGenericParameters)
            return false;

        if (TypeSpecHelpers.IsGenericTypeParameter(namedTypeSpec.Name))
            return false;

        if (!namedTypeSpec.HasModule())
            return false;

        if (moduleDecl == null)
        {
            if (_typeDatabase.TryGetTypeRecord(namedTypeSpec, out var typeRecord) &&
                TypeDatabaseExtensions.IsBareGenericTypeName(typeRecord.CSharpTypeName.FullyQualifiedName))
            {
                return true;
            }

            return IsBareStdlibGeneric(namedTypeSpec);
        }

        var typeName = SwiftTypeName.FromTypeSpec(namedTypeSpec);
        var typeDecl = FindTypeDecl(moduleDecl, typeName);
        if (typeDecl != null)
            return typeDecl.IsGeneric;

        if (_typeDatabase.TryGetTypeRecord(namedTypeSpec, out var record) &&
            TypeDatabaseExtensions.IsBareGenericTypeName(record.CSharpTypeName.FullyQualifiedName))
        {
            return true;
        }

        return IsBareStdlibGeneric(namedTypeSpec);
    }

    /// <summary>
    /// Returns true when any nested type within the specification is a generic declaration
    /// used without type arguments.
    /// </summary>
    public bool HasBareGenericUsage(TypeSpec typeSpec, ModuleDecl? moduleDecl)
    {
        if (typeSpec is NamedTypeSpec namedTypeSpec)
        {
            if (IsBareGenericUsage(namedTypeSpec, moduleDecl))
                return true;

            foreach (var genericParameter in namedTypeSpec.GenericParameters)
            {
                if (HasBareGenericUsage(genericParameter, moduleDecl))
                    return true;
            }

            return false;
        }

        if (typeSpec is TupleTypeSpec tupleTypeSpec)
        {
            foreach (var element in tupleTypeSpec.Elements)
            {
                if (HasBareGenericUsage(element, moduleDecl))
                    return true;
            }

            return false;
        }

        if (typeSpec is ClosureTypeSpec closureTypeSpec)
        {
            return HasBareGenericUsage(closureTypeSpec.Arguments, moduleDecl) ||
                   HasBareGenericUsage(closureTypeSpec.ReturnType, moduleDecl);
        }

        return false;
    }

    /// <summary>
    /// Returns true when any concrete bound generic argument cannot satisfy C#'s implicit
    /// ISwiftObject constraint. Checks ObjC-bridged types (always blocked) and tuples
    /// (blocked except in Swift.Optional, which has no ISwiftObject constraint).
    /// Closures are NOT checked — they fall back to object via AnyType/ContainsPlaceholder,
    /// so the entire type becomes [UnsupportedSwiftType] object (compiles fine).
    /// </summary>
    public bool HasNonSwiftObjectGenericArg(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec || !namedTypeSpec.ContainsGenericParameters)
            return false;

        // Swift.Optional (SwiftOptional<T>) has no ISwiftObject constraint on T,
        // so tuples are valid generic args. All other emitted generics have
        // 'where T : ISwiftObject', making ValueTuple args a CS0311 error.
        bool outerIsOptional = namedTypeSpec.Name == "Swift.Optional";

        foreach (var genericParam in namedTypeSpec.GenericParameters)
        {
            // Swift.Void (named) maps to SwiftVoid, which doesn't implement ISwiftObject
            if (!outerIsOptional && genericParam is NamedTypeSpec { Name: "Swift.Void" })
                return true;

            // All tuples (including empty tuple = Void) don't implement ISwiftObject
            if (!outerIsOptional && genericParam is TupleTypeSpec)
                return true;

            // B5: Optional tuple with existential element — check tuple elements for unresolvable existentials
            if (outerIsOptional && genericParam is TupleTypeSpec optTuple && !optTuple.IsEmptyTuple)
            {
                foreach (var element in optTuple.Elements)
                {
                    if (_existentialHandler.IsExistential(element))
                    {
                        var protocolList = _existentialHandler.ToProtocolListTypeSpec(element);
                        if (protocolList == null || !_existentialHandler.IsSupportedExistential(protocolList))
                            return true;
                        var publicType = _existentialHandler.GetPublicExistentialType(protocolList);
                        if (publicType == "object")
                            return true;
                    }
                }
            }

            if (genericParam is NamedTypeSpec namedArg && (IsObjCBridgedType(namedArg) || IsNonSwiftObjectMappedType(namedArg)))
                return true;

            if (HasNonSwiftObjectGenericArg(genericParam))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Helper method to convert a Swift <see cref="NamedTypeSpec"/> into its corresponding C# type name.
    /// </summary>
    /// <param name="namedTypeSpec">The named type specification.</param>
    /// <returns>The C# type name string.</returns>
    private string TranslateBoundGenericTypeToCSharp(NamedTypeSpec namedTypeSpec) =>
        TranslateBoundGenericTypeToCSharp(namedTypeSpec, GenericContext.Empty, moduleDecl: null);

    /// <summary>
    /// Helper method to convert a Swift <see cref="NamedTypeSpec"/> into its corresponding C# type name,
    /// using a generic context to resolve type parameters within generic arguments.
    /// </summary>
    private string TranslateBoundGenericTypeToCSharp(NamedTypeSpec namedTypeSpec, GenericContext genericContext)
        => TranslateBoundGenericTypeToCSharp(namedTypeSpec, genericContext, moduleDecl: null);

    /// <summary>
    /// Helper method to convert a Swift <see cref="NamedTypeSpec"/> into its corresponding C# type name,
    /// using declaration context to qualify nested generic owners when needed.
    /// </summary>
    private string TranslateBoundGenericTypeToCSharp(
        NamedTypeSpec namedTypeSpec,
        GenericContext genericContext,
        ModuleDecl? moduleDecl)
    {
        if (namedTypeSpec.Name == "Swift.Void")
            return "Swift.SwiftVoid";

        // Check if this named type is itself a generic type parameter (e.g., τ_0_0)
        if (TypeSpecHelpers.IsGenericTypeParameter(namedTypeSpec.Name) &&
            genericContext.TryResolve(namedTypeSpec.Name, out var resolvedName))
        {
            return resolvedName;
        }

        var typeReference = _typeDatabase.GetTypeRecordOrAnyType(namedTypeSpec); // TODO: consider throwing an exception instead

        // If the type falls back to AnyType or is IntPtr (pointer types), don't append generic parameters
        // since these are not generic types in C# and adding <T1, T2> would be invalid C#
        // Pointer types like UnsafeMutablePointer<T> resolve to IntPtr which doesn't support generics
        if (typeReference == TypeDatabaseExtensions.AnyType ||
            typeReference == TypeDatabaseExtensions.IntPtrType)
        {
            return typeReference.CSharpTypeName.FullyQualifiedName;
        }

        List<string> translatedGenericParameters = new();
        foreach (var genericParameter in namedTypeSpec.GenericParameters)
        {
            translatedGenericParameters.Add(TranslateTypeSpecToCSharp(genericParameter, genericContext, moduleDecl));
        }

        var typeName = QualifyNestedGenericOwners(typeReference.CSharpTypeName.FullyQualifiedName, namedTypeSpec, genericContext, moduleDecl);
        return typeName +
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
        TranslateTypeSpecToCSharp(typeSpec, GenericContext.Empty, moduleDecl: null);

    /// <summary>
    /// Translates any TypeSpec to its C# equivalent, using a generic context to resolve type parameters.
    /// </summary>
    private string TranslateTypeSpecToCSharp(TypeSpec typeSpec, GenericContext genericContext, ModuleDecl? moduleDecl)
    {
        // Handle existential types (including bare 'Any' with 0 protocols and 'any Protocol' syntax).
        // For fully supported existentials (resolvable, known protocols, non-object), return
        // ExistentialContainer{N} — the correct ABI type for containers like
        // SwiftDictionary<SwiftString, ExistentialContainer1>.
        // For unsupported/unresolvable existentials, return AnyType (blocked by B6 gate anyway).
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList != null &&
                _existentialHandler.IsSupportedExistential(protocolList) &&
                _existentialHandler.AllProtocolsHaveTypeRecords(protocolList) &&
                _existentialHandler.GetPublicExistentialType(protocolList) != "object")
            {
                return _existentialHandler.GetCSharpExistentialType(protocolList);
            }
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
            NamedTypeSpec { Name: "Swift.Void" } => "Swift.SwiftVoid",
            NamedTypeSpec namedTypeSpec => TranslateBoundGenericTypeToCSharp(namedTypeSpec, genericContext, moduleDecl),
            ClosureTypeSpec closureTypeSpec => TranslateClosureTypeToCSharp(closureTypeSpec),
            TupleTypeSpec { IsEmptyTuple: true } => "Swift.SwiftVoid",
            TupleTypeSpec tupleTypeSpec => _tupleHandler.GetCSharpTupleType(tupleTypeSpec,
                ts => TranslateTypeSpecToCSharp(ts, genericContext, moduleDecl)),
            // Associated type references (e.g., Self.Element inside Array<Self.Element>) degrade to AnyType.
            // These appear in protocol signatures with associated types and can't be resolved without
            // concrete type binding. Callers (ProtocolHandler, ProtocolSignatureHelper, etc.) handle
            // AnyType appropriately.
            AssociatedTypeReferenceSpec => TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName,
            ProtocolListTypeSpec => TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName,
            _ => throw new NotSupportedException(
                $"Type spec {typeSpec.GetType().Name} ({typeSpec}) is not supported as a generic parameter")
        };
    }

    private string QualifyNestedGenericOwners(
        string fullyQualifiedTypeName,
        NamedTypeSpec namedTypeSpec,
        GenericContext genericContext,
        ModuleDecl? moduleDecl)
    {
        if (moduleDecl == null || !namedTypeSpec.HasModule() || genericContext.IsEmpty)
            return fullyQualifiedTypeName;

        var typeDecl = FindTypeDecl(moduleDecl, SwiftTypeName.FromTypeSpec(namedTypeSpec));
        if (typeDecl == null)
            return fullyQualifiedTypeName;

        var typeChain = GetTypeDeclChain(typeDecl);
        if (typeChain.Count <= 1)
            return fullyQualifiedTypeName;

        var segments = fullyQualifiedTypeName.Split('.');
        if (segments.Length < typeChain.Count)
            return fullyQualifiedTypeName;

        var firstTypeSegment = segments.Length - typeChain.Count;
        for (var i = 0; i < typeChain.Count - 1; i++)
        {
            var ownerType = typeChain[i];
            if (!ownerType.IsGeneric)
                continue;

            var ownerArgs = ResolveTypeDeclGenericArguments(ownerType, genericContext);
            if (ownerArgs.Count != ownerType.GenericParameters.Count || ownerArgs.Count == 0)
                continue;

            segments[firstTypeSegment + i] = $"{segments[firstTypeSegment + i]}<{string.Join(", ", ownerArgs)}>";
        }

        return string.Join(".", segments);
    }

    private static List<TypeDecl> GetTypeDeclChain(TypeDecl typeDecl)
    {
        var chain = new List<TypeDecl>();
        for (BaseDecl? current = typeDecl; current is TypeDecl currentType; current = currentType.ParentDecl)
        {
            chain.Add(currentType);
        }
        chain.Reverse();
        return chain;
    }

    private static List<string> ResolveTypeDeclGenericArguments(TypeDecl typeDecl, GenericContext genericContext)
    {
        var args = new List<string>();
        foreach (var genericParam in typeDecl.GenericParameters)
        {
            if (!genericContext.TryResolve(genericParam.TypeName, out var resolvedArg))
                return new List<string>();

            args.Add(resolvedArg);
        }

        return args;
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
        // Use IntPtr (safe opaque pointer) instead of AnyType (managed type → CS8500 warnings)
        return "IntPtr";
    }

    private bool TryGetFirstUnsatisfiedConstraint(TypeSpec typeSpec, ModuleDecl moduleDecl,
        IReadOnlyList<GenericArgumentDecl>? parentTypeGenericParams, out string details)
    {
        details = string.Empty;

        switch (typeSpec)
        {
            case NamedTypeSpec namedTypeSpec:
                if (namedTypeSpec.ContainsGenericParameters &&
                    TryValidateGenericTypeConstraints(namedTypeSpec, moduleDecl, parentTypeGenericParams, out details))
                {
                    return true;
                }

                foreach (var genericParameter in namedTypeSpec.GenericParameters)
                {
                    if (TryGetFirstUnsatisfiedConstraint(genericParameter, moduleDecl, parentTypeGenericParams, out details))
                        return true;
                }
                return false;

            case TupleTypeSpec tupleTypeSpec:
                foreach (var element in tupleTypeSpec.Elements)
                {
                    if (TryGetFirstUnsatisfiedConstraint(element, moduleDecl, parentTypeGenericParams, out details))
                        return true;
                }
                return false;

            case ClosureTypeSpec closureTypeSpec:
                if (TryGetFirstUnsatisfiedConstraint(closureTypeSpec.Arguments, moduleDecl, parentTypeGenericParams, out details))
                    return true;
                return TryGetFirstUnsatisfiedConstraint(closureTypeSpec.ReturnType, moduleDecl, parentTypeGenericParams, out details);

            default:
                return false;
        }
    }

    private bool TryValidateGenericTypeConstraints(NamedTypeSpec boundGenericType, ModuleDecl moduleDecl,
        IReadOnlyList<GenericArgumentDecl>? parentTypeGenericParams, out string details)
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

                if (SatisfiesConstraint(typeArgument, conformance.ConformanceTarget, moduleDecl, parentTypeGenericParams))
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

    private bool SatisfiesConstraint(TypeSpec typeArgument, SwiftTypeName protocolConstraint, ModuleDecl moduleDecl,
        IReadOnlyList<GenericArgumentDecl>? parentTypeGenericParams)
    {
        if (TypeSpecHelpers.IsGenericTypeParameter(typeArgument))
        {
            // A generic type parameter (e.g., τ_0_0 / T0) only satisfies a constraint
            // if the parent type's generic declaration includes that constraint.
            // Swift conditional extensions add constraints in the method's genericSig
            // (e.g., Table<T> where T: FetchableRecord), but these are NOT reflected
            // on the C# type declaration (Table<T0> where T0 : ISwiftObject).
            // Without the constraint on the parent type, C# rejects the usage (CS0314).
            return GenericTypeParamSatisfiesConstraint(typeArgument, protocolConstraint, parentTypeGenericParams, moduleDecl);
        }

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

    /// <summary>
    /// Checks whether a generic type parameter satisfies a protocol constraint based on
    /// the parent type's generic declarations. When no parent type generic parameters are
    /// available (e.g., free functions), the check is permissive and returns true.
    /// </summary>
    private static bool GenericTypeParamSatisfiesConstraint(
        TypeSpec typeArgument, SwiftTypeName protocolConstraint,
        IReadOnlyList<GenericArgumentDecl>? parentTypeGenericParams,
        ModuleDecl? moduleDecl = null)
    {
        // If no parent type generic parameters are available (e.g., free functions,
        // non-generic parent types), be permissive — the constraint can't be
        // validated against a parent type and may be satisfied at the call site.
        if (parentTypeGenericParams == null || parentTypeGenericParams.Count == 0)
            return true;

        var paramName = typeArgument is NamedTypeSpec namedArg ? namedArg.Name : typeArgument.ToString();

        // Find the matching generic parameter in the parent type's declarations
        var matchingParam = parentTypeGenericParams.FirstOrDefault(p => p.TypeName == paramName);
        if (matchingParam == null)
        {
            // The type parameter doesn't belong to the parent type (e.g., a method-level
            // type parameter). Be permissive — method-level constraints are emitted on the
            // method itself.
            return true;
        }

        // Check if the parent type's constraints on this parameter include the required protocol
        // (either directly or via protocol inheritance)
        foreach (var conformance in matchingParam.GenericConformances)
        {
            if (conformance.Kind != ConformanceKind.Protocol)
                continue;

            // Direct match
            if (conformance.ConformanceTarget == protocolConstraint)
                return true;

            // Inherited match: check if the conformance target protocol inherits
            // from the required protocol (e.g., T: ChildProtocol satisfies T: ParentProtocol)
            if (moduleDecl != null && ProtocolInheritsFrom(conformance.ConformanceTarget, protocolConstraint, moduleDecl))
                return true;
        }

        // The parent type does not constrain this parameter to conform to the required protocol.
        // This is a conditional extension constraint that cannot be expressed in C#.
        return false;
    }

    /// <summary>
    /// Checks whether a protocol transitively inherits from a target protocol.
    /// Uses the module's protocol declarations to resolve the inheritance chain.
    /// </summary>
    private static bool ProtocolInheritsFrom(SwiftTypeName childProtocol, SwiftTypeName targetProtocol, ModuleDecl moduleDecl)
    {
        var visited = new HashSet<string>();
        return ProtocolInheritsFromRecursive(childProtocol, targetProtocol, moduleDecl, visited);
    }

    private static bool ProtocolInheritsFromRecursive(SwiftTypeName current, SwiftTypeName target, ModuleDecl moduleDecl, HashSet<string> visited)
    {
        var key = current.ModuleQualifiedName;
        if (!visited.Add(key))
            return false;

        var protocolDecl = moduleDecl.Protocols
            .FirstOrDefault(p => p.SwiftTypeName.Module == current.Module && p.SwiftTypeName.Name == current.Name);
        if (protocolDecl == null)
            return false;

        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            var inheritedName = SwiftTypeName.FromTypeSpec(inherited);
            if (inheritedName == target)
                return true;
            if (ProtocolInheritsFromRecursive(inheritedName, target, moduleDecl, visited))
                return true;
        }

        return false;
    }

    private bool IsObjCBridgedType(NamedTypeSpec typeSpec)
    {
        if (!typeSpec.HasModule())
            return false;

        if (_typeDatabase.TryGetTypeRecord(typeSpec, out var record))
            return record.Flags.HasFlag(TypeRecordFlags.ObjCBridged);

        return TypeDatabaseExtensions.IsObjCModuleType(typeSpec);
    }

    /// <summary>
    /// Returns true when a type maps to a .NET type that doesn't implement ISwiftObject.
    /// Catches NativeTypeName mappings (e.g., Foundation.URL → NSUrl) and non-Swift module types
    /// mapped to System.* namespace (e.g., Foundation.Date → System.DateTimeOffset, Foundation.UUID → System.Guid).
    /// Swift module types like Swift.Bool → System.Boolean are excluded — they're primitives
    /// handled by special marshalling paths and never appear in bound generic constraints.
    /// </summary>
    private bool IsNonSwiftObjectMappedType(NamedTypeSpec typeSpec)
    {
        if (!typeSpec.HasModule())
            return false;

        // Foundation.Data implements ISwiftObject at runtime — don't block from generics
        if (typeSpec.Name == "Foundation.Data")
            return false;

        if (_typeDatabase.TryGetTypeRecord(typeSpec, out var record))
        {
            if (record.NativeTypeName != null)
                return true;

            // C3: Non-Swift module types mapped to System.* (e.g., Foundation.Date → DateTimeOffset)
            // don't implement ISwiftObject. Exclude Swift module types (Swift.Bool → System.Boolean,
            // Swift.Int → System.Int32) which are primitives handled by special marshalling paths.
            if (record.CSharpTypeName.Namespace == "System")
            {
                var module = typeSpec.Name.Split('.')[0];
                if (module != "Swift")
                    return true;
            }
        }

        return false;
    }

    // 8-byte Swift stdlib value types whose Optionals exceed IntPtr capacity.
    // String = 16 bytes → Optional is 16 bytes. Others = 8 bytes → Optional is 9 bytes (value + discriminator).
    // Int/UInt are 8 bytes because all supported targets are 64-bit ARM64.
    private static readonly HashSet<string> s_largeOptionalInnerTypes = new(StringComparer.Ordinal)
    {
        "Swift.String",
        "Swift.Int",
        "Swift.UInt",
        "Swift.Int64",
        "Swift.UInt64",
        "Swift.Double",
    };

    /// <summary>
    /// Returns true if typeSpec is Optional&lt;T&gt; where T's size is ≥ 8 bytes,
    /// making the Optional too large for IntPtr (8 bytes) to hold without truncation.
    /// </summary>
    public bool IsLargeOptionalParam(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType || namedType.Name != "Swift.Optional")
            return false;
        var innerElement = namedType.GenericParameters.FirstOrDefault();
        if (innerElement is not NamedTypeSpec innerNamed)
            return false;
        return s_largeOptionalInnerTypes.Contains(innerNamed.Name);
    }

    /// <summary>
    /// Returns true if any non-return parameter is a large Optional.
    /// </summary>
    public bool HasLargeOptionalParams(MethodDecl methodDecl)
    {
        return methodDecl.CSSignature.Skip(1)
            .Any(p => IsLargeOptionalParam(p.SwiftTypeSpec));
    }

    /// <summary>
    /// Returns true if the method's return type is a large Optional (Optional&lt;T&gt; where T ≥ 8B).
    /// Async methods and constructors are excluded — async returns go through heap-allocated callbacks,
    /// and constructors return Self (not Optional) in normal path.
    /// </summary>
    public bool IsLargeOptionalReturn(MethodDecl methodDecl)
    {
        if (methodDecl.IsAsync || methodDecl.IsConstructor)
            return false;
        var returnType = methodDecl.CSSignature.FirstOrDefault();
        return returnType != null && IsLargeOptionalParam(returnType.SwiftTypeSpec);
    }

    private static bool IsBareStdlibGeneric(NamedTypeSpec typeSpec) => s_stdlibGenerics.Contains(typeSpec.Name);

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
