// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Cdecl wrappers around six Swift stdlib generic-collection ops whose direct
// `CallConvSwift` P/Invoke shape — `SwiftIndirectResult` + one or more
// explicit integer arguments + `SwiftSelf` — is mishandled by the Mac
// Catalyst-x64 workload Mono runtime's CallConvSwift trampoline. The bug is
// deterministic: the trampoline writes the correct `sret` result but corrupts
// the caller's `self` slot when intermediate integer arguments are present.
//
// The same managed C# code + same Swift dylib + same x86_64 Rosetta slice
// PASSES on macOS-x64 (CoreCLR osx-x64) and on arm64 across every target;
// only the maccatalyst-x64 workload Mono runtime fails. Reproduced and proven
// by `SretSelfProbeTests` (`BindingTests/RuntimeTestsApp/Marshalling/`)
// paired with `AbiSafety.swift::SretSelfProbe`. A four-test probe was used:
// direct heap, direct stack, cdecl control, and a no-arg sret+self
// corroborator (`FactoryMake`). The no-arg corroborator and the cdecl
// control PASS on every target; the two direct probes fail deterministically
// only on Catalyst-x64. The discriminator is "explicit integer args between
// `SwiftIndirectResult` and `SwiftSelf`".
//
// Architecture of the fix: clang's `__attribute__((swiftcall))` lets us
// declare a function with the Swift calling convention, and the parameter
// attributes `swift_indirect_result` / `swift_context` map to LLVM's
// `sret` / `swiftself` register classes — exactly what swiftc emits. Inside
// each wrapper, clang lowers the call to the stdlib mangled symbol using
// LLVM's `swiftcc`, which produces correct x86_64 code (the same machinery
// swiftc uses). Mono never sees CallConvSwift at the managed boundary —
// each wrapper is exported as a plain Cdecl symbol, which Mono's
// well-tested cdecl trampoline handles correctly. The broken CallConvSwift
// trampoline is bypassed entirely.
//
// All six wrappers are also linked on arm64 to keep the dispatch shape
// identical across architectures. arm64 does not exhibit the bug (AAPCS64
// swiftcc + Mono's arm64 trampoline are both correct), but funnelling
// through the same wrapper means a single code path everywhere — easier
// to reason about than per-arch dispatch — and one extra function call is
// not measurable next to a stdlib generic dictionary operation.
//
// Coverage rule: only those six mutating ops are wrapped. Non-mutating
// reads (`Dictionary.subscript`, `Set.contains`, `Array.subscript`, `count`,
// `makeIterator`, `removeAll(keepingCapacity:)`, `Array.append/insert/set`)
// either don't combine sret+intermediate-args+SwiftSelf or pass on
// Catalyst-x64 already; routing them through wrappers would be churn
// without benefit.

#include <stddef.h>

// -----------------------------------------------------------------------------
// Calling-convention attribute helpers.
// -----------------------------------------------------------------------------

// `swiftcall` is clang's spelling for the Swift calling convention; LLVM's
// x86_64 backend lowers it to the same swiftcc registers swiftc emits.
#define SBW_SWIFTCALL __attribute__((swiftcall))

// `swift_indirect_result` marks a parameter as the indirect-result (sret)
// pointer. On x86_64 swiftcc this register is `%rax`; on arm64 AAPCS64
// swiftcc it is `x8`. There is at most one such parameter, conventionally
// declared first.
#define SBW_SWIFT_INDIRECT_RESULT __attribute__((swift_indirect_result))

// `swift_context` marks the `self` parameter for an instance/mutating
// method. On x86_64 swiftcc this register is `%r13`; on arm64 AAPCS64
// swiftcc it is `x20`. There is at most one such parameter, conventionally
// declared last.
#define SBW_SWIFT_CONTEXT __attribute__((swift_context))

// -----------------------------------------------------------------------------
// External Swift stdlib symbols (CallConvSwift). The `__asm` label binds the C
// declaration directly to the mangled linker symbol — clang skips ALL of its
// usual name-mangling, including the Mach-O leading-underscore that C
// identifiers normally pick up. Apple's libswiftCore.tbd exports its Swift
// mangled symbols WITH the underscore (`_$sSD…`), so each asm label keeps
// that leading underscore explicitly. Equivalent to how `@_silgen_name`
// works on the Swift side — but clang requires the user to spell the prefix
// in the asm label, where Swift adds it implicitly via its LLVM backend.
//
// Each declaration mirrors the swiftcc ABI of the corresponding Swift
// stdlib method exactly:
//   - first param: `swift_indirect_result` (sret) — the Optional<…> return buffer
//   - middle params: by-pointer K/V/element + the hidden generic-context metadata
//   - last param: `swift_context` — the collection instance pointer (`self`)
//
// The metadata parameter type differs across the ops in Swift's signature
// (full collection metadata vs Iterator metadata), but they are all opaque
// pointers at the ABI level — `void*` here is sufficient.
// -----------------------------------------------------------------------------

// Dictionary.updateValue(_:forKey:) — returns Optional<Value>, mutating.
extern SBW_SWIFTCALL void
_sbw_swift_dict_updateValue(
    void* SBW_SWIFT_INDIRECT_RESULT result,
    void* value,
    void* key,
    void* dictionaryMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSD11updateValue_6forKeyq_Sgq_n_xtF");

// Dictionary.removeValue(forKey:) — returns Optional<Value>, mutating.
extern SBW_SWIFTCALL void
_sbw_swift_dict_removeValue(
    void* SBW_SWIFT_INDIRECT_RESULT result,
    void* key,
    void* dictionaryMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSD11removeValue6forKeyq_Sgx_tF");

// Dictionary<K,V>.Iterator.next() — returns Optional<(K, V)>, mutating.
extern SBW_SWIFTCALL void
_sbw_swift_dict_iterator_next(
    void* SBW_SWIFT_INDIRECT_RESULT result,
    void* iteratorMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSD8IteratorV4nextx3key_q_5valuetSgyF");

// Set.remove(_:) — returns Optional<Element>, mutating.
extern SBW_SWIFTCALL void
_sbw_swift_set_remove(
    void* SBW_SWIFT_INDIRECT_RESULT result,
    void* element,
    void* setMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSh6removeyxSgxF");

// Set<E>.Iterator.next() — returns Optional<Element>, mutating.
extern SBW_SWIFTCALL void
_sbw_swift_set_iterator_next(
    void* SBW_SWIFT_INDIRECT_RESULT result,
    void* iteratorMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSh8IteratorV4nextxSgyF");

// Array.remove(at:) — returns Element, mutating. Element is unconstrained
// generically, so the lowered ABI returns indirectly via the sret pointer
// even when the concrete element type is register-sized — runtime dispatch
// has no concrete-type knowledge here.
extern SBW_SWIFTCALL void
_sbw_swift_array_remove(
    void* SBW_SWIFT_INDIRECT_RESULT result,
    ptrdiff_t index,
    void* arrayMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSa6remove2atxSi_tF");

// -----------------------------------------------------------------------------
// Cdecl entry points (C# side calls these via CallingConvention.Cdecl).
//
// Argument order matches the underlying stdlib signature one-to-one so the
// wrapper bodies are a single forwarding call. Compilers will tail-call this
// optimally on every supported target; even without the tail call, one extra
// function call is unmeasurable next to a stdlib generic collection op.
// -----------------------------------------------------------------------------

void SBW_Dict_UpdateValue(
    void* result,
    void* value,
    void* key,
    void* dictionaryMetadata,
    void* selfPtr
) {
    _sbw_swift_dict_updateValue(result, value, key, dictionaryMetadata, selfPtr);
}

void SBW_Dict_RemoveValue(
    void* result,
    void* key,
    void* dictionaryMetadata,
    void* selfPtr
) {
    _sbw_swift_dict_removeValue(result, key, dictionaryMetadata, selfPtr);
}

void SBW_Dict_IteratorNext(
    void* result,
    void* iteratorMetadata,
    void* selfPtr
) {
    _sbw_swift_dict_iterator_next(result, iteratorMetadata, selfPtr);
}

void SBW_Set_Remove(
    void* result,
    void* element,
    void* setMetadata,
    void* selfPtr
) {
    _sbw_swift_set_remove(result, element, setMetadata, selfPtr);
}

void SBW_Set_IteratorNext(
    void* result,
    void* iteratorMetadata,
    void* selfPtr
) {
    _sbw_swift_set_iterator_next(result, iteratorMetadata, selfPtr);
}

void SBW_Array_Remove(
    void* result,
    ptrdiff_t index,
    void* arrayMetadata,
    void* selfPtr
) {
    _sbw_swift_array_remove(result, index, arrayMetadata, selfPtr);
}
