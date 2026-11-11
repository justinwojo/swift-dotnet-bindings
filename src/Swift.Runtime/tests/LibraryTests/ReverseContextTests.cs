// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ReverseContextCollection
{
    // Destroy counting keys on GCHandle values, which the runtime reuses once freed. Running
    // alone keeps another class's handles out of the count.
    public const string Name = "Reverse context destroy counting";
}

/// <summary>
/// The native reverse-callback context box: its layout contract, its ownership of the payload
/// <see cref="GCHandle"/>, and the mandatory first-use checks.
/// </summary>
[Collection(ReverseContextCollection.Name)]
public sealed unsafe class ReverseContextTests : IDisposable
{
    // The live counter for each payload this class has handed to a box. A freed handle value can be
    // reallocated; each allocation registers a fresh counter, so a reused value never inflates an
    // earlier box's count.
    private static readonly ConcurrentDictionary<IntPtr, StrongBox<int>> s_destroys = new();

    public ReverseContextTests() => SwiftClosureContext.ContextDestroyed += OnDestroyed;

    public void Dispose() => SwiftClosureContext.ContextDestroyed -= OnDestroyed;

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

    [Fact]
    public void LayoutExportReportsContractVersionOne()
    {
        var image = SwiftClosureContext.RegisteredImageHandle;
        Assert.NotEqual(IntPtr.Zero, image);
        Assert.True(NativeLibrary.TryGetExport(image, SwiftReverseContext.LayoutExport, out var layout));

        int version, targetOffset, payloadOffset;
        ((delegate* unmanaged[Cdecl]<int*, int*, int*, void>)layout)(&version, &targetOffset, &payloadOffset);

        Assert.Equal(1, version);
        Assert.Equal(16, targetOffset);
        Assert.Equal(24, payloadOffset);
    }

    [Fact]
    public void SwiftCreatedBoxCarriesTargetAndPayloadAtContractOffsets()
    {
        var (payload, destroys) = AllocatePayload(new object());
        var box = SwiftReverseContext.Create(Target, payload);
        try
        {
            // These are the reads the native stub makes from the closure context register.
            Assert.Equal(Target, Marshal.ReadIntPtr(box, 16));
            Assert.Equal(payload, Marshal.ReadIntPtr(box, 24));
            Assert.Equal(1, (int)Arc.RetainCount(box));
            Assert.Equal(0, destroys.Value);
        }
        finally
        {
            SwiftReverseContext.Release(box);
        }
        Assert.Equal(1, destroys.Value);
    }

    [Fact]
    public void DestroyFiresExactlyOnceOnTheLastRelease()
    {
        var (payload, destroys) = AllocatePayload(new object());
        var box = SwiftReverseContext.Create(Target, payload);

        // A second owner, as when Swift stores the closure.
        Arc.Retain(box);
        Arc.Retain(box);
        Assert.Equal(3, (int)Arc.RetainCount(box));

        SwiftReverseContext.Release(box);
        Assert.Equal(0, destroys.Value);
        SwiftReverseContext.Release(box);
        Assert.Equal(0, destroys.Value);
        Assert.True(GCHandle.FromIntPtr(payload).IsAllocated);

        SwiftReverseContext.Release(box);
        Assert.Equal(1, destroys.Value);
    }

    [Fact]
    public void BoxKeepsItsTargetAliveUntilSwiftReleasesIt()
    {
        var (box, weak, destroys) = CreateBoxForUnreferencedTarget();

        CollectThoroughly();
        Assert.True(weak.IsAlive, "The managed target was collected while a Swift reference held its box.");
        Assert.Equal(0, destroys.Value);

        // Swift's last reference goes on another thread, as an escaping closure's often does.
        var releaser = new Thread(() => SwiftReverseContext.Release(box));
        releaser.Start();
        releaser.Join();
        Assert.Equal(1, destroys.Value);

        CollectThoroughly();
        Assert.False(weak.IsAlive, "The box was released but its managed target is still rooted.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IntPtr Box, WeakReference Weak, StrongBox<int> Destroys) CreateBoxForUnreferencedTarget()
    {
        Func<int> callback = new CallbackOwner().Read;
        var weak = new WeakReference(callback.Target);
        var (payload, destroys) = AllocatePayload(callback);
        return (SwiftReverseContext.Create(Target, payload), weak, destroys);
    }

    [Fact]
    public void ConcurrentCreationFromManyThreadsDestroysEachPayloadOnce()
    {
        const int threads = 16;
        const int boxesPerThread = 250;
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
        Assert.Empty(failures);
    }

    [Fact]
    public void MissingLayoutExportThrowsNamingTheExport()
    {
        // A real loaded image without the runtime's exports.
        var libSystem = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        var binding = SwiftReverseContext.BindImage(libSystem);

        var ex = Assert.Throws<InvalidOperationException>(() => binding.Create(Target, (IntPtr)1));
        Assert.Contains("'SwiftBindings_ReverseContextLayout' is missing", ex.Message);
        Assert.Contains("There is no fallback", ex.Message);
    }

    [Fact]
    public void MissingFactoryExportThrowsNamingTheExport()
    {
        var binding = SwiftReverseContext.BindExports(
            (IntPtr)(delegate* unmanaged[Cdecl]<int*, int*, int*, void>)&ReportContractLayout,
            IntPtr.Zero, "the test image");

        var ex = Assert.Throws<InvalidOperationException>(() => binding.Create(Target, (IntPtr)1));
        Assert.Contains("'SwiftBindings_NewReverseContext' is missing from the test image", ex.Message);
    }

    [Theory]
    [InlineData(2, 16, 24, "version 2 (target +16, payload +24)")]
    [InlineData(1, 16, 32, "version 1 (target +16, payload +32)")]
    [InlineData(1, -1, 24, "version 1 (target not found, payload +24)")]
    public void SeededLayoutMismatchThrowsWithoutCreatingABox(int version, int target, int payload, string reported)
    {
        s_seededLayout = (version, target, payload);
        Interlocked.Exchange(ref s_seededFactoryCalls, 0);
        var binding = SwiftReverseContext.BindExports(
            (IntPtr)(delegate* unmanaged[Cdecl]<int*, int*, int*, void>)&ReportSeededLayout,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&CountingFactory,
            "the test image");

        var ex = Assert.Throws<InvalidOperationException>(() => binding.Create(Target, (IntPtr)1));
        Assert.Contains("layout mismatch in the test image", ex.Message);
        Assert.Contains("expects version 1 (target +16, payload +24)", ex.Message);
        Assert.Contains($"reports {reported}", ex.Message);
        Assert.Equal(0, Volatile.Read(ref s_seededFactoryCalls));
    }

    [Fact]
    public void MatchingSeededLayoutUsesTheFactory()
    {
        // Positive control for the seeded path: the same seam with the contract layout creates.
        s_seededLayout = (1, 16, 24);
        Interlocked.Exchange(ref s_seededFactoryCalls, 0);
        var binding = SwiftReverseContext.BindExports(
            (IntPtr)(delegate* unmanaged[Cdecl]<int*, int*, int*, void>)&ReportSeededLayout,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&CountingFactory,
            "the test image");

        Assert.Null(binding.Failure);
        Assert.Equal((IntPtr)0x5150, binding.Create(Target, (IntPtr)1));
        Assert.Equal(1, Volatile.Read(ref s_seededFactoryCalls));
    }

    [Fact]
    public void NullFactoryResultThrowsInsteadOfReturningZero()
    {
        var binding = SwiftReverseContext.BindExports(
            (IntPtr)(delegate* unmanaged[Cdecl]<int*, int*, int*, void>)&ReportContractLayout,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&NullFactory,
            "the test image");

        var ex = Assert.Throws<InvalidOperationException>(() => binding.Create(Target, (IntPtr)1));
        Assert.Contains("SwiftBindings_NewReverseContext returned null", ex.Message);
    }

    [Fact]
    public void ZeroTargetOrPayloadIsRejectedBeforeAnyNativeCall()
    {
        Assert.Throws<ArgumentException>("target", () => SwiftReverseContext.Create(IntPtr.Zero, (IntPtr)1));
        Assert.Throws<ArgumentException>("payload", () => SwiftReverseContext.Create(Target, IntPtr.Zero));
        SwiftReverseContext.Release(IntPtr.Zero);
    }

    private static (int Version, int Target, int Payload) s_seededLayout;
    private static int s_seededFactoryCalls;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReportSeededLayout(int* version, int* target, int* payload)
    {
        *version = s_seededLayout.Version;
        *target = s_seededLayout.Target;
        *payload = s_seededLayout.Payload;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReportContractLayout(int* version, int* target, int* payload)
    {
        *version = 1;
        *target = 16;
        *payload = 24;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IntPtr CountingFactory(IntPtr target, IntPtr payload)
    {
        Interlocked.Increment(ref s_seededFactoryCalls);
        return 0x5150;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IntPtr NullFactory(IntPtr target, IntPtr payload) => IntPtr.Zero;

    private static void CollectThoroughly()
    {
        var collector = new Thread(() =>
        {
            for (int i = 0; i < 6; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
        });
        collector.Start();
        collector.Join();
    }

    private sealed class CallbackOwner
    {
        public int Read() => 42;
    }
}
