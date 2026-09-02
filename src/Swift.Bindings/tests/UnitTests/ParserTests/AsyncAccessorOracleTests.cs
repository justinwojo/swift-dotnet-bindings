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
/// Coverage for accessor async-ness detection, which has no direct representation in the
/// ABI JSON: accessor nodes carry <c>throwing</c> but no async flag, and an async accessor's
/// mangled name is a plain <c>…vg</c> with no <c>Ya</c> marker.
///
/// Two independent oracles answer the question and either one saying "async" wins:
///   1. the TBD symbol table, via a sibling <c>{getter}Tu</c> (or <c>{getter}TjTu</c>) symbol;
///   2. the swiftinterface, via the <c>AsyncAccessorMembers</c> fact set, which is keyed by
///      the full type-qualified path spelled the way Swift source spells it.
///
/// Reading only the TBD makes the answer hostage to the symbol set being complete — a stub
/// library shipped without one, or a .tbd shape the parser reads as empty, silently turns
/// every <c>get async</c> property into a synchronous one. For <c>get async throws</c> that
/// is worse than a compile break: the property lands on a direct CallConvSwift P/Invoke with
/// a <c>ref SwiftError</c> out-param aimed at an async entry point, which compiles and then
/// mismatches the ABI on the first read.
/// </summary>
public class AsyncAccessorOracleTests
{
    private const string LabelGetterSymbol = "$s10TestModule8AnalyzerV5labelSSvg";
    private const string PixelsGetterSymbol = "$s10TestModule8AnalyzerV6RegionV6pixelss5Int32Vvg";
    private const string StaticLabelGetterSymbol = "$s10TestModule8AnalyzerV5labelSSvgZ";
    private const string EventHandlerGetterSymbol = "$s10TestModule5eventV7handlers5Int32Vvg";

    [Fact]
    public void Getter_InterfaceFactAloneSaysAsync_EmptyTbd_MarkedAsync()
    {
        // The .swiftinterface oracle standing alone. This is the case the TBD probe cannot
        // answer: no symbols at all, which is indistinguishable from "synchronous" to it.
        using var fixture = CreateFixture(
            tbdSymbols: new HashSet<string>(),
            asyncAccessorMembers: new HashSet<string> { "Analyzer.label" });

        var getter = GetLabelGetter(fixture);

        Assert.True(getter.Method.IsAsync,
            "The swiftinterface fact must be sufficient on its own — a missing or empty TBD " +
            "symbol set otherwise silently demotes `get async` to a synchronous getter.");
    }

    [Fact]
    public void Getter_TbdProbeAloneSaysAsync_NoInterfaceFact_MarkedAsync()
    {
        // The TBD oracle standing alone — the pre-existing behavior, which must survive the
        // addition of the interface fact. Reached whenever no .swiftinterface is available
        // (dependency modules) or the walker could not render the member's key.
        using var fixture = CreateFixture(
            tbdSymbols: new HashSet<string> { LabelGetterSymbol + "Tu" },
            asyncAccessorMembers: new HashSet<string>());

        var getter = GetLabelGetter(fixture);

        Assert.True(getter.Method.IsAsync,
            "The TBD `{getter}Tu` sibling symbol must still mark the getter async on its own.");
    }

    [Fact]
    public void Getter_BothOraclesAgreeAsync_MarkedAsync()
    {
        using var fixture = CreateFixture(
            tbdSymbols: new HashSet<string> { LabelGetterSymbol + "Tu" },
            asyncAccessorMembers: new HashSet<string> { "Analyzer.label" });

        Assert.True(GetLabelGetter(fixture).Method.IsAsync);
    }

    [Fact]
    public void Getter_NeitherOracleSaysAsync_StaysSynchronous()
    {
        // The negative control: neither oracle fires, so a genuinely synchronous getter must
        // NOT be promoted to async. Without this the OR could be satisfied by anything.
        using var fixture = CreateFixture(
            tbdSymbols: new HashSet<string> { LabelGetterSymbol },
            asyncAccessorMembers: new HashSet<string>());

        Assert.False(GetLabelGetter(fixture).Method.IsAsync,
            "A synchronous getter must stay synchronous — neither oracle fired.");
    }

    [Fact]
    public void Getter_InterfaceFactKeyedByOtherMember_DoesNotLeak()
    {
        // Key matching must be exact. A fact for a different property on the same type must
        // not mark this one async — the fact set is a flat string set, so a sloppy
        // prefix/suffix match would cross-contaminate siblings.
        using var fixture = CreateFixture(
            tbdSymbols: new HashSet<string>(),
            asyncAccessorMembers: new HashSet<string> { "Analyzer.labelSuffix", "Other.label" });

        Assert.False(GetLabelGetter(fixture).Method.IsAsync,
            "Only the member's own fully-qualified key may mark it async.");
    }

    [Fact]
    public void Getter_NestedType_InterfaceFactUsesFullChainKey()
    {
        // The fact key shape is the full nested chain (module prefix stripped), matching
        // BuildTypeQualifiedPath. A last-dot-only key shape would miss every nested type.
        using var fixture = CreateFixture(
            tbdSymbols: new HashSet<string>(),
            asyncAccessorMembers: new HashSet<string> { "Analyzer.Region.pixels" });

        var getter = GetNestedPixelsGetter(fixture);

        Assert.True(getter.Method.IsAsync,
            "A nested type's async accessor is keyed by its full `Outer.Inner.member` chain.");
    }

    [Fact]
    public void Getter_NestedType_BareMemberKeyDoesNotMatch()
    {
        // Guards the pairing above from the other side: the unqualified member name must NOT
        // match a nested member, or a module-level `pixels` fact would leak into every type.
        using var fixture = CreateFixture(
            tbdSymbols: new HashSet<string>(),
            asyncAccessorMembers: new HashSet<string> { "pixels", "Region.pixels" });

        Assert.False(GetNestedPixelsGetter(fixture).Method.IsAsync,
            "A nested member must not be matched by a bare or partially-qualified key.");
    }

    [Fact]
    public void Getter_StaticProperty_InterfaceFactUsesStaticPrefixedKey()
    {
        // A type-level property lives in its own key namespace, because Swift lets a static
        // and an instance property share a name and only one of the two getters may be async.
        using var fixture = CreateFixture(
            tbdSymbols: new HashSet<string>(),
            asyncAccessorMembers: new HashSet<string> { "static Analyzer.label" });

        Assert.True(GetStaticLabelGetter(fixture).Method.IsAsync,
            "A static property's async accessor is keyed with the `static ` prefix.");
    }

    [Fact]
    public void Getter_StaticAsyncFact_DoesNotMarkInstanceNamesakeAsync()
    {
        // The defect the prefix exists to prevent: `static var label { get async }` alongside a
        // synchronous instance `var label`. A shared key would project the instance property as
        // a Task-returning method aimed at an entry point that does not exist, and drop its
        // setter along the way.
        using var fixture = CreateFixture(
            tbdSymbols: new HashSet<string>(),
            asyncAccessorMembers: new HashSet<string> { "static Analyzer.label" });

        Assert.False(GetLabelGetter(fixture).Method.IsAsync,
            "A static property's fact must not reach its synchronous instance namesake.");
    }

    [Fact]
    public void Getter_StaticProperty_UnprefixedKeyDoesNotMatch()
    {
        // The pairing from the other side: an instance-shaped key must not reach the static
        // getter either, or the two namespaces are only half-separated.
        using var fixture = CreateFixture(
            tbdSymbols: new HashSet<string>(),
            asyncAccessorMembers: new HashSet<string> { "Analyzer.label" });

        Assert.False(GetStaticLabelGetter(fixture).Method.IsAsync,
            "An instance-keyed fact must not reach the static getter.");
    }

    [Fact]
    public void Getter_KeywordNamedType_SwiftSpelledKeyMatches()
    {
        // Swift's `struct event` is stored on the decl as `_event`, because the parser escapes
        // C# keywords for the emitted name. The fact set is keyed by Swift identifiers, so the
        // lookup has to un-escape that back: without it every keyword-named type — `event`,
        // `class`, `default` — silently loses this oracle and falls back to the TBD alone.
        using var fixture = CreateFixture(
            tbdSymbols: new HashSet<string>(),
            asyncAccessorMembers: new HashSet<string> { "event.handler" });

        Assert.True(GetEventHandlerGetter(fixture).Method.IsAsync,
            "A keyword-named Swift type's accessor is keyed by the Swift spelling of the type.");
    }

    [Fact]
    public void Getter_KeywordNamedType_CSharpEscapedKeyDoesNotMatch()
    {
        // The pairing from the other side. The producer never sees C# keyword escaping, so a
        // `_event.handler` key can only come from a fact set that drifted out of the Swift
        // spelling — matching it would paper over exactly that drift.
        using var fixture = CreateFixture(
            tbdSymbols: new HashSet<string>(),
            asyncAccessorMembers: new HashSet<string> { "_event.handler" });

        Assert.False(GetEventHandlerGetter(fixture).Method.IsAsync,
            "The C#-escaped spelling is not the fact key shape and must not match.");
    }

    #region Test Helpers

    private static GetAccessorDecl GetEventHandlerGetter(ParserFixture fixture)
    {
        // Stored under the escaped name: the parser prefixes C# keywords with "_".
        var eventType = fixture.Result.ModuleDecl.Types.Single(t => t.Name == "_event");
        var property = eventType.Properties.Single(p => p.Name == "handler");
        return property.Accessors.OfType<GetAccessorDecl>().Single();
    }

    private static GetAccessorDecl GetLabelGetter(ParserFixture fixture)
    {
        var analyzer = fixture.Result.ModuleDecl.Types.Single(t => t.Name == "Analyzer");
        var property = analyzer.Properties.Single(p => p.Name == "label" && !p.IsStatic);
        return property.Accessors.OfType<GetAccessorDecl>().Single();
    }

    private static GetAccessorDecl GetStaticLabelGetter(ParserFixture fixture)
    {
        var analyzer = fixture.Result.ModuleDecl.Types.Single(t => t.Name == "Analyzer");
        var property = analyzer.Properties.Single(p => p.Name == "label" && p.IsStatic);
        return property.Accessors.OfType<GetAccessorDecl>().Single();
    }

    private static GetAccessorDecl GetNestedPixelsGetter(ParserFixture fixture)
    {
        var analyzer = fixture.Result.ModuleDecl.Types.Single(t => t.Name == "Analyzer");
        var region = analyzer.Types.Single(t => t.Name == "Region");
        var property = region.Properties.Single(p => p.Name == "pixels");
        return property.Accessors.OfType<GetAccessorDecl>().Single();
    }

    /// <summary>
    /// Builds a two-level fixture module:
    /// <code>
    /// public struct Analyzer {
    ///     public var label: Swift.String { get }
    ///     public static var label: Swift.String { get }
    ///     public struct Region { public var pixels: Swift.Int32 { get } }
    /// }
    /// public struct event { public var handler: Swift.Int32 { get } }
    /// </code>
    /// with both oracles under test control. The static/instance name pair is legal Swift and
    /// exports two separate getters, so it is the shape that proves the fact keys stay apart.
    /// <c>event</c> is a legal Swift type name and a C# keyword, so it is the shape that proves
    /// the lookup speaks Swift rather than the escaped name the decl is stored under.
    /// </summary>
    private static ParserFixture CreateFixture(
        HashSet<string> tbdSymbols,
        HashSet<string> asyncAccessorMembers)
    {
        var labelProperty = CreatePropertyNode(
            name: "label",
            typeName: "String",
            getterMangledName: LabelGetterSymbol);

        var staticLabelProperty = CreatePropertyNode(
            name: "label",
            typeName: "String",
            getterMangledName: StaticLabelGetterSymbol,
            isStatic: true);

        var pixelsProperty = CreatePropertyNode(
            name: "pixels",
            typeName: "Int32",
            getterMangledName: PixelsGetterSymbol);

        var regionNode = CreateNode(kind: "TypeDecl", declKind: "Struct", name: "Region",
            mangledName: "$s10TestModule8AnalyzerV6RegionV");
        regionNode.Children = new[] { pixelsProperty };

        var analyzerNode = CreateNode(kind: "TypeDecl", declKind: "Struct", name: "Analyzer",
            mangledName: "$s10TestModule8AnalyzerV");
        analyzerNode.Children = new[] { labelProperty, staticLabelProperty, regionNode };

        var handlerProperty = CreatePropertyNode(
            name: "handler",
            typeName: "Int32",
            getterMangledName: EventHandlerGetterSymbol);

        var eventNode = CreateNode(kind: "TypeDecl", declKind: "Struct", name: "event",
            mangledName: "$s10TestModule5eventV");
        eventNode.Children = new[] { handlerProperty };

        var importNode = CreateNode(kind: "Import", name: "TestModule");

        var root = new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                Children = new[] { importNode, analyzerNode, eventNode },
            },
        };

        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, JsonConvert.SerializeObject(root));

        var facts = SwiftInterfaceFacts.Empty with { AsyncAccessorMembers = asyncAccessorMembers };

        var parser = new SwiftABIParser(
            filePath,
            new TypeDatabase(),
            CreateDemanglingResults(tbdSymbols),
            NullLogger<SwiftABIParser>.Instance,
            facts);

        return new ParserFixture(parser.ParseModule(), filePath);
    }

    private static Node CreatePropertyNode(
        string name,
        string typeName,
        string getterMangledName,
        bool isStatic = false)
    {
        var getter = CreateNode(kind: "Function", declKind: "Accessor", name: name,
            mangledName: getterMangledName);
        getter.AccessorKind = "get";
        getter.@static = isStatic;
        getter.Children = new[] { CreateTypeNominalNode(typeName) };

        var property = CreateNode(kind: "Var", declKind: "Var", name: name,
            mangledName: getterMangledName + "p");
        property.DeclAttributes = new[] { "HasStorage" };
        property.@static = isStatic;
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

    #endregion
}
