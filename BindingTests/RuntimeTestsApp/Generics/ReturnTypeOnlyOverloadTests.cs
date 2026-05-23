// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime coverage for AppIntents 0.12.0 site #2 (doc 14 carry-out):
/// return-type-only overload disambiguation. The Swift fixture in
/// <c>ReturnTypeOnlyOverload.swift</c> exposes two <c>selectExpression(_:)</c>
/// overloads with identical parameter types but different return types
/// (one <c>VariadicSection</c>, one <c>[VariadicSection]</c>). Without the
/// function-reference <c>as</c> cast emitted by
/// <c>MethodWrapperEmitter.HasReturnTypeOnlyOverloadSibling</c>, the disfavored
/// wrapper fails to compile because Swift's overload resolution at the call
/// site picks the array-returning overload, mismatching
/// <c>initializeMemory(as: VariadicSection.self, ...)</c>.
/// <para>The C# emit layer dedupes one overload (same-name same-params is a
/// C# signature collision); this test validates that whichever overload
/// survives dedup invokes through its wrapper and returns the right runtime
/// value. The durable gate is the wrapper-compile step — if the cast were
/// removed, this fixture's Swift build would fail.</para>
/// </summary>
public class ReturnTypeOnlyOverloadTests : TestBase
{
    public ReturnTypeOnlyOverloadTests(TestResults results) : base(results) { }

    public void TestSelectExpression_DispatchesThroughDisambiguatingCast()
    {
        using var section = new VariadicSection("disambiguator");

        var result = ReturnTypeOnlyOverloadHost.SelectExpression(section);

        AssertNotNull(result, "SelectExpression returns a non-null projection");
    }
}
