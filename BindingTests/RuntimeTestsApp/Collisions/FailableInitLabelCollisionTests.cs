// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Two <c>init?</c> overloads on <see cref="LabeledLoginConfig"/> differ only by Swift argument label
/// (<c>nonce:</c> vs <c>messengerPageId:</c>) but erase to the SAME projected C# factory signature
/// (<c>TryCreate(IEnumerable&lt;string&gt;, LoginTrackingPref, string, out …)</c>). Before the
/// failable-init overload-collapse fix the second was silently dropped as DuplicateSignature.
///
/// Now the first-declared init keeps the plain <c>TryCreate</c> and the colliding sibling is recovered
/// under a label-disambiguated factory (<c>TryCreateWithMessengerPageId</c>). Both must be reachable and
/// construct distinct instances — the per-init <c>detail</c> value proves which Swift body each factory
/// reaches. If the sibling were still dropped, its factory would be absent and this file would fail to
/// compile (the compile gate), so these assertions cover both reachability and correct routing.
/// </summary>
public class FailableInitLabelCollisionTests : TestBase
{
    public FailableInitLabelCollisionTests(TestResults results) : base(results) { }

    public void TestFirstFailableInitKeepsPlainTryCreate()
    {
        var ok = LabeledLoginConfig.TryCreate(new[] { "public_profile" }, LoginTrackingPref.Enabled, "abc123", out var cfg);
        AssertTrue(ok, "first init? overload succeeds under the plain TryCreate");
        AssertNotNull(cfg, "successful TryCreate yields a non-null instance");
        AssertEqual("nonce:abc123", cfg!.Detail.ToString(), "plain TryCreate reaches the nonce: init body");
        cfg.Dispose();
    }

    public void TestCollidingSiblingRecoveredUnderDisambiguatedFactory()
    {
        var ok = LabeledLoginConfig.TryCreateWithMessengerPageId(new[] { "email" }, LoginTrackingPref.Limited, "page-42", out var cfg);
        AssertTrue(ok, "colliding init? overload is reachable (not dropped) under its disambiguated factory");
        AssertNotNull(cfg, "successful TryCreateWithMessengerPageId yields a non-null instance");
        AssertEqual("page:page-42", cfg!.Detail.ToString(), "disambiguated factory reaches the messengerPageId: init body");
        cfg.Dispose();
    }

    public void TestBothFactoriesConstructDistinctInstances()
    {
        var ok1 = LabeledLoginConfig.TryCreate(new[] { "p" }, LoginTrackingPref.Enabled, "n", out var a);
        var ok2 = LabeledLoginConfig.TryCreateWithMessengerPageId(new[] { "p" }, LoginTrackingPref.Enabled, "m", out var b);
        AssertTrue(ok1 && ok2, "both failable-init factories succeed");
        AssertNotNull(a, "first factory instance is non-null");
        AssertNotNull(b, "second factory instance is non-null");
        AssertEqual("nonce:n", a!.Detail.ToString(), "first factory retains the nonce: body");
        AssertEqual("page:m", b!.Detail.ToString(), "second factory retains the messengerPageId: body");
        a.Dispose();
        b.Dispose();
    }

    public void TestFailableInitReturnsFalseForEmptyPermissions()
    {
        // Both inits `guard !permissions.isEmpty else { return nil }` — the failable path must surface as
        // a false return + null out on BOTH recovered factories.
        var ok1 = LabeledLoginConfig.TryCreate(System.Array.Empty<string>(), LoginTrackingPref.Enabled, "n", out var a);
        AssertFalse(ok1, "plain TryCreate fails (init? returns nil) for empty permissions");
        AssertNull(a, "failed TryCreate yields a null result");

        var ok2 = LabeledLoginConfig.TryCreateWithMessengerPageId(System.Array.Empty<string>(), LoginTrackingPref.Enabled, "m", out var b);
        AssertFalse(ok2, "disambiguated factory fails (init? returns nil) for empty permissions");
        AssertNull(b, "failed TryCreateWithMessengerPageId yields a null result");
    }
}
