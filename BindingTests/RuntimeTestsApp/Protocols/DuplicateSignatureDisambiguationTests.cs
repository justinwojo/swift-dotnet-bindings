// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Regression tests for the DuplicateSignature label-only-overload drop.
///
/// <para>
/// A Cocoa-style delegate declares requirements that share a base name and identical
/// parameter types, differing only by argument label —
/// <c>conversationManager(_:didActivate:)</c> / <c>conversationManager(_:didDeactivate:)</c>
/// (the LCK shape) and <c>captureSession(_:didAdd:)</c> / <c>didChange:</c> / <c>didUpdate:</c>
/// (the RoomPlan shape). They all erase to one C# overload signature once labels are dropped,
/// so the generator previously kept only the first and silently dropped the rest.
/// </para>
///
/// <para>
/// The generator now disambiguates collided projections with ObjC-selector-style names built
/// from the Swift labels. These tests implement the protocols in C#, let a Swift harness call
/// each requirement, and assert (a) every member survives as a distinct C# method, (b) the
/// correct member fired (recorded tag), and (c) the per-method return value round-trips back
/// through Swift — proving each disambiguated member maps to its own reverse-dispatch slot, not
/// a collapsed/mis-routed one.
/// </para>
/// </summary>
public class DuplicateSignatureDisambiguationTests : TestBase
{
    public DuplicateSignatureDisambiguationTests(TestResults results) : base(results) { }

    /// <summary>
    /// LCK pair: drive both label-distinct overloads and assert each routes to its own
    /// disambiguated member with its own return value.
    /// </summary>
    public void TestConversationManagerLabelPairRoundTrips()
    {
        var impl = new ConversationDelegateImpl();
        var harness = new ConversationManagerHarness();

        int activated = harness.Activate(impl, manager: 7, session: 5);
        AssertEqual("activate", impl.LastTag, "didActivate member fired for the activate path");
        AssertEqual(7, impl.LastManager, "didActivate received the manager argument");
        AssertEqual(5, impl.LastSession, "didActivate received the session argument");
        AssertEqual(10, activated, "didActivate return value (session * 2) round-tripped through Swift");

        int deactivated = harness.Deactivate(impl, manager: 3, session: 9);
        AssertEqual("deactivate", impl.LastTag, "didDeactivate member fired for the deactivate path");
        AssertEqual(3, impl.LastManager, "didDeactivate received the manager argument");
        AssertEqual(9, impl.LastSession, "didDeactivate received the session argument");
        AssertEqual(27, deactivated, "didDeactivate return value (session * 3) round-tripped through Swift");
    }

    /// <summary>
    /// RoomPlan triple: all three label-only overloads must survive and route independently.
    /// </summary>
    public void TestCaptureSessionLabelTripleRoundTrips()
    {
        var impl = new CaptureSessionObserverImpl();
        var harness = new CaptureSessionHarness();

        int added = harness.Add(impl, session: 1, value: 4);
        AssertEqual("add", impl.LastTag, "didAdd member fired for the add path");
        AssertEqual(4, impl.LastValue, "didAdd received the value argument");
        AssertEqual(40, added, "didAdd return value (value * 10) round-tripped through Swift");

        int changed = harness.Change(impl, session: 1, value: 4);
        AssertEqual("change", impl.LastTag, "didChange member fired for the change path");
        AssertEqual(80, changed, "didChange return value (value * 20) round-tripped through Swift");

        int updated = harness.Update(impl, session: 1, value: 4);
        AssertEqual("update", impl.LastTag, "didUpdate member fired for the update path");
        AssertEqual(120, updated, "didUpdate return value (value * 30) round-tripped through Swift");
    }
}

/// <summary>
/// C# conformance to the LCK-shape delegate. The two requirements collide on the
/// label-erased projection, so the generated interface must expose them under the
/// disambiguated names <c>ConversationManagerDidActivate</c> /
/// <c>ConversationManagerDidDeactivate</c> — this class fails to compile if either is
/// dropped or mis-named.
/// </summary>
internal sealed class ConversationDelegateImpl : IConversationManagerDelegate
{
    public string LastTag { get; private set; } = "";
    public int LastManager { get; private set; }
    public int LastSession { get; private set; }

    public int ConversationManagerDidActivate(int manager, int session)
    {
        LastTag = "activate";
        LastManager = manager;
        LastSession = session;
        return session * 2;
    }

    public int ConversationManagerDidDeactivate(int manager, int session)
    {
        LastTag = "deactivate";
        LastManager = manager;
        LastSession = session;
        return session * 3;
    }
}

/// <summary>
/// C# conformance to the RoomPlan-shape observer — three disambiguated members.
/// </summary>
internal sealed class CaptureSessionObserverImpl : ICaptureSessionObserver
{
    public string LastTag { get; private set; } = "";
    public int LastValue { get; private set; }

    public int CaptureSessionDidAdd(int session, int value)
    {
        LastTag = "add";
        LastValue = value;
        return value * 10;
    }

    public int CaptureSessionDidChange(int session, int value)
    {
        LastTag = "change";
        LastValue = value;
        return value * 20;
    }

    public int CaptureSessionDidUpdate(int session, int value)
    {
        LastTag = "update";
        LastValue = value;
        return value * 30;
    }
}
