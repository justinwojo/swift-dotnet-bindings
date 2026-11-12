# Swift lowering evidence

A bridge plan predicts how swiftc lowers a Swift function: its calling convention and its physical
signature. The prediction is a model. Generated code may call a Swift function through the plan's
native bridge only when swiftc itself confirms the prediction for the artifact being built. This
document defines that confirmation (a *receipt*), when a receipt authorizes a plan, and how the
SDK's own qualification differs from a consumer build's authorization.

The code lives in `src/Swift.Bindings/src/Emitter/CallFacts/SwiftLoweringEvidence.cs` (the format)
and `SwiftLoweringJudgement.cs` (the rules). The tests that pin the contract are in
`src/Swift.Bindings/tests/UnitTests/CallFactsTests/SwiftLoweringEvidenceTests.cs`.

## Terms

- **Artifact.** The module whose plans are judged: a generated binding's Swift module, or a
  hand-written transition table's assembly (`Swift.Runtime`, `SwiftBindings.Apple`).
- **Slice.** One target triple the artifact's native code is built for, such as
  `arm64-apple-ios15.0` or `x86_64-apple-tvos15.0-simulator`. A device triple and its simulator
  triple are different slices, and so are two architectures of the same platform.
- **Artifact scope.** The artifact and the exact set of slices it claims (`SwiftArtifactScope`).
  - A generated binding claims its platform's evidence slices (`SwiftArtifactScope.PlatformSlices`).
  - A hand-written table names its slices in its `slices` field. The runtime's must equal the
    triples `build-runtime.sh` compiles, and a unit test holds them equal.
- **Receipt.** What swiftc showed for one artifact under one compiler, per slice, with the
  provenance of the compile that showed it (`SwiftLoweringReceipt`).
- **Record.** A file of receipts (`SwiftLoweringEvidence`, schema 2). The SDK's qualification record
  is `build/baselines/swift-lowering-evidence.json`. A consumer's receipt uses the same format.

## The receipt (schema 2)

```
{ "schema": 2,
  "receipts": [
    { "artifact": "SwiftBindingsTestLib",
      "compiler": "Apple Swift version 6.2.4 (swiftlang-6.2.4.1.4 clang-1700.6.4.2)",
      "contract": { "planVersion": 1, "factsVersion": 3, "collectorVersion": 1 },
      "generator": "Swift.Bindings 0.21.0",
      "slices": {
        "arm64-apple-ios15.0": {
          "sdk": "iphoneos 26.2 (23C53)",
          "sources": [ { "role": "wrapper", "name": "…SwiftBindings",
                         "digest": "sha256:…", "flags": ["-wmo", "-parse-as-library", …] } ],
          "symbols": { "$s…": "swiftcc i64 (i64)" },
          "unobserved": { "$s…": "why the collector could not observe it" } } } } ] }
```

| Field | What it pins |
|---|---|
| `artifact` | The module the receipt answers. A receipt never answers for another artifact. |
| `compiler` | The full `swiftc --version` line. Its *family* (vendor plus major.minor, `SwiftCompilerIdentity.Family`) is what qualification lists. |
| `contract.planVersion`, `contract.factsVersion` | The plan and facts formats the evidence was matched against (`SwiftCallPlan.Version`, `SwiftCallFacts.CurrentVersion`). |
| `contract.collectorVersion` | How evidence is read out of IR: the signature normalization, a definition beating a declaration, disagreeing modules recorded as a conflict (`SwiftLoweringContract.CurrentCollectorVersion`). Raise it whenever that reading changes. |
| `generator` | Informational only, and never compared: the contract versions decide whether evidence still reads the same way. Comparing the build would expire every receipt on every generator release. |
| `slices.<triple>.sdk` | The SDK name, version and build the slice was compiled against (`SwiftToolchainProbe.SdkIdentity`). |
| `slices.<triple>.sources` | Every input the slice's IR came from. Each records its role (`wrapper`, `probe`, `interface`, `definition`), its name, a digest over file names and bytes (never paths, so a checkout's location does not change it), and its swiftc flags (without paths, target, SDK or outputs). |
| `slices.<triple>.symbols` | Each observed target's `convention signature`, or `conflict: …` when two sources disagree. |
| `slices.<triple>.unobserved` | Targets the collector looked for and could not observe, each with a reason. This is never evidence. |

Receipts are kept one per artifact and compiler family. `WithSlice` replaces a slice. When it
records under another patch version of the same family, the old receipt is replaced. When it
records under another contract, the slices collected under the old contract are dropped.

## The judgement

`SwiftLoweringJudgement.Judge(symbol, callee)` decides one forward plan. It works in this order:

1. **Contradictions.** Every receipt in the record is checked: the artifact's own and any other
   artifact's that shows the same symbol, under every compiler and on every slice it covers,
   whether or not the scope claims that slice and whether or not the receipt is current. Any slice
   that shows the target with a different convention or signature makes the plan non-executable,
   and so does a `conflict:`. Nothing overrides a known mismatch. The evidence string contains
   `the plan predicts`, which the bridge gate fails on, and names the other artifact's receipt
   when the contradiction came from one.
2. **Refusal.** If the receipts do not hold up (see below), no plan of the artifact is executable,
   and each one's evidence states the refusal.
3. **Coverage.** Every authorizing receipt must show the target on every claimed slice. Otherwise
   the evidence reads `swiftc shows no <symbol> on <triple> under <compiler>`, followed by the
   recorded reason when the target was unobserved.
4. **Match.** The evidence reads
   `swiftc gives swiftcc <callee> on <N> triple(s) under <M> toolchain(s)`.

Extra slices (covered by a receipt but not claimed) authorize nothing. A contradiction on one still
counts (step 1). They do not make the receipt stale either: each slice carries its own SDK and
sources, so evidence for a claimed slice does not depend on which other slices were collected. A
receipt covering iOS and tvOS therefore authorizes an iOS-only build of the same artifact.

A plan set takes only a judgement of its own module (`SwiftCallPlanSet.WithEvidence` refuses
another artifact's).

An artifact with no bridge plans is valid. A receipt with empty `symbols` authorizes, and judging
no plans yields nothing. Fixtures that need "at least one executable plan" assert that themselves.

`Join(coverage, plans)` applies the judgement to a plan set as before. Entries that are not planned
carry no evidence. Thick and reverse plans are never executable. An unread argument without a
reason fails closed. A judged sidecar records `evidenceScope { artifact, slices, basis }`.

## Qualification vs authorization

The two are distinct operations that read the same format.

**Qualification** (`SwiftLoweringQualification.Qualify(record, scope)`) is SDK qualification. The
SDK is qualified against a fixed list of compiler families, `SwiftLoweringQualification.Families`
(currently Apple Swift 6.2 and 6.4, that is Xcode 26.3 and 27.2). Its committed record must hold,
for each artifact the SDK qualifies:

- a receipt under **every** listed family;
- **no** receipt of an unlisted family;
- every receipt collected under the **current contract**;
- every receipt covering **every** claimed slice.

Otherwise the artifact does not qualify, and the gate reports why. Qualification never consults the
host toolchain, so the verdict is the same under any profile. It judges:

- the SDK's own artifacts: the products of a hand-written table that routes through bridges (the
  runtime's), from `nuke swift-bridge-products`. A table that routes nothing (the Apple
  supplement's) is planned but not judged, even though the record holds its receipts. A plan judged
  executable that nothing routes would be a transition left on its direct import, which the
  transition ledger reports as a routing gap;
- the qualification corpus: the iOS BindingTests regeneration, via `--swift-lowering-qualification`;
- the bridge gate (`nuke swift-bridge-gate`), which re-judges every sidecar by the scope it records.
  It refuses a sidecar with an authorization basis or with the scope of an artifact other than its
  own module, and a routed generation's sidecar that records no scope. A sidecar of a table that
  routes nothing records none and stays unjudged.

`swift-lowering-oracle` writes the record, one family per run, so it runs once under each toolchain
profile. The corpus evidence includes the libraries' own definition IR, which a binary framework's
consumer never has. That is why corpus agreement qualifies the planner and the bridges, but never
authorizes a third-party plan by itself.

**Authorization** (`SwiftLoweringAuthorization.Authorize(expectation, evidence)`) covers one
artifact built by one compiler. `SwiftEvidenceExpectation` states what the build is:

- the scope;
- the selected compiler's version line;
- the contract;
- each claimed slice's SDK identity;
- once known, each slice's evidence sources.

The receipt authorizes only when all of the following hold:

- it is the artifact's own;
- it was collected by exactly that compiler;
- that compiler belongs to a qualified family;
- the plan, facts and collector versions all match;
- it covers every claimed slice, each against the same SDK;
- if the expectation names sources, it was read from exactly those sources (none missing, none
  extra, each with the same digest and flags).

Any change to the compiler, an SDK, an input, the contract or the slices makes the receipt stale
and non-authorizing. For .NET 11 routing that is fail-closed: the transition keeps its direct
import, and the corpus's evidence-gap tripwire reports it. A consumer does not need every
qualified Xcode, only one qualified one, plus its own receipt.

`SwiftToolchainProbe.Expect(scope)` builds the expectation for the selected toolchain (`xcrun`,
which honours `DEVELOPER_DIR`), without sources. In binding generation,
`--swift-lowering-evidence <receipt>` (the SDK's `<SwiftLoweringEvidence>` property) authorizes
each module over the slices of `--platform`. The two options are exclusive.

Because the verdict depends on the selected toolchain, an SDK build that sets
`<SwiftLoweringEvidence>` adds the `swiftc --version` line and every Apple SDK's version and build to
its incremental-build fingerprint. Switching Xcode regenerates the binding rather than reusing
bridges routed under the previous toolchain.

## What generation can and cannot check

Routing is decided while the module is generated. The wrapper source is written after that and is
rewritten again on disk before it is compiled. So at generation time the final wrapper digest does
not exist, and the expectation carries no sources: authorization at generation compares artifact,
compiler, family, contract, slices and SDKs, but not source digests. A build that collects its own
receipt must finalize routing after its native inputs are final, and compare sources then
(`SwiftEvidenceExpectation.Sources`). Until it does, a receipt authorizes the plans of whatever
wrapper the same compiler and SDKs produce.

## Limits

- Matching signatures do not establish aggregate byte offsets, metadata or witness-table
  arguments, or ownership. Those are qualified separately: layout checks in the oracle, and the
  bridge frame, unwind and linked-image checks.
- Evidence of an imported declaration shows what the selected compiler expects of the interface. It
  does not inspect the vendor's binary.
- A target that no client-visible declaration exposes (for example, an `@usableFromInline internal`
  function) cannot be observed. It is recorded under `unobserved`, with a reason, and never
  authorizes. Probes must never declare a target with `@_silgen_name`: that would ask swiftc to
  lower the prediction rather than the library's declaration.
