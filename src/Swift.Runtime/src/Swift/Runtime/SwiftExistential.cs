// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Threading;

namespace Swift.Runtime;

/// <summary>
/// An owned Swift existential value whose protocol the binding has no C# type for: what a member
/// returning such an existential (<c>any CustomStringConvertible</c>, a protocol with associated
/// types and no known conformers, and the like) hands back as <c>object</c>.
/// </summary>
/// <remarks>
/// <para>Swift returns an existential at <c>+1</c>. With no proxy class to adopt it, the generated
/// code used to box the raw container into <c>object</c>, and nothing ever released the payload: each
/// read leaked the value. This box owns that <c>+1</c> and releases it once, on <see cref="Dispose"/> or
/// when the box is finalized.</para>
/// <para>The value can be passed back to a member that takes the same existential as <c>object</c>;
/// that call borrows it. After disposal it can no longer be passed (<see cref="ObjectDisposedException"/>
/// before any native call).</para>
/// </remarks>
/// <typeparam name="TContainer">The existential container carrying the value.</typeparam>
public sealed class SwiftExistential<TContainer> : ISwiftExistentialConvertible<TContainer>, IDisposable
    where TContainer : unmanaged, IExistentialContainer
{
    private TContainer _container;
    private readonly bool _classBound;
    private readonly TypeMetadata _containerMetadata;
    private int _released;

    /// <summary>
    /// Adopts an opaque existential container Swift returned at <c>+1</c>.
    /// </summary>
    /// <param name="container">The owned container. It is consumed and must not be released elsewhere.</param>
    public SwiftExistential(TContainer container) : this(container, classBound: false)
    {
    }

    /// <summary>
    /// Adopts an existential container Swift returned at <c>+1</c>.
    /// </summary>
    /// <param name="container">The owned container. It is consumed and must not be released elsewhere.</param>
    /// <param name="classBound">
    /// True when the container was widened from a class-bound existential (a class reference in
    /// <c>Payload0</c>, no stored type metadata), whose <c>+1</c> is that reference.
    /// </param>
    public SwiftExistential(TContainer container, bool classBound)
    {
        _container = container;
        _classBound = classBound;
        // Resolved now so neither release path has to look metadata up (the finalizer must not).
        _containerMetadata = classBound ? default : TypeMetadata.GetExistentialTypeMetadata(container.Count);
    }

    /// <summary>
    /// Takes an owned copy of a container Swift still owns (a borrowed argument, an element read
    /// in place), so the box stays valid after the source is gone.
    /// </summary>
    /// <param name="container">The borrowed container. Its ownership is unchanged.</param>
    /// <param name="classBound">True for a container widened from a class-bound existential.</param>
    public static unsafe SwiftExistential<TContainer> FromBorrowed(TContainer container, bool classBound)
    {
        if (classBound)
        {
            Arc.UnknownObjectRetain(container.Payload0);
            return new SwiftExistential<TContainer>(container, classBound: true);
        }
        var metadata = TypeMetadata.GetExistentialTypeMetadata(container.Count);
        TContainer copy = default;
        InteropServices.SwiftMarshal.CopyWireBufferRetains((IntPtr)(&copy), (IntPtr)(&container), metadata);
        return new SwiftExistential<TContainer>(copy, classBound: false);
    }

    /// <summary>Whether the value has been released.</summary>
    public bool IsDisposed => Volatile.Read(ref _released) != 0;

    /// <summary>Whether the container holds a class-bound existential's reference in <c>Payload0</c>.</summary>
    internal bool IsClassBound => _classBound;

    /// <summary>
    /// The container, borrowed: the box keeps ownership. Used to pass the value back to Swift.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The value has been released.</exception>
    public TContainer GetExistentialContainer()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return _container;
    }

    /// <summary>Releases the Swift value. Safe to call more than once.</summary>
    public void Dispose()
    {
        Release(finalizing: false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the Swift value if it was never disposed.</summary>
    ~SwiftExistential()
    {
        Release(finalizing: true);
    }

    private unsafe void Release(bool finalizing)
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;
        if (_classBound)
        {
            if (finalizing)
                Arc.UnknownObjectReleaseFinalizerSafe(_container.Payload0);
            else
                Arc.UnknownObjectRelease(_container.Payload0);
            return;
        }
        // _container is a field of this object; pin it for the native destroy.
        fixed (TContainer* pinned = &_container)
        {
            if (finalizing)
                InteropServices.SwiftMarshal.DestroyWireBufferRetainsFinalizerSafe((IntPtr)pinned, _containerMetadata);
            else
                InteropServices.SwiftMarshal.DestroyWireBufferRetains((IntPtr)pinned, _containerMetadata);
        }
    }
}
