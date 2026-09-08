// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;

namespace RuntimeTestsApp.Lifetime;

/// <summary>Observes the injected runtime's closure-owner factory and destroy registration.</summary>
public class ClosureOwnerTokenCapabilityTests : TestBase
{
    public ClosureOwnerTokenCapabilityTests(TestResults results) : base(results) { }

    public void TestNativeClosureOwnerFactoryUnboxingAndDestroyRegistration()
    {
        var target = RunOnFinishedThread(AllocateObserveAndRelease);
        ForceGCThorough();
        AssertTrue(!target.IsAlive,
            "The native box was released but its managed target remains rooted: " +
            "the registered closure-owner destroy callback was not observed.");
        Console.WriteLine("CLOSURE_OWNER_CAPABILITY: factory=yes; unbox=yes; registered-destroy=observed");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateObserveAndRelease()
    {
        SwiftClosureContext.EnsureRegistered();
        var target = new Target();
        Func<int> callback = target.Read;
        var weak = new WeakReference(target);
        var handle = GCHandle.Alloc(callback);
        var box = SwiftClosureMarshaller.TryAllocateBoxedContext(GCHandle.ToIntPtr(handle));
        if (box == IntPtr.Zero)
        {
            handle.Free();
            throw new InvalidOperationException(
                "Nuke's injected SwiftBindingsRuntime closure-owner factory is unavailable. " +
                "This lane cannot qualify closure-owner lifetime behavior.");
        }

        try
        {
            var unboxed = SwiftClosureMarshaller.GetDelegateFromBoxedContext<Func<int>>(box);
            if (!ReferenceEquals(callback, unboxed) || unboxed() != 73)
                throw new InvalidOperationException("Native closure-owner box did not preserve its managed context.");
        }
        finally
        {
            // The registered native deinit callback owns the GCHandle once a box exists.
            // Never free the stale local handle after release, including on an assertion failure.
            SwiftClosureMarshaller.ReleaseBoxedContext(box);
        }
        return weak;
    }

    private sealed class Target
    {
        public int Read() => 73;
    }
}
