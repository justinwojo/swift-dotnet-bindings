// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;
using Xunit;

namespace Swift.Runtime.Tests;

/// <summary>
/// Tests for SwiftDisposeScope — automatic batch disposal of Swift objects.
/// </summary>
public class SwiftDisposeScopeTests
{
    /// <summary>
    /// Mock ISwiftObject for testing scope tracking without real Swift objects.
    /// </summary>
    private sealed class MockSwiftObject : ISwiftObject, IDisposable
    {
        public bool IsDisposed { get; private set; }
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
            DisposeCount++;
        }

        // ISwiftObject stubs
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => throw new NotSupportedException();
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    [Fact]
    public void BasicScope_DisposesAllObjectsOnExit()
    {
        var obj1 = new MockSwiftObject();
        var obj2 = new MockSwiftObject();
        var obj3 = new MockSwiftObject();

        using (new SwiftDisposeScope())
        {
            SwiftDisposeScope.TryRegister(obj1);
            SwiftDisposeScope.TryRegister(obj2);
            SwiftDisposeScope.TryRegister(obj3);
        }

        Assert.True(obj1.IsDisposed);
        Assert.True(obj2.IsDisposed);
        Assert.True(obj3.IsDisposed);
    }

    [Fact]
    public void NestedScopes_InnerDisposesItsOwn_OuterDisposesItsOwn()
    {
        var outerObj = new MockSwiftObject();
        var innerObj = new MockSwiftObject();

        using (new SwiftDisposeScope())
        {
            SwiftDisposeScope.TryRegister(outerObj);

            using (new SwiftDisposeScope())
            {
                SwiftDisposeScope.TryRegister(innerObj);
            }

            // Inner scope has exited — innerObj should be disposed
            Assert.True(innerObj.IsDisposed);
            // Outer scope still active — outerObj should NOT be disposed yet
            Assert.False(outerObj.IsDisposed);
        }

        // Outer scope exited — outerObj should now be disposed
        Assert.True(outerObj.IsDisposed);
    }

    [Fact]
    public void DetachFromScope_ObjectSurvivesScopeExit()
    {
        var obj1 = new MockSwiftObject();
        var obj2 = new MockSwiftObject();

        using (new SwiftDisposeScope())
        {
            SwiftDisposeScope.TryRegister(obj1);
            SwiftDisposeScope.TryRegister(obj2);

            // Detach obj2 — it should survive scope exit
            obj2.DetachFromScope();
        }

        Assert.True(obj1.IsDisposed);
        Assert.False(obj2.IsDisposed); // Detached, not disposed
    }

    [Fact]
    public void DetachFromNestedScope_WalksChainToFindOwningScope()
    {
        var outerObj = new MockSwiftObject();

        using (new SwiftDisposeScope())
        {
            SwiftDisposeScope.TryRegister(outerObj);

            using (new SwiftDisposeScope())
            {
                // outerObj was registered in the outer scope.
                // Detaching while inner scope is active should walk the chain.
                var detached = SwiftDisposeScope.Detach(outerObj);
                Assert.True(detached);
            }
        }

        // outerObj was detached from the outer scope, so it should NOT be disposed
        Assert.False(outerObj.IsDisposed);
    }

    [Fact]
    public void MoveToParentScope_ObjectTransfersToParent()
    {
        var obj = new MockSwiftObject();

        using (var outer = new SwiftDisposeScope())
        {
            using (new SwiftDisposeScope())
            {
                SwiftDisposeScope.TryRegister(obj);
                obj.MoveToParentScope();
            }

            // Inner scope exited — obj was moved to outer, so NOT disposed yet
            Assert.False(obj.IsDisposed);
        }

        // Outer scope exited — obj should now be disposed
        Assert.True(obj.IsDisposed);
    }

    [Fact]
    public void MoveToParentFromNestedScope_WalksChainToFindOwningScope()
    {
        var obj = new MockSwiftObject();

        using (var grandparent = new SwiftDisposeScope())
        {
            using (var parent = new SwiftDisposeScope())
            {
                SwiftDisposeScope.TryRegister(obj);

                using (new SwiftDisposeScope())
                {
                    // obj is in the parent scope. MoveToParent while innermost is active
                    // should find it in parent and move to grandparent.
                    var moved = SwiftDisposeScope.MoveToParent(obj);
                    Assert.True(moved);
                }
            }

            // Parent scope exited — obj was moved to grandparent, so NOT disposed
            Assert.False(obj.IsDisposed);
        }

        // Grandparent scope exited — obj should now be disposed
        Assert.True(obj.IsDisposed);
    }

    [Fact]
    public void NoScopeActive_TryRegisterIsNoOp()
    {
        var obj = new MockSwiftObject();

        // No scope active — TryRegister should silently do nothing
        SwiftDisposeScope.TryRegister(obj);

        Assert.False(obj.IsDisposed);
        Assert.Null(SwiftDisposeScope.Current);
    }

    [Fact]
    public async Task AsyncAwait_ScopeTracksAcrossAwaitBoundaries()
    {
        var obj1 = new MockSwiftObject();
        var obj2 = new MockSwiftObject();

        using (new SwiftDisposeScope())
        {
            SwiftDisposeScope.TryRegister(obj1);

            await Task.Yield();

            // After await, the scope should still be active (AsyncLocal flows)
            Assert.NotNull(SwiftDisposeScope.Current);
            SwiftDisposeScope.TryRegister(obj2);
        }

        Assert.True(obj1.IsDisposed);
        Assert.True(obj2.IsDisposed);
    }

    [Fact]
    public void ExceptionSafety_ObjectsDisposedWhenScopeBodyThrows()
    {
        var obj1 = new MockSwiftObject();
        var obj2 = new MockSwiftObject();

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using (new SwiftDisposeScope())
            {
                SwiftDisposeScope.TryRegister(obj1);
                SwiftDisposeScope.TryRegister(obj2);
                throw new InvalidOperationException("test");
            }
        }));

        // Objects should still be disposed even though the scope body threw
        Assert.True(obj1.IsDisposed);
        Assert.True(obj2.IsDisposed);
    }

    [Fact]
    public void EmptyScope_DisposeIsNoOp()
    {
        // Empty scope — no objects registered, dispose should not throw
        using (new SwiftDisposeScope())
        {
            // No registrations
        }

        // If we got here, it worked
        Assert.Null(SwiftDisposeScope.Current);
    }

    [Fact]
    public void DoubleDisposeSafety_ScopeToleratesAlreadyDisposedObjects()
    {
        var obj = new MockSwiftObject();

        using (new SwiftDisposeScope())
        {
            SwiftDisposeScope.TryRegister(obj);

            // Dispose the object before the scope exits
            obj.Dispose();
            Assert.Equal(1, obj.DisposeCount);
        }

        // Scope called Dispose again — our mock counts it
        Assert.Equal(2, obj.DisposeCount);
    }

    [Fact]
    public void LIFODisposalOrder_ObjectsDisposedInReverseCreationOrder()
    {
        var disposalOrder = new List<int>();

        var obj1 = new DisposalOrderTracker(1, disposalOrder);
        var obj2 = new DisposalOrderTracker(2, disposalOrder);
        var obj3 = new DisposalOrderTracker(3, disposalOrder);

        using (new SwiftDisposeScope())
        {
            SwiftDisposeScope.TryRegister(obj1);
            SwiftDisposeScope.TryRegister(obj2);
            SwiftDisposeScope.TryRegister(obj3);
        }

        Assert.Equal(new[] { 3, 2, 1 }, disposalOrder);
    }

    [Fact]
    public void ScopeRestoresParentOnDispose()
    {
        Assert.Null(SwiftDisposeScope.Current);

        var outer = new SwiftDisposeScope();
        var outerRef = SwiftDisposeScope.Current;
        Assert.NotNull(outerRef);

        var inner = new SwiftDisposeScope();
        Assert.NotEqual(outerRef, SwiftDisposeScope.Current);

        inner.Dispose();
        Assert.Same(outerRef, SwiftDisposeScope.Current);

        outer.Dispose();
        Assert.Null(SwiftDisposeScope.Current);
    }

    [Fact]
    public void DoubleDisposeScope_SecondDisposeIsNoOp()
    {
        var obj = new MockSwiftObject();
        var scope = new SwiftDisposeScope();
        SwiftDisposeScope.TryRegister(obj);

        scope.Dispose();
        Assert.True(obj.IsDisposed);
        Assert.Equal(1, obj.DisposeCount);

        // Second dispose should be a no-op
        scope.Dispose();
        Assert.Equal(1, obj.DisposeCount);
    }

    [Fact]
    public void DetachReturnsTrue_WhenObjectFound()
    {
        var obj = new MockSwiftObject();
        using (new SwiftDisposeScope())
        {
            SwiftDisposeScope.TryRegister(obj);
            Assert.True(SwiftDisposeScope.Detach(obj));
        }
    }

    [Fact]
    public void DetachReturnsFalse_WhenObjectNotFound()
    {
        var obj = new MockSwiftObject();
        using (new SwiftDisposeScope())
        {
            // Never registered — detach should return false
            Assert.False(SwiftDisposeScope.Detach(obj));
        }
    }

    [Fact]
    public void DetachReturnsFalse_WhenNoScopeActive()
    {
        var obj = new MockSwiftObject();
        Assert.False(SwiftDisposeScope.Detach(obj));
    }

    [Fact]
    public void MoveToParentReturnsFalse_WhenNoParentScope()
    {
        var obj = new MockSwiftObject();
        using (new SwiftDisposeScope())
        {
            SwiftDisposeScope.TryRegister(obj);

            // No parent scope — MoveToParent should remove from current but not add anywhere
            var moved = SwiftDisposeScope.MoveToParent(obj);
            Assert.True(moved);
        }

        // Object was removed from the only scope, so it should NOT be disposed by scope exit
        Assert.False(obj.IsDisposed);
    }

    [Fact]
    public void ExtensionMethods_ReturnSameObjectForFluentChaining()
    {
        var obj = new MockSwiftObject();
        using (new SwiftDisposeScope())
        {
            SwiftDisposeScope.TryRegister(obj);

            var detached = obj.DetachFromScope();
            Assert.Same(obj, detached);
        }
    }

    [Fact]
    public void ExtensionMethods_MoveToParent_ReturnsSameObject()
    {
        var obj = new MockSwiftObject();
        using (new SwiftDisposeScope())
        {
            using (new SwiftDisposeScope())
            {
                SwiftDisposeScope.TryRegister(obj);
                var moved = obj.MoveToParentScope();
                Assert.Same(obj, moved);
            }
        }
    }

    /// <summary>
    /// Mock ISwiftStruct for testing — verifies the marker interface works correctly
    /// with scope tracking (same behavior as ISwiftObject).
    /// </summary>
    private sealed class MockSwiftStruct : ISwiftStruct, IDisposable
    {
        public bool IsDisposed { get; private set; }
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
            DisposeCount++;
        }

        // ISwiftObject stubs
        public int MarshalToSwift(ref Span<byte> swiftDestSpan) => throw new NotSupportedException();
        public static TypeMetadata GetTypeMetadata() => throw new NotSupportedException();
        public static ISwiftObject NewFromPayload(IntPtr payload) => throw new NotSupportedException();
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
            => throw new NotSupportedException();
    }

    [Fact]
    public void ISwiftStructMarker_WorksWithScopeTracking()
    {
        var structObj = new MockSwiftStruct();
        var classObj = new MockSwiftObject();

        using (new SwiftDisposeScope())
        {
            SwiftDisposeScope.TryRegister(structObj);
            SwiftDisposeScope.TryRegister(classObj);
        }

        Assert.True(structObj.IsDisposed);
        Assert.True(classObj.IsDisposed);
    }

    [Fact]
    public void ISwiftStructMarker_DetachWorksCorrectly()
    {
        var structObj = new MockSwiftStruct();

        using (new SwiftDisposeScope())
        {
            SwiftDisposeScope.TryRegister(structObj);
            structObj.DetachFromScope();
        }

        // Detached struct should NOT be disposed by scope
        Assert.False(structObj.IsDisposed);
    }

    /// <summary>
    /// Helper that records its disposal order ID into a shared list.
    /// </summary>
    private sealed class DisposalOrderTracker : IDisposable
    {
        private readonly int _id;
        private readonly List<int> _disposalOrder;

        public DisposalOrderTracker(int id, List<int> disposalOrder)
        {
            _id = id;
            _disposalOrder = disposalOrder;
        }

        public void Dispose()
        {
            _disposalOrder.Add(_id);
        }
    }
}
