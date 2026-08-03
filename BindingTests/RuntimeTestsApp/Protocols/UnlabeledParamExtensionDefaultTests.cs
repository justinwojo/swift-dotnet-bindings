// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// A protocol requirement whose FIRST parameter is unlabeled (<c>_</c>) and which is satisfied
/// for most conformers by an unconstrained protocol-extension default. Conformers that lean on
/// the default used to lose their ENTIRE <c>: IFrameStage</c> conformance, while a sibling that
/// spelled the member out kept its own — so the surface below (existential dispatch through the
/// interface for <c>DoublingStage</c>/<c>OffsetStage</c>) only exists once the defaults index is
/// keyed the way the swiftinterface prints the requirement.
/// </summary>
public class UnlabeledParamExtensionDefaultTests : TestBase
{
    public UnlabeledParamExtensionDefaultTests(TestResults results) : base(results) { }

    // ─── The recovered conformance is real: default-relying conformers reach Swift's
    //     existential-taking free functions, which is only possible through IFrameStage. ───

    public void TestDefaultRelyingConformerFlowsThroughInterface()
    {
        using var stage = SwiftBindingsTestLib.Functions.MakeDoublingStage();

        IFrameStage asInterface = stage;
        AssertEqual("doubling", asInterface.StageIdentifier, "DoublingStage.StageIdentifier via IFrameStage");
        AssertEqual(42, asInterface.Apply(21), "DoublingStage.Apply(int) via IFrameStage");
    }

    public void TestDefaultRelyingConformerDispatchesThroughSwiftExistential()
    {
        using var stage = SwiftBindingsTestLib.Functions.MakeDoublingStage();

        // Swift receives `any FrameStage` and calls back into the witness — a real round trip,
        // not just a compile-time cast.
        AssertEqual("doubling", SwiftBindingsTestLib.Functions.DescribeStage(stage), "DescribeStage(DoublingStage)");
        AssertEqual(14, SwiftBindingsTestLib.Functions.ApplyStage(stage, 7), "ApplyStage(DoublingStage, 7)");
    }

    public void TestSecondDefaultRelyingConformerDispatches()
    {
        // A second recovered conformer, so the recovery is not a single-type coincidence.
        using var stage = SwiftBindingsTestLib.Functions.MakeOffsetStage(5);

        AssertEqual("offset", SwiftBindingsTestLib.Functions.DescribeStage(stage), "DescribeStage(OffsetStage)");
        AssertEqual(12, SwiftBindingsTestLib.Functions.ApplyStage(stage, 7), "ApplyStage(OffsetStage, 7)");
    }

    // ─── Control: the conformer that implements the defaulted requirements explicitly keeps
    //     working, and its own members override the interface defaults. ───

    public void TestExplicitConformerOverridesInterfaceDefault()
    {
        using var stage = SwiftBindingsTestLib.Functions.MakeExplicitStage();
        using var outcome = new StageOutcome(10);
        using var context = new StageContext(3);

        IFrameStage asInterface = stage;
        // The interface member resolves to ExplicitStage's OWN implementation (10 + 3), not to the
        // interface default body — so the recovered requirement is genuinely overridable.
        using var applied = asInterface.Apply(outcome, context);
        AssertEqual(13, applied.Width, "ExplicitStage.Apply(StageOutcome, StageContext) via IFrameStage");
        AssertEqual(7, asInterface.Measure(outcome, context), "ExplicitStage.Measure via IFrameStage");
    }

    public void TestExplicitConformerDispatchesThroughSwiftExistential()
    {
        using var stage = SwiftBindingsTestLib.Functions.MakeExplicitStage();

        AssertEqual("explicit", SwiftBindingsTestLib.Functions.DescribeStage(stage), "DescribeStage(ExplicitStage)");
        AssertEqual(8, SwiftBindingsTestLib.Functions.ApplyStage(stage, 7), "ApplyStage(ExplicitStage, 7)");
    }

    // ─── The recovered requirement's interface body is honest about what it can't do. ───

    public void TestDefaultRelyingConformerReportsUnsupportedOnInterfaceDefault()
    {
        using var stage = SwiftBindingsTestLib.Functions.MakeDoublingStage();
        using var outcome = new StageOutcome(10);
        using var context = new StageContext(3);

        IFrameStage asInterface = stage;
        // DoublingStage has no C# member for the defaulted requirement, so the call lands on the
        // interface default body. That body must say so plainly rather than mis-dispatching —
        // Swift's own default is reachable on the concrete type, not through this slot.
        var threw = false;
        try
        {
            using var _ = asInterface.Apply(outcome, context);
        }
        catch (NotSupportedException)
        {
            threw = true;
        }
        AssertTrue(threw, "DoublingStage interface default throws NotSupportedException");
    }
}
