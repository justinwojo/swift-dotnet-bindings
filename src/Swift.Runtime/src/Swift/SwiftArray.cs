// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift;

/// <summary>
/// Represents a Swift collection protocol.
/// </summary>
public interface ISwiftCollection { }

/// <summary>
/// Represents a Swift array.
/// </summary>
/// <typeparam name="Element">The element type contained in the array.</typeparam>
public class SwiftArray<Element> : ISwiftObject, ISwiftStruct, IReadOnlyList<Element>, IList<Element>, IDisposable
{
    // Thread-safe lazy initialization to avoid calling Swift runtime during static construction.
    // This prevents crashes when Element is an existential container type, where
    // swift_getExistentialTypeMetadata called from .cctor triggers a Mono JIT/async assertion.
    // Lazy<T> guarantees exactly one thread computes the value (ExecutionAndPublication mode).
    private static readonly Lazy<TypeMetadata> _lazyElementMetadata =
        new Lazy<TypeMetadata>(() => TypeMetadata.GetTypeMetadataOrThrow<Element>());
    private static readonly Lazy<nuint> _lazyElementSize =
        new Lazy<nuint>(() => _lazyElementMetadata.Value.Size);

    private static TypeMetadata CachedElementTypeMetadata => _lazyElementMetadata.Value;

    private static nuint ElementSize => _lazyElementSize.Value;

    private SwiftSafeHandle<SwiftArray<Element>> _payload;
    private bool _disposed;

    public SwiftSafeHandle<SwiftArray<Element>> Payload
    {
        get { ThrowIfDisposed(); return _payload; }
    }

    public unsafe PayloadBuffer<IntPtr> PayloadBuffer
    {
        get { ThrowIfDisposed(); return new PayloadBuffer<IntPtr>(_payload); }
    }

    private static Dictionary<Type, string> _protocolConformanceSymbols;

    static SwiftArray()
    {
        _protocolConformanceSymbols = new Dictionary<Type, string>
        {
            { typeof(ISwiftCollection), "$sSayxGSlsMc" }
        };

        // On NativeAOT, pre-register factory and cache metadata during type init.
        // Reflection on explicit interface implementations of generic types (GetTypeMetadata,
        // NewFromPayload) may fail on NativeAOT. Direct dispatch via SwiftObjectHelper<T>
        // avoids reflection entirely.
        // On Mono, skip this — calling Swift runtime during static construction can trigger
        // JIT assertions with existential container element types.
        if (SwiftRuntimeInfo.IsNativeAotRuntime)
        {
            TryEagerInitialize();
        }
    }

    /// <summary>
    /// Attempts eager initialization of metadata and factory registration for NativeAOT.
    /// Returns true if initialization succeeded, false if it fell back to lazy init.
    /// Exposed as internal for testability — the .cctor calls this gated behind IsNativeAotRuntime.
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
            // Element metadata may be unavailable during type init for certain Element types
            // (e.g., ExistentialContainer types require protocol descriptor pointers for
            // swift_getExistentialTypeMetadata, which aren't available here).
            // Fall back to lazy initialization — metadata will be fetched on first use.
            System.Diagnostics.Debug.WriteLine(
                $"SwiftArray<{typeof(Element).Name}>: NativeAotInitialize skipped, using lazy init");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NativeAotInitialize()
    {
        // SwiftObjectHelper<T>.GetTypeMetadata() → DirectDispatchGetTypeMetadata():
        // - Registers NewFromPayload factory in NewFromPayloadDispatcher
        // - Caches metadata in TypeMetadata.Cache
        var _ = SwiftObjectHelper<SwiftArray<Element>>.GetTypeMetadata();
    }

    IntPtr ISwiftObject.SwiftHandle
    {
        get { ThrowIfDisposed(); return _payload.DangerousGetHandle(); }
    }

    // Non-reflective borrowed-marshal finalizer suppression (Finding 56a). See ISwiftObject.SuppressPayloadFinalizer.
    void ISwiftObject.SuppressPayloadFinalizer() => global::System.GC.SuppressFinalize(_payload);

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return TypeMetadata.Cache.GetOrAdd(typeof(SwiftArray<Element>), _ => SwiftArrayPInvokes.PInvoke_getMetadata(TypeMetadataRequest.Complete, ElementTypeMetadata));
    }

    // Use cached version to avoid static constructor issues
    static TypeMetadata ElementTypeMetadata => CachedElementTypeMetadata;

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new SwiftArray<Element>(handle);
    }

    /// <inheritdoc/>
    static PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics
        => PayloadConstructionSemantics.Copy;

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        ThrowIfDisposed();
        var metadata = SwiftObjectHelper<SwiftArray<Element>>.GetTypeMetadata();
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
            throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type SwiftArray and protocol {typeof(TProtocol).Name}, but no conformance was found.");
        }
        return ProtocolConformanceDescriptor.LoadFromSymbol("/usr/lib/swift/libswiftCore.dylib", symbolName);
    }

    /// <summary>
    /// Constructs a new SwiftArray from the given handle.
    /// Uses VWT InitializeWithCopy to properly retain the Array's CoW buffer.
    /// Without this, async callbacks would return stale data because the original
    /// Swift Array goes out of scope after the callback returns.
    /// </summary>
    unsafe SwiftArray(IntPtr handle)
    {
        var metadata = SwiftObjectHelper<SwiftArray<Element>>.GetTypeMetadata();
        IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc(metadata.Size);
        metadata.ValueWitnessTable->InitializeWithCopy((void*)bufferPtr, (void*)handle, metadata);
        _payload = new SwiftSafeHandle<SwiftArray<Element>>(bufferPtr);
    }

    /// <summary>
    /// Constructs a new empty SwiftArray.
    /// </summary>
    public unsafe SwiftArray()
    {
        IntPtr result = SwiftArrayPInvokes.Init(ElementTypeMetadata);
        IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc((nuint)sizeof(IntPtr));
        *(IntPtr*)bufferPtr = result;
        _payload = new SwiftSafeHandle<SwiftArray<Element>>(bufferPtr);
    }

    /// <summary>
    /// Constructs a new SwiftArray from an array of elements.
    /// </summary>
    /// <param name="source">The source array to copy elements from.</param>
    public SwiftArray(Element[] source) : this()
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        AppendRange(source);
    }

    /// <summary>
    /// Constructs a new SwiftArray from an enumerable source.
    /// </summary>
    /// <param name="source">The source enumerable to copy elements from.</param>
    public SwiftArray(IEnumerable<Element> source) : this()
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        AppendRange(source);
    }

    /// <summary>
    /// Implicitly converts a .NET array to a SwiftArray.
    /// </summary>
    public static implicit operator SwiftArray<Element>(Element[] source)
        => new SwiftArray<Element>(source);

    /// <summary>
    /// Gets the number of elements in the array.
    /// </summary>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            using PayloadBuffer<IntPtr> disposable = PayloadBuffer;
            int result = (int)SwiftArrayPInvokes.Count(disposable.Buffer, ElementTypeMetadata);
            return result;
        }
    }

    /// <summary>
    /// Appends the given element to the array.
    /// </summary>
    public unsafe void Append(Element item)
    {
        ThrowIfDisposed();
        var metadata = SwiftObjectHelper<SwiftArray<Element>>.GetTypeMetadata();
        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            AppendUnsafe(item, metadata, _payload.DangerousGetHandle());
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Bulk-append every item from <paramref name="source"/> under a single
    /// <see cref="System.Runtime.InteropServices.SafeHandle.DangerousAddRef(ref bool)"/>
    /// scope. Hoisting the SafeHandle ref-count ops outside the loop replaces
    /// N managed↔SafeHandle interlocked operations with one for large inserts;
    /// the per-element marshal + Swift-stdlib P/Invoke is unchanged.
    /// </summary>
    private unsafe void AppendRange(IEnumerable<Element> source)
    {
        // Avoid acquiring the SafeHandle scope if the source is provably empty.
        if (source is ICollection<Element> collection && collection.Count == 0)
            return;

        var metadata = SwiftObjectHelper<SwiftArray<Element>>.GetTypeMetadata();
        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            IntPtr handle = _payload.DangerousGetHandle();
            foreach (var item in source)
            {
                AppendUnsafe(item, metadata, handle);
            }
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Append a single element without acquiring/releasing the payload SafeHandle.
    /// The caller is responsible for holding a successful <c>DangerousAddRef</c>
    /// scope across the call.
    /// </summary>
    private static unsafe void AppendUnsafe(Element item, TypeMetadata metadata, IntPtr handle)
    {
        Span<byte> span = stackalloc byte[(int)ElementSize];
        IntPtr payload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));
        SwiftMarshal.MarshalToSwift(item, ref span);
        // Cdecl-wrapped: an untyped SwiftSelf collides with Mono's
        // GC-safe-region cookie register; see SwiftCollectionCdeclWrappers.
        SwiftCollectionCdeclWrappers.ArrayAppend(payload, metadata, handle);
    }

    /// <summary>
    /// Inserts the given element at the given index.
    /// </summary>
    public unsafe void Insert(int index, Element item)
    {
        ThrowIfDisposed();
        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            var metadata = SwiftObjectHelper<SwiftArray<Element>>.GetTypeMetadata();
            Span<byte> span = stackalloc byte[(int)ElementSize];
            IntPtr payload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));
            SwiftMarshal.MarshalToSwift(item, ref span);
            // Cdecl-wrapped: an untyped SwiftSelf collides with Mono's
            // GC-safe-region cookie register; see SwiftCollectionCdeclWrappers.
            SwiftCollectionCdeclWrappers.ArrayInsert(payload, index, metadata, _payload.DangerousGetHandle());
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Removes the element at the given index.
    /// </summary>
    public unsafe void Remove(int index)
    {
        ThrowIfDisposed();
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            var metadata = SwiftObjectHelper<SwiftArray<Element>>.GetTypeMetadata();
            byte* payload = stackalloc byte[(int)ElementSize];
            bool slotLive = false;
            try
            {
                // Array.remove(at:) initializes `payload` with an owned result even though this
                // void API discards it. Destroy that result after the call; otherwise every
                // reference-bearing removed element leaves its +1 behind.
                SwiftCollectionCdeclWrappers.ArrayRemove((IntPtr)payload, index, metadata, _payload.DangerousGetHandle());
                slotLive = true;
            }
            finally
            {
                // The slot is uninitialized until the native call returns, so an invalid index
                // (rejected above) or native failure can never destroy arbitrary stack bytes.
                if (slotLive)
                    ElementTypeMetadata.ValueWitnessTable->Destroy(payload, ElementTypeMetadata);
            }
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Removes all elements from the array.
    /// </summary>
    public unsafe void RemoveAll()
    {
        ThrowIfDisposed();
        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            var metadata = SwiftObjectHelper<SwiftArray<Element>>.GetTypeMetadata();
            // Cdecl-wrapped: an untyped SwiftSelf collides with Mono's
            // GC-safe-region cookie register; see SwiftCollectionCdeclWrappers.
            SwiftCollectionCdeclWrappers.ArrayRemoveAll(1, metadata, _payload.DangerousGetHandle());
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Gets or sets the element at the given index.
    /// </summary>
    public unsafe Element this[int index]
    {
        get
        {
            ThrowIfDisposed();
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            using PayloadBuffer<IntPtr> disposable = PayloadBuffer;
            return ReadOwnedElement(index, disposable.Buffer);
        }
        set
        {
            ThrowIfDisposed();
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            using PayloadBuffer<IntPtr> _ = PayloadBuffer;
            var metadata = SwiftObjectHelper<SwiftArray<Element>>.GetTypeMetadata();
            Span<byte> span = stackalloc byte[(int)ElementSize];
            IntPtr payload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));
            SwiftMarshal.MarshalToSwift(value, ref span);
            // Cdecl-wrapped: an untyped SwiftSelf collides with Mono's
            // GC-safe-region cookie register; see SwiftCollectionCdeclWrappers.
            SwiftCollectionCdeclWrappers.ArraySet(payload, index, metadata, _payload.DangerousGetHandle());
        }
    }

    /// <summary>
    /// Reads one element from Swift's non-mutating Array subscript. The getter initializes an
    /// <b>owned</b> value in the temporary, so every carrier shape must pass through the same
    /// moved-slot ownership seam used by Dictionary and Set before the storage is freed raw.
    /// </summary>
    private static unsafe Element ReadOwnedElement(int index, IntPtr storage)
    {
        var metadata = ElementTypeMetadata;
        void* payload = NativeMemory.Alloc(ElementSize);
        bool slotLive = false;
        try
        {
            SwiftArrayPInvokes.Get(new SwiftIndirectResult(payload), index, storage, metadata);
            slotLive = true;

            // MarshalMovedValueFromSlot is the complete carrier table:
            // - true class: dereference the slot and transfer its +1;
            // - Adopt/Copy reference-backed wrapper: copy independently, then VWT-Destroy the slot;
            // - Adopt POD: copy into independent storage;
            // - Move/Inline/POD/direct value: transfer/read the value without a Destroy.
            // It consumes atomically; on a throw the intact slot remains ours to destroy below.
            Element result = SwiftMarshal.MarshalMovedValueFromSlot<Element>(payload, metadata);
            slotLive = false;
            return result;
        }
        finally
        {
            if (slotLive)
                metadata.ValueWitnessTable->Destroy(payload, metadata);
            NativeMemory.Free(payload);
        }
    }

    /// <summary>
    /// Returns a lazy projection of this array, applying the selector to each element on access.
    /// The returned <see cref="IReadOnlyList{TResult}"/> is a live view — it does not copy elements.
    /// </summary>
    /// <typeparam name="TResult">The projected element type.</typeparam>
    /// <param name="selector">The function to apply to each element.</param>
    public IReadOnlyList<TResult> AsProjected<TResult>(Func<Element, TResult> selector)
    {
        ThrowIfDisposed();
        if (selector == null) throw new ArgumentNullException(nameof(selector));
        return new SwiftArrayProjection<Element, TResult>(this, selector);
    }

    #region IList<Element> explicit implementation

    void ICollection<Element>.Add(Element item) => Append(item);

    void IList<Element>.RemoveAt(int index) => Remove(index);

    void ICollection<Element>.Clear() => RemoveAll();

    int IList<Element>.IndexOf(Element item)
    {
        int count = Count;
        var comparer = EqualityComparer<Element>.Default;
        for (int i = 0; i < count; i++)
        {
            if (comparer.Equals(this[i], item))
                return i;
        }
        return -1;
    }

    bool ICollection<Element>.Contains(Element item) => ((IList<Element>)this).IndexOf(item) >= 0;

    bool ICollection<Element>.Remove(Element item)
    {
        int index = ((IList<Element>)this).IndexOf(item);
        if (index < 0) return false;
        Remove(index);
        return true;
    }

    void ICollection<Element>.CopyTo(Element[] array, int arrayIndex)
    {
        if (array == null) throw new ArgumentNullException(nameof(array));
        int count = Count;
        if (arrayIndex < 0 || arrayIndex + count > array.Length)
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        ExtractRange(array, arrayIndex, count);
    }

    bool ICollection<Element>.IsReadOnly => false;

    // IList<Element>.this[int index] is satisfied by the existing public indexer

    // IList<Element>.Insert is satisfied by the existing public Insert(int, Element)

    #endregion

    /// <summary>
    /// Returns an enumerator that iterates through the array.
    /// </summary>
    public IEnumerator<Element> GetEnumerator()
    {
        ThrowIfDisposed();
        return GetEnumeratorCore();
    }

    private IEnumerator<Element> GetEnumeratorCore()
    {
        int count = Count;
        for (int i = 0; i < count; i++)
        {
            yield return this[i];
        }
    }

    /// <summary>
    /// Returns an enumerator that iterates through the array.
    /// </summary>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Creates a new SwiftArray from an IEnumerable source.
    /// </summary>
    /// <param name="source">The source enumerable to copy elements from.</param>
    /// <returns>A new SwiftArray containing the elements from the source.</returns>
    public static SwiftArray<Element> FromEnumerable(IEnumerable<Element> source)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        var array = new SwiftArray<Element>();
        array.AppendRange(source);
        return array;
    }

    /// <summary>
    /// Copies the elements to a new .NET array.
    /// </summary>
    public Element[] ToArray()
    {
        ThrowIfDisposed();
        int count = Count;
        var result = new Element[count];
        ExtractRange(result, 0, count);
        return result;
    }

    /// <summary>
    /// Copies the elements to a new List.
    /// </summary>
    public List<Element> ToList()
    {
        ThrowIfDisposed();
        int count = Count;
        var result = new List<Element>(count);
        if (count > 0)
        {
            // Allocate once, fill via the bulk-extract path, then dump into the list.
            // List<T>.Add inside a hot per-element loop would otherwise reacquire the
            // payload SafeHandle on every Get call.
            var buffer = new Element[count];
            ExtractRange(buffer, 0, count);
            for (int i = 0; i < count; i++)
                result.Add(buffer[i]);
        }
        return result;
    }

    /// <summary>
    /// Reads <paramref name="count"/> elements out of the Swift array under a single
    /// <see cref="System.Runtime.InteropServices.SafeHandle.DangerousAddRef(ref bool)"/>
    /// scope, writing them into <paramref name="destination"/> starting at
    /// <paramref name="destinationIndex"/>. Bounds are caller-checked.
    /// </summary>
    private unsafe void ExtractRange(Element[] destination, int destinationIndex, int count)
    {
        if (count == 0) return;

        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            // Swift's Array.subscript getter is non-mutating: `self` is borrowed by value
            // (a single storage pointer for Array). The Get P/Invoke's third parameter is
            // that storage pointer, NOT the address-of-storage. The single-element indexer
            // dereferences via `PayloadBuffer<IntPtr>.Buffer` (= *(IntPtr*)handle); we do
            // the same here, once, hoisted outside the loop.
            IntPtr storage = *(IntPtr*)_payload.DangerousGetHandle();

            for (int i = 0; i < count; i++)
                destination[destinationIndex + i] = ReadOwnedElement(i, storage);
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Returns a string representation of the array.
    /// </summary>
    public override string ToString()
    {
        ThrowIfDisposed();
        return $"SwiftArray<{typeof(Element).Name}>[{Count}]";
    }

    /// <summary>
    /// Releases the resources used by the SwiftArray.
    /// </summary>
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

internal static class SwiftArrayPInvokes
{
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSaMa")]
    public static extern TypeMetadata PInvoke_getMetadata(TypeMetadataRequest request, TypeMetadata typeMetadata);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sS2ayxGycfC")]
    public static extern IntPtr Init(TypeMetadata typeMetadata);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSayxSicig")]
    public static extern void Get(SwiftIndirectResult result, nint index, IntPtr handle, TypeMetadata elementMetadata);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSa5countSivg")]
    public static extern nint Count(IntPtr handle, TypeMetadata elementMetadata);
}
