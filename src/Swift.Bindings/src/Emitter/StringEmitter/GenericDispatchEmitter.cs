// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Specifies which kind of generic member dispatch is being evaluated or emitted.
/// </summary>
internal enum GenericDispatchKind
{
    Method,
    PropertyGetter,
    PropertySetter,
    Constructor
}

/// <summary>
/// Unified eligibility rules for generic dispatch patterns (protocol-based type erasure).
/// Consolidates guard logic previously spread across MethodWrapperEmitter, ConstructorWrapperEmitter,
/// PropertyWrapperEmitter, and WrapperValidation into a single discoverable location.
///
/// Three-level eligibility:
/// 1. <see cref="CanEmitGenericDispatch"/> — can ANY generic wrapper be emitted?
/// 2. <see cref="NeedsStaticDispatch"/> — does this member need the STATIC protocol pattern?
/// 3. <see cref="CanEmitStaticDispatch"/> — can the static pattern handle this specific member?
///
/// Emission remains in the individual emitters (PropertyWrapperEmitter, MethodWrapperEmitter,
/// ConstructorWrapperEmitter) because each kind has structurally different output: different
/// metadata ordering, self handling, return handling, and error paths.
/// </summary>
internal static class GenericDispatchEmitter
{
    // ═══════════════════════════════════════════════════════════════════════
    // Level 1: Can ANY generic wrapper be emitted?
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when a generic member can use ANY wrapper dispatch (instance or static).
    /// Replaces MethodWrapperEmitter.CanEmitGenericWrapper,
    ///          ConstructorWrapperEmitter.CanEmitGenericConstructorWrapper,
    ///          and the property eligibility path in PropertyWrapperEmitter.
    /// Called from WrapperValidation.ShouldEmitWrapper (generic_parent guard).
    /// </summary>
    internal static bool CanEmitGenericDispatch(
        MethodEnvironment env, TypeDecl parentTypeDecl, GenericDispatchKind kind)
    {
        // The two wrapper-helper gates (HasUnresolvableTypeConformances and
        // WouldExceedRegisterArgumentThreshold) only apply to dispatch paths that
        // actually route through MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded.
        // They are pushed into the per-path branches below so that "safe" paths —
        // concrete-signature generic class instance methods, concrete properties on
        // generic classes — are NOT rejected just because the parent type has e.g.
        // an associated-type conformance or >3 register args. Those safe paths use
        // SelfReconstructionEmitter.EmitProtocolCast and never touch _sbw_meta_*.
        // Dynamic PWT resolution and buffer-mode ABI are tracked in
        // src/docs/roadmap.md.

        switch (kind)
        {
            case GenericDispatchKind.Method:
            {
                // Path 1: Generic class with concrete (non-T-referencing) signature —
                // instance dispatch via SelfReconstructionEmitter.EmitProtocolCast at
                // MethodWrapperEmitter.cs:495. Does NOT call EmitMetadataAccessorHelperIfNeeded,
                // so the wrapper-helper gates DO NOT apply here.
                if (parentTypeDecl is ClassDecl && env.MethodDecl.MethodType != MethodType.Static)
                {
                    if (!HasGenericTypeParamInSignature(env, parentTypeDecl))
                        return true;
                }

                // Path 2: Static protocol dispatch routes through EmitGenericStaticDispatchMethod,
                // which calls EmitMetadataAccessorHelperIfNeeded. Apply the wrapper-helper gates
                // before delegating to CanEmitStaticDispatch.
                if (HasWrapperHelperGateBlocker(parentTypeDecl, env.TypeDatabase, GenericDispatchKind.Method))
                    return false;
                return CanEmitStaticDispatch(env, parentTypeDecl, GenericDispatchKind.Method);
            }

            case GenericDispatchKind.Constructor:
            {
                // BOTH constructor paths route through EmitMetadataAccessorHelperIfNeeded:
                //  • Path 1 (concrete params, final class) — ConstructorWrapperEmitter.cs:532
                //  • Path 2 (static factory)               — ConstructorWrapperEmitter.cs:1050
                // The wrapper-helper gates apply to both, so check them up front.
                if (HasWrapperHelperGateBlocker(parentTypeDecl, env.TypeDatabase, GenericDispatchKind.Constructor))
                    return false;

                // Path 1: Generic class with concrete (non-T-referencing) params — metatype dispatch
                // via _SBW_CI_ protocol with init() requirement. Only works for final classes
                // because non-final classes can't satisfy protocol init() without `required`.
                // Non-final classes (whether or not they have T-typed params) fall through to
                // Path 2 (GSF static factory), which uses `static func _sbw_create_*` on a
                // protocol extension that calls the concrete-type init directly — no `init()`
                // protocol requirement, so no `required init` needed on the class itself.
                if (parentTypeDecl is ClassDecl classDecl)
                {
                    var genericParamNames = parentTypeDecl.GenericParameters
                        .Select(p => p.TypeName)
                        .ToHashSet();
                    bool hasGenericParams = env.MethodDecl.CSSignature.Skip(1)
                        .Any(arg => WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames));
                    if (!hasGenericParams && classDecl.IsFinal)
                        return true; // Final class with no T params: Path 1 (_SBW_CI_)
                    // Non-final classes (any param shape) and final classes with T params
                    // fall through to Path 2 (CanEmitStaticDispatch) below.
                }
                // Path 2: Static factory dispatch
                return CanEmitStaticDispatch(env, parentTypeDecl, GenericDispatchKind.Constructor);
            }

            case GenericDispatchKind.PropertyGetter:
            case GenericDispatchKind.PropertySetter:
                // Properties always have a dispatch path for generic types
                // (concrete-signature generic classes use instance dispatch,
                //  T-referencing or struct types use static dispatch).
                // The wrapper-helper gates are scoped per-property in
                // PropertyWrapperEmitter.ShouldEmitWrapper because we need the
                // PropertyDecl to know whether the property type references T
                // (only T-typed properties go through the helper-using path).
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Returns true if the parent type would trip either wrapper-helper gate:
    ///  - <see cref="MetatypeHelperEmitter.HasUnresolvableTypeConformances"/>: parent has a
    ///    Self-requirement / associated-type protocol that <c>GetResolvablePwtParameterCount</c>
    ///    silently undercounts. Calling the dlsym'd Ma symbol with too few PWT slots shifts
    ///    caller-saved registers and PAC-traps on arm64e.
    ///  - <see cref="MetatypeHelperEmitter.WouldExceedRegisterArgumentThreshold"/>: total
    ///    (num_metadata + num_pwts) > 3 forces Swift's metadata accessor into the indirect
    ///    buffer ABI. Our wrapper helper always declares the symbol as a thin function with
    ///    explicit register args, so the call would shift registers and PAC-trap.
    /// Both refuse to emit any wrapper that would route through
    /// <see cref="MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded"/>. Dynamic
    /// PWT resolution and buffer-mode ABI are tracked in <c>src/docs/roadmap.md</c>.
    /// </summary>
    internal static bool HasWrapperHelperGateBlocker(TypeDecl parentTypeDecl, ITypeDatabase typeDatabase, GenericDispatchKind kind = GenericDispatchKind.Method)
    {
        // The GSF cdecl-constructor path threads PAT / Self-requirement conformances
        // through the dynamic-PWT runtime path (Get{Proto}PWT(metadata) →
        // SwiftConformance.GetWitnessTableOrThrow) and emits an explicit UnsafeRawPointer
        // slot for each. So it only needs to refuse a parent type when the unresolvable
        // conformance lacks a descriptor symbol (no way to look the witness table up).
        // Property/Method/Subscript paths still use the strict predicate because their C#
        // call site does not yet thread dynamic PWTs to the @_cdecl wrapper — relaxing
        // them would create a Swift/C# slot-count mismatch that PAC-traps on arm64e.
        // The matching slot counts on the Constructor path use
        // <see cref="MetatypeHelperEmitter.GetTotalPwtParameterCount"/> and
        // <see cref="MetatypeHelperEmitter.WouldExceedRegisterArgumentThresholdTotal"/>.
        // Real well-known PWT-carrying protocols (_Concurrency.Actor / Swift.Error) are
        // rejected by IsProtocolAvailableForConstraint on the C# @_cdecl path, but the
        // dlsym'd ...Ma symbol still expects PWT slots for them. The three signatures
        // (Swift _pwtN, C# P/Invoke, Ma symbol) can only stay in lockstep by refusing to
        // emit a wrapper for types that carry such a conformance. Pure stdlib markers
        // (Sendable / Copyable / Escapable / SendableMetatype / BitwiseCopyable) carry no
        // witness table, never appear in ...Ma signatures, and are skipped by both the
        // gate predicate and the PWT counters; they do NOT trigger this refusal.
        if (MetatypeHelperEmitter.HasWellKnownRuntimeProtocolConformance(parentTypeDecl, typeDatabase))
            return true;

        // Nested-in-generic-outer parents: the GSF render emits
        // `Module.Outer.Inner<T, U>(...)`, but Swift wants `Module.Outer<T>.Inner<U>(...)`.
        // Until the renderer can place generic args on the correct path segment, refuse
        // these parents from both Constructor and Method/Property/Subscript dispatch.
        if (HasGenericOuterAncestor(parentTypeDecl))
            return true;

        if (kind == GenericDispatchKind.Constructor)
        {
            if (MetatypeHelperEmitter.HasUnresolvableTypeConformancesWithoutDescriptor(parentTypeDecl, typeDatabase))
                return true;
            if (MetatypeHelperEmitter.WouldExceedRegisterArgumentThresholdTotal(parentTypeDecl, typeDatabase))
                return true;
            return false;
        }

        if (MetatypeHelperEmitter.HasUnresolvableTypeConformances(parentTypeDecl, typeDatabase))
            return true;
        if (MetatypeHelperEmitter.WouldExceedRegisterArgumentThreshold(parentTypeDecl, typeDatabase))
            return true;
        return false;
    }

    // True when any ancestor type in the ParentDecl chain is generic. Used to refuse the
    // GSF render path for nested types whose outer is generic — the dotted construction
    // expression places generic args on the wrong segment.
    internal static bool HasGenericOuterAncestor(TypeDecl parentTypeDecl)
    {
        var ancestor = parentTypeDecl.ParentDecl;
        while (ancestor is TypeDecl outerType)
        {
            if (outerType.IsGeneric)
                return true;
            ancestor = outerType.ParentDecl;
        }
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Level 2: Does this member NEED the static protocol dispatch pattern?
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when a generic member needs the STATIC protocol dispatch pattern
    /// (vs instance protocol dispatch for generic classes with concrete signatures).
    /// Replaces MethodWrapperEmitter.NeedsGenericStaticDispatch,
    ///          ConstructorWrapperEmitter.NeedsGenericStaticFactory,
    ///          and WrapperValidation.NeedsGenericDispatch's per-kind switch.
    /// </summary>
    internal static bool NeedsStaticDispatch(
        MethodEnvironment env, TypeDecl parentTypeDecl, GenericDispatchKind kind)
    {
        if (!parentTypeDecl.IsGeneric) return false;

        switch (kind)
        {
            case GenericDispatchKind.Method:
            {
                // Generic class with concrete signature uses existing instance dispatch
                if (parentTypeDecl is ClassDecl && env.MethodDecl.MethodType != MethodType.Static)
                {
                    if (!HasGenericTypeParamInSignature(env, parentTypeDecl))
                        return false;
                }
                // All generic struct methods need static dispatch;
                // class methods with T in signature need it too
                return true;
            }

            case GenericDispatchKind.Constructor:
            {
                // Generic class with no T in params uses existing metatype dispatch
                // via _SBW_CI_ protocol with init() requirement — BUT only for final
                // classes. Non-final classes can't satisfy protocol init() without
                // `required`, so they must NOT use the _SBW_CI_ path. This must stay
                // in sync with CanEmitGenericDispatch which rejects non-final classes.
                if (parentTypeDecl is ClassDecl classDecl)
                {
                    if (!classDecl.IsFinal)
                        return true; // Force static factory — _SBW_CI_ won't compile

                    var genericParamNames = parentTypeDecl.GenericParameters
                        .Select(p => p.TypeName)
                        .ToHashSet();
                    bool hasGenericParams = env.MethodDecl.CSSignature.Skip(1)
                        .Any(arg => WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames));
                    if (!hasGenericParams) return false;
                }
                // All generic struct constructors need static factory approach
                return true;
            }

            case GenericDispatchKind.PropertyGetter:
            case GenericDispatchKind.PropertySetter:
            {
                // Generic struct (not class) always needs static dispatch
                if (parentTypeDecl is not ClassDecl)
                    return true;
                // Generic class: needs static dispatch only if property type references T
                // (caller must pass the propertyDecl to check — handled by the overload below)
                return false;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Property-specific overload that checks whether the property type references T.
    /// Generic class properties only need static dispatch when their type involves T.
    /// </summary>
    internal static bool NeedsStaticDispatchForProperty(
        MethodEnvironment env, TypeDecl parentTypeDecl, PropertyDecl propertyDecl)
    {
        if (!parentTypeDecl.IsGeneric) return false;

        // Generic struct always needs static dispatch
        if (parentTypeDecl is not ClassDecl)
            return true;

        // Generic class: check if property type references T
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();
        return WrapperValidation.TypeSpecReferencesGenericParam(propertyDecl.SwiftTypeSpec, genericParamNames);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Level 3: Can the static dispatch pattern handle this specific member?
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when the static dispatch pattern can handle this specific member.
    /// Replaces MethodWrapperEmitter.CanEmitGenericStaticMethodWrapper,
    ///          ConstructorWrapperEmitter.CanEmitGenericStaticFactoryWrapper.
    /// Unified logic: checks instance-only (methods), T-param simplicity,
    /// T-closure rejection (constructors), failable rejection (constructors).
    /// </summary>
    internal static bool CanEmitStaticDispatch(
        MethodEnvironment env, TypeDecl parentTypeDecl, GenericDispatchKind kind)
    {
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();

        switch (kind)
        {
            case GenericDispatchKind.Method:
            {
                // Instance methods only — static methods lack self pointer for dispatch
                if (env.MethodDecl.MethodType == MethodType.Static)
                    return false;

                // For non-class parents (structs), only allow methods that reference T in their
                // signature. Methods with concrete-only signatures may come from constrained extensions.
                //
                // EXCEPTION — Collection-family conformers. Generic structs conforming to
                // Swift.Collection / Sequence / BidirectionalCollection / RandomAccessCollection
                // often declare nint-arithmetic methods (e.g. `index(_:offsetBy:) -> Int`,
                // `distance(from:to:) -> Int`) whose signatures never mention the parent's
                // generic parameter. Pre-fix these were rejected with skip reason
                // `generic_parent`, leaving MusicKit's `MusicItemCollection<TMusicItemType>`
                // with four SB0001s. Because Collection conformance is unconditional on the
                // type's own generic signature, these methods are guaranteed witness-callable
                // on every instantiation — the constrained-extension concern doesn't apply.
                if (parentTypeDecl is not ClassDecl)
                {
                    bool signatureReferencesT = env.MethodDecl.CSSignature
                        .Any(arg => WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames));
                    if (!signatureReferencesT && !ParentHasCollectionFamilyConformance(parentTypeDecl))
                        return false;
                }

                // Check params: T-typed must be simple direct generic params
                foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
                {
                    if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                        continue;
                    if (arg.SwiftTypeSpec.IsEmptyTuple)
                        continue;

                    if (WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames))
                    {
                        if (arg.SwiftTypeSpec is NamedTypeSpec named && genericParamNames.Contains(named.Name))
                            continue;
                        return false;
                    }
                }

                // Check return: T-typed returns are OK (routed through resultPtr).
                // Allow either bare T (e.g. `T`) or a NamedTypeSpec whose T-referencing
                // generic arguments are themselves bare parent generics (e.g.
                // `AliasGenericPayload<T>`). Wrapper emission already supports both
                // shapes via RenderSwiftTypeSpecWithSugaredNames + initializeMemory.
                var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;
                if (WrapperValidation.TypeSpecReferencesGenericParam(returnSpec, genericParamNames)
                    && !IsBareOrSimplyParameterizedNamedTypeSpec(returnSpec, genericParamNames))
                {
                    return false;
                }

                return true;
            }

            case GenericDispatchKind.Constructor:
            {
                // Constrained constructors (e.g., init where Value == Data?) can't use the
                // protocol factory pattern because the constraint can't be expressed in the protocol.
                // Detect same-type requirements on parent generic params (τ_0_X == ConcreteType).
                if (HasSameTypeConstraintOnParentGenericParam(env.MethodDecl))
                    return false;

                foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
                {
                    if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                        continue;
                    if (arg.SwiftTypeSpec.IsEmptyTuple)
                        continue;

                    // Closure parameters that reference T are not supported
                    if (env.ClosureHandler.IsClosure(arg))
                    {
                        var closureSpec = env.ClosureHandler.GetClosureTypeSpec(arg);
                        if (closureSpec != null && WrapperValidation.TypeSpecReferencesGenericParam(closureSpec, genericParamNames))
                            return false;
                    }

                    // T-typed params must be a shape the static factory can render:
                    //  - bare parent generic (e.g. `T`)
                    //  - Array<T> whose element is itself a bare parent generic. The factory
                    //    emits this as UnsafeRawPointer + `assumingMemoryBound(to: Array<T>.self).pointee`,
                    //    mirroring what the method static-dispatch path does today.
                    //  - KeyPath family of parent generic (PartialKeyPath<T>, KeyPath<T,V>,
                    //    WritableKeyPath<T,V>, ReferenceWritableKeyPath<T,V>). KeyPaths are
                    //    Swift classes; the factory reconstructs via Unmanaged.fromOpaque
                    //    rather than `.assumingMemoryBound(to:).pointee` (load of a class ref
                    //    through its own address would re-load metadata, not the ref itself).
                    //  - Nested-of-parent (`Outer<T>.Inner`) where Inner is a non-generic
                    //    struct. Covers the AppIntents 0.12 StringInterpolation pattern.
                    //    Renderer emits `Outer<T>.Inner` via InnerType; `T` is in scope
                    //    inside the static-factory extension.
                    //  - Bound-generic-of-parent (`Box<T>` where T is parent generic). The
                    //    static-factory extension renders the type via
                    //    `RenderSwiftTypeSpecWithSugaredNames` + the same
                    //    `assumingMemoryBound(to: <Box<T>>.self).pointee` reconstruction
                    //    that bare T uses. Covers the AppIntents 0.12 site #4 shape
                    //    `IntentParameterSummary<Intent>.init(_: ParameterSummaryString<Intent>, …)`.
                    //
                    // Dictionary<K,T>/Set<T> render the same way, but end-to-end runtime
                    // round-trip for them hasn't been validated — keep the gate narrowed to
                    // the shapes proven by BindingTests.
                    if (WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames))
                    {
                        if (arg.SwiftTypeSpec is NamedTypeSpec named && genericParamNames.Contains(named.Name))
                            continue;
                        if (IsArrayOfParentGeneric(arg.SwiftTypeSpec, genericParamNames))
                            continue;
                        if (IsKeyPathFamilyOfParentGeneric(arg.SwiftTypeSpec, genericParamNames))
                            continue;
                        if (IsNestedTypeOfParentGeneric(arg.SwiftTypeSpec, genericParamNames, parentTypeDecl))
                            continue;
                        if (IsBareOrSimplyParameterizedNamedTypeSpec(arg.SwiftTypeSpec, genericParamNames))
                            continue;
                        return false;
                    }
                }

                return true;
            }

            case GenericDispatchKind.PropertyGetter:
            case GenericDispatchKind.PropertySetter:
                // Properties always support static dispatch when needed
                return true;

            default:
                return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Shared helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true if the method's generic signature contains a same-type constraint
    /// on a parent generic param (e.g., "τ_0_0 == Foundation.Data?"). Such constructors
    /// only exist for specific specializations and can't be dispatched through a protocol
    /// factory that covers ALL specializations.
    /// </summary>
    private static bool HasSameTypeConstraintOnParentGenericParam(MethodDecl methodDecl)
    {
        var sig = methodDecl.RawGenericSig;
        if (string.IsNullOrEmpty(sig))
            return false;

        // Match patterns like "τ_0_0 ==" which constrain parent-level generic params
        // (depth 0) to specific types. Method-level params are depth 1+ (τ_1_0).
        return System.Text.RegularExpressions.Regex.IsMatch(sig, @"τ_0_\d+\s*==");
    }

    /// <summary>
    /// Returns true when <paramref name="spec"/> is either a bare parent generic param
    /// (e.g. <c>T</c>) or a NamedTypeSpec like <c>AliasGenericPayload&lt;T&gt;</c> whose
    /// every T-referencing generic argument is itself a bare parent generic, or
    /// <c>Array&lt;T&gt;</c> / <c>Optional&lt;T&gt;</c> (the validated one-level nesting
    /// shapes). The static dispatch wrapper can render these shapes directly via
    /// <c>RenderSwiftTypeSpecWithSugaredNames</c> + <c>initializeMemory(as: ...)</c>.
    /// Deeper nesting like <c>Foo&lt;Bar&lt;T&gt;&gt;</c> is still excluded — those would
    /// render correctly but haven't been validated end-to-end.
    /// </summary>
    internal static bool IsBareOrSimplyParameterizedNamedTypeSpec(TypeSpec spec, HashSet<string> genericParamNames)
    {
        if (spec is not NamedTypeSpec named)
            return false;
        if (genericParamNames.Contains(named.Name))
            return true;
        // Nested types (e.g. `StreamOf<E>.Iterator`) are kept out of the CSM widening
        // conservatively: the renderer now emits them correctly via InnerType, but the
        // full static-dispatch round-trip for a nested bound-generic return hasn't been
        // validated end-to-end. Detect nesting via InnerType (populated by TypeSpecParser
        // for dotted names) — don't use Name.Contains('.') because Name is module-qualified
        // and that dot can simply be a cross-module prefix on a top-level type.
        if (named.InnerType is not null)
            return false;
        foreach (var gp in named.GenericParameters)
        {
            if (!WrapperValidation.TypeSpecReferencesGenericParam(gp, genericParamNames))
                continue;
            if (gp is NamedTypeSpec gpNamed && genericParamNames.Contains(gpNamed.Name))
                continue;
            // Validated one-level nesting: Array<T> (covers the ObjectMapper
            // `Mapper<N>.map(...) -> [N]?` shape — the outer here is Optional with
            // GP=Array<N>, which is what GenericExtensionOptionalReturn exercises).
            // Array<Optional<T>> (`[N?]`) would route through IsOptionalOfParentGeneric
            // here but has no end-to-end coverage; it stays behind the gate until a
            // BindingTest proves the static-dispatch round-trip.
            if (IsArrayOfParentGeneric(gp, genericParamNames))
                continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="spec"/> is <c>Array&lt;T&gt;</c> whose element is
    /// a bare parent generic param. This is the single collection shape whose static-factory
    /// round-trip is proven end-to-end by BindingTests (CollectibleBag + IndexedSeries).
    /// Dictionary/Set render the same way but aren't validated, so they stay behind the gate.
    /// Nested bound generics (e.g. <c>Array&lt;Optional&lt;T&gt;&gt;</c>) are rejected.
    /// </summary>
    internal static bool IsArrayOfParentGeneric(TypeSpec spec, HashSet<string> genericParamNames)
    {
        if (spec is not NamedTypeSpec named)
            return false;
        if (named.Name is not ("Swift.Array" or "Array"))
            return false;
        if (named.GenericParameters.Count != 1)
            return false;
        var gp = named.GenericParameters[0];
        return gp is NamedTypeSpec gpNamed && genericParamNames.Contains(gpNamed.Name);
    }

    /// <summary>
    /// Returns true when <paramref name="spec"/> is a Swift KeyPath family class
    /// (<c>PartialKeyPath&lt;T&gt;</c>, <c>KeyPath&lt;T,V&gt;</c>, <c>WritableKeyPath&lt;T,V&gt;</c>,
    /// <c>ReferenceWritableKeyPath&lt;T,V&gt;</c>) whose Root generic argument is a bare parent
    /// generic param. The Value argument (when present) may be any concrete type — the
    /// KeyPath erases it at the @_cdecl boundary, so the factory wrapper only needs the
    /// Root to be reconstructable from the type's own generic context.
    ///
    /// KeyPath is always a Swift class, so the wrapper body uses
    /// <c>Unmanaged&lt;PartialKeyPath&lt;T&gt;&gt;.fromOpaque(by).takeUnretainedValue()</c>
    /// to round-trip the class reference, rather than the
    /// <c>.assumingMemoryBound(to:).pointee</c> pattern used for value-type T params.
    /// </summary>
    internal static bool IsKeyPathFamilyOfParentGeneric(TypeSpec spec, HashSet<string> genericParamNames)
    {
        if (spec is not NamedTypeSpec named)
            return false;
        var expectedArity = TypeProjectionFactory.GetKeyPathArity(named.Name);
        if (expectedArity <= 0)
            return false;
        if (named.GenericParameters.Count != expectedArity)
            return false;
        var root = named.GenericParameters[0];
        return root is NamedTypeSpec rootNamed && genericParamNames.Contains(rootNamed.Name);
    }

    /// <summary>
    /// Returns true when <paramref name="spec"/> is a single-level nested type whose outer
    /// segment is a NamedTypeSpec parameterised purely on parent generic params, e.g.
    /// <c>NestedHostStruct&lt;TElement&gt;.Caption</c> (same-host) or
    /// <c>CrossHostOuter&lt;T&gt;.Body</c> (cross-host) — both as a param to
    /// <c>SomeHost&lt;T&gt;.init</c>. Covers AppIntents 0.12 sites where the init's
    /// declarative param is a <c>StringInterpolation</c>-style nested struct on either
    /// the SAME generic host (<c>EnumURLRepresentation&lt;TEnum&gt;.StringInterpolation</c>
    /// as a param to <c>EnumURLRepresentation&lt;TEnum&gt;.init</c>) OR a FOREIGN generic
    /// host with shared parent generic params (<c>EnumSingleURLRepresentation.init(
    /// stringInterpolation: EnumURLRepresentation&lt;TEnum&gt;.StringInterpolation)</c>).
    ///
    /// Inner is assumed to be a value-type struct (the AppIntents <c>StringInterpolation</c>
    /// shape); the default <c>assumingMemoryBound(to: ...).pointee</c> reconstruction in
    /// <see cref="ConstructorWrapperEmitter.EmitGenericStaticFactoryConstructor"/> handles it.
    /// Deeper nesting (<c>Outer&lt;T&gt;.Inner.DeepInner</c>) or inner-with-own-generics is
    /// rejected to keep the gate to shapes proven by BindingTests.
    /// </summary>
    internal static bool IsNestedTypeOfParentGeneric(TypeSpec spec, HashSet<string> genericParamNames, TypeDecl parentTypeDecl)
    {
        if (spec is not NamedTypeSpec named)
            return false;
        if (named.InnerType is null)
            return false;
        if (named.GenericParameters.Count == 0)
            return false;
        foreach (var gp in named.GenericParameters)
        {
            if (gp is not NamedTypeSpec gpNamed || !genericParamNames.Contains(gpNamed.Name))
                return false;
        }
        if (named.InnerType.InnerType is not null)
            return false;
        if (named.InnerType.GenericParameters.Count > 0)
            return false;
        // Cross-host shape (outer != parent) faults at host VWT.destroy on Dispose: the
        // foreign outer's value-witness table does not flow through `any _SBW_GSF_X.Type`
        // existential dispatch, so the destroy walks a layout that does not match the
        // initialized storage. Same-host resolves the nested-type's metadata through
        // Self's metadata directly (member-of-Self path) and works correctly. Doc 14
        // hypothesis 1 (Option B reconstruction) did not change runtime behavior;
        // hypothesis 3 keeps cross-host on direct CallConvSwift (SB0001) until the
        // GSF existential dispatch can carry the foreign outer's witness table.
        if (!OuterMatchesParent(named, parentTypeDecl))
            return false;
        return true;
    }

    /// <summary>
    /// True when <paramref name="named"/>'s outer name matches
    /// <paramref name="parentTypeDecl"/>. Distinguishes same-host nested-of-parent
    /// (outer == host, admitted) from cross-host (outer != host, rejected — see
    /// IsNestedTypeOfParentGeneric for the runtime-fault reasoning).
    /// </summary>
    internal static bool OuterMatchesParent(NamedTypeSpec named, TypeDecl parentTypeDecl)
    {
        var parentSimpleName = parentTypeDecl.SwiftTypeName.Name;
        var parentQualifiedName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        // When the outer spec carries a module prefix, only the exact module-qualified
        // name matches. Falling back to simple-name equality here would admit a cross-
        // module sibling whose short name happens to collide with the parent — exactly
        // the cross-host shape this gate is meant to reject.
        if (named.Name.Contains('.'))
            return named.Name == parentQualifiedName;
        return named.NameWithoutModule == parentSimpleName;
    }

    /// <summary>
    /// Returns true when <paramref name="spec"/> is <c>Optional&lt;T&gt;</c> whose payload is
    /// a bare parent generic param. Round-trip is proven end-to-end by BindingTests
    /// (<c>OptionalGenericHolder&lt;Value&gt;.stored</c>). Other simply-parameterized shapes
    /// (Dictionary&lt;K,T&gt;, Pair&lt;T,T&gt;, etc.) render through the same emitter path but
    /// aren't validated, so they stay behind the gate.
    /// </summary>
    internal static bool IsOptionalOfParentGeneric(TypeSpec spec, HashSet<string> genericParamNames)
    {
        if (spec is not NamedTypeSpec named)
            return false;
        if (named.Name is not ("Swift.Optional" or "Optional"))
            return false;
        if (named.GenericParameters.Count != 1)
            return false;
        var gp = named.GenericParameters[0];
        return gp is NamedTypeSpec gpNamed && genericParamNames.Contains(gpNamed.Name);
    }

    /// <summary>
    /// Checks whether any parameter or the return type references the parent type's generic
    /// type parameters (e.g., τ_0_0, τ_0_1).
    /// </summary>
    internal static bool HasGenericTypeParamInSignature(MethodEnvironment env, TypeDecl parentTypeDecl)
    {
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();

        foreach (var arg in env.MethodDecl.CSSignature)
        {
            if (WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="parentTypeDecl"/> is a struct that conforms to
    /// Swift.Collection / Sequence / BidirectionalCollection / RandomAccessCollection.
    /// Used by <see cref="CanEmitStaticDispatch"/> to relax the <c>signatureReferencesT</c>
    /// hard-gate for nint-arithmetic Collection methods on generic structs (matches
    /// MusicKit's <c>MusicItemCollection&lt;TMusicItemType&gt;</c> shape).
    /// </summary>
    private static bool ParentHasCollectionFamilyConformance(TypeDecl parentTypeDecl)
    {
        return parentTypeDecl is StructDecl structDecl
            && CollectionProjectionEmitter.HasCollectionConformance(structDecl);
    }
}
