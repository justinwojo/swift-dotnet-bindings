// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

namespace BindingsGeneration.Tests;

using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class CodableJsonEmitterTests
{
    [Fact]
    public void Emit_PropagatesTypeAvailabilityToSwiftTrampolines()
    {
        // Without an @available prefix on the @_cdecl wrappers, swiftc rejects the
        // wrapper file when a Codable type sits above the binding's deployment floor
        // (e.g. MusicKit.Curator is iOS 15.4+ and the wrapper compiles at iOS 15.0).
        var structDecl = MakeStructDecl("Curator", isFrozen: false, conformances: new[] { "Codable" });
        structDecl.AvailabilityAnnotations = new List<AvailabilityAnnotation>
        {
            new("iOS", "15.4", null, null, false, false, null, null),
        };

        var typeDb = new MockTypeDatabase { AsyncLibraryName = "MusicKitSwiftBindings" };
        using var csOut = new StringWriter();
        using var swiftOut = new StringWriter();
        var csWriter = new CSharpWriter(csOut);
        var swiftWriter = new SwiftWriter(swiftOut);

        CodableJsonEmitter.Emit(
            csWriter, swiftWriter, structDecl, structDecl.ModuleDecl!,
            "Curator", typeDb, NullLogger.Instance);

        var swift = swiftOut.ToString();
        Assert.Contains("@available(iOS 15.4, *)", swift);
        Assert.Contains("@_cdecl(\"SBW_TestModule_Curator_EncodeJson\")", swift);
        Assert.Contains("@_cdecl(\"SBW_TestModule_Curator_DecodeJson\")", swift);
        // The annotation must appear before each @_cdecl, not just somewhere in the file.
        var encodeIdx = swift.IndexOf("@_cdecl(\"SBW_TestModule_Curator_EncodeJson\"");
        var decodeIdx = swift.IndexOf("@_cdecl(\"SBW_TestModule_Curator_DecodeJson\"");
        var firstAvailIdx = swift.IndexOf("@available(iOS 15.4, *)");
        var lastAvailIdx = swift.LastIndexOf("@available(iOS 15.4, *)");
        Assert.True(firstAvailIdx >= 0 && firstAvailIdx < encodeIdx);
        Assert.True(lastAvailIdx >= 0 && lastAvailIdx < decodeIdx && lastAvailIdx > encodeIdx);
    }

    [Fact]
    public void Emit_NestedTypesWithSameLeafName_ProduceDistinctSymbols()
    {
        // Two nested structs that share a leaf name (e.g. MPIOptions.Marker and
        // MPIOptions.FloatingLabelAppearance.Marker in Mappedin) must not both emit
        // SBW_<Module>_Marker_EncodeJson — swiftc rejects the wrapper with
        // "multiple definitions of symbol". The @_cdecl symbol must include the full
        // nested path so the two trampolines stay distinct.
        var inner = MakeStructDecl(
            "Marker",
            isFrozen: false,
            conformances: new[] { "Codable" },
            qualifiedName: "TestModule.MPIOptions.FloatingLabelAppearance.Marker");
        var outer = MakeStructDecl(
            "Marker",
            isFrozen: false,
            conformances: new[] { "Codable" },
            qualifiedName: "TestModule.MPIOptions.Marker");

        var typeDb = new MockTypeDatabase { AsyncLibraryName = "TestModuleSwiftBindings" };
        using var csOut = new StringWriter();
        using var swiftOut = new StringWriter();
        var csWriter = new CSharpWriter(csOut);
        var swiftWriter = new SwiftWriter(swiftOut);

        CodableJsonEmitter.Emit(csWriter, swiftWriter, inner, inner.ModuleDecl!, "Marker", typeDb, NullLogger.Instance);
        CodableJsonEmitter.Emit(csWriter, swiftWriter, outer, outer.ModuleDecl!, "Marker", typeDb, NullLogger.Instance);

        var swift = swiftOut.ToString();
        Assert.Contains("@_cdecl(\"SBW_TestModule_MPIOptions_FloatingLabelAppearance_Marker_EncodeJson\")", swift);
        Assert.Contains("@_cdecl(\"SBW_TestModule_MPIOptions_Marker_EncodeJson\")", swift);
        Assert.Contains("@_cdecl(\"SBW_TestModule_MPIOptions_FloatingLabelAppearance_Marker_DecodeJson\")", swift);
        Assert.Contains("@_cdecl(\"SBW_TestModule_MPIOptions_Marker_DecodeJson\")", swift);
    }

    [Fact]
    public void Emit_NoAvailability_OmitsAvailablePrefix()
    {
        // Types without availability constraints should not get a spurious @available line.
        var structDecl = MakeStructDecl("Plain", isFrozen: false, conformances: new[] { "Codable" });
        var typeDb = new MockTypeDatabase { AsyncLibraryName = "TestModuleSwiftBindings" };
        using var csOut = new StringWriter();
        using var swiftOut = new StringWriter();
        var csWriter = new CSharpWriter(csOut);
        var swiftWriter = new SwiftWriter(swiftOut);

        CodableJsonEmitter.Emit(
            csWriter, swiftWriter, structDecl, structDecl.ModuleDecl!,
            "Plain", typeDb, NullLogger.Instance);

        Assert.DoesNotContain("@available(", swiftOut.ToString());
    }


    [Fact]
    public void ShouldEmit_FrozenStructProjectedAsClass_ReturnsFalse()
    {
        // Frozen + class-projected = ClassWithBufferStruct. It exposes `_payload` + `PayloadBuffer<Buffer>`
        // but NOT `_payloadSize` / `NewFromPayloadCore`, so the decode factory cannot construct an
        // instance. JSON is only emitted for ClassWithOpaquePayload (non-frozen).
        var s = MakeStructDecl("Forecast", isFrozen: true, conformances: new[] { "Encodable", "Decodable" });
        Assert.False(CodableJsonEmitter.ShouldEmit(s, isProjectedAsClass: true));
    }

    [Fact]
    public void ShouldEmit_FrozenStructWithCodableAlias_ReturnsFalse()
    {
        // Same ClassWithBufferStruct exclusion when the conformance is reported via the `Codable` alias.
        var s = MakeStructDecl("Cached", isFrozen: true, conformances: new[] { "Codable" });
        Assert.False(CodableJsonEmitter.ShouldEmit(s, isProjectedAsClass: true));
    }

    [Fact]
    public void ShouldEmit_NonFrozenStructProjectedAsClass_ReturnsTrue()
    {
        // Non-frozen + class-projected = ClassWithOpaquePayload (NonFrozenStructHandler), which emits
        // the full _payload / NewFromPayloadCore / _payloadSize trio the decoder relies on.
        var s = MakeStructDecl("Loose", isFrozen: false, conformances: new[] { "Encodable", "Decodable" });
        Assert.True(CodableJsonEmitter.ShouldEmit(s, isProjectedAsClass: true));
    }

    [Fact]
    public void ShouldEmit_GenericStruct_ReturnsFalse()
    {
        // Generic Codable types (closed-instantiation) are not yet supported and should not emit JSON helpers.
        var s = MakeStructDecl("Forecast", isFrozen: true, conformances: new[] { "Encodable", "Decodable" }, generic: true);
        Assert.False(CodableJsonEmitter.ShouldEmit(s, isProjectedAsClass: true));
    }

    [Fact]
    public void ShouldEmit_OnlyEncodable_ReturnsFalse()
    {
        var s = MakeStructDecl("Outbound", isFrozen: false, conformances: new[] { "Encodable" });
        Assert.False(CodableJsonEmitter.ShouldEmit(s, isProjectedAsClass: true));
    }

    [Fact]
    public void ShouldEmit_OnlyDecodable_ReturnsFalse()
    {
        var s = MakeStructDecl("Inbound", isFrozen: false, conformances: new[] { "Decodable" });
        Assert.False(CodableJsonEmitter.ShouldEmit(s, isProjectedAsClass: true));
    }

    [Fact]
    public void ShouldEmit_StructProjection_ReturnsFalse()
    {
        // Pure-struct projection (no _payload) is not yet supported — only class projection is handled.
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

    private class MockTypeDatabase : ITypeDatabase
    {
        public string? AsyncLibraryName { get; set; }
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => false;
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TypeRecord? record)
        {
            record = null;
            return false;
        }
        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    private static StructDecl MakeStructDecl(string name, bool isFrozen, string[] conformances, bool generic = false, string? qualifiedName = null)
    {
        var module = TestModelFactory.CreateModuleDecl();
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName ?? $"TestModule.{name}");
        var conformanceList = new List<TypeConformance>();
        foreach (var protocolName in conformances)
        {
            conformanceList.Add(new TypeConformance(
                swiftTypeName,
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
            SwiftTypeName = swiftTypeName,
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
