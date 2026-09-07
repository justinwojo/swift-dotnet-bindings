// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.FoundationInterop;

/// <summary>
/// End-to-end coverage for the runtime's hand-written <see cref="Swift.DispatchQueue"/> projection
/// crossing into a generated binding. The queue is an Objective-C object marshalled by the class
/// convention, so these tests check the three things a unit test on the host cannot: that the
/// queue C# obtains from the runtime is the same object Swift resolves (identity, not label), that a
/// queue a Swift call returns is adopted as a working wrapper, and that a queue with a real reference
/// count survives the round trip and is released through the wrapper without corrupting it.
/// </summary>
public class DispatchQueueInteropTests : TestBase
{
    public DispatchQueueInteropTests(TestResults results) : base(results) { }

    public void TestMain_IsTheQueueSwiftCallsMain()
    {
        using var main = Swift.DispatchQueue.Main;
        AssertTrue(DispatchQueueInteropProbe.IsMainQueue(main), "runtime Main is DispatchQueue.main on the Swift side");
        AssertEqual("com.apple.main-thread", main.Label, "main queue label read through the runtime");
        AssertEqual(main.Label, DispatchQueueInteropProbe.Label(main), "Swift reads the same label off the marshalled queue");
    }

    public void TestGlobal_DefaultQosIsSwiftsDefaultGlobalQueue()
    {
        using var global = Swift.DispatchQueue.Global();
        AssertTrue(DispatchQueueInteropProbe.IsGlobalQueue(global, (uint)Swift.DispatchQoSClass.Default), "Global() is DispatchQueue.global()");
        AssertEqual(DispatchQueueInteropProbe.Label(global), global.Label, "label agrees across the boundary");
    }

    public void TestGlobal_EachQosClassResolvesItsOwnQueue()
    {
        foreach (var qos in new[]
                 {
                     Swift.DispatchQoSClass.Background,
                     Swift.DispatchQoSClass.Utility,
                     Swift.DispatchQoSClass.Default,
                     Swift.DispatchQoSClass.UserInitiated,
                     Swift.DispatchQoSClass.UserInteractive,
                 })
        {
            using var queue = Swift.DispatchQueue.Global(qos);
            AssertTrue(DispatchQueueInteropProbe.IsGlobalQueue(queue, (uint)qos), $"Global({qos}) is DispatchQueue.global(qos: {qos})");
        }
    }

    public void TestReturnedMainQueue_IsAdoptedAsTheSameObject()
    {
        using var fromSwift = DispatchQueueInteropProbe.GetMainQueue();
        using var fromRuntime = Swift.DispatchQueue.Main;
        AssertTrue(DispatchQueueInteropProbe.IsMainQueue(fromSwift), "queue returned by Swift is still the main queue after adoption");
        AssertEqual(fromRuntime.Payload.DangerousGetHandle(), fromSwift.Payload.DangerousGetHandle(), "both wrappers hold the same object pointer");
    }

    public void TestReturnedGlobalQueue_IsSchedulable()
    {
        using var global = DispatchQueueInteropProbe.GetDefaultGlobalQueue();
        AssertTrue(DispatchQueueInteropProbe.IsGlobalQueue(global, (uint)Swift.DispatchQoSClass.Default), "returned queue is the default global queue");
        AssertEqual((nint)84, DispatchQueueInteropProbe.RunSync(global, 42), "a block scheduled on the marshalled queue runs");
    }

    public void TestSerialQueue_AdoptedReleasedAndSchedulable()
    {
        // A custom queue has a real reference count: the wrapper adopts Swift's +1 and its Dispose
        // must release exactly that one count through the Objective-C-safe path. Repeating it
        // surfaces an over-release or a destroy-as-buffer as a crash rather than a leak.
        for (int i = 0; i < 64; i++)
        {
            var label = $"com.swiftbindings.tests.serial.{i}";
            using var queue = DispatchQueueInteropProbe.MakeSerialQueue(label);
            AssertEqual(label, queue.Label, "custom queue label survives the round trip");
            AssertEqual((nint)(2 * i), DispatchQueueInteropProbe.RunSync(queue, (nint)i), "block runs on the custom queue");
        }
    }

    public void TestSerialQueue_FinalizerReleasesWithoutDisposal()
    {
        for (int i = 0; i < 32; i++)
        {
            var queue = DispatchQueueInteropProbe.MakeSerialQueue($"com.swiftbindings.tests.finalized.{i}");
            AssertTrue(queue.Label.Length > 0, "queue is live before being dropped");
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        AssertTrue(true, "undisposed queue wrappers finalized without a crash");
    }

    public void TestDisposedQueue_RejectsFurtherUse()
    {
        var queue = Swift.DispatchQueue.Global(Swift.DispatchQoSClass.Utility);
        queue.Dispose();
        queue.Dispose();
        AssertThrows<ObjectDisposedException>(() => _ = queue.Label, "Label after Dispose throws");
        AssertThrows<ObjectDisposedException>(() => DispatchQueueInteropProbe.Label(queue), "marshalling a disposed queue throws instead of passing a released pointer");
    }
}
