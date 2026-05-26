// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Regression coverage for generic types whose single generic parameter is
/// constrained by a Swift stdlib protocol that was absent from
/// SwiftDatabase.xml (Equatable / Decodable / Encodable). Without the XML
/// entries the metadata-accessor PWT slot could not be resolved and the
/// enclosing type silently tombstoned — same shape as WeatherKit's
/// <c>Forecast&lt;TElement&gt;</c>. These tests assert that the type emits,
/// can be instantiated via a concrete-T factory, and releases cleanly.
/// </summary>
public class StdlibProtocolConstraintTests : TestBase
{
    public StdlibProtocolConstraintTests(TestResults results) : base(results) { }

    public void TestEquatableContainer_ConcreteFactoryRoundTrips()
    {
        using var container = Functions.MakeEquatableContainer(42);
        AssertTrue(container is not null, "Functions.MakeEquatableContainer should return a live instance");
        AssertTrue(
            container!.Payload.DangerousGetHandle() != IntPtr.Zero,
            "EquatableContainer payload must be a non-null Swift handle");
    }

    public void TestCodableContainer_ConcreteFactoryRoundTrips()
    {
        using var container = Functions.MakeCodableContainer(42);
        AssertTrue(container is not null, "Functions.MakeCodableContainer should return a live instance");
        AssertTrue(
            container!.Payload.DangerousGetHandle() != IntPtr.Zero,
            "CodableContainer payload must be a non-null Swift handle");
    }

    // Phase A ISwiftObject seed-drop gate. `EquatableContainer<T: Equatable>` is a
    // generic constrained by a Self-requirement (descriptor-path-safe) protocol. A
    // Swift STRUCT conformer projects to a C# type implementing `ISwiftObject`, so it
    // satisfies the historical seed regardless — the only conformer that actually
    // exercises the seed being dropped is a PRIMITIVE. `Int` is already `Equatable`
    // and projects to `nint`, which does NOT implement `ISwiftObject`; before the seed
    // drop `EquatableContainer<nint>` failed to type-check (CS0315). Constructing it via
    // the factory and reading `Item` back is the durable end-to-end proof the drop
    // produces a usable binding.
    public void TestEquatableContainer_PrimitiveElement_SeedDropRoundTrips()
    {
        using var container = Functions.MakeIntEquatableContainer(42);
        AssertTrue(container is not null, "Functions.MakeIntEquatableContainer should return a live instance");
        AssertEqual(
            42L,
            (long)container!.Item,
            "EquatableContainer<nint>.Item must read back the stored primitive value");
    }

    // Phase 1 Codable JSON round-trip: synthesized Codable conformance now
    // surfaces JSON encode/decode helpers via Foundation's concrete encoder/decoder.
    // EquatableTicket is `struct ... : Equatable, Codable` — exactly the non-generic
    // frozen-struct shape the Phase 1 emitter targets.
    public void TestEquatableTicket_EncodeToJson_ProducesNonEmptyBytes()
    {
        using var ticket = new EquatableTicket(7);
        var json = ticket.EncodeToJson();
        AssertTrue(json is not null, "EncodeToJson must return a non-null byte array");
        AssertTrue(json!.Length > 0, "EncodeToJson must produce non-empty JSON for a populated value");
        var text = System.Text.Encoding.UTF8.GetString(json);
        AssertTrue(text.Contains("\"id\""), $"Encoded JSON must contain the 'id' key (got '{text}')");
        AssertTrue(text.Contains("7"), $"Encoded JSON must contain the integer value (got '{text}')");
    }

    public void TestEquatableTicket_RoundTripJson_PreservesValue()
    {
        using var original = new EquatableTicket(42);
        var json = original.EncodeToJson();
        using var decoded = EquatableTicket.DecodeFromJson(json);
        AssertEqual(42, decoded.Id, "Decoded id must equal the original id (42)");
        AssertTrue(original == decoded, "Round-tripped EquatableTicket must compare equal to the original");
    }

    public void TestEquatableTicket_DecodeFromJson_RejectsMalformed()
    {
        var bad = System.Text.Encoding.UTF8.GetBytes("{not valid json");
        bool threw = false;
        try
        {
            using var _ = EquatableTicket.DecodeFromJson(bad);
        }
        catch (System.InvalidOperationException)
        {
            threw = true;
        }
        AssertTrue(threw, "DecodeFromJson must throw InvalidOperationException on malformed JSON");
    }

    public void TestEquatableTicket_DecodeFromJson_NullArgumentThrows()
    {
        bool threw = false;
        try
        {
            using var _ = EquatableTicket.DecodeFromJson(null!);
        }
        catch (System.ArgumentNullException)
        {
            threw = true;
        }
        AssertTrue(threw, "DecodeFromJson must throw ArgumentNullException for null input");
    }
}
