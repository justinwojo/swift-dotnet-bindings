// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Shared skip-gate for the four generic-type handlers (enum, frozen struct,
/// non-frozen struct, class). Inspects a freshly-built <see cref="PInvokeHelperContext"/>
/// for conditions that prevent emitting a correct type metadata accessor.
///
/// Two fail-closed conditions:
/// <list type="number">
/// <item><see cref="PInvokeHelperContext.HasUnsupportedConstraint"/> — a generic-parameter
/// constraint protocol is missing from the type database, or has neither a projectable C#
/// interface nor a protocol descriptor symbol that runtime witness-table lookup could
/// use.</item>
/// <item><see cref="PInvokeHelperContext.ExceedsRegisterArgumentThreshold"/> — when
/// (num_metadata + num_pwts) &gt; 3 the actual Swift <c>Ma</c> symbol uses the indirect-
/// buffer ABI, but every C# call site we emit (cctor field initializers, expression-bodied
/// <c>GetTypeMetadata()</c>, allocating-init metadata, enum case factories, raw-value
/// <c>FromRawValue</c>) still calls it with explicit per-arg parameters. Letting the type
/// through with a "wrong-but-lazy" call site only defers the PAC trap to first use, so we
/// fail closed and skip the entire type instead. Audited across the validation matrix; no
/// current library exceeds 3 metadata/PWT args. Full buffer-mode support is tracked as a
/// 0.8.0 follow-up.</item>
/// </list>
/// In either case the handler records the type as skipped, emits an <c>// Unsupported</c>
/// comment in the generated C# file, logs a warning, and returns <c>true</c> so the handler
/// can return early without emitting an invalid metadata accessor.
/// </summary>
public static class TypeMetadataAccessorSkipGate
{
    /// <summary>
    /// Returns <c>true</c> when the handler should skip emission of <paramref name="typeDecl"/>
    /// because the type's metadata accessor cannot be expressed correctly.
    /// </summary>
    public static bool ShouldSkip(
        TypeDecl typeDecl,
        PInvokeHelperContext context,
        CSharpWriter csWriter,
        ILogger logger)
    {
        if (context.HasUnsupportedConstraint)
        {
            var reason = context.UnsupportedConstraintReason ?? "unsupported generic constraint";
            ReportCollector.RecordTypeSkipped(typeDecl, SkipReason.UnsupportedType,
                $"Constrained generic type metadata accessor: {reason}");
            UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, typeDecl.Name, SkipReason.UnsupportedType,
                $"constrained generic metadata accessor: {reason}");
            logger.LogWarning(
                "Skipping type '{TypeName}' — constrained generic metadata accessor cannot be emitted: {Reason}.",
                typeDecl.Name, reason);
            return true;
        }

        if (context.ExceedsRegisterArgumentThreshold)
        {
            const string reason = "(num_metadata + num_pwts) > 3 — Swift Ma symbol uses indirect-buffer ABI, not yet supported (tracked as 0.8.0 follow-up)";
            ReportCollector.RecordTypeSkipped(typeDecl, SkipReason.UnsupportedType,
                $"Constrained generic type metadata accessor: {reason}");
            UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, typeDecl.Name, SkipReason.UnsupportedType,
                $"constrained generic metadata accessor: {reason}");
            logger.LogWarning(
                "Skipping type '{TypeName}' — constrained generic metadata accessor would require buffer-mode ABI: {Reason}.",
                typeDecl.Name, reason);
            return true;
        }

        return false;
    }
}
