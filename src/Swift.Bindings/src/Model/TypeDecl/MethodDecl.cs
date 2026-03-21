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
        /// Mangled name of the declaration.
        /// </summary>
        public required string MangledName { get; set; }

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
        /// Gets or sets the visibility of the method.
        /// </summary>
        public required Visibility Visibility { get; set; } = Visibility.Public;

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
        /// Set to true during emission when this method passes all validation gates and is
        /// actually written to the C# output. Used by override resolution to verify that a
        /// base class method exists in the emitted C# hierarchy (not just the parsed model).
        /// </summary>
        public bool WasEmitted { get; set; } = false;

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
        /// Whether this method is defined in a Swift extension (isFromExtension in ABI JSON).
        /// Extension methods use static dispatch — they have no vtable entry and no Tj
        /// dispatch thunk symbol. ComputeEntryPoint must NOT append "Tj" for these methods.
        /// This is critical for cross-module extensions (e.g., StripePayments extending
        /// StripeCore.STPAPIClient) where Tj thunks don't exist in any binary.
        /// </summary>
        public bool IsExtensionMethod { get; set; } = false;

        /// <summary>
        /// The wrapper strategy for this method's P/Invoke routing.
        /// Enforces mutual exclusivity of CdeclConstructor/CdeclProperty/CdeclMethod
        /// by the type system instead of guard ordering.
        /// </summary>
        public WrapperStrategy WrapperStrategy { get; set; } = WrapperStrategy.LegacyCallConvSwift;

        /// <summary>
        /// When true, this constructor uses a @_cdecl Swift wrapper with C calling convention
        /// instead of CallConvSwift. Routes constructor P/Invokes through CallingConvention.Cdecl
        /// to avoid NativeAOT/ARM64 ABI mismatches with struct parameters and indirect results.
        /// Set by ConstructorWrapperEmitter before SignatureHandler construction.
        /// </summary>
        public bool UsesCdeclConstructorWrapper
        {
            get => WrapperStrategy == WrapperStrategy.CdeclConstructor;
            set { if (value) WrapperStrategy = WrapperStrategy.CdeclConstructor;
                  else if (WrapperStrategy == WrapperStrategy.CdeclConstructor) WrapperStrategy = WrapperStrategy.LegacyCallConvSwift; }
        }

        /// <summary>
        /// When true, this property accessor uses a @_cdecl Swift wrapper with C calling convention
        /// instead of CallConvSwift. Routes property getter/setter P/Invokes through CallingConvention.Cdecl
        /// to avoid NativeAOT/ARM64 ABI mismatches with enum, string, and non-blittable struct properties.
        /// Set by PropertyWrapperEmitter before SignatureHandler construction.
        /// </summary>
        public bool UsesCdeclPropertyWrapper
        {
            get => WrapperStrategy == WrapperStrategy.CdeclProperty;
            set { if (value) WrapperStrategy = WrapperStrategy.CdeclProperty;
                  else if (WrapperStrategy == WrapperStrategy.CdeclProperty) WrapperStrategy = WrapperStrategy.LegacyCallConvSwift; }
        }

        /// <summary>
        /// When true, this method uses a @_cdecl Swift wrapper with C calling convention
        /// instead of CallConvSwift. Routes method P/Invokes through CallingConvention.Cdecl
        /// to avoid NativeAOT/ARM64 ABI mismatches with non-blittable params (NSUrl, etc.)
        /// and remaining Mono JIT crashes on device.
        /// Set by MethodWrapperEmitter before SignatureHandler construction.
        /// </summary>
        public bool UsesCdeclMethodWrapper
        {
            get => WrapperStrategy == WrapperStrategy.CdeclMethod;
            set { if (value) WrapperStrategy = WrapperStrategy.CdeclMethod;
                  else if (WrapperStrategy == WrapperStrategy.CdeclMethod) WrapperStrategy = WrapperStrategy.LegacyCallConvSwift; }
        }

        /// <summary>
        /// Computed property: true if any @_cdecl wrapper is active (constructor, property, or method).
        /// Used in PInvokeEmitter and MethodMarshalPlanBuilder to route through Cdecl calling convention.
        /// </summary>
        public bool UsesCdeclWrapper => WrapperStrategy is WrapperStrategy.CdeclConstructor
            or WrapperStrategy.CdeclProperty
            or WrapperStrategy.CdeclMethod;

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
        /// <summary>Default: no wrapper, uses CallConvSwift calling convention.</summary>
        LegacyCallConvSwift,

        /// <summary>Method intentionally not emitted (skipped by a gate).</summary>
        None,

        /// <summary>Constructor routed through a @_cdecl Swift wrapper.</summary>
        CdeclConstructor,

        /// <summary>Property accessor routed through a @_cdecl Swift wrapper.</summary>
        CdeclProperty,

        /// <summary>Method routed through a @_cdecl Swift wrapper.</summary>
        CdeclMethod,
    }
}
