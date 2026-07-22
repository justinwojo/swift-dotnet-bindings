// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Mode flags controlling how <see cref="ProtocolSignatureHelper.ProjectTypeToCSharp"/> resolves types.
/// Consolidates the behavioral differences between proxy, interface, and property contexts.
/// </summary>
[Flags]
internal enum TypeResolutionMode
{
    /// <summary>Default: returns PublicType from factory projection. Used by interface signatures.</summary>
    Default = 0,

    /// <summary>Returns MarshalFromSwiftType instead of PublicType (for ABI marshalling in proxy receivers).</summary>
    AbiMarshalling = 1,

    /// <summary>Applies NativeIntOverloadEmitter.NarrowNativeIntType() to the result (property interface context).</summary>
    NarrowNativeInt = 2,

    /// <summary>Includes ExistentialHandler fallback when factory can't resolve an existential (proxy context).</summary>
    ExistentialFallback = 4,

    /// <summary>Include tuple element labels in tuple type output (proxy context).</summary>
    IncludeTupleLabels = 8,
}

/// <summary>
/// Shared signature key generation for protocol member matching.
/// Used by both ProtocolHandler (interface emission) and ProtocolConformanceValidator.
/// </summary>
internal static class ProtocolSignatureHelper
{
    /// <summary>
    /// Creates a unique signature key for a method based on name and parameter types.
    /// </summary>
    /// <param name="includeAsyncEffect">
    /// When true (the default), a method's <c>async</c> effect is part of the key, so a
    /// protocol's <c>func m()</c> and <c>func m() async</c> overloads produce DISTINCT keys.
    /// This is load-bearing for every vtable/interface slot-allocation and requirement-dedup
    /// caller: the two are separate Swift witness-table requirements occupying separate slots,
    /// and an async-insensitive key would alias the async overload onto the sync slot — dropping
    /// a C# member and, worse, drifting the proxy's slot count from Swift's StructLayout. Only the
    /// lenient concrete-conformance matchers (<c>FindMatchingMethod</c>/<c>FindMatchingStaticMethod</c>)
    /// pass <c>false</c> to preserve their existing async-agnostic witness matching.
    /// </param>
    public static string GetMethodSignatureKey(MethodDecl methodDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null, bool includeAsyncEffect = true)
    {
        var paramTypes = new List<string>();
        // Skip first element (return type) in CSSignature
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            paramTypes.Add(GetSignatureKeyTypeName(methodDecl.CSSignature[i].SwiftTypeSpec, typeDatabase, protocolContext));
        }
        var asyncSuffix = includeAsyncEffect && methodDecl.IsAsync ? ":async" : "";
        return $"{methodDecl.Name}({string.Join(",", paramTypes)}){asyncSuffix}";
    }

    /// <summary>
    /// Resolves one parameter type to its raw-signature-key name. A bound generic resolves only
    /// the CONTAINER's TypeRecord — <c>Swift.Array&lt;BaseRow&gt;</c> and <c>Swift.Array&lt;Section&gt;</c>
    /// both yield the same record name — so two requirements differing only in the generic ARGUMENT
    /// would collapse as a false DuplicateSignature: the second overload (a distinct, legal C#
    /// overload — <c>IEnumerable&lt;BaseRow&gt;</c> vs <c>IEnumerable&lt;Section&gt;</c>) drops from
    /// the interface while the proxy still emits both members, so the proxy's forward call fails to
    /// convert (CS1503). Appending the recursively-resolved arguments keeps such pairs distinct;
    /// arguments that genuinely erase (both resolve to <c>Swift.AnyType</c>) still collapse exactly
    /// as before, and a pair whose PROJECTED C# signatures nonetheless collide is still deduped by
    /// the projected-key gate downstream.
    /// </summary>
    private static string GetSignatureKeyTypeName(TypeSpec? typeSpec, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext)
    {
        // Handle associated type references for protocols
        if (typeSpec is AssociatedTypeReferenceSpec assocRef)
            return MapAssociatedTypeToGenericParam(assocRef, protocolContext);
        if (typeSpec == null)
            return "unknown";
        try
        {
            var typeRecord = typeDatabase.GetTypeRecordOrAnyType(typeSpec);
            var baseName = typeRecord.CSharpTypeName.FullyQualifiedName;
            if (typeSpec is NamedTypeSpec named && named.GenericParameters.Count > 0)
            {
                var argNames = named.GenericParameters
                    .Select(g => GetSignatureKeyTypeName(g, typeDatabase, protocolContext));
                return $"{baseName}<{string.Join(",", argNames)}>";
            }
            return baseName;
        }
        catch
        {
            // For generic type parameters or other unsupported types,
            // use the string representation of the type spec
            return typeSpec.ToString() ?? "unknown";
        }
    }

    /// <summary>
    /// Creates a unique signature key for a subscript based on index parameter types.
    /// </summary>
    public static string GetSubscriptSignatureKey(SubscriptDecl subscriptDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null)
    {
        var paramTypes = new List<string>();
        foreach (var param in subscriptDecl.IndexParameters)
        {
            paramTypes.Add(GetSignatureKeyTypeName(param.SwiftTypeSpec, typeDatabase, protocolContext));
        }
        return $"subscript[{string.Join(",", paramTypes)}]";
    }

    /// <summary>
    /// Options selecting per-path behavior for <see cref="BuildProjectedMethodKey"/>.
    /// </summary>
    internal readonly struct ProjectedKeyOptions
    {
        /// <summary>Sibling property-name set threaded into the name's collision rename (Foo→FooMethod / Foo→WithFoo).</summary>
        public IReadOnlySet<string>? PropertyNames { get; init; }

        /// <summary>
        /// True selects the protocol-interface projection path (<see cref="ProjectTypeToCSharp"/>);
        /// false uses the class/default-overload projection (<see cref="TypeProjectionFactory"/>).
        /// This is an EXPLICIT selector, not inferred from <see cref="ProtocolContext"/> being non-null:
        /// the protocol shim must take the protocol projection even when its caller passes a null
        /// context (some unit-test callers do), so the merge stays byte-identical for them.
        /// </summary>
        public bool UseProtocolProjection { get; init; }

        /// <summary>Protocol context passed to <see cref="ProjectTypeToCSharp"/> (associated-type / Self aware); may be null.</summary>
        public ProtocolDecl? ProtocolContext { get; init; }

        /// <summary>When true, unsupported closure params collapse to <c>object?</c> (closure-tombstone view).</summary>
        public bool TreatAsClosureTombstone { get; init; }

        /// <summary>When true, the name's ParentTypeName drives the CS0542 Get-rename; false on the protocol path.</summary>
        public bool IncludeParentTypeName { get; init; }

        /// <summary>Optional logger for projection-failure warnings (class/default-overload path only).</summary>
        public ILogger? Logger { get; init; }

        /// <summary>
        /// When non-null, replaces the method's natural base name (the value
        /// <see cref="PublicMethodNameContext.ForMethod"/> would derive from <c>decl.Name</c>) BEFORE the
        /// name-shaping passes run. Used by <see cref="ProtocolMethodDisambiguator"/> to project the
        /// label-derived disambiguated name (e.g. <c>conversationManagerDidActivate</c>) so the key this
        /// builder produces matches the C# member the interface emitter will actually declare. Null on
        /// every natural path — the key then stays byte-identical.
        /// </summary>
        public string? NameOverride { get; init; }
    }

    /// <summary>
    /// Single parameterized core for the three projected-C#-method-key builders
    /// (<see cref="BaseHandler.GetProjectedCSharpMethodKey"/>,
    /// <see cref="DefaultParameterOverloadEmitter.GetProjectedOverloadKey"/>, and the protocol-path
    /// <see cref="GetProjectedCSharpMethodKey"/> shim below). Each is now a thin shim over this, so the
    /// projected-key / overload-dedup logic has one home (AF05 Target D; retires the former
    /// constraints.md "three builders must stay structurally identical" rule by construction).
    ///
    /// Key shape: <c>"{publicMethodName}({projectedParamType,...})"</c> — no return type (C# overload identity).
    /// Path selection: <c>opts.UseProtocolProjection</c> (an explicit selector, NOT inferred from
    /// <see cref="ProjectedKeyOptions.ProtocolContext"/> being non-null) takes the protocol-interface
    /// projection (<see cref="ProjectTypeToCSharp"/>); otherwise the class/default-overload projection
    /// (<see cref="TypeProjectionFactory"/> + <see cref="BaseHandler.NormalizeContainerForOverloadKey"/>).
    /// </summary>
    internal static string BuildProjectedMethodKey(MethodDecl decl, ITypeDatabase typeDatabase, in ProjectedKeyOptions opts)
    {
        bool isProtocolPath = opts.UseProtocolProjection;

        // Name: same context the authoritative emitted name (MethodEnvironment.CSharpMethodName) uses,
        // so the key's name component applies the same Foo→FooMethod / Foo→WithFoo property-collision
        // rename (PublicMethodNameContext.ForMethod threads PropertyNames). Constructors hardcode "ctor"
        // (the rename never applies); the protocol path never routes a constructor here (every protocol
        // caller guards out IsConstructor), so this branch is inert on that path.
        //
        // ParentTypeName is suppressed on the protocol path (IncludeParentTypeName=false) — AF05 ruling
        // (a): the protocol's emitted enclosing C# type is I{Name}, so the CS0542 raw-parent-name rename
        // can never legally fire (DatabaseRegion ≠ IDatabaseRegion); applying it would spuriously rename
        // a valid interface member. Emission agrees — ProtocolHandler.EmitInterfaceMethod likewise omits
        // parentTypeName. Do NOT "fix" this to apply the rename (the KeyBuilderParentNameProtocol fixture
        // locks it green).
        string methodName;
        if (decl.IsConstructor)
        {
            methodName = "ctor";
        }
        else
        {
            var nameContext = PublicMethodNameContext.ForMethod(decl, opts.PropertyNames);
            if (!opts.IncludeParentTypeName)
                nameContext = nameContext with { ParentTypeName = null };
            // Disambiguator override: swap the natural base name for the label-derived one BEFORE shaping,
            // so the same PascalCase / property-rename / async-suffix passes apply to it identically.
            if (opts.NameOverride != null)
                nameContext = nameContext with { MethodName = opts.NameOverride };
            methodName = NameProvider.GetPublicMethodName(nameContext);
        }

        // Generic param names visible in this method's scope (parent type's + method's own) — used to
        // collapse Optional<GenericParam> onto the bare GenericParam form for overload-key identity.
        var visibleGenericNames = BaseHandler.CollectVisibleGenericParamNames(decl);

        // Closure-tombstone (Fix K): when this method routes through ClosureParamTombstoneEmitter, every
        // unsupported closure parameter emits as object? regardless of its Swift shape, and the key must
        // mirror that or two overloads with different unsupported closure shapes key apart yet emit the
        // same C# signature (CS0111). Only the class path opts in: its shim folds
        // (methodDecl.IsClosureParamTombstone || treatAsClosureTombstone) into TreatAsClosureTombstone, so
        // the core reads one flag and the default-overload / protocol paths (which never apply the
        // collapse) pass false.
        ClosureHandler? closureHandlerForTombstone = opts.TreatAsClosureTombstone
            ? new ClosureHandler(typeDatabase)
            : null;

        var paramTypes = new List<string>();
        for (int i = 1; i < decl.CSSignature.Count; i++)
        {
            var arg = decl.CSSignature[i];
            // Debug params (#file, #line, etc.) are stripped from the public signature
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            // Empty tuple () params are stripped from the C# signature (zero-sized Void)
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;
            if (closureHandlerForTombstone != null && closureHandlerForTombstone.IsClosure(arg))
            {
                var spec = closureHandlerForTombstone.GetClosureTypeSpec(arg);
                if (spec == null || !closureHandlerForTombstone.IsSupportedClosure(spec))
                {
                    paramTypes.Add("object?");
                    continue;
                }
            }
            // C11: Optional<ClassLike> and bare ClassLike are the same C# overload (nullable annotations
            // on reference types are erased at runtime). The recursive helper unwraps Optional<ClassLike>
            // at every depth so Array<Optional<Class>> and Array<Class> collapse onto the same key.
            var typeSpecForKey = StripOptionalClassLikeForOverloadIdentity(
                arg.SwiftTypeSpec, typeDatabase, visibleGenericNames);

            string paramType;
            if (isProtocolPath)
            {
                // Protocol-interface projection (associated-type / Self-requirement aware). Bare call,
                // no try/catch — preserves the protocol path's prior behavior byte-for-byte.
                paramType = ProjectTypeToCSharp(typeSpecForKey, typeDatabase, opts.ProtocolContext, isParameter: true);
            }
            else
            {
                // Class / default-overload projection. The whole block is wrapped so a projection failure
                // degrades to a string fallback (the prior BaseHandler class-path behavior). The pre-merge
                // default-overload builder caught only the Normalize fallback, so the unified wrap newly
                // covers factory.Project there too. factory.Project CAN throw (TypeProjectionFactory →
                // SwiftTypeName.FromModuleQualifiedName rejects a generic-rendered '<...>' name), but on any
                // such input the pre-merge default-overload path would have propagated an UNHANDLED exception
                // — a crash produces no output, so it cannot be a *different successful key*. The unified
                // catch only converts that crash into the same string fallback the class path always used.
                // No input yields a different successful key, so the class/default output is byte-identical
                // (the compile-only byte oracle is the gate); the sole delta is crash → graceful-fallback on
                // a pathological generic-rendered default-overload param — strictly safer, never a regression.
                try
                {
                    var factory = new TypeProjectionFactory();
                    var projection = factory.Project(typeSpecForKey, new ProjectionContext
                    {
                        TypeDatabase = typeDatabase,
                        IsParameter = true
                    });
                    paramType = projection != null
                        ? projection.PublicType
                        : BaseHandler.NormalizeContainerForOverloadKey(typeSpecForKey, typeDatabase);
                }
                catch (Exception ex)
                {
                    opts.Logger?.LogWarning($"GetProjectedCSharpMethodKey: Failed to resolve type '{typeSpecForKey}' for method '{decl.Name}', using string fallback: {ex.Message}");
                    paramType = typeSpecForKey?.ToString() ?? "unknown";
                }
            }

            // Normalize nullable reference types: Optional<Class> and Class produce the same C# overload.
            paramType = NormalizeParamTypeForOverloadIdentity(paramType, arg.SwiftTypeSpec, typeDatabase);
            paramTypes.Add(paramType);
        }

        // All async methods get a trailing CancellationToken at emission time — include it in the key so
        // native async methods collide with completion-handler overloads. AF05 ruling (b): the protocol
        // path previously OMITTED this, silently dropping a `func foo() async` requirement whose key
        // collided with a sibling `func fooAsync()` (the KeyBuilderAsyncOverloadProtocol fixture proves
        // both members now emit). The class/default paths already did this — unchanged for them.
        if (decl.IsAsync)
        {
            paramTypes.Add("System.Threading.CancellationToken");
        }

        var key = $"{methodName}({string.Join(",", paramTypes)})";

        // Method-level generic arity is part of C# overload identity: a generic method and a
        // non-generic (or differently-arity) namesake with otherwise-identical projected params are
        // DISTINCT C# overloads — `Request(A, B)` and `Request<T>(A, B<T>)` coexist legally — so they
        // must NOT collision-group together. An arity-blind key groups them, suffixes one (`Request2`),
        // and when the colliding generic requirement isn't expressible on the C# interface (so only the
        // concrete is declared, bare) the impl's renamed member no longer satisfies the interface →
        // CS0535. Append the arity marker ONLY for generic methods so the arity-0 keys — the vast
        // majority — stay byte-identical and don't churn the api-manifest baseline. The marker sits
        // after the closing ')', and arity ≥ 1, so it can never equal a non-generic key.
        if (decl.GenericParameters.Count > 0)
            key += $"`{decl.GenericParameters.Count}";

        return key;
    }

    /// <summary>
    /// Creates a projected C# method signature key for protocol-interface dedup purposes.
    /// Two methods that would produce the same C# interface signature get the same key.
    /// Key format: "MethodName(paramType1,paramType2,...)" — no return type (C# overload identity).
    /// Thin shim over <see cref="BuildProjectedMethodKey"/> on the protocol path.
    ///
    /// Pass <paramref name="propertyNames"/> with the same set the interface emitter used
    /// for this protocol when collision-aware comparison matters (e.g. BFS shadow detection
    /// across protocols whose own property sets differ); otherwise the rename `Foo` →
    /// `FooMethod` is silently dropped and methods that emit under different C# names
    /// produce identical keys.
    /// </summary>
    public static string GetProjectedCSharpMethodKey(MethodDecl methodDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null, IReadOnlySet<string>? propertyNames = null)
        => BuildProjectedMethodKey(methodDecl, typeDatabase, new ProjectedKeyOptions
        {
            PropertyNames = propertyNames,
            // Always take the protocol projection — including when protocolContext is null (some unit-test
            // callers pass null and rely on ProjectTypeToCSharp's fallbacks); inferring from
            // ProtocolContext != null would silently route those through class-style projection.
            UseProtocolProjection = true,
            ProtocolContext = protocolContext,
            // Ruling (a): the protocol path omits parentTypeName by design (benign; see the core's comment).
            IncludeParentTypeName = false,
            // Protocol path never applies the closure-tombstone collapse and never logs (matches prior behavior).
            TreatAsClosureTombstone = false,
            Logger = null,
        });

    /// <summary>
    /// Projects a Swift TypeSpec to the C# type name for protocol contexts.
    /// This is the single consolidated entry point for type resolution across proxy,
    /// interface, and property contexts. Use <see cref="TypeResolutionMode"/> flags
    /// to control context-specific behavior.
    /// </summary>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <param name="typeDatabase">Type database for lookups.</param>
    /// <param name="protocolContext">Protocol context for associated type resolution and Self-requirement detection.</param>
    /// <param name="isParameter">True for parameter types (arrays → IEnumerable), false for return types (arrays → IReadOnlyList).</param>
    /// <param name="genericContext">Explicit generic context override. When null, auto-computed from protocolContext
    /// (ForProtocolSelf when HasSelfRequirement, otherwise Empty).</param>
    /// <param name="mode">Mode flags controlling resolution behavior. Default is interface context.</param>
    /// <param name="currentModuleName">Emitting module name. When set, cross-module existential
    /// projections are namespace-qualified (e.g. <c>RealityFoundation.IHasCollision?</c> when a
    /// RealityKit proxy/signature references a RealityFoundation existential). Left null by
    /// gating/dedup callers so overload-identity keys stay module-agnostic and consistent.</param>
    public static string ProjectTypeToCSharp(
        TypeSpec typeSpec,
        ITypeDatabase typeDatabase,
        ProtocolDecl? protocolContext = null,
        bool isParameter = false,
        GenericContext? genericContext = null,
        TypeResolutionMode mode = TypeResolutionMode.Default,
        string? currentModuleName = null)
    {
        bool forAbiMarshalling = mode.HasFlag(TypeResolutionMode.AbiMarshalling);
        bool narrowNativeInt = mode.HasFlag(TypeResolutionMode.NarrowNativeInt);
        bool existentialFallback = mode.HasFlag(TypeResolutionMode.ExistentialFallback);
        bool includeTupleLabels = mode.HasFlag(TypeResolutionMode.IncludeTupleLabels);

        // Mode for recursive calls: strip AbiMarshalling (nested types always use public type)
        // and NarrowNativeInt (applied once at the top level only).
        var recurMode = mode & ~(TypeResolutionMode.AbiMarshalling | TypeResolutionMode.NarrowNativeInt);

        // Associated type references → generic param (factory doesn't handle these)
        if (typeSpec is AssociatedTypeReferenceSpec assocRef)
            return MaybeNarrow(MapAssociatedTypeToGenericParam(assocRef, protocolContext), narrowNativeInt);

        // Resolve generic context: explicit override, or auto-compute from protocolContext
        var effectiveGenericContext = genericContext
            ?? (protocolContext?.HasSelfRequirement == true
                ? GenericContext.ForProtocolSelf()
                : GenericContext.Empty);

        // Factory-first: handles existentials, closures, tuples, containers (Array, Dict, Optional),
        // string, bool, ObjC bridged, simple enum, native remapped, class, non-frozen, blittable
        var factory = new TypeProjectionFactory();
        var projection = factory.Project(typeSpec, new ProjectionContext
        {
            TypeDatabase = typeDatabase,
            IsParameter = isParameter,
            GenericContext = effectiveGenericContext,
            CurrentModuleName = currentModuleName
        });
        if (projection != null)
        {
            var result = forAbiMarshalling ? projection.MarshalFromSwiftType : projection.PublicType;
            return MaybeNarrow(result, narrowNativeInt);
        }

        // Closure fallback when factory can't fully resolve (e.g., inner types not in TypeDatabase)
        if (typeSpec is ClosureTypeSpec closureType)
        {
            var args = closureType.EachArgument()
                .Select(a => ProjectTypeToCSharp(a, typeDatabase, protocolContext, isParameter: true, genericContext, recurMode, currentModuleName))
                .ToList();
            bool hasReturn = !closureType.ReturnType.IsEmptyTuple;

            string closureResult;
            if (!hasReturn)
            {
                closureResult = args.Count == 0 ? "Action" : $"Action<{string.Join(", ", args)}>";
            }
            else
            {
                // Closure return types use isParameter:false (return position) so arrays project
                // as IReadOnlyList<T>, matching ProtocolHandler.GetClosureCSharpType for interface parity.
                var retName = ProjectTypeToCSharp(closureType.ReturnType, typeDatabase, protocolContext, isParameter: false, genericContext, recurMode, currentModuleName);
                closureResult = args.Count == 0 ? $"Func<{retName}>" : $"Func<{string.Join(", ", args)}, {retName}>";
            }
            return MaybeNarrow(closureResult, narrowNativeInt);
        }

        // Tuple fallback
        if (typeSpec is TupleTypeSpec tupleType)
        {
            if (tupleType.IsEmptyTuple) return "void";

            var elements = new List<string>();
            foreach (var element in tupleType.Elements)
            {
                var typeName = ProjectTypeToCSharp(element, typeDatabase, protocolContext, isParameter, genericContext, recurMode, currentModuleName);
                if (includeTupleLabels && !string.IsNullOrEmpty(element.TypeLabel))
                    elements.Add($"{typeName} {element.TypeLabel}");
                else
                    elements.Add(typeName);
            }
            return MaybeNarrow($"({string.Join(", ", elements)})", narrowNativeInt);
        }

        // Existential fallback: when factory can't resolve but ExistentialHandler can.
        // Only used in proxy context where the factory may not cover all existential patterns.
        if (existentialFallback)
        {
            var existentialHandler = new ExistentialHandler(typeDatabase) { CurrentModuleName = currentModuleName };
            if (existentialHandler.IsExistential(typeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
                if (protocolList != null)
                {
                    if (existentialHandler.TryGetWellKnownProtocolType(protocolList, out var wellKnownType))
                        return MaybeNarrow(wellKnownType, narrowNativeInt);

                    if (existentialHandler.IsSupportedExistential(protocolList))
                    {
                        var existentialResult = forAbiMarshalling
                            ? existentialHandler.GetCSharpExistentialType(protocolList)
                            : existentialHandler.GetPublicExistentialType(protocolList);
                        return MaybeNarrow(existentialResult, narrowNativeInt);
                    }
                }
            }
        }

        // Bound generic fallback: produce full type name with generic args
        // (e.g., BatchedCollection<Swift.AnyType> for unknown inner types).
        if (typeSpec is NamedTypeSpec boundGeneric && boundGeneric.ContainsGenericParameters)
        {
            var bgh = new BoundGenericsHandler(typeDatabase);
            return MaybeNarrow(bgh.TranslateBoundGenericTypeToCSharp(typeSpec, effectiveGenericContext), narrowNativeInt);
        }

        // Final fallback: raw type record lookup
        var record = typeDatabase.GetTypeRecordOrAnyType(typeSpec);
        return MaybeNarrow(record.CSharpTypeName.FullyQualifiedName, narrowNativeInt);
    }

    /// <summary>
    /// Conditionally applies NativeInt narrowing to a type name.
    /// </summary>
    private static string MaybeNarrow(string typeName, bool narrow)
        => narrow ? NativeIntOverloadEmitter.NarrowNativeIntType(typeName) : typeName;

    /// <summary>
    /// Recursively unwraps <c>Swift.Optional&lt;T&gt;</c> where <c>T</c> projects to a C# reference
    /// type at any depth inside a TypeSpec, returning a structurally normalized spec for
    /// overload-identity comparison. Top-level Optional&lt;ClassLike&gt; is already handled
    /// post-projection by <see cref="NormalizeParamTypeForOverloadIdentity"/> (string trim),
    /// but the projected string for a container like <c>Array&lt;Optional&lt;Class&gt;&gt;</c>
    /// comes out as <c>IEnumerable&lt;Class?&gt;</c> — the <c>?</c> sits inside the generic
    /// argument and the trailing-trim approach can't see it. Two overloads taking
    /// <c>Array&lt;Class&gt;</c> and <c>Array&lt;Optional&lt;Class&gt;&gt;</c> resolve to the
    /// same C# overload (nullability is erased for reference types) and produce CS0111
    /// unless we collapse them before projection.
    ///
    /// "ClassLike" = the same set already enumerated in <see cref="NormalizeParamTypeForOverloadIdentity"/>:
    /// Class, Protocol, Existential, non-simple Enum, non-Frozen Struct, frozen-struct-projected-as-class,
    /// ClosureTypeSpec, and Swift value types whose C# projection is a reference type
    /// (string, object). Generic parameters visible in scope are also stripped — for a
    /// reference-constrained T, <c>Array&lt;T?&gt;</c> and <c>Array&lt;T&gt;</c> collide too.
    /// </summary>
    public static TypeSpec StripOptionalClassLikeForOverloadIdentity(
        TypeSpec spec,
        ITypeDatabase typeDatabase,
        IReadOnlyCollection<string>? genericParamNamesInScope = null)
    {
        switch (spec)
        {
            case NamedTypeSpec named when named.Name == "Swift.Optional" && named.GenericParameters.Count == 1:
            {
                var innerStripped = StripOptionalClassLikeForOverloadIdentity(named.GenericParameters[0], typeDatabase, genericParamNamesInScope);
                if (IsReferenceLikeForOverloadIdentity(innerStripped, typeDatabase, genericParamNamesInScope))
                    return innerStripped;
                return new NamedTypeSpec(named.Name, innerStripped);
            }
            case NamedTypeSpec named when named.GenericParameters.Count > 0:
            {
                var rebuilt = new NamedTypeSpec(
                    named.Name,
                    named.GenericParameters
                        .Select(g => StripOptionalClassLikeForOverloadIdentity(g, typeDatabase, genericParamNamesInScope))
                        .ToArray());
                rebuilt.InnerType = named.InnerType;
                return rebuilt;
            }
            case TupleTypeSpec tuple:
                return new TupleTypeSpec(
                    tuple.Elements.Select(e => StripOptionalClassLikeForOverloadIdentity(e, typeDatabase, genericParamNamesInScope)));
            default:
                return spec;
        }
    }

    /// <summary>
    /// Mirrors the ClassLike branch of <see cref="NormalizeParamTypeForOverloadIdentity"/>:
    /// returns true when the type, if wrapped in <c>Swift.Optional&lt;_&gt;</c>, projects to
    /// a nullable annotation on a CLR reference type — meaning <c>T?</c> and <c>T</c> are
    /// indistinguishable for C# overload resolution.
    /// </summary>
    private static bool IsReferenceLikeForOverloadIdentity(
        TypeSpec spec,
        ITypeDatabase typeDatabase,
        IReadOnlyCollection<string>? genericParamNamesInScope)
    {
        if (spec is ClosureTypeSpec)
            return true;
        if (spec is NamedTypeSpec named)
        {
            if (genericParamNamesInScope != null && genericParamNamesInScope.Contains(named.Name))
                return true;
            if (TypeSpecHelpers.IsGenericTypeParameter(named.Name))
                return true;
        }
        try
        {
            var record = typeDatabase.GetTypeRecordOrAnyType(spec);
            if (record.Kind == TypeRecordKind.Class ||
                record.Kind == TypeRecordKind.Protocol ||
                record.Kind == TypeRecordKind.Existential ||
                (record.Kind == TypeRecordKind.Enum && !record.Flags.HasFlag(TypeRecordFlags.SimpleEnum)) ||
                (record.Kind == TypeRecordKind.Struct && !record.Flags.HasFlag(TypeRecordFlags.Frozen)) ||
                MarshallingHelpers.IsFrozenStructProjectedAsClass(record))
                return true;
            // Swift value types whose C# projection is a reference type (Swift.String → string).
            var name = record.CSharpTypeName.FullyQualifiedName;
            if (name == "string" || name == "object")
                return true;
        }
        catch
        {
            // Unknown record — be conservative and don't strip.
        }
        return false;
    }

    /// <summary>
    /// Normalizes a projected C# parameter type for overload identity comparison.
    /// In C#, nullability annotations don't affect overload resolution for reference types —
    /// Optional&lt;Class&gt; and Class resolve to the same overload. This strips the trailing '?'
    /// for reference-like types so that emission dedup correctly detects collisions.
    /// </summary>
    public static string NormalizeParamTypeForOverloadIdentity(string projectedType, TypeSpec swiftTypeSpec, ITypeDatabase typeDatabase)
    {
        if (swiftTypeSpec is NamedTypeSpec optNamed && optNamed.Name == "Swift.Optional" &&
            optNamed.GenericParameters.Count == 1)
        {
            var innerRecord = typeDatabase.GetTypeRecordOrAnyType(optNamed.GenericParameters[0]);
            if (innerRecord.Kind == TypeRecordKind.Class ||
                innerRecord.Kind == TypeRecordKind.Protocol ||
                innerRecord.Kind == TypeRecordKind.Existential ||
                (innerRecord.Kind == TypeRecordKind.Enum && !innerRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum)) ||
                // Non-frozen structs are emitted as C# classes (ClassWithOpaquePayload),
                // making nullable annotation irrelevant for overload resolution.
                (innerRecord.Kind == TypeRecordKind.Struct && !innerRecord.Flags.HasFlag(TypeRecordFlags.Frozen)) ||
                // Frozen structs with reference-type fields are emitted as C# classes (ClassWithBufferStruct),
                // so nullable annotation is also irrelevant for overload resolution.
                MarshallingHelpers.IsFrozenStructProjectedAsClass(innerRecord))
                return projectedType.TrimEnd('?');

            // Swift value types that project to C# reference types (e.g., Swift.String → string).
            // In C#, Optional<String> and String both map to 'string' / 'string?' which are the
            // same CLR type (nullability is annotation-only for reference types).
            if (projectedType.EndsWith("?") && IsCSharpReferenceTypeProjection(projectedType.TrimEnd('?')))
                return projectedType.TrimEnd('?');
        }

        return projectedType;
    }

    /// <summary>
    /// Checks if a projected C# type name is a reference type in the CLR,
    /// where nullability is annotation-only and doesn't affect overload resolution.
    /// </summary>
    private static bool IsCSharpReferenceTypeProjection(string projectedType) =>
        projectedType is "string" or "object";

    /// <summary>
    /// Maps an associated type reference to a C# generic parameter name.
    /// For example, "Self.Element" in a protocol with associated type "Element" becomes "TElement".
    /// </summary>
    internal static string MapAssociatedTypeToGenericParam(AssociatedTypeReferenceSpec assocRef, ProtocolDecl? protocolDecl)
    {
        // Handle Self reference
        if (assocRef.BaseType == "Self" && string.IsNullOrEmpty(assocRef.AssociatedTypeName))
        {
            return "TSelf";
        }

        // Handle associated type reference like "Self.Element"
        if (!string.IsNullOrEmpty(assocRef.AssociatedTypeName))
        {
            // Map "Element" -> "TElement"
            return $"T{assocRef.AssociatedTypeName}";
        }

        // Fallback for generic parameter like τ_0_0
        if (assocRef.BaseType.StartsWith("τ_") || assocRef.BaseType.StartsWith("T"))
        {
            // Already a generic param reference
            return assocRef.BaseType;
        }

        return "object";
    }
}
