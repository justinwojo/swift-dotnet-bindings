// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

public class UnsupportedCommentEmitterTests
{
    [Theory]
    [InlineData(SkipReason.SwiftUIView, "SwiftUI View type")]
    [InlineData(SkipReason.UnsupportedType, "type not exported")]
    [InlineData(SkipReason.SwiftUIConstraint, "generic constraint on SwiftUI")]
    [InlineData(SkipReason.CombineFramework, "Combine framework")]
    [InlineData(SkipReason.MissingHandler, "no handler")]
    public void EmitTypeSkipped_ContainsReasonDescription(SkipReason reason, string expectedSubstring)
    {
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);

        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, "MyType", reason);

        var output = sw.ToString();
        Assert.Contains("// Unsupported: type 'MyType'", output);
        Assert.Contains(expectedSubstring, output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmitTypeSkipped_WithDetails_AppendsDetails()
    {
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);

        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, "MyView", SkipReason.SwiftUIConstraint, "generic constraint: SwiftUI.View");

        var output = sw.ToString();
        Assert.Contains("// Unsupported: type 'MyView'", output);
        Assert.Contains("(generic constraint: SwiftUI.View)", output);
    }

    [Theory]
    [InlineData(BindingItemKind.Method, "method")]
    [InlineData(BindingItemKind.Property, "property")]
    [InlineData(BindingItemKind.Operator, "operator")]
    [InlineData(BindingItemKind.Subscript, "subscript")]
    public void EmitMemberSkipped_ContainsKindLabel(BindingItemKind kind, string expectedKindLabel)
    {
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);

        UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, "doStuff", kind, SkipReason.UnsupportedSignature);

        var output = sw.ToString();
        Assert.Contains($"// Unsupported: {expectedKindLabel} 'doStuff'", output);
    }

    [Fact]
    public void EmitMemberSkipped_WithDetails_AppendsDetails()
    {
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);

        UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, "fetch", BindingItemKind.Method, SkipReason.UnsupportedClosure, "closure param T -> Void");

        var output = sw.ToString();
        Assert.Contains("// Unsupported: method 'fetch'", output);
        Assert.Contains("(closure param T -> Void)", output);
    }

    [Fact]
    public void EmitMemberSkipped_DuplicateSignature_ContainsDescription()
    {
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);

        UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, "process", BindingItemKind.Method, SkipReason.DuplicateSignature);

        var output = sw.ToString();
        Assert.Contains("// Unsupported: method 'process'", output);
        Assert.Contains("C# signature collides", output);
    }

    [Fact]
    public void EmitTypeSkipped_NullDetails_NoParentheses()
    {
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);

        UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, "MyType", SkipReason.UnderscorePrefixInternal);

        var output = sw.ToString();
        Assert.Contains("// Unsupported: type 'MyType'", output);
        Assert.DoesNotContain("()", output);
    }
}
