// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using BindingsGeneration.Demangling;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Coverage for the two-phase cross-module fact-resolver seam: the order-sensitive
/// <see cref="LegacyCrossModuleFactResolver"/> (today's <c>_demangledTbd + _typeDatabase</c>
/// behavior) and the graph-wide <see cref="IndexBackedCrossModuleFactResolver"/> that recovers a
/// foreign fact from a preloaded <see cref="ModuleFactIndexSet"/> when the sequential order lost it.
///
/// Each fact kind (ownership, metadata accessor, conformance descriptor) is exercised in
/// REVERSE-SUPPLY order: the dependent module is "being parsed" while the dependency's facts are
/// NOT in the current module's TBD and NOT yet registered in the type database — the exact shape
/// that makes the legacy path throw / return empty ("metadata accessor not found", "protocol
/// conformance descriptor not found"). The legacy assertions are the RED baseline; the composite
/// assertions are the GREEN recovery the graph-wide index provides. Symbols round-trip by value —
/// the resolver only carries them, so the tests assert the fact survives, not any string shape.
/// </summary>
public class CrossModuleFactResolverTests
{
    // A dependency-owned nominal the dependent module references. Its facts live ONLY in the
    // dependency's TBD, which — in reverse-supply order — has not been folded into the current
    // module's demangled TBD nor registered in the running type database.
    private const string DependencyModule = "SwiftBindingsTestLibDependency";
    private const string ForeignTypeQualified = DependencyModule + ".MiniEntityProperty";
    private const string ForeignAccessorSymbol = "$s29SwiftBindingsTestLibDependency17MiniEntityPropertyVMa";

    private const string ProtocolQualified = DependencyModule + ".DependencyMarker";
    private const string ConformanceDescriptorSymbol =
        "$s29SwiftBindingsTestLibDependency17MiniEntityPropertyVAA0D6MarkerAAMc";

    // Deliberately-wrong symbols the index would return if the composite ever preferred it over a
    // legacy hit — used to prove the composite does NOT, so a preload cannot perturb a fact the
    // legacy path already resolves (the byte-identity contract).
    private const string ConflictingAccessorSymbol = "$sWRONG_ACCESSOR_DO_NOT_RETURNMa";
    private const string ConflictingConformanceSymbol = "$sWRONG_CONFORMANCE_DO_NOT_RETURNMc";

    // ---- Fact kind 1: nominal ownership (reverse-supply order) -------------------------------

    [Fact]
    public void Ownership_LegacyWithoutDependencyLoaded_TypeNotRegistered_Red()
    {
        // RED baseline: with nothing registered (the dependency was not finalized before this
        // parse), the order-sensitive legacy resolver cannot see the foreign type at all.
        var legacy = new LegacyCrossModuleFactResolver(new TypeDatabase(), EmptyTbd());
        Assert.False(legacy.IsTypeRegistered(ForeignType()));
    }

    [Fact]
    public void Ownership_IndexKnowsOwningModule_RegardlessOfSupplyOrder_Green()
    {
        // GREEN: the graph-wide index — built from the dependency's TBD up front — names the
        // owning module even though nothing has been registered yet.
        var index = IndexOfDependencyModule();
        Assert.True(index.TryGetOwningModule(ForeignType(), out var owningModule));
        Assert.Equal(DependencyModule, owningModule);
    }

    [Fact]
    public void Ownership_CompositeStillDelegatesRegistrationToLegacy_LayoutBoundaryHeld()
    {
        // Stage-2 boundary: ownership/registration feeds layout/frozenness decisions, so the
        // composite deliberately does NOT answer IsTypeRegistered from the index (that is a later
        // stage). It must mirror legacy exactly — false while the type DB is empty — so preloading
        // the index cannot silently flip a layout verdict.
        var composite = new IndexBackedCrossModuleFactResolver(
            IndexOfDependencyModule(),
            new LegacyCrossModuleFactResolver(new TypeDatabase(), EmptyTbd()));
        Assert.False(composite.IsTypeRegistered(ForeignType()));
    }

    // ---- Fact kind 2: metadata accessor (reverse-supply order) -------------------------------

    [Fact]
    public void MetadataAccessor_LegacyWithoutDependency_Throws_Red()
    {
        // RED baseline: the foreign accessor is absent from the current TBD and the type is not
        // registered, so the legacy terminal throws — the loss that kills the enclosing TypeDecl.
        var legacy = new LegacyCrossModuleFactResolver(new TypeDatabase(), EmptyTbd());
        Assert.False(legacy.TryGetMetadataAccessor(ForeignType(), out _));
        Assert.Throws<Exception>(() => legacy.GetMetadataAccessor(ForeignType()));
    }

    [Fact]
    public void MetadataAccessor_CompositeRecoversRealSymbolFromIndex_Green()
    {
        // GREEN: the composite recovers the dependency's REAL accessor symbol from the preloaded
        // index instead of throwing — order-independent, and a recovery (not a synthesized {mangled}Ma).
        var composite = new IndexBackedCrossModuleFactResolver(
            IndexOfDependencyModule(),
            new LegacyCrossModuleFactResolver(new TypeDatabase(), EmptyTbd()));
        Assert.Equal(ForeignAccessorSymbol, composite.GetMetadataAccessor(ForeignType()));
    }

    [Fact]
    public void MetadataAccessor_CompositeTryGetIsLegacyOnly_ConflictingIndexNeverOverrides()
    {
        // Byte-identity guard on the PRODUCTION entry: the parser resolves an accessor through
        // TryGetMetadataAccessor first (GetMetadataAccessor is the terminal reached only on a
        // TryGet miss). So even with an index that holds a DIFFERENT symbol for the same type, a
        // TryGet that the current TBD resolves must return the legacy symbol — the preload cannot
        // perturb an accessor the legacy path already resolves.
        var currentTbd = TbdWith(new IReduction[]
        {
            new MetadataAccessorReduction
            {
                Symbol = ForeignAccessorSymbol,
                TypeSpec = new NamedTypeSpec(ForeignTypeQualified),
            },
        });
        var conflictingIndex = new ModuleFactIndexSet(new[]
        {
            ModuleFactIndex.FromDemangledTbd(DependencyModule, TbdWith(new IReduction[]
            {
                new MetadataAccessorReduction
                {
                    Symbol = ConflictingAccessorSymbol,
                    TypeSpec = new NamedTypeSpec(ForeignTypeQualified),
                },
            })),
        });
        var composite = new IndexBackedCrossModuleFactResolver(
            conflictingIndex,
            new LegacyCrossModuleFactResolver(new TypeDatabase(), currentTbd));

        Assert.True(composite.TryGetMetadataAccessor(ForeignType(), out var symbol));
        Assert.Equal(ForeignAccessorSymbol, symbol);
    }

    [Fact]
    public void MetadataAccessor_CompositeThrowsWhenNoModuleInGraphOwnsIt()
    {
        // A genuinely missing input must still surface loudly: neither the current TBD nor any
        // indexed module owns the type, so the composite falls through to the legacy throw.
        var composite = new IndexBackedCrossModuleFactResolver(
            ModuleFactIndexSet.Empty,
            new LegacyCrossModuleFactResolver(new TypeDatabase(), EmptyTbd()));
        Assert.Throws<Exception>(() => composite.GetMetadataAccessor(ForeignType()));
    }

    // ---- Fact kind 3: protocol conformance descriptor (reverse-supply order) -----------------

    [Fact]
    public void Conformance_LegacyWithoutDependency_ReturnsFalse_Red()
    {
        // RED baseline: the descriptor lives in the dependency's TBD, absent here, so legacy
        // returns false and the caller emits an empty descriptor (a dropped witness).
        var legacy = new LegacyCrossModuleFactResolver(new TypeDatabase(), EmptyTbd());
        Assert.False(legacy.TryGetProtocolConformanceDescriptor(ForeignType(), ProtocolType(), out _));
    }

    [Fact]
    public void Conformance_CompositeRecoversDescriptorFromIndex_Green()
    {
        // GREEN: the composite recovers the descriptor from the preloaded graph-wide index.
        var composite = new IndexBackedCrossModuleFactResolver(
            IndexOfDependencyModule(),
            new LegacyCrossModuleFactResolver(new TypeDatabase(), EmptyTbd()));

        Assert.True(composite.TryGetProtocolConformanceDescriptor(ForeignType(), ProtocolType(), out var symbol));
        Assert.Equal(ConformanceDescriptorSymbol, symbol);
    }

    [Fact]
    public void Conformance_CompositeIsLegacyFirst_ConflictingIndexNeverOverrides()
    {
        // Byte-identity guard: the composite is legacy-first for conformance, so when the current
        // TBD resolves the descriptor it returns the legacy symbol even if the index holds a
        // DIFFERENT symbol for the same (implementing type, protocol) pair — the index is consulted
        // only on a legacy miss.
        var currentTbd = TbdWith(new IReduction[]
        {
            new ProtocolConformanceDescriptorReduction
            {
                Symbol = ConformanceDescriptorSymbol,
                ImplementingType = new NamedTypeSpec(ForeignTypeQualified),
                ProtocolType = new NamedTypeSpec(ProtocolQualified),
                Module = DependencyModule,
            },
        });
        var conflictingIndex = new ModuleFactIndexSet(new[]
        {
            ModuleFactIndex.FromDemangledTbd(DependencyModule, TbdWith(new IReduction[]
            {
                new ProtocolConformanceDescriptorReduction
                {
                    Symbol = ConflictingConformanceSymbol,
                    ImplementingType = new NamedTypeSpec(ForeignTypeQualified),
                    ProtocolType = new NamedTypeSpec(ProtocolQualified),
                    Module = DependencyModule,
                },
            })),
        });
        var composite = new IndexBackedCrossModuleFactResolver(
            conflictingIndex,
            new LegacyCrossModuleFactResolver(new TypeDatabase(), currentTbd));

        Assert.True(composite.TryGetProtocolConformanceDescriptor(ForeignType(), ProtocolType(), out var symbol));
        Assert.Equal(ConformanceDescriptorSymbol, symbol);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static SwiftTypeName ForeignType() => SwiftTypeName.FromModuleQualifiedName(ForeignTypeQualified);

    private static SwiftTypeName ProtocolType() => SwiftTypeName.FromModuleQualifiedName(ProtocolQualified);

    // The dependency module's index, built from ITS TBD — the "supplied up front regardless of
    // parse order" half of the two-phase design.
    private static ModuleFactIndexSet IndexOfDependencyModule()
    {
        var dependencyTbd = TbdWith(new IReduction[]
        {
            new MetadataAccessorReduction
            {
                Symbol = ForeignAccessorSymbol,
                TypeSpec = new NamedTypeSpec(ForeignTypeQualified),
            },
            new ProtocolConformanceDescriptorReduction
            {
                Symbol = ConformanceDescriptorSymbol,
                ImplementingType = new NamedTypeSpec(ForeignTypeQualified),
                ProtocolType = new NamedTypeSpec(ProtocolQualified),
                Module = DependencyModule,
            },
        });
        return new ModuleFactIndexSet(new[]
        {
            ModuleFactIndex.FromDemangledTbd(DependencyModule, dependencyTbd),
        });
    }

    private static DemanglingResults EmptyTbd() => TbdWith(Array.Empty<IReduction>());

    private static DemanglingResults TbdWith(IReduction[] reductions)
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)],
            modifiers: null)!;
        return (DemanglingResults)ctor.Invoke([reductions, null]);
    }
}
