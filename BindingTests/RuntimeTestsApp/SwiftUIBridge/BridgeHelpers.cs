// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if SWIFTUI_BRIDGE

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.SwiftUIBridge;

#region Managed Session Wrapper

public class BridgeSession : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;
    private readonly string _name;
    private readonly Action<IntPtr> _freeAction;

    public BridgeSession(IntPtr handle, string name, Action<IntPtr> freeAction)
    {
        _handle = handle;
        _name = name;
        _freeAction = freeAction;
    }

    public IntPtr Handle => !_disposed
        ? _handle
        : throw new ObjectDisposedException(_name);

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _freeAction(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

#endregion

#region Callback State

internal static class TypedClosureState
{
    internal static volatile int LastArgValue;
    internal static volatile bool LastReturnedTrue;
    internal static volatile int CallCount;

    internal static void Reset()
    {
        LastArgValue = 0;
        LastReturnedTrue = false;
        CallCount = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static int OnValueCallback(int value, IntPtr userData)
    {
        LastArgValue = value;
        Interlocked.Increment(ref CallCount);
        var result = value > 0;
        LastReturnedTrue = result;
        return result ? 1 : 0;
    }
}

internal static class MultiArgClosureState
{
    internal static volatile int LastVal;
    internal static volatile bool LastFlag;
    internal static volatile int CallCount;

    internal static void Reset()
    {
        LastVal = 0;
        LastFlag = false;
        CallCount = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void OnEventCallback(int val, int flag, IntPtr userData)
    {
        LastVal = val;
        LastFlag = flag != 0;
        Interlocked.Increment(ref CallCount);
    }
}

internal static class MixedActionState
{
    internal static volatile int CallCount;

    internal static void Reset()
    {
        CallCount = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void OnActionCallback(IntPtr userData)
    {
        Interlocked.Increment(ref CallCount);
    }
}

internal static class StringClosureState
{
    internal static volatile string? LastValue;
    internal static volatile int CallCount;

    internal static void Reset()
    {
        LastValue = null;
        CallCount = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void OnResultCallback(IntPtr ptr, nint len, IntPtr userData)
    {
        if (ptr != IntPtr.Zero && len > 0)
        {
            var bytes = new byte[(int)len];
            Marshal.Copy(ptr, bytes, 0, (int)len);
            LastValue = Encoding.UTF8.GetString(bytes);
        }
        else
        {
            LastValue = "";
        }
        Interlocked.Increment(ref CallCount);
    }
}

internal static class ClassClosureState
{
    internal static volatile IntPtr LastModelPtr;
    internal static volatile int CallCount;

    internal static void Reset()
    {
        LastModelPtr = IntPtr.Zero;
        CallCount = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void OnModelCallback(IntPtr modelPtr, IntPtr userData)
    {
        LastModelPtr = modelPtr;
        Interlocked.Increment(ref CallCount);
    }
}

internal static class OptionalClosureState
{
    internal static volatile int LastValue;
    internal static volatile int CallCount;

    internal static void Reset()
    {
        LastValue = 0;
        CallCount = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void OnCallback(int value, IntPtr userData)
    {
        LastValue = value;
        Interlocked.Increment(ref CallCount);
    }
}

internal static class LifecycleCallbackState
{
    internal static volatile int AppearCount;
    internal static volatile int DisappearCount;

    internal static void Reset()
    {
        AppearCount = 0;
        DisappearCount = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void OnAppearCallback(IntPtr userData)
    {
        Interlocked.Increment(ref AppearCount);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void OnDisappearCallback(IntPtr userData)
    {
        Interlocked.Increment(ref DisappearCount);
    }
}

internal static class StringReturnCallbackState
{
    internal static volatile int CallCount;
    internal static volatile int LastArg;

    internal static void Reset()
    {
        CallCount = 0;
        LastArg = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static unsafe IntPtr TransformerCallback(int arg, IntPtr retLenPtr, IntPtr userData)
    {
        LastArg = arg;
        Interlocked.Increment(ref CallCount);

        var result = $"value_{arg}";
        var bytes = Encoding.UTF8.GetBytes(result);
        var nativePtr = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.CopyTo(new Span<byte>(nativePtr, bytes.Length));
        *(nint*)retLenPtr = bytes.Length;
        return (IntPtr)nativePtr;
    }
}

internal static class ClassReturnCallbackState
{
    internal static volatile int CallCount;
    internal static volatile int LastArg;

    internal static void Reset()
    {
        CallCount = 0;
        LastArg = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static IntPtr FactoryCallback(int arg, IntPtr userData)
    {
        LastArg = arg;
        Interlocked.Increment(ref CallCount);
        // Create a SimpleModel with value = arg * 10 and return retained pointer.
        // SBW_TEST_CreateSimpleModel does not use SBW_onMainThread, so safe to call from callback.
        return BridgeTestHelpers.CreateSimpleModel(arg * 10);
    }
}

/// Callback state for FormatMenuView closure (receives BoundStruct pointer — TransformOutcome).
internal static class FormatMenuCallbackState
{
    internal static volatile int CallCount;
    internal static volatile IntPtr LastOutcomePtr;

    internal static void Reset()
    {
        CallCount = 0;
        LastOutcomePtr = IntPtr.Zero;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void OnFormatCallback(IntPtr outcomePtr, IntPtr userData)
    {
        LastOutcomePtr = outcomePtr;
        Interlocked.Increment(ref CallCount);
    }
}

internal static class AsyncCallbackState
{
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void OnReady(IntPtr handle, IntPtr userData)
    {
        if (userData == IntPtr.Zero) return;
        var stateHandle = GCHandle.FromIntPtr(userData);
        if (!stateHandle.IsAllocated) return;
        var tcs = (TaskCompletionSource<IntPtr>)stateHandle.Target!;
        stateHandle.Free();
        tcs.TrySetResult(handle);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void OnError(IntPtr msgPtr, nint msgLen, IntPtr userData)
    {
        if (userData == IntPtr.Zero) return;
        var stateHandle = GCHandle.FromIntPtr(userData);
        if (!stateHandle.IsAllocated) return;
        var tcs = (TaskCompletionSource<IntPtr>)stateHandle.Target!;
        stateHandle.Free();
        string msg = "(unknown error)";
        if (msgPtr != IntPtr.Zero && msgLen > 0)
        {
            var bytes = new byte[(int)msgLen];
            Marshal.Copy(msgPtr, bytes, 0, (int)msgLen);
            msg = Encoding.UTF8.GetString(bytes);
        }
        tcs.TrySetException(new InvalidOperationException(msg));
    }
}

#endregion

#region Async View Helper

internal static class AsyncViewHelper
{
    /// <summary>
    /// Shared helper for creating async view sessions via callback-based P/Invoke.
    /// The caller provides a lambda that invokes the specific native Create function.
    /// </summary>
    internal static unsafe Task<IntPtr> CreateAsyncView(Action<IntPtr, IntPtr, IntPtr> invokeCreate)
    {
        var tcs = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stateHandle = GCHandle.Alloc(tcs);

        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> readyPtr = &AsyncCallbackState.OnReady;
        delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, void> errorPtr = &AsyncCallbackState.OnError;

        try
        {
            invokeCreate((IntPtr)readyPtr, (IntPtr)errorPtr, GCHandle.ToIntPtr(stateHandle));
        }
        catch
        {
            if (stateHandle.IsAllocated)
                stateHandle.Free();
            throw;
        }

        return tcs.Task;
    }
}

#endregion

#endif
