// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A type nested in a bound generic (<c>Outer&lt;Payload&gt;.Leaf</c>) reaches the generator as the
/// outer spec carrying the argument with an InnerType chain naming the leaf. Its C# spelling must put
/// the argument on the outer segment (<c>Outer&lt;Payload&gt;.Leaf</c>). The translators that run
/// without a declaration chain to walk (no module context, as in the Optional and tuple container
/// paths) used to append the argument after the leaf, or name the outer instead of the leaf, and the
/// member was withdrawn with CS0305.
/// </summary>
public class NestedInBoundGenericSpellingTests
{
    private const string Module = "TestModule";

    private static TypeDatabase CreateDatabase()
    {
        var database = new TypeDatabase();
        var module = new ModuleTypeDatabase(Module, "/fake/path");
        Register(module, "Outer", TypeRecordKind.Struct);
        Register(module, "Outer.Leaf", TypeRecordKind.Struct);
        Register(module, "Outer.Node", TypeRecordKind.Class);
        Register(module, "Outer.Inner", TypeRecordKind.Struct);
        Register(module, "Payload", TypeRecordKind.Struct);
        Register(module, "Other", TypeRecordKind.Struct);
        database.AddModuleDatabase(module);
        return database;
    }

    private static void Register(ModuleTypeDatabase module, string name, TypeRecordKind kind)
    {
        var swiftName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}");
        module.RegisterType(swiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(Module, name),
            SwiftTypeName = swiftName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.None,
            Kind = kind
        });
    }

    private static NamedTypeSpec Named(string name) => new($"{Module}.{name}");

    /// <summary><c>Outer&lt;Payload&gt;.{leaf}</c> as the ABI parser encodes it.</summary>
    private static NamedTypeSpec NestedInOuter(string leaf, NamedTypeSpec leafArgument = null)
    {
        var outer = new NamedTypeSpec($"{Module}.Outer", Named("Payload"));
        outer.InnerType = leafArgument == null ? new NamedTypeSpec(leaf) : new NamedTypeSpec(leaf, leafArgument);
        return outer;
    }

    [Theory]
    [InlineData("Leaf")]
    [InlineData("Node")]
    public void BoundGenericsHandler_WithoutModuleContext_PutsArgumentOnOuterSegment(string leaf)
    {
        var handler = new BoundGenericsHandler(CreateDatabase(), conformanceGraph: null, currentModuleName: Module);

        var spelled = handler.TranslateBoundGenericTypeToCSharp(NestedInOuter(leaf), GenericContext.Empty);

        Assert.Equal($"{Module}.Outer<{Module}.Payload>.{leaf}", spelled);
    }

    [Fact]
    public void BoundGenericsHandler_NestedInsideContainerArgument_PutsArgumentOnOuterSegment()
    {
        // The nested reference as a generic argument of another bound generic still goes through the
        // same translator; the outer container itself is an ordinary bound generic.
        var handler = new BoundGenericsHandler(CreateDatabase(), conformanceGraph: null, currentModuleName: Module);
        var container = new NamedTypeSpec($"{Module}.Other", NestedInOuter("Leaf"));

        var spelled = handler.TranslateBoundGenericTypeToCSharp(container, GenericContext.Empty);

        Assert.Equal($"{Module}.Other<{Module}.Outer<{Module}.Payload>.Leaf>", spelled);
    }

    [Fact]
    public void BoundGenericsHandler_PlainBoundGeneric_IsUnchanged()
    {
        var handler = new BoundGenericsHandler(CreateDatabase(), conformanceGraph: null, currentModuleName: Module);

        var spelled = handler.TranslateBoundGenericTypeToCSharp(
            new NamedTypeSpec($"{Module}.Outer", Named("Payload")), GenericContext.Empty);

        Assert.Equal($"{Module}.Outer<{Module}.Payload>", spelled);
    }

    [Theory]
    [InlineData("Leaf")]
    [InlineData("Node")]
    public void TupleElementTranslator_NamesTheLeafWithArgumentOnOuter(string leaf)
    {
        var database = CreateDatabase();

        var spelled = BoundGenericTranslation.TranslateBoundGenericToCSharp(
            database, new ExistentialHandler(database), NestedInOuter(leaf),
            spec => database.GetTypeRecordOrAnyType(spec).CSharpTypeName.FullyQualifiedName,
            mapEmptyTupleArgumentToSwiftVoid: false, bareGenericSafetyNet: false);

        Assert.Equal($"{Module}.Outer<{Module}.Payload>.{leaf}", spelled);
    }

    [Fact]
    public void NestedHelper_DoublyGeneric_PlacesEachArgumentListOnItsOwnSegment()
    {
        var database = CreateDatabase();

        Assert.True(BoundGenericTranslation.TryTranslateNestedInBoundGeneric(
            database, NestedInOuter("Inner", Named("Other")),
            spec => database.GetTypeRecordOrAnyType(spec).CSharpTypeName.FullyQualifiedName,
            out var spelled));

        Assert.Equal($"{Module}.Outer<{Module}.Payload>.Inner<{Module}.Other>", spelled);
    }

    [Fact]
    public void NestedHelper_UsesCallerTranslatedOuterArguments()
    {
        var database = CreateDatabase();

        Assert.True(BoundGenericTranslation.TryTranslateNestedInBoundGeneric(
            database, NestedInOuter("Leaf"), _ => "unused", out var spelled,
            outerArguments: new[] { "int" }));

        Assert.Equal($"{Module}.Outer<int>.Leaf", spelled);
    }

    [Fact]
    public void NestedHelper_UnknownLeaf_DeclinesSoCallerKeepsItsOwnSpelling()
    {
        var database = CreateDatabase();

        Assert.False(BoundGenericTranslation.TryTranslateNestedInBoundGeneric(
            database, NestedInOuter("Missing"), _ => "unused", out _));
    }

    [Fact]
    public void NestedHelper_NoInnerChain_Declines()
    {
        var database = CreateDatabase();

        Assert.False(BoundGenericTranslation.TryTranslateNestedInBoundGeneric(
            database, new NamedTypeSpec($"{Module}.Outer", Named("Payload")), _ => "unused", out _));
    }
}
