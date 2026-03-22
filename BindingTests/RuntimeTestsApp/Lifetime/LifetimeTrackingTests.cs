// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Tests for TrackedObject, TrackedContainer, and ValuePoint lifecycle patterns.
/// Exercises allocation tracking, reference chains, closure capture, and value semantics.
/// </summary>
public class LifetimeTrackingTests : TestBase
{
    public LifetimeTrackingTests(TestResults results) : base(results) { }

    #region TrackedObject Construction

    [SkipOnSimulator("CreateTrackedObject(int) uses CallConvSwift (no @_cdecl wrapper)")]
    public void TestTrackedObjectCreation()
    {
        using var obj = TestLibFunctions.CreateTrackedObject(1);
        AssertEqual(1, obj.ObjectId, "ObjectId preserved after construction");
    }

    public void TestTrackedObjectWithLabel()
    {
        using var obj = TestLibFunctions.CreateTrackedObject(2, "custom");
        AssertEqual(2, obj.ObjectId, "ObjectId preserved");
        AssertEqual("custom", obj.Label.ToString(), "Label preserved");
    }

    [SkipOnSimulator("CreateTrackedObject(int) uses CallConvSwift (no @_cdecl wrapper)")]
    public void TestTrackedObjectIsAlive()
    {
        using var obj = TestLibFunctions.CreateTrackedObject(3);
        AssertTrue(obj.IsAlive(), "Object reports alive");
    }

    public void TestTrackedObjectDescribe()
    {
        using var obj = TestLibFunctions.CreateTrackedObject(4, "test");
        var desc = obj.GetDescribe();
        AssertTrue(desc.Contains("4"), "Describe contains object ID");
        AssertTrue(desc.Contains("test"), "Describe contains label");
    }

    #endregion

    #region TrackedContainer

    public void TestTrackedContainerEmpty()
    {
        using var container = TestLibFunctions.CreateTrackedContainer(10, null);
        AssertEqual(10, container.ContainerId, "ContainerId preserved");
        AssertFalse(container.HasChild(), "Empty container has no child");
    }

    public void TestTrackedContainerWithChild()
    {
        using var container = TestLibFunctions.CreateTrackedContainer(11, 42);
        AssertTrue(container.HasChild(), "Container has child after creation");
    }

    #endregion

    #region Reference Semantics

    [SkipOnSimulator("CreateTrackedObject(int) uses CallConvSwift (no @_cdecl wrapper)")]
    public void TestIdentityPreservesObject()
    {
        using var obj = TestLibFunctions.CreateTrackedObject(20);
        using var same = TestLibFunctions.Identity(obj);
        AssertEqual(20, same.ObjectId, "Identity returns same object ID");
    }

    public void TestCloneCreatesNewObject()
    {
        using var obj = TestLibFunctions.CreateTrackedObject(30, "original");
        using var cloned = TestLibFunctions.Clone(obj);
        AssertEqual(30, cloned.ObjectId, "Clone preserves object ID");
        AssertTrue(cloned.Label.Contains("clone"), "Clone label indicates copy");
    }

    #endregion

    #region ValuePoint (Frozen Struct Value Semantics)

    [SkipOnSimulator("CreateValuePoint uses CallConvSwift (no @_cdecl wrapper)")]
    public void TestValuePointCreation()
    {
        var pt = TestLibFunctions.CreateValuePoint(3, 4);
        AssertEqual(3, pt.X, "X preserved");
        AssertEqual(4, pt.Y, "Y preserved");
    }

    [SkipOnSimulator("ModifyValuePoint uses CallConvSwift (no @_cdecl wrapper)")]
    public void TestValuePointModifyCopy()
    {
        var original = TestLibFunctions.CreateValuePoint(10, 20);
        var modified = TestLibFunctions.ModifyValuePoint(original, 99);
        AssertEqual(99, modified.X, "Modified copy has new X");
        AssertEqual(20, modified.Y, "Modified copy preserves Y");
        AssertEqual(10, original.X, "Original X unchanged (value semantics)");
    }

    #endregion

    #region Closure Capture

    [Skip("Closure returns: non-blittable callback through CallConvSwift")]
    public void TestCapturingClosureRetainsObject()
    {
        // createCapturingClosure returns a closure that captures a TrackedObject
    }

    [Skip("Closure returns: non-blittable callback through CallConvSwift")]
    public void TestMutatingClosureCaptureLifetime()
    {
        // createMutatingClosure returns a closure that mutates captured state
    }

    #endregion

    #region Protocol Conformance

    [Skip("some Ownable: opaque existential parameter not yet supported")]
    public void TestOwnableProtocolConformance()
    {
        // getOwnerId accepts `some Ownable` — requires existential boxing
    }

    #endregion

    #region Async Ownership

    [Skip("Async free functions: _payload/this in static context (generator bug)")]
    public void TestAsyncCreateObject()
    {
        // asyncCreateObject — async ownership disabled in Swift source
    }

    [Skip("Async free functions: _payload/this in static context (generator bug)")]
    public void TestAsyncRoundTrip()
    {
        // asyncRoundTrip — async ownership disabled in Swift source
    }

    #endregion
}
