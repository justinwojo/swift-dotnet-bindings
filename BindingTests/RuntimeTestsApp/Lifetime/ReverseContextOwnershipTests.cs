// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// The reverse-callback context box in the platform's own SwiftBindingsRuntime slice: the
/// layout a native stub reads, and the box's ownership of the delegate's <see cref="GCHandle"/>.
/// </summary>
public unsafe class ReverseContextOwnershipTests : TestBase
{
    // One counter per payload allocation. A freed handle value can be reallocated; registering a
    // fresh counter for each allocation keeps a reused value from inflating an earlier box's count.
    private static readonly ConcurrentDictionary<IntPtr, StrongBox<int>> s_destroys = new();
    private static int s_observerInstalled;

    public ReverseContextOwnershipTests(TestResults results) : base(results)
    {
        if (Interlocked.Exchange(ref s_observerInstalled, 1) == 0)
            SwiftClosureContext.ContextDestroyed += OnDestroyed;
    }

    private static void OnDestroyed(IntPtr payload)
    {
        if (s_destroys.TryGetValue(payload, out var count))
            Interlocked.Increment(ref count.Value);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void TargetCallback(IntPtr payload) { }

    private static IntPtr Target => (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&TargetCallback;

    private static (IntPtr Payload, StrongBox<int> Destroys) AllocatePayload(object value)
    {
        var payload = GCHandle.ToIntPtr(GCHandle.Alloc(value));
        var destroys = new StrongBox<int>();
        s_destroys[payload] = destroys;
        return (payload, destroys);
    }

    public void TestLayoutExportAndSwiftCreatedBoxAgreeOnContractOffsets()
    {
        var image = SwiftClosureContext.RegisteredImageHandle;
        AssertTrue(image != IntPtr.Zero,
            $"No registered SwiftBindingsRuntime image ({SwiftClosureContext.Status}: {SwiftClosureContext.RegistrationDiagnostic}).");
        AssertTrue(NativeLibrary.TryGetExport(image, SwiftReverseContext.LayoutExport, out var layout),
            "The registered SwiftBindingsRuntime image does not export SwiftBindings_ReverseContextLayout.");
        int version, targetOffset, payloadOffset;
        ((delegate* unmanaged[Cdecl]<int*, int*, int*, void>)layout)(&version, &targetOffset, &payloadOffset);
        AssertEqual(1, version, "layout version");
        AssertEqual(16, targetOffset, "target offset");
        AssertEqual(24, payloadOffset, "payload offset");

        var (payload, destroys) = AllocatePayload(new object());
        var box = SwiftReverseContext.Create(Target, payload);
        try
        {
            AssertEqual(Target, Marshal.ReadIntPtr(box, 16), "target read at +16");
            AssertEqual(payload, Marshal.ReadIntPtr(box, 24), "payload read at +24");
        }
        finally
        {
            SwiftReverseContext.Release(box);
        }
        AssertEqual(1, destroys.Value, "destroy count after the only release");
    }

    public void TestDestroyFiresExactlyOnceOnTheLastRelease()
    {
        var (payload, destroys) = AllocatePayload(new object());
        var box = SwiftReverseContext.Create(Target, payload);
        Arc.Retain(box);

        SwiftReverseContext.Release(box);
        AssertEqual(0, destroys.Value, "destroy count while a second reference remains");
        SwiftReverseContext.Release(box);
        AssertEqual(1, destroys.Value, "destroy count after the last release");
    }

    public void TestBoxKeepsItsTargetAliveUntilSwiftReleasesIt()
    {
        var (box, weak, destroys) = RunOnFinishedThread(CreateBoxForUnreferencedTarget);

        ForceGCThorough();
        AssertTrue(weak.IsAlive, "The managed target was collected while a Swift reference held its box.");
        AssertEqual(0, destroys.Value, "destroy count while Swift holds the box");

        RunOnFinishedThread(() => SwiftReverseContext.Release(box));
        AssertEqual(1, destroys.Value, "destroy count after Swift's release");

        ForceGCThorough();
        AssertFalse(weak.IsAlive, "The box was released but its managed target is still rooted.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IntPtr Box, WeakReference Weak, StrongBox<int> Destroys) CreateBoxForUnreferencedTarget()
    {
        Func<int> callback = new CallbackOwner().Read;
        var weak = new WeakReference(callback.Target);
        var (payload, destroys) = AllocatePayload(callback);
        return (SwiftReverseContext.Create(Target, payload), weak, destroys);
    }

    public void TestConcurrentCreationFromManyThreadsDestroysEachPayloadOnce()
    {
        const int threads = 8;
        const int boxesPerThread = 200;
        var failures = new ConcurrentQueue<string>();
        using var start = new Barrier(threads);
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            start.SignalAndWait();
            try
            {
                for (int i = 0; i < boxesPerThread; i++)
                {
                    var (payload, destroys) = AllocatePayload(new object());
                    var box = SwiftReverseContext.Create(Target, payload);
                    if (Marshal.ReadIntPtr(box, 16) != Target || Marshal.ReadIntPtr(box, 24) != payload)
                        failures.Enqueue($"thread {t} box {i}: fields do not match the values it was created with");
                    SwiftReverseContext.Release(box);
                    if (Volatile.Read(ref destroys.Value) != 1)
                        failures.Enqueue($"thread {t} box {i}: destroyed {destroys.Value} times");
                }
            }
            catch (Exception ex)
            {
                failures.Enqueue($"thread {t}: {ex}");
            }
        })).ToList();

        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
        AssertTrue(failures.IsEmpty, string.Join("; ", failures.Take(5)));
    }

    private sealed class CallbackOwner
    {
        public int Read() => 42;
    }
}
