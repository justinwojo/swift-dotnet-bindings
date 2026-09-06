// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <c>CollectionProjectionEmitter</c> — the emitter that advertises a Swift
/// <c>Collection</c> conformer as a C# <c>IReadOnlyList&lt;TElement&gt;</c>.
///
/// <para>The contract under test is the index space. <c>IReadOnlyList&lt;T&gt;</c> is
/// zero-based: position 0 is the first element and the valid range is <c>0 .. Count-1</c>.
/// A Swift <c>Collection</c> makes no such promise — a slice or a window over a larger
/// buffer starts wherever its base does — so the projection has to translate the managed
/// offset into the collection's own index space, and range-check the offset against the
/// element count rather than against the native index bounds.</para>
///
/// <para>The witness-backed shape does that translation natively, inside the emitted
/// <c>@_cdecl</c> shim, which keeps the read to a single native call under a single payload
/// lease. The array-backed shape delegates to a projected Swift <c>Array</c>, whose
/// <c>startIndex</c> is always zero, so it needs no translation of its own.</para>
/// </summary>
public class CollectionProjectionEmitterTests
{
    private const string ModuleName = "TestModule";

    // ===================================================================
    //  Witness-backed shape — the emitted native shim owns the translation
    // ===================================================================

    [Fact]
    public void WitnessBacked_Shim_TranslatesManagedOffsetOntoTheCollectionsOwnIndexSpace()
    {
        var (_, swift) = EmitWitnessBacked();

        // The managed position is an offset from startIndex, applied with the Collection
        // API for exactly that. Reaching the element through Collection's own index
        // arithmetic is what makes the projection correct for a nonzero startIndex.
        Assert.Contains("offsetBy: position", swift);
        Assert.Contains("obj.startIndex", swift);
    }

    [Fact]
    public void WitnessBacked_Shim_NeverSubscriptsWithTheManagedOffsetDirectly()
    {
        var (_, swift) = EmitWitnessBacked();

        // The defect this guards: using the managed zero-based offset as if it were the
        // collection's native index. For a collection based at 5 that accepts view[5] and
        // rejects view[0] — the exact inverse of the advertised contract.
        Assert.DoesNotContain("obj[position]", swift);
    }

    [Fact]
    public void WitnessBacked_Shim_RangeChecksTheOffsetAgainstTheElementCount()
    {
        var (_, swift) = EmitWitnessBacked();

        // Zero-based lower bound, count-based upper bound. Checking the offset against the
        // collection's own startIndex/endIndex would reject valid offsets (and admit invalid
        // ones) on any collection that does not happen to start at zero.
        Assert.Contains("position >= 0", swift);
        Assert.Contains("obj.count", swift);
        Assert.DoesNotContain("position >= obj.startIndex", swift);
        Assert.DoesNotContain("position < obj.endIndex", swift);
    }

    [Fact]
    public void WitnessBacked_Shim_ChecksBoundsBeforeReadingSoSwiftsPreconditionIsNeverReached()
    {
        var (_, swift) = EmitWitnessBacked();

        // Swift's Collection subscript is a precondition, not a throwing call: an
        // out-of-range read traps the process. The shim has to return the verdict instead,
        // and the guard has to sit ahead of the element read for that to hold.
        var guardAt = swift.IndexOf("guard position", System.StringComparison.Ordinal);
        var readAt = swift.IndexOf("let result:", System.StringComparison.Ordinal);

        Assert.True(guardAt >= 0, "shim declares a bounds guard");
        Assert.True(readAt >= 0, "shim reads the element");
        Assert.True(guardAt < readAt, "the bounds guard precedes the element read");
    }

    [Fact]
    public void WitnessBacked_Indexer_ThrowsArgumentOutOfRangeOnTheOutOfBoundsVerdict()
    {
        var (cs, _) = EmitWitnessBacked();

        // A managed bounds error, catchable by the consumer, is the whole point of routing
        // the check through the shim's verdict rather than letting Swift trap.
        Assert.Contains("public TElement this[int index]", cs);
        Assert.Contains("ArgumentOutOfRangeException", cs);
        Assert.Contains("nameof(index)", cs);
    }

    [Fact]
    public void WitnessBacked_Indexer_PassesTheManagedOffsetThroughUnchanged()
    {
        var (cs, _) = EmitWitnessBacked();

        // The translation belongs on the Swift side: the shim already holds the collection,
        // so doing it there keeps the read to ONE native call under ONE payload lease.
        // A managed-side translation would need a second call just to learn startIndex.
        Assert.Contains("(nint)index", cs);
        Assert.DoesNotContain("index - ", cs);
        Assert.DoesNotContain("index +", cs);
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(cs, "PInvoke_collSubscript_"));
    }

    [Fact]
    public void WitnessBacked_Enumerator_WalksZeroToCountThroughTheProjectedIndexer()
    {
        var (cs, _) = EmitWitnessBacked();

        // The enumerator is the surface most consumers actually touch (foreach/LINQ). It
        // must agree with the indexer's index space, i.e. start at 0 and stop at Count.
        Assert.Contains("int __count = Count;", cs);
        Assert.Contains("for (int __i = 0; __i < __count; __i++)", cs);
        Assert.Contains("yield return this[__i];", cs);
    }

    [Fact]
    public void WitnessBacked_Count_ProjectsTheCollectionsElementCount()
    {
        var (cs, _) = EmitWitnessBacked();

        // Count is the element count, not endIndex — the two differ on any offset collection,
        // and the indexer's upper bound is keyed off Count.
        Assert.Contains("public int Count", cs);
        Assert.Contains("PInvoke_collCount_", cs);
    }

    // ===================================================================
    //  Array-backed shape — a Swift Array is zero-based by construction
    // ===================================================================

    [Fact]
    public void ArrayBacked_MembersDelegateToTheProjectedArray()
    {
        var structDecl = BuildArrayBackedStruct();
        var typeDatabase = BuildTypeDatabase();
        var csStringWriter = new StringWriter();

        CollectionProjectionEmitter.EmitMembers(
            new CSharpWriter(csStringWriter),
            structDecl,
            "Bag<TElement>",
            typeDatabase,
            propertyRenames: null,
            NullLogger.Instance);

        var cs = csStringWriter.ToString();

        // Swift's Array always starts at zero, so delegating the managed offset straight to
        // the projected array is already zero-based-correct and needs no translation.
        Assert.Contains("public int Count => Items.Count;", cs);
        Assert.Contains("public TElement this[int index] => Items[index];", cs);
        Assert.Contains("GetEnumerator() => Items.GetEnumerator();", cs);
    }

    // ===================================================================
    //  Interface planning
    // ===================================================================

    [Fact]
    public void TryPlanInterface_WitnessBackedCollection_AdvertisesReadOnlyList()
    {
        var structDecl = BuildWitnessBackedStruct();
        var typeDatabase = BuildTypeDatabase();

        var iface = CollectionProjectionEmitter.TryPlanInterface(structDecl, typeDatabase);

        // The zero-based index contract the rest of this class tests is inherited from this
        // interface; a projection that stopped advertising it would owe nothing.
        Assert.NotNull(iface);
        Assert.Contains("IReadOnlyList<TElement>", iface!);
    }

    [Fact]
    public void TryPlanInterface_NoCollectionConformance_DoesNotProject()
    {
        var structDecl = BuildWitnessBackedStruct();
        structDecl.Conformances.Clear();
        var typeDatabase = BuildTypeDatabase();

        Assert.Null(CollectionProjectionEmitter.TryPlanInterface(structDecl, typeDatabase));
    }

    [Fact]
    public void TryPlanInterface_WitnessShapeOnBareSequence_DoesNotProject()
    {
        // The shim reads `count` and walks with `index(_:offsetBy:)`, both of which come from
        // Collection. A conformer that publishes the index shape on a bare Sequence has
        // neither, so the emitted wrapper would not build — no projection is the honest
        // answer, and it is the same answer the type got before it published that shape.
        var structDecl = BuildWitnessBackedStruct();
        ReplaceConformance(structDecl, "Swift.Sequence");
        var typeDatabase = BuildTypeDatabase();

        Assert.Null(CollectionProjectionEmitter.TryPlanInterface(structDecl, typeDatabase));
    }

    [Fact]
    public void TryPlanInterface_ArrayBackedOnBareSequence_StillProjects()
    {
        // The array-backed shape delegates to a projected Swift Array and calls no Collection
        // member of its own, so the narrower conformance requirement must not reach it.
        var structDecl = BuildArrayBackedStruct();
        ReplaceConformance(structDecl, "Swift.Sequence");
        var typeDatabase = BuildTypeDatabase();

        var iface = CollectionProjectionEmitter.TryPlanInterface(structDecl, typeDatabase);

        Assert.NotNull(iface);
        Assert.Contains("IReadOnlyList<TElement>", iface!);
    }

    [Fact]
    public void TryPlanInterface_WitnessShapeOnRandomAccessCollection_StillProjects()
    {
        // Collection's refinements supply the same members, so they keep the witness path.
        var structDecl = BuildWitnessBackedStruct();
        ReplaceConformance(structDecl, "Swift.RandomAccessCollection");
        var typeDatabase = BuildTypeDatabase();

        var iface = CollectionProjectionEmitter.TryPlanInterface(structDecl, typeDatabase);

        Assert.NotNull(iface);
        Assert.Contains("IReadOnlyList<TElement>", iface!);
    }

    // ===================================================================
    //  Fixtures
    // ===================================================================

    private static (string CSharp, string Swift) EmitWitnessBacked()
    {
        var structDecl = BuildWitnessBackedStruct();
        var typeDatabase = BuildTypeDatabase();

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();

        CollectionProjectionEmitter.EmitMembers(
            new CSharpWriter(csStringWriter),
            structDecl,
            "Window<TElement>",
            typeDatabase,
            propertyRenames: null,
            NullLogger.Instance,
            new SwiftWriter(swiftStringWriter),
            new ModuleEmissionContext(),
            new PInvokeHelperContext("Window", new[] { "TElement" }));

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    private static ModuleDecl CreateModule() => new()
    {
        Name = ModuleName,
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static StructDecl CreateCollectionStruct(string name, ModuleDecl module)
    {
        var structDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{ModuleName}.{name}"),
            MangledName = $"$s{ModuleName.Length}{ModuleName}{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "Element",
                    new List<GenericParameterConformance>(),
                    new List<GenericParameterConformance>()),
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = module,
            ModuleDecl = module,
            IsFrozen = false,
            MetadataAccessor = $"$s{ModuleName.Length}{ModuleName}{name.Length}{name}VMa",
        };

        structDecl.Conformances.Add(new TypeConformance(
            structDecl.SwiftTypeName,
            SwiftTypeName.FromModuleQualifiedName("Swift.Collection"),
            "$sCollectionConformanceDescriptor"));

        return structDecl;
    }

    /// <summary>
    /// The opaque-storage shape: no public array to delegate to, only the Collection
    /// requirements on the ABI. This is the path that emits its own <c>@_cdecl</c> shim.
    /// </summary>
    private static StructDecl BuildWitnessBackedStruct()
    {
        var module = CreateModule();
        var structDecl = CreateCollectionStruct("Window", module);

        foreach (var name in new[] { "startIndex", "endIndex" })
        {
            structDecl.Properties.Add(
                AttachProperty(
                    TestDecls.Property(name, new NamedTypeSpec("Swift.Int"), module: ModuleName),
                    structDecl,
                    module));
        }

        var subscriptGetter = TestDecls.Method("subscript_get", module: ModuleName);
        subscriptGetter.ParentDecl = structDecl;
        subscriptGetter.ModuleDecl = module;

        structDecl.Subscripts.Add(new SubscriptDecl
        {
            Name = "subscript",
            MangledName = $"$s{ModuleName.Length}{ModuleName}6WindowyxSicig",
            ReturnTypeSpec = new NamedTypeSpec("τ_0_0"),
            IsStatic = false,
            IndexParameters = new List<ArgumentDecl>
            {
                new()
                {
                    Name = string.Empty,
                    PrivateName = "position",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = structDecl,
                    ModuleDecl = module,
                },
            },
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = subscriptGetter },
            },
            ParentDecl = structDecl,
            ModuleDecl = module,
        });

        return structDecl;
    }

    /// <summary>
    /// The "Collection with public array backing" shape — the projection delegates to the
    /// already-projected array rather than minting a native shim.
    /// </summary>
    private static StructDecl BuildArrayBackedStruct()
    {
        var module = CreateModule();
        var structDecl = CreateCollectionStruct("Bag", module);

        structDecl.Properties.Add(
            AttachProperty(
                TestDecls.Property(
                    "items",
                    new NamedTypeSpec("Swift.Array", new NamedTypeSpec("τ_0_0")),
                    module: ModuleName),
                structDecl,
                module));

        return structDecl;
    }

    /// <summary>
    /// Swaps the struct's single Collection-family conformance for another protocol, so a
    /// test can vary which family member the conformer actually publishes.
    /// </summary>
    private static void ReplaceConformance(StructDecl structDecl, string protocolQualifiedName)
    {
        structDecl.Conformances.Clear();
        structDecl.Conformances.Add(new TypeConformance(
            structDecl.SwiftTypeName,
            SwiftTypeName.FromModuleQualifiedName(protocolQualifiedName),
            "$sConformanceDescriptor"));
    }

    /// <summary>
    /// Parents a property and its synthesized accessor methods onto the owning struct.
    /// The emission validator marshals each accessor, which requires a parent declaration.
    /// </summary>
    private static PropertyDecl AttachProperty(PropertyDecl property, StructDecl owner, ModuleDecl module)
    {
        property.ParentDecl = owner;
        property.ModuleDecl = module;
        foreach (var accessor in property.Accessors)
        {
            accessor.Method.ParentDecl = owner;
            accessor.Method.ModuleDecl = module;
        }
        return property;
    }

    private static TypeDatabase BuildTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        typeDatabase.LoadModuleDatabaseFromFile(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "SwiftDatabase.xml")).Wait();
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase(ModuleName, "/fake/path"));
        return typeDatabase;
    }
}
