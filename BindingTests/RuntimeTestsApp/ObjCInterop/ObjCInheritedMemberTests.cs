// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ObjCInterop;

/// <summary>
/// NSObject subclasses whose Swift overrides meet NSObject's own C# surface. The binding must
/// override NSObject's virtual members rather than hide them, so a caller holding the object as
/// <see cref="NSObject"/> (string interpolation, <c>ToString()</c>, KVO plumbing) reaches the
/// Swift implementation. Fixture: ObjCInterop/ObjCInheritedMembers.swift.
/// </summary>
public class ObjCInheritedMemberTests : TestBase
{
    public ObjCInheritedMemberTests(TestResults results) : base(results) { }

    public void TestDescriptionOverrideReachesSwift()
    {
        using var value = new InheritedDescriptionObject("alpha");
        AssertEqual("described:alpha", value.Description, "Description through the binding type");
        AssertEqual("described:alpha", ((NSObject)value).Description, "Description through NSObject");
        AssertEqual("described:alpha", value.ToString(), "ToString returns the Swift description");
        AssertEqual("described:alpha", ((object)value).ToString(), "ToString through object");
        AssertEqual("<described:alpha>", $"<{value}>", "String interpolation uses the Swift description");
    }

    public void TestDebugDescriptionOverrideReachesSwift()
    {
        using var value = new InheritedDescriptionObject("beta");
        AssertEqual("debug-described:beta", value.DebugDescription, "DebugDescription through the binding type");
        AssertEqual("debug-described:beta", ((NSObject)value).DebugDescription, "DebugDescription through NSObject");
    }

    public void TestSecondLevelDescriptionOverride()
    {
        using var child = new InheritedDescriptionChild("gamma");
        AssertEqual("child:gamma", child.Description, "Child Description");
        AssertEqual("child:gamma", ((InheritedDescriptionObject)child).Description, "Child Description through its base");
        AssertEqual("child:gamma", ((NSObject)child).Description, "Child Description through NSObject");
        AssertEqual("child:gamma", ((NSObject)child).ToString(), "Child ToString through NSObject");
        AssertEqual("debug-described:gamma", child.DebugDescription, "Child inherits the base DebugDescription");
    }

    public void TestHashAndIsEqualReachSwift()
    {
        using var a = new InheritedDescriptionObject("delta");
        using var b = new InheritedDescriptionObject("delta");
        using var c = new InheritedDescriptionObject("epsilon");
        AssertEqual(5, a.Hash, "Hash is the Swift override (name length)");
        AssertTrue(a.IsEqual(b), "IsEqual compares names in Swift");
        AssertFalse(a.IsEqual(c), "IsEqual distinguishes names");
        AssertTrue(a.Equals(b), "Equals follows Swift's isEqual(_:)");
        AssertFalse(a.Equals(c), "Equals distinguishes names");
        AssertEqual(a.GetHashCode(), b.GetHashCode(), "Equal values hash alike");
        var set = new HashSet<InheritedDescriptionObject> { a, b, c };
        AssertEqual(2, set.Count, "A HashSet merges the two equal values");
    }

    public void TestDisposedObjectHashesWithoutItsNativePeer()
    {
        var disposed = new InheritedDescriptionObject("zeta");
        disposed.Dispose();
        AssertEqual(disposed.GetHashCode(), disposed.GetHashCode(), "A disposed object hashes stably without reaching Swift");
    }

    public void TestOverriddenNSObjectMethodDispatchesThroughNSObject()
    {
        using var hook = new InheritedHookObject(3);
        var before = TestLibFunctions.GetInheritedKeyChangeCount();
        hook.DidChangeValue("direct");
        AssertEqual(before + 1, TestLibFunctions.GetInheritedKeyChangeCount(), "Direct call reaches the Swift override");
        AssertEqual("direct", TestLibFunctions.GetInheritedLastChangedKey(), "Direct call passes its key");
        ((NSObject)hook).DidChangeValue("through-base");
        AssertEqual(before + 2, TestLibFunctions.GetInheritedKeyChangeCount(), "NSObject-typed call reaches the Swift override");
        AssertEqual("through-base", TestLibFunctions.GetInheritedLastChangedKey(), "NSObject-typed call passes its key");
    }

    public void TestMembersRenamedAwayFromNSObjectSurface()
    {
        using var hook = new InheritedHookObject(7);
        AssertEqual(7, hook.HandleValue, "Swift `handle` reads as HandleValue");
        AssertTrue(hook.Handle != IntPtr.Zero, "NSObject.Handle stays the native handle");
        hook.HandleValue = 10;
        AssertEqual(10, hook.HandleValue, "HandleValue round-trips");
        AssertEqual(11, hook.GetMarkDirty(), "Swift markDirty() runs under its own name");
        AssertEqual(11, hook.HandleValue, "markDirty() mutated the Swift property");
    }

    public void TestOverrideOfRenamedPropertyKeepsTheRenamedName()
    {
        using var child = new InheritedHookChild(5);
        AssertEqual(105, child.HandleValue, "The Swift override's getter runs");
        InheritedHookObject asBase = child;
        AssertEqual(105, asBase.HandleValue, "A base-typed read reaches the Swift override");
        asBase.HandleValue = 20;
        AssertEqual(120, child.HandleValue, "The override's setter stores through super");
        AssertTrue(child.Handle != IntPtr.Zero, "NSObject.Handle stays the native handle");
    }

    public void TestRenamedPropertyImplementsTheProtocolRequirement()
    {
        using var carrier = new InheritedHandleCarrier(9);
        AssertEqual(9, carrier.SuperHandleValue, "Swift `superHandle` reads as SuperHandleValue");
        ISuperHandleCarrying asProtocol = carrier;
        AssertEqual(9, asProtocol.SuperHandle, "The interface member forwards to the renamed property");
        AssertTrue(carrier.Handle != IntPtr.Zero, "NSObject.Handle stays the native handle");
    }
}
