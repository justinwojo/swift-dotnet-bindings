// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;

namespace Swift;

/// <summary>
/// A lazy projection over a <see cref="SwiftArray{TSource}"/>, applying a selector function
/// to each element on access. This is a live view — mutations to the source array are reflected
/// in the projection, and elements are not copied upfront.
/// </summary>
/// <remarks>
/// The projection owns the underlying <see cref="SwiftArray{TSource}"/>, whose SafeHandle holds a
/// retain on the Swift copy-on-write storage. Disposing the projection releases that retain
/// deterministically; otherwise it is released only when the GC finalizes the carrier. The public
/// return type is <see cref="IReadOnlyList{T}"/>, so callers that want deterministic native-storage
/// release must cast to <see cref="IDisposable"/>.
/// </remarks>
/// <typeparam name="TSource">The element type of the source SwiftArray.</typeparam>
/// <typeparam name="TResult">The projected element type.</typeparam>
internal sealed class SwiftArrayProjection<TSource, TResult> : IReadOnlyList<TResult>, IDisposable
{
    private readonly SwiftArray<TSource> _source;
    private readonly Func<TSource, TResult> _selector;

    public SwiftArrayProjection(SwiftArray<TSource> source, Func<TSource, TResult> selector)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
    }

    public int Count => _source.Count;

    public TResult this[int index] => _selector(_source[index]);

    public IEnumerator<TResult> GetEnumerator()
    {
        int count = _source.Count;
        for (int i = 0; i < count; i++)
        {
            yield return _selector(_source[i]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose() => _source.Dispose();
}
