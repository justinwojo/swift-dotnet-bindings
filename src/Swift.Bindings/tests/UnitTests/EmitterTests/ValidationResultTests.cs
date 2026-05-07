// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ValidationResult"/> — the four-way classification used by
/// <see cref="MemberValidationPipeline.ValidateMethodEmission"/>:
/// <list type="bullet">
///   <item><c>Emit</c> — member is supported and should be emitted.</item>
///   <item><c>Skip</c> — member is unsupported (carries a <see cref="SkipReason"/>).
///     Consumers must emit a <c>// Unsupported:</c> comment and record as skipped.</item>
///   <item><c>Synthesized</c> — member is a synthesized protocol conformance (e.g.
///     <c>hash(into:)</c> for <c>Hashable</c>). Consumers must call
///     <see cref="ReportCollector.RecordMemberSynthesized"/>, NOT the unsupported emitter.</item>
///   <item><c>RoutedElsewhere</c> — open-form member is intentionally suppressed because
///     concrete specializations (CSM-async per-conformer overloads, CSM-sync generic-parent
///     extensions) provide the public surface. Consumers must NOT emit <c>// Unsupported:</c>
///     and MUST NOT record as a skipped/degraded member.</item>
/// </list>
/// The four classifications are mutually exclusive — <see cref="ValidationResult.IsSynthesized"/>
/// and <see cref="ValidationResult.IsRoutedElsewhere"/> never coexist on the same result.
/// </summary>
public class ValidationResultTests
{
    [Fact]
    public void Emit_HasShouldEmitTrue_AllOtherFlagsFalse()
    {
        var result = ValidationResult.Emit;

        Assert.True(result.ShouldEmit);
        Assert.Null(result.Reason);
        Assert.Null(result.Details);
        Assert.False(result.IsSynthesized);
        Assert.False(result.IsRoutedElsewhere);
    }

    [Fact]
    public void Skip_CarriesReasonAndDetails_NotSynthesizedNotRouted()
    {
        var result = ValidationResult.Skip(SkipReason.UnsupportedSignature, "tuple of unsupported");

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.UnsupportedSignature, result.Reason);
        Assert.Equal("tuple of unsupported", result.Details);
        Assert.False(result.IsSynthesized);
        Assert.False(result.IsRoutedElsewhere);
    }

    [Fact]
    public void Synthesized_CarriesDetailsOnly_NoReason_NotRouted()
    {
        var result = ValidationResult.Synthesized("Synthesized Hashable conformance.");

        Assert.False(result.ShouldEmit);
        Assert.Null(result.Reason);
        Assert.Equal("Synthesized Hashable conformance.", result.Details);
        Assert.True(result.IsSynthesized);
        Assert.False(result.IsRoutedElsewhere);
    }

    [Fact]
    public void RoutedElsewhere_CarriesDetailsOnly_NoReason_NotSynthesized()
    {
        var result = ValidationResult.RoutedElsewhere("Routed to concrete CSM-async specialization.");

        Assert.False(result.ShouldEmit);
        Assert.Null(result.Reason);
        Assert.Equal("Routed to concrete CSM-async specialization.", result.Details);
        Assert.False(result.IsSynthesized);
        Assert.True(result.IsRoutedElsewhere);
    }

    [Fact]
    public void RoutedElsewhere_AndSynthesized_AreMutuallyExclusive()
    {
        // Mutual exclusivity matters because consumers branch on these flags in
        // priority order: IsSynthesized → IsRoutedElsewhere → fallback Skip path.
        // If both could be true on the same result, the consumer would silently
        // enter the wrong branch.
        var routed = ValidationResult.RoutedElsewhere("routed");
        var synthesized = ValidationResult.Synthesized("synthesized");

        Assert.True(routed.IsRoutedElsewhere);
        Assert.False(routed.IsSynthesized);

        Assert.True(synthesized.IsSynthesized);
        Assert.False(synthesized.IsRoutedElsewhere);
    }

    /// <summary>
    /// Smoke test for the consumer-side branching shape used by
    /// <c>IHandler.HandleBaseDecl</c> and <c>ModuleHandler</c>: when the branching
    /// is wired the same way the production consumers wire it, a
    /// <see cref="ValidationResult.RoutedElsewhere"/> result must NOT route into
    /// <see cref="UnsupportedCommentEmitter.EmitMemberSkipped"/>. This is a pattern
    /// check, not full end-to-end coverage of the real consumers — regressions
    /// inside <c>IHandler.HandleBaseDecl</c> or <c>ModuleHandler</c> themselves
    /// (forgetting the branch entirely, reordering branches, or skipping the
    /// <c>RecordMemberSkipped</c> guard) are caught at the integration layer by the
    /// skip-surface trend gate, which observes the absence of the routed
    /// <c>// Unsupported:</c> markers in generated source.
    /// </summary>
    [Fact]
    public void ConsumerPattern_RoutedElsewhereResult_DoesNotEmitUnsupportedComment()
    {
        var result = ValidationResult.RoutedElsewhere("Routed to concrete CSM-async specialization.");

        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);

        // Mirrors the consumer pattern at IHandler.cs HandleBaseDecl + ModuleHandler.cs.
        if (!result.ShouldEmit)
        {
            if (result.IsSynthesized)
            {
                // Synthesized branch — would call ReportCollector.RecordMemberSynthesized.
            }
            else if (result.IsRoutedElsewhere)
            {
                // Routed branch — no comment, no record. The contract under test.
            }
            else
            {
                UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, "insertAsync", BindingItemKind.Method,
                    result.Reason ?? SkipReason.Unknown, result.Details);
            }
        }

        var output = sw.ToString();
        Assert.DoesNotContain("// Unsupported:", output);
        Assert.Empty(output);
    }

    /// <summary>
    /// Negative companion to the routed-pattern contract: a regular Skip result
    /// MUST still produce a <c>// Unsupported:</c> comment so the existing
    /// unsupported-method audit signal continues to work for genuinely unsupported
    /// shapes.
    /// </summary>
    [Fact]
    public void ConsumerPattern_SkipResult_StillEmitsUnsupportedComment()
    {
        var result = ValidationResult.Skip(SkipReason.UnsupportedSignature, "type not exported");

        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);

        if (!result.ShouldEmit)
        {
            if (result.IsSynthesized)
            {
                // unreachable for Skip
            }
            else if (result.IsRoutedElsewhere)
            {
                // unreachable for Skip
            }
            else
            {
                UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, "doStuff", BindingItemKind.Method,
                    result.Reason ?? SkipReason.Unknown, result.Details);
            }
        }

        var output = sw.ToString();
        Assert.Contains("// Unsupported: method 'doStuff'", output);
    }
}
