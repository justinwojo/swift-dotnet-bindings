// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

#nullable enable

namespace Swift.Runtime;

/// <summary>How a provisionally minted existential argument's +1 is undone when its callee never takes it.</summary>
internal enum OwnedExistentialRollback
{
    /// <summary>Nothing was minted (the aliased fallback when the Swift runtime is unavailable).</summary>
    None,

    /// <summary>The existential value witness destroys the container's payload.</summary>
    Destroy,

    /// <summary>A class-constrained carrier widened into the opaque words: the count is on word 0 alone.</summary>
    ReleaseClassReference,
}

/// <summary>
/// Begins the provisional +1 an existential argument carries to a callee that consumes it
/// (an initializer's value, a setter's new value): <see cref="OwnedExistentialArgument{TContainer}"/>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class OwnedExistentialArgument
{
    /// <summary>
    /// Mints <paramref name="value"/> as an owned <c>any TProtocol</c> argument, wrapping a plain C#
    /// implementation in the proxy <paramref name="wrapFallback"/> builds.
    /// </summary>
    /// <param name="value">The argument.</param>
    /// <param name="wrapFallback">Builds the proxy a plain C# implementation is wrapped in.</param>
    /// <param name="classBoundCarrier">
    /// The protocol is class-constrained: its two-word layout is widened into the container, and the
    /// +1 is a retain on the class reference.
    /// </param>
    public static OwnedExistentialArgument<ExistentialContainer1> Begin<TProtocol>(
        TProtocol value,
        Func<TProtocol, ISwiftExistentialConvertible<ExistentialContainer1>> wrapFallback,
        bool classBoundCarrier)
        where TProtocol : class
    {
        var container = ExistentialContainerFactory.GetOrCreate(value, wrapFallback, out bool owns, out object? keepAlive);
        var owned = ExistentialContainerFactory.MintOrDonateExistential1(container, owns, classBoundCarrier, out var rollback);
        // The mint reads the payload the (possibly auto-wrapped) proxy owns; keep the proxy alive
        // until it holds its own count.
        GC.KeepAlive(keepAlive);
        return new OwnedExistentialArgument<ExistentialContainer1>(owned, rollback);
    }

    /// <summary>
    /// Mints <paramref name="value"/> as an owned <c>any TProtocol</c> argument where no proxy exists
    /// to wrap a plain C# implementation: the value must already be a Swift-vended conformer or a
    /// boxable one.
    /// </summary>
    public static OwnedExistentialArgument<ExistentialContainer1> Begin<TProtocol>(TProtocol value, bool classBoundCarrier)
        where TProtocol : class
    {
        var container = ExistentialContainerFactory.GetOrCreate(value, out bool owns, out object? keepAlive);
        var owned = ExistentialContainerFactory.MintOrDonateExistential1(container, owns, classBoundCarrier, out var rollback);
        GC.KeepAlive(keepAlive);
        return new OwnedExistentialArgument<ExistentialContainer1>(owned, rollback);
    }

    /// <summary>
    /// The consumer-owned lane of <see cref="Begin{TProtocol}(TProtocol, Func{TProtocol, ISwiftExistentialConvertible{ExistentialContainer1}}, bool)"/>:
    /// a setter whose storage does not retain (<c>weak</c>/<c>unowned</c>) but whose new value is
    /// still <c>@owned</c>. The carrier is rooted by the caller's implementation (see
    /// <see cref="ExistentialContainerFactory.GetOrCreateConsumerOwned{TProtocol}(TProtocol, Func{TProtocol, ISwiftExistentialConvertible{ExistentialContainer1}}, out bool, out object)"/>),
    /// and the mint supplies the count the setter releases.
    /// </summary>
    public static OwnedExistentialArgument<ExistentialContainer1> BeginConsumerOwned<TProtocol>(
        TProtocol value,
        Func<TProtocol, ISwiftExistentialConvertible<ExistentialContainer1>> wrapFallback,
        bool classBoundCarrier)
        where TProtocol : class
    {
        var container = ExistentialContainerFactory.GetOrCreateConsumerOwned(value, wrapFallback, out bool owns, out object? keepAlive);
        var owned = ExistentialContainerFactory.MintOrDonateExistential1(container, owns, classBoundCarrier, out var rollback);
        GC.KeepAlive(keepAlive);
        return new OwnedExistentialArgument<ExistentialContainer1>(owned, rollback);
    }

    /// <summary>
    /// Mints <paramref name="value"/> as an owned composition (<c>any P &amp; Q…</c>) argument. Only a
    /// Swift-vended proxy implements a composition interface, and its container is borrowed, so the
    /// mint is always a fresh copy.
    /// </summary>
    public static OwnedExistentialArgument<TContainer> BeginComposition<TProtocol, TContainer>(TProtocol value)
        where TProtocol : class
        where TContainer : unmanaged, IExistentialContainer
    {
        var owned = ExistentialContainerFactory.CreateOwnedCompositionExistential<TProtocol, TContainer>(value, out var rollback);
        return new OwnedExistentialArgument<TContainer>(owned, rollback);
    }
}

/// <summary>
/// An existential argument minted at +1 for a callee that consumes it, held until the call has
/// entered Swift.
/// </summary>
/// <remarks>
/// <para>A consuming callee (an initializer's value parameter, a setter's new value) releases the
/// container it is handed, so the caller mints one count for it: an independent copy of a proxy's
/// borrowed container, or a boxed conformer's fresh box. Minting inside the call's argument list
/// strands that count when anything after it throws before native entry: a later argument's
/// conversion (a disposed wrapper, an incompatible implementation), its own lease, or the entry
/// point's resolution. Nothing then owns the copy, and the conformer it refers to never dies.</para>
///
/// <para>Generated callers therefore mint the argument before the call, with a <c>using</c>
/// declaration, pass <see cref="Container"/>, and call <see cref="Complete"/> immediately after the
/// P/Invoke returns, including a Swift error return: the callee owns the count from native entry on.
/// Disposal without completion destroys the mint. This is the existential counterpart of
/// <see cref="OwnedArgument.ObjectTransfer"/> and <see cref="OwnedArgument.ClassTransfer"/>.</para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class OwnedExistentialArgument<TContainer> : IDisposable
    where TContainer : unmanaged, IExistentialContainer
{
    private readonly TContainer _container;
    private OwnedExistentialRollback _rollback;
    private bool _transferred;

    internal OwnedExistentialArgument(TContainer container, OwnedExistentialRollback rollback)
    {
        _container = container;
        _rollback = rollback;
    }

    /// <summary>The owned container the call passes.</summary>
    public TContainer Container => _container;

    /// <summary>Records native consumption before any managed result or error conversion.</summary>
    public void Complete() => _transferred = true;

    /// <summary>Destroys the mint unless the callee took it. Idempotent.</summary>
    public unsafe void Dispose()
    {
        var rollback = _rollback;
        _rollback = OwnedExistentialRollback.None;
        if (_transferred || rollback == OwnedExistentialRollback.None)
            return;
        var owned = _container;
        if (rollback == OwnedExistentialRollback.ReleaseClassReference)
        {
            if (owned.Payload0 != IntPtr.Zero)
                Arc.UnknownObjectRelease(owned.Payload0);
            return;
        }
        try
        {
            var metadata = TypeMetadata.GetExistentialTypeMetadata(owned.Count);
            Swift.Runtime.InteropServices.SwiftMarshal.DestroyWireBufferRetains((IntPtr)Unsafe.AsPointer(ref owned), metadata);
        }
        catch (SwiftRuntimeException)
        {
            // Thrown only when SwiftBindingsRuntime is unavailable, where nothing was minted either
            // (the mint itself reports None then). A genuine destroy fault has another type and propagates.
        }
    }
}
