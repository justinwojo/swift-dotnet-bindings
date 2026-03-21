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
        // Collections always need pointer override
        if (projection is ArrayProjection or DictionaryProjection or SetProjection)
            return true;

        if (projection is not OptionalProjection optProj)
            return false;

        // Optional<Collection> needs override
        if (optProj.InnerProjection is ArrayProjection or DictionaryProjection or SetProjection)
            return true;

        // Optional<ReferenceType> uses nullable pointer ABI — no override needed
        if (optProj.InnerProjection is ClassProjection or ObjCBridgedProjection or ObjCRootedClassProjection)
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
    internal static void RenderWithHandleOverride(CSharpWriter writer, MarshalPlan plan, string variableName)
    {
        var bufferName = NameProvider.GetBoundGenericBufferName(variableName);
        foreach (var stmt in plan.SetupStatements)
        {
            // Skip PayloadBuffer and .Buffer lines — replace with DangerousGetHandle
            if (stmt is MarshalStatement.Using u && u.Type.StartsWith("PayloadBuffer"))
                continue;
            if (stmt is MarshalStatement.Line l && l.Code.Contains("Disposable.Buffer"))
                continue;
            MarshalPlanRenderer.RenderStatement(writer, stmt);
        }
        writer.WriteLine($"IntPtr {bufferName} = {variableName}Swift.Payload.DangerousGetHandle();");
    }
}
