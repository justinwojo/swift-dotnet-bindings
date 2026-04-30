# Replacing the Managed Demangler — Investigation

**Outcome**: NO-GO. The managed demangler stays.

This document records a one-session investigation into replacing the ~5,800 LOC managed Swift demangler (`Swift5Demangler.cs`, `Swift5Reducer.cs`, `DemanglingResults.cs` and ancillary types) with a producer driven by `swift-symbolgraph-extract` plus a small forward suffix mangler. The investigation was the kill-gate for "M3" in `architecture-gameplan-v2.md`; it failed decisively, and the gameplan's M3 milestone closed without a code change.

The summary: forward-constructing `Mc` (protocol conformance descriptor) and `WP` (witness pattern) symbols from symbol-graph `conformsTo` edges hits ~61% / ~33% of TBD-exported symbols under an A–Z word-substitution heuristic, vs the ≥99% required to skip implementing Swift mangling substitutions. Closing the gap requires ~335 LOC of stateful forward-mangling logic — a state machine, not a rule list — which is the territory the redesign was specifically trying to avoid.

A "tuple-based redesign" that drops the symbol string entirely is **not** a smaller variant of the same idea: today's emitted bindings call `ProtocolConformanceDescriptor.LoadFromSymbol(libPath, symbolName)` at runtime via `dlsym`, so any producer that doesn't reproduce the mangled string still owes the toolchain *some* way to recover it. Both candidate paths to do so are themselves separately-scoped changes (see §6 / §8) and are not pursued.

The investigation is preserved here rather than discarded because the conclusion is non-obvious from the code alone — anyone who looks at the documented `swift-symbolgraph-extract` output would reasonably ask "why didn't they just use this?" This document is the answer. The recipe in §9 is enough to re-derive the same conclusions in an afternoon if the question is ever revisited.

---

## 1. Question

Can symbol-graph `conformsTo` relationships deterministically recover every `Mc` and `WP` symbol present in BindingTests + a sample of validation library TBDs, **without** implementing Swift mangling substitutions?

**Pass condition**: ≥99% byte-equal hit rate WITH a substitution rule set bounded at ≤50 LOC.

## 2. Method

1. **Symbol-graph extraction.** `swift-symbolgraph-extract` invoked once per module (target `arm64-apple-ios15.0-simulator`, `-minimum-access-level public`). Tool location: `xcrun --find swift-symbolgraph-extract`.
2. **TBD enumeration.** For each module, exported symbols collected via `nm -gU <binary>` (or, for SDK frameworks shipped only as TBDs, by grepping the `_$s…Mc` / `_$s…WP` pattern out of the SDK's TBD).
3. **Forward construction.** For each `conformsTo` edge, four cumulative strategies were measured:
   - **S0 naive** — `_$s + sourceMangled + targetMangled + module + suffix`, no substitutions.
   - **S1 +mod-subs** — module substitution table (`AA`, `AB`, …).
   - **S2 +strip-P** — strip the trailing `P` kind suffix from non-stdlib protocol target bodies (Mc/WP encode the protocol name without its `P` kind marker).
   - **S3 +word-subs** — A–Z compound-identifier word substitution heuristic (`0…<sub>0` form). Lowercase substitution letters (subs ≥26) and shared name/module substitution-state are deliberately not implemented; §5 measures the impact.
4. **Tuple-based fallback** — measured separately: filter conformsTo edges to drop documented auto-conformance protocols, then compare edge count to TBD Mc count.

Harness was a small Python script that loaded each module's symbols.json, walked `conformsTo` edges, applied the four strategies, and intersected the produced candidates with the TBD-exported `_$s…(Mc|WP)` set. Reproducing it from §9 is straightforward.

## 3. Corpus

| Module | conformsTo edges | TBD Mc | TBD WP | Notes |
|---|---:|---:|---:|---|
| SwiftBindingsTestLib | 651 | 300 | 75 | Our test corpus. Cross-module conformances to Foundation, SwiftUI, Swift stdlib. |
| Alamofire | 504 | 137 | 63 | Heavy stdlib (`Codable`, `Equatable`, `Sendable`) usage; retroactive Foundation conformances (`URLRequest : URLConvertible`). |
| Lottie | 459 | 144 | 36 | UIKit / Foundation / Swift cross-module; nested types in `DotLottie`. |
| DeviceKit | 36 | 20 | 0 | Small enum-only library; serves as a simple lower bound. |
| CryptoKit | 481 | 166 | 71 | Apple framework; deeply nested types (`Curve25519.KeyAgreement.PublicKey`); HPKE protocols; SecureEnclave. |
| **Total** | **2,131** | **767** | **245** | 1,012 Mc/WP samples across 5 modules. |

Samples include same-module conformances (most), cross-module conformances declared *in* the module on foreign types (Alamofire's `Foundation.URLRequest : URLConvertible`), nested-type conformances (CryptoKit's `Curve25519.KeyAgreement.PublicKey : HPKEDiffieHellmanPublicKey`), and conformances on generic types (Alamofire's `DataResponse<T,U> : CustomStringConvertible`).

## 4. Per-module hit rate

```
                       S0 naive     S1 mod-subs    S2 strip-P    S3 word-subs
SwiftBindingsTestLib   Mc   0.0%    Mc  46.7%      Mc  60.0%     Mc  63.7%
                       WP   0.0%    WP   0.0%      WP  38.7%     WP  50.7%
Alamofire              Mc   0.0%    Mc  29.2%      Mc  51.1%     Mc  56.2%
                       WP   0.0%    WP   0.0%      WP  25.4%     WP  38.1%
Lottie                 Mc   0.0%    Mc  56.9%      Mc  70.1%     Mc  70.1%
                       WP   0.0%    WP   0.0%      WP  33.3%     WP  33.3%
DeviceKit              Mc   0.0%    Mc  70.0%      Mc 100.0%     Mc 100.0%
                       WP   0.0%    WP   0.0%      WP   0.0%     WP   0.0%   (no WP in TBD)
CryptoKit              Mc   0.0%    Mc  25.3%      Mc  47.0%     Mc  47.0%
                       WP   0.0%    WP   0.0%      WP  11.3%     WP  11.3%

OVERALL (S3)           Mc 467/767  (60.89%)
                       WP  82/245  (33.47%)
```

Best measured strategy (S3 — A–Z word-substitution heuristic active) hits **60.89% Mc, 33.47% WP**. Required: ≥99%. **Gap: 38–66 percentage points.** This is not a true upper bound: extending S3 to the full Swift mangling grammar (lowercase subs, shared name/module state) would raise the rate, but doing so is the very thing the kill-gate forbids and §5 quantifies.

DeviceKit hits 100% Mc only because its conformances are uniformly enum-conforms-to-stdlib-protocol-by-standard-substitution and have no inner-word substitution surface to stress. It is the easy baseline.

## 5. Gap analysis

S3 misses **463 symbols total** (300 Mc + 163 WP). The categorisation below was run over a sampled subset — the harness truncated per-module miss dumps to the first 50 Mc and first 50 WP, so 343 of the 463 misses were classified directly; the remaining 120 were not classified by this pass. Because the *classified* set already exceeds the 50 LOC budget on its own, the unclassified remainder cannot affect the kill-gate result. Every category requires its own piece of Swift mangling grammar; some individual categories fit the 50 LOC budget, but the combined set does not, and they compose against shared substitution state.

The categorisation came from a regex pass over the dumped miss set:

| Category | Count | LOC estimate | Notes |
|---|---:|---:|---|
| Cross-module retroactive conformance (`<src><dst-mod><dst-name>AD…AB…` substitution chain) | 166 | ~120 | Module substitution table interacts with name-substitution table; the conformance module emits as `AD` because two prior modules (source's, target's) are already in the table. Forward-construction has to walk a multi-module sub state. |
| Cross-module name with word substitution (e.g. `0A2UI0F0` for `SwiftUI.View` after `SwiftUI` is partially substituted) | 75 | ~80 | Same as compound-identifier word subs, but the *module* of the protocol is also substituted, and the substitution table is shared between modules and identifier names. |
| Other (multi-cause; commonly extension/inheritance combined with generics) | 43 | n/a | Overlapping categories. Most still need word-sub + module-sub coordination. |
| Lowercase sub-letter (`0bC` style — substitutions ≥26 use lowercase) | 25 | ~15 | The substitution alphabet extends past 'Z' into 'a'-'z'. Forward construction for any word-table of size > 26. Trivial individually but adds to the surface. |
| Stdlib protocol with word-substituted name (`s5ErrorP` USR → `s0F0` mangled) | 17 | ~30 | When a stdlib protocol's name (e.g. `Error`) is already a word in the source's word table, the protocol reference must be re-mangled `s0<sub>0` instead of using the canonical USR form. The symbol graph produces the canonical form; the TBD has the substituted form. Requires applying word-subs through stdlib bodies. |
| Nested type chains (`Curve25519O12KeyAgreementO06PublicD0V`) | 12 | ~50 | Nested types use length-prefix segments separated by kind markers (`O`/`V`/`C`). Each segment participates in word substitution. CryptoKit-heavy. |
| Generic parameter splicing (`Vyxq_G` for `DataResponse<T,U>`) | 5 | ~40 | Symbol-graph USRs strip generic parameters; TBD encodes them. Forward construction has to look up the type's generic signature and emit `yxq_G`-style placeholders. Alamofire-heavy. |

The smallest single category alone fits in 50 LOC; the full set does not. Composed, they reach **~335 LOC of forward mangling logic** before any error handling or interaction tests — and that's a floor, since each category has known edge cases (associated types, opaque returns, primary-associated-types) we did not exercise in this corpus.

The composability is the core problem. Each rule is small in isolation, but the rules **share state** (the substitution table is global across module name, type name, protocol name, and conformance module within a single mangled symbol). This is exactly the demangler grammar in reverse: not a rule set, but a state machine.

### A second, separate over-prediction problem

S3 emits **3,365 candidate symbols across 5 modules that are not present in the TBD** (vs 549 hits and 1,012 actual Mc/WP). The over-prediction comes from:

- Auto-conformance protocols (`Sendable`, `SendableMetatype`, `Copyable`, `Escapable`, `BitwiseCopyable`) — checked at compile time, never emitted as runtime Mc/WP. The current spike harness emitted `Mc` and `WP` candidates for every conformsTo edge regardless. **Filtering these is bounded** (5 specific protocol USRs, ~5 LOC).
- WP-not-emitted-for-stdlib-substitutions — stdlib protocol conformances emit `Mc` only, not `WP`. Detectable from the target USR's stdlib-substitution form (target body that is not length-prefixed).
- ObjC-target conformances (`c:objc(pl)NSObject`) — emit ObjC runtime metadata, not Swift Mc/WP.

These are fixable with a known filter. They do not, on their own, decide pass/fail.

## 6. Tuple-based coverage check (alternative)

The TBD-parsing layer surfaces `(ImplementingType, ProtocolType, Module)` tuples through `ProtocolConformanceDescriptorReduction`, but the **emitted bindings consume the mangled symbol string directly at runtime** — `ProtocolConformanceDescriptor.LoadFromSymbol(libPath, symbolName)` does a `dlsym` against the dylib (`Emitter/StringEmitter/Handler/ClassHandler.cs:973`, populated via `TypeHandlerHelpers.cs:1027` from `Model/TypeDecl/ProtocolConformance.cs:15` whose `ProtocolConformanceDescriptor` field comes from `Demangler/DemanglingResults.cs:152`). The symbol string is therefore load-bearing, not a debug artefact. A tuple-only producer would still owe the rest of the toolchain a way to *get* that exported symbol back.

Two candidate paths exist for keeping the symbol-string contract while sourcing facts from the symbol graph:

- **Map tuples → exported symbol via the TBD itself.** Index the TBD's `_$s…(Mc|WP)` symbols by their demangled `(implementingType, protocol)` pair, then look them up by tuple at production time. This still needs a demangler — i.e. the very component M3 was meant to retire.
- **Drop the runtime `dlsym` requirement.** Migrate `LoadFromSymbol` consumers to a runtime conformance-resolution path (Swift runtime's conformance lookup, or descriptor pointers gathered another way). This is a separately-scoped change to the runtime + emitter, not a smaller continuation of L.

So before proceeding to the count check below, the tuple-fallback path is contingent on one of those two preconditions being solved. The numerical check is included for completeness, since it bounds the degree of conformsTo-vs-Mc divergence regardless of which downstream design is picked.

If we can show the symbol-graph `conformsTo` set (after the bounded auto-conformance filter) maps 1:1 to the TBD's Mc set, that *bounds* the divergence; it does not on its own retire forward mangling.

After dropping `Sendable`, `SendableMetatype`, `Copyable`, `Escapable`, `BitwiseCopyable`, and ObjC-targeted conformances:

| Module | conformsTo (filtered) | TBD Mc | Ratio |
|---|---:|---:|---:|
| SwiftBindingsTestLib | 350 | 300 | 1.17 |
| Alamofire | 177 | 137 | 1.29 |
| Lottie | 221 | 144 | 1.53 |
| DeviceKit | 25 | 20 | 1.25 |
| CryptoKit | 159 | 166 | **0.96** |
| **Total** | **932** | **767** | 1.22 |

Two distinct issues remain even on the tuple-based path:

1. **Over-coverage** in 4 of 5 modules — there are conformsTo edges in the symbol graph whose Mc symbol is *not* exported. Likely causes: conformances on internal types declared `public`-by-extension but inlined; conformances synthesized from inheritance not separately emitted; default implementations. Each cause needs its own filter. Without writing a demangler, we cannot tell which.
2. **Under-coverage** for CryptoKit (159 < 166) — there are Mc symbols whose source/target USR pair is *not* present in `conformsTo`. Likely causes: inherited conformances (the compiler emits Mc for both the directly-conforming type and any superprotocols / supertypes in the chain); generic specialisation Mc symbols; conformances synthesised from associated-type witnesses (`requirementOf`).

These are *also* unbounded. They are not the same unbounded problems as the forward-mangling ones, but they show the symbol-graph `conformsTo` set is not a clean 1:1 image of the TBD Mc set in either direction. Closing those gaps would itself require a multi-session investigation, distinct from this spike.

## 7. Verdict

**FAIL the kill-gate** as specified.

- Forward-mangling hit rate of **60.89% / 33.47%** is far below the 99% bar.
- Substitution rule set required to close the gap is **~335 LOC and stateful** — not the bounded ≤50 LOC the gate calls for.
- The forward-mangling problem is structurally a state machine, not a rule list. Every category of miss is a piece of Swift's mangling grammar interacting with the substitution table. This is the territory the M3 redesign was specifically trying to avoid (option E was rejected for the same reason).

## 8. Recommendation

**No path forward under the constraints that motivated this investigation.** The forward-mangling approach does not retire the managed demangler within an AI-maintainable surface area, and no smaller variant exists:

- A **tuple-based redesign** that drops the symbol string is not a smaller continuation of this approach — it is the same identity problem under a new name plus a missing symbol-loading story. Today's emitted bindings call `ProtocolConformanceDescriptor.LoadFromSymbol(libPath, symbolName)` at runtime (see §6), so any producer that doesn't reproduce the mangled string still owes the toolchain *some* way to recover it. The candidate paths to do so are not bounded:
  - Indexing the TBD by tuple still requires a demangler — the component this investigation was meant to retire.
  - Migrating `LoadFromSymbol` callers to a different runtime conformance-resolution path is a separately-scoped runtime/emitter change with its own design surface, motivated by a different problem (runtime ABI evolution, not demangler maintenance).
  - Adopting an external authority (`xcrun swift-demangle --tree-only` at generation time, Swift runtime conformance lookup, compiler-emitted index files) is feasible but is a different architecture, not a smaller version of this idea.

- **Corpus drift monitoring** (running the managed port against `xcrun swift-demangle` in CI) was held in reserve as a consolation deliverable. On reflection it is also not worth scheduling: drift in the managed demangler already manifests as test failures via `LoadFromSymbol` returning null at runtime, which is gated by `nuke validate` and BindingTests. A unit-level corpus check would add a parallel gate against the same drift the integration gates already catch.

**Action**: keep the managed demangler. Do not file the demangler swap in `Future/post-1.0-architecture-roadmap.md` — there is no near-term motivation that revives it under the same shape. If a future change to the runtime symbol-loading contract (e.g. a redesigned `LoadFromSymbol` mechanism) ever lands for independent reasons, the demangler retirement may fall out as a side effect of *that* work, but it should not be tracked as its own initiative now.

## 9. Reproduction

If this question is ever revisited, the recipe is:

```bash
# 1. Locate toolchain binary
xcrun --find swift-symbolgraph-extract

# 2. Extract symbol graphs (one per module)
mkdir -p symbolgraphs/<Module>
xcrun swift-symbolgraph-extract \
  -module-name <Module> \
  -target arm64-apple-ios15.0-simulator \
  -F <path-to-frameworks-dir> \
  -sdk "$(xcrun --sdk iphonesimulator --show-sdk-path)" \
  -minimum-access-level public \
  -output-dir symbolgraphs/<Module>

# 3. Capture exported symbols (TBD or nm -gU)
nm -gU <path-to-binary> | awk '{print $NF}' > <Module>.symbols
# or, for an SDK-only framework:
grep -oE '_\$s[A-Za-z0-9_]+' <SDK>/<Module>.framework/<Module>.tbd \
  | sort -u > <Module>.symbols

# 4. Walk conformsTo edges in symbols.json, forward-construct candidates per
#    the four strategies in §2, and intersect against the TBD-exported set.
```

Modules used in the original investigation: SwiftBindingsTestLib, Alamofire, Lottie, DeviceKit, CryptoKit (chosen for spread across same-module / cross-module / nested-type / generic-type / Apple-framework conformance shapes).

Toolchain at time of investigation: Xcode 26.x default, Apple Swift 6.2.3, iPhone Simulator SDK 26.2, on macOS 25.4 (Darwin), arm64.
