// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Plain-<see cref="CallingConvention.Cdecl"/> P/Invokes into the seven
/// C-side wrappers in <c>SwiftBindingsRuntimeCollections.c</c>. Each entry
/// mirrors a Swift stdlib generic-collection operation whose direct
/// <c>CallConvSwift</c> P/Invoke shape is mishandled by a Mono CallConvSwift
/// trampoline. Two distinct broken shapes are covered, with different
/// reproduction footprints.
///
/// <para><b>Shape A</b> — <c>SwiftIndirectResult</c> + intermediate integer
/// args + <c>SwiftSelf</c>. Six entries: the Dictionary/Set/Array ops other
/// than <see cref="SetInsert"/>. Broken only on the Mac Catalyst-x64 workload
/// Mono runtime: the trampoline writes the correct sret result but corrupts
/// the caller's <c>self</c> slot when explicit integer args are interleaved
/// between the indirect-result and self registers. The same managed code +
/// Swift dylib + x86_64 Rosetta slice PASSES on macOS-x64 (CoreCLR osx-x64)
/// and on arm64 across every target. See <c>SretSelfProbeTests</c> for the
/// minimal hand-marshalled reproduction proving this is upstream.</para>
///
/// <para><b>Shape B</b> — a mixed tuple return
/// <c>(Bool direct, @out Element)</c> where the <c>@out</c> buffer pointer is
/// an ordinary leading argument (x0/rdi) rather than an sret register, plus
/// <c>SwiftSelf</c>. One entry: <see cref="SetInsert"/>. Broken on the iOS
/// Simulator Mono runtime (isolated on arm64 simulator; x86_64
/// simulator/Catalyst share that trampoline). The failure is not a wrong
/// value — it corrupts Mono's own thread state: an immediate SIGABRT with
/// <c>Cannot transition thread 0x0 from STARTING with DONE_BLOCKING</c>, or a
/// Set whose <c>count</c> reads garbage after a trampoline scratch address is
/// written into the caller's <c>self</c> slot, then a SIGSEGV on a later
/// insert or on the Set's release. Not reproduced on NativeAOT (device) or
/// CoreCLR (macOS). Shape B is not a variant of shape A:
/// <c>Dictionary.updateValue</c> (pure <c>@out</c> via
/// <c>SwiftIndirectResult</c>) and <c>Set.contains</c> (single direct return)
/// both pass on the iOS Simulator.</para>
///
/// The C wrappers redeclare the stdlib symbols with clang's
/// <c>__attribute__((swiftcall))</c> + <c>swift_indirect_result</c> /
/// <c>swift_context</c> parameter attrs, so the inner Swift-to-Swift call is
/// lowered through LLVM swiftcc — the same machinery swiftc uses, correct
/// on every supported arch. C# enters via plain Cdecl, which Mono's
/// well-tested cdecl path handles correctly. The broken CallConvSwift
/// trampoline is bypassed entirely. Every wrapper is used on every runtime,
/// including ones that do not exhibit either bug, so there is a single
/// dispatch path everywhere.
///
/// Coverage rule: only those seven ops are wrapped. Non-mutating reads
/// (<c>Dictionary.subscript</c>, <c>Set.contains</c>,
/// <c>Array.subscript</c>, <c>count</c>, <c>makeIterator</c>,
/// <c>removeAll(keepingCapacity:)</c>, <c>Array.append/insert/set</c>) keep
/// their direct CallConvSwift P/Invoke — those match neither broken shape and
/// pass on every runtime already. Routing them through wrappers would be
/// churn without benefit.
/// </summary>
internal static class SwiftCollectionCdeclWrappers
{
    private const string LibraryName = "SwiftBindingsRuntime";

    // -----------------------------------------------------------------
    // Dictionary<K,V>
    // -----------------------------------------------------------------

    /// <summary>
    /// Cdecl wrapper for <c>Dictionary.updateValue(_:forKey:)</c>
    /// (<c>$sSD11updateValue_6forKeyq_Sgq_n_xtF</c>). Writes the prior
    /// <c>Optional&lt;Value&gt;</c> into <paramref name="result"/>.
    /// </summary>
    /// <param name="result">Pointer to the caller-allocated
    /// <c>Optional&lt;Value&gt;</c> sret buffer.</param>
    /// <param name="value">Pointer to the new value's marshalled payload.</param>
    /// <param name="key">Pointer to the key's marshalled payload.</param>
    /// <param name="dictionaryMetadata">Full <c>Dictionary&lt;K,V&gt;</c>
    /// type metadata (hidden generic-context arg).</param>
    /// <param name="self">Pointer to the dictionary's storage slot.</param>
    [DllImport(LibraryName, EntryPoint = "SBW_Dict_UpdateValue", CallingConvention = CallingConvention.Cdecl)]
    public static extern void DictUpdateValue(
        IntPtr result, IntPtr value, IntPtr key,
        TypeMetadata dictionaryMetadata, IntPtr self);

    /// <summary>
    /// Cdecl wrapper for <c>Dictionary.removeValue(forKey:)</c>
    /// (<c>$sSD11removeValue6forKeyq_Sgx_tF</c>). Writes the removed
    /// <c>Optional&lt;Value&gt;</c> into <paramref name="result"/>.
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "SBW_Dict_RemoveValue", CallingConvention = CallingConvention.Cdecl)]
    public static extern void DictRemoveValue(
        IntPtr result, IntPtr key,
        TypeMetadata dictionaryMetadata, IntPtr self);

    /// <summary>
    /// Cdecl wrapper for <c>Dictionary&lt;K,V&gt;.Iterator.next()</c>
    /// (<c>$sSD8IteratorV4nextx3key_q_5valuetSgyF</c>). Writes the next
    /// <c>Optional&lt;(K, V)&gt;</c> into <paramref name="result"/> and
    /// advances the iterator in place.
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "SBW_Dict_IteratorNext", CallingConvention = CallingConvention.Cdecl)]
    public static extern void DictIteratorNext(
        IntPtr result, TypeMetadata iteratorMetadata, IntPtr self);

    // -----------------------------------------------------------------
    // Set<Element>
    // -----------------------------------------------------------------

    /// <summary>
    /// Cdecl wrapper for <c>Set.insert(_:)</c>
    /// (<c>$sSh6insertySb8inserted_x17memberAfterInserttxnF</c>). Shape B —
    /// the <c>memberAfterInsert</c> buffer is an ordinary leading pointer
    /// argument, not an sret, which is why this takes an <see cref="IntPtr"/>
    /// rather than a <c>SwiftIndirectResult</c>.
    ///
    /// <para><b>Ownership</b> (read off the swiftc-emitted call site, and the
    /// caller must reproduce it exactly): <paramref name="element"/> is
    /// passed <c>@in</c> consuming — the call takes over its +1 and the
    /// caller must NOT destroy it. <paramref name="outMember"/> receives
    /// <c>memberAfterInsert</c> at +1 and IS the caller's to destroy through
    /// the element type's value-witness table.</para>
    /// </summary>
    /// <param name="outMember">Pointer to a caller-allocated buffer of the
    /// element type's size, receiving <c>memberAfterInsert</c> at +1.</param>
    /// <param name="element">Pointer to the element's marshalled payload,
    /// consumed by the call.</param>
    /// <param name="setMetadata">Full <c>Set&lt;Element&gt;</c> type metadata
    /// (hidden generic-context arg).</param>
    /// <param name="self">Pointer to the set's storage slot.</param>
    /// <returns>Exactly 1 if the element was newly inserted; exactly 0 if an
    /// equal element was already present. The C wrapper narrows the result to
    /// one bit before returning it — swiftc declares this symbol's Swift
    /// <c>Bool</c> as an <c>i1</c> with no zero-extension, so only bit 0 of the
    /// return register is defined.</returns>
    [DllImport(LibraryName, EntryPoint = "SBW_Set_Insert", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern byte SetInsert(
        IntPtr outMember, IntPtr element,
        TypeMetadata setMetadata, IntPtr self);

    /// <summary>
    /// Cdecl wrapper for <c>Set.remove(_:)</c>
    /// (<c>$sSh6removeyxSgxF</c>). Writes the removed
    /// <c>Optional&lt;Element&gt;</c> into <paramref name="result"/>.
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "SBW_Set_Remove", CallingConvention = CallingConvention.Cdecl)]
    public static extern void SetRemove(
        IntPtr result, IntPtr element,
        TypeMetadata setMetadata, IntPtr self);

    /// <summary>
    /// Cdecl wrapper for <c>Set&lt;Element&gt;.Iterator.next()</c>
    /// (<c>$sSh8IteratorV4nextxSgyF</c>). Writes the next
    /// <c>Optional&lt;Element&gt;</c> into <paramref name="result"/> and
    /// advances the iterator in place.
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "SBW_Set_IteratorNext", CallingConvention = CallingConvention.Cdecl)]
    public static extern void SetIteratorNext(
        IntPtr result, TypeMetadata iteratorMetadata, IntPtr self);

    // -----------------------------------------------------------------
    // Array<Element>
    // -----------------------------------------------------------------

    /// <summary>
    /// Cdecl wrapper for <c>Array.remove(at:)</c>
    /// (<c>$sSa6remove2atxSi_tF</c>). Writes the removed element into
    /// <paramref name="result"/>.
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "SBW_Array_Remove", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ArrayRemove(
        IntPtr result, nint index,
        TypeMetadata arrayMetadata, IntPtr self);
}
