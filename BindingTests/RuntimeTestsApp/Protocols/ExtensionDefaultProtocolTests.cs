// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Tests the TipKit.Tip-like protocol pattern:
/// - Protocol with required properties (protocolReq=true)
/// - Extension default methods (protocolReq=false)
/// - Verifies MissingRequirements fix: extension defaults that fail ABI parsing
///   should not block proxy generation
/// </summary>
public class ExtensionDefaultProtocolTests : TestBase
{
    public ExtensionDefaultProtocolTests(TestResults results) : base(results) { }

    public void TestWelcomeTipCreation()
    {
        using var tip = SwiftBindingsTestLib.Functions.CreateWelcomeTip();
        AssertEqual("welcome", tip.TipId, "WelcomeTip.TipId");
        TestLogger.Info($"WelcomeTip created: id=\"{tip.TipId}\"");
    }

    public void TestWelcomeTipTitle()
    {
        using var tip = SwiftBindingsTestLib.Functions.CreateWelcomeTip();
        AssertEqual("Welcome!", tip.TipTitle, "WelcomeTip.TipTitle");
        TestLogger.Info($"WelcomeTip.TipTitle = \"{tip.TipTitle}\"");
    }

    public void TestWelcomeTipMessage()
    {
        using var tip = SwiftBindingsTestLib.Functions.CreateWelcomeTip();
        AssertEqual("Get started with our app.", tip.TipMessage, "WelcomeTip.TipMessage");
        TestLogger.Info($"WelcomeTip.TipMessage = \"{tip.TipMessage}\"");
    }

    public void TestGetTipTitle()
    {
        using var tip = SwiftBindingsTestLib.Functions.CreateWelcomeTip();
        var title = SwiftBindingsTestLib.Functions.GetTipTitle(tip);
        AssertEqual("Welcome!", title, "GetTipTitle existential dispatch");
        TestLogger.Info($"GetTipTitle returned \"{title}\"");
    }

    public void TestGetTipMessage()
    {
        using var tip = SwiftBindingsTestLib.Functions.CreateWelcomeTip();
        var message = SwiftBindingsTestLib.Functions.GetTipMessage(tip);
        AssertEqual("Get started with our app.", message, "GetTipMessage existential dispatch");
        TestLogger.Info($"GetTipMessage returned \"{message}\"");
    }

    // ─── Read-only extension-default PROPERTIES surfaced as synthetic getters ───
    // shouldDisplayTip / tipPriorityScore are declared on `extension TipLike`, not as
    // protocol requirements. The symbol graph never emits them on conformers, so the
    // generator surfaces them as synthetic getter methods (GetX()) on each concrete type.

    public void TestWelcomeTipShouldDisplay()
    {
        using var tip = SwiftBindingsTestLib.Functions.CreateWelcomeTip();
        // tipId == "welcome" (non-empty) → shouldDisplayTip == true.
        AssertTrue(tip.GetShouldDisplayTip(), "WelcomeTip.GetShouldDisplayTip()");
        TestLogger.Info($"WelcomeTip.GetShouldDisplayTip() = {tip.GetShouldDisplayTip()}");
    }

    public void TestEmptyTipShouldNotDisplay()
    {
        using var tip = SwiftBindingsTestLib.Functions.CreateEmptyTip();
        // tipId == "" (empty) → shouldDisplayTip == false. Discriminates real self-dispatch
        // from a constant: the same extension default returns the other boolean here.
        AssertFalse(tip.GetShouldDisplayTip(), "EmptyTip.GetShouldDisplayTip()");
        TestLogger.Info($"EmptyTip.GetShouldDisplayTip() = {tip.GetShouldDisplayTip()}");
    }

    public void TestWelcomeTipPriorityScore()
    {
        using var tip = SwiftBindingsTestLib.Functions.CreateWelcomeTip();
        // Int32(tipId.count) == Int32("welcome".count) == 7 — proves a non-Bool primitive
        // value round-trips through the synthetic getter, not just true/false.
        AssertEqual(7, tip.GetTipPriorityScore(), "WelcomeTip.GetTipPriorityScore()");
    }

    public void TestEmptyTipPriorityScore()
    {
        using var tip = SwiftBindingsTestLib.Functions.CreateEmptyTip();
        AssertEqual(0, tip.GetTipPriorityScore(), "EmptyTip.GetTipPriorityScore()");
    }

    // ─── Read-only extension-default Bool properties on a GENERIC conformer ───
    // isReversible / isAdditive are extension defaults on `CsmReversibleAction`, surfaced on
    // the GENERIC conformer `CsmFromToBy<Value>` through the concrete-specialization (CSM) path
    // rather than the non-generic free-function path. That path once rendered the getter as a
    // method CALL (`__self.isReversible()`), which swiftc rejects for a Bool value, so the whole
    // specialization wrapper failed to compile and the SDK gave up (SWIFTBIND051) — the exact
    // RealityFoundation `FromToByAction<Value>` regression this fixture pins. These round-trip a
    // value derived from instance state (stepCount), proving real self-dispatch, not a constant.

    public void TestJointActionIsReversibleTrue()
    {
        // stepCount == 3 → isReversible (3 > 0) == true.
        using var action = SwiftBindingsTestLib.Functions.MakeJointAction(3);
        AssertTrue(action.IsReversible(), "CsmFromToBy<CsmJointValue>(3).IsReversible()");
    }

    public void TestJointActionIsReversibleFalse()
    {
        // stepCount == 0 → isReversible (0 > 0) == false. Discriminates real self-dispatch
        // from a constant: the same extension default returns the other boolean here.
        using var action = SwiftBindingsTestLib.Functions.MakeJointAction(0);
        AssertFalse(action.IsReversible(), "CsmFromToBy<CsmJointValue>(0).IsReversible()");
    }

    public void TestJointActionIsAdditiveTrue()
    {
        // stepCount == 4 → isAdditive (4 % 2 == 0) == true; isReversible (4 > 0) == true.
        using var action = SwiftBindingsTestLib.Functions.MakeJointAction(4);
        AssertTrue(action.IsAdditive(), "CsmFromToBy<CsmJointValue>(4).IsAdditive()");
        AssertTrue(action.IsReversible(), "CsmFromToBy<CsmJointValue>(4).IsReversible()");
    }

    public void TestJointActionIsAdditiveFalse()
    {
        // stepCount == 3 → isAdditive (3 % 2 == 0) == false — the second getter flips
        // independently of the first, so both specialization wrappers are exercised.
        using var action = SwiftBindingsTestLib.Functions.MakeJointAction(3);
        AssertFalse(action.IsAdditive(), "CsmFromToBy<CsmJointValue>(3).IsAdditive()");
    }
}
