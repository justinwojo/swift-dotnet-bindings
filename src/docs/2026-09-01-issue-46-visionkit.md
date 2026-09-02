# Issue #46: VisionKit binding fails — findings & analysis

Date: 2026-09-01
Report: https://github.com/justinwojo/swift-dotnet-bindings/issues/46 (reporter: Felix-Dev)
Status: FIXED on main (2026-09-01, uncommitted wave) — all four fix streams landed: multi-document
`.tbd` parsing (S1), NSString-backed Apple enum records (S2), the async-accessor interface oracle
(S3), and direct-mode verify-recover (S4). The reproduction below now yields exit 0, 19/19 types,
122/124 members, `await __self` async accessors, 2 `extension EveryProtocol`, 0 Tq misses, and
`Barcode(IEnumerable<VNBarcodeSymbology>)` emitted, with zero recovery rounds. Residual skips are
the benign conformance/witness rows plus 2 pre-existing member skips (`selectedRanges`
AnyTypeFallback; one `analyze` overload via unregistered CoreVideo). Leftovers are routed to
`not-planned.md`. The analysis below is kept as the record of the diagnosis.

## The report

The reporter bound Apple's VisionKit (they want `DataScannerViewController`) with the documented
Apple-framework-yourself recipe on `SwiftBindings.Sdk/0.19.2`:

```xml
<Project Sdk="SwiftBindings.Sdk/0.19.2">
  <PropertyGroup><TargetFramework>net10.0-ios26.5</TargetFramework></PropertyGroup>
  <ItemGroup>
    <SwiftAppleFrameworkTarget Include="VisionKit"><MinDeploymentVersion>18.0</MinDeploymentVersion></SwiftAppleFrameworkTarget>
  </ItemGroup>
</Project>
```

`dotnet build` fails in the Swift wrapper compile:

```
VisionKit.Wrapper.swift:785:18: error: 'async' property access in a function that does not support concurrency
```

They also asked whether the 32 `ConformanceProtocolNotInTypeDatabase` rows in `binding-report.json`
matter, and whether the failure is a setup problem or a real interop gap.

**Short answer: not a setup problem, and not the async-support gap it looks like.** One generator bug
(a `.tbd` parser defect) produces the compile error *and* silently disables the delegate path; one
independent, smaller bug breaks the C# compile behind it. Both are ours. The reporter's use case is
otherwise well covered.

## Reproduction

Reproduced exactly at HEAD (`e0ce8272`) on Xcode 26.3 / iOS 26.2 SDK by replicating the SDK's
Apple-direct invocation (`Sdk.targets` `_DumpAppleFrameworkAbi` + `_GenerateSwiftBindingsAppleFramework`):

```bash
SDK=$(xcrun --sdk iphonesimulator --show-sdk-path)
FW=$SDK/System/Library/Frameworks/VisionKit.framework
xcrun swift-api-digester -dump-sdk -module VisionKit -target arm64-apple-ios18.0-simulator -sdk "$SDK" -o VisionKit.abi.json
dotnet exec src/Swift.Bindings/src/bin/Debug/net10.0/Swift.Bindings.dll \
  -a VisionKit.abi.json -d "$FW/VisionKit.tbd" -t "$FW/VisionKit.tbd" \
  -s "$FW/Modules/VisionKit.swiftmodule/arm64-apple-ios-simulator.swiftinterface" \
  -l '\@rpath/VisionKit.framework/VisionKit' -o out \
  --platform ios --platform-target simulator --platform-version 26.2 --swift-runtime-version 0.19.2 \
  --wrapper-architectures simulator --package-id VisionKit.Binding --assembly-name VisionKit.Binding \
  --apple-version 26.2.4 --sdk-mode -v 2
```

Same file, same line 785, same error. The HEAD `binding-report.json` is identical to the reporter's on
every count and on the entire 44-row skip set, so the reporter's Xcode 26.6 beta / iOS 26.5 SDK is
immaterial — nothing relevant in VisionKit's interface changed between 26.2 and 26.5.

CLI footguns hit while reproducing (worth remembering, not user-facing): `-l "@rpath/…"` must be
written `\@rpath` or System.CommandLine treats it as a response file; `--apple-version` must have an
integer major (`0.19.2` is rejected); `--wrapper-architectures all` is rejected in direct mode.

## Root cause 1 — `YamlLikeTbdFormatParser` cannot read multi-document `.tbd` files

`VisionKit.tbd` is a **two-document** YAML file because VisionKit declares `reexported-libraries`:

```
1:   --- !tapi-tbd
4:   install-name:    '/System/Library/Frameworks/VisionKit.framework/VisionKit'
6:   reexported-libraries: …
475: --- !tapi-tbd
478: install-name:    '/System/Library/PrivateFrameworks/DocumentCamera.framework/DocumentCamera'
611: ...
```

`src/Swift.Bindings/src/Demangler/TbdParser/Parsing/YamlLikeTbdFormatParser.cs` consumes only the
*first* `--- !tapi-tbd` marker (`:50-55`) and then loops to EOF, breaking only on `...`. The second
marker has no colon, so `ParseKeyValuePair` throws, the exception is logged and swallowed (`:81-84`),
and parsing continues into the second document with the **same `tbdFile` object**. Then:

```csharp
:126  tbdFile.Exports = ParseExports(lines, ref lineIndex);   // assignment, not append
```

The second document's `exports:` replaces the first's (`install-name`, `targets`, `tbd-version`,
`swift-abi-version` are overwritten the same way). Measured on the real file: the parse yields
**138 symbols, all DocumentCamera's** — VisionKit's own **507 Swift symbols are discarded**. The `-v 2`
log corroborates: `Successfully parsed TBD file version 4 with 1 exports`, `SWIFTBIND058` demangle
warnings quoting *DocumentCamera* symbols, and `Unknown top-level key:` for `reexported-libraries`.

`DemanglingResults.AllSymbols` (`Demangler/DemanglingResults.cs:84-101`) is built from those exports.
Two independent generator decisions read that set, and both go wrong when it is empty of the module's
own symbols:

### Symptom 1a — async accessors emitted as synchronous (the reported compile error)

Accessor async-ness has exactly **one oracle** in the generator. The swift-api-digester ABI JSON has
no async flag on accessor nodes (verified by walking the real output — it carries `throwing`, nothing
else), and an async *accessor*'s mangled name has no `Ya` marker (it's a plain `…vg`). The only
ABI-visible evidence is a sibling symbol in the TBD: `{getter}Tu` (async function pointer) or
`{getter}TjTu` (through the class dispatch thunk). So:

- `Parser/SwiftABIParser.cs:3110` — `isAsync = ManglingProbes.IsAsyncAccessor(_demangledTbd.AllSymbols, accessor.MangledName)`
- `Parser/ManglingProbes.cs:71` — `tbdSymbols.Contains(mangled + "Tu") || tbdSymbols.Contains(mangled + "TjTu")`

With VisionKit's symbols gone, every VisionKit accessor reads as sync. Async *methods* are unaffected
because they derive async from their **own** mangled name (`SwiftABIParser.cs:2677`,
`HasAsyncMarker`) — which is why `capturePhoto() async`, `subject(at:) async`, `image(for:) async
throws` all bound fine while both `get async` properties failed. That asymmetry is the whole story.

VisionKit has two async accessors, and they fail differently downstream:

| Declaration | `IsAsync` seen | `Throws` seen | What happened |
|---|---|---|---|
| `ImageAnalysisInteraction.subjects: Set<Subject> { get async }` (interface L228) | false | false | Both wrapper-eligibility gates in `PropertyWrapperEmitter.cs:110-121` pass → sync `@_cdecl` getter emitted → **wrapper compile error** (the reported failure) |
| `ImageAnalysisInteraction.Subject.image: UIImage { get async throws }` (L219) | false | true | `SWIFTBIND107` throwing-getter wrapper rejection (`PropertyWrapperEmitter.cs:119`) → no wrapper → falls through to a **direct `CallConvSwift` P/Invoke onto `$s…C7SubjectV5imageSo7UIImageCvg`** with `ref SwiftError` (`VisionKit.Types.ImageAnalysisInteraction.cs`). That symbol is the *async* entry (its `…CvgTu` twin exists). Compiles, ships, ABI-mismatched at the first `subject.Image` read. |

The second row is the nastier one: fixing only the compile error would ship a latent crash.

**The generator already supports `get async` properties end to end** — `PropertyHandler.cs:415-421`
routes them through `EmitAsyncPropertyAsMethods` (`:1545+`) into the ordinary async-method machinery
(`AsyncResultPlanner`, `_async` entry points), covered by
`BindingTests/Sources/SwiftBindingsTestLib/Async/AsyncProperties.swift` and four passing
`AsyncPropertyTests`. Nothing in that path needs work; it simply never fired because the parser flag
was false. (That fixture's header comment still says "Async properties are not yet supported by the
generator" — stale, fix it.)

### Symptom 1b — both delegate proxies suppressed (the delegate path is a silent no-op)

`DataScannerViewControllerDelegate` and `ImageAnalysisInteractionDelegate` are public, non-`@objc`,
`@MainActor`, class-bound protocols. For non-`@objc` protocols `SwiftABIParser.cs:1679-1725` probes
the TBD for each requirement's `…Tq` method descriptor and sets `HasMissingTbdMethodDescriptors` on the
first miss; `EveryProtocolEmitter.cs:2504-2510` then skips the conformance
(`MissingTbdMethodDescriptors`), `ProtocolProxyEmissionPolicy.cs:55-110` suppresses the proxy, and
`ProtocolHandler.cs:804-819` raises `EveryProtocolConformanceSkipped` — which cascades into the five
`SuppressedProxyMemberDegraded` rows (`delegate` getter/setter on both types, plus the
delegate-taking `ImageAnalysisInteraction.init`).

The `.tbd` **does** contain all six `DataScannerViewControllerDelegate` `…Tq` descriptors (grep-verified
in both device and simulator SDK copies). The gate's logic is right; its input is wrong. The `-v 2`
log names the miss verbatim — `required method 'dataScannerDidZoom' has no Tq method descriptor in
TBD ($s9VisionKit33DataScannerViewControllerDelegateP04dataD7DidZoomyyAA0cdeF0CFTq missing)` — for a
symbol that is at line ~300 of the file. The report row self-flags `AttributionConfidence: Low`, which
in hindsight was the generator saying it didn't trust its own answer.

Ruled out explicitly (each hypothesis checked against the code path): `@MainActor` isolation (no
actor predicate exists anywhere in the 19-token EveryProtocol skip set), class-bound `AnyObject`
(`EveryProtocolEmitter.cs:6773-6775` deliberately does not skip on it), requirement payload types (the
gate looks only at mangled-name presence), `weak` storage, and the Facebook "public-@objc" precedent
(these protocols are the *non*-@objc branch, which is exactly what routes them into the probe).

Consumer-visible effect today: `IDataScannerViewControllerDelegate` is emitted with all six callbacks,
`scanner.Delegate = myDelegate` **compiles and runs**, and the callbacks **never fire**. The getter is
`[Obsolete(error: true, DiagnosticId = "SB0006")]`. Since Apple's docs present the delegate as the
primary API, this is what the reporter would have hit next.

### Controlled experiment

Same generator, same ABI JSON, same `.swiftinterface`, with the second `.tbd` document truncated
(`head -474` + `...`):

| | real 2-doc `.tbd` (reproduces reporter) | 1-doc `.tbd` |
|---|---|---|
| Swift symbols in `AllSymbols` | 129 (all DocumentCamera) | 507 (VisionKit) |
| generator exit | 1, wrapper compile failure | **0**, wrapper xcframework built |
| `subjects` | sync `let result = obj.subjects` | `let resultgetSubjects = await __self.subjects` |
| `Subject.image` | sync P/Invoke on async symbol | `public Task<UIKit.UIImage> GetImageAsync(…)` |
| `extension EveryProtocol` blocks in wrapper | 0 | 2 (both proxies) |
| `Tq method descriptor` misses in log | 6 | 0 |
| Emitted / skipped members | 116 / 12 | 119 / 5 |
| `DegradedConsumeCount` | 3 | 0 |
| Wrapper entry points | 162 | 145 (17 degraded/stub routes gone) |

After the fix the report shows 9 `ProtocolWitnessNotDispatchable` rows, 5 on DataScanner callbacks.
These are **produce-direction only** (`BindingReport.cs:542-557`): calling a *Swift-vended* delegate
through the protocol-typed value throws. The direction the reporter needs — C# implements
`IDataScannerViewControllerDelegate`, Swift calls it — is fully wired: all 6 of 6 requirements get
`Receive_*` trampolines in `DataScannerViewControllerDelegateLocalVTable`.

### Blast radius

Of 252 iOS-simulator SDK frameworks with a `.tbd`, **9 are multi-document**:
Accelerate (12 docs), MetalPerformanceShaders (9), **UIKit (8)**, GameKit (5), **AVFoundation (3)**,
PassKit (3), AudioToolbox (2), VisionKit (2), WebKit (2). None of the 17 Apple frameworks currently
shipped from `swift-dotnet-packages` is on that list, so nothing published is affected — but any
future UIKit / AVFoundation / PassKit / WebKit / GameKit binding hits the same wall: every
non-`@objc` Swift protocol spuriously loses its proxy, and every `get async` accessor is emitted sync.
In-tree gates could not see this because BindingTests compile Swift from source and produce
single-document TBDs; `TbdParserTests.cs` has three `--- !tapi-tbd` literals, each a separate
single-document string. The `ManglingProbesTests` exercise the probe against a hand-built set.

## Root cause 2 — `Vision.VNBarcodeSymbology` misclassified as an ObjC class (C# compile error)

Hidden behind the wrapper failure, and **independent** of the TBD bug (it persists in the 1-doc run;
it is the *only* C# error there):

```
VisionKit.Types.DataScannerViewController.cs(1174,83): error CS1061:
  'VNBarcodeSymbology' does not contain a definition for 'Handle'
```

From `DataScannerViewController.RecognizedDataType.barcode(symbologies: [Vision.VNBarcodeSymbology] = [])`
(interface L311) the generator emits `symbologies.Select(e => (IntPtr)e.Handle)` into a
`SwiftArray<IntPtr>`. `VNBarcodeSymbology` is an `NS_STRING_ENUM` (`Vision/VNTypes.h:50`,
`typedef NSString *VNBarcodeSymbology NS_STRING_ENUM`): Swift imports it as a `RawRepresentable`
struct over `String`, and Microsoft.iOS projects it as a plain C# `enum` with `GetConstant()`.

Mechanism: Vision's entry in `src/Swift.Bindings/src/Data/apple-frameworks.json:1015` is
`{"module": "Vision", "autoBridge": true, "optionalFallback": true, "wrapperImportable": true, "objcPrefixes": ["VN"]}`
with **no `valueTypes`**. `MarshallingHelpers.IsObjCPrefixBridgeCandidate` (`Marshaler/MarshallingHelpers.cs:236`)
then classifies any un-recorded `VN*` type as an ObjC class. Its own doc comment names this exact
trap — *"an ObjC prefix alone does not prove a class (e.g. `PassKit.PKPaymentNetwork` is a value
type with a PK prefix)"* — and PassKit's entry lists `PKPaymentNetwork` under `valueTypes` for
precisely that reason. Vision never got the equivalent. The report lists the type under
`ObjCPrefixBridges` and counts the member as *emitted*, so nothing flags it.

Note the shape even once classified correctly: the Swift side wants `[VNBarcodeSymbology]`, an
array of 16-byte String-backed structs, and the C# side has an enum whose `GetConstant()` yields an
`NSString`. This is the NS_TYPED_ENUM bridge family that already exists (see `not-planned.md`
§ "Two minor FB mixed-binding drops (attribution + cross-module typed-enum)"); it needs the
cross-module case, not a new mechanism. The no-argument `GetBarcode()` (all symbologies) is emitted
and fine, so the reporter loses only the *filtered* form once the assembly compiles.

## Everything else in the report

Once both root causes are fixed, the remaining rows are the honest limitations. In order of relevance
to the reporter:

| Item | Assessment |
|---|---|
| `recognizedItems: AsyncStream<[RecognizedItem]>` | **Works today**, in both the broken and fixed runs: `public IAsyncEnumerable<IReadOnlyList<RecognizedItem>> RecognizedItems`, a real `SwiftAsyncStream` bridge with element/completion callbacks, fault channel, and C# cancellation routed to `SBW_CancelTask`. `AsyncStreamHandler` keys off the property *type*, not the TBD, so it was immune to root cause 1. This is the reporter's primary data path. |
| `RecognizedItem` (payload enum) | **Fully bound**: `Tag`, `TryGetText(out Text)`, `TryGetBarcode(out Barcode)`; `Text` → `Id`, `Bounds`, `Transcript`, `Observation (VNRecognizedTextObservation)`; `Barcode` → `Id`, `Bounds`, `PayloadStringValue`, `Observation (VNBarcodeObservation)`. Covered by `ClassPayloadEnum` fixtures. **The wiki is stale** — `Supported-Features.md:10,207-210` and `How-Bindings-Map.md:106-126` still document the older `IsXxx`/`XxxValue` shape. |
| `DataScannerViewController` init / `StartScanning` / `StopScanning` / `IsScanning` / `RecognizedDataType.text(…)` / all zoom, guidance, ROI, `IsSupported`/`IsAvailable` members / `GetCapturePhotoAsync` | All emitted, full fidelity. |
| `ImageAnalyzer.analyze` — 4 of 6 overloads skipped `UnsupportedSignature` | `Unprojected Apple type: ImageIO.CGImagePropertyOrientation`. `ImageIO` is in `apple-frameworks.json:509` with no `valueTypes`, so the enum never registers. One data line recovers the `CGImage` / `CIImage` / `CVPixelBuffer` / `URL` overloads. The two `UIImage` overloads are emitted. (Cosmetic emitter smell: the second one names its parameter `pixelBuffer` while casting `as! UIImage` — sibling-overload name bleed.) |
| `ImageAnalysisInteraction.selectedRanges: [Range<String.Index>]` → `AnyTypeFallback` | `Range<T>` is absent from `KnownGenericTypes` (`TypeDatabaseExtensions.cs:983-989`) and `s_stdlibGenerics` (`BoundGenericsHandler.cs:14-17`) while `ClosedRange` is present. Looks unintentional; owner call. `SelectedText` and `SelectedAttributedText` are emitted, so selection is usable. |
| 32 × `ConformanceProtocolNotInTypeDatabase` | **Informational noise, already classified as such** (`SkipDisposition.cs:143` → `KnownLimitation`; written intent in `2026-08-15-next-direction.md:78-82`). A single-framework run loads no stdlib/UIKit protocol databases, so `Hashable`, `OptionSet`, `NSObjectProtocol`, `UITraitEnvironment`, … have no record to name a C# interface after. No ergonomics are lost: `InteractionTypes`/`AnalysisTypes` get synthesized `\|`/`&`/`^`/`~`/`Contains`, `Set<RecognizedDataType>` init parameters bind. They are 32 of the 44 `PublicSurfaceLost` rows and dominate the report's apparent severity — that is how this issue came to look worse than it is. Reporting-only change worth considering: suppress or fold them for system-module protocols. |
| `OverloadRenames` (8) | All `LabelDerived`, no numeric suffixes — policy-compliant. `DataScannerDidAddAllItems`, `InteractionShouldBeginAtFor`, etc. Verbose but stable. |
| `ObjCPrefixBridges` (3) | `UIFont`, `UIViewController` correct; `VNBarcodeSymbology` is root cause 2. |
| `SwiftBindings.Apple` dependency | The emitted C# references `Swift.Foundation.AttributedString`; the SDK adds the supplement implicitly (`Sdk.props:148`). Not a blocker. |

### What the reporter gets once both root causes are fixed

init → `StartScanning()` → `await foreach (var batch in scanner.RecognizedItems)` → `TryGetText` /
`TryGetBarcode` → `StopScanning()` works via the stream route, and with the TBD fix the delegate route
works too (C# implements `IDataScannerViewControllerDelegate`, receives all six callbacks including
the `RecognizedItem` / `[RecognizedItem]` payload arguments). The only DataScanner-relevant loss left
is `RecognizedDataType.Barcode(symbologies:)` until root cause 2 lands; `GetBarcode()` covers the
all-symbologies case meanwhile.

## Secondary defects found on the way (not blockers, worth logging)

1. **Wrapper-eligibility rejections are not `SkippedItems`.** A `WrapperEligibility.Reject`
   (`PropertyWrapperEmitter.cs:110-121` — `async_property`, `SWIFTBIND107`) shows up as an anonymous
   count in `binding-emission-report.json` and a log line, never as a named row in
   `binding-report.json`. From the consumer's side the member vanishes — the exact "silent drop"
   the reporter noticed. `SkipReason.AsyncProperty` already exists; a throwing-getter reason would
   need adding. Worse, as `Subject.image` shows, the rejection can fall through to a direct P/Invoke
   rather than dropping the member.
2. **Apple-direct mode has no Swift-plane attribution or recovery.** `BindingsGeneratorCommand.cs:1050-1082`
   wires `verifyRecoverCompile` only when `resolution != null` (xcframework mode); `--sdk-mode` gets a
   C#-plane-only loop (`:1108-1114`) and its wrapper-compile failure path (`:1813-1823`) calls
   `EmitCommandFailureReport` with no attribution pass — hence `RecoveryRounds: 0`,
   `AttributedUnits: []`. The attribution stack (`WrapperBlockIndex`, `SwiftDiagnosticParser`,
   `DiagnosticAttributor`) would have mapped line 785 to the `subjects` unit; it simply never ran. One
   bad wrapper block in an Apple framework = whole-file failure with no named culprit.
3. **`AsyncSequence` *conformance* adoption drops silently** (`AsyncSequenceEmitter.cs:31-40,58-68`,
   `TypeHandlerHelpers.cs:1342-1348`) with no report row and no roadmap/not-planned/wiki entry.
   Unrelated to #46 (VisionKit uses a concrete `AsyncStream`, which is handled), but the same
   reporting-gap class.
4. **No `get async throws` fixture** anywhere in BindingTests — the exact shape of `Subject.image`.
5. **No zero-own-symbols tripwire.** A parsed TBD whose symbol set contains nothing matching the
   module's `install-name` is always wrong; a cheap warning there would have named this bug in the
   reporter's log.

## Recommended fix plan (priority order)

1. **Multi-document `.tbd` parsing** — `YamlLikeTbdFormatParser.cs`. Start a new document on each
   `--- !tapi-tbd`; accumulate `Exports` across documents (or select the document whose `install-name`
   matches the framework being bound — decide deliberately; today's last-wins is accidental, and
   first-wins for `install-name`/`targets`/`tbd-version` is the sane default). Ship with (a) a
   two-document literal in `TbdParserTests.cs` asserting the first document's symbols survive, (b) a
   BindingTests-layer check that exercises the Apple-direct path on a multi-document SDK framework —
   VisionKit itself is the natural one (`get async` + non-@objc `@MainActor` delegate in one small
   module), and UIKit's 8-document `.tbd` is the highest-value regression parse. Per the freeze
   policy no new prediction gate is warranted: once the TBD parses, the existing
   `PropertyWrapperEmitter.cs:110` gate and `PropertyHandler.cs:415` async path fire on their own.
2. **`VNBarcodeSymbology` as a value type** — add it to Vision's `valueTypes` in
   `apple-frameworks.json` (the `PKPaymentNetwork` precedent), then make sure the NS_TYPED_ENUM
   bridge handles the cross-module `[TypedEnum]` array parameter; fixture: an Apple-direct or
   mixed-fixture member taking `[SomeStringEnum]`. Also the `CGImagePropertyOrientation` data line
   for `ImageIO` — same file, recovers 4 `analyze` overloads.
3. **TBD-independent async-accessor fact** (defence in depth). `ExtensionsWalker.swift:417-434`
   already sees `effects.asyncSpecifier` and uses it only to *exclude* candidates; emit it as a new
   `InterfaceFactKind`, bump `kSchemaVersion` (`Output.swift:30`) and `ExpectedSchemaVersion`
   (`InterfaceFactsJson.cs:73`) in lockstep, OR it into `SwiftABIParser.cs:3110`. Removes the
   single-oracle fragility that let a parser bug silently turn "async" into "sync".
4. **Reporting**: route wrapper-eligibility rejections into `SkippedItems`; add the zero-own-symbols
   TBD warning; run attribution over the Apple-direct wrapper failure before `EmitCommandFailureReport`
   so the failure report names the unit (full recovery in direct mode is the larger version of the
   same change); consider folding `ConformanceProtocolNotInTypeDatabase` for unloaded system-module
   protocols in consumer-facing output.
5. **Docs/test hygiene**: wiki payload-enum member shape (`Tag`/`TryGet{Case}`); stale header in
   `AsyncProperties.swift`; add a `get async throws` property to that fixture; `Range<T>` owner call.

## Suggested reply to the reporter (substance)

- Not a setup problem; two generator bugs, both ours. The headline one is a `.tbd` parser defect —
  VisionKit's `.tbd` re-exports a private framework as a second YAML document and the parser keeps only
  the last document, so the generator never saw VisionKit's own symbols. That is why `subjects { get
  async }` was emitted synchronously (async accessors are detected from the symbol table) and why the
  `DataScannerViewControllerDelegate` proxy was suppressed. A second, smaller bug (`VNBarcodeSymbology`
  treated as an ObjC class) breaks the C# compile behind the first.
- The `ConformanceProtocolNotInTypeDatabase` rows are informational — stdlib/UIKit protocol names a
  single-framework run can't resolve — and cost nothing.
- Their use case is in good shape once fixed: `RecognizedItems` already binds as
  `IAsyncEnumerable<IReadOnlyList<RecognizedItem>>` with `TryGetText`/`TryGetBarcode`, and the
  delegate route comes back with the parser fix. Filtered `Barcode(symbologies:)` is the one member
  that waits on the second fix; `GetBarcode()` covers all symbologies meanwhile.
- Tip for future reports: `-v 2` (SDK: `SwiftBindingsVerbosity`) puts the per-protocol "why" in the
  log; it is what cracked this one.
