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
/// A <c>mutating get</c> takes <c>self</c> inout, like a <c>mutating func</c>, so the accessor
/// decl has to say so or the wrapper hands the getter a borrowed copy. The nodes below follow
/// what <c>swift-frontend -emit-abi-descriptor-path</c> writes for
/// <code>
/// public struct Counter {
///     public subscript(i: Int) -> Int { mutating get }
///     public subscript(j j: Int) -> Int { get set }
///     public var tick: Int { mutating get }
/// }
/// </code>
/// a <c>Mutating</c> declaration attribute on a mutating getter, property or subscript alike,
/// and no <c>funcSelfKind</c> on any accessor. A setter carries no attribute in that dialect;
/// its pointer self is inferred from its kind, so it is not asserted here.
/// </summary>
public class MutatingAccessorParserTests
{
    [Fact]
    public void SubscriptGetter_MutatingDeclAttribute_IsMutating()
    {
        using var fixture = CreateFixture();

        Assert.True(SubscriptGetter(fixture, MutatingSubscriptGetterSymbol).IsMutating,
            "A subscript `mutating get` must be marked mutating so its wrapper passes self inout.");
    }

    [Fact]
    public void SubscriptGetter_PlainGet_IsNotMutating()
    {
        using var fixture = CreateFixture();

        Assert.False(SubscriptGetter(fixture, PlainSubscriptGetterSymbol).IsMutating);
    }

    [Fact]
    public void PropertyGetter_MutatingDeclAttribute_IsMutating()
    {
        using var fixture = CreateFixture();

        var counter = fixture.Result.ModuleDecl.Types.Single(t => t.Name == "Counter");
        var tick = counter.Properties.Single(p => p.Name == "tick");
        Assert.True(tick.Accessors.OfType<GetAccessorDecl>().Single().Method.IsMutating);
    }

    /// <summary>
    /// A function node spells the same fact as <c>funcSelfKind</c>. Accessors are read for
    /// either spelling, so a dump that moved the marker there would still be honored.
    /// </summary>
    [Theory]
    [InlineData(new[] { "Mutating" }, null, true)]
    [InlineData(new string[0], "Mutating", true)]
    [InlineData(new string[0], null, false)]
    [InlineData(new[] { "Transparent" }, "NonMutating", false)]
    public void IsMutatingAccessor_ReadsEitherSpelling(string[] declAttributes, string? funcSelfKind, bool expected)
    {
        var accessor = CreateNode("Accessor", "Accessor", "Get");
        accessor.DeclAttributes = declAttributes;
        accessor.funcSelfKind = funcSelfKind;

        Assert.Equal(expected, SwiftABIParser.IsMutatingAccessor(accessor));
    }

    #region Test Helpers

    private const string MutatingSubscriptGetterSymbol = "$s10TestModule7CounterVyS2icig";
    private const string PlainSubscriptGetterSymbol = "$s10TestModule7CounterV1jS2i_tcig";

    private static MethodDecl SubscriptGetter(ParserFixture fixture, string getterSymbol)
    {
        var counter = fixture.Result.ModuleDecl.Types.Single(t => t.Name == "Counter");
        return counter.Subscripts
            .SelectMany(s => s.Accessors.OfType<GetAccessorDecl>())
            .Single(a => a.Method.MangledName == getterSymbol)
            .Method;
    }

    private static ParserFixture CreateFixture()
    {
        var mutatingSubscript = CreateSubscriptNode("subscript(_:)", label: null,
            getterSymbol: MutatingSubscriptGetterSymbol, getterAttributes: new[] { "Mutating" }, withSetter: false);
        var plainSubscript = CreateSubscriptNode("subscript(j:)", label: "j",
            getterSymbol: PlainSubscriptGetterSymbol, getterAttributes: new string[0], withSetter: true);

        var tickGetter = CreateNode("Accessor", "Accessor", "Get", "$s10TestModule7CounterV4tickSivg");
        tickGetter.AccessorKind = "get";
        tickGetter.DeclAttributes = new[] { "Mutating" };
        tickGetter.Children = new[] { IntNode() };
        var tick = CreateNode("Var", "Var", "tick", "$s10TestModule7CounterV4tickSivp");
        tick.DeclAttributes = new[] { "AccessControl" };
        tick.Children = new[] { IntNode() };
        tick.Accessors = new[] { tickGetter };

        var counter = CreateNode("TypeDecl", "Struct", "Counter", "$s10TestModule7CounterV");
        counter.Children = new[] { mutatingSubscript, plainSubscript, tick };

        var root = new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                Children = new[] { CreateNode("Import", name: "TestModule"), counter },
            },
        };

        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, JsonConvert.SerializeObject(root));
        var parser = new SwiftABIParser(
            filePath,
            new TypeDatabase(),
            CreateDemanglingResults(),
            NullLogger<SwiftABIParser>.Instance,
            SwiftInterfaceFacts.Empty);
        return new ParserFixture(parser.ParseModule(), filePath);
    }

    private static Node CreateSubscriptNode(
        string printedName, string? label, string getterSymbol, string[] getterAttributes, bool withSetter)
    {
        var getter = CreateNode("Accessor", "Accessor", "Get", getterSymbol);
        getter.AccessorKind = "get";
        getter.DeclAttributes = getterAttributes;
        getter.Children = new[] { IntNode(), IntNode() };
        var accessors = new List<Node> { getter };
        if (withSetter)
        {
            var setter = CreateNode("Accessor", "Accessor", "Set", getterSymbol[..^1] + "s");
            setter.AccessorKind = "set";
            setter.Children = new[] { VoidNode(), IntNode(), IntNode() };
            accessors.Add(setter);
        }

        var subscript = CreateNode("Subscript", "Subscript", "subscript", getterSymbol[..^1] + "p");
        subscript.PrintedName = printedName;
        subscript.DeclAttributes = new[] { "AccessControl" };
        subscript.Children = new[] { IntNode(), IntNode() };
        subscript.Accessors = accessors.ToArray();
        return subscript;
    }

    private static Node IntNode()
    {
        var node = CreateNode("TypeNominal", name: "Int", moduleName: "Swift");
        node.PrintedName = "Swift.Int";
        return node;
    }

    private static Node VoidNode()
    {
        var node = CreateNode("TypeNominal", name: "Void", moduleName: "Swift");
        node.PrintedName = "()";
        return node;
    }

    private static Node CreateNode(
        string kind, string declKind = "", string name = "", string mangledName = "$s", string moduleName = "TestModule")
        => new()
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

    private static DemanglingResults CreateDemanglingResults()
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)],
            modifiers: null)!;
        return (DemanglingResults)ctor.Invoke([System.Array.Empty<IReduction>(), new HashSet<string>()]);
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

    #endregion
}
