// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Cdecl wrappers around the Swift stdlib entry points that `Swift.Runtime`
// calls by hand and whose direct `CallConvSwift` P/Invoke shape is mishandled
// by a Mono runtime's CallConvSwift trampoline. Fourteen wrappers, covering
// two distinct broken ABI shapes with different reproduction footprints — do
// not collapse them into one story.
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
// Shape B — an untyped `SwiftSelf` on a `CallConvSwift` call. Eight wrappers:
// `SBW_Set_Insert`, `SBW_Array_Set`, `SBW_Array_Append`, `SBW_Array_Insert`,
// `SBW_Array_RemoveAll`, `SBW_Set_RemoveAll`, `SBW_Dict_RemoveAll`,
// `SBW_Hashable_HashValue`.
//
// Unlike shape A this is not really a *shape*. Its eligible population is
// every `CallConvSwift` call carrying an untyped `SwiftSelf`, and which of
// those actually break is decided by Mono's register allocator rather than by
// anything in the signature. Mono's managed-to-native wrapper parks the cookie
// returned by `mono_threads_enter_gc_safe_region_unbalanced` in a callee-saved
// register its allocator picks, and that allocator does not exclude x20 — the
// register CallConvSwift reserves for an untyped `SwiftSelf`. When it picks
// x20, the wrapper's own argument setup overwrites the cookie with the `self`
// pointer, and the matching exit call then hands
// `mono_threads_exit_gc_safe_region_unbalanced` a `self` pointer where a
// `MonoThreadInfo*` should be.
//
// `Set.insert(_:)` is the member that was first observed failing, on the iOS
// Simulator Mono runtime (isolated on arm64 simulator; x86_64
// simulator/Catalyst share that wrapper codegen). Its failure mode differs
// from shape A: the call does not merely return a wrong value, it corrupts
// Mono's own thread state. Observed signatures are an immediate SIGABRT with
// `Cannot transition thread 0x0 from STARTING with DONE_BLOCKING` (Mono's
// thread-state machine asserting on the exit of the managed-to-native
// GC-safe region), or — when the process survives the call — a Set whose
// `count` reads garbage because a trampoline scratch address was written
// into the caller's `self` slot, then a SIGSEGV on a later insert or on the
// Set's release. Not reproduced on NativeAOT (device) or CoreCLR (macOS);
// those runtimes handle the raw shape correctly, but go through the wrapper
// anyway so the dispatch shape stays identical everywhere (see below).
//
// The doc comment here previously scoped shape B to `Set.insert`'s
// `(inserted: Bool, memberAfterInsert: Element)` tuple return — a mixed tuple
// whose `@out` buffer pointer is a REGULAR leading argument (x0 / rdi) rather
// than an `sret` (x8) register. That correlation held across three samples and
// is not the cause; the register account above is. The tuple shape is still
// worth recording because it is why `SBW_Set_Insert`'s declaration does not
// look like its siblings, so it is documented at that declaration.
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
// All fourteen wrappers are linked on every architecture, including ones that
// do not exhibit either bug, to keep the dispatch shape identical: a single
// code path everywhere is easier to reason about than per-arch dispatch, and
// one extra function call is not measurable next to a stdlib generic
// collection operation.
//
// ---------------------------------------------------------------------------
// Coverage rule: every hand-written `Swift.Runtime` P/Invoke that carries an
// untyped `SwiftSelf` is wrapped, plus the six shape-A ops. Nothing else.
//
// That rule replaces a deliberately reactive one — "wrap a member once it is
// observed to break, because wrapping a member that has never failed is
// churn". The register-level account above does not survive it. Which member
// parks its cookie in x20 is a property of one compiled binary, so a member
// that is safe today moves onto the defect from a Mono codegen change, a .NET
// update, or an unrelated edit to the calling method — with nothing on our
// side changing, and with no test that would have gone red first. The evidence
// that stands in for a crash is a direct measurement: disassemble the Mono
// full-AOT app binary, and for every `wrapper_managed_to_native_*` symbol
// carrying a `SwiftSelf`, read which callee-saved register receives the cookie
// and whether the argument setup between the park and the native call writes
// it. Two independently built binaries produced the same clobbering members in
// the same register, so the allocation is reproducible per call site rather
// than a per-build coin flip. Reproducible register-allocation evidence for a
// known defect class counts as an observed failure, and the remedy is applied
// to the class rather than to whichever members happened to be unlucky in the
// binary someone last looked at.
//
// Calls with no untyped `SwiftSelf` are outside the eligible population
// entirely and keep their direct `CallConvSwift` P/Invoke: metadata accessors
// (`…Ma`), the `init` entry points, `Set.contains`, `Array.subscript.getter`,
// `count`, and `makeIterator` all pass `self` as an ordinary pointer argument,
// so there is no cookie register to clobber.

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
// SIL: (@in Value, @in Key, @inout Dictionary<Key, Value>) -> @out Optional<Value>.
// arm64: x0 = value, x1 = key, x2 = the generic-context metadata (full
// Dictionary<K,V> metadata, not K/V/witness-table separately), x8 = result,
// x20 = self.
extern SBW_SWIFTCALL void
_sbw_swift_dict_updateValue(
    void* SBW_SWIFT_INDIRECT_RESULT result,
    void* value,
    void* key,
    void* dictionaryMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSD11updateValue_6forKeyq_Sgq_n_xtF");

// Dictionary.removeValue(forKey:) — returns Optional<Value>, mutating.
// SIL: (@in_guaranteed Key, @inout Dictionary<Key, Value>) -> @out Optional<Value>.
// arm64: x0 = key, x1 = the generic-context metadata (full Dictionary<K,V>
// metadata), x8 = result, x20 = self.
extern SBW_SWIFTCALL void
_sbw_swift_dict_removeValue(
    void* SBW_SWIFT_INDIRECT_RESULT result,
    void* key,
    void* dictionaryMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSD11removeValue6forKeyq_Sgx_tF");

// Dictionary<K,V>.Iterator.next() — returns Optional<(K, V)>, mutating.
// SIL: (@inout Dictionary<Key, Value>.Iterator) -> @out Optional<(Key, Value)>.
// arm64: x0 = the generic-context metadata (full Iterator metadata, not the
// Dictionary's), x8 = result, x20 = self.
extern SBW_SWIFTCALL void
_sbw_swift_dict_iterator_next(
    void* SBW_SWIFT_INDIRECT_RESULT result,
    void* iteratorMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSD8IteratorV4nextx3key_q_5valuetSgyF");

// Dictionary.removeAll(keepingCapacity:) — mutating, void return.
// SIL: (Bool, @inout Dictionary<Key, Value>) -> ().
// arm64: x0 = keepCapacity, x1 = the generic-context metadata, x20 = self.
// The `_Bool` parameter type is deliberate: swiftc lowers a Swift `Bool`
// argument as an `i1`, and `unsigned char` would make clang pass an `i8`
// whose upper bits are undefined to the callee.
extern SBW_SWIFTCALL void
_sbw_swift_dict_removeAll(
    _Bool keepCapacity,
    void* dictionaryMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSD9removeAll15keepingCapacityySb_tF");

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
// SIL: (@in_guaranteed Element, @inout Set<Element>) -> @out Optional<Element>.
// A pure `@out` return does use `swift_indirect_result`, unlike the mixed
// tuple `insert` returns below.
// arm64: x0 = element, x1 = the generic-context metadata (full Set metadata),
// x8 = result, x20 = self.
extern SBW_SWIFTCALL void
_sbw_swift_set_remove(
    void* SBW_SWIFT_INDIRECT_RESULT result,
    void* element,
    void* setMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSh6removeyxSgxF");

// Set<E>.Iterator.next() — returns Optional<Element>, mutating.
// SIL: (@inout Set<Element>.Iterator) -> @out Optional<Element>.
// arm64: x0 = the generic-context metadata (full Iterator metadata, not the
// Set's), x8 = result, x20 = self.
extern SBW_SWIFTCALL void
_sbw_swift_set_iterator_next(
    void* SBW_SWIFT_INDIRECT_RESULT result,
    void* iteratorMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSh8IteratorV4nextxSgyF");

// Set.removeAll(keepingCapacity:) — mutating, void return.
// SIL: (Bool, @inout Set<Element>) -> ().
// arm64: x0 = keepCapacity, x1 = the generic-context metadata, x20 = self.
extern SBW_SWIFTCALL void
_sbw_swift_set_removeAll(
    _Bool keepCapacity,
    void* setMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSh9removeAll15keepingCapacityySb_tF");

// Array.remove(at:) — returns Element, mutating. Element is unconstrained
// generically, so the lowered ABI returns indirectly via the sret pointer
// even when the concrete element type is register-sized — runtime dispatch
// has no concrete-type knowledge here.
// SIL: (Int, @inout Array<Element>) -> @out Element.
// arm64: x0 = index, x1 = the generic-context metadata (full Array<Element>
// metadata), x8 = result, x20 = self.
extern SBW_SWIFTCALL void
_sbw_swift_array_remove(
    void* SBW_SWIFT_INDIRECT_RESULT result,
    ptrdiff_t index,
    void* arrayMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSa6remove2atxSi_tF");

// Array.subscript(_:).setter — mutating, void return.
// SIL: (@in Element, Int, @inout Array<Element>) -> ().
// arm64: x0 = element, x1 = index, x2 = the generic-context metadata,
// x20 = self. The element is passed `@in` consuming: the call takes over the
// caller's +1 and the caller must not destroy the buffer afterwards.
extern SBW_SWIFTCALL void
_sbw_swift_array_set(
    void* value,
    ptrdiff_t index,
    void* arrayMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSayxSicis");

// Array.append(_:) — mutating, void return.
// SIL: (@in Element, @inout Array<Element>) -> ().
// arm64: x0 = element, x1 = the generic-context metadata, x20 = self.
extern SBW_SWIFTCALL void
_sbw_swift_array_append(
    void* value,
    void* arrayMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSa6appendyyxnF");

// Array.insert(_:at:) — mutating, void return.
// SIL: (@in Element, Int, @inout Array<Element>) -> ().
// arm64: x0 = element, x1 = index, x2 = the generic-context metadata,
// x20 = self.
extern SBW_SWIFTCALL void
_sbw_swift_array_insert(
    void* value,
    ptrdiff_t index,
    void* arrayMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSa6insert_2atyxn_SitF");

// Array.removeAll(keepingCapacity:) — mutating, void return.
// SIL: (Bool, @inout Array<Element>) -> ().
// arm64: x0 = keepCapacity, x1 = the generic-context metadata, x20 = self.
extern SBW_SWIFTCALL void
_sbw_swift_array_removeAll(
    _Bool keepCapacity,
    void* arrayMetadata,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSa9removeAll15keepingCapacityySb_tF");

// -----------------------------------------------------------------------------
// Hashable.hashValue — the one entry point here that is not a concrete stdlib
// function but a protocol DISPATCH THUNK (`…Tj`). The thunk itself performs no
// hashing: it loads the requirement's function pointer out of the witness table
// it is handed and tail-branches to it, so the conforming type's own
// `hashValue` runs. Verbatim on arm64:
//
//     ldr x2, [x1, #0x10]
//     br  x2
//
// which fixes the register contract for every caller: x1 must be the `Hashable`
// witness table (the requirement sits at witness index 2), x0 the conforming
// type's metadata, and x20 the pointer to the value. Concrete witnesses confirm
// the last of those from the other side — Int's
// `_$sSiSHsSH9hashValueSivgTW` opens with `ldr x1, [x20]`.
//
// That contract is what makes the wrapper worth having and also what makes it
// dangerous to get wrong: unlike the collection ops, a misplaced argument here
// does not produce a wrong value, it produces a garbage function pointer that
// the thunk branches to. Declaring `self` as the `swift_context` parameter and
// the metadata/witness pair as the two ordinary leading arguments reproduces
// the layout above, and lets clang — not Mono — lower it.
// -----------------------------------------------------------------------------

// Swift.Hashable.hashValue.getter dispatch thunk.
// SIL: @convention(witness_method: Hashable) <Self: Hashable>
//          (@in_guaranteed Self) -> Int
// arm64: x0 = Self metadata, x1 = Hashable witness table, x20 = self;
// the Swift.Int result comes back in x0.
extern SBW_SWIFTCALL ptrdiff_t
_sbw_swift_hashable_hashValue(
    void* selfMetadata,
    void* hashableWitnessTable,
    void* SBW_SWIFT_CONTEXT selfPtr
) __asm("_$sSH9hashValueSivgTj");

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

// `keepCapacity` crosses the Cdecl boundary as a byte and is normalised to a
// single bit here, so the swiftcc call always receives a well-formed `i1`.
void SBW_Dict_RemoveAll(
    unsigned char keepCapacity,
    void* dictionaryMetadata,
    void* selfPtr
) {
    _sbw_swift_dict_removeAll(keepCapacity != 0, dictionaryMetadata, selfPtr);
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

void SBW_Set_RemoveAll(
    unsigned char keepCapacity,
    void* setMetadata,
    void* selfPtr
) {
    _sbw_swift_set_removeAll(keepCapacity != 0, setMetadata, selfPtr);
}

void SBW_Array_Remove(
    void* result,
    ptrdiff_t index,
    void* arrayMetadata,
    void* selfPtr
) {
    _sbw_swift_array_remove(result, index, arrayMetadata, selfPtr);
}

// The element buffer is consumed by the call — the caller must not destroy it.
void SBW_Array_Set(
    void* value,
    ptrdiff_t index,
    void* arrayMetadata,
    void* selfPtr
) {
    _sbw_swift_array_set(value, index, arrayMetadata, selfPtr);
}

// The element buffer is consumed by the call — the caller must not destroy it.
void SBW_Array_Append(
    void* value,
    void* arrayMetadata,
    void* selfPtr
) {
    _sbw_swift_array_append(value, arrayMetadata, selfPtr);
}

// The element buffer is consumed by the call — the caller must not destroy it.
void SBW_Array_Insert(
    void* value,
    ptrdiff_t index,
    void* arrayMetadata,
    void* selfPtr
) {
    _sbw_swift_array_insert(value, index, arrayMetadata, selfPtr);
}

void SBW_Array_RemoveAll(
    unsigned char keepCapacity,
    void* arrayMetadata,
    void* selfPtr
) {
    _sbw_swift_array_removeAll(keepCapacity != 0, arrayMetadata, selfPtr);
}

// -----------------------------------------------------------------------------
// Hashable
// -----------------------------------------------------------------------------

// Dispatches `hashValue` through the supplied `Hashable` witness table and
// returns the conforming type's own Swift.Int hash. `selfPtr` is borrowed —
// the requirement is `@in_guaranteed`, so the buffer stays the caller's to
// destroy.
ptrdiff_t SBW_Hashable_HashValue(
    void* selfMetadata,
    void* hashableWitnessTable,
    void* selfPtr
) {
    return _sbw_swift_hashable_hashValue(selfMetadata, hashableWitnessTable, selfPtr);
}
