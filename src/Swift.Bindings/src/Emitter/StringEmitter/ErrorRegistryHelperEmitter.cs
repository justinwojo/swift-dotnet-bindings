// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Phase 4 plain-throws → typed-exception bridge — Layer 3 (cascade helper).
///
/// Emits the per-module Swift cascade helper and C# typed-exception dispatcher
/// that consume the registry built by <see cref="ErrorEnumRegistryEmitter"/>:
/// <list type="bullet">
///   <item>Swift <c>_SBW_dispatchSwiftError_{Module}(error:_sbwTask:errorCallback:)</c> —
///         module-level helper that performs the alphabetical-id-ordered <c>as?</c>
///         cascade against every registered error type, packages the matched
///         payload according to its shape (allocated value buffer for value
///         types, retained class-pointer for class-shaped errors), and invokes
///         the 6-param C error callback with the matched id. Falls through with
///         id <c>0</c> when no registered type matches.</item>
///   <item>C# <c>_SbwModuleErrorRegistry_{Module}.CreateException(errorTypeId, errorPtr, errorSize, errorMessage)</c> —
///         namespace-level static helper that consumes the wire id, marshals the
///         typed payload via <see cref="Swift.Runtime.SwiftMarshal"/>
///         (or callers' equivalent), and constructs the matching
///         <c>SwiftException&lt;TError&gt;</c>. id <c>0</c> falls back to untyped
///         <c>SwiftException</c>.</item>
/// </list>
/// Emission is deduped per module via <see cref="ModuleEmissionContext"/>:
/// <see cref="ModuleEmissionContext.ErrorRegistryHelperEmittedSwift"/> and
/// <see cref="ModuleEmissionContext.ErrorRegistryHelperEmittedCSharp"/>.
/// </summary>
/// <remarks>
/// Phase 4 Layer 5 ownership model: the dispatcher selects one of four shapes
/// per registered error type, mirroring the typed-throws ownership rules in
/// <see cref="WrapperEmitter"/>:
/// <list type="bullet">
///   <item><b>Value-copy</b> (simple enums, plain frozen structs) — Swift
///         allocates a fresh buffer and copies the matched value into it; C#
///         marshals by value and frees the buffer in the per-case
///         <c>finally</c>. <c>MarshalFromSwift</c> does not retain the buffer.</item>
///   <item><b>Buffer-owned-by-SafeHandle</b> (complex enums, non-frozen
///         structs) — Swift allocates a fresh buffer and copies the matched
///         value into it; C# wraps the wire pointer directly into a
///         <c>SwiftSafeHandle</c>. The buffer is freed only in the per-case
///         <c>catch</c> (marshal failure); on success the SafeHandle's
///         finalizer releases it.</item>
///   <item><b>Buffer-copied-needs-VWT-destroy</b> (frozen-with-memory
///         structs, i.e. <c>IsFrozenStructProjectedAsClass</c>) — Swift
///         allocates a fresh buffer and copies the matched value into it; C#'s
///         frozen-struct <c>NewFromPayload</c> does an
///         <c>InitializeWithCopy</c> into a separate <c>NativeMemory.Alloc</c>
///         buffer owned by the SafeHandle, leaving the wire carrier with +1
///         retains on heap fields. The per-case <c>finally</c> calls
///         <c>SwiftMarshal.DestroyWireBufferRetains&lt;T&gt;</c> (releases the
///         retains via VWT <c>Destroy</c>) followed by <c>SBW_Free</c>.</item>
///   <item><b>Class-pointer-direct</b> (Swift classes conforming to Error) —
///         Swift hands a +1 retained class pointer via
///         <c>Unmanaged.passRetained(_:).toOpaque()</c> (no carrier buffer);
///         C# passes that pointer to <c>MarshalFromSwift&lt;T&gt;</c>, whose
///         <c>NewFromPayload</c> takes ownership of the +1 retain. There is
///         nothing to <c>SBW_Free</c> in this shape.</item>
/// </list>
/// The Swift cascade emits per-type code paths so the wire shape matches the
/// C# helper's per-case dispatch — both sides must agree on whether the
/// callback's <c>errorPtr</c> is a buffer-of-bytes or a retained-class-pointer.
/// </remarks>
public static class ErrorRegistryHelperEmitter
{
    /// <summary>
    /// Emits the Swift cascade helper for the current module if not already
    /// emitted. No-op when the module has no registered error types
    /// (<c>ctx.ErrorTypeOrder.Count == 0</c>).
    /// </summary>
    /// <param name="typeDatabase">Drives per-type Swift cascade shape selection
    /// (value-copy buffer vs class-pointer-direct). May be null for legacy / unit-test
    /// callers; the null path defaults every entry to value-copy buffer emit, which is
    /// safe for the simple-enum fixtures unit tests use and never wired without a
    /// real database in production.</param>
    public static bool EmitSwiftCascadeIfNeeded(
        SwiftWriter swiftWriter,
        string moduleName,
        ModuleEmissionContext ctx,
        ITypeDatabase? typeDatabase = null)
    {
        ArgumentNullException.ThrowIfNull(swiftWriter);
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.ErrorRegistryHelperEmittedSwift)
            return false;
        if (ctx.ErrorTypeOrder.Count == 0)
            return false;

        ctx.ErrorRegistryHelperEmittedSwift = true;

        var dispatchSymbol = GetSwiftDispatchSymbolName(moduleName);
        var cascadeBody = BuildSwiftCascadeBody(ctx, typeDatabase, indent: "    ");

        // Inherit @available from every registered error type — the dispatcher body
        // references each by name in `as? Type` casts, so the function must be at
        // least as restrictive as the strictest type. Without this, validate fails
        // on modules like WeatherKit where `WeatherError` is iOS 16+ but the
        // dispatcher is unannotated. EmitSwiftAvailability collapses to the strictest
        // version per platform across the merged list.
        WrapperEmitterHelpers.EmitSwiftAvailability(
            swiftWriter, CollectMergedErrorTypeAvailability(ctx));

        // The helper is a regular Swift function (not @_cdecl) because its first
        // parameter is `any Error` — an existential the C ABI cannot represent.
        // It's called directly from generated `} catch { ... }` blocks in the same
        // wrapper file, so module-private visibility is sufficient.
        //
        // The function-pointer parameter `errorCallback` matches the wire format
        // emitted by the C# side: 6 params, all C-representable, Cdecl-callable.
        // Param order mirrors the existing typed-throws shape (errorPtr, errorSize,
        // messagePtr, isCancellation, _sbwTask) with `errorTypeId` appended as the
        // discriminator (id 0 = untyped fallback, > 0 = registry id). _sbwTask is
        // Int64 for parity with the rest of the async wrapper surface, which encodes
        // the GCHandle as Int64 across the @convention(c) boundary. Optional pointers
        // (errorPtr / msgPtr) so cancellation / fallthrough branches can pass nil.
        swiftWriter.WriteLines($$"""
            // Phase 4 plain-throws → typed-exception cascade dispatcher for {{moduleName}}.
            // Emitted once per module; called from every plain-throws async wrapper's
            // catch block. Performs an alphabetical-by-id cascade of `as?` casts
            // against registered Error-conforming types and invokes the C# callback
            // with a buffer + matched typeId (or a retained class pointer for class
            // -shaped errors), or falls through with id 0 (untyped).
            internal func {{dispatchSymbol}}(
                _ error: any Error,
                _ _sbwTask: Int64,
                _ errorCallback: @convention(c) (UnsafeRawPointer?, Int, UnsafePointer<CChar>?, Int32, Int64, Int32) -> Void
            ) {
                let _isCancelled: Int32 = (error is CancellationError) ? 1 : 0
                let errorMessage = String(describing: error)
                if _isCancelled != 0 {
                    errorMessage.withCString { _msgPtr in
                        errorCallback(nil, 0, _msgPtr, _isCancelled, _sbwTask, 0)
                    }
                    return
                }
            {{cascadeBody}}
                // Fallthrough: no registered error type matched — untyped fallback.
                errorMessage.withCString { _msgPtr in
                    errorCallback(nil, 0, _msgPtr, 0, _sbwTask, 0)
                }
            }

            """);

        return true;
    }

    /// <summary>
    /// Emits the C# typed-exception dispatcher class for the current module if
    /// not already emitted. Writes a namespace-level <c>internal static class</c>
    /// with a <c>CreateException</c> method that switches on the wire <c>errorTypeId</c>,
    /// marshals the typed payload, and returns the constructed exception. id 0
    /// falls back to untyped <see cref="Swift.Runtime.SwiftException"/>.
    /// </summary>
    /// <param name="typeDatabase">Drives per-id buffer ownership selection. When
    /// available, the dispatch body emits one of three shapes (value-copy,
    /// buffer-owned-by-SafeHandle, class-pointer-direct) per type, mirroring
    /// <c>typedErrorTransfersOwnershipAsync</c> in <see cref="WrapperEmitter"/>.
    /// May be null for legacy callers (tests); when null, all entries default to
    /// value-copy ownership semantics — safe for the simple-enum fixtures unit
    /// tests use, and never wired without a real database in production.</param>
    public static bool EmitCSharpRegistryIfNeeded(
        CSharpWriter csWriter,
        string moduleName,
        string wrapperLibPath,
        ModuleEmissionContext ctx,
        ITypeDatabase? typeDatabase = null)
    {
        ArgumentNullException.ThrowIfNull(csWriter);
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.ErrorRegistryHelperEmittedCSharp)
            return false;
        if (ctx.ErrorTypeOrder.Count == 0)
            return false;

        ctx.ErrorRegistryHelperEmittedCSharp = true;

        var helperClassName = GetCSharpHelperClassName(moduleName);
        var freeSymbol = Utf8SliceEmitter.GetFreeSymbolName(moduleName);
        var dispatchBody = BuildCSharpDispatchBody(ctx, typeDatabase, moduleName, indent: "                ");

        // Per-id ownership: value-copy cases free in their per-case `finally`;
        // buffer-owned-by-SafeHandle cases free only in their per-case `catch` (marshal
        // failure) so a successful marshal hands the buffer to the constructed
        // SafeHandle. Class-pointer-direct cases never free — the wire `errorPtr`
        // IS the class pointer, owned by the resulting SwiftObject. The default
        // branch frees defensively for any unknown id with a non-null buffer (the
        // Swift cascade should never produce that pair, but belt-and-suspenders
        // against future drift). Marshal exceptions surface as bare SwiftException
        // so the consumer still sees the original Swift message.
        csWriter.WriteLines($$"""
            /// <summary>
            /// Phase 4 plain-throws → typed-exception bridge — module-scoped C# dispatcher
            /// for <c>{{moduleName}}</c>. Wire-format consumer: switches on
            /// <c>errorTypeId</c> to reconstruct the matching <c>SwiftException&lt;TError&gt;</c>
            /// from the Swift-allocated error buffer (or class pointer for class-shaped
            /// errors). id 0 is the untyped fallback.
            /// </summary>
            internal static partial class {{helperClassName}}
            {
                [global::System.Runtime.InteropServices.LibraryImport("{{wrapperLibPath}}", EntryPoint = "{{freeSymbol}}")]
                private static partial void SBW_Free(IntPtr ptr);

                internal static System.Exception CreateException(
                    int errorTypeId,
                    IntPtr errorPtr,
                    nint errorSize,
                    string errorMessage)
                {
                    // Untyped fallback path: Swift dispatcher passes id 0 with nil pointer.
                    if (errorTypeId == 0)
                        return new global::Swift.Runtime.SwiftException(errorMessage);

                    System.Exception result;
                    try
                    {
                        switch (errorTypeId)
                        {
            {{dispatchBody}}
                            default:
                                // Unknown id with a real buffer: free defensively, fall back to untyped.
                                if (errorPtr != IntPtr.Zero)
                                    SBW_Free(errorPtr);
                                result = new global::Swift.Runtime.SwiftException(errorMessage);
                                break;
                        }
                    }
                    catch (System.Exception marshalEx)
                    {
                        // Per-case catch / finally already freed (or transferred) the buffer.
                        // Surface the marshal failure as bare SwiftException so the consumer
                        // still sees the Swift error message; the typed payload is unrecoverable.
                        result = new global::Swift.Runtime.SwiftException(
                            $"{errorMessage} (typed marshal failed for id {errorTypeId}: {marshalEx.Message})");
                    }
                    return result;
                }
            }

            """);

        return true;
    }

    /// <summary>Per-module Swift cascade-dispatch symbol name.</summary>
    public static string GetSwiftDispatchSymbolName(string moduleName) =>
        $"_SBW_dispatchSwiftError_{moduleName}";

    /// <summary>Per-module C# helper class name (namespace-level static).</summary>
    public static string GetCSharpHelperClassName(string moduleName) =>
        $"_SbwModuleErrorRegistry_{moduleName}";

    /// <summary>
    /// Per-type cascade payload shape. Distinguishes:
    /// <list type="bullet">
    ///   <item><see cref="ValueCopy"/> — simple enum / plain frozen struct;
    ///         marshal copies bytes by value, free in <c>finally</c>.</item>
    ///   <item><see cref="BufferOwnedBySafeHandle"/> — complex enum or
    ///         non-frozen struct; <c>NewFromPayload</c> wraps the wire buffer
    ///         directly into a <c>SwiftSafeHandle</c>, which owns the carrier
    ///         after a successful marshal (free only on marshal failure).</item>
    ///   <item><see cref="BufferCopiedNeedsVwtDestroy"/> — frozen-with-memory
    ///         struct (<c>IsFrozenStructProjectedAsClass</c>);
    ///         <c>NewFromPayload</c> copies the payload into a fresh
    ///         <c>NativeMemory.Alloc</c> buffer via <c>InitializeWithCopy</c>,
    ///         leaving the source carrier with <c>+1</c> retains on its heap
    ///         fields. The original carrier needs both VWT <c>Destroy</c> (to
    ///         release those retains) and <c>SBW_Free</c> (to free the carrier
    ///         allocation) on every successful marshal.</item>
    ///   <item><see cref="ClassPointerDirect"/> — Swift class conforming to
    ///         <c>Error</c>; wire is a +1 retained class pointer (no carrier
    ///         buffer); <c>NewFromPayload</c> takes ownership of the retain.</item>
    /// </list>
    /// </summary>
    private enum CascadePayloadShape
    {
        ValueCopy,
        BufferOwnedBySafeHandle,
        BufferCopiedNeedsVwtDestroy,
        ClassPointerDirect,
    }

    /// <summary>
    /// Concatenates the per-type availability annotations registered for this module
    /// into a single list. <see cref="WrapperEmitterHelpers.EmitSwiftAvailability"/>
    /// collapses to the strictest (max) version per platform, so the dispatcher's
    /// emitted floor is the union-of-strictest across every registered type.
    /// Returns null when no registered type carries availability — callers treat
    /// that as "emit nothing".
    /// </summary>
    private static IReadOnlyList<AvailabilityAnnotation>? CollectMergedErrorTypeAvailability(ModuleEmissionContext ctx)
    {
        List<AvailabilityAnnotation>? merged = null;
        foreach (var swiftTypeName in ctx.ErrorTypeOrder)
        {
            var perType = ctx.GetErrorTypeAvailability(swiftTypeName);
            if (perType is { Count: > 0 })
            {
                merged ??= new List<AvailabilityAnnotation>();
                merged.AddRange(perType);
            }
        }
        return merged;
    }

    private static string BuildSwiftCascadeBody(ModuleEmissionContext ctx, ITypeDatabase? typeDatabase, string indent)
    {
        var sb = new System.Text.StringBuilder();
        var idx = 0;
        foreach (var swiftTypeName in ctx.ErrorTypeOrder)
        {
            idx++;
            var shape = ClassifyShape(swiftTypeName, typeDatabase);
            sb.AppendLine($"{indent}if let _typed = error as? {swiftTypeName} {{");
            if (shape == CascadePayloadShape.ClassPointerDirect)
            {
                // Class-shaped error: hand a +1 retained class pointer directly.
                // No carrier buffer is allocated; the wire `errorPtr` IS the class
                // pointer. C# `MarshalFromSwift<T>` then routes through `NewFromPayload`,
                // which constructs the SwiftObject taking ownership of the +1 retain.
                // Wire `errorSize` is unused in this shape — pass 0.
                sb.AppendLine($"{indent}    let _ptr = Unmanaged.passRetained(_typed as AnyObject).toOpaque()");
                sb.AppendLine($"{indent}    errorMessage.withCString {{ _msgPtr in");
                sb.AppendLine($"{indent}        errorCallback(UnsafeRawPointer(_ptr), 0, _msgPtr, 0, _sbwTask, {idx})");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine($"{indent}    return");
            }
            else
            {
                // Value-types and SafeHandle-owned types: allocate a fresh buffer and
                // value-witness-table-aware copy the matched value into it. C# marshals
                // by value (free in finally) or wraps into a SafeHandle (free only on
                // exception) per the per-case dispatch shape on the C# side.
                sb.AppendLine($"{indent}    let _size = MemoryLayout<{swiftTypeName}>.size");
                sb.AppendLine($"{indent}    let _align = MemoryLayout<{swiftTypeName}>.alignment");
                sb.AppendLine($"{indent}    let _buf = UnsafeMutableRawPointer.allocate(byteCount: max(_size, 1), alignment: _align)");
                sb.AppendLine($"{indent}    _buf.initializeMemory(as: {swiftTypeName}.self, repeating: _typed, count: 1)");
                sb.AppendLine($"{indent}    errorMessage.withCString {{ _msgPtr in");
                sb.AppendLine($"{indent}        errorCallback(UnsafeRawPointer(_buf), Int(Int64(_size)), _msgPtr, 0, _sbwTask, {idx})");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine($"{indent}    return");
            }
            sb.AppendLine($"{indent}}}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string BuildCSharpDispatchBody(ModuleEmissionContext ctx, ITypeDatabase? typeDatabase, string moduleName, string indent)
    {
        // Map each Swift module-qualified name to its C# fully-qualified name.
        // For a first cut we mirror the Swift module-qualified path: WeatherKit.WeatherError
        // becomes global::WeatherKit.WeatherError. This works for module-top-level error
        // types — nested error types in a class/struct may need adjustment in a follow-up
        // (the C# nested-type path can diverge from the Swift module-qualified form).
        var sb = new System.Text.StringBuilder();
        var idx = 0;
        foreach (var swiftTypeName in ctx.ErrorTypeOrder)
        {
            idx++;
            var csharpQualifiedName = ToCSharpFullyQualifiedName(swiftTypeName);
            var shape = ClassifyShape(swiftTypeName, typeDatabase);

            sb.AppendLine($"{indent}case {idx}:");
            sb.AppendLine($"{indent}{{");
            // Per-case ownership pattern mirrors WrapperEmitter.Async.cs's typed-throws emit.
            // - ValueCopy: free in `finally` (marshal copies bytes; buffer is owned by us).
            // - BufferOwnedBySafeHandle: free only in `catch` (successful marshal hands
            //   buffer ownership to the constructed SafeHandle).
            // - BufferCopiedNeedsVwtDestroy: VWT-destroy + free in `finally`. The frozen
            //   `NewFromPayload` does an InitializeWithCopy into a fresh NativeMemory buffer
            //   and wraps the COPY in the SafeHandle, so the original wire carrier still
            //   holds +1 retains on heap fields. Without the destroy those retains leak;
            //   without the free the carrier allocation leaks. (The free symbol matches the
            //   Swift wrapper's `UnsafeMutableRawPointer.allocate` because SBW_Free is
            //   `ptr?.deallocate()` on the same allocator.)
            // - ClassPointerDirect: never `SBW_Free` — wire `errorPtr` is the +1 retained
            //   class pointer (no carrier buffer). On successful marshal, `MarshalFromSwift`'s
            //   `NewFromPayload` constructs a `SwiftClassHandle` that takes ownership of the
            //   +1; the SafeHandle's release path balances the retain. On marshal failure we
            //   must release the +1 ourselves — otherwise the retain leaks via the outer
            //   fallback `catch`.
            sb.AppendLine($"{indent}    {csharpQualifiedName} _typed;");
            sb.AppendLine($"{indent}    try");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        _typed = ({csharpQualifiedName})global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{csharpQualifiedName}>(errorPtr);");
            sb.AppendLine($"{indent}    }}");
            if (shape == CascadePayloadShape.ValueCopy)
                sb.AppendLine($"{indent}    finally {{ if (errorPtr != IntPtr.Zero) SBW_Free(errorPtr); }}");
            else if (shape == CascadePayloadShape.BufferOwnedBySafeHandle)
                sb.AppendLine($"{indent}    catch {{ if (errorPtr != IntPtr.Zero) SBW_Free(errorPtr); throw; }}");
            else if (shape == CascadePayloadShape.BufferCopiedNeedsVwtDestroy)
                sb.AppendLine($"{indent}    finally {{ if (errorPtr != IntPtr.Zero) {{ global::Swift.Runtime.InteropServices.SwiftMarshal.DestroyWireBufferRetains<{csharpQualifiedName}>(errorPtr); SBW_Free(errorPtr); }} }}");
            else // ClassPointerDirect
                sb.AppendLine($"{indent}    catch {{ if (errorPtr != IntPtr.Zero) global::Swift.Runtime.Arc.Release(errorPtr); throw; }}");
            sb.AppendLine($"{indent}    result = new global::Swift.Runtime.SwiftException<{csharpQualifiedName}>(_typed, errorMessage);");
            sb.AppendLine($"{indent}    break;");
            sb.AppendLine($"{indent}}}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Layer 5 per-id buffer ownership selector. Mirrors
    /// <c>typedErrorTransfersOwnershipAsync</c> in <see cref="WrapperEmitter"/>
    /// so the cascade dispatcher stays semantically in lockstep with the
    /// typed-throws path:
    /// <list type="bullet">
    ///   <item>Class types → <see cref="CascadePayloadShape.ClassPointerDirect"/>:
    ///         Swift's <c>NewFromPayload(handle)</c> for a generated class wrapper
    ///         expects the raw class pointer, not a buffer holding it. Class-shaped
    ///         errors hand a +1 retained class pointer over the wire instead of an
    ///         allocated value buffer.</item>
    ///   <item>Complex enums, non-frozen structs →
    ///         <see cref="CascadePayloadShape.BufferOwnedBySafeHandle"/>: the
    ///         generated <c>NewFromPayloadCore</c> wraps the supplied IntPtr in a
    ///         <c>SwiftSafeHandle</c>, so on successful marshal the SafeHandle owns
    ///         the wire buffer; freeing here would double-free with its finalizer.</item>
    ///   <item>Frozen-with-memory structs (<c>IsFrozenStructProjectedAsClass</c>) →
    ///         <see cref="CascadePayloadShape.BufferCopiedNeedsVwtDestroy"/>: the
    ///         frozen-struct <c>NewFromPayload</c> does an
    ///         <c>InitializeWithCopy</c> into a fresh <c>NativeMemory.Alloc</c>
    ///         buffer (owned by the constructed SafeHandle), leaving the original
    ///         wire carrier with +1 retains on heap fields. Free both the retains
    ///         (VWT <c>Destroy</c>) and the carrier (<c>SBW_Free</c>) in
    ///         <c>finally</c>.</item>
    ///   <item>Everything else (simple enums, plain frozen structs) →
    ///         <see cref="CascadePayloadShape.ValueCopy"/>: <c>MarshalFromSwift</c>
    ///         reads bytes by value; the buffer is owned by us and freed in
    ///         <c>finally</c>.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// When <paramref name="typeDatabase"/> is null (legacy / unit-test callers
    /// that don't have a TypeDatabase), the answer defaults to
    /// <see cref="CascadePayloadShape.ValueCopy"/>. Production callers always
    /// pass a non-null database; the null path is only reached by simple-enum
    /// unit fixtures whose marshal is genuinely value-copy.
    /// </remarks>
    private static CascadePayloadShape ClassifyShape(string moduleQualifiedName, ITypeDatabase? typeDatabase)
    {
        if (typeDatabase is null)
            return CascadePayloadShape.ValueCopy;
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName);
        if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return CascadePayloadShape.ValueCopy;
        if (record.Kind == TypeRecordKind.Class)
            return CascadePayloadShape.ClassPointerDirect;
        bool isFrozenStructAsClass = record.Kind == TypeRecordKind.Struct &&
            MarshallingHelpers.IsFrozenStructProjectedAsClass(record);
        if (isFrozenStructAsClass)
            return CascadePayloadShape.BufferCopiedNeedsVwtDestroy;
        bool isComplexEnum = record.Kind == TypeRecordKind.Enum &&
            !record.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
        bool isNonFrozenStruct = record.Kind == TypeRecordKind.Struct &&
            !MarshallingHelpers.IsTypeFrozen(record);
        if (isComplexEnum || isNonFrozenStruct)
            return CascadePayloadShape.BufferOwnedBySafeHandle;
        return CascadePayloadShape.ValueCopy;
    }

    private static string ToCSharpFullyQualifiedName(string swiftModuleQualifiedName) =>
        $"global::{swiftModuleQualifiedName}";
}
