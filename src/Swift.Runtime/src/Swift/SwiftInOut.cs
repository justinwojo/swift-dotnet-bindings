// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift;

/// <summary>
/// Carrier for an <c>inout</c> parameter that appears in a Swift closure's own signature —
/// <c>(inout [String: Any]) -&gt; Void</c>, <c>(inout Int32) -&gt; Bool</c>. Swift hands such a
/// parameter to the block by reference and reads back whatever the block left behind, so the
/// managed side needs a slot it can both read and write. A BCL <c>Action&lt;&gt;</c>/<c>Func&lt;&gt;</c>
/// cannot express a <c>ref</c> parameter, so the projection carries the mutable slot as this
/// object instead: <c>Action&lt;SwiftInOut&lt;int&gt;&gt;</c> rather than an invented
/// per-signature delegate type.
/// </summary>
/// <remarks>
/// <para>
/// The consumer reads the value Swift seeded through <see cref="Value"/> and assigns to the same
/// property to publish a mutation. When the block returns, the generated trampoline takes the
/// final value and writes it into a Swift-owned cell, which the Swift adapter then assigns into
/// the caller's storage — so ARC and value-witness correctness of the write-back belong to the
/// Swift compiler, not to marshalling code.
/// </para>
/// <para>
/// The slot is live only for the duration of one call. Swift's <c>inout</c> storage may be the
/// caller's stack, which is gone the moment the block returns, so an instance captured out of the
/// block would otherwise read or write freed memory. Instead the trampoline closes the slot after
/// it takes the value, and every later access throws <see cref="InvalidOperationException"/>.
/// </para>
/// </remarks>
/// <typeparam name="T">The projected C# type of the Swift <c>inout</c> parameter.</typeparam>
public sealed class SwiftInOut<T>
{
    private T _value;
    private bool _closed;

    /// <summary>
    /// Creates a slot seeded with the value Swift passed in. Called by generated trampolines;
    /// an instance a consumer creates is inert — nothing reads it back.
    /// </summary>
    /// <param name="value">The value Swift seeded the <c>inout</c> cell with.</param>
    public SwiftInOut(T value)
    {
        _value = value;
    }

    /// <summary>
    /// The value in the slot. Reading yields what Swift seeded, or the most recent assignment;
    /// assigning is what Swift observes after the block returns.
    /// </summary>
    /// <exception cref="InvalidOperationException">The block that received this slot has returned.</exception>
    public T Value
    {
        get
        {
            ThrowIfClosed();
            return _value;
        }
        set
        {
            ThrowIfClosed();
            _value = value;
        }
    }

    /// <summary>
    /// Takes the final value and closes the slot. Called by generated trampolines once the
    /// consumer's block has returned and immediately before the value is written back to Swift.
    /// </summary>
    /// <returns>The value the block left in the slot.</returns>
    public T Close()
    {
        ThrowIfClosed();
        _closed = true;
        var value = _value;

        // Drop the carrier's own reference on the way out. A consumer that captured the slot
        // keeps it alive past the block, and for a native-backed projection that would pin the
        // Swift storage behind it for as long as the capture lives — even though the slot can
        // never hand the value out again.
        _value = default!;
        return value;
    }

    private void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new InvalidOperationException(
                $"This {nameof(SwiftInOut<T>)}<{typeof(T).Name}> belongs to a Swift 'inout' parameter and is " +
                "valid only while the block that received it is running. Swift's storage for the parameter " +
                "may no longer exist. Copy the value out of it before the block returns instead of capturing it.");
        }
    }
}
