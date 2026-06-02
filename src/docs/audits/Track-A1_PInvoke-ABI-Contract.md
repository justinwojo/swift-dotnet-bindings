# Track A1 — P/Invoke ABI Contract + x64/ARM64 Thunks (consolidated)

**Scope:** the cdecl ↔ swiftcc bridge contract — `CdeclSignatureContract`, the C# P/Invoke signature builder, the per-arch thunk emitters (`Arm64ThunkTarget`, `SysVThunkTarget`, `ThunkAssemblyEmitter`, `TypeLowering`), `NativeThunkEmitter` decision gates, `CdeclParamMapper`, the `@_cdecl` wrapper marshalling/return path, parameter-ownership modeling, and generic protocol-witness-table (PWT) ordering — verified against generated `BindingTests/output/*.cs`/`*.swift` and compile/disassembly probes.

> **This is a consolidated report.** Track A1 was (accidentally) audited by **three independent heavy runs**. They found **largely non-overlapping** defects, so this file is the deduped **union** of all three. Each finding carries a **Found-by** tag (**A** / **B** / **C**) and, where runs disagreed, both severities. The three raw per-run reports are preserved verbatim under [`Track-A1_run-reports/`](./Track-A1_run-reports/) for provenance.
>
> - **A** = run `wf_7b85adb7` — thunk register/ordering + PWT + Foundation.Data (10 confirmed, self-rated risk 5/5)
> - **B** = run `wf_705dc776` — ownership/`consuming` + `@convention(c)` closures + SIMD return (7 confirmed, risk 5/5)
> - **C** = run `wf_94614753` — struct-layout / register-packing / alignment (5–6 confirmed, risk 4/5)

**Overall risk: 5 / 5 (critical).** Across the three runs, **two distinct P0 memory-corruption defects** were each probe-demonstrated (throwing-class-ctor error-register swap; `consuming` non-copyable double-destroy), plus a probe-demonstrated crash on the ARM64 static-metatype sret path (rated P0 by A, P2 by C). Every confirmed defect is *silent* (no compile-time signal) except the `@convention(c)` closure case (hard `CS1503`). All are gated behind specific-but-reachable type shapes, so the *common* binding corpus is unaffected — A's and C's 30+-wrapper mechanical sweeps both found the ordinary path clean.

> **Independent code-level verification — 2026-06-01 (Claude).** All **12 confirmed findings (§1)** were re-verified by reading the exact cited code (no live harness run). **Result: zero false positives; every line citation precise; every claimed mechanism matches the code.** Spot-confirmed facts:
> - **#1** — `CdeclSignatureContract.cs:105` sets `needsResultPtr = !isClass` → false for a class ctor, so the phase order really is `[ErrorOut][Arguments][Metadata]` (error in the *first* register); `Arm64ThunkTarget.cs:62-67` computes `errorOutRegIndex = baseIndex + ParameterCount` (trailing), and the comment at `:106` explicitly assumes the *regular-method* order. Genuine mismatch.
> - **#2** — `NeedsReturnBridge` returns false for `IsIndirect` (`ThunkAssemblyEmitter.cs:164`), so `Arm64ThunkTarget.cs:88` skips `mov x19,x8`; `EmitMetatypeSetup` saves only x0-x7/d0-d7, never x8.
> - **#3** — `LowerStruct` returns a 0-slot `IsIndirect` result for a non-frozen `~Copyable` struct (`TypeLowering.cs:174-178`); `AreAllParametersLowerable` admits it via `Slots.Count<=1` (`NativeThunkEmitter.cs:614`) under a comment mislabeling it "single-slot param."
> - **#4/#5** — `NeedsReturnBridging` (`NativeThunkEmitter.cs:700-702`) returns false for *any* frozen ≤8B struct regardless of int/float mix.
> - **#6/#7** — `ReturnBufferSlots` (`ThunkAssemblyEmitter.cs:188`) aligns each field to its own size and packs sequentially (loses a nested struct's alignment boundary); `LowerStruct` (`TypeLowering.cs:215`) adds one slot per field positionally with no eightbyte coalescing.
> - **#8** — `CdeclParamMapper.cs:286-288` emits the Foundation.Data `@_cdecl` side as two `Int` words; the comment at `:281` confirms C# passes a single 16-byte struct.
> - **#9** — `PInvokeEmitter.cs:839` + `MethodMarshalPlanBuilder.cs:162` use culture-sensitive `OrderBy`; sibling `PInvokeHelperEmitter.cs:235` uses `StringComparer.Ordinal`.
> - **#10** — `WrapperEmitter.Marshalling.cs:985` forwards the param raw into a bool/enum-typed delegate while the return side IS bridged (`:1013`) → hard `CS1503`.
> - **#11** — `NativeThunkEmitter.cs:525` is `record.InlineSize switch { 2 => 2, null or 0 or 1 => 1, _ => 0 }`; null defaults to a 1-byte extension.
> - **#12** — `ArgumentDecl.cs:25` carries only `IsInOut`; `SwiftABIParser.cs:2167` reads `paramValueOwnership` solely as `== "InOut"`, collapsing `Owned`/`Shared` to false.
>
> **Only soft spots:** the two *severity* disputes (#2 P0-vs-P2; and the A3 opaque-proxy crash tier) — both flagged honestly by the audit. **Not verified:** §2 inconclusive / §3 deferred candidates (unconfirmed in the audit too), #13's coverage-gap grep, and — the audit's own §5 gap — **no live `nuke binding-tests` reproduction was run for any finding.**

---

## 0. Cross-run reliability summary (a method finding, not a code finding)

Three independent heavy runs of the *same* track produced strikingly different output. This is itself a result about how much to trust a single audit run.

| | Run A (`wf_7b85adb7`) | Run B (`wf_705dc776`) | Run C (`wf_94614753`) |
|---|---|---|---|
| Self-rated risk | 5/5 critical | 5/5 high | 4/5 |
| Confirmed | 10 | 7 | 5–6 |
| Headline P0 | throwing-class-ctor swifterror register swap | `consuming` non-copyable double-destroy | *(none — top was P1)* |
| Flavor | thunk register math, PWT ordering, Data decomposition | ownership/`consuming`, `@convention(c)` closures, SIMD return | struct layout, register-file packing, alignment |

**Overlap:** of ~15 distinct confirmed defects, only these appeared in more than one run —

| Defect | A | B | C | Note |
|---|---|---|---|---|
| Contradictory ARM64 self/error comment (`Arm64ThunkTarget.cs:59`) | P2 | P2 | P2 | **only finding in all 3** |
| ARM64 x8 sret lost across metadata accessor | **P0** | — | **P2** | **severity disagreement** |
| `SmallStructReturnDivergesFromCAbi` false on null lowering | — | P1 | P1 | agree |
| `simd_float2`→`Vector2` x86_64 return corruption | — | P2 | (folded into above) | agree |
| `ComputeReturnZeroExtension` null-InlineSize tag truncation | P2 | (deferred) | P2 | agree |

**Everything else — including each run's headline P0 — was found by exactly one run.** No run's findings were a superset of another's; per-run recall looks like ~40–60% of the discoverable set.

**Implications:**
1. **A single heavy run is a *sample*, not a census.** For completeness on a complex track, run N times and union/dedup (as done here), or raise finders/rounds.
2. **Severity is unstable** even among probe-confirmed findings (P0 vs P2 on the x8 bug) — severity needs cross-run or human adjudication, not blind trust in one label.
3. **Precision held up:** every run produced real, probe-backed defects, and the verify stage *did* refute false alarms (see §4). But "confirmed" ≠ certain — a couple of one-run findings are flagged below as wanting an independent re-probe.

---

## 1. Confirmed findings (union, deduped)

| # | file:line | severity | found-by | claim | what the probe showed |
|---|---|---|---|---|---|
| 1 | `Arm64ThunkTarget.cs:62-67,95,134` + `SysVThunkTarget.cs:163-165,277-280` | **P0** | A | Throwing **class** constructor: errorOut placed FIRST by `CdeclSignatureContract` (`[ErrorOut][Arguments][Metadata]`) but both thunk targets read it as **trailing** (`errorOutRegIndex = needsSelf + ParameterCount`) → full register swap | ARM64: `mov x19,x1`/`str x21,[x19]` captures *value* as error ptr; x86_64: `movq %rsi,-32(%rbp)`+`movq %rdi,(%rsp)`. Throw path = wild store / SIGSEGV; success path clears the wrong slot. A plain `throws` value-arg class ctor passes every `ShouldEmitThunk` gate. |
| 2 | `Arm64ThunkTarget.cs:88-89,164` + `ThunkAssemblyEmitter.cs:163-165` | **P0 (A) / P2 (C)** — *disputed* | A, C | ARM64 thunk does not preserve **x8** (cdecl sret) across the metadata-accessor `bl` for a static method returning a >32B `IsIndirect` frozen struct; `x8→x19` save is gated on `needsReturnBridge`, false for `IsIndirect` | Link generated `thunk.arm64.s` against real dylib → **SIGBUS/SIGSEGV (exit 138)**; the `*Ma` accessor calls `objc_opt_self` (`ldr x8,[x0]`) clobbering x8; adding `mov x19,x8`/`mov x8,x19` fixes it. SysV stashes `%rdi→%rbx` and works. **A rated P0; C rated P2** (gated behind static + >32B + ObjC-accessor). |
| 3 | `NativeThunkEmitter.cs:614` (gate) + `TypeLowering.cs:171-178` | **P0** | B | `consuming` (`__owned`, +1) non-copyable struct param forwarded at **+0** through a bare tail-call thunk → Swift consumes the buffer, then C# `SafeHandle.ReleaseHandle` destroys it again | `~Copyable` struct with observable `deinit` counted **2** destroys (consume + VWT destroy); heap-owning variant **SIGABRT (134)** = real double-free. `AreAllParametersLowerable` keys on `Slots.Count<=1` and admits the 0-slot indirect result; the only non-copyable guard checks *self*, not *params*. |
| 4 | `NativeThunkEmitter.cs:321-323,700-702` | **P1** | B, C | `SmallStructReturnDivergesFromCAbi` early-returns `false` when `LowerReturnType==null`; a frozen ≤8B struct with absent/unparseable `AbiFieldLayout` then slips the divergence gate and is tail-call-thunked by value | `@frozen {Int32,Float}` / `{Int16,Float}` (null layout) e2e: float field read back **0** on both arm64 and x86_64. swiftcc returns field-wise across two register files; C-ABI/CLR reads from one register. Sibling *with* `abiLayout="i4,f4"` correctly routes to `@_cdecl`. |
| 5 | `NativeThunkEmitter.cs:700` (`NeedsReturnBridging`) + `CdeclParamMapper.IsSimdVectorType` | **P1 (C) / P2 (B)** | B, C | `simd_float2`→`Vector2` returns escape both the SIMD-param indirection and the `>8B` SIMD-return `@_cdecl` path (admitted by the generic frozen `InlineSize<=8` gate) → corrupted on x86_64 | On CoreCLR x86_64 (Rosetta) the 2nd float comes back **0** (`doubleFloat2(3,7)→Y=7` un-doubled; `MakeFloat2Scaled→(10,1)` vs `(10,22)`); arm64 happens to survive only for trivial bodies. **Refutes** the long-standing "SIMD2 packed in one xmm" comment. Same null-lowering root as #4. |
| 6 | `TypeLowering.cs:205` (`LowerStruct`/`ReturnBufferSlots`) + `ThunkAssemblyEmitter.cs:188` | **P1** | C | Nested frozen struct flattened into a flat `AbiFieldLayout` string loses the inner struct's **alignment** → `ReturnBufferSlots` `AlignUp` reconstructs wrong byte offsets across the nested boundary | Real Swift offsets for `Outer{p:Int8; x:Inner{a:Int8,b:Int64}; q:Int8}` = 0/8/16/24; thunk stores at 0/1/8/16 → swapped/garbage. Runtime-confirmed in `NestLib.arm64.s` on the macOS-arm64 slice. |
| 7 | `TypeLowering.cs:205,215-218` (register slots) | **P1** | C | `LowerStruct` assigns one register per field **positionally**; swiftcc **coalesces** multiple same-register-file sub-eightbyte fields into one register | `{Int8,Int8,Int64,Int64}` → swiftcc x0=(a\|b packed),x1=c,x2=d (3 regs); generator emits a 4-slot store → b/c/d read wrong, last reads stack garbage. Affects SysV x86_64 identically. |
| 8 | `CdeclParamMapper.cs:284-289` + `MarshallingHelpers.cs:64-66` | **P1** | A | `Foundation.Data` `@_cdecl` param decomposes into **two `Int` words** on the Swift side but the C# P/Invoke passes a single **16-byte struct by value** (`ShouldDecomposeStringForCdecl` gates two-word emission on `IsSwiftString` only) | AAPCS64 probe: with 7 leading int args, a 16-byte composite goes **wholly to the stack** while two `Int` args fill the last reg + spill → callees read different bytes. e2e: `Data{0x1111…,0x2222…}` → callee read `{0,0x1111…}` (2nd word lost). ARM64-only as tested. |
| 9 | `PInvokeEmitter.cs:839` + `MethodMarshalPlanBuilder.cs:162` | **P1** | A | Generic PWT params ordered by **culture-sensitive** comparer; Swift's canonical witness-table ABI order is **Ordinal** (ASCII byte). Sibling `PInvokeHelperEmitter.cs:235` already uses `StringComparer.Ordinal` — these two are the outliers | SIL/IR: `combineWitness<T:_Internal & View2>` → swiftcc `(…, %T.View2, %T._Internal)`; generator emits `[_Internal, View2]`. Underscore-prefixed pairs diverge in **every** culture. Direct `CallConvSwift` symbol bind → witness tables swapped → wrong witness dispatched. |
| 10 | `WrapperEmitter.Marshalling.cs:985` (`EmitConventionCCallback`) | **P1** | B | Non-optional `@convention(c)` closure with a **Bool / simple-enum parameter** declares the `[UnmanagedCallersOnly]` param at P/Invoke type (`byte`/underlying int) and forwards it **raw** into a delegate typed at the idiomatic C# type | Generated `_impl(byte arg0){ _del!(arg0); }` into `Action<bool>` → **`CS1503` byte→bool**; enum → `CS1503 long→EnumType`. Return side *is* bridged; optional-param path *is* bridged — only the non-optional param direction breaks. (B surfaced this as two overlapping findings #4/#5; one root, one fix.) |
| 11 | `NativeThunkEmitter.cs:525` (+ `SysVThunkTarget.cs:344-355`) | **P2** | A, C | `ComputeReturnZeroExtension` defaults the `InlineSize==null` domain (every XML/cross-module enum record) to a **1-byte `movzbl`**, truncating the 2-byte tag of a >256-case frozen no-payload enum (x86_64) | Buggy thunk vs real symbols: case **299→43**, **256→0**; correct `movzwl` returns 299/256. `NeedsReturnBridging` admits a frozen simple enum unconditionally so it's never diverted to `@_cdecl`. x86_64-only (AArch64 callee widens). |
| 12 | `ArgumentDecl.cs:25` + `SwiftABIParser.cs:61,2167` | **P1** | B | Parser never models `consuming`/`borrowing` ownership — `paramValueOwnership` is read only to compute `IsInOut`, so `Owned`/`Shared` collapse to `false`. **Upstream enabler of #3** (no code path can branch +1-vs-+0) | digester emits `paramValueOwnership="Owned"/"Shared"`; SIL `(@owned R)` vs `(@guaranteed R)`; generated C# byte-identical apart from the entry-point hash. |
| 13 | `BindingTests/Sources/SwiftBindingsTestLib/Initializers/Throwing.swift:22` | **P1** (coverage) | A | Throwing/erroring **constructor** + static-throwing **thunk** paths have **zero** e2e coverage — the structural reason #1 ships silently | `grep -c KcfC` on both generated `.s` = 0 throwing-ctor thunks; the only error-bridge+metatype thunk is a throwing *static* method (`safeDivide`), a different register layout. The two register models never intersect in any fixture. |

### Headline detail (the three crash-class defects)

**#1 — Throwing class-ctor error-register swap (A, P0, both arches).** `CdeclSignatureContract.DetermineParameterOrder` puts a class ctor's phases as `[ErrorOut][Arguments][Metadata]` (`:100-114`; `needsResultPtr=!isClass`), so errorOut is in the **first** integer register (x0/%rdi). Both thunk targets compute the error register as **trailing** (`errorOutRegIndex = (needsSelf?1:0)+ParameterCount`). For a 1-arg ctor that's x1/%rsi — the value register. The thunk then feeds the error pointer in as the integer arg and writes swifterror through the value-as-address. `ShouldEmitThunk` only gates `HasTypedThrows`, so a plain `throws` non-failable value-arg class ctor IS thunked. Fix must drive the thunk register math from the contract's phase order, not the trailing-error formula.

**#2 — ARM64 x8 sret loss (A:P0 / C:P2, disputed).** For a `static func` returning an `IsIndirect` (>32B) frozen struct, the sret buffer arrives in x8. `Arm64ThunkTarget.cs:88-89` saves x8→x19 only when `needsReturnBridge`, which is false for `IsIndirect`. `EmitMetatypeSetup` spills only x0..xN/d-regs around the accessor `bl`, never x8. The `*Ma` accessor's `objc_opt_self` clobbers x8 → the subsequent call writes the return struct through a corrupt pointer (**SIGBUS**). SysV handles it via the independent `cdeclUsesSret`/`swiftIndirect` pair (`%rdi→%rbx`). **Severity dispute:** A called it P0 (guaranteed crash, demonstrated); C called it P2 (gated behind static + >32B + ObjC-accessor, hence latent for trivial accessors). *Reconciled view: a guaranteed crash on a reachable shape — treat as P0/P1, not P2.*

**#3 — `consuming` non-copyable double-destroy (B, P0).** `UniqueResource` (`~Copyable`, non-frozen) lowers to `Slots=[]`, `IsIndirect=true`. The thunk gate keys on `Slots.Count<=1` and admits the 0-slot result; the only non-copyable guard checks *self*, not the *param*. `transferOwnership(_ r: consuming UniqueResource)` is reached by a bare tail-call forwarding the C# `SafeHandle` pointer at +0. Swift's callee runs `deinit` through it; C# later runs VWT `Destroy`+`Free` on the consumed buffer. Probe: **2** destroys for the consuming case (1 for borrowing); heap-owning variant **SIGABRT**. Silent today only because the real `deinit` is empty with an `Int32` payload.

---

## 2. Inconclusive / needs deeper probe (union)

| file:line | from | status | resolution hinge |
|---|---|---|---|
| `MethodMarshalPlanBuilder.cs:162` as an *independent third* PWT-ordering defect | A | split (confirmed vs refuted) | Whether line 162 alone mis-binds, or is inert because the named PWT decls are consumed by name while slot binding is governed by `PInvokeEmitter.cs:839`. **Net:** still switch to `StringComparer.Ordinal` and move in lockstep with `:839`; independence unresolved. |
| `borrowing` non-copyable param thunked as +0 pointer | B | split | Whether the shipped framework is built **resilient** (pointer-at-+0 correct — stronger evidence: disassembled resilient `borrowResource` does `ldr w8,[x0]`, tests pass) or **same-module/loadable** (value-in-register, binding wrong). The *consuming* sibling (#3) is broken regardless. |
| 0-slot/`IsIndirect` conflated with single-register params in thunk admission (`NativeThunkEmitter.cs:614`) | B | split | Structural premise confirmed (the `Slots.Count<=1` branch never inspects `IsIndirect`); disagreement on whether it's a standalone defect beyond #3. Recommend splitting the predicate to require `!IsIndirect` for the fast path. |
| `MaxDirectSlots=4` treated as a combined int+float budget (`TypeLowering.cs:228`) | B | split | A 20-byte `{Int32×3,Float×2}` (5 combined slots, fits per-class register-return budget) was shown returned **directly** by swiftcc but marked `IsIndirect` by the generator (corrupt return); opposing probe used 40/48-byte cases that are genuinely indirect. Definitive probe must hold total size ≤ register-return budget while exceeding 4 combined slots. |

*(Run C reported no inconclusive items — all its candidates reached a decisive verdict or were explicitly deferred.)*

---

## 3. Deferred (candidate, unverified) (union)

Real candidates not probed due to the per-track cap. **Recurring across runs** (elevated confidence it's worth a look):

- **`TypeLowering.LowerOptional` value-type tag-slot model** (`:297-302`) — flagged by **A, B, and C**. Appends a 1-byte integer tag slot unconditionally for non-class value-type inners; `Optional<Bool>`/small no-payload enums reuse a spare bit (size==inner, no tag), so it over-reports shape (e.g. `Optional<Bool>` as 2 slots/2 bytes vs real 1/1). Latent — Optional multi-slot params and Optional returns are gated out today — but a wrong primitive shape a future relaxed gate would trust.

Single-run deferrals:
- **Constructor-only struct-return thunk guard narrower than stated hazard** (`NativeThunkEmitter.cs:102`, C) — free/static functions returning a >16B frozen struct by value emit the structurally identical `LibraryImport` and are *not* gated; probe via `nuke binding-tests --device` `TestMixedWideFactoryRoundTrip`.
- **3-byte no-payload enum tag never zero-extended on x86_64** (`NativeThunkEmitter.cs:525`, `_ => 0` arm, A) — opposite corner of #11; leaves the 4th byte of `%eax` dirty for 65537–16M-case enums.
- **Bool-string-match fragility** (`MethodSignature.cs:401`, `MarshallingHelpers.cs:578`, `CdeclParamMapper.cs:72,836`, A) — `IsBoolType(string)=>type=="bool"` plus a redundant `|| =="Bool"` disjunct; a fully-qualified/sugared Bool render could drop `[MarshalAs(U1)]` → 1-vs-4-byte mismatch. (C separately *refuted* the live version of this on the sampled path — see §4 — so this is a fragility/hardening item, not a live defect.)
- **`ThunkDescriptor.SelfLowering` computed but never read by any thunk target** (`NativeThunkEmitter.cs:740`, B) — dead ABI input; a future multi-register value-type self would silently diverge from the stored lowering.
- **SysV `EmitMetatypeSetup` offsets int spill by `sretOffset` but uses a fixed xmm base for floats** (`SysVThunkTarget.cs:284`, B) — correct today (hidden sret is integer-class), no assertion guarding the invariant.
- **`NeedsReturnBridge` conflates "needs repacking" with "needs indirect pointer preserved across a call"** (`ThunkAssemblyEmitter.cs:163`, A) — the structural root of #2; SysV uses two predicates, ARM64 one. Clean fix = an `IsIndirect`-aware ARM64 predicate.

---

## 4. Checked & refuted — do not re-chase

| file:line | claim | why refuted |
|---|---|---|
| `CdeclParamMapper.cs:799` + `EnumHandler.SimpleEnum.cs:28-29` (A) | Tag-only simple enum: Swift 8-byte `Int` vs C# 4-byte `int` → register-width ABI violation | .NET deterministically zero-extends the 32-bit arg into the upper 32 bits of x0/%rax on **both** Mono and NativeAOT; Swift reads only the tag bytes (`load(as:)`/`ldrb`); enum indices are 0..N-1 so the cast can't truncate. **Cosmetic only.** *(This false-positive was killed by the verify stage — a precision win.)* |
| `Arm64ThunkTarget.cs:51` (C) | ARM64 enum return lacks defensive `uxtb` widening | ~25 inputs incl. 2-byte-tag + adversarial 64-bit-derived enums: 0 dirty bits. Tag always materialized by a 32-bit write or zero-extending load. The justifying comment is factually wrong but the code is safe. |
| `SysVThunkTarget.cs:328` (C) | SysV return-store indexes past 4 integer return registers | Mechanism real but unreachable: `slots.Count>4` forces `IsIndirect` (no bridge), so any bridged return has ≤4 total slots → max int index ≤3. |
| `SysVThunkTarget.cs:110` (C) | x86_64 zero-extend tail-call frame misaligns the stack | Measured callee entry `%rsp` = `8 mod 16` (16-aligned at `callq`, SysV-correct); negative control with a stray push flipped it (probe is sensitive). |
| `SysVThunkTarget.cs:215` (C) | SysV swiftIndirect+metatype `%rbx` assumption | `%rbx` is callee-saved and every metadata-accessor flavor preserves it; e2e run correct. Missing unit test is a doc gap, not a defect. |
| `CdeclParamMapper.cs:72` (C) | Bool round-trip width mismatch | Every Bool P/Invoke param is `[MarshalAs(U1)]` via the single path; assembly-wide `DisableRuntimeMarshalling` makes an unattributed `bool` a hard compile error, not a silent 4-byte BOOL; `!= 0` masks the low byte. |
| `Arm64ThunkTarget.cs:111` (B refuted as live; C confirmed as P2-latent) | ARM64 has no `CanEmit` register-overflow guard | **Both agree on substance:** the missing cap is real but latent — the never-null SysV `CanEmit` (≤6 GPR) shadows it on every production path; it becomes a live P0 only on the arm64-only emission path no caller exercises today. Categorize as **defense-in-depth hardening**, not a current defect. |
| `SwiftBindingsTestLib.cs` 30+-wrapper mechanical sweep (A, C) | (looking for ordinary-path defects) | Both sweeps found the common P/Invoke ↔ `@_cdecl` ↔ thunk-asm path clean: calling convention, param count/type, self placement, sret/error, bool marshalling, metadata order all agree. The confirmed defects are all edge shapes. |

---

## 5. Coverage gaps (union — what A1 did NOT reach)

- **Runtime on the real harness.** All probes were standalone compile/disassembly/emit in `/tmp` (+ CoreCLR-x86_64-under-Rosetta and host-arm64). **None** ran the full `nuke binding-tests --device` (NativeAOT/ARM64) or `--sim` (Mono x64) app pipeline — precisely the durable gate the confirmed P0s need. The `simd_float2` behavior on the *default* `--sim` (Mono `Vector2` marshalling) is unverified.
- **Parameter-side struct lowering** — the nested-struct/packed-field analysis (#6/#7) focused on the **return** buffer; whether the same flat-layout encoding mis-places *inbound* struct args was not probed.
- **`@_cdecl` wrapper path** (`WrapperEmitter.Marshalling.cs`, `WrapperEmitter.Return.cs`, `MethodMarshalPlanBuilder.cs`) read for the bool/optional/Data contract but not exhaustively cross-checked against every projection; deepest probing went to the thunk path.
- **Protocol-extension cdecl ordering** (the 4th `CdeclSignatureContract` branch `[ResultPtr?][Self?][Arguments?][Metadata][ErrorOut?]`) read but never probed through a thunk (a throwing protocol-extension method through a thunk was not exercised).
- **Async / `swifttailcc`** thunking — excluded by `ShouldEmitThunk`, out of scope. Async error propagation / continuation marshalling unprobed.
- **`StringEmitter` UTF-8 slice marshalling** (`SBW_Utf8Slice`) ABI not exercised.
- **tvOS / Mac Catalyst thunk slices** not separately inspected (assumed arm64 + SysV cover the register-model space).
- **The four inconclusive items** (§2) each need one focused register-level probe to settle.

---

## 6. Recommended BindingTests fixtures (union — Swift shapes, no fixes)

Each locks one confirmed defect by round-tripping field values on `--sim` **and** `--device` (plus an x86_64/Rosetta path where the defect is arch-specific).

1. **(#1, P0) Throwing non-failable final-class init with value args.** `public final class ValidatedBox { public let v: Int32; public init(value: Int32) throws { guard value >= 0 else { throw E.bad }; self.v = value } }` — passes every gate → thunked. Test throwing + succeeding calls on `--device` and `--sim`; add a 2-arg variant for the argument-shift cascade. Today: SIGSEGV / corrupt `v` / uninitialized `errorPtr` read.
2. **(#2, P0/P2) Static method returning a >32B frozen struct.** `@frozen struct Big40 { let a,b,c,d,e: Int64 }` + `public static func make() -> Big40` on a class. Assert all fields on `--device` (NativeAOT/arm64). Contrast with the same as an *instance* method (self in x20, no accessor — already works). Today: SIGBUS.
3. **(#3, P0) `consuming` non-copyable double-destroy.** `public struct R: ~Copyable { let id: Int32; deinit { /* observable counter */ } }` + `take(_ r: consuming R)` and contrast `borrow(_ r: borrowing R)`. Assert `deinit` runs **exactly once** (today: 2); add a `deinit` that frees a `malloc`'d pointer to force a hard crash. Plus a unit assertion that the parsed `ArgumentDecl` carries an ownership flag distinct from `borrowing` (locks **#12**).
4. **(#4/#5, P1) ≤8B mixed/float struct with absent layout.** `@frozen struct TupleHolder { var pair: (Int16, Float) }` (so `ComputeAbiFieldLayout` is null while `InlineSize=8`); assert the float survives (not 0.0) on arm64 **and** x86_64. Add a non-SIMD `@frozen struct TwoFloats { var a,b: Float }` registered without `abiLayout`, and exercise `MakeFloat2`→`Vector2` with a **non-trivial** body (`simd_float2(x,y)*2`) so the HFA coincidence can't mask the lost lane.
5. **(#6/#7, P1) Nested + packed struct returns.** `@frozen struct Inner{a:Int8;b:Int64}` inside `@frozen struct Outer{p:Int8;x:Inner;q:Int8}` and `@frozen struct PackedRet{a:Int8;b:Int8;c:Int64;d:Int64}` returned by value; assert every leaf field (first field after the packed pair is the canary). Must run on a host-loadable slice (`--macos`) so `InlineSize` is non-null and the >16B return path is taken.
6. **(#8, P1) `Foundation.Data` after 7 leading `Int` params.** `public func packed(a:Int,…,g:Int, payload: Data) -> Int` reconstructing a checksum; round-trip a known `Data` and assert byte survival on `--sim`/`--device`. Fails on device today (16-byte struct on stack vs two `Int` words).
7. **(#9, P1) Generic method, type param conforming to two protocols whose names diverge under culture vs Ordinal.** `func combine<T: _Aux & View2>(_ x: T) -> Int32` where each witness returns a distinct value; assert values are not transposed. Companion unit test: `HandleProtocolConformance`, `MethodMarshalPlanBuilder`, `FlattenConformances` produce identical orderings for a deliberately-divergent set.
8. **(#10, P1) `@convention(c)` closure with a Bool/enum param.** Add to `Closures/ConventionC.swift`: `callCBoolConsumer(_ fn: @convention(c)(Bool)->Void)`, `callCBoolBinary(_ fn: @convention(c)(Bool,Int32)->Int32)->Int32`, and an `@objc` enum variant. Regenerate (`--compile-only`) — today `CS1503`. Add a round-trip asserting the managed delegate observes the correct boolean.
9. **(#11, P2) >256-case frozen enum return.** `@frozen` no-payload enum with ≥300 cases in a separate module (null `InlineSize`), returned selecting case 256 and 299; assert C# == 256/299 on x86_64 (not 0/43). Optional 3-byte-tag variant for the deferred `InlineSize==3→0` corner.
10. **(comment traps, P2) High-arity throwing instance method + thunk unit test.** `func f(_ a: Int) throws -> Int` (and a ≥7-int-param throwing variant) with a `ThunkAssemblyEmitterTests` assertion that ARM64 reads self from `x{ParameterCount}` and error last — codifying the self-after-args layout the `Arm64ThunkTarget.cs:59` comment contradicts (locks the all-three-runs comment trap + the latent ARM64 `CanEmit` cap).

---

*Provenance: the three raw per-run reports are preserved unmodified in [`Track-A1_run-reports/`](./Track-A1_run-reports/) — `runA_wf7b85adb7_thunk-ordering.md`, `runB_wf705dc776_ownership-closures.md`, `runC_wf94614753_struct-layout.md`.*
