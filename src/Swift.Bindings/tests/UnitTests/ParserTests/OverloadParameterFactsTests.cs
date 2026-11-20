// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using BindingsGeneration.Demangling;
using BindingsGeneration.Producers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Per-parameter swiftinterface facts (internal parameter names, default values) of overloads that
/// share their argument labels. The producer keys these facts by the member's labels, so
/// <c>f(_ a: Int, _ s: any P)</c> and <c>f(_ b: Int, _ c: () -&gt; Int)</c> share the key
/// <c>Box.f(_:_:)</c> and the last declaration's values used to reach both overloads: the first took
/// the second's parameter names. The producer now adds a <c>key|signature</c> entry per overload of
/// such a family, and the ABI parser reads that entry before the label-only one.
/// </summary>
public class OverloadParameterFactsTests
{
    [Fact]
    public void ParameterNames_EachOverloadTakesItsOwnNames()
    {
        var result = Parse(SwiftInterfaceFacts.Empty with
        {
            ParameterNames = new Dictionary<string, List<string>>
            {
                ["Worker.work(_:)"] = new() { "last" },
                [Refined("Worker.work(_:)", "Swift.Int32")] = new() { "number" },
                [Refined("Worker.work(_:)", "Swift.String")] = new() { "text" },
            },
        });

        Assert.Equal("number", Overload(result, "Swift.Int32").CSSignature[1].PrivateName);
        Assert.Equal("text", Overload(result, "Swift.String").CSSignature[1].PrivateName);
        // An overload the producer recorded no refinement for keeps the label-only entry.
        Assert.Equal("last", Overload(result, "Swift.Bool").CSSignature[1].PrivateName);
    }

    [Fact]
    public void ParameterNames_FreeFunctionRefinementsNeverReachAMemberThatSharesTheLabels()
    {
        // Free functions `work(_ globalInt: Int32)` / `work(_ globalText: String)` share the member's
        // labels, so the producer refines their module-level key. The member has its own type-scoped
        // entry, which it keeps.
        var result = Parse(SwiftInterfaceFacts.Empty with
        {
            ParameterNames = new Dictionary<string, List<string>>
            {
                ["Worker.work(_:)"] = new() { "member" },
                ["work(_:)"] = new() { "globalText" },
                [Refined("work(_:)", "Swift.Int32")] = new() { "globalInt" },
                [Refined("work(_:)", "Swift.String")] = new() { "globalText" },
            },
        });

        Assert.Equal("member", Overload(result, "Swift.Int32").CSSignature[1].PrivateName);
        Assert.Equal("member", Overload(result, "Swift.String").CSSignature[1].PrivateName);
    }

    [Fact]
    public void ParameterNames_AFreeFunctionNeverReadsTheKeyOfATypeNamedAfterItsModule()
    {
        // Module `TestModule` declares a type `TestModule` with `work(_ member: Int32)`, whose entry is
        // keyed `TestModule.work(_:)`, beside free functions `work(_ number: Int32)` and
        // `work(_ text: String)`. The free functions keep their own module-level entries.
        var result = Parse(SwiftInterfaceFacts.Empty with
        {
            ParameterNames = new Dictionary<string, List<string>>
            {
                ["TestModule.work(_:)"] = new() { "member" },
                ["work(_:)"] = new() { "text" },
                [Refined("work(_:)", "Swift.Int32")] = new() { "number" },
                [Refined("work(_:)", "Swift.String")] = new() { "text" },
            },
        }, freeFunctions: true);

        Assert.Equal("number", FreeFunction(result, "Swift.Int32").CSSignature[1].PrivateName);
        Assert.Equal("text", FreeFunction(result, "Swift.String").CSSignature[1].PrivateName);
    }

    [Fact]
    public void DefaultValues_EachOverloadTakesItsOwnDefault()
    {
        var result = Parse(SwiftInterfaceFacts.Empty with
        {
            DefaultParameterValues = new Dictionary<string, List<string>>
            {
                ["Worker.work(_:)"] = new() { "\"text\"" },
                [Refined("Worker.work(_:)", "Swift.Int32")] = new() { "7" },
                [Refined("Worker.work(_:)", "Swift.String")] = new() { "\"text\"" },
            },
        }, hasDefaultArg: true);

        Assert.Equal("7", Overload(result, "Swift.Int32").CSSignature[1].SwiftDefaultExpression);
        Assert.Equal("\"text\"", Overload(result, "Swift.String").CSSignature[1].SwiftDefaultExpression);
    }

    [SkippableFact]
    public void Producer_RefinesFactsOfOverloadsThatShareTheirLabels()
    {
        var binaryPath = SwiftSyntaxInterfaceFactsProducer.TryLocateBinary();
        Skip.IfNot(binaryPath is not null && File.Exists(binaryPath),
            "SwiftInterfaceParser binary not found. Run `nuke compile` (or set SWIFT_INTERFACE_PARSER_PATH) and re-run tests.");
        var source = """
            // swift-module-flags: -module-name Kit
            public protocol Shape {
            }
            public class Box {
              public func f(_ a: Swift.Int, _ s: any Kit.Shape) -> Swift.Int
              public func f(_ b: Swift.Int, _ c: @escaping () -> Swift.Int) -> Swift.Int
              public func h(value: Swift.Int = 7) -> Swift.Int
              public func h(value: Swift.String) -> Swift.String
              public func only(_ x: Swift.Int)
            }
            """;
        var path = Path.Combine(Path.GetTempPath(), $"OverloadFacts-{Guid.NewGuid()}.swiftinterface");
        File.WriteAllText(path, source);
        try
        {
            var facts = new SwiftSyntaxInterfaceFactsProducer(binaryPath!).Produce(path, NullLogger.Instance).Facts;

            var names = facts.ParameterNames!
                .Where(e => e.Key.StartsWith("Box.f(_:_:)|", StringComparison.Ordinal))
                .Select(e => string.Join(",", e.Value))
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "a,s", "b,c" }, names);

            var defaults = facts.DefaultParameterValues!
                .Where(e => e.Key.StartsWith("Box.h(value:)|", StringComparison.Ordinal))
                .Select(e => e.Value.Single() ?? "<none>")
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "7", "<none>" }, defaults);

            // A member with no label-sharing sibling gains no refinement; the label-only entries stay.
            Assert.DoesNotContain(facts.ParameterNames!.Keys, k => k.StartsWith("Box.only(_:)|", StringComparison.Ordinal));
            Assert.True(facts.ParameterNames!.ContainsKey("Box.f(_:_:)"));
            Assert.True(facts.ParameterNames!.ContainsKey("Box.only(_:)"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Refined(string key, string swiftParameterType)
        => MemberSignatureNormalizer.ComposeKey(key, MemberSignatureNormalizer.BuildSignature(new[] { swiftParameterType }));

    private static MethodDecl Overload(ModuleParsingResult result, string parameterType)
        => result.ModuleDecl.Types.Single().Methods.Single(m => m.Name == "work" && m.CSSignature[1].SwiftTypeSpec.ToString() == parameterType);

    private static MethodDecl FreeFunction(ModuleParsingResult result, string parameterType)
        => result.ModuleDecl.Methods.Single(m => m.Name == "work" && m.CSSignature[1].SwiftTypeSpec.ToString() == parameterType);

    private static ModuleParsingResult Parse(SwiftInterfaceFacts facts, bool hasDefaultArg = false, bool freeFunctions = false)
    {
        const string prefix = "$s10TestModule6WorkerV";
        var worker = Node("TypeDecl", "Worker", prefix, "Struct");
        worker.Children = new[] { "Int32", "String", "Bool" }.Select(p => Function(prefix, p, hasDefaultArg)).ToArray();
        var children = new List<Node> { Node("Import", "TestModule", "$s"), worker };
        if (freeFunctions)
            children.AddRange(new[] { "Int32", "String" }.Select(p => Function("$s10TestModule", p, hasDefaultArg)));
        var root = new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root", Name = "Root", PrintedName = "Root",
                Children = children.ToArray()
            }
        };
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(root));
            var constructor = typeof(DemanglingResults).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, new[] { typeof(IReduction[]), typeof(HashSet<string>) }, modifiers: null);
            var demangling = (DemanglingResults)constructor.Invoke(new object[] { Array.Empty<IReduction>(), new HashSet<string>() });
            return new SwiftABIParser(path, new TypeDatabase(), demangling, NullLogger<SwiftABIParser>.Instance, facts).ParseModule();
        }
        finally { File.Delete(path); }
    }

    private static Node Function(string parentMangle, string parameter, bool hasDefaultArg)
    {
        var parameterMangle = parameter switch { "Int32" => "s5Int32V", "String" => "SS", _ => "Sb" };
        var method = Node("Function", "work", parentMangle + "4workyys5Int32V" + parameterMangle + "F", "Func");
        method.PrintedName = "work(_:)";
        var argument = Nominal(parameter);
        argument.hasDefaultArg = hasDefaultArg;
        method.Children = new[] { Nominal("Int32"), argument };
        return method;
    }

    private static Node Nominal(string name)
    {
        var node = Node("TypeNominal", name, "$s", module: "Swift");
        node.PrintedName = "Swift." + name;
        return node;
    }

    private static Node Node(string kind, string name, string mangled, string declKind = "", string module = "TestModule") => new()
    {
        Kind = kind, DeclKind = declKind, Name = name, PrintedName = name, MangledName = mangled,
        ModuleName = module, DeclAttributes = new[] { "AccessControl" }, @static = false,
        IsInternal = false, GenericSig = null, sugared_genericSig = null, throwing = false,
        AccessorKind = null, EnumRawTypeName = null, paramValueOwnership = null, hasDefaultArg = null,
        Children = Array.Empty<Node>(), Conformances = Array.Empty<Node>(), Accessors = Array.Empty<Node>()
    };
}
