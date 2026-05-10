// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

namespace BindingsGeneration.Tests;

using Xunit;

public class CodableJsonEmitterTests
{
    [Fact]
    public void ShouldEmit_CodableFrozenStructProjectedAsClass_ReturnsTrue()
    {
        var s = MakeStructDecl("Forecast", isFrozen: true, conformances: new[] { "Encodable", "Decodable" });
        Assert.True(CodableJsonEmitter.ShouldEmit(s, isProjectedAsClass: true));
    }

    [Fact]
    public void ShouldEmit_CodableTypealiasFrozenStructProjectedAsClass_ReturnsTrue()
    {
        // Swift `Codable` is a typealias for `Encodable & Decodable`; ABI may report the alias name.
        var s = MakeStructDecl("Cached", isFrozen: true, conformances: new[] { "Codable" });
        Assert.True(CodableJsonEmitter.ShouldEmit(s, isProjectedAsClass: true));
    }

    [Fact]
    public void ShouldEmit_NonFrozenStructProjectedAsClass_ReturnsTrue()
    {
        // Non-frozen structs are always projected as C# classes via NonFrozenStructHandler,
        // and use the same _payload / NewFromPayloadCore / _payloadSize pattern that the
        // emitted JSON helpers rely on. Gate is `isProjectedAsClass`, not `IsFrozen`.
        var s = MakeStructDecl("Loose", isFrozen: false, conformances: new[] { "Encodable", "Decodable" });
        Assert.True(CodableJsonEmitter.ShouldEmit(s, isProjectedAsClass: true));
    }

    [Fact]
    public void ShouldEmit_GenericStruct_ReturnsFalse()
    {
        // Phase 2 (closed-instantiation) deferred — generic Codable types should not emit JSON helpers yet.
        var s = MakeStructDecl("Forecast", isFrozen: true, conformances: new[] { "Encodable", "Decodable" }, generic: true);
        Assert.False(CodableJsonEmitter.ShouldEmit(s, isProjectedAsClass: true));
    }

    [Fact]
    public void ShouldEmit_OnlyEncodable_ReturnsFalse()
    {
        var s = MakeStructDecl("Outbound", isFrozen: true, conformances: new[] { "Encodable" });
        Assert.False(CodableJsonEmitter.ShouldEmit(s, isProjectedAsClass: true));
    }

    [Fact]
    public void ShouldEmit_OnlyDecodable_ReturnsFalse()
    {
        var s = MakeStructDecl("Inbound", isFrozen: true, conformances: new[] { "Decodable" });
        Assert.False(CodableJsonEmitter.ShouldEmit(s, isProjectedAsClass: true));
    }

    [Fact]
    public void ShouldEmit_StructProjection_ReturnsFalse()
    {
        // Pure-struct projection (no _payload) is deferred — Phase 1 only handles class projection.
        var s = MakeStructDecl("Bare", isFrozen: true, conformances: new[] { "Codable" });
        Assert.False(CodableJsonEmitter.ShouldEmit(s, isProjectedAsClass: false));
    }

    [Fact]
    public void ShouldEmit_NotAStruct_ReturnsFalse()
    {
        var module = TestModelFactory.CreateModuleDecl();
        var classDecl = new ClassDecl
        {
            Name = "Service",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Service"),
            MangledName = "$s10TestModule7ServiceC",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>
            {
                new(SwiftTypeName.FromModuleQualifiedName("TestModule.Service"),
                    SwiftTypeName.FromModuleQualifiedName("Swift.Codable"),
                    "TestModuleServiceCodableMc"),
            },
            ParentDecl = module,
            ModuleDecl = module,
        };
        Assert.False(CodableJsonEmitter.ShouldEmit(classDecl, isProjectedAsClass: true));
    }

    private static StructDecl MakeStructDecl(string name, bool isFrozen, string[] conformances, bool generic = false)
    {
        var module = TestModelFactory.CreateModuleDecl();
        var conformanceList = new List<TypeConformance>();
        foreach (var protocolName in conformances)
        {
            conformanceList.Add(new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
                SwiftTypeName.FromModuleQualifiedName($"Swift.{protocolName}"),
                $"TestModule{name}{protocolName}Mc"));
        }
        var generics = new List<GenericArgumentDecl>();
        if (generic)
        {
            generics.Add(new GenericArgumentDecl(
                TypeName: "T",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>()));
        }
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}V",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = generics,
            Conformances = conformanceList,
            IsFrozen = isFrozen,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            ParentDecl = module,
            ModuleDecl = module,
        };
    }
}
