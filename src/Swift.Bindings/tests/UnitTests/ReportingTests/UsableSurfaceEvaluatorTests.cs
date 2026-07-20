// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the D-R6 usable-surface predicate: a degenerate binding that still exposes something callable
/// ships; one exposing nothing usable fails closed. Both arms (usable member OR non-tombstone type) and
/// the tombstone-netting are exercised, since a tombstone-only type-set reads as usable if not netted out.
/// </summary>
public class UsableSurfaceEvaluatorTests
{
    private static BindingReport Report(int emittedTypes, int emittedMembers) => new()
    {
        ModuleName = "Test",
        EmittedTypes = emittedTypes,
        EmittedMembers = emittedMembers,
    };

    [Fact]
    public void Evaluate_UsableMembers_HasUsableSurface()
    {
        var result = UsableSurfaceEvaluator.Evaluate(Report(emittedTypes: 0, emittedMembers: 3), silentTombstoneCount: 0);
        Assert.True(result.HasUsableSurface);
    }

    [Fact]
    public void Evaluate_ValueTypeOnly_NoMembers_HasUsableSurface()
    {
        // A value-type-only binding legitimately emits types with zero method-level members recorded —
        // the non-tombstone-type arm must keep it usable (OR, not AND).
        var result = UsableSurfaceEvaluator.Evaluate(Report(emittedTypes: 2, emittedMembers: 0), silentTombstoneCount: 0);
        Assert.True(result.HasUsableSurface);
    }

    [Fact]
    public void Evaluate_FreeFunctionsOnly_NoTypes_HasUsableSurface()
    {
        // Direct-native free functions: members but no types. Still usable.
        var result = UsableSurfaceEvaluator.Evaluate(Report(emittedTypes: 0, emittedMembers: 4), silentTombstoneCount: 0);
        Assert.True(result.HasUsableSurface);
    }

    [Fact]
    public void Evaluate_AllTypesAreTombstones_NoMembers_FailsClosed()
    {
        // Every emitted type degraded to an opaque tombstone (zero usable members) and nothing else was
        // emitted → nothing callable → fail closed.
        var result = UsableSurfaceEvaluator.Evaluate(Report(emittedTypes: 3, emittedMembers: 0), silentTombstoneCount: 3);
        Assert.False(result.HasUsableSurface);
        Assert.Contains("tombstone", result.Reason);
    }

    [Fact]
    public void Evaluate_SomeTombstonesButANonTombstoneTypeRemains_HasUsableSurface()
    {
        // 3 emitted types, 2 tombstones → 1 real type remains → usable.
        var result = UsableSurfaceEvaluator.Evaluate(Report(emittedTypes: 3, emittedMembers: 0), silentTombstoneCount: 2);
        Assert.True(result.HasUsableSurface);
    }

    [Fact]
    public void Evaluate_NothingEmitted_FailsClosed()
    {
        var result = UsableSurfaceEvaluator.Evaluate(Report(emittedTypes: 0, emittedMembers: 0), silentTombstoneCount: 0);
        Assert.False(result.HasUsableSurface);
        Assert.Contains("no types or members", result.Reason);
    }

    [Fact]
    public void Evaluate_MembersPresentEvenWhenAllTypesTombstones_HasUsableSurface()
    {
        // A tombstone type can coexist with usable free-function members; the member arm keeps it usable.
        var result = UsableSurfaceEvaluator.Evaluate(Report(emittedTypes: 1, emittedMembers: 2), silentTombstoneCount: 1);
        Assert.True(result.HasUsableSurface);
    }
}
