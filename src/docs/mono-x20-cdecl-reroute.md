# Rerouting untyped-`SwiftSelf` members onto the `@_cdecl` wrapper route

**Status:** design brief for a future work session. Nothing here is implemented.
**Decision already taken (2026-09-08):** reroute every exposed member whose direct P/Invoke carries an untyped `SwiftSelf` onto the existing Swift `@_cdecl` wrapper route. This document is that session's starting position — the code map, the exposure inventory, the cost, and the verification plan. It does not re-argue the decision.

> Citations were read against the working tree on 2026-09-08, which had uncommitted edits in flight under `src/Swift.Bindings/src/Emitter/StringEmitter/`. Treat each `path:line` as a starting point for a symbol search, not a fixed address. Every count in §4 was taken from a frozen copy of `BindingTests/output/` at **git HEAD `e986332c3`**; that directory is a working artifact that regeneration moves, so re-measure before relying on any figure here.

---

## 1. What is being removed

Mono's managed-to-native P/Invoke wrapper brackets the call with a GC-safe-region transition whose cookie (a `MonoThreadInfo*`) is parked in a callee-saved register chosen by the wrapper's register allocator. That allocator does not exclude `x20`/`x21` — the registers `CallConvSwift` reserves for `SwiftSelf`/`SwiftError`. When it picks `x20` and the same call then loads an untyped `SwiftSelf` into `x20`, the wrapper's own argument setup destroys the cookie before the branch; after the Swift callee returns normally the exit helper reads a garbage thread record and aborts with `Cannot transition thread 0x0 from STARTING with DONE_BLOCKING`.

Mechanism, disassembly and the suggested upstream fix: `src/docs/Future/upstream-issue-03-mono-set-insert-done-blocking.md`. Runtime-side registry entry: `src/Swift.Runtime/src/Swift/RuntimeLimitations.cs:80` (`MonoSwiftCallDoneBlockingAbort`), `IsAffected` → `isMono` at `:119`.

Three observations from that survey set the scope of this work:

- **An untyped `SwiftSelf` alone is sufficient exposure.** A non-throwing member with no `SwiftError` shows the identical clobber; `throws`, tuple returns and `@out` are incidental.
- **Typed `SwiftSelf<T>` is outside the defect.** A frozen-struct self travels in ordinary argument registers, so an `x20` cookie survives (`LeaseProbe.consumeGated` is the control).
- **Which members abort is Mono's register allocation, not a signature shape.** No predicate over the Swift signature or the C# declaration expresses it; a shape gate would be an unsound guess and would violate the prediction-gate freeze policy (`src/docs/roadmap.md`).

That last point is why the disposition is a blanket reroute of an *ABI-visible register class* (does this call put something in `x20`?) rather than a prediction about which members fail.

---

## 2. Route selection authority

### 2.1 Three emission routes; the direct one is the fallback

Per member the emitter tries, in order: a **native ARM64 thunk**, then a **Swift `@_cdecl` wrapper**, then falls back to a **direct `CallConvSwift` P/Invoke** against the module's own mangled `$s…` symbol. The live routing call site for ordinary methods is `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs:1376-1401`, guarded by `NativeThunkEmitter.ShouldEmitThunk(methodEnv)` and then `WrapperValidation.DetermineMethodWrapperDecision(methodEnv) == WrapperDecision.WrapperRequired` (`:1389`); constructor equivalents at `:425` and `:1312`.

The stated policy is that the wrapper is the route and direct `CallConvSwift` is what remains when no wrapper can be built:

| Decision point | `path:line` (under `src/Swift.Bindings/src/Emitter/`) |
|---|---|
| `WrapperDecision { CannotWrap, WrapperRequired }` — "All eligible methods get @_cdecl wrappers" | `StringEmitter/WrapperValidation.cs:10` |
| `DetermineMethodWrapperDecision` / `…Constructor…` / `…Property…` | `WrapperValidation.cs:74`, `:85`, `:96` |
| `GetCallingConvention` — Cdecl iff `UsesCdeclMethodWrapper \|\| UsesCdeclConstructorWrapper \|\| UsesCdeclPropertyWrapper \|\| UsesNativeThunk`; **everything else, `@_silgen_name` wrappers included, stays Swift** | `WrapperValidation.cs:110` |
| Attribute + `LibraryImport` emission, incl. `SelectCallingConvention` | `StringEmitter/PInvokeEmitHelper.cs:177,238` |
| Debug log naming why a member ended up direct | `Handler/MethodHandler.cs:1544` → `GetWrapperRejectionReason` |

So **this work is not "add a predicate that forces the wrapper route."** It is "close the eligibility gaps that reject these members, so the existing default reaches them" — which keeps the change inside the emitter's contract instead of adding a Mono-keyed special case, and makes the acceptance criterion a `skipReasons` ledger rather than a new flag.

A separate predicate, `WrapperValidation.RequiresCdeclForAbiSafety` (`WrapperValidation.cs:1878` methods/ctors, `:1988` properties), is the codebase's existing *"why direct `CallConvSwift` is unsafe"* map — typed throws, class allocating constructors, struct constructors, static class methods, non-final class instance methods (`:1940`), non-frozen struct instance members (`:1949`), register-unsafe self/return/param types. Its one live production caller today is the operator path (`Handler/OperatorHandler.cs:1097`). It is the natural home for this defect's condition if the session wants the reason expressed in code — but adding a condition there sits close to a prediction gate, so express it as the register class (untyped `SwiftSelf`), never as a signature shape.

### 2.2 The eligibility traversals (guards that force the direct route)

Each member kind has one single-traversal evaluator returning the *first* guard that rejected the member, wrapped by a boolean shim so predicate and diagnostic cannot drift. Result type `WrapperEligibility` (`StringEmitter/WrapperEligibility.cs:17`).

| Kind | Evaluator (`StringEmitter/…`) |
|---|---|
| Method | `MethodWrapperEmitter.EvaluateWrapperEligibility` `:35` (shim `:27`) |
| Constructor | `ConstructorWrapperEmitter.EvaluateWrapperEligibility` `:34` |
| Property accessor | `PropertyWrapperEmitter.EvaluateWrapperEligibility` `:35` |
| Subscript accessor | `SubscriptWrapperEmitter.EvaluateWrapperEligibility` `:34` |
| ObjC-override property | `ObjCOverridePropertyWrapperEmitter.ShouldEmitWrapper` `:26` |
| Shared first-pass guard | `WrapperValidation.GetMemberRejectionReason` `:167` |

Method-route rejections in evaluation order (`MethodWrapperEmitter.cs`): `constructor`/`accessor`/`cdecl_property_wrapper` routed elsewhere (`:38,42,46`); the shared guard — xcframework mode, `module_internal`, `parent_module_internal`, SPI, **async**, actor isolation, inherited generic context (`:50`); `no_parent` (`:60`); `generic_parent_type` (`:67`); `generic_parent_inout` (`:81`); `method_level_generics` (`:90`); `closure_params`/`async_closure` (`:98`); `inout_abi_mismatch` (`:115`, with a narrow `supportsInoutString` opt-in for synchronous non-generic methods); `variadic_parameter` (`:134`); `const_literal_parameter` (`:141`); `nested_frozen_struct_parameter` (`:145`); `uses_wrapper_library` (`:152`); unsupported type signature (`:156` → `GetUnsupportedTypeSignatureReason` `:180`). Property-route rejections add `generic_parent_unresolved_pwt_constraint`, `metatype_property`, `self_property`, `direct_closure_setter`, `optional_closure_not_cdecl_compatible`, `async_property`, `unsupported_generic_container`, `raw_generic_type_params` (`PropertyWrapperEmitter.cs:52`–`:169`); constructor-route rejections add `failable_non_frozen_struct` (`ConstructorWrapperEmitter.cs:52`), `metatype_param` (`:76`), `non_copyable_struct_parameter` (`:85`), `unsupported_buffer_pointer_parameter` (`:99`), `raw_generic_type_params` (`:106`), `variadic_parameter` (`:114`), `const_literal_parameter`, `variadic_expansion_pattern`.

### 2.3 Where the direct P/Invoke and its `SwiftSelf` are emitted

`PInvokeEmitter.HandleSwiftSelf` (`Handler/PInvokeEmitter.cs:1043`) decides the self parameter: wrapper / native-thunk / free-function routes take a plain `IntPtr` self and never a `SwiftSelf` (`:1043`–`:1083`); async instance methods likewise (`:1088`); direct-route frozen-struct **setters** take `MarshalledType.SwiftSelfUntyped` because the mutation needs pointer semantics (`:1112`); direct-route frozen-struct **getters** take `SwiftSelfTyped(…)` (`:1120`) — the 9 unexposed members; everything else on the direct route (classes, non-frozen structs, enums) takes `SwiftSelfUntyped` (`:1131`). Class allocating initializers on the direct route thread the metatype through an untyped `SwiftSelf` in `HandleAllocatingInitMetatypeSelf` (`:1137`). `HandleSwiftError` sits alongside (`:1156`): `out IntPtr errorPtr` on the wrapper/thunk routes, `ref SwiftError` on the direct route (`:1177`). Marshalled-type spellings: `src/Swift.Bindings/src/Marshaler/MarshalledType.cs:107,145,210`.

**Library path and calling convention are decided independently.** `PInvokeEmitter.NeedsWrapperLib` (`:1282`) points async / opaque-return / `UsesWrapperLibrary` members at the `SwiftBindings` wrapper dylib, while `GetCallingConvention` separately keeps them on `CallConvSwift` if no `@_cdecl` was emitted. That yields a third shape: **a direct `CallConvSwift` call into the wrapper library.** The corpus holds 10 such `SBSW_` (`@_silgen_name`) entry points, 2 carrying an untyped `SwiftSelf` (`SBSW_MCB_94415C77_0_run`, `SBSW_MCB_502B33E8_0_runWithFilter`, both in `BindingTests/output/SwiftBindingsTestLib.Types.GenericProcessor.cs`). Moving a member into the wrapper *library* is therefore not the same as rerouting it — the *calling convention* is what has to change.

### 2.4 Wrapper Swift source, symbol naming, and the build

| Concern | Where |
|---|---|
| Swift wrapper text emitted | `MethodWrapperEmitter.EmitSwiftMethodWrapper` (`StringEmitter/MethodWrapperEmitter.cs:250`) and the ctor / property / subscript equivalents, into a shared `SwiftWriter` |
| On-disk file (one per module) | `{namespace}.Wrapper.swift` in the generator output dir — `StringEmitter/ModuleEmitter.cs:202`; cleanup at `src/Swift.Bindings/src/Program.cs:1558` |
| Method symbol | `SBW_{module}_{type}_{method}_{hash8}` (`MethodWrapperEmitter.cs:233`) |
| Constructor / subscript symbols | `SBW_{module}_{type}_init_{hash8}` (`ConstructorWrapperEmitter.cs:322`); `SBW_{Get\|Set}_{module}_{type}_{hash8}` (`SubscriptWrapperEmitter.cs:154`) |
| Property symbol | `SBW_{Get\|Set}_{module}_{type}_{property}` — **no hash** (`PropertyWrapperEmitter.cs:186`) |
| Hash | `DeterministicHash8` — FNV-1a 32-bit over the *original mangled* name, `X8` (`StringEmitter/HashUtility.cs:15`) |
| Cross-emitter structural dedup | `ModuleEmissionContext.TryClaimWrapperSymbol` (`StringEmitter/ModuleEmissionContext.cs:2096`) |
| Symbol registry — **first registration wins; the loser is silently rejected and must skip** | `ModuleEmissionContext.AddWrapperSymbolInternal` (`:1515`) |
| In-band contract check (throws → member rolled back to a skip) | `WrapperSymbolContractGate` + `PInvokeEmitHelper.cs:246` |
| Post-emission fail-closed reconciliation (`SWIFTBIND108`) | `StringEmitter/WrapperSymbolIntegrityGate.cs:66` |
| Compilation into `SwiftBindings.xcframework` | `src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs`, driven from `Program.cs:2125,2149` |
| Two-phase MSBuild | `_GenerateSwiftBindings` with `--skip-wrapper-compilation` (`src/Swift.Bindings.Sdk/Sdk/Sdk.targets:1811`), then `_CompileSwiftWrapper` with `--compile-wrapper-only` once project references resolve (`:2999`); source discovery at `:1122` |

**The property-symbol scheme is unhashed**, and 94 of the 149 exposed members are accessors. Adding that many property wrappers raises the collision surface on a naming scheme with no hash to disambiguate with, against a registry that resolves collisions by silently dropping the loser. Check that explicitly rather than trusting `SWIFTBIND108` to catch it afterwards.

---

## 3. The signature class to reroute

> **Every member for which `MarshallingHelpers.MethodRequiresSwiftSelf(env)` holds and `PInvokeEmitter.HandleSwiftSelf` would emit `MarshalledType.SwiftSelfUntyped`** — i.e. every instance member (method, property getter/setter, subscript getter/setter, initializer) on a class, a non-frozen struct, an enum, or a frozen struct in the *setter* position, that currently falls through to the direct `CallConvSwift` route. The 2 `SBSW_` shims carrying an untyped `SwiftSelf` belong to the class too, even though they already live in the wrapper library.

This is not a new predicate at the P/Invoke site — it is the set of members whose eligibility evaluators reject today. The work is to make each rejection bucket wrappable.

**What deliberately stays direct:** static methods, free functions and module-level members (no `SwiftSelf`, so nothing lands in `x20`); typed `SwiftSelf<T>` frozen-struct non-setter accessors (self travels in ordinary argument registers, observed outside the defect); metadata accessors (`…Ma`), witness-table lookups and other `SwiftSelf`-free `CallConvSwift` calls.

### Subtleties to settle in the session

- **Shapes the wrapper route deliberately refuses today** — not merely unimplemented; each has a recorded reason the wrapper would be unsafe or unbuildable, and each must be re-decided rather than force-wrapped:
  - **Failable initializers on non-frozen structs** are pinned to the direct route because `Optional<T>.initialize(to:)` interacts badly with the VWT-based `TryCreate` path (`ConstructorWrapperEmitter.cs:52`, cross-referenced from `MemberValidationPipeline.cs:1152`).
  - **Throwing property getters** decline the wrapper and use direct `CallConvSwift` with `ref SwiftError`, which the surrounding comments describe as ABI-correct on its own terms (`PropertyWrapperEmitter.cs`, guard 4b); one appears in the emission report as `SWIFTBIND107`.
  - **Module-internal parents.** A public member on a `@usableFromInline internal` parent cannot get a wrapper at all — the `@_cdecl` body would have to name the internal type from a separate compilation unit (`WrapperValidation.cs:188`). Sync members drop back to `CallConvSwift`; async/closure/operator shapes with no safe fallback are **dropped entirely** (`SkipReason.ParentModuleInternalNoFallback`). This bucket (22 + 1 rejections) is the expected unreroutable residue; closing it properly would mean emitting a wrapper compilation unit with same-module visibility.
  - **Inherited generic context** — `extension Outer.Inner: Protocol {}` is not expressible (`WrapperValidation.cs:309`).
- **Class allocating initializers** are `@convention(method)` over `@thick Self.Type`: the metatype must arrive in the `swiftself` register, which is an `x20` write exactly like a self pointer. Non-generic classes already handle it; generic classes on the direct lane still pass the metatype in a normal register (a known latent recorded in `src/docs/not-planned.md`). The wrapper lane sidesteps both, since the `@_cdecl` shim takes the metatype explicitly — so rerouting retires that latent too.
- **Protocol witnesses / dispatch thunks.** 15 exposed members target a `…Tj` dispatch thunk; an `@_cdecl` wrapper calling the requirement through the conforming type re-acquires dynamic dispatch on the Swift side, so no managed thunk is needed. Expected to be routine, but least-exercised.
- **Existentials** are handled per-parameter rather than per-member: `Optional<existential>` is blocked (it needs a proxy) while opaque `some Protocol` returns are allowed via `IndirectResult` boxing (`MethodWrapperEmitter.cs:180-227`). Confirm none of the exposed members trips the blocked arm.
- **Async members.** The shared guard rejects async from the ordinary gate (`WrapperValidation.cs:249`); promotion runs through the separate `IsAsyncCdeclEligible` (`:816`), consumed at `MethodHandler.cs:1512`. Members that fail it land on the hybrid shape of §2.3. None currently carry an untyped `SwiftSelf`, but that is a property of the current corpus rather than a structural fact — re-check.
- **Throwing members / the `x21` arm.** 4 exposed members carry both an untyped `SwiftSelf` and a `ref SwiftError`; rerouting removes both registers at once. The `x21` arm *on its own* (13 `SwiftError`-carrying members with no `SwiftSelf`) is **unverified** — Mono writes `x21` on every such call, but no sampled wrapper parked its cookie there. Those 13 are out of scope; note them.
- **Hand-written runtime P/Invokes.** `Swift.Runtime` declares 9 untyped-`SwiftSelf` `CallConvSwift` P/Invokes by hand — `src/Swift.Runtime/src/Swift/SwiftArray.cs:742,750,754,758,762` and `SwiftSet.cs:915,923,931,949`. Outside the generated corpus, identical exposure; `Swift/Runtime/SwiftCollectionCdeclWrappers.cs` already exists as the place to move them. Include them or record why not.

---

## 4. Exposure inventory

### 4.1 Method

Parsed from a frozen copy of the generated `.cs` under `BindingTests/output/` taken at git HEAD `e986332c3`: every `[UnmanagedCallConv(… CallConvSwift …)]` attribute with its following `LibraryImport` and extern declaration, classified on whether the parameter list contains `SwiftSelf <name>` (untyped), `SwiftSelf<…>` (typed) or `SwiftError`; deduplicated on `(declaring type, entry-point symbol, managed name)`. Declaring-type kinds resolved against `BindingTests/output/swift-types.json`; member kinds from the entry point's mangled suffix. The classification was run twice with independently written scanners; the exposed-population figures below agreed exactly, so re-deriving them is a matter of re-running a short scan rather than reconstructing the method.

`BindingTests/output-macos/` was **excluded** — its generated `.cs` is dated 2026-04-11 and is five months stale. `BindingTests/output-objc/` contains no Swift P/Invokes. Regeneration is platform-scoped, so the iOS tree is the only current corpus and every number below is iOS.

### 4.2 Counts

| Population | Count |
|---|---|
| Direct `CallConvSwift` P/Invoke declarations | ~1,600 (see note) |
| … carrying an **untyped `SwiftSelf`** — **the exposed population** | **149** |
| … carrying a typed `SwiftSelf<T>` (not exposed) | 9 |
| … carrying `SwiftError` in any form | 27 |
| &nbsp;&nbsp;`ref SwiftError` on a Swift member | 17 |
| &nbsp;&nbsp;`out SwiftError` on a closure-invocation `_XC` shim (takes `IntPtr __self`, no `SwiftSelf`) | 10 |
| … carrying **both** untyped `SwiftSelf` and `SwiftError` | 4 |

**Corrections to the figures previously recorded (1544 / 147 / 9 / 17 / 4).** The `SwiftError` figure of 17 counted only `ref SwiftError` member-route parameters; a further 10 `out SwiftError` parameters appear on closure-invocation shims (`PInvoke_*_XC`) which pass `IntPtr __self` rather than `SwiftSelf`. The exposed population is unaffected, but the total `SwiftError` count on the direct route is **27, not 17**. The exposed count moved with the corpus too (149 vs 147), the earlier figure having been taken two generator commits back — the same measurement of a moving artifact, neither wrong.

**On the direct-declaration total specifically:** two independently written scanners returned 1,593 and 1,600 for it, differing in how they attribute a declaration to a declaring type and therefore in what they treat as a duplicate. The figure is best read as **~1,600 ± 10**; it is used here only as a denominator for scale, and nothing in the plan depends on its exact value. **The exposed figures — 149 untyped, 9 typed, 27 `SwiftError` (17 `ref` + 10 `out`), 4 dual — reproduced exactly across both passes**, and the four dual members are unchanged: `DefaultedThrowingHasher.appendOrThrow`, `SimpleRowAdapter.layoutedAdapter`, `ThrowingByteCollector.acceptIfSmall`, `ThrowingGetterBox.checkedValue`. Re-measure at session start regardless.

### 4.3 The 149 by member kind and declaring type

| Kind (from the mangled suffix) | Count | | Declaring Swift type kind | Count |
|---|---|---|---|---|
| Property getter (`…vg`) | 68 | | struct (non-frozen, or frozen setter) | 101 |
| Method / dispatch thunk (incl. 15 `…Tj`) | 50 | | class | 37 |
| Property setter (`…vs`) | 15 | | enum | 1 |
| Subscript getter (`…cig`/`…cip`) | 6 | | nested type or cross-module extension (unresolved) | 10 |
| Subscript setter (`…cis`) | 5 | | | |
| Initializer (`…fC`) | 5 | | **86 distinct declaring types** | |
| **Total** | **149** | | | |

Accessors are 94 of 149 (63%) — predominantly a property-and-subscript problem, pointing the work at `PropertyWrapperEmitter` / `SubscriptWrapperEmitter` more than at `MethodWrapperEmitter`. Signature texture, as a difficulty proxy: 99 carry a type-metadata argument (generic context), 17 a protocol-witness-table argument, 15 a `SwiftIndirectResult`, 15 target a `…Tj` thunk.

### 4.4 Why each member is on the direct route — the work ledger

The generator publishes its own rejection histogram — `BindingTests/output/binding-emission-report.json`, `skipReasons`:

| Rejection bucket | Count |
|---|---|
| `method_level_generics` | 67 |
| `generic_parent_type` | 63 |
| `parent_module_internal` | 22 |
| `closure_params` | 17 |
| `unsupported_generic_container` | 10 |
| `async`, `variadic_parameter` | 9 each |
| `direct_closure_setter` | 8 |
| `nested_frozen_struct_parameter`, `generic_parent_unresolved_pwt_constraint`, `generic_parent_metadata_buffer_mode`, `nested_type_return`, `actor_isolated` | 6 each |
| `inherited_generic_context`, `nested_frozen_struct_index_param` | 4 each |
| `inout_abi_mismatch` | 2 |
| `module_internal`, `self_property`, `SWIFTBIND107` (throwing property getter) | 1 each |

This counts *rejections*, not members, and includes members that still land on a `NativeThunk` or `DirectCdecl` route, so it does not sum to 149. It is nonetheless the right work ledger: **the two generic buckets (130 rejections) dominate**, and they line up with the 99 exposed members carrying a metadata argument. Start by joining the histogram to the 149 per-member so each has a named blocking guard. For scale, `wrapperStrategyCounts` in the same report: `CdeclProperty` 1,761, `CdeclMethod` 1,586, `NativeThunk` 1,040, `CdeclConstructor` 749, `DirectCdecl` 105, `CdeclSubscript` 27, **`None` 183** — `None` being the direct-`CallConvSwift` fallback bucket the 149 live inside.

### 4.5 The 14 observed clobbering members and their route today

From the register-level corpus survey in `src/docs/Future/upstream-issue-03-mono-set-insert-done-blocking.md` (181 `SwiftSelf`-carrying wrappers disassembled out of one Mono full-AOT device binary: 14 clobbering, 157 safe, 5 with no transition, 5 unparsed; all 14 clobbers are `x20`). Emission sites are under `BindingTests/output/SwiftBindingsTestLib.Types.*.cs`.

| Member | Route today | Emitted at |
|---|---|---|
| `BufferModeDescribablePair.first` / `.second` (getters) | direct, untyped self | `…BufferModeDescribablePair.cs:955,959` |
| `BufferModeQuad.first` / `.second` / `.third` / `.fourth` (getters) | direct, untyped self | `…BufferModeQuad.cs:411,415,419,423` |
| `CarrierBox.relayThrough` | direct, untyped self | `…CarrierBox.cs:270` |
| `CompositionItemProcessor.describeBoth` | direct, untyped self | `…CompositionItemProcessor.cs:246` |
| `DefaultedHasher.append` | direct, untyped self | `…DefaultedHasher.cs:385` |
| `DefaultedHasherWithFile.append` | direct, untyped self | `…DefaultedHasherWithFile.cs:430` |
| `GenericMethodHost.describeWithTag` | direct, untyped self | `…GenericMethodHost.cs:363` |
| `SimpleRowAdapter.layoutedAdapter` | direct, untyped self + `ref SwiftError` | `…SimpleRowAdapter.cs:256` |
| `OwnershipStructConsumer.consumeDirectAndThrow` | **already on the wrapper route** (`SBW_…_3EA510C3`) | `…OwnershipStructConsumer.cs:385` |
| `OwnershipStructConsumer.consumeDirectWithLater` | **already on the wrapper route** (`SBW_…_EC4FDDBB`) | `…OwnershipStructConsumer.cs:416` |

**12 of the 14 are still on the direct route.** The two that moved did so as a side effect of the `inout`-String wrapper retarget, not as a Mono mitigation.

---

## 5. An unresolved contradiction to settle first

The 12 that remain on the direct route are not all failing today, and that needs explaining before anything is built.

- The standing `device_monoaot` floor is **3,833 pass / 0 fail / 36 skip** (`build/baselines/runtime-identity-baseline.json`, `build/baselines/validation-baseline.json`).
- Exactly one of the 12 appears in that lane's skip set: `MethodLevelGenericTests.TestGenericMethodHost_MixedParams` (the `describeWithTag` member) — `[Skip]` at `BindingTests/RuntimeTestsApp/Generics/MethodLevelGenericTests.cs:44`, whose text names this abort exactly.
- The other 11 are exercised by tests the baseline records as passing — `CarrierBox.RelayThrough` (`RuntimeTestsApp/Generics/CsmClassConformerReturnTests.cs:92`), `CompositionItemProcessor.DescribeBoth` (`Generics/CompositionMethodConstraintTests.cs:41,50`), `DefaultedHasher.Append` (`Generics/DefaultedTrimOverloadTests.cs:62,79`), the `BufferMode*` getters, `DefaultedHasherWithFile.Append`. `SimpleRowAdapter.layoutedAdapter` has no test at all.

Two readings, to be separated by measurement. **The likely one: the clobber set is per-binary, not per-member.** The classification came from one Mono full-AOT build whose fixtures differed (the `OwnershipStructConsumer` members had been forced back onto the direct route to reproduce the abort), and a different compilation reallocates registers across the whole binary. On that reading the 11 are green *in the baseline's binary and clobbering in the surveyed one* — the strongest argument for the blanket reroute, because it means there is no stable member set to fix. **The alternative: the model over-predicts** and the classifier's write-detection is too broad; that would not change the decision (the mechanism is real and `describeWithTag` still aborts) but would change how the red-first fixture has to be built.

The captured device console log for the surveyed binary aborts inside the `OwnershipStructConsumer` fixture and terminates there, so the other 11 predictions were never executed even on that binary.

**One data point exists now, and it favours the mechanism rather than over-prediction.** A probe was written against `SimpleRowAdapter.layoutedAdapter` — the one member of the 12 that has no test — and run on the device Mono full-AOT lane. It aborts, verbatim: `Cannot transition thread 0x3 from STARTING with DONE_BLOCKING`, raised inside that member's own P/Invoke stub. It was run twice, at two different generator states, and reproduced identically. That converts one predicted-clobbering member from a latent into a demonstrated failure, on a binary the classification did not survey, which is what makes it worth having. The probe is deliberately **not** in the tree: it is red by design and would red the lane for everyone.

What it does **not** settle: it says nothing about the other 11, and it does not choose between the two readings, since a member that clobbers in two binaries is consistent with both a per-member and a per-binary story. The next measurement — disassembling the *baseline's own* Mono full-AOT binary and re-running the cookie-register classification against it, rather than against the surveyed one — is what separates them, and it is still the session's first task.

---

## 6. Cost

**Wrapper dylib.** The wrapper currently carries 13,490 `@_cdecl` functions in 93,509 lines of generated Swift (`BindingTests/output/SwiftBindingsTestLib.Wrapper.swift`), compiling to a 9.77 MB simulator slice — roughly 720 bytes of binary per wrapper, with 7,531 distinct `SBW_` entry points against ~1,600 direct P/Invokes. Adding ~149 wrappers is about **+1.1% in count and on the order of +100 KB of native code**, plus ~1,000 lines of generated Swift. Some of the 149 need more than a trivial shim (generic contexts need explicit metadata parameters), so treat the per-wrapper average as a floor.

**Runtime.** One extra native hop per call: managed → `@_cdecl` shim → Swift member. Paid on **every** runtime, not just Mono — there is one generated assembly and no Mono-only emission mode. NativeAOT and CoreCLR, which implement the convention correctly, absorb the reroute for a defect they do not have. That is the accepted price of the decision.

**Public C# surface.** Expected unchanged: only the P/Invoke target, its parameter list and the managed shim that calls it change. This is verifiable rather than assumed — the API-manifest gate keys on the C# signature and records the native symbol separately, so a surface change would show up as a *removal*, not a retarget.

**A side benefit worth counting.** 36 of the 95 `SB0001` `[Obsolete]` markers in the generated corpus sit on members whose body calls one of these 149 direct P/Invokes. The marker's own text is *"No @_cdecl wrapper or native thunk available … P/Invoke calling convention may not match Swift ABI."* Rerouting makes that statement false and those markers should clear — a consumer-facing improvement independent of the Mono fix.

### Baselines that will move

| Baseline | Moves? | Handling |
|---|---|---|
| `build/baselines/api-manifest-baseline.json` | **Yes, large.** Entries are `{module, signature, symbol}` where `symbol` is the P/Invoke entry point (`src/Swift.Bindings/src/Emitter/ApiManifestEmitter.cs:35`, recorded via `ModuleEmissionContext.RecordApiManifestEntry` `:1768` from `methodEnv.EmissionSymbol`). The reroute flips `symbol` from `$s…` to `SBW_…` on an unchanged `signature` — exactly the gate's *"N symbol retarget(s) on a stable C# signature"* failure (`build/Build.ApiManifestGate.cs:106,120`). Expect ~149. | Reseed with `nuke seed-api-manifest-baseline` **in the same commit**, with a written adjudication naming the retargets as intended. This gate throws before the skip-surface gate, so reseed it first. |
| `build/baselines/skip-surface-baseline.json` | **Yes, downward** — up to 36 of the 95 `ObsoleteSB0001` markers should clear; the ratchet welcomes a decrease but still diffs. | Reseed after the manifest baseline. |
| `build/baselines/runtime-identity-baseline.json` | **Yes**, per lane (`simulator`, `device`, `device_monoaot`, `macos`, `maccatalyst`, `tvos_simulator`) — skips attributable to this defect disappear, pass counts rise. | `--seed-runtime-identity-baseline` per lane, with a written adjudication of every changed identity. |
| `build/baselines/validation-baseline.json` (`runtime_tests.*`) | **Yes** — pass counts rise on lanes that were skipping. | Auto-updated on a green improvement. |
| `build/baselines/parity-baseline.json` | Possibly — artifact/wrapper parity counts shift with ~149 new wrappers. | Check; reseed only if the gate reports it. |
| `build/baselines/partial-success-kitchen-baseline.json` | Only if the fixture's own emission changes; its compare is exact. | Read the drift lines before reseeding. |

---

## 7. Verification plan

### 7.1 Red first

**Requirement:** a BindingTests fixture that reproduces the abort on Mono full-AOT device (`nuke binding-tests --device --mono-aot --device-udid <UDID>`) *before* the reroute lands, and goes green after. §5 is why this is the hard part — register allocation is chosen per binary, so a fixture cannot be made red by choosing a member. Two candidates, in order:

1. **`SimpleRowAdapter.layoutedAdapter`** — classified as clobbering, still on the direct route, and has **no test at all**. A throwaway probe against it has already been run under `--mono-aot` and **aborts** (§5), so the red half of the requirement is known to be reachable on this member. What is still owed is a *committable* fixture: the probe was written to be discarded, and a fixture that stays in the tree has to be one the reroute turns green rather than one that reds the lane until then. Land it in the same change as the reroute, not before it.
2. **`OwnershipStructConsumer.consumeDirectAndThrow`** is the *observed* abort but is now on the wrapper route twice over, so using it would mean manufacturing a configuration the generator does not produce. Do not hand-edit generated output to force it.

If neither yields a deterministic red, say so plainly and record the reroute as a *mechanism-motivated* change verified by the absence of regressions plus the retirement of the existing `[Skip]` — not as a fix with a red-to-green witness. Do not manufacture a witness.

### 7.2 Lanes

| Gate | Why |
|---|---|
| `nuke test` | Emitter/eligibility unit coverage; new or relaxed guards need new tests. |
| `nuke binding-tests --compile-only --strict` | Regen + compile + the unflagged gates (parity, API manifest, resilience kitchen, ingestion kitchen, overload names) and `SWIFTBIND108`. Expect the API-manifest red until reseeded. |
| `nuke binding-tests --compile-only --skip-surface` | The `SB0001` ratchet — should move down. |
| `nuke binding-tests --sim` | Mono JIT. |
| `nuke binding-tests --device --device-udid <UDID>` | NativeAOT control: it never had the defect, so it must not regress from the extra hop. |
| `nuke binding-tests --device --mono-aot --device-udid <UDID>` | **The lane that exhibits the defect** — the primary signal. |
| `nuke binding-tests --macos` / `--catalyst` / `--tvos` | CoreCLR; Mono JIT second flavour; fourth platform. |
| `nuke binding-tests --partial-success-kitchen` | Cheap (~30s) and directly relevant: it asserts the *unsupported* surface fails honestly, and this change moves what counts as unsupported. |
| `nuke validate` | A cross-cutting emitter change over the real-world corpus — exactly the case the opt-in policy names. |

Device runs need the explicit `--device-udid`.

### 7.3 Acceptance

1. Every member that carried an untyped `SwiftSelf` on the direct route is on the `@_cdecl` wrapper route, **or** carries a written per-bucket explanation of why it cannot be — the `parent_module_internal` residue, non-frozen-struct failable inits and throwing property getters being the expected candidates.
2. `binding-emission-report.json` `skipReasons` shows zero for every bucket the session claimed to close, and `wrapperStrategyCounts.None` drops by the corresponding amount.
3. Zero `[Skip]` / `[SkipOnMonoJit]` attributable to this defect remains — in particular `BindingTests/RuntimeTestsApp/Generics/MethodLevelGenericTests.cs:44` is deleted, not reworded.
4. No `SWIFTBIND108`, and no silently-dropped wrapper from a symbol collision — check the emitted wrapper count against the expected member count rather than trusting the gate alone, given the unhashed property-symbol scheme and the first-registration-wins registry.
5. Every lane's pass count is ≥ its baseline; runtime-identity baselines reseeded per lane with a written adjudication of each changed identity.
6. The API-manifest baseline is reseeded in the same commit as the generator change, so its `git_sha` stays meaningful.
7. No new hand-coded prediction gate was added. If the implementation reaches for one, stop — the freeze policy applies and this change is specifically *not* a shape gate.

---

## 8. Reopen / narrowing trigger

This reroute is a defensive widening against a defect we do not control. Narrow it back if either fires:

- **Mono excludes `x20`/`x21` from the GC-safe-region cookie's candidate set** (the suggested upstream fix), or spills the cookie to the frame. Then re-check which of the ~149 reroutes still earn their keep — several will, for reasons unrelated to Mono (the `SB0001` markers they cleared, the generic-context wrapper work that had to be built anyway).
- **CoreCLR-on-iOS previews land in .NET 11.** The interpreter path is entirely unmeasured, and a CoreCLR iOS lane in `nuke binding-tests` is the measurement instrument — without it there is no way to say whether the third runtime shares Mono's allocation behaviour. Adding that lane is a prerequisite for any narrowing argument resting on "the affected runtime is going away."

Either way the narrowing is a separate decision with its own evidence, not an automatic revert.

---

## 9. Out of scope

- **Typed `SwiftSelf<T>`.** Not implicated; leave the 9 members alone.
- **The `x21` / `SwiftError`-only arm.** 13 members carry `SwiftError` without an untyped `SwiftSelf`. Mechanically destroyable if Mono ever parks a cookie in `x21`, but not observed. Record; do not reroute on this pass.
- **Issue 1** (`!ji->async`) and **Issue 2** (non-blittable rejection) — separate defects with separate registry entries and separate remedies.
- **`[SuppressGCTransition]`.** Rejected: it removes the transition bracket entirely, which is only legal for calls that neither block nor re-enter managed code, and the affected surface includes members that do both.
- **Any shape gate.** There is no shape. A `SkipReason` / `MemberValidationPipeline` / `WrapperValidation` predicate keyed on the Swift signature would be an unsound guess and violates the prediction-gate freeze policy.
- **Filing or chasing the upstream `dotnet/runtime` issue.** Owner-owned.

---

## 10. Flagged as unverified

- The §5 contradiction — 11 of 12 predicted-clobbering members sit under tests the `device_monoaot` baseline records as green. The leading explanation (per-binary register allocation) is plausible but not measured.
- The `x21` arm is *not excluded and destroyable if chosen*, not reproduced.
- Whether the wrapper route can express **every** shape among the 149 — in particular the 15 `…Tj` dispatch thunks, the 22 `parent_module_internal` members, and the shapes §3 lists as deliberately refused. The two generic buckets (130 rejections) are the bulk of the work and their difficulty is unmeasured here.
- The collision risk from ~94 additional unhashed property wrapper symbols is read off the naming and registry code, not measured.
- The ~100 KB / +1.1% dylib estimate is a linear extrapolation from the current average wrapper size, not a measurement of these particular wrappers.
- Counts are a snapshot of a working artifact (`BindingTests/output/`) at git HEAD `e986332c3`, which any regeneration moves; `BindingTests/output-macos/` is stale (2026-04-11) and was excluded, so whether the macOS lane's current output would add exposed members is unmeasured. The direct-declaration denominator is scanner-sensitive (~1,600 ± 10); the exposed figures are not.
- Line citations were read against a working tree with uncommitted edits under `src/Swift.Bindings/src/Emitter/StringEmitter/`.

---

## 11. Ledger changes to make when this work starts

Not to be applied now — recorded so the session does not have to rediscover them.

1. **`src/docs/not-planned.md` → *Pending owner decisions*:** the row *"Do we mitigate Mono's `x20` cookie clobber, and how — or wait for upstream?"* **converts.** The decision is taken; remove the row from Pending owner decisions and record the outcome (reroute; not `[SuppressGCTransition]`; not a shape gate) in the roadmap entry below.
2. **`src/docs/not-planned.md` → *Marshalling, runtime seams & ABI*:** the row *"Mono clobbers the GC-safe-region cookie in `x20` …"* **stays** — it is the upstream attribution and remains true. Amend its *"Why nothing is done in the generator"* and *"Live residual risk"* paragraphs to point at this document and at the reroute as the disposition, and correct the `SwiftError` count to 27 (17 `ref` + 10 closure-shim `out`).
3. **`src/docs/not-planned.md` → *Marshalling, runtime seams & ABI*:** the row *"Generic direct-lane class allocating initializers pass the metatype in a normal register"* — its hazard is retired by the reroute, since the wrapper takes the metatype explicitly. Close it when the reroute lands, citing the reroute rather than a separate fix.
4. **`src/docs/roadmap.md` → *Pending agreed work*:** add one row — *"**Reroute untyped-`SwiftSelf` members onto `@_cdecl` wrappers.** Mono's managed-to-native wrapper can park its GC-safe-region cookie in `x20`, which `CallConvSwift` reserves for `SwiftSelf`; the reroute removes the register collision by removing the reserved register from the signature. Design brief: `src/docs/mono-x20-cdecl-reroute.md`."* Not under *Blocked (Confirmed Upstream Only)* — the upstream defect is confirmed, but the work is ours and is not blocked on Mono.
5. **When the work lands**, delete the `[Skip]` at `BindingTests/RuntimeTestsApp/Generics/MethodLevelGenericTests.cs:44` together with its "Long-term fix: generator needs to emit @_cdecl wrappers for class-instance method-level generics" note — that long-term fix will have shipped.
