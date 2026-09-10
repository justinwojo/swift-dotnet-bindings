// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Plain-<see cref="CallingConvention.Cdecl"/> P/Invokes into the fourteen
/// C-side wrappers in <c>SwiftBindingsRuntimeCollections.c</c>. Each entry
/// mirrors a Swift stdlib entry point this runtime calls by hand and whose
/// direct <c>CallConvSwift</c> P/Invoke shape is mishandled by a Mono
/// CallConvSwift trampoline. Two distinct broken shapes are covered, with
/// different reproduction footprints.
///
/// <para><b>Shape A</b> — <c>SwiftIndirectResult</c> + intermediate integer
/// args + <c>SwiftSelf</c>. Six entries: <see cref="DictUpdateValue"/>,
/// <see cref="DictRemoveValue"/>, <see cref="DictIteratorNext"/>,
/// <see cref="SetRemove"/>, <see cref="SetIteratorNext"/>,
/// <see cref="ArrayRemove"/>. Broken only on the Mac Catalyst-x64 workload
/// Mono runtime: the trampoline writes the correct sret result but corrupts
/// the caller's <c>self</c> slot when explicit integer args are interleaved
/// between the indirect-result and self registers. The same managed code +
/// Swift dylib + x86_64 Rosetta slice PASSES on macOS-x64 (CoreCLR osx-x64)
/// and on arm64 across every target. See <c>SretSelfProbeTests</c> for the
/// minimal hand-marshalled reproduction proving this is upstream.</para>
///
/// <para><b>Shape B</b> — unlike shape A this is not really a *shape*. Its
/// eligible population is every <c>CallConvSwift</c> call carrying an untyped
/// <c>SwiftSelf</c>; which of those actually break is a per-member outcome of
/// Mono's register allocator, not a property of the signature. Mono's
/// managed-to-native wrapper parks its GC-safe-region cookie in a callee-saved
/// register chosen by that allocator, which does not exclude x20 — the
/// register CallConvSwift reserves for an untyped <c>SwiftSelf</c> — and the
/// wrapper's own argument setup then destroys it, so the exit call is handed a
/// <c>self</c> pointer where a <c>MonoThreadInfo*</c> should be.
/// <see cref="SetInsert"/> is the member that was observed failing, on the iOS
/// Simulator Mono runtime (isolated on arm64 simulator; x86_64
/// simulator/Catalyst share that wrapper codegen). The failure is not a wrong
/// value — it corrupts Mono's own thread state: an immediate SIGABRT with
/// <c>Cannot transition thread 0x0 from STARTING with DONE_BLOCKING</c>, or a
/// Set whose <c>count</c> reads garbage after a scratch address is written
/// into the caller's <c>self</c> slot, then a SIGSEGV on a later insert or on
/// the Set's release. Not reproduced on NativeAOT (device) or CoreCLR (macOS).
/// The other seven shape-B entries — <see cref="ArraySet"/>,
/// <see cref="ArrayAppend"/>, <see cref="ArrayInsert"/>,
/// <see cref="ArrayRemoveAll"/>, <see cref="SetRemoveAll"/>,
/// <see cref="DictRemoveAll"/> and <see cref="HashableHashValue"/> — are here
/// under the coverage rule below rather than because each was seen to
/// crash.</para>
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
/// <para><b>Coverage rule.</b> Every hand-written <c>Swift.Runtime</c>
/// P/Invoke carrying an untyped <c>SwiftSelf</c> is wrapped, plus the six
/// shape-A ops. Nothing else. This replaces a deliberately reactive rule —
/// wrap a member only once it is seen to break — which does not survive the
/// register-level account of shape B: which member parks its cookie in x20 is
/// a property of one compiled binary, so a member that is safe today moves
/// onto the defect from a Mono codegen change, a .NET update, or an unrelated
/// edit to the calling method, with nothing on our side changing and no test
/// that would have gone red first. What stands in for a crash is a direct
/// measurement — disassembling the Mono full-AOT app binary and reading, for
/// every <c>wrapper_managed_to_native_*</c> symbol carrying a
/// <c>SwiftSelf</c>, which callee-saved register receives the cookie and
/// whether the argument setup writes it before the native call. Two
/// independently built binaries produced the same clobbering members in the
/// same register, so the allocation is reproducible per call site rather than
/// a per-build coin flip. Reproducible register-allocation evidence for a
/// known defect class counts as an observed failure, and the remedy is applied
/// to the class rather than to whichever members happened to be unlucky in the
/// last binary anyone looked at.</para>
///
/// <para>Calls with no untyped <c>SwiftSelf</c> are outside the eligible
/// population and keep their direct CallConvSwift P/Invoke: metadata accessors
/// (<c>…Ma</c>), the <c>init</c> entry points, <c>Set.contains</c>,
/// <c>Array.subscript</c>'s getter, <c>count</c> and <c>makeIterator</c> all
/// pass <c>self</c> as an ordinary pointer argument, so there is no cookie
/// register for the argument setup to clobber.</para>
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

    /// <summary>
    /// Cdecl wrapper for <c>Dictionary.removeAll(keepingCapacity:)</c>
    /// (<c>$sSD9removeAll15keepingCapacityySb_tF</c>).
    /// </summary>
    /// <param name="keepCapacity">Swift's <c>keepingCapacity:</c> flag. Any
    /// non-zero byte means <c>true</c>; the C wrapper narrows it to one bit
    /// before the swiftcc call, which expects an <c>i1</c>.</param>
    /// <param name="dictionaryMetadata">Full <c>Dictionary&lt;K,V&gt;</c>
    /// type metadata (hidden generic-context arg).</param>
    /// <param name="self">Pointer to the dictionary's storage slot.</param>
    [DllImport(LibraryName, EntryPoint = "SBW_Dict_RemoveAll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void DictRemoveAll(
        byte keepCapacity, TypeMetadata dictionaryMetadata, IntPtr self);

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

    /// <summary>
    /// Cdecl wrapper for <c>Set.removeAll(keepingCapacity:)</c>
    /// (<c>$sSh9removeAll15keepingCapacityySb_tF</c>).
    /// </summary>
    /// <param name="keepCapacity">Swift's <c>keepingCapacity:</c> flag; any
    /// non-zero byte means <c>true</c>.</param>
    /// <param name="setMetadata">Full <c>Set&lt;Element&gt;</c> type metadata
    /// (hidden generic-context arg).</param>
    /// <param name="self">Pointer to the set's storage slot.</param>
    [DllImport(LibraryName, EntryPoint = "SBW_Set_RemoveAll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void SetRemoveAll(
        byte keepCapacity, TypeMetadata setMetadata, IntPtr self);

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

    /// <summary>
    /// Cdecl wrapper for <c>Array.subscript(_:)</c>'s setter
    /// (<c>$sSayxSicis</c>).
    /// </summary>
    /// <param name="value">Pointer to the element's marshalled payload.
    /// Passed <c>@in</c> consuming — the call takes over its +1 and the caller
    /// must NOT destroy it.</param>
    /// <param name="index">Zero-based element index; the caller has already
    /// bounds-checked it.</param>
    /// <param name="arrayMetadata">Full <c>Array&lt;Element&gt;</c> type
    /// metadata (hidden generic-context arg).</param>
    /// <param name="self">Pointer to the array's storage slot.</param>
    [DllImport(LibraryName, EntryPoint = "SBW_Array_Set", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ArraySet(
        IntPtr value, nint index,
        TypeMetadata arrayMetadata, IntPtr self);

    /// <summary>
    /// Cdecl wrapper for <c>Array.append(_:)</c>
    /// (<c>$sSa6appendyyxnF</c>). <paramref name="value"/> is consumed by the
    /// call — the caller must NOT destroy it.
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "SBW_Array_Append", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ArrayAppend(
        IntPtr value, TypeMetadata arrayMetadata, IntPtr self);

    /// <summary>
    /// Cdecl wrapper for <c>Array.insert(_:at:)</c>
    /// (<c>$sSa6insert_2atyxn_SitF</c>). <paramref name="value"/> is consumed
    /// by the call — the caller must NOT destroy it.
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "SBW_Array_Insert", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ArrayInsert(
        IntPtr value, nint index,
        TypeMetadata arrayMetadata, IntPtr self);

    /// <summary>
    /// Cdecl wrapper for <c>Array.removeAll(keepingCapacity:)</c>
    /// (<c>$sSa9removeAll15keepingCapacityySb_tF</c>).
    /// </summary>
    /// <param name="keepCapacity">Swift's <c>keepingCapacity:</c> flag; any
    /// non-zero byte means <c>true</c>.</param>
    /// <param name="arrayMetadata">Full <c>Array&lt;Element&gt;</c> type
    /// metadata (hidden generic-context arg).</param>
    /// <param name="self">Pointer to the array's storage slot.</param>
    [DllImport(LibraryName, EntryPoint = "SBW_Array_RemoveAll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ArrayRemoveAll(
        byte keepCapacity, TypeMetadata arrayMetadata, IntPtr self);

    // -----------------------------------------------------------------
    // Hashable
    // -----------------------------------------------------------------

    /// <summary>
    /// Cdecl wrapper for Swift's <c>Hashable.hashValue</c> getter dispatch
    /// thunk (<c>$sSH9hashValueSivgTj</c>). Unlike every other entry here this
    /// targets a protocol <c>…Tj</c> thunk rather than a concrete stdlib
    /// function: the thunk loads the requirement's function pointer out of
    /// <paramref name="hashableWitnessTable"/> and branches to it, so the
    /// conforming type's own <c>hashValue</c> runs. That makes the register
    /// contract unforgiving — a misplaced argument here does not yield a wrong
    /// hash, it yields a garbage function pointer the thunk jumps to.
    /// </summary>
    /// <param name="selfMetadata">Type metadata for the conforming type.</param>
    /// <param name="hashableWitnessTable">The type's <c>Hashable</c> protocol
    /// witness table.</param>
    /// <param name="self">Pointer to the marshalled value. Borrowed — the
    /// requirement is <c>@in_guaranteed</c>, so the buffer stays the caller's
    /// to destroy.</param>
    /// <returns>Swift's 64-bit <c>Int</c> hash value.</returns>
    [DllImport(LibraryName, EntryPoint = "SBW_Hashable_HashValue", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint HashableHashValue(
        TypeMetadata selfMetadata, ProtocolWitnessTable hashableWitnessTable, IntPtr self);
}
