# Regression Audit R2 — Type-Grammar + Availability-Key Consolidation (Session 10)

**Scope:** Regression hunt over commits `cb1ff96d` (centralize the Swift type-string grammar around `TypeSpecParser`) and `0ae1d556` (consolidate the availability-key grammar into the shared type-spec lib). Targets: `TypeSpecParser.Parse`/`ParsePrefix`, `TypeSpecParseException`, the shared `SwiftTypeListText` splitter, the 26 migrated grammar call sites, the `MemberSignatureNormalizer` relocation, and the cross-producer (C#↔Swift) availability-key parity corpus.

**Overall risk: 3 / 5 (confidence: high).** The consolidation is structurally sound and the two probe-confirmed defects are both real, narrow-blast-radius declaration drops keyed on toolchain-specific ABI shapes. There is no key-corruption or wrong-marshalling finding — the parity machinery holds by construction today. The risk is concentrated in one fragile primitive: the EOF-strict `Parse` is wired into several `CreateTypeSpec`/emitter sites with no local `try`/`catch`, so any future toolchain that surfaces an un-consumed leading modifier silently drops the enclosing declaration with no red test.

**Confirmed findings: 2, both REGRESSIONS** (introduced by `cb1ff96d`, not pre-existing). Both are P1 declaration-drop regressions rooted at `SwiftABIParser.cs:3494` and the now-stale guarantee comment at `SwiftABIParser.cs:3465-3468`. Inconclusive: 2 (mechanism real, real-world trigger not demonstrated on today's ABI shape). Deferred-unverified candidates: 12. Refuted: 2.

---

## Confirmed findings

| file:line | severity | regression? | claim | what the probe showed |
|---|---|---|---|---|
| `src/Swift.Bindings/src/Parser/SwiftABIParser.cs:3494` (comment `:3465-3468`) | P1 | **Yes** | EOF-strict `Parse` turns the documented "broken-but-present `NamedTypeSpec("some")`" fallthrough into a dropped declaration; the stale comment now lies | `Parse("some Mod.P")` THROWS `TypeSpecParseException`; `ParsePrefix` returns `NamedTypeSpec("some")` (old behavior). Direct `CreateTypeSpec` reflection probe with `_opaqueParamCapture==null` THROWS → propagates to `HandleNode` catch-all (`:1093`) → whole decl dropped. Real trigger is a **subscript index param** `some P` (verified live: `subscript(shape: some Shape)` → digester node `GenericTypeParam`/`"some Lib5.Shape"`, capture never installed by `CreateSubscriptDecl`/`HandleSubscriptAccessors`). Claim's enumerated positions (property/enum/tuple/generic-arg) were refuted — those encode as `OpaqueTypeArchetype` (caught at `:3445`) or are illegal Swift. |
| `src/Swift.Bindings/src/Parser/SwiftABIParser.cs:3494` (also `:3534`) | P1 | **Yes** | EOF-strict `Parse` rejects ownership-modifier / `@convention`-prefixed printedNames at the unwrapped `CreateTypeSpec` sites, dropping declarations the old parser tolerated | `Parse("__owned …"/"borrowing …"/"consuming …"/"@convention(c) …"/"sending …")` all THROW; `ParsePrefix` returns the degraded leading spec. **Real end-to-end repro:** `public var producer: () -> sending Box` compiled with the live toolchain yields digester printedName `() -> sending Lib.Box`; the real generator dropped BOTH `producer` and `Holder.init` with `Unexpected trailing token 'Lib.Box'`. The claim's `__owned`/`@convention(c)` examples were refuted as not-emitted-by-digester (ownership lives in a separate `paramValueOwnership` field; `@convention` is stripped to plain `(Int) -> ()`). The only modifier the digester actually surfaces in printedName is Swift 6 **`sending`** — present in 3 corpus libs. |

### Finding 1 — `some`-typed subscript index params are dropped (regression)

`cb1ff96d` made `TypeSpecParser.Parse` EOF-strict: after a complete top-level type, a trailing token now throws `TypeSpecParseException` (`TypeSpecParser.cs:434-440`). The lenient pre-commit behavior survives only under `ParsePrefix` (`TypeSpecParser.cs:450`). The parser special-cases `inout`/`any` as prefixes but **not** `some`, so `"some Mod.P"` parses `some` as a bare name and rejects `Mod.P` as trailing garbage.

The unwrapped call at `SwiftABIParser.cs:3494` (`var spec = TypeSpecParser.Parse(node.PrintedName);`) has no local `try`/`catch`. The `some `-divert just above it (`:3469-3474`) only fires when `_opaqueParamCapture != null` — i.e. inside `CreateMethodDecl`'s param loop (`:2560-2561`, the sole install site). A subscript index parameter typed `some P` is legal Swift and `swift-api-digester -dump-sdk` encodes it as `GenericTypeParam`/`"some Lib5.Shape"`. `CreateSubscriptDecl` (`:3088`, calls `CreateTypeSpec` at `:3111`) and `HandleSubscriptAccessors` (`:3262`) never install `_opaqueParamCapture`, so the node reaches `:3494` with capture `null` → throws → `HandleNode`'s catch-all (`:1093-1097`) sets `droppedWithError` and the **entire subscript vanishes**. Before `cb1ff96d` the lenient `Parse` returned `NamedTypeSpec("some")` and the subscript emitted.

The in-code comment at `SwiftABIParser.cs:3465-3468` is now factually false: it promises fall-through "will still produce a broken `NamedTypeSpec("some")` … no worse than pre-fix behavior" — it no longer produces that, it throws and the decl disappears.

**Ingestion-path caveat (blast radius):** `swift-frontend -emit-abi-descriptor-path` (used for third-party `.xcframework`s, `XCFrameworkResolver.cs:1337-1343`) desugars opaque params to `τ_0_0` and does **not** trigger the throw. `swift-api-digester -dump-sdk` (Apple-framework SDK-direct mode, `Sdk.targets` Target 2c, `:705-713`) emits the `"some …"` form that does. So in practice this bites an **Apple Swift framework with a `some P` subscript parameter consumed via SDK-direct mode**; third-party xcframework bindings are unaffected by this specific path.

### Finding 2 — `sending`-bearing closure types drop their declaration (regression)

Same root cause, different surface. The unwrapped sites `SwiftABIParser.cs:3494` (`kNominal`/`kFunc`) and `:3534` (`kGenericTypeParam`) throw on any printedName carrying a leading modifier the grammar doesn't consume. The param loop is wrapped only in `try`/`finally` (no `catch`, `:2562-2604`), so an uncaught throw reaches `HandleNode`'s catch-all (`:1093`) and drops the declaration.

The verified real-world trigger is the Swift 6 `sending` modifier inside a closure-bearing type. A purpose-built `public var producer: () -> sending Box` produced digester printedName `() -> sending Lib.Box`; running the real generator end-to-end dropped both `producer` and the enclosing `Holder.init` with `Error while processing node 'producer (...)': Unexpected trailing token 'Lib.Box' after a complete type`. Pre-`cb1ff96d`, `ParsePrefix` returned a non-throwing degraded `ClosureTypeSpec` and the member emitted. `sending` appears in 3 corpus libs' ABI JSON (Alamofire, Firebase/Firestore, a Firebase backup), e.g. `(...) -> sending Any?` and `(inout sending τ_0_0) -> sending τ_1_0`.

The claim's broader example set (`__owned`/`__shared`/`consuming`/`borrowing`/`@convention(c)`) was refuted against real digester output: across 831 corpus `abi.json` files, **zero** printedNames carry a leading ownership keyword (ownership is encoded in a separate `paramValueOwnership` field, read at `SwiftABIParser.cs:2579`), and `@convention(c)` is stripped to plain `(Int) -> ()` in the printedName. Blast radius is narrow today (`sending` is uncommon and the one corpus instance is filtered earlier as an ObjC-extension method) but grows with Swift 6 concurrency adoption.

---

## Inconclusive / needs deeper probe

Both items below confirm the **mechanism + stale comment** but could not demonstrate a dropped declaration on the current (Xcode 26.3) toolchain's ABI shape — the throw is latent, guarded only by digester encoding conventions that could shift across toolchains.

| file:line | severity | claim | status |
|---|---|---|---|
| `src/Swift.Bindings/src/Parser/SwiftABIParser.cs:3494` | P1 | EOF-strict `Parse` at `kNominal` throws on ownership/opaque-modified printedNames the old lenient parser tolerated, dropping the whole decl | **Inconclusive.** Parser divergence + stale comment CONFIRMED (`some SwiftUI.View`, `borrowing Foundation.Data`, `consuming X`, `__owned X`, `isolated X`, `some P & Q` all throw vs `ParsePrefix` returning the prefix). Reachability REFUTED on real output: the cited fixture `Existentials.swift:52 opaqueItem: some Describable` currently emits (its node is `OpaqueTypeArchetype`, caught at `:3445`); ownership/`isolated`/`sending` live in side fields or canonicalize to `τ_0_0`. 56/56 leading-modifier nodes in the real testlib ABI are `OpaqueTypeArchetype`. No member dropped today — latent, not live. |
| `src/Swift.Bindings/src/Parser/SwiftABIParser.cs:3591` | P1 | `CreateProtocolCompositionTypeSpec`'s printedName-`&`-split fallback (the comment-documented common no-children case) calls EOF-strict `Parse` with no `try`/`catch`; a trailing-token part drops the whole decl | **Inconclusive.** Structural exposure CONFIRMED (all 16 probed `ProtocolComposition` nodes had zero children → `:3591` is the live path; a thrown exception provably reaches `HandleNode` `:1093`; asymmetric with the enum/typed-throws siblings `cb1ff96d` wrapped). Trigger REFUTED: across 15 real composition shapes, `swift-api-digester` emitted only clean ` & `-separated EOF-valid specs; the hypothetical `where`-clause input is never produced for a composition node (it lands on the generic-signature node instead). Latent hardening gap, no demonstrated input. |

---

## Deferred (candidate, unverified)

Real candidates past the per-track verification cap — each has a concrete probe idea but no decisive probe was run. They cluster into three families.

**Family A — more un-`try`/`catch`'d EOF-strict `Parse` sites (same root cause as Findings 1/2):**

| file:line | sev | claim |
|---|---|---|
| `SwiftABIParser.cs:3570` (also `:3534`, `:3591`, `:3615`) | P2 | Four sibling `Parse` sites with no `try`/`catch`; `as NamedTypeSpec` + null-check protects a NULL result but NOT a THROW, so a trailing-token child/part printedName drops the enclosing decl instead of losing one protocol. |
| `Emitter/.../ForeignTypeExtensionEmitter.cs:1181` (param) / `:289` (property) | P2 | Param/property type slices from raw swiftinterface text feed strict `Parse` inside `try { } catch { return null; }`; an un-stripped ownership/`some`/`where` tail now drops the member where lenient `Parse` emitted it. Sibling return-type slices correctly use `ParsePrefix`. |
| `Emitter/.../ForeignTypeExtensionEmitter.cs:289` (property, named explicitly) | P2 | Property-site sibling of `:1181`; `StripSwiftAttributes` (`:1198-1210`) strips only `@`/`inout`, leaving `some`/`borrowing`/`__owned` to throw at `:289` and drop the property. |
| `Emitter/.../ProtocolExtensionEmitter.cs:646` | P2 | `ParseParameter` strips `@`/`inout`/defaults but not `borrowing`/`consuming`/`sending`/`__owned`/`__shared`/`some`; strict `Parse` flips "emit-then-prune-with-reason" to "drop-at-parse". |
| `Marshaler/ConcreteSpecializationEngine.cs:1375` | P2 | `NormalizeTypeForComparison` round-trips via `Parse(raw)?.ToString(true)` with raw fallback on throw; a same-type RHS that previously canonicalized from its prefix now throws → raw fallback → mismatch → CSM declines a specialization it previously emitted. |
| `TypeSpecParser.cs:426` | P2 | Root primitive: `Parse("some X")` throws but `Parse("any X")` succeeds — every un-stripped opaque/existential site drops `some`-typed members while keeping `any`-typed ones, an asymmetry that didn't exist under lenient `Parse`. |
| `ProtocolExtensionEmitter.cs:631` | P2 (not regression) | `FindDefaultValueStart` detects `=` only when space-preceded; `Int=5` / `Int = 5` reach `Parse`, whose tokenizer rejects `=` as an illegal char → member dropped on non-canonical spacing. Pre-existing tokenizer behavior amplified by EOF-strict. |

**Family B — `SwiftTypeListText` shared-splitter behavior changes:**

| file:line | sev | claim |
|---|---|---|
| `Parser/SwiftTypeListText.cs:85` | P2 | `SplitTopLevelParameters`' arrow guard (`'>' && prev=='-'`) suppresses the depth decrement for ANY `>` preceded by `-`, not just closure arrows; also a real behavior flip for `SwiftInterfaceContextTracker`'s subscript-label synthesis (the replaced clone lacked the guard), unpinned by any test. |
| `Parser/SwiftTypeListText.cs:85` | P2 (not regression) | No `if (depth > 0)` floor on the `>`/`)`/`]` decrement; a comparison `>` in an un-stripped default (`x: Int = (a > b), y: Bool`) drives depth negative and merges params. `ExtractParamTypesFromSwiftClause` strips the default AFTER splitting (`:323`), so the `>` reaches the splitter. |
| `Parser/SwiftTypeListText.cs:106` | P2 | `IndexOfTopLevelArrow` returns `-1` on an unbalanced/truncated slice where the old `IndexOf("->")` found the arrow; the three migrated arrow scans (`ProtocolExtensionEmitter:566,1247`, `ForeignTypeExtensionEmitter:1116`) now mis-slice the return region. |

**Family C — `MemberSignatureNormalizer` C#↔Swift mirror / sugar-convergence gaps:**

| file:line | sev | claim |
|---|---|---|
| `MemberSignatureNormalizer.cs:133` | P2 (not regression) | C# strips trailing `...`, the Swift mirror doesn't and collapses a `...`-bearing input to the EMPTY string via the last-dot rule — maximally destructive desync IF the invariant (`param.type` never carries `...`) is ever violated. |
| `MemberSignatureNormalizer.cs:168` / `AvailabilityWalker.swift:918` | P2 | Trailing-dot + space-before-ellipsis divergence: C# guards `lastDot+1 < length` and strips `...`; Swift mirror has neither, so `Foo.` → `Foo.` (C#) vs `""` (Swift) and `Int ...` → `Int` vs `""`. |
| `MemberSignatureNormalizer.cs:196` | P2 (not regression) | `CanonicalizeCollectionSugar` bails on any closure-element collection, so `[() -> Int]` and `Array<() -> Int>` never converge — interface-producer-vs-ABI-consumer key detach for closure-element-array overloads. Shared identically by both mirrors. |

---

## Checked & refuted

| file:line | claim | why refuted |
|---|---|---|
| `Emitter/.../ForeignTypeExtensionEmitter.cs:289` | Foreign-extension property with `some`/ownership type is silently dropped under EOF-strict `Parse` (a member previously present) | Mechanics true, **consequence false**. The old lenient `Parse` returned `NamedTypeSpec("some")`, but the very next gate `ClassifyReturnType` (`ExtensionMarshallingHelper.cs:46`) returns null for it (`IsSwiftPrimitive("some")==false`, no `TypeRecord`) → property dropped at `:305` under the OLD parse too. Both versions drop it; only the drop site + log text differ. A code-hygiene gap (property path should mirror the method path's `ParsePrefix`), zero output impact. |
| `tools/SwiftInterfaceParser/.../AvailabilityWalker.swift:917` | Swift mirror `normalizeParamType` mangles `Foo...`/`[Foo]...` to an empty key (no `...` strip) → C#↔Swift parity detach | Pure-function divergence true, **exploitability false**. The variadic ellipsis is a separate `ellipsis` token on `FunctionParameterSyntax`, structurally outside `type: TypeSyntax`, so `param.type.trimmedDescription` NEVER carries `...`. All 7 variadic shapes (incl. the finder's own closure-typed adversarial case `((Int)->Void)...`) CONVERGE against pinned swift-syntax 601.0.1. The C# strip exists for the ABI-consumer path whose printedName DOES carry `...` (pinned by `MemberSignatureNormalizerTests.cs:105`); the Swift omission is correct by construction. Keyed to the swift-syntax pin — the mirror-in-lockstep instruction remains the right guard. |

---

## Coverage gaps

1. **No subscript-`some-P`-param coverage.** `OpaqueParameterSynthesisTests` only exercise the method-param path where `_opaqueParamCapture` IS installed. The subscript index-param path (`CreateSubscriptDecl`/`HandleSubscriptAccessors`) that reaches `:3494` with capture `null` has zero unit or BindingTests coverage — Finding 1 is invisible to the suite.
2. **No `sending`-in-closure-type coverage.** No fixture surfaces a `() -> sending T` (or `(inout sending T) -> …`) printedName, so Finding 2's declaration drop is undetected. The corpus instances are filtered before `CreateMethodDecl`, so even `nuke validate` misses it.
3. **EOF-strict throw paths are unpinned.** Findings 1/2 plus the four Family-A sibling sites (`:3534/3570/3591/3615`) and the four emitter sites all rely on digester encoding conventions, not on a guard test that asserts "an un-consumed leading modifier must not drop the decl." A toolchain ABI-shape shift would silently regress with no red test.
4. **C#↔Swift mirror parity corpus has no edge inputs.** `MemberSignatureNormalizerTests` + `InterfaceFactsProducerParityTests` contain no trailing-dot, space-before-ellipsis, or closure-element-collection case, so the documented byte-divergences (Family C) are untested — parity is asserted, enforced only by the "mirror in lockstep" instruction.
5. **`SwiftInterfaceContextTracker` splitter path unpinned.** The corpus guards the availability/regex producer path, not the context-tracker's subscript-label/printedName synthesis, so the arrow-guard behavior flip (Family B) is unverified for that consumer.

---

## Recommended BindingTests fixtures (lock down each confirmed defect)

These describe the Swift shape needed to make each confirmed regression permanently observable. No fixes proposed.

1. **Subscript with a `some P` index parameter (Finding 1).** An Apple-framework-style type consumed via **SDK-direct Apple-framework mode** (so the ABI flows through `swift-api-digester -dump-sdk`, which emits the `"some …"` form), declaring `subscript(shape: some Shape) -> Int { get }` where `Shape` is a local protocol. Assert the subscript is present in the generated C# and round-trips. Pair with a generator unit test feeding a `GenericTypeParam`/`"some Lib.Shape"` node through `CreateSubscriptDecl` with `_opaqueParamCapture` null and asserting the subscript is NOT dropped. Add `Shape` to `PreservedProtocols` in `build/Helpers/SwiftSourceStripper.cs` so the stripper keeps its witness-table getter.

2. **A `sending`-bearing closure-typed property/return (Finding 2).** A type with `public var producer: () -> sending Box { get }` (and/or a method `func make() -> sending Box`), `Box` a small public class. Assert both the property and the enclosing type's other members (incl. the synthesized initializer) emit — pre-fix the whole declaration vanishes with `Unexpected trailing token 'Box'`. This is the durable end-to-end gate; a unit test asserting `TypeSpecParser.Parse("() -> sending Lib.Box")` either parses or is routed through `ParsePrefix` complements it at the grammar layer.

3. **(Hardening, covers the inconclusive + Family-A sites.)** A generator unit test that drives each unwrapped `CreateTypeSpec` site (`:3494`, `:3534`, `:3570`, `:3591`, `:3615`) and the emitter `ParseParameter`/property paths with a synthetic node whose printedName carries an un-consumed leading modifier, asserting the enclosing declaration is NOT silently dropped (or is degraded, not vanished). This pins the EOF-strict throw paths against future toolchain ABI-shape drift independent of whatever modifier the current digester happens to surface.
