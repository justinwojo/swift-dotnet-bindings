// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared helpers for @_cdecl parameter marshalling that consolidate pointer extraction
/// logic previously duplicated across WrapperEmitter.Marshalling, EnumHandler.CaseConstruction,
/// and PropertyHandler.
///
/// Extracted to eliminate the DangerousGetHandle vs PayloadBuffer confusion that caused
/// bugs MJ-02, MJ-07, and MJ-08 (3 independent SIGSEGVs in 3 different handlers).
///
/// Key invariant: @_cdecl wrappers receive non-reference parameters as UnsafeRawPointer
/// and call .load(as: T.self). C# must pass a POINTER TO the value (DangerousGetHandle),
/// not the dereferenced value (PayloadBuffer.Buffer). However, Optional types wrapping
/// reference types (classes, ObjC-bridged) use nullable pointer ABI where the actual
/// IntPtr value matters, not a pointer to it.
/// </summary>
internal static class CdeclMarshallingHelper
{
    /// <summary>
    /// Determines whether a projection needs the DangerousGetHandle pointer override
    /// for @_cdecl wrappers. When true, the projection's MarshalPlan PayloadBuffer
    /// statements should be replaced with DangerousGetHandle via
    /// <see cref="RenderWithHandleOverride"/>.
    ///
    /// Returns true for:
    /// - Collection types (Array, Dictionary, Set) — always pass pointer to container
    /// - Optional wrapping a collection — same reason
    /// - Optional wrapping a non-reference type (struct, enum, etc.) — .load(as: Optional&lt;T&gt;.self)
    ///
    /// Returns false for:
    /// - Optional wrapping a reference type (Class, ObjCBridged, ObjCRooted) — nullable pointer ABI
    /// - Non-Optional, non-Collection projections
    /// </summary>
    internal static bool NeedsCdeclPointerOverride(ITypeProjection projection)
    {
        // ObjC-bridge container projections build their own {name}Buffer line in their
        // parameter plan (IntPtr {name}Buffer = {name}NSArray.Handle) and do NOT create a
        // {name}Swift variable. Skipping the override here avoids a duplicate {name}Buffer
        // definition (CS0128) and a reference to an undefined {name}Swift (CS0103) when
        // EnumHandler bound-generic factories or other callers route an ObjC-bridged array
        // through RenderWithHandleOverride.
        if (projection.UsesObjCContainerBridge)
            return false;

        // Collections always need pointer override
        if (projection is ArrayProjection or DictionaryProjection or SetProjection)
            return true;

        if (projection is not OptionalProjection optProj)
            return false;

        // Optional<ObjC-bridged collection> — same rationale as above
        if (optProj.InnerProjection.UsesObjCContainerBridge)
            return false;

        // Optional<Collection> needs override
        if (optProj.InnerProjection is ArrayProjection or DictionaryProjection or SetProjection)
            return true;

        // Optional<ReferenceType> uses nullable pointer ABI — no override needed
        if (optProj.InnerProjection is ClassProjection or KeyPathProjection or ObjCBridgedProjection or ObjCBridgeableProjection or ObjCRootedClassProjection)
            return false;

        // Optional<NonFrozenStruct> direct-param: OptionalProjection already emits a complete,
        // self-contained plan for this case — `IntPtr {name}Pointee = ... ; IntPtr {name}Buffer
        // = (IntPtr)(&{name}Pointee);` — and the wrapper signature is the resilient
        // `(_ p: UnsafeRawPointer, ...)` reading `Optional<UnsafeMutableRawPointer>` (8-byte
        // pointer-or-null buffer). No `{name}Swift` SwiftOptional is created, so calling
        // RenderWithHandleOverride here would emit a duplicate `{name}Buffer` declaration
        // (CS0128) referencing an undefined `{name}Swift` (CS0103).
        if (optProj.InnerProjection is NonFrozenStructProjection)
            return false;

        // Optional<ValueType> needs pointer override
        return true;
    }

    /// <summary>
    /// Renders a MarshalPlan's setup statements with the PayloadBuffer extraction replaced
    /// by DangerousGetHandle for @_cdecl wrapper compatibility.
    ///
    /// This filters out:
    /// - PayloadBuffer using declarations (which dereference the buffer)
    /// - Lines that read .Buffer from the Disposable (which gives the dereferenced value)
    ///
    /// And emits a DangerousGetHandle line instead (which gives a pointer TO the value
    /// matching what Swift's .load(as:) expects).
    ///
    /// The variable naming convention follows the projection's pattern: the container
    /// variable is always {variableName}Swift (created by the projection's setup statements),
    /// and the buffer variable is NameProvider.GetBoundGenericBufferName(variableName).
    /// </summary>
    /// <param name="writer">The C# output writer.</param>
    /// <param name="plan">The MarshalPlan from the projection.</param>
    /// <param name="variableName">The raw parameter name (without "Swift" suffix).</param>
    /// <param name="asyncDeferredDisposeListName">When non-null, the SwiftArray/Set/Dictionary
    /// container is hoisted out of its <c>using var</c> and appended to this list (an
    /// <c>AsyncDeferredDisposeList</c>) so its lifetime extends past the foreground async
    /// wrapper's <c>tcs.Task</c> return — the Swift continuation reads the buffer on its
    /// own thread after the wrapper has exited.</param>
    internal static void RenderWithHandleOverride(CSharpWriter writer, MarshalPlan plan, string variableName, string? asyncDeferredDisposeListName = null)
    {
        var bufferName = NameProvider.GetBoundGenericBufferName(variableName);
        var swiftVarName = $"{variableName}Swift";
        foreach (var stmt in plan.SetupStatements)
        {
            // Skip PayloadBuffer and .Buffer lines — replace with DangerousGetHandle
            if (stmt is MarshalStatement.Using u && u.Type.StartsWith("PayloadBuffer"))
                continue;
            if (stmt is MarshalStatement.Line l && l.Code.Contains("Disposable.Buffer"))
                continue;
            // Async deferred-dispose hand-off: replace `using var {name}Swift = expr;` with
            // `var {name}Swift = expr; _asyncDeferredList.Items.Add({name}Swift);` so the
            // container outlives the foreground wrapper. Disposed by the holder cleanup
            // loop after the Swift continuation completes.
            if (asyncDeferredDisposeListName is not null
                && stmt is MarshalStatement.Using uContainer
                && uContainer.Name == swiftVarName)
            {
                writer.WriteLine($"var {uContainer.Name} = {uContainer.InitExpression};");
                writer.WriteLine($"{asyncDeferredDisposeListName}.Items.Add({uContainer.Name});");
                continue;
            }
            MarshalPlanRenderer.RenderStatement(writer, stmt);
        }
        writer.WriteLine($"IntPtr {bufferName} = {swiftVarName}.Payload.DangerousGetHandle();");
    }
}
