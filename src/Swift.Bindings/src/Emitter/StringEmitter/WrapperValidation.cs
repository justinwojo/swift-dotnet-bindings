// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Result of the combined ShouldEmitWrapper + RequiresCdeclForAbiSafety decision.
/// Replaces the implicit AND gate pattern at call sites.
/// </summary>
public enum WrapperDecision
{
    /// <summary>
    /// The wrapper emitter's ShouldEmitWrapper returned false — wrapping is not possible
    /// (unsupported signature, missing xcframework mode, etc.).
    /// </summary>
    CannotWrap,

    /// <summary>
    /// Wrapping is possible (ShouldEmitWrapper passed) but not required for ABI safety
    /// (RequiresCdeclForAbiSafety returned false). Use legacy CallConvSwift.
    /// </summary>
    NoWrapperNeeded,

    /// <summary>
    /// Wrapping is both possible and required for ABI safety. Emit the @_cdecl wrapper.
    /// </summary>
    WrapperRequired
}

/// <summary>
/// Identifies which kind of member is being evaluated for generic dispatch decisions.
/// Used by <see cref="WrapperValidation.NeedsGenericDispatch"/> to apply the correct
/// dispatch logic, since the guard conditions differ by member kind.
/// </summary>
public enum MemberKind
{
    /// <summary>Instance or static method (non-constructor, non-accessor).</summary>
    Method,
    /// <summary>Property getter or setter accessor.</summary>
    Property,
    /// <summary>Constructor (init).</summary>
    Constructor
}

/// <summary>
/// Shared guard predicates for the four wrapper emitters (Method, Constructor, Property, Subscript).
/// Each method is the single source of truth for its predicate — wrapper emitters should call
/// these instead of duplicating the logic. All methods are pure queries with no side effects.
/// </summary>
public static class WrapperValidation
{
    /// <summary>
    /// Named constants for ABI size thresholds used in @_cdecl safety checks.
    /// Self parameters use SwiftSelf&lt;T&gt; register layout (different from regular params).
    /// </summary>
    internal static class AbiSizeLimits
    {
        /// <summary>
        /// Maximum inline size for a frozen struct passed via SwiftSelf&lt;T&gt; (self parameter).
        /// Mono JIT can't generate correct CallConvSwift stubs for multi-register self params.
        /// </summary>
        public const int MaxSelfSize = 8;

        /// <summary>
        /// Maximum inline size for a custom integer frozen struct passed as a regular parameter.
        /// NativeAOT SIGSEGV for structs exceeding this size.
        /// </summary>
        public const int MaxParamSize = 16;
    }

    /// <summary>
    /// Centralized decision for method @_cdecl wrapper emission.
    /// Combines MethodWrapperEmitter.ShouldEmitWrapper + RequiresCdeclForAbiSafety.
    /// </summary>
    public static WrapperDecision DetermineMethodWrapperDecision(MethodEnvironment env)
    {
        if (!MethodWrapperEmitter.ShouldEmitWrapper(env))
            return WrapperDecision.CannotWrap;
        if (!RequiresCdeclForAbiSafety(env))
            return WrapperDecision.NoWrapperNeeded;
        return WrapperDecision.WrapperRequired;
    }

    /// <summary>
    /// Centralized decision for constructor @_cdecl wrapper emission.
    /// Combines ConstructorWrapperEmitter.ShouldEmitWrapper + RequiresCdeclForAbiSafety.
    /// </summary>
    public static WrapperDecision DetermineConstructorWrapperDecision(MethodEnvironment env)
    {
        if (!ConstructorWrapperEmitter.ShouldEmitWrapper(env))
            return WrapperDecision.CannotWrap;
        if (!RequiresCdeclForAbiSafety(env))
            return WrapperDecision.NoWrapperNeeded;
        return WrapperDecision.WrapperRequired;
    }

    /// <summary>
    /// Centralized decision for property @_cdecl wrapper emission.
    /// Combines PropertyWrapperEmitter.ShouldEmitWrapper + RequiresCdeclForAbiSafety.
    /// </summary>
    public static WrapperDecision DeterminePropertyWrapperDecision(PropertyDecl propertyDecl, MethodEnvironment env)
    {
        if (!PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env))
            return WrapperDecision.CannotWrap;
        if (!RequiresCdeclForAbiSafety(env, propertyDecl))
            return WrapperDecision.NoWrapperNeeded;
        return WrapperDecision.WrapperRequired;
    }

    /// <summary>
    /// Returns true when the generator is running in xcframework mode, where the wrapper
    /// library exists. This is a prerequisite for all @_cdecl wrapper emission.
    /// </summary>
    public static bool IsXCFrameworkMode(ITypeDatabase db)
    {
        return !string.IsNullOrEmpty(db.AsyncLibraryName);
    }

    /// <summary>
    /// Checks if a parent decl is a non-copyable struct.
    /// In Swift 6.2+, ALL types explicitly list both Copyable and Escapable in ABI JSON.
    /// Non-copyable types list Escapable WITHOUT Copyable.
    /// </summary>
    public static bool IsNonCopyableStructParent(BaseDecl? parentDecl)
    {
        if (parentDecl is StructDecl structDecl)
        {
            return structDecl.Conformances.Any(c => c.Protocol.ToString() == "Swift.Escapable") &&
                   !structDecl.Conformances.Any(c => c.Protocol.ToString() == "Swift.Copyable");
        }
        return false;
    }

    /// <summary>
    /// Checks whether a member should be blocked from @_cdecl wrapper emission due to actor isolation.
    /// Blocks:
    /// (a) Custom actor types (ClassDecl { IsActor: true }) — all members require async dispatch
    /// (b) Per-member custom actor isolation (e.g., @ProcessingActor) on non-actor classes
    ///
    /// Does NOT block @MainActor members — those are exposed as synchronous C# APIs following
    /// the Xamarin.iOS precedent. The consumer manages thread affinity.
    /// </summary>
    public static bool IsActorIsolatedMember(BaseDecl? parentDecl, bool memberIsActorIsolated, bool memberIsMainActorIsolated)
    {
        // (a) Parent is a custom actor class — all members require async dispatch
        if (parentDecl is ClassDecl { IsActor: true })
            return true;

        // (b) Per-member custom actor isolation (not @MainActor) — requires async dispatch
        // memberIsActorIsolated covers BOTH @MainActor and custom actors;
        // memberIsMainActorIsolated is true only for @MainActor.
        // Block only when it's a custom actor (actor-isolated but NOT main-actor-isolated).
        if (memberIsActorIsolated && !memberIsMainActorIsolated)
            return true;

        return false;
    }

    /// <summary>
    /// Backward-compatible overload for callers that don't have the IsMainActorIsolated flag.
    /// Assumes any actor isolation is from a custom actor (conservative — blocks emission).
    /// </summary>
    public static bool IsActorIsolatedMember(BaseDecl? parentDecl, bool memberIsActorIsolated)
    {
        // Without the MainActor distinction, treat all per-member isolation as custom actor
        return IsActorIsolatedMember(parentDecl, memberIsActorIsolated, memberIsMainActorIsolated: false);
    }

    /// <summary>
    /// Returns true when a @_cdecl wrapper function should be annotated with @MainActor.
    /// Swift 6 requires the caller to share the isolation context. @MainActor on @_cdecl is
    /// a compile-time constraint only (no ABI change). The C# consumer manages thread affinity.
    ///
    /// Only returns true for @MainActor isolation — NOT for custom global actors.
    /// </summary>
    public static bool NeedsMainActorAnnotation(BaseDecl? parentDecl, bool memberIsMainActorIsolated, bool memberIsNonisolated = false)
    {
        // nonisolated members explicitly opt out of their parent's isolation
        if (memberIsNonisolated)
            return false;

        // Parent type is @MainActor — all members inherit isolation
        if (parentDecl is TypeDecl { IsMainActorIsolated: true })
            return true;

        // Per-member @MainActor isolation (NOT custom actors)
        if (memberIsMainActorIsolated)
            return true;

        return false;
    }

    /// <summary>
    /// Checks whether any closure parameter is an async closure (IsAsync).
    /// GetSwiftClosureAdapterCode() only emits synchronous adapter code, so async closures
    /// (even non-throwing ones) are not supported in @_cdecl wrappers.
    /// </summary>
    public static bool HasAnyAsyncClosure(MethodEnvironment env)
    {
        return env.MethodDecl.CSSignature.Skip(1)
            .Where(env.ClosureHandler.IsClosure)
            .Any(arg =>
            {
                var spec = env.ClosureHandler.GetClosureTypeSpec(arg);
                return spec != null && env.ClosureHandler.IsAsyncClosure(spec);
            });
    }

    /// <summary>
    /// Returns true if a type is a generic container that can't be handled by @_cdecl wrappers.
    /// Allows: Optional&lt;value-type&gt; (IndirectResult), Optional&lt;reference&gt; (nullable pointer),
    /// Array, Dictionary, Set (UnsafeRawPointer transport).
    /// Blocks: Result&lt;T,E&gt;, Optional&lt;protocol existential&gt; (needs proxy conversion).
    /// </summary>
    public static bool IsUnsupportedGenericContainer(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (!CdeclParamMapper.IsGenericContainerType(typeSpec))
            return false;
        if (IsOptionalSupportedForCdecl(typeSpec, typeDatabase))
            return false;  // Optional<value-type/reference>: IndirectResult or nullable pointer
        if (IsSupportedCollectionType(typeSpec))
            return false;  // Array, Dictionary, Set pass through via UnsafeRawPointer
        return true;  // Result<T,E>, Optional<existential> still blocked
    }

    /// <summary>
    /// Returns true for metatype types (Any.Type, T.Type, etc.) which are not
    /// C-representable in @_cdecl wrappers. The generator renders them as bare "Type"
    /// which doesn't exist in Swift, causing compilation errors.
    /// </summary>
    public static bool IsMetatypeType(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec named)
        {
            // Metatypes appear as "Any.Type", "SomeModule.SomeType.Type", or bare "Type"
            if (named.Name == "Type" || named.Name.EndsWith(".Type"))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true for Swift.Optional&lt;T&gt; type specs (any generic parameter count &gt; 0).
    /// </summary>
    public static bool IsOptionalType(TypeSpec typeSpec)
        => typeSpec is NamedTypeSpec { Name: "Swift.Optional", GenericParameters.Count: > 0 };

    /// <summary>
    /// Returns true for Optional types that can be handled by @_cdecl wrappers:
    /// - Optional&lt;reference&gt;: nullable pointer ABI (UnsafeMutableRawPointer?)
    /// - Optional&lt;value-type&gt;: IndirectResult via resultPtr
    /// Returns false for Optional&lt;protocol existential&gt; which needs proxy conversion
    /// that the @_cdecl IndirectResult path doesn't handle.
    /// </summary>
    public static bool IsOptionalSupportedForCdecl(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (!IsOptionalType(typeSpec))
            return false;
        // Optional<protocol existential> needs special proxy conversion
        if (CdeclParamMapper.IsProtocolExistentialType(typeSpec, typeDatabase))
            return false;
        return true;
    }

    /// <summary>
    /// Returns true for Optional&lt;T&gt; where T is a reference-like type (Class, ObjC-bridged, ObjC-rooted).
    /// These use nullable pointer ABI (UnsafeMutableRawPointer?) in @_cdecl wrappers.
    ///
    /// Path 1: TypeRecord check — Class, ObjC-bridged, ObjC-rooted kinds, with NSString typedef exclusion
    /// (e.g., CALayerContentsGravity wraps NSString as a struct, not a class — Unmanaged requires class).
    ///
    /// Path 2: Fallback via MarshallingHelpers.IsOptionalObjCBridged for unresolved Apple framework
    /// ObjC classes, with defense-in-depth guards (!ContainsGenericParameters, !IsPointerType).
    /// </summary>
    public static bool IsOptionalWithReferenceInner(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (!IsOptionalType(typeSpec))
            return false;

        var inner = ((NamedTypeSpec)typeSpec).GenericParameters[0];
        if (inner is not NamedTypeSpec innerNamed)
            return false;

        // Path 1: Type has a TypeRecord — check kind directly
        if (typeDatabase.TryGetTypeRecord(inner, out var typeRecord))
        {
            // ObjC-bridged/ObjC-rooted structs (e.g., UIFont.Weight, PHPickerResult,
            // CALayerContentsGravity) are flagged ObjCBridged/ObjCRooted in the type database
            // but are Swift structs, not class instances. Unmanaged<T> requires T: AnyObject,
            // so these must NOT be treated as reference types. Only true classes qualify.
            // This generalizes the previous NSString-only guard to cover all ObjC-bridged structs.
            if (typeRecord.Kind == TypeRecordKind.Struct || typeRecord.Kind == TypeRecordKind.Enum)
                return false;

            return typeRecord.Kind == TypeRecordKind.Class ||
                   MarshallingHelpers.IsObjCBridged(typeRecord) ||
                   MarshallingHelpers.IsObjCRooted(typeRecord);
        }

        // Path 2: Unresolved Apple framework ObjC class fallback.
        // Delegate to MarshallingHelpers.IsOptionalObjCBridged which handles both the
        // TypeRecord path AND the fallback heuristic: IsOptionalFallbackModule +
        // !IsNestedType + !IsKnownAppleValueType + HasObjCClassPrefix.
        // Since Path 1 already handled the TypeRecord case, this only triggers the
        // fallback heuristic. Add defense-in-depth checks matching TypeProjectionFactory.
        if (!innerNamed.ContainsGenericParameters &&
            !AppleFrameworkRegistry.IsPointerType(innerNamed.Name) &&
            MarshallingHelpers.IsOptionalObjCBridged(typeSpec, typeDatabase))
            return true;

        return false;
    }

    /// <summary>
    /// Returns true for collection container types that can be transported through @_cdecl
    /// wrappers via UnsafeRawPointer + .load(as:) / resultPtr.initializeMemory(as:).
    /// </summary>
    public static bool IsSupportedCollectionType(TypeSpec typeSpec)
    {
        return typeSpec is NamedTypeSpec named &&
            named.Name is "Swift.Array" or "Swift.Dictionary" or "Swift.Set";
    }

    /// <summary>
    /// Returns true when an Optional type needs decomposed getter/setter wrappers.
    /// This applies to Optional&lt;T&gt; where T is a complex enum or non-frozen struct
    /// (both use opaque SafeHandle payloads where VWT InitializeWithCopy crashes Mono).
    /// The decomposed pattern passes (rawPayload, hasValue) as separate parameters,
    /// with the Optional being reconstructed on the Swift side.
    /// </summary>
    public static bool IsDecomposedOptionalType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is not NamedTypeSpec named || named.Name != "Swift.Optional" || named.GenericParameters.Count != 1)
            return false;
        var inner = named.GenericParameters[0];
        if (inner is not NamedTypeSpec innerNamed)
            return false;
        if (!typeDatabase.TryGetTypeRecord(innerNamed, out var typeRecord))
            return false;
        // Exclude ObjC bridged/rooted types — they use .Handle, not .Payload.DangerousGetHandle()
        if (MarshallingHelpers.IsObjCBridged(typeRecord) || MarshallingHelpers.IsObjCRooted(typeRecord))
            return false;
        // Exclude native-remapped types (URL → NSUrl, Data → NSData) — they use conversion methods
        if (typeRecord.NativeTypeName != null)
            return false;
        // Exclude classes — they use nullable pointer ABI (IntPtr.Zero = None)
        if (typeRecord.Kind == TypeRecordKind.Class)
            return false;
        // Complex enums (non-simple) use opaque SafeHandle payloads
        if (typeRecord.Kind == TypeRecordKind.Enum && !typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            return true;
        // Non-frozen structs also use opaque SafeHandle payloads
        if (typeRecord.Kind == TypeRecordKind.Struct && !MarshallingHelpers.IsTypeFrozen(typeRecord))
            return true;
        return false;
    }

    /// <summary>
    /// Returns true if the type spec represents a nested Apple framework type that can't
    /// be represented in @_cdecl wrapper parameters (e.g., OuterType.InnerType).
    /// C-compatible structs (CGSize, UIEdgeInsets) work fine, but pure Swift nested types
    /// fail at wrapper compilation.
    /// </summary>
    public static bool IsNestedType(TypeSpec typeSpec)
    {
        return typeSpec is NamedTypeSpec named &&
            named.HasModule() &&
            AppleFrameworkRegistry.IsNestedType(named.Name);
    }

    /// <summary>
    /// Per-param check: is this argument a nested frozen struct?
    /// Nested type: the name after stripping the module prefix still contains a dot.
    /// e.g. "ModuleName.NestedOuter.Inner" -> "NestedOuter.Inner" (has dot = nested)
    /// vs   "ModuleName.Point" -> "Point" (no dot = top-level)
    /// </summary>
    public static bool IsNestedFrozenStructParam(ArgumentDecl arg, ITypeDatabase typeDatabase)
    {
        if (arg.SwiftTypeSpec is not NamedTypeSpec namedSpec)
            return false;
        if (!typeDatabase.TryGetTypeRecord(namedSpec, out var typeRecord))
            return false;
        if (typeRecord.Kind != TypeRecordKind.Struct)
            return false;
        if (!MarshallingHelpers.IsTypeFrozen(typeRecord))
            return false;
        var name = namedSpec.Name;
        var dotIndex = name.IndexOf('.');
        if (dotIndex >= 0 && name.Substring(dotIndex + 1).Contains('.'))
            return true;
        return false;
    }

    /// <summary>
    /// Per-param check: is this argument a non-primitive frozen struct?
    /// @_cdecl rejects "Swift structs cannot be represented in Objective-C" for custom frozen
    /// struct types. Primitives (Int, Float, Bool, CGFloat) and String are handled via
    /// GetCdeclParamMapping.
    /// </summary>
    public static bool IsNonPrimitiveFrozenStructParam(ArgumentDecl arg, ITypeDatabase typeDatabase)
    {
        var spec = arg.SwiftTypeSpec;
        if (CdeclParamMapper.IsCdeclPrimitive(spec))
            return false;
        if (spec is NamedTypeSpec strNamed && strNamed.Name == "Swift.String")
            return false;
        if (typeDatabase.TryGetTypeRecord(spec, out var typeRecord) &&
            typeRecord.Kind == TypeRecordKind.Struct &&
            MarshallingHelpers.IsTypeFrozen(typeRecord))
        {
            // System/Apple framework frozen structs (CGRect, CGSize, Foundation.Date, etc.)
            // are blittable and safe for @_cdecl by-value passing. Only custom frozen structs
            // from third-party/user libraries need UnsafeRawPointer marshalling.
            if (spec is NamedTypeSpec namedSpec && CdeclParamMapper.IsSystemFrozenStruct(namedSpec))
                return false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Shared function-level eligibility check for @_cdecl wrappers.
    /// Contains guards that apply to ALL wrapper paths (method, closure, optional-pointer, async, arrayslice).
    /// Per-param checks are NOT included — each wrapper path has its own param-level gates
    /// because different wrappers transform different param types before checking.
    /// </summary>
    public static bool HasCdeclCompatibleFunctionShape(MethodEnvironment env)
    {
        // Guard 4: xcframework mode required
        if (string.IsNullOrEmpty(env.TypeDatabase.AsyncLibraryName))
            return false;
        // Guard 5b: Generic parent type — allow non-final class instance methods with concrete signatures
        var parentTypeDecl = env.ParentDecl as TypeDecl;
        if (parentTypeDecl?.IsGeneric == true)
        {
            if (!GenericDispatchEmitter.CanEmitGenericDispatch(env, parentTypeDecl, GenericDispatchKind.Method))
                return false;
        }
        // Guard 5c: No inout parameters — write-back semantics incompatible with @_cdecl wrappers.
        // This is a function-level gate (not per-param) because no wrapper path can handle inout.
        if (env.MethodDecl.CSSignature.Skip(1).Any(a => a.IsInOut))
            return false;
        // Guard 6: No method-level generics
        if (env.MethodDecl.IsGeneric)
            return false;
        // Guard 6a: Raw generic type params in signature (e.g., from parent generics leaking)
        if (HasRawGenericTypeParams(env.MethodDecl))
            return false;
        // Guard 6b: Not actor parent
        if (parentTypeDecl is ClassDecl { IsActor: true })
            return false;
        // Guard 10: No variadic parameters — Swift variadic (T...) appears as Array<T> in ABI JSON.
        // Wrapper would pass [T] where T... is expected, causing "cannot pass array" compile error.
        if (env.MethodDecl.HasVariadicParameter)
            return false;
        // Guard 11: Not non-copyable struct parent
        if (IsNonCopyableStructParent(env.ParentDecl))
            return false;
        // Guards 15-15d: Return type checks
        var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;
        if (returnSpec is ProtocolListTypeSpec { IsOpaque: true })
            return false;
        // Closure returns: blocked here because wrapper-owned trampoline paths (ClosureEmitter,
        // OptionalPointerWrapper, ArraySliceNormalization) use this predicate to check function shape.
        // MethodWrapperEmitter.ShouldEmitWrapper allows closure returns since Session 5, but the
        // trampoline paths don't handle closure return marshalling (they delegate to the method wrapper).
        if (returnSpec is ClosureTypeSpec)
            return false;
        // Tuple returns: allowed — routed through IndirectResult (resultPtr buffer).
        // DynamicSelf returns: allowed for class parents — Self resolves to parent class type.
        // Structs/enums with DynamicSelf blocked — Unmanaged requires class type.
        if (returnSpec.IsDynamicSelf && env.ParentDecl is not ClassDecl)
            return false;
        // Guard 17: Nested type returns — ALLOWED. @_cdecl wrapper return types are always
        // C-compatible (Int32, UnsafeMutableRawPointer, void+resultPtr). Nested type names only
        // appear in function bodies (initializeMemory(as:), rawValue casts), which is valid Swift.

        // Guard 10: No protocol existential return
        if (CdeclParamMapper.IsProtocolExistentialType(returnSpec, env.TypeDatabase))
            return false;
        return true;
    }

    /// <summary>
    /// Returns true if the given parent type declaration is a generic class type.
    /// Used to determine whether protocol-based type erasure is needed in emission.
    /// </summary>
    public static bool IsGenericClassParent(BaseDecl? parentDecl)
    {
        return parentDecl is ClassDecl cd && cd.IsGeneric;
    }

    /// <summary>
    /// Returns true if the given parent type declaration is any generic type (class or struct).
    /// Used to determine whether generic wrapper emission is needed.
    /// </summary>
    public static bool IsGenericParent(BaseDecl? parentDecl)
    {
        return parentDecl is TypeDecl td && td.IsGeneric;
    }

    /// <summary>
    /// Centralized generic dispatch guard: determines whether a member on a generic parent
    /// type needs the static protocol dispatch pattern (protocol with static method + metatype cast)
    /// as opposed to instance protocol dispatch (protocol conformance + existential cast).
    ///
    /// The decision differs by member kind:
    /// - Method: delegates to <see cref="MethodWrapperEmitter.NeedsGenericStaticDispatch"/> —
    ///   needs static dispatch when parent is generic struct, or class with T in signature.
    /// - Property: needs static dispatch when parent is generic struct (not class),
    ///   OR when parent is generic class but the property type references T.
    /// - Constructor: delegates to <see cref="ConstructorWrapperEmitter.NeedsGenericStaticFactory"/> —
    ///   needs static factory when parent is generic struct, or class with T in constructor params.
    ///
    /// Returns false if the parent is not generic, or if the parent type declaration is null.
    /// </summary>
    /// <param name="env">The method environment (contains ParentDecl, MethodDecl).</param>
    /// <param name="memberKind">Which kind of member is being checked.</param>
    /// <param name="propertyDecl">Required for Property kind — the property being checked.</param>
    /// <returns>True if static dispatch is needed; false if instance dispatch suffices or no generic dispatch is needed.</returns>
    public static bool NeedsGenericDispatch(
        MethodEnvironment env,
        MemberKind memberKind,
        PropertyDecl? propertyDecl = null)
    {
        if (!IsGenericParent(env.ParentDecl))
            return false;

        var parentTypeDecl = env.ParentDecl as TypeDecl;
        if (parentTypeDecl == null)
            return false;

        switch (memberKind)
        {
            case MemberKind.Method:
                return GenericDispatchEmitter.NeedsStaticDispatch(env, parentTypeDecl, GenericDispatchKind.Method);

            case MemberKind.Property:
                if (propertyDecl != null)
                    return GenericDispatchEmitter.NeedsStaticDispatchForProperty(env, parentTypeDecl, propertyDecl);
                return GenericDispatchEmitter.NeedsStaticDispatch(env, parentTypeDecl, GenericDispatchKind.PropertyGetter);

            case MemberKind.Constructor:
                return GenericDispatchEmitter.NeedsStaticDispatch(env, parentTypeDecl, GenericDispatchKind.Constructor);

            default:
                return false;
        }
    }

    /// <summary>
    /// Recursively checks whether a TypeSpec references any of the given generic type parameter names.
    /// Handles NamedTypeSpec (including generic parameters), ClosureTypeSpec, TupleTypeSpec,
    /// ProtocolListTypeSpec, and AssociatedTypeReferenceSpec.
    /// </summary>
    public static bool TypeSpecReferencesGenericParam(TypeSpec spec, HashSet<string> genericParamNames)
    {
        if (spec is NamedTypeSpec named)
        {
            if (genericParamNames.Contains(named.Name))
                return true;
            foreach (var gp in named.GenericParameters)
            {
                if (TypeSpecReferencesGenericParam(gp, genericParamNames))
                    return true;
            }
        }
        else if (spec is ClosureTypeSpec closure)
        {
            if (TypeSpecReferencesGenericParam(closure.ReturnType, genericParamNames))
                return true;
            if (TypeSpecReferencesGenericParam(closure.Arguments, genericParamNames))
                return true;
        }
        else if (spec is TupleTypeSpec tuple)
        {
            foreach (var elem in tuple.Elements)
            {
                if (TypeSpecReferencesGenericParam(elem, genericParamNames))
                    return true;
            }
        }
        else if (spec is ProtocolListTypeSpec protocolList)
        {
            foreach (var proto in protocolList.Protocols.Keys)
            {
                if (TypeSpecReferencesGenericParam(proto, genericParamNames))
                    return true;
            }
        }
        else if (spec is AssociatedTypeReferenceSpec assocRef)
        {
            // Associated type references like τ_0_0.Element reference the base generic param.
            if (genericParamNames.Contains(assocRef.BaseType))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Diagnostic method: runs through the MethodWrapperEmitter.ShouldEmitWrapper guards in order
    /// and returns the name of the first guard that rejects the method. Returns null if no guard
    /// rejects (the method would be wrapped). For logging/debugging only.
    /// </summary>
    public static string? GetRejectionReason(MethodEnvironment env)
    {
        // 1. Must NOT be a constructor
        if (env.MethodDecl.IsConstructor)
            return "constructor";

        // 2. Must NOT be an accessor
        if (env.MethodDecl.IsAccessor)
            return "accessor";

        // 3. Must NOT already have a cdecl property wrapper
        if (env.MethodDecl.UsesCdeclPropertyWrapper)
            return "cdecl_property_wrapper";

        // 3b. Skip @_spi protected methods
        if (env.MethodDecl.IsSpiProtected)
            return "spi_protected";

        // 3c. Skip internal methods — wrapper can't call them from external code
        if (env.MethodDecl.IsModuleInternal)
            return "module_internal";

        // 4. xcframework mode required
        if (!IsXCFrameworkMode(env.TypeDatabase))
            return "xcframework_mode";

        // 5. Must be on a type or module (free function)
        var parentTypeDecl = env.ParentDecl as TypeDecl;
        if (parentTypeDecl == null && env.ParentDecl is not ModuleDecl)
            return "no_parent";

        // 5b. Generic parent type
        if (parentTypeDecl?.IsGeneric == true)
        {
            if (IsInheritedGenericContext(parentTypeDecl))
                return "inherited_generic_context";
            if (!GenericDispatchEmitter.CanEmitGenericDispatch(env, parentTypeDecl, GenericDispatchKind.Method))
                return "generic_parent";
        }

        // 6. No method-level generics
        if (env.MethodDecl.IsGeneric)
            return "method_level_generics";

        // 6a. Raw generic type params in signature (e.g., from parent generics leaking)
        if (HasRawGenericTypeParams(env.MethodDecl))
            return "raw_generic_type_params";

        // 6b. Custom actor types (requires async dispatch)
        if (parentTypeDecl is ClassDecl { IsActor: true })
            return "actor_type";

        // 6c. Per-member custom actor isolation (not @MainActor)
        if (env.MethodDecl.IsActorIsolated && !env.MethodDecl.IsMainActorIsolated)
            return "custom_actor_isolated";

        // 7. Not async
        if (env.MethodDecl.IsAsync)
            return "async_method";

        // 8. Closure parameters
        if (env.MethodDecl.CSSignature.Skip(1).Any(env.ClosureHandler.IsClosure))
        {
            if (!ClosureEmitter.NeedsClosureCdeclWrapper(env.MethodDecl, env.ClosureHandler))
                return "closure_params";
            if (HasAnyAsyncClosure(env))
                return "closure_params";
        }

        // 11. Non-copyable struct guards
        if (IsNonCopyableStructParent(env.ParentDecl))
            return "non_copyable_struct";

        // 11b. No inout parameters
        if (env.MethodDecl.CSSignature.Skip(1).Any(a => a.IsInOut))
            return "inout_params";

        // 11c. No variadic parameters
        if (env.MethodDecl.HasVariadicParameter)
            return "variadic_params";

        // 12. No nested frozen struct parameters
        if (env.MethodDecl.CSSignature.Skip(1).Any(arg => IsNestedFrozenStructParam(arg, env.TypeDatabase)))
            return "nested_frozen_struct_param";

        // 12b. Non-primitive frozen struct parameters are now handled via UnsafeRawPointer
        // in @_cdecl wrappers — no longer a rejection reason.

        // 13. Not already using wrapper library
        if (env.MethodDecl.UsesWrapperLibrary)
            return "uses_wrapper_library";

        // 14. No unsupported generic container params/returns
        {
            var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;
            if (IsUnsupportedGenericContainer(returnSpec, env.TypeDatabase))
                return "unsupported_generic_container";
            foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
            {
                if (IsUnsupportedGenericContainer(arg.SwiftTypeSpec, env.TypeDatabase))
                    return "unsupported_generic_container";
            }
        }

        // 14b. No metatype parameters
        if (env.MethodDecl.CSSignature.Skip(1).Any(a => IsMetatypeType(a.SwiftTypeSpec)))
            return "metatype_param";

        // 14c-15: Return type checks
        {
            var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;

            if (IsMetatypeType(returnSpec))
                return "metatype_return";

            if (returnSpec is ProtocolListTypeSpec { IsOpaque: true })
                return "opaque_return";

            // 15d. DynamicSelf returns: only allowed for class parents
            if (returnSpec.IsDynamicSelf && env.ParentDecl is not ClassDecl)
                return "dynamic_self_non_class";

            // 17. Nested type returns — ALLOWED (see HasCdeclCompatibleFunctionShape guard 17)
        }

        return null;
    }

    /// <summary>
    /// Returns true if any parameter or return type in the method signature contains
    /// raw ABI generic type parameters (τ_0_0, τ_1_0, etc.) that would cause Swift
    /// compilation failures. Uses the same TypeSpec traversal as EveryProtocolEmitter.
    /// </summary>
    public static bool HasRawGenericTypeParams(MethodDecl methodDecl)
    {
        foreach (var arg in methodDecl.CSSignature)
        {
            if (arg.SwiftTypeSpec != null && ContainsRawGenericTypeParam(arg.SwiftTypeSpec))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Recursively checks if a TypeSpec contains a raw ABI generic type parameter.
    /// Public so property/subscript wrapper emitters can check individual type specs.
    /// </summary>
    public static bool ContainsRawGenericTypeParam(TypeSpec typeSpec)
    {
        switch (typeSpec)
        {
            case NamedTypeSpec named:
                if (TypeSpecHelpers.IsGenericTypeParameter(named.Name))
                    return true;
                foreach (var gp in named.GenericParameters)
                {
                    if (ContainsRawGenericTypeParam(gp))
                        return true;
                }
                return false;

            case TupleTypeSpec tuple:
                foreach (var elem in tuple.Elements)
                {
                    if (ContainsRawGenericTypeParam(elem))
                        return true;
                }
                return false;

            case ClosureTypeSpec closure:
                if (ContainsRawGenericTypeParam(closure.Arguments))
                    return true;
                if (ContainsRawGenericTypeParam(closure.ReturnType))
                    return true;
                return false;

            case ProtocolListTypeSpec protocolList:
                foreach (var proto in protocolList.Protocols.Keys)
                {
                    if (ContainsRawGenericTypeParam(proto))
                        return true;
                }
                return false;

            case AssociatedTypeReferenceSpec assocType:
                return TypeSpecHelpers.IsGenericTypeParameter(assocType.BaseType);

            default:
                return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CallConvSwift ABI Safety — determines whether @_cdecl is REQUIRED
    // for a method/constructor/property to avoid ABI mismatches.
    // Orthogonal to ShouldEmitWrapper() validation gates.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Determines whether a method requires @_cdecl wrapping for ABI safety or functional reasons.
    /// Returns true when any parameter or return type would cause an ABI mismatch
    /// if used directly with CallConvSwift on ARM64, or when the method has closure params
    /// that require the @_cdecl adapter mechanism.
    ///
    /// Decision framework (from NativeAOT investigation evidence matrix):
    /// - Non-blittable param (SafeHandle: non-frozen struct, complex enum) → required
    /// - ValueTuple param/return → required (StructLayout.Auto)
    /// - Custom struct with float/double fields → required (param AND return)
    /// - Custom integer struct > 16 bytes → required (param only)
    /// - Closure params → required (adapter mechanism only works inside @_cdecl wrappers)
    /// - Everything else → CallConvSwift safe
    /// </summary>
    public static bool RequiresCdeclForAbiSafety(MethodEnvironment env)
    {
        // Typed throws (throws(ErrorType)) requires @_cdecl wrapper because the swifterror
        // register contains the raw typed error value, NOT a boxed Error existential.
        // Our error extraction code (SBW_GetErrorDescription, SBW_ExtractTypedError) uses
        // Unmanaged<AnyObject>.fromOpaque() which crashes on raw enum/struct values.
        // The @_cdecl wrapper catches the error in Swift and boxes it properly.
        if (env.MethodDecl.HasTypedThrows)
            return true;

        // Generic type constructors need @_cdecl wrapper for metatype dispatch.
        // The @_cdecl wrapper builds the specialized metatype (e.g., Wrapper<T>.self) from
        // raw metadata pointers and dispatches the init via protocol cast. Without this,
        // C# calls the mangled allocating init symbol directly via CallConvSwift, which
        // crashes on both Mono (no CallConvSwift) and NativeAOT (missing metatype).
        // Exclude nested types that only inherit their parent's generic context
        // (e.g., AuthenticationInterceptor<A>.RefreshWindow) — the @_cdecl extension can't
        // bind the parent's unresolved generic context. Detect this by checking whether the
        // outer parent type is also generic with the same parameter names.
        if (env.MethodDecl.IsConstructor && env.ParentDecl is TypeDecl genericParent && genericParent.IsGeneric
            && !IsInheritedGenericContext(genericParent))
            return true;

        // Class methods need @_cdecl in two cases:
        // 1. Static methods: Swift's @convention(method) passes @thick Self.Type (metatype)
        //    as a hidden parameter. The C# P/Invoke doesn't include this parameter, so the
        //    direct call reads garbage from the metatype register → SIGSEGV.
        // 2. Non-final instance methods: use Tj dispatch thunks (vtable indirection).
        //    Direct CallConvSwift against Tj symbols crashes on both Mono and NativeAOT.
        // Final class instance methods use direct symbols with SwiftSelf — safe for CallConvSwift.
        if (env.ParentDecl is ClassDecl classDecl)
        {
            if (env.MethodDecl.MethodType == MethodType.Static)
                return true;  // Hidden metatype parameter
            if (!classDecl.IsFinal && !env.MethodDecl.IsFinal)
                return true;  // Tj dispatch thunk
        }

        // Non-frozen struct instance members: C# projects these as ClassWithOpaquePayload
        // (SafeHandle/IntPtr self), but the Swift ABI expects SwiftSelf<T> (struct by value
        // in registers). CallConvSwift with IntPtr self doesn't match — crashes on Mono.
        // @_cdecl wrapper bridges this by accepting UnsafeRawPointer self_ and extracting
        // the struct value with .load(as:) or .assumingMemoryBound(to:).pointee.
        if (IsNonFrozenStructInstanceMember(env))
            return true;

        // Check self type for instance methods on frozen structs.
        // SwiftSelf<T> passes the struct by value in registers — if the struct has
        // float fields, Mono/NativeAOT may put them in wrong registers (GPR vs FPR).
        if (IsSelfTypeCdeclRequired(env))
            return true;

        // Check return type
        var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;
        if (!returnSpec.IsEmptyTuple && IsReturnTypeCdeclRequired(returnSpec, env.TypeDatabase))
            return true;

        // Check parameters
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            // Closure params require @_cdecl for the adapter mechanism (converting C# delegates
            // to Swift closures via function pointer + context pair). This is a functional
            // requirement, not ABI safety, but the wrapper is still required.
            if (env.ClosureHandler.IsClosure(arg))
                return true;

            if (IsParamTypeCdeclRequired(arg.SwiftTypeSpec, env))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Property-specific overload: checks the property type for ABI safety.
    /// Getter return = property type, setter param = property type.
    /// </summary>
    public static bool RequiresCdeclForAbiSafety(MethodEnvironment env, PropertyDecl propertyDecl)
    {
        // Non-final class property accessors use Tj dispatch thunks (vtable indirection).
        // Same as method path — direct CallConvSwift crashes on both runtimes.
        if (env.ParentDecl is ClassDecl classDecl &&
            !classDecl.IsFinal && !propertyDecl.IsFinal)
            return true;

        // Non-frozen struct instance properties: same as method path — C# has IntPtr self
        // but Swift expects SwiftSelf<T> (struct by value). @_cdecl required.
        if (!propertyDecl.IsStatic && IsNonFrozenStructInstanceMember(env))
            return true;

        // Check self type for properties on frozen structs (SwiftSelf<T> passes struct by value)
        if (IsSelfTypeCdeclRequired(env))
            return true;

        var typeSpec = propertyDecl.SwiftTypeSpec;

        // Check as return type (getter)
        if (IsReturnTypeCdeclRequired(typeSpec, env.TypeDatabase))
            return true;

        // Check as parameter type (setter)
        if (propertyDecl.Accessors.OfType<SetAccessorDecl>().Any())
        {
            if (IsParamTypeCdeclRequired(typeSpec, env))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Determines whether a closure parameter should be treated as effectively escaping.
    /// Optional closures in Swift are always escaping by definition — there is no
    /// <c>@noescape Optional&lt;Closure&gt;</c>. The ABI parser only propagates the escaping
    /// attribute to top-level ClosureTypeSpec nodes, not those wrapped in Optional, so
    /// callers must check both <c>IsEscaping</c> and <c>IsOptionalClosure</c>.
    /// </summary>
    /// <param name="closureTypeSpec">The inner ClosureTypeSpec (unwrapped from Optional if applicable).</param>
    /// <param name="originalType">The original argument's SwiftTypeSpec (may be Optional&lt;Closure&gt;).</param>
    /// <param name="closureHandler">The closure handler for Optional detection.</param>
    /// <returns><c>true</c> if the closure is escaping or wrapped in Optional; otherwise <c>false</c>.</returns>
    public static bool IsEffectivelyEscaping(ClosureTypeSpec closureTypeSpec, TypeSpec originalType, ClosureHandler closureHandler)
    {
        return closureTypeSpec.IsEscaping || closureHandler.IsOptionalClosure(originalType);
    }

    /// <summary>
    /// Determines whether the member is an instance member on a non-frozen struct parent.
    /// Non-frozen structs are projected as ClassWithOpaquePayload in C# (SafeHandle/IntPtr self),
    /// but the Swift ABI expects SwiftSelf&lt;T&gt; (struct by value in registers). The mismatch
    /// means CallConvSwift with IntPtr self crashes on Mono. @_cdecl wrappers bridge this gap
    /// by accepting UnsafeRawPointer self_ and using .load(as:) or .assumingMemoryBound(to:).pointee.
    /// Constructors are exempt — they use SwiftIndirectResult, not SwiftSelf.
    /// </summary>
    internal static bool IsNonFrozenStructInstanceMember(MethodEnvironment env)
    {
        // Only instance members (not static, not constructors)
        if (env.MethodDecl.MethodType == MethodType.Static || env.MethodDecl.IsConstructor)
            return false;

        if (env.ParentDecl is not TypeDecl parentType)
            return false;

        var parentNamedSpec = new NamedTypeSpec(parentType.SwiftTypeName.ModuleQualifiedName);
        if (!env.TypeDatabase.TryGetTypeRecord(parentNamedSpec, out var parentRecord))
            return false;

        // Non-frozen structs: projected as ClassWithOpaquePayload, IntPtr self ≠ SwiftSelf<T>
        return parentRecord.Kind == TypeRecordKind.Struct && !MarshallingHelpers.IsTypeFrozen(parentRecord);
    }

    /// <summary>
    /// Returns true if a generic type's generic parameters are inherited from an outer generic
    /// parent rather than declared on the type itself. For example, AuthenticationInterceptor&lt;A&gt;.RefreshWindow
    /// inherits A from its parent — the @_cdecl extension can't bind the parent's unresolved context.
    /// A truly generic nested type like Outer.Inner&lt;T&gt; declares its own T independent of Outer.
    ///
    /// Used by constructor, method, and property wrapper emission to skip @_cdecl wrappers for
    /// nested types with inherited generic context. The protocol conformance extension
    /// (e.g., "extension Outer.Inner: Protocol {}") won't compile when the outer type has
    /// unresolved generic parameters.
    /// </summary>
    internal static bool IsInheritedGenericContext(TypeDecl typeDecl)
    {
        if (typeDecl.ParentDecl is not TypeDecl outerType)
            return false; // Top-level type → own generic params

        if (!outerType.IsGeneric)
            return false; // Non-generic parent → own generic params

        // If the outer parent is generic and the nested type's generic params are a subset
        // of (or equal to) the parent's, the context is inherited. A nested type with its own
        // generic params would have params not present in the parent.
        var outerParamNames = outerType.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();

        return typeDecl.GenericParameters
            .All(p => outerParamNames.Contains(p.TypeName));
    }

    /// <summary>
    /// Returns true if a TypeRecord has float or bool field flags, which are incompatible
    /// with .NET's CallConvSwift register assignment:
    /// - Float fields → GPR/FPR mismatch on NativeAOT
    /// - Bool fields → non-blittable in .NET CallConvSwift
    /// </summary>
    internal static bool HasIncompatibleFields(TypeRecord typeRecord)
    {
        if (typeRecord.Flags.HasFlag(TypeRecordFlags.HasFloatFields))
            return true;
        if (typeRecord.Flags.HasFlag(TypeRecordFlags.HasBoolFields))
            return true;
        return false;
    }

    /// <summary>
    /// Determines whether the self/parent type requires @_cdecl for ABI safety.
    /// For instance methods/properties on frozen structs, SwiftSelf&lt;T&gt; passes the struct
    /// by value in registers. If the struct has float fields, the GPR/FPR register
    /// assignment differs between Swift and Mono/NativeAOT CallConvSwift stubs.
    /// </summary>
    internal static bool IsSelfTypeCdeclRequired(MethodEnvironment env)
    {
        // Only applies to instance members on frozen structs (SwiftSelf<T> by-value self)
        // Class/protocol instance methods use IntPtr self (always safe)
        if (env.ParentDecl is not TypeDecl parentType)
            return false;

        var parentNamedSpec = new NamedTypeSpec(parentType.SwiftTypeName.ModuleQualifiedName);
        if (!env.TypeDatabase.TryGetTypeRecord(parentNamedSpec, out var parentRecord))
            return false;

        // Only frozen structs pass self by value via SwiftSelf<T>
        if (parentRecord.Kind != TypeRecordKind.Struct || !MarshallingHelpers.IsTypeFrozen(parentRecord))
            return false;

        // System frozen structs (CGRect, etc.) have special runtime handling — safe
        if (CdeclParamMapper.IsSystemFrozenStruct(parentNamedSpec))
            return false;

        // Custom frozen struct with float/bool fields → incompatible with .NET CallConvSwift
        if (HasIncompatibleFields(parentRecord))
            return true;

        // Custom frozen struct > MaxSelfSize bytes passed by value via SwiftSelf<T> → multi-register
        // Mono JIT can't generate correct CallConvSwift stubs for multi-register self params.
        // The MaxParamSize param threshold doesn't apply here — SwiftSelf<T> register layout is
        // different from regular parameter passing.
        if (parentRecord.InlineSize.HasValue && parentRecord.InlineSize.Value > AbiSizeLimits.MaxSelfSize)
            return true;

        // When InlineSize is unavailable (metadata couldn't be resolved, e.g. simulator dylib on macOS),
        // use the parent struct's stored property count as a heuristic. Multiple stored properties
        // means multiple fields → likely > 8 bytes → require @_cdecl for safety.
        if (!parentRecord.InlineSize.HasValue && parentType is StructDecl structDecl)
        {
            var storedPropertyCount = structDecl.Properties.Count(p => p.HasStorage && !p.IsStatic);
            if (storedPropertyCount > 1)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Determines whether a parameter type requires @_cdecl for ABI safety.
    /// Checks: non-blittable (SafeHandle), ValueTuple, custom float struct, large integer struct.
    /// </summary>
    internal static bool IsParamTypeCdeclRequired(TypeSpec typeSpec, MethodEnvironment env)
    {
        // Primitives are always safe
        if (CdeclParamMapper.IsCdeclPrimitive(typeSpec))
            return false;

        // ValueTuple → StructLayout.Auto → @_cdecl required
        if (typeSpec is TupleTypeSpec tts && !tts.IsEmptyTuple)
            return true;

        // Generic containers (Array, Dict, Set, Optional) → non-blittable in CallConvSwift
        if (CdeclParamMapper.IsGenericContainerType(typeSpec))
            return true;

        // Look up TypeRecord for further classification
        if (!env.TypeDatabase.TryGetTypeRecord(typeSpec, out var typeRecord))
            return false; // Unknown type, let existing gates handle

        // Non-frozen struct → SafeHandle → non-blittable → @_cdecl required
        if (typeRecord.Kind == TypeRecordKind.Struct && !MarshallingHelpers.IsTypeFrozen(typeRecord))
            return true;

        // Complex enum → SafeHandle → non-blittable → @_cdecl required
        if (typeRecord.Kind == TypeRecordKind.Enum && !typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            return true;

        // Frozen struct classification
        if (typeRecord.Kind == TypeRecordKind.Struct && MarshallingHelpers.IsTypeFrozen(typeRecord))
        {
            // System types from C-bridging modules (CoreGraphics, CoreFoundation, Darwin, simd)
            // are pure C structs with well-defined register layouts — safe at any size.
            // Evidence matrix: CGRect (32B) passes on both Mono and NativeAOT.
            // Only Swift-module system types (String = 16 bytes) need the > 8 byte restriction
            // because Mono JIT can't generate correct CallConvSwift stubs for them.
            if (typeSpec is NamedTypeSpec named && CdeclParamMapper.IsSystemFrozenStruct(named))
            {
                if (IsCBridgingModuleType(named))
                    return false;  // Pure C struct — safe at any size
                return typeRecord.InlineSize.HasValue && typeRecord.InlineSize.Value > 8;
            }

            // Custom struct with float/bool fields → incompatible with .NET CallConvSwift
            if (HasIncompatibleFields(typeRecord))
                return true;

            // Custom integer struct > MaxParamSize bytes → NativeAOT SIGSEGV
            if (typeRecord.InlineSize.HasValue && typeRecord.InlineSize.Value > AbiSizeLimits.MaxParamSize)
                return true;

            // Custom integer struct ≤ 16 bytes → safe
            return false;
        }

        // Classes, ObjC bridged, simple enums → IntPtr → safe
        return false;
    }

    /// <summary>
    /// Determines whether a return type requires @_cdecl for ABI safety.
    /// Only custom frozen structs with float fields returned by value need @_cdecl;
    /// other return types use SwiftIndirectResult or IntPtr which are safe.
    /// </summary>
    internal static bool IsReturnTypeCdeclRequired(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        // Primitives are always safe
        if (CdeclParamMapper.IsCdeclPrimitive(typeSpec))
            return false;

        // ValueTuple → StructLayout.Auto → @_cdecl required
        if (typeSpec is TupleTypeSpec tts && !tts.IsEmptyTuple)
            return true;

        // Closures → 16-byte SwiftClosureData struct returned via CallConvSwift crashes Mono JIT
        // (`!ji->async` assertion on multi-register struct return) and NativeAOT (SIGSEGV).
        // Route through @_cdecl with indirect result (resultPtr buffer).
        if (typeSpec is ClosureTypeSpec)
            return true;

        // Generic containers → in CallConvSwift, returns use SwiftIndirectResult → safe
        // (Array, Dict, Set, Optional all go through indirect result)
        // But they use SafeHandle in the CallConvSwift param path, which is different from return.
        // For returns, non-frozen types go through IntPtr/IndirectResult → safe.

        // Look up TypeRecord for further classification
        if (!typeDatabase.TryGetTypeRecord(typeSpec, out var typeRecord))
            return false;

        // Non-frozen struct returns → IndirectResult/IntPtr in CallConvSwift → safe
        // Class returns → IntPtr → safe

        // Non-simple enum returns (enums with associated values / payloads) use
        // SwiftIndirectResult marshalling which crashes Mono JIT. Route through @_cdecl
        // wrapper. This matches the !SimpleEnum classification used elsewhere in the
        // generator (MethodMarshalPlanBuilder, TypeProjectionFactory).
        if (typeRecord.Kind == TypeRecordKind.Enum &&
            !typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            return true;

        // Frozen struct with float fields returned BY VALUE → Mono SIGSEGV
        // Only applies to pure frozen structs (no RequiresMemoryManagement) since
        // those with memory management use IndirectResult.
        if (typeRecord.Kind == TypeRecordKind.Struct &&
            MarshallingHelpers.IsTypeFrozen(typeRecord) &&
            !MarshallingHelpers.RequiresMemoryManagement(typeRecord))
        {
            // System types from C-bridging modules (CoreGraphics, etc.) — safe at any size.
            // Only Swift-module system types (String = 16 bytes) need the > 8 byte restriction.
            if (typeSpec is NamedTypeSpec named && CdeclParamMapper.IsSystemFrozenStruct(named))
            {
                if (IsCBridgingModuleType(named))
                    return false;  // Pure C struct — safe at any size
                return typeRecord.InlineSize.HasValue && typeRecord.InlineSize.Value > 8;
            }

            // Custom struct with float/bool fields → incompatible with .NET CallConvSwift
            if (HasIncompatibleFields(typeRecord))
                return true;
        }

        // System frozen struct > 8 bytes with memory management (e.g., String = 16 bytes)
        // returned by value as Buffer struct — Mono JIT can't handle multi-register CallConvSwift.
        // C-bridging module types don't have memory management (pure C structs), so this
        // only applies to Swift-module types like String.
        if (typeRecord.Kind == TypeRecordKind.Struct &&
            MarshallingHelpers.IsTypeFrozen(typeRecord) &&
            typeSpec is NamedTypeSpec namedRet && CdeclParamMapper.IsSystemFrozenStruct(namedRet) &&
            !IsCBridgingModuleType(namedRet) &&
            typeRecord.InlineSize.HasValue && typeRecord.InlineSize.Value > 8)
            return true;

        return false;
    }

    /// <summary>
    /// Returns true for types from C-bridging modules (CoreGraphics, CoreFoundation, Darwin, simd).
    /// These modules expose pure C structs via Swift overlays — they have well-defined, platform-stable
    /// register layouts that both Mono and NativeAOT handle correctly at any size.
    /// Evidence: CGRect (32 bytes) passes on both runtimes (evidence matrix, Session 2).
    ///
    /// Does NOT include Swift, ObjectiveC, or _Concurrency modules — those contain types with
    /// internal complexity (e.g., Swift.String at 16 bytes) that Mono JIT may not handle correctly
    /// in multi-register CallConvSwift stubs.
    /// </summary>
    internal static bool IsCBridgingModuleType(NamedTypeSpec typeSpec)
    {
        if (!typeSpec.HasModule())
            return false;
        var module = SwiftTypeName.FromTypeSpec(typeSpec).Module;
        return module is "CoreGraphics" or "CoreFoundation" or "Darwin" or "simd";
    }

    /// <summary>
    /// Creates a mapping from ABI generic parameter names (τ_0_0, τ_0_1) to their sugared
    /// source names (T, Element, U) from the type declaration's GenericParameters.
    /// </summary>
    public static Dictionary<string, string> GetAbiToSugaredNameMap(TypeDecl parentTypeDecl)
    {
        return parentTypeDecl.GenericParameters
            .Where(p => !string.IsNullOrEmpty(p.SugaredTypeName))
            .ToDictionary(p => p.TypeName, p => p.SugaredTypeName);
    }

    /// <summary>
    /// Renders a TypeSpec using sugared source names (T, Element) instead of ABI names (τ_0_0).
    /// Used inside protocol extension bodies where Swift's source-level names are in scope,
    /// not the ABI-level mangled names.
    /// </summary>
    public static string RenderSwiftTypeSpecWithSugaredNames(TypeSpec typeSpec, Dictionary<string, string> abiToSugaredName)
    {
        if (typeSpec is NamedTypeSpec named && abiToSugaredName.TryGetValue(named.Name, out var sugared))
            return sugared;
        return ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);
    }
}
