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

    public void TestValuePointCreation()
    {
        var pt = TestLibFunctions.CreateValuePoint(3, 4);
        AssertEqual(3, pt.X, "X preserved");
        AssertEqual(4, pt.Y, "Y preserved");
    }

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

    public void TestCapturingClosureRetainsObject()
    {
        // createCapturingClosure captures a TrackedObject(objectId) and returns its objectId
        var closure = TestLibFunctions.CreateCapturingClosure(objectId: 99);
        AssertNotNull(closure, "CreateCapturingClosure returned non-null");
        var result = closure();
        AssertEqual(99, result, "Captured closure returns objectId 99");
        // Call again to verify closure is stable
        var result2 = closure();
        AssertEqual(99, result2, "Second invocation still returns 99");
        TestLogger.Info($"CreateCapturingClosure(99)() = {result}");
    }

    public void TestMutatingClosureCaptureLifetime()
    {
        // createMutatingClosure captures a TrackedObject + callCount, returns incremented callCount
        var closure = TestLibFunctions.CreateMutatingClosure(objectId: 42);
        AssertNotNull(closure, "CreateMutatingClosure returned non-null");
        var call1 = closure();
        AssertEqual(1, call1, "First call returns count 1");
        var call2 = closure();
        AssertEqual(2, call2, "Second call returns count 2");
        var call3 = closure();
        AssertEqual(3, call3, "Third call returns count 3");
        TestLogger.Info($"CreateMutatingClosure: call counts = {call1}, {call2}, {call3}");
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
