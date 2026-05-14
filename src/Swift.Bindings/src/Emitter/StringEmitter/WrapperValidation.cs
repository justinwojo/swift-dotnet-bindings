// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Result of the wrapper eligibility decision.
/// All eligible methods get @_cdecl wrappers — there is no CallConvSwift fallback.
/// </summary>
public enum WrapperDecision
{
    /// <summary>
    /// The wrapper emitter's ShouldEmitWrapper returned false — wrapping is not possible
    /// (unsupported signature, missing xcframework mode, etc.).
    /// </summary>
    CannotWrap,

    /// <summary>
    /// Wrapping is possible (ShouldEmitWrapper passed). Emit the @_cdecl wrapper.
    /// </summary>
    WrapperRequired
}

/// <summary>
/// Identifies which kind of member is being evaluated for wrapper eligibility decisions.
/// Used by <see cref="WrapperValidation.CanEmitMember"/> to apply shared guards with
/// member-kind-specific parameterization, and by <see cref="WrapperValidation.NeedsGenericDispatch"/>
/// for generic dispatch decisions.
/// </summary>
public enum MemberKind
{
    /// <summary>Instance or static method (non-constructor, non-accessor).</summary>
    Method,
    /// <summary>Property getter or setter accessor.</summary>
    Property,
    /// <summary>Constructor (init).</summary>
    Constructor,
    /// <summary>Subscript accessor (getter/setter with index params).</summary>
    Subscript,
    /// <summary>Operator (==, !=, etc.).</summary>
    Operator
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
    /// All eligible methods get @_cdecl wrappers — CallConvSwift is eliminated.
    /// </summary>
    public static WrapperDecision DetermineMethodWrapperDecision(MethodEnvironment env)
    {
        if (!MethodWrapperEmitter.ShouldEmitWrapper(env))
            return WrapperDecision.CannotWrap;
        return WrapperDecision.WrapperRequired;
    }

    /// <summary>
    /// Centralized decision for constructor @_cdecl wrapper emission.
    /// All eligible constructors get @_cdecl wrappers — CallConvSwift is eliminated.
    /// </summary>
    public static WrapperDecision DetermineConstructorWrapperDecision(MethodEnvironment env)
    {
        if (!ConstructorWrapperEmitter.ShouldEmitWrapper(env))
            return WrapperDecision.CannotWrap;
        return WrapperDecision.WrapperRequired;
    }

    /// <summary>
    /// Centralized decision for property @_cdecl wrapper emission.
    /// All eligible properties get @_cdecl wrappers — CallConvSwift is eliminated.
    /// </summary>
    public static WrapperDecision DeterminePropertyWrapperDecision(PropertyDecl propertyDecl, MethodEnvironment env)
    {
        if (!PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env))
            return WrapperDecision.CannotWrap;
        return WrapperDecision.WrapperRequired;
    }

    /// <summary>
    /// Returns the P/Invoke calling convention for a method.
    /// Methods routed through @_cdecl wrappers or native ARM64 thunks use CallConvCdecl.
    /// Methods using @_silgen_name wrappers (ObjCOverridePropertyWrapper, DefaultParameterOverload,
    /// standalone closure wrappers without @_cdecl conversion) use CallConvSwift because
    /// @_silgen_name only assigns a symbol name — the function itself uses Swift calling convention.
    /// </summary>
    public static PInvokeCallingConvention GetCallingConvention(MethodDecl methodDecl)
    {
        // @_cdecl wrappers and native thunks use C calling convention
        if (methodDecl.UsesCdeclMethodWrapper || methodDecl.UsesCdeclConstructorWrapper ||
            methodDecl.UsesCdeclPropertyWrapper || methodDecl.UsesNativeThunk)
            return PInvokeCallingConvention.Cdecl;

        // Standalone closure @_cdecl wrappers (HasClosureCdeclWrapper=true with UsesCdeclMethodWrapper=true
        // already caught above). HasClosureCdeclWrapper alone means @_silgen_name — Swift convention.

        // Everything else (direct Swift calls, @_silgen_name wrappers) uses Swift calling convention.
        // This includes: ObjCOverridePropertyWrapper, DefaultParameterOverload without @_cdecl,
        // standalone closure wrappers that couldn't convert to @_cdecl, OptionalPointerWrapper
        // without @_cdecl conversion — all use @_silgen_name with Swift ABI.
        return PInvokeCallingConvention.Swift;
    }

    /// <summary>
    /// Consolidated shared wrapper eligibility gate. Checks guards that are common across
    /// multiple ShouldEmitWrapper implementations. Each handler's ShouldEmitWrapper should
    /// call this first, then check handler-specific guards.
    ///
    /// Shared guards checked (applicable member kinds in parentheses):
    /// 1. xcframework mode (all)
    /// 2. Module internal (Method, Constructor, Property)
    /// 3. SPI protected (Method, Property)
    /// 4. (removed — noncopyable struct parents are now allowed with borrowing pointer semantics)
    /// 5. Async (Method, Constructor — Property/Subscript check differently)
    /// 6. Actor isolation (Method, Property, Subscript)
    /// 7. Inherited generic context (Method, Property, Constructor — checked when parent is generic)
    ///
    /// The <paramref name="isModuleInternal"/>, <paramref name="isSpiProtected"/>,
    /// <paramref name="isAsync"/>, <paramref name="isActorIsolated"/>, and
    /// <paramref name="isMainActorIsolated"/> parameters let each handler pass the
    /// correct source for these properties (MethodDecl for methods, PropertyDecl for
    /// properties, etc.).
    /// </summary>
    /// <returns>True if all shared guards pass. False if any shared guard rejects the member.</returns>
    public static bool CanEmitMember(
        MethodEnvironment env,
        MemberKind kind,
        bool isModuleInternal = false,
        bool isSpiProtected = false,
        bool isAsync = false,
        bool isActorIsolated = false,
        bool isMainActorIsolated = false,
        bool isNonisolated = false)
    {
        // 1. xcframework mode required (all handlers)
        if (!IsXCFrameworkMode(env.TypeDatabase))
            return false;

        // 2. Module internal (Method, Constructor, Property)
        if (kind is MemberKind.Method or MemberKind.Constructor or MemberKind.Property)
        {
            if (isModuleInternal)
                return false;
        }

        // 3. SPI protected (Method, Property)
        if (kind is MemberKind.Method or MemberKind.Property)
        {
            if (isSpiProtected)
                return false;
        }

        // 4. Non-copyable struct parent — ALLOWED. Noncopyable types get @_cdecl wrappers
        // with borrowing pointer semantics (no .pointee copy). Self is accessed inline through
        // self_.assumingMemoryBound(to:).pointee which gives a borrow in Swift 6.

        // 5. Async (Method, Constructor — Property/Subscript check differently via accessor)
        if (kind is MemberKind.Method or MemberKind.Constructor)
        {
            if (isAsync)
                return false;
        }

        // 6. Actor isolation (Method, Property, Subscript, Constructor)
        if (kind is MemberKind.Method or MemberKind.Property or MemberKind.Subscript or MemberKind.Constructor)
        {
            // Nonisolated actor members only bypass the actor gate when their signature doesn't
            // require parameterized-protocol runtime support (iOS 16+ feature). When it does,
            // fall back to the default gate (i.e., treat the member as actor-isolated).
            var effectiveNonisolated = isNonisolated &&
                !SignatureContainsParameterizedProtocol(env.MethodDecl, env.TypeDatabase);

            // Constructors on Swift `actor` parent types are implicitly nonisolated from the
            // outside — Swift permits `MyActor()` synchronously from any context because the
            // actor's isolation domain doesn't exist before the actor is constructed. The
            // parser deliberately leaves them sync (see SwiftABIParser actor-isolation block:
            // "Constructors on Swift `actor` types stay sync because their default inits are
            // nonisolated from outside"). The matching @_cdecl wrapper is a free Swift function
            // that calls `MyActor()` directly, which compiles fine. Without this bypass the
            // gate falls through to the broken direct `cfC` PInvoke — that path doesn't pass
            // Self.Type metadata in x20, so the allocated heap object's metadata pointer is
            // garbage and the first swift_release on the resulting handle SIGSEGVs inside
            // swift_release_dealloc trying to invoke a corrupt destructor.
            bool actorInitSync = kind == MemberKind.Constructor &&
                env.ParentDecl is ClassDecl { IsActor: true } &&
                !isMainActorIsolated;

            if (!actorInitSync &&
                IsActorIsolatedMember(env.ParentDecl, isActorIsolated, isMainActorIsolated, effectiveNonisolated))
                return false;
        }

        // 6b. SWIFTBIND022: Synchronous @_cdecl wrappers for constructors on
        // @<CustomActor>-isolated parent types are unreachable. Swift 6 only exposes
        // synchronous global-actor entry for `@MainActor` (a stdlib special case); a
        // `<Actor>.shared.assumeIsolated { _ in init(...) }` thunk enters *instance*-actor
        // isolation, a different domain than the @<Actor> *global-actor* isolation the
        // init requires, so swiftc rejects that wrapper. A direct CallConvSwift call from
        // C# to the Swift-native `cfC` init also doesn't satisfy the actor contract —
        // the metatype lands in `x20` from C# but the function still expects to enter
        // the global actor's isolation domain before allocating, and we cannot establish
        // that from a foreign runtime.
        //
        // Async constructors on these parents are tagged IsAsync by the parser (see
        // SwiftABIParser actor-isolation block) and reach C# via the `static Task<T>
        // CreateAsync(...)` factory pipeline — the Swift wrapper becomes
        // `Task { try await Type.init(...) }` where the implicit actor hop at the await
        // lands the init on the actor's executor. Gate #5 above already returns false
        // for IsAsync constructors, so this gate fires only for any sync ctor that the
        // parser couldn't tag (defense in depth).
        if (kind is MemberKind.Constructor &&
            env.ParentDecl is TypeDecl { IsCustomActorIsolated: true } &&
            !isNonisolated)
        {
            return false;
        }

        // 7. Inherited generic context on parent (Method, Property, Constructor)
        // Nested types that inherit generic context from an outer parent
        // (e.g., AuthenticationInterceptor<A>.RefreshWindow) can't have @_cdecl wrappers
        // because "extension Outer.Inner: Protocol {}" won't compile.
        if (kind is MemberKind.Method or MemberKind.Property or MemberKind.Constructor)
        {
            if (env.ParentDecl is TypeDecl td && td.IsGeneric && IsInheritedGenericContext(td))
                return false;
        }

        return true;
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
    /// Returns true if the given TypeSpec resolves to a `~Copyable` (noncopyable) struct.
    /// Walks the same paths as <see cref="ConstructorWrapperEmitter.HasNonCopyableStructParameter"/>:
    /// same-module StructDecl conformances first, then cross-module TypeRecord.NonCopyable flag.
    /// </summary>
    public static bool IsNonCopyableType(TypeSpec? typeSpec, ITypeDatabase typeDatabase, ModuleDecl? currentModule = null)
    {
        if (typeSpec is not NamedTypeSpec namedSpec)
            return false;

        var moduleTypes = currentModule?.Types;
        if (moduleTypes != null)
        {
            StructDecl? FindStruct(IEnumerable<TypeDecl> types, string fqName)
            {
                foreach (var t in types)
                {
                    if (t is StructDecl s && s.SwiftTypeName.ModuleQualifiedName == fqName)
                        return s;
                    var nested = FindStruct(t.Types, fqName);
                    if (nested != null) return nested;
                }
                return null;
            }
            var structDecl = FindStruct(moduleTypes, namedSpec.Name);
            if (structDecl != null)
            {
                return structDecl.Conformances.Any(c => c.Protocol.ToString() == "Swift.Escapable") &&
                       !structDecl.Conformances.Any(c => c.Protocol.ToString() == "Swift.Copyable");
            }
        }

        if (typeDatabase.TryGetTypeRecord(namedSpec, out var typeRecord) &&
            typeRecord.Flags.HasFlag(TypeRecordFlags.NonCopyable))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Checks whether a member should be blocked from @_cdecl wrapper emission due to actor isolation.
    /// Blocks:
    /// (a) Custom actor types (ClassDecl { IsActor: true }) — isolated members require async dispatch
    /// (b) Per-member custom actor isolation (e.g., @ProcessingActor) on non-actor classes
    ///
    /// Does NOT block:
    /// - @MainActor members — exposed as synchronous C# APIs following the Xamarin.iOS precedent.
    /// - `nonisolated` members on actor types — these opt out of the actor's isolation and are
    ///   safe to call from any context (Swift 6 guarantees via the compiler).
    ///
    /// Note: Type-level custom global actor isolation (e.g., <c>@ImagePipelineActor class X</c>)
    /// is NOT handled here. Constructor-specific blocking is in <see cref="CanEmitMember"/>'s
    /// SWIFTBIND022 gate; default-parameter overload extensions are skipped in
    /// <c>DefaultParameterOverloadEmitter</c>. Plain nonisolated instance methods/properties
    /// on such types are still safe to wrap.
    /// </summary>
    public static bool IsActorIsolatedMember(
        BaseDecl? parentDecl,
        bool memberIsActorIsolated,
        bool memberIsMainActorIsolated,
        bool memberIsNonisolated = false)
    {
        // Nonisolated members explicitly opt out of the parent's actor isolation —
        // they run in the caller's context and can be reached synchronously via @_cdecl.
        if (memberIsNonisolated)
            return false;

        // (a) Parent is a custom actor class — isolated members require async dispatch
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
    /// Returns true if the TypeSpec contains a bound generic whose argument is a Swift protocol
    /// (i.e., a parameterized-protocol usage like <c>EventStream&lt;any UIEvent&gt;</c>).
    ///
    /// Runtime metadata for parameterized protocol types requires iOS 16 / macOS 13 or newer.
    /// The @_cdecl wrapper can't safely spell such a type at an earlier deployment target, so
    /// the Fix 13 nonisolated-actor bypass must reject any signature containing this pattern.
    /// </summary>
    public static bool ContainsParameterizedProtocol(TypeSpec? typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec == null) return false;

        if (typeSpec is NamedTypeSpec named)
        {
            // The base type itself being a protocol with generic parameters is the parameterized
            // protocol pattern (e.g., `EventStream<UIEvent>` where EventStream is a protocol with
            // a primary associated type). Also covers `any EventStream<UIEvent>`.
            if (named.GenericParameters.Count > 0 &&
                !TypeSpecHelpers.IsGenericTypeParameter(named.Name))
            {
                try
                {
                    var baseSwiftName = SwiftTypeName.FromTypeSpec(named);
                    if (typeDatabase.TryGetTypeRecord(baseSwiftName, out var baseRecord) &&
                        baseRecord.Kind == TypeRecordKind.Protocol)
                    {
                        return true;
                    }
                }
                catch { }
            }

            foreach (var genericArg in named.GenericParameters)
            {
                if (genericArg is NamedTypeSpec gArg &&
                    !TypeSpecHelpers.IsGenericTypeParameter(gArg.Name))
                {
                    try
                    {
                        var gSwiftName = SwiftTypeName.FromTypeSpec(gArg);
                        if (typeDatabase.TryGetTypeRecord(gSwiftName, out var gRecord) &&
                            gRecord.Kind == TypeRecordKind.Protocol)
                        {
                            return true;
                        }
                    }
                    catch { }
                }
                if (genericArg is ProtocolListTypeSpec)
                    return true;
                if (ContainsParameterizedProtocol(genericArg, typeDatabase))
                    return true;
            }
        }
        else if (typeSpec is ProtocolListTypeSpec protoList)
        {
            // Top-level protocol-list existentials (e.g., `any P<T>` parsed as ProtocolList)
            // can carry parameterized protocols. Inspect each protocol key.
            foreach (var proto in protoList.Protocols.Keys)
            {
                if (ContainsParameterizedProtocol(proto, typeDatabase))
                    return true;
                // A protocol key itself being a parameterized-protocol base type
                // (e.g., EventStream<UIEvent>) is the pattern we reject.
                if (proto is NamedTypeSpec protoNamed && protoNamed.GenericParameters.Count > 0)
                    return true;
            }
        }
        else if (typeSpec is TupleTypeSpec tuple)
        {
            foreach (var element in tuple.Elements)
                if (ContainsParameterizedProtocol(element, typeDatabase))
                    return true;
        }
        else if (typeSpec is ClosureTypeSpec closure)
        {
            if (ContainsParameterizedProtocol(closure.ReturnType, typeDatabase))
                return true;
            if (closure.Arguments is NamedTypeSpec argNamed)
            {
                if (ContainsParameterizedProtocol(argNamed, typeDatabase))
                    return true;
            }
            else if (closure.Arguments is TupleTypeSpec argTuple)
            {
                foreach (var element in argTuple.Elements)
                    if (ContainsParameterizedProtocol(element, typeDatabase))
                        return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if any parameter or return type in the method signature contains a
    /// parameterized-protocol generic usage (see <see cref="ContainsParameterizedProtocol"/>).
    /// </summary>
    public static bool SignatureContainsParameterizedProtocol(MethodDecl methodDecl, ITypeDatabase typeDatabase)
    {
        foreach (var arg in methodDecl.CSSignature)
        {
            if (ContainsParameterizedProtocol(arg.SwiftTypeSpec, typeDatabase))
                return true;
        }
        return false;
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
    /// Checks whether any closure parameter is an async closure the emitter can NOT
    /// bridge. Baseline async-throwing closures (see
    /// <see cref="ClosureHandler.IsBaselineAsyncThrowingClosure"/>) are emitted via
    /// <c>withCheckedThrowingContinuation</c> and are therefore considered supported;
    /// anything else (async-only, arg-bearing, non-primitive return) falls through
    /// the existing skip path.
    /// </summary>
    public static bool HasUnsupportedAsyncClosure(MethodEnvironment env)
    {
        return env.MethodDecl.CSSignature.Skip(1)
            .Where(env.ClosureHandler.IsClosure)
            .Any(arg =>
            {
                var spec = env.ClosureHandler.GetClosureTypeSpec(arg);
                if (spec == null || !env.ClosureHandler.IsAsyncClosure(spec))
                    return false;
                // Throwing baseline: requires an async-throws outer method — the
                // generated Task {} body uses `try await` and routes errors
                // through the async-throws harness's catch block.
                if (env.ClosureHandler.IsBaselineAsyncThrowingClosure(spec))
                    return !env.MethodDecl.IsAsync || !env.MethodDecl.Throws;
                // Non-throwing baseline (Session C): the adapter uses
                // `await` (no try). The outer method must still be async, but
                // it does NOT need to be throwing.
                if (env.ClosureHandler.IsBaselineAsyncNonThrowingClosure(spec))
                    return !env.MethodDecl.IsAsync;
                return true;
            });
    }

    /// <summary>
    /// Returns true when a method declares an async-throwing closure parameter
    /// that cannot be bridged by the Session A baseline adapter. The P/Invoke
    /// layer emits <c>(context, startFunc)</c> whenever it sees an
    /// async-throwing closure, but the matching Swift-side adapter is only
    /// generated when the method itself is promoted to a <c>@_cdecl</c> wrapper
    /// AND the outer method is <c>async throws</c> AND the closure has the
    /// baseline shape. If any of those fail, the P/Invoke and the Swift wrapper
    /// disagree on the parameter ABI — catch it at the member level so the
    /// method is skipped cleanly instead of emitting a broken binding.
    ///
    /// Also rejects methods whose parameter names collide with the reserved
    /// <c>_SBW</c> prefix used by generated handoff/adapter Swift identifiers —
    /// without this, a sibling param named e.g. <c>_SBWAdapted_x</c> would
    /// shadow the adapter var for a closure named <c>x</c>.
    /// </summary>
    public static bool HasUnbridgeableAsyncThrowingClosure(MethodEnvironment env)
    {
        var closureArgs = env.MethodDecl.CSSignature.Skip(1)
            .Where(env.ClosureHandler.IsClosure)
            .ToList();
        if (closureArgs.Count == 0) return false;

        bool hasBridgeableShape = closureArgs.Any(arg =>
        {
            var spec = env.ClosureHandler.GetClosureTypeSpec(arg);
            if (spec == null) return false;
            return env.ClosureHandler.IsAsyncThrowingClosure(spec)
                || (env.ClosureHandler.IsAsyncClosure(spec)
                    && env.ClosureHandler.IsBaselineAsyncNonThrowingClosure(spec));
        });
        if (!hasBridgeableShape) return false;

        // Reserved-prefix collision: any param named _SBW* would shadow generated
        // handoff/adapter/widened identifiers inside the Swift wrapper scope.
        if (env.MethodDecl.CSSignature.Skip(1).Any(p =>
            p.Name != null && p.Name.StartsWith("_SBW", System.StringComparison.Ordinal)))
        {
            return true;
        }

        return closureArgs.Any(arg =>
        {
            var spec = env.ClosureHandler.GetClosureTypeSpec(arg);
            if (spec == null) return false;
            if (env.ClosureHandler.IsAsyncThrowingClosure(spec))
            {
                // Throwing baseline requires outer async throws + Cdecl wrapper +
                // baseline shape; otherwise the P/Invoke emits (ctx, startFunc)
                // but no Swift adapter will materialise.
                return !(env.MethodDecl.UsesCdeclMethodWrapper &&
                         env.MethodDecl.IsAsync &&
                         env.MethodDecl.Throws &&
                         env.ClosureHandler.IsBaselineAsyncThrowingClosure(spec));
            }
            if (env.ClosureHandler.IsAsyncClosure(spec)
                && env.ClosureHandler.IsBaselineAsyncNonThrowingClosure(spec))
            {
                // Non-throwing baseline: outer method still has to be async +
                // Cdecl-wrapped, but Throws is NOT required.
                return !(env.MethodDecl.UsesCdeclMethodWrapper &&
                         env.MethodDecl.IsAsync);
            }
            return false;
        });
    }

    /// <summary>
    /// Returns true when a method has its own generic type parameters that are NOT inherited
    /// from the parent type. In Swift ABI JSON, methods on generic types include the parent's
    /// generic signature (e.g., &lt;τ_0_0 where τ_0_0 : Describable&gt;) in GenericSig. This means
    /// <see cref="MethodDecl.IsGeneric"/> returns true for ALL methods on generic types, not just
    /// methods with their own generic params (like <c>func pair&lt;T,U&gt;(...)</c>).
    ///
    /// This helper distinguishes the two cases: parent-inherited generics (which the generic
    /// dispatch emitter handles) vs method-own generics (which require different wrapper patterns).
    /// </summary>
    public static bool HasMethodOwnGenericParameters(MethodDecl methodDecl)
    {
        if (!methodDecl.IsGeneric)
            return false;

        // Collect parent type generic parameter names
        var parentTypeParamNames = methodDecl.ParentDecl is TypeDecl td && td.IsGeneric
            ? new HashSet<string>(td.GenericParameters.Select(p => p.TypeName))
            : new HashSet<string>();

        // A method has its own generics if any of its generic params are NOT in the parent's set
        return methodDecl.GenericParameters.Any(p => !parentTypeParamNames.Contains(p.TypeName));
    }

    /// <summary>
    /// Returns true if a type is a generic container that can't be handled by @_cdecl wrappers.
    /// Allows: Optional&lt;value-type&gt; (IndirectResult), Optional&lt;reference&gt; (nullable pointer),
    /// Array, Dictionary, Set (UnsafeRawPointer transport), Result&lt;T,E&gt; (UnsafeRawPointer transport).
    /// Blocks: Optional&lt;protocol existential&gt; (needs proxy conversion).
    /// </summary>
    public static bool IsUnsupportedGenericContainer(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (!CdeclParamMapper.IsGenericContainerType(typeSpec))
            return false;
        if (IsOptionalSupportedForCdecl(typeSpec, typeDatabase))
            return false;  // Optional<value-type/reference>: IndirectResult or nullable pointer
        if (IsSupportedCollectionType(typeSpec))
            return false;  // Array, Dictionary, Set pass through via UnsafeRawPointer
        if (IsSupportedResultType(typeSpec))
            return false;  // Result<T,E> passes through via UnsafeRawPointer
        return true;  // Optional<existential> still blocked
    }

    /// <summary>
    /// Returns true for Result&lt;T, E&gt; types that can be transported through @_cdecl
    /// wrappers via UnsafeRawPointer + initializeMemory(as:) / assumingMemoryBound(to:).pointee.
    /// Result is a frozen enum — its memory layout is stable and can be safely copied
    /// through the boundary as raw bytes.
    /// </summary>
    public static bool IsSupportedResultType(TypeSpec typeSpec)
    {
        return typeSpec is NamedTypeSpec named &&
            named.Name == "Swift.Result" && named.GenericParameters.Count == 2;
    }

    /// <summary>
    /// Returns true for metatype types (Any.Type, T.Type, etc.) which are not
    /// C-representable in @_cdecl wrappers. The generator renders them as bare "Type"
    /// which doesn't exist in Swift, causing compilation errors.
    /// Handles both flat names ("Any.Type") and nested NamedTypeSpec chains
    /// (Foundation → Decimal → Type) produced by TypeSpecParser for module-qualified metatypes.
    /// </summary>
    public static bool IsMetatypeType(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec named)
        {
            // Metatypes appear as "Any.Type", "SomeModule.SomeType.Type", or bare "Type"
            if (named.Name == "Type" || named.Name.EndsWith(".Type"))
                return true;

            // TypeSpecParser produces nested InnerType chains for dotted names:
            // "Foundation.Decimal.Type" → Foundation(Decimal(Type))
            // Walk the chain to find the leaf — if it's "Type", this is a metatype.
            var inner = named.InnerType;
            while (inner != null)
            {
                if (inner.Name == "Type" && inner.InnerType == null)
                    return true;
                inner = inner.InnerType;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="typeSpec"/> is either a metatype directly
    /// (<see cref="IsMetatypeType"/>) or a single-arg <c>Swift.Optional</c> whose payload is
    /// a metatype (<c>AnyClass.Type?</c>, <c>Foundation.Decimal.Type?</c>). Wrapper-eligibility
    /// gates use this so the Swift wrapper does not try to render <c>(any AnyObject.Type).self</c>
    /// or fall back to the bare token <c>Type</c>. Distinct from <see cref="IsMetatypeType"/>
    /// because <see cref="MetatypeStrategy"/> intentionally only collapses bare metatypes —
    /// collapsing <c>Optional&lt;Metatype&gt;</c> there would lose the Optional wrapping in the
    /// type-record graph.
    /// </summary>
    public static bool IsMetatypeTypeIncludingOptional(TypeSpec typeSpec)
    {
        if (IsMetatypeType(typeSpec))
            return true;
        if (typeSpec is NamedTypeSpec { Name: "Swift.Optional", GenericParameters.Count: 1 } opt)
            return IsMetatypeType(opt.GenericParameters[0]);
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
    /// - Optional&lt;Any&gt;: nullable pointer ABI (UnsafeMutableRawPointer?) with AnyObject reconstruction
    /// - Optional&lt;Self&gt;: nullable class pointer ABI (ObjC-bridged protocol methods)
    /// Returns false for Optional&lt;protocol existential&gt; which needs proxy conversion
    /// that the @_cdecl IndirectResult path doesn't handle.
    /// </summary>
    public static bool IsOptionalSupportedForCdecl(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (!IsOptionalType(typeSpec))
            return false;
        // Optional<protocol existential> needs special proxy conversion
        if (CdeclParamMapper.IsProtocolExistentialType(typeSpec, typeDatabase))
        {
            if (typeSpec is NamedTypeSpec optSpec && optSpec.GenericParameters.Count == 1)
            {
                // Exception: Optional<Any> (empty protocol list) — passed as nullable pointer.
                // Used for NSObject.isEqual(_ object: Any?) and similar ObjC-inherited methods.
                // CdeclParamMapper handles reconstruction via Unmanaged<AnyObject>.
                if (optSpec.GenericParameters[0] is ProtocolListTypeSpec { Protocols.Count: 0 })
                    return true;
                // Exception: Optional<Self> — Self resolves to the concrete class type at call site.
                // Used for ObjC-bridged protocol methods like decodedObject(fromAPIResponse:) -> Self?.
                // The DynamicSelf fast path in TryGetTypeRecord returns AnyType{Kind=Protocol},
                // which incorrectly flags Optional<Self> as Optional<existential>. Allow it here.
                if (optSpec.GenericParameters[0].IsDynamicSelf)
                    return true;
            }
            return false;
        }
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
    ///
    /// Path 3: Concrete-class fallback for modules that ship Swift classes whose names do not
    /// always match an ObjC class prefix (RealityFoundation.Entity, RealityKit.AnchorEntity,
    /// SceneKit.ProgramNode). Opt-in per-module via the <c>concreteClassFallback</c> flag in
    /// <c>apple-frameworks.json</c>. Same defense-in-depth guards as Path 2.
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
            // ObjC-bridgeable value types (e.g., Foundation.URL) bridge to ObjC class pointers
            // at the @_cdecl boundary via _ObjectiveCBridgeable — they use nullable pointer ABI like classes.
            if (MarshallingHelpers.IsObjCBridgeable(typeRecord))
                return true;

            // ObjC-bridged structs/enums: allow nullable pointer ABI for types that bridge
            // to ObjC classes via _ObjectiveCBridgeable (e.g., IndexPath → NSIndexPath).
            // The getter returns via `as AnyObject` and setter reconstructs via
            // `Unmanaged<AnyObject>.fromOpaque($0).takeUnretainedValue() as! T`.
            // Exclude NSString typedefs (e.g., CALayerContentsGravity) — they wrap NSString
            // via RawRepresentable, not _ObjectiveCBridgeable. `as AnyObject` gives a boxed
            // Swift struct, not NSString, so round-trip fails.
            if (typeRecord.Kind == TypeRecordKind.Struct || typeRecord.Kind == TypeRecordKind.Enum)
            {
                if (MarshallingHelpers.IsObjCBridged(typeRecord) &&
                    !(AppleFrameworkRegistry.TryGetNetTypeName(innerNamed.Name, out var remapped) &&
                      remapped == "Foundation.NSString"))
                    return true;
                return false;
            }

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

        // Path 3: Concrete-class fallback for modules that ship Swift classes whose names
        // don't always match an ObjC class prefix (RealityFoundation.Entity,
        // RealityKit.AnchorEntity, SceneKit.ProgramNode). Same defense-in-depth as Path 2,
        // plus the same module/value-type/nested guards TypeProjectionFactory uses.
        if (!innerNamed.ContainsGenericParameters &&
            !AppleFrameworkRegistry.IsPointerType(innerNamed.Name) &&
            !AppleFrameworkRegistry.IsNestedType(innerNamed.Name) &&
            !AppleFrameworkRegistry.IsKnownValueType(innerNamed.Name))
        {
            var dotIndex = innerNamed.Name.IndexOf('.');
            if (dotIndex > 0)
            {
                var moduleName = innerNamed.Name.Substring(0, dotIndex);
                if (AppleFrameworkRegistry.IsConcreteClassFallbackModule(moduleName))
                    return true;
            }
        }

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
        // Guard 5c: inout parameters on generic parent types — concrete (non-T-referencing)
        // inout params are threaded through the protocol boundary as UnsafeMutableRawPointer,
        // with load/call/write-back inside the extension body (mirroring the direct @_cdecl
        // pattern). T-referencing inout params are still rejected because a typed pointer
        // can't bind to an opaque generic parameter at the protocol signature level.
        if (parentTypeDecl?.IsGeneric == true)
        {
            var parentGenericParamNames = parentTypeDecl.GenericParameters
                .Select(p => p.TypeName)
                .ToHashSet();
            if (env.MethodDecl.CSSignature.Skip(1).Any(a => a.IsInOut &&
                TypeSpecReferencesGenericParam(a.SwiftTypeSpec, parentGenericParamNames)))
                return false;
        }
        // Guard 5d: inout params with types that have C# ABI mismatch.
        // MapInout produces a single UnsafeMutableRawPointer. Types with multi-word
        // C# representation (String → 2 nint words), non-pointer C# representation
        // (non-frozen → SafeHandle, classes → Unmanaged), or incompatible copy semantics
        // (non-copyable) create param type/count mismatches with the PInvokeEmitter output.
        if (HasInoutWithAbiMismatch(env))
            return false;
        // Guard 6: No method-level generics
        if (env.MethodDecl.IsGeneric)
            return false;
        // Guard 6a: Raw generic type params in signature (e.g., from parent generics leaking)
        if (HasRawGenericTypeParams(env.MethodDecl))
            return false;
        // Guard 6b: Not actor parent (nonisolated members opt out, unless the signature
        // contains a parameterized-protocol type that requires iOS 16+ runtime support).
        // Async methods (including instance methods normalized from actor isolation) route
        // through the async @_cdecl wrapper and are safe — Task { await self.method() }
        // handles the actor hop automatically.
        if (parentTypeDecl is ClassDecl { IsActor: true } && !env.MethodDecl.IsAsync &&
            (!env.MethodDecl.IsNonisolated ||
             SignatureContainsParameterizedProtocol(env.MethodDecl, env.TypeDatabase)))
            return false;
        // Guard 10: No variadic parameters — Swift variadic (T...) appears as Array<T> in ABI JSON.
        // Wrapper would pass [T] where T... is expected, causing "cannot pass array" compile error.
        if (env.MethodDecl.HasVariadicParameter)
            return false;
        // Guard 11: (removed — noncopyable struct parents now use borrowing pointer semantics)
        // Guards 15-15d: Return type checks
        var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;
        // Opaque returns (some Protocol): ALLOWED — @_cdecl wrapper boxes into existential.
        // Closure returns: blocked here because wrapper-owned trampoline paths (ClosureEmitter,
        // OptionalPointerWrapper, ArraySliceNormalization) use this predicate to check function shape.
        // MethodWrapperEmitter.ShouldEmitWrapper allows closure returns, but the
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
        // Guard 10b: No metatype return (including Optional<Metatype>). Secondary wrapper
        // paths (closure / large-optional / ArraySlice) reach this gate independently of
        // MethodWrapperEmitter.ShouldEmitWrapper, so the metatype check has to be here too —
        // otherwise a method with a closure/large-optional/ArraySlice trigger plus an
        // AnyClass.Type? return would slip through and emit an invalid wrapper.
        if (IsMetatypeTypeIncludingOptional(returnSpec))
            return false;
        return true;
    }

    /// <summary>
    /// Returns true if any inout parameter has a type whose C# ABI representation
    /// doesn't match MapInout's single UnsafeMutableRawPointer pattern.
    /// Types with multi-word decomposition (String → 2 nint words), non-pointer
    /// representation (non-frozen → SafeHandle, classes → Unmanaged), or
    /// incompatible copy semantics (non-copyable) are rejected.
    /// </summary>
    public static bool HasInoutWithAbiMismatch(MethodEnvironment env)
    {
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (!arg.IsInOut) continue;
            if (arg.SwiftTypeSpec is NamedTypeSpec inoutNamed && inoutNamed.Name == "Swift.String")
                return true;
            if (env.TypeDatabase.TryGetTypeRecord(arg.SwiftTypeSpec, out var inoutTypeRec))
            {
                if (inoutTypeRec.Kind == TypeRecordKind.Class ||
                    MarshallingHelpers.IsObjCBridged(inoutTypeRec) ||
                    MarshallingHelpers.IsObjCRooted(inoutTypeRec) ||
                    MarshallingHelpers.IsObjCBridgeable(inoutTypeRec) ||
                    inoutTypeRec.Kind == TypeRecordKind.Protocol ||
                    inoutTypeRec.Kind == TypeRecordKind.Existential ||
                    inoutTypeRec.Flags.HasFlag(TypeRecordFlags.NonCopyable) ||
                    (!MarshallingHelpers.IsTypeFrozen(inoutTypeRec) && inoutTypeRec.Kind != TypeRecordKind.Enum))
                    return true;
            }
        }
        return false;
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
            // The wrapper-helper-path gates only apply when the method actually routes through
            // EmitGenericStaticDispatchMethod (which calls EmitMetadataAccessorHelperIfNeeded).
            // Concrete instance methods on generic class parents use protocol-cast dispatch
            // and never touch _sbw_meta_*, so we MUST NOT report them as gate-rejected.
            // Mirrors GenericDispatchEmitter.CanEmitGenericDispatch's per-path gate placement.
            if (GenericDispatchEmitter.NeedsStaticDispatch(env, parentTypeDecl, GenericDispatchKind.Method))
            {
                if (MetatypeHelperEmitter.HasUnresolvableTypeConformances(parentTypeDecl, env.TypeDatabase))
                    return "generic_parent_unresolved_pwt_constraint";
                if (MetatypeHelperEmitter.WouldExceedRegisterArgumentThreshold(parentTypeDecl, env.TypeDatabase))
                    return "generic_parent_metadata_buffer_mode";
            }
            if (!GenericDispatchEmitter.CanEmitGenericDispatch(env, parentTypeDecl, GenericDispatchKind.Method))
                return "generic_parent";
        }

        // 6. No method-level generics (only those with method-own generic params)
        if (HasMethodOwnGenericParameters(env.MethodDecl))
            return "method_level_generics";

        // 6a. Raw generic type params in signature (e.g., from parent generics leaking)
        if (HasRawGenericTypeParams(env.MethodDecl))
            return "raw_generic_type_params";

        // 6b. Custom actor types (requires async dispatch) — nonisolated members opt out,
        // but only if their signature is safe to spell at the library's deployment target.
        // Parameterized-protocol usage (e.g., EventStream<any UIEvent>) requires iOS 16+ runtime
        // support, so those wrappers fall back to async dispatch.
        if (parentTypeDecl is ClassDecl { IsActor: true } &&
            (!env.MethodDecl.IsNonisolated ||
             SignatureContainsParameterizedProtocol(env.MethodDecl, env.TypeDatabase)))
            return "actor_type";

        // 6c. Per-member custom actor isolation (not @MainActor)
        if (env.MethodDecl.IsActorIsolated && !env.MethodDecl.IsMainActorIsolated &&
            (!env.MethodDecl.IsNonisolated ||
             SignatureContainsParameterizedProtocol(env.MethodDecl, env.TypeDatabase)))
            return "custom_actor_isolated";

        // 7. Not async
        if (env.MethodDecl.IsAsync)
            return "async_method";

        // 8. Closure parameters
        if (env.MethodDecl.CSSignature.Skip(1).Any(env.ClosureHandler.IsClosure))
        {
            if (!ClosureEmitter.NeedsClosureCdeclWrapper(env.MethodDecl, env.ClosureHandler))
                return "closure_params";
            // Sync outer methods cannot bridge async closures (no Task harness to host
            // the adapter). Keep rejecting regardless of baseline-shape eligibility —
            // async-closure bridging is currently gated to async outer methods only.
            if (env.MethodDecl.CSSignature.Skip(1)
                    .Where(env.ClosureHandler.IsClosure)
                    .Any(arg =>
                    {
                        var spec = env.ClosureHandler.GetClosureTypeSpec(arg);
                        return spec != null && env.ClosureHandler.IsAsyncClosure(spec);
                    }))
                return "closure_params";
        }

        // 11. (removed — noncopyable struct parents now use borrowing pointer semantics)

        // 11b. (removed — inout parameters now use UnsafeMutableRawPointer with write-back semantics)

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

        // 14b. No metatype parameters (including Optional<Metatype>)
        if (env.MethodDecl.CSSignature.Skip(1).Any(a => IsMetatypeTypeIncludingOptional(a.SwiftTypeSpec)))
            return "metatype_param";

        // 14c-15: Return type checks
        {
            var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;

            if (IsMetatypeTypeIncludingOptional(returnSpec))
                return "metatype_return";

            // Opaque returns (some Protocol): now supported — @_cdecl wrapper boxes into existential.

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
    /// Note: With CallConvSwift eliminated, all eligible methods now get @_cdecl wrappers
    /// regardless of this check. This method is retained for diagnostic/reporting purposes
    /// (SB0001 diagnostic, operator wrapper decisions).
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

        // All class allocating constructors need @_cdecl wrappers.
        // Swift's allocating init (__allocating_init) uses @convention(method) which passes
        // @thick Self.Type as a hidden metatype parameter — same pattern as static methods.
        // On Mono JIT, the CallConvSwift P/Invoke doesn't include this parameter, causing
        // the call to read garbage from the metatype register → SIGSEGV (jit-info.c:918).
        // Even parameterless constructors crash (Keychain(), MD5()) because the hidden
        // metatype is always present in the allocating init ABI. Constructors with MarshalAs
        // parameters (BooleanDisposable(bool)) compound the issue.
        // The generic constructor check above already handles generic classes for metatype
        // dispatch; this catches the remaining non-generic class constructors.
        if (env.MethodDecl.IsConstructor && env.ParentDecl is ClassDecl)
            return true;

        // All struct constructors need @_cdecl wrappers because:
        // 1. Frozen structs: Larger ones use SwiftIndirectResult to return the constructed value,
        //    and Mono JIT can't handle CallConvSwift + SwiftIndirectResult → jit-info.c:918
        //    assertion (e.g., URLEncoding(destination:arrayEncoding:boolEncoding:)).
        //    Even smaller frozen struct constructors with MarshalAs parameters (bool, enum)
        //    crash on Mono with CallConvSwift.
        // 2. Non-frozen structs: Also use SwiftIndirectResult (always passed indirectly),
        //    causing the same Mono JIT crash. E.g., LottieColor(r:g:b:a:denominator:) uses
        //    CallConvSwift + SwiftIndirectResult with only primitive params, but still crashes.
        // The @_cdecl wrapper uses a resultPtr buffer pattern that avoids both issues.
        // Note: failable non-frozen struct constructors are blocked by ShouldEmitWrapper
        // (VWT-based initialization incompatible with @_cdecl's Optional<T>.initialize(to:)).
        if (env.MethodDecl.IsConstructor && env.ParentDecl is StructDecl)
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
        // All class instance property accessors need @_cdecl wrappers:
        // - Non-final: Tj dispatch thunks (vtable indirection) crash on both runtimes.
        // - Final: Direct symbols with SwiftSelf — NativeAOT handles this correctly but
        //   Mono JIT can't handle CallConvSwift + SwiftSelf → jit-info.c:918 assertion
        //   (e.g., ImagePrefetcher.Priority, ImageTask.Priority on Nuke).
        // Static properties are excluded — they don't use SwiftSelf.
        if (env.ParentDecl is ClassDecl && !propertyDecl.IsStatic)
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
    /// Detects the genuine ABI-unsafe direct-CallConvSwift case: an async method that lost
    /// its <c>@_cdecl</c> wrapper and would otherwise emit a P/Invoke against Swift's mangled
    /// async symbol with <see cref="CallConvSwift"/>. The Swift async ABI is not stable
    /// for direct P/Invoke — continuation tracking, executor hopping, and error propagation
    /// are all under-specified at this boundary — so the method must be skipped with a
    /// diagnostic instead of emitted as a working-looking-but-broken API.
    ///
    /// <para><b>Discriminator:</b> behind the flag-family early-returns (any wrapper flag means
    /// a real Swift symbol exists with Swift CC, which is safe), the predicate fires only
    /// for async methods whose signature carries a feature the legacy direct path cannot
    /// service correctly:</para>
    /// <list type="bullet">
    /// <item><b>Method-level generics</b> (e.g. <c>some Protocol</c> in StoreKit
    /// <c>Product.purchase(confirmIn: some UIScene)</c>) — the cdecl wrapper would need to
    /// emit a generic Swift function that captures the metatype, which the wrapper emitter
    /// doesn't yet support. The legacy path has no place to plumb the metatype.</item>
    /// <item><b>Closure parameters</b> on async methods — closure ownership transfer needs the
    /// destroy-thunk projection that lives only on the cdecl-wrapped path. The legacy
    /// <c>@_silgen_name</c> trampoline cannot bridge a Swift async to a cdecl callback.
    /// Pinned by <c>SilgenNameTrampolineTests.Async_WithClosureParam_NoConversion_*</c>.</item>
    /// <item><b>Existential parameters</b> (<c>any Protocol</c>, protocol compositions) on
    /// async methods — PWT passing is ABI-unsafe through the legacy path.</item>
    /// </list>
    ///
    /// <para><b>Empirically working paths intentionally NOT flagged:</b> async methods on
    /// generic class parents (<c>AsyncGenericContainer&lt;T&gt;.processAsync</c>,
    /// <c>fetchOrThrow</c>) where the metatype is captured at instance-construction time
    /// via the existing PWT machinery. Sync methods through the legacy path are also out of
    /// scope here — they go through SB0001 / <see cref="HasNonBlittablePInvokeTypes"/>.</para>
    ///
    /// <para><b>Out of scope:</b> the wrapper-library "symbol missing in wrapper dylib" case
    /// (<c>bug-0.10.0-generic-async-wrapper-symbol-missing</c> — MusicKit
    /// <c>MusicPlayer.Queue.insert&lt;S: Sequence&gt;</c>) cannot be detected from method
    /// flags alone: many legitimate paths (ArraySlice, default-parameter, metatype-array,
    /// protocol-extension wrappers) set <see cref="MethodDecl.UsesWrapperLibrary"/> without
    /// a <c>@_cdecl</c> flag because they emit <c>@_silgen_name</c> wrappers whose symbols
    /// ARE present in the wrapper dylib. Distinguishing real misses requires a wrapper-export
    /// cross-reference and is owned by Session C.</para>
    /// </summary>
    /// <param name="env">The method environment (after all wrapper flags have been resolved).</param>
    /// <returns><c>true</c> when the C# emission must skip this method with an
    /// "ABI-unsafe direct call" diagnostic; <c>false</c> when the legacy path is safe.</returns>
    public static bool IsSkippedWrapperDirectPInvoke(MethodEnvironment env)
    {
        // Predicate is xcframework-mode only. Outside xcframework mode the wrapper library
        // doesn't exist, no wrapper symbols are emitted, and the legacy CallConvSwift
        // direct-P/Invoke is the only available path — there's no "skipped wrapper" condition
        // to detect. Test fixtures that don't set AsyncLibraryName fall into this branch.
        if (!IsXCFrameworkMode(env.TypeDatabase))
            return false;

        // Methods routed through any wrapper path that physically emits a Swift symbol
        // (cdecl wrapper, native ARM64 thunk, OptionalPointer wrapper, standalone closure
        // wrapper) have a real entry point in the wrapper dylib — they are safe.
        if (env.MethodDecl.UsesCdeclWrapper
            || env.MethodDecl.UsesNativeThunk
            || env.MethodDecl.HasOptionalPointerWrapper
            || env.MethodDecl.HasClosureCdeclWrapper)
            return false;

        // Methods routed through the wrapper library (via @_silgen_name in ArraySlice,
        // default-parameter, metatype-array, protocol-extension emitters, etc.) have an
        // emitted wrapper symbol with Swift CC — calling them with CallConvSwift is correct
        // even without a @_cdecl flag. Filter them out before flagging async.
        if (env.MethodDecl.UsesWrapperLibrary)
            return false;

        // Sync methods through the legacy direct-CallConvSwift path are diagnosed via SB0001
        // / HasNonBlittablePInvokeTypes (they're emitted with an [Obsolete] warning, not
        // skipped). The Bundle 7 trigger is async-specific because the Swift async ABI is the
        // boundary that's under-specified for direct P/Invoke.
        if (!env.MethodDecl.IsAsync)
            return false;

        // Method-level generics on async: `some Protocol` and explicit `<T>` parameters
        // require per-call metatype passing the legacy async path can't synthesise. Use
        // HasMethodOwnGenericParameters to exclude parent-type generics (the parser folds
        // the parent's generic signature into MethodDecl.GenericParameters), so plain async
        // methods on `Container<T>` continue to flow through the legacy path.
        if (HasMethodOwnGenericParameters(env.MethodDecl))
            return true;

        // Async + closure / existential parameter: closures need the destroy-thunk projection
        // that only the cdecl wrapper plumbs through; existentials (any Protocol /
        // ProtocolListTypeSpec) need PWT passing. Skip the return slot (CSSignature[0]).
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (env.ClosureHandler.IsClosure(arg))
                return true;
            if (env.ExistentialHandler.IsExistential(arg.SwiftTypeSpec))
                return true;
        }

        // Simple-signature async — legacy direct path is empirically safe.
        return false;
    }

    /// <summary>
    /// Returns true when a method has no @_cdecl wrapper or native thunk AND has non-blittable
    /// P/Invoke types. Used for SB0001 diagnostic — these methods are still emitted but may
    /// crash at runtime. Suppression was not feasible because it breaks protocol conformance (CS0535).
    /// </summary>
    public static bool ShouldSuppressNonBlittableCallConvSwift(MethodEnvironment env)
    {
        // If the method already has a @_cdecl wrapper or native thunk, it's safe — no suppression needed
        if (env.MethodDecl.UsesCdeclWrapper || env.MethodDecl.UsesNativeThunk)
            return false;

        // Check the method wrapper decision: only flag when wrapping is not possible
        var decision = env.MethodDecl.IsConstructor
            ? DetermineConstructorWrapperDecision(env)
            : DetermineMethodWrapperDecision(env);

        // CannotWrap: ShouldEmitWrapper=false — method has no wrapper/thunk path
        // WrapperRequired: will get a wrapper — safe
        if (decision != WrapperDecision.CannotWrap)
            return false;

        // Wrapping is impossible. Check if the P/Invoke would have non-blittable types.
        return HasNonBlittablePInvokeTypes(env);
    }

    /// <summary>
    /// Property-specific overload: returns true when a property accessor has no @_cdecl
    /// wrapper or native thunk AND has non-blittable P/Invoke types. Used for SB0001 diagnostic.
    /// </summary>
    public static bool ShouldSuppressNonBlittableCallConvSwift(PropertyDecl propertyDecl, MethodEnvironment env)
    {
        // If the property already has a @_cdecl wrapper or native thunk, it's safe
        if (env.MethodDecl.UsesCdeclWrapper || env.MethodDecl.UsesNativeThunk)
            return false;

        var decision = DeterminePropertyWrapperDecision(propertyDecl, env);

        if (decision != WrapperDecision.CannotWrap)
            return false;

        // Wrapping is impossible. Check if the P/Invoke would have non-blittable types.
        return HasNonBlittablePInvokeTypes(env, propertyDecl);
    }

    /// <summary>
    /// Checks whether a method's P/Invoke signature would contain non-blittable types
    /// (SafeHandle from non-frozen structs/classes, tuples, generic containers) when using
    /// CallConvSwift directly with no @_cdecl wrapper or native thunk. Drives SB0001.
    ///
    /// Uses the narrower <see cref="IsParamPInvokeNonBlittable"/> /
    /// <see cref="IsReturnTypePInvokeNonBlittable"/> classifiers which mirror the actual
    /// ITypeProjection.PInvokeType decisions, rather than the broader
    /// <see cref="IsParamTypeCdeclRequired"/> classifier that drives @_cdecl wrapper emission.
    /// This keeps SB0001 from over-broadcasting on shapes whose direct-CallConvSwift P/Invoke
    /// is genuinely safe (e.g., <c>Swift.String</c> via blittable <c>SwiftString.Buffer</c>,
    /// complex enums via <c>IntPtr</c>).
    /// </summary>
    internal static bool HasNonBlittablePInvokeTypes(MethodEnvironment env)
    {
        // Async methods always go through a Swift-side @_silgen_name (or @_cdecl) wrapper that
        // converts non-blittable params to UnsafeRawPointer and boxes non-frozen self. The C#
        // P/Invoke signature is uniformly blittable: callback/errorCallback/taskId + IntPtrs +
        // function pointers. CallConvSwift on that shape is ABI-stable on both Mono and NativeAOT.
        if (env.MethodDecl.IsAsync)
            return false;

        // Non-frozen struct instance members: self is IntPtr (non-blittable with CallConvSwift)
        if (IsNonFrozenStructInstanceMember(env))
            return true;

        // Check return type for non-blittable types (narrower than IsReturnTypeCdeclRequired)
        var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;
        if (!returnSpec.IsEmptyTuple && IsReturnTypePInvokeNonBlittable(returnSpec, env.TypeDatabase))
            return true;

        // Check parameters for non-blittable types (narrower than IsParamTypeCdeclRequired).
        // Skip closures — they use function pointers / IntPtr in P/Invoke, which are blittable.
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;
            if (env.ClosureHandler.IsClosure(arg))
                continue;
            if (IsParamPInvokeNonBlittable(arg.SwiftTypeSpec, env))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Property-specific overload: checks whether a property accessor P/Invoke would
    /// contain non-blittable types when using CallConvSwift directly. Uses the narrower
    /// SB0001 classifiers.
    /// </summary>
    internal static bool HasNonBlittablePInvokeTypes(MethodEnvironment env, PropertyDecl propertyDecl)
    {
        // Non-frozen struct instance properties: same non-blittable self issue
        if (!propertyDecl.IsStatic && IsNonFrozenStructInstanceMember(env))
            return true;

        var typeSpec = propertyDecl.SwiftTypeSpec;

        // Check as return type (getter)
        if (IsReturnTypePInvokeNonBlittable(typeSpec, env.TypeDatabase))
            return true;

        // Check as parameter type (setter)
        if (propertyDecl.Accessors.OfType<SetAccessorDecl>().Any())
        {
            if (IsParamPInvokeNonBlittable(typeSpec, env))
                return true;
        }

        return false;
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
    /// Checks: non-blittable (SafeHandle from classes, non-frozen structs, complex enums),
    /// ValueTuple, custom float struct, large integer struct.
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

        // Swift class parameters → NonFrozenSafeHandle in PInvokeEmitter → non-blittable → @_cdecl required.
        // PInvokeEmitter treats classes as non-frozen types (they're not frozen), so class params get
        // SafeHandle in the P/Invoke signature. SafeHandle is non-blittable with CallConvSwift — both
        // Mono and NativeAOT throw InvalidProgramException. Route through @_cdecl wrapper where
        // CallConvCdecl handles SafeHandle marshalling correctly.
        // ObjC bridged/rooted classes are excluded — PInvokeEmitter handles them separately with
        // ObjCBridged type (IntPtr via .Handle), which is blittable.
        if (typeRecord.Kind == TypeRecordKind.Class &&
            !MarshallingHelpers.IsObjCBridged(typeRecord) &&
            !MarshallingHelpers.IsObjCRooted(typeRecord))
            return true;

        // ObjC bridged/rooted classes, simple enums → IntPtr → safe
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
    /// SB0001-specific narrower classifier: returns true only when a parameter would emit a
    /// non-blittable P/Invoke type for the direct-CallConvSwift path (no @_cdecl wrapper, no
    /// native thunk). Mirrors the type-projection decisions made in PInvokeEmitter so the
    /// diagnostic matches what's actually emitted.
    ///
    /// Differs from <see cref="IsParamTypeCdeclRequired"/>: that classifier drives @_cdecl
    /// wrapper emission and is intentionally broader (it also flags ABI-stable shapes the
    /// generator chooses to wrap when possible). The SB0001 gate must only fire when the
    /// direct CallConvSwift call is genuinely unsafe — i.e., the actual P/Invoke parameter
    /// type is non-blittable or has a register layout Mono/NativeAOT can't handle.
    ///
    /// Carve-outs vs <see cref="IsParamTypeCdeclRequired"/>:
    /// <list type="bullet">
    ///   <item><c>Swift.String</c> (frozen + RequiresMemoryManagement) → FrozenBuffer projection
    ///         (<c>SwiftString.Buffer</c>, two-word blittable struct).</item>
    ///   <item>Complex enum (non-Simple) → EnumSafeHandle marker → <c>IntPtr</c> in P/Invoke.</item>
    /// </list>
    /// </summary>
    internal static bool IsParamPInvokeNonBlittable(TypeSpec typeSpec, MethodEnvironment env)
    {
        // Primitives are always safe
        if (CdeclParamMapper.IsCdeclPrimitive(typeSpec))
            return false;

        // ValueTuple → StructLayout.Auto → non-blittable in P/Invoke
        if (typeSpec is TupleTypeSpec tts && !tts.IsEmptyTuple)
            return true;

        // Generic containers (Optional, Array, Dictionary, Set, Result) — direct-CallConvSwift
        // path goes through SafeHandle / opaque-pointer marshalling that's not blittable. The
        // OptionalPointer wrapper is the exception, but a method using it has UsesCdeclWrapper /
        // UsesFreeFunctionWrapper set, which short-circuits the SB0001 gate before this is reached.
        if (CdeclParamMapper.IsGenericContainerType(typeSpec))
            return true;

        if (!env.TypeDatabase.TryGetTypeRecord(typeSpec, out var typeRecord))
            return false;

        // ObjC bridged/rooted/bridgeable types → IntPtr in P/Invoke (blittable)
        if (MarshallingHelpers.IsObjCBridged(typeRecord) ||
            MarshallingHelpers.IsObjCRooted(typeRecord) ||
            MarshallingHelpers.IsObjCBridgeable(typeRecord))
            return false;

        // Native-remapped types: ObjC-bridgeable already handled above.
        // Frozen native-remapped (Data, Date) → wrapper struct (blittable Double or two-word struct).
        // Non-frozen native-remapped → SafeHandle in sync (non-blittable), IntPtr in async (blittable).
        if (typeRecord.NativeTypeName != null)
        {
            if (!MarshallingHelpers.IsTypeFrozen(typeRecord))
                return !env.MethodDecl.IsAsync;
            return false;
        }

        // Simple enum → underlying integer (blittable)
        // Complex enum → EnumSafeHandle marker → IntPtr in P/Invoke (blittable in both sync & async)
        if (typeRecord.Kind == TypeRecordKind.Enum)
            return false;

        // Non-frozen struct/class → NonFrozenSafeHandle in sync (SafeHandle in P/Invoke, non-blittable)
        //                         → NonFrozenIntPtr in async (IntPtr in P/Invoke, blittable)
        if (!MarshallingHelpers.IsTypeFrozen(typeRecord))
            return !env.MethodDecl.IsAsync;

        // Frozen struct
        //  - With memory management → FrozenBuffer projection: {Type}.Buffer (two-word blittable struct)
        if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            return false;

        //  - Without memory management:
        //    - System frozen (CGRect, CGSize, etc.): pure C struct, register layout always safe
        //    - Custom frozen with float/bool fields → GPR/FPR mismatch with .NET CallConvSwift
        //    - Custom frozen > MaxParamSize bytes → NativeAOT SIGSEGV
        if (typeSpec is NamedTypeSpec named && CdeclParamMapper.IsSystemFrozenStruct(named))
            return false;

        if (HasIncompatibleFields(typeRecord))
            return true;

        if (typeRecord.InlineSize.HasValue && typeRecord.InlineSize.Value > AbiSizeLimits.MaxParamSize)
            return true;

        return false;
    }

    /// <summary>
    /// SB0001-specific narrower classifier for return types: returns true only when the
    /// direct-CallConvSwift P/Invoke return type is non-blittable or has a register layout
    /// Mono/NativeAOT can't handle. Mirrors the ITypeProjection.PInvokeType decisions.
    ///
    /// Carve-outs vs <see cref="IsReturnTypeCdeclRequired"/>:
    /// <list type="bullet">
    ///   <item><c>Swift.String</c> return → FrozenBuffer projection (<c>SwiftString.Buffer</c>,
    ///         two-word blittable struct returned by-value or via IndirectResult).</item>
    /// </list>
    /// Complex enum returns are still flagged: the SwiftIndirectResult marshalling path crashes
    /// Mono JIT in practice, matching the existing <see cref="IsReturnTypeCdeclRequired"/> rule.
    /// </summary>
    internal static bool IsReturnTypePInvokeNonBlittable(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (CdeclParamMapper.IsCdeclPrimitive(typeSpec))
            return false;

        if (typeSpec is TupleTypeSpec tts && !tts.IsEmptyTuple)
            return true;

        // Closure returns route through 16-byte SwiftClosureData passed by-value across CallConvSwift,
        // which crashes Mono JIT (`!ji->async` multi-register struct return) and NativeAOT (SIGSEGV).
        if (typeSpec is ClosureTypeSpec)
            return true;

        if (!typeDatabase.TryGetTypeRecord(typeSpec, out var typeRecord))
            return false;

        // ObjC bridged/rooted/bridgeable returns → IntPtr (blittable)
        if (MarshallingHelpers.IsObjCBridged(typeRecord) ||
            MarshallingHelpers.IsObjCRooted(typeRecord) ||
            MarshallingHelpers.IsObjCBridgeable(typeRecord))
            return false;

        // Native-remapped frozen returns → wrapper struct (blittable). Non-frozen native-remapped
        // returns flow through IntPtr / IndirectResult — still blittable on the return slot.
        if (typeRecord.NativeTypeName != null)
            return false;

        // Complex enum returns: SwiftIndirectResult marshalling crashes Mono JIT (matches the
        // existing IsReturnTypeCdeclRequired rule with empirical evidence).
        if (typeRecord.Kind == TypeRecordKind.Enum &&
            !typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            return true;

        // Simple enum returns → underlying integer (blittable)
        if (typeRecord.Kind == TypeRecordKind.Enum)
            return false;

        // Non-frozen struct/class returns → IntPtr / SwiftIndirectResult (blittable)
        if (!MarshallingHelpers.IsTypeFrozen(typeRecord))
            return false;

        // Frozen with memory management → Buffer return (blittable two-word struct)
        if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            return false;

        // Frozen without memory management:
        //  - System frozen (CGRect, etc.) → pure C register layout, safe
        //  - Custom frozen with float/bool fields → Mono SIGSEGV on by-value return
        //  - Custom frozen > MaxParamSize bytes → NativeAOT SIGSEGV
        if (typeSpec is NamedTypeSpec named && CdeclParamMapper.IsSystemFrozenStruct(named))
            return false;

        if (HasIncompatibleFields(typeRecord))
            return true;

        if (typeRecord.InlineSize.HasValue && typeRecord.InlineSize.Value > AbiSizeLimits.MaxParamSize)
            return true;

        return false;
    }

    /// <summary>
    /// Returns true for types from C-bridging modules (CoreGraphics, CoreFoundation, Darwin, simd).
    /// These modules expose pure C structs via Swift overlays — they have well-defined, platform-stable
    /// register layouts that both Mono and NativeAOT handle correctly at any size.
    /// Evidence: CGRect (32 bytes) passes on both runtimes.
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
    /// not the ABI-level mangled names. Substitution applies recursively so bound generic
    /// arguments like <c>AliasGenericPayload&lt;τ_0_0&gt;</c> render as <c>AliasGenericPayload&lt;T&gt;</c>.
    /// </summary>
    public static string RenderSwiftTypeSpecWithSugaredNames(TypeSpec typeSpec, Dictionary<string, string> abiToSugaredName)
    {
        var rendered = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);
        foreach (var (abiName, sugared) in abiToSugaredName)
            rendered = System.Text.RegularExpressions.Regex.Replace(rendered, $@"(?<![\w_]){System.Text.RegularExpressions.Regex.Escape(abiName)}(?![\w_])", sugared);
        return rendered;
    }
}
