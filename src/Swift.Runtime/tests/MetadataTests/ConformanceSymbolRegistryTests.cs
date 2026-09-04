// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A payload-less Swift raw-value enum projects to a plain C# enum, which can never
/// implement <see cref="ISwiftObject"/>. These tests cover the symbol-keyed conformance
/// lane that lets such a type still resolve a protocol witness table.
///
/// Each test uses its own enum type so the process-global registry cannot leak state
/// between them.
/// </summary>
public class ConformanceSymbolRegistryTests
{
    private const string SwiftCore = "/usr/lib/swift/libswiftCore.dylib";

    // Swift.Int : Hashable / Equatable. The projected enums below are Int-width and
    // Int-layout, so pairing these descriptors with Int metadata yields a witness table
    // that is genuinely usable, not merely non-null.
    private const string IntHashableSymbol = "$sSiSHsMc";
    private const string IntEquatableSymbol = "$sSiSQsMc";

    private enum RegisteredHashableKind : long { First = 0, Second = 1 }

    private enum UnregisteredKind : long { First = 0, Second = 1 }

    private enum BogusSymbolKind : long { First = 0 }

    private enum EmptyLocationKind : long { First = 0 }

    private enum WitnessTableKind : long { First = 0, Second = 1 }

    private enum EquatableKind : long { First = 0 }

    private enum ReRegisteredKind : long { First = 0 }

    private enum ReRegisteredOverSuccessKind : long { First = 0 }

    private enum SupersededKind : long { First = 0 }

    private enum LivePublishKind : long { First = 0 }

    private enum RacedReRegistrationKind : long { First = 0 }

    private enum UnracedResolveKind : long { First = 0 }

    private const string MissingSymbol = "$sThisSymbolDoesNotExistMc";

    [Fact]
    public void RegisteredSymbol_ResolvesConformanceForCSharpEnum()
    {
        SwiftMarshal.RegisterConformanceSymbol(
            typeof(RegisteredHashableKind), typeof(ISwiftHashable), SwiftCore, IntHashableSymbol);

        Assert.True(ProtocolConformanceDescriptor.TryGet<RegisteredHashableKind, ISwiftHashable>(out var descriptor));
        Assert.NotNull(descriptor);
        Assert.True(descriptor!.Value.IsValid);
    }

    [Fact]
    public void UnregisteredEnum_ReportsNoConformance()
    {
        // The pre-existing behaviour for a type with no declared conformance is preserved:
        // the lookup reports failure rather than throwing.
        Assert.False(ProtocolConformanceDescriptor.TryGet<UnregisteredKind, ISwiftHashable>(out var descriptor));
        Assert.Null(descriptor);
    }

    [Fact]
    public void RegisteredSymbolThatDoesNotExist_DegradesToNoConformance()
    {
        // A symbol that cannot be loaded must degrade to the same "no conformance" answer
        // an unregistered type gets, not leak the load failure to the caller.
        SwiftMarshal.RegisterConformanceSymbol(
            typeof(BogusSymbolKind), typeof(ISwiftHashable), SwiftCore, "$sThisSymbolDoesNotExistMc");

        Assert.False(ProtocolConformanceDescriptor.TryGet<BogusSymbolKind, ISwiftHashable>(out var descriptor));
        Assert.Null(descriptor);
    }

    [Theory]
    [InlineData("", IntHashableSymbol)]
    [InlineData(SwiftCore, "")]
    [InlineData(null, IntHashableSymbol)]
    [InlineData(SwiftCore, null)]
    public void IncompleteLocation_IsNotRegistered(string? library, string? symbol)
    {
        // The generator emits these arguments from parser-provided facts; a missing one
        // means the conformance was never actually located and must not be recorded.
        SwiftMarshal.RegisterConformanceSymbol(
            typeof(EmptyLocationKind), typeof(ISwiftHashable), library!, symbol!);

        Assert.False(ProtocolConformanceDescriptor.TryGet<EmptyLocationKind, ISwiftHashable>(out _));
    }

    [Fact]
    public void RegisteredEnum_ResolvesAUsableWitnessTable()
    {
        // The full shape a generated [ModuleInitializer] sets up for a simple enum:
        // metadata plus the conformance-descriptor symbol. Together they must produce a
        // witness table — this is the lookup SwiftSet/SwiftDictionary perform per element.
        Assert.True(TypeMetadata.TryGetTypeMetadata<nint>(out var intMetadata));
        TypeMetadata.RegisterMetadata(typeof(WitnessTableKind), intMetadata!.Value);
        SwiftMarshal.RegisterConformanceSymbol(
            typeof(WitnessTableKind), typeof(ISwiftHashable), SwiftCore, IntHashableSymbol);

        var witnessTable = ProtocolWitnessTable.GetOrThrow<WitnessTableKind, ISwiftHashable>();
        Assert.True(witnessTable.IsValid);
    }

    [Fact]
    public void EquatableLane_ResolvesAlongsideHashable()
    {
        SwiftMarshal.RegisterConformanceSymbol(
            typeof(EquatableKind), typeof(ISwiftEquatable), SwiftCore, IntEquatableSymbol);

        Assert.True(ProtocolConformanceDescriptor.TryGet<EquatableKind, ISwiftEquatable>(out var equatable));
        Assert.True(equatable!.Value.IsValid);

        // Registration is per (type, protocol) pair: declaring Equatable must not imply Hashable.
        Assert.False(ProtocolConformanceDescriptor.TryGet<EquatableKind, ISwiftHashable>(out _));
    }

    [Fact]
    public void ReRegistration_SupersedesACachedFailure()
    {
        // Registration is last-one-wins, so a resolve that already cached a load failure must not
        // shadow a later, correct registration for the same pair.
        SwiftMarshal.RegisterConformanceSymbol(
            typeof(ReRegisteredKind), typeof(ISwiftHashable), SwiftCore, MissingSymbol);
        Assert.False(ProtocolConformanceDescriptor.TryGet<ReRegisteredKind, ISwiftHashable>(out _));

        SwiftMarshal.RegisterConformanceSymbol(
            typeof(ReRegisteredKind), typeof(ISwiftHashable), SwiftCore, IntHashableSymbol);

        Assert.True(ProtocolConformanceDescriptor.TryGet<ReRegisteredKind, ISwiftHashable>(out var descriptor));
        Assert.True(descriptor!.Value.IsValid);
    }

    [Fact]
    public void ReRegistration_SupersedesACachedSuccess()
    {
        // The same rule in the other direction: a cached successful resolution belongs to the
        // registration that produced it, not to the pair, so it cannot answer for a later one.
        SwiftMarshal.RegisterConformanceSymbol(
            typeof(ReRegisteredOverSuccessKind), typeof(ISwiftHashable), SwiftCore, IntHashableSymbol);
        Assert.True(ProtocolConformanceDescriptor.TryGet<ReRegisteredOverSuccessKind, ISwiftHashable>(out _));

        SwiftMarshal.RegisterConformanceSymbol(
            typeof(ReRegisteredOverSuccessKind), typeof(ISwiftHashable), SwiftCore, MissingSymbol);

        Assert.False(ProtocolConformanceDescriptor.TryGet<ReRegisteredOverSuccessKind, ISwiftHashable>(out _));
    }

    [Fact]
    public void ResolutionAgainstASupersededDeclaration_IsNotPublished()
    {
        // Drives the interleaving a concurrent resolve produces, without depending on thread
        // timing: a resolve captures the declaration in force, a Register replaces it, and only
        // then does the in-flight resolve try to publish what it computed. That answer describes
        // a registration that no longer exists and must be dropped, or it would win over the
        // newer registration until something registered the pair again.
        SwiftMarshal.RegisterConformanceSymbol(
            typeof(SupersededKind), typeof(ISwiftHashable), SwiftCore, MissingSymbol);

        var captured = ConformanceSymbolRegistry.PeekDeclaration(typeof(SupersededKind), typeof(ISwiftHashable));
        Assert.NotNull(captured);

        SwiftMarshal.RegisterConformanceSymbol(
            typeof(SupersededKind), typeof(ISwiftHashable), SwiftCore, IntHashableSymbol);

        Assert.False(ConformanceSymbolRegistry.PublishResolution(
            typeof(SupersededKind), typeof(ISwiftHashable), captured!, ProtocolConformanceDescriptor.Zero));

        // The live registration's own resolution is what callers get.
        Assert.True(ProtocolConformanceDescriptor.TryGet<SupersededKind, ISwiftHashable>(out var descriptor));
        Assert.True(descriptor!.Value.IsValid);
    }

    [Fact]
    public void ResolutionAgainstTheLiveDeclaration_IsPublished()
    {
        // Positive control for the supersession check above: with no intervening Register the
        // same publish path caches the answer, so the check cannot pass by never publishing.
        SwiftMarshal.RegisterConformanceSymbol(
            typeof(LivePublishKind), typeof(ISwiftHashable), SwiftCore, IntHashableSymbol);

        var captured = ConformanceSymbolRegistry.PeekDeclaration(typeof(LivePublishKind), typeof(ISwiftHashable));
        Assert.NotNull(captured);

        Assert.True(ConformanceSymbolRegistry.PublishResolution(
            typeof(LivePublishKind), typeof(ISwiftHashable), captured!, ProtocolConformanceDescriptor.Zero));
    }

    [Fact]
    public void ResolveRacedByAReRegistration_ReturnsTheNewRegistrationsDescriptor()
    {
        // The in-flight caller, not just the cache. A resolve computes against the declaration it
        // captured; a Register replaces that declaration before the resolve publishes. Dropping the
        // computed value from the CACHE is not enough — handing it to this caller lets an obsolete
        // descriptor be cached elsewhere (witness-table caches keep what they are given), so the
        // newer registration never takes effect for it. The resolve must re-read and answer from
        // the registration in force.
        SwiftMarshal.RegisterConformanceSymbol(
            typeof(RacedReRegistrationKind), typeof(ISwiftHashable), SwiftCore, MissingSymbol);

        // Filter on the exact declaration this test registered, so a resolve from any test running
        // in parallel cannot trip the hook — and so the hook fires once, for the first attempt only.
        var captured = ConformanceSymbolRegistry.PeekDeclaration(
            typeof(RacedReRegistrationKind), typeof(ISwiftHashable));
        Assert.NotNull(captured);

        var superseded = false;
        ConformanceSymbolRegistry.ResolvedBeforePublish = declaration =>
        {
            if (!ReferenceEquals(declaration, captured))
                return;
            superseded = true;
            // Lands exactly in the window between "descriptor computed" and "descriptor published".
            SwiftMarshal.RegisterConformanceSymbol(
                typeof(RacedReRegistrationKind), typeof(ISwiftHashable), SwiftCore, IntHashableSymbol);
        };

        try
        {
            Assert.True(ProtocolConformanceDescriptor.TryGet<RacedReRegistrationKind, ISwiftHashable>(out var descriptor));
            Assert.True(descriptor!.Value.IsValid);
        }
        finally
        {
            ConformanceSymbolRegistry.ResolvedBeforePublish = null;
        }

        Assert.True(superseded, "the re-registration must have landed inside the resolve window");

        // And the retry's answer is the one cached, so the next caller takes the fast path to it.
        Assert.True(ProtocolConformanceDescriptor.TryGet<RacedReRegistrationKind, ISwiftHashable>(out var cached));
        Assert.True(cached!.Value.IsValid);
    }

    [Fact]
    public void ResolveNotRaced_PublishesOnTheFirstAttempt()
    {
        // Positive control for the retry above: with no re-registration the observed declaration
        // is still the one in force, so exactly one resolution is computed and the loop does not
        // spin — the retry cannot be passing merely because every resolve runs twice.
        SwiftMarshal.RegisterConformanceSymbol(
            typeof(UnracedResolveKind), typeof(ISwiftHashable), SwiftCore, IntHashableSymbol);

        var captured = ConformanceSymbolRegistry.PeekDeclaration(
            typeof(UnracedResolveKind), typeof(ISwiftHashable));
        Assert.NotNull(captured);

        var observed = 0;
        ConformanceSymbolRegistry.ResolvedBeforePublish = declaration =>
        {
            if (ReferenceEquals(declaration, captured))
                observed++;
        };
        try
        {
            Assert.True(ProtocolConformanceDescriptor.TryGet<UnracedResolveKind, ISwiftHashable>(out var descriptor));
            Assert.True(descriptor!.Value.IsValid);
        }
        finally
        {
            ConformanceSymbolRegistry.ResolvedBeforePublish = null;
        }

        Assert.Equal(1, observed);
    }

    [Fact]
    public void ISwiftObjectLane_StillResolvesWhenNothingIsRegistered()
    {
        // Regression guard for the symbol-lane consult added ahead of the ISwiftObject
        // branch: a type that carries its own conformance descriptor must be unaffected.
        Assert.True(ProtocolConformanceDescriptor.TryGet<SwiftIntMock, ISwiftHashable>(out var descriptor));
        Assert.True(descriptor!.Value.IsValid);
    }
}
