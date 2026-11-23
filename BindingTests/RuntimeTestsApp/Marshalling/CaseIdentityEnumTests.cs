// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Simple enums with no raw value the binding can know cross by case identity: each C# constant is
/// its case's declaration-order index, and every crossing maps it to that case through a switch on
/// the case rather than the enum's in-memory tag. A case the binding cannot name (here an
/// <c>@_spi</c> one the public interface omits, so the binary's tags run one ahead of the constants)
/// reaches C# as an unnamed value that is no published member, and comes back to Swift as the same
/// case. Each Swift function names the case it holds, so a crossing that picked
/// the wrong case answers with the wrong name. Fixture: Types/CaseIdentityEnums.swift.
/// </summary>
public class CaseIdentityEnumTests : TestBase
{
    public CaseIdentityEnumTests(TestResults results) : base(results) { }

    #region A hidden case before the visible ones

    public void TestTheConstantsCountOnlyTheCasesTheInterfacePublishes()
    {
        // The public interface omits the @_spi case, so the binary's tags (first = 1, second = 2)
        // are not the binding's constants: a crossing by tag would read First as the hidden case.
        AssertEqual(0, (int)CaseShift.First, "CaseShift.First is the first published case");
        AssertEqual(1, (int)CaseShift.Second, "CaseShift.Second is the second published case");
    }

    public void TestVisibleCasesCrossAsThemselves()
    {
        AssertEqual("first", Functions.CaseShiftName(CaseShift.First), "argument First");
        AssertEqual("second", Functions.CaseShiftName(CaseShift.Second), "argument Second");
        AssertEqual(CaseShift.Second, Functions.GetCaseShiftSecond(), "return Second");
        AssertEqual(CaseShift.First, Functions.CaseShiftEcho(CaseShift.First), "echo First");
        AssertEqual(CaseShift.Second, Functions.CaseShiftEcho(CaseShift.Second), "echo Second");
    }

    public void TestAHiddenCaseIsNoPublishedMemberAndComesBackAsItself()
    {
        var hidden = Functions.GetCaseShiftHidden();
        AssertFalse(Enum.IsDefined(hidden), $"the hidden case is no published member (got {(int)hidden})");
        AssertTrue(hidden != CaseShift.First && hidden != CaseShift.Second, "the hidden case is no visible case");
        AssertEqual("hidden", Functions.CaseShiftName(hidden), "the hidden value reaches Swift as the hidden case");
        AssertEqual(hidden, Functions.CaseShiftEcho(hidden), "the hidden value round-trips");
    }

    public void TestOptionalCrossings()
    {
        var hidden = Functions.GetCaseShiftHidden();
        AssertEqual("none", Functions.CaseShiftNameOrNone(null), "nil argument");
        AssertEqual("second", Functions.CaseShiftNameOrNone(CaseShift.Second), "optional argument Second");
        AssertEqual("hidden", Functions.CaseShiftNameOrNone(hidden), "optional argument hidden");
        AssertEqual<CaseShift?>(hidden, Functions.CaseShiftOptional(true), "optional return hidden");
        AssertEqual<CaseShift?>(null, Functions.CaseShiftOptional(false), "optional return nil");
        AssertEqual<CaseShift?>(hidden, Functions.CaseShiftOptionalThrowing(true), "throwing optional return hidden");
        AssertEqual<CaseShift?>(null, Functions.CaseShiftOptionalThrowing(false), "throwing optional return nil");
    }

    public void TestAnOptionalArgumentThatNamesNoCaseIsRefused()
    {
        // The Optional is built in Swift's own layout through the enum's case codec, which names
        // the case a value means; a value that names none (past the published cases, or one whose
        // low byte would alias a case) is refused before Swift.
        AssertThrows<ArgumentOutOfRangeException>(() => Functions.CaseShiftNameOrNone((CaseShift)2), "the nil byte");
        AssertThrows<ArgumentOutOfRangeException>(() => Functions.CaseShiftNameOrNone((CaseShift)254), "the hidden code's low byte");
        AssertThrows<ArgumentOutOfRangeException>(() => Functions.CaseShiftNameOrNone((CaseShift)257), "Second's low byte");
    }

    public void TestTheEnumsOwnMembers()
    {
        var hidden = Functions.GetCaseShiftHidden();
        AssertEqual("second", CaseShift.Second.GetCaseName(), "Second's own property");
        AssertEqual("hidden", hidden.GetCaseName(), "the hidden case's own property");
        AssertEqual(CaseShift.Second, CaseShift.First.Toggled(), "First's own method");
        AssertEqual(hidden, hidden.Toggled(), "the hidden case's own method returns it");
    }

    public void TestPropertySubscriptAndOptionalReturn()
    {
        var hidden = Functions.GetCaseShiftHidden();
        using var box = new CaseShiftBox(CaseShift.Second);
        AssertEqual(CaseShift.Second, box.Mode, "getter");
        box.Mode = CaseShift.First;
        AssertEqual("first", box.ModeName, "setter First");
        box.Mode = hidden;
        AssertEqual("hidden", box.ModeName, "setter hidden");
        AssertEqual(hidden, box.Mode, "getter hidden");
        AssertEqual(hidden, box[0], "subscript hidden");
        AssertEqual(CaseShift.Second, box[1], "subscript Second");
        AssertEqual<CaseShift?>(hidden, box.OptionalMode(true), "optional result hidden");
        AssertEqual<CaseShift?>(null, box.OptionalMode(false), "optional result nil");
    }

    public void TestClosuresInBothDirections()
    {
        var hidden = Functions.GetCaseShiftHidden();
        using var box = new CaseShiftBox(CaseShift.Second);

        CaseShift? seen = null;
        box.Report(m => seen = m);
        AssertEqual<CaseShift?>(CaseShift.Second, seen, "closure argument Second");

        box.Transform(m => m == CaseShift.Second ? hidden : CaseShift.First);
        AssertEqual("hidden", box.ModeName, "closure result hidden reaches Swift as itself");
        box.Report(m => seen = m);
        AssertEqual<CaseShift?>(hidden, seen, "closure argument hidden");

        box.Transform(m => m == hidden ? CaseShift.First : CaseShift.Second);
        AssertEqual("first", box.ModeName, "closure result First");

        CaseShift? optionalSeen = CaseShift.Second;
        box.ReportOptional(false, m => optionalSeen = m);
        AssertEqual<CaseShift?>(null, optionalSeen, "optional closure argument nil");
        box.ReportOptional(true, m => optionalSeen = m);
        AssertEqual<CaseShift?>(CaseShift.First, optionalSeen, "optional closure argument First");
        box.Mode = hidden;
        box.ReportOptional(true, m => optionalSeen = m);
        AssertEqual<CaseShift?>(hidden, optionalSeen, "optional closure argument hidden");

        AssertEqual("none", box.PickOptional(() => null), "optional closure result nil");
        AssertEqual("second", box.PickOptional(() => CaseShift.Second), "optional closure result Second");
        AssertEqual("hidden", box.PickOptional(() => hidden), "optional closure result hidden");
    }

    #endregion

    #region Single-case and wide enums

    public void TestSingleCaseEnum()
    {
        AssertEqual(SoloPlain.Only, Functions.GetSoloPlainMake(), "return");
        AssertEqual("only", Functions.SoloPlainName(SoloPlain.Only), "argument");
    }

    public void TestEnumWithMoreCasesThanAByteHolds()
    {
        foreach (var index in new[] { 0, 1, 127, 128, 255, 256, 257, 299 })
        {
            var value = Functions.WideCase(index);
            AssertEqual(index, (int)value, $"case {index} returns as constant {index}");
            AssertEqual((nint)index, Functions.WideIndex(value), $"constant {index} reaches Swift as case {index}");
        }
        AssertEqual((nint)299, Functions.WideIndex(WideCases.C299), "the last case");
    }

    public void TestAHiddenCaseAfterAHalfFullByteRange()
    {
        // 128 published constants (0..127) in a byte-wide C# enum, then a hidden case: its unnamed
        // value must come from the free upper half, not collide with a constant or be refused.
        var hidden = Functions.GetNarrowCasesHidden();
        AssertFalse(Enum.IsDefined(hidden), $"the hidden case is no published member (got {(byte)hidden})");
        AssertTrue((byte)hidden >= 128, $"its value lies above the published constants (got {(byte)hidden})");
        AssertTrue(Functions.NarrowCasesIsHidden(hidden), "it reaches Swift as the hidden case");
        AssertEqual((nint)128, Functions.NarrowCasesRawValue(hidden), "Swift reads its raw value");
        AssertEqual(NarrowCases.N127, Functions.NarrowCase(127), "the last published case");
        AssertFalse(Functions.NarrowCasesIsHidden(NarrowCases.N127), "the last published case is not the hidden one");
    }

    #endregion

    #region Raw-value helpers beside the enum's own toRawValue / fromRawValue

    public void TestRawValueHelpersStepAsideOnAClassProjectedEnum()
    {
        AssertEqual("A", RawNamedMode.Alpha.RawValue, "Swift's rawValue");
        AssertEqual("own:B", RawNamedMode.Beta.ToRawValue(), "the enum's own toRawValue()");
        AssertEqual(RawNamedMode.Beta, RawNamedMode.FromSwiftRawValue("B"), "the binding's helper runs init(rawValue:)");
        AssertTrue(RawNamedMode.FromSwiftRawValue("own") is null, "no case has raw value \"own\"");
        AssertEqual(RawNamedMode.Beta, RawNamedMode.FromRawValue("own"), "the enum's own fromRawValue(_:)");
    }

    public void TestRawValueHelpersStepAsideOnAFrozenStringEnum()
    {
        AssertEqual("A", RawNamedFrozen.Alpha.ToSwiftRawValue(), "the binding's helper reads Swift's rawValue");
        AssertEqual("own:B", RawNamedFrozen.Beta.ToRawValue(), "the enum's own toRawValue()");
        AssertEqual<RawNamedFrozen?>(RawNamedFrozen.Beta, RawNamedFrozenExtensions.FromSwiftRawValue("B"), "the binding's helper runs init(rawValue:)");
        AssertEqual<RawNamedFrozen?>(null, RawNamedFrozenExtensions.FromSwiftRawValue("own"), "no case has raw value \"own\"");
        AssertEqual(RawNamedFrozen.Beta, RawNamedFrozenExtensions.FromRawValue("own"), "the enum's own fromRawValue(_:)");
        AssertEqual(RawNamedFrozen.Alpha, RawNamedFrozenExtensions.FromRawValue("other"), "the enum's own fromRawValue(_:) on another text");
    }

    public void TestTheEnumsOwnMembersTakeStringParameters()
    {
        // Each String crosses the member's wrapper as its UTF-8 bytes; non-ASCII text and an empty
        // String (a null byte pointer) must arrive intact, and two String arguments stay in order.
        AssertEqual("«B»", RawNamedFrozen.Beta.Labeled("«", "»"), "instance member, two String arguments, String result");
        AssertEqual("A", RawNamedFrozen.Alpha.Labeled("", ""), "empty String arguments");
        AssertTrue(RawNamedFrozen.Beta.Matches("B"), "instance member, String argument, Bool result");
        AssertFalse(RawNamedFrozen.Beta.Matches("b"), "a different String");
        AssertEqual("4x3:été!", RawNamedFrozenExtensions.Describe("été!", 3), "static member, String and Int arguments, String result");
        AssertEqual("0x0:", RawNamedFrozenExtensions.Describe("", 0), "an empty String argument");
    }

    public void TestRawValueHelpersStepAsideOnAnIntegralEnum()
    {
        AssertEqual(20L, (long)RawNamedLevel.High.ToSwiftRawValue(), "the binding's helper reads Swift's rawValue");
        AssertEqual(21L, (long)RawNamedLevel.High.ToRawValue(), "the enum's own toRawValue()");
        AssertEqual<RawNamedLevel?>(RawNamedLevel.Low, RawNamedLevelExtensions.FromSwiftRawValue(10), "the binding's helper runs init(rawValue:)");
        AssertEqual(RawNamedLevel.High, RawNamedLevelExtensions.FromRawValue(0), "the enum's own fromRawValue(_:)");
    }

    public void TestRawValueHelpersCarryAHiddenCase()
    {
        var hidden = Functions.GetCaseShiftLevelHidden();
        AssertFalse(Enum.IsDefined(hidden), $"the hidden case is no published member (got {(int)hidden})");
        AssertEqual(5L, (long)hidden.ToRawValue(), "Swift reads the hidden case's raw value");
        var found = CaseShiftLevelExtensions.FromRawValue(5);
        AssertEqual<CaseShiftLevel?>(hidden, found, "init(rawValue:) finds the hidden case as the same unnamed value");
        AssertEqual("hidden", Functions.CaseShiftLevelName(found!.Value), "and it reaches Swift as the hidden case");
        AssertEqual(20L, (long)CaseShiftLevel.High.ToRawValue(), "a published case's raw value");
        AssertEqual<CaseShiftLevel?>(null, CaseShiftLevelExtensions.FromRawValue(7), "no case has raw value 7");
        AssertThrows<ArgumentOutOfRangeException>(() => ((CaseShiftLevel)1234).ToRawValue(),
            "a value that names no case is refused before Swift sees it as a case");
    }

    #endregion
}
