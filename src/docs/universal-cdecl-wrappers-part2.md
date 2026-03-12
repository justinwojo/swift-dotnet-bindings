# Universal @_cdecl Wrappers — Part 2: Remaining Work

> Part 1 (`universal-cdecl-wrappers-design.md`) documents the completed architecture and Sessions 1-5. This document covers all remaining sessions.

---

## Current State

### What's done (Part 1)

Sessions 1-5 of Phase 3.5 are complete, covering:

| Sub-phase | What | Session |
|---|---|---|
| A | Free functions | 1 |
| B | Metadata accessors | 1 |
| H | Runtime fallback removal (SwiftString, TypeMetadata) | 1 |
| C.1 | Optional\<reference-type\> — nullable pointer ABI | 2 |
| E | All 7 @_silgen_name wrapper paths → @_cdecl | 3 |
| F | Protocol existential params/returns | 4 |
| G.1 | Subscript accessors | 4 |
| C.2 | Optional\<value-type\> — IndirectResult | 5 |
| G.4 | Closure returns — IndirectResult | 5 |

Plus Phases 1-3 (property/method/constructor/destroy wrappers, DllImport resolver) and Phase 2.5 (inline closure params in @_cdecl wrappers).

### What remains

| Sub-phase | What | Estimated scope |
|---|---|---|
| D | Generic parent types (methods, properties, constructors) | ~80% of remaining CallConvSwift |
| G.2 | Non-frozen struct returns (methods) | Infrastructure done, blocked by generic parent guard |
| G.3 | Complex enum constructors | Infrastructure done, blocked by generic parent guard |
| 15c | Tuple returns | 19 libraries affected |
| 15d | DynamicSelf returns | 10+ libraries affected |
| Phase 4 | Cleanup + documentation | Workaround removal, diagnostics, doc updates |

### The real numbers

As of the last validation run:

- **4,565 CallConvSwift P/Invokes** remain in C# bindings across 90 libraries
- **1,109 Cdecl P/Invokes** exist — about 20% of the total
- **Generic parent types are the dominant driver**: GRDB (574), Alamofire (506), Kingfisher (320), RxSwift (279), Mappedin (239), StripePaymentSheet (234), StripePayments (223), Lottie (198)
- **Tuples are not zero-impact**: appear in 19/90 libraries (Kingfisher 13, Lottie 12, Alamofire 11, Starscream 10, BonMot 10)
- **DynamicSelf/AnyType is not zero-impact**: GRDB (142), Kingfisher (102), TinyConstraints (65), CryptoSwift (45), Lottie (22)

The "Done" definition says "near-zero CallConvSwift." We're at ~80% CallConvSwift. Most of that is generic parent types.

---

## Session 6: Generic Parent Methods + Properties ✅ COMPLETE

**Goal**: Lift guard 5b in `MethodWrapperEmitter`, guard 2 in `PropertyWrapperEmitter`, and guard 2 in `SubscriptWrapperEmitter` for generic parent types with concrete method/property/subscript signatures (no type-parameter-bearing params/returns).

**Sub-phases**: D (methods + properties + subscripts — constructors are Session 7)

### Architecture: Protocol-based type erasure (single-layer)

Instead of the originally proposed two-layer `@_silgen_name` → `@_cdecl` model, we use **protocol-based type erasure** — a single-layer `@_cdecl` approach that avoids needing to know the generic type parameter at the wrapper level:

1. For each member, generate a **private protocol** with the exact member signature
2. Add a **retroactive extension conformance** for the generic class
3. Reconstruct self via `Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! any ProtocolName`
4. Call the member through the **protocol existential** — Swift dispatches through the vtable

```swift
// Per-member private protocol matching the method signature
private protocol _SBW_P_A1B2C3D4 {
    func removeAll()
}
extension GRDB.Table: _SBW_P_A1B2C3D4 {}

// Single-layer @_cdecl wrapper (no generics needed)
@_cdecl("SBW_GRDB_Table_removeAll_A1B2C3D4")
func _sbw_removeAll_A1B2C3D4(_ _metadata0: UnsafeRawPointer, _ self_: UnsafeMutableRawPointer) {
    let obj = Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! any _SBW_P_A1B2C3D4
    obj.removeAll()
}
```

**Why this approach**: The original two-layer model required `@_silgen_name` to reconstruct generic types from metadata, but library-specific constraints (e.g., `T: FetchableRecord`) can't be satisfied at the wrapper level. Protocol erasure avoids this entirely — the conformance is unconditionally valid because the method already exists on the type for all valid T.

**Limitations**: Only works for class types (AnyObject cast requires classes), instance members (not static), and members where T doesn't appear in the signature. Metadata params from C# `PInvokeHelperContext` are accepted but unused.

### Guards lifted

| Guard | Emitter | What was blocked | Solution |
|---|---|---|---|
| 5b | MethodWrapperEmitter | `parentTypeDecl?.IsGeneric == true` | Protocol erasure for class parents with concrete signatures |
| 2 | PropertyWrapperEmitter | `td.IsGeneric` | Same approach (getter + setter protocols) |
| 2 | SubscriptWrapperEmitter | `td.IsGeneric` | Same approach (getter + setter protocols with subscript syntax) |

### Files changed

- `MethodWrapperEmitter.cs` — lifted guard 5b, added `CanEmitGenericClassWrapper`, `HasGenericTypeParamInSignature`, `TypeSpecReferencesGenericParam` (handles NamedTypeSpec, ClosureTypeSpec, TupleTypeSpec, ProtocolListTypeSpec), `BuildProtocolMethodDeclaration`, `EmitGenericClassProtocolAndConformance`, `IsGenericClassParent`
- `PropertyWrapperEmitter.cs` — lifted guard 2, added getter/setter protocol emission, metadata params, AnyObject-based self reconstruction for both getter and setter
- `SubscriptWrapperEmitter.cs` — lifted guard 2, added getter/setter protocol emission with subscript syntax, metadata params, AnyObject-based self reconstruction
- `SilgenNameTrampolineTests.cs` — updated tests for new generic parent behavior
- `MethodWrapperEmitterTests.cs` — added generic class/struct/static/T-referencing guard tests + emission test
- `PropertyWrapperEmitterTests.cs` — added guard tests + getter/setter emission tests
- `SubscriptWrapperEmitterTests.cs` — added guard tests for generic struct/class/T-referencing return

### Validation gate

- [x] `./run-tests.sh` — 7,340 tests pass, 0 failures
- [x] `./validate-libraries.sh --tier all` — 90/90 pass, 0 regressions
- [x] GPT-5.4 review — all critical findings addressed (ProtocolListTypeSpec handling, unused param cleanup)

---

## Session 7: Generic Parent Constructors

**Goal**: Lift guard 3 in `ConstructorWrapperEmitter` for generic parent types with concrete constructor signatures.

**Why separate from Session 6**: Constructor return/allocation semantics differ from methods/properties:
- Ownership transfer: constructor creates the object, caller takes ownership
- Failable init: `Optional<UnsafeRawPointer>` return (nil = init failed)
- Class vs struct: class returns retained pointer, struct writes to result buffer
- These add complexity on top of generic dispatch

**Prerequisite**: Session 6 proven and stable. The specialization story must work for methods/properties before mixing in constructor semantics.

### Guard to lift

| Guard | Emitter | What it blocks |
|---|---|---|
| 3 | ConstructorWrapperEmitter (line 35) | `typeDecl.IsGeneric` |

### Files

- `ConstructorWrapperEmitter.cs` — lift guard 3, emit two-layer wrapper
- `PInvokeEmitter.cs` — extend generic Cdecl routing for constructors

### Validation gate

- [ ] `./run-tests.sh` — all tests pass
- [ ] `./validate-libraries.sh --tier all` — 90/90 pass
- [ ] Generic type constructors use Cdecl
- [ ] Failable constructors on generic types work correctly

---

## Session 8: Tuple Returns + DynamicSelf Returns

**Goal**: Handle the two remaining non-generic return type guards that were deferred as "zero Nuke impact."

### Sub-phase 15c: Tuple returns

**Guard**: `MethodWrapperEmitter.cs:109` — `if (returnSpec is TupleTypeSpec trs && !trs.IsEmptyTuple) return false;`
Also: `MethodWrapperEmitter.cs:615` (in `HasCdeclCompatibleFunctionShape`), `SubscriptWrapperEmitter.cs:77`

**Impact**: 19 of 90 libraries have tuple types. Kingfisher (13), Lottie (12), Alamofire (11), Starscream (10), BonMot (10).

**Solution**: Write tuple to out-buffer (flattened), same pattern as complex enum returns. Each element is written at its offset using the existing per-element marshalling infrastructure.

```swift
// Tuple return — write each element to result buffer at correct offset
@_cdecl("SBW_Get_Lib_someTupleProperty_A1B2C3D4")
func _sbw_get_someTupleProperty(_ resultPtr: UnsafeMutableRawPointer, _ self_: UnsafeRawPointer) {
    let obj = Unmanaged<Lib.SomeClass>.fromOpaque(self_).takeUnretainedValue()
    let result = obj.someTupleProperty
    resultPtr.initializeMemory(as: (Int32, Swift.String).self, repeating: result, count: 1)
}
```

C# side: `MarshalFromSwift<SwiftTuple<int, SwiftString>>(resultPtr)` or equivalent flattened struct read.

**Requires**: Per-element marshalling rework — need to handle mixed element types (primitives, strings, classes, nested structs) in the buffer layout.

### Sub-phase 15d: DynamicSelf returns

**Guard**: `MethodWrapperEmitter.cs:113` — `if (returnSpec.IsDynamicSelf) return false;`
Also: `MethodWrapperEmitter.cs:617` (in `HasCdeclCompatibleFunctionShape`)

**Impact**: 10+ libraries have AnyType references. GRDB (142), Kingfisher (102), TinyConstraints (65), CryptoSwift (45), Lottie (22).

**Solution**: Return as `UnsafeRawPointer` (class instance), C# wraps in appropriate type. Needs `AnyType` metadata allocation to determine the concrete return type at runtime.

```swift
@_cdecl("SBW_Lib_SomeClass_configure_A1B2C3D4")
func _sbw_configure(_ self_: UnsafeRawPointer) -> UnsafeRawPointer {
    let obj = Unmanaged<Lib.SomeClass>.fromOpaque(self_).takeUnretainedValue()
    let result = obj.configure()
    return Unmanaged.passRetained(result).toOpaque()
}
```

### Validation gate

- [ ] `./run-tests.sh` — all tests pass
- [ ] `./validate-libraries.sh --tier all` — 90/90 pass
- [ ] Kingfisher, Lottie, Alamofire tuple-return APIs use Cdecl
- [ ] DynamicSelf return APIs use Cdecl

---

## Phase 4: Cleanup and Documentation

**Prerequisite**: Sessions 6-8 complete.
**Goal**: Remove ALL workaround infrastructure. Single clean code path. Update documentation. Fix known bugs.

### Code removal

| Component | Why it's unnecessary |
|-----------|---------------------|
| Workaround B: `ClosureEmitter.SwiftWrapper.cs` standalone closure wrappers | All closure-parameter methods route through @_cdecl method/constructor wrappers |
| Workaround B: `HasClosureCdeclWrapper` / `UsesFreeFunctionWrapper` flags | No standalone closure path exists |
| Workaround B: `NeedsClosureCdeclWrapper()` in `MonoJitRiskDetector` | No callers remain |
| Workaround D: `MonoJitRiskDetector.cs` (entire file) | No risky path exists |
| Workaround D: `DetectedJitRisks` on `MethodDecl` | Informational flag with no consumers |
| `[CrashRisk]` attributes on test classes | All tests pass on both Mono and NativeAOT |
| `--safe-only` flag in test runner | No unsafe tests exist |
| Tier 3 deferral for Mono JIT tests | All tiers run everywhere |
| Dual `PInvokeCallingConvention` routing in `PInvokeEmitter` | Single path: all P/Invokes are Cdecl |

### Bug fixes

**Closure adapter heap leak** (from Codex review, Session 1):
- `ClosureEmitter.SwiftWrapper.BuildAdapterClosureBody()` allocates `__heap_N` buffers for complex-enum closure arguments but never deallocates them
- Add `__heap_N.deallocate()` after Cdecl callback invocation
- Add test for repeated closure invocation without native heap growth

### Code quality

**Closure routing flag consolidation** (from Codex review, Session 3):
- Collapse `HasClosureCdeclWrapper`, `UsesFreeFunctionWrapper`, `UsesCdeclMethodWrapper`, `HasCdeclClosureMarshalling` into a single "closure ABI mode" concept
- Separate "closure is supported" vs "closure is Cdecl-adaptable" as distinct concepts in code and comments

### Diagnostics

**SWIFTBIND060 — CallConvSwift fallback diagnostic**:
Any member that can't get a `@_cdecl` wrapper (unfixable Swift compiler restrictions) and retains a `CallConvSwift` P/Invoke must emit:
```
SWIFTBIND060: Member 'ImageProcessor.process(image:)' uses direct CallConvSwift (no @_cdecl wrapper available: non-copyable parameter type)
```
Severity: Warning. Replaces the old `MonoJitRiskDetector` with a proper MSBuild diagnostic.

**Member-level wrapper stripping report** (from Codex review, Session 4):
- Post-processor currently reports stripped block counts, not per-member inventory
- Add member-level reporting: symbol, reason category, fallback status

### Documentation updates

**Ownership contract**: Add centralized ownership invariants section (10 rules from Codex review Session 1). See `cdecl-review-action-items.md` Priority 4.2 for the full list.

**Product contract consolidation**: Establish wrapper-first as the primary ABI boundary story, not a Mono workaround.

**Docs with most drift** (correct during this phase):
- `docs/Known-Limitations.md` — frames wrappers as Mono-specific; NativeAOT also has real crashes
- `docs/NativeAOT-Deployment.md` — describes fallback model no longer true for SwiftString/existential metadata
- `docs/design/binding-closures.md` — reflects old thick-closure architecture, not wrapper-centric model
- `src/docs/known-issues-workarounds.md` — says async+throwing closures unsupported (now partially supported); claims Mono-only (both runtimes affected)
- `PropertyWrapperEmitter.ShouldEmitWrapper()` — stale comment about existential/large-optional exclusions

**Explicit Dispose requirement**: Surface in consumer-facing docs that `Dispose()` is semantically required for full Swift cleanup (finalization intentionally skips destroy).

**Generic destroy fallback**: Document as accepted technical debt — generic containing types skip @_cdecl destroy wrappers and fall back to VWT destroy via `SwiftSafeHandle<T>`.

**Upstream bug reports**: Expand `Future/upstream-bug-reports-draft.md` with all 5 .NET runtime bugs (currently has 3). Add NativeAOT Bugs #2 and #3 with device crash data.

### Validation gate

- [ ] `MonoJitRiskDetector.cs` deleted
- [ ] `HasClosureCdeclWrapper`, `UsesFreeFunctionWrapper`, `DetectedJitRisks` removed
- [ ] `[CrashRisk]` attributes removed from all test classes
- [ ] `--safe-only` flag removed from test runner
- [ ] `PInvokeCallingConvention.Swift` enum value unused
- [ ] `grep -rc "CallConvSwift" /tmp/binding-validation/*/` returns near-zero across all 90 libraries (only unfixable-guard methods)
- [ ] Full test suite passes without workaround code
- [ ] Library validation 90/90 still passes
- [ ] `known-issues-workarounds.md` rewritten
- [ ] `Future/upstream-bug-reports-draft.md` expanded with all 5 bugs
- [ ] Closure heap leak fixed with test

---

## Overall "Done" Definition

- [ ] **Near-zero `CallConvSwift` in generated code** — only unfixable Swift compiler restrictions (actors, non-copyable, nested frozen struct params). Zero in Tier 1-2 validation libraries in practice.
- [ ] **Zero workaround infrastructure** — `MonoJitRiskDetector`, standalone `ClosureEmitter.SwiftWrapper`, `_useWrapperPath`, `HasClosureCdeclWrapper`, `UsesFreeFunctionWrapper`, `DetectedJitRisks`, `[CrashRisk]`, `--safe-only` all deleted
- [ ] **Single code path** — every wrappable P/Invoke goes C# → @_cdecl → Swift. No dual-path routing, no fallbacks, no risk detection. Unfixable-guard methods retain CallConvSwift with `SWIFTBIND060` warning.
- [ ] **All deferred items resolved** — tuple returns, DynamicSelf returns, generic parent types all handled
- [ ] **Upstream bugs documented** — `Future/upstream-bug-reports-draft.md` expanded with all 5 bugs
- [ ] **End-user documentation clear** — ownership contract, product contract, and known limitations all accurate
- [ ] **CallConvSwift fallback diagnostic** — `SWIFTBIND060` emitted for any member that can't get a wrapper

---

## Items Explicitly Remaining Deferred

These are NOT in scope for Sessions 6-8 or Phase 4. They are documented here to prevent scope creep.

### Method-level generics (guard 6)

Requires monomorphization or a full erased ABI spec covering:
- Payload layout (stack allocation based on runtime metadata size)
- Metadata threading (explicit + sometimes implicit metadata pairs)
- Witness-table threading
- Indirect-result behavior
- Ownership and `inout` writeback

Method-level generics are rare in practice — most generic methods in validation libraries are on generic TYPES with concrete method signatures. If they ever move in scope, write the ABI contract first.

### Generic-typed parameters/returns requiring payload erasure

When the generic type parameter appears in a method's params or returns (not just as the parent type), the wrapper must handle erased payload transport. This is an ABI problem, not a dispatch problem. Defer until validation proves it matters enough.

### Generic closure signatures

When type parameters cross the callback boundary, the closure ABI and delegate shape aren't concrete at generation time. Current generic closure bridge support is extremely narrow.

### Overlap combinations

Generic + existential, generic + optional with generic inner, generic + closure with type-parameter crossing. Each combination is only admissible when: (1) the non-generic inner shape is already supported, (2) the generic parameter doesn't introduce a second unsolved ABI problem, (3) validation libraries show the case matters.

### Unfixable Swift compiler restrictions

These affect **zero methods** across all 90 Tier 1-2 validation libraries:

| Guard | What | Why unfixable |
|---|---|---|
| 6b | Actor types | `@_cdecl` is synchronous; actors require async context |
| 11 | Non-copyable structs (`~Copyable`) | C ABI requires copy semantics |
| 12/12b | Nested/non-primitive frozen struct params | Swift: "cannot be represented in Objective-C" |
| 17 | Nested type returns | Swift: "cannot be represented in Objective-C" |

---

## Session Execution Order

Sessions are sequential — each depends on the prior:

1. **Session 6** — Generic parent methods + properties
2. **Session 7** — Generic parent constructors (depends on Session 6 proving the specialization story)
3. **Session 8** — Tuple returns + DynamicSelf returns (independent of generics, but sequenced after to reduce risk)
4. **Phase 4** — Cleanup + documentation (depends on all implementation sessions)

### Validation gate (per session)

Each session must pass:
- [ ] `./run-tests.sh` — all unit + runtime tests pass, 0 failures
- [ ] `./validate-libraries.sh --tier all` — 90/90 pass, 0 regressions
- [ ] CallConvSwift count decreases
