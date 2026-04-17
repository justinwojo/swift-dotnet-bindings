# M11b Recon — Deferred Frameworks Classification

**Date:** 2026-04-17
**Input to:** M11b-finish session
**Scope:** Reproduce each deferred framework's failure once with an
exact failure signature. No fixes applied. Quick wins discovered during
recon are listed at the bottom.

Snapshots regenerated via `nuke regenerate-apple-snapshot --framework <Name>`.
Output at `BindingTests/obj/<Framework>Snapshot/`.

## Summary table

| Framework             | Classification                                              | Blocker layer          | Manifest-addressable | M11b-finish action                                        |
| --------------------- | ----------------------------------------------------------- | ---------------------- | -------------------- | --------------------------------------------------------- |
| CryptoKit             | Clean; architectural dual-emission only                     | —                      | No                   | Decide on single-source for ECDSASignature (see §1)       |
| Translation           | Clean                                                       | —                      | No                   | None                                                      |
| FamilyControls        | Clean; no Swift-only surface beyond ManagedSettings         | —                      | No (already covered) | None                                                      |
| LiveCommunicationKit  | Generator clean; 1 existential fallback                     | —                      | No                   | None — stale doc claim (no `T`/`TT1..3` leak observed)    |
| TipKit                | Swift 6 Sendable compile error + Predicate demangler + 8 AnyTypeFallback on existentials | Swift wrapper compile + demangler + emitter | No (existentials)    | Fix `EveryProtocol.handle` mutability; orthogonal demangler; existentials stay fallback |
| WeatherKit            | 99.1% CPU runaway C# recursion, not ABI-parser loop         | Generator emitter      | No                   | Likely generic-param leak; fix emitter recursion guard    |

## 1. CryptoKit — clean; architectural dual-emission

**Snapshot:** `BindingTests/obj/CryptoKitSnapshot/` (regenerated 2026-04-17)

**Generator result:**
- 0 `AnyTypeFallback`
- 38 `SB0001` warnings — all from existential generic params `D` on
  `IsValidSignature<D>` and `Signature<D>` (e.g. `P256.Signing.PublicKey.isValidSignature<D>`).
  These are unrelated to Swift-only types.
- 85 skip reasons, all orthogonal: `GenericProtocolConstraint`,
  `UnsupportedSignature`, `StaticProtocolMember`,
  `EveryProtocolConformanceSkipped`, `UnsupportedClosure`.

**Fix F "SwiftHandle gap" status:** stale annotation.
The supplement at
`src/Swift.Bindings.Apple/obj/Release/net10.0-macos/AppleTypes/CryptoKit/P256.Signing.ECDSASignature.cs`
emits `ECDSASignature` as `sealed partial class ECDSASignature : ISwiftObject, ISwiftStruct, IDisposable`
with explicit-interface `IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle()`
and VWT-backed `NewFromPayload` / `MarshalToSwift`. Pattern matches
Runtime convention — nothing missing on the supplement side.

**Remaining architectural question (out of scope for M11b-finish):**
CryptoKit's own snapshot also emits parallel `CryptoKit.P256/P384/P521.Signing.ECDSASignature`
classes in `BindingTests/obj/CryptoKitSnapshot/CryptoKit.cs`
(lines 4184, 6381, 8578 — namespace `CryptoKit` vs supplement's `Swift.CryptoKit`).
Dual-emission is an architectural choice about single-sourcing Swift-only
types — not a bug and not blocking ship. Leave for a later pass.

## 2. Translation — clean

**Snapshot:** `BindingTests/obj/TranslationSnapshot/`

- 9 types emitted, 40/43 members, 0 `AnyTypeFallback`, 0 `SB0001`.
- 2 skips: `UnsupportedType` on operator `~=` and `UnsupportedSignature`
  on a placeholder type. Unrelated to Swift-only types.
- `wrapperStrategyCounts`: CdeclProperty 27, CdeclMethod 9,
  CdeclConstructor 4, NativeThunk 3. No fallback paths triggered.

No action.

## 3. FamilyControls — clean; no new manifest entries

**Snapshot:** `BindingTests/obj/FamilyControlsSnapshot/`

Declared Swift types: `FamilyControlsError`, `AuthorizationStatus`,
`FamilyControlsMember` (enums); `FamilyActivityTitleView`,
`FamilyActivityIconView`, `FamilyActivityPicker`, `FamilyActivitySelection`
(structs, 3 are SwiftUI views handled by the bridge generator);
`AuthorizationCenter` (class).

Cross-module references: `ManagedSettings.{Application, WebDomain,
ActivityCategory, ApplicationToken, WebDomainToken, ActivityCategoryToken}`.
Base types (`Application`, `WebDomain`, `ActivityCategory`) are already
in the manifest; `Token<T>` is Runtime-owned generic — not a Swift-only
canonical.

No new manifest entries needed. 0 `SB0001`, 0 `AnyTypeFallback`.

## 4. Foundation.Data.Payload — false positive

`grep -c Payload /Applications/.../Foundation.abi.json` returns 0.
No such Swift type exists in the Foundation ABI. The Appendix A
"Needs investigation" annotation was noise from unrelated uses of
the word "Payload": function names like `reportNewIncomingVoIPPushPayload`
(PushKit) and our own C# convention `public SwiftSafeHandle<T> Payload`.

No action. Remove from Appendix A follow-ups in a future doc cleanup.

## 5. LiveCommunicationKit — stale "generic-param leak" claim

**Snapshot:** `BindingTests/obj/LiveCommunicationKitSnapshot/` (regenerated 2026-04-17 10:46)

Appendix A of `apple-swift-types-architecture.md` states that LCK
leaks emitter generic parameters `T`, `TT1`, `TT2`, `TT3`. Current
snapshot does **not** reproduce this:

- 33/33 types emitted, 101/144 members, 0 skip reasons recorded in
  `binding-emission-report.json` (empty `skipReasons`).
- 0 `AnyTypeFallback`, 0 `SB0001`.
- 1 `UnsupportedSwiftType` in generated C# — existential fallback for
  `any LiveCommunicationKit.ConversationManagerDelegate` (line 3653 of
  `LiveCommunicationKit.cs`). Not a generic-param leak.
- `grep 'TT1\|TT2\|TT3'` on `LiveCommunicationKit.cs` → 0 matches.
- `grep '<T>\|<TT'` on `LiveCommunicationKit.Wrapper.swift` → 0 matches.

Explicit skips (all orthogonal to generic-param leak): 8
`SynthesizedCodable`, 4 `UnsupportedSignature` (placeholder types),
1 `SwiftUIConstraint` (`Foundation.Predicate<...>`),
1 `EveryProtocolConformanceSkipped`.

C# compile against the supplement was not exercised here (requires
`SwiftBindings.Runtime 0.0.0-dev` not in package cache). That will
be picked up by the normal smoke-test + sim-validation pipeline
post-publish.

**Conclusion:** LCK is not currently blocked by the generic-param
emitter bug. Either the bug was fixed incidentally in Sessions 3–7
or it only reproduced against an earlier ABI. No action for M11b-finish
beyond the normal smoke-test coverage.

## 6. TipKit — multi-layer blocker

**Snapshot:** `BindingTests/obj/TipKitSnapshot/` (regenerated 2026-04-17 10:42)

Generator **succeeds at C# emission** (40/41 types, 125/179 members, 12 `SB0001`)
but the post-generate Swift wrapper build fails with exit code 1.
Three distinct issues stacked:

### 6a. Swift 6 language-mode strict Sendable (blocking)

```
TipKit.Wrapper.swift:86:16: warning: stored property 'handle' of
'Sendable'-conforming class 'EveryProtocol' is mutable; this is an
error in the Swift 6 language mode
```

The generated `EveryProtocol` helper class uses a mutable `handle`
stored property; TipKit's dependency train is compiled under Swift 6
strict mode and rejects this. This is a wrapper-emission bug, not
a TipKit specificity — it will bite any framework compiled strict.

**Fix locus:** `WrapperEmitter` / `EveryProtocolWriter`. Either make
`handle` immutable (assign once in init) or drop `Sendable` conformance.

### 6b. Predicate-expression demangler failure (orthogonal)

```
Demangling failed for 'init' ($s6TipKit4TipsO4RuleVyAeC5EventVy_xG_AA0E19PredicateExpression_pSb6Output10Foundation0fG0PRts_XPAK0F11ExpressionsO8VariableVy_AHGXEtcSeRzSERzs8SendableRzlufC):
Error while demangling ... (Parameter 'children')
```

Several `Tips.Rule.init` overloads that take `Foundation.Predicate`
closures fail to demangle. Independent of 6a.

### 6c. Existential-heavy API (by design, not fixable via manifest)

8 `AnyTypeFallback` skips, all on existentials:
`any TipAction`, `any TipRule`, `any TipOption`. These are protocol
existentials, not Swift-only value types — manifest entries cannot
address them. They stay on the existing existential-fallback path.

Also: "Failed to get metadata for struct X" warnings for a long list
of TipKit structs (`Event`, `Donation`, `EmptyDonation`,
`DonationTimeRange`, `DonationLimit`, `Parameter`, `ParameterOption`,
`Rule`, `CompoundOperation`, `RuleBuilder`, `Action`, `ActionBuilder`,
`IgnoresDisplayFrequency`, `MaxDisplayCount`, `MaxDisplayDuration`,
`OptionsBuilder`, `Status`, `InvalidationReason`, `GroupBuilder`,
`ConfigurationOption`, `CloudKitContainer`, `DatastoreLocation`,
`DisplayFrequency`) and classes (`TipUICollectionReusableView`,
`TipUICollectionViewCell`, `TipUIPopoverViewController`, `TipUIView`).
Likely downstream of 6a — metadata accessors needed the wrapper to
compile.

**Ship plan for TipKit:** 6a is the only fix needed to unblock most
of the API surface; 6b affects only Predicate-based initializers and
can stay skipped; 6c is permanent existential fallback.

## 7. WeatherKit — runaway recursion, not ABI-parser loop

**Snapshot:** `BindingTests/obj/WeatherKitSnapshot/` (WeatherKit.abi.json
present at 1.3 MB — swift-api-digester finished; hang is downstream).

**CPU sample:** PID 74958 at 99.1% CPU sustained; `sample 74958 10`
captured to `/tmp/weatherkit-sample-full.txt` (558 KB, 2026-04-17 10:46).

**Stack pattern (stripped to essentials):**

```
main → hostfxr_main → coreclr_execute_assembly →
  Assembly::ExecuteMainMethod → RunMain →
    CallDescrWorkerInternal →
      <~40 frames of JIT'd C# code, all "???" in unknown binary,
       repeating call at 0x10900ad94> →
        RhpNewVariableSizeObject → RhpGcAlloc → AllocateSzArray →
          try_allocate_more_space → trigger_gc_for_alloc →
            WKS::gc_heap::gc1 → mark_phase / plan_phase →
              GCToEEInterface::GcScanRoots → StackWalkFrames ...
```

**Classification evidence:**

1. **Deep C# recursion, not native loop** — ~40 levels of JIT'd frames
   below a single call site (`0x10900ad94` repeats). Recursive generic
   expansion in the emitter or type resolver.
2. **Heavy GC pressure from array allocation** — every iteration hits
   `AllocateSzArray` which triggers GC; that explains the sustained
   99% CPU (CPU alternates between JIT execution and GC mark/sweep).
3. **libclrjit actively compiling** — `Compiler::impImportBlockCode`,
   `SsaBuilder::InsertPhiFunctions`, `LinearScan::allocateReg` all
   appearing. New generic instantiations being JIT-compiled in the
   hot loop. Consistent with Appendix A's "generic-param leak"
   hypothesis: each recursion produces a new closed-over generic
   method.
4. **Not an ABI-parser loop** — `swift-api-digester` completed
   (WeatherKit.abi.json on disk). Hang is in the generator proper,
   downstream of the ABI-JSON parse.
5. **Generator binary symbols absent from sample** — all hot frames
   show `???` (the generator is built with stripped symbols).
   To get line-level resolution, rebuild the generator in Debug
   with `-p:DebugSymbols=true` and re-sample in M11b-finish.

**Classification:** runaway generic-resolution recursion in the
emitter, with unbounded new type-instantiation allocations feeding
GC pressure. Consistent with the Appendix A description of the same
bug family as LCK (now resolved). Fix locus almost certainly in
`TypeOwnerRegistry` or `WrapperEmitter` generic resolution — add a
recursion-depth bound and/or a visited-set guard.

macOS snapshot (`BindingTests/obj/WeatherKitSnapshot-macOS/`) generated
fully with only 6 `AnyTypeFallback`, suggesting the infinite loop is
triggered by a type that's iOS-specific (or by an ordering difference
in the iOS ABI JSON).

## Quick-win discovered: include-types.json gap

`Foundation.DateComponents` and `Foundation.PersonNameComponents`
were added to `manifest.json` in Session 7 but never added to
`include-types.json`. On the next `regenerate.sh` run, those entries
would have been **wiped** (the regen filter is a positive-list intersection).

Fixed in this session by appending both identities to
`src/Swift.Bindings.Sdk/tools/apple-types-manifest/include-types.json`.

## Handoff to M11b-finish

Execute in this order:

1. **WeatherKit** — rebuild generator with debug symbols, re-sample,
   identify the recursive call site, add recursion/visited guard.
   Re-run snapshot to confirm 0% CPU on completion.
2. **TipKit 6a** — make `EveryProtocol.handle` immutable or drop
   `Sendable`. Regenerate TipKit snapshot; confirm wrapper compiles.
3. **CryptoKit architectural dual-emission** — decide whether the
   module snapshot should suppress its own emission of supplement-owned
   types. Not required for ship.
4. **TipKit 6b** — defer. Predicate-init demangler is orthogonal and
   niche.

The LCK, CryptoKit, Translation, FamilyControls, and
`Foundation.Data.Payload` items are resolved and require no further
action in M11b-finish.
