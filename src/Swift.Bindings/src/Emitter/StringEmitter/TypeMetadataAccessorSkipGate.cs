// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Shared skip-gate for the four generic-type handlers (enum, frozen struct,
/// non-frozen struct, class). Inspects a freshly-built <see cref="PInvokeHelperContext"/>
/// for conditions that prevent emitting a correct type metadata accessor.
///
/// Historically the only fail-closed condition was
/// <see cref="PInvokeHelperContext.ExceedsRegisterArgumentThreshold"/>: when
/// (num_metadata + num_pwts) &gt; 3 the Swift <c>Ma</c> symbol uses the indirect-buffer
/// ABI. That case is now handled by <see cref="PInvokeHelperContext.AddMetadataAccessorDeclaration"/>,
/// which emits a buffer-mode P/Invoke plus a managed wrapper that preserves the thin-mode
/// call shape, so the gate does not skip on it.
///
/// The active condition is <see cref="PInvokeHelperContext.HasIndeterminatePwtShape"/>:
/// when the ABI describes a generic conformance the emitter cannot lower (unknown
/// protocol, or PAT/Self protocol missing a descriptor symbol), Swift's metadata
/// accessor still expects a PWT slot at that position. Silently dropping the slot can
/// push the real argument count past the indirect-buffer threshold while the emitter
/// still picks the thin-mode P/Invoke — yielding a binding that compiles but calls the
/// wrong ABI. The gate skips the type and records the skip against the binding report
/// so validation surfaces the regression instead of hiding it in a logger message.
/// </summary>
public static class TypeMetadataAccessorSkipGate
{
    /// <summary>
    /// Returns <c>true</c> when the handler should skip emission of <paramref name="typeDecl"/>
    /// because the type's metadata accessor cannot be expressed correctly. Callers MUST
    /// invoke this BEFORE <see cref="ReportCollector.RecordTypeEmitted"/>, otherwise the
    /// collector's emitted-key suppression swallows the subsequent skip record.
    /// </summary>
    public static bool ShouldSkip(
        TypeDecl typeDecl,
        PInvokeHelperContext context,
        CSharpWriter csWriter,
        ILogger logger)
    {
        if (!context.HasIndeterminatePwtShape)
            return false;

        var details = string.Join(
            "; ",
            context.UnresolvedPwtConstraints.Select(c =>
                $"{c.GenericParamCsName}: {c.ProtocolModuleQualifiedName} ({c.Reason})"));

        logger.LogWarning(
            "Skipping '{TypeName}': ABI declares {Count} generic conformance(s) the emitter " +
            "cannot lower into PWT arguments — the metadata-accessor ABI variant is therefore " +
            "indeterminate. Details: {Details}",
            typeDecl.Name, context.UnresolvedPwtConstraints.Count, details);

        ReportCollector.RecordTypeSkipped(
            typeDecl,
            SkipReason.IndeterminatePwtShape,
            details);
        UnsupportedCommentEmitter.EmitTypeSkipped(
            csWriter, typeDecl.Name, SkipReason.IndeterminatePwtShape, details,
            typeDecl.ParentDecl, DeclIdFactory.ForType(typeDecl));
        return true;
    }
}
