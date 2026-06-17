// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Regression guard for cb1ff96d: <c>SwiftABIParser.CreateTypeSpec</c> must DEGRADE — not throw —
/// when a swift-api-digester <c>PrintedName</c> is not a single strict type.
///
/// cb1ff96d centralized the type-string grammar around <c>TypeSpecParser</c> and made the canonical
/// entry point EOF-strict: a string that parses to a complete type followed by trailing tokens now
/// throws <see cref="TypeSpecParseException"/> rather than silently returning the leading prefix.
/// That is the right default for the common case, but the digester emits two shapes that are NOT a
/// single nominal:
///   - an opaque parameter <c>some P</c> in a position that does not install the per-method opaque
///     capture (a subscript index param) — tokenizes as name "some" + trailing "P";
///   - a <c>sending</c>-modified closure result, e.g. <c>() -&gt; sending Box</c> — tokenizes as a
///     complete <c>() -&gt; sending</c> type + trailing "Box".
/// Both flow through the shared <c>kNominal</c>/<c>kFunc</c> body of <c>CreateTypeSpec</c> to the
/// EOF-strict <c>Parse</c>. Before the fix the throw propagated to <c>HandleNode</c>'s catch-all and
/// DROPPED the whole enclosing declaration (and its parent <c>init</c>) silently. The fix degrades
/// to the lenient prefix parse so the member survives. R2-F1 reproduces only under the
/// swift-api-digester path (Apple-framework SDK-direct mode); the swift-frontend xcframework path
/// desugars opaque params to <c>τ_0_0</c> and never hits this, which is why this lives as a
/// parser-level unit test rather than a BindingTests fixture.
/// </summary>
public class OpaqueAndSendingDegradeTests
{
    [Fact]
    public void CreateTypeSpec_OpaqueParamWithoutCapture_DegradesInsteadOfThrowing()
    {
        // `some Lib5.Shape` as a subscript index param: digester emits Kind=TypeNominal,
        // Name=GenericTypeParam, printedName="some Lib5.Shape". A freshly constructed parser has
        // no opaque-param capture installed (mirrors the subscript path), so the `some`-divert is
        // skipped and the string reaches the strict Parse. It must degrade, not throw.
        var parser = CreateMinimalParser();
        var node = MakeNode(kind: "TypeNominal", name: "GenericTypeParam", printedName: "some Lib5.Shape");

        var result = parser.CreateTypeSpec(node);

        Assert.NotNull(result);
        var named = Assert.IsType<NamedTypeSpec>(result);
        // The leading-prefix parse of "some Lib5.Shape" yields NamedTypeSpec("some") — broken but
        // present, matching pre-cb1ff96d behavior — rather than dropping the declaration.
        Assert.Equal("some", named.Name);
    }

    [Fact]
    public void CreateTypeSpec_SendingModifiedClosure_DegradesInsteadOfThrowing()
    {
        // `() -> sending Box` closure result: digester emits a TypeFunc node whose printedName
        // carries the un-stripped `sending` ownership modifier. It shares the kNominal/kFunc body
        // and reaches the strict Parse, which throws "Unexpected trailing token 'Lib.Box'". The
        // degrade path must keep the closure-typed member instead of dropping it (and its parent).
        var parser = CreateMinimalParser();
        var node = MakeNode(kind: "TypeFunc", name: "", printedName: "() -> sending Lib.Box");

        var result = parser.CreateTypeSpec(node);

        Assert.NotNull(result);
        Assert.IsType<ClosureTypeSpec>(result);
    }

    [Fact]
    public void TypeSpecParser_StrictVsPrefix_DocumentsTheDegradeContract()
    {
        // Pins the contract the degrade path relies on: the strict Parse throws on these shapes
        // (the regression trigger), while ParsePrefix yields the degraded prefix the fix falls back
        // to. If TypeSpecParser ever stops throwing here, the degrade is harmlessly inert; if
        // ParsePrefix starts throwing, CreateTypeSpec would resume dropping decls and this fails.
        Assert.Throws<TypeSpecParseException>(() => TypeSpecParser.Parse("some Lib5.Shape"));
        Assert.Throws<TypeSpecParseException>(() => TypeSpecParser.Parse("() -> sending Lib.Box"));

        var degradedOpaque = Assert.IsType<NamedTypeSpec>(TypeSpecParser.ParsePrefix("some Lib5.Shape"));
        Assert.Equal("some", degradedOpaque.Name);
        Assert.IsType<ClosureTypeSpec>(TypeSpecParser.ParsePrefix("() -> sending Lib.Box"));
    }

    #region Helpers

    private static Node MakeNode(string kind, string name, string printedName)
    {
        return new Node
        {
            Kind = kind,
            DeclKind = "",
            Name = name,
            MangledName = "",
            PrintedName = printedName,
            ModuleName = "",
            DeclAttributes = Array.Empty<string>(),
            @static = null,
            IsInternal = null,
            GenericSig = null,
            sugared_genericSig = null,
            throwing = null,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            Children = Enumerable.Empty<Node>(),
            Conformances = Enumerable.Empty<Node>(),
            Accessors = Enumerable.Empty<Node>(),
        };
    }

    private static SwiftABIParser CreateMinimalParser()
    {
        var abiJson = JsonConvert.SerializeObject(new
        {
            ABIRoot = new
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                Children = new object[]
                {
                    new
                    {
                        Kind = "TypeDecl",
                        DeclKind = "Module",
                        Name = "TestModule",
                        MangledName = "",
                        PrintedName = "TestModule",
                        ModuleName = "TestModule",
                        DeclAttributes = new string[0],
                        @static = false,
                        IsInternal = false,
                        GenericSig = "",
                        sugared_genericSig = "",
                        throwing = false,
                        AccessorKind = "",
                        EnumRawTypeName = "",
                        paramValueOwnership = "",
                        hasDefaultArg = false,
                        Children = new object[0],
                        Conformances = new object[0],
                        Accessors = new object[0],
                    },
                },
            },
        });

        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, abiJson);

        var parser = new SwiftABIParser(
            filePath,
            new TypeDatabase(),
            CreateEmptyDemanglingResults(),
            NullLogger.Instance,
            SwiftInterfaceFacts.Empty);

        File.Delete(filePath);

        return parser;
    }

    private static BindingsGeneration.Demangling.DemanglingResults CreateEmptyDemanglingResults()
    {
        var ctor = typeof(BindingsGeneration.Demangling.DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            new[] { typeof(BindingsGeneration.Demangling.IReduction[]), typeof(HashSet<string>) },
            modifiers: null);
        if (ctor == null)
            throw new InvalidOperationException("Could not find DemanglingResults constructor");
        return (BindingsGeneration.Demangling.DemanglingResults)ctor.Invoke(
            new object[] { Array.Empty<BindingsGeneration.Demangling.IReduction>(), new HashSet<string>() });
    }

    #endregion
}
