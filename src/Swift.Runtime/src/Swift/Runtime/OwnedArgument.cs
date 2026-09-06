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
}
