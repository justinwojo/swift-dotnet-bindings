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

    [Fact]
    public void FinalizeTypeProcessing_FrozenStructWithOptionalBoolField_DeclinesLayout()
    {
        // Finding 44: Optional<Bool> uses Swift's spare-bit optimization — Optional<Bool> is
        // 1 byte (nil folded into a spare Bool bit pattern), NOT inner(1) + tag(1) = 2 bytes.
        // The old ClassifyFieldType blanket `{inner},i1` fabricated a 2-byte field. Only
        // payloads that use every bit pattern (the fixed-width int/float scalars) genuinely
        // gain a tag byte; for spare-bit payloads (Bool, pointers, …) the layout cannot be
        // derived by this hand-rolled grammar, so the struct must decline to @_cdecl
        // (AbiFieldLayout null) rather than ship a wrong width.
        var typeSpecS = new NamedTypeSpec("TestModule.OptBoolHolder");
        var optionalBool = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Bool"));
        var structS = CreateStructDecl("OptBoolHolder", typeSpecS, ("flag", optionalBool));

        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl> { { typeSpecS, structS } };
        var typeDatabase = new TypeDatabase();
        RegisterSwiftOptional(typeDatabase);
        var processor = new ModuleProcessor(
            "TestModule", "/tmp/TestModule.dylib", "TestModule", typeDecls, typeDatabase, NullLogger.Instance);

        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();
        typeDatabase.AddModuleDatabase(result.ModuleDatabase);

        Assert.True(typeDatabase.TryGetTypeRecord(
            SwiftTypeName.FromModuleQualifiedName("TestModule.OptBoolHolder"), out var record));
        // Struct stays frozen — declining the layout does not clear Frozen (that only happens
        // for unresolvable field types); it just leaves AbiFieldLayout null.
        Assert.True(record!.Flags.HasFlag(TypeRecordFlags.Frozen));
        Assert.Null(record.AbiFieldLayout);
    }

    [Fact]
    public void FinalizeTypeProcessing_FrozenStructWithOptionalInt32Field_KeepsTagLayout()
    {
        // Control for Finding 44: a fixed-width integer payload genuinely gains a 1-byte tag
        // (Optional<Int32> is 5 bytes in Swift — Int32 uses every bit pattern, no spare bits),
        // so the `{inner},i1` layout is provably correct and is PRESERVED. Only the spare-bit
        // payloads decline — this case must keep emitting "i4,i1".
        var typeSpecS = new NamedTypeSpec("TestModule.OptInt32Holder");
        var optionalInt32 = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int32"));
        var structS = CreateStructDecl("OptInt32Holder", typeSpecS, ("count", optionalInt32));

        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl> { { typeSpecS, structS } };
        var typeDatabase = new TypeDatabase();
        RegisterSwiftOptional(typeDatabase);
        var processor = new ModuleProcessor(
            "TestModule", "/tmp/TestModule.dylib", "TestModule", typeDecls, typeDatabase, NullLogger.Instance);

        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();
        typeDatabase.AddModuleDatabase(result.ModuleDatabase);

        Assert.True(typeDatabase.TryGetTypeRecord(
            SwiftTypeName.FromModuleQualifiedName("TestModule.OptInt32Holder"), out var record));
        Assert.Equal("i4,i1", record!.AbiFieldLayout);
    }

    [Fact]
    public void FinalizeTypeProcessing_FrozenStructWithUnknownSizeEnumField_DeclinesLayout()
    {
        // Finding 44: a simple-enum field whose own InlineSize is unknown (null — e.g. a
        // cross-module dependency emitted without a measured size) must decline the parent
        // struct's layout (AbiFieldLayout null) rather than fabricate an 8-byte field via the
        // old `record.InlineSize ?? 8`, which silently inflated a 1-byte enum to 8.
        var typeDatabase = new TypeDatabase();

        // Pre-register the referenced simple enum with an UNKNOWN size.
        var enumName = SwiftTypeName.FromModuleQualifiedName("Dep.Mode");
        var enumModule = new ModuleTypeDatabase("Dep", "/tmp/Dep.dylib");
        enumModule.RegisterType(enumName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Dep", "Mode"),
            SwiftTypeName = enumName,
            MetadataAccessor = "$s3Dep4ModeO",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
            Kind = TypeRecordKind.Enum,
            InlineSize = null, // size unknown — must not default to 8
        });
        typeDatabase.AddModuleDatabase(enumModule);

        var typeSpecS = new NamedTypeSpec("TestModule.ModeHolder");
        var structS = CreateStructDecl("ModeHolder", typeSpecS, ("mode", new NamedTypeSpec("Dep.Mode")));
        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl> { { typeSpecS, structS } };
        var processor = new ModuleProcessor(
            "TestModule", "/tmp/TestModule.dylib", "TestModule", typeDecls, typeDatabase, NullLogger.Instance);

        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();
        typeDatabase.AddModuleDatabase(result.ModuleDatabase);

        Assert.True(typeDatabase.TryGetTypeRecord(
            SwiftTypeName.FromModuleQualifiedName("TestModule.ModeHolder"), out var record));
        Assert.True(record!.Flags.HasFlag(TypeRecordFlags.Frozen));
        Assert.Null(record.AbiFieldLayout);
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

    /// <summary>
    /// Registers the stdlib <c>Swift.Optional</c> enum record (seeded from SwiftDatabase.xml in the
    /// real pipeline) so that a frozen struct with an Optional-typed field stays frozen through
    /// CacluateFlags and reaches ComputeAbiFieldLayout — the path Finding 44's Optional `,i1` rule
    /// lives on. Without it, CacluateFlags fails the field's TryGetTypeRecord and clears Frozen, so
    /// the layout is never computed and the rule is never exercised.
    /// </summary>
    private static void RegisterSwiftOptional(TypeDatabase typeDatabase)
    {
        var optionalName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional");
        var swiftModule = new ModuleTypeDatabase("Swift", "/tmp/Swift.dylib");
        swiftModule.RegisterType(optionalName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "Optional"),
            SwiftTypeName = optionalName,
            MetadataAccessor = "$sSqMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Enum,
        });
        typeDatabase.AddModuleDatabase(swiftModule);
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
