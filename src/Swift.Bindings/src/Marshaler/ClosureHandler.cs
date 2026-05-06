// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Helper class for handling closure types in Swift bindings.
/// It provides methods to detect closure arguments and translate them to appropriate
/// C# delegate types or function pointers.
/// </summary>
public class ClosureHandler
{
    private readonly ITypeDatabase _typeDatabase;
    private readonly TupleHandler _tupleHandler;
    private readonly ExistentialHandler _existentialHandler;

    public ClosureHandler(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
        _tupleHandler = new TupleHandler(typeDatabase);
        _existentialHandler = new ExistentialHandler(typeDatabase);
    }

    /// <summary>Gets the type database used by this handler.</summary>
    public ITypeDatabase TypeDatabase => _typeDatabase;

    /// <summary>
    /// Determines whether the specified argument declaration represents a closure type.
    /// Also returns true for Optional closures (e.g., Optional&lt;() -&gt; Void&gt;).
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns><c>true</c> if the argument's Swift type is a closure or optional closure; otherwise, <c>false</c>.</returns>
    public bool IsClosure(ArgumentDecl argumentDecl) =>
        argumentDecl.SwiftTypeSpec is ClosureTypeSpec ||
        IsOptionalClosure(argumentDecl.SwiftTypeSpec);

    /// <summary>
    /// Determines whether the specified property declaration represents a closure type.
    /// Also returns true for Optional closures (e.g., Optional&lt;() -&gt; Void&gt;).
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns><c>true</c> if the property's Swift type is a closure or optional closure; otherwise, <c>false</c>.</returns>
    public bool IsClosure(PropertyDecl propertyDecl) =>
        propertyDecl.SwiftTypeSpec is ClosureTypeSpec ||
        IsOptionalClosure(propertyDecl.SwiftTypeSpec);

    /// <summary>
    /// Gets the ClosureTypeSpec from an argument declaration.
    /// Also extracts the closure from Optional closures.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns>The ClosureTypeSpec if the argument is a closure or optional closure; otherwise, null.</returns>
    public ClosureTypeSpec? GetClosureTypeSpec(ArgumentDecl argumentDecl) =>
        GetClosureTypeSpec(argumentDecl.SwiftTypeSpec);

    /// <summary>
    /// Gets the ClosureTypeSpec from a property declaration.
    /// Also extracts the closure from Optional closures.
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns>The ClosureTypeSpec if the property is a closure; otherwise, null.</returns>
    public ClosureTypeSpec? GetClosureTypeSpec(PropertyDecl propertyDecl) =>
        GetClosureTypeSpec(propertyDecl.SwiftTypeSpec);

    /// <summary>
    /// Gets the ClosureTypeSpec from a TypeSpec.
    /// Also extracts the closure from Optional closures.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns>The ClosureTypeSpec if the type is a closure or optional closure; otherwise, null.</returns>
    public ClosureTypeSpec? GetClosureTypeSpec(TypeSpec typeSpec)
    {
        if (typeSpec is ClosureTypeSpec closure)
            return closure;

        // Check for Optional<Closure>
        if (IsOptionalClosure(typeSpec) &&
            typeSpec is NamedTypeSpec namedType &&
            namedType.GenericParameters.Count > 0 &&
            namedType.GenericParameters[0] is ClosureTypeSpec innerClosure)
        {
            return innerClosure;
        }

        return null;
    }

    /// <summary>
    /// Determines whether the specified type is an Optional containing a closure.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns><c>true</c> if the type is Optional&lt;Closure&gt;; otherwise, <c>false</c>.</returns>
    public bool IsOptionalClosure(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        if (namedType.Name != "Swift.Optional")
            return false;

        if (namedType.GenericParameters.Count != 1)
            return false;

        return namedType.GenericParameters[0] is ClosureTypeSpec;
    }

    /// <summary>
    /// Determines whether the closure has @convention(c) attribute.
    /// @convention(c) closures are simple C function pointers with no context.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns><c>true</c> if the closure is @convention(c); otherwise, <c>false</c>.</returns>
    public bool IsConventionC(ClosureTypeSpec closureTypeSpec)
    {
        if (!closureTypeSpec.HasAttributes)
            return false;

        return closureTypeSpec.Attributes.Exists(attr =>
            attr.Name == "convention" &&
            attr.Parameters.Count > 0 &&
            attr.Parameters[0] == "c");
    }

    /// <summary>
    /// Determines whether the closure has @convention(c) attribute, using the method's mangled
    /// name as a fallback. ABI JSON does not include @convention(c) in ClosureTypeSpec attributes,
    /// but the mangled name encodes it as 'XC' (CFunctionPointer in the demangling grammar).
    /// </summary>
    /// <remarks>
    /// The mangled name fallback is only safe when the method has a single closure parameter.
    /// For methods with multiple closures, XC in the mangled name could belong to a different
    /// closure parameter. In that case, we fall back to the attribute-based check (which returns
    /// false, treating all closures as @convention(swift) — the safe default that generates
    /// correct thunks). Mixed @convention(c) + @convention(swift) methods are rare in practice.
    /// </remarks>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <param name="methodMangledName">The mangled name of the containing method.</param>
    /// <param name="closureParamCount">Number of closure parameters in the method (default 1).</param>
    /// <returns><c>true</c> if the closure is @convention(c); otherwise, <c>false</c>.</returns>
    public bool IsConventionC(ClosureTypeSpec closureTypeSpec, string methodMangledName, int closureParamCount = 1)
    {
        if (IsConventionC(closureTypeSpec))
            return true;

        // Fallback: ABI JSON doesn't include @convention(c) attributes.
        // Detect via mangled name encoding 'XC' (Swift's convention(c) marker).
        // Only safe for single-closure methods — for multi-closure methods, XC could
        // belong to a different parameter, misclassifying this one.
        if (closureParamCount > 1)
            return false;

        return ClosureEmitter.HasConventionCInMangledName(methodMangledName);
    }

    /// <summary>
    /// Determines whether the closure has @MainActor attribute.
    /// @MainActor closures must be called on the main thread/actor.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns><c>true</c> if the closure is @MainActor; otherwise, <c>false</c>.</returns>
    public bool IsMainActor(ClosureTypeSpec closureTypeSpec)
    {
        if (!closureTypeSpec.HasAttributes)
            return false;

        return closureTypeSpec.Attributes.Exists(attr =>
            attr.Name == "MainActor" ||
            attr.Name == "Swift.MainActor" ||
            attr.Name == "_Concurrency.MainActor");
    }

    /// <summary>
    /// Determines whether the closure has @Sendable attribute.
    /// @Sendable closures can be passed across concurrency domains.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns><c>true</c> if the closure is @Sendable; otherwise, <c>false</c>.</returns>
    public bool IsSendable(ClosureTypeSpec closureTypeSpec)
    {
        if (!closureTypeSpec.HasAttributes)
            return false;

        return closureTypeSpec.Attributes.Exists(attr =>
            attr.Name == "Sendable" ||
            attr.Name == "Swift.Sendable" ||
            attr.Name == "_Concurrency.Sendable");
    }

    /// <summary>
    /// Determines whether the closure is a supported type.
    /// Currently supports:
    /// - @convention(c) closures (Phase 1)
    /// - Escaping closures with concrete types (Phase 2)
    /// - Async closures (Phase 3) - mapped to Func&lt;..., Task&gt; or Func&lt;..., Task&lt;T&gt;&gt;
    /// - Throwing closures (Phase 4) - mapped to Func&lt;..., SwiftResult&lt;T, SwiftError&gt;&gt;
    /// - Async+throwing closures (Phase 28) - mapped to Func&lt;..., Task&lt;SwiftResult&lt;T, SwiftError&gt;&gt;&gt;
    ///   via Swift continuation wrapper pattern
    /// All must have concrete (non-generic) argument/return types.
    /// Return types must be primitive/blittable (complex return type marshalling not yet implemented).
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns><c>true</c> if the closure is supported; otherwise, <c>false</c>.</returns>
    public bool IsSupportedClosure(ClosureTypeSpec closureTypeSpec)
    {
        // B13 (removed Session B): async-throwing closures with arguments are now
        // supported via per-arity AsyncThrowingClosureState<A0,…,TResult> + arg-bearing
        // Start signatures. The Session B bridge only kicks in when
        // IsBaselineAsyncThrowingClosure accepts the shape; otherwise the closure still
        // has to pass the generic IsSupportedClosureParameterType loop below.

        // CX-12 (narrowed — Session C): Async-only closures with non-void returns
        // are supported only for the baseline non-throwing shape
        // (@escaping (Args) async -> T where T is a blittable primitive and args
        // are Session B-bridgeable). Anything wider still routes to the generic
        // skip path — the emitter can't synthesize a Task-returning delegate
        // under an [UnmanagedCallersOnly] callback for arbitrary shapes.
        if (closureTypeSpec.IsAsync
            && !closureTypeSpec.Throws
            && !closureTypeSpec.ReturnType.IsEmptyTuple
            && !IsBaselineAsyncNonThrowingClosure(closureTypeSpec))
            return false;

        // Async+throwing closures are now supported via Swift continuation wrapper pattern (Phase 28)
        // The C# side provides a synchronous "start" callback that spawns Task.Run,
        // while Swift uses withCheckedThrowingContinuation to create the actual async closure.
        //
        // Foundation.Data returns are supported via special byte[] marshalling:
        // 1. User provides Func<Task<Swift.Foundation.Data>>
        // 2. C# awaits the task, calls result.ToByteArray()
        // 3. Pins bytes and calls Swift's success callback with (boxPtr, dataPtr, length)
        // 4. Swift copies the bytes to create a new Data object

        // Plain throwing closures are supported - mapped to SwiftResult<T, SwiftError>
        // Plain async closures are supported via Task-based delegates

        // Note: We no longer check for explicit @escaping attribute here.
        // All closures in public Swift APIs are either @convention(c) or @escaping by definition,
        // since non-escaping closures cannot cross API boundaries. The ABI JSON doesn't include
        // these attributes in the printedName field, so we treat all non-async, non-throwing
        // closures as supported (either @convention(c) or implicitly @escaping).

        // Check that all argument types are supported
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (!IsSupportedClosureParameterType(arg))
                return false;
        }

        // Check that return type is supported
        if (!closureTypeSpec.ReturnType.IsEmptyTuple)
        {
            if (!IsSupportedClosureParameterType(closureTypeSpec.ReturnType))
                return false;

            // Closures with return types that require complex marshalling are not yet supported.
            // This includes bound generic types (like Optional<T>, Result<T,E>) and types
            // requiring memory management (like SwiftString). These need native buffer
            // allocation and marshalling which isn't implemented for return values yet.
            if (!IsSupportedClosureReturnType(closureTypeSpec.ReturnType))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Determines if a closure return type requires indirect return marshalling.
    /// Indirect return is needed for non-blittable types that cannot be returned
    /// directly from [UnmanagedCallersOnly] callbacks.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>True if the closure return type requires indirect marshalling.</returns>
    public bool RequiresIndirectReturnMarshalling(ClosureTypeSpec closureTypeSpec)
    {
        if (closureTypeSpec.ReturnType.IsEmptyTuple)
            return false;

        if (closureTypeSpec.ReturnType is NamedTypeSpec namedType)
        {
            // Pointer returns use direct pointer ABI.
            if (IsPointerType(namedType))
                return false;

            // Bound generic return types require indirect marshalling
            if (namedType.ContainsGenericParameters)
                return true;

            // Bare generic type parameters (τ_0_0, T, U, ...) are not module-qualified
            // and can't be resolved through the type database. Mirrors the guard in
            // IsFrozenStruct / IsNonFrozenStruct so SwiftTypeName.FromModuleQualifiedName
            // doesn't throw when this helper is called outside the fully prevalidated path.
            if (IsGenericTypeParameter(namedType.Name) || !namedType.HasModule())
                return false;

            // Struct returns that cannot be returned directly from callbacks
            // (for example non-frozen structs) use indirect return marshalling.
            if (!CanUseDirectCallbackReturn(closureTypeSpec.ReturnType))
            {
                var nonDirectBaseType = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (_typeDatabase.TryGetTypeRecord(nonDirectBaseType, out var nonDirectRecord) &&
                    nonDirectRecord.Kind == TypeRecordKind.Struct)
                {
                    return true;
                }
            }

            // Check if type requires memory management
            var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (_typeDatabase.TryGetTypeRecord(baseTypeName, out var typeRecord))
            {
                if ((typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether a closure callback can return this type directly from
    /// an UnmanagedCallersOnly callback method.
    /// </summary>
    /// <remarks>
    /// Direct return is allowed ONLY for blittable Swift primitives (Int/Double/etc.).
    /// Frozen structs cannot be returned directly because Swift's @convention(c) does not
    /// accept non-C-representable types as return values — the wrapper fails compilation
    /// and gets silently stripped. All non-primitive types use pointer-based return handling.
    /// </remarks>
    public bool CanUseDirectCallbackReturn(TypeSpec returnTypeSpec)
    {
        if (returnTypeSpec.IsEmptyTuple)
            return false;

        if (returnTypeSpec is not NamedTypeSpec namedType)
            return false;

        if (namedType.ContainsGenericParameters)
            return false;

        // Only primitives are returned directly. Frozen structs go through indirect return
        // (pointer-based) because @convention(c) cannot return Swift struct types.
        return GetBlittablePrimitiveType(namedType.Name) != null;
    }

    /// <summary>
    /// Checks if a return type is supported for closure callbacks.
    /// Now supports bound generic return types via indirect return marshalling.
    /// </summary>
    private bool IsSupportedClosureReturnType(TypeSpec typeSpec)
    {
        // Existential return types supported
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            return protocolList != null && _existentialHandler.IsSupportedExistential(protocolList);
        }

        if (typeSpec is NamedTypeSpec namedType)
        {
            // Pointer types are supported (map to IntPtr)
            if (IsPointerType(namedType))
                return true;

            // Bound generic return types are now supported via indirect return marshalling
            if (namedType.ContainsGenericParameters)
            {
                // Check that the base type is in the database
                var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (!_typeDatabase.TryGetTypeRecord(baseTypeName, out _))
                    return false;

                // Recursively check all generic parameters are supported
                foreach (var genericParam in namedType.GenericParameters)
                {
                    // Existential generic parameters (e.g., Optional<any Protocol>) are NOT supported
                    // for closure return types because the emitter can't marshal void* back to the
                    // bound generic type (e.g., SwiftOptional<ExistentialContainer1>)
                    if (_existentialHandler.IsExistential(genericParam))
                        return false;

                    if (!IsSupportedClosureParameterType(genericParam))
                        return false;

                    // B7: Generic parameters that require memory management (e.g., classes inside Optional)
                    // The P/Invoke uses void* for these but the C# delegate expects the actual struct.
                    // Exception: Swift.String — supported via indirect return (void* + SwiftMarshal).
                    // This enables Optional<String> and [String] closure returns.
                    if (genericParam is NamedTypeSpec innerNamed && !innerNamed.ContainsGenericParameters && innerNamed.HasModule())
                    {
                        // Use the NamedTypeSpec overload so ObjC-bridged types from auto-bridge
                        // Apple modules (e.g. PassKit classes) resolve via the synthetic
                        // ObjCBridged fallback — without this, Optional<PKPaymentAuthorizationResult>
                        // would bypass the Optional<Class> block because the scanned DB has no record.
                        if (_typeDatabase.TryGetTypeRecord(innerNamed, out var innerRecord) &&
                            innerRecord != TypeDatabaseExtensions.AnyType &&
                            MarshallingHelpers.RequiresMemoryManagement(innerRecord))
                        {
                            // Allow Swift.String through — the callback uses indirect return
                            // via void* + SwiftMarshal for Optional<String> and [String] returns.
                            if (MarshallingHelpers.IsSwiftString(genericParam))
                                continue;
                            return false;
                        }
                    }
                }
                return true;
            }

            // D1: Complex enums as closure RETURN types are not supported.
            // GetSwiftReturnConversion and RequiresIndirectReturnMarshalling don't handle
            // complex enum returns. Only complex enum parameters are supported (via heap alloc).
            if (IsComplexEnum(typeSpec))
                return false;

            // ObjC-bridged class returns are not supported as closure returns. The closure-invoker
            // emitter reconstructs a C# class from the raw pointer via `new Type(SwiftHandle)`,
            // which is the Swift-class pattern. ObjC-bridged types don't expose that constructor
            // (they take NSCoder / IntPtr factories instead), so the generated code fails to
            // compile. They remain supported as closure PARAMETERS — e.g. PassKit completion
            // handlers `(PKPaymentAuthorizationResult) -> Void` pass pointers directly.
            if (IsObjCBridgedClass(typeSpec))
                return false;

            return true;
        }

        // Tuples as return types - check all elements
        if (typeSpec is TupleTypeSpec tuple)
        {
            foreach (var element in tuple.Elements)
            {
                if (!IsSupportedClosureReturnType(element))
                    return false;
            }
            // Closure-specific: reject tuples where P/Invoke element type differs from C# type.
            // The callback returns ValueTuple<PInvokeType,...> but del() returns ValueTuple<CSharpType,...>.
            // Existential elements cause mismatches (ExistentialContainer vs object/interface).
            if (_tupleHandler.HasClosureUnsafeTupleElements(tuple))
                return false;
            return true;
        }

        return true;
    }

    /// <summary>
    /// Determines whether the closure requires a thunk (callback) function.
    /// @convention(c) closures don't need thunks - delegates can be passed directly as function pointers.
    /// Escaping closures need thunks to handle the context parameter.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns><c>true</c> if the closure requires a thunk; otherwise, <c>false</c>.</returns>
    public bool RequiresThunk(ClosureTypeSpec closureTypeSpec)
    {
        return !IsConventionC(closureTypeSpec);
    }

    /// <summary>
    /// Determines whether the closure requires a thunk, using the method's mangled name
    /// as fallback for @convention(c) detection.
    /// </summary>
    public bool RequiresThunk(ClosureTypeSpec closureTypeSpec, string methodMangledName, int closureParamCount = 1)
    {
        return !IsConventionC(closureTypeSpec, methodMangledName, closureParamCount);
    }

    /// <summary>
    /// Checks if a type is supported as a closure parameter or return type.
    /// </summary>
    private bool IsSupportedClosureParameterType(TypeSpec typeSpec)
    {
        // Closures within closures are not supported yet
        if (typeSpec is ClosureTypeSpec)
            return false;

        // Existential types (any Protocol) are supported
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            return protocolList != null && _existentialHandler.IsSupportedExistential(protocolList);
        }

        // Tuples are supported if they meet TupleHandler's criteria and all elements are closure-safe
        if (typeSpec is TupleTypeSpec tuple && !tuple.IsEmptyTuple)
        {
            if (!_tupleHandler.IsSupportedTuple(tuple))
                return false;
            // Recursively check each element for general closure support
            foreach (var element in tuple.Elements)
            {
                if (!IsSupportedClosureParameterType(element))
                    return false;
            }
            // Closure-specific: reject tuples where P/Invoke element type differs from C# delegate type.
            // Direct non-blittable params use void* with SwiftMarshal conversion, but tuple elements
            // use ValueTuple<PInvokeType,...> vs ValueTuple<CSharpType,...> — type mismatch at invocation.
            if (_tupleHandler.HasClosureUnsafeTupleElements(tuple))
                return false;
            return true;
        }

        // Named types should be resolvable in the type database
        if (typeSpec is NamedTypeSpec namedType)
        {
            // D1: Complex enums pass via heap-allocated pointer ABI (UnsafeMutableRawPointer/IntPtr).
            // Simple enums pass as their underlying integer type (blittable).
            // Both are now supported as closure parameters.
            // Generic type parameters (τ_0_0, τ_0_1, T, etc.) are not supported in closures
            // because their concrete types aren't known at binding generation time
            if (IsGenericTypeParameter(namedType.Name))
                return false;

            // Pointer types are always supported
            if (IsPointerType(namedType))
                return true;

            // Generic types require special handling
            if (namedType.ContainsGenericParameters)
            {
                if (!IsSupportedGenericType(namedType))
                    return false;
            }
            else
            {
                // Types without a module qualifier are not supported (except pointer types handled above)
                if (!namedType.HasModule())
                    return false;

                // Use the NamedTypeSpec overload so ObjC-bridged types from auto-bridge
                // Apple modules (e.g. PassKit classes like PKPaymentAuthorizationResult)
                // resolve via the synthetic ObjCBridged fallback in TypeDatabaseExtensions.
                if (!_typeDatabase.TryGetTypeRecord(namedType, out var closureTypeRecord))
                    return false;

                // The NamedTypeSpec overload short-circuits several non-real types
                // (Swift.Any, Swift.AnyObject, metatypes, unsupported Apple modules) to
                // TypeDatabaseExtensions.AnyType. Those are NOT supported as closure params
                // — downstream callback emission would produce SwiftMarshal calls against
                // Swift.AnyType, which throws at metadata lookup. Reject them here.
                if (closureTypeRecord == TypeDatabaseExtensions.AnyType)
                    return false;

                // Reject bare generic types (e.g., Dictionary without <K,V>) — they resolve
                // to SwiftDictionary but produce CS0305 without type arguments
                if (TypeDatabaseExtensions.IsBareGenericTypeName(closureTypeRecord.CSharpTypeName.FullyQualifiedName))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if a type name represents a generic type parameter.
    /// Swift generic type parameters appear as τ_0_0, τ_0_1, etc., or as simple names like T, U, Element.
    /// Delegates to the shared TypeSpecHelpers.IsGenericTypeParameter method.
    /// </summary>
    private static bool IsGenericTypeParameter(string typeName) =>
        TypeSpecHelpers.IsGenericTypeParameter(typeName);

    /// <summary>
    /// Returns true if the closure's return type or any argument is a generic type parameter
    /// (e.g., tau_0_0, T). This detects closures like <c>(Database) throws -> tau_0_0</c>
    /// that cannot be emitted with concrete types.
    /// </summary>
    public static bool HasGenericTypeParameters(ClosureTypeSpec closureTypeSpec)
    {
        // Check return type
        if (closureTypeSpec.ReturnType is NamedTypeSpec returnNamed &&
            IsGenericTypeParameter(returnNamed.Name))
            return true;

        // Check each argument
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (arg is NamedTypeSpec argNamed && IsGenericTypeParameter(argNamed.Name))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the set of generic type parameter names used in the closure's arguments and return type.
    /// For example, <c>(Database) throws -> tau_0_0</c> returns <c>{ "tau_0_0" }</c>.
    /// </summary>
    public static HashSet<string> GetGenericParamNames(ClosureTypeSpec closureTypeSpec)
    {
        var names = new HashSet<string>();

        if (closureTypeSpec.ReturnType is NamedTypeSpec returnNamed &&
            IsGenericTypeParameter(returnNamed.Name))
            names.Add(returnNamed.Name);

        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (arg is NamedTypeSpec argNamed && IsGenericTypeParameter(argNamed.Name))
                names.Add(argNamed.Name);
        }

        return names;
    }

    /// <summary>
    /// Determines whether a method with a generic closure parameter is eligible for the
    /// monomorphized Swift wrapper bridge pattern (Pattern A: sync, method-generic, noescape).
    /// <para>
    /// Eligible methods must satisfy ALL of the following:
    /// (a) Closure has a generic return type (or generic params) that maps to the method's own generic signature.
    /// (b) Closure is noescape (not @escaping).
    /// (c) All non-generic closure params pass existing IsSupportedClosureParameterType.
    /// (d) The method's return type is the SAME generic parameter as the closure's return type
    ///     (identity-forwarding — ensures T=UnsafeMutableRawPointer specialization is safe).
    /// (e) The method's generic signature has no where clauses constraining T
    ///     (UnsafeMutableRawPointer cannot satisfy protocol conformance constraints).
    /// (f) The method is not async (async generic closures are out of scope).
    /// </para>
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <param name="methodDecl">The containing method declaration.</param>
    /// <returns>True if the method is eligible for the generic closure bridge pattern.</returns>
    public bool IsMethodGenericClosureEligible(ClosureTypeSpec closureTypeSpec, MethodDecl methodDecl)
    {
        // (a) Closure must have generic type parameters
        if (!HasGenericTypeParameters(closureTypeSpec))
            return false;

        // (b) Closure must be noescape (not @escaping)
        if (closureTypeSpec.IsEscaping)
            return false;

        // (b2) Closure must throw — the generated Swift closure body unconditionally contains
        // error propagation (`throw unsafeBitCast(err, to: Swift.Error.self)`), which is
        // invalid Swift for non-throwing closures.
        if (!closureTypeSpec.Throws)
            return false;

        // (c) All non-generic closure arguments must be supported, and generic type parameters
        // must NOT appear in argument position. The cdecl callback signature is built from
        // ALL closure args (each becomes a void*), but the C# closureArgTypes list only
        // includes concrete types (skipping generic params). This creates an ABI mismatch
        // where Swift passes more void* args than C# expects. Gate out until we properly
        // count generic args in the C# callback.
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (arg is NamedTypeSpec argNamed && IsGenericTypeParameter(argNamed.Name))
                return false; // Generic args in input position — ABI mismatch (P0)
            if (!IsSupportedClosureParameterType(arg))
                return false;
            // (c2) Concrete closure args must be reference types (classes). The Swift wrapper
            // converts them via `Unmanaged.passUnretained(param as AnyObject).toOpaque()`
            // which only works for class types. Value types (Int, Bool, frozen structs)
            // would be incorrectly boxed. If no TypeRecord exists, reject — we can't
            // verify the type is a class.
            if (arg is NamedTypeSpec concreteNamed)
            {
                if (!_typeDatabase.TryGetTypeRecord(arg, out var argRecord) ||
                    argRecord.Kind != TypeRecordKind.Class)
                    return false;
            }
        }

        // (d) Identity-forwarding return: the method's return type must be the same
        // generic parameter as the closure's return type. This ensures T=UnsafeMutableRawPointer
        // specialization is safe — the method just passes through whatever the closure returns.
        var closureGenericParams = GetGenericParamNames(closureTypeSpec);
        if (closureGenericParams.Count == 0)
            return false;

        // The closure's return type must be a generic type parameter
        if (closureTypeSpec.ReturnType is not NamedTypeSpec closureReturnNamed ||
            !IsGenericTypeParameter(closureReturnNamed.Name))
            return false;

        // The method must return the same generic parameter
        if (methodDecl.CSSignature.Count == 0)
            return false;
        var methodReturnTypeSpec = methodDecl.CSSignature[0].SwiftTypeSpec;
        if (methodReturnTypeSpec is not NamedTypeSpec methodReturnNamed ||
            methodReturnNamed.Name != closureReturnNamed.Name)
        {
            // Also allow void methods with void closure return (side-effect pattern)
            // But that case is handled differently — the generic closure return must be a type param
            return false;
        }

        // The generic param must belong to the method's own generic signature (not type-level)
        if (!methodDecl.IsGeneric)
            return false;

        var methodGenericParamNames = new HashSet<string>();
        foreach (var gp in methodDecl.GenericParameters)
        {
            methodGenericParamNames.Add(gp.TypeName);
            methodGenericParamNames.Add(gp.SugaredTypeName);
        }

        // All generic params used in the closure must map to method-level generic params
        foreach (var paramName in closureGenericParams)
        {
            if (!methodGenericParamNames.Contains(paramName))
                return false;
        }

        // (e) No constraints on the generic parameter (UnsafeMutableRawPointer can't conform to protocols)
        foreach (var gp in methodDecl.GenericParameters)
        {
            if (closureGenericParams.Contains(gp.TypeName) || closureGenericParams.Contains(gp.SugaredTypeName))
            {
                if (gp.GenericConformances.Count > 0)
                    return false;
            }
        }

        // (f) Method must not be async
        if (methodDecl.IsAsync)
            return false;

        return true;
    }

    /// <summary>
    /// Checks if a generic type is supported in closures.
    /// Supports pointer types and bound generic types whose base type is in the type database.
    /// </summary>
    private bool IsSupportedGenericType(NamedTypeSpec namedType)
    {
        // Pointer types always supported - they map to IntPtr
        if (IsPointerType(namedType))
            return true;

        // Check if base type is in type database
        var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(baseTypeName, out _))
            return false;

        // Recursively check all generic parameters are supported
        foreach (var genericParam in namedType.GenericParameters)
        {
            // Handle existential generic parameters (e.g., Optional<any Protocol>)
            if (_existentialHandler.IsExistential(genericParam))
            {
                var protocolList = _existentialHandler.ToProtocolListTypeSpec(genericParam);
                if (protocolList == null || !_existentialHandler.IsSupportedExistential(protocolList))
                    return false;
                continue; // This parameter is valid
            }

            if (!IsSupportedClosureParameterType(genericParam))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Translates a Swift closure type to a C# delegate type string for wrapper methods.
    /// Async closures are mapped to Func&lt;..., Task&gt; or Func&lt;..., Task&lt;T&gt;&gt;.
    /// Throwing closures are mapped to Func&lt;..., SwiftResult&lt;T, SwiftError&gt;&gt;.
    /// Async+throwing closures are mapped to Func&lt;..., Task&lt;SwiftResult&lt;T, SwiftError&gt;&gt;&gt;.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The C# delegate type name (Action&lt;&gt; or Func&lt;&gt;).</returns>
    public string GetCSharpDelegateType(ClosureTypeSpec closureTypeSpec)
    {
        var argTypes = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            argTypes.Add(TranslateTypeSpecToCSharp(arg));
        }

        bool hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        bool isAsync = closureTypeSpec.IsAsync;
        bool throws = closureTypeSpec.Throws;

        // Determine the core return type
        string coreReturnType;
        if (hasReturn)
        {
            coreReturnType = TranslateTypeSpecToCSharp(closureTypeSpec.ReturnType, isReturnType: true);
        }
        else
        {
            // For void returns, use SwiftVoid as the success type in throwing closures
            coreReturnType = throws ? "Swift.SwiftVoid" : null!;
        }

        // Build the final return type based on async and throws modifiers.
        // BCL types are globally qualified so Swift types with matching names
        // (e.g., TipKit.Tips.Action) can't shadow them in nested scopes.
        string finalReturnType;
        if (isAsync && throws)
        {
            // Async+throwing closures: error handling is via Swift continuation callback,
            // NOT via SwiftResult return type. User's delegate returns Task<T> and
            // exceptions are caught and forwarded to Swift's error callback.
            finalReturnType = hasReturn
                ? $"global::System.Threading.Tasks.Task<{coreReturnType}>"
                : "global::System.Threading.Tasks.Task";
        }
        else if (throws)
        {
            // Throwing closures (non-async) wrap in SwiftResult<T, SwiftError>
            var resultType = hasReturn
                ? $"Swift.SwiftResult<{coreReturnType}, SwiftError>"
                : $"Swift.SwiftResult<Swift.SwiftVoid, SwiftError>";
            finalReturnType = resultType;
        }
        else if (isAsync)
        {
            // Async only: Task or Task<T>
            finalReturnType = hasReturn
                ? $"global::System.Threading.Tasks.Task<{coreReturnType}>"
                : "global::System.Threading.Tasks.Task";
        }
        else
        {
            // Non-async, non-throwing
            finalReturnType = coreReturnType;
        }

        // Generate delegate type
        if (finalReturnType == null)
        {
            // Non-throwing, non-async void -> Action
            if (argTypes.Count == 0)
                return "global::System.Action";
            return $"global::System.Action<{string.Join(", ", argTypes)}>";
        }
        else
        {
            // All other cases -> Func
            if (argTypes.Count == 0)
                return $"global::System.Func<{finalReturnType}>";
            return $"global::System.Func<{string.Join(", ", argTypes)}, {finalReturnType}>";
        }
    }

    /// <summary>
    /// Determines if a closure is a throwing closure.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>True if the closure throws.</returns>
    public bool IsThrowingClosure(ClosureTypeSpec closureTypeSpec)
    {
        return closureTypeSpec.Throws;
    }

    /// <summary>
    /// Determines if a closure is both async and throwing.
    /// Async+throwing closures require special handling via Swift continuation wrappers.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>True if the closure is both async and throws.</returns>
    public bool IsAsyncThrowingClosure(ClosureTypeSpec closureTypeSpec)
    {
        return closureTypeSpec.IsAsync && closureTypeSpec.Throws;
    }

    /// <summary>
    /// Determines whether an async-throwing closure matches the Session A/B/D/F bridge
    /// shape: `@escaping (A0, …) async throws -> T` where:
    ///   - T is a bitwise-copyable primitive (Int32, Int64, Double, …) OR
    ///     <c>Foundation.Data</c> (Session D: routed through <c>DataAsyncClosureHelper</c>
    ///     with a <c>(boxPtr, bytesPtr, length)</c> success callback; zero args only
    ///     per the Data-return emitter guard in <c>ClosureEmitter.Async.cs</c>) OR
    ///     <c>Swift.String</c> (Session F: routed through <c>StringAsyncClosureHelper</c>
    ///     with a <c>(boxPtr, bytesPtr, length)</c> UTF-8 success callback; full
    ///     0–<see cref="MaxAsyncThrowingClosureArity"/> arity supported since it
    ///     unblocks <c>STPConfirmationToken</c>-shaped handlers).
    ///   - arity is 0–<see cref="MaxAsyncThrowingClosureArity"/>,
    ///   - each argument is a Session B-bridgeable type (primitive, Swift.String,
    ///     or a Swift class).
    /// Any wider shape falls through to the existing "unsupported async closure"
    /// skip path.
    /// </summary>
    public bool IsBaselineAsyncThrowingClosure(ClosureTypeSpec closureTypeSpec)
    {
        if (!closureTypeSpec.IsAsync || !closureTypeSpec.Throws)
            return false;
        if (closureTypeSpec.ReturnType is not NamedTypeSpec namedReturn || namedReturn.ContainsGenericParameters)
            return false;

        var args = closureTypeSpec.EachArgument().ToList();
        bool isDataReturn = namedReturn.Name == "Foundation.Data";
        bool isStringReturn = namedReturn.Name == "Swift.String";
        if (isDataReturn)
        {
            // DataAsyncClosureHelper currently only supports zero-arg closures
            // (see NotSupportedException guard in ClosureEmitter.Async.cs).
            if (args.Count > 0)
                return false;
        }
        else if (isStringReturn)
        {
            // StringAsyncClosureHelper supports the full arity range.
        }
        else if (!CdeclParamMapper.IsBlittablePrimitiveSwiftType(namedReturn.Name))
        {
            return false;
        }

        if (args.Count > MaxAsyncThrowingClosureArity)
            return false;
        foreach (var arg in args)
        {
            if (GetAsyncThrowingArgCategory(arg) == AsyncThrowingArgCategory.Unsupported)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Session C counterpart of <see cref="IsBaselineAsyncThrowingClosure"/> for
    /// non-throwing closures: <c>@escaping (A0, …) async -&gt; T</c> where T is a
    /// blittable primitive, arity is 0–<see cref="MaxAsyncThrowingClosureArity"/>,
    /// and each arg matches a Session B category. The non-throwing bridge uses
    /// <c>withCheckedContinuation</c> (no error channel) on the Swift side and
    /// routes C# exceptions to <c>Environment.FailFast</c>.
    /// </summary>
    public bool IsBaselineAsyncNonThrowingClosure(ClosureTypeSpec closureTypeSpec)
    {
        if (!closureTypeSpec.IsAsync || closureTypeSpec.Throws)
            return false;
        if (closureTypeSpec.ReturnType.IsEmptyTuple)
            return false;
        if (closureTypeSpec.ReturnType is not NamedTypeSpec namedReturn || namedReturn.ContainsGenericParameters)
            return false;
        if (!CdeclParamMapper.IsBlittablePrimitiveSwiftType(namedReturn.Name))
            return false;

        var args = closureTypeSpec.EachArgument().ToList();
        if (args.Count > MaxAsyncThrowingClosureArity)
            return false;
        foreach (var arg in args)
        {
            if (GetAsyncThrowingArgCategory(arg) == AsyncThrowingArgCategory.Unsupported)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Combined predicate that accepts either the Session A/B throwing baseline
    /// shape or the Session C non-throwing baseline shape. Call sites that
    /// previously tested <see cref="IsBaselineAsyncThrowingClosure"/> typically
    /// want this — the emitter paths are unified past the <c>isThrowing</c>
    /// branch.
    /// </summary>
    public bool IsBaselineAsyncClosure(ClosureTypeSpec closureTypeSpec)
        => IsBaselineAsyncThrowingClosure(closureTypeSpec)
           || IsBaselineAsyncNonThrowingClosure(closureTypeSpec);

    /// <summary>
    /// Maximum closure arity supported by the Session B async-throwing bridge.
    /// Per-arity state/helper overloads live in <c>Swift.Runtime.AsyncThrowingClosureState</c>
    /// and <c>Swift.Runtime.AsyncClosureHelper</c>; raising this cap requires adding
    /// matching per-arity types there.
    /// </summary>
    public const int MaxAsyncThrowingClosureArity = 4;

    /// <summary>
    /// Category for a closure argument that participates in the Session B async-throwing
    /// bridge. Drives both the Swift-side adapter marshalling and the C# Start thunk
    /// arg-read code. Non-frozen structs, Optionals, and generics are intentionally
    /// excluded from the baseline bridge.
    /// </summary>
    public enum AsyncThrowingArgCategory
    {
        Unsupported = 0,
        Primitive,
        SwiftString,
        SwiftClass,
    }

    /// <summary>
    /// Returns the <see cref="AsyncThrowingArgCategory"/> for a closure argument. Any
    /// shape outside the three supported categories reports <see cref="AsyncThrowingArgCategory.Unsupported"/>,
    /// which forces the closure back onto the generic "unsupported async closure" path.
    /// </summary>
    public AsyncThrowingArgCategory GetAsyncThrowingArgCategory(TypeSpec argSpec)
    {
        if (argSpec is not NamedTypeSpec named || named.ContainsGenericParameters)
            return AsyncThrowingArgCategory.Unsupported;
        // Use the blittable-primitive subset (Bool excluded) — the C# Start thunk is
        // [UnmanagedCallersOnly], which forbids non-blittable C# types on the ABI.
        // Bool support is a follow-up; the existing closure skip path handles it.
        if (CdeclParamMapper.IsBlittablePrimitiveSwiftType(named.Name))
            return AsyncThrowingArgCategory.Primitive;
        if (named.Name == "Swift.String")
            return AsyncThrowingArgCategory.SwiftString;
        // IsClassType already excludes ObjCBridged/ObjCRooted — the Start thunk
        // reads SwiftClass args with Arc.Retain + SwiftMarshal.MarshalFromSwift<T>,
        // which is wrong for ObjC projections (Handle/GetNSObject / ObjCRootedClassProjection).
        if (IsClassType(named)
            && _typeDatabase.TryGetTypeRecord(named, out var record)
            && !TypeDatabaseExtensions.IsBareGenericTypeName(record.CSharpTypeName.FullyQualifiedName))
        {
            return AsyncThrowingArgCategory.SwiftClass;
        }
        return AsyncThrowingArgCategory.Unsupported;
    }

    /// <summary>
    /// Gets the P/Invoke function pointer type for an async+throwing closure's "start" function.
    /// The start function is called synchronously by Swift and spawns the async work via Task.Run.
    /// Signature: (contextPtr, continuationBoxPtr, A0_raw, A1_raw, …, successCallbackPtr, errorCallbackPtr) -> void
    /// where each A_raw is the C# ABI scalar for the closure arg (primitive) or IntPtr (String/class).
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The unmanaged function pointer type string for the start function.</returns>
    public string GetAsyncThrowingStartFunctionPointerType(ClosureTypeSpec closureTypeSpec)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("delegate* unmanaged[Cdecl]<IntPtr, IntPtr");
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            sb.Append(", ");
            sb.Append(GetAsyncThrowingArgCSharpAbiType(arg));
        }
        sb.Append(", IntPtr, IntPtr, void>");
        return sb.ToString();
    }

    /// <summary>
    /// Returns the C# ABI type used for a single async-throwing closure arg in the
    /// <c>[UnmanagedCallersOnly]</c> Start thunk signature. Must stay in sync with
    /// <see cref="GetAsyncThrowingArgCategory"/> so C# signatures match the Swift
    /// <c>@convention(c)</c> startFunc type that the Swift adapter casts to.
    /// </summary>
    public string GetAsyncThrowingArgCSharpAbiType(TypeSpec argSpec)
    {
        var category = GetAsyncThrowingArgCategory(argSpec);
        return category switch
        {
            AsyncThrowingArgCategory.Primitive => GetPrimitiveCSharpAbiType((NamedTypeSpec)argSpec),
            AsyncThrowingArgCategory.SwiftString => "IntPtr",
            AsyncThrowingArgCategory.SwiftClass => "IntPtr",
            _ => throw new InvalidOperationException(
                $"Cannot emit async-throwing closure Start thunk: arg '{argSpec}' is not a supported category.")
        };
    }

    /// <summary>
    /// Returns the user-facing C# type for an async-throwing closure arg (what the
    /// caller-provided <c>Func&lt;…, Task&lt;T&gt;&gt;</c> expects). Primitives use
    /// the scalar C# type; Swift.String becomes managed <c>string</c>; Swift classes
    /// use their generated C# class type via <see cref="TranslateTypeSpecToCSharp"/>.
    /// </summary>
    public string GetAsyncThrowingArgPublicCSharpType(TypeSpec argSpec)
    {
        var category = GetAsyncThrowingArgCategory(argSpec);
        return category switch
        {
            AsyncThrowingArgCategory.Primitive => GetPrimitiveCSharpAbiType((NamedTypeSpec)argSpec),
            AsyncThrowingArgCategory.SwiftString => "string",
            AsyncThrowingArgCategory.SwiftClass => TranslateTypeSpecToCSharp(argSpec),
            _ => throw new InvalidOperationException(
                $"Cannot project async-throwing closure arg '{argSpec}' to a public C# type.")
        };
    }

    /// <summary>
    /// Emits the Start-thunk sync-marshal statements that produce a managed variable
    /// <paramref name="managedVar"/> of type <see cref="GetAsyncThrowingArgPublicCSharpType"/>
    /// from the raw ABI-typed parameter <paramref name="rawVar"/>. Called before
    /// <c>Task.Run</c> so the resulting value is safe to capture. For Swift classes
    /// the helper retains via <c>Arc.Retain</c> and wraps the pointer in an owning
    /// SafeHandle; borrowed lifetime would dangle the moment Start returns.
    /// </summary>
    public string GetAsyncThrowingArgSyncMarshalStatements(TypeSpec argSpec, string rawVar, string managedVar)
    {
        var category = GetAsyncThrowingArgCategory(argSpec);
        return category switch
        {
            AsyncThrowingArgCategory.Primitive =>
                $"var {managedVar} = {rawVar};",
            AsyncThrowingArgCategory.SwiftString =>
                $"var {managedVar} = SwiftMarshal.MarshalBorrowedFromSwift<Swift.SwiftString>({rawVar}).ToString();",
            AsyncThrowingArgCategory.SwiftClass =>
                // Matches the sync SwiftResult class-extraction pattern: Arc.Retain pins
                // the object for C#, MarshalFromSwift wraps into an owning SafeHandle
                // whose Release fires when the capturing Task completes.
                $"Swift.Runtime.Arc.Retain({rawVar});\n"
                + $"var {managedVar} = SwiftMarshal.MarshalFromSwift<{TranslateTypeSpecToCSharp(argSpec)}>({rawVar});",
            _ => throw new InvalidOperationException(
                $"Cannot emit async-throwing closure Start-thunk marshal for arg '{argSpec}'.")
        };
    }

    /// <summary>
    /// Returns the C# scalar type for a Swift primitive arg in the Start thunk
    /// (e.g. <c>Swift.Int32</c> → <c>int</c>, <c>Swift.Double</c> → <c>double</c>).
    /// </summary>
    private static string GetPrimitiveCSharpAbiType(NamedTypeSpec named) => named.Name switch
    {
        "Swift.Int" or "Int" => "nint",
        "Swift.UInt" or "UInt" => "nuint",
        "Swift.Int8" or "Int8" => "sbyte",
        "Swift.UInt8" or "UInt8" => "byte",
        "Swift.Int16" or "Int16" => "short",
        "Swift.UInt16" or "UInt16" => "ushort",
        "Swift.Int32" or "Int32" => "int",
        "Swift.UInt32" or "UInt32" => "uint",
        "Swift.Int64" or "Int64" => "long",
        "Swift.UInt64" or "UInt64" => "ulong",
        "Swift.Float" or "Float" => "float",
        "Swift.Double" or "Double" => "double",
        "CoreFoundation.CGFloat" or "CGFloat" => "double",
        _ => throw new InvalidOperationException($"Unrecognised primitive arg type '{named.Name}'")
    };

    /// <summary>
    /// Gets the Swift success callback signature for an async+throwing closure.
    /// The success callback is called by C# when the async work completes successfully.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The Swift @convention(c) callback signature.</returns>
    public string GetAsyncThrowingSuccessCallbackSwiftSignature(ClosureTypeSpec closureTypeSpec)
    {
        var returnType = closureTypeSpec.ReturnType;
        if (returnType.IsEmptyTuple)
        {
            // Void return: just boxPtr
            return "@convention(c) (UnsafeMutableRawPointer) -> Void";
        }

        // For Data return type (most common case for async data loaders)
        if (returnType is NamedTypeSpec namedType && namedType.Name == "Foundation.Data")
        {
            return "@convention(c) (UnsafeMutableRawPointer, UnsafePointer<UInt8>, Int) -> Void";
        }

        // Generic case - use opaque pointer for result
        return "@convention(c) (UnsafeMutableRawPointer, UnsafeRawPointer) -> Void";
    }

    /// <summary>
    /// Gets the Swift error callback signature for an async+throwing closure.
    /// The error callback is called by C# when the async work throws an exception.
    /// </summary>
    /// <returns>The Swift @convention(c) callback signature.</returns>
    public string GetAsyncThrowingErrorCallbackSwiftSignature()
    {
        // Error callback: (boxPtr, errorMessage) -> Void
        return "@convention(c) (UnsafeMutableRawPointer, UnsafePointer<CChar>) -> Void";
    }

    /// <summary>
    /// Gets the C# delegate type for an optional closure.
    /// Returns the nullable delegate type (e.g., "Func&lt;Task&gt;?").
    /// </summary>
    /// <param name="typeSpec">The type specification (should be Optional&lt;Closure&gt;).</param>
    /// <returns>The nullable C# delegate type name.</returns>
    public string GetCSharpOptionalDelegateType(TypeSpec typeSpec)
    {
        var closureTypeSpec = GetClosureTypeSpec(typeSpec);
        if (closureTypeSpec == null)
            return "object?";

        return GetCSharpDelegateType(closureTypeSpec) + "?";
    }

    /// <summary>
    /// Determines if a closure is an async closure.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>True if the closure is async.</returns>
    public bool IsAsyncClosure(ClosureTypeSpec closureTypeSpec)
    {
        return closureTypeSpec.IsAsync;
    }

    /// <summary>
    /// Gets the P/Invoke function pointer type for a closure.
    /// Uses Swift calling convention since escaping closures are called with Swift ABI.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The unmanaged function pointer type string.</returns>
    public string GetPInvokeFunctionPointerType(ClosureTypeSpec closureTypeSpec)
    {
        var argTypes = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            argTypes.Add(TranslateTypeSpecToPInvokeType(arg));
        }

        bool hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnType = hasReturn ? TranslateTypeSpecToPInvokeType(closureTypeSpec.ReturnType) : "void";

        // Use Swift calling convention for escaping closures (thick closures)
        if (argTypes.Count == 0)
            return $"delegate* unmanaged[Swift]<{returnType}>";

        return $"delegate* unmanaged[Swift]<{string.Join(", ", argTypes)}, {returnType}>";
    }

    /// <summary>
    /// Gets the P/Invoke function pointer type for a closure callback that uses indirect return.
    /// The indirect result pointer is passed as the first parameter (void*), and the callback returns void.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The unmanaged function pointer type string with indirect return.</returns>
    public string GetPInvokeFunctionPointerTypeWithIndirectReturn(ClosureTypeSpec closureTypeSpec)
    {
        var argTypes = new List<string> { "void*" }; // indirectResult first
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            argTypes.Add(TranslateTypeSpecToPInvokeType(arg));
        }

        // Use Swift calling convention, return type is always void with indirect return
        return $"delegate* unmanaged[Swift]<{string.Join(", ", argTypes)}, void>";
    }

    /// <summary>
    /// Gets the P/Invoke function pointer type for a throwing closure callback.
    /// The error pointer is passed as an additional parameter (SwiftError*), and the callback
    /// should set the error pointer if the delegate returns a failure result.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The unmanaged function pointer type string with error parameter.</returns>
    public string GetPInvokeFunctionPointerTypeWithError(ClosureTypeSpec closureTypeSpec)
    {
        var argTypes = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            argTypes.Add(TranslateTypeSpecToPInvokeType(arg));
        }

        // Add SwiftError* out parameter before SwiftSelf context
        argTypes.Add("SwiftError*");

        bool hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var returnType = hasReturn ? TranslateTypeSpecToPInvokeType(closureTypeSpec.ReturnType) : "void";

        // Use Swift calling convention
        if (argTypes.Count == 0)
            return $"delegate* unmanaged[Swift]<{returnType}>";

        return $"delegate* unmanaged[Swift]<{string.Join(", ", argTypes)}, {returnType}>";
    }

    /// <summary>
    /// Gets the P/Invoke function pointer type for a throwing closure callback that uses indirect return.
    /// Combines indirect return (void* first) with error handling (SwiftError* before context).
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The unmanaged function pointer type string with indirect return and error parameter.</returns>
    public string GetPInvokeFunctionPointerTypeWithIndirectReturnAndError(ClosureTypeSpec closureTypeSpec)
    {
        var argTypes = new List<string> { "void*" }; // indirectResult first
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            argTypes.Add(TranslateTypeSpecToPInvokeType(arg));
        }

        // Add SwiftError* out parameter before SwiftSelf context
        argTypes.Add("SwiftError*");

        // Use Swift calling convention, return type is always void with indirect return
        return $"delegate* unmanaged[Swift]<{string.Join(", ", argTypes)}, void>";
    }

    /// <summary>
    /// Translates a TypeSpec to its C# equivalent for delegate type parameters.
    /// Existential types use protocol interfaces for known protocols (with proxy),
    /// well-known runtime types (e.g., AnyError for Swift.Error), or "object" for
    /// unknown protocols. The P/Invoke layer still uses ExistentialContainer.
    /// </summary>
    public string TranslateTypeSpecToCSharp(TypeSpec typeSpec, bool isReturnType = false)
    {
        // Handle existential types — use protocol interface or object (never ExistentialContainer)
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList != null && _existentialHandler.IsSupportedExistential(protocolList))
            {
                // Well-known stdlib protocols → direct runtime type (no proxy needed)
                if (_existentialHandler.TryGetWellKnownProtocolType(protocolList, out var wellKnownType))
                    return wellKnownType;

                // Use public interface (e.g., IImageProcessing) when all protocols have TypeRecords
                // AND a proxy class exists (TryGetFilteredProxyClassName filters ObjC protocols).
                // Without a proxy class, the callback can't convert ExistentialContainer → interface.
                // P1 fix: Also exclude mixed compositions where ObjC filtering drops protocols,
                // because the proxy constructor accepts ExistentialContainer{filteredCount}
                // but P/Invoke passes ExistentialContainer{originalCount}.
                if (_existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
                {
                    var publicType = _existentialHandler.GetPublicExistentialType(protocolList);
                    // Parity with ExistentialHandler.GetEffectiveProtocols (per-module ObjC-prefix gate).
                    var filteredCount = protocolList.Protocols.Keys
                        .Count(p => !TypeDatabaseExtensions.IsObjCExistentialBridgedProtocol(p));
                    if (publicType != "object" &&
                        filteredCount == protocolList.Protocols.Count &&
                        _existentialHandler.TryGetFilteredProxyClassName(protocolList, out _))
                        return publicType;
                }
                // Both params and returns: unknown protocols → "object" instead of ExistentialContainer
                return "object";
            }
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        if (typeSpec is NamedTypeSpec namedType)
        {
            // Handle pointer types
            if (IsPointerType(namedType))
                return "IntPtr";

            // Handle Optional<T> -> use C# nullable syntax T? for simple types
            // For complex types (classes, existentials), keep Swift.SwiftOptional<T> to avoid
            // issues with closure invocation marshalling code
            if (namedType.Name == "Swift.Optional" &&
                namedType.GenericParameters.Count == 1)
            {
                var innerTypeSpec = namedType.GenericParameters[0];
                // For Optional<any Protocol>, use well-known type or container type.
                // Well-known protocols (Swift.Error) → SwiftOptional<AnyError>.
                // Other existentials use container type (not interface) because
                // Optional existentials use void* in P/Invoke → MarshalFromSwift<IProtocol?>
                // would throw NotSupportedException at runtime.
                string innerType;
                bool isWellKnownProtocol = false;
                if (_existentialHandler.IsExistential(innerTypeSpec))
                {
                    var innerProtocolList = _existentialHandler.ToProtocolListTypeSpec(innerTypeSpec);
                    if (innerProtocolList != null && _existentialHandler.TryGetWellKnownProtocolType(innerProtocolList, out var wkt))
                    {
                        innerType = wkt;
                        isWellKnownProtocol = true; // AnyError is a blittable struct → use nullable syntax
                    }
                    else if (innerProtocolList != null && _existentialHandler.IsSupportedExistential(innerProtocolList))
                        innerType = _existentialHandler.GetCSharpExistentialType(innerProtocolList);
                    else
                        innerType = TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
                }
                else
                {
                    innerType = TranslateTypeSpecToCSharp(innerTypeSpec);
                }

                // Use nullable syntax (T?) to align with protocol interface signatures.
                // TypeConversionHandler.GetIdiomaticCSharpType unconditionally uses T? for Optional,
                // so closures must match to avoid CS0535 interface implementation mismatches.
                // - Frozen structs/simple enums: Nullable<T> (distinct value type)
                // - Non-frozen structs (C# class): nullable annotation (same runtime type)
                // - Classes: nullable annotation (same runtime type)
                // Only tuples and other non-named types fall through to SwiftOptional<T>.
                if (isWellKnownProtocol ||
                    IsPrimitiveType(innerTypeSpec) ||
                    innerTypeSpec.IsEmptyTuple ||
                    IsPointerType(innerTypeSpec as NamedTypeSpec) ||
                    innerTypeSpec is NamedTypeSpec)
                {
                    return $"{innerType}?";
                }

                // For non-named types (tuples, etc.), use SwiftOptional wrapper
                return $"Swift.SwiftOptional<{innerType}>";
            }

            // Handle bound generic types (e.g., Result<T, E>, Array<T>)
            if (namedType.ContainsGenericParameters)
                return TranslateBoundGenericToCSharp(namedType);

            // Swift.String must project to "string" to match GetIdiomaticCSharpType's output.
            // Without this, closures use SwiftString while interface methods use string → CS0029.
            if (MarshallingHelpers.IsSwiftString(namedType))
                return "string";

            // Foundation.Data projects to byte[] to match DataProjection's output.
            // Without this, closures use Foundation.NSData while methods use byte[] → CS0029.
            if (namedType.Name == "Foundation.Data")
                return "byte[]";

            var typeRecord = _typeDatabase.GetTypeRecordOrAnyType(namedType);
            // Native remapped types (e.g., Foundation.Data → Foundation.NSData)
            // must use NativeTypeName to match GetIdiomaticCSharpType's output for property types.
            // Without this, closures use the Swift wrapper type while properties use the native .NET type → CS0029.
            if (typeRecord.NativeTypeName != null)
                return typeRecord.NativeTypeName.FullyQualifiedName;
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        if (typeSpec.IsEmptyTuple)
            return "void";

        // Handle tuple types — recurse through THIS translator so closure-arg-tuple
        // elements pick up the same projection rules as top-level closure args
        // (Foundation.Data → byte[], Foundation.URLResponse → Foundation.NSUrlResponse,
        // Swift.Optional<T> → T?, Swift.String → string, etc.). The default
        // overload of GetCSharpTupleType uses TupleHandler.TranslateElementTypeToCSharp
        // which short-circuits to typeRecord.CSharpTypeName.FullyQualifiedName and skips
        // every closure-specific projection — that's the
        // bug-0.10.0-callback-arg-projection-asymmetry symptom where a tuple element of
        // `Foundation.Data` came out as `Swift.Foundation.Data` and `Foundation.URLResponse?`
        // came out as `Swift.SwiftOptional<IntPtr>` even though the top-level async return
        // path (which uses the same TranslateTypeSpecToCSharp recursion) projected them to
        // `byte[]` and `Foundation.NSUrlResponse?`.
        if (typeSpec is TupleTypeSpec tupleType)
            return _tupleHandler.GetCSharpTupleType(tupleType, t => TranslateTypeSpecToCSharp(t));

        // Fallback for unsupported types
        return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Translates a bound generic NamedTypeSpec to its full C# type name with generic parameters.
    /// </summary>
    private string TranslateBoundGenericToCSharp(NamedTypeSpec namedType)
    {
        // Bound-generic SIMD aliases (Swift.SIMD3<Swift.Float> → System.Numerics.Vector3) resolve
        // to a non-generic managed type. Short-circuit before the generic-wrap path so we don't
        // emit invalid syntax like `System.Numerics.Vector3<float>` on a non-generic typealias.
        if (TypeDatabaseExtensions.TryResolveBoundGenericAlias(_typeDatabase, namedType, out var aliasRecord))
        {
            return aliasRecord.CSharpTypeName.FullyQualifiedName;
        }

        var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(baseTypeName, out var typeRecord))
        {
            // Fallback if base type not in database
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        // Pointer types like UnsafeMutablePointer<T> resolve to IntPtr which doesn't support generics
        if (typeRecord == TypeDatabaseExtensions.IntPtrType)
        {
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        // Recursively translate all generic parameters
        var translatedParams = new List<string>();
        foreach (var genericParam in namedType.GenericParameters)
        {
            // Map Swift.Void (empty tuple) to SwiftVoid for generic type arguments (B8)
            // Mirrors the B3 fix in BoundGenericsHandler.TranslateBoundGenericTypeToCSharp
            if (genericParam.IsEmptyTuple)
            {
                translatedParams.Add("Swift.SwiftVoid");
                continue;
            }

            // Handle existential generic parameters (e.g., Array<any Protocol>)
            if (_existentialHandler.IsExistential(genericParam))
            {
                var protocolList = _existentialHandler.ToProtocolListTypeSpec(genericParam);
                if (protocolList != null && _existentialHandler.IsSupportedExistential(protocolList))
                {
                    if (_existentialHandler.TryGetWellKnownProtocolType(protocolList, out var wk))
                        translatedParams.Add(wk);
                    else
                        translatedParams.Add(_existentialHandler.GetPublicExistentialType(protocolList));
                    continue;
                }
            }
            translatedParams.Add(TranslateTypeSpecToCSharp(genericParam));
        }

        // Safety net: if no generic params were translated but the base type requires them,
        // return AnyType to prevent bare generic type names like "SwiftDictionary" (CS0305)
        if (translatedParams.Count == 0 &&
            TypeDatabaseExtensions.IsBareGenericTypeName(typeRecord.CSharpTypeName.FullyQualifiedName))
        {
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        // Build full type name with generics
        return translatedParams.Count > 0
            ? $"{typeRecord.CSharpTypeName.FullyQualifiedName}<{string.Join(", ", translatedParams)}>"
            : typeRecord.CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Translates a TypeSpec to its P/Invoke equivalent type.
    /// For UnmanagedCallersOnly compatibility, only blittable types can be used directly.
    /// Non-blittable types (including those requiring memory management) use void*.
    /// </summary>
    public string TranslateTypeSpecToPInvokeType(TypeSpec typeSpec)
    {
        // Handle existential types
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList != null && _existentialHandler.IsSupportedExistential(protocolList))
                return _existentialHandler.GetPInvokeExistentialType(protocolList);
            return "void*";
        }

        if (typeSpec is NamedTypeSpec namedType)
        {
            // Handle pointer types - all map to void* or IntPtr
            if (IsPointerType(namedType))
                return "void*";

            // Check for known blittable primitive types first
            // Only these can be safely passed directly in unmanaged function pointers
            var primitiveType = GetBlittablePrimitiveType(namedType.Name);
            if (primitiveType != null)
                return primitiveType;

            // Bound generic types are passed as opaque pointers in P/Invoke
            if (namedType.ContainsGenericParameters)
                return "void*";

            // Simple enums: use underlying integer type (blittable for [UnmanagedCallersOnly])
            if (!namedType.ContainsGenericParameters && namedType.HasModule())
            {
                var enumSwiftName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (_typeDatabase.TryGetTypeRecord(enumSwiftName, out var enumRecord) &&
                    enumRecord.Kind == TypeRecordKind.Enum &&
                    (enumRecord.Flags & TypeRecordFlags.SimpleEnum) != 0)
                {
                    return EnumHandler.GetCSharpEnumUnderlyingType(enumRecord.RawValueTypeName);
                }
            }

            // All other types (structs, classes, etc.) must be passed as void*
            // and marshalled manually, even if frozen - only primitives are safe
            // to pass directly in unmanaged function pointers
            return "void*";
        }

        if (typeSpec.IsEmptyTuple)
            return "void";

        // Handle tuple types
        if (typeSpec is TupleTypeSpec tupleType)
            return _tupleHandler.GetPInvokeTupleType(tupleType);

        // Fallback
        return "void*";
    }

    /// <summary>
    /// Checks if a closure can be invoked from C# when received from Swift.
    /// Closures with non-primitive parameters cannot be invoked because we can't
    /// easily marshal C# structs to void* pointers in the lambda.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>True if all parameters are primitive types that can be passed directly.</returns>
    public bool CanInvokeFromCSharp(ClosureTypeSpec closureTypeSpec)
    {
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (!IsInvocableParameter(arg))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if a closure RETURNED from Swift can be safely invoked from C# via a direct
    /// Swift function pointer call. Currently the throwing-closure return path
    /// (<see cref="Emitter.StringEmitter.ClosureEmitter"/> EmitThrowingClosureReturnMarshalling)
    /// emits <c>_fp(_arg0, _arg1, ...)</c> using <see cref="Emitter.StringEmitter.ClosureEmitter"/>'s
    /// <c>GetSwiftInvokeArgExpression</c>. That helper only knows how to convert a fixed set
    /// of types into the void* that the function pointer expects (primitives, enums, classes,
    /// ObjC bridged classes, well-known/known protocols). Anything else (Swift.String, frozen
    /// structs, generic structs, tuples) falls through as the bare C# expression and produces
    /// CS1503 cannot-convert-to-void* errors.
    /// Until full marshaling for those shapes lands, throwing closures whose params include
    /// such types must be pruned at the boundary, otherwise the binding emits broken C#.
    /// Mirror the branch list in <c>GetSwiftInvokeArgExpression</c> exactly — keeping these
    /// in sync is load-bearing.
    /// </summary>
    public bool CanInvokeReturnedThrowingClosure(ClosureTypeSpec closureTypeSpec)
    {
        if (!closureTypeSpec.Throws)
            return true;

        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (!IsDirectInvokeArgSupported(arg))
                return false;
        }

        if (!IsDirectInvokeReturnSupported(closureTypeSpec.ReturnType))
            return false;

        return true;
    }

    /// <summary>
    /// Checks if a returned throwing closure's RETURN type is correctly handled by
    /// <see cref="Emitter.StringEmitter.ClosureEmitter.EmitClosureReturnMarshalling"/>'s
    /// fallback emission path. The default branch there emits a bare
    /// <c>return _fp(...)</c>, which only compiles when the function pointer's P/Invoke
    /// return type matches the delegate's declared C# return type. Bound generics, frozen
    /// structs with reference fields, complex enums, ObjC-bridged classes, etc. translate
    /// to <c>void*</c> in the function pointer but stay as their C# shape in the delegate
    /// — assigning void* to those produces CS1503 at compile time.
    /// Mirror the explicit branch list in <c>EmitClosureReturnMarshalling</c>; keeping
    /// these in sync is load-bearing.
    /// </summary>
    private bool IsDirectInvokeReturnSupported(TypeSpec typeSpec)
    {
        // void return: emitter omits the return statement
        if (typeSpec.IsEmptyTuple)
            return true;
        if (MarshallingHelpers.IsBoolType(typeSpec))
            return true;
        if (NeedsWellKnownProtocolWrapping(typeSpec, out _))
            return true;
        if (NeedsProxyWrapping(typeSpec, out _))
            return true;
        if (IsExistentialParam(typeSpec))
            return true;
        if (IsSimpleEnum(typeSpec))
            return true;
        if (typeSpec is NamedTypeSpec namedType)
        {
            if (IsClassType(namedType))
                return true;
            // Pointer types are NOT supported here for the same reason as in
            // IsDirectInvokeArgSupported: TranslateTypeSpecToCSharp emits IntPtr for
            // the delegate return type while TranslateTypeSpecToPInvokeType emits
            // void* for the function pointer return; bare `return _fp(...)` would
            // produce CS1503 without an explicit cast that the emitter does not insert.
            if (GetBlittablePrimitiveType(namedType.Name) != null)
                return true;
        }
        if (typeSpec is TupleTypeSpec tuple)
        {
            foreach (var element in tuple.Elements)
            {
                if (!IsDirectInvokeReturnSupported(element))
                    return false;
            }
            return true;
        }
        return false;
    }

    private bool IsDirectInvokeArgSupported(TypeSpec typeSpec)
    {
        if (MarshallingHelpers.IsBoolType(typeSpec))
            return true;
        if (IsSimpleEnum(typeSpec) || IsComplexEnum(typeSpec))
            return true;
        if (NeedsWellKnownProtocolWrapping(typeSpec, out _))
            return true;
        if (NeedsProxyWrapping(typeSpec, out _))
            return true;
        if (IsExistentialParam(typeSpec))
            return true;
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Blittable primitives translate to int/long/float/etc. and are passed
            // bare as `_arg{N}` — the function pointer's typed parameter accepts them
            // directly. Without this, `(Int32) throws -> Void` returns get pruned even
            // though the emitter would produce valid C#.
            // (Pointer types are NOT included: TranslateTypeSpecToCSharp emits IntPtr
            // for the delegate signature while TranslateTypeSpecToPInvokeType emits void*
            // for the function pointer; the emitter does not insert the cross-cast, so
            // bare `_argN` would produce CS1503.)
            if (GetBlittablePrimitiveType(namedType.Name) != null)
                return true;
            if (IsClassType(namedType))
                return true;
            if (IsObjCBridgedClass(namedType))
                return true;
        }
        if (typeSpec is TupleTypeSpec tuple && !tuple.IsEmptyTuple)
        {
            foreach (var element in tuple.Elements)
            {
                if (!IsDirectInvokeArgSupported(element))
                    return false;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if a parameter type can be passed when invoking a Swift closure from C#.
    /// Supports primitive types, frozen structs, non-frozen structs, enums, and reference types.
    /// </summary>
    private bool IsInvocableParameter(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Pointer types are supported
            if (IsPointerType(namedType))
                return true;

            // Primitive types are supported (direct pass)
            var primitiveType = GetBlittablePrimitiveType(namedType.Name);
            if (primitiveType != null)
                return true;

            // Frozen structs are supported (via marshalling with stackalloc)
            if (IsFrozenStruct(namedType))
                return true;

            // Non-frozen structs are supported (via ISwiftObject marshalling with NativeMemory)
            if (IsNonFrozenStruct(namedType))
                return true;

            // Class types are supported (via IntPtr / Unmanaged patterns)
            if (IsClassType(namedType))
                return true;

            // ObjC-bridged types are supported (via Handle patterns)
            if (IsObjCBridgedClass(namedType))
                return true;

            // Simple enums are supported (cast to underlying integer type)
            if (IsSimpleEnum(namedType))
                return true;

            // Complex enums are supported (via payload handle extraction)
            if (IsComplexEnum(namedType))
                return true;

            return false;
        }

        // Tuples of primitives could be supported but aren't currently
        if (typeSpec is TupleTypeSpec)
            return false;

        // Empty tuples (void) are fine
        if (typeSpec.IsEmptyTuple)
            return true;

        // Other types (closures, existentials, etc.) are not supported
        return false;
    }

    /// <summary>
    /// Checks if a type is a frozen struct in the type database.
    /// Frozen structs can be marshalled via MarshalToSwift when invoking closures.
    /// </summary>
    /// <param name="typeSpec">The type specification to check.</param>
    /// <returns>True if the type is a frozen struct.</returns>
    public bool IsFrozenStruct(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        // Don't treat generic types as frozen structs - they need special handling
        if (namedType.ContainsGenericParameters)
            return false;

        // Generic type parameters (τ_0_0, T, U, ...) are not module-qualified and can't be
        // resolved through the type database. Treat them as non-frozen.
        if (IsGenericTypeParameter(namedType.Name) || !namedType.HasModule())
            return false;

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            return false;

        // Must be a struct and be frozen
        return typeRecord.Kind == TypeRecordKind.Struct &&
               (typeRecord.Flags & TypeRecordFlags.Frozen) != 0;
    }

    /// <summary>
    /// Checks if a type is a non-frozen struct in the type database.
    /// Non-frozen structs implement ISwiftObject and can be marshalled via
    /// InitializeWithCopy/Destroy when invoking closures.
    /// </summary>
    /// <param name="typeSpec">The type specification to check.</param>
    /// <returns>True if the type is a non-frozen struct.</returns>
    public bool IsNonFrozenStruct(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        // Don't treat generic types as non-frozen structs - they need special handling
        if (namedType.ContainsGenericParameters)
            return false;

        // Generic type parameters (τ_0_0, T, U, ...) are not module-qualified and can't be
        // resolved through the type database. Treat them as non-frozen for this check
        // so SwiftTypeName.FromModuleQualifiedName doesn't throw. Mirrors IsFrozenStruct.
        if (IsGenericTypeParameter(namedType.Name) || !namedType.HasModule())
            return false;

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            return false;

        // NativeRemapped types (Foundation.URL → NSUrl) are ObjC types in C# — no .Payload
        if (typeRecord.NativeTypeName != null)
            return false;

        // Must be a struct and NOT be frozen
        return typeRecord.Kind == TypeRecordKind.Struct &&
               (typeRecord.Flags & TypeRecordFlags.Frozen) == 0;
    }

    /// <summary>
    /// Checks if a type is a class in the type database.
    /// Classes pass as raw pointers (UnsafeMutableRawPointer) in closure callbacks.
    /// </summary>
    public bool IsClassType(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        if (namedType.ContainsGenericParameters)
            return false;

        if (!namedType.HasModule())
            return false;

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            return false;

        return typeRecord.Kind == TypeRecordKind.Class
            && !MarshallingHelpers.IsObjCBridged(typeRecord)
            && !MarshallingHelpers.IsObjCRooted(typeRecord);
    }

    /// <summary>
    /// Checks if a type is a simple enum (no-payload) in the type database.
    /// Simple enums pass as their underlying integer type in closure callbacks.
    /// </summary>
    public bool IsSimpleEnum(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        if (namedType.ContainsGenericParameters)
            return false;

        if (!namedType.HasModule())
            return false;

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            return false;

        return typeRecord.Kind == TypeRecordKind.Enum &&
               (typeRecord.Flags & TypeRecordFlags.SimpleEnum) != 0;
    }

    /// <summary>
    /// Gets the C# underlying type and Swift scalar type for a simple enum.
    /// Returns null if the type is not a simple enum.
    /// </summary>
    public (string csUnderlying, string swiftScalar, bool hasRawValue, string? swiftRawType)? GetSimpleEnumInfo(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return null;

        if (namedType.ContainsGenericParameters || !namedType.HasModule())
            return null;

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            return null;

        if (typeRecord.Kind != TypeRecordKind.Enum ||
            (typeRecord.Flags & TypeRecordFlags.SimpleEnum) == 0)
            return null;

        var csUnderlying = EnumHandler.GetCSharpEnumUnderlyingType(typeRecord.RawValueTypeName);
        var swiftScalar = EnumHandler.GetSwiftScalarType(csUnderlying);
        // hasRawValue is true only for numeric raw values where .rawValue/init(rawValue:)
        // matches the integer callback ABI. String-backed enums use .rawValue -> String
        // which doesn't match the Int32 ABI, so they must use the tag-only pointer path.
        var hasRawValue = !string.IsNullOrEmpty(typeRecord.RawValueTypeName) &&
                          typeRecord.RawValueTypeName != "String";
        // swiftRawType: the actual Swift raw value type (e.g., "Int") which may differ
        // from swiftScalar (e.g., "Int64"). Needed for init(rawValue:) casts where
        // Swift treats Int and Int64 as distinct types.
        var swiftRawType = typeRecord.RawValueTypeName;
        return (csUnderlying, swiftScalar, hasRawValue, swiftRawType);
    }

    /// <summary>
    /// Checks if a type is an Objective-C bridged or ObjC-rooted class in the type database.
    /// ObjC-bridged types (e.g., NSError, UIImage) and ObjC-rooted types (Swift classes
    /// inheriting NSObject) pass as raw pointers (.Handle) in closure callbacks.
    /// </summary>
    public bool IsObjCBridgedClass(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        if (namedType.ContainsGenericParameters)
            return false;

        if (!namedType.HasModule())
            return false;

        // Use the NamedTypeSpec overload so ObjC-bridged types from auto-bridge
        // Apple modules resolve via the synthetic ObjCBridged fallback (e.g. PassKit
        // classes that never appear as plain parameters elsewhere and therefore have
        // no scanned TypeRecord).
        if (!_typeDatabase.TryGetTypeRecord(namedType, out var typeRecord))
            return false;

        return MarshallingHelpers.IsObjCBridged(typeRecord)
            || MarshallingHelpers.IsObjCRooted(typeRecord);
    }

    /// <summary>
    /// Checks if a type is a reference type (class or ObjC-bridged) that uses pointer ABI
    /// in closure callbacks. This is the unified check for nil-pointer Optional ABI.
    /// </summary>
    public bool IsReferenceType(TypeSpec typeSpec) =>
        IsClassType(typeSpec) || IsObjCBridgedClass(typeSpec);

    /// <summary>
    /// Checks if a type has an ObjC NativeTypeName remap (e.g., Foundation.URLResponse →
    /// Foundation.NSUrlResponse) that <see cref="TranslateTypeSpecToCSharp"/> projects to.
    /// Closure callbacks need <see cref="MarshallingHelpers.FormatObjCBridgeCall"/> to
    /// convert the IntPtr to the .NET type, since MarshalBorrowedFromSwift can't bridge
    /// through ObjC.
    /// </summary>
    /// <param name="typeSpec">The Swift type to inspect.</param>
    /// <param name="nativeTypeName">The C# native type to bridge into (e.g.,
    /// "Foundation.NSUrlResponse").</param>
    public bool HasObjCNativeRemap(TypeSpec typeSpec, out string nativeTypeName)
    {
        nativeTypeName = string.Empty;
        if (typeSpec is not NamedTypeSpec namedType)
            return false;
        if (namedType.ContainsGenericParameters || !namedType.HasModule())
            return false;
        if (!_typeDatabase.TryGetTypeRecord(namedType, out var typeRecord))
            return false;
        if (typeRecord.NativeTypeName == null)
            return false;
        nativeTypeName = typeRecord.NativeTypeName.FullyQualifiedName;
        return true;
    }

    /// <summary>
    /// Checks if a type is a complex enum (enum with associated values) in the type database.
    /// Complex enums are non-blittable and require heap allocation for closure parameter passing.
    /// </summary>
    public bool IsComplexEnum(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        if (namedType.ContainsGenericParameters)
            return false;

        if (!namedType.HasModule())
            return false;

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            return false;

        return typeRecord.Kind == TypeRecordKind.Enum &&
               (typeRecord.Flags & TypeRecordFlags.SimpleEnum) == 0;
    }

    /// <summary>
    /// Checks if invoking a closure from C# requires struct marshalling for any parameter.
    /// When true, the invoker lambda needs to marshal struct parameters to void* before calling Swift.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>True if any parameter requires struct marshalling.</returns>
    public bool RequiresStructMarshalling(ClosureTypeSpec closureTypeSpec)
    {
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (arg is NamedTypeSpec namedType &&
                !IsPointerType(namedType) &&
                GetBlittablePrimitiveType(namedType.Name) == null &&
                IsFrozenStruct(namedType))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if invoking a closure from C# requires non-frozen struct marshalling for any parameter.
    /// Non-frozen structs require heap allocation via NativeMemory and InitializeWithCopy/Destroy.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>True if any parameter requires non-frozen struct marshalling.</returns>
    public bool RequiresNonFrozenMarshalling(ClosureTypeSpec closureTypeSpec)
    {
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (IsNonFrozenStruct(arg))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the blittable C# type for known Swift primitive types.
    /// Returns null for non-primitive types that should use void*.
    /// </summary>
    private static string? GetBlittablePrimitiveType(string swiftTypeName)
    {
        return swiftTypeName switch
        {
            "Swift.Int" => "nint",
            "Swift.UInt" => "nuint",
            "Swift.Int8" => "sbyte",
            "Swift.UInt8" => "byte",
            "Swift.Int16" => "short",
            "Swift.UInt16" => "ushort",
            "Swift.Int32" => "int",
            "Swift.UInt32" => "uint",
            "Swift.Int64" => "long",
            "Swift.UInt64" => "ulong",
            "Swift.Float" => "float",
            "Swift.Double" => "double",
            // Bool is non-blittable, use byte instead (Swift.Bool is 1 byte)
            "Swift.Bool" => "byte",
            _ => null // Not a primitive - should use void*
        };
    }

    /// <summary>
    /// Checks if a named type is a Swift pointer type.
    /// </summary>
    private static bool IsPointerType(NamedTypeSpec? namedType)
    {
        if (namedType == null) return false;
        return namedType.Name == "Swift.UnsafePointer" ||
               namedType.Name == "Swift.UnsafeMutablePointer" ||
               namedType.Name == "Swift.UnsafeRawPointer" ||
               namedType.Name == "Swift.UnsafeMutableRawPointer" ||
               namedType.Name == "Swift.OpaquePointer" ||
               namedType.Name == "Builtin.RawPointer";
    }

    /// <summary>
    /// Checks if a type spec is a primitive type (bool, int, float, etc.).
    /// Primitive types can safely use C# nullable syntax (T?).
    /// </summary>
    private bool IsPrimitiveType(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        // Check against known primitive type names
        return namedType.Name switch
        {
            "Swift.Bool" => true,
            "Swift.Int" => true,
            "Swift.Int8" => true,
            "Swift.Int16" => true,
            "Swift.Int32" => true,
            "Swift.Int64" => true,
            "Swift.UInt" => true,
            "Swift.UInt8" => true,
            "Swift.UInt16" => true,
            "Swift.UInt32" => true,
            "Swift.UInt64" => true,
            "Swift.Float" => true,
            "Swift.Double" => true,
            "Swift.Float16" => true,
            "Swift.Float32" => true,
            "Swift.Float80" => true,
            _ => false
        };
    }

    /// <summary>
    /// Generates the callback function name for a closure parameter.
    /// Includes a hash of the method's mangled name to disambiguate overloads.
    /// </summary>
    /// <param name="methodName">The name of the method containing the closure.</param>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <param name="mangledName">The mangled name of the method (used to create a unique hash).</param>
    /// <returns>The callback function name.</returns>
    public static string GetCallbackFunctionName(string methodName, string parameterName, string mangledName)
    {
        var mangledHash = EmitterUtility.DeterministicHash8(mangledName);
        return $"{methodName}_{parameterName}_{mangledHash}_Callback";
    }

    /// <summary>
    /// Generates the closure wrapper field name for storing delegate references.
    /// </summary>
    /// <param name="parameterName">The name of the closure parameter.</param>
    /// <returns>The closure wrapper field name.</returns>
    public static string GetClosureWrapperFieldName(string parameterName)
    {
        return $"_{parameterName}Closure";
    }

    /// <summary>
    /// Determines whether a type spec is an existential that requires boxing/unboxing
    /// between <c>object</c> and <c>ExistentialContainer</c> in callback/invoker code.
    /// Returns true for unknown protocols (no proxy, no well-known mapping).
    /// Returns false for well-known protocols (AnyError), known protocols with proxies,
    /// and non-existential types.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns>True if the type needs object boxing/unboxing.</returns>
    public bool IsExistentialParam(TypeSpec typeSpec)
    {
        if (!_existentialHandler.IsExistential(typeSpec)) return false;
        var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
        if (protocolList == null || !_existentialHandler.IsSupportedExistential(protocolList)) return false;
        // Well-known protocols (Swift.Error → AnyError) handled by NeedsWellKnownProtocolWrapping
        if (_existentialHandler.TryGetWellKnownProtocolType(protocolList, out _)) return false;
        // Known protocols with proxy classes handled by NeedsProxyWrapping
        if (NeedsProxyWrapping(typeSpec, out _)) return false;
        return true;
    }

    /// <summary>
    /// Gets the public C# interface type for a given existential type spec.
    /// Returns the C# interface name (e.g., "IBlockMode") for use with ExistentialContainerFactory.
    /// </summary>
    public string? GetPublicExistentialType(TypeSpec typeSpec)
    {
        var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
        if (protocolList != null)
            return _existentialHandler.GetPublicExistentialType(protocolList);
        return null;
    }

    /// <summary>
    /// Returns true if GetOrCreate should be used for this existential type.
    /// Only valid for single-protocol (EC1) interfaces that aren't well-known types.
    /// Compositions (EC2+), well-known types (AnyError/EC0), and unknown protocols (object)
    /// must use ISwiftExistentialConvertible directly.
    /// </summary>
    public bool ShouldUseGetOrCreate(TypeSpec typeSpec)
    {
        var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
        if (protocolList == null) return false;
        if (_existentialHandler.TryGetWellKnownProtocolType(protocolList, out _)) return false;
        return _existentialHandler.GetPInvokeExistentialType(protocolList) == "Swift.Runtime.ExistentialContainer1";
    }

    /// <summary>
    /// Gets the P/Invoke existential container type for a given existential type spec.
    /// Returns the appropriate <c>ExistentialContainer{N}</c> string.
    /// </summary>
    /// <param name="typeSpec">The type specification (must be an existential).</param>
    /// <returns>The ExistentialContainer type name, or "void*" if not an existential.</returns>
    public string GetPInvokeExistentialType(TypeSpec typeSpec)
    {
        var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
        if (protocolList != null)
            return _existentialHandler.GetPInvokeExistentialType(protocolList);
        return "void*";
    }

    /// <summary>
    /// Determines whether a type spec is a well-known protocol existential that needs
    /// wrapping/unwrapping between the runtime type (e.g., AnyError) and the raw
    /// ExistentialContainer in callback/invoker code.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <param name="wrapType">The well-known C# type name (e.g., "Swift.Foundation.AnyError") if applicable.</param>
    /// <returns>True if the type needs well-known protocol wrapping.</returns>
    public bool NeedsWellKnownProtocolWrapping(TypeSpec typeSpec, out string wrapType)
    {
        wrapType = "";
        if (!_existentialHandler.IsExistential(typeSpec)) return false;
        var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
        if (protocolList == null) return false;
        return _existentialHandler.TryGetWellKnownProtocolType(protocolList, out wrapType);
    }

    public bool NeedsProxyWrapping(TypeSpec typeSpec, out string proxyClassName)
    {
        proxyClassName = "";
        if (!_existentialHandler.IsExistential(typeSpec)) return false;
        var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
        if (protocolList == null || !_existentialHandler.AllProtocolsHaveTypeRecords(protocolList)) return false;
        var publicType = _existentialHandler.GetPublicExistentialType(protocolList);
        if (publicType == "object") return false;
        // P1 fix: Only wrap when filtered protocol count matches original count.
        // If ObjC filtering drops protocols, the proxy class constructor accepts
        // ExistentialContainer{filteredCount} but P/Invoke passes ExistentialContainer{originalCount}.
        if (!_existentialHandler.TryGetFilteredProxyClassName(protocolList, out proxyClassName))
            return false;
        // Parity with ExistentialHandler.GetEffectiveProtocols (per-module ObjC-prefix gate).
        var filteredCount = protocolList.Protocols.Keys
            .Count(p => !TypeDatabaseExtensions.IsObjCExistentialBridgedProtocol(p));
        if (filteredCount != protocolList.Protocols.Count)
        {
            proxyClassName = "";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Gets the cross-module-qualified proxy class name for a given existential type spec,
    /// or null if no proxy class exists for the type (well-known, object, or ObjC-filtered out).
    /// Used by ClosureEmitter to emit the GetOrCreate auto-wrap fallback that lets plain C#
    /// implementations of the interface be passed through closure boundaries without manual
    /// proxy construction.
    /// </summary>
    public string? GetQualifiedProxyClassName(TypeSpec typeSpec)
    {
        if (!NeedsProxyWrapping(typeSpec, out var filteredProxy)) return null;
        var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
        if (protocolList == null) return null;
        return _existentialHandler.QualifyProxyClassName(filteredProxy, protocolList);
    }
}
