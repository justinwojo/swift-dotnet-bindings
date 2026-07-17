// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Bug (f) round-trip: mirrors Time's real <c>ClockStrikes&lt;U&gt;</c> /
/// <c>ClockStrikes&lt;U&gt;.Values</c> shape — a generic struct's OWN extension declares a
/// computed property whose return type is the host's OWN nested type, parameterized by
/// the host's OWN generic parameter. The concrete-specialization engine substitutes the
/// generic parameter throughout the synthesized concrete signature via
/// <c>SubstituteTypeSpec</c>; pre-fix, that substitution didn't recurse into
/// <c>NamedTypeSpec.InnerType</c>, so the nested-type reference collapsed to the bare
/// outer generic and the synthesized wrapper referenced a non-existent flattened type.
/// Exercises BOTH conformers (Seconds/Minutes) to prove the per-conformer substitution
/// loop, not just a single specialization.
/// </summary>
public class NestedGenericPropertyReturnTests : TestBase
{
    public NestedGenericPropertyReturnTests(TestResults results) : base(results) { }

    public void TestValues_SecondsConformer_ReturnsNestedTypeParameterizedByHostGeneric()
    {
        using var host = TestLibFunctions.MakeNestedReturnHostSeconds(5);
        using var values = host.Values();
        AssertEqual(5, values.Count, "NestedReturnHost<Seconds>.values.count must round-trip the seed");
    }

    public void TestValues_MinutesConformer_ReturnsNestedTypeParameterizedByHostGeneric()
    {
        using var host = TestLibFunctions.MakeNestedReturnHostMinutes(11);
        using var values = host.Values();
        AssertEqual(11, values.Count, "NestedReturnHost<Minutes>.values.count must round-trip the seed for the SECOND conformer too");
    }
}
