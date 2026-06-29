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
/// Visibility classification for members declared in the current module as an
/// <c>extension ForeignModule.ForeignType { ... }</c>. The foreign receiver type
/// (e.g. <c>Foundation.Date</c>) is flagged module-internal during parsing because
/// it is absent from this module's public type-name set, yet its current-module
/// extension members ARE emitted via the cross-module trampoline. Negative-space
/// member classification must therefore still run for those current-module members
/// on an internal-flagged foreign receiver, or an internal extension member leaks
/// into the client-compiled wrapper as a call it cannot resolve ("value of type 'X'
/// has no member 'Y'"). The narrowing is precise: it fires only for members whose
/// declaring module is the module being parsed; a genuinely foreign member on the
/// same internal receiver keeps the pre-existing skip.
/// </summary>
public class ForeignExtensionVisibilityTests
{
    [Fact]
    public void ParseModule_CurrentModuleExtensionMemberOnInternalForeignReceiver_AbsentFromPublicSet_IsInternal()
    {
        // extension Foundation.Date { func isOlderThan30Days(); func publicHelper() }
        // declared in TestModule. Date is foreign (absent from TestModule's public
        // type set) so the receiver is flagged internal. publicHelper() is in the
        // public member set; isOlderThan30Days() is not.
        var internalMember = CreateMethodNode(
            "isOlderThan30Days",
            "$s10TestModule10FoundationE17isOlderThan30DaysSbyF",
            moduleName: "TestModule");
        var publicMember = CreateMethodNode(
            "publicHelper",
            "$s10TestModule10FoundationE12publicHelperyyF",
            moduleName: "TestModule");

        var dateStruct = CreateNode(
            kind: "TypeDecl",
            declKind: "Struct",
            name: "Date",
            moduleName: "Foundation",
            mangledName: "$s10Foundation4DateV",
            children: new[] { internalMember, publicMember });

        var facts = SwiftInterfaceFacts.Empty with
        {
            // Non-empty, and deliberately does NOT contain "Date" — so the foreign
            // receiver is negative-space classified as module-internal.
            PublicTypeNames = new HashSet<string> { "PublicMarker" },
            // Keyed by the receiver's short type name (the facts producer keys foreign
            // extension members as "Date.member()"). Only publicHelper() is public.
            PublicMemberNames = new HashSet<string> { "Date.publicHelper()" },
        };

        using var fixture = CreateParserWithNodes(facts, dateStruct);
        var result = fixture.Parser.ParseModule();

        var date = Assert.Single(result.ModuleDecl.Types, t => t.Name == "Date");
        Assert.True(date.IsModuleInternal,
            "Foreign receiver absent from the public type set should be flagged internal.");

        var olderThan = Assert.Single(date.Methods, m => m.Name == "isOlderThan30Days");
        Assert.True(olderThan.IsModuleInternal,
            "Current-module extension member absent from the public member set must be classified internal " +
            "even though its receiver is an internal-flagged foreign type.");

        var helper = Assert.Single(date.Methods, m => m.Name == "publicHelper");
        Assert.False(helper.IsModuleInternal,
            "A genuinely public extension member (present in the public member set) must NOT be over-suppressed.");
    }

    [Fact]
    public void ParseModule_ForeignModuleMemberOnInternalForeignReceiver_AbsentFromPublicSet_StaysPublic()
    {
        // Narrowing control: a member whose declaring module is NOT the module being
        // parsed (moduleName "Foundation") must keep the pre-existing behavior — the
        // negative-space check is skipped because its receiver is internal — even when
        // it is absent from the public member set. A current-module member is included
        // so the receiver still routes through the cross-module extension path.
        var currentModuleMember = CreateMethodNode(
            "currentModuleHook",
            "$s10TestModule10FoundationE17currentModuleHookyyF",
            moduleName: "TestModule");
        var foreignMember = CreateMethodNode(
            "foreignOwnedMember",
            "$s10Foundation4DateV18foreignOwnedMemberyyF",
            moduleName: "Foundation");

        var dateStruct = CreateNode(
            kind: "TypeDecl",
            declKind: "Struct",
            name: "Date",
            moduleName: "Foundation",
            mangledName: "$s10Foundation4DateV",
            children: new[] { currentModuleMember, foreignMember });

        var facts = SwiftInterfaceFacts.Empty with
        {
            PublicTypeNames = new HashSet<string> { "PublicMarker" },
            // Neither member is public; the current-module one would be classified
            // internal, the foreign one must be left untouched by the receiver guard.
            PublicMemberNames = new HashSet<string> { "Date.somethingElse()" },
        };

        using var fixture = CreateParserWithNodes(facts, dateStruct);
        var result = fixture.Parser.ParseModule();

        var date = Assert.Single(result.ModuleDecl.Types, t => t.Name == "Date");
        Assert.True(date.IsModuleInternal);

        var foreign = Assert.Single(date.Methods, m => m.Name == "foreignOwnedMember");
        Assert.False(foreign.IsModuleInternal,
            "A non-current-module member on an internal foreign receiver must keep the pre-existing skip " +
            "(negative-space classification is scoped to current-module members).");

        // The current-module member IS still classified internal — confirms the receiver
        // is being treated as the cross-module extension case, not short-circuited.
        var current = Assert.Single(date.Methods, m => m.Name == "currentModuleHook");
        Assert.True(current.IsModuleInternal);
    }

    private static ParserFixture CreateParserWithNodes(SwiftInterfaceFacts facts, params Node[] nodes)
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
            CreateDemanglingResultsForDate(),
            NullLogger.Instance,
            facts);

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

    // Seeds the metadata accessor for Foundation.Date so the foreign struct receiver is
    // KEPT during parsing. In a real Facebook build the accessor resolves through the
    // Foundation dependency's TBD; with empty demangling results the parser would throw
    // (no accessor) and drop the struct in HandleNode before classification runs.
    private static DemanglingResults CreateDemanglingResultsForDate()
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)],
            modifiers: null)!;
        IReduction[] reductions =
        [
            new MetadataAccessorReduction
            {
                Symbol = "$s10Foundation4DateVMa",
                TypeSpec = new NamedTypeSpec("Foundation.Date"),
            },
        ];
        return (DemanglingResults)ctor.Invoke([reductions, null]);
    }

    private static Node CreateMethodNode(string name, string mangledName, string moduleName)
    {
        var node = CreateNode(kind: "Function", declKind: "Func", name: name, moduleName: moduleName, mangledName: mangledName);
        node.PrintedName = $"{name}()";
        node.Children = new[] { CreateNode(kind: "TypeNominal", name: "Void") };
        return node;
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
}
