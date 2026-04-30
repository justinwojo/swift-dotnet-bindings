// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BindingsGeneration.Demangling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Regression tests for HandleConformance demangler robustness.
///
/// The Swift demangler does not yet recognize every standard library short substitution
/// (notably the `Sc*` family used by `_Concurrency` types — `$sSci` for AsyncSequence,
/// `$sScI` for AsyncIteratorProtocol, etc.). Before the fix, an exception in the demangler
/// would propagate up through CreateStructDecl's `node.Conformances.Select(...)` and kill
/// the entire enclosing TypeDecl via HandleNode's catch-all — silently dropping nested
/// types like `Transaction.Transactions` from real StoreKit bindings.
///
/// The fix wraps the demangler call in a try/catch and falls back to a printedName-derived
/// protocol identity. These tests verify that an unparseable conformance MangledName does
/// not drop the parent or nested type.
/// </summary>
public class HandleConformanceRobustnessTests
{
    [Fact]
    public void ParseModule_ConformanceWithUnparseableMangledName_ParentTypeStillEmitted()
    {
        // Outer class with a single conformance whose MangledName the demangler can't handle.
        // Before the fix, this would throw and the class would be dropped entirely.
        // (Class is used here instead of struct to avoid the unrelated GetMetadataAccessor
        // assertion in the empty test DemanglingResults — the HandleConformance path is
        // shared between StructDecl and ClassDecl, so the regression coverage is identical.)
        var classNode = CreateNode(kind: "TypeDecl", declKind: "Class", name: "Outer",
            mangledName: "$s10TestModule5OuterCN");
        classNode.Conformances = new[]
        {
            CreateConformanceNode("AsyncSequence", "$sSci"),
        };

        var logger = new CapturingLogger();
        using var fixture = CreateParserWithNodes(logger, classNode);
        var result = fixture.Parser.ParseModule();

        // Parent type must still be present despite the unparseable conformance.
        Assert.True(result.ModuleDecl.Types.Count == 1,
            $"Expected 1 type, got {result.ModuleDecl.Types.Count}. Logs:\n{string.Join("\n", logger.Messages)}");
        var outer = Assert.IsType<ClassDecl>(result.ModuleDecl.Types.Single());
        Assert.Equal("Outer", outer.Name);

        // Conformance must still be carried (with a fallback protocol name) — not dropped.
        var conf = Assert.Single(outer.Conformances);
        Assert.NotNull(conf.Protocol);

        // Demangler fallback must have actually fired — confirms my fix executed.
        Assert.Contains(logger.Messages, m => m.Contains("Failed to demangle conformance") && m.Contains("$sSci"));
    }

    [Fact]
    public void ParseModule_NestedTypeWithUnparseableConformance_NestedTypeStillEmitted()
    {
        // Reproduces the StoreKit Transaction.Transactions case: outer type with a nested
        // type whose conformance MangledName fails to demangle. Before the fix, the
        // exception killed the nested type and the outer type came out missing it.
        var nestedNode = CreateNode(kind: "TypeDecl", declKind: "Class", name: "Transactions",
            mangledName: "$s10TestModule11TransactionC12TransactionsCN");
        nestedNode.Conformances = new[]
        {
            CreateConformanceNode("AsyncSequence", "$sSci"),
        };

        var outerNode = CreateNode(kind: "TypeDecl", declKind: "Class", name: "Transaction",
            mangledName: "$s10TestModule11TransactionCN");
        outerNode.Children = new[] { nestedNode };

        var logger = new CapturingLogger();
        using var fixture = CreateParserWithNodes(logger, outerNode);
        var result = fixture.Parser.ParseModule();

        var outer = Assert.IsType<ClassDecl>(Assert.Single(result.ModuleDecl.Types));
        Assert.Equal("Transaction", outer.Name);

        // The whole point of the regression: the nested type must NOT be silently dropped.
        Assert.True(outer.Types.Count == 1,
            $"Nested type was dropped. Logs:\n{string.Join("\n", logger.Messages)}");
        var nested = Assert.IsType<ClassDecl>(outer.Types.Single());
        Assert.Equal("Transactions", nested.Name);
        Assert.Single(nested.Conformances);
    }

    [Fact]
    public void ParseModule_ProtocolWithUnparseableInheritedProtocol_ProtocolStillEmitted()
    {
        // CreateProtocolDecl has its own demangler call (separate from HandleConformance) for
        // building InheritedProtocols. Before the fix, an unsupported substitution like $sSci
        // would throw, HandleNode's catch-all would swallow it, and the entire ProtocolDecl
        // would be silently dropped. Mirror coverage of the nominal-type fix for the protocol path.
        var protocolNode = CreateNode(kind: "TypeDecl", declKind: "Protocol", name: "MyProtocol",
            mangledName: "$s10TestModule10MyProtocolPN");
        protocolNode.Conformances = new[]
        {
            CreateConformanceNode("AsyncSequence", "$sSci"),
        };

        var logger = new CapturingLogger();
        using var fixture = CreateParserWithNodes(logger, protocolNode);
        var result = fixture.Parser.ParseModule();

        Assert.True(result.ModuleDecl.Protocols.Count == 1,
            $"Expected protocol to be present. Logs:\n{string.Join("\n", logger.Messages)}");
        var proto = result.ModuleDecl.Protocols.Single();
        Assert.Equal("MyProtocol", proto.Name);

        // The fallback path must have actually fired in CreateProtocolDecl.
        Assert.Contains(logger.Messages, m =>
            m.Contains("Failed to demangle inherited protocol") && m.Contains("$sSci"));

        // The inherited protocol must still be carried (with the fallback identity).
        Assert.Single(proto.InheritedProtocols);
    }

    [Fact]
    public void ParseModule_MultipleConformances_OneUnparseable_OthersStillCarried()
    {
        // The fallback path must kick in for the broken conformance without dropping siblings.
        var classNode = CreateNode(kind: "TypeDecl", declKind: "Class", name: "Outer",
            mangledName: "$s10TestModule5OuterCN");
        classNode.Conformances = new[]
        {
            CreateConformanceNode("AsyncSequence", "$sSci"),  // unparseable Sc-substitution
            CreateConformanceNode("Sendable", "$ss8SendableP"),
        };

        using var fixture = CreateParserWithNodes(classNode);
        var result = fixture.Parser.ParseModule();

        var outer = Assert.IsType<ClassDecl>(Assert.Single(result.ModuleDecl.Types));
        // Both conformances must be present — not dropped on the first failure.
        Assert.Equal(2, outer.Conformances.Count);
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
            ModuleName = "_Concurrency",
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

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add($"[{logLevel}] {formatter(state, exception)}");
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
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
