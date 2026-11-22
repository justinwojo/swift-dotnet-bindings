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

    public void TestFailableClass_RunsTheFavoredInit()
    {
        using var tip = new BasicPopoverTip("gated");
        AssertTrue(TipGateHost.TryCreate(tip, out var host), "init?(_: any PopoverTip, …) succeeded");
        using (host)
        {
            AssertEqual("any:gated", host.Route, "the favored failable init ran");
            AssertFalse(host.Anchored, "anchor defaulted to nil");
        }
    }

    public void TestFailableClass_ReportsSwiftNil()
    {
        using var tip = new BasicPopoverTip("");
        AssertFalse(TipGateHost.TryCreate(tip, out _), "init? returned nil for an empty title");
    }

    public void TestTrailingLineDefault_RunsTheFavoredInit()
    {
        using var tip = new BasicPopoverTip("lined");
        using var host = new TipLineHost(tip);
        AssertEqual("any:lined", host.Route, "init(_: any PopoverTip, anchor:line:) ran");
        AssertFalse(host.Anchored, "anchor defaulted to nil");
        AssertTrue(host.Line > 0, "Swift filled #line");
    }

    public void TestSwiftUITypedDefault_RunsTheFavoredInit()
    {
        using var tip = new BasicPopoverTip("edge");
        using var host = new TipEdgeHost(tip);
        AssertEqual("any:edge", host.Route, "init(_: any PopoverTip, arrowEdge:actionHandler:) ran");
        AssertFalse(host.Edged, "arrowEdge defaulted to nil");
    }

    public void TestSwiftUITypedDefault_TypeErasedAndManagedTips()
    {
        using var inner = new BasicPopoverTip("erased");
        using var erased = new AnyPopoverTip(inner);
        using var fromErased = new TipEdgeHost(erased);
        AssertEqual("any:erased", fromErased.Route, "the favored init ran for the type-erased tip");
        using var fromManaged = new TipEdgeHost(new ManagedTip());
        AssertEqual("any:managed", fromManaged.Route, "the favored init ran for a C# tip");
    }
}
