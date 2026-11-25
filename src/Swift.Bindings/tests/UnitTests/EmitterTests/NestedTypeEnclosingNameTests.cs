// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A nested type whose C# leaf equals its container's (<c>struct Outer { final class outer {} }</c>)
/// is CS0542 in every class-projected container, so the nested-type pre-pass renames the nested
/// type with the kind-aware suffix. The rule must give the same name in every SDK copy: it reads
/// the container's leaf, the nested type's kind, its sibling nested types and its own members, and
/// never the container's members, which yield to the renamed type instead.
/// </summary>
public class NestedTypeEnclosingNameTests
{
    [Theory]
    [InlineData("struct", "outer", "struct", "Outer.OuterInfo")]
    [InlineData("struct", "Outer", "class", "Outer.OuterInfo")]
    [InlineData("class", "outer", "enum", "Outer.OuterKind")]
    [InlineData("namespace", "outer", "struct", "Outer.OuterInfo")]
    [InlineData("payload", "Outer", "struct", "Outer.OuterInfo")]
    [InlineData("facade", "Outer", "struct", "Outer.OuterInfo")]
    public void PrecomputeNestedTypeRenames_NestedTypeNamedLikeItsContainer_TakesTheKindSuffix(
        string containerKind, string nestedSwiftName, string nestedKind, string expected)
    {
        var fixture = new Fixture(containerKind);
        var nested = fixture.AddNested(nestedSwiftName, nestedKind);

        NameProvider.PrecomputeNestedTypeRenames(fixture.Module, fixture.Database);

        Assert.Equal(expected, fixture.CSharpName(nested));
    }

    [Fact]
    public void PrecomputeNestedTypeRenames_NestedTypeNotNamedLikeItsContainer_KeepsItsName()
    {
        var fixture = new Fixture("struct");
        var nested = fixture.AddNested("Inner", "struct");

        NameProvider.PrecomputeNestedTypeRenames(fixture.Module, fixture.Database);

        Assert.Equal("Outer.Inner", fixture.CSharpName(nested));
    }

    [Fact]
    public void PrecomputeNestedTypeRenames_RenamedNestedType_CarriesItsDescendants()
    {
        var fixture = new Fixture("struct");
        var nested = fixture.AddNested("outer", "class");
        var deep = fixture.AddNested("Deep", "struct", parent: nested);

        NameProvider.PrecomputeNestedTypeRenames(fixture.Module, fixture.Database);

        Assert.Equal("Outer.OuterInfo.Deep", fixture.CSharpName(deep));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrecomputeNestedTypeRenames_ContainerGainingAMemberOnTheNewLeaf_TheMemberYields(bool withMember)
    {
        // A newer SDK adding `var outerInfo: Int` to `Outer` must not move `Outer.OuterInfo`, which
        // every older copy's consumer was compiled against: the added property yields instead.
        var fixture = new Fixture("struct");
        var nested = fixture.AddNested("outer", "class");
        if (withMember)
            fixture.AddProperty(fixture.Container, "outerInfo");

        NameProvider.PrecomputeNestedTypeRenames(fixture.Module, fixture.Database);

        Assert.Equal("Outer.OuterInfo", fixture.CSharpName(nested));
        if (withMember)
            Assert.Equal($"OuterInfo{NameCollisionPolicy.MemberValueSuffix}",
                NameProvider.ComputePropertyRenames(fixture.Container, fixture.Database)["OuterInfo"]);
    }

    [Fact]
    public void PrecomputeNestedTypeRenames_SiblingNestedTypeOnTheNewLeaf_StepsOverIt()
    {
        var fixture = new Fixture("struct");
        var nested = fixture.AddNested("outer", "class");
        var sibling = fixture.AddNested("outerInfo", "struct");

        NameProvider.PrecomputeNestedTypeRenames(fixture.Module, fixture.Database);

        Assert.Equal("Outer.OuterInfo2", fixture.CSharpName(nested));
        Assert.Equal("Outer.OuterInfo", fixture.CSharpName(sibling));
    }

    [Fact]
    public void PrecomputeNestedTypeRenames_OwnMemberOnTheNewLeaf_StepsOverIt()
    {
        // `Outer.outer` declaring `var outerInfo` cannot become `OuterInfo` (CS0542 on its member).
        var fixture = new Fixture("struct");
        var nested = fixture.AddNested("outer", "class");
        fixture.AddProperty(nested, "outerInfo");

        NameProvider.PrecomputeNestedTypeRenames(fixture.Module, fixture.Database);

        Assert.Equal("Outer.OuterInfo2", fixture.CSharpName(nested));
    }

    [Fact]
    public void PrecomputeNestedTypeRenames_SpiNestedTypeNamedLikeItsContainer_IsLeftAlone()
    {
        var fixture = new Fixture("struct");
        var nested = fixture.AddNested("outer", "class", spi: true);

        NameProvider.PrecomputeNestedTypeRenames(fixture.Module, fixture.Database);

        Assert.Equal("Outer.Outer", fixture.CSharpName(nested));
    }

    [Fact]
    public void CaseOnlyRenameOfTheContainer_StepsOverItsRenamedNestedType()
    {
        // `struct Outer` loses a case-only tie to the caseless enum `OUTER` and moves to
        // `OuterInfo`, the leaf its own nested `outer` already took: it steps to `OuterInfo2`.
        var fixture = new Fixture("struct");
        var nested = fixture.AddNested("outer", "class");
        var upper = fixture.AddTopLevel("OUTER", "namespace");
        fixture.AddNested("Mode", "struct", parent: upper);

        NameProvider.PrecomputeNestedTypeRenames(fixture.Module, fixture.Database);
        CaseOnlyCollisionPass.Precompute(fixture.Module, fixture.Database);

        Assert.Equal("OuterInfo2", fixture.CSharpName(fixture.Container));
        Assert.Equal("OuterInfo2.OuterInfo", fixture.CSharpName(nested));
    }

    private sealed class Fixture
    {
        public TypeDatabase Database { get; } = new();
        public ModuleDecl Module { get; }
        public TypeDecl Container { get; }
        private readonly ModuleTypeDatabase _types = new("TestModule", "/tmp/TestModule.dylib");
        private bool _added;

        public Fixture(string containerKind)
        {
            Module = new ModuleDecl
            {
                Name = "TestModule",
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Dependencies = new List<string>(),
                Protocols = new List<ProtocolDecl>(),
                ParentDecl = null,
                ModuleDecl = null,
            };
            Container = AddTopLevel("Outer", containerKind);
        }

        public TypeDecl AddTopLevel(string swiftName, string kind)
        {
            var decl = Create(kind, swiftName, SwiftTypeName.FromModuleQualifiedName($"TestModule.{swiftName}"));
            decl.ParentDecl = Module;
            Module.Types.Add(decl);
            Register(decl, NameProvider.GetNaturalTypeLeafName(decl));
            return decl;
        }

        public TypeDecl AddNested(string swiftName, string kind, TypeDecl? parent = null, bool spi = false)
        {
            parent ??= Container;
            var decl = Create(kind, swiftName, SwiftTypeName.FromModuleQualifiedName($"{parent.SwiftTypeName}.{swiftName}"));
            decl.ParentDecl = parent;
            decl.IsSpiProtected = spi;
            parent.Types.Add(decl);
            Database.TryGetTypeRecord(parent.SwiftTypeName, out var parentRecord);
            Register(decl, $"{parentRecord!.CSharpTypeName.Name}.{NameProvider.GetNaturalTypeLeafName(decl)}");
            return decl;
        }

        public void AddProperty(TypeDecl owner, string name) => owner.Properties.Add(new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = owner is EnumDecl { Cases.Count: 0 },
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = owner,
            ModuleDecl = Module,
        });

        public string CSharpName(TypeDecl decl)
        {
            Assert.True(Database.TryGetTypeRecord(decl.SwiftTypeName, out var record));
            return record!.CSharpTypeName.Name;
        }

        private void Register(TypeDecl decl, string csharpName)
        {
            _types.RegisterType(decl.SwiftTypeName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", csharpName),
                SwiftTypeName = decl.SwiftTypeName,
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = decl switch { EnumDecl => TypeRecordKind.Enum, ClassDecl => TypeRecordKind.Class, _ => TypeRecordKind.Struct },
            });
            if (!_added)
            {
                Database.AddModuleDatabase(_types);
                _added = true;
            }
        }

        private TypeDecl Create(string kind, string name, SwiftTypeName swiftName)
        {
            TypeDecl decl = kind switch
            {
                "class" => new ClassDecl
                {
                    Name = name, SwiftTypeName = swiftName, MangledName = "$sN", ParentDecl = null, ModuleDecl = Module,
                    Properties = new List<PropertyDecl>(), Methods = new List<MethodDecl>(), Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(), Subscripts = new List<SubscriptDecl>(),
                    GenericParameters = new List<GenericArgumentDecl>(), Conformances = new List<TypeConformance>(),
                },
                "enum" or "namespace" or "payload" => new EnumDecl
                {
                    Name = name, SwiftTypeName = swiftName, MangledName = "$sN", ParentDecl = null, ModuleDecl = Module,
                    Properties = new List<PropertyDecl>(), Methods = new List<MethodDecl>(), Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(), Subscripts = new List<SubscriptDecl>(),
                    GenericParameters = new List<GenericArgumentDecl>(), Conformances = new List<TypeConformance>(),
                    Cases = new List<EnumCaseDecl>(), IsFrozen = true, MetadataAccessor = "$sMa",
                },
                _ => new StructDecl
                {
                    Name = name, SwiftTypeName = swiftName, MangledName = "$sN", ParentDecl = null, ModuleDecl = Module,
                    Properties = new List<PropertyDecl>(), Methods = new List<MethodDecl>(), Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(), Subscripts = new List<SubscriptDecl>(),
                    GenericParameters = new List<GenericArgumentDecl>(), Conformances = new List<TypeConformance>(),
                    IsFrozen = true, MetadataAccessor = "$sMa",
                },
            };
            if (kind is "enum" or "payload")
            {
                var enumDecl = (EnumDecl)decl;
                enumDecl.Cases.Add(new EnumCaseDecl { Name = "a", MangledName = "$sN", ParentDecl = enumDecl, ModuleDecl = Module,
                    AssociatedValues = kind == "payload" ? new List<TypeSpec> { new NamedTypeSpec("Swift.Int") } : new List<TypeSpec>() });
                enumDecl.Cases.Add(new EnumCaseDecl { Name = "b", MangledName = "$sN", ParentDecl = enumDecl, ModuleDecl = Module });
            }
            if (kind == "struct")
                ((StructDecl)decl).Properties.Add(new PropertyDecl
                {
                    Name = "x", SwiftTypeSpec = new NamedTypeSpec("Swift.Int"), IsStatic = false, HasStorage = true,
                    Accessors = new List<AccessorDecl>(), ParentDecl = decl, ModuleDecl = Module,
                });
            return decl;
        }
    }
}
