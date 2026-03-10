// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests that ModuleProcessor handles mutually recursive type references
/// without a StackOverflowException (cycle detection via _processingInProgress).
/// </summary>
public class ModuleProcessorCycleTests
{
    [Fact]
    public void FinalizeTypeProcessing_MutuallyRecursiveStructs_CompletesWithoutStackOverflow()
    {
        // Construct two structs with mutual property references:
        // struct A { var b: B }
        // struct B { var a: A }
        // Without cycle detection, ProcessTypeRecursively would infinitely recurse.

        var typeSpecA = new NamedTypeSpec("TestModule.A");
        var typeSpecB = new NamedTypeSpec("TestModule.B");

        var structA = CreateStructDecl("A", typeSpecA, ("b", typeSpecB));
        var structB = CreateStructDecl("B", typeSpecB, ("a", typeSpecA));

        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            { typeSpecA, structA },
            { typeSpecB, structB },
        };

        var typeDatabase = new TypeDatabase();
        var processor = new ModuleProcessor(
            "TestModule",
            "/tmp/TestModule.dylib",
            "TestModule",
            typeDecls,
            typeDatabase,
            NullLogger.Instance);

        // This should complete without StackOverflowException
        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();

        Assert.NotNull(result);
        Assert.NotNull(result.ModuleDatabase);
    }

    [Fact]
    public void FinalizeTypeProcessing_SelfReferencingStruct_CompletesWithoutStackOverflow()
    {
        // struct Node { var next: Node }
        var typeSpecNode = new NamedTypeSpec("TestModule.Node");

        var structNode = CreateStructDecl("Node", typeSpecNode, ("next", typeSpecNode));

        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            { typeSpecNode, structNode },
        };

        var typeDatabase = new TypeDatabase();
        var processor = new ModuleProcessor(
            "TestModule",
            "/tmp/TestModule.dylib",
            "TestModule",
            typeDecls,
            typeDatabase,
            NullLogger.Instance);

        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();

        Assert.NotNull(result);
    }

    [Fact]
    public void FinalizeTypeProcessing_IntraModuleCodableChain_SetsFlagRegardlessOfOrder()
    {
        // protocol A: B {}  — declared BEFORE B
        // protocol B: Codable {}
        // A should get InheritsCodable flag even though it's processed first.
        var typeSpecA = new NamedTypeSpec("TestModule.ProtocolA");
        var typeSpecB = new NamedTypeSpec("TestModule.ProtocolB");

        var protocolB = CreateProtocolDecl("ProtocolB", typeSpecB,
            inherited: new[] { new NamedTypeSpec("Codable") });
        var protocolA = CreateProtocolDecl("ProtocolA", typeSpecA,
            inherited: new[] { new NamedTypeSpec("TestModule.ProtocolB") });

        // A before B — exercises the declaration order issue
        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            { typeSpecA, protocolA },
            { typeSpecB, protocolB },
        };

        var typeDatabase = new TypeDatabase();
        var processor = new ModuleProcessor(
            "TestModule",
            "/tmp/TestModule.dylib",
            "TestModule",
            typeDecls,
            typeDatabase,
            NullLogger.Instance);

        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();
        typeDatabase.AddModuleDatabase(result.ModuleDatabase);

        // Both should have InheritsCodable
        Assert.True(typeDatabase.TryGetTypeRecord(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ProtocolB"), out var recordB));
        Assert.True(recordB!.Flags.HasFlag(TypeRecordFlags.InheritsCodable));

        Assert.True(typeDatabase.TryGetTypeRecord(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ProtocolA"), out var recordA));
        Assert.True(recordA!.Flags.HasFlag(TypeRecordFlags.InheritsCodable));
    }

    private static ProtocolDecl CreateProtocolDecl(string name, NamedTypeSpec typeSpec,
        NamedTypeSpec[]? inherited = null)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromTypeSpec(typeSpec),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = inherited?.ToList() ?? new List<NamedTypeSpec>(),
            HasSelfRequirement = false,
            IsClassBound = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static StructDecl CreateStructDecl(string name, NamedTypeSpec typeSpec, params (string propName, NamedTypeSpec propType)[] properties)
    {
        var propertyDecls = new List<PropertyDecl>();
        foreach (var (propName, propType) in properties)
        {
            propertyDecls.Add(new PropertyDecl
            {
                Name = propName,
                SwiftTypeSpec = propType,
                IsStatic = false,
                HasStorage = true,
                Accessors = new List<AccessorDecl>(),
                ParentDecl = null,
                ModuleDecl = null
            });
        }

        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromTypeSpec(typeSpec),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            Properties = propertyDecls,
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }
}
