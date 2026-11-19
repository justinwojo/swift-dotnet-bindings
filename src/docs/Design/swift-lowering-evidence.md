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
      "contract": { "planVersion": 1, "factsVersion": 4, "collectorVersion": 1 },
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

**A target no client can name.** Under authorization, `Join` refuses a forward plan, rather than
leaving it planned and unproven, when the judgement found it neither executable nor contradicted
and either:

- its facts record why the module interface cannot name the target (`target.notPublic`, carried on
  the coverage entry as `notPublic`). The member, its storage or a type of its own module that
  declares it is internal, `@usableFromInline internal`, unavailable or SPI; or
- every claimed slice that does not show it gives a probe reason of the `not public from the
  interface:` class.

The entry becomes `Refused` with one `NotPublicFromInterface` refusal. It keeps its symbol, hash,
callee and evidence, and stays a guarded direct import, so the binding still declares the member.
No receipt of a consumer's build can show such a target, so a planned entry would only be an
evidence gap that refuses the whole package (SWIFTBIND133, SWIFTBIND132 and the transition gate
share that rule). A contradiction still wins, and an executable plan is never refused. A target
unobserved for any other reason, on any one slice, stays planned and is an evidence gap.
Qualification keeps such plans planned, because the committed record holds definition IR that can
show them. `WithoutEvidence` returns a refused entry to planned, so judging a plan set again gives
the same result.

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
- the qualification corpus: the iOS BindingTests regeneration (the compile-only gate's), the Mac
  Catalyst one (the `--catalyst` leg's) and the tvOS one (the `--tvos` leg's), via
  `--swift-lowering-qualification`. Each is judged over its own platform's slices, so the record
  holds the corpus's receipts for the three iOS triples, the three tvOS triples and both macabi
  triples, and no platform's receipts stand in for another's. The Catalyst and tvOS legs
  compile-check their output for their own framework and run the same ledger, R3 and residual-guard
  cross-check over it, after a control that the plans were judged over that platform's slices, the
  main module still records guarded transitions, and the binding compiles bridge twins;
- the bridge gate (`nuke swift-bridge-gate`), which re-judges every sidecar by the scope it records
  and assembles and validates every bridge for each platform a judged sidecar's slices claim,
  against that platform's own triples and SDK.
  It refuses a sidecar with an authorization basis or with the scope of an artifact other than its
  own module, and a routed generation's sidecar that records no scope. A sidecar of a table that
  routes nothing records none and stays unjudged.

`swift-lowering-oracle` writes the record, one family per run, so it runs once under each toolchain
profile, and once per routed platform (`--platform maccatalyst` or `--platform tvos` regenerates
that platform's corpus first; until the record holds that corpus's receipts, its probe-route
requests cannot be met, so that first run needs `--permissive`). The corpus evidence includes the libraries' own definition IR, which a binary framework's
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

Because the verdict depends on the selected toolchain, an SDK build that reads a receipt
(`<SwiftLoweringEvidence>`) or collects one (see *Production collection*) adds the `swiftc --version`
line and every Apple SDK's version and build to its incremental-build fingerprint. Switching Xcode
regenerates the binding, and recompiles its wrapper, rather than reusing bridges routed, or a
receipt collected, under the previous toolchain. A build that does neither keeps its fingerprint
text unchanged.

## What generation can and cannot check

Routing is decided while the module is generated. The wrapper source is written after that and is
rewritten again on disk before it is compiled. So at generation time the final wrapper digest does
not exist, and the expectation carries no sources: authorization at generation compares artifact,
compiler, family, contract, slices and SDKs, but not source digests. A hand-authored record
therefore authorizes the plans of whatever wrapper the same compiler and SDKs produce. A build that
collects its own receipt routes after its native inputs are final (see *Routing from the collected
receipt*): the receipt was collected from the wrapper it ships, and a relink must read exactly the
sources that receipt records.

## Production collection

A binding build collects its module's receipt from the wrapper it ships. The collection runs in
the generator's wrapper compile (`--compile-wrapper-only --collect-swift-lowering-evidence <path>`,
`SwiftWrapperLoweringEvidence` in `src/Swift.Bindings/src/Configuration/`). The SDK passes it from
`_CompileSwiftWrapper` and writes the receipt to `obj/…/swift-binding/swift-lowering-receipt.json`
(`$(_SwiftLoweringReceipt)`). The IR reader (`SwiftIrModule`) and the slice builder
(`SwiftLoweringEvidenceCollector`) are shipped generator code, shared with the oracle, so the SDK
and the qualification corpus read IR the same way under one collector version.

**What is compiled.** After a slice's wrapper links, a second swiftc pass emits the IR of the same
post-processed source files. It uses the link's own frontend flags, byte for byte: the same
`xcrun swiftc`, `-target`, `-sdk`, `-F` search paths and module name. It adds only
`-parse-as-library`, which `-emit-library` implies, and writes the IR to a file with `-o`. Several
source files (a wrapper staged with extra Swift sources beside the generated one) also take `-wmo`,
which emits one module for all of them. Without it swiftc refuses `-o` for several primary files
and writes their IR to standard error, and its output-file map crashes the 27.2 driver. The reader
still reads every module in the file on its own, so two modules that show a target differently
record a `conflict:`. The pass runs only after the link succeeded, so a wrapper that does not
compile never gets a receipt. A failing IR pass does not fail the build by itself; it withholds the
receipt.

**A receipt that was due and not written.** When the build asked to route (`--route-swift-bridges`)
and no receipt was written, every routable member would ship guarded with no word said. So the
wrapper compile fails with `SWIFTBIND134`, naming the cause (the IR pass, the probe or the slice
that withheld it) and the members left guarded, whenever a guard sidecar still holds a member
whose every transition is `pending` (one a receipt could route). It stays quiet when nothing is
pending and when the compiler is not one the collector qualifies, which withholds every receipt by
design. The SDK reads the cause from
`binding-metadata.props` (`_SwiftBindingUnroutedBridgesCause`) and raises the error from
`_ValidateSwiftWrapperCompilation`; the Apple-framework path reports it with the command failure.

**The lowering probe.** The wrapper calls only some of the plan's targets. So after the IR pass, the
slice's probe (`SwiftLoweringProbe`) calls every target that the wrapper does not define, through
the bound library's public interface, as any client would. It compiles one generated source per
slice to IR, with the link's frontend flags under a module name of its own (`<wrapper>LoweringProbe`).
- **Where the calls come from.** Each call is written from the ABI descriptors (`.abi.json`) the
  slice's frameworks ship: the bound module's descriptor first, then the other frameworks in swiftc's
  `-F` order. It is never written from the generator's prediction. A target is matched to its
  declaration by mangled name. A dispatch thunk (`Tj`) maps to the member it dispatches, and an
  allocating initializer (`fC`) to the initializing one the descriptor names. `swift-demangle` adds
  what the descriptor leaves out: a function type's `@convention`, `_const` and variadic parameters.
- **How a call is written.** Each target gets its own function, which is never called. It is
  `@MainActor`, `async throws`, and available nowhere (`iOS 99, …`), so that an isolated, async,
  throwing or newly available member can be called. Every argument and receiver comes from a generic
  helper that never returns, so the body only has to type-check. Each generic parameter becomes a
  metatype parameter of that function.
- **A member only its own actor can call.** No actor may set another actor's property, so the
  main-actor function cannot call a setter of a type isolated to a custom global actor. A member of
  a non-generic type therefore has a second form: the same body in a static function of an
  extension of its type. An extension's members share the type's isolation, so the probe reaches
  the setter without naming the actor. The second form is compiled only when the first does not
  compile. A member isolated on its own, in a type that is not, still has no call form.
- **What counts.** Only a declaration of the exact mangled target in the probe's IR counts. The
  probe's own definitions are discarded. A call that resolves to another overload or entry shows
  nothing for the target. The probe never names a target by symbol.
- **What a call that fails costs.** A call that does not compile is replaced by its second form,
  when it has one, or dropped, and the compiler's error (both of them, after a second form) becomes
  its reason. The probe then compiles again, at most four times. An error outside every call fails
  the remaining calls, with the error as their reason.
- **The failure rule.** The probe never fails the build and never withholds the receipt. A target
  it cannot show is recorded as unobserved, with the probe's reason. Each reason starts with its
  class:
  - `not public from the interface:` an `@usableFromInline internal` or SPI member, or an internal
    declaration that a descriptor built with `-enable-testing` lists. The probe reads every
    identifier of the module's public and private interface; a descriptor declaration whose own
    name, whose storage's name or whose enclosing type of the same module is not among them is
    internal and gets no call;
  - `inlined:` `@_transparent`, `@_alwaysEmitIntoClient`, or a call that leaves no declaration;
  - `unsupported in the probe:` no call form, a call that did not compile, or no shipped descriptor;
  - `other:` a client reaches it through another entry, no descriptor declares it, or the probe
    did not compile.
- **The source it records.** When its IR is read, the probe is recorded as a `probe` source,
  `<probe module>.<compiled triple>.swift`, with the wrapper's flags. Its text is deterministic: no
  paths, and the targets in order. Its source and IR are deleted after the pass.

**What a slice records.**
- The key is the platform's evidence slice with the compile's architecture, OS and environment
  (`SwiftArtifactScope.EvidenceSliceOf`). The compile's own deployment version is recorded in the
  wrapper source's flags (`-parse-as-library -strict-concurrency=minimal -target <compiled triple>`).
  A changed deployment target therefore changes the sources, and the receipt goes stale.
- The SDK is identified by name, version and build (`SwiftToolchainProbe.SdkIdentity`).
- The sources are the wrapper (a digest of the files compiled) and one `interface` source per
  framework the compile could import. That covers the bound module's framework, then every other
  framework in the bound slice's directory (a slice may ship peer frameworks beside it), then every
  framework on the dependency search paths. The first of each name in swiftc's `-F` order wins. Each
  is digested over its
  `Modules`, `Headers` and `PrivateHeaders`, which are what swiftc reads to import it. Interface
  sources record no flags, since the evidence compile does not compile them. The probe, when its IR
  was read, is a `probe` source.
- Every forward plan target that neither IR shows is `unobserved`. The reason is the probe's, when
  the probe was asked for it. A wrapper of no bound module has no interface to probe; there the
  reason is that the wrapper does not call the target, or that the post-processor stripped it.

**When a receipt is written.** The receipt is the shipped wrapper's, so it is all or nothing:
- It holds the slices of the first compile pass and of every extra-architecture pass that was
  merged into the shipped wrapper. A slice two passes built keeps the first pass's evidence, as
  the shipped binary does.
- If any slice of those passes was not observed, no receipt is written. That happens when its IR
  pass failed, when it had no Swift source (a thunk-only slice), when it could not be keyed, or
  when its SDK could not be identified.
- No receipt is written unless the selected compiler is a qualified family. Such a receipt could
  never authorize, so the extra pass is skipped altogether.
- The receipt is written atomically, as a same-directory temporary renamed into place. It is
  written only after the wrapper compile, its reconciliation and the merge have succeeded.

**Lifecycle.** A receipt never outlives the wrapper it describes:
- The generator removes the previous receipt before it compiles anything (under the
  wrapper-compile lock), so an interrupted or failed compile leaves no receipt.
- Regeneration removes it before the generator runs.
- A wrapper recompile with collection off removes it.
- The up-to-date skip does not require a receipt. A wrapper that compiled without one (an
  unqualified compiler, an unobservable slice) is not recompiled on every build.

The receipt goes stale through any of its inputs, each of which `Authorize` compares once
`Sources` are named:
- the compiler line and each SDK identity, which also join the fingerprint;
- the wrapper digest and the `-target` flag;
- each imported interface's digest;
- the contract's plan, facts and collector versions.

**Where it runs.** `SwiftCollectLoweringEvidence` defaults to `true` for every iOS, Mac Catalyst and
tvOS binding project (`net10.0` and `net11.0`) and every `net11.0` binding project. A project can set it either way. Every
other `net10.0` build runs no extra swiftc pass and keeps its fingerprint text. The extra pass is a
full second frontend compile of the wrapper, without the link. Its cost grows with the wrapper, so
paying it where nothing reads the result is waste. Measured on one simulator slice:
- BindingTests: the IR pass took 29 to 42 s after a 32 to 34 s wrapper compile, under both Xcode 26.3
  and 27.2.
- A third-party networking binding: it took 1.3 to 2.7 s after a 2.1 to 2.5 s compile.

So it roughly doubles the wrapper's swiftc time. The probe adds little: on BindingTests it wrote 89
calls and took 1.2 s, in two compiles, under Xcode 27.2. A build that finds the wrapper up
to date skips all of it, so an incremental build pays nothing.

**Apple-framework mode.** An Apple SDK framework's wrapper compiles inside the generator run
(`_GenerateSwiftBindingsAppleFramework`), so that run collects the receipt and routes from it, under
the same properties and by the same commit-and-route step as `--compile-wrapper-only`
(`BindingsGenerator.CommitReceiptAndRouteBridges`). The rules differ in three places:
- **Every slice in one run.** A receipt covers exactly the slices the wrapper ships, so the run
  compiles every one itself: the simulator slice for each architecture of
  `--target-architectures`, and the device slice. The framework is handed over as it sits in one
  SDK; each slice finds it at the same place in its own SDK (`AppleFrameworkSlices`), with the
  module interface for its triple. A device SDK ships its frameworks' interfaces for `arm64e`
  alone, which swiftc reads for an `arm64` compile. The SDK adds no second slice over a wrapper that
  already ships it. A framework some slice's SDK lacks is compiled as before, without a receipt,
  and logged.
- **The probe reads the digester's descriptor.** An SDK framework ships no `.abi.json`, so the probe
  reads the one the generator bound (`-a`) in place of the framework's own, for every slice.
- **The SDK's frameworks are not sources.** An `interface` source is recorded only for a framework
  outside the slice's SDK. The SDK's own frameworks are covered by its identity (name, version and
  build), which the receipt already records, so digesting each of the hundreds a compile could import
  would buy nothing.

The fingerprint of a Swift Apple framework that collects joins the compiler line and every Apple
SDK's version and build, as a framework bound from an xcframework does. The generator run deletes
the previous receipt first; the SDK deletes it before a regeneration too. Only the plain direct
generator path (a third-party framework given with `-a`/`-d`/`-t`, outside an SDK) does not
collect, and so does not route from a receipt.

The wrapper's IR alone shows 32 of the 128 plan targets on BindingTests. With the probe, the receipt
shows 113, measured under Xcode 27.2, and every one agrees with the oracle record of both compiler
families. The other 15 are unobserved because they are `@usableFromInline internal`, which no
client can call. A setter isolated to a custom global actor is reached by the probe's in-type form.
The probe needs the descriptor a framework
ships. A slice without one leaves its targets unobserved; the probe does not generate a descriptor
from the interface.

## Routing from the collected receipt

A binding that collects its receipt routes from it in the same wrapper compile
(`--route-swift-bridges`, `SwiftBridgeFinalizer` in `src/Swift.Bindings/src/Configuration/`). The
SDK passes the flag when `SwiftRouteBridges` is on, which it is by default for iOS, Mac Catalyst and
tvOS targets, and only when it collects and has no hand-authored `<SwiftLoweringEvidence>`. Nothing in the
finalizer is platform-specific: it judges the receipt over the receipt's own slices, so a Mac Catalyst
wrapper's receipt (both macabi slices) routes the same way, and a plan one architecture's slice
contradicts stays a guarded direct import on both. The managed assembly is shared by every RID of a
universal Catalyst app, so a guard rewritten to refuse nothing must hold for every architecture's
native slice. `--bridge-consumer --catalyst` proves this with a universal (`maccatalyst-arm64;maccatalyst-x64`)
consumer in all three consumption modes. A tvOS library ships an arm64 device slice and a universal
(arm64 + x86_64) simulator slice, so its receipt holds three slices, and a plan any one of them
contradicts stays guarded on all. `--bridge-consumer --tvos` binds a fixture shaped that way, requires
each build's receipt to cover all three slices and the packed simulator wrapper to carry both
architectures, and runs the consumer on the arm64 tvOS simulator in all three consumption modes.
macOS still defaults to off. A hand-authored record routes at generation instead, and the wrapper
compile then leaves the C# alone.

**Why in the wrapper compile.** Generation runs before the wrapper is compiled, so it has no receipt.
Feeding the receipt back as `<SwiftLoweringEvidence>` would make it a fingerprint input of the
generation that deletes it. The finalizer therefore changes no input of generation: the binding's
fingerprint never sees the receipt, and there is no loop.

**What it does.** Once the receipt is written:
1. It restores generation's own C#. A journal beside the C#
   (`{namespace}.swift-bridge-routing.json`) records every import an earlier routing rewrote, and
   generation deletes the journal along with the C# it replaces.
2. It judges the receipt for the plans generation wrote without evidence
   (`SwiftCallPlanSet.WithoutEvidence`). The scope is the receipt's own slices. The receipt holds a
   slice for every slice the wrapper ships and for no other, so an arm64-only wrapper claims no x86_64
   simulator slice.
3. It routes the authorized plans over that C#, as generation would under the same judgement, and
   writes the C#, the bridges and the plan and ledger sidecars to match. It writes the journal first,
   so a build stopped part-way leaves a journal covering every rewrite on disk.
4. The wrapper must define exactly the bridges the C# calls. When the bridges differ from those it was
   linked from, it is compiled again with them: the same sources, flags and slices. Every relinked
   slice must read exactly the inputs the receipt was collected from (`SwiftWrapperLoweringEvidence.ForVerification`):
   the wrapper digest, the probe, each interface digest, the SDK and the compiler. The relinked wrapper
   must strip the same symbols and fold the same architectures as the first compile.

**Fail-closed.** An import the receipt does not authorize keeps its Swift-convention call, and a
contradiction in the receipt refuses its plan whatever else the receipt says. When the relink fails,
or its wrapper is not the one the receipt describes, the first compile's wrapper is put back, the C#
goes back to generation's own and `SWIFTBIND129` says why, naming the members that keep their guarded
direct import and the engines they refuse on. The call-plan sidecar records that withdrawal
(`bridgeWithdrawal`), and the binding packs as a partial binding when it holds up as the record
(see [consumer-transition-check.md](consumer-transition-check.md) § *Partial bindings*); a relink whose
wrapper contradicts the receipt is recorded too, and refused. A journal that does not match the C# fails
the build, because the C# can no longer be trusted to be generation's own. A slice that cannot be
observed withholds the whole receipt (see *When a receipt is written*), and with it every bridge:
a thunk-only slice, which has no Swift source to compile to IR, therefore leaves its module unrouted.

**One receipt per project.** The SDK binds one xcframework per project (`SWIFTBIND002`), so a
project has one module, one receipt and one routing. A multi-framework library is several projects.

**Cost.** Generation always leaves the bridges out, so the first wrapper compile after a
regeneration that routes anything is followed by a relink: one more full compile of every slice. A
build whose wrapper is up to date skips the compile, the collection and the routing, and keeps the
routed C# on disk.

**Fingerprint.** A build that collects adds whether it routes, and every file under each framework's
`Modules/` (the descriptors the probe reads and the private interfaces), to its fingerprint. So a
changed descriptor recompiles the wrapper and collects a new receipt. An Apple SDK framework has no
such directory to hash; its fingerprint joins the compiler line and each Apple SDK's version and
build instead (see "Apple-framework mode"). A build that neither reads nor
collects a receipt fingerprints exactly what it did before.

**Where it is proven.** `nuke binding-tests --bridge-consumer` binds a fixture with default settings
and consumes it as a package, SDK-direct and through a `ProjectReference`, on .NET 10 (Mono JIT on the
simulator, NativeAOT and Mono full-AOT on a device) and the .NET 11 CoreCLR interpreter.

## Shipped evidence: the transition manifest

Routing decisions are made in the build tree: the call-plan sidecar, the guard sidecar and the
receipt sit beside the generated C#. A package carries none of them, so without more a consumer of
a packed binding, of `SwiftBindings.Runtime` or of `SwiftBindings.Apple` cannot check which
transitions were routed or why. Each of those assemblies therefore embeds a *transition manifest*,
and each native image that defines a bridge carries the manifest's hash.

**The manifest.** One JSON file per module (`SwiftTransitionManifestFormat` in
`src/Swift.Bindings/src/Emitter/CallFacts/`), schema 1:

```json
{
  "kind": "swift-transition-manifest",
  "schema": 1,
  "module": "<Swift module, or the assembly of a hand-written table>",
  "origin": "generated | hand-written",
  "generator": "<generator version without build metadata; generated manifests only>",
  "contract": { "planVersion": 1, "factsVersion": 4, "collectorVersion": 1 },
  "native": { "library": "<wrapper or runtime library>", "symbol": "SBTM_<module>", "bridges": ["SBSR_…"] },
  "plans": { … the module's call-plan sidecar … },
  "guards": { … its transition-guard sidecar, when it has one … }
}
```

`plans` and `guards` are the existing records (E1), not a second format: a reader writes them back
out as sidecars and runs the transition gate's own rules over them. `native` is null when the module
routes nothing through a bridge, and a manifest names a native symbol exactly when it lists bridges.
`contract` must agree with the versions inside `plans`. A module with no Swift-convention transition
writes no manifest. The file is indented UTF-8 JSON with LF line endings, and the same records
always produce the same bytes.

**Where it lives.**
- *Managed.* The file is an embedded resource whose logical name is its file name
  (`{stem}.swift-transition-manifest.json`). An assembly attribute locates and authenticates it:
  `[assembly: AssemblyMetadata("SwiftBindings.TransitionManifest", "schema=1;resource=<name>;sha256=<hex>")]`,
  written to `{stem}.SwiftTransitionManifest.g.cs`. An assembly that compiles several modules carries
  one attribute per module.
- *Native.* The bridge assembly of a routing module ends with an exported, `.no_dead_strip` data
  symbol `SBTM_<module>` in `__TEXT,__const` holding `sha256:<hex>`, the same hash. A changed manifest
  changes the bridge source, so a routing that relinks the wrapper links the new hash. Where the
  finalizer does not relink (no receipt, or a failed relink that puts the earlier wrapper back), the
  wrapper keeps the earlier hash and bridges, and the evidence checks report the mismatch.
- *Producers.* Generation writes the manifest from the sidecars; the wrapper-compile finalizer
  rewrites it with the sidecars it settles, so the SDK's build, package, SDK-direct and
  `ProjectReference` modes all embed the routed records. The runtime's and the Apple supplement's
  hand-written tables are produced by `swift-bridge-products` (stem `{Assembly}.HandWritten`), and the
  Apple supplement also embeds the manifest of its generated type manifest.

**The reader.** `SwiftTransitionManifestReader` and `MachOSymbols` (`build/SwiftTransitionGate/`)
need only the base class library and `System.Reflection.Metadata`, so an MSBuild task can carry them.
`Read(assembly)` returns a `TransitionEvidence` in one of four states:

| State | Meaning |
|---|---|
| `PresentValid` | Every locator parses, names a schema the reader knows and an embedded resource with that hash; the manifests parse; the imported bridges are exactly the manifests' bridges, from the named library; every Swift-convention P/Invoke entry point is in a manifest's plans; no member failed inspection. The state says the manifests describe the assembly; whether its transitions satisfy the gate's rules (a Swift-convention reverse callback, R4, for example) is `PackageTransitionEvidence`'s check. |
| `AbsentPrimitiveOnly` | No manifest, and no transition that needs one: no bridge import, no transition-gate rule hit, no Swift-convention reverse callback. |
| `AbsentNonPrimitive` | No manifest, but the assembly imports a bridge or holds a transition the gate rules apply to. The problems name it. |
| `TamperedOrMismatched` | A locator or resource is missing, malformed, unknown or does not hash to its locator, or the manifests disagree with the assembly. |

`CheckNative(evidence, library, image)` checks one native image against it: every architecture slice
of a library that a manifest routes through holds the manifest hash and exports exactly that
manifest's bridges, and an image no manifest routes through exports no `SBSR_` or `SBTM_` symbol.

**Where it is checked.**
- `PackageTransitionEvidence.Check` reads a package alone: the evidence must be `PresentValid`, the
  manifests' plans and guards are written to scratch sidecars, and the transition gate's rules
  (R2–R5, R1 through the residual-guard check, the guard-record cross-check, routing and evidence
  gaps, twin count) run against them. Every native slice is checked against every assembly.
- `nuke pack` runs it on the `SwiftBindings.Runtime` and `SwiftBindings.Apple` nupkgs. The runtime's
  linked-bridge check reads its plans from the packed manifest rather than from the build tree.
- `binding-tests --bridge-consumer` runs it, controls included, on the binding package the SDK packs
  in its `package` mode: a generated binding with default settings.
- `binding-tests --compile-only`, and the ledger cross-check of the Mac Catalyst and tvOS runtime
  lanes, read the compile check's binding, runtime and Apple assemblies, require a manifest for each
  BindingTests module, and check every slice of each wrapper a manifest routes through.
- `SwiftBindings.Sdk` runs it after `Pack` on the binding package it wrote (SWIFTBIND133), and
  `SwiftBindings.Runtime`'s buildTransitive targets run it in every consuming app on an affected
  engine (SWIFTBIND132). Both are described in [consumer-transition-check.md](consumer-transition-check.md).

Both are policy: there is no baseline. Four controls run against the packed iOS assembly on every
pack and must fail with the named reason: a tampered manifest byte, a removed manifest on an
assembly that imports bridges, a renamed bridge twin (the runtime: R3), and a package with its
routing fixture removed (no native image for the runtime; no routed bridge for Apple).

A consumer build refuses `AbsentNonPrimitive`, `TamperedOrMismatched`, and a `PresentValid`
assembly whose rules fail, and accepts an absent manifest on a primitive-only binding
([consumer-transition-check.md](consumer-transition-check.md)).

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
- A probe's call shows how a client of the interface calls the target, which is what a bridge does.
  It does not show that the binary defines that symbol with that lowering; the linked-image checks
  do that.
