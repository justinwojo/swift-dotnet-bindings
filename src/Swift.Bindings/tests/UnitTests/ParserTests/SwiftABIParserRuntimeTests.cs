// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BindingsGeneration.Demangling;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

public class SwiftABIParserRuntimeTests
{
    [Fact]
    public void GetModuleName_ReturnsFirstChildModuleName()
    {
        using var fixture = CreateParserWithNodes(
            CreateNode(kind: "Import", moduleName: "StoreKit", name: "StoreKit"));
        var parser = fixture.Parser;

        Assert.Equal("StoreKit", parser.GetModuleName());
    }

    [Fact]
    public void ParseModule_WithUnsupportedNodeKind_DoesNotThrowAndSkipsNode()
    {
        using var fixture = CreateParserWithNodes(
            CreateNode(kind: "UnknownKind", moduleName: "TestModule", name: "Mystery"));
        var parser = fixture.Parser;

        var result = parser.ParseModule();

        Assert.Equal("TestModule", result.ModuleDecl.Name);
        Assert.Empty(result.ModuleDecl.Types);
        Assert.Empty(result.ModuleDecl.Methods);
        Assert.Empty(result.ModuleDecl.Properties);
    }

    [Fact]
    public void ParseModule_WithKeywordModuleName_EscapesModuleName()
    {
        using var fixture = CreateParserWithNodes(
            CreateNode(kind: "Import", moduleName: "class", name: "class"));
        var parser = fixture.Parser;

        var result = parser.ParseModule();

        Assert.Equal("_class", result.ModuleDecl.Name);
    }

    [Fact]
    public void ParseModule_TypeDeclWithoutMangledName_SkipsType()
    {
        using var fixture = CreateParserWithNodes(
            CreateNode(
                kind: "TypeDecl",
                declKind: "Struct",
                moduleName: "TestModule",
                name: "NoMangle",
                mangledName: string.Empty));
        var parser = fixture.Parser;

        var result = parser.ParseModule();

        Assert.Empty(result.ModuleDecl.Types);
        Assert.Empty(result.TypeDecls);
    }

    #region funcSelfKind → IsMutating Tests

    [Fact]
    public void ParseModule_FuncWithMutatingFuncSelfKind_SetsIsMutatingTrue()
    {
        // A function node with funcSelfKind = "Mutating"
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var paramNode = CreateNode(kind: "TypeNominal", name: "Int", mangledName: "$s");
        paramNode.PrintedName = "Int";

        var funcNode = CreateFunctionNode(
            name: "update",
            printedName: "update(_:)",
            funcSelfKind: "Mutating",
            children: new[] { returnTypeNode, paramNode });

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.True(method.IsMutating);
    }

    [Fact]
    public void ParseModule_FuncWithNonMutatingFuncSelfKind_SetsIsMutatingFalse()
    {
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "read",
            printedName: "read()",
            funcSelfKind: "NonMutating",
            children: new[] { returnTypeNode });

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.False(method.IsMutating);
    }

    [Fact]
    public void ParseModule_FuncWithNullFuncSelfKind_SetsIsMutatingFalse()
    {
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "process",
            printedName: "process()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.False(method.IsMutating);
    }

    #endregion

    #region IsInternal Visibility Tests (Bug #17)

    [Fact]
    public void ParseModule_FuncWithIsInternalTrue_SetsVisibilityInternal()
    {
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "process64",
            printedName: "process64()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.IsInternal = true;

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.Equal(Visibility.Internal, method.Visibility);
    }

    [Fact]
    public void ParseModule_FuncWithIsInternalFalse_SetsVisibilityPublic()
    {
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "encrypt",
            printedName: "encrypt()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.IsInternal = false;

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.Equal(Visibility.Public, method.Visibility);
    }

    [Fact]
    public void ParseModule_FuncWithIsInternalNull_SetsVisibilityPublic()
    {
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "doWork",
            printedName: "doWork()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.IsInternal = null;

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.Equal(Visibility.Public, method.Visibility);
    }

    [Fact]
    public void ParseModule_FuncWithUsableFromInlineWithoutAccessControl_SetsVisibilityInternal()
    {
        // @usableFromInline internal methods have "UsableFromInline" but NOT "AccessControl"
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "process64",
            printedName: "process64()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.IsInternal = null;
        funcNode.DeclAttributes = new[] { "Final", "UsableFromInline" };

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.Equal(Visibility.Internal, method.Visibility);
    }

    [Fact]
    public void ParseModule_FuncWithAccessControlAndUsableFromInline_SetsVisibilityPublic()
    {
        // Public inlinable methods have both "AccessControl" and "UsableFromInline"
        var returnTypeNode = CreateNode(kind: "TypeNominal", name: "Void", mangledName: "$s");
        returnTypeNode.PrintedName = "()";

        var funcNode = CreateFunctionNode(
            name: "encrypt",
            printedName: "encrypt()",
            funcSelfKind: null,
            children: new[] { returnTypeNode });
        funcNode.IsInternal = null;
        funcNode.DeclAttributes = new[] { "Final", "AccessControl", "Inlinable", "UsableFromInline" };

        using var fixture = CreateParserWithNodes(funcNode);
        var result = fixture.Parser.ParseModule();

        var method = Assert.Single(result.ModuleDecl.Methods);
        Assert.Equal(Visibility.Public, method.Visibility);
    }

    #endregion

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
            NullLogger.Instance);

        return new ParserFixture(parser, filePath);
    }

    private sealed class ParserFixture : IDisposable
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
            {
                File.Delete(_filePath);
            }
        }
    }

    private static DemanglingResults CreateEmptyDemanglingResults()
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)],
            modifiers: null)!;

        return (DemanglingResults)ctor.Invoke([Array.Empty<IReduction>(), null]);
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

    private static Node CreateFunctionNode(
        string name,
        string printedName,
        string? funcSelfKind,
        IEnumerable<Node>? children = null)
    {
        return new Node
        {
            Kind = "Function",
            DeclKind = "Func",
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            PrintedName = printedName,
            ModuleName = "TestModule",
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
            funcSelfKind = funcSelfKind,
            Children = children ?? [],
            Conformances = [],
            Accessors = []
        };
    }
}
