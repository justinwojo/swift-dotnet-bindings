// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Regression coverage for the AppIntents 0.12.0 variadic generic parameter pack bug.
///
/// The fixture in <c>Generics/VariadicGenericPack.swift</c> declares
/// <c>public struct VariadicSpec&lt;Output, each R: VariadicMember&gt;</c>.
/// Pre-fix, the generator would render the pack member as the invalid C#
/// identifier <c>Teach R</c>, producing a CS1003/CS1525 cascade around the
/// malformed token. C# has no parameter-pack equivalent, so any type whose own
/// generic parameters include a variadic pack is unbindable. The fix detects
/// the pack via <c>GenericTypeEmitter.TryGetVariadicGenericParameter</c> in
/// each type handler (Class/FrozenStruct/NonFrozenStruct/EnumHandler) and
/// emits a skipped-type comment instead of attempting to render the malformed
/// identifier.
/// </summary>
public class VariadicGenericPackTests : TestBase
{
    public VariadicGenericPackTests(TestResults results) : base(results) { }

    public void TestVariadicMemberIsEmittedNormally()
    {
        // Non-variadic types declared in the same fixture file must still emit
        // normally — the gate is targeted at the variadic-pack type only, not
        // the whole compilation unit.
        var carrier = new VariadicCarrier(label: "alpha");
        AssertEqual("alpha", carrier.Label.ToString(), "VariadicCarrier.Label round-trips");
        TestLogger.Info("VariadicCarrier emitted and round-tripped normally.");
    }

    public void TestVariadicSpecIsNotEmittedAsType()
    {
        // Reflection over the binding's assembly: NO type whose full name
        // begins with `SwiftBindingsTestLib.VariadicSpec` may be present.
        // Targeting the literal `VariadicSpec`2` name was too narrow — a
        // regression that produced a different arity (`VariadicSpec`3`) or a
        // sanitized non-generic projection (`VariadicSpec`) would slip past.
        // Scanning all exported types catches every shape the gate is
        // supposed to suppress.
        var asm = typeof(VariadicCarrier).Assembly;

        var leakedVariadicTypes = asm.GetTypes()
            .Where(t => t.FullName != null &&
                        t.FullName.StartsWith("SwiftBindingsTestLib.VariadicSpec",
                            System.StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .ToArray();

        AssertTrue(leakedVariadicTypes.Length == 0,
            "VariadicSpec must NOT be emitted under any arity or sanitized form — " +
            "variadic generic parameter packs (`each R` / `repeat each R`) have no " +
            "C# equivalent and any binding would surface the malformed identifier " +
            "`Teach R`. Leaked types: " + string.Join(", ", leakedVariadicTypes));

        TestLogger.Info(
            "VariadicSpec correctly absent — variadic generic parameter pack " +
            "gated at the type-handler level.");
    }
}
