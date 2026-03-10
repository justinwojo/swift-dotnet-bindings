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
        /// Whether this method is @MainActor-isolated (individually annotated, not inherited from type).
        /// </summary>
        public bool IsActorIsolated { get; set; } = false;

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
        /// Mono JIT risk flags detected by <see cref="MonoJitRiskDetector"/>.
        /// Informational annotation only — does not affect P/Invoke routing.
        /// Routing is controlled by <see cref="UsesWrapperLibrary"/>, which is only set
        /// when a corresponding Swift wrapper function has been generated.
        /// </summary>
        public MonoJitRiskDetector.MonoJitRisk DetectedJitRisks { get; set; } = MonoJitRiskDetector.MonoJitRisk.None;

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
        /// When true, this method was synthesized from a protocol extension method
        /// parsed from a .swiftinterface file. Protocol extension methods use static
        /// dispatch and are called via generated @_silgen_name Swift wrappers.
        /// </summary>
        public bool IsProtocolExtensionMethod { get; set; } = false;

        /// <summary>
        /// When true, this constructor uses a @_cdecl Swift wrapper with C calling convention
        /// instead of CallConvSwift. Routes constructor P/Invokes through CallingConvention.Cdecl
        /// to avoid NativeAOT/ARM64 ABI mismatches with struct parameters and indirect results.
        /// Set by ConstructorWrapperEmitter before SignatureHandler construction.
        /// </summary>
        public bool UsesCdeclConstructorWrapper { get; set; } = false;
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
}
