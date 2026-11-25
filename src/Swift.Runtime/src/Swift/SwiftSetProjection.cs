// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Swift;

/// <summary>
/// A live view of a <see cref="SwiftSet{Element}"/> whose elements are read through a selector, and
/// whose lookups go back through a reverse selector to the Swift set: membership and every set
/// comparison are Swift's, by the element type's own <c>Hashable</c> conformance, never the
/// projected type's managed equality (which may differ, for a class Swift hashes by a conformance its
/// wrapper does not see, or by one conditional on its generic arguments).
/// </summary>
/// <typeparam name="TSource">The Swift set's element type.</typeparam>
/// <typeparam name="TResult">The projected element type.</typeparam>
internal sealed class SwiftSetProjection<TSource, TResult> : IReadOnlySet<TResult>, IDisposable
{
    private readonly SwiftSet<TSource> _source;
    private readonly Func<TSource, TResult> _selector;
    private readonly Func<TResult, TSource> _reverseSelector;

    public SwiftSetProjection(SwiftSet<TSource> source, Func<TSource, TResult> selector, Func<TResult, TSource> reverseSelector)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _reverseSelector = reverseSelector ?? throw new ArgumentNullException(nameof(reverseSelector));
    }

    public int Count => _source.Count;

    public bool Contains(TResult item)
    {
        // The source element borrows from the item, which stays alive across the lookup.
        var element = _reverseSelector(item);
        try { return _source.Contains(element); }
        finally { (element as IDisposable)?.Dispose(); GC.KeepAlive(item); }
    }

    public IEnumerator<TResult> GetEnumerator()
    {
        foreach (var element in _source)
            yield return _selector(element);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool IsProperSubsetOf(IEnumerable<TResult> other) => Compare(other, (source, elements) => source.IsProperSubsetOf(elements));

    public bool IsProperSupersetOf(IEnumerable<TResult> other) => Compare(other, (source, elements) => source.IsProperSupersetOf(elements));

    public bool IsSubsetOf(IEnumerable<TResult> other) => Compare(other, (source, elements) => source.IsSubsetOf(elements));

    public bool IsSupersetOf(IEnumerable<TResult> other) => Compare(other, (source, elements) => source.IsSupersetOf(elements));

    public bool Overlaps(IEnumerable<TResult> other) => Compare(other, (source, elements) => source.Overlaps(elements));

    public bool SetEquals(IEnumerable<TResult> other) => Compare(other, (source, elements) => source.SetEquals(elements));

    /// <summary>Compares the Swift set with <paramref name="other"/>'s elements, each mapped back to a source element.</summary>
    private bool Compare(IEnumerable<TResult> other, Func<SwiftSet<TSource>, IEnumerable<TSource>, bool> comparison)
    {
        ArgumentNullException.ThrowIfNull(other);
        // The items are held for the whole comparison: each source element borrows from its item.
        var items = other.ToList();
        var elements = new List<TSource>(items.Count);
        try
        {
            foreach (var item in items)
                elements.Add(_reverseSelector(item));
            return comparison(_source, elements);
        }
        finally
        {
            foreach (var element in elements)
                (element as IDisposable)?.Dispose();
            GC.KeepAlive(items);
        }
    }

    public void Dispose() => _source.Dispose();
}
