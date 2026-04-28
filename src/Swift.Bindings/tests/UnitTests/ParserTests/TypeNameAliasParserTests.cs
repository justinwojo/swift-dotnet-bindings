// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for TypeNameAlias parsing — swift-api-digester wraps typealias uses in a
/// TypeNameAlias node whose single child is the underlying TypeNominal (e.g.
/// <c>SHA256.Digest</c> → <c>SHA256Digest</c>). Before this case was handled,
/// CreateTypeSpec threw NotImplementedException and the enclosing member was
/// silently dropped, which is how CryptoKit lost <c>SHA256.finalize()</c> and
/// related Digest-returning methods.
/// </summary>
public class TypeNameAliasParserTests
{
    [Fact]
    public void CreateTypeSpec_TypeNameAlias_UnwrapsToUnderlyingNominal()
    {
        // SHA256.Digest → SHA256Digest shape: TypeNameAlias wrapping a TypeNominal child.
        var underlying = CreateNode(kind: "TypeNominal", name: "SHA256Digest",
            printedName: "CryptoKit.SHA256Digest");
        var alias = CreateAliasNode(name: "Digest", printedName: "CryptoKit.SHA256.Digest", underlying);
        var parser = CreateMinimalParser();

        var result = parser.CreateTypeSpec(alias);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("CryptoKit.SHA256Digest", named.Name);
    }

    [Fact]
    public void CreateTypeSpec_TypeNameAlias_WithGenericChild_PreservesGenerics()
    {
        // HMAC<H>.MAC → HashedAuthenticationCode<H>: alias child is a generic TypeNominal.
        var hParam = CreateNode(kind: "TypeNominal", name: "GenericTypeParam", printedName: "H");
        var underlying = CreateNodeWithChildren(kind: "TypeNominal", name: "HashedAuthenticationCode",
            printedName: "CryptoKit.HashedAuthenticationCode<H>", children: new[] { hParam });
        var alias = CreateAliasNode(name: "MAC", printedName: "CryptoKit.HMAC<H>.MAC", underlying);
        var parser = CreateMinimalParser();

        var result = parser.CreateTypeSpec(alias);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("CryptoKit.HashedAuthenticationCode", named.Name);
        Assert.Single(named.GenericParameters);
        Assert.Equal("H", ((NamedTypeSpec)named.GenericParameters[0]).Name);
    }

    [Fact]
    public void CreateTypeSpec_OptionalOfTypeNameAlias_UnwrapsInnerToUnderlyingNominal()
    {
        // Optional<simd.float4x4> shape: kNominal "Optional" whose only child is a TypeNameAlias
        // pointing at simd.simd_float4x4. TypeSpecParser sees PrintedName "simd.float4x4?" and
        // keeps the alias name as the Optional's inner; without unwrapping, the database lookup
        // falls back to SwiftOptional<IntPtr>. The parser must substitute the alias child to
        // restore the real nominal so it resolves through normal type-database paths.
        var underlying = CreateNode(kind: "TypeNominal", name: "simd_float4x4",
            printedName: "simd.simd_float4x4");
        var alias = CreateAliasNode(name: "float4x4", printedName: "simd.float4x4", underlying);
        var optional = CreateNodeWithChildren(kind: "TypeNominal", name: "Optional",
            printedName: "simd.float4x4?", children: new[] { alias });
        var parser = CreateMinimalParser();

        var result = parser.CreateTypeSpec(optional);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.Optional", named.Name);
        Assert.Single(named.GenericParameters);
        var inner = Assert.IsType<NamedTypeSpec>(named.GenericParameters[0]);
        Assert.Equal("simd.simd_float4x4", inner.Name);
    }

    [Fact]
    public void CreateTypeSpec_TypeNameAlias_NoChildren_Throws()
    {
        // Defensive: a TypeNameAlias without the expected underlying child is malformed.
        var alias = CreateNodeWithChildren(kind: "TypeNameAlias", name: "Broken", printedName: "Broken",
            children: Array.Empty<Node>());
        var parser = CreateMinimalParser();

        var ex = Assert.Throws<Exception>(() => parser.CreateTypeSpec(alias));
        Assert.Contains("TypeNameAlias", ex.Message);
    }

    #region Helpers

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
            NullLogger.Instance);

        File.Delete(filePath);

        return parser;
    }

    private static Node CreateNode(string kind, string name, string printedName)
        => CreateNodeWithChildren(kind, name, printedName, Array.Empty<Node>());

    private static Node CreateNodeWithChildren(string kind, string name, string printedName, IEnumerable<Node> children)
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
            Children = children,
            Conformances = Enumerable.Empty<Node>(),
            Accessors = Enumerable.Empty<Node>()
        };
    }

    private static Node CreateAliasNode(string name, string printedName, Node underlying)
    {
        var children = underlying is null ? Array.Empty<Node>() : new[] { underlying };
        return CreateNodeWithChildren(kind: "TypeNameAlias", name: name, printedName: printedName, children: children);
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
