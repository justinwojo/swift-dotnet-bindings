// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ConcreteSpecializationEngine"/> — protocol conformer discovery
/// from hints and ABI, and specializable method detection.
/// </summary>
public class ConcreteSpecializationEngineTests
{
    private static ITypeDatabase CreateEmptyTypeDatabase() => new EmptyTypeDatabase();

    [Fact]
    public void LoadedHints_ContainsDataProtocol()
    {
        var hints = ConcreteSpecializationEngine.LoadedHints;
        Assert.True(hints.ContainsKey("Swift.DataProtocol"), "Should have DataProtocol hints");
        Assert.True(hints["Swift.DataProtocol"].Count >= 2, "DataProtocol should have at least 2 conformers");
    }

    [Fact]
    public void LoadedHints_ContainsContiguousBytes()
    {
        var hints = ConcreteSpecializationEngine.LoadedHints;
        Assert.True(hints.ContainsKey("Foundation.ContiguousBytes"), "Should have ContiguousBytes hints");
    }

    [Fact]
    public void GetConformers_HintProtocol_ReturnsConformers()
    {
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var protocol = SwiftTypeName.FromModuleQualifiedName("Swift.DataProtocol");
        var conformers = engine.GetConformers(protocol);

        Assert.True(conformers.Count >= 2, "DataProtocol should have at least 2 conformers from hints");
        Assert.Contains(conformers, c => c.CSharpType == "Data");
        Assert.Contains(conformers, c => c.CSharpType == "byte[]");
    }

    [Fact]
    public void GetConformers_UnknownProtocol_ReturnsEmpty()
    {
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var protocol = SwiftTypeName.FromModuleQualifiedName("Unknown.Protocol");
        var conformers = engine.GetConformers(protocol);

        Assert.Empty(conformers);
    }

    [Fact]
    public void IndexModuleConformances_AddsConformers()
    {
        var db = new ResolvingTypeDatabase();
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.MyType");
        db.Register(conformerTypeName, "TestLib", "MyType");

        var engine = new ConcreteSpecializationEngine(db);

        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.MyType", "TestLib.MyProtocol");
        engine.IndexModuleConformances(moduleDecl);

        var protocol = SwiftTypeName.FromModuleQualifiedName("TestLib.MyProtocol");
        var conformers = engine.GetConformers(protocol);

        Assert.Single(conformers);
        Assert.Equal("TestLib.MyType", conformers[0].SwiftQualifiedName);
    }

    [Fact]
    public void FindSpecializableMethods_NonGenericMethod_ReturnsEmpty()
    {
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var typeDecl = CreateStructWithMethod("Processor", "doWork", isGeneric: false);
        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Empty(result);
    }

    [Fact]
    public void FindSpecializableMethods_MethodWithConformers_ReturnsMethods()
    {
        var db = new ResolvingTypeDatabase();
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.ConcreteItem");
        db.Register(conformerTypeName, "TestLib", "ConcreteItem");

        var engine = new ConcreteSpecializationEngine(db);

        // Index module conformances
        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.ConcreteItem", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        // Create type with method-level generic constrained to Processable
        var typeDecl = CreateStructWithProtocolConstrainedMethod(
            "Processor", "process", "TestLib.Processable");

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Single(result);
        Assert.Equal("process", result[0].Method.Name);
        Assert.Single(result[0].SpecializableParams);
    }

    [Fact]
    public void FindSpecializableMethods_MethodWithoutConformers_ReturnsEmpty()
    {
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        // Don't index any conformances → no conformers for the protocol
        var typeDecl = CreateStructWithProtocolConstrainedMethod(
            "Processor", "process", "TestLib.UnknownProtocol");

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Empty(result);
    }

    [Fact]
    public void GetConformers_AttributeKindProtocol_ReturnsThreeConformers()
    {
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var protocol = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.AttributeKind");
        var conformers = engine.GetConformers(protocol);

        Assert.Equal(3, conformers.Count);
        Assert.Contains(conformers, c => c.CSharpType == "ColorAttribute");
        Assert.Contains(conformers, c => c.CSharpType == "SizeAttribute");
        Assert.Contains(conformers, c => c.CSharpType == "FlagAttribute");
    }

    [Fact]
    public void GetConformers_RoomPlanCapturedRoomAttribute_ReturnsFourConformers()
    {
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var protocol = SwiftTypeName.FromModuleQualifiedName("RoomPlan.CapturedRoomAttribute");
        var conformers = engine.GetConformers(protocol);

        Assert.Equal(4, conformers.Count);
        Assert.Contains(conformers, c => c.CSharpType == "RoomPlan.ChairType");
        Assert.Contains(conformers, c => c.CSharpType == "RoomPlan.SofaType");
        Assert.Contains(conformers, c => c.CSharpType == "RoomPlan.TableType");
        Assert.Contains(conformers, c => c.CSharpType == "RoomPlan.StorageType");
    }

    [Fact]
    public void ConcreteConformerNaming_ByteArray_HasSwiftLiteral()
    {
        var hints = ConcreteSpecializationEngine.LoadedHints;
        var dataProtocol = hints["Swift.DataProtocol"];
        var byteArrayConformer = dataProtocol.FirstOrDefault(c => c.CSharpType == "byte[]");

        Assert.NotNull(byteArrayConformer);
        Assert.Equal("[UInt8]", byteArrayConformer!.SwiftLiteral);
    }

    [Fact]
    public void LoadedHints_ContainsSwiftCollection()
    {
        var hints = ConcreteSpecializationEngine.LoadedHints;
        Assert.True(hints.ContainsKey("Swift.Collection"), "Should have Swift.Collection hints");

        var stringArrayConformer = hints["Swift.Collection"]
            .FirstOrDefault(c => c.SwiftLiteral == "[String]");
        Assert.NotNull(stringArrayConformer);
        Assert.Equal("Swift.SwiftArray<Swift.SwiftString>", stringArrayConformer!.CSharpType);
        Assert.NotNull(stringArrayConformer.AssociatedTypes);
        Assert.Equal("Swift.String", stringArrayConformer.AssociatedTypes!["Element"]);
    }

    [Fact]
    public void FindSpecializableMethods_SomeCollectionString_SpecializesToStringArray()
    {
        // `func joinItems(_ items: some Collection<String>) -> String` parses as
        // `<τ_0_0 where τ_0_0 : Swift.Collection, τ_0_0.Element == Swift.String>`.
        // The engine should match the [String] hint (which declares Element == Swift.String)
        // against the associated-type constraint and specialize — NOT blanket-skip.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var typeDecl = CreateClassWithSomeCollectionStringMethod("Host");

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Single(result);
        Assert.Single(result[0].SpecializableParams);
        var specializable = result[0].SpecializableParams[0];
        Assert.Equal("Swift.Collection", specializable.ConstraintProtocol.ToString());
        Assert.Single(specializable.Conformers);
        Assert.Equal("Swift.SwiftArray<Swift.SwiftString>", specializable.Conformers[0].CSharpType);
    }

    [Fact]
    public void FindSpecializableMethods_AssociatedTypeMismatch_NoSpecialization()
    {
        // If the method constrains Element == Swift.Int but the only hint conformer
        // declares Element == Swift.String, we must NOT specialize.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var typeDecl = CreateClassWithSomeCollectionElementMethod("Host", "Swift.Int");

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Empty(result);
    }

    [Fact]
    public void IndexModuleConformances_PropagatesAvailabilityToConformers()
    {
        // Regression test: specialized methods for a conformer like CryptoKit.SHA3_256Digest
        // (iOS 26+) must carry the conformer's @available floor onto the emitted wrapper,
        // otherwise the @_cdecl wrapper fails to compile against an iOS 13 floor.
        var db = new ResolvingTypeDatabase();
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.FuturisticDigest");
        db.Register(conformerTypeName, "TestLib", "FuturisticDigest");

        var engine = new ConcreteSpecializationEngine(db);

        var availability = new List<AvailabilityAnnotation>
        {
            new(Platform: "iOS", IntroducedVersion: "26.0",
                DeprecatedVersion: null, ObsoletedVersion: null,
                IsUnconditionallyDeprecated: false, IsUnconditionallyUnavailable: false,
                Message: null, Renamed: null)
        };
        var moduleDecl = CreateModuleWithConformer(
            "TestLib", "TestLib.FuturisticDigest", "TestLib.Digest",
            availability);
        engine.IndexModuleConformances(moduleDecl);

        var protocol = SwiftTypeName.FromModuleQualifiedName("TestLib.Digest");
        var conformers = engine.GetConformers(protocol);

        Assert.Single(conformers);
        Assert.NotNull(conformers[0].AvailabilityAnnotations);
        Assert.Contains(conformers[0].AvailabilityAnnotations!,
            a => a.Platform == "iOS" && a.IntroducedVersion == "26.0");
    }

    [Fact]
    public void IndexModuleConformances_MergesParentTypeAvailability()
    {
        // Nested conformer types inherit availability from their parent type chain.
        // Verify that when a nested struct conforms to a protocol, its ancestors'
        // @available annotations are merged onto the ConcreteConformer.
        var db = new ResolvingTypeDatabase();
        var parentTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.Outer");
        var nestedTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.Outer.Inner");
        db.Register(parentTypeName, "TestLib", "Outer");
        db.Register(nestedTypeName, "TestLib", "Outer.Inner");

        var engine = new ConcreteSpecializationEngine(db);

        var parentAvailability = new List<AvailabilityAnnotation>
        {
            new(Platform: "iOS", IntroducedVersion: "17.0",
                DeprecatedVersion: null, ObsoletedVersion: null,
                IsUnconditionallyDeprecated: false, IsUnconditionallyUnavailable: false,
                Message: null, Renamed: null)
        };
        var moduleDecl = CreateModuleWithNestedConformer(
            "TestLib", "TestLib.Outer", "TestLib.Outer.Inner",
            "TestLib.Digest", parentAvailability);
        engine.IndexModuleConformances(moduleDecl);

        var protocol = SwiftTypeName.FromModuleQualifiedName("TestLib.Digest");
        var conformers = engine.GetConformers(protocol);

        Assert.Single(conformers);
        Assert.NotNull(conformers[0].AvailabilityAnnotations);
        Assert.Contains(conformers[0].AvailabilityAnnotations!,
            a => a.Platform == "iOS" && a.IntroducedVersion == "17.0");
    }

    [Fact]
    public void FindSpecializableMethods_Constructor_ReturnsMethod()
    {
        var db = new ResolvingTypeDatabase();
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.ConcreteItem");
        db.Register(conformerTypeName, "TestLib", "ConcreteItem");

        var engine = new ConcreteSpecializationEngine(db);

        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.ConcreteItem", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        // Create a type whose constructor has a method-level generic constrained to Processable.
        var typeDecl = CreateStructWithProtocolConstrainedConstructor(
            "Box", "TestLib.Processable");

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Single(result);
        Assert.True(result[0].Method.IsConstructor, "Generic constructor should be specializable");
        Assert.Single(result[0].SpecializableParams);
    }

    // ==================== Test Doubles ====================

    private class EmptyTypeDatabase : ITypeDatabase
    {
        public string? AsyncLibraryName => null;
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => false;
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        {
            record = null;
            return false;
        }
        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    /// <summary>
    /// Type database that resolves specific types — needed because ConcreteSpecializationEngine
    /// only indexes conformers whose C# names can be resolved via the type database.
    /// </summary>
    private class ResolvingTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _records = new();

        public string? AsyncLibraryName => null;
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => _records.ContainsKey(swiftTypeName.ToString());
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
            => _records.TryGetValue(swiftTypeName.ToString(), out record);
        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }

        public void Register(SwiftTypeName swiftTypeName, string csNamespace, string csName)
        {
            _records[swiftTypeName.ToString()] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(csNamespace, csName),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            };
        }
    }

    // ==================== Helpers ====================

    private static ModuleDecl CreateModuleWithConformer(
        string moduleName, string conformerType, string protocolType,
        List<AvailabilityAnnotation>? availability = null)
    {
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName(conformerType);
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(protocolType);

        var structDecl = new StructDecl
        {
            Name = conformerTypeName.Name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = conformerTypeName,
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>
            {
                new(conformerTypeName, protocolTypeName, "")
            },
            MetadataAccessor = "",
            AvailabilityAnnotations = availability
        };

        return new ModuleDecl
        {
            Name = moduleName,
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { structDecl },
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            AvailabilityAnnotations = null
        };
    }

    private static ModuleDecl CreateModuleWithNestedConformer(
        string moduleName, string parentType, string nestedType, string protocolType,
        List<AvailabilityAnnotation>? parentAvailability)
    {
        var parentTypeName = SwiftTypeName.FromModuleQualifiedName(parentType);
        var nestedTypeName = SwiftTypeName.FromModuleQualifiedName(nestedType);
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(protocolType);

        var nestedStruct = new StructDecl
        {
            Name = nestedTypeName.Name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = nestedTypeName,
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>
            {
                new(nestedTypeName, protocolTypeName, "")
            },
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };

        var parentStruct = new StructDecl
        {
            Name = parentTypeName.Name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = parentTypeName,
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { nestedStruct },
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = parentAvailability
        };

        nestedStruct.ParentDecl = parentStruct;

        return new ModuleDecl
        {
            Name = moduleName,
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { parentStruct },
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            AvailabilityAnnotations = null
        };
    }

    private static StructDecl CreateStructWithMethod(string typeName, string methodName, bool isGeneric)
    {
        var method = new MethodDecl
        {
            Name = methodName,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{typeName}{methodName}",
            MethodType = MethodType.Static,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            GenericParameters = isGeneric
                ? new List<GenericArgumentDecl> { new("τ_1_0", "T", new(), new()) }
                : new List<GenericArgumentDecl>(),
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new TupleTypeSpec(new List<TypeSpec>()), IsGeneric = false }
            },
            AvailabilityAnnotations = null
        };

        var structDecl = new StructDecl
        {
            Name = typeName,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestLib.{typeName}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { method },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };

        method.ParentDecl = structDecl;
        return structDecl;
    }

    private static StructDecl CreateStructWithProtocolConstrainedMethod(
        string typeName, string methodName, string protocolName)
    {
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName);
        var conformance = new GenericParameterConformance(
            new[] { "τ_1_0" }, protocolTypeName, ConformanceKind.Protocol);

        var paramTypeSpec = new NamedTypeSpec("τ_1_0");

        var method = new MethodDecl
        {
            Name = methodName,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{typeName}{methodName}",
            MethodType = MethodType.Static,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_1_0", "T", new List<GenericParameterConformance> { conformance }, new())
            },
            CSSignature = new List<ArgumentDecl>
            {
                // Return type (first element)
                new() { Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new NamedTypeSpec("Swift.String"), IsGeneric = false },
                // Parameter
                new() { Name = "item", PrivateName = "item", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = paramTypeSpec, IsGeneric = true }
            },
            AvailabilityAnnotations = null
        };

        var structDecl = new StructDecl
        {
            Name = typeName,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestLib.{typeName}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { method },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };

        method.ParentDecl = structDecl;
        return structDecl;
    }

    private static StructDecl CreateStructWithProtocolConstrainedConstructor(
        string typeName, string protocolName)
    {
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName);
        var conformance = new GenericParameterConformance(
            new[] { "τ_0_0" }, protocolTypeName, ConformanceKind.Protocol);

        var paramTypeSpec = new NamedTypeSpec("τ_0_0");

        var ctor = new MethodDecl
        {
            Name = "init",
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{typeName}init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance> { conformance }, new())
            },
            CSSignature = new List<ArgumentDecl>
            {
                // Return type (first element) — constructor returns Self (Box here)
                new() { Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new NamedTypeSpec($"TestLib.{typeName}"), IsGeneric = false },
                // Parameter of generic type
                new() { Name = "source", PrivateName = "source", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = paramTypeSpec, IsGeneric = true }
            },
            AvailabilityAnnotations = null
        };

        var structDecl = new StructDecl
        {
            Name = typeName,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestLib.{typeName}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { ctor },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };

        ctor.ParentDecl = structDecl;
        return structDecl;
    }

    private static ClassDecl CreateClassWithSomeCollectionStringMethod(string typeName)
        => CreateClassWithSomeCollectionElementMethod(typeName, "Swift.String");

    private static ClassDecl CreateClassWithSomeCollectionElementMethod(string typeName, string elementType)
    {
        var collectionName = SwiftTypeName.FromModuleQualifiedName("Swift.Collection");
        var elementTypeName = SwiftTypeName.FromModuleQualifiedName(elementType);

        // Mirror the ABI parser output for `some Collection<String>`:
        //   GenericConformances:   Path=["τ_0_0"], target=Swift.Collection, Kind=Protocol
        //   AssosiatedTypeConformances: Path=["τ_0_0", "Element"], target=<elementType>, Kind=ConcreteType
        var protocolConformance = new GenericParameterConformance(
            new[] { "τ_0_0" }, collectionName, ConformanceKind.Protocol);
        var elementConformance = new GenericParameterConformance(
            new[] { "τ_0_0", "Element" }, elementTypeName, ConformanceKind.ConcreteType);

        var paramTypeSpec = new NamedTypeSpec("τ_0_0");

        var method = new MethodDecl
        {
            Name = "joinItems",
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{typeName}joinItems",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T",
                    new List<GenericParameterConformance> { protocolConformance },
                    new List<GenericParameterConformance> { elementConformance })
            },
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new NamedTypeSpec("Swift.String"), IsGeneric = false },
                new() { Name = "items", PrivateName = "items", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = paramTypeSpec, IsGeneric = true }
            },
            AvailabilityAnnotations = null
        };

        var classDecl = new ClassDecl
        {
            Name = typeName,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestLib.{typeName}"),
            MangledName = "",
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { method },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            AvailabilityAnnotations = null,
            IsFinal = false,
        };

        method.ParentDecl = classDecl;
        return classDecl;
    }
}
