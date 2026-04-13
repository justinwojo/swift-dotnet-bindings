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
}
