// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;

namespace Swift.Runtime;

/// <summary>
/// Element conversions for the Swift collections a binding builds from a C# sequence. A Swift
/// collection's builder (<c>FromEnumerable</c>, <c>FromDictionary</c>) copies each element into Swift
/// storage as it reads it, before it asks the sequence for the next one; these conversions rely on that.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SwiftCollectionSource
{
    /// <summary>
    /// Converts each element of <paramref name="source"/> to a value that borrows from it, such as an
    /// Objective-C object's pointer, keeping the element reachable until the builder has copied (and so
    /// retained) the converted value. A converted pointer holds no managed reference, so an element only
    /// a lazy sequence refers to could otherwise be finalized, releasing its only reference, before Swift
    /// copies the pointer. The sequence is read one element at a time, as the builder reads it.
    /// </summary>
    /// <param name="source">The C# sequence.</param>
    /// <param name="selector">The conversion to the collection's element.</param>
    public static IEnumerable<TResult> Borrowing<TSource, TResult>(IEnumerable<TSource> source, Func<TSource, TResult> selector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);
        return BorrowingIterator(source, selector);
    }

    /// <summary>
    /// Converts each element of <paramref name="source"/> to a Swift value built only to be copied into
    /// the collection (an inner collection of a nested one), and disposes that value once the builder
    /// has copied it. Left to its finalizer, the temporary would keep its storage, and every reference
    /// the storage holds, alive after the collection that copied it releases them. Each source element
    /// stays reachable until its value is copied, as in <see cref="Borrowing"/>.
    /// </summary>
    /// <param name="source">The C# sequence.</param>
    /// <param name="selector">The conversion to the collection's element.</param>
    public static IEnumerable<TResult> Consuming<TSource, TResult>(IEnumerable<TSource> source, Func<TSource, TResult> selector)
        where TResult : IDisposable
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);
        return ConsumingIterator(source, selector);
    }

    /// <summary>
    /// Converts each element of <paramref name="source"/> to a dictionary entry whose value is a Swift
    /// value built only to be copied into the dictionary (an inner collection, or an optional of one),
    /// and disposes that value once the builder has copied the entry, as <see cref="Consuming"/> does
    /// for an array's or set's elements. The key is left alone: it borrows from the source element,
    /// which stays reachable until the entry is copied.
    /// </summary>
    /// <param name="source">The C# sequence.</param>
    /// <param name="selector">The conversion to the dictionary's entry.</param>
    public static IEnumerable<KeyValuePair<TKey, TValue>> ConsumingValues<TSource, TKey, TValue>(
        IEnumerable<TSource> source, Func<TSource, KeyValuePair<TKey, TValue>> selector)
        where TValue : IDisposable
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);
        return ConsumingValuesIterator(source, selector);
    }

    private static IEnumerable<KeyValuePair<TKey, TValue>> ConsumingValuesIterator<TSource, TKey, TValue>(
        IEnumerable<TSource> source, Func<TSource, KeyValuePair<TKey, TValue>> selector)
        where TValue : IDisposable
    {
        foreach (var element in source)
        {
            var pair = selector(element);
            try
            {
                yield return pair;
            }
            finally
            {
                // Runs when the builder resumes the sequence, having copied the entry, or abandons it.
                pair.Value?.Dispose();
            }
            GC.KeepAlive(element);
        }
    }

    private static IEnumerable<TResult> BorrowingIterator<TSource, TResult>(IEnumerable<TSource> source, Func<TSource, TResult> selector)
    {
        foreach (var element in source)
        {
            yield return selector(element);
            // Resumed only after the builder has copied the value.
            GC.KeepAlive(element);
        }
    }

    private static IEnumerable<TResult> ConsumingIterator<TSource, TResult>(IEnumerable<TSource> source, Func<TSource, TResult> selector)
        where TResult : IDisposable
    {
        foreach (var element in source)
        {
            var value = selector(element);
            try
            {
                yield return value;
            }
            finally
            {
                // Runs when the builder resumes the sequence, having copied the value, or abandons it.
                value.Dispose();
            }
            GC.KeepAlive(element);
        }
    }
}
