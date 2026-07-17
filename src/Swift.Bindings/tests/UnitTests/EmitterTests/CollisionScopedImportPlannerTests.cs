// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit tests for <see cref="CollisionScopedImportPlanner"/>.
///
/// When a module's name is shadowed by a type of the same name, the wrapper strips
/// <c>Module.</c> qualifiers and relies on scoped imports
/// (<c>import class M.Type</c>) so bare names resolve to the bound module's types.
/// These tests lock the planner's membership, kind keywords, and spelling rules.
/// </summary>
public class CollisionScopedImportPlannerTests
{
    [Fact]
    public void Plan_EmitsKindKeywordPerDeclSubtype()
    {
        var module = CreateModule("Shadowed",
            types:
            [
                CreateClass("MyClass"),
                CreateStruct("MyStruct"),
                CreateEnum("MyEnum"),
            ],
            protocols: [CreateProtocol("MyProtocol")]);

        var lines = CollisionScopedImportPlanner.Plan(module, compileImport: "Shadowed");

        Assert.Contains("import class Shadowed.MyClass", lines);
        Assert.Contains("import struct Shadowed.MyStruct", lines);
        Assert.Contains("import enum Shadowed.MyEnum", lines);
        Assert.Contains("import protocol Shadowed.MyProtocol", lines);
        Assert.Equal(4, lines.Count);
    }

    [Fact]
    public void Plan_SkipsModuleInternalTypes()
    {
        var internalClass = CreateClass("InternalHelper");
        internalClass.IsModuleInternal = true;
        var publicClass = CreateClass("PublicType");

        var module = CreateModule("Mod", types: [internalClass, publicClass]);

        var lines = CollisionScopedImportPlanner.Plan(module, compileImport: "Mod");

        Assert.DoesNotContain(lines, l => l.Contains("InternalHelper"));
        Assert.Contains("import class Mod.PublicType", lines);
        Assert.Single(lines);
    }

    [Fact]
    public void Plan_SkipsTypeWhoseNameEqualsModuleName()
    {
        // The shadowing type itself must not be scoped-imported: that would re-bind the
        // name the nested-type carve-out relies on, and nested members are reached via
        // the preserved Module.Nested qualification instead.
        var shadowingClass = CreateClass("SwiftMessages");
        var sibling = CreateStruct("AnimationContext");

        var module = CreateModule("SwiftMessages", types: [shadowingClass, sibling]);

        var lines = CollisionScopedImportPlanner.Plan(module, compileImport: "SwiftMessages");

        Assert.DoesNotContain(lines, l => l.EndsWith(".SwiftMessages", StringComparison.Ordinal));
        Assert.Contains("import struct SwiftMessages.AnimationContext", lines);
        Assert.Single(lines);
    }

    [Fact]
    public void Plan_SkipsUnderscorePrefixedNames()
    {
        var underscored = CreateStruct("_PrivateHelper");
        var publicType = CreateStruct("Visible");

        var module = CreateModule("Mod", types: [underscored, publicType]);

        var lines = CollisionScopedImportPlanner.Plan(module, compileImport: "Mod");

        Assert.DoesNotContain(lines, l => l.Contains("_PrivateHelper"));
        Assert.Contains("import struct Mod.Visible", lines);
        Assert.Single(lines);
    }

    [Fact]
    public void Plan_OutputIsSortedAndUsesCompileImportSpelling()
    {
        // compileImport is the remapped umbrella spelling; it must appear in the
        // import lines even when it differs from moduleDecl.Name.
        var module = CreateModule("InternalName",
            types:
            [
                CreateClass("Zebra"),
                CreateStruct("Apple"),
                CreateEnum("Middle"),
            ]);

        var lines = CollisionScopedImportPlanner.Plan(module, compileImport: "UmbrellaKit");

        Assert.Equal(
            [
                "import class UmbrellaKit.Zebra",
                "import enum UmbrellaKit.Middle",
                "import struct UmbrellaKit.Apple",
            ],
            lines);

        // SortedSet ordinal order: "import class..." < "import enum..." < "import struct..."
        // Within the same keyword, type names also sort: already covered by kind ordering above.
        Assert.Equal(lines.OrderBy(l => l, StringComparer.Ordinal).ToList(), lines);

        // Never spell the module from moduleDecl.Name when compileImport differs.
        Assert.All(lines, l => Assert.DoesNotContain("InternalName", l));
        Assert.All(lines, l => Assert.Contains("UmbrellaKit.", l));
    }

    [Fact]
    public void Plan_EmptyCompileImport_ReturnsEmptyList()
    {
        var module = CreateModule("Mod", types: [CreateClass("Foo")]);

        Assert.Empty(CollisionScopedImportPlanner.Plan(module, compileImport: ""));
        Assert.Empty(CollisionScopedImportPlanner.Plan(module, compileImport: null!));
    }

    [Fact]
    public void Plan_EmptyModule_ReturnsEmptyList()
    {
        var module = CreateModule("Mod");

        Assert.Empty(CollisionScopedImportPlanner.Plan(module, compileImport: "Mod"));
    }

    #region Helpers

    private static ModuleDecl CreateModule(
        string name,
        TypeDecl[]? types = null,
        ProtocolDecl[]? protocols = null)
    {
        return new ModuleDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Types = (types ?? Array.Empty<TypeDecl>()).ToList(),
            Dependencies = new List<string>(),
            Protocols = (protocols ?? Array.Empty<ProtocolDecl>()).ToList(),
        };
    }

    private static ClassDecl CreateClass(string name) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"Mod.{name}"),
        MangledName = $"$s3Mod{name.Length}{name}C",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Conformances = new List<TypeConformance>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static StructDecl CreateStruct(string name) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"Mod.{name}"),
        MangledName = $"$s3Mod{name.Length}{name}V",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Conformances = new List<TypeConformance>(),
        ParentDecl = null,
        ModuleDecl = null,
        IsFrozen = true,
        MetadataAccessor = $"$s3Mod{name.Length}{name}VMa",
    };

    private static EnumDecl CreateEnum(string name) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"Mod.{name}"),
        MangledName = $"$s3Mod{name.Length}{name}O",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Conformances = new List<TypeConformance>(),
        ParentDecl = null,
        ModuleDecl = null,
        IsFrozen = true,
        MetadataAccessor = $"$s3Mod{name.Length}{name}OMa",
    };

    private static ProtocolDecl CreateProtocol(string name) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"Mod.{name}"),
        MangledName = $"$s3Mod{name.Length}{name}P",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    #endregion
}
