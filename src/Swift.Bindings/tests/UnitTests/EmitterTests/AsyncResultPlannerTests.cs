// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="AsyncResultPlanner"/> — the single source of truth for the async-result
/// carrier-ownership decision (<see cref="AsyncResultPlan"/>). This algebra previously lived inlined
/// and duplicated in <see cref="AsyncHarnessEmitter"/> and <see cref="AsyncMethodGenericBridgeEmitter"/>,
/// where the two copies could drift silently. The tests assert the ownership contract directly so a
/// future edit that breaks the carrier <c>+1</c> release (a leak) or over-destroys a callback-owned
/// carrier (a use-after-free) fails here rather than at runtime.
/// </summary>
public class AsyncResultPlannerTests
{
    // (Kind, Flags) -> (CallbackTakesOwnership, CarrierNeedsDestroy).
    [Theory]
    // Non-frozen struct: callback adopts the value (SafeHandle), so the carrier is NOT destroyed.
    [InlineData(TypeRecordKind.Struct, TypeRecordFlags.None, true, true)]
    // Frozen blittable struct: trivial value witness — no +1, nothing to release or adopt.
    [InlineData(TypeRecordKind.Struct, TypeRecordFlags.Frozen, false, false)]
    // Frozen struct projected as class (has ref-type fields): not callback-owned, but the carrier's
    // internal +1 must be value-witness-destroyed.
    [InlineData(TypeRecordKind.Struct, TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement, false, true)]
    // Complex (non-simple, payload-carrying) enum: callback-owned.
    [InlineData(TypeRecordKind.Enum, TypeRecordFlags.None, true, true)]
    // Simple (payload-free) enum: trivial, nothing to release.
    [InlineData(TypeRecordKind.Enum, TypeRecordFlags.SimpleEnum, false, false)]
    // Class: ref-counted on a separate path; the ownership algebra leaves both false.
    [InlineData(TypeRecordKind.Class, TypeRecordFlags.RequiresMemoryManagement, false, false)]
    public void ClassifyCarrierOwnership_MatchesOwnershipAlgebra(
        TypeRecordKind kind, TypeRecordFlags flags, bool expectedCallbackOwns, bool expectedNeedsDestroy)
    {
        var record = MakeRecord("TestModule.Result", kind, flags);

        var plan = AsyncResultPlanner.ClassifyCarrierOwnership(record);

        Assert.Equal(expectedCallbackOwns, plan.CallbackTakesOwnership);
        Assert.Equal(expectedNeedsDestroy, plan.CarrierNeedsDestroy);
    }

    [Fact]
    public void ClassifyCarrierOwnership_CallbackOwnedAlwaysNeedsDestroy()
    {
        // Invariant: a callback-owned carrier always needs destroy (CarrierNeedsDestroy is the
        // superset). Guards against an edit that decouples the two for an owned type.
        foreach (var (kind, flags) in new[]
        {
            (TypeRecordKind.Struct, TypeRecordFlags.None),
            (TypeRecordKind.Enum, TypeRecordFlags.None),
        })
        {
            var plan = AsyncResultPlanner.ClassifyCarrierOwnership(MakeRecord("TestModule.Owned", kind, flags));
            Assert.True(plan.CallbackTakesOwnership);
            Assert.True(plan.CarrierNeedsDestroy);
        }
    }

    [Fact]
    public void WidenDestroyForOptionalPayload_NonOptional_ReturnsFalse()
    {
        var db = CreateTypeDatabase();
        // A bare value type is not Optional — the widening never fires.
        var spec = new NamedTypeSpec("TestModule.NonFrozen");

        Assert.False(AsyncResultPlanner.WidenDestroyForOptionalPayload(spec, db));
    }

    [Theory]
    [InlineData("TestModule.NonFrozen", true)]   // Optional<non-frozen struct> -> destroy
    [InlineData("TestModule.FrozenAsClass", true)] // Optional<frozen-as-class struct> -> destroy
    [InlineData("TestModule.ComplexEnum", true)] // Optional<complex enum> -> destroy
    [InlineData("TestModule.SimpleEnum", false)] // Optional<simple enum> -> trivial, no destroy
    [InlineData("TestModule.FrozenBlittable", false)] // Optional<frozen blittable struct> -> trivial
    [InlineData("TestModule.RefClass", false)]   // Optional<class> -> reference path, no carrier destroy
    public void WidenDestroyForOptionalPayload_WidensOnNonTrivialInner(string innerName, bool expected)
    {
        var db = CreateTypeDatabase();
        var spec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(innerName));

        Assert.Equal(expected, AsyncResultPlanner.WidenDestroyForOptionalPayload(spec, db));
    }

    [Fact]
    public void WidenDestroyForOptionalPayload_UnregisteredInner_ReturnsFalse()
    {
        var db = CreateTypeDatabase();
        // Inner type not in the database — cannot prove a non-trivial witness, so don't widen.
        var spec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("TestModule.Unknown"));

        Assert.False(AsyncResultPlanner.WidenDestroyForOptionalPayload(spec, db));
    }

    private static TypeRecord MakeRecord(string swiftName, TypeRecordKind kind, TypeRecordFlags flags)
        => new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", swiftName.Split('.')[^1]),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftName),
            MetadataAccessor = "$sTestMa",
            Flags = flags,
            Kind = kind,
        };

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        void Register(string name, TypeRecordKind kind, TypeRecordFlags flags)
            => testModule.RegisterType(SwiftTypeName.FromModuleQualifiedName(name), MakeRecord(name, kind, flags));

        Register("TestModule.NonFrozen", TypeRecordKind.Struct, TypeRecordFlags.None);
        Register("TestModule.FrozenBlittable", TypeRecordKind.Struct, TypeRecordFlags.Frozen);
        Register("TestModule.FrozenAsClass", TypeRecordKind.Struct, TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement);
        Register("TestModule.ComplexEnum", TypeRecordKind.Enum, TypeRecordFlags.None);
        Register("TestModule.SimpleEnum", TypeRecordKind.Enum, TypeRecordFlags.SimpleEnum);
        Register("TestModule.RefClass", TypeRecordKind.Class, TypeRecordFlags.RequiresMemoryManagement);

        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }
}
