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
/// Interface facts are keyed by Swift identifiers. The parser holds a C#-keyword-named Swift type
/// (<c>struct event</c>) as <c>_event</c>, so every fact lookup has to spell the type the way
/// Swift does; a lookup keyed by the sanitized name reads the type as absent from the public
/// interface (module-internal, its members internal by negative space) and loses its members'
/// availability floors and parameter names.
/// </summary>
public class KeywordNamedTypeFactKeyTests
{
    [Fact]
    public void KeywordNamedType_IsPublic_WhenTheInterfaceListsItsSwiftSpelling()
    {
        using var fixture = CreateFixture(SwiftInterfaceFacts.Empty with
        {
            PublicTypeNames = new HashSet<string> { "event", "Analyzer" },
        });

        Assert.False(EventType(fixture).IsModuleInternal,
            "A public keyword-named type keys the public type set by its Swift spelling.");
    }

    [Fact]
    public void KeywordNamedTypesMember_IsPublic_WhenTheInterfaceListsItsSwiftSpelling()
    {
        using var fixture = CreateFixture(SwiftInterfaceFacts.Empty with
        {
            PublicMemberNames = new HashSet<string> { "event.handler", "Analyzer.label" },
        });

        Assert.False(EventType(fixture).Properties.Single(p => p.Name == "handler").IsModuleInternal,
            "A public member of a keyword-named type keys the public member set by the type's Swift spelling.");
    }

    [Fact]
    public void KeywordNamedTypesInlinableInternalMember_IsInternal()
    {
        // The interface lists `event.handler` both as a public member (a public overload) and as an
        // internal one (an @inlinable internal overload); the ABI node carrying Inlinable is the
        // internal one, which the internal key spelled the Swift way finds.
        using var fixture = CreateFixture(SwiftInterfaceFacts.Empty with
        {
            PublicMemberNames = new HashSet<string> { "event.handler", "Analyzer.label" },
            InternalMemberKeys = new HashSet<string> { "event.handler" },
        }, eventHandlerAttributes: new[] { "HasStorage", "Inlinable" });

        Assert.True(EventType(fixture).Properties.Single(p => p.Name == "handler").IsModuleInternal);
    }

    [Fact]
    public void KeywordNamedTypesMember_ReadsItsAvailabilityFloor()
    {
        using var fixture = CreateFixture(SwiftInterfaceFacts.Empty with
        {
            AvailabilityAnnotations = new Dictionary<string, List<AvailabilityAnnotation>>
            {
                ["event.handler"] = new() { new AvailabilityAnnotation("iOS", "99.0", null, null, false, false, null, null) },
            },
        });

        var handler = EventType(fixture).Properties.Single(p => p.Name == "handler");
        Assert.Contains(handler.AvailabilityAnnotations ?? new List<AvailabilityAnnotation>(),
            a => a.Platform == "iOS" && a.IntroducedVersion == "99.0");
    }

    [Fact]
    public void KeywordNamedType_CSharpEscapedKeyDoesNotMatch()
    {
        // The producer never sees C# keyword escaping, so `_event` can only come from a fact set
        // that drifted from the Swift spelling; matching it would hide that drift.
        using var fixture = CreateFixture(SwiftInterfaceFacts.Empty with
        {
            PublicTypeNames = new HashSet<string> { "_event", "Analyzer" },
        });

        Assert.True(EventType(fixture).IsModuleInternal);
    }

    [Fact]
    public void UnderscoreNamedType_KeysByItsOwnName()
    {
        // A Swift type genuinely named `_class` is not the sanitizer's output (the sanitizer only
        // prefixes a keyword), so its key keeps the underscore.
        using var fixture = CreateFixture(SwiftInterfaceFacts.Empty with
        {
            PublicTypeNames = new HashSet<string> { "_class", "Analyzer", "event" },
        });

        var underscored = fixture.Result.ModuleDecl.Types.Single(t => t.SwiftTypeName.Name == "_class");
        Assert.False(underscored.IsModuleInternal);
    }

    [Fact]
    public void KeywordNamedProtocol_ReadsItsConventionCFact()
    {
        using var fixture = CreateFixture(SwiftInterfaceFacts.Empty with
        {
            ConventionCProtocols = new HashSet<string> { "default" },
        }, extraNode: CreateNode(kind: "TypeDecl", declKind: "Protocol", name: "default",
            mangledName: "$s10TestModule7defaultP"));

        var protocol = Assert.IsType<ProtocolDecl>(fixture.Result.ModuleDecl.Types.Single(t => t.Name == "_default"));
        Assert.True(protocol.HasConventionCClosureParameters);
    }

    private static TypeDecl EventType(ParserFixture fixture) =>
        fixture.Result.ModuleDecl.Types.Single(t => t.Name == "_event");

    private static ParserFixture CreateFixture(
        SwiftInterfaceFacts facts, string[]? eventHandlerAttributes = null, Node? extraNode = null)
    {
        var analyzerNode = CreateNode(kind: "TypeDecl", declKind: "Struct", name: "Analyzer",
            mangledName: "$s10TestModule8AnalyzerV");
        analyzerNode.Children = new[] { CreatePropertyNode("label", "Int32", "$s10TestModule8AnalyzerV5labels5Int32Vvg") };

        var eventNode = CreateNode(kind: "TypeDecl", declKind: "Struct", name: "event",
            mangledName: "$s10TestModule5eventV");
        var handler = CreatePropertyNode("handler", "Int32", "$s10TestModule5eventV7handlers5Int32Vvg");
        if (eventHandlerAttributes is not null)
            handler.DeclAttributes = eventHandlerAttributes;
        eventNode.Children = new[] { handler };

        var underscoredNode = CreateNode(kind: "TypeDecl", declKind: "Struct", name: "_class",
            mangledName: "$s10TestModule6_classV");

        var root = new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                Children = new[] { CreateNode(kind: "Import", name: "TestModule"), analyzerNode, eventNode, underscoredNode }
                    .Concat(extraNode is null ? System.Array.Empty<Node>() : new[] { extraNode }).ToArray(),
            },
        };

        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, JsonConvert.SerializeObject(root));

        var parser = new SwiftABIParser(
            filePath,
            new TypeDatabase(),
            CreateDemanglingResults(new HashSet<string>()),
            NullLogger<SwiftABIParser>.Instance,
            facts);

        return new ParserFixture(parser.ParseModule(), filePath);
    }

    private static Node CreatePropertyNode(string name, string typeName, string getterMangledName)
    {
        var getter = CreateNode(kind: "Function", declKind: "Accessor", name: name, mangledName: getterMangledName);
        getter.AccessorKind = "get";
        getter.Children = new[] { CreateTypeNominalNode(typeName) };

        var property = CreateNode(kind: "Var", declKind: "Var", name: name, mangledName: getterMangledName + "p");
        property.DeclAttributes = new[] { "HasStorage" };
        property.Children = new[] { CreateTypeNominalNode(typeName) };
        property.Accessors = new[] { getter };
        return property;
    }

    private static Node CreateTypeNominalNode(string name)
    {
        var node = CreateNode(kind: "TypeNominal", name: name, moduleName: "Swift");
        node.PrintedName = $"Swift.{name}";
        return node;
    }

    private static Node CreateNode(
        string kind,
        string declKind = "",
        string name = "",
        string moduleName = "TestModule",
        string mangledName = "$s")
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
            Children = [],
            Conformances = [],
            Accessors = [],
        };
    }

    private static DemanglingResults CreateDemanglingResults(HashSet<string> allSymbols)
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)],
            modifiers: null)!;

        return (DemanglingResults)ctor.Invoke([System.Array.Empty<IReduction>(), allSymbols]);
    }

    private sealed class ParserFixture : System.IDisposable
    {
        public ParserFixture(ModuleParsingResult result, string filePath)
        {
            Result = result;
            _filePath = filePath;
        }

        public ModuleParsingResult Result { get; }
        private readonly string _filePath;

        public void Dispose()
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
        }
    }
}
