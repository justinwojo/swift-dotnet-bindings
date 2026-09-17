// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// End-to-end gate for a Swift declaration that repeats an external argument label.
///
/// Swift allows one declaration to use the same label twice when the internal names differ, and
/// the ABI JSON records only the labels — so both parameters reached the emitters under a single
/// name. The generated witness introduced <c>inSection</c> twice and derived two <c>inSectionCopy</c>
/// locals from it, an invalid redeclaration that failed the entire wrapper module rather than one
/// member. The fix gives the repeats distinct internal names (<c>inSection</c>, <c>inSection2</c>)
/// the way the C# plane has always disambiguated the same collision.
///
/// Reaching these call sites already proves the wrapper compiled. The assertions additionally prove
/// the rename stayed cosmetic: each parameter still carries its own value to its own position, so a
/// witness that had merged or transposed the repeats fails here even though it would compile.
/// </summary>
public class RepeatedArgumentLabelTests : TestBase
{
    public RepeatedArgumentLabelTests(TestResults results) : base(results) { }

    /// <summary>
    /// Reverse dispatch: Swift calls <c>move(item:inSection:toItem:inSection:)</c> on a C#
    /// conformer with four distinguishable magnitudes. The conformer folds all four into one
    /// number, so a witness that passed either repeat twice, or swapped them, reports a
    /// different total.
    /// </summary>
    public void TestRepeatedLabelArgumentsReachTheConformerInOrder()
    {
        var mover = new RecordingSectionMover();

        var folded = Functions.ApplySectionMove(mover);

        // Swift passes item: 1, inSection: 20, toItem: 3, inSection: 400.
        AssertEqual(1 * 1000000 + 20 * 10000 + 3 * 1000 + 400, folded,
            "Each repeated-label argument arrived at its own parameter of the managed conformer");
    }

    /// <summary>
    /// The conformer sees four separate parameters rather than three, which is the arity the
    /// pre-fix merge would have destroyed.
    /// </summary>
    public void TestRepeatedLabelParametersAreDistinctOnTheConformer()
    {
        var mover = new RecordingSectionMover();

        Functions.ApplySectionMove(mover);

        AssertEqual(20, mover.LastFirstSection,
            "First `inSection` argument bound to the first repeat");
        AssertEqual(400, mover.LastSecondSection,
            "Second `inSection` argument bound to the second repeat (not a copy of the first)");
    }

    /// <summary>
    /// Forward dispatch through the generic-class dispatch protocol, the second emitter that
    /// declares a requirement from these parameter facts: all three parameters must still bind and
    /// the value must come through the one the body can read.
    /// </summary>
    public void TestRepeatedLabelMethodOnGenericClassDispatches()
    {
        using var reporter = new RepeatedLabelReporter<int>();

        var middle = reporter.Describe(20, 7, 400);

        AssertEqual(7, middle,
            "Generic-class method with a repeated label bound all three parameters and returned the middle one");
    }
}

/// <summary>
/// Managed conformer that records each repeated-label argument separately and folds all four into a
/// single positional number, so a merged or transposed repeat is visible in the result.
/// </summary>
internal class RecordingSectionMover : ISectionMoving
{
    public int LastFirstSection { get; private set; } = -1;
    public int LastSecondSection { get; private set; } = -1;

    public int Move(int item, int inSection, int toItem, int inSection2)
    {
        LastFirstSection = inSection;
        LastSecondSection = inSection2;
        return item * 1000000 + inSection * 10000 + toItem * 1000 + inSection2;
    }
}
