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
        => GetMemberRejectionReason(env, kind, isModuleInternal, isSpiProtected, isAsync,
            isActorIsolated, isMainActorIsolated, isNonisolated) is null;

    /// <summary>
    /// Reason-returning twin of <see cref="CanEmitMember"/>: returns the name of the first
    /// shared guard that rejects the member, or <c>null</c> when every shared guard passes.
    /// <see cref="CanEmitMember"/> is its boolean shim, so the predicate and the diagnostic
    /// can never drift apart (Finding 12). The reason is diagnostic only — it feeds logs and
    /// the emission-report skip-reason histogram, never the generated C#.
    /// </summary>
    public static string? GetMemberRejectionReason(
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
            return "xcframework_mode";

        // 2. Module internal (Method, Constructor, Property)
        if (kind is MemberKind.Method or MemberKind.Constructor or MemberKind.Property)
        {
            if (isModuleInternal)
                return "module_internal";
        }

        // 2b. Parent type is module-internal (Method, Constructor, Property, Subscript).
        // A *public* member on a `@usableFromInline internal` parent slips the
        // member-keyed `module_internal` arm above (the member's own flag is false),
        // but its @_cdecl wrapper body reconstructs `self` via the parent's
        // module-qualified name (`Unmanaged<Module.Internal>.fromOpaque(...)` /
        // `assumingMemoryBound(to: Module.Internal.self)`). The separate
        // wrapper-compilation module cannot name that internal type, so swiftc
        // rejects the wrapper and the post-processor strips it
        // (StripSubCause.InternalType). Reject the wrapper here, at emission, so it
        // is never produced: the non-wrapper path keeps the C# member as a direct
        // CallConvSwift P/Invoke to the dylib silgen symbol (precedent:
        // ClassHandler internal-class metadata accessor at ClassHandler.cs; the same
        // CS0535-avoidance policy that keeps non-blittable CallConvSwift members
        // emitting). This drives the BindingTests internal-receiver strip to 0.
        //
        // SCOPE — sync members only (Method, Constructor, Property, Subscript). A
        // subscript is an accessor pair like a property, so it shares the property's
        // clean CallConvSwift fallback (its getter/setter resolve to bare silgen /
        // Tj symbols the dylib already exports) and is gated identically — without
        // it, a public subscript on an internal parent still emit-then-strips and
        // leaves the C# indexer bound to a stripped @_cdecl symbol (a runtime
        // EntryPointNotFoundException a compile-only gate cannot catch).
        //
        // The async, closure-@_cdecl, and operator promotion sites are NOT handled by
        // this arm, because those kinds have no clean CallConvSwift fallback: an async
        // member ALWAYS needs a Swift wrapper (which still names the parent under
        // @_silgen_name), a closure member degrades to the legacy CallConvSwift path
        // that crashes (InvalidProgramException), and a frozen-struct operator's direct
        // CallConvSwift P/Invoke segfaults ILC on NativeAOT (OperatorHandler.cs
        // documents the wrapper exists for exactly that reason). Because no fallback
        // exists, the correct outcome for those three shapes on an internal parent is
        // to DROP the member entirely rather than reject only the wrapper and keep the
        // member. That drop happens earlier, at emission: async / closure-bearing
        // methods are caught by MemberValidationPipeline.ValidateMethodEmission and
        // operators by OperatorHandler.EmitOperator, both with
        // SkipReason.ParentModuleInternalNoFallback. The net public API is identical to
        // the previous emit-then-strip + C# reconcile, so the SwiftWrapperPostProcessor
        // no longer strips any internal-receiver wrapper. (The post-processor remains in
        // place for the other strip classes it owns — NSInvocation, EveryProtocol /
        // safety-net placeholders, extension and private _SBW_ protocol blocks, and
        // standalone public wrapper funcs — none of which this emission-time gating
        // covers.)
        if (kind is MemberKind.Method or MemberKind.Constructor or MemberKind.Property
            or MemberKind.Subscript)
        {
            if (IsParentTypeModuleInternal(env))
                return "parent_module_internal";
        }

        // 3. SPI protected (Method, Property)
        if (kind is MemberKind.Method or MemberKind.Property)
        {
            if (isSpiProtected)
                return "spi_protected";
        }

        // 4. Non-copyable struct parent — ALLOWED. Noncopyable types get @_cdecl wrappers
        // with borrowing pointer semantics (no .pointee copy). Self is accessed inline through
        // self_.assumingMemoryBound(to:).pointee which gives a borrow in Swift 6.

        // 5. Async (Method, Constructor — Property/Subscript check differently via accessor)
        if (kind is MemberKind.Method or MemberKind.Constructor)
        {
            if (isAsync)
                return "async";
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
                return "actor_isolated";
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
            return "custom_actor_constructor";
        }

        // 7. Inherited generic context on parent (Method, Property, Constructor)
        // Nested types that inherit generic context from an outer parent
        // (e.g., AuthenticationInterceptor<A>.RefreshWindow) can't have @_cdecl wrappers
        // because "extension Outer.Inner: Protocol {}" won't compile.
        if (kind is MemberKind.Method or MemberKind.Property or MemberKind.Constructor)
        {
            if (env.ParentDecl is TypeDecl td && td.IsGeneric && IsInheritedGenericContext(td))
                return "inherited_generic_context";
        }

        return null;
    }

    /// <summary>
    /// True when the member's PARENT type is module-internal (`@usableFromInline internal`,
    /// or truly internal but ABI-visible — <see cref="TypeDecl.IsModuleInternal"/>). A
    /// @_cdecl wrapper for such a member must name the parent type by its module-qualified
    /// name to reconstruct `self`, which the separate wrapper-compilation module cannot do
    /// → swiftc "no type named X in module Y". This is the single source of truth for the
    /// parent-internal eligibility decision so the rejection site cannot drift. Returns
    /// false for free functions (their <see cref="MethodEnvironment.ParentDecl"/> is a
    /// <c>ModuleDecl</c>, not a <see cref="TypeDecl"/>).
    ///
    /// <para>Deliberately the IMMEDIATE parent only, unlike the type-keyed
    /// <see cref="IsTypeOrEnclosingModuleInternal"/>. Walking enclosing types here would
    /// withdraw working wrappers: a type declared in an extension of a FOREIGN module's
    /// type has that foreign type as its enclosing decl, and the foreign type carries
    /// <see cref="TypeDecl.IsModuleInternal"/> merely because it is absent from THIS
    /// module's public-type names — it is public where it is declared and perfectly
    /// spellable from wrapper source through the import. Treating that as unspellable
    /// retargets those members off wrappers that compile today. Closing the genuine
    /// same-module nested case therefore needs a spellability fact that separates the two,
    /// not a broader walk over this flag.</para>
    /// </summary>
    public static bool IsParentTypeModuleInternal(MethodEnvironment env)
        => (env.ParentDecl as TypeDecl)?.IsModuleInternal == true;

    /// <summary>
    /// Fails closed when an emission path is about to claim an <c>SBW_</c> wrapper symbol while the
    /// Swift plane that would define it is being discarded (see <see cref="SwiftWriter.IsDiscarding"/>).
    ///
    /// <para>
    /// This is the structural half of the plan/emit contract. The predicate half —
    /// <see cref="IsTypeOrEnclosingModuleInternal"/> — is what a planner is supposed to consult
    /// BEFORE deciding to emit a member; this is what catches a planner that did not. Because a
    /// discard writer is non-null and accepts every write, a planner that skips the predicate
    /// produces perfectly plausible-looking output: C# externs for wrapper functions that were
    /// written into a buffer nobody reads. The failure only surfaces at the end-of-generation
    /// integrity gate as a dangling entry point, attributed to the module rather than to the site
    /// that planned it. Throwing here attributes it to the site.
    /// </para>
    /// </summary>
    /// <param name="swiftWriter">The Swift plane the caller would emit the wrapper into.</param>
    /// <param name="claimant">
    /// What is claiming the symbol (an emitter/surface name plus the owning type), used verbatim in
    /// the exception message so the violating site is identifiable from the log alone.
    /// </param>
    public static void RequireLiveWrapperPlane(SwiftWriter swiftWriter, string claimant)
    {
        ArgumentNullException.ThrowIfNull(swiftWriter);
        if (!swiftWriter.IsDiscarding)
            return;

        throw new InvalidOperationException(
            $"{claimant} tried to claim a wrapper symbol on a discarded Swift plane. The wrapper source "
            + "is thrown away for this type, so the C# P/Invoke would reference a symbol nothing defines "
            + "(EntryPointNotFoundException at runtime). The planner must decline the member up front — "
            + $"consult {nameof(IsTypeOrEnclosingModuleInternal)} — instead of emitting against it.");
    }

    /// <summary>
    /// True when <paramref name="decl"/> — or ANY type enclosing it — is module-internal, so a
    /// separate wrapper-compilation module cannot name it by its module-qualified path.
    ///
    /// <para>
    /// This is the single decision record for "can wrapper source spell this type?". A @_cdecl
    /// wrapper body always names the type through its FULL qualified path
    /// (<c>Module.Outer.Inner.case</c>), so one internal link anywhere in the chain makes the
    /// whole path unspellable — swiftc rejects the wrapper with "no type named X in module Y".
    /// Checking only the type's own flag misses the nested shape (a public enum inside an
    /// internal parent), where the wrapper is emitted, fails to compile, is stripped by the
    /// post-processor, and leaves the C# P/Invoke that was planned against it pointing at a
    /// symbol nothing defines.
    /// </para>
    ///
    /// <para>
    /// Every plane that decides whether wrapper source exists for a type must consult THIS, so
    /// the side that plans C# P/Invokes and the side that emits Swift symbols cannot drift into
    /// two predicates that merely happen to agree.
    /// </para>
    /// </summary>
    public static bool IsTypeOrEnclosingModuleInternal(TypeDecl? decl)
    {
        for (var current = decl; current is not null; current = current.ParentDecl as TypeDecl)
        {
            if (current.IsModuleInternal)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true when the generator is running in xcframework mode, where the wrapper
    /// library exists. This is a prerequisite for all @_cdecl wrapper emission. This is the
    /// single chokepoint for the mode decision — it consults the explicit
    /// <see cref="GenerationMode"/> rather than re-deriving the <c>AsyncLibraryName</c> sentinel.
    /// </summary>
    public static bool IsXCFrameworkMode(ITypeDatabase db)
    {
        return db.GenerationMode == GenerationMode.XCFramework;
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
    /// Note: Type-level custom global actor isolation (e.g., <c>@CustomActor class X</c>)
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
                // Non-throwing baseline: the adapter uses
                // `await` (no try). The outer method must still be async, but
                // it does NOT need to be throwing.
                if (env.ClosureHandler.IsBaselineAsyncNonThrowingClosure(spec))
                    return !env.MethodDecl.IsAsync;
                return true;
            });
    }

    /// <summary>
    /// Returns true when a method declares an async-throwing closure parameter
    /// that cannot be bridged by the async baseline adapter. The P/Invoke
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
        => HasUnbridgeableAsyncThrowingClosure(env, env.MethodDecl.UsesCdeclMethodWrapper);

    /// <summary>
    /// The same verdict as <see cref="HasUnbridgeableAsyncThrowingClosure(MethodEnvironment)"/>, but
    /// asked BEFORE emission has decided anything — for callers that must agree with what emission
    /// will do rather than with what it has already done.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plain overload reads <see cref="MethodDecl.UsesCdeclMethodWrapper"/>, which emission sets
    /// inside <c>MethodHandler</c>. Every EMISSION caller runs after that point and reads a settled
    /// flag — correct there, and the reason this must not be folded into the plain overload. Callers
    /// that run EARLIER read a flag that is still false on a method emission will promote, and so
    /// conclude "unbridgeable" for a member that in fact binds cleanly. On the conformance path that
    /// verdict drops the whole interface: the type loses a conformance it satisfies.
    /// </para>
    /// <para>
    /// Only the promotion is predicted. The outer method's own async/throws facts and the closure's
    /// baseline shape are parser facts that emission never changes, so a SYNC member carrying an
    /// async closure stays unbridgeable here exactly as it does at emission — the prediction can only
    /// rescue a member that will genuinely get its adapter.
    /// </para>
    /// </remarks>
    public static bool HasUnbridgeableAsyncThrowingClosureBeforeEmission(MethodEnvironment env)
        => HasUnbridgeableAsyncThrowingClosure(env, WillPromoteToCdeclMethodWrapper(env));

    /// <summary>
    /// Predicts whether emission will promote this method to a <c>@_cdecl</c> method wrapper —
    /// a mirror of the promotion branch in <c>MethodHandler</c>'s method-emission path, for
    /// pre-emission callers that must reach the same verdict emission will.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An already-set flag is honoured first (a caller running after promotion gets the settled
    /// answer). Otherwise the branch's own preconditions are re-checked in the same order emission
    /// applies them: a constructor or an already-wrapped property/constructor never takes this
    /// branch, and a native thunk — which emission prefers and tries first — takes the method off
    /// the <c>@_cdecl</c> path entirely.
    /// </para>
    /// <para>
    /// Async methods promote on a DIFFERENT branch. The synchronous decision path rejects every
    /// async member outright (the shared member gate treats <c>isAsync</c> as a rejection), so
    /// asking it about an async method always answers "no wrapper" — which is exactly the wrong
    /// answer for the async-closure callers this predicate serves. Async promotion is decided by
    /// <see cref="IsAsyncCdeclEligible"/>, the shared predicate emission itself calls.
    /// </para>
    /// </remarks>
    public static bool WillPromoteToCdeclMethodWrapper(MethodEnvironment env)
    {
        var decl = env.MethodDecl;
        if (decl.UsesCdeclMethodWrapper)
            return true;

        if (decl.IsConstructor ||
            decl.UsesCdeclPropertyWrapper ||
            decl.UsesCdeclConstructorWrapper ||
            decl.UsesNativeThunk)
            return false;

        if (decl.IsAsync)
            return IsAsyncCdeclEligible(env);

        if (NativeThunkEmitter.ShouldEmitThunk(env))
            return false;

        return DetermineMethodWrapperDecision(env) == WrapperDecision.WrapperRequired;
    }

    /// <summary>
    /// Whether an async method is eligible for <c>@_cdecl</c> async-wrapper promotion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the ONE definition of async <c>@_cdecl</c> eligibility: <c>MethodHandler</c>'s
    /// promotion branch calls it to decide, and <see cref="WillPromoteToCdeclMethodWrapper"/> calls
    /// it to predict, so the decision and its prediction cannot drift. The synchronous wrapper
    /// decision can't be reused here — its shared member gate rejects async members, which is why
    /// this eligibility check exists at all and why it is built on
    /// <see cref="HasCdeclCompatibleFunctionShape"/> instead.
    /// </para>
    /// <para>
    /// The <c>UsesWrapperLibrary</c> conjunct is where the two phases can disagree, because emission
    /// runs several wrapper installs BEFORE reaching the async branch and each one sets that flag.
    /// Enumerated, in emission order, with why each is or isn't predictable here:
    /// </para>
    /// <list type="number">
    /// <item>the debug-default-parameter Swift wrapper — the ONE live divergence for an async
    /// method: no async guard, so it fires and then makes this branch decline. Predicted via the
    /// shared <c>WillInstallDebugParamWrapper</c>.</item>
    /// <item>the constructor native thunk, and</item>
    /// <item>the <c>@_cdecl</c> constructor wrapper — both constructor-only, and
    /// <see cref="WillPromoteToCdeclMethodWrapper"/> rejects constructors before reaching
    /// here.</item>
    /// <item>the method native thunk — <c>NativeThunkEmitter.ShouldEmitThunk</c> returns false for
    /// any async method (async uses swifttailcc), so it can never fire first.</item>
    /// <item>the synchronous <c>@_cdecl</c> method wrapper — gated on the wrapper decision, whose
    /// shared member gate rejects async.</item>
    /// <item>the closure <c>@_cdecl</c> wrapper — <c>NeedsClosureCdeclWrapper</c> returns false for
    /// any async method.</item>
    /// <item>the optional-pointer wrapper — explicitly conditioned on <c>!IsAsync</c>.</item>
    /// </list>
    /// <para>
    /// Items 2–7 are inert for the async shapes this predicate serves, so only item 1 needs
    /// mirroring. A method already carrying <c>UsesWrapperLibrary</c> on entry (a bridge-normalized
    /// clone, an <c>ArraySlice</c> normalization, a parser-level wrapper claim) is declined by the
    /// raw flag read, which is correct in both phases.
    /// </para>
    /// </remarks>
    public static bool IsAsyncCdeclEligible(MethodEnvironment env)
    {
        var decl = env.MethodDecl;
        if (!decl.IsAsync || decl.UsesWrapperLibrary)
            return false;
        if (DefaultParameterOverloadEmitter.WillInstallDebugParamWrapper(decl))
            return false;
        if (!HasCdeclCompatibleFunctionShape(env))
            return false;

        return decl.CSSignature.Skip(1).All(p =>
        {
            if (p.IsGeneric) return false;
            // Metatype check runs BEFORE the closure / large-optional / nested-struct
            // bypasses below: AnyClass.Type? would otherwise be widened to UnsafeRawPointer
            // by the async wrapper and the body would still try to render the bare metatype.
            if (IsMetatypeTypeIncludingOptional(p.SwiftTypeSpec)) return false;
            if (p.SwiftTypeSpec is ClosureTypeSpec closureSpec)
            {
                // Baseline async closures (throwing + non-throwing) are bridged
                // by the async wrapper via a CheckedContinuation. The throwing
                // baseline requires the outer method to also be `throws` (adapter
                // uses `try await` inside the catch harness). The non-throwing
                // baseline only requires the outer method to be async — the
                // adapter uses plain `await`.
                if (env.ClosureHandler.IsBaselineAsyncThrowingClosure(closureSpec))
                    return decl.Throws;
                if (env.ClosureHandler.IsBaselineAsyncNonThrowingClosure(closureSpec))
                    return true;
                return false;
            }
            if (IsNestedFrozenStructParam(p, env.TypeDatabase)) return false;
            // Frozen blittable struct params are supported in async via heap allocation
            // (NativeMemory.Alloc instead of stackalloc). See WrapperEmitter.Async.cs.
            // Protocol existentials are marshalled as UnsafeRawPointer to the
            // ExistentialContainer1 heap allocation — see CdeclParamMapper.
            return true;
        });
    }

    private static bool HasUnbridgeableAsyncThrowingClosure(MethodEnvironment env, bool usesCdeclMethodWrapper)
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
                return !(usesCdeclMethodWrapper &&
                         env.MethodDecl.IsAsync &&
                         env.MethodDecl.Throws &&
                         env.ClosureHandler.IsBaselineAsyncThrowingClosure(spec));
            }
            if (env.ClosureHandler.IsAsyncClosure(spec)
                && env.ClosureHandler.IsBaselineAsyncNonThrowingClosure(spec))
            {
                // Non-throwing baseline: outer method still has to be async +
                // Cdecl-wrapped, but Throws is NOT required.
                return !(usesCdeclMethodWrapper &&
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
    /// Returns true when <paramref name="memberGenericParams"/> place a generic-parameter
    /// constraint on a PARENT-declared generic parameter that the parent type itself does
    /// not declare — the constrained-extension shape (e.g.
    /// <c>extension Box where Base : UIView</c> or the same-type <c>where Base == Foo</c>)
    /// where the constraint lives on the member's own generic signature, not the parent's.
    /// <para>
    /// The generated wrapper's conformance/dispatch extension is emitted unconditionally
    /// (no where-clause), so any extra constraint on a parent-declared generic parameter is
    /// invisible at the call site and swiftc rejects the unconditional conformance because
    /// the member is only available under the extension's where-clause. Both the
    /// generic-static-dispatch method path and the instance-class-dispatch property path
    /// share this failure mode, so both share this predicate.
    /// </para>
    /// <para>
    /// Member-local generic parameters (introduced by the member signature rather than
    /// inherited from the parent) are filtered out by the parent-name membership test:
    /// their constraints are scoped to the member and do not require propagation onto the
    /// conformance extension.
    /// </para>
    /// </summary>
    public static bool GenericParamsNarrowParentConstraints(
        IEnumerable<GenericArgumentDecl> memberGenericParams, TypeDecl parentTypeDecl)
    {
        if (!parentTypeDecl.IsGeneric) return false;

        // Parent's set of generic-parameter names and the conformances it already
        // declares on them. A member constraint keyed to a parent param that is NOT in
        // this conformance set narrows the parent.
        var parentParamNames = new HashSet<string>(StringComparer.Ordinal);
        var parentConformances = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gp in parentTypeDecl.GenericParameters)
        {
            parentParamNames.Add(gp.TypeName);
            foreach (var c in gp.GenericConformances)
                parentConformances.Add(BuildNarrowingConformanceKey(c));
            foreach (var c in gp.AssosiatedTypeConformances)
                parentConformances.Add(BuildNarrowingConformanceKey(c));
        }

        foreach (var gp in memberGenericParams)
        {
            // Only constraints on parent-declared generic params can narrow the
            // conformance extension — member-local generics are scoped to the member.
            if (!parentParamNames.Contains(gp.TypeName)) continue;
            foreach (var c in gp.GenericConformances)
            {
                if (!parentConformances.Contains(BuildNarrowingConformanceKey(c)))
                    return true;
            }
            foreach (var c in gp.AssosiatedTypeConformances)
            {
                if (!parentConformances.Contains(BuildNarrowingConformanceKey(c)))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Builds a path + target + kind key for narrowing-conformance comparison. Path
    /// captures both direct (<c>τ_0_0</c>) and associated-type (<c>τ_0_0.Element</c>)
    /// constraints, ConformanceTarget distinguishes different protocols/types on the
    /// same parameter, and Kind separates protocol conformance from same-type constraints
    /// (<c>where N == Foo</c>).
    /// </summary>
    public static string BuildNarrowingConformanceKey(GenericParameterConformance c)
        => $"{string.Join(".", c.Path)}|{c.ConformanceTarget.ModuleQualifiedName}|{c.Kind}";

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
    /// - Optional&lt;Any&gt;: buffer-pointer ABI (UnsafeRawPointer to a SwiftOptional&lt;ExistentialContainer0&gt;
    ///   payload, loaded as Optional&lt;Any&gt; in the wrapper)
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
                // Exception: Optional<Any> (empty protocol list) — passed by buffer pointer.
                // C# emits SwiftOptional<ExistentialContainer0> matching Swift's 32-byte
                // Optional<Any> layout (4-word EC; nil via null-metadata extra-inhabitant).
                // CdeclParamMapper loads as Optional<Any> directly.
                if (optSpec.GenericParameters[0] is ProtocolListTypeSpec { Protocols.Count: 0 })
                    return true;
                // Exception: Optional<Self> — Self resolves to the concrete class type at call site.
                // Used for ObjC-bridged protocol methods like decodedObject(fromAPIResponse:) -> Self?.
                // The DynamicSelf fast path in TryGetTypeRecord returns AnyType{Kind=Protocol},
                // which incorrectly flags Optional<Self> as Optional<existential>. Allow it here.
                if (optSpec.GenericParameters[0].IsDynamicSelf)
                    return true;
                // Exception: Optional<generic-param> on generic-extension static dispatch
                // (e.g., `GenericType<N>.method(...) -> N?` shape). The ABI looks up τ_0_X
                // and returns a Protocol-kind TypeRecord placeholder, which makes
                // IsProtocolExistentialType report true. The protocol-based static dispatch
                // path handles the wrapping correctly via RenderSwiftTypeSpecWithSugaredNames
                // + initializeMemory(as: Optional<N>.self).
                if (optSpec.GenericParameters[0] is NamedTypeSpec genericInner
                    && TypeSpecHelpers.IsGenericTypeParameter(genericInner.Name))
                    return true;
            }
            return false;
        }
        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="typeSpec"/> is an Optional whose payload is a single
    /// class-bound existential (one <c>AnyObject</c>- or superclass-constrained protocol). Such a
    /// value is the compact 2-word <c>[classRef][witnessTable]</c> cell, which Swift returns in
    /// registers (x0:x1), not via an indirect-result pointer in x8. The raw method-dispatch-thunk
    /// fallback assumes an x8 sret contract, but the <c>...Tj</c> dispatch thunk clobbers x8 to load
    /// the vtable slot before tailing to the implementation — so the sret buffer is never written and
    /// C# reads nil even for a present value. Routing the RETURN through an @_cdecl wrapper makes
    /// Swift capture the register-returned value and write it into a stable result buffer that C#
    /// reads via <c>ClassExistentialContainer1.ReadHeapCell</c> (offset-0 null-niche check in
    /// <see cref="OptionalProjection"/>).
    ///
    /// Return-position only: the parameter path keeps rejecting <c>Optional&lt;existential&gt;</c>
    /// (its reconstruction has separate pointer/handle assumptions). Class-bound only: the 5-word
    /// opaque <c>Optional&lt;any P&gt;</c> is genuinely sret-returned and its dispatch thunk preserves
    /// x8, so it already works on the raw path and must stay there.
    /// </summary>
    public static bool IsOptionalClassBoundExistentialReturn(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is not NamedTypeSpec { Name: "Swift.Optional", GenericParameters.Count: 1 } opt)
            return false;
        if (!CdeclParamMapper.IsProtocolExistentialType(opt.GenericParameters[0], typeDatabase))
            return false;
        var handler = new ExistentialHandler(typeDatabase);
        var protocolList = handler.ToProtocolListTypeSpec(opt.GenericParameters[0]);
        return protocolList != null && handler.IsClassBoundArity1Existential(protocolList);
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
    /// ClosedRange&lt;Bound: Comparable&gt; rides the same UnsafeRawPointer transport — it's an
    /// in-place frozen struct (no out-of-line storage) whose bytes can be moved across the
    /// boundary identically to the other collections.
    /// </summary>
    public static bool IsSupportedCollectionType(TypeSpec typeSpec)
    {
        return typeSpec is NamedTypeSpec named &&
            named.Name is "Swift.Array" or "Swift.Dictionary" or "Swift.Set" or "Swift.ClosedRange";
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
            // SIMD vectors are the exception inside the "system frozen" set: the input-side
            // ABI mismatch (Swift NEON vector register vs .NET HFA across s0,s1,s2,…) drops
            // every lane past the first. Treat them as non-primitive so PInvokeEmitter wires
            // up the CdeclFrozenStruct (stackalloc + IntPtr) marshalling that preserves all
            // lanes — keeping CdeclParamMapper.Map's SIMD wedge in lockstep on the Swift side.
            if (spec is NamedTypeSpec namedSpec && CdeclParamMapper.IsSystemFrozenStruct(namedSpec)
                && !CdeclParamMapper.IsSimdVectorType(namedSpec))
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
        if (!IsXCFrameworkMode(env.TypeDatabase))
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
        // Guard 10: Variadic parameters — supported via the unsafeBitCast bridge when the
        // shape is simple (see MethodWrapperEmitter.IsSupportedVariadicShape). Otherwise the
        // wrapper would pass [T] where T... is expected, causing a Swift compile error.
        if (env.MethodDecl.HasVariadicParameter && !MethodWrapperEmitter.IsSupportedVariadicShape(env))
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
        => HasInoutWithAbiMismatch(env.MethodDecl, env.TypeDatabase);

    /// <inheritdoc cref="HasInoutWithAbiMismatch(MethodEnvironment)"/>
    /// <remarks>Decl+database overload for callers (e.g. the member-validation pipeline) that
    /// have no <see cref="MethodEnvironment"/> yet.</remarks>
    public static bool HasInoutWithAbiMismatch(MethodDecl methodDecl, ITypeDatabase typeDatabase)
    {
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (!arg.IsInOut) continue;
            if (arg.SwiftTypeSpec is NamedTypeSpec inoutNamed && inoutNamed.Name == "Swift.String")
                return true;
            if (typeDatabase.TryGetTypeRecord(arg.SwiftTypeSpec, out var inoutTypeRec))
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
            else
            {
                // No TypeRecord — commonly a foreign ObjC/Foundation type never registered in the
                // TypeDatabase (e.g. NSMutableAttributedString). Its layout can't be verified for a
                // safe UnsafeMutableRawPointer round-trip, and every fallback path is wrong for it:
                // the raw CallConvSwift P/Invoke (PInvokeEmitter's ObjC-bridged / enum arms) drops
                // the inout modifier, and a MethodWrapper's own P/Invoke emission calls
                // GetTypeRecordOrThrow and throws. Treat it as a confirmed mismatch so the caller
                // declines the wrapper (and the MemberValidationPipeline inout+large-Optional gate
                // skips the member cleanly) rather than emitting a wrapper that mis-forwards inout.
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
    /// Diagnostic shim: returns the name of the first guard that rejects the method for @_cdecl
    /// wrapping, or null if the method would be wrapped. For logging/the emission report only —
    /// never feeds generated C#. Delegates to the single eligibility traversal so the reason can
    /// never disagree with <see cref="MethodWrapperEmitter.ShouldEmitWrapper"/> (Finding 12).
    /// </summary>
    public static string? GetRejectionReason(MethodEnvironment env)
        => MethodWrapperEmitter.EvaluateWrapperEligibility(env).Reason;

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
        //    causing the same Mono JIT crash. A non-frozen struct constructor with only
        //    primitive params (CallConvSwift + SwiftIndirectResult) still crashes.
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
        //   Mono JIT can't handle CallConvSwift + SwiftSelf → jit-info.c:918 assertion.
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
    /// <para><b>Handled elsewhere, not by this predicate:</b> async methods on generic
    /// class parents (<c>AsyncGenericContainer&lt;T&gt;.processAsync</c>, <c>fetchOrThrow</c>)
    /// are NOT serviced by this gate — they reach it with <see cref="MethodDecl.UsesWrapperLibrary"/>
    /// set (a <c>@_silgen_name</c> async wrapper is emitted), so the wrapper-library early-return
    /// above lets them past. They are an <b>ABI mismatch, not a working path</b>: the wrapper is
    /// itself a generic instance method, so Swift passes <c>self</c> + the parent's type metadata
    /// through the implicit self / metadata registers while the C# CallConvSwift P/Invoke hands
    /// them over as trailing <c>IntPtr</c> args (TMetadata, _selfClass) — the registers hold
    /// garbage and the call SIGSEGVs. They are suppressed at their source in
    /// <see cref="MemberValidationPipeline"/> (the parent-generic async gate next to the
    /// CSM routing) so no live wrong-ABI method ships. Sync methods through the legacy path are
    /// out of scope here — they go through SB0001 / <see cref="HasNonBlittablePInvokeTypes"/>.</para>
    ///
    /// <para><b>Out of scope:</b> the wrapper-library "symbol missing in wrapper dylib" case
    /// The wrapper-library "symbol missing in wrapper dylib" case for MusicKit
    /// <c>MusicPlayer.Queue.insert&lt;S: Sequence&gt;</c> cannot be detected from method
    /// flags alone: many legitimate paths (ArraySlice, default-parameter, metatype-array,
    /// protocol-extension wrappers) set <see cref="MethodDecl.UsesWrapperLibrary"/> without
    /// a <c>@_cdecl</c> flag because they emit <c>@_silgen_name</c> wrappers whose symbols
    /// ARE present in the wrapper dylib. Distinguishing real misses requires a wrapper-export
    /// cross-reference (not yet implemented).</para>
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
        // / HasNonBlittablePInvokeTypes — marked with [Obsolete] and still emitted, except for
        // the module-internal subset whose body becomes a throwing tombstone. Either way the
        // declaration survives. The trigger here is async-specific because the Swift async ABI is the
        // boundary that's under-specified for direct P/Invoke.
        if (!env.MethodDecl.IsAsync)
            return false;

        // Method-level generics on async: `some Protocol` and explicit `<T>` parameters
        // require per-call metatype passing the legacy async path can't synthesise. Use
        // HasMethodOwnGenericParameters to scope this to METHOD-OWN generics (the parser folds
        // the parent's generic signature into MethodDecl.GenericParameters, so MethodDecl.IsGeneric
        // is true even for a method with no own generic). The bare parent-generic-only async shape
        // (e.g. `Container<T>.processAsync()`) is NOT this predicate's responsibility: it is
        // suppressed upstream by the parent-generic async gate in MemberValidationPipeline.ValidateMethodEmission
        // (or specialized via CSM routing) before emission ever reaches the direct path — so
        // returning false for it here is correct and does NOT mean it "flows through the legacy path".
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
    /// True when a member reaches the direct-CallConvSwift path carrying <b>no mitigation</b>:
    /// no @_cdecl wrapper, no native thunk and no @_silgen_name free-function wrapper, while
    /// <see cref="HasNonBlittablePInvokeTypes(MethodEnvironment)"/> predicts a non-blittable
    /// signature from the Swift declaration.
    ///
    /// <para>This is the <b>advisory</b> condition — it drives the SB0001 marker, not a refusal
    /// to emit. The prediction runs on the Swift type spec, before projection, so it fires on
    /// shapes the emitter then passes <i>indirectly</i> and which call correctly: an open generic
    /// (<c>identity&lt;T&gt;(_: T) -&gt; T</c>) predicts "generic container in the signature" yet
    /// lowers to a fully blittable <c>(SwiftIndirectResult, IntPtr payload, IntPtr metadata)</c>
    /// extern that round-trips its value. Treating this predicate as fatal would therefore
    /// destroy working surface; the fatal subset is
    /// <see cref="IsUncallableInternalDirectDispatch"/>.</para>
    ///
    /// <para>Accessors are excluded because the property/subscript accessor path never reaches
    /// the marker.</para>
    /// </summary>
    internal static bool HasUnmitigatedNonBlittableCallConvSwift(MethodEnvironment env) =>
        !env.MethodDecl.IsAccessor
        && !env.MethodDecl.UsesCdeclWrapper
        && !env.MethodDecl.UsesNativeThunk
        && !env.MethodDecl.UsesFreeFunctionWrapper
        && HasNonBlittablePInvokeTypes(env);

    /// <summary>
    /// The subset of <see cref="HasUnmitigatedNonBlittableCallConvSwift"/> that has <b>no call
    /// route at all</b>: a @_cdecl wrapper is not merely undesirable but impossible, and the
    /// direct-CallConvSwift fallback it is left with carries a signature the Swift calling
    /// convention cannot deliver.
    ///
    /// <para>Impossibility is decided on what the wrapper would have to spell, since the wrapper
    /// compiles as a separate client module and can only name what that module can see. Two
    /// independent ways to be unspellable, and either is sufficient: the member is itself
    /// module-internal (its own symbol cannot be named), or its parent type is, so the wrapper
    /// body cannot reconstruct <c>self</c> through a module-qualified path. Both are the same
    /// proof, differing only in which name is missing, so both belong to the same floor; gating
    /// on the member's own flag alone would leave a public member on a
    /// <c>@usableFromInline internal</c> parent emitting a live call that faults.</para>
    ///
    /// <para>The receiver half deliberately shares
    /// <see cref="IsParentTypeModuleInternal"/> with the wrapper-eligibility gate rather than
    /// carrying its own, broader notion of unspellability. The two must agree by construction:
    /// this floor may only fire where that gate has already refused the wrapper, so a member it
    /// tombstones is one the emitter has itself established cannot be wrapped — never one whose
    /// wrapper merely failed to materialise for some other reason.</para>
    ///
    /// <para>A member with a fully blittable signature is NOT in this set however unspellable it
    /// is: those call correctly through the direct path today (a plain <c>Bool</c>/<c>Int</c>
    /// setter on an internal-but-exported symbol round-trips). It is the combination — no wrapper
    /// possible AND a signature carrying an existential / SafeHandle / multi-register value —
    /// that leaves the emitted call with nothing sound to bind to, and invoking one faults the
    /// process rather than failing cleanly.</para>
    ///
    /// <para>Members in this set are emitted as throwing tombstones: the declaration and its
    /// attributes stay so a conformance requiring the member still compiles (dropping it would
    /// be CS0535) and the pinned public surface does not silently shrink, but the body throws
    /// instead of making a call that cannot work.</para>
    /// </summary>
    internal static bool IsUncallableInternalDirectDispatch(MethodEnvironment env) =>
        (env.MethodDecl.IsModuleInternal || IsParentTypeModuleInternal(env))
        && HasUnmitigatedNonBlittableCallConvSwift(env);

    /// <summary>
    /// True when the member reaches the direct-CallConvSwift path carrying an
    /// <c>Optional&lt;T&gt;</c> that is wider than the single pointer-sized slot that path gives
    /// it — a value the emitted call physically cannot transfer intact.
    ///
    /// <para>This is a <b>second, independent</b> floor alongside
    /// <see cref="IsUncallableInternalDirectDispatch"/>, and the two overlap only by coincidence.
    /// That one asks whether a call route exists at all and answers from visibility; this one
    /// assumes the route exists and asks whether the bytes fit. Neither subsumes the other: the
    /// blittability predicate behind the first is <b>width-blind</b> — the truncating fallback
    /// declares its return as <c>IntPtr</c>, which is perfectly .NET-blittable, so a member can be
    /// public, wrappable in principle, and still silently lose half its return value.</para>
    ///
    /// <para>What goes wrong without this floor: the emitter's preferred route for a wide Optional
    /// is a Swift wrapper with an out-buffer parameter, but that route is conditional on the member
    /// being wrapper-eligible. When it is not — an internal parent, a generic parent, a
    /// generic or opaque return, a DynamicSelf return — the emitter falls back to a direct P/Invoke
    /// that declares the Optional as one <c>IntPtr</c>, then hands the address of that
    /// pointer-sized local to a marshaller which copies the type metadata's full
    /// <c>Size</c> out of it. For a 16-byte <c>Optional&lt;String&gt;</c> that reads 8 real bytes
    /// and 8 bytes of whatever else was on the stack. It is not a corrupt payload with an intact
    /// nil flag either: an Optional whose payload has spare bits carries no separate tag byte, so
    /// the value witness derives Some-vs-None from the full width — and the discriminating bits
    /// are exactly the ones that were never transferred. The observable result is a nil that
    /// decodes as a non-nil garbage value, or differently on each runtime, with no diagnostic.</para>
    ///
    /// <para>Deliberately <b>not</b> keyed on <c>IsLargeOptionalParam</c> alone. That predicate is
    /// a routing preference and calls several genuinely one-word Optionals "large" — notably
    /// <c>Optional&lt;Array&gt;</c>, <c>Optional&lt;Dictionary&gt;</c> and
    /// <c>Optional&lt;Set&gt;</c>, each a single refcounted pointer using null as its extra
    /// inhabitant. Those members bind correctly on the direct path today, so refusing them would
    /// destroy working surface to fix a bug they do not have. The width question is therefore
    /// asked separately, of <see cref="DirectOptionalAbi"/>, which answers only from lowerings it
    /// can positively establish.</para>
    ///
    /// <para>Accessors are deliberately <b>in</b> scope, unlike
    /// <see cref="HasUnmitigatedNonBlittableCallConvSwift"/>, which excludes them because the
    /// advisory marker it drives is never rendered on the accessor path. That exclusion is about
    /// where a marker can be printed; this is about whether a call can be correct. A
    /// <c>public var name: String?</c> getter on a wrapper-ineligible parent truncates exactly
    /// like the equivalent method, so excluding accessors here would leave the defect reachable
    /// through the most ordinary shape a Swift API has.</para>
    ///
    /// <para>Like the sibling floor, members in this set are emitted as throwing tombstones rather
    /// than dropped: the declaration and its attributes stay, so conformances still compile and
    /// the pinned public surface does not silently shrink, but the body throws instead of making a
    /// call that returns a value derived from uninitialized memory. Refusing is the conservative
    /// direction — a member that throws is a bug report, whereas a member that answers with stack
    /// garbage is a data-corruption incident that reaches production looking like it worked.</para>
    /// </summary>
    internal static bool HasTruncatedLargeOptionalDirectDispatch(MethodEnvironment env)
    {
        // Any Swift-side carrier — an @_cdecl wrapper, a native thunk, a @_silgen_name free
        // function, or the Optional-pointer out-buffer wrapper built for exactly this problem —
        // moves the value through memory rather than a register slot, so width stops mattering.
        if (env.MethodDecl.UsesCdeclWrapper
            || env.MethodDecl.UsesNativeThunk
            || env.MethodDecl.UsesFreeFunctionWrapper
            || env.MethodDecl.UsesWrapperLibrary
            || env.MethodDecl.HasOptionalPointerWrapper)
            return false;

        // An async member delivers its result through a completion-handler buffer, never through
        // the synchronous return slot this floor is about.
        if (env.MethodDecl.IsAsync)
            return false;

        var signature = env.MethodDecl.CSSignature.ToList();
        if (signature.Count == 0)
            return false;

        // The classifier is asked about every Optional on this member, NOT only the ones the
        // large-Optional routing predicates flag. Gating on those predicates leaves a hole
        // exactly where they answer "not large": they early-out on IsOptionalWithReferenceInner,
        // which is a question about *bridging* — true for ObjC-bridgeable Swift value types like
        // Foundation.URL and Date. There is no bridging on this path, so those keep their native
        // layout (URL? measures 16 bytes, Date? 9, IndexPath? 17) and the emitted call reads a
        // single word and hands it to GetINativeObject as though it were an object pointer,
        // releasing a value that was never a reference. That is the same defect as the String?
        // truncation with an added bogus release, so the floor has to reach it too.
        //
        // Return slot. Skipped when the result is passed indirectly, since an sret buffer is as
        // wide as the value is.
        if (!IsClosurePayloadOptional(signature[0].SwiftTypeSpec)
            && !MarshallingHelpers.MethodRequiresIndirectResult(env)
            && DirectOptionalAbi.ExceedsDirectSlot(signature[0].SwiftTypeSpec, env.TypeDatabase))
            return true;

        // Parameter slots. Same reasoning in the other direction: Swift reads a wider-than-a-word
        // Optional argument out of more than one register, and supplying only one slot leaves the
        // callee's own nil check reading whatever the rest happened to hold. Protocol existentials
        // are covered here too — an existential container is five words wide, nowhere near a slot.
        //
        // Measured on the iOS Simulator against this exact emission: with this arm disabled, a
        // String? argument SIGSEGVs the process on the first call, while a single-word [String]?
        // argument returns correct answers for both Some and None. So the width split is the right
        // one on the parameter side as well, and it is load-bearing rather than precautionary.
        var visibleGenericNames = BaseHandler.CollectVisibleGenericParamNames(env.MethodDecl);
        foreach (var arg in signature.Skip(1))
        {
            if (!IsClosurePayloadOptional(arg.SwiftTypeSpec)
                && !IsGenericPayloadOptional(arg.SwiftTypeSpec, visibleGenericNames)
                && DirectOptionalAbi.ExceedsDirectSlot(arg.SwiftTypeSpec, env.TypeDatabase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the Optional's payload is a function value. These are outside this floor's
    /// jurisdiction, and deliberately so.
    ///
    /// <para>Width alone would not decide them correctly: a <c>@convention(c)</c> function value
    /// is a bare C function pointer and its Optional is one word (measured 8 bytes), while a
    /// Swift closure carries a context alongside the pointer and its Optional is two (measured
    /// 16). The distinction is not decidable from the type spec on its own — closures parsed
    /// from ABI JSON carry no convention attribute, which is why ClosureHandler.IsConventionC
    /// has a second overload that consults the method's mangled name. Refusing every
    /// Optional-closure here would therefore tombstone working <c>@convention(c)</c> members on
    /// the strength of a missing attribute.</para>
    ///
    /// <para>They also do not need this floor: Optional closures have their own marshalling
    /// path, which passes the function pointer and its context explicitly rather than reading a
    /// value out of a single return slot. This floor exists for the value shapes nothing else
    /// models.</para>
    /// </summary>
    private static bool IsClosurePayloadOptional(TypeSpec typeSpec)
        => IsOptionalType(typeSpec)
           && typeSpec is NamedTypeSpec named
           && named.GenericParameters.Count == 1
           && named.GenericParameters[0] is ClosureTypeSpec;

    /// <summary>
    /// True when the Optional's payload is one of the generic parameters visible at this member —
    /// the parent type's or the method's own. Like the closure case this is a question of
    /// jurisdiction rather than width, but for the opposite reason: the value is not carried in
    /// the argument slot at all.
    ///
    /// <para>The caller has no static size for a generic payload, so Swift takes such an argument
    /// indirectly and the emitter matches it — <c>WrapperEmitter</c>'s marshalling passes the
    /// buffer ADDRESS for an Optional whose inner projection is a generic parameter, rather than
    /// a value word. A pointer is a carrier for the whole value however wide the value turns out
    /// to be, so there is nothing here to truncate and the floor must not fire. Confirmed on the
    /// Simulator: a <c>Tag?</c> argument on a public generic parent answers correctly for both
    /// nil and non-nil, so refusing it destroys surface that works.</para>
    ///
    /// <para>Scoped to the parameter side deliberately. A generic RESULT is already excluded by
    /// the indirect-result check on the return arm, and unlike the parameter case it has not been
    /// measured here — so it keeps the conservative answer rather than inheriting this one.</para>
    /// </summary>
    private static bool IsGenericPayloadOptional(TypeSpec typeSpec, HashSet<string> visibleGenericNames)
        => IsOptionalType(typeSpec)
           && typeSpec is NamedTypeSpec named
           && named.GenericParameters.Count == 1
           && named.GenericParameters[0] is NamedTypeSpec inner
           && (visibleGenericNames.Contains(inner.Name)
               || TypeSpecHelpers.IsGenericTypeParameter(inner.Name));

    /// <summary>
    /// True when the ABI floor will replace this member's body with a throw, for either of the two
    /// independent reasons it recognises — no nameable call route
    /// (<see cref="IsUncallableInternalDirectDispatch"/>) or a value too wide for the slot
    /// (<see cref="HasTruncatedLargeOptionalDirectDispatch"/>).
    ///
    /// <para>Exists so that emission sites which must agree with the tombstone decision without
    /// caring which arm produced it — chiefly the failable-factory path, which decides separately
    /// whether to stamp the marker on the declaration — cannot fall out of step by consulting only
    /// one arm. A site checking a single arm silently emits an unmarked tombstone for members
    /// caught by the other.</para>
    /// </summary>
    internal static bool IsAbiFloorTombstoned(MethodEnvironment env)
        => HasTruncatedLargeOptionalDirectDispatch(env) || IsUncallableInternalDirectDispatch(env);

    /// <summary>
    /// Diagnostic id for a member left on the direct-CallConvSwift path with a predicted
    /// non-blittable signature. Advisory: the member is still callable, and for the shapes the
    /// emitter passes indirectly the call works.
    /// </summary>
    internal const string DirectCallConvSwiftDiagnosticId = "SB0001";

    /// <summary>
    /// Diagnostic id for a member with no sound call route at all
    /// (<see cref="IsUncallableInternalDirectDispatch"/>), whose body is therefore a throw.
    ///
    /// <para>Deliberately NOT <see cref="DirectCallConvSwiftDiagnosticId"/>: that id is suppressed
    /// wholesale by consumers running in the NativeAOT-oriented interop mode, on the reasoning that
    /// a direct CallConvSwift call is safe there. That reasoning does not reach this member — it
    /// throws on every runtime — so sharing the id would silently hide the one notice a consumer
    /// gets before calling something that cannot work.</para>
    /// </summary>
    internal const string UncallableAbiDiagnosticId = "SB0009";

    /// <summary>
    /// The diagnostic id and sentence for <paramref name="env"/>'s unmitigated direct-CallConvSwift
    /// condition, or <c>null</c> when the member carries none. Two different things are marked here
    /// and they must neither claim the same thing nor share an id: an <b>uncallable</b> member (see
    /// <see cref="IsUncallableInternalDirectDispatch"/>) says plainly that it throws, because its
    /// body is a throw, while the rest are a caution — the direct call may still be exercised and,
    /// for the indirectly-passed shapes, works. Single source so every site that renders the marker
    /// (the method signature emitter and the derived async-overload attribute) stays consistent with
    /// the body the emitter actually produced.
    /// </summary>
    internal static (string DiagnosticId, string Message)? GetNonBlittableCallConvSwiftIssue(MethodEnvironment env)
    {
        // Checked before the blittability condition because it is not a subset of it: the
        // truncating fallback's IntPtr slot IS blittable, so this member can be perfectly
        // well-formed by that measure and still be unable to carry its own value.
        //
        // Accessors are excluded HERE and only here. The floor itself still covers them — a
        // property getter truncates exactly like the equivalent method, and its body is replaced
        // by a throw on the separate ApplyAbiFloorTombstone path, so nothing unsound is emitted.
        // What must not happen is stamping the marker on the PRIVATE synthesized accessor: the
        // public property's `get => Name_Get()` then calls an error-severity-obsolete member and
        // the generated binding stops compiling. That is the same "property deferral" the
        // sibling risk markers below observe, which they get for free because
        // HasUnmitigatedNonBlittableCallConvSwift opens with its own !IsAccessor test — a shield
        // this arm sits in front of and therefore has to repeat.
        if (!env.MethodDecl.IsAccessor && HasTruncatedLargeOptionalDirectDispatch(env))
        {
            return (UncallableAbiDiagnosticId,
                "This member carries an Optional whose Swift representation is wider than the "
                + "single machine word the direct P/Invoke signature has for it, and no Swift "
                + "wrapper is available to pass it through memory instead. Calling it would read "
                + "the bytes past the first word from uninitialized memory, which for an Optional "
                + "with no separate tag byte also decides whether the value reads as nil. It is "
                + "declared for source and conformance compatibility only and throws "
                + "NotSupportedException when called");
        }

        if (!HasUnmitigatedNonBlittableCallConvSwift(env))
            return null;

        if (IsUncallableInternalDirectDispatch(env))
        {
            return (UncallableAbiDiagnosticId,
                "This member, or the Swift type declaring it, is internal to its Swift module, so no "
                + "@_cdecl wrapper or native thunk can be generated for it, and the direct P/Invoke "
                + "signature it falls back to is not blittable, which the Swift calling convention "
                + "cannot carry. It is declared for source and conformance compatibility only and "
                + "throws NotSupportedException when called");
        }

        return (DirectCallConvSwiftDiagnosticId,
            "No @_cdecl wrapper or native thunk available. "
            + "P/Invoke calling convention may not match Swift ABI");
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
