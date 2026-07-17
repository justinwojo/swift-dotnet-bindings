// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for SwiftABIParser.CreateTypeSpec implicitly-unwrapped-optional handling.
///
/// swift-api-digester renders an IUO member's type with a `?` and gives it Optional's USR
/// ("s:Sq"), so neither printedName nor the USR distinguishes `T!` from `T?`. The ONLY surviving
/// signal is the type node's structural name, which is literally "ImplicitlyUnwrappedOptional".
///
/// The distinction is not cosmetic. Swift's conformance checker rejects a `T?` witness for a `T!`
/// requirement — "candidate has non-matching type" — so a synthesized EveryProtocol stub for a
/// protocol declaring `var backgroundView: UIView! { get }` fails to compile if the witness renders
/// as `UIView?`. Observed against SwiftMessages (BackgroundViewable.backgroundView), Hero
/// (HeroPreprocessor.hero) and Eureka (BaseRowType.baseCell).
///
/// The spec keeps its Swift.Optional identity — IUO and Optional are the same type with the same
/// layout and the same C# projection — and carries the spelling as a marker flag, mirroring how
/// IsVariadic marks an otherwise ordinary Swift.Array.
/// </summary>
public class ImplicitlyUnwrappedOptionalTypeSpecTests
{
    [Fact]
    public void ImplicitlyUnwrappedOptional_Node_MarksSpecWithoutChangingItsType()
    {
        var parser = CreateMinimalParser();
        var node = CreateOptionalNode("ImplicitlyUnwrappedOptional", "UIKit.UIView?");

        var result = parser.CreateTypeSpec(node);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.Optional", named.Name);
        Assert.True(named.IsImplicitlyUnwrappedOptional);
    }

    [Fact]
    public void PlainOptional_Node_IsNotMarked()
    {
        // Guard the far more common path: a real `T?` must not start rendering as `T!`.
        var parser = CreateMinimalParser();
        var node = CreateOptionalNode("Optional", "UIKit.UIView?");

        var result = parser.CreateTypeSpec(node);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.Optional", named.Name);
        Assert.False(named.IsImplicitlyUnwrappedOptional);
    }

    [Theory]
    [InlineData(true, "(UIKit.UIView)!")]
    [InlineData(false, "(UIKit.UIView)?")]
    public void DeclarationRendering_TakesTheSigilFromTheMarker(bool iuo, string expected)
    {
        Assert.Equal(expected, SwiftTypeNameHelper.GetSwiftTypeNameForDeclaration(BuildOptional(iuo)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GeneralRendering_IsAlwaysPlainOptional_EvenWhenMarked(bool iuo)
    {
        // The marker must NOT leak out of declaration positions. This renderer also feeds generic
        // arguments (UnsafeMutablePointer<T>.allocate, CheckedContinuation<T, _>) and vtable dedup
        // keys; `!` is a syntax error in the former ("using '!' is not allowed here") and would
        // split one Swift type across two keys in the latter.
        Assert.Equal("(UIKit.UIView)?", SwiftTypeNameHelper.GetSwiftTypeName(BuildOptional(iuo)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MetatypeRendering_IsAlwaysPlainOptional_EvenWhenMarked(bool iuo)
    {
        // `assumingMemoryBound(to: (UIKit.UIView)!.self)` does not compile. The witness body reads
        // the value back through a metatype, so this is the sibling of every declaration the
        // marker DOES apply to — regression-locking it keeps the two halves from drifting.
        Assert.Equal("(UIKit.UIView)?", SwiftTypeNameHelper.GetSwiftTypeNameForMetatype(BuildOptional(iuo)));
    }

    [Fact]
    public void ImplicitlyUnwrappedOptional_SurvivesParseToSwiftDeclarationRendering()
    {
        // End-to-end over the two hops that matter: the parser marks the spec from the ABI node,
        // and the declaration renderer turns the marker back into the `!` the protocol declared.
        // These are the only two places the spelling exists — the C# side never sees it.
        var parser = CreateMinimalParser();
        var node = CreateOptionalNode("ImplicitlyUnwrappedOptional", "UIKit.UIView?");

        var rendered = SwiftTypeNameHelper.GetSwiftTypeNameForDeclaration(parser.CreateTypeSpec(node));

        Assert.Equal("(UIKit.UIView)!", rendered);
    }

    [Fact]
    public void DeclarationRendering_LeavesNonOptionalsAlone()
    {
        Assert.Equal("UIKit.UIView", SwiftTypeNameHelper.GetSwiftTypeNameForDeclaration(new NamedTypeSpec("UIKit.UIView")));
    }

    private static NamedTypeSpec BuildOptional(bool iuo)
    {
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(new NamedTypeSpec("UIKit.UIView"));
        optional.IsImplicitlyUnwrappedOptional = iuo;
        return optional;
    }

    #region Helpers

    // Mirrors swift-api-digester's emission for an optional-typed member. For an IUO the `name`
    // field reads "ImplicitlyUnwrappedOptional" while printedName still renders with `?` and the
    // USR is Optional's — verbatim shape observed in SwiftMessages' abi.json for
    // BackgroundViewable.backgroundView.
    private static Node CreateOptionalNode(string nodeName, string printedName)
    {
        var innerPrinted = printedName.TrimEnd('?');
        return new Node
        {
            Kind = "TypeNominal",
            DeclKind = "",
            Name = nodeName,
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
            Children = new[]
            {
                new Node
                {
                    Kind = "TypeNominal",
                    DeclKind = "",
                    Name = innerPrinted.Contains('.') ? innerPrinted.Split('.').Last() : innerPrinted,
                    MangledName = "",
                    PrintedName = innerPrinted,
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
                    Accessors = Enumerable.Empty<Node>()
                }
            },
            Conformances = Enumerable.Empty<Node>(),
            Accessors = Enumerable.Empty<Node>()
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
                        Accessors = new object[0]
                    }
                }
            }
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
