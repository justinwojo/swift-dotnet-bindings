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
/// call shape, so the gate no longer skips on it. The gate is retained so handlers have a
/// single, opt-in hook for any future fail-closed metadata-accessor conditions.
/// </summary>
public static class TypeMetadataAccessorSkipGate
{
    /// <summary>
    /// Returns <c>true</c> when the handler should skip emission of <paramref name="typeDecl"/>
    /// because the type's metadata accessor cannot be expressed correctly. No conditions
    /// fire today.
    /// </summary>
    public static bool ShouldSkip(
        TypeDecl typeDecl,
        PInvokeHelperContext context,
        CSharpWriter csWriter,
        ILogger logger)
    {
        return false;
    }
}
