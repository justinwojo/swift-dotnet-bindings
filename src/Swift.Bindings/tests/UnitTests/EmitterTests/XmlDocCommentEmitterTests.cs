// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.CodeDom.Compiler;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

public class XmlDocCommentEmitterTests
{
    [Fact]
    public void EmitDocComment_SummaryOnly()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateBaseDecl(new DocComment { Summary = "A simple type." });

        XmlDocCommentEmitter.EmitDocComment(csWriter, decl);

        var output = stringWriter.ToString();
        Assert.Contains("/// <summary>", output);
        Assert.Contains("/// A simple type.", output);
        Assert.Contains("/// </summary>", output);
    }

    [Fact]
    public void EmitDocComment_NullDocumentation_NoOutput()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateBaseDecl(null);

        XmlDocCommentEmitter.EmitDocComment(csWriter, decl);

        Assert.Empty(stringWriter.ToString());
    }

    [Fact]
    public void EmitDocComment_EmptyDocumentation_NoOutput()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateBaseDecl(new DocComment());

        XmlDocCommentEmitter.EmitDocComment(csWriter, decl);

        Assert.Empty(stringWriter.ToString());
    }

    [Fact]
    public void EmitDocComment_WithRemarks_MultipleUsesPara()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateBaseDecl(new DocComment
        {
            Summary = "A type.",
            Remarks = new List<string> { "Note: This is important.", "Warning: Handle with care." }
        });

        XmlDocCommentEmitter.EmitDocComment(csWriter, decl);

        var output = stringWriter.ToString();
        Assert.Contains("/// <remarks>", output);
        Assert.Contains("/// <para>Note: This is important.</para>", output);
        Assert.Contains("/// <para>Warning: Handle with care.</para>", output);
        Assert.Contains("/// </remarks>", output);
    }

    [Fact]
    public void EmitDocComment_SingleRemark_NoPara()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var decl = CreateBaseDecl(new DocComment
        {
            Summary = "A type.",
            Remarks = new List<string> { "Note: Single remark." }
        });

        XmlDocCommentEmitter.EmitDocComment(csWriter, decl);

        var output = stringWriter.ToString();
        Assert.Contains("/// <remarks>", output);
        Assert.Contains("/// Note: Single remark.", output);
        Assert.DoesNotContain("<para>", output);
        Assert.Contains("/// </remarks>", output);
    }

    [Fact]
    public void EmitMethodDocComment_ThrowsAndRemarks_UsesPara()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var methodDecl = CreateMethodDecl(
            new DocComment
            {
                Summary = "Does work.",
                Throws = "Error on failure.",
                Remarks = new List<string> { "Note: Requires setup." }
            });

        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl);

        var output = stringWriter.ToString();
        Assert.Contains("/// <remarks>", output);
        Assert.Contains("/// <para>Throws: Error on failure.</para>", output);
        Assert.Contains("/// <para>Note: Requires setup.</para>", output);
        Assert.Contains("/// </remarks>", output);
    }

    [Fact]
    public void EmitMethodDocComment_WithParams()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var methodDecl = CreateMethodDecl(
            new DocComment
            {
                Summary = "Adds two numbers.",
                Parameters = new Dictionary<string, string>
                {
                    { "lhs", "The left operand." },
                    { "rhs", "The right operand." }
                },
                Returns = "The sum."
            },
            ("lhs", "lhs", null),
            ("rhs", "rhs", null));

        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl);

        var output = stringWriter.ToString();
        Assert.Contains("/// <summary>", output);
        Assert.Contains("/// Adds two numbers.", output);
        Assert.Contains("/// <param name=\"lhs\">The left operand.</param>", output);
        Assert.Contains("/// <param name=\"rhs\">The right operand.</param>", output);
        Assert.Contains("/// <returns>The sum.</returns>", output);
    }

    [Fact]
    public void EmitMethodDocComment_Constructor_SuppressesReturns()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var methodDecl = CreateMethodDecl(
            new DocComment
            {
                Summary = "Creates a new instance.",
                Returns = "A new instance."
            });

        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl, isConstructor: true);

        var output = stringWriter.ToString();
        Assert.Contains("/// <summary>", output);
        Assert.DoesNotContain("<returns>", output);
    }

    [Fact]
    public void EmitMethodDocComment_FailableFactory_MapsReturnsToResultParam()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var methodDecl = CreateMethodDecl(
            new DocComment
            {
                Summary = "Tries to create an instance.",
                Returns = "The created instance if valid."
            });

        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl, isFailableFactory: true);

        var output = stringWriter.ToString();
        Assert.Contains("/// <param name=\"result\">The created instance if valid.</param>", output);
        Assert.DoesNotContain("<returns>", output);
    }

    [Fact]
    public void EmitMethodDocComment_WithThrows()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var methodDecl = CreateMethodDecl(
            new DocComment
            {
                Summary = "Parses input.",
                Throws = "ParseError if input is malformed."
            });

        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl);

        var output = stringWriter.ToString();
        Assert.Contains("/// <remarks>", output);
        Assert.Contains("/// Throws: ParseError if input is malformed.", output);
        Assert.Contains("/// </remarks>", output);
    }

    [Fact]
    public void EmitMethodDocComment_FailableFactory_NoDuplicateResultParam()
    {
        // If a Swift init? has a parameter that maps to C# "result",
        // the synthetic <param name="result"> from Returns should be skipped
        var (csWriter, stringWriter) = CreateWriter();
        var methodDecl = CreateMethodDecl(
            new DocComment
            {
                Summary = "Tries to create.",
                Parameters = new Dictionary<string, string>
                {
                    { "result", "The result parameter." }
                },
                Returns = "The created instance."
            },
            ("result", "result", null));

        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl, isFailableFactory: true);

        var output = stringWriter.ToString();
        // Should have exactly one <param name="result"> from the mapped parameter
        var count = output.Split("/// <param name=\"result\">").Length - 1;
        Assert.Equal(1, count);
        // The mapped parameter description wins, not the Returns text
        Assert.Contains("/// <param name=\"result\">The result parameter.</param>", output);
    }

    [Fact]
    public void FormatDocText_XmlEscaping()
    {
        var result = XmlDocCommentEmitter.FormatDocText("Use List<T> & Dictionary<K, V>.");
        Assert.Equal("Use List&lt;T&gt; &amp; Dictionary&lt;K, V&gt;.", result);
    }

    [Fact]
    public void FormatDocText_BacktickToCodeTag()
    {
        var result = XmlDocCommentEmitter.FormatDocText("Use `String` and `Int` types.");
        Assert.Equal("Use <c>String</c> and <c>Int</c> types.", result);
    }

    [Fact]
    public void FormatDocText_BacktickWithXmlChars()
    {
        // Content inside backticks should be XML-escaped, but <c> tags preserved
        var result = XmlDocCommentEmitter.FormatDocText("Returns `Array<Int>` value.");
        Assert.Equal("Returns <c>Array&lt;Int&gt;</c> value.", result);
    }

    [Fact]
    public void XmlEscape_AllSpecialChars()
    {
        var result = XmlDocCommentEmitter.XmlEscape("a & b < c > d \"e\"");
        Assert.Equal("a &amp; b &lt; c &gt; d &quot;e&quot;", result);
    }

    [Fact]
    public void EmitMethodDocComment_ParamNameMapping_SwiftLabelToCSharp()
    {
        // Swift doc uses public label "to", C# parameter name resolves to "other" (via PrivateName)
        var (csWriter, stringWriter) = CreateWriter();
        var methodDecl = CreateMethodDecl(
            new DocComment
            {
                Summary = "Compares.",
                Parameters = new Dictionary<string, string>
                {
                    { "to", "The other value." }
                }
            },
            ("to", "to", "other"));

        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl);

        var output = stringWriter.ToString();
        // NameProvider.GetCSharpParameterName prefers PrivateName, so the param name should be "other"
        Assert.Contains("/// <param name=\"other\">The other value.</param>", output);
    }

    [Fact]
    public void EmitMethodDocComment_UnlabeledArgFallback()
    {
        // Swift param labeled "_" with private name "value", doc uses "value" (private name)
        // NameProvider.SanitizeForCSharp("value") → "_value" since "value" is a C# contextual keyword
        var (csWriter, stringWriter) = CreateWriter();
        var methodDecl = CreateMethodDecl(
            new DocComment
            {
                Summary = "Processes a value.",
                Parameters = new Dictionary<string, string>
                {
                    { "value", "The value to process." }
                }
            },
            ("_", "_", "value"));

        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl);

        var output = stringWriter.ToString();
        // "value" matches via PrivateName fallback, C# name is "_value" (sanitized keyword)
        Assert.Contains("/// <param name=\"_value\">The value to process.</param>", output);
    }

    [Fact]
    public void EmitMethodDocComment_UnmatchedParam_Dropped()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var methodDecl = CreateMethodDecl(
            new DocComment
            {
                Summary = "Does something.",
                Parameters = new Dictionary<string, string>
                {
                    { "nonexistent", "This param doesn't exist in the signature." }
                }
            },
            ("x", "x", null));

        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl);

        var output = stringWriter.ToString();
        Assert.DoesNotContain("<param name=\"nonexistent\">", output);
    }

    [Fact]
    public void EmitDocComment_FullDocComment()
    {
        var (csWriter, stringWriter) = CreateWriter();
        var methodDecl = CreateMethodDecl(
            new DocComment
            {
                Summary = "Fetches data from a `URL`.",
                Parameters = new Dictionary<string, string>
                {
                    { "url", "The `URL` to fetch." }
                },
                Returns = "The fetched `Data`.",
                Throws = "`NetworkError` if the request fails.",
                Remarks = new List<string> { "Note: Requires network access." }
            },
            ("url", "url", null));

        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl);

        var output = stringWriter.ToString();
        Assert.Contains("/// <summary>", output);
        Assert.Contains("Fetches data from a <c>URL</c>.", output);
        Assert.Contains("<param name=\"url\">The <c>URL</c> to fetch.</param>", output);
        Assert.Contains("<returns>The fetched <c>Data</c>.</returns>", output);
        Assert.Contains("Throws: <c>NetworkError</c> if the request fails.", output);
        Assert.Contains("Note: Requires network access.", output);
    }

    // --- Helpers ---

    private static (CSharpWriter csWriter, StringWriter stringWriter) CreateWriter()
    {
        var stringWriter = new StringWriter();
        var csWriter = new CSharpWriter(stringWriter);
        return (csWriter, stringWriter);
    }

    private static BaseDecl CreateBaseDecl(DocComment? doc)
    {
        return new StructDecl
        {
            Name = "TestType",
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestType"),
            MangledName = "$sTestTypeVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Conformances = new List<TypeConformance>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            MetadataAccessor = "",
            Documentation = doc
        };
    }

    private static MethodDecl CreateMethodDecl(DocComment doc, params (string name, string publicLabel, string? privateName)[] args)
    {
        var csSignature = new List<ArgumentDecl>();

        // CSSignature[0] is the return type
        csSignature.Add(new ArgumentDecl
        {
            Name = "",
            PrivateName = "",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = new TupleTypeSpec(),
            IsInOut = false,
            IsGeneric = false
        });

        foreach (var (name, publicLabel, privateName) in args)
        {
            csSignature.Add(new ArgumentDecl
            {
                Name = publicLabel,
                PrivateName = privateName ?? "",
                ParentDecl = null,
                ModuleDecl = null,
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                IsInOut = false,
                IsGeneric = false
            });
        }

        return new MethodDecl
        {
            Name = "testMethod",
            MangledName = "$sTestMethod",
            ParentDecl = null,
            ModuleDecl = null,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Visibility = Visibility.Public,
            Documentation = doc
        };
    }
}
