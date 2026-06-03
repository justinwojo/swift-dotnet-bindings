# Protocol-Proxy Reverse-Callback: Swift-Class Parameter Marshalling Fix

**Status:** Implemented and fully verified — Tier 1 (sim/Mono + device/NativeAOT, TDD RED/GREEN per site) **and** Tier 2 live Kidoz E2E (sim/Mono **9/0** + device/NativeAOT **9/0**; the literal issue #40 `OnInterstitialAdFailedToLoad(KidozError)` callback fires live and reads back cleanly). The PR was then **expanded** to sweep the same ObjC-rooted ARC defect class beyond the receiver direction — async `self`-retain across the await and enum class-payload extraction — see [Scope expansion](#scope-expansion--objc-rooted-arc-sweep-async-self-retain--enum-payloads). One adjacent return-direction leak ("Fix A") — initially scoped out as deferred — was then **also resolved** in this PR by adopting the owned +1 on the `Optional<@objc>` return path (the *missing-adopt* choice), with its regression test un-skipped on sim/Mono and device/NativeAOT — see [Fix A](#fix-a--optionalobjc-rooted-class-return-over-retain-resolved).
**Date:** 2026-06-02
**Origin:** GitHub issue [#40](https://github.com/justinwojo/swift-dotnet-bindings/issues/40) (Kidoz binding), SDK v0.12.1
**Related audit finding:** Track A3 / **P1-01** (audit doc lives on the `audit-workflows` branch only — see [Relationship to the audit backlog](#relationship-to-the-audit-backlog); facts reproduced inline)

## TL;DR

When a C# class implements a Swift protocol and **Swift calls back** into it with a method
whose parameter is a **Swift class instance** (e.g. `func onInterstitialAdFailedToLoad(kidozError: KidozError)`),
the generated proxy receiver marshals that parameter with a naive `Unsafe.Read<T>`. For a reference
type that reinterprets a Swift heap-object pointer as a .NET managed object reference → garbage ref →
**SIGSEGV** the moment it is used. Strings dodge this via a special case; Swift classes have no branch.

The fix is two coordinated changes:
1. **Generator** — route Swift-class (and `Optional<class>`) receiver parameters through the runtime's
   copy-out marshalling instead of the local `Unsafe.Read<T>` helper, at all receiver sites.
2. **Runtime** — fix the ObjC-rooted ARC half (audit **P1-01**): the copy-out path must use
   `Arc.UnknownObjectRetain` (isa-dispatching), not native-only `Arc.Retain`, because the affected
   Kidoz types are `@objc … : NSObject`.

## Symptom (from the field)

`carljohansen` reported (issue #40) that after a successful `Kidoz.Instance.Initialize(...)`, calling
`KidozInterstitialAd.Load(delegate)` against a server-side rejection crashes natively when the SDK
invokes the failure callback. Attached crash log (`OnInterstitialAdFailedToLoad_crash.txt`) stack,
bottom-up:

```
WebViewWrapper (kdzError "No offers")
 → FullscreenAdManager.error(with:andMessage:)
 → InterstitialAdLoader.WrapperDelegate.onInterstitialAdFailedToLoad(error: KPError)
 → EveryProtocol.onInterstitialAdFailedToLoad(kidozError: KidozError)   [generated Swift wrapper]
 → (C# function pointer)
 → SIGSEGV  (do_icall / native_to_interp_trampoline, Mono interpreter)
```

The team's own Kidoz testing passed because it only exercised callbacks that are **no-arg**
(`onInitSuccess`) or **string** (`onInitError`) — both handled correctly. The crash needs a callback
parameter that is a **Swift class**.

## Root cause

### The reverse-callback ABI

For a protocol method that Swift invokes on the C# impl, the generated Swift wrapper
(`EveryProtocolEmitter.cs:~3153`) passes each class argument **by address of a local copy**:

```swift
public func onInterstitialAdFailedToLoad(kidozError: KidozSDK.KidozError) {
    var selfProto: KidozSDK.KidozInterstitialDelegate = self
    var kidozErrorCopy = kidozError                 // borrowed +1, released when wrapper returns
    _kidozInterstitialDelegate_vtable.func_onInterstitialAdFailedToLoad_1!(
        _kidozInterstitialDelegate_vtable.csVTHandle, &selfProto, &kidozErrorCopy)
}
```

So the C# receiver gets `IntPtr rawArg0 == &kidozErrorCopy` — **a pointer to the slot that holds the
object pointer**, not the object pointer itself, and the slot is only valid for the duration of the call.

### The broken unmarshal

The generated C# receiver does:

```csharp
var param0 = MarshalFromSwift<KidozSDK.KidozError>(rawArg0);   // BUG
impl.OnInterstitialAdFailedToLoad(param0);
```

where `MarshalFromSwift<T>` is a **per-proxy local helper** (one copy per proxy class), emitted at
`ProtocolProxyEmitter.SwiftObject.cs:~279`:

```csharp
private static T MarshalFromSwift<T>(IntPtr ptr) => Unsafe.Read<T>((void*)ptr);
```

`Unsafe.Read<KidozError>` reads 8 bytes at `rawArg0` (the Swift heap-object pointer) and
**reinterprets them as a managed `KidozError` reference**. The first use of that fake reference —
method dispatch, a GC write barrier, or reading a property — dereferences garbage → SIGSEGV. This
matches the `do_icall` / `native_to_interp_trampoline` frames in the crash.

### Why String is fine but a class is not

The per-parameter unmarshal loop (`ProtocolProxyEmitter.Receivers.cs:~1001-1080`) special-cases
`String`:

```csharp
else if (IsStringTypeSpec(param.SwiftTypeSpec))   // Receivers.cs:~1051
{
    var rawParam0 = SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(rawArg0);
    var param0 = rawParam0.ToString();
}
```

Everything else falls through `GetReceiverSetterConversion` (`Receivers.cs:~1340-1366`), whose `switch`
ends in `_ => null` with **no arm for a Swift class** (`ClassProjection` / `ObjCRootedClassProjection`),
into the naive fallback at `Receivers.cs:~1075`. This is the exact fragile spot flagged in
`.claude/rules/constraints.md` ("ProtocolProxyEmitter.Receivers still uses switch with `_ => null`
fallback — check manually").

## Scope — the full broken category

Anything whose ABI marshal type is a **managed class/wrapper name** (not a blittable value or `IntPtr`)
and reaches the local `Unsafe.Read<T>` fallback is affected:

| Param shape | Status | Notes |
|---|---|---|
| Plain Swift class (`ClassProjection`) | **Broken** | — |
| ObjC-rooted Swift class (`ObjCRootedClassProjection`) | **Broken** | Kidoz case (`KidozError`, `KPError`, `KidozInterstitialAd`, `KidozBannerView`, `KidozRewardedAd`) |
| `Optional<class>` | **Likely broken** | Slot read as `SwiftOptional<T>` via `Unsafe.Read`; needs its own branch |
| Non-frozen struct / complex enum projected as class | Risky | SafeHandle-backed; `Unsafe.Read` ≠ `NewFromPayload` |
| KeyPath, tuple-with-class element, ref-struct wrappers | Risky | Same fallback |
| `String` | OK | Special-cased (`MarshalFromSwiftObject<SwiftString>`) |
| Closures | OK | Dedicated `SwiftEscapingClosure` path |
| Blittable primitives / simple enums | OK | `Unsafe.Read` layout matches |
| ObjC-bridged value types (e.g. `URL`/`NSUrl`) | OK | ABI is `IntPtr`; `Unsafe.Read<IntPtr>` + `GetNSObject` |
| Existentials (`any P`) | Separate path | `GetReceiverExistential*` handles |

**Affected receiver sites** (`ProtocolProxyEmitter.Receivers.cs`):
- Method-receiver params (`~1075`) — the Kidoz crash
- Property-setter value (`~247-251`) — built as a separate `marshalExpr`, **not** covered by
  `GetReceiverSetterConversion`; must be patched independently
- Subscript getter index params (`~654`)
- Subscript setter value + index params (`~731`, `~757`)

In Kidoz this poisons every class-carrying callback across `IKidozInterstitialDelegate`,
`IKidozRewardedDelegate`, and `IKidozBannerDelegate` (loaded / failedToLoad / shown / closed /
impression). The interstitial-failure path is simply the first one the consumer hit.

## The ARC sub-issue (audit P1-01 / Track A3)

The correct reconstruction for a borrowed class slot is **COPY semantics**: deref the slot, take an
**independent `+1`**, then `NewFromPayload`. MOVE/borrow variants are wrong here:
- `MarshalMovedValueFromSlot<T>` class branch does **not** retain → use-after-free after Swift releases
  `kidozErrorCopy`.
- `MarshalBorrowedFromSwift<T>` suppresses the finalizer → wrong ownership for a value that escapes the
  callback.

The runtime already has the COPY entry point — `SwiftMarshal.ExtractCopiedValue<T>` (`SwiftMarshal.cs:456`),
whose class fast-path is `classPtr = *(IntPtr*)source; Arc.Retain(classPtr); MarshalFromSwift<T>(classPtr)`.
**But it uses native-only `Arc.Retain` (`swift_retain`).** The Kidoz payload types are confirmed
`@objc … : NSObject` (symbolgraph: `c:@M@KidozSDK@objc(cs)KidozError -> c:objc(cs)NSObject`), and
`Arc.cs:~90` documents that `swift_retain` is for **native Swift only**; `swift_unknownObjectRetain`
inspects the isa and dispatches to `swift_retain` *or* `objc_retain`.

This is already a known, documented defect — `src/docs/audits/STATE-OF-THE-CODEBASE.md`:

> **P1-01** | `SwiftMarshal.cs:466` + `:1466` | `Arc.Retain` (`swift_retain`, **no-op on NSObject
> subclass**) on `Kind==Class`, no ObjC discrimination → **over-release/SIGSEGV** | fix: use
> `Arc.UnknownObjectRetain` both sites

So the Kidoz crash is a real-world manifestation of audit finding **A3/P1-01**. The two `Arc.Retain`
sites are `ExtractCopiedValue` (`:466`) and its tuple twin `ExtractCopiedElement` (`:1466`).

## Fix design

### 1. Runtime (close P1-01)

- In `ExtractCopiedValue<T>` (`SwiftMarshal.cs:466`) and `ExtractCopiedElement` (`SwiftMarshal.cs:1466`),
  replace `Arc.Retain` with `Arc.UnknownObjectRetain` for the class fast-path. `swift_unknownObjectRetain`
  is correct for **both** pure-Swift and ObjC-rooted classes (it dispatches by isa), so this is a strict
  upgrade — no separate ObjC-vs-Swift branch required at the retain site.
- Optionally add a thin, intent-named helper
  `SwiftMarshal.MarshalBorrowedClassFromSlot<T>(IntPtr slot)` that centralizes deref + ObjC-aware retain +
  `NewFromPayload`, so generated receiver code stays a single readable call symmetric with the String
  path's `MarshalFromSwiftObject<SwiftString>`.

### 2. Generator (route class params off the naive helper)

In `ProtocolProxyEmitter.Receivers.cs`, add a Swift-class detection branch **ahead of** the `_ => null`
fallback, mirroring the *outgoing* direction which already special-cases classes
(`InterfaceImpl.cs` uses `IsSwiftClassType` / `.Payload.DangerousGetHandle()` / `.Handle`). Apply at all
receiver sites:
- method params (`~1075`)
- property-setter `marshalExpr` (`~247-251`) — **separate code path**
- subscript getter index params (`~654`)
- subscript setter value + index params (`~731`, `~757`)

For a class param emit the runtime copy-out call (the new helper, or `ExtractCopiedValue<T>` directly).
Also handle **`Optional<class>`** so a nil/non-nil class payload is read via the pointer path rather than
`Unsafe.Read<SwiftOptional<T>>`.

**Out of scope (do not touch):** the return-value / witness-dispatch directions already use the runtime
marshaller correctly. Confirmed by `ProtocolProxyEmitterTests.cs:~461`. *(One return-direction exception —
the `Optional<@objc>` return over-retain — surfaced later and was resolved as part of this PR; see
[Fix A](#fix-a--optionalobjc-rooted-class-return-over-retain-resolved).)*

## Relationship to the audit backlog

Cross-referenced so we don't duplicate effort and so this lands in the audit's prescribed shape.

> **Heads up — the source doc is not in this worktree.** The audit capstone
> `src/docs/audits/STATE-OF-THE-CODEBASE.md` (and the per-track reports) live **only on the
> `audit-workflows` branch** (committed in `d83af2d8`), which is *ahead* of `main`. This fix branch is
> cut from `main`, so the file is absent here and the `P*-NN` / "Cluster N" / "Top-20 #N" / "§7 #N" labels
> below are provenance pointers, not local links. Every fact you need is reproduced inline (file:line +
> description). To read the full backlog: `git show audit-workflows:src/docs/audits/STATE-OF-THE-CODEBASE.md`.

- **Runtime half = audit P1-01 (Track A3) — closing it, not re-finding it.** `ExtractCopiedValue`
  (`SwiftMarshal.cs:466`) is verified to be `Arc.Retain(classPointer)` gated on `Kind==Class` (`:458-463`)
  with no ObjC discrimination — exactly P1-01. The audit prescribes our fix verbatim: Cluster 3 "use
  `Arc.UnknownObjectRetain` for class payloads"; Top-20 #6 "fix `ExtractCopiedValue` and its `:1466` twin
  together." **When this lands, mark P1-01 resolved in the backlog.**
- **Tier-1 fixture overlaps audit §7 fixture #7** ("ObjC-backed `NSObject` subclass extracted via
  `Optional`/`Result`/tuple-in-carrier — assert retain-count balance, A3 #1"). Make our fixture satisfy
  both: the audit frames the *extraction carrier*; we add the protocol-proxy *reverse-callback* site.
- **Generator half is NEW — not in the audit.** The audit's `Receivers.cs` findings are both
  *existential*, not concrete-class: **P1-04** (`:931`, optional-existential always-nil) and **P1-08**
  (`:1580/1519`, class-bound `[any P]` array wrong stride). The scalar Swift-class `Unsafe.Read<T>`
  fallback (`Receivers.cs:~1075` + helper `SwiftObject.cs:~279`) is unrecorded — consistent with the
  audit's ~40–60% recall caveat. Complementary, not redundant.
- **Same-file neighbors — coordinate, don't collide:**
  - **P1-04** (`Receivers.cs:931`): the optional-existential path has a dead-stub-wins bug right where we
    add the `Optional<class>` branch — keep them distinct.
  - **P0-09** (`InterfaceImpl.cs:1857` + `WitnessDispatchEmitter.cs:1901`): opaque `any P` **return**
    double-release. This is the return direction we scope *out*; the audit confirms it's a separate live
    P0 — scoping it out is correct, but we are **not** fixing it here.
  - **P0-10** (`SwiftObject.cs:92`): finalizer-thread VWT Destroy — same file as our helper (`:279`),
    different concern.
- **`MarshalBorrowedFromSwift` (rejected alternative) carries its own confirmed defect — P1-02.** The
  method (`SwiftMarshal.cs:825`) does suppress the finalizer (wrapper + Payload SafeHandle), so it is a
  borrow, not the COPY we need. But P1-02 notes an explicit user `.Dispose()` still calls `ReleaseHandle`
  (SuppressFinalize skips only the finalizer queue) → double-free. Our reason to reject it for an escaping
  value holds; just don't treat the method as defect-free.

## Reproduction & test plan — two tiers

This is **round 4** of Kidoz fixes. The recurring failure mode is shipping a binding that passes our
gate but breaks on first real use. We close that gap with two complementary tiers: a **durable in-repo
fixture** (permanent regression prevention) **and** a **live real-deal Kidoz E2E** (round-4 confidence
that the actual SDK works end-to-end). Both ship.

### Why our gate kept missing it

`internal-binding-testing/Kidoz/Program.cs` only ever exercised the **init** path: it calls
`Kidoz.Instance.Initialize("BINDING-TEST", "BINDING-TEST", …)` whose callbacks are `onInitSuccess()`
(no-arg) and `onInitError(String)` — neither carries a class param, so the bug can't fire. The harness
**never calls `KidozInterstitialAd.Load`**, which is exactly the path the reporter hit. And the fake key
fails init, so even the rewarded/banner load paths were never reached. Our "tested it, passed" was
testing the one corner that happens to be safe.

Every method on `IKidozInterstitialDelegate` (`KidozSDK.cs:3571`) takes a class param — there is **no
safe interstitial callback**:

```csharp
void OnInterstitialAdLoaded(KidozInterstitialAd);          // class
void OnInterstitialAdFailedToLoad(KidozError);             // @objc:NSObject  ← reporter's crash
void OnInterstitialAdShown(KidozInterstitialAd);           // class
void OnInterstitialAdFailedToShow(KidozInterstitialAd, KidozError);  // two classes
void OnInterstitialImpression(KidozInterstitialAd);        // class
void OnInterstitialAdClosed(KidozInterstitialAd);          // class
```

So **any** interstitial callback firing reproduces the crash — we don't depend on the server returning an
ad vs. a rejection.

### Tier 1 — durable in-repo fixture (BindingTests, the permanent gate)

There is currently **no** BindingTests fixture for "Swift → C# reverse callback with a concrete
Swift-class parameter." Add one — this is what permanently prevents regression in the main repo.

1. **Swift fixture** (new `BindingTests/Sources/SwiftBindingsTestLib/Protocols/ClassParamCallback.swift`,
   auto-globbed):
   - a **pure-Swift** class payload + a protocol method taking it + a synchronous driver free function;
   - a **`@objc … : NSObject`** class payload variant — this is the one that actually exercises the
     `UnknownObjectRetain` fix (matches Kidoz exactly).
2. **C# test** (new `BindingTests/RuntimeTestsApp/Protocols/ClassParamCallbackTests.cs`): implement the
   generated `I…` interface, trigger the driver, and **read a property off the received instance**
   (the operation that crashes today). Add a lifetime/leak assertion so the ARC fix is verified, not just
   the non-crash. Keep the C# impl strongly rooted (the proxy holds it via `WeakReference`).
3. **Emitter unit test** — extend `ProtocolProxyEmitterTests` to assert the generated `Receive_*` body
   emits the runtime copy-out call, **not** `MarshalFromSwift<ConcreteClass>(rawArg0)`.
4. **Confirm red** → apply fix → **confirm green**.

### Tier 2 — live real-deal Kidoz E2E (round-4 confidence)

The reporter handed us a **public test appId/token from the Kidoz sample app** (`appId "14428"`,
`token "6yAsKUngaG5yC4X5HsRoatKTso40NMoZ"`). With a real key, init **succeeds**, so we can finally drive
`KidozInterstitialAd.Load` against the live Kidoz server and observe the actual callback — the literal
reporter repro. (Public demo key, lifted from Kidoz's own open sample app; safe to use. Do **not** bake
any private key. It may rotate — if init starts failing, refresh from the upstream sample.)

5. **Extend `internal-binding-testing/Kidoz/Program.cs`** (the existing harness; uses `[PASS]`/`[FAIL]`
   lines that `run-all-sim.sh` gates via `grep -c`, so new smokes integrate with no runner change):
   - **SMOKE 6 — real init:** call `Initialize("14428", "6yAsKUngaG5yC4X5HsRoatKTso40NMoZ", initDelegate)`
     and wait for `OnInitSuccess` (the existing fake-key SMOKE 5 stays as the offline/no-arg case).
   - **SMOKE 7 — interstitial load (the crash path):** after init success, implement a full
     `IKidozInterstitialDelegate` that records *which* callback fired and **reads `kidozError.ErrorCode`
     / `kidozError.Message`** (the reporter's was `"No offers"`) — and a property off `KidozInterstitialAd`
     on the loaded path. Call `KidozInterstitialAd.Load(interstitialDelegate)`, pump the runloop
     generously (~20 s), then assert: a callback fired **and** the property read returned sane data with
     no crash.
   - **Pre-fix expectation:** SIGSEGV the moment any interstitial callback marshals its class param.
     **Post-fix:** the callback delivers a usable `KidozError` / `KidozInterstitialAd`.
   - **Fail-closed on silence:** "no callback within the timeout" must score `[FAIL]`, never pass — a
     network stall must not be able to masquerade as success and re-hide the bug.
6. **Regenerate the real Kidoz binding under the fixed generator** before running tier 2, so the E2E
   exercises freshly-emitted receiver code, not the v0.12.1 artifact.

> Tier 2 lives in `internal-binding-testing` (throwaway third-party scaffolding — see memory
> `project_internal_binding_testing_temporary`), so it is the **real-deal confidence** gate, not the
> durable regression gate. Adding two smokes to the existing `Program.cs` is using the harness for its
> purpose, not new tooling investment. The durable gate stays Tier 1.

## Verification gates

| Gate | When |
|---|---|
| `nuke binding-tests --compile-only` then `--skip-regen` (sim/Mono) | After generator + runtime change |
| `nuke binding-tests --device` (NativeAOT) | **Required** — calling-convention / marshalling / ARC change; Mono and NativeAOT have different failure modes |
| `nuke test` (emitter/runtime unit) | After change |
| Tier 2: regenerate Kidoz → run SMOKE 6/7 live, **sim and device** | Pre-release E2E. Device matters — the reporter is on a real device/NativeAOT app, and Mono vs NativeAOT diverge on ARC + `UnmanagedCallersOnly` |

Rebuild the Debug generator (`dotnet build src/Swift.Bindings/src -c Debug`) before regen — `nuke`
targets call the generator from `bin/Debug/` and only rebuild when the dll is missing, so a stale binary
would mask the fix.

## Execution sequence (do these in order)

TDD per project rule `feedback_tdd_for_regression_fixes`: **the failing test comes first** — write it,
watch it crash with the *unpatched* generator, then fix. Don't apply any fix before step 2 is red.

1. **Write the Tier-1 fixture and unit test (no fix yet).**
   - Add the Swift fixture (`ClassParamCallback.swift`) and the C# test (`ClassParamCallbackTests.cs`)
     per Tier 1 above — both the pure-Swift and the `@objc:NSObject` class-param variants.
   - Add the emitter unit test asserting the generated `Receive_*` body does **not** emit
     `MarshalFromSwift<ConcreteClass>(rawArg0)`.
2. **Confirm RED.** `nuke binding-tests --compile-only` then `--skip-regen` (sim) — the
   `@objc:NSObject` variant must crash/SIGSEGV, and the emitter unit test must fail. If it doesn't crash,
   the fixture doesn't reproduce the bug — fix the fixture before going further.
3. **Apply the runtime fix (P1-01).** `ExtractCopiedValue` (`SwiftMarshal.cs:466`) +
   `ExtractCopiedElement` (`SwiftMarshal.cs:1466`): `Arc.Retain` → `Arc.UnknownObjectRetain`. Optionally
   add the `MarshalBorrowedClassFromSlot<T>` helper.
4. **Apply the generator fix.** Route Swift-class and `Optional<class>` receiver params through the
   runtime copy path at all four sites (method params, property-setter `marshalExpr`, subscript getter
   index, subscript setter value/index). **Both steps 3 and 4 are required for green** — step 4 stops the
   `Unsafe.Read` crash; step 3 makes the resulting retain correct on `@objc:NSObject` types. Neither alone
   is sufficient.
5. **Rebuild the Debug generator** (`dotnet build src/Swift.Bindings/src -c Debug`) — `nuke` won't rebuild
   a stale one.
6. **Confirm GREEN (sim/Mono).** `nuke binding-tests --compile-only` then `--skip-regen`; `nuke test`.
   The Tier-1 fixture and emitter unit test now pass.
7. **Confirm GREEN (device/NativeAOT).** `nuke binding-tests --device`. Required — ARC + `UnmanagedCallersOnly`
   behave differently under NativeAOT, which is what the reporter runs.
8. **Tier-2 live Kidoz E2E.** Regenerate the real Kidoz binding under the fixed generator; add SMOKE 6/7
   to `internal-binding-testing/Kidoz/Program.cs`; run on **sim and device**. Pre-fix this path SIGSEGVs;
   post-fix `kidozError.Message` reads back (`"No offers"`).

Grep the whole emitter for the same `Unsafe.Read<T>` fallback shape before declaring done — if a fifth
receiver site exists, fix it in the same pass (project rule: enumerate the category, don't whack moles).

## Pair-review summary

Reviewed in consult mode via `/coding-rules` (Codex + Grok-build + Grok-Composer, same prompt, parallel).

- **Unanimous:** root cause; COPY-not-MOVE; all four receiver sites; sync repro sufficient + run device;
  add emitter unit test; don't touch return/witness paths.
- **Decisive divergence — ARC flavor:** Codex, Grok-build, and independent verification confirmed the
  ObjC-rooted retain issue (`Arc.UnknownObjectRetain`); Grok-build cited the audit doc P1-01 (verified
  real). Composer asserted `Arc.Retain` is fine for concrete `NSObject` classes — **contradicted** by the
  audit doc and `Arc.cs`, so rejected.
- **Adopted from review:** `Optional<class>` receiver gap; fix both `ExtractCopiedValue:466` and
  `ExtractCopiedElement:1466`; patch the property-setter `marshalExpr` separately.

## Open decisions

1. Fix **P1-01** as part of this change (closes the audit finding + Kidoz in one pass) vs. scope the
   generator change only and leave the runtime ARC fix to the audit follow-up. *Recommendation: fix both
   — they are the same root cause and Kidoz needs the runtime half to be correct on ObjC-rooted types.*
2. Dedicated `MarshalBorrowedClassFromSlot<T>` helper vs. emit `ExtractCopiedValue<T>` inline.
   *Recommendation: dedicated helper — readable generated code, single home for the ObjC-aware retain.*
3. Verification breadth.
   *Decision (user): two tiers — minimal in-repo BindingTests fixture (pure + `@objc:NSObject`) as the
   durable gate **plus** a live real-deal Kidoz E2E: regenerate the actual binding and drive
   `KidozInterstitialAd.Load` against the live server with the public test key, sim and device. Round 4
   must not ship a half-usable binding; the actual SDK has to be proven working end-to-end.*

## Implementation outcome

What actually shipped, and where reality diverged from the plan above.

### Both fixes landed as designed

- **Generator** (`ProtocolProxyEmitter.Receivers.cs`): a `GetReceiverClassCopyOutExpr` branch now routes
  `ClassProjection` / `ObjCRootedClassProjection` / `OptionalProjection{Inner: class}` receiver params
  through `SwiftMarshal.MarshalBorrowedClassFromSlot<T>` / `MarshalBorrowedOptionalClassFromSlot<T>` at
  **all** receiver sites (method param, subscript getter index, subscript setter value/index). Verified in
  generated output: all four `ClassParamCallback` receivers (`ClassParamPayload` + `ObjCClassParamPayload`,
  plain + optional) emit the helper call, not `Unsafe.Read<T>` / `MarshalFromSwift<ConcreteClass>`.
- **Runtime** (`SwiftMarshal.cs`): `Arc.Retain` → `Arc.UnknownObjectRetain` at `ExtractCopiedValue`
  (now `:469`) and `ExtractCopiedElement` (now `:1520`); the two `MarshalBorrowedClassFromSlot` /
  `MarshalBorrowedOptionalClassFromSlot` helpers added (their own ObjC-aware retains at `:501`/`:521`).

### The P1-01 fixtures were re-aimed — the plan's carrier premise was empirically wrong

The plan's Tier-1 P1-01 probes were written assuming `makeOptionalObjCPayload`, `makeObjCPayloadCodeTuple`,
and `makeObjCPayloadArray` reach `ExtractCopiedValue` / `ExtractCopiedElement`. Verified against the
generated C#, **they do not**:

- `Optional<@objc>` **return** → **originally** emitted inline as `result == IntPtr.Zero ? null : GetNSObject<T>(result)`
  (the bare-`GetNSObject` path), never `ExtractCopiedValue`. *This was the "Fix A" leak — now **resolved**: the
  path adopts the owned +1 via `GetINativeObject<T>(result, true)`; see [Fix A](#fix-a--optionalobjc-rooted-class-return-over-retain-resolved).*
- non-optional `(@objc, scalar)` tuple → the emitter **unrolls** it per element
  (`_tupleMetaPtr->GetElementOffset` + direct `MarshalFromSwift`), bypassing `MarshalTupleFromSwift`, so it
  never reaches `ExtractCopiedElement`.
- `[@objc]` array → `SwiftArray.Get` (already-owned element adopt), not `ExtractCopiedElement`.

Per `feedback_tdd_for_regression_fixes` (round-1 minimum-repro fixtures mask the real surface), the probes
were rebuilt to hit the lines for real, holding the payload in a Swift global so an over/under-retain shows
up as the global's live count diverging from 1:

- `stashSharedObjCRefAndReturnResult` → `Result<ObjCClassParamPayload, TrackedRefError>`; the C# `.Success`
  getter drives `SwiftResult.ExtractPayloadValue` → **`ExtractCopiedValue`** (`:469`). Test
  `TestObjCResultSuccessExtractionBalancesArc`.
- `stashSharedObjCRefAndReturnOptionalTuple` → `Optional<(ObjCClassParamPayload, Int32)>`; wrapping the
  tuple in `Optional` routes it through the runtime `MarshalTupleFromSwift` →
  `MarshalElementFromSwiftUnsafe` → **`ExtractCopiedElement`** (`:1520`). Test
  `TestObjCOptionalTupleExtractionBalancesArc`.

**TDD RED/GREEN was run per site, isolated:** reverting *only* `ExtractCopiedValue` → `Arc.Retain` reds
*only* the Result probe (`Expected 1 live object(s), got 0` — native `swift_retain` under-retains the
NSObject so the managed wrapper over-releases it while the Swift global still holds it); reverting *only*
`ExtractCopiedElement` reds *only* the tuple probe; both restored → all green. The `@objc` receiver
no-leak test stayed green throughout (it uses the untouched borrowed-slot helper), proving the probes
isolate exactly the two P1-01 lines.

### `@objc` receiver no-leak needed a runloop pump, not just GC

`ObjCClassParamPayload` is an `NSObject` peer whose finalization is **deferred to the main-thread queue**,
so a GC-only drain (`GC.Collect` + `WaitForPendingFinalizers`) leaves it alive and the leak assert flakes.
`TestObjCClassParamReceiverNoLeak` uses a `DrainObjCFinalizers` helper that interleaves GC with
`NSRunLoop.Current.RunUntil` to pump the main queue. (Pure-Swift `ClassParamPayload` releases synchronously
and needs no pump.)

### Verification results

| Gate | Result |
|---|---|
| `nuke binding-tests --compile-only` | Succeeded (generated output reaches the intended marshalling paths) |
| `nuke binding-tests --skip-regen` (sim/Mono), `--class-filter ClassParamCallbackTests` | **14 pass, 0 fail, 0 skip** (the former Fix A skip is now an active, passing regression guard) |
| `nuke binding-tests --device` (NativeAOT), same filter | **14 pass, 0 fail, 0 skip** |
| `nuke test` → `Swift.Bindings.Unit.Tests` | **12194 pass, 0 fail, 1 skip** (incl. the new emitter unit test) |
| **Tier 2: live Kidoz E2E, sim/Mono** (regenerated real binding + locally-packed fixed runtime) | **9 pass, 0 fail** — `OnInterstitialAdFailedToLoad(KidozError)` fired live (`ErrorCode=10400`, `Message='Visible ViewController is nil'`) and read back cleanly |
| **Tier 2: live Kidoz E2E, device/NativeAOT** | **9 pass, 0 fail** — identical: the exact issue #40 callback fired and the `KidozError` class param read back without SIGSEGV |

(`Swift.Analyzers.Tests` 21/21 fail in-sandbox is an environmental refpack-download gap, not this change —
see memory `feedback_analyzer_tests_need_refpack`. The single-class `--class-filter` runs trip the global
pass-count baseline check; that's expected for a filtered run, not a regression.)

**Tier 2 detail.** The real Kidoz binding was regenerated under the fixed generator (25 `MarshalBorrowedClassFromSlot`
call sites in `KidozSDK.cs`; the reporter's literal `Receive_onInterstitialAdFailedToLoad_1` receiver now routes
`KidozError` through the helper instead of `Unsafe.Read<KidozError>`). The fixed runtime was packed locally as
`SwiftBindings.Runtime 0.12.1` (version-stable, stale same-version nupkg/cache wiped) because the regenerated
binding *calls* `MarshalBorrowedClassFromSlot`, which does not exist in the published 0.12.1 — so the binding
cannot even compile against the shipped runtime, making the local pack mandatory. `SMOKE 5/6` initialize the SDK
with Kidoz's own public sample key (`OnInitSuccess` over a real network round-trip); `SMOKE 7` drives
`KidozInterstitialAd.Load` and survives the `OnInterstitialAdFailedToLoad(KidozError)` reverse callback — the
literal field crash. Pre-fix this path SIGSEGVs the instant the callback fires; post-fix it reads
`kidozError.ErrorCode` / `.Message`. (The ad "fails to load" benignly — no visible view controller in the headless
smoke — which is precisely the failure callback that carries the `KidozError` class parameter.)

## Scope expansion — ObjC-rooted ARC sweep (async self-retain + enum payloads)

After the receiver fix landed, the PR was expanded (user decision: "fix all now") to sweep the **same
ObjC-rooted ARC defect class** wherever else the generator/runtime retains a class pointer that may be
`@objc … : NSObject`. The original sweep flagged eight candidate sites; root-causing them split the work
into three distinct outcomes — two real fixes (with opposite shapes) and a pair of false positives.

### Bucket 2A — async `self` retain across the await (family upgrade)

An async instance method on a Swift class keeps `self` alive across the `await` by retaining the
self-pointer into the Task/call holder and releasing it in the completion callback. For an
`@objc:NSObject`-rooted `self`, native-only `swift_retain`/`swift_release` touch the wrong refcount word
(the same root cause as P1-01) → over-release / leak. The retain **and its paired release** were upgraded
to the isa-dispatching variants at every async self-retain site:

- `WrapperEmitter.Async.cs` — async instance-method self-retain (`Arc.Retain(_selfPtr)` →
  `Arc.UnknownObjectRetain`) + the completion-callback release (`Arc.Release(retained.Ptr)` →
  `Arc.UnknownObjectRelease`).
- `AsyncMethodGenericBridgeEmitter.cs` — the generic-bridge self-retain + release, same upgrade.
- `AsyncHarnessEmitter.cs` — the `RetainedSelfPtr` cleanup release, same upgrade.

`swift_unknownObjectRetain`/`Release` dispatch by isa, so the upgrade is correct for **both** pure-Swift
and ObjC-rooted self — a strict improvement. Retain and release were moved together so the pairing stays
balanced (this is a *borrowed* self-pointer that genuinely needs the +1 across the suspension — distinct
from the enum over-retain below).

**Fixture:** `BindingTests/Sources/SwiftBindingsTestLib/Async/ObjCAsyncSelf.swift`
(`@objc public class ObjCAsyncSelf : NSObject` with tracked init/deinit, `computeAsync(factor:)`
with-params + `pingAsync()` no-params) and `BindingTests/RuntimeTestsApp/Async/ObjCAsyncSelfTests.cs` —
drives both async methods over many iterations and asserts ARC balance (every `self` deallocs).

### Bucket 2B — enum class-payload extraction over-retain (spurious retain removed)

The enum class-payload extraction sites — E1 (offset path), E2 (marshal path), E3
(generic-type-parameter path) in `EnumHandler.Marshalling.cs` — previously emitted
`classPtr = *(IntPtr*)…; Arc.Retain(classPtr); MarshalFromSwift<T>(classPtr)`. This is the **opposite**
shape from 2A, and the original "just switch these to `UnknownObjectRetain`" framing for them was **wrong**.
The corrected analysis:

- The enclosing `TryGet` already takes an **owned copy** of the enum via `InitializeWithCopy` (an
  isa-correct +1: `objc_retain` for an `@objc:NSObject`-rooted payload, `swift_retain` for pure-Swift),
  then `DestructiveProjectEnumData` strips the tag (no release), then reads the class pointer out of the copy.
- That copy is **never VWT-destroyed** (`TryGet` returns the projected value directly), so
  `InitializeWithCopy`'s +1 is the wrapper's to **adopt**.
- `MarshalFromSwift<T>` → `NewFromPayload` **adopts exactly one reference** (a pure-Swift wrapper stores
  the handle in its SafeHandle; an `@objc:NSObject` peer base-retains then `DangerousRelease`s the extra).

So the explicit `Arc.Retain` was a **spurious +1 → over-retain leak**, not an under-retain. The fix is to
**remove the retain at all three sites** — the wrapper adopts the copy. The contrast with the receiver and
async sites is the load-bearing distinction: those read a **borrowed** slot with **no** preceding
`InitializeWithCopy`, so their retain is genuinely required and stays (now isa-dispatching).

**Fixture:** `ClassPayloadEnum.swift` / `GenericPayloadHolder.swift` +
`BindingTests/RuntimeTestsApp/Marshalling/ClassPayloadEnumTests.cs` — `@objc` payload extraction through
all three enum paths, asserting the shared LifetimeTracker live count returns to zero (an over-retain
leaves the payload pinned; verified RED before the removal, GREEN after).

### Bucket 2C — two false positives left untouched (the 8 → 6 narrowing)

Two of the originally-flagged candidate sites (labelled CE1 and SUI1 in the sweep) reach their retain
**only for pure-Swift class payloads** — no `@objc`-rooted type ever flows through them — so native
`swift_retain` is already correct there. Changing them to `UnknownObjectRetain` would be churn with no
behavioural effect, so they were **left untouched**. This is why the sweep narrowed from eight flagged
sites to the corrections above. The two sites are gated by **different** mechanisms, both verified:

- **CE1 — outbound closure class-return** (`ClosureEmitter.IndirectReturn.cs:175`, async-throwing arm
  `ClosureHandler.cs:1084`): gated by a **boolean type predicate**. `ClosureHandler.IsClassType`
  (`ClosureHandler.cs:1837`) returns `Kind == Class && !IsObjCBridged && !IsObjCRooted`, so the
  `isClassReturn`/`Arc.Retain(__ptr)` branch is unreachable for `@objc`-rooted types. The ObjC-bridged
  return takes a *separate* branch (`IndirectReturn.cs:145`, `*(IntPtr*)indirectResult = result.Handle`
  with **no** retain).
- **SUI1 — SwiftUI bridge trampoline class-return** (`SwiftUIBridgeEmitter.cs:3590`): gated by a
  **compile-time member-access** invariant rather than a boolean. `InitAnalyzer.MapParameterType` maps
  *every* class to `BridgeParameterKind.BoundType` (it does **not** exclude ObjC-rooted), but the emitted
  body is `result.Payload.DangerousGetHandle()` — and `.Payload` exists **only** on pure-Swift
  `ISwiftObject` projections. An `@objc:NSObject`-rooted return type exposes `.Handle`, not `.Payload`,
  so such a trampoline would **fail to compile** and the view falls back to template; it can never ship a
  silently-wrong native retain at runtime. (The SwiftUI bridge therefore does not yet *support*
  ObjC-rooted class returns at all — a feature gap, not an ARC defect. If it is ever extended to emit a
  `.Handle`-based ObjC return branch, that new branch must use the `UnknownObjectRetain` family; noted for
  whoever extends the SwiftUI bridge to ObjC-rooted returns — the same return-direction ARC concern that
  [Fix A](#fix-a--optionalobjc-rooted-class-return-over-retain-resolved) addressed for the non-bridge `Optional` return path.)

For completeness the sweep also confirmed the only other emitter site that emits `Arc.Retain` into
generated code — `WrapperEmitter.Marshalling.cs:595` (`EmitArrayOwnershipRetain`) — is a **`SwiftArray`
storage** ownership transfer gated by `IsSwiftArray`, not a class-instance retain, and is correct as-is.

### Verification note — the async-self no-leak test is Dispose-driven, not finalization-driven

`ObjCAsyncSelfTests.TestObjCAsyncSelf_SelfRetainBalancesArc_NoLeak` tears each `self` down with an
explicit `Dispose()` rather than relying on finalization. This is deliberate and necessary, not a
weakening of the assertion:

- Under Mono's conservative GC the final loop iteration's `self` — hoisted into the lingering **async
  state-machine box** — is reliably false-rooted by a stale slot/register copy and never collects,
  surfacing a spurious "1 object not deallocated" straggler **even though the generated ARC is balanced**.
  Confirmed empirically: the straggler survives 15 GC + runloop cycles and disappears the instant
  `Dispose()` force-releases the peer (proving the hold is managed reachability, not a native over-retain).
  The synchronous sibling `ClassPayloadEnumTests` *can* rely on finalization because its stack frame is
  reused and cleared between calls; an async frame's heap-allocated state machine cannot.
- Deterministic `Dispose` preserves the assertion's strictness — an over-retain still leaves an unmatched
  native +1 that `Dispose` cannot drop (`live > 0`); an over-release goes negative or crashes — while
  removing the false root. The sibling `ComputeAsync_WithParams` / `PingAsync_NoParams` tests were switched
  from `GC.KeepAlive` to `Dispose` for the same reason: a deferred finalization there could otherwise let a
  prior test's peer dealloc inside the no-leak test's global LifetimeTracker measurement window.

### Final full-suite gates (post Bucket-2)

| Gate | Result |
|---|---|
| `nuke binding-tests --skip-regen` (sim/Mono), full suite | **2555 pass, 0 fail, 44 skip** (baseline 2535 → 2555, +20 from the new fixtures) |
| `nuke binding-tests --device` (NativeAOT), full suite | **2569 pass, 0 fail, 30 skip** (baseline 2546 → 2569, +23) |
| `nuke test` → `Swift.Bindings.Unit.Tests` | **0 fail** (incl. the updated `EnumHandlerOutputTests` adopt-the-copy assertions + async-emitter unit tests) |
| `nuke validate` (cross-cutting emitter sweep) | _(see commit gate — baseline ≥ prior; version-stamp artifacts reverted, `.validation-baseline.json` kept)_ |

## Fix A — `Optional<@objc-rooted-class>` return over-retain (resolved)

**What it was.** A separate, pre-existing leak in the **return** direction (the primary fix in this PR is the
*receiver* direction). When a function returned `Optional<@objc … : NSObject>`, the emitter marshalled it
inline as:

```csharp
return result == IntPtr.Zero ? null : GetNSObject<ObjCClassParamPayload>(result);
```

`GetNSObject<T>(ptr)` takes an **owning +1** (`owns:false` semantics — it retains rather than adopts), but
the Swift free-function copy-out already handed back an **owned +1**. That first +1 was never balanced, so
each call leaked one NSObject. The value itself was always correct — `TestOptionalObjCPayloadReturnReads`
confirms the read returns sane data and nil→null — it was purely a lifetime leak.

**The fix (missing-adopt).** Of the two candidates originally floated — *mis-route* (push the
`Optional<reference>` return through the `SwiftOptional<T>` / `TypeProjectionFactory` projection) vs
*missing-adopt* (keep the inline path but adopt instead of retain) — the **missing-adopt** choice was
taken. `OptionalProjection.GetReturnPlan` now emits the **adopting** form whenever the inner projection is
one of the three ObjC kinds (`ObjCRootedClassProjection`, `ObjCBridgedProjection`, `ObjCBridgeableProjection`):

```csharp
return result == IntPtr.Zero ? null : GetINativeObject<ObjCClassParamPayload>(result, true);
```

`GetINativeObject<T>(ptr, ownsReference: true)` **adopts** the existing +1 rather than taking a second one,
so Dispose/finalize releases exactly once — net zero. Only the ownership verb flips (`owns:false` retain →
`owns:true` adopt); the inline nullable-`IntPtr` ABI and the nil guard are unchanged. The indirect-result /
out-buffer arm reads the slot through `*(IntPtr*)result` under the same guard and sets `RequiresUnsafe`.

This mirrors the **accessor-getter** path exactly (`OptionalAccessorGetterVisitor` already used `owns:true`)
and is net-parity with the **non-optional** sibling, which balances via the legacy type-record branch
(`WrapperEmitter.Return.cs` — `GetNSObject` then `DangerousRelease`, net +0) once `TryEmitReturnViaProjection`
declines for that case. The fix deliberately did **not** re-route through `SwiftOptional<T>` /
`TypeProjectionFactory`: adopting on the inline path balances ARC without touching the
`IsOptionalObjCBridged` ↔ `TypeProjectionFactory` parity that `.claude/rules/constraints.md` governs, and the
inline `IntPtr` form matches what the accessor-getter already emits. So the original *mis-route-vs-missing-adopt*
question is settled in favour of the smaller, parity-preserving change.

**Coverage.**
- `TestOptionalObjCPayloadReturnNoLeak_KnownFixA` is **un-skipped and active** on sim/Mono **and**
  device/NativeAOT: it loops the `Optional<@objc>` return, disposes each returned peer, and `AssertNoLeaks`
  — RED before the adopt change, GREEN after. (The `KnownFixA` identifier is retained as the stable name for
  this fix; the docstring + assert message reframe it as a regression guard.)
- `TestOptionalObjCPayloadReturnReads` (active) continues to lock in that the functional read is correct.
- Emitter-layer unit coverage pins the *shape* (adopt vs over-retain) for all three ObjC inner projections,
  Direct and IndirectResult, in `ClassObjCRootedTests.cs` (the Optional<ObjC reference> return region) and
  `CompositeProjectionTests.cs`.

**Known coverage edge (not a regression).** The un-skipped runtime no-leak guard exercises the
`@objc : NSObject` inner only; the bridged / bridgeable inners are pinned at the unit layer (shape) but not
yet by an ARC-behaviour *runtime* test, because `LifetimeTracker` counts the test lib's own tracked-ref
instances and cannot count framework peers (`UIImage` / `URL`). A bridged/bridgeable runtime no-leak probe
would need a countable bridged fixture; left as a low-priority follow-up since the three inners share one
emission path that is already shape-pinned.

**Tuple-element note (out of scope, already balanced).** An `Optional<@objc>` returned as a *tuple element*
still flows through the unrolled cdecl-sret path (`WrapperEmitter.Return.cs` tuple-element handling), which
uses the `GetNSObject(nonNull)` + `?.DangerousRelease()` dance — a different code path from this top-level
projection, but **already net +0**. Unchanged by this fix.
