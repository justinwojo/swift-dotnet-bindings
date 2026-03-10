// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for @_spi member filtering — IsSpiProtected defaults, CanEmitProperty rejection,
/// and InheritsCodable protocol detection.
/// </summary>
[Collection("ReportCollector")]
public class SpiMemberFilteringTests
{
    #region IsSpiProtected Default Tests

    [Fact]
    public void MethodDecl_IsSpiProtected_DefaultsFalse()
    {
        var method = new MethodDecl
        {
            Name = "doSomething",
            MangledName = "$s10TestModule11doSomethingyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArgument(string.Empty, TupleTypeSpec.Empty)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        Assert.False(method.IsSpiProtected);
    }

    [Fact]
    public void PropertyDecl_IsSpiProtected_DefaultsFalse()
    {
        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        Assert.False(property.IsSpiProtected);
    }

    #endregion

    #region CanEmitProperty SPI Tests

    [Fact]
    public void CanEmitProperty_SpiProtected_ReturnsModuleInternal()
    {
        var typeDatabase = CreateTypeDatabase();
        var property = new PropertyDecl
        {
            Name = "internalProp",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = true,
            IsSpiProtected = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var result = MemberEmissionValidator.CanEmitProperty(property, typeDatabase, out var skipDetails, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.ModuleInternal, result);
        Assert.Contains("@_spi", skipDetails);
    }

    #endregion

    #region InheritsCodable Tests

    [Fact]
    public void InheritsCodable_ProtocolWithDecodable_ReturnsTrue()
    {
        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Decodable"));

        Assert.True(ModuleHandler.InheritsCodable(protocol));
    }

    [Fact]
    public void InheritsCodable_ProtocolWithEncodable_ReturnsTrue()
    {
        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Encodable"));

        Assert.True(ModuleHandler.InheritsCodable(protocol));
    }

    [Fact]
    public void InheritsCodable_ProtocolWithCodable_ReturnsTrue()
    {
        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Codable"));

        Assert.True(ModuleHandler.InheritsCodable(protocol));
    }

    [Fact]
    public void InheritsCodable_ProtocolWithoutCodable_ReturnsFalse()
    {
        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Equatable"));
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Hashable"));

        Assert.False(ModuleHandler.InheritsCodable(protocol));
    }

    [Fact]
    public void InheritsCodable_ModuleQualifiedCodable_ReturnsTrue()
    {
        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("Swift.Decodable"));

        Assert.True(ModuleHandler.InheritsCodable(protocol));
    }

    [Fact]
    public void InheritsCodable_TransitiveInheritance_ReturnsTrue()
    {
        // Chain: ProtocolA : ProtocolB, ProtocolB : Codable
        var protocolB = CreateSimpleProtocol("ProtocolB");
        protocolB.InheritedProtocols.Add(new NamedTypeSpec("Codable"));

        var protocolA = CreateSimpleProtocol("ProtocolA");
        protocolA.InheritedProtocols.Add(new NamedTypeSpec("ProtocolB"));

        var allProtocols = new List<ProtocolDecl> { protocolA, protocolB };

        // Without transitive lookup, should be false (direct only)
        Assert.False(ModuleHandler.InheritsCodable(protocolA));
        // With transitive lookup, should be true
        Assert.True(ModuleHandler.InheritsCodable(protocolA, allProtocols));
    }

    [Fact]
    public void InheritsCodable_TransitiveDeep_ReturnsTrue()
    {
        // Chain: A : B, B : C, C : Decodable
        var protocolC = CreateSimpleProtocol("ProtocolC");
        protocolC.InheritedProtocols.Add(new NamedTypeSpec("Decodable"));

        var protocolB = CreateSimpleProtocol("ProtocolB");
        protocolB.InheritedProtocols.Add(new NamedTypeSpec("ProtocolC"));

        var protocolA = CreateSimpleProtocol("ProtocolA");
        protocolA.InheritedProtocols.Add(new NamedTypeSpec("ProtocolB"));

        var allProtocols = new List<ProtocolDecl> { protocolA, protocolB, protocolC };
        Assert.True(ModuleHandler.InheritsCodable(protocolA, allProtocols));
    }

    [Fact]
    public void InheritsCodable_TransitiveNoCodable_ReturnsFalse()
    {
        // Chain: A : B, B : Equatable (no Codable anywhere)
        var protocolB = CreateSimpleProtocol("ProtocolB");
        protocolB.InheritedProtocols.Add(new NamedTypeSpec("Equatable"));

        var protocolA = CreateSimpleProtocol("ProtocolA");
        protocolA.InheritedProtocols.Add(new NamedTypeSpec("ProtocolB"));

        var allProtocols = new List<ProtocolDecl> { protocolA, protocolB };
        Assert.False(ModuleHandler.InheritsCodable(protocolA, allProtocols));
    }

    [Fact]
    public void InheritsCodable_CrossModule_ViaTypeRecordFlag_ReturnsTrue()
    {
        // Cross-module: MyProtocol : ExternalModule.ExternalBase,
        // where ExternalBase has InheritsCodable flag in type database
        var typeDatabase = CreateTypeDatabase();
        var externalModule = new ModuleTypeDatabase("ExternalModule", "/tmp/ExternalModule.dylib");
        externalModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("ExternalModule.ExternalBase"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ExternalModule", "IExternalBase"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ExternalModule.ExternalBase"),
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.InheritsCodable,
                Kind = TypeRecordKind.Protocol,
            });
        typeDatabase.AddModuleDatabase(externalModule);

        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("ExternalModule.ExternalBase"));

        // Without type database, should be false
        Assert.False(ModuleHandler.InheritsCodable(protocol));
        // With type database, should detect via flag
        Assert.True(ModuleHandler.InheritsCodable(protocol, typeDatabase: typeDatabase));
    }

    [Fact]
    public void InheritsCodable_CrossModule_NoFlag_ReturnsFalse()
    {
        // Cross-module: MyProtocol : ExternalModule.ExternalBase,
        // but ExternalBase does NOT have InheritsCodable flag
        var typeDatabase = CreateTypeDatabase();
        var externalModule = new ModuleTypeDatabase("ExternalModule", "/tmp/ExternalModule.dylib");
        externalModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("ExternalModule.ExternalBase"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ExternalModule", "IExternalBase"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ExternalModule.ExternalBase"),
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol,
            });
        typeDatabase.AddModuleDatabase(externalModule);

        var protocol = CreateSimpleProtocol("MyProtocol");
        protocol.InheritedProtocols.Add(new NamedTypeSpec("ExternalModule.ExternalBase"));

        Assert.False(ModuleHandler.InheritsCodable(protocol, typeDatabase: typeDatabase));
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

    private static ProtocolDecl CreateSimpleProtocol(string name)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            HasSelfRequirement = false,
            IsClassBound = false,
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
