# Session 16 — Upstream resilience

**Goal:** make the project's silent dependencies on ~10 unstable upstream formats *loud at the chokepoint* instead of corrupt at a consumer. **Findings:** 58 (toolchain identity + supported-matrix + golden censuses), 59 (the four remaining ABI-inventory corners after the 0.15.0 tripwire), 60 (a `ManglingProbes` golden-grammar module), 61 (App-Store compliance pipeline loudness). **Flags:** standard.

This is an implementation plan. Read it top to bottom; every cited `file:line` was verified against `main` at the time of writing (commit `8ec160df`) and drift from the architecture review is called out explicitly.

> **✅ STATUS — COMPLETE (2026-06-18).** All four findings implemented honoring amendments A–F.
> **Phase A = F60** (`ManglingProbes` module + `SymbolicReferenceGrammar` + golden parity test;
> literal-preserving refactor of the suffix-concatenation call sites). **B = F59** (the four
> ABI-inventory corners: VWT POD/bitwise-takable flag bits, symbolic-reference grammar, and `@frozen`
> 8/16/24-byte straddle fixtures routed through the generator's wrapper-selection path — amendment A).
> **C = F58** (`SupportedToolchain` matrix + startup `AssertSupported` Xcode-envelope gate, warn /
> `--strict-inputs` fail-closed; `KnownAbiNodeKinds` census coexisting with SWIFTBIND034 — amendment C;
> max-Xcode ceiling + pinned-swift-syntax assertion — amendment E). **D = F61** (dependency-free
> `MachOReader` install_name reader, positive `_StampSwiftRuntimeEmbed` tripwire conditioned on the
> NativeReference's own Apple-TFM + `Exists(xcframework)` guards — amendment D, tri-state gate result).
> SWIFTBIND057 dropped (amendment B). Post-implementation Codex + Grok paired review (no Highs) yielded
> six fixes: Mach-O `cmdSize` overflow hardening + `FAT_MAGIC_64`/sentinel tests, the stamp `Exists`
> guard, and two by-design doc clarifications. **Gates:** `nuke test` 13553/0; `binding-tests
> --skip-regen` sim 2892/0/0; `binding-tests --appstore-hygiene` gate OK on a signed `.ipa`. See the
> Findings 58–61 Status notes in `architecture-review-2026-06.md`.

---

## ⚠️ Review amendments — Grok + Codex design review (2026-06-16) — READ BEFORE IMPLEMENTING

Both reviewers ran a full pre-implementation pass. Verdicts: Grok **READY WITH CHANGES**, Codex **READY WITH THE LISTED CHANGES**. The four findings hold; these amendments correct **what actually gets tested** and keep two existing hard-fail contracts intact. They **supersede** conflicting body steps. Resume tokens — Grok `019ed3be-e951-7f40-8875-0c5ac8382ec5`, Codex `019ed3be-b2ee-7bd3-9db6-92c5de96e816`.

**A — F59 size-threshold tripwire proves nothing as designed (both, High).** The plan's "Plain `@_cdecl`/exported funcs" fixtures (`:136,:166`) **bypass the generator's wrapper-selection constants** — `WrapperValidation.AbiSizeLimits.MaxSelfSize=8` / `MaxParamSize=16` (`WrapperValidation.cs:55,61,67`), consumed at decisions like `InlineSize.Value > MaxSelfSize` (`:1945,1949`) and `> MaxParamSize` (`:2015,2016`). A direct `@_cdecl` call passes even if the generator's CallConvSwift-vs-wrapper threshold is wrong. → **The F59 fixtures MUST route through the generator's wrapper-selection path** — real Swift types whose `InlineSize` straddles 8/16, generated bindings, round-tripped — **and be `@frozen`** so the fixture layout is the ABI layout. A plain `@_cdecl` fixture does not exercise F59's goal.

**B — Drop or narrow SWIFTBIND057; do NOT turn fact-skew into warn-and-continue (both, Medium).** The interface-fact census targets a chokepoint that **already hard-fails**: `InterfaceFactsJson.cs:15,17` disallows unmapped members; unknown `coveredFacts` throw (`SwiftSyntaxInterfaceFactsProducer.cs:191,196,198`); `InterfaceFactKind` is the allowlist with a parity-test contract. A warn-and-continue census is a **regression**. → A golden is acceptable **only** if sourced from the existing `InterfaceFactKind`/`coveredFacts` contract and it **preserves the hard-error** semantics; otherwise drop SWIFTBIND057.

**C — Reconcile SWIFTBIND056 with the existing SWIFTBIND034 (Codex, Low).** `SwiftABIParser` already has `_unknownNodeKinds` (`:206,212`) + `SWIFTBIND034` AbiJson degradation for unrecognized node kinds (`:1051,1060,1064`). → The new ABI node-kind golden must state how it **coexists with or replaces** SWIFTBIND034, not duplicate the diagnostic.

**D — F61 embed-stamp must be conditioned (Codex, doc-Q b).** `SwiftBindings.Runtime.targets` is imported by **every** consumer leg (`:60,63`) and explicitly carries "deliberately no SwiftSupport-folder injection" (`:89`). → Condition the stamp target on the same Apple/native-reference assumptions (e.g. `IncludeSwiftBindingsRuntimeNative`) so it can't fire on non-Apple legs. The `--skip-regen` ordinary-build sanity gate is the right validation.

**E — Add supported-matrix max-Xcode + pinned-SwiftSyntax assertions (Grok, Medium).** The committed support matrix should declare a **max-tested Xcode major** (not just a floor), and the pinned SwiftSyntax revision needs a **runtime assert**, not just a build pin.

**F — Stale cites to re-anchor against live `main` (Codex, Low; S16 rebases after S19).** `+ "Tq"` at `SwiftABIParser.cs:1487` (body says 1468); `+ "Tu"`/`"TjTu"` at `:2859/:2860` (body says 2840–2841); `TryGetModuleFromMangledName` at `:3978` (body says 3899/3905); `$ss` at `EveryProtocolEmitter.cs:6412` (body says 6402).

**Confirmed, no change:** warn-by-default / `--strict-inputs` posture correct; 3-item F61 residual scope honest (the SwiftSupport injector and loose-dylib path are genuinely gone); **S19 must land first**, then S16's residual suffix work is `Tq` + accessor `Tu`/`TjTu`.

---

## Current-state verification

### Finding 58 — toolchain identity (review §1965–1981)

- **Review claim:** "zero toolchain version detection … the only declared support matrix is README's 'Xcode 26 or later, and .NET 10 SDK'." **Partially drifted.**
  - README matrix line confirmed: `README.md:130` — `**Requires**: macOS, [Xcode 26] or later, and [.NET 10 SDK] …`. Still open-ended, still the only declared matrix.
  - **Already-shipped corner:** the ABI-JSON *format* version IS now gated — `SwiftABIParser.GateAbiFormatVersion` (`SwiftABIParser.cs:935–964`), `ExpectedAbiFormatVersion = 8` (`:220`), diagnostic `SWIFTBIND033`, called from `ParseModule` at `:883`. This is Finding 45's ingestion gate, NOT Finding 58's toolchain gate. It proves the *digester output shape*, not the *swiftc/Xcode identity*.
  - **Still absent (verified by grep):** no `swiftc --version` / `xcodebuild -version` / `xcrun … version` read anywhere in `src/Swift.Bindings/src`, `src/Swift.Bindings.Sdk`, or `build/`; no committed supported-matrix file; no swiftc/clang/digester golden census. So items (i) startup toolchain assertion, (ii) committed supported-matrix, (iii) golden-census canary are all unbuilt.
  - **Reusable precedents found:** (a) the `InputResolutionReport` degradation/info channel (`src/Swift.Bindings/src/Reporting/InputResolutionReport.cs:11–30`, categories `SliceSelection/Architecture/SwiftInterface/AbiJson/Tbd/Dependency`) plus the `--strict-inputs` escalation (`CliOptions.cs:159`, `BindingsGeneratorCommand.cs:464/515/744` via `EmitStrictInputsFailureIfDegraded`). The toolchain assertion should plug straight into this — warn-by-default, hard-fail under `--strict-inputs`. (b) The **node-kind census golden** pattern already exists for the clang path: `ClangAstParser.KnownTopLevelNodeKinds` + `SWIFTBIND029` (`src/Swift.Bindings/src/ObjC/Parser/ClangAstParser.cs:18,112–126,241,351`) with a golden test `ClangAstCensusTests.cs`. This is the exact shape Finding 58's census wants, generalized to ABI-JSON node kinds + interface fact kinds.

### Finding 59 — remaining ABI-inventory corners (review §1983–2007, status note §2002–2007)

The 0.15.0 tripwire (`1fc81403`) shipped via `BindingTests/Sources/SwiftBindingsTestLib/Metadata/AbiLayoutTripwire.swift` + `BindingTests/RuntimeTestsApp/Metadata/AbiLayoutTripwireTests.cs`. Verified it covers: VWT size/stride/alignment, existential arity 0–8 + inline buffer, metadata-kind discriminators incl. `> 0x7ff` class heuristic, tuple element offsets, frozen `String` buffer size, and an `InitializeWithCopy` behavioral round-trip. **The four named-as-open corners are confirmed still un-pinned:**

1. **VWT flag-bit positions.** `ValueWitnessFlags` (`src/Swift.Runtime/src/Swift/Runtime/ValueWitnessTable.cs:16–29`): `AlignmentMask=0x000000FF`, `IsNonPOD=0x00010000`, `IsNonInline=0x00020000`, `HasSpareBits=0x00080000`, `IsNonBitwiseTakable=0x00100000`, `HasEnumWitnesses=0x00200000`, `Incomplete=0x00400000`. Consumed at `Alignment` (`:132`), `IsNonPOD` (`:145`), `IsNonBitwiseTakable` (`:150`), `HasExtraInhabitants` (`:155`). The existing tripwire reads `Size/Stride/Alignment` only; the *flag bits themselves* (POD/bitwise-takable/inline) have no live cross-check.
2. **Symbolic-reference byte ranges.** `SwiftMetadata.cs`: relative `0x01–0x17`, absolute `0x18–0x1F` appear at FOUR sites — `:311/:316` (`GetMangledNameTarget`-style resolution), `:361/:365` (`GetMangledNameSymbol`), `:412/:414` (`GetOffset`), `:430/:431` (`GetComponent`). The "unknown bytes silently skip" behavior is the `default → index++` arm of `GetComponent`/`GetOffset`. No test asserts these ranges against a live symbolic reference.
3. **Optional extra-inhabitant rules.** Logic in `src/Swift.Runtime/src/Swift/SwiftOptional.cs` + `TypeMetadata.cs` + `SwiftMarshal.cs` (the `Optional<T>.Size == T.Size ⇒ extra-inhabitant, else tag-byte` decision; `Bool` excluded). The 0.15.0 tripwire only confirms the `Optional` metadata-*kind* discriminator (`AbiMetadataKindWord(TypeOptionalInt)`), never the size-equality rule that drives the marshalling branch.
4. **`MaxSelfSize` / `MaxParamSize` calling-convention thresholds.** `WrapperValidation.AbiSizeLimits` (`src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs:61 MaxSelfSize=8`, `:67 MaxParamSize=16`), used at `:1949/:2016/:2195/:2269`. Guarded today only by host-side constant-equality unit tests (`WrapperConsistencyTests.AbiSizeLimits_MaxSelfSize_Is8` / `_MaxParamSize_Is16`, `EmitterTests/WrapperConsistencyTests.cs:1938–1946`) — exactly the "C# constant vs C# constant" tautology Finding 59 calls out. No live cross-check that 8/16 is where AArch64 register passing actually flips.

### Finding 60 — mangling-suffix string concatenation (review §2009–2022)

- **Cites drifted (line numbers), evidence intact (behavior).**
  - `+ "Tq"` conformance-eligibility gate: review says `SwiftABIParser.cs:1236`; **actual `:1468`** (`!_demangledTbd.AllSymbols.Contains(method.MangledName + "Tq")` → `HasMissingTbdMethodDescriptors`, skips EveryProtocol conformance).
  - `+ "Tu"` / `+ "TjTu"` async-accessor classification: review says `:2427–2428`; **actual `:2840–2841`**, with the honest comment at `:2836–2839` ("The ABI JSON doesn't mark accessors as async directly").
  - Prefix family confirmed across the predicted sites: `$s`/`_$s` module attribution `TryGetModuleFromMangledName` (`SwiftABIParser.cs:3905–3906`), `$ss` stdlib filter `EveryProtocolEmitter.cs:6402`, `$s{len}{module}` module-prefix checks `ModuleHandler.cs:1680–1688`, `$s` convention coercion `PInvokeEmitHelper.cs:156` + `AbiContractChecker.cs:273/300/660/663`, demangler prefixes `Swift5Demangler.cs:31`, synthesized descriptor `UnderscoreProtocolSynthesizer.cs:383`.
- **No `ManglingProbes` module exists** (grep: the only hit is the review doc). The in-tree demangler (`Swift5Demangler.cs`, Finding 17) is still the documented "dead" path — Finding 60's interim ask (one `ManglingProbes` module + golden test enumerating every assumed fragment) is the correct scope; the "route through demangler" destination stays parked with Finding 17.

### Finding 61 — App-Store compliance loudness (review §2024–2039)

- **Heavily drifted — the dangerous couplings the review described are largely *gone*, but the residual loudness asks remain.**
  - **The SwiftSupport injector and loose-dylib pack path are deleted.** `SwiftBindings.Runtime.targets:89–94` carries an explicit "deliberately no SwiftSupport-folder injection" note; the runtime ships `SwiftBindingsRuntime.xcframework` (framework slices). So the review's "structural bet that Apple keeps shipping back-deployment dylibs … script exits 0 and SwiftSupport is silently omitted" is no longer the architecture — there is no such script.
  - **A real gate now exists:** `--appstore-hygiene` (`build/Build.BindingTests.AppStoreHygiene.cs`, wired in `build/Build.BindingTests.cs`). It does a cheap structural nupkg check (`AssertRuntimeNupkgPackaging`, `:135`) AND builds a real device IPA and inspects it (`AssertAppStoreHygieneIpa`, `:349`): framework embed + `@rpath` install_name, `codesign --verify --strict`, zero loose dylib, zero `libswift*.dylib`, no `SwiftSupport/`, no `.DS_Store`/`__MACOSX`. This is the `--swiftsupport` gate's successor and is materially better than what the review saw.
  - **Residual Finding-61 asks that are NOT yet met:**
    1. **otool text-scraping still present.** `MachOInstallName` (`Build.BindingTests.AppStoreHygiene.cs:457–466`) parses `otool -D` stdout by line position. The review's "replace otool text scraping with a Mach-O reader" applies here.
    2. **No hook-fired stamp.** The IPA leg *infers* that `CreateIpa` ran by searching for a produced `.ipa` (`:237–246`); if the workload renamed the target the publish would simply produce no IPA and the gate throws "no .ipa produced" — better than silent, but it is not a positive *"the embed hook fired"* stamp asserted at build completion. The runtime `.targets` has no embed/back-deploy hook that stamps a sentinel.
    3. **Script tri-state.** There is no Swift-deps/back-deploy *script* left to tri-state (it was deleted), so this sub-ask is **mostly obsolete**; what survives is the *gate's* own tri-state: distinguishing "host cannot sign / no IPA built" (skip, not pass) from "IPA built and clean" (pass) from "IPA built and dirty" (fail). Today an unsignable host throws rather than reporting an honest skip.

**Net for Finding 61:** scope shrinks to three concrete items — a Mach-O reader replacing `otool -D` scraping, a positive build-time embed stamp, and an honest skip/pass/fail tri-state in the gate. Do **not** re-introduce any SwiftSupport injector or loose-dylib path (memory `feedback_apple_supplement_decoupling` + CLAUDE.md Known Issues forbid it).

---

## Target design

The end-state is one principle applied four ways: **every read of an upstream-owned format passes through a single named declaration of what we assume, and a test/gate diffs the live world against that declaration.** No new copies; each finding collapses N scattered literals to one source of truth plus one tripwire.

### 58 — toolchain identity

Three artifacts, one new category:

1. **`SupportedToolchain` (new static class, generator side).** Single source of the tested envelope:
   ```csharp
   internal static class SupportedToolchain
   {
       internal const int MinXcodeMajor = 26;           // README floor
       internal const string MinDotnetSdk = "10.0";
       internal const int ExpectedAbiFormatVersion = 8; // re-exported from SwiftABIParser (one owner)
       // swift-syntax revision the InterfaceFactsProducer host is pinned to (Swift 6.1 grammar).
       internal const string PinnedSwiftSyntaxRevision = "601.0.1";
   }
   ```
   `SwiftABIParser.ExpectedAbiFormatVersion` becomes a forwarder to (or the canonical home moves to) this class so there is exactly one `8`.
2. **Startup toolchain assertion** in `BindingsGeneratorCommand.Execute` (before parsing): run `xcrun swift -version` / `xcodebuild -version` (already a hard dependency — `xcrun`/`swiftc` are required everywhere), parse the Xcode/Swift version, and feed a new `InputResolutionCategory.Toolchain`:
   - in-envelope → `RecordInfo`;
   - below floor / unreadable / above the highest-tested major → `LogWarning("SWIFTBIND055: …")` + `RecordDegradation(Toolchain, …)`, so `--strict-inputs` (CI's `--compile-only` default) escalates it. **Warn, never hard-block by default** (a newer Xcode must still *run*, just loudly).
3. **Committed supported-matrix file** `build/supported-toolchain.json` — the human-readable matrix (Xcode major range, .NET SDK floor, ABI format version, swift-syntax revision). A unit test asserts `SupportedToolchain` constants equal the JSON (so the two can't drift), and README's line is regenerated/checked from it. This is the file Finding 58(ii) wants "gating emit and feeding README."
4. **Golden censuses** (Finding 58(iii)) — generalize the existing `ClangAstParser.KnownTopLevelNodeKinds` pattern to the two other ingestion chokepoints:
   - **ABI-JSON node-kind census:** tally `kind` strings encountered in `SwiftABIParser`, diff against a committed golden set; an unknown kind → `SWIFTBIND056` (warn + degradation), mirroring `SWIFTBIND029`.
   - **Interface-fact census:** the `InterfaceFactsJson` schema already has the `kSchemaVersion ↔ ExpectedSchemaVersion` handshake (`Parser/Producers/InterfaceFactsJson.cs:36,50,67`); add a fact-kind census so a *new fact category* the host emits but .NET ignores is named, not dropped.

### 59 — close the four corners in the existing tripwire

Extend the **already-shipping** `AbiLayoutTripwire.swift` / `AbiLayoutTripwireTests.cs` pair (do **not** create a parallel fixture — one source of ABI ground truth). New `@_cdecl` probes export live truth; new C# asserts compare the mirrors:

1. **VWT flag bits** — Swift probe `abi_vwt_is_pod(typeId)` / `abi_vwt_is_bitwise_takable(typeId)` derived from `_getTypeByMangledNameInContext`/value-witness reads (or from `MemoryLayout`-observable behavior: a trivial type vs a class-holding struct). C# reads `vwt->IsNonPOD` / `IsNonBitwiseTakable` and asserts agreement for `Int` (POD), `String`/`AbiTripwireProbeClass`-holding struct (non-POD). Pins `IsNonPOD=0x10000`, `IsNonBitwiseTakable=0x100000` bit positions behaviorally.
2. **Symbolic-reference ranges** — probe a type whose demangled metadata name embeds a symbolic reference (a generic stdlib type, e.g. `Array<Int>` / a nested generic). C# walks it through `SwiftMetadata.GetMangledNameSymbol` and asserts a non-empty, expected symbol resolves — exercising the `0x01–0x17` / `0x18–0x1F` arms against a live mangled name rather than a synthetic byte buffer.
3. **Optional extra-inhabitant rule** — probe `abi_layout_size` for `Optional<class>` / `Optional<String>` (size == payload size ⇒ extra-inhabitant) vs `Optional<Int>` / `Optional<Bool>` (Int gets a tag byte, Bool uses inhabitants). C# asserts the runtime's `SwiftOptional`/`SwiftMarshal` size-equality branch matches: `MemoryLayout<Optional<class>>.size == MemoryLayout<class>.size` and `MemoryLayout<Optional<Int>>.size == MemoryLayout<Int>.size + 1`.
4. **`MaxSelfSize` / `MaxParamSize`** — these are *calling-convention* thresholds, not pure layout, so the durable pin is a **BindingTests round-trip at the boundary**, not just a constant check. Add Swift wrapper functions taking/returning an 8-byte-self struct (passes in-register) and a 16-byte param struct (boundary) plus a >16-byte param struct (must go indirect); a C# test round-trips values through them and asserts correctness. This proves 8/16 is the real AArch64 flip point on the device leg, retiring the tautological unit tests as the *sole* guard (keep them as the cheap host-side echo).

### 60 — `ManglingProbes` module + golden grammar test

One new file `src/Swift.Bindings/src/Parser/ManglingProbes.cs` that **owns every assumed mangling fragment as a named constant** and provides the helpers that today inline string concatenation:

```csharp
internal static class ManglingProbes
{
    // Suffix grammar (Swift symbol mangling; later additions — provably move).
    internal const string MethodDescriptorSuffix = "Tq";     // protocol method descriptor
    internal const string AsyncFunctionSuffix    = "Tu";     // async function pointer
    internal const string DispatchThunkSuffix    = "Tj";     // class dispatch thunk
    internal const string AsyncDispatchThunkSuffix = "TjTu"; // thunk + async
    // Prefix grammar.
    internal const string StablePrefix     = "$s";
    internal const string StablePrefixUnderscored = "_$s";
    internal const string StdlibPrefix     = "$ss";

    internal static bool HasMethodDescriptor(ISet<string> tbd, string mangled) => tbd.Contains(mangled + MethodDescriptorSuffix);
    internal static bool IsAsyncAccessor(ISet<string> tbd, string mangled) =>
        tbd.Contains(mangled + AsyncFunctionSuffix) || tbd.Contains(mangled + AsyncDispatchThunkSuffix);
    internal static bool TryGetModuleFromMangledName(string? mangled, out string? module) { /* moved verbatim from SwiftABIParser:3899 */ }
}
```

The call sites at `SwiftABIParser.cs:1468` and `:2840–2841` (and `TryGetModuleFromMangledName` at `:3899`, the `$ss` check at `EveryProtocolEmitter.cs:6402`, the `$s{len}{module}` checks in `ModuleHandler.cs:1680–1688`) route through `ManglingProbes`. A `ManglingProbesTests.cs` golden test enumerates every constant and asserts both its literal value and its documented meaning, so a toolchain grammar change is a one-file audit (the Finding-60 goal). This does **not** revive the demangler — that stays with Finding 17.

### 61 — gate loudness

Three surgical changes to `Build.BindingTests.AppStoreHygiene.cs` (+ one runtime `.targets` stamp):

1. **Mach-O reader** — replace `MachOInstallName`'s `otool -D` line-scrape (`:457–466`) with a small `LC_ID_DYLIB` reader over the Mach-O load commands (read the framework binary bytes, walk load commands for `LC_ID_DYLIB`, decode the dylib name). Keeps the same `@rpath/...` assertion but on structured bytes, not column positions.
2. **Positive embed stamp** — add a `_StampSwiftRuntimeEmbed` target in `SwiftBindings.Runtime.targets` that fires `AfterTargets` the framework-embed target and writes a sentinel file into the intermediate output; the gate asserts the sentinel exists post-publish. If the workload renames the embed target on .NET 11, the sentinel is absent → loud named failure (the Finding-61 "hook asserts it fired" ask, and a sibling of Finding 62's wiring tripwire).
3. **Tri-state gate result** — `RunAppStoreHygieneLeg` distinguishes (a) **skip** — no signing identity on host → log a clear skip, exit non-failing; (b) **pass** — IPA built and clean; (c) **fail** — IPA built and dirty. Today an unsignable host throws inside the publish and looks like a defect; an honest skip keeps the gate runnable on CI hosts without identities while still failing on real hygiene regressions.

---

## Change inventory (ordered)

> Order is chosen so each step is independently shippable and the cheap/low-risk work lands first. Steps within a finding are grouped.

**Phase A — Finding 60 (lowest risk; pure refactor + test):**
1. **New** `src/Swift.Bindings/src/Parser/ManglingProbes.cs` — the constants + helpers in *Target design §60*. Move `TryGetModuleFromMangledName` verbatim from `SwiftABIParser.cs:3899–3918` (leave a `[Obsolete]`-free thin forwarder OR update all callers — prefer updating callers).
2. **Edit** `SwiftABIParser.cs:1468` — `!_demangledTbd.AllSymbols.Contains(method.MangledName + "Tq")` → `!ManglingProbes.HasMethodDescriptor(_demangledTbd.AllSymbols, method.MangledName)`. *Why:* one owner for `Tq`.
3. **Edit** `SwiftABIParser.cs:2840–2841` — the `Tu`/`TjTu` pair → `ManglingProbes.IsAsyncAccessor(_demangledTbd.AllSymbols, accessor.MangledName)`. Keep the explanatory comment at `:2836–2839`. *Why:* one owner for async classification.
4. **Edit** call sites of `TryGetModuleFromMangledName` (grep `TryGetModuleFromMangledName(`), `EveryProtocolEmitter.cs:6402` (`$ss`), `ModuleHandler.cs:1680–1688` (`$s{len}{module}`) → route through `ManglingProbes` constants. *Why:* collapse the prefix family.
5. **New** `src/Swift.Bindings/tests/UnitTests/ParserTests/ManglingProbesTests.cs` — golden test (see Test plan).

**Phase B — Finding 59 (BindingTests-only; no generator change):**
6. **Edit** `BindingTests/Sources/SwiftBindingsTestLib/Metadata/AbiLayoutTripwire.swift` — add `@_cdecl` probes: `abi_vwt_is_pod`, `abi_vwt_is_bitwise_takable`, `abi_optional_payload_size(typeId)`, and a symbolic-reference probe target. Keep the type-id table comment (`:64–66`) in lockstep.
7. **New** `BindingTests/Sources/SwiftBindingsTestLib/WrapperCoverage/AbiSizeThreshold.swift` — Swift wrappers taking/returning an 8-byte struct (in-register self), a 16-byte param struct (boundary), and a 24-byte param struct (indirect). Plain `@_cdecl`/exported funcs; no protocol → **no PreservedProtocols entry needed** (these are not reverse-dispatch).
8. **Edit** `BindingTests/RuntimeTestsApp/Metadata/AbiLayoutTripwireTests.cs` — new asserts: `TestValueWitnessFlagBitsMatchLive`, `TestSymbolicReferenceResolvesLiveMangledName`, `TestOptionalExtraInhabitantRuleMatchesLive`. Mirror the existing `[DllImport]` + `AssertEqual` style; no skip attribute (runs on sim + device).
9. **New** `BindingTests/RuntimeTestsApp/<domain>/AbiSizeThresholdTests.cs` — round-trips the 8/16/24-byte structs through the wrappers; asserts values preserved. **Run on `--device`** (NativeAOT is where the register-vs-indirect flip actually bites).
10. **Edit** `WrapperConsistencyTests.cs:1938–1946` — annotate the two constant-equality tests as the cheap host echo, cross-referencing the new BindingTests as the durable pin (no behavior change; doc only).

**Phase C — Finding 58 (generator + build + new files):**
11. **New** `src/Swift.Bindings/src/Configuration/SupportedToolchain.cs` — the constants class. Make `SwiftABIParser.ExpectedAbiFormatVersion` (`:220`) forward to `SupportedToolchain.ExpectedAbiFormatVersion` (single owner of `8`).
12. **Edit** `src/Swift.Bindings/src/Reporting/InputResolutionReport.cs:11–30` — add `Toolchain` to `InputResolutionCategory`.
13. **Edit** `src/Swift.Bindings/src/BindingsGeneratorCommand.cs` (in `Execute`, before parse; near the existing input-resolution flow) — read `xcrun swift -version`/`xcodebuild -version`, classify against `SupportedToolchain`, `RecordInfo`/`RecordDegradation(Toolchain,…)` + `LogWarning("SWIFTBIND055: …")`. Reuse `EmitStrictInputsFailureIfDegraded` so `--strict-inputs` escalates. *Why:* startup assertion (58-i).
14. **New** `build/supported-toolchain.json` — committed matrix. **New** unit test `SupportedToolchainMatrixTests.cs` asserting JSON ⇄ `SupportedToolchain` parity AND that README's requires-line matches the JSON floor. *Why:* 58-ii.
15. **Edit** `SwiftABIParser.cs` parse loop — add an ABI-JSON node-kind census (tally `kind`, diff vs a committed golden set, `SWIFTBIND056` on unknown), modeled on `ClangAstParser.cs:112–126,241,351`. **New** golden `build/baselines/abi-node-kind-golden.json` (or in-code `KnownAbiNodeKinds` set mirroring `KnownTopLevelNodeKinds`). **New** `AbiNodeKindCensusTests.cs`. *Why:* 58-iii.
16. **Edit** `src/Swift.Bindings/src/Parser/Producers/InterfaceFactsJson.cs` — add a fact-kind census paralleling the schema-version handshake (`:36,50,67`); unknown fact category → `SWIFTBIND057`. *Why:* 58-iii second chokepoint.

**Phase D — Finding 61 (build gate + runtime targets):**
17. **Edit** `build/Build.BindingTests.AppStoreHygiene.cs:457–466` — replace `MachOInstallName`'s `otool -D` scrape with a Mach-O `LC_ID_DYLIB` byte reader (new helper, e.g. `MachOReader.ReadInstallName`). *Why:* 61 Mach-O reader.
18. **Edit** `src/Swift.Runtime/src/build/SwiftBindings.Runtime.targets` — add `_StampSwiftRuntimeEmbed` (`AfterTargets` the framework-embed target) writing a sentinel; **edit** `Build.BindingTests.AppStoreHygiene.cs` (`RunAppStoreHygieneIpaLeg`) to assert the sentinel post-publish. *Why:* positive embed stamp (61-1).
19. **Edit** `Build.BindingTests.AppStoreHygiene.cs:82` (`RunAppStoreHygieneLeg`) — detect "no signing identity" up front and return an honest **skip** (log + non-failing) instead of throwing deep in publish. *Why:* tri-state (61-3).

---

## Test plan (red-first per repo TDD policy)

For each, write the assertion, watch it fail (or, for refactors, prove parity), then implement.

- **Finding 60 — `ManglingProbesTests.cs` (unit).** Red-first: a test that imports `ManglingProbes` before the file exists fails to compile. Assert literal values: `MethodDescriptorSuffix == "Tq"`, `AsyncFunctionSuffix == "Tu"`, `AsyncDispatchThunkSuffix == "TjTu"`, `StablePrefix == "$s"`, `StdlibPrefix == "$ss"`; and behavior: `IsAsyncAccessor(set{"fooTu"}, "foo")` true, `IsAsyncAccessor(set{"fooTjTu"}, "foo")` true, `HasMethodDescriptor(set{"barTq"}, "bar")` true. **Parity guard:** a test feeding a known ABI fixture through the OLD inline logic and the NEW `ManglingProbes` path and asserting identical classification (proves the refactor is behavior-preserving before deleting the inline code).

- **Finding 59 — extend `AbiLayoutTripwireTests.cs` (runtime, sim+device, no skip):**
  - *VWT flags:* Swift `abi_vwt_is_pod(TypeInt)==true`, `abi_vwt_is_pod(TypeProbeClass-holding struct)==false`; C# asserts `vwt->IsNonPOD` matches `!pod`. Red-first by flipping a wrong bit in a scratch copy to confirm the assert bites.
  - *Symbolic reference:* probe a generic stdlib type's metadata name; assert `GetMangledNameSymbol()` returns the expected non-empty symbol (exercises `0x01–0x17`/`0x18–0x1F`).
  - *Optional extra-inhabitant:* assert `AbiLayoutSize(Optional<class>) == AbiLayoutSize(class)` and `AbiLayoutSize(Optional<Int>) == AbiLayoutSize(Int) + 1`.
  - **`AbiSizeThresholdTests.cs` (BindingTests, run on `--device`):** Swift shape — `func takeSelf8(_:Self8)`, `func takeParam16(_:P16)`, `func takeParam24(_:P24)` round-tripping a known multi-field value. C# assertion — values round-trip byte-exact; a regression where the 16↔24 boundary moves shows as a corrupted field. **No PreservedProtocols note** (no reverse dispatch).

- **Finding 58 — unit + census tests:**
  - `SupportedToolchainMatrixTests.cs` (unit): `SupportedToolchain.MinXcodeMajor == json.xcodeMajorFloor`, `ExpectedAbiFormatVersion == json.abiFormatVersion`, and README requires-line contains `Xcode {MinXcodeMajor}`. Red-first: write JSON+test before the class; compile fails.
  - `AbiNodeKindCensusTests.cs` (unit): mirror `ClangAstCensusTests` — feed a synthetic ABI-JSON node with an unknown `kind`, assert `SWIFTBIND056` is raised naming the kind; a known kind does not warn.
  - Toolchain-assertion test: a `SwiftABIParser`/command-level test injecting a stub version string below floor → asserts a `Toolchain` degradation is recorded and `--strict-inputs` escalates (reuse the existing `EmitStrictInputsFailureIfDegraded` test scaffolding).

- **Finding 61 — gate is build-host only; assert via the gate itself:**
  - Mach-O reader: a small unit test over a checked-in fixture Mach-O (or the runtime framework binary if present in the test env) asserting `MachOReader.ReadInstallName` returns the `@rpath/...` name — replaces trusting `otool` text. If a fixture binary is impractical in unit scope, assert parity (new reader vs `otool -D`) inside the `--appstore-hygiene` leg before removing the scrape.
  - Stamp + tri-state are exercised by running `nuke binding-tests --appstore-hygiene` (host with identity → pass; simulate missing stamp by temporarily renaming the embed target in a scratch copy → named failure; host without identity → honest skip).

---

## Sequencing & parity gates

1. **Phase A (Finding 60) first** — pure refactor, fully covered by the unit parity guard. Gate: `nuke test` green + the new parity test proving OLD vs NEW classification identical on a real ABI fixture. Optional `nuke validate` canary only if you suspect a behavioral diff (you should not — it is a literal-for-literal move).
2. **Phase B (Finding 59) second** — additive BindingTests; cannot regress the generator. Gate: `nuke binding-tests --skip-regen` (sim) then `nuke binding-tests --device` for the size-threshold round-trips. New Swift source means a **full** `nuke binding-tests` (regen) once to build the wrapper (per `--skip-regen` caveat in the bindingtests rule). Baselines: BindingTests pass count must be ≥ baseline.
3. **Phase C (Finding 58) third** — generator-facing; the census + toolchain warn must not turn an in-envelope run red. **Parity bake:** before/after the census, run the generator on the BindingTests lib and the validation libs and diff `binding-report.json` / generated `.cs` — output must be byte-identical (the census is observe-only; it must not change emission). Gate: `nuke test` + `nuke binding-tests --compile-only --strict` (proves the warn path doesn't fail-closed in-envelope) + a **`nuke validate` canary** (this is a cross-cutting parser change — exactly the case the CLAUDE.md table says to run validate for; confirm `cs_compile`/`swift_compile` ≥ baseline and re-baseline only if validate actually ran). Watch for the `behaviortier`/version-stamp dirtying (`feedback_validate_version_stamp_artifacts`) — `git checkout HEAD --` those, keep the baseline JSON.
4. **Phase D (Finding 61) last** — isolated to the opt-in gate + a runtime `.targets` stamp. Gate: `nuke binding-tests --appstore-hygiene` on a signing host. The stamp edit touches `SwiftBindings.Runtime.targets`, so also run a normal `nuke binding-tests --skip-regen` to confirm the new target doesn't perturb an ordinary build, and (before release) `--mixed-pack` since native packaging policy is in scope.

**Stale-binary guard (applies to every generator edit):** after editing generator source, `dotnet build src/Swift.Bindings/src -c Debug` (or `nuke compile`) before any gate — the gates run the prebuilt Debug dll and won't rebuild a stale one (`feedback_stale_release_binary_masks_regen`).

---

## Risks & owner-decision points

- **[OWNER] Finding 61 scope reduction.** The review's Finding 61 was written against a SwiftSupport-injector architecture that has since been deleted. The honest current scope is three items (Mach-O reader, embed stamp, gate tri-state), not "make the injector script tri-state." Confirm the owner accepts treating the injector-script sub-ask as obsolete rather than re-deriving a script to make loud.
- **[OWNER] Toolchain assertion default severity.** Plan is warn-by-default + hard-fail under `--strict-inputs`. A newer-than-tested Xcode must still produce bindings (just loudly). Confirm we do NOT want a hard block above the tested major — blocking would break consumers the day a new Xcode ships, which contradicts "newer Xcode should still run."
- **[OWNER] Census golden maintenance burden.** Two new goldens (ABI node-kinds, interface fact-kinds) join the existing clang golden + the validation baselines. Each new legitimate kind requires a deliberate golden update. Confirm this is acceptable (it mirrors `KnownTopLevelNodeKinds`, already accepted).
- **Diagnostic-code allocation.** `SWIFTBIND055/056/057` are unused (verified: used set tops out at `104` with gaps; `055/056/057` are free). Reserve them; if the owner prefers a contiguous block, pick from a free range.
- **Symbolic-reference probe fragility.** Picking a stdlib type whose mangled metadata name *reliably* contains a symbolic reference across Xcode versions is non-trivial; if no stable choice exists, fall back to asserting the *range constants* against the documented Swift ABI (`Mangling.rst`) via a host-side test rather than a live probe — weaker, but still better than the current zero coverage. Flag at session start.
- **MaxSelfSize/MaxParamSize live pin is a behavioral, not layout, claim.** The device round-trip proves correctness at 8/16, but cannot by itself prove the threshold is *exactly* 8/16 (only that values at/below it round-trip). The unit constants remain the literal pin; the BindingTests add real-ABI confidence. Don't over-claim.

---

## Gate matrix

| Phase / change | `nuke test` | `nuke binding-tests` | `nuke validate` |
|---|---|---|---|
| A — Finding 60 (mangling refactor + unit) | **Yes** (unit + parity) | Sim (`--skip-regen`) sanity — refactor touches the parser path | No (literal move; canary only if a diff is suspected) |
| B — Finding 59 (tripwire + size-threshold fixtures) | No (no generator logic) | **Full regen once** (new Swift source), then **sim** and **`--device`** (register/indirect flip is NativeAOT-sensitive) | No |
| C — Finding 58 (toolchain assert + censuses) | **Yes** | **`--compile-only --strict`** (prove warn path is not fail-closed in-envelope) + sim | **Yes — canary** (cross-cutting parser change; assert `cs_compile`/`swift_compile` ≥ baseline; re-baseline only if run) |
| D — Finding 61 (gate loudness + embed stamp) | Unit for the Mach-O reader if a fixture is feasible | **`--appstore-hygiene`** (signing host) + `--skip-regen` sanity (targets edit) + `--mixed-pack` before release | No |

Per CLAUDE.md "run only what the change warrants": A and B do not need validate; C does (parser-wide); D is gate/packaging-scoped. Always rebuild the Debug generator dll before any gate after a generator edit.
