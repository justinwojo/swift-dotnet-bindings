// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Cdecl wrappers around seven Swift stdlib generic-collection ops whose
// direct `CallConvSwift` P/Invoke shape is mishandled by a Mono runtime's
// CallConvSwift trampoline. Two distinct broken ABI shapes are covered here,
// with different reproduction footprints — do not collapse them into one
// story.
//
// ---------------------------------------------------------------------------
// Shape A — `SwiftIndirectResult` + one or more explicit integer arguments +
// `SwiftSelf`. Six wrappers: `SBW_Dict_UpdateValue`, `SBW_Dict_RemoveValue`,
// `SBW_Dict_IteratorNext`, `SBW_Set_Remove`, `SBW_Set_IteratorNext`,
// `SBW_Array_Remove`.
//
// Broken only on the Mac Catalyst-x64 workload Mono runtime, and
// deterministically so: the trampoline writes the correct `sret` result but
// corrupts the caller's `self` slot when intermediate integer arguments are
// present. The same managed C# code + same Swift dylib + same x86_64 Rosetta
// slice PASSES on macOS-x64 (CoreCLR osx-x64) and on arm64 across every
// target; only the maccatalyst-x64 workload Mono runtime fails. Reproduced
// and proven by `SretSelfProbeTests`
// (`BindingTests/RuntimeTestsApp/Marshalling/`) paired with
// `AbiSafety.swift::SretSelfProbe`. A four-test probe was used: direct heap,
// direct stack, cdecl control, and a no-arg sret+self corroborator
// (`FactoryMake`). The no-arg corroborator and the cdecl control PASS on
// every target; the two direct probes fail deterministically only on
// Catalyst-x64. The discriminator is "explicit integer args between
// `SwiftIndirectResult` and `SwiftSelf`".
//
// ---------------------------------------------------------------------------
// Shape B — a mixed tuple return `(Bool direct, @out Element)` where the
// `@out` buffer pointer is a REGULAR leading argument (x0 / rdi) rather than
// an `sret` (x8 / swiftself-adjacent) register, combined with `SwiftSelf`.
// One wrapper: `SBW_Set_Insert`.
//
// Broken on the iOS Simulator Mono runtime — reproduced on arm64 simulator
// (.NET 10.0, Xcode 26.x), which is where the failure was first isolated;
// x86_64 simulator/Catalyst share that trampoline, so the wrapper covers
// them too. Failure mode differs from shape A: the call does not merely
// return a wrong value, it corrupts Mono's own thread state. Observed
// signatures are an immediate SIGABRT with
// `Cannot transition thread 0x0 from STARTING with DONE_BLOCKING` (Mono's
// thread-state machine asserting on the exit of the managed-to-native
// GC-safe region), or — when the process survives the call — a Set whose
// `count` reads garbage because a trampoline scratch address was written
// into the caller's `self` slot, then a SIGSEGV on a later insert or on the
// Set's release. Not reproduced on NativeAOT (device) or CoreCLR (macOS);
// those runtimes handle the raw shape correctly, but go through the wrapper
// anyway so the dispatch shape stays identical everywhere (see below).
//
// Shape B is NOT a variant of shape A: `Dictionary.updateValue` (a pure
// `@out` via `SwiftIndirectResult`) and `Set.contains` (single direct return,
// no `@out`) both pass on the iOS Simulator. The unique failing shape is a
// return tuple that is part-direct and part-`@out`, where `x0` serves as both
// the inbound out-pointer argument and the outbound scalar result.
//
// ---------------------------------------------------------------------------
// Architecture of the fix: clang's `__attribute__((swiftcall))` lets us
// declare a function with the Swift calling convention, and the parameter
// attributes `swift_indirect_result` / `swift_context` map to LLVM's
// `sret` / `swiftself` register classes — exactly what swiftc emits. Inside
// each wrapper, clang lowers the call to the stdlib mangled symbol using
// LLVM's `swiftcc`, which produces correct code (the same machinery swiftc
// uses). Mono never sees CallConvSwift at the managed boundary — each
// wrapper is exported as a plain Cdecl symbol, which Mono's well-tested
// cdecl trampoline handles correctly. The broken CallConvSwift trampoline is
// bypassed entirely.
//
// All seven wrappers are linked on every architecture, including ones that
// do not exhibit either bug, to keep the dispatch shape identical: a single
// code path everywhere is easier to reason about than per-arch dispatch, and
// one extra function call is not measurable next to a stdlib generic
// collection operation.
//
// Coverage rule: only those seven ops are wrapped. Non-mutating reads
// (`Dictionary.subscript`, `Set.contains`, `Array.subscript`, `count`,
// `makeIterator`, `removeAll(keepingCapacity:)`, `Array.append/insert/set`)
// match neither broken shape and pass on every runtime already; routing them
// through wrappers would be churn without benefit.

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
// stdlib method exactly. The six shape-A ops share one layout:
//   - first param: `swift_indirect_result` (sret) — the Optional<…> return buffer
//   - middle params: by-pointer K/V/element + the hidden generic-context metadata
//   - last param: `swift_context` — the collection instance pointer (`self`)
//
// `Set.insert(_:)` (shape B) deliberately does NOT follow that layout — its
// `@out` buffer is an ordinary leading pointer argument, not an sret. See its
// declaration below.
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

// Set.insert(_:) — returns (inserted: Bool, memberAfterInsert: Element),
// mutating. Shape B: the SIL type is
// `(@in Element, @inout Set<Element>) -> (Bool, @out Element)`, and swiftc
// lowers that mixed tuple with the `@out Element` buffer as an ORDINARY
// first pointer argument — NOT `swift_indirect_result`. Verified by
// disassembling a `Set<T>.insert` call site emitted by swiftc for both
// simulator slices:
//   arm64:  x0 = memberAfterInsert buffer, x1 = element, x2 = Set metadata,
//           x20 = self (swiftself); Bool returned in w0.
//   x86_64: rdi = memberAfterInsert buffer, rsi = element, rdx = Set
//           metadata, r13 = self (swiftself); Bool returned in al.
// `x0`/`rdi` therefore carries the inbound out-pointer AND the outbound
// scalar result — the register reuse that shape B is named for.
//
// Ownership, also read off the swiftc-emitted call site: the caller copies
// the element into a temporary (+1) which `insert` CONSUMES — the call site
// does not destroy it — and the caller DOES destroy the memberAfterInsert
// buffer through the element's value-witness table once it is done with it.
// The C# caller must reproduce exactly that.
//
// The result is declared `_Bool`, not `unsigned char`, and that is load-bearing.
// swiftc declares this symbol `swiftcc i1` with NO `zeroext` — so only bit 0 of
// the return register is defined, and bits 1-7 are whatever the callee left
// there. Declaring the result as a byte makes clang lower the call as
// `swiftcc i8` and mask with 0xff, which preserves those undefined bits and can
// turn a `false` into a nonzero byte. `_Bool` type-matches the `i1` and clang
// masks with 0x1 (verified in the emitted arm64 and x86_64 code).
extern SBW_SWIFTCALL _Bool
_sbw_swift_set_insert(
    void* outMember,
    void* element,
    void* setMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSh6insertySb8inserted_x17memberAfterInserttxnF");

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

// Returns exactly 1 if the element was newly inserted, exactly 0 if an equal
// element was already present. `outMember` receives `memberAfterInsert` at +1
// and is the caller's to destroy; `element` is consumed by the call.
unsigned char SBW_Set_Insert(
    void* outMember,
    void* element,
    void* setMetadata,
    void* selfPtr
) {
    return _sbw_swift_set_insert(outMember, element, setMetadata, selfPtr) ? 1 : 0;
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
