// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// The per-conformer specializations of a static <c>some P</c> → <c>[any P]</c> builder method
/// (<c>LedgerOptionsBuilder.buildFinalResult</c>, the shape of a result builder's
/// <c>buildFinalResult</c>). Each specialization is its own recovery unit, standing whether or not
/// the generic member does, and each is called here through its own wrapper with a resilient
/// conformer: the returned array, projected as <c>IReadOnlyList&lt;ILedgerOption&gt;</c> like the
/// generic member's, must hold one existential that answers with the value the conformer was
/// built from.
/// </summary>
public class OpaqueOptionBuilderTests : TestBase
{
    public OpaqueOptionBuilderTests(TestResults results) : base(results) { }

    private void AssertSingleOption(IReadOnlyList<ILedgerOption> result, int expectedCode, string label)
    {
        AssertEqual(1, result.Count, $"{label}: buildFinalResult returns the one component");
        AssertEqual(expectedCode, result[0].OptionCode, $"{label}: the element is the component it was given");
    }

    // A call resolves to the generic member when its concrete overload is missing, and the generic
    // member returns the same shape, so the value checks alone would pass without any
    // specialization. Each conformer's overload must be declared on its own.
    public void TestEachConformerHasItsOwnConcreteOverload()
    {
        foreach (var conformer in new[] { typeof(LedgerCountOption), typeof(LedgerFlagOption), typeof(LedgerDurationOption) })
        {
            var overload = typeof(LedgerOptionsBuilder).GetMethod(
                nameof(LedgerOptionsBuilder.BuildFinalResult), BindingFlags.Public | BindingFlags.Static, new[] { conformer });
            AssertNotNull(overload, $"BuildFinalResult({conformer.Name}) is declared");
            AssertFalse(overload!.IsGenericMethodDefinition, $"BuildFinalResult({conformer.Name}) is a concrete overload");
            AssertEqual(typeof(IReadOnlyList<ILedgerOption>), overload.ReturnType, $"BuildFinalResult({conformer.Name}) returns the projected list");
        }
    }

    public void TestCountOptionSpecialization_ReturnsItsComponent()
    {
        using var option = new LedgerCountOption(7);
        var result = LedgerOptionsBuilder.BuildFinalResult(option);
        AssertSingleOption(result, 1007, "LedgerCountOption");
    }

    public void TestFlagOptionSpecialization_ReturnsItsComponent()
    {
        using var on = new LedgerFlagOption(true);
        using var off = new LedgerFlagOption(false);
        var onResult = LedgerOptionsBuilder.BuildFinalResult(on);
        var offResult = LedgerOptionsBuilder.BuildFinalResult(off);
        AssertSingleOption(onResult, 2001, "LedgerFlagOption(true)");
        AssertSingleOption(offResult, 2000, "LedgerFlagOption(false)");
    }

    public void TestDurationOptionSpecialization_ReturnsItsComponent()
    {
        using var option = new LedgerDurationOption(42.0);
        var result = LedgerOptionsBuilder.BuildFinalResult(option);
        AssertSingleOption(result, 3042, "LedgerDurationOption");
    }

    public void TestSpecializations_RepeatedCallsStayIndependent()
    {
        for (int i = 0; i < 50; i++)
        {
            using var option = new LedgerCountOption(i);
            var result = LedgerOptionsBuilder.BuildFinalResult(option);
            AssertSingleOption(result, 1000 + i, $"LedgerCountOption({i})");
        }
    }
}
