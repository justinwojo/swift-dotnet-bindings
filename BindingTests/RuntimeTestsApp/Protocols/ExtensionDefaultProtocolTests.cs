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
}
