// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Plain-<see cref="CallingConvention.Cdecl"/> P/Invokes into the six C-side
/// wrappers in <c>SwiftBindingsRuntimeCollections.c</c>. Each entry mirrors a
/// Swift stdlib generic-collection operation whose direct
/// <c>CallConvSwift</c> shape (<c>SwiftIndirectResult</c> +
/// intermediate integer args + <c>SwiftSelf</c>) is mishandled by the Mac
/// Catalyst-x64 workload Mono runtime's CallConvSwift trampoline: the
/// trampoline writes the correct sret result but corrupts the caller's
/// <c>self</c> slot when explicit integer args are interleaved between the
/// indirect-result and self registers. The same managed code + Swift dylib +
/// x86_64 Rosetta slice PASSES on macOS-x64 (CoreCLR osx-x64) and on arm64
/// across every target; only the maccatalyst-x64 workload Mono runtime
/// fails. See <c>SretSelfProbeTests</c> for the minimal hand-marshalled
/// reproduction proving this is upstream.
///
/// The C wrappers redeclare the stdlib symbols with clang's
/// <c>__attribute__((swiftcall))</c> + <c>swift_indirect_result</c> +
/// <c>swift_context</c> parameter attrs, so the inner Swift-to-Swift call is
/// lowered through LLVM swiftcc — the same machinery swiftc uses, correct
/// on every supported arch. C# enters via plain Cdecl, which Mono's
/// well-tested cdecl path handles correctly. The broken CallConvSwift
/// trampoline is bypassed entirely.
///
/// Coverage rule: only the six mutating ops that combine
/// sret + intermediate args + SwiftSelf are wrapped. Non-mutating reads
/// (<c>Dictionary.subscript</c>, <c>Set.contains</c>,
/// <c>Array.subscript</c>, <c>count</c>, <c>makeIterator</c>,
/// <c>removeAll(keepingCapacity:)</c>, <c>Array.append/insert/set</c>) keep
/// their direct CallConvSwift P/Invoke — those don't combine
/// sret + intermediate-args + SwiftSelf and pass on Catalyst-x64 already.
/// Routing them through wrappers would be churn without benefit.
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
