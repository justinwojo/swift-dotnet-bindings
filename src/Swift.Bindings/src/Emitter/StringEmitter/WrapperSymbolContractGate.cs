// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Centralizes how binding-emit reacts when the in-band wrapper-symbol contract
/// trips inside <see cref="PInvokeEmitter.EmitPInvoke"/> /
/// <see cref="PInvokeEmitHelper.FormatDeclarationLines"/>.
/// </summary>
/// <remarks>
/// Wrapper-emit registers each SBW_… symbol it produces in
/// <see cref="ModuleEmissionContext"/>. PInvokeEmitter checks that registry
/// before emitting a wrapper-targeting P/Invoke and throws
/// <see cref="WrapperSymbolContractException"/> when the symbol is missing —
/// the failure shape behind the 0.10.0 bugs where binding-emit referenced a
/// symbol that wrapper-emit silently dropped.
///
/// Method/Constructor handlers wrap wrapper-emit + pinvoke-emit in a try/catch
/// for that exception; on catch they call <see cref="HandleViolation"/>, which:
/// <list type="bullet">
///   <item>Records the skip on <see cref="ReportCollector"/> with
///   <see cref="SkipReason.MissingWrapperSymbol"/>, so the binding-emission
///   report and report-projection pick it up.</item>
///   <item>Records the violated entry point and C# P/Invoke method name on
///   <see cref="ModuleEmissionContext.RecordContractViolation"/> so the post-emit
///   cogater can strip the orphan caller bodies the wrapper-emit pass already
///   wrote to the output buffer before the throw fired.</item>
///   <item>Writes an <c>// Unsupported: …</c> marker so the generated source
///   documents the omission.</item>
///   <item>Emits a structured warning into the generator log.</item>
/// </list>
/// </remarks>
internal static class WrapperSymbolContractGate
{
    /// <summary>
    /// Records and surfaces a contract violation. Callers must <c>return</c>
    /// from their handler immediately after invoking this — the rest of the
    /// emit path (post-processors, RecordMemberEmitted, …) must not run for
    /// a member that won't compile.
    /// </summary>
    public static void HandleViolation(
        MethodEnvironment methodEnv,
        WrapperSymbolContractException exception,
        CSharpWriter csWriter,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(methodEnv);
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(csWriter);

        var methodDecl = (MethodDecl)methodEnv.MethodDecl;
        var details = $"wrapper symbol '{exception.EntryPoint}' not registered by wrapper-emit";

        UnsupportedCommentEmitter.EmitMemberSkipped(
            csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.MissingWrapperSymbol, details, containingDecl: methodDecl.ParentDecl);
        ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.MissingWrapperSymbol, details);
        // Stash both shapes of the rejected symbol + the qualified containing-type
        // path on the module's emission context so the post-emit cogater can find
        // every caller that already emitted before the contract throw fired AND
        // restrict the strip to the violated scope (a same-named P/Invoke in
        // another scope must survive).
        var containingType = BuildContainingTypePath(methodEnv.ParentDecl, methodEnv.TypeDatabase);
        methodEnv.EmissionContext?.RecordContractViolation(
            exception.EntryPoint, exception.MethodName, containingType);
        logger.LogWarning(
            "Wrapper-symbol contract: skipping member '{Member}' on '{Parent}' — wrapper symbol '{Symbol}' not registered.",
            methodDecl.Name, containingType, exception.EntryPoint);
    }

    /// <summary>
    /// Walks the parent decl chain to build a dotted qualified-type path that matches
    /// what <c>CSharpWrapperCoGater.BuildLineToTypeMap</c> reads off the emitted C#
    /// source (e.g., <c>"Transaction.AsyncIterator"</c>). Each segment is the emitted
    /// C# leaf name, not <c>TypeDecl.Name</c>: nested-collision renames stored on
    /// <c>TypeRecord.CSharpTypeName</c> and the PascalCase transform applied by
    /// <c>NameProvider.ToPascalCaseForTypeName</c> would otherwise leave the scope
    /// string mismatched with the actual C# class declaration, causing the
    /// scope-restricted strip to silently miss the orphan caller. Returns null when
    /// there is no enclosing type — defensive; the contract path always fires inside
    /// a type.
    /// </summary>
    internal static string? BuildContainingTypePath(BaseDecl? parentDecl, ITypeDatabase typeDatabase)
    {
        if (parentDecl is not TypeDecl)
            return null;

        var segments = new List<string>();
        BaseDecl? current = parentDecl;
        while (current is TypeDecl td)
        {
            segments.Add(ResolveEmittedCSharpLeafName(td, typeDatabase));
            current = td.ParentDecl;
        }
        segments.Reverse();
        return string.Join(".", segments);
    }

    /// <summary>
    /// Resolves a single C# leaf name for a Swift <see cref="TypeDecl"/>. Mirrors the
    /// resolution used by <c>ModuleEmitter</c> when qualifying namespace references:
    /// prefer <c>TypeRecord.CSharpTypeName.Name</c> (which captures both module-level
    /// renames and nested-collision renames like <c>Connection → ConnectionType</c>),
    /// taking the last dotted segment if the record stores a composite path. Falls
    /// back to PascalCase of the Swift name when no record is registered for the type.
    /// </summary>
    private static string ResolveEmittedCSharpLeafName(TypeDecl td, ITypeDatabase typeDatabase)
    {
        if (typeDatabase.TryGetTypeRecord(td.SwiftTypeName, out var record))
        {
            var csName = record.CSharpTypeName.Name;
            int lastDot = csName.LastIndexOf('.');
            return lastDot >= 0 ? csName.Substring(lastDot + 1) : csName;
        }
        return NameProvider.ToPascalCaseForTypeName(td.Name);
    }
}
