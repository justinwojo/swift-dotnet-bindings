// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Represents a method declaration.
    /// </summary>
    public sealed record MethodDecl : BaseDecl
    {
        /// <summary>
        /// The immutable parser ABI fact: the <c>@_silgen_name</c> / silgen mangled symbol for this
        /// declaration, as read from the ABI JSON. AF13 (Finding 13) made this <c>init</c>-only — it is
        /// never rewritten during emission. Emission-time symbol promotion (cdecl/thunk/wrapper
        /// re-targeting) lives on the emission-scoped <see cref="MethodEnvironment.EmissionSymbol"/>
        /// side table instead of mutating this in place. The <c>init</c> accessor still permits
        /// object-initializer and <c>with</c>-expression construction (e.g. synthesized overload decls).
        /// </summary>
        public required string MangledName { get; init; }

        /// <summary>
        /// Indicates if the method is a static method.
        /// </summary>
        public required MethodType MethodType { get; set; }

        /// <summary>
        /// Indicates if the method is a constructor.
        /// </summary>
        public required bool IsConstructor { get; set; }

        /// <summary>
        /// Indicates if the constructor is failable (init? or init!).
        /// When true, the initializer may return nil and should be emitted
        /// as a static factory method returning a nullable type.
        /// </summary>
        public bool IsFailable { get; set; } = false;

        /// <summary>
        /// Signature of the method.
        /// </summary>
        public required List<ArgumentDecl> CSSignature { get; set; }

        /// <summary>
        /// Indicates if method can throw an exception.
        /// </summary>
        public required bool Throws { get; set; }

        /// <summary>
        /// Indicates if the method is async.
        /// </summary>
        public required bool IsAsync { get; set; }

        /// <summary>
        /// Generic parameters of the method.
        /// </summary>
        public required List<GenericArgumentDecl> GenericParameters { get; set; }

        /// <summary>
        /// Indicates if the method is generic.
        /// </summary>
        public bool IsGeneric => GenericParameters.Count > 0;

        /// <summary>
        /// Finding 48: true when this method is a synthesized accessor — a getter/setter the
        /// parser produces for a stored property or subscript (and which the emitter renders as
        /// a <c>private</c> C# helper behind the public property/indexer). It is NOT an
        /// access-control level: Swift access control never reaches the generator (only public
        /// API is bound), so the field that once masqueraded as a <c>Visibility</c> enum only
        /// ever carried this one bit. Real method internal-ness is tracked separately by
        /// <c>BaseDecl.IsModuleInternal</c> (see parser-marshaler rule: use that, not a
        /// visibility enum, to avoid CS0737). Defaults to <c>false</c> (an ordinary method
        /// emits as <c>public</c>).
        /// </summary>
        public bool IsSynthesizedAccessor { get; set; } = false;

        /// <summary>
        /// Indicates if this method is a property accessor (getter or setter).
        /// When true, automatic type conversions should not be applied.
        /// </summary>
        public bool IsAccessor { get; set; } = false;

        /// <summary>
        /// Indicates if this method is a subscript accessor (getter or setter).
        /// Used to exclude subscripts from decomposed Optional property patterns.
        /// </summary>
        public bool IsSubscriptAccessor { get; set; } = false;

        /// <summary>
        /// Indicates if the method is mutating (modifies self on value types).
        /// Parsed from funcSelfKind in the ABI JSON.
        /// </summary>
        public bool IsMutating { get; set; } = false;

        /// <summary>
        /// Indicates if the method takes ownership of self (Swift's <c>consuming func</c>).
        /// Parsed from <c>funcSelfKind == "Consuming"</c> in the ABI JSON.
        /// On a <c>~Copyable</c> parent, self must be <c>move()</c>d out of the buffer rather than
        /// borrowed through <c>.pointee</c> (a borrow cannot be consumed), and the owning C# handle
        /// must be marked consumed so its value-witness Destroy does not run a second time.
        /// </summary>
        public bool IsConsuming { get; set; } = false;

        /// <summary>
        /// Indicates if the method borrows self read-only (Swift's <c>borrowing func</c>).
        /// Parsed from <c>funcSelfKind == "Borrowing"</c> in the ABI JSON.
        /// Borrowing self is reconstructed via a non-owning <c>.pointee</c> borrow through the pointer.
        /// </summary>
        public bool IsBorrowing { get; set; } = false;

        /// <summary>
        /// Whether this method or accessor is declared as 'final'.
        /// Final members use direct dispatch even inside non-final classes
        /// (bare symbols exported, no Tj dispatch thunk needed).
        /// Stored property accessors on let properties are implicitly final.
        /// </summary>
        public bool IsFinal { get; set; } = false;

        /// <summary>
        /// Whether this method overrides a superclass method.
        /// Parsed from the ABI JSON 'overriding' field or 'Override' in declAttributes.
        /// Used to emit 'override' keyword on C# methods.
        /// </summary>
        public bool IsOverride { get; set; } = false;

        /// <summary>
        /// Raw genericSig string from ABI JSON (e.g., "&lt;τ_0_0 where τ_0_0 == Foundation.Data?&gt;").
        /// Used to detect constructors with same-type constraints on parent generic params,
        /// which prevent the protocol factory pattern from working.
        /// </summary>
        public string? RawGenericSig { get; set; }

        /// <summary>
        /// The structured form of <see cref="RawGenericSig"/> (Finding 19): the single grammar that
        /// every method-signature predicate queries instead of hand-scanning the raw string. Computed
        /// on access (not cached in a backing field, which would perturb record equality); the parse
        /// is cheap and these signatures are short.
        /// </summary>
        public GenericSignatureModel ParsedGenericSignature =>
            GenericSignatureParser.ParseSignature(RawGenericSig);

        /// <summary>
        /// Set to true during emission when this method passes all validation gates and is
        /// actually written to the C# output. Used by override resolution to verify that a
        /// base class method exists in the emitted C# hierarchy (not just the parsed model).
        /// </summary>
        public bool WasEmitted { get; set; } = false;

        /// <summary>
        /// Marks this method as emitted. The single mutation entry point for <see cref="WasEmitted"/>
        /// — every emitter that successfully writes a method stamps it through here rather than
        /// assigning the flag inline, so "an emitter that produced a member stamps it" lives in one
        /// place (pinned by <c>WasEmittedAssignmentCountTests</c>).
        /// </summary>
        public void MarkEmitted() => WasEmitted = true;

        /// <summary>
        /// The actual emitted public C# method name as it appears in the generated source —
        /// post-NameProvider renames (property/nested-type collisions, "Get" prefix, "Async"
        /// suffix, builder rules) AND post-collision-disambiguation suffix (when two Swift
        /// overloads project to the same C# signature, IHandler.HandleBaseDecl assigns a
        /// numeric suffix via <c>MethodEnvironment.CollisionIndex</c>; the second overload
        /// emits as <c>Foo2</c>, third as <c>Foo3</c>, etc.). Stamped by the conductor right
        /// after <c>handler.Emit</c> returns, when <see cref="WasEmitted"/> is true. Null
        /// until emission. Cross-module override verification reads this directly so the
        /// downstream module's verifier sees the truth, not a recomputation that lacks the
        /// runtime-assigned collision suffix.
        /// </summary>
        public string? EmittedCSharpName { get; set; }

        /// <summary>
        /// Indicates the method is @usableFromInline internal — visible in the ABI but not
        /// callable from external modules. Used by ArraySlice normalization to skip generating
        /// wrapper extensions for inaccessible methods.
        /// </summary>
        public bool IsModuleInternal { get; set; } = false;

        /// <summary>
        /// Whether this method is marked @_spi (System Programming Interface).
        /// @_spi members on public types are only visible to SPI consumers (e.g., other modules
        /// in the same package) and should not appear in generated bindings.
        /// </summary>
        public bool IsSpiProtected { get; set; } = false;

        /// <summary>
        /// Whether this is an @objc optional protocol method.
        /// ObjC protocols can declare optional methods that conforming types may omit.
        /// When called on a protocol existential, optional methods return Optional results
        /// and require ?. chaining. Witness dispatch and EveryProtocol conformance should
        /// skip these methods.
        /// </summary>
        public bool IsObjCOptional { get; set; } = false;

        /// <summary>
        /// Whether this method is compiler-synthesized (implicit inherited constructor).
        /// Parsed from the ABI JSON 'implicit' field. Used to filter out inherited
        /// constructors that appear in the ABI but are not callable from external code.
        /// </summary>
        public bool IsImplicit { get; set; } = false;

        /// <summary>
        /// Whether this method is actor-isolated via a per-member annotation (e.g., @MainActor or @ProcessingActor).
        /// Does NOT include isolation inherited from the parent type — that uses TypeDecl.IsMainActorIsolated
        /// or ClassDecl.IsActor. Both @MainActor and custom global actors set this flag.
        /// </summary>
        public bool IsActorIsolated { get; set; } = false;

        /// <summary>
        /// Whether this method is specifically @MainActor-isolated (per-member annotation).
        /// A subset of IsActorIsolated — true only for @MainActor, false for custom actors like @ProcessingActor.
        /// Used to decide whether a @_cdecl wrapper needs @MainActor annotation (vs. blocking entirely).
        /// </summary>
        public bool IsMainActorIsolated { get; set; } = false;

        /// <summary>
        /// Whether this method is declared nonisolated (opts out of containing type's isolation).
        /// </summary>
        public bool IsNonisolated { get; set; } = false;

        /// <summary>
        /// When true, PInvokeEmitter uses the wrapper library (AsyncLibraryName) instead of the module library.
        /// Set by normalization emitters that generate Swift wrapper functions.
        /// </summary>
        public bool UsesWrapperLibrary { get; set; } = false;

        /// <summary>
        /// When true, escaping closure parameters are adapted via a Cdecl Swift wrapper.
        /// The P/Invoke signature uses separate (funcPtr, context) IntPtr pairs instead of
        /// SwiftClosureData, and callbacks use CallConvCdecl instead of CallConvSwift.
        /// Set by MethodHandler (and wrapper generators) before SignatureHandler builds P/Invoke signature.
        /// Does NOT imply explicit IntPtr self — see <see cref="UsesFreeFunctionWrapper"/>.
        /// </summary>
        public bool HasClosureCdeclWrapper { get; set; } = false;

        /// <summary>
        /// When true, the Swift wrapper is a free function (not extension method), so self
        /// is passed as explicit IntPtr parameter instead of SwiftSelf register.
        /// Only set for standalone closure wrappers where closures are the sole wrapping reason.
        /// NOT set for wrapper generator paths (ArraySlice, DefaultParam, Existential) which
        /// use extension methods with implicit self.
        /// </summary>
        public bool UsesFreeFunctionWrapper { get; set; } = false;

        /// <summary>
        /// When true, large Optional parameters (e.g., Optional&lt;String&gt;) are passed via
        /// UnsafeRawPointer in a generated Swift wrapper, avoiding IntPtr truncation.
        /// The C# side passes a pointer to the full Optional buffer instead of reading
        /// only 8 bytes through PayloadBuffer&lt;IntPtr&gt;.
        /// </summary>
        public bool HasOptionalPointerWrapper { get; set; } = false;

        /// <summary>
        /// When true, the P/Invoke entry point for this method is not exported by the library's TBD.
        /// Calling this method at runtime will throw <see cref="System.EntryPointNotFoundException"/>.
        /// Set by <see cref="MethodHandler"/> during symbol cross-referencing.
        /// </summary>
        public bool IsMissingExportedSymbol { get; set; } = false;

        /// <summary>
        /// The typed error type for methods declared with Swift's typed throws syntax
        /// (e.g., <c>throws(ParseError)</c>). Parsed from the .swiftinterface file.
        /// When non-null, the generated code throws <c>SwiftException&lt;TError&gt;</c>
        /// instead of <c>SwiftRuntimeException</c> (sync) or <c>SwiftException</c> (async).
        /// </summary>
        public TypeSpec? ThrownErrorType { get; set; }

        /// <summary>
        /// Whether this method uses Swift's typed throws syntax.
        /// </summary>
        public bool HasTypedThrows => ThrownErrorType != null;

        /// <summary>
        /// When true, this method has a generic closure parameter that is handled via
        /// the monomorphized Swift wrapper bridge (Pattern A: sync, method-generic, noescape,
        /// identity-forwarding return). The Swift wrapper specializes T=UnsafeMutableRawPointer
        /// and passes a pre-allocated result buffer. The C# side uses a GCHandle-based callback
        /// with aligned buffer allocation and VWT lifecycle management.
        /// Set by MethodHandler when IsMethodGenericClosureEligible returns true.
        /// </summary>
        public bool HasGenericClosureBridge { get; set; } = false;

        /// <summary>
        /// When true, this method has throwing closure parameters that will produce
        /// simplified Action/Func overloads via <see cref="ThrowingClosureSimplificationEmitter"/>.
        /// Set during pre-scan in MethodHandler before WrapperEmitter so the original method
        /// gets [EditorBrowsable(Never)] in the standard attribute pipeline.
        /// </summary>
        public bool HasThrowingClosureSimplification { get; set; } = false;

        /// <summary>
        /// When true, one or more parameters of this method are Swift variadic parameters
        /// (e.g., String..., Disposable...). The ABI JSON represents these as Array&lt;T&gt;,
        /// but the actual Swift API expects T... — passing [T] where T... is expected causes
        /// a compilation error. Detected from the demangler's IsVariadic flag on the inner
        /// element type of the Array parameter.
        /// @_cdecl wrappers cannot call variadic methods correctly, so this blocks wrapper emission.
        /// </summary>
        public bool HasVariadicParameter { get; set; } = false;

        /// <summary>
        /// When true, this method was synthesized from a protocol extension method
        /// parsed from a .swiftinterface file. Protocol extension methods use static
        /// dispatch and are called via generated @_silgen_name Swift wrappers.
        /// </summary>
        public bool IsProtocolExtensionMethod { get; set; } = false;

        /// <summary>
        /// When true, this synthetic method actually wraps a read-only protocol-extension
        /// PROPERTY default surfaced as a zero-parameter getter method (see
        /// <c>ProtocolExtensionEmitter</c>). The Swift member is read as a property
        /// (<c>instance.name</c>), NOT invoked (<c>instance.name()</c>): any wrapper body
        /// that renders the call — the free-function path or the concrete-specialization
        /// (CSM) path for generic parents — MUST omit the call parens for these, or swiftc
        /// rejects the wrapper with "cannot call value of non-function type". The
        /// <c>IsProperty</c> signal is otherwise lost once the property is lowered into a
        /// <see cref="MethodDecl"/>, so this flag carries it forward.
        /// </summary>
        public bool IsExtensionPropertyGetter { get; set; } = false;

        /// <summary>
        /// Stable cross-emitter identity for the underlying Swift method that this
        /// wrapper claims. Used by
        /// <see cref="ModuleEmissionContext.TryClaimWrapperSymbol"/> so two emitters
        /// reaching the same Swift method through different naming schemes
        /// (<c>MethodWrapperEmitter</c>'s hash-based <c>SBW_&lt;Module&gt;_&lt;Type&gt;_&lt;method&gt;_&lt;hash8&gt;</c>
        /// vs. <c>ProtocolExtensionEmitter</c>'s label-based
        /// <c>SBW_&lt;FlatType&gt;_&lt;method&gt;_&lt;labels&gt;</c>) collapse to a single
        /// structural identity rather than competing for two distinct symbol-string
        /// registrations. Synthetic protocol-extension methods carry a key built
        /// from <c>ProtocolQualifiedName::PrintedName::RawSignature</c> so genuine
        /// Swift overloads that share external labels but differ on parameter type
        /// stay distinct. <c>null</c> on ordinary methods, where the rendered
        /// <c>SBW_</c> symbol string is used as the structural identity directly.
        /// </summary>
        public string? StructuralIdentityKey { get; set; }

        /// <summary>
        /// Whether this method is defined in a Swift extension (isFromExtension in ABI JSON).
        /// Extension methods use static dispatch — they have no vtable entry and no Tj
        /// dispatch thunk symbol. ComputeEntryPoint must NOT append "Tj" for these methods.
        /// This is critical for cross-module extensions (one module extending a class defined
        /// in another module) where Tj thunks don't exist in any binary.
        /// </summary>
        public bool IsExtensionMethod { get; set; } = false;

        /// <summary>
        /// Whether this method is a protocol requirement (protocolReq=true in ABI JSON).
        /// Protocol requirements must be implemented by conforming types. Extension default
        /// methods (protocolReq=false) provide default implementations and don't need stubs
        /// in EveryProtocol conformances. Used by MissingRequirements detection to avoid
        /// false positives when only extension defaults fail ABI parsing.
        /// </summary>
        public bool IsProtocolRequirement { get; set; } = false;

        /// <summary>
        /// The wrapper strategy for this method's P/Invoke routing.
        /// Enforces mutual exclusivity of CdeclConstructor/CdeclProperty/CdeclMethod
        /// by the type system instead of guard ordering.
        /// </summary>
        public WrapperStrategy WrapperStrategy { get; set; } = WrapperStrategy.None;

        /// <summary>
        /// When true, this constructor uses a @_cdecl Swift wrapper with C calling convention.
        /// Routes constructor P/Invokes through CallingConvention.Cdecl.
        /// Set by ConstructorWrapperEmitter before SignatureHandler construction.
        /// </summary>
        public bool UsesCdeclConstructorWrapper
        {
            get => WrapperStrategy == WrapperStrategy.CdeclConstructor;
            set { if (value) WrapperStrategy = WrapperStrategy.CdeclConstructor;
                  else if (WrapperStrategy == WrapperStrategy.CdeclConstructor) WrapperStrategy = WrapperStrategy.None; }
        }

        /// <summary>
        /// When true, this property accessor uses a @_cdecl Swift wrapper with C calling convention.
        /// Routes property getter/setter P/Invokes through CallingConvention.Cdecl.
        /// Set by PropertyWrapperEmitter before SignatureHandler construction.
        /// </summary>
        public bool UsesCdeclPropertyWrapper
        {
            get => WrapperStrategy == WrapperStrategy.CdeclProperty;
            set { if (value) WrapperStrategy = WrapperStrategy.CdeclProperty;
                  else if (WrapperStrategy == WrapperStrategy.CdeclProperty) WrapperStrategy = WrapperStrategy.None; }
        }

        /// <summary>
        /// When true, this method uses a @_cdecl Swift wrapper with C calling convention.
        /// Routes method P/Invokes through CallingConvention.Cdecl.
        /// Set by MethodWrapperEmitter before SignatureHandler construction.
        /// </summary>
        public bool UsesCdeclMethodWrapper
        {
            get => WrapperStrategy == WrapperStrategy.CdeclMethod;
            set { if (value) WrapperStrategy = WrapperStrategy.CdeclMethod;
                  else if (WrapperStrategy == WrapperStrategy.CdeclMethod) WrapperStrategy = WrapperStrategy.None; }
        }

        /// <summary>
        /// Computed property: true if any @_cdecl wrapper is active (constructor, property, or method).
        /// Used in PInvokeEmitter and MethodMarshalPlanBuilder to route through Cdecl calling convention.
        /// </summary>
        public bool UsesCdeclWrapper => WrapperStrategy is WrapperStrategy.CdeclConstructor
            or WrapperStrategy.CdeclProperty
            or WrapperStrategy.CdeclMethod;

        /// <summary>
        /// Computed property: true if this method is routed through a native ARM64 assembly thunk.
        /// Thunks bridge cdecl → swiftcc at the native level, eliminating the need for @_cdecl
        /// Swift wrappers or CallConvSwift runtime support.
        /// </summary>
        public bool UsesNativeThunk => WrapperStrategy == WrapperStrategy.NativeThunk;

        /// <summary>
        /// Whether the native ARM64 thunk assembly has already been emitted for this method.
        /// Set by PropertyHandler and SubscriptHandler when they emit thunks directly,
        /// to prevent MethodHandler from emitting a duplicate thunk.
        /// </summary>
        public bool ThunkAssemblyEmitted { get; set; } = false;

        /// <summary>
        /// When set, this method was originally an async property getter routed through
        /// method emission. Contains the original Swift property name for the async wrapper
        /// call expression (e.g., "image" → "await instance.image" instead of "instance.getImage()").
        /// </summary>
        public string? AsyncPropertyName { get; set; }

        /// <summary>
        /// Whether this method has closure parameters handled by the @_cdecl wrapper.
        /// Set by MethodHandler when UsesCdeclMethodWrapper/UsesCdeclConstructorWrapper is set
        /// on a method with closures.
        /// </summary>
        public bool HasClosureParams { get; set; } = false;

        /// <summary>
        /// True when closure params should use Cdecl marshalling (IntPtr funcPtr + IntPtr context).
        /// Covers standalone closure wrappers (HasClosureCdeclWrapper) and @_cdecl wrappers
        /// (method or constructor) that handle closure params inline.
        /// </summary>
        public bool HasCdeclClosureMarshalling => HasClosureCdeclWrapper || (UsesCdeclWrapper && HasClosureParams);

        /// <summary>
        /// When true, this constructor had unsupported optional closure params that were
        /// stripped from CSSignature. The @_cdecl wrapper passes nil for them.
        /// Set by MethodHandler when forcing a wrapper for constructors with collection params
        /// whose only blocking factor is unsupported optional closures.
        /// </summary>
        public bool HasNilOptionalClosures { get; set; } = false;

        /// <summary>
        /// The original argument list (excluding return) before optional closures were stripped,
        /// with each entry marked as kept or nil. Used by ConstructorWrapperEmitter to emit
        /// nil args at the correct positions in the forwarding call.
        /// Each entry is (arg, isNilClosure, argLabel) where isNilClosure=true means this
        /// was a stripped optional closure that should be passed as nil.
        /// </summary>
        public List<(ArgumentDecl Arg, bool IsNilClosure, string ArgLabel)>? OriginalArgsWithNilClosures { get; set; }

        /// <summary>
        /// When true, this member's signature contains an unsupported closure parameter that
        /// the generator cannot bridge today. Instead of skipping the member entirely (which
        /// hides the API from consumers), the emitter writes a tombstoned-but-reachable
        /// declaration: the unsupported closure parameter projects to <c>object?</c>, the
        /// member carries <c>[Obsolete(... DiagnosticId = "SB0005")]</c> plus
        /// <c>[UnsupportedSwiftType("Unsupported closure fallback", ...)]</c>, and the body
        /// throws <see cref="System.NotSupportedException"/>. Set in HandleBaseDecl /
        /// ModuleHandler when ValidateMethodEmission returns UnsupportedClosure and the
        /// member is tombstone-eligible (see ClosureParamTombstoneEmitter.IsEligible).
        /// </summary>
        public bool IsClosureParamTombstone { get; set; } = false;

        /// <summary>
        /// When true, this decl is a reduced overload synthesized by the pre-gate
        /// trailing-default rescue: a clone of a member that the gate dropped solely because a
        /// trailing default-valued parameter had an unbindable type, with those trailing
        /// parameters removed. The clone KEEPS the original full-ABI <c>MangledName</c> but emits
        /// FEWER arguments, so it is correct ONLY when realized by a @_cdecl wrapper whose Swift
        /// body calls the declaration by name with the kept arguments and lets Swift supply the
        /// dropped trailing defaults. A native ARM64 thunk cannot do this — it emits
        /// <c>bl &lt;swift_symbol&gt;</c> straight to the full-ABI symbol and has no way to fill a
        /// Swift default, so it would call that symbol with the dropped parameter's register left
        /// uninitialized (garbage → runtime fault). The handler prefers a thunk over a @_cdecl
        /// wrapper whenever one is eligible, so this flag forces the @_cdecl path:
        /// <see cref="NativeThunkEmitter.ShouldEmitThunk"/> returns false for any decl carrying it.
        /// </summary>
        public bool IsGateReducedOverload { get; set; } = false;
    }

    /// <summary>
    /// Represents a method type.
    /// </summary>
    public enum MethodType
    {
        /// <summary>
        /// Indicates that the method is an instance method.
        /// </summary>
        Instance,

        /// <summary>
        /// Indicates that the method is a static method.
        /// </summary>
        Static
    }

    /// <summary>
    /// Describes how a method's P/Invoke is routed to the Swift library.
    /// Replaces the previous mutually-exclusive boolean flags on MethodDecl.
    /// </summary>
    public enum WrapperStrategy
    {
        /// <summary>Default: no wrapper or thunk. Uses CallConvSwift for direct Swift symbol P/Invoke.</summary>
        None,

        /// <summary>Constructor routed through a @_cdecl Swift wrapper.</summary>
        CdeclConstructor,

        /// <summary>Property accessor routed through a @_cdecl Swift wrapper.</summary>
        CdeclProperty,

        /// <summary>Method routed through a @_cdecl Swift wrapper.</summary>
        CdeclMethod,

        /// <summary>Method routed through a native ARM64 assembly thunk (cdecl → swiftcc bridge).</summary>
        NativeThunk,
    }
}
