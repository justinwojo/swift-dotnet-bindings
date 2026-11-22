// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Initializers;

/// <summary>
/// <c>TipPopoverHost</c>'s favored <c>init(_: any PopoverTip, anchor:actionHandler:)</c> binds with
/// its unbindable defaults trimmed, so the type is constructible; the <c>some PopoverTip</c> twin's
/// specializations, whose calls Swift resolves to the favored declaration, are refused rather than
/// bound to it. The route each initializer records proves the favored one ran, for a Swift
/// conformer, the type-erased conformer and a C# implementation, and that Swift filled the
/// defaults (no anchor).
/// </summary>
public class FavoredExistentialInitTests : TestBase
{
    public FavoredExistentialInitTests(TestResults results) : base(results) { }

    sealed class ManagedTip : IPopoverTip
    {
        public string Title => "managed";
    }

    public void TestSwiftConformer_RunsTheFavoredInit()
    {
        using var tip = new BasicPopoverTip("basic");
        using var host = new TipPopoverHost(tip);
        AssertEqual("any:basic", host.Route, "init(_: any PopoverTip, …) ran");
        AssertFalse(host.Anchored, "anchor defaulted to nil");
    }

    public void TestTypeErasedConformer_RunsTheFavoredInit()
    {
        using var inner = new BasicPopoverTip("erased");
        using var tip = new AnyPopoverTip(inner);
        using var host = new TipPopoverHost(tip);
        AssertEqual("any:erased", host.Route, "init(_: any PopoverTip, …) ran for the type-erased tip");
        AssertFalse(host.Anchored, "anchor defaulted to nil");
    }

    public void TestManagedConformer_RunsTheFavoredInit()
    {
        using var host = new TipPopoverHost(new ManagedTip());
        AssertEqual("any:managed", host.Route, "init(_: any PopoverTip, …) ran for a C# tip");
    }
}
