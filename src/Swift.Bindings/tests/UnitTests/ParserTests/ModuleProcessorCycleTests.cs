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

    [Fact]
    public void FinalizeTypeProcessing_QualifiedInheritedShadowsLocalSimpleName_PrefersCrossModule()
    {
        // Shadowing regression: `protocol Child: External.Parent` plus a local
        // `TestModule.Parent`. The intra-module walk used to match by simple name,
        // recurse into the (non-class-bound) local Parent, and `continue` — never
        // consulting the cross-module External.Parent's ClassBound flag.
        //
        // After the fix, a module-qualified inherited reference is matched only
        // against the exact qualified intra-module entry; if absent, the cross-module
        // TypeDatabase is consulted. Child must inherit class-boundedness from
        // External.Parent rather than be shadowed by the same-simple-named local.
        var typeDatabase = new TypeDatabase();

        var externalParentSpec = new NamedTypeSpec("External.Parent");
        var externalParent = CreateProtocolDecl("Parent", externalParentSpec,
            classBound: true, moduleName: "External");
        var externalDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            { externalParentSpec, externalParent },
        };
        var externalProcessor = new ModuleProcessor(
            "External",
            "/tmp/External.dylib",
            "External",
            externalDecls,
            typeDatabase,
            NullLogger.Instance);
        var externalResult = externalProcessor.FinalizeTypeProcessingAndCreateModuleDatabase();
        typeDatabase.AddModuleDatabase(externalResult.ModuleDatabase);

        // Sanity: External.Parent picked up ClassBound at parse time.
        Assert.True(typeDatabase.TryGetTypeRecord(
            SwiftTypeName.FromModuleQualifiedName("External.Parent"), out var externalRecord));
        Assert.True(externalRecord!.Flags.HasFlag(TypeRecordFlags.ClassBound));

        var localParentSpec = new NamedTypeSpec("TestModule.Parent");
        var childSpec = new NamedTypeSpec("TestModule.Child");
        var localParent = CreateProtocolDecl("Parent", localParentSpec);
        var child = CreateProtocolDecl("Child", childSpec,
            inherited: new[] { externalParentSpec });
        var testDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            { localParentSpec, localParent },
            { childSpec, child },
        };
        var processor = new ModuleProcessor(
            "TestModule",
            "/tmp/TestModule.dylib",
            "TestModule",
            testDecls,
            typeDatabase,
            NullLogger.Instance);
        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();
        typeDatabase.AddModuleDatabase(result.ModuleDatabase);

        Assert.True(typeDatabase.TryGetTypeRecord(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Child"), out var childRecord));
        Assert.True(childRecord!.Flags.HasFlag(TypeRecordFlags.ClassBound),
            "Child should inherit ClassBound from cross-module External.Parent, " +
            "not be shadowed by the non-class-bound local TestModule.Parent.");

        // Local Parent stays non-class-bound (unchanged behavior).
        Assert.True(typeDatabase.TryGetTypeRecord(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Parent"), out var localParentRecord));
        Assert.False(localParentRecord!.Flags.HasFlag(TypeRecordFlags.ClassBound));
    }

    [Fact]
    public void FinalizeTypeProcessing_FrozenGenericStructWithGenericParamField_DoesNotThrow()
    {
        // Regression: a frozen generic struct whose stored property is typed as its own
        // type parameter (e.g. `@frozen struct Pair<T> { let first: T }`) used to crash
        // ModuleProcessor.CacluateFlags. The property's TypeSpec is a bare `τ_0_0`
        // NamedTypeSpec with no module qualifier; SwiftTypeName.FromTypeSpec throws
        // ArgumentException("Invalid module-qualified name: τ_0_0") on the single-segment name.
        // TryGetTypeRecord now short-circuits when !HasModule() and the struct is treated
        // as non-frozen + RequiresMemoryManagement.
        var typeSpecPair = new NamedTypeSpec("TestModule.Pair");

        var bareGenericParam = new NamedTypeSpec("τ_0_0");
        var structPair = CreateStructDecl("Pair", typeSpecPair,
            ("first", bareGenericParam),
            ("second", bareGenericParam));

        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>
        {
            { typeSpecPair, structPair },
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

        // The struct was processed (no throw) and a record exists. Because the property
        // type could not be resolved, the Frozen flag was cleared and RequiresMemoryManagement
        // was set — the conservative fallback for unknown field layout.
        Assert.True(typeDatabase.TryGetTypeRecord(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Pair"), out var record));
        Assert.False(record!.Flags.HasFlag(TypeRecordFlags.Frozen));
        Assert.True(record.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement));
    }

    private static ProtocolDecl CreateProtocolDecl(string name, NamedTypeSpec typeSpec,
        NamedTypeSpec[]? inherited = null, bool classBound = false, string moduleName = "TestModule")
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromTypeSpec(typeSpec),
            MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}P",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = inherited?.ToList() ?? new List<NamedTypeSpec>(),
            HasSelfRequirement = false,
            IsClassBound = classBound,
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
