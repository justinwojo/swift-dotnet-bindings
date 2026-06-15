// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

// NOTE: The former `MethodMarshalPlan` aggregate and its exclusively-owned support records
// (MethodSignatureInfo, PInvokeDeclarationInfo, PInvokeParameterInfo, ParameterMarshalInfo,
// GenericMetadataSetup, GenericParameterMetadata, AsyncMethodSetup) were deleted: they had
// zero production references, described a consumption topology that does not exist, and
// `PInvokeDeclarationInfo` shadowed the live `PInvokeDeclaration` class in
// PInvokeHelperEmitter.cs. The live plan type is `SyncMethodPlan` (built by
// MethodMarshalPlanBuilder, consumed by WrapperEmitter); the setup records below are the
// shared building blocks it actually uses.

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

    /// <summary>
    /// When non-null, the indirect-result scratch buffer is a constant-size, non-escaping
    /// copy-out buffer that <see cref="MethodMarshalPlan"/>'s emitter <c>stackalloc</c>s
    /// (<c>byte* _cdeclBuf = stackalloc byte[StackAllocByteCount]</c>) instead of
    /// <c>NativeMemory.Alloc</c>-ing. Used for returns whose value is copied out of the buffer
    /// before the wrapper returns (a 2-word <c>SBW_Utf8Slice</c> for String, a 2-word
    /// <c>SwiftClosureData</c> for closures), so the buffer never escapes the stack frame. In
    /// that mode <see cref="AllocationCode"/> only derives <c>resultPtr</c> from <c>_cdeclBuf</c>
    /// (no allocation) and <see cref="CleanupCode"/> is null — the stack reclaims the buffer on
    /// frame exit, exactly as the former <c>NativeMemory.Free</c> reclaimed the heap container.
    /// The value is a C# expression of type <c>int</c> (e.g. <c>"nint.Size * 2"</c>).
    /// </summary>
    public string? StackAllocByteCount { get; init; }
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

    /// <summary>
    /// Name of the local variable holding the P/Invoke return value.
    /// Defaults to "result"; renamed to "__result" when a method parameter is also named "result"
    /// to avoid CS0841/CS0136 self-referential shadowing.
    /// </summary>
    public string ReturnLocalName { get; init; } = "result";

    /// <summary>The fixed block header (e.g., "fixed (T* __self = &amp;this)"), or null.</summary>
    public string? FixedBlockHeader { get; init; }

    /// <summary>Whether the method body requires an unsafe block.</summary>
    public bool RequiresUnsafe { get; init; }
}
