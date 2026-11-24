// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift;

/// <summary>
/// Represents a Swift dictionary.
/// </summary>
/// <typeparam name="TKey">The key type (must be Hashable in Swift).</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public class SwiftDictionary<TKey, TValue> : ISwiftObject, ISwiftStruct, IReadOnlyDictionary<TKey, TValue>, IDisposable
    where TKey : notnull
{
    // Lazy initialization to avoid calling Swift runtime during static construction.
    // This prevents crashes when TKey/TValue is an existential container type, where
    // swift_getExistentialTypeMetadata called from .cctor triggers a Mono JIT/async assertion.
    private static nuint? _cachedKeySize;
    private static nuint? _cachedValueSize;

    private static nuint KeySize
    {
        get
        {
            _cachedKeySize ??= KeyTypeMetadata.Size;
            return _cachedKeySize.Value;
        }
    }

    private static nuint ValueSize
    {
        get
        {
            _cachedValueSize ??= ValueTypeMetadata.Size;
            return _cachedValueSize.Value;
        }
    }

    private SwiftSafeHandle<SwiftDictionary<TKey, TValue>> _payload;
    private bool _disposed;

    public SwiftSafeHandle<SwiftDictionary<TKey, TValue>> Payload
    {
        get { ThrowIfDisposed(); return _payload; }
    }

    public unsafe PayloadBuffer<IntPtr> PayloadBuffer
    {
        get { ThrowIfDisposed(); return new PayloadBuffer<IntPtr>(_payload); }
    }

    private static Dictionary<Type, string> _protocolConformanceSymbols;

    static SwiftDictionary()
    {
        _protocolConformanceSymbols = new Dictionary<Type, string>
        {
            { typeof(ISwiftCollection), "$sSDyq_xGSlsMc" }
        };

        // On NativeAOT, eagerly populate the type metadata cache during type init.
        // Reflection on explicit interface implementations of generic types
        // (ISwiftObject.GetTypeMetadata) may fail on NativeAOT, so callers that go
        // through TypeMetadata.GetTypeMetadataOrThrow<SwiftDictionary<TKey,TValue>>()
        // (e.g., SwiftOptional<SwiftDictionary<...>>.cctor field initializer) cannot
        // resolve metadata via reflection. Direct dispatch via SwiftObjectHelper<T>
        // populates the cache without reflection.
        // On Mono, skip this — calling Swift runtime during static construction can
        // trigger JIT assertions when TKey/TValue is an existential container type.
        if (SwiftRuntimeInfo.IsNativeAotRuntime)
        {
            TryEagerInitialize();
        }
    }

    /// <summary>
    /// Attempts eager initialization of metadata and factory registration for NativeAOT.
    /// Mirrors SwiftArray.TryEagerInitialize. Returns true on success, false if it
    /// fell back to lazy init (e.g., when TKey/TValue metadata isn't yet available).
    /// </summary>
    internal static bool TryEagerInitialize()
    {
        try
        {
            NativeAotInitialize();
            return true;
        }
        catch (Exception)
        {
            // TKey/TValue metadata may be unavailable during type init for certain types
            // (e.g., ExistentialContainer types require protocol descriptor pointers).
            // Fall back to lazy initialization — metadata will be fetched on first use.
            System.Diagnostics.Debug.WriteLine(
                $"SwiftDictionary<{typeof(TKey).Name}, {typeof(TValue).Name}>: NativeAotInitialize skipped, using lazy init");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NativeAotInitialize()
    {
        // SwiftObjectHelper<T>.GetTypeMetadata() → DirectDispatchGetTypeMetadata():
        // - Registers NewFromPayload factory in NewFromPayloadDispatcher
        // - Caches metadata in TypeMetadata.Cache
        var _ = SwiftObjectHelper<SwiftDictionary<TKey, TValue>>.GetTypeMetadata();
    }

    IntPtr ISwiftObject.SwiftHandle
    {
        get { ThrowIfDisposed(); return _payload.DangerousGetHandle(); }
    }

    // Non-reflective borrowed-marshal finalizer suppression (Finding 56a). See ISwiftObject.SuppressPayloadFinalizer.
    void ISwiftObject.SuppressPayloadFinalizer() => global::System.GC.SuppressFinalize(_payload);

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        // Uses HashableConformanceRegistry for NativeAOT safety — avoids MakeGenericType
        // reflection for unconstrained TKey types (e.g., SwiftDictionary<IntPtr, EC0>).
        var witnessTable = HashableConformanceRegistry.GetHashableWitnessTable<TKey>();
        return TypeMetadata.Cache.GetOrAdd(typeof(SwiftDictionary<TKey, TValue>), _ =>
            SwiftDictionaryPInvokes.PInvoke_getMetadata(TypeMetadataRequest.Complete, KeyTypeMetadata, ValueTypeMetadata, witnessTable));
    }

    static TypeMetadata KeyTypeMetadata
    {
        get => TypeMetadata.GetTypeMetadataOrThrow<TKey>();
    }

    static TypeMetadata ValueTypeMetadata
    {
        get => TypeMetadata.GetTypeMetadataOrThrow<TValue>();
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new SwiftDictionary<TKey, TValue>(handle);
    }

    /// <inheritdoc/>
    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Copy;

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        ThrowIfDisposed();
        var metadata = SwiftObjectHelper<SwiftDictionary<TKey, TValue>>.GetTypeMetadata();
        if ((int)metadata.Size > swiftDestSpan.Length)
        {
            throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
        }
        unsafe
        {
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure the payload is valid before making copy
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, (void*)_payload.DangerousGetHandle(), metadata);
                    return (int)metadata.Size;
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
            }
        }
    }

    /// <summary>
    /// Gets the protocol conformance descriptor for the given type.
    /// </summary>
    /// <typeparam name="TProtocol"></typeparam>
    /// <returns></returns>
    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
        where TProtocol : class
    {
        if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
        {
            throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type SwiftDictionary and protocol {typeof(TProtocol).Name}, but no conformance was found.");
        }
        return ProtocolConformanceDescriptor.LoadFromSymbol("/usr/lib/swift/libswiftCore.dylib", symbolName);
    }

    /// <summary>
    /// Constructs a new SwiftDictionary from the given handle.
    /// </summary>
    unsafe SwiftDictionary(IntPtr handle)
    {
        var metadata = SwiftObjectHelper<SwiftDictionary<TKey, TValue>>.GetTypeMetadata();
        IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc(metadata.Size);
        metadata.ValueWitnessTable->InitializeWithCopy((void*)bufferPtr, (void*)handle, metadata);
        _payload = new SwiftSafeHandle<SwiftDictionary<TKey, TValue>>(bufferPtr);
    }

    private static readonly IntPtr _emptyDictionarySingleton = LoadEmptyDictionarySingleton();

    private static IntPtr LoadEmptyDictionarySingleton()
    {
        if (!NativeLibrary.TryLoad(KnownLibraries.SwiftCore, typeof(SwiftDictionary<TKey, TValue>).Assembly, null, out var lib))
            throw new SwiftRuntimeException("Unable to load libswiftCore.dylib");
        if (!NativeLibrary.TryGetExport(lib, "_swiftEmptyDictionarySingleton", out var addr))
            throw new SwiftRuntimeException("Unable to find _swiftEmptyDictionarySingleton");
        return addr;
    }

    /// <summary>
    /// Constructs a new empty SwiftDictionary.
    /// </summary>
    public unsafe SwiftDictionary()
    {
        // An empty Swift Dictionary is a single pointer to the global
        // _swiftEmptyDictionarySingleton storage. We retain the singleton
        // so that the SafeHandle's release is balanced.
        Arc.Retain(_emptyDictionarySingleton);
        IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc((nuint)sizeof(IntPtr));
        *(IntPtr*)bufferPtr = _emptyDictionarySingleton;
        _payload = new SwiftSafeHandle<SwiftDictionary<TKey, TValue>>(bufferPtr);
    }

    /// <summary>
    /// Gets the number of key-value pairs in the dictionary.
    /// </summary>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            using PayloadBuffer<IntPtr> disposable = PayloadBuffer;
            var witnessTable = HashableConformanceRegistry.GetHashableWitnessTable<TKey>();
            int result = (int)SwiftDictionaryPInvokes.Count(disposable.Buffer, KeyTypeMetadata, ValueTypeMetadata, witnessTable);
            return result;
        }
    }

    /// <summary>
    /// Gets or sets the value associated with the specified key.
    /// Getting throws <see cref="KeyNotFoundException"/> if the key is not found.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The key does not exist in the dictionary.</exception>
    public unsafe TValue this[TKey key]
    {
        get
        {
            ThrowIfDisposed();
            if (!TryGetValue(key, out var value))
                throw new KeyNotFoundException($"The given key was not present in the dictionary.");
            return value;
        }
        set
        {
            ThrowIfDisposed();
            var metadata = SwiftObjectHelper<SwiftDictionary<TKey, TValue>>.GetTypeMetadata();
            var optionalMetadata = SwiftObjectHelper<SwiftOptional<TValue>>.GetTypeMetadata();

            bool success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                UpdateValueUnsafe(key, value, metadata, optionalMetadata, _payload.DangerousGetHandle());
            }
            finally
            {
                if (success)
                    _payload.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Performs a single <c>updateValue(_:forKey:)</c> P/Invoke without acquiring the
    /// payload SafeHandle. The caller must hold a successful <c>DangerousAddRef</c>
    /// scope across the call.
    /// </summary>
    private static unsafe void UpdateValueUnsafe(
        TKey key,
        TValue value,
        TypeMetadata dictionaryMetadata,
        TypeMetadata optionalValueMetadata,
        IntPtr handle)
    {
        // `updateValue(_:forKey:)` consumes the value (`__owned`) but borrows the key, so the owned
        // key copy marshalling makes is this frame's to destroy once the call returns.
        Span<byte> keySpan = stackalloc byte[(int)KeySize];
        SwiftMarshal.MarshalToSwift(key, ref keySpan);
        IntPtr keyPayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(keySpan));

        // Allocate space for the optional old-value return.
        void* resultPayload = null;
        bool slotLive = false;
        try
        {
            Span<byte> valueSpan = stackalloc byte[(int)ValueSize];
            SwiftMarshal.MarshalToSwift(value, ref valueSpan);
            IntPtr valuePayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(valueSpan));

            resultPayload = NativeMemory.Alloc(optionalValueMetadata.Size);
            // Cdecl-wrapped to bypass the Mac Catalyst-x64 workload Mono
            // CallConvSwift trampoline; see SwiftCollectionCdeclWrappers.
            SwiftCollectionCdeclWrappers.DictUpdateValue(
                (IntPtr)resultPayload,
                valuePayload,
                keyPayload,
                dictionaryMetadata,
                handle);
            // updateValue always initializes Optional<TValue>: .some owns the replaced value,
            // while .none is still a valid initialized Optional. The public setter discards that
            // result, so destroy the complete Optional before freeing its storage.
            slotLive = true;
        }
        finally
        {
            if (slotLive)
                optionalValueMetadata.ValueWitnessTable->Destroy(resultPayload, optionalValueMetadata);
            if (resultPayload != null)
                NativeMemory.Free(resultPayload);
            if (SwiftMarshal.MarshalsOwnedCopy(key))
                KeyTypeMetadata.ValueWitnessTable->Destroy((void*)keyPayload, KeyTypeMetadata);
        }
    }

    /// <summary>
    /// Tries to get the value associated with the specified key.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="value">When this method returns, contains the value associated with the specified key,
    /// or <c>default(TValue)</c> if the key was not found.</param>
    /// <returns><c>true</c> if the key was found; otherwise, <c>false</c>.</returns>
    public unsafe bool TryGetValue(TKey key, out TValue value)
    {
        ThrowIfDisposed();
        var optionalMetadata = SwiftObjectHelper<SwiftOptional<TValue>>.GetTypeMetadata();
        void* resultPayload = NativeMemory.Alloc(optionalMetadata.Size);
        bool slotLive = false; // resultPayload holds an initialized .some whose slot is unconsumed
        try
        {
            if (!LookUp(key, resultPayload, optionalMetadata))
            {
                value = default!;
                return false;
            }

            // Move the +1 the Get wrote into the Optional<TValue> buffer out into the wrapper
            // (buffer is freed raw on success, not Destroyed). MarshalMovedValueFromSlot consumes the
            // slot atomically (class deref-transfer, or ADOPT/COPY copy-then-Destroy); `slotLive`
            // guards the exception path so a throw before consumption releases the intact slot's +1.
            slotLive = true;
            value = SwiftMarshal.MarshalMovedValueFromSlot<TValue>(resultPayload, ValueTypeMetadata);
            slotLive = false;
            return true;
        }
        finally
        {
            if (slotLive)
                optionalMetadata.ValueWitnessTable->Destroy(resultPayload, optionalMetadata);
            NativeMemory.Free(resultPayload);
        }
    }

    /// <summary>
    /// Runs Swift's subscript getter for <paramref name="key"/>, writing the owned
    /// <c>Optional&lt;TValue&gt;</c> it returns into <paramref name="resultPayload"/>, and returns
    /// whether that Optional holds a value. On <c>.none</c> the buffer holds nothing to release; on
    /// <c>.some</c> the caller owns the value's +1 and must move it out or destroy the buffer.
    /// </summary>
    private unsafe bool LookUp(TKey key, void* resultPayload, TypeMetadata optionalMetadata)
    {
        using PayloadBuffer<IntPtr> disposable = PayloadBuffer;
        var witnessTable = HashableConformanceRegistry.GetHashableWitnessTable<TKey>();

        // Marshalling initializes the key buffer with an owned copy (see
        // SwiftMarshal.MarshalsOwnedCopy), and the subscript getter borrows its key, so the copy is
        // this frame's to destroy once the call returns.
        Span<byte> keySpan = stackalloc byte[(int)KeySize];
        IntPtr keyPayload = IntPtr.Zero;
        try
        {
            SwiftMarshal.MarshalToSwift(key, ref keySpan);
            keyPayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(keySpan));

            SwiftDictionaryPInvokes.Get(
                new SwiftIndirectResult(resultPayload),
                keyPayload,
                disposable.Buffer,
                KeyTypeMetadata,
                ValueTypeMetadata,
                witnessTable);
        }
        finally
        {
            if (keyPayload != IntPtr.Zero && SwiftMarshal.MarshalsOwnedCopy(key))
                KeyTypeMetadata.ValueWitnessTable->Destroy((void*)keyPayload, KeyTypeMetadata);
        }

        // Use Optional metadata to read the enum tag
        return (SwiftOptionalCases)optionalMetadata.ValueWitnessTable->GetEnumTag((byte*)resultPayload, optionalMetadata)
            != SwiftOptionalCases.None;
    }

    /// <summary>
    /// Determines whether the dictionary contains the specified key.
    /// </summary>
    /// <param name="key">The key to check.</param>
    /// <returns><c>true</c> if the dictionary contains the key; otherwise, <c>false</c>.</returns>
    public unsafe bool ContainsKey(TKey key)
    {
        ThrowIfDisposed();
        // The lookup returns the value at +1 and nothing here hands it out, so it is destroyed
        // in place rather than materialized and dropped.
        var optionalMetadata = SwiftObjectHelper<SwiftOptional<TValue>>.GetTypeMetadata();
        void* resultPayload = NativeMemory.Alloc(optionalMetadata.Size);
        bool written = false;
        try
        {
            bool found = LookUp(key, resultPayload, optionalMetadata);
            written = true;
            return found;
        }
        finally
        {
            if (written)
                optionalMetadata.ValueWitnessTable->Destroy(resultPayload, optionalMetadata);
            NativeMemory.Free(resultPayload);
        }
    }

    /// <summary>
    /// Gets a collection containing the keys in the dictionary.
    /// </summary>
    public IEnumerable<TKey> Keys
    {
        get
        {
            ThrowIfDisposed();
            return GetKeys();
        }
    }

    private IEnumerable<TKey> GetKeys()
    {
        foreach (var kvp in CollectEntries(EntryParts.Key))
            yield return kvp.Key;
    }

    /// <summary>
    /// Gets a collection containing the values in the dictionary.
    /// </summary>
    /// <remarks>
    /// A value read out of the collection is the reader's: Swift hands every value over at +1. A raw
    /// existential container (an <c>ExistentialContainer…</c> value type) has no finalizer, so whoever
    /// reads one must destroy it. For such a value the collection is also a read-only
    /// <see cref="IList{T}"/>, so LINQ's positional operators (<c>Last</c>, <c>ElementAt</c>, their
    /// <c>OrDefault</c> forms) read only the value they return; the others are released.
    /// </remarks>
    public IEnumerable<TValue> Values
    {
        get
        {
            ThrowIfDisposed();
            return IsRawCarrier<TValue>()
                ? new RawValueList(this)
                : new ValueSnapshot<TValue>(this, static value => value);
        }
    }

    /// <summary>
    /// Whether <typeparamref name="T"/> is a raw existential carrier: a value type that holds a Swift
    /// +1 and nothing releases but an explicit destroy.
    /// </summary>
    private static bool IsRawCarrier<T>()
        => typeof(T).IsValueType
            && (typeof(IExistentialContainer).IsAssignableFrom(typeof(T)) || typeof(T) == typeof(ClassExistentialContainer1));

    /// <summary>
    /// <see cref="Values"/> passed through <paramref name="selector"/>, which runs when an entry's
    /// value is first read. A projection's <c>Values</c> must not be a LINQ <c>Select</c> over
    /// <see cref="Values"/>: <c>Last()</c> reads every raw value and converts only the final one,
    /// so a raw existential container it drops is never released. Converted on read, a dropped
    /// value is an owning wrapper that releases its +1 when it is finalized.
    /// </summary>
    internal IEnumerable<TResult> ProjectValues<TResult>(Func<TValue, TResult> selector)
    {
        ThrowIfDisposed();
        return new ValueSnapshot<TResult>(this, selector);
    }

    /// <summary>
    /// Returns an enumerator that iterates through the dictionary's key-value pairs.
    /// Uses Swift's Dictionary.makeIterator() and Dictionary.Iterator.next() P/Invokes.
    /// </summary>
    /// <remarks>
    /// An entry is the reader's once its <see cref="IEnumerator{T}.Current"/> is read; the entries an
    /// enumeration steps over or stops before are released. A raw existential container value read
    /// this way must be destroyed by the reader, so an operator that reads every pair and keeps one
    /// (LINQ's <c>Last</c> over the pairs) leaves the others unreleased. Read such values positionally
    /// through <see cref="Values"/>, or look them up by key.
    /// </remarks>
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        ThrowIfDisposed();
        return new SnapshotEnumerator<KeyValuePair<TKey, TValue>>(
            CollectEntries(EntryParts.Key | EntryParts.Value), static kvp => kvp);
    }

    /// <summary>
    /// <see cref="Values"/>: each enumeration takes its own value snapshot when it starts.
    /// </summary>
    private sealed class ValueSnapshot<TOut> : IEnumerable<TOut>
    {
        private readonly SwiftDictionary<TKey, TValue> _dictionary;
        private readonly Func<TValue, TOut> _selector;

        public ValueSnapshot(SwiftDictionary<TKey, TValue> dictionary, Func<TValue, TOut> selector)
        {
            _dictionary = dictionary;
            _selector = selector;
        }

        public IEnumerator<TOut> GetEnumerator()
        {
            _dictionary.ThrowIfDisposed();
            var selector = _selector;
            return new SnapshotEnumerator<TOut>(_dictionary.CollectEntries(EntryParts.Value), kvp => selector(kvp.Value));
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// <see cref="Values"/> for a raw existential carrier. Enumerating it hands out a snapshot's
    /// values exactly as <see cref="ValueSnapshot{TOut}"/> does. As an <see cref="IList{T}"/> it also
    /// lets LINQ's <c>Last</c> and <c>ElementAt</c> index the one value they return, instead of reading
    /// every value and dropping all but one: an enumerated value counts as handed out once read, and a
    /// dropped raw container is never released. Each indexed read walks the dictionary up to the value
    /// it returns and releases the values before it, so it costs the index's position and sees the
    /// dictionary as it is at that moment, as any operator that indexes a list does.
    /// <see cref="Contains"/> and <see cref="IndexOf"/> hand out nothing and release them all.
    /// </summary>
    private sealed class RawValueList : IList<TValue>
    {
        private readonly SwiftDictionary<TKey, TValue> _dictionary;

        public RawValueList(SwiftDictionary<TKey, TValue> dictionary) => _dictionary = dictionary;

        public int Count => _dictionary.Count;

        public bool IsReadOnly => true;

        public TValue this[int index]
        {
            get
            {
                if (index < 0)
                    throw new ArgumentOutOfRangeException(nameof(index));
                // Stop at the entry returned: the walk fetches, and so releases, only the ones before it.
                var entries = TakeSnapshot(index == int.MaxValue ? int.MaxValue : index + 1);
                if (index >= entries.Count)
                {
                    ReleaseAllExcept(entries, -1);
                    throw new ArgumentOutOfRangeException(nameof(index));
                }
                TValue value = entries[index].Value;
                ReleaseAllExcept(entries, index);
                return value;
            }
            set => throw ReadOnly();
        }

        public IEnumerator<TValue> GetEnumerator()
            => new SnapshotEnumerator<TValue>(TakeSnapshot(), static kvp => kvp.Value);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public int IndexOf(TValue item)
        {
            var entries = TakeSnapshot();
            var comparer = EqualityComparer<TValue>.Default;
            int found = -1;
            for (int i = 0; i < entries.Count && found < 0; i++)
            {
                if (comparer.Equals(entries[i].Value, item))
                    found = i;
            }
            ReleaseAllExcept(entries, -1);
            return found;
        }

        public bool Contains(TValue item) => IndexOf(item) >= 0;

        public void CopyTo(TValue[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);
            var entries = TakeSnapshot();
            if (arrayIndex < 0 || array.Length - arrayIndex < entries.Count)
            {
                ReleaseAllExcept(entries, -1);
                throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            }
            for (int i = 0; i < entries.Count; i++)
                array[arrayIndex + i] = entries[i].Value;
        }

        public void Add(TValue item) => throw ReadOnly();
        public void Clear() => throw ReadOnly();
        public void Insert(int index, TValue item) => throw ReadOnly();
        public bool Remove(TValue item) => throw ReadOnly();
        public void RemoveAt(int index) => throw ReadOnly();

        private List<KeyValuePair<TKey, TValue>> TakeSnapshot(int limit = int.MaxValue)
        {
            _dictionary.ThrowIfDisposed();
            return _dictionary.CollectEntries(EntryParts.Value, limit);
        }

        private static void ReleaseAllExcept(List<KeyValuePair<TKey, TValue>> entries, int kept)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (i != kept)
                    ReleaseUnreadCarrier(entries[i].Value, ValueTypeMetadata);
            }
        }

        private static NotSupportedException ReadOnly()
            => new NotSupportedException("The values of a SwiftDictionary are read-only.");
    }

    /// <summary>
    /// Hands out a snapshot's entries. Every value of the snapshot already holds the +1 the Swift
    /// iterator returned, and an entry counts as handed out once its <see cref="Current"/> is read:
    /// the first read converts it, once, and later reads return the same result.
    /// An entry the enumeration steps over unread (LINQ's <c>ElementAt</c>) is released when
    /// <see cref="MoveNext"/> leaves it, and the entries still ahead
    /// when the enumerator is disposed (a <c>break</c>, a <c>First()</c>, a consumer's conversion
    /// that throws, or no enumeration at all) are released then: a raw existential container has no
    /// finalizer and nothing else would. Keys need no such pass, since a key is a concrete
    /// <c>Hashable</c> value, never a raw container.
    /// </summary>
    private sealed class SnapshotEnumerator<TOut> : IEnumerator<TOut>
    {
        private readonly List<KeyValuePair<TKey, TValue>> _entries;
        private readonly Func<KeyValuePair<TKey, TValue>, TOut> _part;
        private int _index = -1;
        private bool _currentRead;
        private TOut _current = default!;

        public SnapshotEnumerator(List<KeyValuePair<TKey, TValue>> entries, Func<KeyValuePair<TKey, TValue>, TOut> part)
        {
            _entries = entries;
            _part = part;
        }

        public TOut Current
        {
            get
            {
                if ((uint)_index >= (uint)_entries.Count)
                    return default!;
                if (!_currentRead)
                {
                    // Handed out before the conversion runs: a conversion that throws has taken
                    // the value (an owned unbox destroys it either way), so it is not released again.
                    _currentRead = true;
                    _current = default!;
                    _current = _part(_entries[_index]);
                }
                return _current;
            }
        }

        object? IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_index >= _entries.Count)
                return false;
            // Leave the entry before releasing it, so a release that reenters (a deinit that
            // disposes this enumerator) cannot release the same entry again.
            int leaving = _index;
            bool releaseLeaving = leaving >= 0 && !_currentRead;
            _index++;
            _currentRead = false;
            _current = default!;
            if (releaseLeaving)
                ReleaseUnreadCarrier(_entries[leaving].Value, ValueTypeMetadata);
            return _index < _entries.Count;
        }

        public void Reset() => throw new NotSupportedException("A dictionary snapshot is enumerated once.");

        public void Dispose()
        {
            if (_index >= _entries.Count)
                return;
            int first = _index < 0 ? 0 : _currentRead ? _index + 1 : _index;
            _index = _entries.Count;
            for (int i = first; i < _entries.Count; i++)
                ReleaseUnreadCarrier(_entries[i].Value, ValueTypeMetadata);
        }
    }

    /// <summary>
    /// Releases the +1 a snapshot moved into a raw existential container that was never handed out.
    /// The container carries the existential's bytes as Swift wrote them, so writing it back
    /// (bitwise, at +0) and destroying it releases exactly that +1. A Swift wrapper is left alone: it
    /// releases its own reference when it is finalized, and a class wrapper may be an instance the
    /// caller already holds.
    /// </summary>
    private static unsafe void ReleaseUnreadCarrier<T>(T value, TypeMetadata metadata)
    {
        if (value is not (IExistentialContainer or ClassExistentialContainer1))
            return;
        int size = (int)metadata.Size;
        void* buffer = NativeMemory.AllocZeroed((nuint)size);
        try
        {
            var span = new Span<byte>(buffer, size);
            SwiftMarshal.MarshalToSwift(value, ref span);
            metadata.ValueWitnessTable->Destroy(buffer, metadata);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    /// <summary>The halves of each entry a snapshot hands out.</summary>
    [Flags]
    private enum EntryParts
    {
        Key = 1,
        Value = 2,
    }

    /// <summary>
    /// Collects all key-value pairs from the Swift dictionary using the iterator P/Invokes.
    /// Separated from GetEnumerator because yield return cannot be used in unsafe methods.
    /// The iterator returns each key and value at +1. A half named in <paramref name="parts"/> is
    /// moved into the entry; the other half is destroyed in its slot and left <c>default</c> in the
    /// entry, so a <see cref="Keys"/> or <see cref="Values"/> read releases what it does not return.
    /// </summary>
    private unsafe List<KeyValuePair<TKey, TValue>> CollectEntries(EntryParts parts, int limit = int.MaxValue)
    {
        var result = new List<KeyValuePair<TKey, TValue>>();
        var witnessTable = HashableConformanceRegistry.GetHashableWitnessTable<TKey>();

        // Get the metadata for Dictionary.Iterator<TKey, TValue>
        var iteratorMetadata = SwiftDictionaryPInvokes.PInvoke_getIteratorMetadata(
            TypeMetadataRequest.Complete, KeyTypeMetadata, ValueTypeMetadata, witnessTable);

        // Get tuple (Key, Value) metadata for proper layout (element offsets).
        // Using Swift runtime metadata instead of manual AlignTo() ensures correct offsets
        // for all type combinations (different alignments, sizes).
        var tupleMetadata = TypeMetadata.GetTupleTypeMetadataFromElements(KeyTypeMetadata, ValueTypeMetadata);
        var tupleMetaPtr = tupleMetadata.AsTupleMetadata();
        nuint valueOffset = tupleMetaPtr->GetElementOffset(1);

        // Get Optional<(Key, Value)> metadata for proper .none detection via GetEnumTag.
        // Byte-zero inspection is invalid: a valid .some tuple can contain all-zero bytes
        // (e.g., key=0, value=0), and pointer-based optionals use extra inhabitants.
        var optionalTupleMetadata = PInvokesForSwiftOptional._MetadataAccessor(
            TypeMetadataRequest.Complete, tupleMetadata);

        // Allocate the iterator buffer
        void* iteratorBuffer = NativeMemory.AllocZeroed(iteratorMetadata.Size);
        try
        {
            // Call Dictionary.makeIterator() — writes the iterator into the indirect result.
            // Arc.Retain the dictionary storage before calling makeIterator. The iterator
            // takes ownership of the storage reference passed via the dictionary value
            // parameter. Without this retain, the iterator's VWT Destroy would over-release
            // the dictionary's storage, causing a crash on dict.Dispose().
            using (PayloadBuffer<IntPtr> disposable = PayloadBuffer)
            {
                Arc.Retain((IntPtr)disposable.Buffer);
                SwiftDictionaryPInvokes.MakeIterator(
                    new SwiftIndirectResult(iteratorBuffer),
                    disposable.Buffer,
                    KeyTypeMetadata,
                    ValueTypeMetadata,
                    witnessTable);
            }

            void* nextResultBuffer = NativeMemory.Alloc(optionalTupleMetadata.Size);
            // The buffer holds Optional<(TKey, TValue)>. Each MarshalMovedValueFromSlot consumes ITS
            // OWN slot: a class/String/POD/existential slot transfers its +1 via a bitwise read; an
            // ADOPT/COPY ref-backed slot copies out an independent wrapper and value-witness-Destroys
            // the slot. On the SUCCESS path nothing further is destroyed (the buffer is freed raw).
            // On an EXCEPTION mid-extraction we must release ONLY the slots not yet consumed — re-
            // destroying an already-consumed slot (a transferred class pointer, or an already-
            // Destroyed ADOPT/COPY slot) via a whole-tuple Destroy double-frees it. `bufferLive` marks
            // an initialized .some still holding unconsumed slots; `keyMoved`/`valueMoved` track per-
            // slot consumption so the finally destroys exactly the intact remainder.
            bool bufferLive = false;
            bool keyMoved = false;
            bool valueMoved = false;
            try
            {
                while (result.Count < limit)
                {
                    // Call Dictionary.Iterator.next() — mutates the iterator in-place.
                    // Cdecl-wrapped to bypass the Mac Catalyst-x64 workload Mono
                    // CallConvSwift trampoline; see SwiftCollectionCdeclWrappers.
                    SwiftCollectionCdeclWrappers.DictIteratorNext(
                        (IntPtr)nextResultBuffer,
                        iteratorMetadata,
                        (IntPtr)iteratorBuffer);
                    bufferLive = true;
                    keyMoved = false;
                    valueMoved = false;

                    // Use VWT GetEnumTag to check Optional.none — the only correct way
                    // to detect .none for all type combinations (pointer, value, existential).
                    var tag = (SwiftOptionalCases)optionalTupleMetadata.ValueWitnessTable->GetEnumTag(
                        (byte*)nextResultBuffer, optionalTupleMetadata);
                    if (tag == SwiftOptionalCases.None)
                    {
                        // .none has no ref-counted payload to leak, but destroy for correctness
                        optionalTupleMetadata.ValueWitnessTable->Destroy(nextResultBuffer, optionalTupleMetadata);
                        bufferLive = false;
                        break;
                    }

                    // Move key from offset 0, value from the tuple metadata offset. Each helper
                    // consumes its own slot's +1 (class deref-transfer, or ADOPT/COPY copy-then-
                    // Destroy); the buffer is freed raw afterward with no whole-tuple Destroy. A
                    // half the caller does not want is destroyed in its slot instead, which
                    // consumes that slot just as completely.
                    TKey key = default!;
                    if ((parts & EntryParts.Key) != 0)
                        key = SwiftMarshal.MarshalMovedValueFromSlot<TKey>(nextResultBuffer, KeyTypeMetadata);
                    else
                        KeyTypeMetadata.ValueWitnessTable->Destroy(nextResultBuffer, KeyTypeMetadata);
                    keyMoved = true;
                    TValue val = default!;
                    if ((parts & EntryParts.Value) != 0)
                        val = SwiftMarshal.MarshalMovedValueFromSlot<TValue>((byte*)nextResultBuffer + valueOffset, ValueTypeMetadata);
                    else
                        ValueTypeMetadata.ValueWitnessTable->Destroy((byte*)nextResultBuffer + valueOffset, ValueTypeMetadata);
                    valueMoved = true;

                    result.Add(new KeyValuePair<TKey, TValue>(key, val));
                    bufferLive = false; // both slots consumed; buffer freed raw below
                }
            }
            finally
            {
                if (bufferLive)
                {
                    // Exception between IteratorNext and full consumption: release only the intact slots.
                    if (!keyMoved)
                    {
                        // Neither slot consumed — the whole Optional<(K,V)> is intact.
                        optionalTupleMetadata.ValueWitnessTable->Destroy(nextResultBuffer, optionalTupleMetadata);
                    }
                    else if (!valueMoved)
                    {
                        // Key consumed (its +1 moved into the key wrapper, or its slot already
                        // Destroyed); only the value slot remains live — Destroy just that element.
                        ValueTypeMetadata.ValueWitnessTable->Destroy((byte*)nextResultBuffer + valueOffset, ValueTypeMetadata);
                    }
                }
                NativeMemory.Free(nextResultBuffer);
            }
        }
        finally
        {
            // Destroy the iterator — this releases the retained storage reference
            iteratorMetadata.ValueWitnessTable->Destroy(iteratorBuffer, iteratorMetadata);
            NativeMemory.Free(iteratorBuffer);
        }

        return result;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Removes all key-value pairs from the dictionary.
    /// </summary>
    public unsafe void RemoveAll()
    {
        ThrowIfDisposed();
        var metadata = SwiftObjectHelper<SwiftDictionary<TKey, TValue>>.GetTypeMetadata();

        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            // Cdecl-wrapped: an untyped SwiftSelf collides with Mono's
            // GC-safe-region cookie register; see SwiftCollectionCdeclWrappers.
            SwiftCollectionCdeclWrappers.DictRemoveAll(
                1, // keepingCapacity: true
                metadata,
                _payload.DangerousGetHandle());
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Removes the value for the specified key.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <returns>The removed value, or default if the key was not present.</returns>
    public unsafe TValue RemoveValue(TKey key)
    {
        ThrowIfDisposed();
        var metadata = SwiftObjectHelper<SwiftDictionary<TKey, TValue>>.GetTypeMetadata();

        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            // Allocate space for optional return value and check .none via VWT GetEnumTag
            var optionalMetadata = SwiftObjectHelper<SwiftOptional<TValue>>.GetTypeMetadata();
            void* resultPayload = NativeMemory.Alloc(optionalMetadata.Size);
            bool slotLive = false; // resultPayload holds an initialized .some whose slot is unconsumed
            try
            {
                // Marshal the key. `removeValue(forKey:)` borrows it, so the owned copy marshalling
                // makes is destroyed right after the call.
                Span<byte> keySpan = stackalloc byte[(int)KeySize];
                SwiftMarshal.MarshalToSwift(key, ref keySpan);
                IntPtr keyPayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(keySpan));
                try
                {
                    // Cdecl-wrapped to bypass the Mac Catalyst-x64 workload Mono
                    // CallConvSwift trampoline; see SwiftCollectionCdeclWrappers.
                    SwiftCollectionCdeclWrappers.DictRemoveValue(
                        (IntPtr)resultPayload,
                        keyPayload,
                        metadata,
                        _payload.DangerousGetHandle());
                }
                finally
                {
                    if (SwiftMarshal.MarshalsOwnedCopy(key))
                        KeyTypeMetadata.ValueWitnessTable->Destroy((void*)keyPayload, KeyTypeMetadata);
                }

                // Check Optional.none before marshalling — raw MarshalFromSwift<TValue>
                // on a .none buffer can produce undefined results depending on TValue.
                var tag = (SwiftOptionalCases)optionalMetadata.ValueWitnessTable->GetEnumTag(
                    (byte*)resultPayload, optionalMetadata);
                if (tag == SwiftOptionalCases.None)
                    return default!;

                // Move the removed value's +1 out of the Optional<TValue> buffer into the wrapper
                // (buffer is freed raw on success). MarshalMovedValueFromSlot consumes the slot
                // atomically; `slotLive` releases the intact slot if the move throws first.
                slotLive = true;
                TValue removed = SwiftMarshal.MarshalMovedValueFromSlot<TValue>(resultPayload, ValueTypeMetadata);
                slotLive = false;
                return removed;
            }
            finally
            {
                if (slotLive)
                    optionalMetadata.ValueWitnessTable->Destroy(resultPayload, optionalMetadata);
                NativeMemory.Free(resultPayload);
            }
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Creates a new SwiftDictionary from an enumerable of key-value pairs.
    /// </summary>
    /// <param name="source">The source key-value pairs.</param>
    /// <returns>A new SwiftDictionary containing the key-value pairs.</returns>
    public static SwiftDictionary<TKey, TValue> FromDictionary(IEnumerable<KeyValuePair<TKey, TValue>> source)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        var dict = new SwiftDictionary<TKey, TValue>();
        dict.UpdateRange(source);
        return dict;
    }

    /// <summary>
    /// Bulk-update every pair from <paramref name="source"/> under a single
    /// payload SafeHandle scope, replacing N <c>DangerousAddRef</c>/<c>Release</c>
    /// pairs (one per <c>this[key] = value</c>) with one. The per-element marshal
    /// + Swift-stdlib P/Invoke is unchanged.
    /// </summary>
    private unsafe void UpdateRange(IEnumerable<KeyValuePair<TKey, TValue>> source)
    {
        if (source is ICollection<KeyValuePair<TKey, TValue>> collection && collection.Count == 0)
            return;

        var metadata = SwiftObjectHelper<SwiftDictionary<TKey, TValue>>.GetTypeMetadata();
        var optionalMetadata = SwiftObjectHelper<SwiftOptional<TValue>>.GetTypeMetadata();

        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            IntPtr handle = _payload.DangerousGetHandle();
            foreach (var kvp in source)
            {
                UpdateValueUnsafe(kvp.Key, kvp.Value, metadata, optionalMetadata, handle);
            }
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Returns a lazy projection that converts only values.
    /// The returned <see cref="IReadOnlyDictionary{TKey, TResult}"/> is a live view.
    /// </summary>
    /// <typeparam name="TResult">The projected value type.</typeparam>
    /// <param name="valueSelector">The function to apply to each value.</param>
    public IReadOnlyDictionary<TKey, TResult> AsProjected<TResult>(Func<TValue, TResult> valueSelector)
    {
        ThrowIfDisposed();
        if (valueSelector == null) throw new ArgumentNullException(nameof(valueSelector));
        return new SwiftDictionaryValueProjection<TKey, TValue, TResult>(this, valueSelector);
    }

    /// <summary>
    /// Returns a lazy projection that converts both keys and values.
    /// The returned <see cref="IReadOnlyDictionary{TResultKey, TResultValue}"/> is a live view.
    /// </summary>
    /// <typeparam name="TResultKey">The projected key type.</typeparam>
    /// <typeparam name="TResultValue">The projected value type.</typeparam>
    /// <param name="keySelector">The function to apply to each key (forward: source → result).</param>
    /// <param name="reverseKeySelector">The function to convert result keys back to source keys for lookup.</param>
    /// <param name="valueSelector">The function to apply to each value.</param>
    public IReadOnlyDictionary<TResultKey, TResultValue> AsProjected<TResultKey, TResultValue>(
        Func<TKey, TResultKey> keySelector,
        Func<TResultKey, TKey> reverseKeySelector,
        Func<TValue, TResultValue> valueSelector)
        where TResultKey : notnull
    {
        ThrowIfDisposed();
        if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));
        if (reverseKeySelector == null) throw new ArgumentNullException(nameof(reverseKeySelector));
        if (valueSelector == null) throw new ArgumentNullException(nameof(valueSelector));
        return new SwiftDictionaryProjection<TKey, TValue, TResultKey, TResultValue>(
            this, keySelector, reverseKeySelector, valueSelector);
    }

    private static nuint AlignTo(nuint size, nuint alignment)
    {
        return (size + alignment - 1) & ~(alignment - 1);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _payload?.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

// Direct CallConvSwift bindings for Swift stdlib `Dictionary` operations.
//
// Only the non-mutating ops live here. Everything that takes `self` as an
// untyped `SwiftSelf` — `updateValue`, `removeValue`, `removeAll` and
// `Iterator.next` — dispatches through the C-side cdecl shims in
// `SwiftCollectionCdeclWrappers`, and its register layout is recorded there
// and in the C wrapper's own declaration.
//
// Get and MakeIterator are declared in Bridges/SwiftTransitions.declarations.cs and compiled from
// the bridge products in Bridges/SwiftTransitions.g.cs.
internal static partial class SwiftDictionaryPInvokes
{
    // Dictionary metadata accessor: $sSDMa
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSDMa")]
    public static extern TypeMetadata PInvoke_getMetadata(
        TypeMetadataRequest request,
        TypeMetadata keyTypeMetadata,
        TypeMetadata valueTypeMetadata,
        ProtocolWitnessTable witnessTable);

    // Dictionary count: $sSD5countSivg
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSD5countSivg")]
    public static extern nint Count(
        IntPtr handle,
        TypeMetadata keyTypeMetadata,
        TypeMetadata valueTypeMetadata,
        ProtocolWitnessTable witnessTable);

    // Dictionary.Iterator metadata accessor: $sSD8IteratorVMa
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSD8IteratorVMa")]
    public static extern TypeMetadata PInvoke_getIteratorMetadata(
        TypeMetadataRequest request,
        TypeMetadata keyTypeMetadata,
        TypeMetadata valueTypeMetadata,
        ProtocolWitnessTable witnessTable);
}
