// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Reflection;
using BindingsGeneration.Demangling;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The two ABI producers spell generic parameters differently: the compiler's ABI descriptor
/// prints <c>τ_depth_index</c> and keeps the source names in <c>sugared_genericSig</c>;
/// <c>swift-api-digester -dump-sdk</c> (Apple SDK frameworks) prints only source names such as
/// <c>Value</c> and <c>Element</c>. These tests feed the digester spelling and require the same
/// result the compiler spelling yields — first at the node level, then through a full module parse,
/// where a multi-letter source name used to be read as an unknown nominal type.
/// </summary>
public class GenericParameterDialectNormalizerTests
{
    // ── Node level ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_DigesterDialect_MatchesCompilerDialectNodeForNode()
    {
        var digester = BuildBoxModule(digesterDialect: true);
        var compiler = BuildBoxModule(digesterDialect: false);

        int rewritten = GenericParameterDialectNormalizer.Normalize(digester);

        Assert.True(rewritten > 0);
        Assert.Equal(Flatten(compiler), Flatten(digester));
    }

    [Fact]
    public void Normalize_CompilerDialect_IsLeftUntouched()
    {
        var compiler = BuildBoxModule(digesterDialect: false);
        var before = Flatten(compiler);

        int rewritten = GenericParameterDialectNormalizer.Normalize(compiler);

        Assert.Equal(0, rewritten);
        Assert.Equal(before, Flatten(compiler));
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var digester = BuildBoxModule(digesterDialect: true);
        GenericParameterDialectNormalizer.Normalize(digester);
        var once = Flatten(digester);

        Assert.Equal(0, GenericParameterDialectNormalizer.Normalize(digester));
        Assert.Equal(once, Flatten(digester));
    }

    [Fact]
    public void Normalize_ProtocolScope_KeepsSelfSpelling()
    {
        var requirement = Decl("Function", "req", "<Self, T where Self : G.P>",
            children: [Type("Self"), Type("T"), Type("Self.A", name: "DependentMember")]);
        var root = Root(Decl("TypeDecl", "P", null, declKind: "Protocol", children: [requirement]));

        Assert.Equal(0, GenericParameterDialectNormalizer.Normalize(root));
        Assert.Equal("<Self, T where Self : G.P>", requirement.GenericSig);
        Assert.Null(requirement.sugared_genericSig);
        Assert.Equal(["Self", "T", "Self.A"], requirement.Children.Select(c => c.PrintedName));
    }

    [Fact]
    public void Normalize_SignatureThatDoesNotExtendItsParent_LeavesSubtreeAlone()
    {
        var member = Decl("Function", "f", "<Other>", children: [Type("Other")]);
        var root = Root(Decl("TypeDecl", "Box", "<Value>", children: [member]));

        GenericParameterDialectNormalizer.Normalize(root);

        Assert.Equal("<Other>", member.GenericSig);
        Assert.Equal("Other", member.Children.Single().PrintedName);
    }

    [Theory]
    // Dictionary keys print with a space before the colon, so both sides are types.
    [InlineData("[Key : Value]", "[τ_0_0 : τ_0_1]")]
    // A tuple label is not a type, even when it shares a parameter's spelling.
    [InlineData("(Key: Value, label: Key)", "(Key: τ_0_1, label: τ_0_0)")]
    // A member type after a dot is not a parameter; its base is.
    [InlineData("Value.Element", "τ_0_1.Element")]
    [InlineData("G.Box<Value>.Key", "G.Box<τ_0_1>.Key")]
    // Module-qualified nominals are never respelled.
    [InlineData("Swift.Optional<G.Value>", "Swift.Optional<G.Value>")]
    // A name that merely starts with a parameter's spelling is a different identifier.
    [InlineData("KeyPath<Values>", "KeyPath<Values>")]
    [InlineData("(Key) async throws -> [Value]?", "(τ_0_0) async throws -> [τ_0_1]?")]
    public void Normalize_PrintedTypeSpelling_RespellsOnlyParameterPositions(string printed, string expected)
    {
        var property = Decl("Var", "p", null, children: [Type(printed)]);
        var root = Root(Decl("TypeDecl", "C", "<Key, Value where Key : Swift.Hashable>", children: [property]));

        GenericParameterDialectNormalizer.Normalize(root);

        Assert.Equal(expected, property.Children.Single().PrintedName);
    }

    [Fact]
    public void Normalize_WhereClause_RespellsConstraintsAndKeepsSugaredTwin()
    {
        var decl = Decl("TypeDecl", "C", "<Key, Value where Key : Swift.Hashable, Value.Element == Key>");
        var root = Root(decl);

        GenericParameterDialectNormalizer.Normalize(root);

        Assert.Equal("<τ_0_0, τ_0_1 where τ_0_0 : Swift.Hashable, τ_0_1.Element == τ_0_0>", decl.GenericSig);
        Assert.Equal("<Key, Value where Key : Swift.Hashable, Value.Element == Key>", decl.sugared_genericSig);
    }

    [Fact]
    public void Normalize_GenericSuperclassAndWitnesses_FollowTheTypeScope()
    {
        var witness = Decl("TypeWitness", "Element", null, children: [Type("G.Seq<Element>.Element")]);
        var conformance = Decl("Conformance", "Sequence", null, children: [witness]);
        var seq = Decl("TypeDecl", "Seq", "<Element>", declKind: "Class");
        seq.superclassNames = ["G.Base<[Element]>"];
        seq.Conformances = [conformance];

        GenericParameterDialectNormalizer.Normalize(Root(seq));

        Assert.Equal(["G.Base<[τ_0_0]>"], seq.superclassNames);
        Assert.Equal("G.Seq<τ_0_0>.Element", witness.Children.Single().PrintedName);
    }

    [Fact]
    public void Normalize_GenericMethodOfNonGenericType_StartsAtDepthZero()
    {
        var method = Decl("Function", "gen", "<Value, Other>", children: [Type("(Value, Other)")]);
        var root = Root(Decl("TypeDecl", "Plain", null, children: [method]));

        GenericParameterDialectNormalizer.Normalize(root);

        Assert.Equal("<τ_0_0, τ_0_1>", method.GenericSig);
        Assert.Equal("(τ_0_0, τ_0_1)", method.Children.Single().PrintedName);
    }

    [Theory]
    [InlineData("<Value>", new[] { "Value" })]
    [InlineData("<Key, Value where Key : Swift.Hashable>", new[] { "Key", "Value" })]
    [InlineData("<Element : AnyObject>", new[] { "Element" })]
    [InlineData("<each Item>", new[] { "Item" })]
    [InlineData("<let count : Swift.Int, Element>", new[] { "count", "Element" })]
    [InlineData("<C, E where C : Swift.Collection, C.Element == (label: Swift.String?, value: Any)>", new[] { "C", "E" })]
    public void ParseParameterNames_ReadsTheParameterList(string signature, string[] expected)
    {
        Assert.Equal(expected, GenericParameterDialectNormalizer.ParseParameterNames(signature));
    }

    // ── Through the parser ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseModule_DigesterDialect_BindsSourceNamedParametersLikeTheCompilerDialect()
    {
        var fromDigester = ParseBox(digesterDialect: true);
        var fromCompiler = ParseBox(digesterDialect: false);

        foreach (var (box, dialect) in new[] { (fromDigester, "digester"), (fromCompiler, "compiler") })
        {
            Assert.Equal(["Value"], box.GenericParameters.Select(p => p.SugaredTypeName));
            var value = Assert.Single(box.Properties, p => p.Name == "value");
            Assert.True(TypeSpecHelpers.IsGenericTypeParameter(value.SwiftTypeSpec),
                $"{dialect}: Box.value must be typed by Box's generic parameter, got {value.SwiftTypeSpec}");

            var inner = Assert.Single(box.Types, t => t.Name == "Inner");
            Assert.Equal(["Value", "Element"], inner.GenericParameters.Select(p => p.SugaredTypeName));
            foreach (var name in new[] { "e", "v" })
            {
                var property = Assert.Single(inner.Properties, p => p.Name == name);
                Assert.True(TypeSpecHelpers.IsGenericTypeParameter(property.SwiftTypeSpec),
                    $"{dialect}: Inner.{name} must be typed by a generic parameter, got {property.SwiftTypeSpec}");
            }
        }

        Assert.Equal(TypeSpecsOf(fromCompiler), TypeSpecsOf(fromDigester));
    }

    private static string[] TypeSpecsOf(TypeDecl box)
    {
        var inner = box.Types.Single(t => t.Name == "Inner");
        return box.Properties.Select(p => $"Box.{p.Name}: {p.SwiftTypeSpec}")
            .Concat(inner.Properties.Select(p => $"Inner.{p.Name}: {p.SwiftTypeSpec}"))
            .Concat(box.Methods.Select(m => $"Box.{m.Name}({string.Join(", ", m.CSSignature.Select(a => a.SwiftTypeSpec))})"))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
    }

    // ── Fixture: struct Box<Value> { var value: Value; func map<T>(_ f: (Value) -> T) -> Box<T>;
    //                                 struct Inner<Element> { var e: Element; var v: Value } } ──

    /// <summary>
    /// The same declarations in either dialect. Only generic-parameter spellings differ, exactly
    /// as the two producers print them; the compiler dialect also carries the sugared twin.
    /// </summary>
    private static RootNode BuildBoxModule(bool digesterDialect)
    {
        string P(string source, string depthIndex) => digesterDialect ? source : depthIndex;
        Node Generic(string source, string depthIndex) => Type(P(source, depthIndex), name: "GenericTypeParam");
        Node WithSig(Node node, string source, string depthIndex)
        {
            node.GenericSig = P(source, depthIndex);
            node.sugared_genericSig = digesterDialect ? null : source;
            return node;
        }
        Node StoredProperty(string name, string mangled, Func<Node> type, string sig, string tauSig) =>
            Decl("Var", name, null, mangled: mangled + "vp", children: [type()],
                accessors:
                [
                    WithSig(Decl("Accessor", "Get", null, mangled: mangled + "vg", accessorKind: "get", children: [type()]), sig, tauSig),
                    WithSig(Decl("Accessor", "Set", null, mangled: mangled + "vs", accessorKind: "set", children: [Type("()", name: "Void"), type()]), sig, tauSig),
                ]);

        var map = WithSig(Decl("Function", "map", null, declKind: "Func", printedName: "map(_:)",
                mangled: "$s1G3BoxV3mapyACyqd__Gqd__xXElF",
                children:
                [
                    Type(P("G.Box<T>", "G.Box<τ_1_0>"), name: "Box", children: [Generic("T", "τ_1_0")]),
                    Type(P("(Value) -> T", "(τ_0_0) -> τ_1_0"), kind: "TypeFunc", name: "Function",
                        children: [Generic("T", "τ_1_0"), Generic("Value", "τ_0_0")]),
                ]),
            "<Value, T>", "<τ_0_0, τ_1_0>");

        var inner = WithSig(Decl("TypeDecl", "Inner", null, declKind: "Struct", mangled: "$s1G3BoxV5InnerV",
                children:
                [
                    StoredProperty("e", "$s1G3BoxV5InnerV1eqd__", () => Generic("Element", "τ_1_0"), "<Value, Element>", "<τ_0_0, τ_1_0>"),
                    StoredProperty("v", "$s1G3BoxV5InnerV1vx", () => Generic("Value", "τ_0_0"), "<Value, Element>", "<τ_0_0, τ_1_0>"),
                ]),
            "<Value, Element>", "<τ_0_0, τ_1_0>");

        var box = WithSig(Decl("TypeDecl", "Box", null, declKind: "Struct", mangled: "$s1G3BoxV",
                children:
                [
                    StoredProperty("value", "$s1G3BoxV5valuex", () => Generic("Value", "τ_0_0"), "<Value>", "<τ_0_0>"),
                    map,
                    inner,
                ]),
            "<Value>", "<τ_0_0>");

        return Root(box);
    }

    private static TypeDecl ParseBox(bool digesterDialect)
    {
        var filePath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(filePath, JsonConvert.SerializeObject(new ABIRootNode { ABIRoot = BuildBoxModule(digesterDialect) }));
            var parser = new SwiftABIParser(filePath, new TypeDatabase(), CreateEmptyDemanglingResults(),
                NullLogger.Instance, SwiftInterfaceFacts.Empty);
            var result = parser.ParseModule();
            return Assert.Single(result.ModuleDecl.Types, t => t.Name == "Box");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    // ── Node builders ──────────────────────────────────────────────────────────────────────────

    private static RootNode Root(params Node[] children) => new()
    {
        Kind = "Root",
        Name = "TopLevel",
        PrintedName = "TopLevel",
        Children = children,
    };

    private static Node Type(string printedName, string kind = "TypeNominal", string name = "Nominal", Node[]? children = null) =>
        Decl(kind, name, null, printedName: printedName, children: children ?? [], declKind: "");

    private static Node Decl(
        string kind,
        string name,
        string? genericSig,
        string declKind = "",
        string? printedName = null,
        string mangled = "",
        string? accessorKind = null,
        Node[]? children = null,
        Node[]? accessors = null) => new()
    {
        Kind = kind,
        DeclKind = declKind,
        Name = name,
        MangledName = mangled,
        usr = mangled.Length > 0 ? "s:" + mangled[2..] : null,
        PrintedName = printedName ?? name,
        ModuleName = "G",
        DeclAttributes = kind is "TypeDecl" or "Var" or "Function" ? ["AccessControl"] : [],
        @static = false,
        IsInternal = false,
        GenericSig = genericSig,
        sugared_genericSig = null,
        throwing = false,
        AccessorKind = accessorKind,
        EnumRawTypeName = null,
        paramValueOwnership = null,
        hasDefaultArg = null,
        Children = children ?? [],
        Conformances = [],
        Accessors = accessors ?? [],
    };

    /// <summary>Every node's kind, name, spelling and signatures, depth-first.</summary>
    private static List<string> Flatten(RootNode root)
    {
        var lines = new List<string>();
        void Walk(Node node, int depth)
        {
            lines.Add($"{new string(' ', depth)}{node.Kind} {node.Name} '{node.PrintedName}' gs={node.GenericSig} sg={node.sugared_genericSig}");
            foreach (var child in node.Children) Walk(child, depth + 1);
            foreach (var accessor in node.Accessors) Walk(accessor, depth + 1);
            foreach (var conformance in node.Conformances) Walk(conformance, depth + 1);
        }
        foreach (var child in root.Children) Walk(child, 0);
        return lines;
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
}
