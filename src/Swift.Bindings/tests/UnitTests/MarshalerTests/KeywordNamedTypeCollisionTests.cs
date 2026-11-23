// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The member-collision rules (CS0542 against the enclosing type, CS0102 against a nested type)
/// must compare against the C# leaf a type is declared under, not the parser's stored name. A type
/// spelled with a C# keyword (<c>struct checked</c>) is stored as <c>_checked</c> but declared as
/// <c>Checked</c>, and a lowercase Swift type (<c>struct vec</c>) is declared as <c>Vec</c>; a
/// rule keyed on the stored name misses the collision, the member compiles to CS0542/CS0102, and
/// verify-recover withdraws it.
/// </summary>
public class KeywordNamedTypeCollisionTests
{
    private const string Module = "KwMod";

    private static ModuleDecl CreateModule() => new()
    {
        Name = Module,
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static StructDecl CreateStruct(string storedName, string swiftPath, ModuleDecl module) => new()
    {
        Name = storedName,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{swiftPath}"),
        MangledName = "$sN",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        ParentDecl = module,
        ModuleDecl = module,
        IsFrozen = true,
        MetadataAccessor = "$sMa",
    };

    private static EnumDecl CreateEnum(string storedName, string swiftPath, ModuleDecl module) => new()
    {
        Name = storedName,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{swiftPath}"),
        MangledName = "$sN",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        Cases = new List<EnumCaseDecl>(),
        ParentDecl = module,
        ModuleDecl = module,
        IsFrozen = true,
        MetadataAccessor = "$sMa",
    };

    private static PropertyDecl IntProperty(string name, BaseDecl parent) => new()
    {
        Name = name,
        SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
        IsStatic = false,
        HasStorage = true,
        Accessors = new List<AccessorDecl>(),
        ParentDecl = parent,
        ModuleDecl = parent.ModuleDecl,
    };

    private static EnumCaseDecl Case(string name, EnumDecl parent, bool payload) => new()
    {
        Name = name,
        MangledName = $"$s{name}",
        ParentDecl = parent,
        ModuleDecl = parent.ModuleDecl,
        AssociatedValues = payload ? new List<TypeSpec> { new NamedTypeSpec("Swift.Int") } : new List<TypeSpec>(),
    };

    private static TypeDatabase DatabaseWith(params (TypeDecl Decl, TypeRecordKind Kind, TypeRecordFlags Flags)[] types)
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase(Module, $"/tmp/{Module}.dylib");
        foreach (var (decl, kind, flags) in types)
        {
            module.RegisterType(decl.SwiftTypeName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(Module,
                    string.Join(".", decl.SwiftTypeName.ModuleQualifiedName.Split('.').Skip(1).Select(NameProvider.ToPascalCaseForTypeName))),
                SwiftTypeName = decl.SwiftTypeName,
                MetadataAccessor = "$sMa",
                Flags = flags,
                Kind = kind,
            });
        }
        typeDatabase.AddModuleDatabase(module);
        return typeDatabase;
    }

    [Theory]
    [InlineData("_event", "event", "Event")]
    [InlineData("vec", "vec", "Vec")]
    [InlineData("Plain", "Plain", "Plain")]
    // A Swift type really spelled with a leading underscore keeps it: SwiftTypeName confirms the
    // stored name is the Swift spelling, so nothing is undone.
    [InlineData("_class", "_class", "_class")]
    public void GetNaturalTypeLeafName_IsTheDeclaredCSharpLeaf(string storedName, string swiftName, string expected)
    {
        var decl = CreateStruct(storedName, swiftName, CreateModule());
        Assert.Equal(expected, NameProvider.GetNaturalTypeLeafName(decl));
    }

    [Theory]
    [InlineData("_checked", "checked", "checked", "CheckedValue")]
    [InlineData("vec", "vec", "vec", "VecValue")]
    [InlineData("_checked", "checked", "count", "Count")]
    public void GetPropertyName_PropertyNamedLikeItsEnclosingType_TakesTheValueSuffix(
        string storedName, string swiftName, string propertyName, string expected)
    {
        var type = CreateStruct(storedName, swiftName, CreateModule());
        var property = IntProperty(propertyName, type);
        type.Properties.Add(property);

        Assert.Equal(expected, NameProvider.GetPropertyName(property, type));
    }

    [Fact]
    public void ForMethod_MethodNamedLikeItsKeywordType_KeepsItsKeywordName()
    {
        // `struct explicit { func explicit(_ v: Int) -> Int }`: the method is stored `_explicit` and
        // emits `_explicit`, which cannot collide with the declared type `Explicit`. Keying the
        // CS0542 rule on the stored type name `_explicit` added an unneeded `Get` prefix.
        var type = CreateStruct("_explicit", "explicit", CreateModule());
        var method = TestModelFactory.CreateMethod("_explicit", parent: type,
            args: new[] { ("", "Swift.Int"), ("v", "Swift.Int") });
        type.Methods.Add(method);

        var ctx = PublicMethodNameContext.ForMethod(method, siblingPropertyNames: null);

        Assert.Equal("Explicit", ctx.ParentTypeName);
        Assert.Equal("_explicit", NameProvider.GetPublicMethodName(in ctx));
    }

    [Fact]
    public void ForMethod_MethodNamedLikeItsLowercaseType_StillTakesTheGetPrefix()
    {
        // `struct vec { func vec(_ v: Int) -> Int }` emits `Vec(...)` inside class `Vec`: a real
        // CS0542 collision, which the stored name `vec` hid.
        var type = CreateStruct("vec", "vec", CreateModule());
        var method = TestModelFactory.CreateMethod("vec", parent: type,
            args: new[] { ("", "Swift.Int"), ("v", "Swift.Int") });
        type.Methods.Add(method);

        var ctx = PublicMethodNameContext.ForMethod(method, siblingPropertyNames: null);

        Assert.Equal("GetVec", NameProvider.GetPublicMethodName(in ctx));
    }

    [Fact]
    public void ComputePropertyRenames_PropertyNamedLikeAKeywordNestedType_IsRenamed()
    {
        // `struct KwHost { struct object {}; var object: Int }` declares nested `Object` and a
        // property `Object` (CS0102); the stored nested name `_object` hid the collision.
        var module = CreateModule();
        var host = CreateStruct("KwHost", "KwHost", module);
        var nested = CreateStruct("_object", "KwHost.object", module);
        nested.ParentDecl = host;
        host.Types.Add(nested);
        host.Properties.Add(IntProperty("object", host));
        var db = DatabaseWith((host, TypeRecordKind.Struct, TypeRecordFlags.Frozen),
            (nested, TypeRecordKind.Struct, TypeRecordFlags.Frozen));

        var renames = NameProvider.ComputePropertyRenames(host, db);

        Assert.Equal("ObjectValue", Assert.Single(renames).Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ComputePropertyRenames_PayloadEnumCaseNamedLikeTheEnum_IsRenamed(bool payload)
    {
        // `enum implicit { case implicit(Int); case other }` projects as class `Implicit`, whose
        // `Implicit` case factory (or static property) is a CS0542 duplicate of the class.
        var module = CreateModule();
        var type = CreateEnum("_implicit", "implicit", module);
        type.Cases.Add(Case("implicit", type, payload));
        type.Cases.Add(Case("other", type, payload: true));
        var db = DatabaseWith((type, TypeRecordKind.Enum, TypeRecordFlags.Frozen));

        var renames = NameProvider.ComputePropertyRenames(type, db);

        Assert.Equal("ImplicitValue", renames["Implicit"]);
        Assert.False(renames.ContainsKey("Other"));
    }

    [Fact]
    public void ComputePropertyRenames_SimpleEnumCaseNamedLikeTheEnum_IsNotRenamed()
    {
        // A simple enum projects as a C# enum, whose members may share the enum's name.
        var module = CreateModule();
        var type = CreateEnum("_volatile", "volatile", module);
        type.Cases.Add(Case("volatile", type, payload: false));
        type.Cases.Add(Case("other", type, payload: false));
        var db = DatabaseWith((type, TypeRecordKind.Enum, TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum));

        Assert.Empty(NameProvider.ComputePropertyRenames(type, db));
    }
}
