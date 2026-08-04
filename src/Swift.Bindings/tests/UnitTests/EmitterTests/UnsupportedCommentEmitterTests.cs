// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
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

    [Fact]
    public void EmitMemberSkipped_WithContainingType_QualifiesMemberName()
    {
        // Finding 53 (Codex Low): a non-module containing decl qualifies the member as Type.member,
        // both in the human-readable comment and (downstream) the SWIFTBIND025 dedup key.
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        var owner = new BaseDecl { Name = "Loader", ParentDecl = null, ModuleDecl = null };

        UnsupportedCommentEmitter.EmitMemberSkipped(
            csWriter, "fetch", BindingItemKind.Method, SkipReason.UnsupportedSignature, containingDecl: owner);

        var output = sw.ToString();
        Assert.Contains("// Unsupported: method 'Loader.fetch'", output);
        Assert.DoesNotContain("'fetch'", output); // never the bare, unqualified form
    }

    [Fact]
    public void EmitMemberSkipped_WithModuleDeclParent_StaysUnqualified()
    {
        // A module-level free function has no declaring type and cannot name-collide across types,
        // so a ModuleDecl parent is treated as "no containing type" — the member stays unqualified.
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        var module = NewModuleDecl();

        UnsupportedCommentEmitter.EmitMemberSkipped(
            csWriter, "topLevelFunc", BindingItemKind.Method, SkipReason.UnsupportedSignature, containingDecl: module);

        var output = sw.ToString();
        Assert.Contains("// Unsupported: method 'topLevelFunc'", output);
        Assert.DoesNotContain("'TestModule.topLevelFunc'", output);
    }

    [Fact]
    public void EmitMemberSkipped_NestedContainingType_UsesFullPath()
    {
        // Finding 53 (Codex round 2): qualify by the FULL declaring-type path so a member of
        // A.Inner stays distinct from one of B.Inner. The walk stops at the module.
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        var module = NewModuleDecl();
        var outer = new BaseDecl { Name = "Outer", ParentDecl = module, ModuleDecl = module };
        var inner = new BaseDecl { Name = "Inner", ParentDecl = outer, ModuleDecl = module };

        UnsupportedCommentEmitter.EmitMemberSkipped(
            csWriter, "foo", BindingItemKind.Method, SkipReason.UnsupportedSignature, containingDecl: inner);

        var output = sw.ToString();
        Assert.Contains("// Unsupported: method 'Outer.Inner.foo'", output);
    }

    [Fact]
    public void EmitMemberSkipped_NullContainingDecl_StaysUnqualified()
    {
        // Backward-compatible default: a null containing decl leaves the member unqualified.
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);

        UnsupportedCommentEmitter.EmitMemberSkipped(
            csWriter, "doStuff", BindingItemKind.Method, SkipReason.UnsupportedSignature);

        var output = sw.ToString();
        Assert.Contains("// Unsupported: method 'doStuff'", output);
    }

    [Fact]
    public void EmitMemberSkipped_IdenticalRepeat_EmitsOneComment()
    {
        // A property declared in two constrained extensions of the same generic type skips twice
        // with identical reason and details; the file must carry ONE tombstone, not a stutter.
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        var owner = new BaseDecl { Name = "Witness", ParentDecl = null, ModuleDecl = null };

        UnsupportedCommentEmitter.EmitMemberSkipped(
            csWriter, "markerLabel", BindingItemKind.Property, SkipReason.UnsupportedType,
            "suppressed at the open-generic class level", containingDecl: owner);
        UnsupportedCommentEmitter.EmitMemberSkipped(
            csWriter, "markerLabel", BindingItemKind.Property, SkipReason.UnsupportedType,
            "suppressed at the open-generic class level", containingDecl: owner);

        csWriter.Flush();
        var occurrences = CountOccurrences(sw.ToString(), "// Unsupported: property 'Witness.markerLabel'");
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void EmitMemberSkipped_SameMemberDifferentDetails_EmitsBoth()
    {
        // Distinct details are distinct information — only exact repeats collapse.
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        var owner = new BaseDecl { Name = "Witness", ParentDecl = null, ModuleDecl = null };

        UnsupportedCommentEmitter.EmitMemberSkipped(
            csWriter, "markerLabel", BindingItemKind.Property, SkipReason.UnsupportedType,
            "first detail", containingDecl: owner);
        UnsupportedCommentEmitter.EmitMemberSkipped(
            csWriter, "markerLabel", BindingItemKind.Property, SkipReason.UnsupportedType,
            "second detail", containingDecl: owner);

        csWriter.Flush();
        var occurrences = CountOccurrences(sw.ToString(), "// Unsupported: property 'Witness.markerLabel'");
        Assert.Equal(2, occurrences);
    }

    [Fact]
    public void EmitMemberSkipped_RepeatAfterRollback_ReEmits()
    {
        // A rollback erases the first tombstone from the buffer; the dedup must notice and let the
        // second emission through, or the member loses its marker entirely.
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        var owner = new BaseDecl { Name = "Witness", ParentDecl = null, ModuleDecl = null };

        var checkpoint = csWriter.Checkpoint();
        UnsupportedCommentEmitter.EmitMemberSkipped(
            csWriter, "markerLabel", BindingItemKind.Property, SkipReason.UnsupportedType,
            containingDecl: owner);
        csWriter.RollbackTo(checkpoint);
        UnsupportedCommentEmitter.EmitMemberSkipped(
            csWriter, "markerLabel", BindingItemKind.Property, SkipReason.UnsupportedType,
            containingDecl: owner);

        csWriter.Flush();
        var occurrences = CountOccurrences(sw.ToString(), "// Unsupported: property 'Witness.markerLabel'");
        Assert.Equal(1, occurrences);
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static ModuleDecl NewModuleDecl() => new()
    {
        Name = "TestModule",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null
    };
}
