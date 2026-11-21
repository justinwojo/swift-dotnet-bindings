// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Per-conformer specializations of static <c>some LedgerOption</c> builders whose results are
/// arrays of existentials other than a single Swift protocol
/// (<c>LedgerExistentialArrayBuilder</c>): a two-protocol composition, read through the
/// composition's container and proxy; <c>[Any]</c> and <c>[any Sendable]</c>, read through the
/// zero-witness container and unboxed; and an @objc protocol, which the generator refuses rather
/// than reading through a Swift container. A result read at the wrong stride or through the wrong
/// carrier comes back as a wrong value or a crash.
/// </summary>
public class ExistentialArraySpecializationTests : TestBase
{
    public ExistentialArraySpecializationTests(TestResults results) : base(results) { }

    private static readonly System.Type[] Conformers =
        { typeof(LedgerCountOption), typeof(LedgerFlagOption), typeof(LedgerDurationOption) };

    // A call resolves to the generic member when its concrete overload is missing, so each
    // conformer's overload must be declared on its own, with the projected element type.
    private void AssertConcreteOverloads(string name, System.Type returnType)
    {
        foreach (var conformer in Conformers)
        {
            var overload = typeof(LedgerExistentialArrayBuilder).GetMethod(
                name, BindingFlags.Public | BindingFlags.Static, new[] { conformer });
            AssertNotNull(overload, $"{name}({conformer.Name}) is declared");
            AssertFalse(overload!.IsGenericMethodDefinition, $"{name}({conformer.Name}) is a concrete overload");
            AssertEqual(returnType, overload.ReturnType, $"{name}({conformer.Name}) returns the projected list");
        }
    }

    public void TestCompositionSpecializationsAreDeclared()
        => AssertConcreteOverloads(nameof(LedgerExistentialArrayBuilder.Composed), typeof(IReadOnlyList<ILedgerCodedAndLedgerLabelled>));

    public void TestAnySpecializationsAreDeclared()
        => AssertConcreteOverloads(nameof(LedgerExistentialArrayBuilder.Anything), typeof(IReadOnlyList<object>));

    public void TestSendableSpecializationsAreDeclared()
        => AssertConcreteOverloads(nameof(LedgerExistentialArrayBuilder.Sendables), typeof(IReadOnlyList<object>));

    // A composition's proxy carries the container but does not dispatch members itself, so each
    // element is handed back to Swift, which reads it through both witness tables.
    public void TestComposedElementsAnswerThroughBothWitnessTables()
    {
        using var option = new LedgerCountOption(7);
        var result = LedgerExistentialArrayBuilder.Composed(option);
        AssertEqual(2, result.Count, "Composed(count 7): two elements");
        AssertEqual(1007, TestLibFunctions.LedgerCodeOfComposed(result[0]), "element 0 answers through the first protocol's witness table");
        AssertEqual(1008, TestLibFunctions.LedgerCodeOfComposed(result[1]), "element 1 is read at the composition's stride");
        AssertEqual("entry-1007", TestLibFunctions.LedgerLabelOfComposed(result[0]), "element 0 answers through the second protocol's witness table");
        AssertEqual("entry-1008", TestLibFunctions.LedgerLabelOfComposed(result[1]), "element 1's second witness table");
    }

    public void TestComposedSpecializationOfAnotherConformer()
    {
        using var option = new LedgerDurationOption(4.0);
        var result = LedgerExistentialArrayBuilder.Composed(option);
        AssertEqual(2, result.Count, "Composed(duration 4): two elements");
        AssertEqual(3004, TestLibFunctions.LedgerCodeOfComposed(result[0]), "element 0");
        AssertEqual("entry-3005", TestLibFunctions.LedgerLabelOfComposed(result[1]), "element 1");
    }

    public void TestRepeatedReadsOfTheSameElementStayIntact()
    {
        using var option = new LedgerCountOption(2);
        var anything = LedgerExistentialArrayBuilder.Anything(option);
        var composed = LedgerExistentialArrayBuilder.Composed(option);
        for (var i = 0; i < 100; i++)
        {
            AssertEqual((object)"option-1002", anything[1], $"read {i} of the String element");
            AssertEqual("entry-1003", TestLibFunctions.LedgerLabelOfComposed(composed[1]), $"read {i} of the composed element");
        }
    }

    public void TestAnyElementsUnboxToTheirValues()
    {
        using var option = new LedgerFlagOption(true);
        var result = LedgerExistentialArrayBuilder.Anything(option);
        AssertEqual(2, result.Count, "Anything(flag on): two elements");
        AssertEqual((object)2001, result[0], "element 0 is the Int32 code");
        AssertEqual((object)"option-2001", result[1], "element 1 is the String label, read at the opaque box's stride");
    }

    public void TestAnyElementsGoBackToSwiftAsThemselves()
    {
        using var option = new LedgerCountOption(3);
        var result = LedgerExistentialArrayBuilder.Anything(option);
        AssertEqual(1003, TestLibFunctions.LedgerCodeOfAny(result[0]), "the code reaches Swift as an Int32");
        AssertEqual("option-1003", TestLibFunctions.LedgerLabelOfAny(result[1]), "the label reaches Swift as a String");
    }

    public void TestSendableElementsUnboxToTheirValues()
    {
        using var option = new LedgerCountOption(5);
        var result = LedgerExistentialArrayBuilder.Sendables(option);
        AssertEqual(2, result.Count, "Sendables(count 5): two elements");
        AssertEqual((object)1005, result[0], "element 0");
        AssertEqual((object)1007, result[1], "element 1, read at the opaque box's stride");
        AssertEqual(1007, TestLibFunctions.LedgerCodeOfSendable(result[1]), "the element reaches Swift as an Int32");
    }

    // An @objc protocol's existential is one object pointer, not a Swift container. The generator
    // refuses the specialization rather than reading the array through a Swift carrier, so no
    // concrete overload may exist for any conformer.
    public void TestObjCProtocolSpecializationsAreRefused()
    {
        foreach (var conformer in Conformers)
        {
            var overload = typeof(LedgerExistentialArrayBuilder).GetMethod(
                "ObjcCoded", BindingFlags.Public | BindingFlags.Static, new[] { conformer });
            AssertNull(overload, $"ObjcCoded({conformer.Name}) is not declared");
        }
    }
}
