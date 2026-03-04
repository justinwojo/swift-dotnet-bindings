// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MemberEmissionValidator — CanEmitSubscript, Codable pruning,
/// HasUnsupportedPropertyType, non-simple enum detection.
/// </summary>
public class MemberEmissionValidatorTests
{
    #region CanEmitSubscript Tests

    [Fact]
    public void CanEmitSubscript_AlwaysReturnsUnsupportedType()
    {
        // Subscripts on concrete types are not yet supported — always returns SkipReason
        var typeDatabase = CreateTypeDatabase();
        var subscript = new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$sTest_subscript",
            IsStatic = false,
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl>
            {
                CreateArgument("index", new NamedTypeSpec("Swift.Int"))
            },
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var result = MemberEmissionValidator.CanEmitSubscript(subscript, typeDatabase, out var skipDetails, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedType, result);
        Assert.Contains("not yet supported", skipDetails);
    }

    [Fact]
    public void CanEmitSubscript_WithAnyTypeIndex_StillReturnsUnsupportedType()
    {
        // Even with AnyType index, the early return catches it first
        var typeDatabase = CreateTypeDatabase();
        var subscript = new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$sTest_subscript2",
            IsStatic = false,
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = new List<ArgumentDecl>
            {
                CreateArgument("key", new NamedTypeSpec("UnknownModule.Foo"))
            },
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var result = MemberEmissionValidator.CanEmitSubscript(subscript, typeDatabase, out _, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedType, result);
    }

    #endregion

    #region Codable Pruning Tests (via ShouldSkipMethodEmission)

    [Fact]
    public void ShouldSkipMethodEmission_CodableEncodeMember_ReturnsSynthesizedCodable()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "encode",
            MangledName = "$s10TestModule6encodeyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty),
                CreateArgument("to", new NamedTypeSpec("Swift.Encoder"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var skipDetails);

        Assert.Equal(SkipReason.SynthesizedCodable, result);
        Assert.Contains("Codable", skipDetails);
    }

    [Fact]
    public void ShouldSkipMethodEmission_CodableInitFromDecoder_ReturnsSynthesizedCodable()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4inityyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.MyType")),
                CreateArgument("from", new NamedTypeSpec("Swift.Decoder"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var skipDetails);

        Assert.Equal(SkipReason.SynthesizedCodable, result);
    }

    [Fact]
    public void ShouldSkipMethodEmission_NormalMethod_ReturnsNull()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule7processyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty),
                CreateArgument("value", new NamedTypeSpec("Swift.Int"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
    }

    [Fact]
    public void ShouldSkipMethodEmission_SwiftUIParam_ReturnsSwiftUIConstraint()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "render",
            MangledName = "$s10TestModule6renderyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty),
                CreateArgument("view", new NamedTypeSpec("SwiftUI.View"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var skipDetails);

        Assert.Equal(SkipReason.SwiftUIConstraint, result);
        Assert.Contains("SwiftUI", skipDetails);
    }

    #endregion

    #region HasUnsupportedPropertyType Tests

    [Fact]
    public void HasUnsupportedPropertyType_NormalProperty_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var result = MemberEmissionValidator.HasUnsupportedPropertyType(property, typeDatabase);

        Assert.False(result);
    }

    [Fact]
    public void HasUnsupportedPropertyType_SwiftUIType_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var property = new PropertyDecl
        {
            Name = "body",
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.View"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var result = MemberEmissionValidator.HasUnsupportedPropertyType(property, typeDatabase);

        Assert.True(result);
    }

    [Fact]
    public void HasUnsupportedPropertyType_UnresolvableType_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var property = new PropertyDecl
        {
            Name = "data",
            SwiftTypeSpec = new NamedTypeSpec("UnknownModule.SomeType"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var result = MemberEmissionValidator.HasUnsupportedPropertyType(property, typeDatabase);

        Assert.True(result);
    }

    #endregion

    #region Constructor Unsupported Module Gate (Issue 5)

    [Fact]
    public void ShouldSkipMethodEmission_Constructor_WithSwiftUIParam_ReturnsSwiftUIConstraint()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4inityyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.MyType")),
                CreateArgument("view", new NamedTypeSpec("SwiftUI.Color"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var skipDetails);

        Assert.Equal(SkipReason.SwiftUIConstraint, result);
        Assert.Contains("SwiftUI", skipDetails);
    }

    [Fact]
    public void CanEmitMethod_Constructor_WithSwiftUIParam_ReturnsSwiftUIConstraint()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4inityyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.MyType")),
                CreateArgument("view", new NamedTypeSpec("SwiftUI.Color"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var skipDetails, out _);

        Assert.Equal(SkipReason.SwiftUIConstraint, result);
        Assert.Contains("SwiftUI", skipDetails);
    }

    [Fact]
    public void ShouldSkipMethodEmission_Constructor_WithNormalParam_ReturnsNull()
    {
        // Constructors with normal params should still be allowed through
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4inityyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, new NamedTypeSpec("TestModule.MyType")),
                CreateArgument("value", new NamedTypeSpec("Swift.Int"))
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var result = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(result);
    }

    #endregion

    #region Helper Methods

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));
        return typeDatabase;
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    #endregion
}
