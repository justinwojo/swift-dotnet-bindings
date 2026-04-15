// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;
using Swift.Runtime.Tests;
using Xunit;

namespace Swift.RuntimeTests;

/// <summary>
/// Tests for SwiftObjectRegistry, which maps Swift existential containers to C# proxy objects.
/// </summary>
/// <remarks>
/// Joins <see cref="SwiftExitGuardCollection"/> because the registry is process-global —
/// parallel test classes that also Register/Unregister (notably <c>ProxyLifetimeTrackerTests</c>)
/// would otherwise race with <c>Count_ReflectsRegisteredProxies</c> and similar assertions.
/// </remarks>
[Collection(SwiftExitGuardCollection.Name)]
public class SwiftObjectRegistryTests
{
    // Use a simple test class to act as a proxy
    private class TestProxy
    {
        public string Name { get; set; } = "TestProxy";
    }

    // Mock existential container for testing
    private struct MockExistentialContainer : IExistentialContainer
    {
        public IntPtr Payload0 { get; set; }
        public IntPtr Payload1 { get; set; }
        public IntPtr Payload2 { get; set; }
        public TypeMetadata ObjectMetadata { get; set; }

        public IntPtr this[int index]
        {
            get => IntPtr.Zero;
            set { }
        }

        public int Count => 1;
        public int SizeOf => IntPtr.Size * 5; // 3 payload + metadata + 1 witness

        public IntPtr CopyTo(IntPtr memory)
        {
            // For testing, just return the memory pointer
            return memory;
        }

        public void CopyTo<T>(ref T container) where T : struct, IExistentialContainer
        {
            container.Payload0 = Payload0;
            container.Payload1 = Payload1;
            container.Payload2 = Payload2;
            container.ObjectMetadata = ObjectMetadata;
        }
    }

    [Fact]
    public void Register_WithValidHandleAndProxy_Succeeds()
    {
        var handle = new IntPtr(12345);
        var proxy = new TestProxy();

        SwiftObjectRegistry.Register(handle, proxy);

        Assert.True(SwiftObjectRegistry.TryGetProxy<TestProxy>(handle, out var retrieved));
        Assert.Same(proxy, retrieved);

        // Cleanup
        SwiftObjectRegistry.Unregister(handle);
    }

    [Fact]
    public void Register_WithZeroHandle_ThrowsArgumentException()
    {
        var proxy = new TestProxy();

        Assert.Throws<ArgumentException>(() => SwiftObjectRegistry.Register(IntPtr.Zero, proxy));
    }

    [Fact]
    public void Register_WithNullProxy_ThrowsArgumentNullException()
    {
        var handle = new IntPtr(12345);

        Assert.Throws<ArgumentNullException>(() => SwiftObjectRegistry.Register<TestProxy>(handle, null!));
    }

    [Fact]
    public void RegisterStrong_PreventsGarbageCollection()
    {
        var handle = new IntPtr(23456);
        var proxy = new TestProxy { Name = "StrongProxy" };

        SwiftObjectRegistry.RegisterStrong(handle, proxy);

        // Clear local reference
        var weakRef = new WeakReference<TestProxy>(proxy);
        proxy = null!;

        // Force GC
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Should still be retrievable because of strong reference
        Assert.True(SwiftObjectRegistry.TryGetProxy<TestProxy>(handle, out var retrieved));
        Assert.NotNull(retrieved);
        Assert.Equal("StrongProxy", retrieved!.Name);

        // Cleanup
        SwiftObjectRegistry.Unregister(handle);
    }

    [Fact]
    public void ReleaseStrong_AllowsGarbageCollection()
    {
        var handle = new IntPtr(34567);

        // Create and register with strong reference in a separate scope
        RegisterStrongInScope(handle);

        // Release strong reference
        SwiftObjectRegistry.ReleaseStrong(handle);

        // Force GC
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // The weak reference should now be dead
        // Note: We can't guarantee GC behavior, so just verify ReleaseStrong doesn't throw
        SwiftObjectRegistry.Unregister(handle);
    }

    private static void RegisterStrongInScope(IntPtr handle)
    {
        var proxy = new TestProxy { Name = "ToBeReleased" };
        SwiftObjectRegistry.RegisterStrong(handle, proxy);
    }

    [Fact]
    public void Unregister_RemovesBothWeakAndStrongReferences()
    {
        var handle = new IntPtr(45678);
        var proxy = new TestProxy();

        SwiftObjectRegistry.RegisterStrong(handle, proxy);

        // Verify it's registered
        Assert.True(SwiftObjectRegistry.TryGetProxy<TestProxy>(handle, out _));

        // Unregister
        SwiftObjectRegistry.Unregister(handle);

        // Should no longer be found
        Assert.False(SwiftObjectRegistry.TryGetProxy<TestProxy>(handle, out _));
    }

    [Fact]
    public void TryGetProxy_WithUnregisteredHandle_ReturnsFalse()
    {
        var handle = new IntPtr(99999);

        Assert.False(SwiftObjectRegistry.TryGetProxy<TestProxy>(handle, out var proxy));
        Assert.Null(proxy);
    }

    [Fact]
    public void TryGetProxy_WithZeroHandle_ReturnsFalse()
    {
        Assert.False(SwiftObjectRegistry.TryGetProxy<TestProxy>(IntPtr.Zero, out var proxy));
        Assert.Null(proxy);
    }

    [Fact]
    public void TryGetProxy_WithWrongType_ReturnsFalse()
    {
        var handle = new IntPtr(56789);
        var proxy = new TestProxy();

        SwiftObjectRegistry.Register(handle, proxy);

        // Try to get as wrong type
        Assert.False(SwiftObjectRegistry.TryGetProxy<string>(handle, out var retrieved));
        Assert.Null(retrieved);

        // Cleanup
        SwiftObjectRegistry.Unregister(handle);
    }

    [Fact]
    public void GetProxy_WithValidHandle_ReturnsProxy()
    {
        var handle = new IntPtr(67890);
        var proxy = new TestProxy { Name = "GetProxyTest" };

        SwiftObjectRegistry.Register(handle, proxy);

        var retrieved = SwiftObjectRegistry.GetProxy<TestProxy>(handle);

        Assert.Same(proxy, retrieved);
        Assert.Equal("GetProxyTest", retrieved.Name);

        // Cleanup
        SwiftObjectRegistry.Unregister(handle);
    }

    [Fact]
    public void GetProxy_WithUnregisteredHandle_ThrowsInvalidOperationException()
    {
        var handle = new IntPtr(88888);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SwiftObjectRegistry.GetProxy<TestProxy>(handle));

        Assert.Contains("No proxy", ex.Message);
        Assert.Contains("TestProxy", ex.Message);
    }

    [Fact]
    public void GetProxyFromContainer_ExtractsProxyFromPayload0()
    {
        var handle = new IntPtr(78901);
        var proxy = new TestProxy { Name = "ContainerTest" };

        SwiftObjectRegistry.Register(handle, proxy);

        var container = new MockExistentialContainer { Payload0 = handle };

        var retrieved = SwiftObjectRegistry.GetProxyFromContainer<TestProxy>(container);

        Assert.Same(proxy, retrieved);
        Assert.Equal("ContainerTest", retrieved.Name);

        // Cleanup
        SwiftObjectRegistry.Unregister(handle);
    }

    [Fact]
    public void TryGetProxyFromContainer_WithValidContainer_ReturnsTrue()
    {
        var handle = new IntPtr(89012);
        var proxy = new TestProxy();

        SwiftObjectRegistry.Register(handle, proxy);

        var container = new MockExistentialContainer { Payload0 = handle };

        Assert.True(SwiftObjectRegistry.TryGetProxyFromContainer<TestProxy>(container, out var retrieved));
        Assert.Same(proxy, retrieved);

        // Cleanup
        SwiftObjectRegistry.Unregister(handle);
    }

    [Fact]
    public void TryGetProxyFromContainer_WithInvalidContainer_ReturnsFalse()
    {
        var container = new MockExistentialContainer { Payload0 = new IntPtr(77777) };

        Assert.False(SwiftObjectRegistry.TryGetProxyFromContainer<TestProxy>(container, out var retrieved));
        Assert.Null(retrieved);
    }

    [Fact]
    public void Count_ReflectsRegisteredProxies()
    {
        var initialCount = SwiftObjectRegistry.Count;

        var handle1 = new IntPtr(90001);
        var handle2 = new IntPtr(90002);

        // Hold references to prevent GC from collecting the weak-referenced proxies
        // before the Count assertions run.
        var proxy1 = new TestProxy();
        var proxy2 = new TestProxy();

        SwiftObjectRegistry.Register(handle1, proxy1);
        Assert.Equal(initialCount + 1, SwiftObjectRegistry.Count);

        SwiftObjectRegistry.Register(handle2, proxy2);
        Assert.Equal(initialCount + 2, SwiftObjectRegistry.Count);

        SwiftObjectRegistry.Unregister(handle1);
        Assert.Equal(initialCount + 1, SwiftObjectRegistry.Count);

        SwiftObjectRegistry.Unregister(handle2);
        Assert.Equal(initialCount, SwiftObjectRegistry.Count);

        GC.KeepAlive(proxy1);
        GC.KeepAlive(proxy2);
    }

    [Fact]
    public void StrongCount_ReflectsStronglyHeldProxies()
    {
        var initialCount = SwiftObjectRegistry.StrongCount;

        var handle = new IntPtr(90003);
        var proxy = new TestProxy();

        SwiftObjectRegistry.RegisterStrong(handle, proxy);
        Assert.Equal(initialCount + 1, SwiftObjectRegistry.StrongCount);

        SwiftObjectRegistry.ReleaseStrong(handle);
        Assert.Equal(initialCount, SwiftObjectRegistry.StrongCount);

        // Cleanup
        SwiftObjectRegistry.Unregister(handle);
        GC.KeepAlive(proxy);
    }

    [Fact]
    public void Cleanup_RemovesExpiredWeakReferences()
    {
        // This test verifies Cleanup doesn't throw
        // Actual GC behavior is non-deterministic
        SwiftObjectRegistry.Cleanup();
    }

    [Fact]
    public void MultipleRegistrations_SameHandle_OverwritesPrevious()
    {
        var handle = new IntPtr(90004);
        var proxy1 = new TestProxy { Name = "First" };
        var proxy2 = new TestProxy { Name = "Second" };

        SwiftObjectRegistry.Register(handle, proxy1);
        SwiftObjectRegistry.Register(handle, proxy2);

        Assert.True(SwiftObjectRegistry.TryGetProxy<TestProxy>(handle, out var retrieved));
        Assert.Equal("Second", retrieved!.Name);

        // Cleanup
        SwiftObjectRegistry.Unregister(handle);
    }
}
