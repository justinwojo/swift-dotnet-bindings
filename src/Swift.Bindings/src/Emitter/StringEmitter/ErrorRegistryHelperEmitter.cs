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
///         cascade against every registered error type, allocates a typed buffer
///         on match, and invokes the 6-param C error callback with the matched id.
///         Falls through with id <c>0</c> when no registered type matches.</item>
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
/// Ownership model: the cascade always allocates a fresh buffer
/// (<c>UnsafeMutableRawPointer.allocate</c> + <c>initializeMemory</c>), so the
/// C# side always frees on the way out via <see cref="Utf8SliceEmitter"/>'s
/// <c>SBW_Free</c>. This is the simple value-copy ownership shape (no ownership
/// transfer to a SafeHandle); refinement for class-shaped errors is deferred
/// to Layer 5.
/// </remarks>
public static class ErrorRegistryHelperEmitter
{
    /// <summary>
    /// Emits the Swift cascade helper for the current module if not already
    /// emitted. No-op when the module has no registered error types
    /// (<c>ctx.ErrorTypeOrder.Count == 0</c>).
    /// </summary>
    /// <param name="typeDatabase">Used to filter the cascade to value-copy-safe types
    /// (simple enums, plain frozen structs). Complex enums, non-frozen structs,
    /// frozen-with-memory structs, and classes hand ownership of the typed buffer to
    /// the C# <c>SafeHandle</c> via <c>SwiftMarshal.MarshalFromSwift</c>; freeing in
    /// the helper's <c>finally</c> would double-free. Phase 4 Layer 2 ships only the
    /// value-copy shape; ownership transfer for class-shaped errors is Layer 5.
    /// May be null for legacy callers (tests); when null, no filtering applies.</param>
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
            // with a buffer + matched typeId, or falls through with id 0 (untyped).
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

        csWriter.WriteLines($$"""
            /// <summary>
            /// Phase 4 plain-throws → typed-exception bridge — module-scoped C# dispatcher
            /// for <c>{{moduleName}}</c>. Wire-format consumer: switches on
            /// <c>errorTypeId</c> to reconstruct the matching <c>SwiftException&lt;TError&gt;</c>
            /// from the Swift-allocated error buffer. id 0 is the untyped fallback.
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
                    System.Exception result;
                    try
                    {
                        switch (errorTypeId)
                        {
            {{dispatchBody}}
                            default:
                                result = new global::Swift.Runtime.SwiftException(errorMessage);
                                break;
                        }
                    }
                    catch (System.Exception marshalEx)
                    {
                        // MarshalFromSwift failure: surface the registered type id but fall back
                        // to untyped exception so the consumer still sees the Swift error message.
                        result = new global::Swift.Runtime.SwiftException(
                            $"{errorMessage} (typed marshal failed for id {errorTypeId}: {marshalEx.Message})");
                    }
                    finally
                    {
                        if (errorPtr != IntPtr.Zero)
                            SBW_Free(errorPtr);
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

    private static string BuildSwiftCascadeBody(ModuleEmissionContext ctx, ITypeDatabase? typeDatabase, string indent)
    {
        var sb = new System.Text.StringBuilder();
        var idx = 0;
        foreach (var swiftTypeName in ctx.ErrorTypeOrder)
        {
            idx++;
            // Phase 4 Layer 2 ownership scope: skip cascade entries for types that hand
            // buffer ownership to the C# SafeHandle on MarshalFromSwift (complex enums,
            // non-frozen structs, frozen-with-memory structs, classes). Freeing the
            // buffer in the helper's `finally` would double-free for those. Skipped
            // types still hold a registry id (deterministic alphabetical ordering is
            // preserved across versions) but never match in cascade, so they fall
            // through to id 0 → bare SwiftException. Layer 5 will add per-id ownership
            // and re-enable typed dispatch for class-shaped errors.
            if (!IsValueCopySafeForCascade(swiftTypeName, typeDatabase))
                continue;
            sb.AppendLine($"{indent}if let _typed = error as? {swiftTypeName} {{");
            sb.AppendLine($"{indent}    let _size = MemoryLayout<{swiftTypeName}>.size");
            sb.AppendLine($"{indent}    let _align = MemoryLayout<{swiftTypeName}>.alignment");
            sb.AppendLine($"{indent}    let _buf = UnsafeMutableRawPointer.allocate(byteCount: max(_size, 1), alignment: _align)");
            sb.AppendLine($"{indent}    _buf.initializeMemory(as: {swiftTypeName}.self, repeating: _typed, count: 1)");
            sb.AppendLine($"{indent}    errorMessage.withCString {{ _msgPtr in");
            sb.AppendLine($"{indent}        errorCallback(UnsafeRawPointer(_buf), Int(Int64(_size)), _msgPtr, 0, _sbwTask, {idx})");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}    return");
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
            // Mirror the Swift cascade filter: only emit C# dispatch cases for
            // value-copy-safe types. The Swift dispatcher never calls back with an id
            // for a skipped type, so an emitted case would be unreachable; omitting it
            // also prevents accidental misuse if a future cascade emit forgets to skip.
            if (!IsValueCopySafeForCascade(swiftTypeName, typeDatabase))
                continue;
            var csharpQualifiedName = ToCSharpFullyQualifiedName(swiftTypeName);
            sb.AppendLine($"{indent}case {idx}:");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    var _typed = ({csharpQualifiedName})global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{csharpQualifiedName}>(errorPtr);");
            sb.AppendLine($"{indent}    result = new global::Swift.Runtime.SwiftException<{csharpQualifiedName}>(_typed, errorMessage);");
            sb.AppendLine($"{indent}    break;");
            sb.AppendLine($"{indent}}}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Phase 4 Layer 2 ownership filter. Returns true when an Error-conforming type
    /// can be safely allocated, value-copied into a fresh buffer in Swift, marshalled
    /// into C# via <c>SwiftMarshal.MarshalFromSwift</c>, and freed via <c>SBW_Free</c>
    /// from the cascade helper's <c>finally</c>. Mirrors the inverse of
    /// <c>typedErrorTransfersOwnershipAsync</c> in <see cref="WrapperEmitter"/> — types
    /// that transfer buffer ownership to a <c>SafeHandle</c> must NOT be freed by the
    /// helper, so they're skipped from the cascade in this layer and fall through to
    /// the untyped <c>SwiftException</c>. Layer 5 lifts the restriction by emitting
    /// per-id ownership behavior.
    /// </summary>
    /// <remarks>
    /// When <paramref name="typeDatabase"/> is null (legacy / unit-test callers that
    /// don't have a TypeDatabase), no filtering applies — callers in that mode are
    /// driving the registry deterministically and don't go through the production
    /// cascade emit path. Production callers always pass a non-null database.
    /// </remarks>
    private static bool IsValueCopySafeForCascade(string moduleQualifiedName, ITypeDatabase? typeDatabase)
    {
        if (typeDatabase is null)
            return true;
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName);
        if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return false; // unknown — be conservative and skip
        bool isComplexEnum = record.Kind == TypeRecordKind.Enum &&
            !record.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
        bool isNonFrozenStruct = record.Kind == TypeRecordKind.Struct &&
            !MarshallingHelpers.IsTypeFrozen(record);
        bool isFrozenStructAsClass = record.Kind == TypeRecordKind.Struct &&
            MarshallingHelpers.IsFrozenStructProjectedAsClass(record);
        bool isClassError = record.Kind == TypeRecordKind.Class;
        return !(isComplexEnum || isNonFrozenStruct || isFrozenStructAsClass || isClassError);
    }

    private static string ToCSharpFullyQualifiedName(string swiftModuleQualifiedName) =>
        $"global::{swiftModuleQualifiedName}";
}
