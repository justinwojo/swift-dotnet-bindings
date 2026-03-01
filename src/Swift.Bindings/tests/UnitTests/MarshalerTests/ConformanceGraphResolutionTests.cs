// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ConformanceGraph resolution in BoundGenericsHandler and ResolveSelfElement.
/// </summary>
public class ConformanceGraphResolutionTests
{
    #region BoundGenericsHandler Graph Resolution

    [Fact]
    public void BoundGenericHandler_AssociatedTypeRef_ResolvesViaGraph()
    {
        // Array<τ_0_0.Element> where conforming type maps Element → Swift.Int (a known type)
        var graph = new ConformanceGraph();
        graph.AddWitness("TestModule.MyCursor", "TestModule.Cursor", "Element",
            new NamedTypeSpec("Swift.Int"));

        var typeDatabase = new MockTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase, graph);

        // Create a conforming type with the matching conformance
        var conformingType = CreateClassDecl("MyCursor", "TestModule",
            conformances: new[] { CreateConformance("TestModule.Cursor") });

        // Array<τ_0_0.Element> — τ_0_0.Element is an AssociatedTypeReferenceSpec
        var assocRef = new AssociatedTypeReferenceSpec("τ_0_0", "Element");
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(assocRef);

        var argDecl = new ArgumentDecl
        {
            SwiftTypeSpec = arraySpec,
            Name = "elements",
            PrivateName = "elements",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = conformingType,
            ModuleDecl = null
        };

        var result = handler.TranslateBoundGenericTypeToCSharp(argDecl);

        // Graph resolves Element → Swift.Int → System.Int64
        Assert.Contains("SwiftArray", result);
        Assert.Contains("long", result);
        Assert.DoesNotContain("AnyType", result);
    }

    [Fact]
    public void BoundGenericHandler_AssociatedTypeRef_MissingEntry_FallsBackToAnyType()
    {
        // No graph entries — should fall back to AnyType
        var graph = new ConformanceGraph();
        var typeDatabase = new MockTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase, graph);

        var conformingType = CreateClassDecl("MyType", "MyModule",
            conformances: new[] { CreateConformance("MyModule.MyProtocol") });

        var assocRef = new AssociatedTypeReferenceSpec("τ_0_0", "Element");
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(assocRef);

        var argDecl = new ArgumentDecl
        {
            SwiftTypeSpec = arraySpec,
            Name = "elements",
            PrivateName = "elements",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = conformingType,
            ModuleDecl = null
        };

        var result = handler.TranslateBoundGenericTypeToCSharp(argDecl);

        // Falls back to AnyType for the unresolved associated type
        Assert.Contains("AnyType", result);
    }

    [Fact]
    public void BoundGenericHandler_MethodGenericParam_SkipsGraphResolution()
    {
        // τ_1_0.Element — method-level generic (depth 1), should NOT try graph resolution
        var graph = new ConformanceGraph();
        // Even though there's a mapping, depth-1 should be ignored
        graph.AddWitness("MyModule.MyType", "MyModule.MyProtocol", "Element",
            new NamedTypeSpec("Swift.String"));

        var typeDatabase = new MockTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase, graph);

        var conformingType = CreateClassDecl("MyType", "MyModule",
            conformances: new[] { CreateConformance("MyModule.MyProtocol") });

        // τ_1_0 is method-level (depth 1), NOT type-level (depth 0)
        var assocRef = new AssociatedTypeReferenceSpec("τ_1_0", "Element");
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(assocRef);

        var argDecl = new ArgumentDecl
        {
            SwiftTypeSpec = arraySpec,
            Name = "elements",
            PrivateName = "elements",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = conformingType,
            ModuleDecl = null
        };

        var result = handler.TranslateBoundGenericTypeToCSharp(argDecl);

        // Should fall back to AnyType since τ_1_0 is method-level
        Assert.Contains("AnyType", result);
    }

    [Fact]
    public void BoundGenericHandler_NoGraph_FallsBackToAnyType()
    {
        // When constructed without a graph, associated types still degrade to AnyType
        var typeDatabase = new MockTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase);  // No graph

        var assocRef = new AssociatedTypeReferenceSpec("τ_0_0", "Element");
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(assocRef);

        var argDecl = new ArgumentDecl
        {
            SwiftTypeSpec = arraySpec,
            Name = "elements",
            PrivateName = "elements",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };

        var result = handler.TranslateBoundGenericTypeToCSharp(argDecl);

        // Without graph, falls back to AnyType
        Assert.Contains("AnyType", result);
    }

    [Fact]
    public void BoundGenericHandler_SelfBaseType_ResolvesViaGraph()
    {
        // Self.Element — explicit Self reference should resolve via graph
        var graph = new ConformanceGraph();
        graph.AddWitness("TestModule.RowCursor", "TestModule.Cursor", "Element",
            new NamedTypeSpec("Swift.String"));

        var typeDatabase = new MockTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase, graph);

        var conformingType = CreateClassDecl("RowCursor", "TestModule",
            conformances: new[] { CreateConformance("TestModule.Cursor") });

        var assocRef = new AssociatedTypeReferenceSpec("Self", "Element");
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(assocRef);

        var argDecl = new ArgumentDecl
        {
            SwiftTypeSpec = arraySpec,
            Name = "elements",
            PrivateName = "elements",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = conformingType,
            ModuleDecl = null
        };

        var result = handler.TranslateBoundGenericTypeToCSharp(argDecl);

        // Self is a valid base type — graph resolves Element → Swift.String → SwiftString
        Assert.Contains("SwiftArray", result);
        Assert.Contains("SwiftString", result);
        Assert.DoesNotContain("AnyType", result);
    }

    [Fact]
    public void BoundGenericHandler_ChainedAssocType_FallsBackToAnyType()
    {
        // Graph has a chained entry (AssociatedTypeReferenceSpec) — should skip it
        var graph = new ConformanceGraph();
        graph.AddWitness("GRDB.SomeType", "GRDB.SomeProtocol", "Fetcher",
            new AssociatedTypeReferenceSpec("τ_0_0", "Fetcher")); // Chained

        var typeDatabase = new MockTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase, graph);

        var conformingType = CreateClassDecl("SomeType", "GRDB",
            conformances: new[] { CreateConformance("GRDB.SomeProtocol") });

        var assocRef = new AssociatedTypeReferenceSpec("τ_0_0", "Fetcher");
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(assocRef);

        var argDecl = new ArgumentDecl
        {
            SwiftTypeSpec = arraySpec,
            Name = "fetchers",
            PrivateName = "fetchers",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = conformingType,
            ModuleDecl = null
        };

        var result = handler.TranslateBoundGenericTypeToCSharp(argDecl);

        // Chained references can't be further resolved, falls back to AnyType
        Assert.Contains("AnyType", result);
    }

    [Fact]
    public void BoundGenericHandler_AmbiguousWitnesses_FallsBackToAnyType()
    {
        // Type conforms to two protocols that both define "Element" but map to different types.
        // Graph should detect the ambiguity and fall back to AnyType.
        var graph = new ConformanceGraph();
        graph.AddWitness("TestModule.MyType", "TestModule.ProtocolA", "Element",
            new NamedTypeSpec("Swift.Int"));
        graph.AddWitness("TestModule.MyType", "TestModule.ProtocolB", "Element",
            new NamedTypeSpec("Swift.String"));

        var typeDatabase = new MockTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase, graph);

        var conformingType = CreateClassDecl("MyType", "TestModule",
            conformances: new[]
            {
                CreateConformance("TestModule.ProtocolA"),
                CreateConformance("TestModule.ProtocolB")
            });

        var assocRef = new AssociatedTypeReferenceSpec("τ_0_0", "Element");
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(assocRef);

        var argDecl = new ArgumentDecl
        {
            SwiftTypeSpec = arraySpec,
            Name = "elements",
            PrivateName = "elements",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = conformingType,
            ModuleDecl = null
        };

        var result = handler.TranslateBoundGenericTypeToCSharp(argDecl);

        // Ambiguous: two protocols resolve Element differently → AnyType
        Assert.Contains("AnyType", result);
    }

    [Fact]
    public void BoundGenericHandler_ConsistentWitnesses_ResolvesCorrectly()
    {
        // Type conforms to two protocols that both define "Element" and map to the SAME type.
        // This should resolve normally (no ambiguity).
        var graph = new ConformanceGraph();
        graph.AddWitness("TestModule.MyType", "TestModule.ProtocolA", "Element",
            new NamedTypeSpec("Swift.Int"));
        graph.AddWitness("TestModule.MyType", "TestModule.ProtocolB", "Element",
            new NamedTypeSpec("Swift.Int"));

        var typeDatabase = new MockTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase, graph);

        var conformingType = CreateClassDecl("MyType", "TestModule",
            conformances: new[]
            {
                CreateConformance("TestModule.ProtocolA"),
                CreateConformance("TestModule.ProtocolB")
            });

        var assocRef = new AssociatedTypeReferenceSpec("τ_0_0", "Element");
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(assocRef);

        var argDecl = new ArgumentDecl
        {
            SwiftTypeSpec = arraySpec,
            Name = "elements",
            PrivateName = "elements",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = conformingType,
            ModuleDecl = null
        };

        var result = handler.TranslateBoundGenericTypeToCSharp(argDecl);

        // Both protocols agree: Element → Swift.Int → Int64
        Assert.Contains("long", result);
        Assert.DoesNotContain("AnyType", result);
    }

    #endregion

    #region Helpers

    private static ClassDecl CreateClassDecl(string name, string module,
        TypeConformance[]? conformances = null)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
            MangledName = "",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = conformances?.ToList() ?? new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static TypeConformance CreateConformance(string protocolQualifiedName)
    {
        return new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("Test.DummyType"),
            SwiftTypeName.FromModuleQualifiedName(protocolQualifiedName),
            "");
    }

    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

        public string AsyncLibraryName => null!;

        public MockTypeDatabase()
        {
            _types = new Dictionary<string, TypeRecord>
            {
                ["Swift.Array"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftArray"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.String"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Int"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                }
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record!);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
