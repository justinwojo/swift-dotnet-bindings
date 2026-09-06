// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BindingsGeneration.Demangling;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Parser coverage for Swift's <c>@_alignment(N)</c> on a struct or an enum.
///
/// <para>
/// The ABI descriptor records the attribute's bare presence as a valueless <c>Alignment</c> entry
/// in <c>declAttributes</c> — N itself never appears anywhere in the dump. That makes such a
/// struct's inline layout underivable from its declaration: the raised alignment moves both its
/// interior padding and the offset it takes inside a container, so a field walk would confidently
/// produce a width smaller than the real one. The parser's job here is only to surface the fact;
/// the layout derivation reads it and declines.
/// </para>
/// </summary>
public class CustomAlignmentParserTests
{
    [Fact]
    public void ParseModule_AlignmentAttribute_SetsHasCustomAlignment()
    {
        var structDecl = ParseSingleStruct(["Frozen", "AccessControl", "Alignment"]);

        Assert.True(structDecl.HasCustomAlignment);
    }

    [Fact]
    public void ParseModule_NoAlignmentAttribute_LeavesHasCustomAlignmentFalse()
    {
        // The overwhelmingly common shape. Reading it as custom-aligned would mark every ordinary
        // frozen struct's layout indeterminate and skip the Buffer projections that work today.
        var structDecl = ParseSingleStruct(["Frozen", "AccessControl"]);

        Assert.False(structDecl.HasCustomAlignment);
    }

    [Fact]
    public void ParseModule_AlignmentAttribute_LeavesTheStructsOtherFactsIntact()
    {
        // The read must be additive: a custom-aligned struct is still frozen and still parses its
        // name and stored property the same way as its ordinary sibling.
        var structDecl = ParseSingleStruct(["Frozen", "AccessControl", "Alignment"]);

        Assert.True(structDecl.IsFrozen);
        Assert.Equal("Aligned", structDecl.Name);
        Assert.Contains(structDecl.Properties, p => p.Name == "value");
    }

    [Fact]
    public void ParseModule_EnumAlignmentAttribute_SetsHasCustomAlignment()
    {
        // `@_alignment` is spelled the same way on an enum, and an over-aligned payload enum stored
        // in a frozen struct mis-places exactly as an over-aligned struct does — so the enum path
        // reads the attribute too rather than relying on the struct path alone.
        var enumDecl = ParseSingleEnum(["Frozen", "AccessControl", "Alignment"]);

        Assert.True(enumDecl.HasCustomAlignment);
    }

    [Fact]
    public void ParseModule_EnumNoAlignmentAttribute_LeavesHasCustomAlignmentFalse()
    {
        var enumDecl = ParseSingleEnum(["Frozen", "AccessControl"]);

        Assert.False(enumDecl.HasCustomAlignment);
    }

    #region Test Helpers

    /// <summary>
    /// Parses a single case-less enum with the given <c>declAttributes</c>.
    /// </summary>
    private static EnumDecl ParseSingleEnum(string[] declAttributes)
    {
        var enumNode = new Node
        {
            Kind = "TypeDecl",
            DeclKind = "Enum",
            Name = "AlignedEnum",
            PrintedName = "AlignedEnum",
            ModuleName = "TestModule",
            MangledName = "$s10TestModule11AlignedEnumON",
            DeclAttributes = declAttributes,
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
            Accessors = []
        };

        using var fixture = CreateParserWithNodes(enumNode);
        var result = fixture.Parser.ParseModule();

        return Assert.IsType<EnumDecl>(Assert.Single(result.ModuleDecl.Types));
    }

    /// <summary>
    /// Parses a single struct carrying one stored <c>Int64</c> property, with the given
    /// <c>declAttributes</c>, and returns its declaration.
    /// </summary>
    private static StructDecl ParseSingleStruct(string[] declAttributes)
    {
        var propertyType = new Node
        {
            Kind = "TypeNominal",
            DeclKind = "",
            Name = "Int64",
            PrintedName = "Swift.Int64",
            ModuleName = "Swift",
            MangledName = "$ss5Int64V",
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
            Accessors = []
        };

        var varNode = new Node
        {
            Kind = "Var",
            DeclKind = "Var",
            Name = "value",
            PrintedName = "value",
            ModuleName = "TestModule",
            MangledName = "$s10TestModule7AlignedV5values5Int64Vvp",
            DeclAttributes = ["HasStorage", "AccessControl"],
            @static = false,
            IsInternal = false,
            GenericSig = null,
            sugared_genericSig = null,
            throwing = false,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            Children = new[] { propertyType },
            Conformances = [],
            Accessors = []
        };

        var structNode = new Node
        {
            Kind = "TypeDecl",
            DeclKind = "Struct",
            Name = "Aligned",
            PrintedName = "Aligned",
            ModuleName = "TestModule",
            MangledName = "$s10TestModule7AlignedVN",
            DeclAttributes = declAttributes,
            @static = false,
            IsInternal = false,
            GenericSig = null,
            sugared_genericSig = null,
            throwing = false,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            Children = new[] { varNode },
            Conformances = [],
            Accessors = []
        };

        using var fixture = CreateParserWithNodes(structNode);
        var result = fixture.Parser.ParseModule();

        return Assert.IsType<StructDecl>(Assert.Single(result.ModuleDecl.Types));
    }

    private static ParserFixture CreateParserWithNodes(params Node[] nodes)
    {
        var root = new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                Children = nodes
            }
        };

        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, JsonConvert.SerializeObject(root));

        var parser = new SwiftABIParser(
            filePath,
            new TypeDatabase(),
            CreateEmptyDemanglingResults(),
            NullLogger.Instance,
            SwiftInterfaceFacts.Empty);

        return new ParserFixture(parser, filePath);
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

    private sealed class ParserFixture : System.IDisposable
    {
        public ParserFixture(SwiftABIParser parser, string filePath)
        {
            Parser = parser;
            _filePath = filePath;
        }

        public SwiftABIParser Parser { get; }
        private readonly string _filePath;

        public void Dispose()
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
    }

    #endregion
}
