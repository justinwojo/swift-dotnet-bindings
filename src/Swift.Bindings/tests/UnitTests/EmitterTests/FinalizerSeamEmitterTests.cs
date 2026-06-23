// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the single source of the <c>ISwiftObject.SuppressPayloadFinalizer</c> override line shared by
/// the class / frozen-struct / non-frozen-struct / enum / Apple-manifest emitters. The byte-identical
/// assertions are the per-emitter field-name correctness guard (the inline literals each handler used
/// before centralization); the fail-closed assertions are the hardening — an unrecognized seam field
/// must abort generation rather than emit a SuppressFinalize on a field the type never declares.
/// </summary>
public class FinalizerSeamEmitterTests
{
    [Fact]
    public void SuppressPayloadFinalizerLine_HandleField_MatchesClassWrapperLiteral()
    {
        // Byte-identical to ClassHandler's former inline literal (class wrappers own _handle).
        Assert.Equal(
            "void ISwiftObject.SuppressPayloadFinalizer() => GC.SuppressFinalize(_handle);",
            FinalizerSeamEmitter.SuppressPayloadFinalizerLine("_handle"));
    }

    [Fact]
    public void SuppressPayloadFinalizerLine_PayloadField_MatchesValueTypeWrapperLiteral()
    {
        // Byte-identical to the frozen/non-frozen struct + enum handlers' former inline literal
        // (value-type wrappers own _payload).
        Assert.Equal(
            "void ISwiftObject.SuppressPayloadFinalizer() => GC.SuppressFinalize(_payload);",
            FinalizerSeamEmitter.SuppressPayloadFinalizerLine("_payload"));
    }

    [Fact]
    public void SuppressPayloadFinalizerLine_QualifiedGc_MatchesAppleManifestLiteral()
    {
        // The Apple value-type manifest emitter writes outside a `using System;` context, so it takes
        // the fully-qualified global::System.GC form. Byte-identical to its former inline literal.
        Assert.Equal(
            "void ISwiftObject.SuppressPayloadFinalizer() => global::System.GC.SuppressFinalize(_payload);",
            FinalizerSeamEmitter.SuppressPayloadFinalizerLine("_payload", qualifyGc: true));
    }

    [Theory]
    [InlineData("_handle")]
    [InlineData("_payload")]
    public void SuppressPayloadFinalizerLine_KnownField_DoesNotThrow(string field)
    {
        var line = FinalizerSeamEmitter.SuppressPayloadFinalizerLine(field);
        Assert.Contains($"SuppressFinalize({field});", line);
    }

    [Theory]
    [InlineData("_state")]
    [InlineData("payload")]   // missing the leading underscore
    [InlineData("_Handle")]   // wrong case
    [InlineData("")]
    public void SuppressPayloadFinalizerLine_UnknownField_FailsClosed(string field)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => FinalizerSeamEmitter.SuppressPayloadFinalizerLine(field));
        Assert.Contains("SWIFTBIND048", ex.Message);
        Assert.Contains("SuppressPayloadFinalizer", ex.Message);
    }

    [Fact]
    public void SuppressPayloadFinalizerLine_NullField_FailsClosed()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => FinalizerSeamEmitter.SuppressPayloadFinalizerLine(null));
        Assert.Contains("SWIFTBIND048", ex.Message);
    }
}
