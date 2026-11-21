// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using DependencySendable = SwiftBindingsTestLibDependency.ISendable;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Existentials whose layout depends on which of their protocols are markers, a fact of each
/// protocol's declaration rather than its name (SwiftBindingsTestLib/Generics/MarkerIdentityExistentials.swift).
/// The dependency's own <c>Sendable</c> is an ordinary protocol, so <c>any Sendable</c> of it is the
/// 40-byte one-table container; this library's <c>@_marker</c> <c>LedgerTransferTag</c> adds no
/// witness table, so <c>any LedgerCoded &amp; LedgerLabelled &amp; LedgerTransferTag</c> is the
/// 48-byte two-table container. Every element of each array is read, and one goes back to Swift,
/// which reads it through its witness tables: an element read at the wrong stride, or a value
/// passed in the wrong container, answers with a wrong code or crashes.
/// </summary>
public class MarkerIdentityExistentialTests : TestBase
{
    public MarkerIdentityExistentialTests(TestResults results) : base(results) { }

    private static readonly System.Type[] Conformers =
        { typeof(LedgerCountOption), typeof(LedgerFlagOption), typeof(LedgerDurationOption) };

    // A call resolves to the generic member when its concrete overload is missing, so each
    // conformer's overload must be declared on its own, with the projected element type.
    private void AssertConcreteOverloads(string name, System.Type returnType)
    {
        foreach (var conformer in Conformers)
        {
            var overload = typeof(LedgerMarkerIdentityArrayBuilder).GetMethod(
                name, BindingFlags.Public | BindingFlags.Static, new[] { conformer });
            AssertNotNull(overload, $"{name}({conformer.Name}) is declared");
            AssertFalse(overload!.IsGenericMethodDefinition, $"{name}({conformer.Name}) is a concrete overload");
            AssertEqual(returnType, overload.ReturnType, $"{name}({conformer.Name}) returns the projected list");
        }
    }

    public void TestDependencySendableSpecializationsAreDeclared()
        => AssertConcreteOverloads(nameof(LedgerMarkerIdentityArrayBuilder.DependencySendables), typeof(IReadOnlyList<DependencySendable>));

    public void TestTaggedSpecializationsAreDeclared()
        => AssertConcreteOverloads(nameof(LedgerMarkerIdentityArrayBuilder.Tagged), typeof(IReadOnlyList<ILedgerCodedAndLedgerLabelled>));

    public void TestDependencySendableSpecializationReadsEveryElement()
    {
        using var option = new LedgerCountOption(5);
        var result = LedgerMarkerIdentityArrayBuilder.DependencySendables(option);
        AssertEqual(2, result.Count, "DependencySendables(count 5): two elements");
        AssertEqual(11005, result[0].DependencySendableCode, "element 0 answers through its witness table");
        AssertEqual(11008, result[1].DependencySendableCode, "element 1 is read at the one-table container's stride");
        AssertEqual(11008, TestLibFunctions.LedgerCodeOfDependencySendable(result[1]), "element 1 goes back to Swift as itself");
    }

    public void TestTaggedSpecializationReadsEveryElement()
    {
        using var option = new LedgerDurationOption(6.0);
        var result = LedgerMarkerIdentityArrayBuilder.Tagged(option);
        AssertEqual(2, result.Count, "Tagged(duration 6): two elements");
        AssertEqual(3006, TestLibFunctions.LedgerCodeOfTagged(result[0]), "element 0 answers through the first witness table");
        AssertEqual(3010, TestLibFunctions.LedgerCodeOfTagged(result[1]), "element 1 is read at the two-table container's stride");
        AssertEqual("entry-3006", TestLibFunctions.LedgerLabelOfTagged(result[0]), "element 0 answers through the second witness table");
        AssertEqual("entry-3010", TestLibFunctions.LedgerLabelOfTagged(result[1]), "element 1's second witness table");
    }

    public void TestDependencySendableArrayReadsEveryElement()
    {
        var result = TestLibFunctions.MakeDependencySendables(40);
        AssertEqual(3, result.Count, "three elements");
        for (var i = 0; i < result.Count; i++)
            AssertEqual(10_040 + i, result[i].DependencySendableCode, $"element {i} is read at the one-table container's stride");
        AssertEqual(10_042, TestLibFunctions.LedgerCodeOfDependencySendable(result[2]), "the last element goes back to Swift as itself");
        AssertEqual(30_123, TestLibFunctions.SumDependencySendableCodes(result), "the array goes back to Swift at Swift's stride");
    }

    public void TestTaggedArrayReadsEveryElement()
    {
        var result = TestLibFunctions.MakeTaggedLedgerEntries(70);
        AssertEqual(3, result.Count, "three elements");
        for (var i = 0; i < result.Count; i++)
        {
            AssertEqual(70 + i, TestLibFunctions.LedgerCodeOfTagged(result[i]), $"element {i}'s first witness table");
            AssertEqual($"entry-{70 + i}", TestLibFunctions.LedgerLabelOfTagged(result[i]), $"element {i}'s second witness table");
        }
        AssertEqual(213, TestLibFunctions.SumTaggedLedgerCodes(result), "the array goes back to Swift at Swift's stride");
    }

    public void TestDependencySendableValueRoundTrips()
    {
        var value = TestLibFunctions.MakeDependencySendable(9);
        AssertEqual(10_009, value.DependencySendableCode, "the value answers through its witness table");
        AssertEqual(10_009, TestLibFunctions.LedgerCodeOfDependencySendable(value), "the value goes back to Swift in the one-table container");
    }

    public void TestTaggedValueRoundTrips()
    {
        var value = TestLibFunctions.MakeTaggedLedgerEntry(12);
        AssertEqual(12, TestLibFunctions.LedgerCodeOfTagged(value), "the value goes back to Swift in the two-table container");
        AssertEqual("entry-12", TestLibFunctions.LedgerLabelOfTagged(value), "through its second witness table");
    }

    public void TestRepeatedReadsStayIntact()
    {
        var sendables = TestLibFunctions.MakeDependencySendables(1);
        var tagged = TestLibFunctions.MakeTaggedLedgerEntries(1);
        for (var i = 0; i < 100; i++)
        {
            AssertEqual(10_003, sendables[2].DependencySendableCode, $"read {i} of the last dependency-Sendable element");
            AssertEqual("entry-3", TestLibFunctions.LedgerLabelOfTagged(tagged[2]), $"read {i} of the last tagged element");
        }
    }
}
