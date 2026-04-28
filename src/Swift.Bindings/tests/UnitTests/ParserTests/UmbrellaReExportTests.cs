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
/// Regression coverage for nested struct/enum types whose metadata accessor symbols
/// are not present in the current module's TBD. Apple frameworks like RealityFoundation
/// umbrella-re-export types declared in a different framework (RealityKit), so the
/// metadata accessor lives in the source framework's TBD. Before the fix,
/// `_demangledTbd.GetMetadataAccessor` threw inside `CreateStructDecl`/`CreateEnumDecl`,
/// the exception was caught at `HandleNode`, and the nested type was silently dropped.
/// Nested classes were unaffected because `CreateClassDecl` stores the raw mangled name
/// and `RegisterClassType` derives the accessor from convention (`{mangled}Ma`).
/// </summary>
public class UmbrellaReExportTests
{
    [Fact]
    public void ParseModule_NestedStructInClass_NotInTbd_IsRegistered()
    {
        var nestedStruct = CreateNode(
            kind: "TypeDecl",
            declKind: "Struct",
            name: "Nested",
            mangledName: "$s10Re-export5OuterC6NestedV");

        var outerClass = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "Outer",
            mangledName: "$s10Re-export5OuterC",
            children: new[] { nestedStruct });

        using var fixture = CreateParserWithNodes(outerClass);
        var result = fixture.Parser.ParseModule();

        var outer = Assert.Single(result.ModuleDecl.Types);
        var nested = Assert.Single(outer.Types);
        Assert.IsType<StructDecl>(nested);
        Assert.Equal("Nested", nested.Name);
        Assert.Equal("$s10Re-export5OuterC6NestedVMa", ((StructDecl)nested).MetadataAccessor);
    }

    [Fact]
    public void ParseModule_NestedEnumInClass_NotInTbd_IsRegistered()
    {
        var nestedEnum = CreateNode(
            kind: "TypeDecl",
            declKind: "Enum",
            name: "Mode",
            mangledName: "$s10Re-export5OuterC4ModeO");

        var outerClass = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "Outer",
            mangledName: "$s10Re-export5OuterC",
            children: new[] { nestedEnum });

        using var fixture = CreateParserWithNodes(outerClass);
        var result = fixture.Parser.ParseModule();

        var outer = Assert.Single(result.ModuleDecl.Types);
        var nested = Assert.Single(outer.Types);
        Assert.IsType<EnumDecl>(nested);
        Assert.Equal("Mode", nested.Name);
        Assert.Equal("$s10Re-export5OuterC4ModeOMa", ((EnumDecl)nested).MetadataAccessor);
    }

    [Fact]
    public void ParseModule_NestedStructAndEnumAndClassInClass_NotInTbd_AllRegistered()
    {
        // Mirrors RealityFoundation.TextureResource: a re-exported class with nested
        // struct (CreateOptions), enum (Semantic), and class (Drawable) children. Before
        // the fix only the nested class was registered.
        var nestedStruct = CreateNode(
            kind: "TypeDecl",
            declKind: "Struct",
            name: "CreateOptions",
            mangledName: "$s10Re-export5OuterC13CreateOptionsV");
        var nestedEnum = CreateNode(
            kind: "TypeDecl",
            declKind: "Enum",
            name: "Semantic",
            mangledName: "$s10Re-export5OuterC8SemanticO");
        var nestedClass = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "Drawable",
            mangledName: "$s10Re-export5OuterC8DrawableC");

        var outerClass = CreateNode(
            kind: "TypeDecl",
            declKind: "Class",
            name: "Outer",
            mangledName: "$s10Re-export5OuterC",
            children: new[] { nestedStruct, nestedEnum, nestedClass });

        using var fixture = CreateParserWithNodes(outerClass);
        var result = fixture.Parser.ParseModule();

        var outer = Assert.Single(result.ModuleDecl.Types);
        Assert.Equal(3, outer.Types.Count);
        Assert.Contains(outer.Types, t => t is StructDecl && t.Name == "CreateOptions");
        Assert.Contains(outer.Types, t => t is EnumDecl && t.Name == "Semantic");
        Assert.Contains(outer.Types, t => t is ClassDecl && t.Name == "Drawable");
    }

    [Fact]
    public void ParseModule_CrossModuleAppleStructExtension_NotRegistered()
    {
        // FamilyControls's ABI exposes a `SwiftUI.Label` extension. The parser still walks the
        // SwiftUI.Label TypeDecl (so the constructor children are seen), but Label belongs to
        // SwiftUI — it must NOT register into the FamilyControls (TestModule) database, or the
        // SwiftUI bridge will pick up a half-resolved generic Token<T> as a non-generic type.
        // The fallback only applies when node.ModuleName matches the module being parsed.
        var crossModuleStruct = CreateNode(
            kind: "TypeDecl",
            declKind: "Struct",
            name: "Label",
            moduleName: "SwiftUI",
            mangledName: "$s7SwiftUI5LabelV");

        using var fixture = CreateParserWithNodes(crossModuleStruct);
        var result = fixture.Parser.ParseModule();

        Assert.DoesNotContain(result.ModuleDecl.Types, t => t.Name == "Label");
    }

    [Fact]
    public void TryGetMetadataAccessor_EmptyResults_ReturnsFalse()
    {
        var dr = CreateEmptyDemanglingResults();
        var typeName = SwiftTypeName.FromModuleQualifiedName("RealityFoundation.TextureResource.Semantic");

        Assert.False(dr.TryGetMetadataAccessor(typeName, out var symbol));
        Assert.Equal(string.Empty, symbol);
    }

    private static ParserFixture CreateParserWithNodes(params Node[] nodes)
    {
        var importNode = CreateNode(kind: "Import", moduleName: "TestModule", name: "TestModule");
        var allNodes = new[] { importNode }.Concat(nodes).ToArray();

        var root = new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                Children = allNodes
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
        IEnumerable<Node>? children = null,
        string[]? declAttributes = null)
    {
        return new Node
        {
            Kind = kind,
            DeclKind = declKind,
            Name = name,
            MangledName = mangledName,
            PrintedName = name,
            ModuleName = moduleName,
            DeclAttributes = declAttributes ?? [],
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
}
