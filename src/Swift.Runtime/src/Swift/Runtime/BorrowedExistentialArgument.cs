// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.ComponentModel;

#nullable enable

namespace Swift.Runtime;

/// <summary>
/// A single-protocol existential argument that a direct Swift-convention call borrows, held for
/// exactly the duration of that call.
/// </summary>
/// <remarks>
/// <para>A direct call passes <c>any P</c> to a callee that borrows it (<c>@guaranteed</c>): the
/// callee takes no reference of its own, so whatever owns the container's payload must stay alive
/// until the call returns. For a plain C# implementation that owner is the generated proxy the
/// factory wraps it in, and the proxy is registered only weakly (see
/// <see cref="ProxyLifetimeTracker"/>): once the container's words are copied into the call nothing
/// managed refers to it, so a collection during the call finalizes it and releases the box's only
/// reference while Swift is still using it. Keeping the user's implementation alive does not help,
/// because the implementation does not reference its proxy.</para>
///
/// <para>A boxable conformer (a Swift type's own wrapper) has the opposite problem: boxing it mints a
/// fresh +1 that the borrowing callee never releases, so it has to be destroyed after the call.</para>
///
/// <para>Generated callers declare the lease with a <c>using</c> declaration before the call and pass
/// <see cref="Container"/>. Disposal runs after the call (and on every path out of the scope, so a
/// later argument that throws before native entry still destroys the box): it destroys an owned
/// container and drops the reference that kept the proxy alive.</para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class BorrowedExistentialArgument : IDisposable
{
    private readonly ExistentialContainer1 _container;
    private object? _keepAlive;
    private bool _owns;

    private BorrowedExistentialArgument(ExistentialContainer1 container, bool owns, object? keepAlive)
    {
        _container = container;
        _owns = owns;
        _keepAlive = keepAlive;
    }

    /// <summary>The container the call passes. Valid until the lease is disposed.</summary>
    public ExistentialContainer1 Container => _container;

    /// <summary>
    /// Borrows <paramref name="value"/> as an <c>any TProtocol</c> argument, wrapping a plain C#
    /// implementation in the proxy <paramref name="wrapFallback"/> builds.
    /// </summary>
    public static BorrowedExistentialArgument Begin<TProtocol>(
        TProtocol value,
        Func<TProtocol, ISwiftExistentialConvertible<ExistentialContainer1>> wrapFallback)
        where TProtocol : class
    {
        var container = ExistentialContainerFactory.GetOrCreate(value, wrapFallback, out bool owns, out object? keepAlive);
        return new BorrowedExistentialArgument(container, owns, keepAlive);
    }

    /// <summary>
    /// Borrows <paramref name="value"/> as an <c>any TProtocol</c> argument where no proxy exists to
    /// wrap a plain C# implementation: the value must already be a Swift-vended conformer or a
    /// boxable one.
    /// </summary>
    public static BorrowedExistentialArgument Begin<TProtocol>(TProtocol value)
        where TProtocol : class
    {
        var container = ExistentialContainerFactory.GetOrCreate(value, out bool owns, out object? keepAlive);
        return new BorrowedExistentialArgument(container, owns, keepAlive);
    }

    /// <summary>
    /// Ends the borrow: destroys the container when boxing minted its +1, and releases the reference
    /// that kept the proxy alive. Idempotent.
    /// </summary>
    public unsafe void Dispose()
    {
        var keepAlive = _keepAlive;
        _keepAlive = null;
        if (_owns)
        {
            _owns = false;
            var owned = _container;
            try
            {
                // The opaque existential value witness releases an inline class reference, an
                // inline value's references or a swift_allocBox payload alike. A boxed class
                // conformer is also laid out in the opaque positions (see
                // ExistentialContainerFactory.Create), so the same destroy balances it when the call
                // passed the narrowed class-bound pair.
                var metadata = TypeMetadata.GetExistentialTypeMetadata(owned.Count);
                Swift.Runtime.InteropServices.SwiftMarshal.DestroyWireBufferRetains((IntPtr)(&owned), metadata);
            }
            catch (SwiftRuntimeException)
            {
                // GetExistentialTypeMetadata throws this only when SwiftBindingsRuntime is
                // unavailable, where nothing was boxed natively either (as in
                // DestroyAndFreeExistential). A genuine destroy fault has another type and propagates.
            }
        }
        GC.KeepAlive(keepAlive);
    }
}
