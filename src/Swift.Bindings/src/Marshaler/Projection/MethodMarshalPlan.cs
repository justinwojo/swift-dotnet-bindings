// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// A complete marshalling plan for a Swift method call from C#.
/// Composes per-parameter MarshalPlans (from TypeProjectionFactory) with
/// method-level concerns (SwiftSelf, SwiftError, generic metadata, etc.).
/// Consumed by Sessions 5B/5C for plan-driven emission.
/// </summary>
public record MethodMarshalPlan
{
    /// <summary>The C# public method signature.</summary>
    public required MethodSignatureInfo PublicSignature { get; init; }

    /// <summary>The [LibraryImport] P/Invoke declaration.</summary>
    public required PInvokeDeclarationInfo PInvokeDeclaration { get; init; }

    /// <summary>Per-parameter marshalling plans, in order.</summary>
    public required IReadOnlyList<ParameterMarshalInfo> ParameterPlans { get; init; }

    /// <summary>Return value marshalling plan (null for void methods).</summary>
    public MarshalPlan? ReturnPlan { get; init; }

    /// <summary>SwiftSelf setup for instance methods (null for static/free functions).</summary>
    public SwiftSelfSetup? SwiftSelf { get; init; }

    /// <summary>SwiftError setup for throwing methods (null for non-throwing).</summary>
    public SwiftErrorSetup? SwiftError { get; init; }

    /// <summary>Generic type metadata setup (null for non-generic methods).</summary>
    public GenericMetadataSetup? GenericMetadata { get; init; }

    /// <summary>Indirect result allocation for large return types (null for direct returns).</summary>
    public IndirectResultSetup? IndirectResult { get; init; }

    /// <summary>Async infrastructure: TCS, callbacks, Swift wrapper (null for sync methods).</summary>
    public AsyncMethodSetup? Async { get; init; }

    /// <summary>Optional pointer wrapper for large Optional returns (null when not needed).</summary>
    public OptionalPointerWrapperSetup? OptionalPointerWrapper { get; init; }

    /// <summary>Whether the method body requires an unsafe block.</summary>
    public bool RequiresUnsafe { get; init; }

    /// <summary>Whether the method requires a fixed block (frozen struct pointer pinning).</summary>
    public bool RequiresFixed { get; init; }

    /// <summary>Generated Swift wrapper code (null when no wrapper needed).</summary>
    public string? SwiftWrapperCode { get; init; }

    /// <summary>Callback declarations to emit alongside the method (closures, async callbacks).</summary>
    public IReadOnlyList<CallbackDeclaration> CallbackDeclarations { get; init; } = Array.Empty<CallbackDeclaration>();
}

/// <summary>
/// Information about the public C# method signature.
/// </summary>
public record MethodSignatureInfo
{
    /// <summary>The method name.</summary>
    public required string Name { get; init; }

    /// <summary>The return type string.</summary>
    public required string ReturnType { get; init; }

    /// <summary>The parameter declarations (type + name pairs).</summary>
    public required IReadOnlyList<(string Type, string Name)> Parameters { get; init; }

    /// <summary>Generic type parameters (e.g., "T0", "T1").</summary>
    public IReadOnlyList<string> GenericTypeParameters { get; init; } = Array.Empty<string>();

    /// <summary>Access modifier (public, internal, etc.).</summary>
    public string AccessModifier { get; init; } = "public";

    /// <summary>Whether the method is static.</summary>
    public bool IsStatic { get; init; }
}

/// <summary>
/// Information about the [LibraryImport] P/Invoke declaration.
/// </summary>
public record PInvokeDeclarationInfo
{
    /// <summary>The P/Invoke method name (mangled or @_silgen_name).</summary>
    public required string EntryPoint { get; init; }

    /// <summary>The library name for the DllImport.</summary>
    public required string LibraryName { get; init; }

    /// <summary>The P/Invoke return type.</summary>
    public required string ReturnType { get; init; }

    /// <summary>Whether the return is bool (needs [return: MarshalAs(UnmanagedType.U1)]).</summary>
    public bool ReturnIsBool { get; init; }

    /// <summary>The P/Invoke parameters.</summary>
    public required IReadOnlyList<PInvokeParameterInfo> Parameters { get; init; }

    /// <summary>Calling convention types (always CallConvCdecl).</summary>
    public IReadOnlyList<string> CallingConventions { get; init; } = new[] { "typeof(CallConvCdecl)" };
}

/// <summary>
/// A single P/Invoke parameter with type and optional marshalling attribute.
/// </summary>
public record PInvokeParameterInfo
{
    /// <summary>The P/Invoke type string.</summary>
    public required string Type { get; init; }

    /// <summary>The parameter name.</summary>
    public required string Name { get; init; }

    /// <summary>Optional marshalling attribute (e.g., "[MarshalAs(UnmanagedType.U1)]").</summary>
    public string? MarshalAttribute { get; init; }
}

/// <summary>
/// Per-parameter marshalling info combining the type projection with the parameter name.
/// </summary>
public record ParameterMarshalInfo
{
    /// <summary>The parameter name in the public API.</summary>
    public required string PublicName { get; init; }

    /// <summary>The parameter name in the P/Invoke call.</summary>
    public required string PInvokeName { get; init; }

    /// <summary>The marshalling plan for this parameter.</summary>
    public required MarshalPlan Plan { get; init; }

    /// <summary>The type projection that produced this plan.</summary>
    public required ITypeProjection Projection { get; init; }
}

/// <summary>
/// SwiftSelf creation for instance methods.
/// </summary>
public record SwiftSelfSetup
{
    /// <summary>The kind of self access needed.</summary>
    public required SwiftSelfKind Kind { get; init; }

    /// <summary>The C# code to create the SwiftSelf variable.</summary>
    public required string CreationCode { get; init; }

    /// <summary>The resolved type name for the parent type.</summary>
    public string? ResolvedTypeName { get; init; }
}

/// <summary>
/// The kind of SwiftSelf creation needed for an instance method.
/// </summary>
public enum SwiftSelfKind
{
    /// <summary>Frozen struct value semantics: SwiftSelf&lt;T&gt;(this)</summary>
    FrozenStructValue,

    /// <summary>Frozen struct with buffer: SwiftSelf&lt;T.Buffer&gt;(*payload)</summary>
    FrozenStructBuffer,

    /// <summary>Frozen struct setter: SwiftSelf((void*)payload)</summary>
    FrozenStructSetter,

    /// <summary>Class: SwiftSelf(*(void**)payload) — dereference pointer to pointer</summary>
    Class,

    /// <summary>ObjC-rooted class: SwiftSelf((void*)Handle) — Handle IS the object pointer</summary>
    ObjCRootedClass,

    /// <summary>Non-frozen struct: SwiftSelf((void*)payload) — buffer IS the data</summary>
    NonFrozenStruct,

    /// <summary>Fixed block: SwiftSelf(__self) — uses fixed pointer</summary>
    FixedBlock,

    /// <summary>Async/free function wrapper — no SwiftSelf variable needed</summary>
    None
}

/// <summary>
/// SwiftError setup for throwing methods.
/// </summary>
public record SwiftErrorSetup
{
    /// <summary>Whether the method uses typed throws (SwiftException&lt;TError&gt;).</summary>
    public bool IsTypedThrows { get; init; }

    /// <summary>The C# error type name for typed throws, or null for untyped.</summary>
    public string? TypedErrorTypeName { get; init; }

    /// <summary>
    /// The fully-qualified Swift error type name (e.g., "SwiftBindingsTestLib.ParseError"),
    /// used for emitting the Swift extractor function. Null for untyped throws.
    /// </summary>
    public string? SwiftErrorTypeName { get; init; }

    /// <summary>
    /// Sanitized suffix for the extractor P/Invoke and Swift symbol name (e.g., "SwiftBindingsTestLib_ParseError").
    /// Dots replaced by underscores to form valid identifiers. Prevents collision when different modules
    /// define same-named error types. Null for untyped throws.
    /// </summary>
    public string? TypedErrorSafeSuffix { get; init; }

    /// <summary>Post-call error check code.</summary>
    public required string ErrorCheckCode { get; init; }
}

/// <summary>
/// Generic type metadata extraction for generic methods.
/// </summary>
public record GenericMetadataSetup
{
    /// <summary>Per-generic-parameter metadata extraction.</summary>
    public required IReadOnlyList<GenericParameterMetadata> Parameters { get; init; }

    /// <summary>Protocol witness table extraction statements.</summary>
    public IReadOnlyList<string> WitnessTableStatements { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Metadata for a single generic type parameter.
/// </summary>
public record GenericParameterMetadata
{
    /// <summary>The C# generic parameter name (e.g., "T0").</summary>
    public required string ParameterName { get; init; }

    /// <summary>The metadata variable declaration code.</summary>
    public required string MetadataCode { get; init; }
}

/// <summary>
/// Indirect result setup for large return types.
/// </summary>
public record IndirectResultSetup
{
    /// <summary>Whether this is a constructor indirect result.</summary>
    public bool IsConstructor { get; init; }

    /// <summary>The return type for TypeMetadata size calculation.</summary>
    public required string ReturnTypeName { get; init; }

    /// <summary>Allocation and SwiftIndirectResult creation code.</summary>
    public required string AllocationCode { get; init; }

    /// <summary>
    /// Cleanup code emitted after reading the return value (e.g., NativeMemory.Free).
    /// Null when no cleanup is needed (stack-based SwiftIndirectResult, constructor paths).
    /// </summary>
    public string? CleanupCode { get; init; }
}

/// <summary>
/// Async method infrastructure (Task, callbacks, Swift wrapper).
/// </summary>
public record AsyncMethodSetup
{
    /// <summary>TaskCompletionSource type string.</summary>
    public required string TaskCompletionSourceType { get; init; }

    /// <summary>The success callback declaration.</summary>
    public required CallbackDeclaration SuccessCallback { get; init; }

    /// <summary>The error callback declaration (null for non-throwing async).</summary>
    public CallbackDeclaration? ErrorCallback { get; init; }

    /// <summary>The generated Swift async wrapper code.</summary>
    public required string SwiftWrapperCode { get; init; }
}

/// <summary>
/// Optional pointer wrapper setup for large Optional return values.
/// </summary>
public record OptionalPointerWrapperSetup
{
    /// <summary>The SwiftOptional type for size calculation.</summary>
    public required string OptionalTypeName { get; init; }

    /// <summary>Stack allocation code for the out-buffer.</summary>
    public required string AllocationCode { get; init; }
}

/// <summary>
/// A complete plan for emitting a sync method body.
/// Built by MethodMarshalPlanBuilder, consumed by WrapperEmitter's thin Emit* wrappers.
/// </summary>
public record SyncMethodPlan
{
    /// <summary>SwiftSelf creation for instance methods (null for static/free functions).</summary>
    public SwiftSelfSetup? SwiftSelf { get; init; }

    /// <summary>SwiftError setup for throwing methods (null for non-throwing).</summary>
    public SwiftErrorSetup? SwiftError { get; init; }

    /// <summary>Indirect result setup for constructors (null when not needed).</summary>
    public IndirectResultSetup? IndirectResultConstructor { get; init; }

    /// <summary>Indirect result setup for methods (null when not needed).</summary>
    public IndirectResultSetup? IndirectResultMethod { get; init; }

    /// <summary>Optional return buffer setup for large Optional returns (null when not needed).</summary>
    public OptionalPointerWrapperSetup? OptionalReturnBuffer { get; init; }

    /// <summary>Declaration lines emitted before the try block (TypeMetadata, IntPtr, GCHandle).</summary>
    public IReadOnlyList<string> DeclarationLines { get; init; } = Array.Empty<string>();

    /// <summary>Generic argument marshalling lines emitted inside the try block (stackalloc + MarshalToSwift).</summary>
    public IReadOnlyList<string> GenericArgumentMarshallingLines { get; init; } = Array.Empty<string>();

    /// <summary>Generic inout writeback lines emitted after the P/Invoke call.</summary>
    public IReadOnlyList<string> GenericInoutWritebackLines { get; init; } = Array.Empty<string>();

    /// <summary>Protocol witness table extraction statements.</summary>
    public IReadOnlyList<string> WitnessTableStatements { get; init; } = Array.Empty<string>();

    /// <summary>The P/Invoke call statement.</summary>
    public required string PInvokeCallStatement { get; init; }

    /// <summary>The fixed block header (e.g., "fixed (T* __self = &amp;this)"), or null.</summary>
    public string? FixedBlockHeader { get; init; }

    /// <summary>Whether the method body requires an unsafe block.</summary>
    public bool RequiresUnsafe { get; init; }
}
