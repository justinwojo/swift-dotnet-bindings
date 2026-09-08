// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class ClosureContextTests
{
    [Fact]
    public unsafe void RegisteredFactoryIsProcessVisibleBeforeAnotherLibraryLoad()
    {
        // The test project copies the real native runtime. Module initialization
        // must register and expose its factory before any test-side load occurs.
        Assert.True(SwiftClosureContext.Status == SwiftClosureContext.OwnerTokenStatus.Ready,
            SwiftClosureContext.RegistrationDiagnostic);
        fixed (byte* symbol = "SwiftBindings_NewClosureContext\0"u8)
        {
            var factory = Dlsym(new IntPtr(-2), symbol);
            Assert.NotEqual(IntPtr.Zero, factory);
            Assert.Equal(SwiftClosureContext.FactoryAddress, factory);
            SwiftClosureContext.EnsureRegistered();
            Assert.Equal(factory, SwiftClosureContext.FactoryAddress);
        }
    }

    [Fact]
    public void RegisteredBoxUnboxesOriginalContextAndReleasesItsManagedRoot()
    {
        var weak = AllocateAndRelease();
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
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateAndRelease()
    {
        var target = new object();
        var weak = new WeakReference(target);
        var handle = GCHandle.Alloc(target);
        var context = GCHandle.ToIntPtr(handle);
        var box = SwiftClosureContext.TryAllocateBox(context);
        if (box == IntPtr.Zero)
        {
            handle.Free();
            Assert.Fail(SwiftClosureContext.RegistrationDiagnostic ?? "Native owner factory unavailable");
        }
        try
        {
            Assert.Equal(context, SwiftClosureContext.GetCtx(box));
        }
        finally
        {
            // The box's registered native destroy callback owns the handle.
            SwiftClosureContext.ReleaseBox(box);
        }
        return weak;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlsym", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe IntPtr Dlsym(IntPtr handle, byte* symbol);
}
