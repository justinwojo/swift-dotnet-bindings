// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLibDependency;
// RawPalette is a Swift struct whose only public member is the nested Tone enum, so it projects as a
// C# namespace, which `using SwiftBindingsTestLib;` does not bring into scope.
using RawPalette = SwiftBindingsTestLib.RawPalette;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Plain (non-<c>@objc</c>) integral enums whose explicit Swift raw values are not their
/// declaration order. Their interface prints no raw values, so the C# constants are the case
/// indices, and every crossing must hand Swift exactly the case the constant names. Each Swift
/// function reports the raw value of the case it received (or builds a case from a raw value), so
/// a crossing that mixed up indices and raw values answers with the wrong raw value or traps in
/// <c>init(rawValue:)</c>. Fixture: Types/ExplicitRawValueEnums.swift.
/// </summary>
public class ExplicitRawValueEnumTests : TestBase
{
    public ExplicitRawValueEnumTests(TestResults results) : base(results) { }

    private static readonly (RawShade Case, nint Raw)[] Shades = { (RawShade.Light, 1), (RawShade.Dark, 2) };
    private static readonly (RawGapped Case, nint Raw)[] Gapped =
        { (RawGapped.A, 1), (RawGapped.B, 5), (RawGapped.C, 100), (RawGapped.D, 101) };

    #region Constants and the Swift-backed raw-value helpers

    public void TestConstantsAreKeyedByCaseNameNotRawValue()
    {
        // Non-frozen and at least 32 bits wide: each constant is keyed by its case's name, so a
        // newer SDK's inserted case renumbers none of them. A byte cannot hold a name key, so
        // RawByte keeps its case index.
        AssertEqual((long)NameKeyedCase.Of("light"), (long)RawShade.Light, "RawShade.Light is keyed by its name");
        AssertEqual((long)NameKeyedCase.Of("dark"), (long)RawShade.Dark, "RawShade.Dark is keyed by its name");
        AssertEqual((long)NameKeyedCase.Of("d"), (long)RawGapped.D, "RawGapped.D is keyed by its name");
        AssertEqual(NameKeyedCase.Of("minus"), (int)RawSigned.Minus, "RawSigned.Minus is keyed by its name");
        AssertEqual((byte)1, (byte)RawByte.High, "RawByte.High is case 1");
    }

    public void TestToRawValueAsksSwift()
    {
        foreach (var (c, raw) in Shades)
            AssertEqual((long)raw, c.ToRawValue(), $"{c}.ToRawValue()");
        foreach (var (c, raw) in Gapped)
            AssertEqual((long)raw, c.ToRawValue(), $"{c}.ToRawValue()");
        AssertEqual(-7, RawSigned.Minus.ToRawValue(), "RawSigned.Minus raw value");
        AssertEqual(0, RawSigned.Zero.ToRawValue(), "RawSigned.Zero raw value");
        AssertEqual(9, RawSigned.Plus.ToRawValue(), "RawSigned.Plus raw value");
        AssertEqual(9_000_000_000_000L, RawLarge.Huge.ToRawValue(), "RawLarge.Huge raw value");
        AssertEqual(7L, RawSolo.Only.ToRawValue(), "RawSolo.Only raw value");
        AssertEqual((byte)250, RawByte.High.ToRawValue(), "RawByte.High raw value");
        AssertEqual(8L, RawPalette.Tone.Cool.ToRawValue(), "Nested Tone.Cool raw value");
        AssertEqual(2, RawStaged.Base.ToRawValue(), "RawStaged.Base raw value");
    }

    public void TestFromRawValueAsksSwift()
    {
        foreach (var (c, raw) in Gapped)
            AssertEqual<RawGapped?>(c, RawGappedExtensions.FromRawValue(raw), $"FromRawValue({raw})");
        AssertEqual<RawGapped?>(null, RawGappedExtensions.FromRawValue(2), "2 names no RawGapped case");
        AssertEqual<RawGapped?>(null, RawGappedExtensions.FromRawValue(0), "the index 0 is not a raw value");
        AssertEqual<RawSigned?>(RawSigned.Minus, RawSignedExtensions.FromRawValue(-7), "FromRawValue(-7)");
        AssertEqual<RawLarge?>(RawLarge.Huge, RawLargeExtensions.FromRawValue(9_000_000_000_000L), "FromRawValue(huge)");
        AssertEqual<RawByte?>(RawByte.High, RawByteExtensions.FromRawValue(250), "FromRawValue(250)");
        AssertEqual<RawSolo?>(RawSolo.Only, RawSoloExtensions.FromRawValue(7), "FromRawValue(7)");
        AssertEqual<RawSolo?>(null, RawSoloExtensions.FromRawValue(0), "0 names no RawSolo case");
        AssertEqual<RawPalette.Tone?>(RawPalette.Tone.Warm, RawPaletteToneExtensions.FromRawValue(3), "Tone FromRawValue(3)");
    }

    #endregion

    #region Arguments and returns

    public void TestEveryCaseReachesSwiftAsItsOwnCase()
    {
        foreach (var (c, raw) in Shades)
        {
            AssertEqual(raw, TestLibFunctions.RawShadeValue(c), $"{c} argument");
            AssertEqual(c == RawShade.Light ? "light" : "dark", TestLibFunctions.RawShadeName(c), $"{c} name");
        }
        foreach (var (c, raw) in Gapped)
            AssertEqual(raw, TestLibFunctions.RawGappedValue(c), $"{c} argument");
        AssertEqual(-7, TestLibFunctions.RawSignedValue(RawSigned.Minus), "RawSigned.Minus argument");
        AssertEqual(0, TestLibFunctions.RawSignedValue(RawSigned.Zero), "RawSigned.Zero argument");
        AssertEqual(9, TestLibFunctions.RawSignedValue(RawSigned.Plus), "RawSigned.Plus argument");
        AssertEqual(3L, TestLibFunctions.RawLargeValue(RawLarge.Small), "RawLarge.Small argument");
        AssertEqual(9_000_000_000_000L, TestLibFunctions.RawLargeValue(RawLarge.Huge), "RawLarge.Huge argument");
        AssertEqual((nint)7, TestLibFunctions.RawSoloValue(RawSolo.Only), "RawSolo.Only argument");
        AssertEqual((byte)10, TestLibFunctions.RawByteValue(RawByte.Low), "RawByte.Low argument");
        AssertEqual((byte)250, TestLibFunctions.RawByteValue(RawByte.High), "RawByte.High argument");
        AssertEqual((nint)3, TestLibFunctions.RawToneValue(RawPalette.Tone.Warm), "Tone.Warm argument");
        AssertEqual((nint)8, TestLibFunctions.RawToneValue(RawPalette.Tone.Cool), "Tone.Cool argument");
        AssertEqual(2, TestLibFunctions.RawStagedValue(RawStaged.Base), "RawStaged.Base argument");
    }

    public void TestEveryCaseComesBackFromSwiftAsItsOwnCase()
    {
        foreach (var (c, raw) in Shades)
        {
            AssertEqual(c, TestLibFunctions.RawShadeFor(raw), $"case for raw {raw}");
            AssertEqual(c, TestLibFunctions.RawShadeEcho(c), $"{c} echo");
        }
        foreach (var (c, raw) in Gapped)
            AssertEqual(c, TestLibFunctions.RawGappedFor(raw), $"case for raw {raw}");
        AssertEqual(RawSigned.Minus, TestLibFunctions.RawSignedFor(-7), "RawSigned for -7");
        AssertEqual(RawSigned.Plus, TestLibFunctions.RawSignedFor(9), "RawSigned for 9");
        AssertEqual(RawLarge.Huge, TestLibFunctions.RawLargeFor(9_000_000_000_000L), "RawLarge for huge");
        AssertEqual(RawSolo.Only, TestLibFunctions.GetRawSoloMake(), "RawSolo returned");
        AssertEqual(RawByte.High, TestLibFunctions.RawByteFor(250), "RawByte for 250");
        AssertEqual(RawPalette.Tone.Cool, TestLibFunctions.RawToneFor(8), "Tone for 8");
        AssertEqual(RawStaged.Base, TestLibFunctions.GetRawStagedBase(), "RawStaged.Base returned");
    }

    public void TestOptionalCrossings()
    {
        foreach (var (c, raw) in Shades)
        {
            AssertEqual<RawShade?>(c, TestLibFunctions.RawShadeLookup(raw), $"Optional return for raw {raw}");
            AssertEqual(raw, TestLibFunctions.RawShadeOptionalValue(c), $"Optional {c} argument");
            AssertEqual<RawShade?>(c, TestLibFunctions.RawShadeOptionalEcho(c), $"Optional {c} echo");
        }
        AssertEqual<RawShade?>(null, TestLibFunctions.RawShadeLookup(0), "No case for raw 0");
        AssertEqual((nint)(-1), TestLibFunctions.RawShadeOptionalValue(null), "nil argument");
        AssertEqual<RawShade?>(null, TestLibFunctions.RawShadeOptionalEcho(null), "nil echo");
    }

    #endregion

    #region Collections

    public void TestArrayElementsCrossAsTheirCases()
    {
        var all = TestLibFunctions.GetRawGappedAll();
        AssertEqual(4, all.Count, "four elements");
        for (int i = 0; i < Gapped.Length; i++)
            AssertEqual(Gapped[i].Case, all[i], $"element {i} returned");

        var raws = TestLibFunctions.RawGappedRawValues(new[] { RawGapped.D, RawGapped.A, RawGapped.C, RawGapped.B });
        AssertEqual("101,1,100,5", string.Join(",", raws), "array argument elements reach Swift as their cases");
    }

    public void TestSetElementsCrossAsTheirCases()
    {
        AssertTrue(TestLibFunctions.RawShadeSetContainsDark(new HashSet<RawShade> { RawShade.Dark }), "{dark} contains dark");
        AssertFalse(TestLibFunctions.RawShadeSetContainsDark(new HashSet<RawShade> { RawShade.Light }), "{light} lacks dark");
        AssertEqual((nint)2, TestLibFunctions.RawShadeSetCount(new HashSet<RawShade> { RawShade.Light, RawShade.Dark }), "two-element set");
    }

    #endregion

    #region The enum's own members

    public void TestSelfCrossesAsItsCase()
    {
        foreach (var (c, raw) in Gapped)
            AssertEqual(raw * 2, c.GetDoubled(), $"{c}.doubled");
        AssertEqual(RawGapped.B, RawGapped.A.Following(), "a.following()");
        AssertEqual(RawGapped.D, RawGapped.C.Following(), "c.following()");
        AssertEqual(RawGapped.A, RawGapped.D.Following(), "d.following()");
        AssertEqual((nint)105, RawGapped.B.Sum(RawGapped.C), "b.sum(with: c)");
        AssertEqual(RawGapped.C, RawGappedExtensions.FromSwift(100), "static member returns c");
    }

    #endregion

    #region Properties and subscripts

    public void TestClassPropertiesAndSubscripts()
    {
        using var holder = new RawEnumHolder();
        AssertEqual(RawShade.Dark, holder.Shade, "initial shade read back");
        AssertEqual(RawPalette.Tone.Cool, holder.Tone, "initial tone read back");
        AssertEqual(RawSigned.Minus, holder.Signed, "initial signed read back");
        AssertEqual<RawShade?>(null, holder.MaybeShade, "initial optional shade");

        holder.Shade = RawShade.Light;
        AssertEqual(1, holder.ShadeRawValue, "setter stored light");
        holder.Tone = RawPalette.Tone.Warm;
        AssertEqual(3, holder.ToneRawValue, "setter stored warm");
        holder.Signed = RawSigned.Plus;
        AssertEqual(9, holder.SignedRawValue, "setter stored plus");
        holder.MaybeShade = RawShade.Dark;
        AssertEqual<RawShade?>(RawShade.Dark, holder.MaybeShade, "optional setter round-trips");
        holder.MaybeShade = null;
        AssertEqual<RawShade?>(null, holder.MaybeShade, "optional setter clears");

        foreach (var (c, raw) in Gapped)
        {
            AssertEqual(c, holder[raw], $"subscript returns the case for {raw}");
            AssertEqual(raw, holder[c], $"subscript key {c} reaches Swift");
        }
    }

    public void TestStructProperty()
    {
        using var box = new RawEnumValueBox(RawShade.Dark);
        AssertEqual(2, box.ShadeRawValue, "init argument");
        AssertEqual(RawShade.Dark, box.Shade, "getter");
        box.Shade = RawShade.Light;
        AssertEqual(1, box.ShadeRawValue, "setter");
    }

    #endregion

    #region Closures, callbacks and protocols

    public void TestClosureArgumentAndResult()
    {
        RawShade received = (RawShade)99;
        var raw = TestLibFunctions.RawShadeApply(RawShade.Light, s => { received = s; return RawShade.Dark; });
        AssertEqual(RawShade.Light, received, "callback received light");
        AssertEqual((nint)2, raw, "callback's dark reached Swift");

        RawShade notified = (RawShade)99;
        TestLibFunctions.RawShadeNotify(RawShade.Dark, s => notified = s);
        AssertEqual(RawShade.Dark, notified, "escaping callback received dark");

        RawGapped visited = (RawGapped)99;
        var visitResult = TestLibFunctions.RawGappedVisit(g => { visited = g; return 42; });
        AssertEqual(RawGapped.C, visited, "callback received c");
        AssertEqual((nint)42, visitResult, "callback result");
    }

    public void TestVendedClosures()
    {
        var maker = TestLibFunctions.GetRawShadeMaker();
        AssertEqual(RawShade.Light, maker(1), "vended closure returns light for 1");
        AssertEqual(RawShade.Dark, maker(2), "vended closure returns dark for 2");
        var reader = TestLibFunctions.GetRawShadeReader();
        AssertEqual((nint)1, reader(RawShade.Light), "vended closure receives light");
        AssertEqual((nint)2, reader(RawShade.Dark), "vended closure receives dark");
    }

    private sealed class FlipChooser : IRawShadeChooser
    {
        public RawShade LastSeen { get; private set; } = (RawShade)99;

        public RawShade Choose(RawShade current)
        {
            LastSeen = current;
            return current == RawShade.Light ? RawShade.Dark : RawShade.Light;
        }
    }

    public void TestProtocolImplementedInCSharp()
    {
        var chooser = new FlipChooser();
        AssertEqual((nint)2, TestLibFunctions.RawShadeAsk(chooser, RawShade.Light), "C# answer dark reached Swift");
        AssertEqual(RawShade.Light, chooser.LastSeen, "C# received light");
        AssertEqual((nint)1, TestLibFunctions.RawShadeAsk(chooser, RawShade.Dark), "C# answer light reached Swift");
        AssertEqual(RawShade.Dark, chooser.LastSeen, "C# received dark");
    }

    #endregion

    #region Cross-module and floored cases

    public void TestDependencyEnumCrossesModules()
    {
        var codes = new[] { (DependencyRawCode.Alpha, (nint)11), (DependencyRawCode.Beta, (nint)22), (DependencyRawCode.Gamma, (nint)(-5)) };
        foreach (var (c, raw) in codes)
        {
            AssertEqual(raw, TestLibFunctions.RawDependencyCodeValue(c), $"{c} argument from the dependent module");
            AssertEqual(c, TestLibFunctions.RawDependencyCodeFor(raw), $"{c} returned to the dependent module");
            AssertEqual(raw, SwiftBindingsTestLibDependency.Functions.DependencyRawCodeValue(c), $"{c} argument in its own module");
            AssertEqual((long)raw, c.ToRawValue(), $"{c}.ToRawValue()");
        }
    }

    public void TestFlooredCaseIsRefusedBeforeSwift()
    {
        var future = Enum.Parse<RawStaged>("Future");
        AssertThrows<PlatformNotSupportedException>(() => TestLibFunctions.RawStagedValue(future), "floored case argument");
        AssertThrows<PlatformNotSupportedException>(() => future.ToRawValue(), "floored case ToRawValue");
    }

    #endregion

    #region Non-integral raw values

    private static readonly string LengthyTitle = new string('x', 200);

    /// <summary>
    /// A frozen String-backed enum is a C# enum of case indices; its raw values come from Swift,
    /// including a non-ASCII one and one longer than the helper's first buffer.
    /// </summary>
    public void TestFrozenStringRawValuesComeFromSwift()
    {
        var titles = new[]
        {
            (RawFrozenTitle.Greeting, "hello"), (RawFrozenTitle.Farewell, "see you, \U0001F44B"),
            (RawFrozenTitle.Lengthy, LengthyTitle), (RawFrozenTitle.Plain, "plain"),
        };
        foreach (var (c, raw) in titles)
        {
            AssertEqual(raw, c.ToRawValue(), $"{c}.ToRawValue()");
            AssertEqual<RawFrozenTitle?>(c, RawFrozenTitleExtensions.FromRawValue(raw), $"FromRawValue for {c}");
            AssertEqual(raw, TestLibFunctions.RawFrozenTitleValue(c), $"{c} argument");
            AssertEqual(c, TestLibFunctions.RawFrozenTitleFor(raw), $"{c} returned");
        }
        AssertEqual<RawFrozenTitle?>(null, RawFrozenTitleExtensions.FromRawValue("greeting"), "a case name is not a raw value");
        AssertEqual<RawFrozenTitle?>(null, RawFrozenTitleExtensions.FromRawValue(null!), "null names no case");
    }

    /// <summary>
    /// Non-frozen String, Double and Character raw values project as classes that read the raw
    /// value from Swift.
    /// </summary>
    public void TestClassProjectedRawValuesComeFromSwift()
    {
        AssertEqual("hello", TestLibFunctions.RawTitleValue(RawTitle.Greeting), "RawTitle.Greeting argument");
        AssertEqual("hello", RawTitle.Greeting.RawValue.ToString(), "RawTitle.Greeting raw value");
        AssertEqual("plain", RawTitle.Plain.RawValue.ToString(), "RawTitle.Plain raw value");
        AssertEqual(0.5, TestLibFunctions.RawRatioValue(RawRatio.Half), "RawRatio.Half argument");
        AssertEqual(0.5, RawRatio.Half.RawValue, "RawRatio.Half raw value");
        AssertEqual(1.0, TestLibFunctions.RawRatioValue(TestLibFunctions.RawRatioFor(1.0)), "RawRatio for 1.0");
        AssertEqual("Y", TestLibFunctions.RawLetterValue(RawLetter.Why), "RawLetter.Why argument");
        // A Character raw value crosses as the one-grapheme string it spells.
        AssertTrue(RawLetter.FromRawValue("x") == RawLetter.Ex, "RawLetter from \"x\"");
        AssertTrue(RawLetter.FromRawValue("Y") == RawLetter.Why, "RawLetter from \"Y\"");
        AssertTrue(RawLetter.FromRawValue("y") is null, "\"y\" names no RawLetter");
        AssertTrue(RawLetter.FromRawValue("xY") is null, "two characters name no RawLetter");
        AssertTrue(RawLetter.FromRawValue("") is null, "an empty string names no RawLetter");
        AssertTrue(RawTitle.FromRawValue("hello") == RawTitle.Greeting, "RawTitle from \"hello\"");
    }

    /// <summary>
    /// A callback whose enum argument sits beside an <c>any Error</c> goes through the method-closure
    /// bridge, not the plain closure adapter; the enum still arrives as its own case.
    /// </summary>
    public void TestMethodClosureBridgeCallbackArgument()
    {
        foreach (var c in new[] { RawShade.Light, RawShade.Dark })
        {
            using var reporter = new RawShadeReporter(c);
            RawShade? received = null;
            reporter.Report((shade, _) => received = shade);
            AssertEqual(c, received, $"{c} reaches the callback through the method-closure bridge");
        }
    }

    #endregion

    #region Existing fixtures with explicit raw values

    /// <summary>
    /// <c>BridgeLevel: Int32 { low = 1, high = 2 }</c> returned through a method-generic bridge.
    /// </summary>
    public void TestBridgeLevelGenericBridgeReturn()
    {
        using var host = new BridgeHost();
        using var low = new BridgeTagSource(bridgeTag: 1);
        using var high = new BridgeTagSource(bridgeTag: 2);
        AssertEqual(BridgeLevel.Low, host.Level(low), "tag 1 -> low");
        AssertEqual(BridgeLevel.High, host.Level(high), "tag 2 -> high");
        AssertEqual(1, BridgeLevel.Low.ToRawValue(), "low raw value");
        AssertEqual(2, BridgeLevel.High.ToRawValue(), "high raw value");
    }

    /// <summary>
    /// <c>SingletonFlag: Int32 { case active = 1 }</c>: the case index 0 is not its raw value.
    /// </summary>
    public void TestSingletonFlagRawValue()
    {
        AssertEqual(1, SingletonFlag.Active.ToRawValue(), "active raw value");
        AssertEqual<SingletonFlag?>(SingletonFlag.Active, SingletonFlagExtensions.FromRawValue(1), "FromRawValue(1)");
        AssertEqual<SingletonFlag?>(null, SingletonFlagExtensions.FromRawValue(0), "0 names no case");
    }

    #endregion
}
