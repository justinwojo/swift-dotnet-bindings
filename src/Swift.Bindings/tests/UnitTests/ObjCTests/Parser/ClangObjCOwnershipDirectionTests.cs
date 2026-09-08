// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

public class ClangObjCOwnershipDirectionTests
{
    [Theory]
    [InlineData("NSReturnsRetainedAttr", ObjCReturnOwnership.Retained)]
    [InlineData("NSReturnsNotRetainedAttr", ObjCReturnOwnership.Borrowed)]
    [InlineData("NSReturnsAutoreleasedAttr", ObjCReturnOwnership.Borrowed)]
    public void CompilerOwnershipFact_ReachesMethodAndProperty(string attribute, ObjCReturnOwnership expected)
    {
        var json = $$$"""
        {"kind":"TranslationUnitDecl","inner":[{"kind":"ObjCInterfaceDecl","name":"Owner",
          "loc":{"file":"/framework/Headers/Owner.h"},"inner":[
          {"kind":"ObjCPropertyDecl","name":"value","type":{"qualType":"NSObject *"},"readonly":true},
          {"kind":"ObjCMethodDecl","name":"value","instance":true,"isImplicit":true,
           "returnType":{"qualType":"NSObject *"},"inner":[{"kind":"{{{attribute}}}","implicit":true}]},
          {"kind":"ObjCMethodDecl","name":"factory","instance":false,"returnType":{"qualType":"NSObject *"},
           "inner":[{"kind":"{{{attribute}}}"}]}]}]}
        """;
        var owner = ClangAstParser.Parse(json, "Contracts", "/framework/Headers").Classes.Single();
        Assert.Equal(expected, owner.Methods.Single().ReturnOwnership);
        Assert.Equal(expected, owner.Properties.Single().GetterOwnership);
    }

    [Theory]
    [InlineData("inout", ObjCParameterDirection.InOut)]
    [InlineData("in", ObjCParameterDirection.In)]
    [InlineData("out", ObjCParameterDirection.Out)]
    [InlineData("", ObjCParameterDirection.Unspecified)]
    public void CanonicalCompilerPrint_RecoversExpandedDirection(string qualifier, ObjCParameterDirection expected)
    {
        // The real Clang JSON drops the qualifier; -ast-print expands source macros to these words.
        var print = $"@interface Owner : NSObject\n- (void)increment:({qualifier} int *)value;\n@end";
        var model = ParseMethod(print);
        Assert.Equal(expected, model.Classes.Single().Methods.Single().Parameters.Single().Direction);
    }

    [Fact]
    public void ConflictingPrint_IsUnknown_AndLegacyAbsentIsDistinct()
    {
        Assert.Equal(ObjCParameterDirection.Unspecified, ParseMethod(null).Classes.Single().Methods.Single().Parameters.Single().Direction);
        var conflicting = "@interface Owner\n- (void)increment:(inout int *)value;\n- (void)increment:(out int *)value;\n@end";
        Assert.Equal(ObjCParameterDirection.Unknown, ParseMethod(conflicting).Classes.Single().Methods.Single().Parameters.Single().Direction);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("int unrelated;")]
    [InlineData("@interface Other\n- (void)increment:(out int *)value;\n@end")]
    public void SuppliedPrintWithNoFrameworkJoins_FailsAsAcquisitionError(string print)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ParseMethod(print));
        Assert.Contains("canonical declaration acquisition failed", error.Message);
        Assert.Contains("Contracts", error.Message);
        Assert.Contains("class:Owner|-|increment:", error.Message);
    }

    [Theory]
    [InlineData("{\"kind\":\"FunctionDecl\",\"name\":\"answer\",\"type\":{\"qualType\":\"int (void)\"},\"loc\":{\"file\":\"/framework/Headers/Owner.h\"}}")]
    [InlineData("{\"kind\":\"ObjCInterfaceDecl\",\"name\":\"Owner\",\"loc\":{\"file\":\"/framework/Headers/Owner.h\"},\"inner\":[{\"kind\":\"ObjCPropertyDecl\",\"name\":\"value\",\"type\":{\"qualType\":\"int\"},\"readonly\":true}]}")]
    public void EmptyPrintWithoutExplicitObjCMethods_DoesNotInventAnAcquisitionFailure(string node)
    {
        var model = ClangAstParser.Parse(WrapInTranslationUnit(node), "Contracts", "/framework/Headers",
            canonicalDeclarations: "");
        Assert.Equal("Contracts", model.ModuleName);
        Assert.NotEmpty(model.Functions.Select(f => f.Name).Concat(model.Classes.Select(c => c.Name)));
    }

    [Fact]
    public void PartialJoin_PreservesUnknownAndNamedRefusal_WithoutGuessingOut()
    {
        var model = ClangAstParser.Parse("""
        {"kind":"TranslationUnitDecl","inner":[{"kind":"ObjCInterfaceDecl","name":"Owner",
          "loc":{"file":"/framework/Headers/Owner.h"},"inner":[
          {"kind":"ObjCMethodDecl","name":"increment:","instance":true,"returnType":{"qualType":"void"},
           "inner":[{"kind":"ParmVarDecl","name":"value","type":{"qualType":"int *"}}]},
          {"kind":"ObjCMethodDecl","name":"write:","instance":true,"returnType":{"qualType":"void"},
           "inner":[{"kind":"ParmVarDecl","name":"value","type":{"qualType":"int *"}}]}]}]}
        """, "Contracts", "/framework/Headers", canonicalDeclarations:
            "@interface Owner\n- (void)write:(out int *)value;\n@end");
        var methods = model.Classes.Single().Methods;
        Assert.Equal(ObjCParameterDirection.Unknown, methods.Single(m => m.Selector == "increment:").Parameters.Single().Direction);
        Assert.Equal(ObjCParameterDirection.Out, methods.Single(m => m.Selector == "write:").Parameters.Single().Direction);
        var (api, diagnostics) = EmitApiDefinitionWithDiagnostics(model);
        Assert.DoesNotContain("increment:", api);
        Assert.Contains("write:", api);
        var skip = Assert.Single(diagnostics.SkippedSymbols);
        Assert.Equal("increment:", skip.SymbolName);
        Assert.Contains("unresolved compiler direction", skip.Detail);
    }

    [Fact]
    public void CanonicalPrint_KeepsCategoryProtocolAndInstanceIdentitiesSeparate()
    {
        var facts = new ClangObjCParameterDirections("""
        @interface Owner<T>(Extras)
        + (void)increment:(inout int *)value;
        - (void)increment:(out int *)value;
        @end
        @protocol Owner
        - (void)increment:(in int *)value;
        @end
        """);
        using var category = System.Text.Json.JsonDocument.Parse("""{"kind":"ObjCCategoryDecl","name":"Extras","interface":{"name":"Owner"}}""");
        using var protocol = System.Text.Json.JsonDocument.Parse("""{"kind":"ObjCProtocolDecl","name":"Owner"}""");
        Assert.Equal(ObjCParameterDirection.InOut, facts.Get(category.RootElement, "increment:", false, 1).Single());
        Assert.Equal(ObjCParameterDirection.Out, facts.Get(category.RootElement, "increment:", true, 1).Single());
        Assert.Equal(ObjCParameterDirection.In, facts.Get(protocol.RootElement, "increment:", true, 1).Single());
    }

    static ObjCModule ParseMethod(string? print) => ClangAstParser.Parse("""
        {"kind":"TranslationUnitDecl","inner":[{"kind":"ObjCInterfaceDecl","name":"Owner",
          "loc":{"file":"/framework/Headers/Owner.h"},"inner":[
          {"kind":"ObjCMethodDecl","name":"increment:","instance":true,"returnType":{"qualType":"void"},
           "inner":[{"kind":"ParmVarDecl","name":"value","type":{"qualType":"int *"}}]}]}]}
        """, "Contracts", "/framework/Headers", canonicalDeclarations: print);

    [Fact]
    public void ConsumedFacts_ReachParametersAndExplicitReceiver_WithoutRefusingOrdinaryInitializer()
    {
        var model = ClangAstParser.Parse("""
        {"kind":"TranslationUnitDecl","inner":[{"kind":"ObjCInterfaceDecl","name":"Owner",
          "loc":{"file":"/framework/Headers/Owner.h"},"inner":[
          {"kind":"ObjCMethodDecl","name":"consume:","returnType":{"qualType":"void"},"inner":[
            {"kind":"ParmVarDecl","name":"value","type":{"qualType":"NSObject *"},"inner":[{"kind":"NSConsumedAttr"}]}]},
          {"kind":"ObjCMethodDecl","name":"finish","returnType":{"qualType":"void"},"inner":[{"kind":"NSConsumesSelfAttr"}]},
          {"kind":"ObjCMethodDecl","name":"initWithValue:","returnType":{"qualType":"instancetype"},"inner":[{"kind":"NSConsumesSelfAttr","implicit":true},{"kind":"ParmVarDecl","name":"value","type":{"qualType":"int"}}]}]}]}
        """, "Contracts", "/framework/Headers");
        var methods = model.Classes.Single().Methods;
        Assert.True(methods[0].Parameters.Single().IsConsumed);
        Assert.True(methods[1].ConsumesSelf);
        Assert.False(methods[2].ConsumesSelf);
        var (api, diagnostics) = EmitApiDefinitionWithDiagnostics(model);
        Assert.Contains("Constructor", api);
        Assert.DoesNotContain("consume:", api);
        Assert.DoesNotContain("finish", api);
        Assert.Equal(2, diagnostics.SkippedSymbols.Count);
    }
}
