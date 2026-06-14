// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Verifies that all IDisposable runtime types throw ObjectDisposedException
/// on post-dispose access, and that double-dispose is safe (idempotent).
/// </summary>
public class DisposeSafetyTests
{
    #region SwiftString

    [Fact]
    public void SwiftString_DoubleDispose_DoesNotThrow()
    {
        var str = new SwiftString("hello");
        str.Dispose();
        str.Dispose(); // must not throw
    }

    [Fact]
    public void SwiftString_Length_ThrowsAfterDispose()
    {
        var str = new SwiftString("hello");
        str.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = str.Length);
    }

    [Fact]
    public void SwiftString_ToString_ThrowsAfterDispose()
    {
        var str = new SwiftString("hello");
        str.Dispose();
        Assert.Throws<ObjectDisposedException>(() => str.ToString());
    }

    [Fact]
    public void SwiftString_Payload_ThrowsAfterDispose()
    {
        var str = new SwiftString("hello");
        str.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = str.Payload);
    }

    [Fact]
    public void SwiftString_ImplicitConversion_ThrowsAfterDispose()
    {
        var str = new SwiftString("hello");
        str.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { string _ = str; });
    }

    #endregion

    #region SwiftArray

    [Fact]
    public void SwiftArray_DoubleDispose_DoesNotThrow()
    {
        var array = new SwiftArray<int>();
        array.Dispose();
        array.Dispose();
    }

    [Fact]
    public void SwiftArray_Count_ThrowsAfterDispose()
    {
        var array = new SwiftArray<int>();
        array.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = array.Count);
    }

    [Fact]
    public void SwiftArray_Append_ThrowsAfterDispose()
    {
        var array = new SwiftArray<int>();
        array.Dispose();
        Assert.Throws<ObjectDisposedException>(() => array.Append(42));
    }

    [Fact]
    public void SwiftArray_Indexer_ThrowsAfterDispose()
    {
        var array = new SwiftArray<int>();
        array.Append(1);
        array.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = array[0]);
    }

    [Fact]
    public void SwiftArray_Insert_ThrowsAfterDispose()
    {
        var array = new SwiftArray<int>();
        array.Dispose();
        Assert.Throws<ObjectDisposedException>(() => array.Insert(0, 42));
    }

    [Fact]
    public void SwiftArray_Remove_ThrowsAfterDispose()
    {
        var array = new SwiftArray<int>();
        array.Append(1);
        array.Dispose();
        Assert.Throws<ObjectDisposedException>(() => array.Remove(0));
    }

    [Fact]
    public void SwiftArray_RemoveAll_ThrowsAfterDispose()
    {
        var array = new SwiftArray<int>();
        array.Dispose();
        Assert.Throws<ObjectDisposedException>(() => array.RemoveAll());
    }

    [Fact]
    public void SwiftArray_GetEnumerator_ThrowsAfterDispose()
    {
        var array = new SwiftArray<int>();
        array.Dispose();
        Assert.Throws<ObjectDisposedException>(() => array.GetEnumerator());
    }

    [Fact]
    public void SwiftArray_ToArray_ThrowsAfterDispose()
    {
        var array = new SwiftArray<int>();
        array.Dispose();
        Assert.Throws<ObjectDisposedException>(() => array.ToArray());
    }

    [Fact]
    public void SwiftArray_ToList_ThrowsAfterDispose()
    {
        var array = new SwiftArray<int>();
        array.Dispose();
        Assert.Throws<ObjectDisposedException>(() => array.ToList());
    }

    [Fact]
    public void SwiftArray_ToString_ThrowsAfterDispose()
    {
        var array = new SwiftArray<int>();
        array.Dispose();
        Assert.Throws<ObjectDisposedException>(() => array.ToString());
    }

    [Fact]
    public void SwiftArray_Payload_ThrowsAfterDispose()
    {
        var array = new SwiftArray<int>();
        array.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = array.Payload);
    }

    #endregion

    #region SwiftDictionary

    [Fact]
    public void SwiftDictionary_DoubleDispose_DoesNotThrow()
    {
        var dict = new SwiftDictionary<SwiftString, nint>();
        dict.Dispose();
        dict.Dispose();
    }

    [Fact]
    public void SwiftDictionary_Count_ThrowsAfterDispose()
    {
        var dict = new SwiftDictionary<SwiftString, nint>();
        dict.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = dict.Count);
    }

    [Fact]
    public void SwiftDictionary_Indexer_Get_ThrowsAfterDispose()
    {
        var dict = new SwiftDictionary<SwiftString, nint>();
        dict.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = dict[new SwiftString("k")]);
    }

    [Fact]
    public void SwiftDictionary_Indexer_Set_ThrowsAfterDispose()
    {
        var dict = new SwiftDictionary<SwiftString, nint>();
        dict.Dispose();
        Assert.Throws<ObjectDisposedException>(() => dict[new SwiftString("k")] = (nint)1);
    }

    [Fact]
    public void SwiftDictionary_TryGetValue_ThrowsAfterDispose()
    {
        var dict = new SwiftDictionary<SwiftString, nint>();
        dict.Dispose();
        Assert.Throws<ObjectDisposedException>(() => dict.TryGetValue(new SwiftString("k"), out _));
    }

    [Fact]
    public void SwiftDictionary_ContainsKey_ThrowsAfterDispose()
    {
        var dict = new SwiftDictionary<SwiftString, nint>();
        dict.Dispose();
        Assert.Throws<ObjectDisposedException>(() => dict.ContainsKey(new SwiftString("k")));
    }

    [Fact]
    public void SwiftDictionary_RemoveAll_ThrowsAfterDispose()
    {
        var dict = new SwiftDictionary<SwiftString, nint>();
        dict.Dispose();
        Assert.Throws<ObjectDisposedException>(() => dict.RemoveAll());
    }

    [Fact]
    public void SwiftDictionary_RemoveValue_ThrowsAfterDispose()
    {
        var dict = new SwiftDictionary<SwiftString, nint>();
        dict.Dispose();
        Assert.Throws<ObjectDisposedException>(() => dict.RemoveValue(new SwiftString("k")));
    }

    [Fact]
    public void SwiftDictionary_GetEnumerator_ThrowsAfterDispose()
    {
        var dict = new SwiftDictionary<SwiftString, nint>();
        dict.Dispose();
        Assert.Throws<ObjectDisposedException>(() => dict.GetEnumerator());
    }

    [Fact]
    public void SwiftDictionary_Payload_ThrowsAfterDispose()
    {
        var dict = new SwiftDictionary<SwiftString, nint>();
        dict.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = dict.Payload);
    }

    #endregion

    #region SwiftSet

    [Fact]
    public void SwiftSet_DoubleDispose_DoesNotThrow()
    {
        var set = new SwiftSet<SwiftIntMock>();
        set.Dispose();
        set.Dispose();
    }

    [Fact]
    public void SwiftSet_Count_ThrowsAfterDispose()
    {
        var set = new SwiftSet<SwiftIntMock>();
        set.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = set.Count);
    }

    [Fact]
    public void SwiftSet_Add_ThrowsAfterDispose()
    {
        var set = new SwiftSet<SwiftIntMock>();
        set.Dispose();
        Assert.Throws<ObjectDisposedException>(() => set.Add(new SwiftIntMock(1)));
    }

    [Fact]
    public void SwiftSet_Contains_ThrowsAfterDispose()
    {
        var set = new SwiftSet<SwiftIntMock>();
        set.Dispose();
        Assert.Throws<ObjectDisposedException>(() => set.Contains(new SwiftIntMock(1)));
    }

    [Fact]
    public void SwiftSet_Remove_ThrowsAfterDispose()
    {
        var set = new SwiftSet<SwiftIntMock>();
        set.Dispose();
        Assert.Throws<ObjectDisposedException>(() => set.Remove(new SwiftIntMock(1)));
    }

    [Fact]
    public void SwiftSet_RemoveAll_ThrowsAfterDispose()
    {
        var set = new SwiftSet<SwiftIntMock>();
        set.Dispose();
        Assert.Throws<ObjectDisposedException>(() => set.RemoveAll());
    }

    [Fact]
    public void SwiftSet_GetEnumerator_ThrowsAfterDispose()
    {
        var set = new SwiftSet<SwiftIntMock>();
        set.Dispose();
        Assert.Throws<ObjectDisposedException>(() => set.GetEnumerator());
    }

    [Fact]
    public void SwiftSet_ToArray_ThrowsAfterDispose()
    {
        var set = new SwiftSet<SwiftIntMock>();
        set.Dispose();
        Assert.Throws<ObjectDisposedException>(() => set.ToArray());
    }

    [Fact]
    public void SwiftSet_ToList_ThrowsAfterDispose()
    {
        var set = new SwiftSet<SwiftIntMock>();
        set.Dispose();
        Assert.Throws<ObjectDisposedException>(() => set.ToList());
    }

    [Fact]
    public void SwiftSet_ToString_ThrowsAfterDispose()
    {
        var set = new SwiftSet<SwiftIntMock>();
        set.Dispose();
        Assert.Throws<ObjectDisposedException>(() => set.ToString());
    }

    [Fact]
    public void SwiftSet_Payload_ThrowsAfterDispose()
    {
        var set = new SwiftSet<SwiftIntMock>();
        set.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = set.Payload);
    }

    [Fact]
    public void SwiftSet_SetOperations_ThrowAfterDispose()
    {
        var set = new SwiftSet<SwiftIntMock>();
        set.Dispose();
        var other = new List<SwiftIntMock> { new SwiftIntMock(1) };
        Assert.Throws<ObjectDisposedException>(() => set.IsSubsetOf(other));
        Assert.Throws<ObjectDisposedException>(() => set.IsSupersetOf(other));
        Assert.Throws<ObjectDisposedException>(() => set.IsProperSubsetOf(other));
        Assert.Throws<ObjectDisposedException>(() => set.IsProperSupersetOf(other));
        Assert.Throws<ObjectDisposedException>(() => set.Overlaps(other));
        Assert.Throws<ObjectDisposedException>(() => set.SetEquals(other));
    }

    #endregion

    #region SwiftOptional

    [Fact]
    public void SwiftOptional_DoubleDispose_DoesNotThrow()
    {
        var opt = SwiftOptional<int>.NewSome(42);
        opt.Dispose();
        opt.Dispose();
    }

    [Fact]
    public void SwiftOptional_Case_ThrowsAfterDispose()
    {
        var opt = SwiftOptional<int>.NewSome(42);
        opt.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = opt.Case);
    }

    [Fact]
    public void SwiftOptional_Some_ThrowsAfterDispose()
    {
        var opt = SwiftOptional<int>.NewSome(42);
        opt.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = opt.Some);
    }

    [Fact]
    public void SwiftOptional_Value_ThrowsAfterDispose()
    {
        var opt = SwiftOptional<int>.NewSome(42);
        opt.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = opt.Value);
    }

    [Fact]
    public void SwiftOptional_HasValue_ThrowsAfterDispose()
    {
        var opt = SwiftOptional<int>.NewSome(42);
        opt.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = opt.HasValue);
    }

    [Fact]
    public void SwiftOptional_ToNullable_ThrowsAfterDispose()
    {
        var opt = SwiftOptional<int>.NewSome(42);
        opt.Dispose();
        Assert.Throws<ObjectDisposedException>(() => opt.ToNullable());
    }

    [Fact]
    public void SwiftOptional_Payload_ThrowsAfterDispose()
    {
        var opt = SwiftOptional<int>.NewSome(42);
        opt.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = opt.Payload);
    }

    [Fact]
    public void SwiftOptional_None_DoubleDispose_DoesNotThrow()
    {
        var opt = SwiftOptional<int>.NewNone();
        opt.Dispose();
        opt.Dispose();
    }

    #endregion

    #region SwiftAsyncStream

    [Fact]
    public void SwiftAsyncStream_DoubleDispose_DoesNotThrow()
    {
        var stream = new SwiftAsyncStream<int>();
        stream.Dispose();
        stream.Dispose();
    }

    // DeliverElement is the Swift-executor entry point: it MUST NOT throw across the native
    // boundary, so after Dispose it returns false (stop iteration) rather than throwing. The
    // structural guard fires before any pointer dereference, so IntPtr.Zero is safe here.
    [Fact]
    public void SwiftAsyncStream_DeliverElement_ReturnsFalseAfterDispose_NoThrow()
    {
        var stream = new SwiftAsyncStream<int>();
        stream.Dispose();
        Assert.False(stream.DeliverElement(IntPtr.Zero));
    }

    // Complete/FaultChannel are completion-path callbacks. Calling them after Dispose is a no-op
    // (the channel is already completed by Dispose's SignalProducerStop) and must not throw.
    [Fact]
    public void SwiftAsyncStream_Complete_AfterDispose_NoThrow()
    {
        var stream = new SwiftAsyncStream<int>();
        stream.Dispose();
        stream.Complete();
    }

    [Fact]
    public void SwiftAsyncStream_FaultChannel_AfterDispose_NoThrow()
    {
        var stream = new SwiftAsyncStream<int>();
        stream.Dispose();
        stream.FaultChannel(new InvalidOperationException("late fault"));
    }

    [Fact]
    public void SwiftAsyncStream_GetContext_ThrowsAfterDispose()
    {
        var stream = new SwiftAsyncStream<int>();
        stream.Dispose();
        Assert.Throws<ObjectDisposedException>(() => stream.GetContext());
    }

    [Fact]
    public void SwiftAsyncStream_Cancel_ThrowsAfterDispose()
    {
        var stream = new SwiftAsyncStream<int>();
        stream.Dispose();
        Assert.Throws<ObjectDisposedException>(() => stream.Cancel());
    }

    // Completion-owns-free invariant: GetContext allocates the rooting handle; Complete (the last
    // Swift→C# callback) frees it. After completion the instance is no longer self-rooted.
    [Fact]
    public void SwiftAsyncStream_Complete_FreesContextHandle()
    {
        var stream = new SwiftAsyncStream<int>();
        stream.GetContext();
        Assert.True(stream.IsContextHandleAllocated);
        stream.Complete();
        Assert.False(stream.IsContextHandleAllocated);
    }

    // A channel fault deliberately does NOT free the rooting handle. FaultChannel is reachable from
    // a non-last element trampoline (a mid-stream marshal fault), and the emitted Swift wrapper always
    // calls completionCallback after the consume loop — so completion still runs on this context after
    // the fault. Freeing here would reopen the GCHandle cookie-recycling window between the fault and
    // that trailing completion; completion (Complete) remains the sole free site.
    [Fact]
    public void SwiftAsyncStream_FaultChannel_DoesNotFreeContextHandle_CompletionDoes()
    {
        var stream = new SwiftAsyncStream<int>();
        stream.GetContext();
        Assert.True(stream.IsContextHandleAllocated);
        stream.FaultChannel(new InvalidOperationException("boom"));
        Assert.True(stream.IsContextHandleAllocated);
        // The trailing completion callback frees it.
        stream.Complete();
        Assert.False(stream.IsContextHandleAllocated);
    }

    // Dispose deliberately does NOT free the context handle: an in-flight Swift callback could
    // still resolve it, and freeing early engages the GCHandle cookie-recycling hazard. The free
    // is owned by the producer's completion path.
    [Fact]
    public void SwiftAsyncStream_Dispose_DoesNotFreeContextHandle()
    {
        var stream = new SwiftAsyncStream<int>();
        stream.GetContext();
        stream.Dispose();
        Assert.True(stream.IsContextHandleAllocated);
        // Completion still frees it after Dispose (idempotent one-shot).
        stream.Complete();
        Assert.False(stream.IsContextHandleAllocated);
    }

    #endregion
}
