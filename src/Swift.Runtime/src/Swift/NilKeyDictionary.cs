// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Swift;

/// <summary>Builds <see cref="NilKeyDictionary{TKey, TValue}"/>s.</summary>
public static class NilKeyDictionary
{
    /// <summary>
    /// A dictionary of one entry per element of <paramref name="source"/>, keyed and valued by the
    /// selectors, a <see langword="null"/> key among them: <c>Enumerable.ToDictionary</c> for an
    /// optional key type.
    /// </summary>
    /// <exception cref="ArgumentException">Two elements have equal keys.</exception>
    public static NilKeyDictionary<TKey, TValue> From<TSource, TKey, TValue>(IEnumerable<TSource> source, Func<TSource, TKey> keySelector, Func<TSource, TValue> valueSelector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(valueSelector);
        var result = new NilKeyDictionary<TKey, TValue>();
        foreach (var element in source)
            result.Add(keySelector(element), valueSelector(element));
        return result;
    }
}

/// <summary>
/// A dictionary that, like a Swift dictionary over an optional key type (<c>[Node?: V]</c>,
/// <c>[Int?: V]</c>), can hold <c>nil</c> as a key: a <see langword="null"/> key is one entry like any
/// other. A .NET <see cref="Dictionary{TKey, TValue}"/> refuses a null key, so a binding reads an
/// optional-keyed Swift dictionary into this type, and a caller passes one to hand Swift a
/// <c>nil</c> key.
/// </summary>
/// <remarks>
/// Every key other than <see langword="null"/> is held in a <see cref="Dictionary{TKey, TValue}"/>
/// with the given comparer (the key type's own equality by default).
/// </remarks>
/// <typeparam name="TKey">The key type, a nullable type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class NilKeyDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
{
    private readonly Dictionary<NonNullKey, TValue> _entries;
    private bool _hasNil;
    private TValue _nilValue = default!;

    /// <summary>An empty dictionary that compares keys by their own equality.</summary>
    public NilKeyDictionary()
        : this(comparer: null)
    {
    }

    /// <summary>An empty dictionary that compares keys other than <see langword="null"/> with <paramref name="comparer"/>.</summary>
    public NilKeyDictionary(IEqualityComparer<TKey>? comparer)
        => _entries = new Dictionary<NonNullKey, TValue>(new NonNullKeyComparer(comparer ?? EqualityComparer<TKey>.Default));

    /// <summary>A dictionary of the given entries, a <see langword="null"/> key among them.</summary>
    /// <exception cref="ArgumentException">Two entries have equal keys.</exception>
    public NilKeyDictionary(IEnumerable<KeyValuePair<TKey, TValue>> entries)
        : this()
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
            Add(entry.Key, entry.Value);
    }

    /// <inheritdoc/>
    public int Count => _entries.Count + (_hasNil ? 1 : 0);

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <inheritdoc/>
    public TValue this[TKey key]
    {
        get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"The key '{(key is null ? "nil" : key.ToString())}' is not in the dictionary.");
        set
        {
            if (key is null)
            {
                _nilValue = value;
                _hasNil = true;
            }
            else
            {
                _entries[new NonNullKey(key)] = value;
            }
        }
    }

    /// <inheritdoc/>
    public ICollection<TKey> Keys
    {
        get
        {
            var keys = new List<TKey>(Count);
            foreach (var entry in this)
                keys.Add(entry.Key);
            return keys;
        }
    }

    /// <inheritdoc/>
    public ICollection<TValue> Values
    {
        get
        {
            var values = new List<TValue>(Count);
            foreach (var entry in this)
                values.Add(entry.Value);
            return values;
        }
    }

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

    /// <inheritdoc/>
    public void Add(TKey key, TValue value)
    {
        if (key is null)
        {
            if (_hasNil)
                throw new ArgumentException("The dictionary already holds the nil key.", nameof(key));
            _nilValue = value;
            _hasNil = true;
            return;
        }
        _entries.Add(new NonNullKey(key), value);
    }

    /// <inheritdoc/>
    public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

    /// <inheritdoc/>
    public void Clear()
    {
        _entries.Clear();
        _hasNil = false;
        _nilValue = default!;
    }

    /// <inheritdoc/>
    public bool Contains(KeyValuePair<TKey, TValue> item)
        => TryGetValue(item.Key, out var value) && EqualityComparer<TValue>.Default.Equals(value, item.Value);

    /// <inheritdoc/>
    public bool ContainsKey(TKey key) => key is null ? _hasNil : _entries.ContainsKey(new NonNullKey(key));

    /// <inheritdoc/>
    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        if (array.Length - arrayIndex < Count)
            throw new ArgumentException("The array is too small to hold the dictionary's entries.", nameof(array));
        foreach (var entry in this)
            array[arrayIndex++] = entry;
    }

    /// <inheritdoc/>
    public bool Remove(TKey key)
    {
        if (key is not null)
            return _entries.Remove(new NonNullKey(key));
        if (!_hasNil)
            return false;
        _hasNil = false;
        _nilValue = default!;
        return true;
    }

    /// <inheritdoc/>
    public bool Remove(KeyValuePair<TKey, TValue> item) => Contains(item) && Remove(item.Key);

    /// <inheritdoc/>
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (key is null)
        {
            value = _hasNil ? _nilValue : default!;
            return _hasNil;
        }
        return _entries.TryGetValue(new NonNullKey(key), out value);
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        if (_hasNil)
            yield return new KeyValuePair<TKey, TValue>(default!, _nilValue);
        foreach (var entry in _entries)
            yield return new KeyValuePair<TKey, TValue>(entry.Key.Value, entry.Value);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // A key other than null, so the inner dictionary never sees one.
    private readonly struct NonNullKey
    {
        public NonNullKey(TKey value) => Value = value;

        public TKey Value { get; }
    }

    private sealed class NonNullKeyComparer : IEqualityComparer<NonNullKey>
    {
        private readonly IEqualityComparer<TKey> _comparer;

        public NonNullKeyComparer(IEqualityComparer<TKey> comparer) => _comparer = comparer;

        public bool Equals(NonNullKey x, NonNullKey y) => _comparer.Equals(x.Value, y.Value);

        public int GetHashCode(NonNullKey key) => _comparer.GetHashCode(key.Value!);
    }
}
