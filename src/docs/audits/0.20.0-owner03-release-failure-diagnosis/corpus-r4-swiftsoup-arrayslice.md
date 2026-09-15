# OWNER-03 corpus-r4 inventory and SwiftSoup ArraySlice repair

Date: 2026-09-15

Base: `203fdd9858c98009ac74bc42b76c877573001943`

Runtime: macOS (`Darwin`)

Disposition: **the selected SwiftSoup root cause is repaired and validated; the
historical corpus-r4 publication verdict remains BLOCKED**

This receipt freezes the exact corpus-r4 red/absent inventory, separates direct
`swift-bindings` work from prerequisite and external blockers, and records the
selected SwiftSoup repair. It is not a corpus rerun and grants no waiver. The
historical authority remains 120 named candidates: 84 available, 60 successful,
24 nonzero, and 36 absent. The 24 candidate outcomes are exactly 12
`generate_failed`, three `compile_failed`, and nine with at least one
`named_missing_input` product. Mixed-product and prerequisite relationships mean
the causal clusters below are not additional disjoint outcome counts.

## Authority and caveats

The authoritative run began `2026-09-13T20:19:43.087765+00:00`, ended
`2026-09-13T21:00:11.450102+00:00`, and recorded the post-preflight complete
checkout with warm Runtime/analyzer/Apple artifacts retained. `outcomes.json`
records source, inputs, compiler, platform, consumer context, and harness as
unchanged across the run. Its harness identity is commit `69f80c4`, SHA-256
`40acd5aac9a29e1b9488291c4f347009782e872149e84d58d94d00314a941282`.

| Authority | SHA-256 |
|---|---|
| `src/docs/sessions/0.20.0/execution/P8/corpus-r4/adjudication.md` | `a1465e263a553b87675f591f31fb80adef2085647e2e270ffe97a4189f31fd92` |
| `src/docs/sessions/0.20.0/execution/P8/corpus-r4/corpus-q-r4/receipts/outcomes.json` | `d5a24ec2b5b5646aecc2f66742336f7d42364e27a62a4cd6b8e223682ff2d8f8` |
| `src/docs/sessions/0.20.0/execution/P8/corpus-r4/corpus-q-r4/receipts/fixed-input-absent.json` | `e095438ed6098cace1cac55798b4d75ac56823a31b7dd0e1ca81194acba90ff5` |
| `src/docs/sessions/0.20.0/execution/P8/corpus-r4/corpus-q-r4/receipts/qualification-artifact-authority.json` | `d9e8ff1694d0eb19417d50d117b859c2373367e7722536a710284f37c0d7472e` |
| `src/docs/sessions/0.20.0/execution/P8/corpus-r4/tested-source-q.json` | `4d698ce815243f162944c71af1ab04352bcd5074adedb348f628a293c63b9fbe` |
| SwiftSoup generation attempt `1789331944900443000-85158` | `73d29ef9a367b8f1c5314734d9a445c4bb3e05efd5d0efb222003ad5cd180d9c` |

All paths in the table are relative to
`/Users/wojo/Dev/swift-bindings/`. The qualification artifact authority says its
hashes describe post-preflight built artifacts, not byte identity to the original
r23 toolchain; source identity is separately frozen by `tested-source-q.json`.
The embedded preflight receipt SHA-256 is
`fb6d847b6e2cadba1cbfc3f21a5654d735f5441583e093a5aeaa4c947449fb40`.
The current checked-out `run-corpus-qualification-r4.py` hashes to
`c12f8b3425421aa05dbfa5e72357273e00f848e0e9aca5d70bc93d38b3e60ad6`,
so it is not substituted for the run-embedded harness identity above.

The preserved output under
`/Users/wojo/.Trash/swift-bindings-corpus-q-r4-stale-20260914` was used strictly
read-only. It was never moved, restored, modified, or deleted. Its selected
SwiftSoup hashes are:

| Preserved file under `sweep/output/SwiftSoup/SwiftSoup/` | SHA-256 |
|---|---|
| `SwiftSoup.api-manifest.json` | `7ebc86939bccc510ef3b3a2740f9c28b3d4b248c053ac37a969af73882106c15` |
| `SwiftSoup.Types.ParsingStrings.cs` | `d4473e54b65c57bea3d50e0a9a9fd3fb7b94c1c8d9d3ea0ee5b0850eec4e33a2` |
| `SwiftSoup.Types.StringBuilder.cs` | `0a0fdf229035795b33e9c45b753bd6bd70dd121b22b3d0d8ff3ce071e0a3c031` |
| `SwiftSoup.Types.StringUtil.cs` | `e176382a5a22d78473a7e0bf1d1f06f5f0f70040b1b08400b186ff5387227c00` |
| `SwiftSoup.Types.TextNode.cs` | `13821a56502fc2b6dc323a9ba4034203f532d4df355808f1fc35b2041f197caa` |

## Exact 24 nonzero candidates

| # | Candidate | Authoritative candidate outcome | Root-cause disposition |
|---:|---|---|---|
| 1 | Euclid | `generate_failed` | **Owner defect:** `SWIFTBIND111` recovery exhausted four rounds (`IterationCapExhausted`). |
| 2 | SwiftDraw | `named_missing_input` | **Owner root plus sibling consequence:** `SwiftDrawDOM` stopped with `RequiresGraphClosure` (`SendableProxy` missing); `SwiftDraw` therefore was not compiled. |
| 3 | epoxy-ios | `named_missing_input` | **Sibling consequence:** required `EpoxyBars` failed `RequiresGraphClosure` (`EveryProtocol` did not conform to `EpoxyModeled`); `Epoxy` was not compiled. |
| 4 | swiftui-charts | `compile_failed` | **Owner defect:** generated `Charts` passed generation but emitted `IEnumerable<SwiftUI.Color>` where `IEnumerable<SwiftUI.Color.Buffer>` was required (`CS1503`). |
| 5 | Auth0.swift | `generate_failed` | **Owner defect:** `Auth0` stopped with `SWIFTBIND111 RequiresGraphClosure`. |
| 6 | SwiftOTP | `named_missing_input` | **Sibling consequence:** required `Crypto` failed its generated ObjC C# verification (including malformed declarations/`void` parameters); `SwiftOTP` was not compiled. |
| 7 | CoreStore | `generate_failed` | **Owner defect:** `CoreStore` stopped with `SWIFTBIND111 RequiresGraphClosure`. |
| 8 | SwiftLocation | `generate_failed` | **Owner defect:** `SwiftLocation` stopped with `SWIFTBIND111 RequiresGraphClosure`. |
| 9 | SwiftRichString | `generate_failed` | **Owner defect:** `SwiftRichString` stopped with `SWIFTBIND111 RequiresGraphClosure`. |
| 10 | Time | `generate_failed` | **Owner defect:** `Time` stopped with `SWIFTBIND111 RequiresGraphClosure`. |
| 11 | ReactorKit | `named_missing_input` | **Input/feed blocker:** generation completed, but the fixed local feed lacked `SwiftBindings.Apple` (`NU1101`). |
| 12 | swift-dependencies | `named_missing_input` | **Input-graph blocker:** both `Dependencies` and `DependenciesMacros` failed `SWIFTBIND119`; required module `Testing` was not supplied. |
| 13 | swift-identified-collections | `named_missing_input` | **Sibling consequence:** required `OrderedCollections` failed the owner silent-tombstone invariant for four `_HashTable` types; `IdentifiedCollections` was not compiled. |
| 14 | Moya | `named_missing_input` | **Input/feed blocker:** `Moya` passed; `CombineMoya` could not restore `SwiftBindings.Apple` from the fixed feed (`NU1101`). |
| 15 | SocketIO | `generate_failed` | **Owner defect:** `SocketIO` stopped with `SWIFTBIND111 RequiresGraphClosure`. |
| 16 | swift-protobuf | `generate_failed` | **Owner defect:** `SwiftProtobuf` stopped with `SWIFTBIND111 RequiresGraphClosure`. |
| 17 | SwiftSoup | `generate_failed` | **Selected owner defect:** five ArraySlice-normalized overloads disagreed between manifest and emitted C# names. Fixed in this batch. |
| 18 | Yams | `generate_failed` | **Third-party input packaging:** `SWIFTBIND109`; umbrella import `Yams/yaml.h` is absent from the supplied distribution/dependencies. |
| 19 | analytics-swift | `compile_failed` | **Owner defects:** `Segment` emitted invalid optional-array-to-`nint` conversions (`CS0029`) and `SwiftInheritanceChain` to `SwiftHandle` (`CS1503`). |
| 20 | RevenueCat | `named_missing_input` | **Owner root plus sibling consequence:** `RevenueCat` stopped with `SWIFTBIND111 RequiresGraphClosure`; `RevenueCatUI` then lacked the required sibling. |
| 21 | TelemetryDeck | `generate_failed` | **Third-party input packaging:** `SWIFTBIND109`; `TelemetryDeck/TelemetryClient.h` is absent from the supplied distribution/dependencies. |
| 22 | swift-clocks | `named_missing_input` | **Sibling consequence:** required `IssueReporting` failed `RequiresGraphClosure` around `_FatalErrorReporter`; `Clocks` was not compiled. |
| 23 | swift-numerics | `compile_failed` | **Owner root plus product consequence:** `ComplexModule` emitted unresolved `IAdditiveArithmetic`/`INumeric` (`CS0246`), `RealModule` passed, and `Numerics` then lacked `ComplexModule`. |
| 24 | swift-system | `generate_failed` | **Platform/language blocker:** `SystemPackage` recovery reported `RequiresGraphClosure`, rooted in Swift ownership diagnostics such as `'unknown' is borrowed and cannot be consumed`; keep separate from an established generator-shape defect. |

The primary direct/actionable `RequiresGraphClosure` cohort is the eight failed
products `Auth0`, `CoreStore`, `SwiftLocation`, `SwiftRichString`, `Time`,
`SocketIO`, `SwiftProtobuf`, and `RevenueCat`, plus the causative sibling
`SwiftDrawDOM`: nine roots for the candidate adjudication. `EpoxyBars` and
`IssueReporting` expose additional sibling-product closure failures, but their named
candidate products are consequences and remain in the sibling bucket. Although
`SystemPackage` has the same recovery-category label, its preserved error is a Swift
ownership/platform diagnostic and is deliberately in the third-party/platform
bucket. The direct compile defects in `Charts`, `Segment`, and `ComplexModule` are
separate owner work. This batch repairs only the selected SwiftSoup row.

## Exact 36 absent inputs

These names were absent from the fixed restored input population; none was run and
none is counted as a pass:

1. `Hue`
2. `SwiftCharts`
3. `jwt-kit`
4. `swift-certificates`
5. `swift-crypto`
6. `swift-nio-ssl`
7. `Defaults`
8. `fluent-kit`
9. `MongoKitten`
10. `sqlite-data`
11. `Timepiece`
12. `Version`
13. `ComposableArchitecture`
14. `swift-case-paths`
15. `swift-navigation`
16. `Apollo`
17. `grpc-swift`
18. `Pulse`
19. `swift-nio`
20. `swift-url-routing`
21. `swift-markdown-ui`
22. `swift-parsing`
23. `adyen-ios`
24. `braintree_ios`
25. `dd-sdk-ios`
26. `posthog-ios`
27. `Sentry`
28. `combine-schedulers`
29. `swift-async-algorithms`
30. `swift-atomics`
31. `FlexLayout`
32. `SwiftUIX`
33. `swift-algorithms`
34. `swift-collections`
35. `swift-log`
36. `SwifterSwift`

They are unavailable-input blockers, not evidence that their bindings succeed or
fail. Requalifying them requires recovering the exact allowed inputs (or an explicit
owner decision changing the population) and a new authoritative run.

Capacity is separate: no corpus-r4 candidate outcome in the historical run was
caused by capacity. The current host has approximately 15 GiB free and this task must
preserve a 12 GiB reserve, which prevents a safe full corpus rerun now. That is a
rerun blocker, not an input identity or a candidate root cause.

## Selected SwiftSoup root cause

The authoritative attempt log, lines 1754-1761, records five manifest keys with no
distinct emitted C# counterpart:

- `ParsingStrings.ContainsWithArraySliceUInt8(IEnumerable<byte>)`
- `StringBuilder.AppendWithArraySliceUInt8(IEnumerable<byte>)`
- `StringUtil.AppendNormalisedWhitespaceStringStripLeadingWithStringBuilderAndArraySliceUInt8AndBool(SwiftSoup.StringBuilder,IEnumerable<byte>,bool)`
- `StringUtil.NormaliseWhitespaceWithArraySliceUInt8(IEnumerable<byte>)`
- `TextNode.NormaliseWhitespaceWithArraySliceUInt8(IEnumerable<byte>)`

The preserved files show the contradiction directly. For example,
`ParsingStrings` emitted the Array overload as `ContainsWithArrayUInt8` but emitted
the ArraySlice overload as bare `Contains`; `StringUtil` and `TextNode` likewise
paired `NormaliseWhitespaceWithArrayUInt8` with a bare
`NormaliseWhitespace(IEnumerable<byte>)`. The manifest retained the intended
ArraySlice-derived names and original Swift source symbols.

Overload resolution runs against the parsed declaration before
`ArraySliceNormalizationEmitter` replaces `ArraySlice<T>` with `Array<T>` in a
cloned declaration. The clone's fresh `MethodEnvironment` discarded the resolver's
`DisambiguatedNameInput` and related name/reservation state. Consequently the C#
writer recomputed a name from the normalized clone, while the manifest chokepoint
later inspected the original declaration. The clone's emitted shape was never
published back to that authoritative declaration.

The repair carries `SourceDeclId`, `DisambiguatedNameInput`, adopted-override name,
and overload reservation/shape state into the normalized environment. After the
normal wrapper/P/Invoke emitters run, it copies the normalized declaration's actual
emitted C# name and parameter portion into the original declaration's
`ModuleEmissionContext` shape. It deliberately does **not** mutate the original
Swift declaration or promote its environment symbol: the manifest keeps its stable
source-ABI symbol, while the normalized clone continues to own the `SBW_`/`SBSW_`
implementation entry point. This preserves symbol/ABI behavior while making the
public name and manifest shape agree.

## Regression coverage and validation

The unit regression creates static `Array<UInt8>` and `ArraySlice<UInt8>` overloads
that both project to `IEnumerable<byte>`. It asserts the emitted
`ContainsWithArrayUInt8` and `ContainsWithArraySliceUInt8` names, absence of a bare
collision, exact manifest keys, semantic reconciliation with the emitted C# text,
stable source-symbol identity, and the emitted-shape stamps on both source
declarations.

The Collections BindingTests fixture adds the same overload pair. The Array branch
returns `1000 + sum`; the ArraySlice branch returns `2000 + sum`. The focused
simulator test calls both collision-safe generated names and observes `1006` versus
`2006`, proving correct native dispatch rather than name-only compilation.

| Gate | Result | Log SHA-256 |
|---|---|---|
| `dotnet test ... --filter FullyQualifiedName~ArraySliceNormalizationEmitterTests --no-restore` | PASS: 42/42 | `/private/tmp/owner03-swiftsoup-focused-unit.log` — `b759fa318e1c5144a0268b3f1c03bb24c28962aff527a65ef21fd6dfe8a7d49b` |
| explicit Debug generator refresh | PASS: 0 warnings, 0 errors | `/private/tmp/owner03-swiftsoup-generator-build.log` — `d327d324deaea156e27fd546132b20a5d406d56d3f9e9276da2a1bb389645d57` |
| `nuke binding-tests --compile-only` | PASS in 4:45: 1,707 C# files; C#/Swift compile, ABI-manifest, parity, resilience, ingestion, and overload-name gates green | `/private/tmp/owner03-swiftsoup-binding-compile.log` — `1691f2620b2010476d9191763d50f79555a2b0c2c8144d3e371bff844a7af6c5` |
| `nuke binding-tests --skip-regen --class-filter ArraySliceOverloadTests` | PASS in 1:41: 2/2 simulator tests | `/private/tmp/owner03-swiftsoup-binding-runtime.log` — `65d17060ce45a0d671902ced87e12cea45fb7725be33c5b4cea06c0b4190bf9c` |
| `nuke test` | PASS: 19,086 generator tests (2 known skips), 79 analyzer tests, 922 runtime tests (1 known skip), 65 withdrawal assertions | `/private/tmp/owner03-swiftsoup-full-unit.log` — `081b0d8f093382193efeed72af3c0e38a6c78b4929141564bd6f06bc0df6a972` |

The full test gate correctly raised
`build/baselines/validation-baseline.json` from 19,085 to 19,086. No full corpus or
`nuke validate` was run. A single-library SwiftSoup canary was also intentionally
not added after validation reached the storage boundary; the exact semantic unit
reproduction, full BindingTests generation/compile gates, and focused live dispatch
cover the repaired path without generating another corpus artifact tree.

### Post-Grok focused repair pass (2026-09-15)

Grok's actionable finding was a test-specificity gap, not a further production-code
defect: the collision regression relied on name substrings and the arity-oriented API
reconciler, so some declaration-shape drift could escape. The repaired test now pins
both complete public static C# declarations (return type, collision-safe name, and
the exact `IEnumerable<byte> scalar` parameter), directly reads
`ModuleEmissionContext.GetEmittedApiShape` for the original parsed Array and
ArraySlice declarations, and asserts each `CSharpName` and `ParameterPortion`. It
also pins the source-symbol split and ownership contract: the ArraySlice manifest
retains its original Swift ABI symbol, its normalized declaration has a distinct
`DeclId`, and the synthesized wrapper is attributed to the original source
declaration. Removing the publish-back therefore fails the direct ArraySlice-shape
assertions. No emitter code changed in this repair pass.

| Focused repair gate | Result | Log SHA-256 |
|---|---|---|
| `dotnet test ... --filter FullyQualifiedName~ArraySliceNormalizationEmitterTests --no-restore` | PASS: 42/42 | `/private/tmp/owner03-swiftsoup-post-grok-focused-unit.log` — `873c83a1c70e62c7259fbeadf45f6fe24fe48dbe8f6fa7689e2d718553d32ea0` |
| `dotnet build src/Swift.Bindings/src/Swift.Bindings.csproj --no-restore` | PASS: 0 warnings, 0 errors | `/private/tmp/owner03-swiftsoup-post-grok-generator-build.log` — `341a5284733639799feba5e63a5b8a4c618a198349698dc00820e8b680ac325d` |

No full tests, BindingTests, simulator/device run, corpus generation, or
`nuke validate` was repeated during this bounded repair pass. Free space afterward
was 16,077,456 KiB (15.33 GiB), above the required binary 12 GiB reserve.

Free space began at approximately 16 GiB. Before `nuke test`, rounded `df -h`
reported 12 GiB; the exact post-run value was 12,521,420 KiB (11.94 GiB), a brief
61,492 KiB shortfall against a binary 12 GiB reserve. Further validation stopped
immediately. Cleaning only generated Debug solution and simulator-app artifacts
restored 16,112,832 KiB (15.37 GiB). This shortfall and the omitted canary are
recorded rather than hidden; the read-only stale corpus was untouched.
