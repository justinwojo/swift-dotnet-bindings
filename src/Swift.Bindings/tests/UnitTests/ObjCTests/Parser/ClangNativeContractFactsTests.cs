// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.Json;
using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

public class ClangNativeContractFactsTests
{
    const string Headers = "/framework/Headers";

    [Theory]
    [InlineData("9223372036854775808", "unsigned long long", 9223372036854775808UL, true)]
    [InlineData("18446744073709551615", "unsigned long long", ulong.MaxValue, true)]
    [InlineData("-9223372036854775808", "long long", 9223372036854775808UL, false)]
    [InlineData("42", "int", 42UL, false)]
    public void EvaluatedDecimal_RecordsBitsAndSignedness(string value, string type, ulong bits, bool unsigned)
    {
        // Clang emits evaluated decimal ConstantExpr values even when source uses shifts/~.
        var json = $$$"""
        { "kind":"TranslationUnitDecl", "inner":[
          { "kind":"EnumDecl", "name":"ContractEnum", "loc":{"file":"/framework/Headers/Test.h"},
            "fixedUnderlyingType":{"qualType":"{{{type}}}"}, "inner":[
              {"kind":"EnumConstantDecl","name":"ContractEnumValue","inner":[
                {"kind":"ConstantExpr","type":{"qualType":"{{{type}}}"},"value":"{{{value}}}",
                 "inner":[{"kind":"BinaryOperator","inner":[{"kind":"IntegerLiteral","value":"1"}]}]}]},
              {"kind":"EnumConstantDecl","name":"ContractEnumImplicit"} ]} ]}
        """;
        var e = ClangAstParser.Parse(json, "Contracts", Headers).Enums.Single();
        Assert.Equal(new ObjCIntegerValue(bits, unsigned), e.Cases[0].EvaluatedValue);
        Assert.True(e.Cases[0].HasExplicitValue);
        Assert.False(e.Cases[1].HasExplicitValue);
        Assert.Null(e.Cases[1].EvaluatedValue);
    }

    [Fact]
    public void EvaluatedUnsignedAlias_UsesDesugaredSignedness()
    {
        var json = """
        {"kind":"TranslationUnitDecl","inner":[
          {"kind":"EnumDecl","name":"ContractEnum","loc":{"file":"/framework/Headers/Test.h"},"inner":[
            {"kind":"EnumConstantDecl","name":"ContractEnumAll","inner":[
              {"kind":"ConstantExpr","value":"18446744073709551615",
               "type":{"qualType":"CustomMask","desugaredQualType":"unsigned long long"}}]}]}]}
        """;
        var value = ClangAstParser.Parse(json, "Contracts", Headers).Enums.Single().Cases.Single().EvaluatedValue;
        Assert.Equal(new ObjCIntegerValue(ulong.MaxValue, true), value);
    }

    [Fact]
    public void UnknownEvaluation_DoesNotUseExpressionOperandOrImplicitValue()
    {
        var json = """
        { "kind":"TranslationUnitDecl", "inner":[
          { "kind":"EnumDecl", "name":"ContractEnum", "loc":{"file":"/framework/Headers/Test.h"}, "inner":[
              {"kind":"EnumConstantDecl","name":"ContractEnumUnknown","inner":[
                {"kind":"ConstantExpr","value":"not-evaluated","inner":[{"kind":"IntegerLiteral","value":"1"}]}]} ]} ]}
        """;
        var e = ClangAstParser.Parse(json, "Contracts", Headers).Enums.Single();
        Assert.True(e.Cases[0].HasExplicitValue);
        Assert.Null(e.Cases[0].EvaluatedValue);
        Assert.Null(e.Cases[0].Value);
    }

    [Theory]
    [InlineData("PackedAttr", true, false)]
    [InlineData("MaxFieldAlignmentAttr", true, false)]
    [InlineData("AlignedAttr", false, true)]
    public void RecordAttributesAndMeasuredLayout_SurviveNamedAndAnonymousTypedef(string attribute, bool packed, bool aligned)
    {
        foreach (var anonymous in new[] { false, true })
        {
            var name = anonymous ? "" : "\"name\":\"ContractRecord\",";
            var typedef = anonymous ? """
                , {"kind":"TypedefDecl","name":"ContractRecord","loc":{"file":"/framework/Headers/Test.h"},
                   "type":{"qualType":"struct ContractRecord"}}
                """ : "";
            var json = $$$"""
            {"kind":"TranslationUnitDecl","inner":[
              {"kind":"RecordDecl",{{{name}}}"tagUsed":"struct","loc":{"file":"/framework/Headers/Test.h","line":4,"col":9},
               "inner":[{"kind":"{{{attribute}}}"},
                        {"kind":"FieldDecl","name":"tag","type":{"qualType":"unsigned char"}},
                        {"kind":"FieldDecl","name":"value","type":{"qualType":"unsigned int"}}]}
              {{{typedef}}} ]}
            """;
            var nativeName = anonymous ? "struct (unnamed at /framework/Headers/Test.h:4:9)" : "struct ContractRecord";
            var layouts = $$$"""
            *** Dumping AST Record Layout
            Type: {{{nativeName}}}
            Layout: <ASTRecordLayout
              Size:40
              DataSize:40
              Alignment:8
              FieldOffsets: [0, 8]>
            """;
            var record = ClangAstParser.Parse(json, "Contracts", Headers, recordLayouts: layouts).Structs.Single();
            Assert.Equal("ContractRecord", record.Name);
            Assert.Equal(packed, record.IsPacked);
            Assert.Equal(aligned, record.HasExplicitAlignment);
            Assert.NotNull(record.NativeLayout);
            Assert.Equal(40, record.NativeLayout.SizeBits);
            Assert.Equal(8, record.NativeLayout.AlignmentBits);
            Assert.Equal(new long[] { 0, 8 }, record.NativeLayout.FieldOffsetsBits);
        }
    }

    [Fact]
    public void FieldAlignmentAndBitfieldFacts_AreRetained()
    {
        var json = """
        {"kind":"TranslationUnitDecl","inner":[
          {"kind":"RecordDecl","name":"ContractBits","loc":{"file":"/framework/Headers/Test.h"},
           "inner":[{"kind":"FieldDecl","name":"bits","isBitfield":true,"type":{"qualType":"unsigned int"}},
                    {"kind":"FieldDecl","name":"value","type":{"qualType":"int"},"inner":[{"kind":"AlignedAttr"}]}]}]}
        """;
        var record = ClangAstParser.Parse(json, "Contracts", Headers).Structs.Single();
        Assert.True(record.HasBitFields);
        Assert.True(record.HasExplicitAlignment);
        Assert.True(record.HasUnsafeLayout);
    }

    [Fact]
    public void LayoutInvocation_ReusesSuccessfulModuleRetryArguments_SeparatesJson()
    {
        var runner = new LayoutCommandRunner();
        var dump = new ClangAstInvoker(runner, Logger).InvokeClangAstDumpWithLayouts("/tmp/header.h", "/tmp/frameworks", true, moduleName: "Contracts");
        using var doc = JsonDocument.Parse(dump.Json);
        Assert.Equal("TranslationUnitDecl", doc.RootElement.GetProperty("kind").GetString());
        Assert.Contains("Type: struct Contract", dump.RecordLayouts);
        Assert.Contains(runner.Arguments, a => a.Contains("-fdump-record-layouts-simple") && a.Contains("-fmodule-name=Contracts") && a.Contains("-fmodules"));
    }

    sealed class LayoutCommandRunner : BindingsGeneration.ICommandRunner
    {
        public List<string> Arguments { get; } = [];
        public (int ExitCode, string StdOut, string StdErr) Run(string command, string arguments, int timeoutMs = 30000)
        {
            Arguments.Add(arguments);
            if (arguments.Contains("--show-sdk-path")) return (0, "/sdk", "");
            if (arguments.Contains("--show-sdk-platform-path")) return (1, "", "");
            if (!arguments.Contains("-fmodules")) return (1, "", "use of '@import' when modules are disabled");
            return arguments.Contains("-fdump-record-layouts-simple")
                ? (0, "Type: struct Contract", "")
                : (0, "{\"kind\":\"TranslationUnitDecl\",\"inner\":[]}", "");
        }
    }
}
