// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BindingsGeneration.Demangling;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Parser-layer regression coverage for the enum-case associated-value classification
/// in <c>CreateEnumCaseDecl</c>. swift-api-digester encodes an enum case's payload as the
/// second child of the inner function node:
/// <code>Var(EnumElement) → TypeFunc → [TypeFunc → [returnType, assocValuesNode], metatype]</code>
/// When that <c>assocValuesNode</c> is neither a <c>Tuple</c> nor a plain <c>TypeNominal</c> —
/// most commonly a <c>TypeFunc</c> (a function/closure payload such as
/// <c>case custom((String, Int) -&gt; String)</c>) — the parser must still record an associated
/// value. The earlier code matched only Tuple/TypeNominal, so a closure-payload case recorded
/// ZERO associated values, the enum was misclassified as a simple (Int32-backed) enum, the
/// parameter was marshalled as a 4-byte tag, and the Swift wrapper read the real multi-word
/// closure-carrying enum out of that undersized buffer — the Alamofire
/// URLEncoding.ArrayEncoding SIGSEGV. These tests drive the ABI-node path via
/// <see cref="SwiftABIParser.ParseModule"/> (the routing decision lives there, not in
/// <c>CreateTypeSpec</c>, which the existing direct-call tests already cover).
/// </summary>
public class EnumCasePayloadParserTests
{
    [Fact]
    public void ParseModule_EnumCaseWithClosurePayload_RecordsAssociatedValue()
    {
        // case custom((Swift.String, Swift.Int) -> Swift.String)
        var enumNode = BuildPayloadEnum(
            BuildCaseWithPayload("custom", payloadKind: "TypeFunc",
                payloadPrintedName: "(Swift.String, Swift.Int) -> Swift.String"));

        using var fixture = CreateParserWithNodes(enumNode);
        var result = fixture.Parser.ParseModule();

        var enumDecl = Assert.IsType<EnumDecl>(result.ModuleDecl.Types.Single());
        var customCase = Assert.Single(enumDecl.Cases);

        // The load-bearing property: a non-tuple/non-nominal payload is NEVER demoted to a
        // simple Int32 enum. Without the fix this case carries zero associated values and the
        // enum reports HasAssociatedValueCases == false (the undersized-buffer crash shape).
        Assert.True(customCase.HasAssociatedValues);
        Assert.NotEmpty(customCase.AssociatedValues);
        Assert.True(enumDecl.HasAssociatedValueCases);
    }

    [Fact]
    public void ParseModule_EnumCaseWithClosurePayload_PayloadIsClosureTypeSpec()
    {
        // The parseable closure printedName routes through CreateTypeSpec (not the catch-path
        // placeholder), so the recorded payload is a real ClosureTypeSpec — proving the payload
        // type is modelled, not merely counted.
        var enumNode = BuildPayloadEnum(
            BuildCaseWithPayload("custom", payloadKind: "TypeFunc",
                payloadPrintedName: "(Swift.String, Swift.Int) -> Swift.String"));

        using var fixture = CreateParserWithNodes(enumNode);
        var result = fixture.Parser.ParseModule();

        var enumDecl = Assert.IsType<EnumDecl>(result.ModuleDecl.Types.Single());
        var payload = Assert.Single(Assert.Single(enumDecl.Cases).AssociatedValues);
        Assert.IsType<ClosureTypeSpec>(payload);
    }

    [Fact]
    public void ParseModule_SimpleEnumCase_HasNoAssociatedValues()
    {
        // Boundary contrast: a no-payload case (returnPart is the enum nominal directly, not a
        // nested function) must still record ZERO associated values — the fix must not
        // over-attribute payloads to plain cases.
        var caseNode = CreateNode(kind: "Var", declKind: "EnumElement", name: "plain",
            mangledName: "$s10TestModule11PayloadEnumO5plainyA2CmF");
        var outerFunc = CreateNode(kind: "TypeFunc");
        // returnPart is the enum nominal itself (no inner TypeFunc) → simple case.
        var returnNominal = CreateNode(kind: "TypeNominal", name: "PayloadEnum");
        returnNominal.PrintedName = "TestModule.PayloadEnum";
        var metatype = CreateNode(kind: "TypeNominal", name: "PayloadEnum.Type");
        metatype.PrintedName = "TestModule.PayloadEnum.Type";
        outerFunc.Children = new[] { returnNominal, metatype };
        caseNode.Children = new[] { outerFunc };

        var enumNode = BuildPayloadEnum(caseNode);

        using var fixture = CreateParserWithNodes(enumNode);
        var result = fixture.Parser.ParseModule();

        var enumDecl = Assert.IsType<EnumDecl>(result.ModuleDecl.Types.Single());
        var plainCase = Assert.Single(enumDecl.Cases);
        Assert.False(plainCase.HasAssociatedValues);
        Assert.Empty(plainCase.AssociatedValues);
    }

    #region Test Helpers

    /// <summary>
    /// Wraps one or more enum-case nodes in a public EnumDecl node.
    /// </summary>
    private static Node BuildPayloadEnum(params Node[] caseNodes)
    {
        var enumNode = CreateNode(kind: "TypeDecl", declKind: "Enum", name: "PayloadEnum",
            mangledName: "$s10TestModule11PayloadEnumON");
        enumNode.Children = caseNodes;
        return enumNode;
    }

    /// <summary>
    /// Builds an enum-case node carrying a single associated value of the given kind/printedName.
    /// Mirrors the swift-api-digester shape:
    /// Var(EnumElement) → TypeFunc → [TypeFunc → [returnType, payload], metatype].
    /// </summary>
    private static Node BuildCaseWithPayload(string caseName, string payloadKind, string payloadPrintedName)
    {
        var caseNode = CreateNode(kind: "Var", declKind: "EnumElement", name: caseName,
            mangledName: $"$s10TestModule11PayloadEnumO{caseName.Length}{caseName}yACcADmF");

        var payloadNode = CreateNode(kind: payloadKind);
        payloadNode.PrintedName = payloadPrintedName;

        var enumReturn = CreateNode(kind: "TypeNominal", name: "PayloadEnum");
        enumReturn.PrintedName = "TestModule.PayloadEnum";

        // Inner function: payload application clause -> enum, i.e. [returnType, payload].
        var returnPart = CreateNode(kind: "TypeFunc");
        returnPart.PrintedName = $"({payloadPrintedName}) -> TestModule.PayloadEnum";
        returnPart.Children = new[] { enumReturn, payloadNode };

        // Outer function: (PayloadEnum.Type) -> (payload) -> PayloadEnum, i.e. [returnPart, metatype].
        var metatype = CreateNode(kind: "TypeNominal", name: "PayloadEnum.Type");
        metatype.PrintedName = "TestModule.PayloadEnum.Type";
        var outerFunc = CreateNode(kind: "TypeFunc");
        outerFunc.Children = new[] { returnPart, metatype };

        caseNode.Children = new[] { outerFunc };
        return caseNode;
    }

    private static ParserFixture CreateParserWithNodes(params Node[] nodes)
    {
        var root = new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                Children = nodes
            }
        };

        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, JsonConvert.SerializeObject(root));

        var parser = new SwiftABIParser(
            filePath,
            new TypeDatabase(),
            CreateEmptyDemanglingResults(),
            NullLogger.Instance,
            SwiftInterfaceFacts.Empty);

        return new ParserFixture(parser, filePath);
    }

    private static DemanglingResults CreateEmptyDemanglingResults()
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)],
            modifiers: null)!;

        return (DemanglingResults)ctor.Invoke([System.Array.Empty<IReduction>(), null]);
    }

    private static Node CreateNode(
        string kind,
        string declKind = "",
        string name = "",
        string moduleName = "TestModule",
        string mangledName = "$s",
        IEnumerable<Node>? children = null)
    {
        return new Node
        {
            Kind = kind,
            DeclKind = declKind,
            Name = name,
            MangledName = mangledName,
            PrintedName = name,
            ModuleName = moduleName,
            DeclAttributes = [],
            @static = false,
            IsInternal = false,
            GenericSig = null,
            sugared_genericSig = null,
            throwing = false,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            Children = children ?? [],
            Conformances = [],
            Accessors = []
        };
    }

    private sealed class ParserFixture : System.IDisposable
    {
        public ParserFixture(SwiftABIParser parser, string filePath)
        {
            Parser = parser;
            _filePath = filePath;
        }

        public SwiftABIParser Parser { get; }
        private readonly string _filePath;

        public void Dispose()
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
        }
    }

    #endregion
}
