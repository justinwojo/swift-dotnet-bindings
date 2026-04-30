// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for SwiftABIParser.CreateTypeSpec variadic handling.
///
/// When a variadic parameter (T...) uses a generic type parameter as the element
/// type, swift-api-digester emits a TypeNominal Array node with printedName "T..."
/// instead of the usual "[ModuleName.T]" bracket form used for concrete element
/// types. TypeSpecParser treats '.' as a valid in-name character (so module-qualified
/// names like "Swift.Int32" tokenize as a single name), which means Parse("T...")
/// silently produces NamedTypeSpec("T...") rather than failing.
///
/// The malformed name then crashes downstream validators: HasModule() returns true
/// because the name contains '.', and SwiftTypeName.FromModuleQualifiedName throws
/// "Invalid module-qualified name: T..." when it tries to split on the dots.
///
/// CreateTypeSpec now detects the variadic shape (Name=="Array" &amp;&amp; printedName ends
/// with "...") and rebuilds the spec from the child node, producing the canonical
/// demangler shape: Swift.Array&lt;T&gt; with IsVariadic=true on the inner element.
/// This lets HasVariadicElement fire downstream so the method/constructor is skipped
/// by the wrapper emitters.
///
/// Hit in the wild by MusicKit's <c>MusicItemCollection.init(arrayLiteral: MusicItemType...)</c>.
/// </summary>
public class VariadicTypeSpecCreationTests
{
    [Fact]
    public void VariadicArray_GenericElementType_BuildsSwiftArrayWithVariadicElement()
    {
        var parser = CreateMinimalParser();
        var node = CreateArrayVariadicNode("MusicItemType");

        var result = parser.CreateTypeSpec(node);

        var array = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.Array", array.Name);
        Assert.Single(array.GenericParameters);

        var element = Assert.IsType<NamedTypeSpec>(array.GenericParameters[0]);
        Assert.Equal("MusicItemType", element.Name);
        Assert.True(element.IsVariadic);
    }

    [Fact]
    public void VariadicArray_ProducesShapeHasVariadicElementRecognizes()
    {
        // End-to-end: the rebuilt spec must be the exact shape that
        // HasVariadicElement walks, so the wrapper emitters see HasVariadicParameter=true
        // and skip the method instead of crashing in the marshaler.
        var parser = CreateMinimalParser();
        var node = CreateArrayVariadicNode("MusicItemType");

        var result = parser.CreateTypeSpec(node);
        var tuple = new TupleTypeSpec(new TypeSpec[] { result });

        Assert.True(SwiftABIParser.HasVariadicElement(tuple));
    }

    [Fact]
    public void VariadicArray_DoesNotAffectRegularArray()
    {
        // Guard against regressing the normal "[Swift.Int32]" array path.
        var parser = CreateMinimalParser();
        var node = new Node
        {
            Kind = "TypeNominal",
            DeclKind = "",
            Name = "Array",
            MangledName = "",
            PrintedName = "[Swift.Int32]",
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
                    Name = "Int32",
                    MangledName = "",
                    PrintedName = "Swift.Int32",
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

        var result = parser.CreateTypeSpec(node);
        var array = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.Array", array.Name);
        Assert.Single(array.GenericParameters);
        var element = Assert.IsType<NamedTypeSpec>(array.GenericParameters[0]);
        Assert.Equal("Swift.Int32", element.Name);
        Assert.False(element.IsVariadic);
    }

    #region Helpers

    private static Node CreateArrayVariadicNode(string elementTypeName)
    {
        // Mirrors swift-api-digester's emission for a variadic parameter whose
        // element type is a generic type parameter (e.g., init(values: T...) on
        // a generic struct). The child is a GenericTypeParam with the bare name.
        return new Node
        {
            Kind = "TypeNominal",
            DeclKind = "",
            Name = "Array",
            MangledName = "",
            PrintedName = $"{elementTypeName}...",
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
                    Name = "GenericTypeParam",
                    MangledName = "",
                    PrintedName = elementTypeName,
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
