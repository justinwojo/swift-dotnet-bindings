// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The lowering probe: ordinary Swift calls, written from a slice's ABI descriptors, whose IR shows each
/// plan target as a client of the binary framework calls it, and a reason for every target it cannot show.
/// </summary>
public sealed class SwiftLoweringProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"swlp_{Guid.NewGuid():N}");

    public SwiftLoweringProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ---- where a slice's descriptor is ----

    [Theory]
    [InlineData("arm64-apple-ios15.0-simulator", "arm64-apple-ios-simulator")]
    [InlineData("arm64-apple-ios16.0", "arm64-apple-ios")]
    [InlineData("x86_64-apple-ios15.0-simulator", "x86_64-apple-ios-simulator")]
    [InlineData("arm64-apple-ios15.0-macabi", "arm64-apple-ios-macabi")]
    [InlineData("arm64-apple-tvos17.0", "arm64-apple-tvos")]
    [InlineData("x86_64-apple-macosx12.0", "x86_64-apple-macos")]
    [InlineData("arm64-apple-macos14.0", "arm64-apple-macos")]
    [InlineData("arm64-unknown-linux-gnu", null)]
    public void ModuleFileStem_IsTheTripleWithoutItsDeploymentVersion(string triple, string? stem) =>
        Assert.Equal(stem, SwiftLoweringProbe.ModuleFileStem(triple));

    [Fact]
    public void DescriptorOf_PrefersTheSlicesOwnFile_AndFallsBackOnlyToTheArchitecturesOnlyOne()
    {
        var modules = Path.Combine(_dir, "Lib.framework", "Modules", "Lib.swiftmodule");
        Directory.CreateDirectory(modules);
        var framework = Path.Combine(_dir, "Lib.framework");

        Assert.Null(SwiftLoweringProbe.DescriptorOf(framework, "Lib", "arm64-apple-ios15.0-simulator"));

        var other = Path.Combine(modules, "arm64-apple-ios-simulator-variant.abi.json");
        File.WriteAllText(other, "{}");
        Assert.Equal(other, SwiftLoweringProbe.DescriptorOf(framework, "Lib", "arm64-apple-ios15.0-simulator"));
        // Another architecture's descriptor is never read for this one.
        Assert.Null(SwiftLoweringProbe.DescriptorOf(framework, "Lib", "x86_64-apple-ios15.0-simulator"));

        var exact = Path.Combine(modules, "arm64-apple-ios-simulator.abi.json");
        File.WriteAllText(exact, "{}");
        Assert.Equal(exact, SwiftLoweringProbe.DescriptorOf(framework, "Lib", "arm64-apple-ios15.0-simulator"));

        File.Delete(exact);
        File.WriteAllText(Path.Combine(modules, "arm64-apple-ios-simulator-other.abi.json"), "{}");
        Assert.Null(SwiftLoweringProbe.DescriptorOf(framework, "Lib", "arm64-apple-ios15.0-simulator"));
    }

    [Fact]
    public void Descriptors_ReadTheBoundModuleFirst_ThenTheFirstFrameworkOfEachName()
    {
        var bound = Path.Combine(_dir, "slice");
        var deps = Path.Combine(_dir, "deps");
        WriteDescriptor(bound, "Lib", Root());
        WriteDescriptor(bound, "Peer", Root());
        WriteDescriptor(deps, "Peer", Root());
        WriteDescriptor(deps, "Dep", Root());
        Directory.CreateDirectory(Path.Combine(deps, "NoDescriptor.framework"));

        var descriptors = SwiftLoweringProbe.Descriptors("Lib", bound, [deps], "arm64-apple-ios15.0-simulator");

        Assert.Equal(new[] { "Lib", "Peer", "Dep" }, descriptors.Select(d => d.Module));
        Assert.StartsWith(bound, descriptors[1].Path);
        Assert.Null(SwiftLoweringProbe.Descriptors("Missing", bound, null, "arm64-apple-ios15.0-simulator")[0].Path);
    }

    /// <summary>
    /// An Apple SDK framework ships no ABI descriptor; the build dumps one (swift-api-digester) and hands
    /// it to the probe, which reads it for the bound module in place of the framework's.
    /// </summary>
    [Fact]
    public void Descriptors_ReadAGivenBoundDescriptor_InPlaceOfTheFrameworks()
    {
        var bound = Path.Combine(_dir, "slice");
        WriteDescriptor(bound, "Lib", Root());
        WriteDescriptor(bound, "Peer", Root());
        var given = Path.Combine(_dir, "Lib.abi.json");
        File.WriteAllText(given, Root().ToJsonString());

        var descriptors = SwiftLoweringProbe.Descriptors("Lib", bound, null, "arm64-apple-ios15.0-simulator", given);

        Assert.Equal(new[] { "Lib", "Peer" }, descriptors.Select(d => d.Module));
        Assert.Equal(given, descriptors[0].Path);
        Assert.StartsWith(bound, descriptors[1].Path);
        Assert.Equal(given, SwiftLoweringProbe.Descriptors("Missing", bound, null, "arm64-apple-ios15.0-simulator", given)[0].Path);
    }

    // ---- which targets get a call, and why the others do not ----

    private const string Scale = "$s3Lib7AccountC5scaleys5Int32VAEF";
    private const string Hidden = "$s3Lib7AccountC6hiddens5Int32VyF";
    private const string InHidden = "$s3Lib6HiddenC4names5Int32VyF";
    private const string Spi = "$s3Lib7AccountC6secrets5Int32VyF";
    private const string Modify = "$s3Lib7AccountC5countSivM";
    private const string Getter = "$s3Lib7AccountC5countSivg";
    private const string Pack = "$s3Lib4packyyxxQpRvzlF";
    private const string Init = "$s3Lib7AccountC5totalACs5Int64V_tcfc";
    private const string Open = "$s3Lib4BaseC5values5Int32VyF";
    private const string Always = "$s3Lib6alwaysSiyF";

    private static JsonObject Root(params JsonObject[] children) =>
        new() { ["ABIRoot"] = new JsonObject { ["kind"] = "Root", ["name"] = "TopLevel", ["children"] = Array(children) } };

    private static JsonArray Array(IEnumerable<JsonNode> nodes) => new(nodes.Select(n => n.DeepClone()).ToArray());

    private static JsonArray Strings(params string[] values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static JsonObject Type(string printedName) => new() { ["kind"] = "TypeNominal", ["name"] = printedName, ["printedName"] = printedName };

    private static JsonObject TypeDecl(string name, string declKind, string[] attributes, params JsonObject[] children) => new()
    {
        ["kind"] = "TypeDecl", ["name"] = name, ["printedName"] = name, ["declKind"] = declKind, ["moduleName"] = "Lib",
        ["declAttributes"] = Strings(attributes), ["children"] = Array(children),
    };

    private static JsonObject Function(string printedName, string mangled, string[] attributes, string result, params string[] parameters) => new()
    {
        ["kind"] = "Function", ["name"] = printedName[..printedName.IndexOf('(')], ["printedName"] = printedName, ["declKind"] = "Func",
        ["mangledName"] = mangled, ["moduleName"] = "Lib", ["declAttributes"] = Strings(attributes),
        ["children"] = Array(parameters.Prepend(result).Select(Type)),
    };

    private static JsonObject Descriptor() => Root(
        TypeDecl("Account", "Class", ["Final"],
            Function("scale(_:)", Scale, [], "Swift.Int32", "Swift.Int32"),
            Function("hidden()", Hidden, ["UsableFromInline"], "Swift.Int32"),
            WithSpi(Function("secret()", Spi, [], "Swift.Int32")),
            new JsonObject
            {
                ["kind"] = "Constructor", ["name"] = "init", ["printedName"] = "init(total:)", ["declKind"] = "Constructor",
                ["mangledName"] = Init, ["moduleName"] = "Lib", ["children"] = Array([Type("Lib.Account"), Type("Swift.Int64")]),
            },
            new JsonObject
            {
                ["kind"] = "Var", ["name"] = "count", ["printedName"] = "count", ["declKind"] = "Var", ["moduleName"] = "Lib",
                ["children"] = Array([Type("Swift.Int")]),
                ["accessors"] = Array(
                [
                    new JsonObject { ["kind"] = "Accessor", ["name"] = "Get", ["printedName"] = "Get()", ["accessorKind"] = "get", ["declKind"] = "Accessor", ["mangledName"] = Getter, ["children"] = Array([Type("Swift.Int")]) },
                    new JsonObject { ["kind"] = "Accessor", ["name"] = "Modify", ["printedName"] = "Modify()", ["accessorKind"] = "_modify", ["declKind"] = "Accessor", ["mangledName"] = Modify, ["children"] = Array([Type("()")]) },
                ]),
            }),
        TypeDecl("Hidden", "Class", ["UsableFromInline"],
            Function("name()", InHidden, [], "Swift.Int32")),
        TypeDecl("Base", "Class", [],
            Function("value()", Open, [], "Swift.Int32")),
        WithSignature(Function("pack(_:)", Pack, [], "()", "repeat each τ_0_0"), "<each τ_0_0>"),
        Function("always()", Always, ["AlwaysEmitIntoClient"], "Swift.Int"));

    private static JsonObject WithSpi(JsonObject node)
    {
        node["spi_group_names"] = Strings("Internal");
        return node;
    }

    private static JsonObject WithSignature(JsonObject node, string signature)
    {
        node["genericSig"] = signature;
        return node;
    }

    private static SwiftLoweringProbe.ProbePlan Plan(params string[] targets)
    {
        using var index = new AbiDescriptorIndex();
        index.LoadText(Descriptor().ToJsonString());
        return SwiftLoweringProbe.Plan(targets, index, new Dictionary<string, string>(), "Lib", [], SwiftLoweringProbe.NotDeclaredReason);
    }

    [Theory]
    [InlineData(Hidden, SwiftLoweringProbe.UsableFromInlineReason)]
    [InlineData(InHidden, SwiftLoweringProbe.UsableFromInlineReason)]
    [InlineData(Spi, SwiftLoweringProbe.SpiReason)]
    [InlineData("$s3Lib7missingyyF", SwiftLoweringProbe.NotDeclaredReason)]
    public void Plan_ATargetNoClientCanCall_GetsNoCall_AndItsReason(string target, string reason)
    {
        var plan = Plan(target, Scale);

        Assert.Equal(reason, plan.Unprobed[target]);
        Assert.Equal(Scale, Assert.Single(plan.Calls).Target);
    }

    private const string Helper = "$s10Foundation4DateV3LibE6helperSiyF";
    private const string Synthesized = "$s3Lib7AccountC4hash4intoys6HasherVz_tF";

    /// <summary>
    /// <see cref="Descriptor"/> as a module built for testing describes it, with a member the compiler
    /// synthesized, which no interface spells, and a member this module adds to another module's type.
    /// </summary>
    private static SwiftLoweringProbe.ProbePlan PlanAgainst(IReadOnlySet<string>? interfaceNames, params string[] targets)
    {
        var descriptor = Descriptor();
        var children = descriptor["ABIRoot"]!["children"]!.AsArray();
        var synthesized = Function("hash(into:)", Synthesized, [], "()", "Swift.Hasher");
        synthesized["implicit"] = true;
        children[0]!["children"]!.AsArray().Add((JsonNode)synthesized);
        var date = TypeDecl("Date", "Struct", [], Function("helper()", Helper, [], "Swift.Int"));
        date["moduleName"] = "Foundation";
        children.Add((JsonNode)date);
        using var index = new AbiDescriptorIndex();
        index.LoadText(descriptor.ToJsonString());
        var names = interfaceNames is null ? null : new Dictionary<string, IReadOnlySet<string>> { ["Lib"] = interfaceNames };
        return SwiftLoweringProbe.Plan(targets, index, new Dictionary<string, string>(), "Lib", [], SwiftLoweringProbe.NotDeclaredReason, names);
    }

    /// <summary>
    /// A declaration whose type, or whose own name, the module's interface never spells is internal:
    /// it gets no call, and its reason is not public from the interface. An initializer, an accessor of
    /// a spelled property, a synthesized member and a member of another module's type are not judged
    /// by a name the interface need not spell.
    /// </summary>
    [Fact]
    public void Plan_ADeclarationTheInterfaceDoesNotName_IsInternal_AndGetsNoCall()
    {
        string[] targets = [Scale, Getter, Init, Open, Synthesized, Helper];
        IReadOnlySet<string> withoutAccount = new HashSet<string> { "Base", "value", "count", "scale", "helper" };

        var plan = PlanAgainst(withoutAccount, targets);

        // Every member of the internal Account, initializer included, is internal.
        foreach (var target in new[] { Scale, Getter, Init, Synthesized })
            Assert.Equal(SwiftLoweringProbe.InternalReason, plan.Unprobed[target]);
        Assert.Equal(new[] { Open, Helper }.Order(StringComparer.Ordinal), plan.Calls.Select(c => c.Target).Order(StringComparer.Ordinal));
        Assert.Equal(SwiftLoweringProbe.NotPublicCategory, SwiftLoweringProbe.ReasonCategory(SwiftLoweringProbe.InternalReason));

        // A public Account with an internal scale(_:) and an internal count. This module's interface
        // spells its extension of Date by the member's name, never needing Date's own declaration.
        IReadOnlySet<string> withoutMembers = new HashSet<string> { "Account", "Base", "value", "helper" };
        plan = PlanAgainst(withoutMembers, targets);

        Assert.Equal(SwiftLoweringProbe.InternalReason, plan.Unprobed[Scale]);
        Assert.Equal(SwiftLoweringProbe.InternalReason, plan.Unprobed[Getter]);
        Assert.Equal(new[] { Init, Open, Synthesized, Helper }.Order(StringComparer.Ordinal), plan.Calls.Select(c => c.Target).Order(StringComparer.Ordinal));

        // With no interface to read, nothing is judged internal by its absence.
        Assert.Empty(PlanAgainst(null, targets).Unprobed);
    }

    [Fact]
    public void InterfaceNames_AreEveryIdentifierOfThePublicAndPrivateInterface_OrNoneWithoutOne()
    {
        var path = Path.Combine(_dir, "arm64-apple-ios.swiftinterface");
        Assert.Null(SwiftLoweringProbe.InterfaceNames(path));
        Assert.Null(SwiftLoweringProbe.InterfaceNames(null));

        File.WriteAllText(path, "public final class Host {\n  public func `default`() -> Swift.Int32\n  public var größe: Swift.Int { get }\n}\n");
        File.WriteAllText(Path.Combine(_dir, "arm64-apple-ios.private.swiftinterface"), "@_spi(Internal) public func gated()\n");

        var names = SwiftLoweringProbe.InterfaceNames(path)!;
        Assert.Contains("Host", names);
        Assert.Contains("default", names);
        Assert.Contains("größe", names);
        Assert.Contains("Int32", names);
        Assert.Contains("gated", names);
        Assert.DoesNotContain("hidden", names);
    }

    [Theory]
    [InlineData(Modify, "_modify accessor")]
    [InlineData(Pack, "parameter pack")]
    public void Plan_ACallTheProbeCannotWrite_IsUnsupportedInTheProbe_WithWhatItLacks(string target, string what)
    {
        var plan = Plan(target);

        Assert.Empty(plan.Calls);
        Assert.Equal("unsupported in the probe", SwiftLoweringProbe.ReasonCategory(plan.Unprobed[target]));
        Assert.Contains(what, plan.Unprobed[target]);
    }

    [Theory]
    [InlineData(Scale)]
    [InlineData(Getter)]
    [InlineData(Open)]
    [InlineData(Open + "Tj")]                     // a dispatch thunk calls the member it dispatches
    [InlineData("$s3Lib7AccountC5totalACs5Int64V_tcfC")] // the allocating entry of an initializer the descriptor names by its initializing one
    [InlineData(Always)]
    public void Plan_EveryCallableTarget_GetsACallOfItsOwn(string target)
    {
        var plan = Plan(target, Scale, Getter);

        Assert.Empty(plan.Unprobed);
        Assert.Single(plan.Calls, c => c.Target == target);
        Assert.Equal(plan.Calls.Length, plan.Calls.Select(c => c.Function).Distinct().Count());
    }

    /// <summary>
    /// A member of a non-generic type is also written from inside its type, whose isolation an
    /// extension shares; a global has no type to be inside, and a generic one's extension could not
    /// name the probe's parameters.
    /// </summary>
    [Fact]
    public void Plan_ANonGenericTypesMember_IsAlsoWrittenFromInsideItsType()
    {
        var plan = Plan(Scale, Getter, Always, Pack);

        var scale = plan.Calls.Single(c => c.Target == Scale);
        Assert.NotNull(scale.InType);
        Assert.StartsWith($"// {Scale}\nextension Lib.Account {{\n", scale.InType);
        Assert.Contains($"public static func {scale.Function}() async throws {{", scale.InType);
        Assert.DoesNotContain("@MainActor", scale.InType);
        // The same body as the main-actor form.
        var body = scale.Text.Split('\n').SkipWhile(l => !l.Contains("var recv")).Select(l => l.Trim()).Where(l => l != "}");
        Assert.All(body, line => Assert.Contains(line, scale.InType));
        Assert.Contains("extension Lib.Account {", plan.Calls.Single(c => c.Target == Getter).InType);
        Assert.Null(plan.Calls.Single(c => c.Target == Always).InType);
    }

    [Fact]
    public void Render_ImportsTheBoundModuleUnconditionally_AndEveryOtherOnlyWhereItResolves()
    {
        using var index = new AbiDescriptorIndex();
        index.LoadText(Descriptor().ToJsonString());
        var plan = SwiftLoweringProbe.Plan([Scale], index, new Dictionary<string, string>(), "Lib", ["Foundation", "Lib"], SwiftLoweringProbe.NotDeclaredReason);

        var (text, lines) = SwiftLoweringProbe.Render(plan, plan.Calls);

        Assert.Equal(new[] { "Lib", "Foundation" }, plan.Imports);
        Assert.Contains("\nimport Lib\n", "\n" + text);
        Assert.Contains("#if canImport(Foundation)\nimport Foundation\n#endif", text);
        // The span recorded for a call is the call's own text.
        var (call, first, last) = Assert.Single(lines);
        var rendered = text.Split('\n')[(first - 1)..last];
        Assert.Equal(call.Text.Split('\n'), rendered);
    }

    [Fact]
    public void WrapperImports_AreTheFileScopeImportsOfEverySource_InOrder()
    {
        var a = Path.Combine(_dir, "a.swift");
        var b = Path.Combine(_dir, "b.swift");
        File.WriteAllText(a, "import Foundation\n@_implementationOnly import Hidden\nfunc f() {\n    import Nested\n}\n");
        File.WriteAllText(b, "@preconcurrency import UIKit\nimport struct Lib.Thing\nimport Foundation\npublic import Spelled\n");

        Assert.Equal(new[] { "Foundation", "Hidden", "UIKit", "Lib", "Spelled" }, SwiftLoweringProbe.WrapperImports([a, b, Path.Combine(_dir, "missing.swift")]));
    }

    // ---- error attribution ----

    [Fact]
    public void Errors_FallInTheCallWhoseLinesHoldThem_AndTheDecoratedEchoIsIgnored()
    {
        var first = new SwiftLoweringProbe.ProbeCall("$sA", "_sbProbe_0", "", null);
        var second = new SwiftLoweringProbe.ProbeCall("$sB", "_sbProbe_1", "", null);
        var stderr = string.Join('\n',
            "/work/P.arm64.swift:12:9: error: cannot find 'x' in scope",
            "10 |     var recv: Lib.T = _sbProbeValue()",
            "   |         `- error: cannot find 'x' in scope",
            "/work/P.arm64.swift:13:9: error: a second error in the same call",
            "/work/P.arm64.swift:20:5: warning: something harmless",
            "/work/P.arm64.swift:21:5: error: extra argument in call");

        var failed = SwiftLoweringProbe.Errors(stderr, "P.arm64.swift", [(first, 10, 14), (second, 18, 23)], out var unattributed);

        Assert.Null(unattributed);
        Assert.Equal(new[] { ("$sA", "cannot find 'x' in scope"), ("$sB", "extra argument in call") }, failed.Select(f => (f.Call.Target, f.Error)));
    }

    [Theory]
    [InlineData("/work/P.arm64.swift:3:1: error: no such module 'Lib'")]   // the header, outside every call
    [InlineData("/elsewhere/Lib.swiftinterface:9:1: error: bad interface")] // another file
    [InlineData("error: unable to load standard library")]                  // no location at all
    public void Errors_OutsideEveryCall_AreUnattributed_SinceDroppingACallCannotFixThem(string line)
    {
        var call = new SwiftLoweringProbe.ProbeCall("$sA", "_sbProbe_0", "", null);

        var failed = SwiftLoweringProbe.Errors(line, "P.arm64.swift", [(call, 10, 14)], out var unattributed);

        Assert.Empty(failed);
        Assert.NotNull(unattributed);
    }

    // ---- what the demangling adds to the descriptor ----

    [Theory]
    [InlineData("Lib.Account.accumulate(_: Swift.Int64, through: @convention(c) (Swift.Int64) -> Swift.Int64) throws -> Swift.Int64",
        "Swift.Int64|@convention(c) (Swift.Int64) -> Swift.Int64", "", "", "Swift.Int64")]
    [InlineData("Lib.ConstBox.init(lo: _const Swift.Int) -> Lib.ConstBox", "Swift.Int", "lo", "", "Lib.ConstBox")]
    [InlineData("Lib.Account.sum(Swift.Int32...) -> Swift.Int32", "Swift.Int32", "", "sum", "Swift.Int32")]
    [InlineData("Lib.Box.mutate(inout Swift.Int, __owned Swift.String) async throws(Lib.Failure) -> ()", "Swift.Int|Swift.String", "", "", "()")]
    [InlineData("Lib.Box.transform<A>(Swift.Optional<Swift.Int>) -> Swift.Optional<A>", "Swift.Optional<Swift.Int>", "", "", "Swift.Optional<A>")]
    public void Demangled_Parse_ReadsEachParameter_WithWhatTheDescriptorLeavesOut(
        string demangled, string types, string constOf, string variadicOf, string result)
    {
        var (parameters, parsedResult) = SwiftLoweringProbe.Demangled.Parse(demangled);

        Assert.NotNull(parameters);
        Assert.Equal(types.Split('|'), parameters!.Select(p => p.Type));
        Assert.Equal(result, parsedResult);
        Assert.Equal(constOf.Length > 0, parameters.Any(p => p.Const));
        Assert.Equal(variadicOf.Length > 0, parameters.Any(p => p.Variadic));
    }

    /// <summary>
    /// The SDK's digester prints a generic parameter as it was declared, not as <c>τ_depth_index</c>,
    /// and repeats a generic type's parameters in its members' signatures. The probe gives each the name
    /// it would have had in swiftc's own descriptor, so a call's parameters, requirements, receiver and
    /// types agree, as they do for a canonical descriptor.
    /// </summary>
    [Fact]
    public void Plan_ADescriptorThatNamesGenericParametersAsDeclared_IsWrittenWithTheProbesNames()
    {
        const string wrapped = "$s3Lib4WrapC5valuexvg";
        const string map = "$s3Lib4WrapC3map_2toqd__qd___xmtSQRd__lF";
        var box = WithSignature(TypeDecl("Wrap", "Class", ["Final"],
            new JsonObject
            {
                ["kind"] = "Var", ["name"] = "value", ["printedName"] = "value", ["declKind"] = "Var", ["moduleName"] = "Lib",
                ["children"] = Array([Type("Value")]),
                ["accessors"] = Array(
                [
                    new JsonObject { ["kind"] = "Accessor", ["name"] = "Get", ["printedName"] = "Get()", ["accessorKind"] = "get", ["declKind"] = "Accessor", ["mangledName"] = wrapped, ["children"] = Array([Type("Value")]) },
                ]),
            },
            WithSignature(Function("map(_:to:)", map, [], "Output", "Output", "Value.Type"),
                "<Value, Output where Value : Swift.Sendable, Output : Swift.Equatable>")),
            "<Value where Value : Swift.Sendable>");
        using var index = new AbiDescriptorIndex();
        index.LoadText(Root(box).ToJsonString());

        var plan = SwiftLoweringProbe.Plan([wrapped, map], index, new Dictionary<string, string>(), "Lib", [], SwiftLoweringProbe.NotDeclaredReason);

        Assert.Empty(plan.Unprobed);
        var getter = plan.Calls.Single(c => c.Target == wrapped).Text;
        Assert.Contains("<T_0_0>(_: T_0_0.Type) async throws where T_0_0 : Swift.Sendable {", getter);
        Assert.Contains("var recv: Lib.Wrap<T_0_0>", getter);
        Assert.Contains("let _: T_0_0 = try await recv.value", getter);
        var method = plan.Calls.Single(c => c.Target == map).Text;
        Assert.Contains("<T_0_0, T_1_0>(_: T_0_0.Type, _: T_1_0.Type) async throws where T_0_0 : Swift.Sendable, T_1_0 : Swift.Equatable {", method);
        Assert.Contains("as T_1_0", method);
        Assert.Contains("as T_0_0.Type", method);
        Assert.DoesNotMatch(@"\b(Value|Output)\b", getter + method);
    }

    /// <summary>
    /// A module that is an implementation detail of its umbrella cannot be imported by name; the probe
    /// imports it through the umbrella, as the wrapper does, and still names its declarations by their
    /// own module.
    /// </summary>
    [Fact]
    public void Plan_AModuleImportedThroughItsUmbrella_IsImportedAsTheWrapperImportsIt()
    {
        using var index = new AbiDescriptorIndex();
        index.LoadText(Descriptor().ToJsonString());

        var plan = SwiftLoweringProbe.Plan([Scale], index, new Dictionary<string, string>(), "RealityFoundation",
            ["RealityKit", "RealityFoundation", "Foundation"], SwiftLoweringProbe.NotDeclaredReason);

        Assert.Equal(new[] { "RealityKit", "Foundation", "Lib" }, plan.Imports);
        Assert.Contains("Lib.Account", Assert.Single(plan.Calls).Text);
    }

    [Theory]
    [InlineData("A", "T_0_0")]
    [InlineData("[B1]", "[T_1_1]")]
    [InlineData("@convention(c) (A) -> Swift.Array<A>", "@convention(c) (T_0_0) -> Swift.Array<T_0_0>")]
    [InlineData("Lib.A", "Lib.A")]         // a qualified type is never a generic parameter
    [InlineData("C", "C")]                 // nor is a name outside the signature
    public void Demangled_Spell_RenamesTheSignaturesGenericParameters_Only(string type, string spelled) =>
        Assert.Equal(spelled, SwiftLoweringProbe.Demangled.Spell(type, new HashSet<string> { "T_0_0", "T_1_1" }));

    // ---- the probe against swiftc ----

    private const string Library = """
        public final class Account {
            public init(total: Int64) {}
            public func accumulate(_ delta: Int64, through map: @convention(c) (Int64) -> Int64) throws -> Int64 { delta }
            public func scale(_ value: Int32) -> Int32 { value }
            public func scale(_ value: Int64) -> Int64 { value }
            public subscript(slot slot: Int32) -> Int64 { get { 0 } set {} }
            public func sum(_ values: Int32...) -> Int32 { 0 }
            public func make<T>() -> [T] { [] }
            public func exchange(_ value: inout Int32, with other: Int32) {}
            public static func create(_ seed: Int32) -> Account { Account(total: 0) }
            @_alwaysEmitIntoClient public func always() -> Int32 { 1 }
            @usableFromInline internal func hidden() -> Int32 { 3 }
        }
        open class Base {
            public init() {}
            open func value() -> Int32 { 0 }
        }
        public struct ConstBox { public init(lo: _const Int) {} }
        public protocol Shape { func area() -> Double }
        @globalActor public actor ProbeActor { public static let shared = ProbeActor() }
        @ProbeActor public final class Isolated { public init() {}; public var count: Int32 = 0 }
        public final class Mixed { public init() {}; @ProbeActor public var count: Int32 = 0 }
        public func global(_ x: Int32) -> Int32 { x }
        @_transparent public func transparent() -> Int32 { 2 }
        public func + (lhs: Account, rhs: Int32) -> Int32 { rhs }
        """;

    private sealed record Fixture(string SearchPath, string FrontendArgs, SwiftOracleModule Module, JsonDocument Descriptor);

    private static readonly Lazy<Fixture> _fixture = new(() => BuildFixture("ProbeKit", Library));

    /// <summary>
    /// A library built for testing (<c>-enable-testing</c>), as some binary frameworks ship: its ABI
    /// descriptor also lists the internal declarations, which its public interface leaves out.
    /// </summary>
    private const string TestableLibrary = """
        public final class Host {
            public init() {}
            public func shown() -> Int32 { 1 }
            static func hidden(_ value: Int32) -> Int32 { value }
            var tally: Int32 { 0 }
        }
        final class InternalRecord {
            init() {}
            func describe(_ value: Int32) -> Int32 { value }
            static func make() -> InternalRecord { InternalRecord() }
        }
        """;

    private static readonly Lazy<Fixture> _testable = new(() => BuildFixture("RecordKit", TestableLibrary, ["-enable-testing"]));

    private static Fixture BuildFixture(string name, string library, IEnumerable<string>? extraArguments = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"swlp_fixture_{Guid.NewGuid():N}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        };
        var build = Path.Combine(root, "build");
        Directory.CreateDirectory(build);
        var source = Path.Combine(build, name + ".swift");
        File.WriteAllText(source, library);
        var target = SwiftOracleTarget.SimulatorArm64;
        var module = SwiftLoweringOracle.Compile(name, [source], target, build, extraArguments);
        // The framework a binary distribution ships: the public interface and the ABI descriptor.
        var search = Path.Combine(root, "frameworks");
        var modules = Path.Combine(search, name + ".framework", "Modules", name + ".swiftmodule");
        Directory.CreateDirectory(modules);
        File.Copy(Path.Combine(SwiftLoweringOracle.InterfaceDirectory(build), name + ".swiftinterface"), Path.Combine(modules, "arm64-apple-ios-simulator.swiftinterface"));
        File.Copy(module.AbiJsonPath, Path.Combine(modules, "arm64-apple-ios-simulator.abi.json"));
        var frontend = $"-target {target.Triple} -sdk \"{SwiftLoweringOracle.SdkPath(target.Sdk)}\" -strict-concurrency=minimal " +
                       $"-F \"{search}\" -module-cache-path \"{Path.Combine(root, "module-cache")}\" ";
        return new Fixture(search, frontend, module,
            JsonDocument.Parse(File.ReadAllText(module.AbiJsonPath), new JsonDocumentOptions { MaxDepth = 1024 }));
    }

    /// <summary>
    /// The mangled name the fixture's descriptor gives the member <paramref name="printedName"/> of
    /// <paramref name="owner"/> (null for a global), or its <paramref name="accessor"/>; of overloads, the
    /// one whose mangled name contains <paramref name="containing"/>.
    /// </summary>
    private static string Symbol(string? owner, string printedName, string? accessor = null, string? containing = null) =>
        SymbolIn(_fixture.Value, owner, printedName, accessor, containing);

    private static string SymbolIn(Fixture fixture, string? owner, string printedName, string? accessor = null, string? containing = null)
    {
        var found = new List<string>();
        void Walk(JsonElement node, string? type)
        {
            if (node.ValueKind != JsonValueKind.Object)
                return;
            var kind = node.TryGetProperty("kind", out var k) ? k.GetString() : null;
            if (kind is "Function" or "Constructor" or "Var" or "Subscript"
                && type == owner && node.GetProperty("printedName").GetString() == printedName)
            {
                found.Add(accessor is null
                    ? node.GetProperty("mangledName").GetString()!
                    : node.GetProperty("accessors").EnumerateArray().Single(a => a.GetProperty("accessorKind").GetString() == accessor).GetProperty("mangledName").GetString()!);
                return;
            }
            var next = kind == "TypeDecl" ? node.GetProperty("name").GetString() : type;
            if (node.TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateArray())
                    Walk(child, next);
            }
        }
        Walk(fixture.Descriptor.RootElement.GetProperty("ABIRoot"), null);
        return found.Where(s => containing is null || s.Contains(containing, StringComparison.Ordinal)).ToList() is [var single]
            ? single
            : throw new InvalidOperationException($"the descriptor declares {found.Count} {owner}.{printedName}");
    }

    private SwiftLoweringProbe.Result Probe(IEnumerable<string> targets, ICommandRunner? runner = null) =>
        SwiftLoweringProbe.Run(
            new SwiftLoweringProbe.Request(targets.ToList(), "ProbeKit", _fixture.Value.SearchPath, null, [], _fixture.Value.FrontendArgs,
                "ProbeKitLoweringProbe", SwiftOracleTarget.SimulatorArm64.Triple, _dir),
            runner ?? new SystemCommandRunner(), NullLogger.Instance, 300_000);

    private static string Shown(SwiftLoweringProbe.Result result, string target) =>
        SwiftLoweringEvidenceCollector.Signatures([target], result.Modules).GetValueOrDefault(target)
        ?? throw new Xunit.Sdk.XunitException($"the probe does not show {target}: {result.Unseen.GetValueOrDefault(target)}");

    private static string Defined(string target) =>
        _fixture.Value.Module.Ir.Functions.TryGetValue(target, out var f)
            ? $"{f.CallingConvention} {f.Signature}"
            : throw new InvalidOperationException($"the library does not define {target}");

    /// <summary>
    /// Every callable shape: the probe's IR declares each exact target, with the signature the library's
    /// own definition has. The C-convention parameter, which the descriptor does not record, is among them.
    /// </summary>
    [SkippableFact]
    public void Run_ShowsEveryCallableTarget_AsTheLibraryDefinesIt()
    {
        Skip.IfNot(SwiftLoweringOracle.IsAvailable, "xcrun and an iOS simulator SDK are required");
        var targets = new[]
        {
            Symbol("Account", "accumulate(_:through:)"),
            Symbol("Account", "scale(_:)", containing: "Int32"),
            Symbol("Account", "subscript(slot:)", "get"),
            Symbol("Account", "subscript(slot:)", "set"),
            Symbol("Account", "sum(_:)"),
            Symbol("Account", "make()"),
            Symbol("Account", "exchange(_:with:)"),
            Symbol("Account", "create(_:)"),
            Symbol("Account", "init(total:)")[..^2] + "fC",
            Symbol("Base", "value()") + "Tj",
            Symbol("Shape", "area()") + "Tj",
            Symbol("ConstBox", "init(lo:)")[..^2] + "fC",
            Symbol(null, "global(_:)"),
            Symbol(null, "+(_:_:)"),
        };

        var result = Probe(targets);

        Assert.True(result.Unseen.IsEmpty, string.Join("\n", result.Unseen.Select(u => $"{u.Key}: {u.Value}")));
        foreach (var target in targets)
            Assert.Equal(Defined(target), Shown(result, target));
        Assert.Equal(1, result.Compiles);
        Assert.Equal(targets.Length, result.Calls);
        var source = Assert.IsType<SwiftEvidenceSource>(result.Source);
        Assert.Equal((SwiftEvidenceSource.ProbeRole, "ProbeKitLoweringProbe"), (source.Role, source.Name));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    /// <summary>
    /// A receipt cannot be satisfied by another entry: of two overloads only the probed one is shown, and
    /// an open member's implementation, which a client reaches only through its dispatch thunk, is not.
    /// </summary>
    [SkippableFact]
    public void Run_ShowsOnlyTheExactEntry()
    {
        Skip.IfNot(SwiftLoweringOracle.IsAvailable, "xcrun and an iOS simulator SDK are required");
        var scale32 = Symbol("Account", "scale(_:)", containing: "Int32");
        var scale64 = Symbol("Account", "scale(_:)", containing: "Int64");
        var open = Symbol("Base", "value()");

        var result = Probe([scale64, open]);

        Assert.Equal(Defined(scale64), Shown(result, scale64));
        Assert.DoesNotContain(result.Modules, m => m.Declarations.ContainsKey(scale32));
        Assert.Equal(SwiftLoweringProbe.ThroughAnotherEntryReason(open + "Tj"), result.Unseen[open]);
        Assert.Equal("other", SwiftLoweringProbe.ReasonCategory(result.Unseen[open]));
    }

    [SkippableFact]
    public void Run_ATargetNoClientCallsByItsSymbol_IsUnobservedWithItsReason()
    {
        Skip.IfNot(SwiftLoweringOracle.IsAvailable, "xcrun and an iOS simulator SDK are required");
        var transparent = Symbol(null, "transparent()");
        var hidden = Symbol("Account", "hidden()");
        // An @_alwaysEmitIntoClient member is not part of the module's ABI, so its descriptor leaves it out.
        const string always = "$s8ProbeKit7AccountC6alwayss5Int32VyF";

        var result = Probe([always, transparent, hidden]);

        Assert.Equal(SwiftLoweringProbe.TransparentReason, result.Unseen[transparent]);
        Assert.Equal(SwiftLoweringProbe.UsableFromInlineReason, result.Unseen[hidden]);
        Assert.Equal(SwiftLoweringProbe.NotDeclaredReason, result.Unseen[always]);
        Assert.Equal(3, result.Unseen.Count);
    }

    /// <summary>
    /// A library built for testing lists its internal declarations in its descriptor. No client of its
    /// public interface can name an internal type, a member of one, or an internal member of a public
    /// type: each is unobserved as not public from the interface, with no call written for it, and the
    /// public member is still shown.
    /// </summary>
    [SkippableFact]
    public void Run_AnInternalDeclarationOfATestableDescriptor_IsNotPublicFromTheInterface()
    {
        Skip.IfNot(SwiftLoweringOracle.IsAvailable, "xcrun and an iOS simulator SDK are required");
        var fixture = _testable.Value;
        var shown = SymbolIn(fixture, "Host", "shown()");
        var internals = new[]
        {
            SymbolIn(fixture, "Host", "hidden(_:)"),
            SymbolIn(fixture, "Host", "tally", "get"),
            SymbolIn(fixture, "InternalRecord", "describe(_:)"),
            SymbolIn(fixture, "InternalRecord", "make()"),
        };

        var result = SwiftLoweringProbe.Run(
            new SwiftLoweringProbe.Request(internals.Append(shown).ToList(), "RecordKit", fixture.SearchPath, null, [], fixture.FrontendArgs,
                "RecordKitLoweringProbe", SwiftOracleTarget.SimulatorArm64.Triple, _dir),
            new SystemCommandRunner(), NullLogger.Instance, 300_000);

        Assert.All(internals, t => Assert.Equal("not public from the interface: it is internal, which the public interface leaves out", result.Unseen.GetValueOrDefault(t)));
        Assert.Equal(internals.Length, result.Unseen.Count);
        Assert.NotEmpty(SwiftLoweringEvidenceCollector.Signatures([shown], result.Modules));
        Assert.Equal(1, result.Calls);
        Assert.Equal(1, result.Compiles);
    }

    /// <summary>
    /// A global-actor type's setter, which no other actor may call, is shown from inside its type; the
    /// getter, which the main actor may await, needs no second form.
    /// </summary>
    [SkippableFact]
    public void Run_AGlobalActorTypesSetter_IsShownFromInsideItsType()
    {
        Skip.IfNot(SwiftLoweringOracle.IsAvailable, "xcrun and an iOS simulator SDK are required");
        var setter = Symbol("Isolated", "count", "set");
        var getter = Symbol("Isolated", "count", "get");

        var result = Probe([setter, getter]);

        Assert.True(result.Unseen.IsEmpty, string.Join("\n", result.Unseen.Select(u => $"{u.Key}: {u.Value}")));
        Assert.Equal(Defined(setter), Shown(result, setter));
        Assert.Equal(Defined(getter), Shown(result, getter));
        Assert.Equal(2, result.Compiles);
    }

    /// <summary>
    /// A call that compiles in neither form is dropped with both of the compiler's errors, and every
    /// other call still shows its target.
    /// </summary>
    [SkippableFact]
    public void Run_ACallThatDoesNotCompile_IsDropped_AndTheRestAreStillShown()
    {
        Skip.IfNot(SwiftLoweringOracle.IsAvailable, "xcrun and an iOS simulator SDK are required");
        // A setter isolated on its own to a global actor: the main actor cannot set it, and neither can
        // its nonisolated type.
        var setter = Symbol("Mixed", "count", "set");
        var getter = Symbol("Mixed", "count", "get");
        var scale = Symbol("Account", "scale(_:)", containing: "Int32");

        var result = Probe([setter, getter, scale]);

        Assert.StartsWith("unsupported in the probe: its call did not compile: ", result.Unseen[setter]);
        Assert.Contains("; and from inside its type: ", result.Unseen[setter]);
        Assert.Equal(Defined(getter), Shown(result, getter));
        Assert.Equal(Defined(scale), Shown(result, scale));
        Assert.Equal(3, result.Compiles);
    }

    /// <summary>A probe that cannot compile at all leaves every target unobserved, with why, and no evidence source.</summary>
    [SkippableFact]
    public void Run_AProbeThatCannotCompile_ShowsNothing_AndSaysWhy()
    {
        Skip.IfNot(SwiftLoweringOracle.IsAvailable, "xcrun and an iOS simulator SDK are required");
        var scale = Symbol("Account", "scale(_:)", containing: "Int32");
        var global = Symbol(null, "global(_:)");

        var result = Probe([scale, global], new UnimportableRunner());

        Assert.Null(result.Source);
        Assert.Empty(result.Modules);
        Assert.All(new[] { scale, global }, t => Assert.StartsWith("other: the probe did not compile: ", result.Unseen[t]));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    /// <summary>Answers every probe compile with an error in the probe's header, as an unresolvable import would.</summary>
    private sealed class UnimportableRunner : ICommandRunner
    {
        public (int ExitCode, string StdOut, string StdErr) Run(string command, string arguments, int timeoutMs = 30000)
        {
            if (!arguments.Contains("-emit-ir", StringComparison.Ordinal))
                return new SystemCommandRunner().Run(command, arguments, timeoutMs);
            var file = System.Text.RegularExpressions.Regex.Match(arguments, "\"([^\"]+\\.swift)\"$").Groups[1].Value;
            return (1, "", $"{file}:3:8: error: no such module 'ProbeKit'");
        }
    }

    /// <summary>
    /// A framework that ships its interface but no descriptor, as an Apple SDK framework does: without a
    /// descriptor the probe writes nothing, and with the one the build dumped it shows the target as the
    /// library defines it, importing what the given interface imports.
    /// </summary>
    [SkippableFact]
    public void Run_AFrameworkWithoutADescriptor_IsProbedThroughTheGivenOne()
    {
        Skip.IfNot(SwiftLoweringOracle.IsAvailable, "xcrun and an iOS simulator SDK are required");
        var search = Path.Combine(_dir, "sdk", "Frameworks");
        var modules = Path.Combine(search, "ProbeKit.framework", "Modules", "ProbeKit.swiftmodule");
        Directory.CreateDirectory(modules);
        var shippedInterface = Path.Combine(_fixture.Value.SearchPath, "ProbeKit.framework", "Modules", "ProbeKit.swiftmodule", "arm64-apple-ios-simulator.swiftinterface");
        var interfacePath = Path.Combine(modules, "arm64-apple-ios-simulator.swiftinterface");
        File.Copy(shippedInterface, interfacePath);
        var work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        var target = Symbol(null, "global(_:)");
        SwiftLoweringProbe.Result Run(SwiftLoweringProbe.ExplicitDescriptor? descriptor) =>
            SwiftLoweringProbe.Run(
                new SwiftLoweringProbe.Request([target], "ProbeKit", search, null, [],
                    _fixture.Value.FrontendArgs.Replace(_fixture.Value.SearchPath, search, StringComparison.Ordinal),
                    "ProbeKitLoweringProbe", SwiftOracleTarget.SimulatorArm64.Triple, work, descriptor),
                new SystemCommandRunner(), NullLogger.Instance, 300_000);

        var without = Run(null);
        Assert.Equal(0, without.Compiles);
        Assert.Equal(SwiftLoweringProbe.NoDescriptorReason("ProbeKit"), without.Unseen[target]);

        var with = Run(new SwiftLoweringProbe.ExplicitDescriptor(_fixture.Value.Module.AbiJsonPath, interfacePath));
        Assert.True(with.Unseen.IsEmpty, string.Join("\n", with.Unseen.Select(u => $"{u.Key}: {u.Value}")));
        Assert.Equal(Defined(target), Shown(with, target));
    }

    [Fact]
    public void Run_WithoutADescriptor_ClassifiesEveryTarget_AndCompilesNothing()
    {
        var runner = new MockCommandRunner();
        var result = SwiftLoweringProbe.Run(
            new SwiftLoweringProbe.Request(["$s3Lib1fyyF", "$s3Lib1gyyF"], "Lib", Path.Combine(_dir, "none"), null, [], "-target x ",
                "LibLoweringProbe", "arm64-apple-ios15.0-simulator", _dir),
            runner, NullLogger.Instance, 1000);

        Assert.Equal(0, result.Compiles);
        Assert.Null(result.Source);
        Assert.All(result.Unseen.Values, v => Assert.Equal(SwiftLoweringProbe.NoDescriptorReason("Lib"), v));
        Assert.Equal(2, result.Unseen.Count);
    }

    private static void WriteDescriptor(string searchPath, string module, JsonObject descriptor)
    {
        var modules = Path.Combine(searchPath, module + ".framework", "Modules", module + ".swiftmodule");
        Directory.CreateDirectory(modules);
        File.WriteAllText(Path.Combine(modules, "arm64-apple-ios-simulator.abi.json"), descriptor.ToJsonString());
    }
}
