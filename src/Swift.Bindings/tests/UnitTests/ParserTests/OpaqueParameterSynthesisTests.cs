// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for SynthesizeOpaqueParameter — verifies that parameter-position opaque types
/// (`some P`, `some P1 & P2`) are lowered into synthetic generic parameters with
/// the correct conformance set.
/// </summary>
public class OpaqueParameterSynthesisTests
{
    [Fact]
    public void SingleProtocol_AddsOneConformance()
    {
        var (parser, capture) = CreateParserWithOpaqueCapture();
        var node = CreateNode("TypeNominal", "GenericTypeParam", "some TestModule.MyProtocol");

        var result = parser.CreateTypeSpec(node);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.StartsWith("τ_opaque_", named.Name);

        var synth = Assert.Single(capture);
        var conformance = Assert.Single(synth.GenericConformances);
        Assert.Equal(ConformanceKind.Protocol, conformance.Kind);
        Assert.Equal("TestModule.MyProtocol", conformance.ConformanceTarget.ModuleQualifiedName);
    }

    [Fact]
    public void ProtocolComposition_AddsOneConformancePerProtocol()
    {
        // `some P1 & P2` parses to a ProtocolListTypeSpec; SynthesizeOpaqueParameter
        // must emit one GenericParameterConformance per protocol so the C# and Swift
        // where-clause emitters produce `where T : P1, P2`.
        var (parser, capture) = CreateParserWithOpaqueCapture();
        var node = CreateNode("TypeNominal", "GenericTypeParam",
            "some TestModule.P1 & TestModule.P2");

        var result = parser.CreateTypeSpec(node);

        Assert.IsType<NamedTypeSpec>(result);
        var synth = Assert.Single(capture);
        Assert.Equal(2, synth.GenericConformances.Count);

        var targetNames = synth.GenericConformances
            .Select(c => c.ConformanceTarget.ModuleQualifiedName)
            .OrderBy(n => n)
            .ToList();
        Assert.Equal(new[] { "TestModule.P1", "TestModule.P2" }, targetNames);
        Assert.All(synth.GenericConformances, c => Assert.Equal(ConformanceKind.Protocol, c.Kind));
    }

    [Fact]
    public void ProtocolComposition_ThreeProtocols_AddsAllThree()
    {
        var (parser, capture) = CreateParserWithOpaqueCapture();
        var node = CreateNode("TypeNominal", "GenericTypeParam",
            "some TestModule.P1 & TestModule.P2 & TestModule.P3");

        parser.CreateTypeSpec(node);

        var synth = Assert.Single(capture);
        Assert.Equal(3, synth.GenericConformances.Count);
    }

    [Fact]
    public void RepeatedOpaqueParameters_GetUniqueSyntheticNames()
    {
        // Multiple `some` parameters in the same signature must not collide.
        var (parser, capture) = CreateParserWithOpaqueCapture();
        var node1 = CreateNode("TypeNominal", "GenericTypeParam", "some TestModule.P1");
        var node2 = CreateNode("TypeNominal", "GenericTypeParam", "some TestModule.P2");

        var result1 = (NamedTypeSpec)parser.CreateTypeSpec(node1);
        var result2 = (NamedTypeSpec)parser.CreateTypeSpec(node2);

        Assert.NotEqual(result1.Name, result2.Name);
        Assert.Equal(2, capture.Count);
    }

    #region Helpers

    private static (SwiftABIParser parser, List<GenericArgumentDecl> capture) CreateParserWithOpaqueCapture()
    {
        var parser = CreateMinimalParser();
        var capture = new List<GenericArgumentDecl>();

        // _opaqueParamCapture is private — set it via reflection so CreateTypeSpec
        // routes parameter-position opaque types into SynthesizeOpaqueParameter.
        var field = typeof(SwiftABIParser).GetField(
            "_opaqueParamCapture",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(parser, capture);

        return (parser, capture);
    }

    private static SwiftABIParser CreateMinimalParser()
    {
        var abiJson = JsonConvert.SerializeObject(new
        {
            ABIRoot = new
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                Children = new object[]
                {
                    new
                    {
                        Kind = "TypeDecl",
                        DeclKind = "Module",
                        Name = "TestModule",
                        MangledName = "",
                        PrintedName = "TestModule",
                        ModuleName = "TestModule",
                        DeclAttributes = new string[0],
                        @static = false,
                        IsInternal = false,
                        GenericSig = "",
                        sugared_genericSig = "",
                        throwing = false,
                        AccessorKind = "",
                        EnumRawTypeName = "",
                        paramValueOwnership = "",
                        hasDefaultArg = false,
                        Children = new object[0],
                        Conformances = new object[0],
                        Accessors = new object[0]
                    }
                }
            }
        });

        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, abiJson);

        var parser = new SwiftABIParser(
            filePath,
            new TypeDatabase(),
            CreateEmptyDemanglingResults(),
            NullLogger.Instance,
            SwiftInterfaceFacts.Empty);

        File.Delete(filePath);

        return parser;
    }

    private static Node CreateNode(string kind, string name, string printedName)
    {
        return new Node
        {
            Kind = kind,
            DeclKind = "",
            Name = name,
            MangledName = "",
            PrintedName = printedName,
            ModuleName = "",
            DeclAttributes = Array.Empty<string>(),
            @static = null,
            IsInternal = null,
            GenericSig = null,
            sugared_genericSig = null,
            throwing = null,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            Children = Enumerable.Empty<Node>(),
            Conformances = Enumerable.Empty<Node>(),
            Accessors = Enumerable.Empty<Node>()
        };
    }

    private static BindingsGeneration.Demangling.DemanglingResults CreateEmptyDemanglingResults()
    {
        var ctor = typeof(BindingsGeneration.Demangling.DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            new[] { typeof(BindingsGeneration.Demangling.IReduction[]), typeof(HashSet<string>) },
            modifiers: null);
        if (ctor == null)
            throw new InvalidOperationException("Could not find DemanglingResults constructor");
        return (BindingsGeneration.Demangling.DemanglingResults)ctor.Invoke(
            new object[] { Array.Empty<BindingsGeneration.Demangling.IReduction>(), new HashSet<string>() });
    }

    #endregion
}
