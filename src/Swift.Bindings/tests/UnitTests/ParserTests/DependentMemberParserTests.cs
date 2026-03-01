// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for DependentMember parsing fix — verifies that CreateTypeSpec correctly
/// produces AssociatedTypeReferenceSpec for TypeNominal nodes with Name="DependentMember".
/// </summary>
public class DependentMemberParserTests
{
    [Fact]
    public void CreateTypeSpec_DependentMember_ProducesAssociatedTypeReferenceSpec()
    {
        // DependentMember nodes in ABI JSON appear as Kind="TypeNominal", Name="DependentMember"
        var node = CreateNode(kind: "TypeNominal", name: "DependentMember", printedName: "τ_0_0.Element");
        var parser = CreateMinimalParser();

        var result = parser.CreateTypeSpec(node);

        var assocRef = Assert.IsType<AssociatedTypeReferenceSpec>(result);
        Assert.Equal("τ_0_0", assocRef.BaseType);
        Assert.Equal("Element", assocRef.AssociatedTypeName);
    }

    [Fact]
    public void CreateTypeSpec_DependentMember_SelfIterator_ProducesCorrectSpec()
    {
        var node = CreateNode(kind: "TypeNominal", name: "DependentMember", printedName: "Self.Iterator");
        var parser = CreateMinimalParser();

        var result = parser.CreateTypeSpec(node);

        var assocRef = Assert.IsType<AssociatedTypeReferenceSpec>(result);
        Assert.Equal("Self", assocRef.BaseType);
        Assert.Equal("Iterator", assocRef.AssociatedTypeName);
    }

    [Fact]
    public void CreateTypeSpec_RegularNominal_StillProducesNamedTypeSpec()
    {
        // Regular nominal types should still work as before
        var node = CreateNode(kind: "TypeNominal", name: "String", printedName: "Swift.String");
        var parser = CreateMinimalParser();

        var result = parser.CreateTypeSpec(node);

        var namedSpec = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.String", namedSpec.Name);
    }

    [Fact]
    public void CreateTypeSpec_OpaqueTypeArchetype_StillHandled()
    {
        // OpaqueTypeArchetype should still take its dedicated code path
        var node = CreateNode(kind: "TypeNominal", name: "OpaqueTypeArchetype",
            printedName: "some TestModule.MyProtocol");
        var parser = CreateMinimalParser();

        var result = parser.CreateTypeSpec(node);

        // OpaqueTypeArchetype produces a ProtocolListTypeSpec with IsOpaque
        var protocolList = Assert.IsType<ProtocolListTypeSpec>(result);
        Assert.True(protocolList.IsOpaque);
    }

    #region Helpers

    private static SwiftABIParser CreateMinimalParser()
    {
        // Create a minimal valid ABI JSON for the parser constructor
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

        // Clean up temp file after parser reads it
        File.Delete(filePath);

        return parser;
    }

    private static Node CreateNode(string kind, string name, string printedName)
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
            Accessors = Enumerable.Empty<Node>()
        };
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
