// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace Swift.Runtime;

/// <summary>
/// A weak reference to a Swift-backed object that lets a consumer break a cross-heap
/// retain cycle. When a Swift object stores a C# callback that captures the same object,
/// the captured strong reference and the Swift-side GCHandle root each other and neither
/// heap can collect the pair. Capturing a <see cref="WeakSwiftReference{T}"/> instead of
/// the object itself — and reaching the object through <see cref="Target"/> /
/// <see cref="TryGetTarget"/> inside the callback — breaks the C#-side leg of that cycle.
/// </summary>
/// <typeparam name="T">
/// The Swift-backed object type. Constrained to a reference type because only heap-backed
/// Swift objects (classes) can participate in a retain cycle; frozen value-type structs
/// cannot.
/// </typeparam>
public sealed class WeakSwiftReference<T> where T : class, ISwiftObject
{
    private readonly WeakReference<T> _weak;

    /// <summary>Creates a weak reference to <paramref name="target"/>.</summary>
    /// <param name="target">The object to reference weakly.</param>
    public WeakSwiftReference(T target)
    {
        if (target is null)
            throw new ArgumentNullException(nameof(target));
        _weak = new WeakReference<T>(target);
    }

    /// <summary>
    /// The referenced object, or <see langword="null"/> if it has been collected.
    /// </summary>
    public T? Target => _weak.TryGetTarget(out var t) ? t : null;

    /// <summary>
    /// Attempts to retrieve the referenced object.
    /// </summary>
    /// <param name="target">The object if still alive; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the object is still alive.</returns>
    public bool TryGetTarget([MaybeNullWhen(false)] out T target)
        => _weak.TryGetTarget(out target);

    /// <summary>Whether the referenced object has not yet been collected.</summary>
    public bool IsAlive => _weak.TryGetTarget(out _);
}
