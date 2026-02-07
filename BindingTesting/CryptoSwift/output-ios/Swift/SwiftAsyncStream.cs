// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift;

/// <summary>
/// Represents a Swift AsyncStream as a C# IAsyncEnumerable.
/// This type bridges Swift's async iteration model with C#'s IAsyncEnumerable pattern.
///
/// Usage pattern:
/// 1. Swift code provides a function pointer that starts iteration
/// 2. For each element, Swift calls the element callback with the element data
/// 3. When iteration completes, Swift calls the completion callback
/// 4. C# code consumes elements via IAsyncEnumerable
/// </summary>
/// <typeparam name="TElement">The element type in the stream.</typeparam>
public class SwiftAsyncStream<TElement> : IAsyncEnumerable<TElement>, IDisposable
    where TElement : ISwiftObject
{
    private readonly Channel<TElement> _channel;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;
    private GCHandle _thisHandle;

    /// <summary>
    /// Delegate type for the element callback from Swift.
    /// Returns true to continue iteration, false to stop.
    /// </summary>
    /// <param name="elementPtr">Pointer to the element data in Swift memory.</param>
    /// <param name="context">Context pointer passed when starting iteration.</param>
    /// <returns>True to continue, false to stop iteration.</returns>
    public delegate bool ElementCallback(IntPtr elementPtr, long context);

    /// <summary>
    /// Delegate type for the completion callback from Swift.
    /// Called when iteration is complete or cancelled.
    /// </summary>
    /// <param name="context">Context pointer passed when starting iteration.</param>
    public delegate void CompletionCallback(long context);

    /// <summary>
    /// Creates a new SwiftAsyncStream.
    /// </summary>
    public SwiftAsyncStream()
    {
        // Use unbounded channel for simplicity - Swift produces, C# consumes
        _channel = Channel.CreateUnbounded<TElement>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Gets the element callback to pass to Swift.
    /// This callback receives elements from the Swift async stream.
    /// </summary>
    public ElementCallback GetElementCallback()
    {
        return OnElement;
    }

    /// <summary>
    /// Gets the completion callback to pass to Swift.
    /// This callback is called when the Swift stream completes.
    /// </summary>
    public CompletionCallback GetCompletionCallback()
    {
        return OnComplete;
    }

    /// <summary>
    /// Gets a context value that can be passed to Swift and used to retrieve this instance.
    /// </summary>
    public long GetContext()
    {
        if (!_thisHandle.IsAllocated)
        {
            _thisHandle = GCHandle.Alloc(this);
        }
        return GCHandle.ToIntPtr(_thisHandle).ToInt64();
    }

    /// <summary>
    /// Called by Swift for each element in the stream.
    /// </summary>
    private bool OnElement(IntPtr elementPtr, long context)
    {
        if (_disposed || _cts.Token.IsCancellationRequested)
        {
            return false; // Stop iteration
        }

        try
        {
            // Marshal the element from Swift
            var element = SwiftMarshal.MarshalFromSwift<TElement>(elementPtr);

            // Write to channel (this should not block since it's unbounded)
            if (!_channel.Writer.TryWrite(element))
            {
                return false; // Channel closed, stop iteration
            }

            return true; // Continue iteration
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SwiftAsyncStream.OnElement: Error marshaling element - {ex.GetType().Name}: {ex.Message}");
            return false; // Stop on error
        }
    }

    /// <summary>
    /// Called by Swift when the stream completes.
    /// </summary>
    private void OnComplete(long context)
    {
        _channel.Writer.TryComplete();
    }

    /// <summary>
    /// Returns an async enumerator for consuming elements.
    /// </summary>
    public async IAsyncEnumerator<TElement> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        // Link the provided token with our internal token
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        await foreach (var element in _channel.Reader.ReadAllAsync(linkedCts.Token))
        {
            yield return element;
        }
    }

    /// <summary>
    /// Cancels the stream iteration.
    /// </summary>
    public void Cancel()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
    }

    /// <summary>
    /// Disposes resources used by the stream.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();
        _channel.Writer.TryComplete();
        _cts.Dispose();

        if (_thisHandle.IsAllocated)
        {
            _thisHandle.Free();
        }
    }

    /// <summary>
    /// Retrieves a SwiftAsyncStream instance from a context value.
    /// </summary>
    public static SwiftAsyncStream<TElement>? FromContext(long context)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(new IntPtr(context));
            return handle.Target as SwiftAsyncStream<TElement>;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SwiftAsyncStream.FromContext: Invalid context {context} - {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Static helper methods for SwiftAsyncStream interop.
/// </summary>
public static class SwiftAsyncStreamInterop
{
    /// <summary>
    /// Callback signature for receiving elements from Swift.
    /// Used as an [UnmanagedCallersOnly] target.
    /// </summary>
    /// <param name="elementPtr">Pointer to the element.</param>
    /// <param name="context">Context identifying the stream instance.</param>
    /// <returns>1 (byte true) to continue, 0 to stop.</returns>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe byte OnElementCallback(void* elementPtr, long context)
    {
        // This is a generic callback that would need to be specialized per element type.
        // In practice, the binding generator will create type-specific callbacks.
        return 0;
    }

    /// <summary>
    /// Callback signature for stream completion from Swift.
    /// Used as an [UnmanagedCallersOnly] target.
    /// </summary>
    /// <param name="context">Context identifying the stream instance.</param>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnCompleteCallback(long context)
    {
        // This is called when the Swift stream completes.
        // The actual implementation would be in generated code.
    }
}
