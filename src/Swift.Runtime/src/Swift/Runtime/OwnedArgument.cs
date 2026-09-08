// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Mints the +1 that a callee-consumed Swift argument must carry.
///
/// <para>SILGen lowers an initializer's value parameters and every parameter of a setter as
/// <c>@owned</c>: the callee releases what it was handed. A C# caller that passes a lowered
/// buffer read out of its own long-lived wrapper is passing a borrow — the wrapper still owns
/// the value and will destroy it when disposed — so the callee's release takes a count that was
/// never transferred. The references inside the value then reach zero early and the heap is
/// corrupted; the crash lands much later, on whichever thread next touches the freed object,
/// usually as a finalizer-thread fault in an unrelated type.</para>
///
/// <para>The transfer is expressed as a value-witness <c>InitializeWithCopy</c> into scratch
/// storage which is then freed <em>raw</em>, without a value-witness destroy. A copy retains
/// whatever the value references; discarding the copy's bytes without destroying it leaves those
/// retains outstanding, so the value at the caller's own address is now carrying one extra count
/// with its bit pattern untouched. The lowered buffer the P/Invoke passes therefore arrives at +1
/// for the callee to consume, and the caller's own destroy stays armed against its own count.</para>
///
/// <para>Going through the value witness rather than a bare retain is what makes this carrier-
/// agnostic: it is correct for a frozen struct holding one class reference, for a Swift
/// <c>String</c> (whose count lives on an out-of-line storage object only when the string is
/// large enough not to fit inline), and for the collection and Optional carriers, without any of
/// them having to spell their own retain.</para>
/// </summary>
public static class OwnedArgument
{
    /// <summary>
    /// Adds the count a consuming callee will release to the value stored in
    /// <paramref name="payload"/>, leaving the value's bytes unchanged.
    /// </summary>
    /// <typeparam name="T">
    /// The wrapper type whose Swift metadata describes the value in the payload.
    /// </typeparam>
    /// <param name="payload">The wrapper's payload handle, pointing at the Swift value.</param>
    public static void Retain<T>(SafeHandle payload) where T : ISwiftObject
        => Retain(SwiftObjectHelper<T>.GetTypeMetadata(), payload);

    /// <summary>
    /// Metadata-driven overload for carriers whose describing type is not available as a generic
    /// argument at the call site.
    /// </summary>
    public static unsafe void Retain(TypeMetadata metadata, SafeHandle payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var pinned = false;
        payload.DangerousAddRef(ref pinned);
        try
        {
            var source = (void*)payload.DangerousGetHandle();
            if (source == null)
                return;

            // Stride, not Size: a value witness may write padding up to the stride, and a
            // Size-sized block would let it run past the allocation.
            nuint bytes = metadata.Stride;
            void* scratch = NativeMemory.Alloc(bytes == 0 ? 1 : bytes);
            try
            {
                metadata.ValueWitnessTable->InitializeWithCopy(scratch, source, metadata);
            }
            finally
            {
                // Deliberately no Destroy: the copy's counts are the ones being handed to the
                // callee. Destroying here would give the value back exactly what it just took
                // and restore the underflow this call exists to prevent.
                NativeMemory.Free(scratch);
            }
        }
        finally
        {
            if (pinned)
                payload.DangerousRelease();
        }
    }
    /// <summary>Provisionally copies a consumed value while keeping its caller payload pinned.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static ValueTransfer BeginValueTransfer<T>(SafeHandle payload) where T : ISwiftObject
        => new(SwiftObjectHelper<T>.GetTypeMetadata(), payload);

    /// <summary>
    /// Holds the initialized copy until native entry succeeds. Generated callers complete the
    /// transfer immediately after P/Invoke returns, including Swift error returns. Earlier setup
    /// or entry failures destroy the provisional copy; completed transfers free its storage raw.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public sealed unsafe class ValueTransfer : IDisposable
    {
        private SafeHandle? _payload;
        private readonly TypeMetadata _metadata;
        private readonly void* _scratch;
        private bool _transferred;

        public ValueTransfer(TypeMetadata metadata, SafeHandle payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (!metadata.IsValid)
                throw new ArgumentException("Valid Swift type metadata is required.", nameof(metadata));
            bool pinned = false;
            void* scratch = null;
            try
            {
                payload.DangerousAddRef(ref pinned);
                var source = (void*)payload.DangerousGetHandle();
                if (source != null)
                {
                    // Witnesses may touch stride padding; zero-sized values still need an address.
                    nuint bytes = metadata.Stride;
                    scratch = NativeMemory.Alloc(bytes == 0 ? 1 : bytes);
                    metadata.ValueWitnessTable->InitializeWithCopy(scratch, source, metadata);
                }
                _metadata = metadata;
                _scratch = scratch;
                _payload = payload;
            }
            catch
            {
                NativeMemory.Free(scratch);
                if (pinned)
                    payload.DangerousRelease();
                throw;
            }
        }

        /// <summary>Records native consumption before any managed result/error conversion.</summary>
        public void Complete() => _transferred = true;

        public void Dispose()
        {
            var payload = System.Threading.Interlocked.Exchange(ref _payload, null);
            if (payload is null)
                return;
            try
            {
                if (!_transferred && _scratch != null)
                    _metadata.ValueWitnessTable->Destroy(_scratch, _metadata);
            }
            finally
            {
                NativeMemory.Free(_scratch);
                payload.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Pins a class argument and provisionally acquires the +1 a consuming callee will release.
    /// A generated caller must use a using declaration and call Complete immediately after the
    /// P/Invoke returns, including a Swift error return. If later argument preparation or native
    /// entry resolution throws first, Dispose rolls the provisional retain back.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public sealed class ClassTransfer : IDisposable
    {
        private SafeHandle? _payload;
        private readonly IntPtr _objectPointer;
        private bool _transferred;

        public ClassTransfer(SafeHandle payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            bool pinned = false;
            try
            {
                payload.DangerousAddRef(ref pinned);
                _objectPointer = payload.DangerousGetHandle();
                Arc.UnknownObjectRetain(_objectPointer);
                _payload = payload;
            }
            catch
            {
                if (pinned)
                    payload.DangerousRelease();
                throw;
            }
        }

        /// <summary>Records native consumption before any managed result/error conversion.</summary>
        public void Complete() => _transferred = true;

        public void Dispose()
        {
            var payload = System.Threading.Interlocked.Exchange(ref _payload, null);
            if (payload is null)
                return;
            try
            {
                if (!_transferred)
                    Arc.UnknownObjectRelease(_objectPointer);
            }
            finally
            {
                payload.DangerousRelease();
            }
        }
    }

}
