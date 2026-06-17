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
    private static readonly HashSet<string> s_stdlibGenerics = new(StringComparer.Ordinal)
    {
        "Swift.Dictionary", "Swift.Array", "Swift.Set", "Swift.Optional", "Swift.Result", "Swift.ClosedRange",
    };

    private readonly ITypeDatabase _typeDatabase;
    private readonly ClosureHandler _closureHandler;
    private readonly TupleHandler _tupleHandler;
    private readonly ExistentialHandler _existentialHandler;
    private readonly ConformanceGraph? _conformanceGraph;
    private readonly ConformanceOracle _conformanceOracle;

    public BoundGenericsHandler(ITypeDatabase typeDatabase, ConformanceGraph? conformanceGraph = null)
    {
        _typeDatabase = typeDatabase;
        _closureHandler = new ClosureHandler(typeDatabase);
        _tupleHandler = new TupleHandler(typeDatabase);
        _existentialHandler = new ExistentialHandler(typeDatabase);
        _conformanceGraph = conformanceGraph;
        _conformanceOracle = new ConformanceOracle(typeDatabase);
    }

    // Almost all generics will be projected into C# as classes.
    // This collection contains types that must be marshalled as structs.
    // We might introduce a new field in the TypeRecord to indicate this
    private static readonly HashSet<SwiftTypeName> s_structGenerics = new()
        {
            SwiftTypeName.FromModuleQualifiedName("Swift.UnsafeMutableBufferPointer"),
            SwiftTypeName.FromModuleQualifiedName("Swift.UnsafeMutablePointer"),
        };

    // Mapping of Swift generic types to their corresponding buffer types.
    private static readonly Dictionary<SwiftTypeName, string> s_bufferTypeMap = new()
        {
            { SwiftTypeName.FromModuleQualifiedName("Swift.Array"), "IntPtr" },
            { SwiftTypeName.FromModuleQualifiedName("Swift.Set"), "IntPtr" },
            { SwiftTypeName.FromModuleQualifiedName("Swift.Optional"), "IntPtr" },
            { SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"), "IntPtr" },
            { SwiftTypeName.FromModuleQualifiedName("Swift.ClosedRange"), "IntPtr" }
        };

    /// <summary>
    /// Core bound-generic check on a raw <see cref="TypeSpec"/>.
    /// A type is considered bound generic when it is a <see cref="NamedTypeSpec"/> that contains
    /// generic parameters, is NOT an optional closure, and is NOT a pointer type.
    ///
    /// Direct existentials (<c>any P</c>, <c>any P&lt;X&gt;</c>) are intentionally
    /// excluded: even when the protocol carries generic arguments, the runtime ABI is an
    /// existential container — boxed via <see cref="ExistentialHandler"/> with proxy
    /// dispatch — not a parametric struct. Treating <c>any P&lt;X&gt;</c> as a bound
    /// generic would route the parameter through the concrete-type marshaller and emit
    /// <c>arg.Payload</c> / <c>SafeHandlePin</c> against an interface reference (CS1061).
    /// Constrained existential Cases 1 and 2: concrete-arg `any P&lt;X&gt;` and plain `any P`.
    /// </summary>
    private bool IsBoundGenericTypeSpec(TypeSpec? typeSpec) =>
        typeSpec is NamedTypeSpec namedTypeSpec &&
        namedTypeSpec.ContainsGenericParameters &&
        !_closureHandler.IsOptionalClosure(typeSpec) &&
        !IsPointerType(namedTypeSpec) &&
        !_existentialHandler.IsExistential(typeSpec);

    /// <summary>
    /// Determines whether the specified property declaration represents a bound generic type.
    /// </summary>
    public bool IsBoundGeneric(PropertyDecl propertyDecl) =>
        IsBoundGenericTypeSpec(propertyDecl.SwiftTypeSpec);

    /// <summary>
    /// Determines whether the specified argument declaration represents a bound generic type.
    /// Additionally rejects method-level generic parameters (<c>argumentDecl.IsGeneric</c>).
    /// </summary>
    public bool IsBoundGeneric(ArgumentDecl argumentDecl) =>
        !argumentDecl.IsGeneric &&
        IsBoundGenericTypeSpec(argumentDecl.SwiftTypeSpec);

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
        if (s_structGenerics.Contains(swiftTypeName))
            return false;

        // Bound-generic SIMD aliases (Swift.SIMD2/3/4<Swift.Float>) resolve to non-generic
        // concrete managed projections (System.Numerics.Vector2/3/4) that are bit-compatible
        // with Swift's simd_floatN by-value ABI. Opt them out of the buffer-marshalling path
        // so the PInvoke signature + call-site emit the managed type directly instead of
        // IntPtr + `.Payload.DangerousGetHandle()` (which would fail CS1061 — Vector4 has no
        // Payload member, and the Swift @_cdecl wrapper expects the 16-byte value by value,
        // not an 8-byte pointer).
        if (TypeDatabaseExtensions.TryResolveBoundGenericAlias(_typeDatabase, namedTypeSpec, out _))
            return false;

        return true;
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
        return TranslateBoundGenericTypeToCSharp(namedTypeSpec, genericContext, propertyDecl.ModuleDecl,
            propertyDecl.ParentDecl as TypeDecl);
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
        return TranslateBoundGenericTypeToCSharp(namedTypeSpec, genericContext, argumentDecl.ModuleDecl,
            argumentDecl.ParentDecl as TypeDecl);
    }

    /// <summary>
    /// Translates a bound generic TypeSpec into a C# type name.
    /// Used by protocol/proxy emission paths that have a TypeSpec but no parent declaration.
    /// </summary>
    public string TranslateBoundGenericTypeToCSharp(TypeSpec typeSpec, GenericContext genericContext)
        => TranslateBoundGenericTypeToCSharp(typeSpec, genericContext, moduleDecl: null);

    /// <summary>
    /// Translates a bound generic TypeSpec into a C# type name, with module context for
    /// nested-generic-owner resolution. When <paramref name="moduleDecl"/> is non-null,
    /// <c>QualifyNestedGenericOwners</c> can walk the TypeDecl chain to place outer generic
    /// arguments on the correct segment (e.g., <c>Outer&lt;T&gt;.Inner</c> vs
    /// <c>Outer.Inner&lt;T&gt;</c>).
    /// </summary>
    public string TranslateBoundGenericTypeToCSharp(TypeSpec typeSpec, GenericContext genericContext, ModuleDecl? moduleDecl)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec || !namedTypeSpec.ContainsGenericParameters)
            throw new NotSupportedException(
                $"Attempted to translate non-bound-generic TypeSpec: {typeSpec}");
        return TranslateBoundGenericTypeToCSharp(namedTypeSpec, genericContext, moduleDecl);
    }

    /// <summary>
    /// Returns true when the type is a <c>Swift.Array&lt;any P.Type&gt;</c> — an array of
    /// existential protocol metatypes. The element type must be both existential
    /// (<c>IsAny=true</c>) and a metatype (trailing <c>.Type</c>). The protocol itself is
    /// returned via <paramref name="protocolName"/> as a module-qualified string (e.g.,
    /// <c>"MusicKit.MusicCatalogSearchable"</c>).
    ///
    /// This pattern is handled by <c>MetatypeArrayBridgeEmitter</c>: the Swift side
    /// accepts a C array of metatype pointers + count and reconstructs <c>[any P.Type]</c>
    /// via <c>unsafeBitCast</c>. The validator allows this pattern through so the bridge
    /// can fire.
    /// </summary>
    /// <summary>
    /// Returns true when <paramref name="typeSpec"/> is <c>Array&lt;any P.Type&gt;</c> and the
    /// specialization-hints registry has at least one conformer for P that is allowed while
    /// generating bindings for <paramref name="moduleFilter"/>. Scoped hints (e.g.
    /// MusicKit-owned conformers) fail closed when <paramref name="moduleFilter"/> is null —
    /// unscoped global hints still match.
    /// </summary>
    public static bool IsArrayOfExistentialMetatypes(TypeSpec typeSpec, string? moduleFilter, out string? protocolName)
    {
        protocolName = null;
        if (typeSpec is not NamedTypeSpec outer || !MarshallingHelpers.IsSwiftArray(outer))
            return false;
        if (outer.GenericParameters.Count != 1)
            return false;
        var element = outer.GenericParameters[0];
        if (element is not NamedTypeSpec namedElement || !namedElement.IsAny)
            return false;
        if (!WrapperValidation.IsMetatypeType(namedElement))
            return false;

        // The TypeSpecTokenizer treats '.' as a valid name character, so dotted qualified
        // metatype names arrive flat in Name (e.g., "SwiftBindingsTestLib.SearchableItem.Type").
        // Strip the trailing ".Type" to get the protocol's qualified name.
        var qualified = namedElement.Name;
        if (!qualified.EndsWith(".Type", StringComparison.Ordinal))
            return false;
        qualified = qualified.Substring(0, qualified.Length - ".Type".Length);
        if (!ConcreteSpecializationEngine.HasKnownHintConformers(qualified, moduleFilter))
            return false;
        protocolName = qualified;
        return true;
    }

    /// <summary>
    /// Returns true when the type is a supported container with existential elements that can
    /// be marshalled. Handles direct containers (Array, Dictionary, KeyPath family) and
    /// Optional-wrapped containers (Optional&lt;Array&lt;any P&gt;&gt;,
    /// Optional&lt;Dictionary&lt;K, any P&gt;&gt;, Optional&lt;KeyPath&lt;any P, V&gt;&gt;). Also
    /// handles direct Optional&lt;any P&gt; as a supported pattern. All existential elements are
    /// validated for TypeRecord availability, non-object public type, and ObjC filter parity.
    /// For KeyPath family containers, the Root (slot 0) must be a supported existential and the
    /// Value (slot 1, where present) must be non-existential and projectable.
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

        // Array<any P> — element directly existential, OR Array<<nested supported container>>
        // (e.g. Array<Array<any P>>, Array<Dictionary<K, any P>>). The nested case recurses (audit
        // L229) but ONLY into genuine Array/Dictionary leaves (see IsNestedExistentialContainer):
        // TypeProjectionFactory builds a nested ArrayProjection whose every direction —
        // forward param (GetParameterElementConversion), forward/owned return
        // (GetReturnElementConversion / GetOwnedReturnElementConversion), and the protocol-receiver
        // getter/setter (which fall back to the projection's recursive conversions when the
        // single-level existential fast paths in ProtocolProxyEmitter.Receivers return null) —
        // threads the existential adoption down to the buried leaf. Real-world libs DO exercise this
        // (nested collections like [String: [String: any P]] and nested dictionary returns), so it is
        // covered by validate as well as the BindingTests owned-return LifetimeTracker probe +
        // reverse-dispatch/param round-trip fixtures.
        if (MarshallingHelpers.IsSwiftArray(outerNamedType) &&
            outerNamedType.GenericParameters.Count > 0)
        {
            var elementSpec = outerNamedType.GenericParameters[0];
            if (_existentialHandler.IsExistential(elementSpec))
                return IsValidExistentialForContainer(elementSpec);
            if (IsNestedExistentialContainer(elementSpec))
                return true;
        }

        // Dictionary<K, any P> — only VALUE position (GenericParameters[1]) may be existential.
        // Key position (GenericParameters[0]) is not allowed (Swift requires Hashable,
        // ExistentialContainer is not Hashable). Reject if key is also existential. The VALUE may
        // also be a nested supported container (e.g. Dictionary<K, Array<any P>>) — recurse, again
        // only into genuine Array/Dictionary leaves (see IsNestedExistentialContainer).
        if (MarshallingHelpers.IsSwiftDictionary(outerNamedType) &&
            outerNamedType.GenericParameters.Count > 1 &&
            !_existentialHandler.IsExistential(outerNamedType.GenericParameters[0]))
        {
            var valueSpec = outerNamedType.GenericParameters[1];
            if (_existentialHandler.IsExistential(valueSpec))
                return IsValidExistentialForContainer(valueSpec);
            if (IsNestedExistentialContainer(valueSpec))
                return true;
        }

        // KeyPath family (KeyPath, PartialKeyPath, WritableKeyPath, ReferenceWritableKeyPath) —
        // Root (slot 0) may be an existential. AnyKeyPath has arity 0 and is rejected here
        // because it has no Root slot to be existential. PartialKeyPath<Root> has arity 1.
        // KeyPath / WritableKeyPath / ReferenceWritableKeyPath have arity 2 (Root + Value).
        // Slot 1 (Value, where present) must not itself be existential, and must project to
        // a real C# type — otherwise the emitted KeyPath<Root, TValue> public signature
        // would not compile.
        if (TypeProjectionFactory.IsKeyPathFamily(outerNamedType.Name))
        {
            var arity = TypeProjectionFactory.GetKeyPathArity(outerNamedType.Name);
            if (arity != outerNamedType.GenericParameters.Count || arity < 1)
                return false;
            var rootSpec = outerNamedType.GenericParameters[0];
            if (!_existentialHandler.IsExistential(rootSpec))
                return false;
            if (!IsValidExistentialForContainer(rootSpec))
                return false;
            if (arity >= 2)
            {
                var valueSpec = outerNamedType.GenericParameters[1];
                if (_existentialHandler.IsExistential(valueSpec))
                    return false;
                // Minimal ProjectionContext is sufficient: Value is non-existential per the
                // gate above, and CurrentModuleName / GenericContext only affect existential
                // qualification (see ProjectExistential). This admission check has no parent
                // or method context to thread.
                var projector = new TypeProjectionFactory();
                var projection = projector.Project(valueSpec, new ProjectionContext
                {
                    TypeDatabase = _typeDatabase,
                    IsParameter = false,
                });
                if (projection is null)
                    return false;
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// The nested-container recursion admits a buried existential ONLY through genuine
    /// nested Array/Dictionary leaves — <c>Array&lt;Array&lt;any P&gt;&gt;</c>,
    /// <c>Array&lt;Dictionary&lt;K, any P&gt;&gt;</c>, <c>Dictionary&lt;K, Array&lt;any P&gt;&gt;</c>,
    /// <c>Dictionary&lt;K, Dictionary&lt;K2, any P&gt;&gt;</c>. It must NOT descend into an Optional-wrapped
    /// existential element (e.g. <c>Array&lt;Optional&lt;any P&gt;&gt;</c>): that lowers to the SAME
    /// <c>Array&lt;Optional&lt;existential&gt;&gt;</c> ABI shape as a variadic array-literal initializer
    /// (<c>ExpressibleByArrayLiteral</c>'s <c>init(arrayLiteral: Element...)</c> → <c>Array&lt;Element&gt;</c>),
    /// which the @_cdecl wrapper cannot forward — it would pass <c>[T]</c> where <c>T...</c> is required, and
    /// the variadic guards in ConstructorWrapperEmitter/MethodHandler key off <c>HasVariadicParameter</c>,
    /// which such an init does not carry, so an uncompilable wrapper would be emitted.
    /// The top-level Optional branch in <see cref="IsContainerWithSupportedDirectExistential"/> still admits an
    /// Optional OUTER container; only the element/value RECURSION is restricted here. (Optional-of-existential
    /// element support needs variadic-init handling that is out of scope; until then it stays unadmitted as at
    /// baseline.)
    /// </summary>
    private bool IsNestedExistentialContainer(TypeSpec spec)
        => spec is NamedTypeSpec named &&
           (MarshallingHelpers.IsSwiftArray(named) || MarshallingHelpers.IsSwiftDictionary(named)) &&
           IsContainerWithSupportedDirectExistential(named);

    /// <summary>
    /// Validates that an existential TypeSpec is supported for use inside a container:
    /// resolvable protocols, supported count (≤8), non-object public type, and ObjC filter parity.
    /// </summary>
    private bool IsValidExistentialForContainer(TypeSpec existentialTypeSpec)
    {
        var protocolList = _existentialHandler.ToProtocolListTypeSpec(existentialTypeSpec);
        if (protocolList == null || !_existentialHandler.IsSupportedExistential(protocolList))
            return false;
        // Bare Any (0 effective protocols) is intentionally supported — it's not an unknown protocol,
        // it's Swift's explicit "any value" type. ExistentialContainer0 is the correct ABI.
        if (_existentialHandler.IsBareAny(protocolList))
            return true;
        if (!_existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
            return false;
        if (_existentialHandler.GetPublicExistentialType(protocolList) == "object")
            return false;
        // P1 fix: Mixed compositions where ObjC filtering drops protocols
        // would produce proxy/container size mismatch at runtime.
        // Mirrors ExistentialHandler.GetEffectiveProtocols (per-module ObjC-prefix gate).
        var filteredCount = protocolList.Protocols.Keys
            .Count(p => !TypeDatabaseExtensions.IsObjCExistentialBridgedProtocol(p));
        if (filteredCount != protocolList.Protocols.Count)
            return false;
        return true;
    }

    /// <summary>
    /// Tries to find the first existential type ARGUMENT carried by a bound-generic
    /// container — e.g. <c>Array&lt;any P&gt;</c>, <c>Optional&lt;any P&gt;</c>,
    /// <c>Dictionary&lt;K, any P&gt;</c>. Direct existential parameters
    /// (<c>any P</c>, <c>any P&lt;X&gt;</c>) are intentionally excluded: they are
    /// routed through <see cref="ExistentialHandler"/> with their own gates and
    /// projection through <see cref="ExistentialHandler"/> with proxy dispatch. If
    /// this method matched on the outer existential too, parameters of type
    /// <c>any P&lt;X&gt;</c> would short-circuit at the bound-generic-existential
    /// gate and never reach the constrained-existential lowering.
    /// </summary>
    /// <param name="typeSpec">The type specification to inspect.</param>
    /// <param name="existentialType">The first existential type argument encountered.</param>
    /// <returns><c>true</c> if a nested existential type argument was found; otherwise, <c>false</c>.</returns>
    public bool TryGetFirstExistentialTypeArgument(TypeSpec typeSpec, out string existentialType)
    {
        existentialType = string.Empty;
        // Direct existentials are not "bound-generic-with-existential-arg" — they're
        // existentials handled separately. Inner-position existentials (Array<any P>)
        // are still caught by the descent below because the recursive call uses
        // `TryGetExistentialInsideTypeArgs`.
        if (_existentialHandler.IsExistential(typeSpec))
            return false;

        return TryGetExistentialInsideTypeArgs(typeSpec, out existentialType);
    }

    /// <summary>
    /// Recursive helper: at every nested position, both direct existentials and
    /// existentials carried inside further generic args count as a hit.
    /// </summary>
    private bool TryGetExistentialInsideTypeArgs(TypeSpec typeSpec, out string existentialType)
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
                    if (TryGetExistentialInsideTypeArgs(genericParameter, out existentialType))
                    {
                        return true;
                    }
                }
                break;
            case TupleTypeSpec tupleTypeSpec:
                foreach (var element in tupleTypeSpec.Elements)
                {
                    if (TryGetExistentialInsideTypeArgs(element, out existentialType))
                    {
                        return true;
                    }
                }
                break;
            case ClosureTypeSpec closureTypeSpec:
                if (TryGetExistentialInsideTypeArgs(closureTypeSpec.Arguments, out existentialType))
                {
                    return true;
                }

                if (TryGetExistentialInsideTypeArgs(closureTypeSpec.ReturnType, out existentialType))
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

        // Extract method-level generic params for conditional extension constraint fallback.
        // When contextDecl is a MethodDecl, its GenericParameters include both parent-type
        // constraints AND conditional extension constraints. The fallback in
        // GenericTypeParamSatisfiesConstraint uses these to accept constraints that the
        // parent type doesn't declare but the extension does.
        var methodGenericParams = contextDecl is MethodDecl methodDecl
            ? methodDecl.GenericParameters
            : null;

        return TryGetFirstUnsatisfiedConstraint(typeSpec, moduleDecl, parentTypeGenericParams, methodGenericParams, out details);
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
    /// ISwiftObject constraint. Checks ObjC-bridged types, native-remapped types, and tuples
    /// — all blocked except inside Swift.Optional, which has no ISwiftObject constraint on T.
    /// Closures are NOT checked — they fall back to object via AnyType/ContainsPlaceholder,
    /// so the entire type becomes [UnsupportedSwiftType] object (compiles fine).
    /// </summary>
    public bool HasNonSwiftObjectGenericArg(TypeSpec typeSpec)
        => HasNonSwiftObjectGenericArg(typeSpec, isParameterPosition: false);

    /// <summary>
    /// Position-aware variant of <see cref="HasNonSwiftObjectGenericArg(TypeSpec)"/>.
    /// When <paramref name="isParameterPosition"/> is true, <c>Swift.Result</c> is NOT
    /// granted the ISwiftObject bypass — outbound Result marshalling is unsupported
    /// (<see cref="ResultProjection.GetParameterPlan"/> throws), and
    /// <c>SwiftResult.FromSuccess</c>/<c>FromFailure</c> produce C#-only instances whose
    /// <c>Payload</c> access throws. Return/property contexts retain the bypass.
    /// </summary>
    public bool HasNonSwiftObjectGenericArg(TypeSpec typeSpec, bool isParameterPosition)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec || !namedTypeSpec.ContainsGenericParameters)
            return false;

        // ObjC-bridgeable containers (e.g., [URL], [String: URL], Set<URL>, [[URL]])
        // bypass the ISwiftObject check entirely — the whole container bridges to its ObjC
        // collection counterpart at the @_cdecl boundary, so SwiftArray<T>/SwiftDictionary<K,V>
        // are never created and ISwiftObject conformance is irrelevant.
        if (CdeclParamMapper.IsObjCBridgeableContainer(namedTypeSpec, _typeDatabase) ||
            CdeclParamMapper.IsOptionalObjCBridgeableContainer(namedTypeSpec, _typeDatabase))
            return false;

        // Containers with ObjC-bridged or native-remapped elements (e.g., [UIImage], [URL: UIColor])
        // bypass the ISwiftObject check. SwiftArray/SwiftDictionary/SwiftSet have no ISwiftObject
        // constraint on their type parameters, and the projection system handles element-to-IntPtr
        // mapping. This is separate from IsObjCBridgeableContainer which also changes the @_cdecl
        // marshalling strategy — this bypass only relaxes the constraint check.
        if (IsUnconstrainedContainerWithProjectableElements(namedTypeSpec))
            return false;

        // Measurement<UnitType> — non-frozen generic struct with ObjC-bridged unit args.
        // The C# Measurement<T> class has no ISwiftObject constraint on T (it uses VWT-backed
        // storage and resolves unit metadata via the ObjC runtime). Bypass the check so members
        // with Measurement parameters/returns are not skipped.
        if (namedTypeSpec.Name == "Foundation.Measurement")
            return false;

        // ManagedSettings.Token<Kind> — non-frozen generic struct used as typed identifier.
        // The marker type args (Application, ActivityCategory, WebDomain) are phantom types
        // that don't implement ISwiftObject. Bypass so FamilyControls token properties are emitted.
        if (namedTypeSpec.Name == "ManagedSettings.Token")
            return false;

        // Swift.Optional (SwiftOptional<T>) and Swift.Result (SwiftResult<TSuccess, TFailure>)
        // have no ISwiftObject constraint on their type parameters, so tuples (incl. empty tuple
        // = Void) and ObjC-bridged types are valid generic args; their projections
        // (OptionalProjection / ResultProjection) handle marshalling. All other emitted generics
        // have 'where T : ISwiftObject', making ValueTuple args a CS0311 error.
        //
        // Result bypass is return/property-only: ResultProjection.GetParameterPlan throws,
        // and SwiftResult.FromSuccess/FromFailure yields a C#-only instance whose Payload
        // access throws. Accepting Result in parameter position would emit constructors /
        // methods that crash as soon as a C# caller supplies a Result argument.
        bool outerIsOptional = namedTypeSpec.Name == "Swift.Optional";
        bool outerIsResult = namedTypeSpec.Name == "Swift.Result";
        bool resultBypassApplies = outerIsResult && !isParameterPosition;
        bool outerBypassesISwiftObject = outerIsOptional || resultBypassApplies;

        foreach (var genericParam in namedTypeSpec.GenericParameters)
        {
            // Swift.Void (named) maps to SwiftVoid, which doesn't implement ISwiftObject
            if (!outerBypassesISwiftObject && genericParam is NamedTypeSpec { Name: "Swift.Void" })
                return true;

            // All tuples (including empty tuple = Void) don't implement ISwiftObject
            if (!outerBypassesISwiftObject && genericParam is TupleTypeSpec)
                return true;

            // B5: Optional/Result tuple with existential element — check tuple elements for unresolvable existentials
            if (outerBypassesISwiftObject && genericParam is TupleTypeSpec optTuple && !optTuple.IsEmptyTuple)
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

            // ObjC-bridged (UIImage) and native-remapped (Foundation.URL → NSUrl) types don't implement
            // ISwiftObject — blocked in constrained generics. But SwiftOptional<T> and SwiftResult<.,.>
            // have no constraint, so Optional<URL>, Optional<UIImage>, Result<UIImage, E> etc.
            // are valid (projection factory handles marshalling).
            if (!outerBypassesISwiftObject && genericParam is NamedTypeSpec namedArg && (IsObjCBridgedType(namedArg) || IsNonSwiftObjectMappedType(namedArg)))
                return true;

            if (HasNonSwiftObjectGenericArg(genericParam, isParameterPosition))
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
        ModuleDecl? moduleDecl,
        TypeDecl? parentTypeDecl = null)
    {
        if (namedTypeSpec.Name == "Swift.Void")
            return "Swift.SwiftVoid";

        // Check if this named type is itself a generic type parameter.
        // Trust the context resolution over the IsGenericTypeParameter shape check:
        // Apple framework ABI JSON emits sugared parameter names ("SignedType", "Element")
        // directly as typespec names instead of the τ_0_0 form. When BuildGenericContextFrom…
        // has stored the sugared name as a context key, TryResolve is the authoritative
        // signal that the typespec is a generic parameter in the current scope.
        if (namedTypeSpec.GenericParameters.Count == 0 && namedTypeSpec.InnerType == null &&
            genericContext.TryResolve(namedTypeSpec.Name, out var resolvedName))
        {
            return resolvedName;
        }

        // Bound-generic SIMD aliases collapse to a non-generic managed type
        // (e.g. Swift.SIMD3<Swift.Float> → System.Numerics.Vector3). The resolved record IS the
        // final C# type — don't append the bound-generic's type arguments, which would produce
        // invalid syntax like `System.Numerics.Vector3<float>` on a non-generic typealias.
        if (TypeDatabaseExtensions.TryResolveBoundGenericAlias(_typeDatabase, namedTypeSpec, out var aliasRecord))
        {
            return aliasRecord.CSharpTypeName.FullyQualifiedName;
        }

        var typeReference = _typeDatabase.GetTypeRecordOrAnyType(namedTypeSpec);

        // If the type falls back to AnyType or is IntPtr (pointer types), don't append generic parameters
        // since these are not generic types in C# and adding <T1, T2> would be invalid C#
        // Pointer types like UnsafeMutablePointer<T> resolve to IntPtr which doesn't support generics
        if (typeReference == TypeDatabaseExtensions.AnyType ||
            typeReference == TypeDatabaseExtensions.IntPtrType)
        {
            return typeReference.CSharpTypeName.FullyQualifiedName;
        }

        List<string> translatedGenericParameters = new();
        bool isStdlibContainer = s_stdlibGenerics.Contains(namedTypeSpec.Name);
        foreach (var genericParameter in namedTypeSpec.GenericParameters)
        {
            // Bare Any (0 effective protocols) inside stdlib containers should use ExistentialContainer0,
            // which is the correct ABI type for [String: Any], [Any], etc.
            // For user-defined generics, bare Any stays as AnyType to avoid ISwiftObject constraint violations.
            if (isStdlibContainer &&
                _existentialHandler.IsExistential(genericParameter))
            {
                var protocolList = _existentialHandler.ToProtocolListTypeSpec(genericParameter);
                if (protocolList != null && _existentialHandler.IsBareAny(protocolList))
                {
                    translatedGenericParameters.Add("Swift.Runtime.ExistentialContainer0");
                    continue;
                }
            }

            // ObjC-bridged class types (UIView, NSURLSessionTask) in stdlib containers
            // map to IntPtr — the raw pointer representation. The projection system handles
            // element conversion (GetNSObject<T> for return, .Handle for parameter).
            if (isStdlibContainer &&
                genericParameter is NamedTypeSpec namedGenericParam &&
                !namedGenericParam.ContainsGenericParameters &&
                IsObjCBridgedType(namedGenericParam) &&
                !IsNonSwiftObjectMappedType(namedGenericParam))
            {
                translatedGenericParameters.Add("IntPtr");
                continue;
            }

            translatedGenericParameters.Add(TranslateTypeSpecToCSharp(genericParameter, genericContext, moduleDecl, parentTypeDecl));
        }

        var (typeName, outerArgsPlaced) = QualifyNestedGenericOwners(
            typeReference.CSharpTypeName.FullyQualifiedName, namedTypeSpec, genericContext, moduleDecl,
            translatedGenericParameters, parentTypeDecl);

        // For nested-type references encoded via InnerType chain (e.g., the parser's output
        // for `VerificationOutcome<String>.Failure`: outer NamedTypeSpec carries the generic
        // args, InnerType points at the non-generic leaf), the outer args belong to the outer
        // segment of the dotted FQN. QualifyNestedGenericOwners places them there (and places any
        // args carried by the InnerType chain on their own segments — the doubly-generic
        // Outer<X>.Inner<Y> case, Step 1b); appending the outer args again at the end would
        // mis-place them on the leaf (producing "Outer.Inner<T>" instead of "Outer<T>.Inner").
        if (namedTypeSpec.InnerType != null && outerArgsPlaced)
            return typeName;

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
    private string TranslateTypeSpecToCSharp(TypeSpec typeSpec, GenericContext genericContext, ModuleDecl? moduleDecl,
        TypeDecl? parentTypeDecl = null)
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

        // Check if the type is a generic type parameter before other dispatch.
        // Trust the context as the authoritative signal (see TranslateBoundGenericTypeToCSharp
        // for why the IsGenericTypeParameter shape check is insufficient for Apple-framework ABIs).
        if (typeSpec is NamedTypeSpec namedSpec &&
            namedSpec.GenericParameters.Count == 0 && namedSpec.InnerType == null &&
            genericContext.TryResolve(namedSpec.Name, out var csName))
        {
            return csName;
        }

        return typeSpec switch
        {
            NamedTypeSpec { Name: "Swift.Void" } => "Swift.SwiftVoid",
            NamedTypeSpec namedTypeSpec => TranslateBoundGenericTypeToCSharp(namedTypeSpec, genericContext, moduleDecl, parentTypeDecl),
            ClosureTypeSpec closureTypeSpec => TranslateClosureTypeToCSharp(closureTypeSpec),
            TupleTypeSpec { IsEmptyTuple: true } => "Swift.SwiftVoid",
            TupleTypeSpec tupleTypeSpec => _tupleHandler.GetCSharpTupleType(tupleTypeSpec,
                ts => TranslateTypeSpecToCSharp(ts, genericContext, moduleDecl, parentTypeDecl)),
            // Associated type references (e.g., Self.Element inside Array<Self.Element>).
            // Try to resolve via ConformanceGraph when parent type context is available.
            AssociatedTypeReferenceSpec assocRef when
                _conformanceGraph != null && parentTypeDecl != null &&
                IsParentSelfReference(assocRef, parentTypeDecl) =>
                ResolveAssociatedTypeViaGraph(assocRef, parentTypeDecl, genericContext, moduleDecl),
            AssociatedTypeReferenceSpec => TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName,
            ProtocolListTypeSpec => TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName,
            _ => throw new NotSupportedException(
                $"Type spec {typeSpec.GetType().Name} ({typeSpec}) is not supported as a generic parameter")
        };
    }

    /// <summary>
    /// Checks whether the associated type reference's base type refers to the parent type's Self
    /// (type-level generic params at depth 0), not method-level generic params (depth > 0).
    /// </summary>
    private static bool IsParentSelfReference(AssociatedTypeReferenceSpec assocRef, TypeDecl parentTypeDecl)
    {
        var baseType = assocRef.BaseType;
        if (baseType == "Self")
            return true;

        // τ_D_I format: depth D, index I. Type-level params have depth 0.
        if (baseType.StartsWith("τ_"))
        {
            var parts = baseType.Substring(2).Split('_');
            if (parts.Length >= 2 && int.TryParse(parts[0], out var depth))
            {
                // Depth 0 = type-level generic params (Self in protocol context)
                // Depth > 0 = method-level generic params — skip graph resolution
                return depth == 0;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves an associated type reference by iterating the parent type's conformances
    /// and querying the ConformanceGraph for each protocol until a match is found.
    /// When multiple protocols resolve the same associated type name to different concrete
    /// types, falls back to AnyType to avoid silently picking the wrong witness.
    /// </summary>
    private string ResolveAssociatedTypeViaGraph(AssociatedTypeReferenceSpec assocRef,
        TypeDecl parentTypeDecl, GenericContext genericContext, ModuleDecl? moduleDecl)
    {
        var conformingTypeName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var conformances = GetConformances(parentTypeDecl);

        TypeSpec? firstMatch = null;

        foreach (var conformance in conformances)
        {
            if (_conformanceGraph!.TryResolve(
                conformingTypeName,
                conformance.Protocol.ModuleQualifiedName,
                assocRef.AssociatedTypeName,
                out var resolved) && resolved != null)
            {
                // Chained references (AssociatedTypeReferenceSpec) can't be resolved further
                if (resolved is AssociatedTypeReferenceSpec)
                    continue;

                if (firstMatch == null)
                {
                    firstMatch = resolved;
                }
                else if (firstMatch.ToString() != resolved.ToString())
                {
                    // Ambiguity: two protocols map the same associated type name to
                    // different concrete types. Fall back to AnyType rather than
                    // silently picking one that could produce a valid-but-wrong signature.
                    return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
                }
            }
        }

        if (firstMatch != null)
        {
            return TranslateTypeSpecToCSharp(firstMatch, genericContext, moduleDecl, parentTypeDecl);
        }

        return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Gets conformances from a TypeDecl, handling the fact that Conformances is defined
    /// on ClassDecl/StructDecl/EnumDecl, not on the base TypeDecl.
    /// </summary>
    private static IReadOnlyList<TypeConformance> GetConformances(TypeDecl typeDecl) => typeDecl switch
    {
        ClassDecl classDecl => classDecl.Conformances,
        StructDecl structDecl => structDecl.Conformances,
        EnumDecl enumDecl => enumDecl.Conformances,
        _ => Array.Empty<TypeConformance>(),
    };

    private (string name, bool outerArgsPlaced) QualifyNestedGenericOwners(
        string fullyQualifiedTypeName,
        NamedTypeSpec namedTypeSpec,
        GenericContext genericContext,
        ModuleDecl? moduleDecl,
        List<string> translatedOwnArgs,
        TypeDecl? parentTypeDecl)
    {
        if (moduleDecl == null || !namedTypeSpec.HasModule())
            return (fullyQualifiedTypeName, false);

        var typeDecl = FindTypeDecl(moduleDecl, SwiftTypeName.FromTypeSpec(namedTypeSpec));
        if (typeDecl == null)
            return (fullyQualifiedTypeName, false);

        var typeChain = GetTypeDeclChain(typeDecl);
        if (typeChain.Count <= 1)
            return (fullyQualifiedTypeName, false);

        var segments = fullyQualifiedTypeName.Split('.');
        if (segments.Length < typeChain.Count)
            return (fullyQualifiedTypeName, false);

        var firstTypeSegment = segments.Length - typeChain.Count;

        // Step 1: when the typespec carries its own translated args (e.g., the outer segment of
        // "VerificationOutcome<T>.Failure"), place them on the OUTERMOST generic ancestor whose
        // declared param count matches. This is the fix for the nested-type-on-generic-outer
        // reference shape — ABI parser encodes outer args on the OUTER NamedTypeSpec with an
        // InnerType chain for the leaf, so the args must land on an outer segment not the leaf.
        var outerArgsPlaced = false;
        if (namedTypeSpec.InnerType != null && translatedOwnArgs.Count > 0)
        {
            for (var i = 0; i < typeChain.Count - 1; i++)
            {
                var ownerType = typeChain[i];
                if (!ownerType.IsGeneric)
                    continue;
                if (ownerType.GenericParameters.Count != translatedOwnArgs.Count)
                    continue;

                segments[firstTypeSegment + i] = $"{segments[firstTypeSegment + i]}<{string.Join(", ", translatedOwnArgs)}>";
                outerArgsPlaced = true;
                break;
            }
        }

        // Step 1b: place generic args carried by the InnerType chain on their own segments. A
        // doubly-generic nested type (e.g. Outer<Int>.Inner<String>) encodes the inner args on
        // namedTypeSpec.InnerType.GenericParameters — the outer loop in the caller only translates
        // the OUTER's args (namedTypeSpec.GenericParameters), so without this the leaf renders bare
        // (Outer<nint>.Inner) and Roslyn rejects it with CS0305 ("requires N type arguments"). The
        // InnerType chain aligns positionally with the type-decl chain below the outer: link k maps
        // to typeChain[1 + k] and segment[firstTypeSegment + 1 + k]. Translate here (while segments
        // are still bare) rather than after qualification — re-splitting an already-qualified name
        // on '.' would break on dotted args like Outer<Module.Foo>.
        var innerLink = namedTypeSpec.InnerType;
        for (var i = 1; i < typeChain.Count && innerLink != null; i++, innerLink = innerLink.InnerType)
        {
            if (innerLink.GenericParameters.Count == 0)
                continue;
            // Skip a segment already qualified (defensive; inner segments are untouched by Step 1).
            if (segments[firstTypeSegment + i].EndsWith('>'))
                continue;
            var innerArgs = new List<string>(innerLink.GenericParameters.Count);
            foreach (var innerArg in innerLink.GenericParameters)
                innerArgs.Add(TranslateTypeSpecToCSharp(innerArg, genericContext, moduleDecl, parentTypeDecl));
            segments[firstTypeSegment + i] = $"{segments[firstTypeSegment + i]}<{string.Join(", ", innerArgs)}>";
        }

        // Step 2: for remaining generic ancestors without args yet, fall back to context-based
        // resolution. Preserves the flat-name path (typespec FQN has all segments in one Name,
        // no InnerType) where the args come from the caller's GenericContext.
        if (!genericContext.IsEmpty)
        {
            for (var i = 0; i < typeChain.Count - 1; i++)
            {
                var ownerType = typeChain[i];
                if (!ownerType.IsGeneric)
                    continue;
                // Skip segments already qualified by Step 1 (avoid double-placement).
                if (segments[firstTypeSegment + i].EndsWith('>'))
                    continue;

                var ownerArgs = ResolveTypeDeclGenericArguments(ownerType, genericContext);
                if (ownerArgs.Count != ownerType.GenericParameters.Count || ownerArgs.Count == 0)
                    continue;

                segments[firstTypeSegment + i] = $"{segments[firstTypeSegment + i]}<{string.Join(", ", ownerArgs)}>";
            }
        }

        return (string.Join(".", segments), outerArgsPlaced);
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
        IReadOnlyList<GenericArgumentDecl>? parentTypeGenericParams,
        IReadOnlyList<GenericArgumentDecl>? methodGenericParams, out string details)
    {
        details = string.Empty;

        switch (typeSpec)
        {
            case NamedTypeSpec namedTypeSpec:
                // ObjC-bridgeable containers (e.g., [URL], [String: URL], Set<URL>, [[URL]])
                // bypass constraint validation entirely — the whole container bridges to its ObjC
                // collection counterpart (NSArray/NSDictionary/NSSet) at the @_cdecl boundary,
                // so we never create SwiftArray<T>/SwiftDictionary<K,V> and ISwiftObject is irrelevant.
                if (CdeclParamMapper.IsObjCBridgeableContainer(namedTypeSpec, _typeDatabase) ||
                    CdeclParamMapper.IsOptionalObjCBridgeableContainer(namedTypeSpec, _typeDatabase))
                    return false;

                if (namedTypeSpec.ContainsGenericParameters &&
                    TryValidateGenericTypeConstraints(namedTypeSpec, moduleDecl, parentTypeGenericParams, methodGenericParams, out details))
                {
                    return true;
                }

                foreach (var genericParameter in namedTypeSpec.GenericParameters)
                {
                    if (TryGetFirstUnsatisfiedConstraint(genericParameter, moduleDecl, parentTypeGenericParams, methodGenericParams, out details))
                        return true;
                }
                return false;

            case TupleTypeSpec tupleTypeSpec:
                foreach (var element in tupleTypeSpec.Elements)
                {
                    if (TryGetFirstUnsatisfiedConstraint(element, moduleDecl, parentTypeGenericParams, methodGenericParams, out details))
                        return true;
                }
                return false;

            case ClosureTypeSpec closureTypeSpec:
                if (TryGetFirstUnsatisfiedConstraint(closureTypeSpec.Arguments, moduleDecl, parentTypeGenericParams, methodGenericParams, out details))
                    return true;
                return TryGetFirstUnsatisfiedConstraint(closureTypeSpec.ReturnType, moduleDecl, parentTypeGenericParams, methodGenericParams, out details);

            default:
                return false;
        }
    }

    private bool TryValidateGenericTypeConstraints(NamedTypeSpec boundGenericType, ModuleDecl moduleDecl,
        IReadOnlyList<GenericArgumentDecl>? parentTypeGenericParams,
        IReadOnlyList<GenericArgumentDecl>? methodGenericParams, out string details)
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

                if (ShouldSkipConstraint(conformance.ConformanceTarget, typeArgument, parentTypeGenericParams, methodGenericParams))
                    continue;

                if (SatisfiesConstraint(typeArgument, conformance.ConformanceTarget, moduleDecl, parentTypeGenericParams, methodGenericParams)
                    && !ConformanceUnreachableInCSharp(typeArgument, conformance.ConformanceTarget, moduleDecl))
                    continue;

                details = $"Type argument '{typeArgument}' does not satisfy constraint '{conformance.ConformanceTarget.ModuleQualifiedName}' on '{boundGenericType.NameWithoutModule}'.";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detects the Swift-extension-on-foreign-type pattern that <see cref="SatisfiesConstraint"/>
    /// cannot reject by itself: the current module owns an <c>extension</c> on a type from a
    /// different module that adds protocol conformance (e.g.
    /// <c>extension Foundation.Data: DataTransformable</c>). Swift permits this freely; C#
    /// does not — you cannot post-hoc add an interface implementation to a type declared in
    /// another assembly. The bound type <c>Backend&lt;Foundation.Data&gt;</c> is emitted with
    /// the C# constraint <c>where T : IDataTransformable</c> on the Apple-supplement
    /// <c>Foundation.Data</c>, which does not implement that interface, producing CS0315.
    ///
    /// Returns true when (a) the type argument lives in a different Swift module than the
    /// bound generic's emitting module, AND (b) the local TypeDecl carrying the conformance
    /// evidence is itself an extension (Kind=Struct/Class/Enum with no own primary declaration
    /// in the type argument's source module). Pragmatic detection: if the typeArgument's
    /// Swift module differs from the emitting module, the conformance has to come from a
    /// local extension — and that extension is unreachable in the C# projection.
    /// </summary>
    private bool ConformanceUnreachableInCSharp(TypeSpec typeArgument, SwiftTypeName protocolConstraint, ModuleDecl moduleDecl)
    {
        if (typeArgument is not NamedTypeSpec namedTypeArgument || !namedTypeArgument.HasModule())
            return false;

        // Generic parameter — propagates through the surrounding `where` clause; not subject
        // to this filter.
        if (TypeSpecHelpers.IsGenericTypeParameter(namedTypeArgument))
            return false;

        // Class-bound constraints (`<T : SomeClass>`) flow through C# inheritance, not
        // interface implementation — `IsSubclassOfViaTypeDatabase` and the local
        // SuperclassNames walk in SatisfiesConstraint already verify them correctly.
        // The "extension on foreign type" problem is interface-implementation-shaped
        // (you can't post-hoc add an interface to a type in another assembly), so it
        // doesn't apply to class subtyping at all. Without this gate, a valid binding
        // site like `Box<PDFKit.PDFView>` for `Box<T: UIKit.UIView>` emitted from a
        // third module would be rejected — PDFKit ≠ emittingModule and UIKit ≠ PDFKit
        // triggers the module-difference heuristic even though the inheritance chain
        // is reachable in C#.
        if (_typeDatabase.TryGetTypeRecord(protocolConstraint, out var constraintRecord) &&
            constraintRecord.Kind != TypeRecordKind.Protocol)
            return false;

        var typeArgumentName = SwiftTypeName.FromTypeSpec(namedTypeArgument);

        // Self-conformance (typeArgument == protocolConstraint) and stdlib well-knowns
        // resolve to real C#-visible relationships — keep them.
        if (typeArgumentName == protocolConstraint)
            return false;
        if (_conformanceOracle.HasStdlibConformance(typeArgumentName, protocolConstraint))
            return false;

        // The "extension on foreign type" shape: the typeArgument's home module differs from
        // the module currently being emitted. Any conformance evidence we found inside
        // moduleDecl can only have come from a Swift extension on a foreign type — which the
        // C# side projects via the foreign module's existing static class and cannot retrofit
        // an interface onto.
        var emittingModule = moduleDecl.Name;
        if (string.IsNullOrEmpty(typeArgumentName.Module) || typeArgumentName.Module == emittingModule)
            return false;

        // Constraint protocol owned by the foreign module too → conformance must already
        // exist in that module's projection. Only Swift extensions inside the emitting
        // module produce the unreachable shape.
        if (protocolConstraint.Module == typeArgumentName.Module)
            return false;

        return true;
    }

    /// <summary>
    /// Decides whether a protocol-conformance constraint on a generic parameter should be
    /// skipped at bound-generic validation time. Sendable and constraints from unsupported
    /// modules (e.g. SwiftUI) are always skipped — they aren't modeled in C# at all.
    ///
    /// For PAT / Self-requirement / method-Self protocols, the skip is conditional on the
    /// type argument. When the argument is itself a generic parameter visible in scope
    /// (parent type or method generics), the constraint propagates through the surrounding
    /// C# `where` clause and bound-time validation has nothing to verify — skip. When the
    /// argument is a CONCRETE type (e.g. <c>Backend&lt;Swift.Foundation.Data&gt;</c>),
    /// the C# constraint emitted on the bound type's declaration (<c>where T : IDataTransformable</c>)
    /// still needs to be satisfied at the binding site, and a foreign supplement type with
    /// no local conformance evidence will fail <c>CS0315</c>. Letting the skip fire here
    /// silently emitted bound generics whose constraints could never be satisfied. Falling
    /// through to <see cref="SatisfiesConstraint"/> for concrete arguments fail-closes
    /// correctly — the member is dropped from the binding instead of emitting unbuildable code.
    /// </summary>
    private bool ShouldSkipConstraint(SwiftTypeName protocolType, TypeSpec typeArgument,
        IReadOnlyList<GenericArgumentDecl>? parentTypeGenericParams,
        IReadOnlyList<GenericArgumentDecl>? methodGenericParams)
    {
        if (protocolType.Name == "Sendable")
            return true;

        if (ValidationRuleSet.IsUnsupportedConstraintModule(protocolType.Module))
            return true;

        if (_typeDatabase.TryGetTypeRecord(protocolType, out var protocolRecord) &&
            protocolRecord.Kind == TypeRecordKind.Protocol &&
            (protocolRecord.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) ||
             protocolRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement) ||
             protocolRecord.Flags.HasFlag(TypeRecordFlags.HasMethodSelfTypeParams)))
        {
            // Generic-param argument → constraint flows through the C# `where` clause
            // of the enclosing type/method. Concrete argument → must verify conformance.
            return TypeSpecHelpers.IsGenericTypeParameter(typeArgument)
                || IsDeclaredGenericParam(typeArgument, parentTypeGenericParams)
                || IsDeclaredGenericParam(typeArgument, methodGenericParams);
        }

        return false;
    }

    private bool SatisfiesConstraint(TypeSpec typeArgument, SwiftTypeName protocolConstraint, ModuleDecl moduleDecl,
        IReadOnlyList<GenericArgumentDecl>? parentTypeGenericParams,
        IReadOnlyList<GenericArgumentDecl>? methodGenericParams = null)
    {
        if (TypeSpecHelpers.IsGenericTypeParameter(typeArgument)
            || IsDeclaredGenericParam(typeArgument, parentTypeGenericParams)
            || IsDeclaredGenericParam(typeArgument, methodGenericParams))
        {
            // A generic type parameter (e.g., τ_0_0 / T0 or a sugared multi-char name like
            // 'MusicItemType' that matches a parent/method generic parameter declaration)
            // satisfies a constraint if:
            // 1. The parent type's generic declaration includes that constraint, OR
            // 2. The method's generic parameters include a conditional extension constraint
            //    for that protocol (and the protocol is emittable — no associated types or Self).
            return GenericTypeParamSatisfiesConstraint(typeArgument, protocolConstraint, parentTypeGenericParams, moduleDecl, methodGenericParams);
        }

        if (typeArgument is not NamedTypeSpec namedTypeArgument || !namedTypeArgument.HasModule())
            return false;

        var typeArgumentName = SwiftTypeName.FromTypeSpec(namedTypeArgument);

        // Concrete type argument. Delegate the "does T satisfy C" decision to the single
        // conformance oracle, which consolidates self-conformance, the committed stdlib fact
        // table, stripped foreign conformances, class-subtyping walks, and transitive protocol
        // inheritance behind one fail-closed {Yes, No, Unknown} answer. Only Yes emits; both No
        // (Swift never promised the conformance) and Unknown (genuinely unprovable) fail closed.
        _typeDatabase.TryGetTypeRecord(protocolConstraint, out var constraintRecord);
        var typeArgumentDecl = FindTypeDecl(moduleDecl, typeArgumentName);
        return _conformanceOracle.ConcreteConforms(
            typeArgumentName, protocolConstraint, constraintRecord, typeArgumentDecl, moduleDecl)
            == ConformanceResult.Yes;
    }


    /// <summary>
    /// Checks whether a TypeSpec references a generic parameter declared by the supplied list.
    /// Matches by <see cref="NamedTypeSpec.Name"/> against <see cref="GenericArgumentDecl.TypeName"/>.
    /// This catches multi-character sugared names (e.g. Swift-source names like 'MusicItemType')
    /// that <see cref="TypeSpecHelpers.IsGenericTypeParameter(string)"/> cannot detect by shape alone.
    /// </summary>
    private static bool IsDeclaredGenericParam(TypeSpec typeArgument, IReadOnlyList<GenericArgumentDecl>? declaredParams)
    {
        if (declaredParams == null || declaredParams.Count == 0)
            return false;

        if (typeArgument is not NamedTypeSpec named)
            return false;

        if (named.GenericParameters.Count != 0 || named.InnerType != null)
            return false;

        var name = named.Name;
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var param in declaredParams)
        {
            if (param.TypeName == name || param.SugaredTypeName == name)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks whether a generic type parameter satisfies a protocol constraint based on
    /// the parent type's generic declarations. When no parent type generic parameters are
    /// available (e.g., free functions), the check is permissive and returns true.
    ///
    /// When the parent type doesn't satisfy the constraint, falls back to the method's
    /// generic parameters to check for conditional extension constraints. The constraint
    /// is accepted if the protocol is emittable (no associated types or Self requirements),
    /// since the P/Invoke infrastructure already generates witness table extraction for these.
    /// </summary>
    private bool GenericTypeParamSatisfiesConstraint(
        TypeSpec typeArgument, SwiftTypeName protocolConstraint,
        IReadOnlyList<GenericArgumentDecl>? parentTypeGenericParams,
        ModuleDecl? moduleDecl = null,
        IReadOnlyList<GenericArgumentDecl>? methodGenericParams = null)
    {
        // If no parent type generic parameters are available (e.g., free functions,
        // non-generic parent types), be permissive — the constraint can't be
        // validated against a parent type and may be satisfied at the call site.
        if (parentTypeGenericParams == null || parentTypeGenericParams.Count == 0)
            return true;

        var paramName = typeArgument is NamedTypeSpec namedArg ? namedArg.Name : typeArgument.ToString();

        // Find the matching generic parameter in the parent type's declarations.
        // Match on both TypeName (ABI internal, e.g. τ_0_0) and SugaredTypeName
        // (source-level name, e.g. 'MusicItemType') since a type-argument reference in a
        // parsed TypeSpec may use either form depending on the ABI capture.
        var matchingParam = parentTypeGenericParams.FirstOrDefault(
            p => p.TypeName == paramName || p.SugaredTypeName == paramName);
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
            if (moduleDecl != null && _conformanceOracle.ProtocolInheritsFrom(conformance.ConformanceTarget, protocolConstraint, moduleDecl))
                return true;
        }

        // Note: conditional extension constraints (e.g., `extension Table<T> where T: FetchableRecord`)
        // appear in the method's GenericParameters but NOT on the parent type. C# cannot express
        // method-level `where` constraints on parent type parameters (CS0699). Methods whose
        // signatures use bound generic types requiring these constraints (e.g., RecordCursor<T>
        // where T: IFetchableRecord) will not compile. We return false here to skip such methods.
        //
        // Methods from conditional extensions that DON'T reference constrained bound generics
        // in their signatures are unaffected — TryGetFirstUnsatisfiedConstraint is only called
        // for bound generic types, so those methods are never checked here.

        // The parent type does not constrain this parameter to conform to the required protocol,
        // and no conditional extension constraint was found.
        return false;
    }

    /// <summary>
    /// Delegates to <see cref="MethodValidationGates.IsProtocolAvailableForConstraint(SwiftTypeName, ITypeDatabase)"/>
    /// so the constraint-emission filter has a single source of truth. Used here to gate
    /// <em>conditional</em> extension constraints — a method-level constraint that doesn't
    /// appear on the parent type's generic parameters.
    /// </summary>
    private bool IsProtocolEmittableForConditionalConstraint(SwiftTypeName protocolTypeName)
        => MethodValidationGates.IsProtocolAvailableForConstraint(protocolTypeName, _typeDatabase);

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

    /// <summary>
    /// Returns true when a type is a container (Array/Dictionary/Set, optionally wrapped in Optional)
    /// whose element types that would fail the ISwiftObject check are all ObjC-bridged class types
    /// (UIImage → IntPtr). The TranslateBoundGenericTypeToCSharp method maps these to IntPtr,
    /// and the projection system handles element conversion (GetNSObject for return, .Handle for param).
    /// Native-remapped types (Foundation.URL → NSUrl, Foundation.Date → DateTimeOffset) are NOT
    /// included because they need different marshalling that the container path doesn't support.
    /// </summary>
    private bool IsUnconstrainedContainerWithProjectableElements(NamedTypeSpec typeSpec)
    {
        var target = typeSpec;
        // Unwrap one level of Optional
        if (target.Name == "Swift.Optional" && target.GenericParameters.Count == 1 &&
            target.GenericParameters[0] is NamedTypeSpec inner)
            target = inner;

        if (target.Name != "Swift.Array" && target.Name != "Swift.Dictionary" && target.Name != "Swift.Set")
            return false;

        // At least one element must be ObjC-bridged for the bypass to be meaningful
        bool hasObjCBridgedElement = false;

        foreach (var param in target.GenericParameters)
        {
            if (param is not NamedTypeSpec namedParam)
                return false;

            // ObjC-bridged class types project to IntPtr
            if (IsObjCBridgedType(namedParam) && !IsNonSwiftObjectMappedType(namedParam))
            {
                hasObjCBridgedElement = true;
                continue;
            }

            // Normal types that implement ISwiftObject are fine in containers
            // (they wouldn't trigger the non-ISwiftObject check on their own)
            if (!IsObjCBridgedType(namedParam) && !IsNonSwiftObjectMappedType(namedParam))
                continue;

            // Recurse into nested containers (e.g., [[UIImage]])
            if (namedParam.ContainsGenericParameters && IsUnconstrainedContainerWithProjectableElements(namedParam))
            {
                hasObjCBridgedElement = true;
                continue;
            }

            // Native-remapped or other non-ISwiftObject types without projections — block
            return false;
        }
        return hasObjCBridgedElement;
    }

    // Swift value types < 8 bytes whose Optional<T> fits within IntPtr (8 bytes).
    // Optional<T> = T + discriminant byte for value types. Only types < 8 bytes are safe.
    // Types ≥ 8 bytes (Int, String, URL, non-frozen structs, etc.) produce Optionals > 8 bytes.
    //
    // Also exposed via SmallOptionalInnerTypes for OptionalMarshalClassifier consistency.
    private static readonly HashSet<string> s_smallOptionalInnerTypes = new(StringComparer.Ordinal)
    {
        "Swift.Bool",       // 1 byte  → Optional = 2 bytes
        "Swift.Int8",       // 1 byte  → Optional = 2 bytes
        "Swift.UInt8",      // 1 byte  → Optional = 2 bytes
        "Swift.Int16",      // 2 bytes → Optional = 3 bytes
        "Swift.UInt16",     // 2 bytes → Optional = 3 bytes
        "Swift.Int32",      // 4 bytes → Optional = 5 bytes
        "Swift.UInt32",     // 4 bytes → Optional = 5 bytes
        "Swift.Float",      // 4 bytes → Optional = 5 bytes
    };

    /// <summary>
    /// Public accessor for the small-optional inner types set, used by
    /// <see cref="OptionalMarshalClassifier"/> to keep strategy classification in sync.
    /// </summary>
    public static IReadOnlyCollection<string> SmallOptionalInnerTypes => s_smallOptionalInnerTypes;

    /// <summary>
    /// Returns true if typeSpec is Optional&lt;T&gt; where T's size makes the Optional too large
    /// for IntPtr (8 bytes) to hold without truncation. This includes all value types ≥ 8 bytes
    /// (Int, String, URL, non-frozen structs, etc.). Only reference types (classes, ObjC-bridged)
    /// and small primitives (&lt; 8 bytes) produce Optionals that fit in IntPtr.
    /// </summary>
    public bool IsLargeOptionalParam(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType || namedType.Name != "Swift.Optional")
            return false;
        var innerElement = namedType.GenericParameters.FirstOrDefault();
        if (innerElement is not NamedTypeSpec innerNamed)
            return false;

        // Reference types (classes, ObjC-bridged) → Optional is pointer-sized → NOT large.
        if (CdeclParamMapper.IsOptionalWithReferenceInner(typeSpec, _typeDatabase))
            return false;

        // Protocol existentials → Optional uses nullable pointer ABI → NOT large.
        // ExistentialContainer is large but the P/Invoke marshals it as IntPtr.
        // Note: For PARAMETERS passed to @_cdecl wrappers, Optional<Protocol> still needs
        // DangerousGetHandle — this is handled separately via IsLargeOptionalProtocolParam().
        if (_typeDatabase.TryGetTypeRecord(innerNamed, out var innerRecord) &&
            innerRecord.Kind == TypeRecordKind.Protocol)
            return false;
        // Also check for unresolved protocol existentials (not in TypeDatabase but in ProtocolList form).
        if (CdeclParamMapper.IsProtocolExistentialType(typeSpec, _typeDatabase))
            return false;

        // Small value types (< 8 bytes) → Optional fits in IntPtr → NOT large.
        if (s_smallOptionalInnerTypes.Contains(innerNamed.Name))
            return false;

        // Everything else (Int, String, URL, non-frozen structs, enums, etc.) → large.
        // The previous approach used a hardcoded "known large" list that missed types like
        // Foundation.URL (~40 bytes), causing buffer truncation → SIGSEGV on NativeAOT.
        return true;
    }

    /// <summary>
    /// Returns true if any non-return parameter is a large Optional.
    /// </summary>
    public bool HasLargeOptionalParams(MethodDecl methodDecl)
    {
        return methodDecl.CSSignature.Skip(1)
            .Any(p => IsLargeOptionalParam(p.SwiftTypeSpec) || IsLargeOptionalProtocolParam(p.SwiftTypeSpec));
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

    /// <summary>
    /// Returns true if typeSpec is Optional&lt;Protocol&gt; — an Optional wrapping a protocol existential.
    /// These are excluded from <see cref="IsLargeOptionalParam"/> (returns false) because the return
    /// type path uses ExistentialContainer1 projection. But for PARAMETERS passed to @_cdecl wrappers,
    /// the SwiftOptional buffer contains the full Optional&lt;ExistentialContainer&gt; (40+ bytes on arm64),
    /// so the C# side must pass the buffer ADDRESS (DangerousGetHandle), not a truncated
    /// PayloadBuffer&lt;IntPtr&gt;.Buffer (8 bytes). This method detects that case.
    /// </summary>
    public bool IsLargeOptionalProtocolParam(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType || namedType.Name != "Swift.Optional")
            return false;
        var innerElement = namedType.GenericParameters.FirstOrDefault();

        // Check TypeDatabase for protocol kind (NamedTypeSpec inner types only).
        if (innerElement is NamedTypeSpec innerNamed &&
            _typeDatabase.TryGetTypeRecord(innerNamed, out var innerRecord) &&
            innerRecord.Kind == TypeRecordKind.Protocol)
            return true;

        // Check for protocol existentials including ProtocolListTypeSpec (any P & Q)
        // and other non-NamedTypeSpec forms. IsProtocolExistentialType handles both
        // Optional<ProtocolListTypeSpec> and Optional<NamedTypeSpec> protocol forms.
        if (CdeclParamMapper.IsProtocolExistentialType(typeSpec, _typeDatabase))
            return true;

        return false;
    }

    private static bool IsBareStdlibGeneric(NamedTypeSpec typeSpec) => s_stdlibGenerics.Contains(typeSpec.Name);

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
