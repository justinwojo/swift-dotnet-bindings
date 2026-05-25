// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Swift;

/// <summary>
/// A lazy projection over a <see cref="SwiftDictionary{TKey, TSource}"/>, applying a value selector
/// function on access. This is a live view — mutations to the source dictionary are reflected
/// in the projection, and entries are not copied upfront.
/// </summary>
/// <typeparam name="TKey">The key type (shared with source).</typeparam>
/// <typeparam name="TSource">The source value type.</typeparam>
/// <typeparam name="TResult">The projected value type.</typeparam>
internal sealed class SwiftDictionaryValueProjection<TKey, TSource, TResult> : IReadOnlyDictionary<TKey, TResult>, IDisposable
    where TKey : notnull
{
    private readonly SwiftDictionary<TKey, TSource> _source;
    private readonly Func<TSource, TResult> _valueSelector;

    public SwiftDictionaryValueProjection(
        SwiftDictionary<TKey, TSource> source,
        Func<TSource, TResult> valueSelector)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _valueSelector = valueSelector ?? throw new ArgumentNullException(nameof(valueSelector));
    }

    public int Count => _source.Count;

    public TResult this[TKey key] => _valueSelector(_source[key]);

    public IEnumerable<TKey> Keys => _source.Keys;

    public IEnumerable<TResult> Values => _source.Values.Select(_valueSelector);

    public bool ContainsKey(TKey key) => _source.ContainsKey(key);

    public bool TryGetValue(TKey key, out TResult value)
    {
        if (_source.TryGetValue(key, out var sourceValue))
        {
            value = _valueSelector(sourceValue);
            return true;
        }
        value = default!;
        return false;
    }

    public IEnumerator<KeyValuePair<TKey, TResult>> GetEnumerator()
    {
        foreach (var kvp in _source)
        {
            yield return new KeyValuePair<TKey, TResult>(kvp.Key, _valueSelector(kvp.Value));
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose() => _source.Dispose();
}

/// <summary>
/// A lazy projection over a <see cref="SwiftDictionary{TSrcKey, TSrcVal}"/>, applying key and value
/// selectors on access. This is a live view — mutations to the source dictionary are reflected
/// in the projection, and entries are not copied upfront.
/// </summary>
/// <typeparam name="TSrcKey">The source key type.</typeparam>
/// <typeparam name="TSrcVal">The source value type.</typeparam>
/// <typeparam name="TResKey">The projected key type.</typeparam>
/// <typeparam name="TResVal">The projected value type.</typeparam>
internal sealed class SwiftDictionaryProjection<TSrcKey, TSrcVal, TResKey, TResVal> : IReadOnlyDictionary<TResKey, TResVal>, IDisposable
    where TSrcKey : notnull
    where TResKey : notnull
{
    private readonly SwiftDictionary<TSrcKey, TSrcVal> _source;
    private readonly Func<TSrcKey, TResKey> _keySelector;
    private readonly Func<TResKey, TSrcKey> _reverseKeySelector;
    private readonly Func<TSrcVal, TResVal> _valueSelector;

    public SwiftDictionaryProjection(
        SwiftDictionary<TSrcKey, TSrcVal> source,
        Func<TSrcKey, TResKey> keySelector,
        Func<TResKey, TSrcKey> reverseKeySelector,
        Func<TSrcVal, TResVal> valueSelector)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        _reverseKeySelector = reverseKeySelector ?? throw new ArgumentNullException(nameof(reverseKeySelector));
        _valueSelector = valueSelector ?? throw new ArgumentNullException(nameof(valueSelector));
    }

    public int Count => _source.Count;

    public TResVal this[TResKey key]
    {
        get
        {
            var sourceKey = _reverseKeySelector(key);
            try { return _valueSelector(_source[sourceKey]); }
            finally { (sourceKey as IDisposable)?.Dispose(); }
        }
    }

    public IEnumerable<TResKey> Keys => _source.Keys.Select(_keySelector);

    public IEnumerable<TResVal> Values => _source.Values.Select(_valueSelector);

    public bool ContainsKey(TResKey key)
    {
        var sourceKey = _reverseKeySelector(key);
        try { return _source.ContainsKey(sourceKey); }
        finally { (sourceKey as IDisposable)?.Dispose(); }
    }

    public bool TryGetValue(TResKey key, out TResVal value)
    {
        var sourceKey = _reverseKeySelector(key);
        try
        {
            if (_source.TryGetValue(sourceKey, out var sourceValue))
            {
                value = _valueSelector(sourceValue);
                return true;
            }
            value = default!;
            return false;
        }
        finally { (sourceKey as IDisposable)?.Dispose(); }
    }

    public IEnumerator<KeyValuePair<TResKey, TResVal>> GetEnumerator()
    {
        foreach (var kvp in _source)
        {
            yield return new KeyValuePair<TResKey, TResVal>(
                _keySelector(kvp.Key), _valueSelector(kvp.Value));
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose() => _source.Dispose();
}
