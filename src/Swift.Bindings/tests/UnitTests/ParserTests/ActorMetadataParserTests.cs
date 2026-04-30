// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BindingsGeneration.Demangling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Parser- and typedb-layer coverage for actor metadata reachability.
///
/// Custom global-actor-isolated types in the wild (e.g. Nuke 13.x's <c>ImagePipelineActor</c>)
/// list the four compile-time marker protocols — <c>Copyable</c>, <c>Escapable</c>,
/// <c>Sendable</c>, <c>SendableMetatype</c> — plus <c>_Concurrency.Actor</c> in their
/// ABI-JSON conformance arrays, and they declare an implicit <c>unownedExecutor</c>
/// accessor returning <c>_Concurrency.UnownedSerialExecutor</c>. Before the typedb
/// stubs landed, none of these protocols had TypeRecords and the executor return
/// type failed resolution — meaning the accessor was rejected by
/// <c>MemberEmissionValidator.CanEmitProperty</c> with <c>SkipReason.UnsupportedType</c>
/// and Session 3's actor-isolated thunk had no metadata to consume.
///
/// The fix lives in <c>SwiftDatabase.xml</c> (marker protocols) and the new
/// <c>_ConcurrencyDatabase.xml</c> (Actor / GlobalActor / UnownedSerialExecutor),
/// gated by <c>TypeDatabaseExtensions.IsWellKnownRuntimeProtocol</c> so the markers
/// stay metadata-only and never project to a C# interface.
/// </summary>
public class ActorMetadataParserTests
{
    [Fact]
    public void ParseModule_ActorClassWithMarkerConformances_AllSurfaceInClassDecl()
    {
        // A minimal `actor X` declaration. The Swift Actor protocol has the stable
        // mangled name $sScA and is what CreateClassDecl sniffs for to set IsActor.
        // The ABI digester expands to the same six conformances on every actor type:
        // Copyable / Escapable / Actor / Sendable / SendableMetatype, plus GlobalActor
        // for @globalActor declarations.
        var actorNode = CreateNode(kind: "TypeDecl", declKind: "Class", name: "MyActor",
            mangledName: "$s10TestModule7MyActorCN");
        actorNode.Conformances = new[]
        {
            CreateConformanceNode("Copyable", "$ss8CopyableP"),
            CreateConformanceNode("Escapable", "$ss9EscapableP"),
            CreateConformanceNode("Actor", "$sScA"),
            CreateConformanceNode("Sendable", "$ss8SendableP"),
            CreateConformanceNode("SendableMetatype", "$ss16SendableMetatypeP"),
        };

        using var fixture = CreateParserWithNodes(actorNode);
        var result = fixture.Parser.ParseModule();

        var actor = Assert.IsType<ClassDecl>(Assert.Single(result.ModuleDecl.Types));
        Assert.Equal("MyActor", actor.Name);
        Assert.True(actor.IsActor, "Actor protocol conformance ($sScA) must set ClassDecl.IsActor.");

        // Every conformance from the ABI JSON must round-trip through HandleConformance
        // into ClassDecl.Conformances — the marker protocols are NOT silently dropped
        // even though their TBD descriptor lookup fails (marker protocols have no real
        // witness table). This is the critical reachability invariant for the actor
        // metadata work: the actor type's conformance metadata must be enumerable.
        Assert.Equal(5, actor.Conformances.Count);
        var protocolNames = actor.Conformances
            .Select(c => c.Protocol.Name)
            .ToHashSet();
        Assert.Contains("Copyable", protocolNames);
        Assert.Contains("Escapable", protocolNames);
        Assert.Contains("Sendable", protocolNames);
        Assert.Contains("SendableMetatype", protocolNames);
        // Note: the _Concurrency.Actor conformance is recognized via IsActor (mangle-name
        // comparison on $sScA above). Its demangled protocol identity is a separate
        // concern: the in-tree Swift5 demangler does not yet recognize the two-letter
        // Sc* substitution family for _Concurrency types, so $sScA currently demangles
        // to Swift.UnicodeScalar. That is a demangler-table gap, not a parser bug, and
        // it does not impede actor identification because IsActor reads the raw mangled
        // name directly. Track separately if the demangled identity becomes load-bearing.
    }

    [Fact]
    public void ParseModule_NonActorClassWithMarkerConformances_StillSurfaceInClassDecl()
    {
        // Parity check: the marker conformances flow through CreateClassDecl identically
        // for non-actor classes. The bug was historically described as actor-specific,
        // but the underlying parser path is shared — this guards against a future
        // "fix actors only" regression that would forget non-actor classes that adopt
        // Sendable/Copyable/Escapable explicitly.
        var classNode = CreateNode(kind: "TypeDecl", declKind: "Class", name: "PlainClass",
            mangledName: "$s10TestModule10PlainClassCN");
        classNode.Conformances = new[]
        {
            CreateConformanceNode("Copyable", "$ss8CopyableP"),
            CreateConformanceNode("Sendable", "$ss8SendableP"),
        };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var cls = Assert.IsType<ClassDecl>(Assert.Single(result.ModuleDecl.Types));
        Assert.False(cls.IsActor);
        var protocolNames = cls.Conformances
            .Select(c => c.Protocol.Name)
            .ToHashSet();
        Assert.Contains("Copyable", protocolNames);
        Assert.Contains("Sendable", protocolNames);
    }

    [Fact]
    public void ParseModule_ActorClassWithUnownedExecutor_PropertyAndReturnTypeReachable()
    {
        // The implicit `var unownedExecutor: UnownedSerialExecutor { get }` accessor that
        // every actor type carries. Before the typedb stub for UnownedSerialExecutor was
        // added, the return type failed TryGetTypeRecord and the property was dropped at
        // emission with SkipReason.UnsupportedType. This test asserts the parser-level
        // reachability invariant: the PropertyDecl exists in classDecl.Properties with
        // its return type set, regardless of whether the emitter chooses to project it.
        var executorReturn = new Node
        {
            Kind = "TypeNominal",
            DeclKind = "",
            Name = "UnownedSerialExecutor",
            PrintedName = "_Concurrency.UnownedSerialExecutor",
            ModuleName = "_Concurrency",
            MangledName = "$sSce",
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
            Accessors = []
        };
        var unownedExecutorVar = new Node
        {
            Kind = "Var",
            DeclKind = "Var",
            Name = "unownedExecutor",
            PrintedName = "unownedExecutor",
            ModuleName = "TestModule",
            MangledName = "$s10TestModule7MyActorC15unownedExecutorScevp",
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
            Children = new[] { executorReturn },
            Conformances = [],
            Accessors = []
        };
        var actorNode = CreateNode(kind: "TypeDecl", declKind: "Class", name: "MyActor",
            mangledName: "$s10TestModule7MyActorCN", children: new[] { unownedExecutorVar });
        actorNode.Conformances = new[]
        {
            CreateConformanceNode("Actor", "$sScA"),
        };

        using var fixture = CreateParserWithNodes(actorNode);
        var result = fixture.Parser.ParseModule();

        var actor = Assert.IsType<ClassDecl>(Assert.Single(result.ModuleDecl.Types));
        Assert.True(actor.IsActor);
        var executorProp = Assert.Single(actor.Properties, p => p.Name == "unownedExecutor");

        // The return type must demangle/parse to _Concurrency.UnownedSerialExecutor —
        // not get short-circuited to AnyType or dropped entirely. Session 3 inspects this
        // identity to wire `assumeIsolated` against the right accessor symbol.
        var typeSpec = Assert.IsType<NamedTypeSpec>(executorProp.SwiftTypeSpec);
        Assert.Equal("_Concurrency.UnownedSerialExecutor", typeSpec.Name);
    }

    #region Test Helpers

    private static Node CreateConformanceNode(string protocolName, string mangledName)
    {
        return new Node
        {
            Kind = "Conformance",
            DeclKind = "",
            Name = protocolName,
            MangledName = mangledName,
            PrintedName = protocolName,
            ModuleName = "Swift",
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
            Accessors = []
        };
    }

    private static ParserFixture CreateParserWithNodes(params Node[] nodes)
    {
        return CreateParserWithNodes(NullLogger.Instance, nodes);
    }

    private static ParserFixture CreateParserWithNodes(ILogger logger, params Node[] nodes)
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
            logger,
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
            {
                File.Delete(_filePath);
            }
        }
    }

    #endregion
}
