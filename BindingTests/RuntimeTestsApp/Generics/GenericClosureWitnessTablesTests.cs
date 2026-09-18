// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime coverage for Generics/GenericClosureWitnessTables.swift: a member that declares its own
/// generic parameter <em>and</em> takes a closure.
///
/// <para>A closure parameter is adapted by a generated Swift wrapper, and the wrapper for such a
/// member has to be generic too. It was emitted referencing <c>τ_0_0</c> without declaring it, so it
/// did not compile at all; and for the instance receiver, where the wrapper is a free function
/// taking <c>self</c> as an explicit parameter, Swift appends the implicit metadata and witness
/// tables after <em>every</em> declared parameter — <c>self</c> included — so the P/Invoke has to
/// place <c>self</c> before the metadata, not after it. Both halves are ABI-invisible to the two
/// compilers: each side builds, and a wrong order simply reads the wrong registers at run time.
/// Only an actual call on a real runtime shows it, which is what these tests are.</para>
///
/// <para>The three receiver shapes are covered because they lay the arguments out differently:
/// a free function (no <c>self</c>), an instance method (<c>self</c> as the trailing declared
/// parameter, then metadata, then the witness table) and a static method (no <c>self</c>, but
/// reached through the type). Every value returned is asymmetric in its inputs, so a
/// transposition of the closure result, the rule's <c>factor</c> or the receiver's <c>base</c>
/// produces a different number rather than an accidentally-correct one.</para>
///
/// <para><c>sumThroughSequence</c> covers the opposite outcome: its constraint is
/// <c>Swift.Sequence</c>, which the binding does not project, so the P/Invoke carries no
/// witness-table slot while the Swift entry point still expects one. That call would hand Swift a
/// witness table from a register nobody wrote, so the member is emitted as an SB0009 ABI-floor
/// tombstone — declared, so conformances and source referencing it still compile, and throwing
/// rather than faulting if it is called.</para>
/// </summary>
public class GenericClosureWitnessTablesTests : TestBase
{
    public GenericClosureWitnessTablesTests(TestResults results) : base(results) { }

    // MARK: - scaleWithRule<R: ScaleRule> — free function, no self

    public void TestScaleWithRule_FreeFunction_PassesMetadataAndWitnessTable()
    {
        using var rule = new DoubleRule(factor: 5);

        var result = TestLibFunctions.ScaleWithRule(rule, x => x * 3);

        // Swift: adjust(rule.factor) == 5 * 3.
        AssertEqual((nint)15, result, "scaleWithRule<DoubleRule>");
    }

    public void TestScaleWithRule_ClosureSeesTheRulesFactor()
    {
        using var rule = new DoubleRule(factor: 9);
        nint seen = 0;

        TestLibFunctions.ScaleWithRule(rule, x => { seen = x; return x; });

        // The value reaching the closure comes from a witness call on R, so a missing or misplaced
        // witness table shows up here as a wrong or garbage factor rather than as a wrong total.
        AssertEqual((nint)9, seen, "closure argument is rule.factor read through the witness table");
    }

    // MARK: - RuleBox.apply<R: ScaleRule> — instance method, self is a declared wrapper parameter

    public void TestRuleBoxApply_InstanceReceiver_SelfPrecedesMetadata()
    {
        using var box = new RuleBox(@base: 100);
        using var rule = new DoubleRule(factor: 7);

        var result = box.Apply(rule, x => x + 1);

        // Swift: base + adjust(rule.factor) == 100 + 8. Every operand is distinct, so reading self,
        // the metadata or the witness table out of the wrong register cannot land on 108.
        AssertEqual((nint)108, result, "RuleBox.apply<DoubleRule>");
    }

    public void TestRuleBoxApply_TwoReceivers_EachReadsItsOwnSelf()
    {
        using var small = new RuleBox(@base: 1);
        using var large = new RuleBox(@base: 1000);
        using var rule = new DoubleRule(factor: 2);

        var fromSmall = small.Apply(rule, x => x);
        var fromLarge = large.Apply(rule, x => x);

        AssertEqual((nint)3, fromSmall, "RuleBox(base: 1).apply");
        AssertEqual((nint)1002, fromLarge, "RuleBox(base: 1000).apply");
    }

    // MARK: - RuleBox.applyStatic<R: ScaleRule> — static method on a class, no self

    public void TestRuleBoxApplyStatic_StaticReceiver_PassesMetadataAndWitnessTable()
    {
        using var rule = new DoubleRule(factor: 4);

        var result = RuleBox.ApplyStatic(rule, x => x - 1);

        // Swift: adjust(rule.factor) * 10 == 3 * 10.
        AssertEqual((nint)30, result, "RuleBox.applyStatic<DoubleRule>");
    }

    // MARK: - sumThroughSequence<T: Sequence> — the constraint the binding cannot project

    public void TestSumThroughSequence_UnprojectableConstraint_IsDeclared()
    {
        // Declared at all is the point of a tombstone: removing the member would break source and
        // any conformance that names it, which is why the body throws instead of the member
        // vanishing.
        var method = typeof(Functions).GetMethod(
            nameof(Functions.SumThroughSequence),
            BindingFlags.Public | BindingFlags.Static);

        AssertNotNull(method, "sumThroughSequence must still be declared as an ABI-floor tombstone");
    }

    public void TestSumThroughSequence_UnprojectableConstraint_CarriesSB0009()
    {
        var method = typeof(Functions).GetMethod(
            nameof(Functions.SumThroughSequence),
            BindingFlags.Public | BindingFlags.Static);
        AssertNotNull(method, "sumThroughSequence must still be declared");

        var obsolete = method!.GetCustomAttribute<ObsoleteAttribute>();
        AssertNotNull(obsolete, "sumThroughSequence must be marked [Obsolete]");
        // SB0009 is the uncallable-ABI tombstone marker, distinct from the advisory SB0001 that
        // consumers suppress wholesale; a tombstone hidden behind SB0001 would warn about nothing.
        AssertEqual("SB0009", obsolete!.DiagnosticId, "tombstone diagnostic id");
    }

    public void TestSumThroughSequence_UnprojectableConstraint_ThrowsRatherThanFaulting()
    {
        using var rule = new DoubleRule(factor: 1);

#pragma warning disable SB0009 // deliberately calling the tombstone
        AssertThrows<NotSupportedException>(
            () => Functions.SumThroughSequence(rule, x => x),
            "sumThroughSequence must throw rather than call Swift without the witness table it expects");
#pragma warning restore SB0009
    }
}
