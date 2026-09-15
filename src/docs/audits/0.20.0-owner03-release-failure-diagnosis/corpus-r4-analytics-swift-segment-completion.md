# OWNER-03 Segment completion receipt

## Scope

- Base commit: `eb8d315fca7828adf7dbaeb749ecb559f1e76797`
- Base tree: `c0d1bc9cd5b3c2bf78d02fa0e713dac4e7bf0de1`
- Corpus: `analytics-swift` 1.7.3 / `Segment`
- Constraints: generator fixes only; no Segment-specific branches, generated-output rewrites,
  weakened gates, skipped stages, or timeout widening.

This batch closes the two remaining Segment failure categories from the corpus audit: emitted
support code whose BCL names are shadowed by the generated `Segment.System` type, and Swift
wrappers that resurrect ABI-only declarations which a separately compiled wrapper module cannot
name.

## Root causes and fixes

### BCL name shadowing

Support-code emitters wrote namespace-relative `System.Exception`, `System.Action<T>`,
`System.Span<T>`, and (in the async `[String]` return path)
`System.Collections.Generic.List<T>`. A generated type named `System` in the binding namespace
therefore captured those references and produced CS0426. The emitters now root-qualify those BCL
types with `global::`.

Changed production paths:

- `src/Swift.Bindings/src/Emitter/StringEmitter/ErrorRegistryHelperEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AsyncHarnessEmitter.cs`

### Wrapper isolation

The parser had already classified `process(incomingEvent:enrichments:)` as module-internal because
it is present in ABI JSON but absent from the public swiftinterface. Concrete-specialization
discovery independently scanned all methods and resurrected it. It now rejects module-internal and
SPI methods at discovery.

Fresh generation then exposed the same isolation defect for ABI-only conformers: CSM offered
`Segment.UserInfo`, and protocol-extension default injection synthesized wrappers for
`Segment.iOSLifecycleEvents` and `Segment.iOSLifecycleMonitor`. A shared wrapper-accessibility
predicate now rejects a same-module type, or a type nested under a same-module enclosing type, when
it is module-internal or SPI. Foreign extension receivers remain eligible because their local
visibility flag means only that their definition comes from another module.

Changed production paths:

- `src/Swift.Bindings/src/Emitter/StringEmitter/WrapperValidation.cs`
- `src/Swift.Bindings/src/Marshaler/ConcreteSpecializationEngine.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ProtocolExtensionEmitter.cs`

The CSM ABI indexes still retain inaccessible declarations so current ABI evidence can disprove a
stale conformer hint; those declarations are filtered only from candidates offered to wrapper
generation.

## Regression coverage

Focused unit coverage was added for:

- Error-registry BCL qualification in a module that declares a generated `System` type.
- Optional generic property-setter `Span<byte>` qualification.
- Async `[String]` return support-code qualification.
- Module-internal and SPI generic methods not being resurrected by CSM discovery.
- Module-internal and SPI conformers not being offered to CSM wrappers.
- Module-internal and SPI conformers not receiving protocol-extension default wrappers.
- Public nested types under module-internal and SPI same-module parents being unavailable to
  wrappers, while internal-marked foreign receivers remain eligible.
- Curated hints not resurrecting a module-internal or SPI conformer suppressed from the ABI
  wrapper-candidate list.

The existing BindingTests async-array fixture and the complete BindingTests corpus were freshly
regenerated, compiled, and run. The Segment package itself is the negative-space integration canary
for the ABI-only method and conformer cases.

## Validation evidence

All commands ran on macOS without stage skips or timeout overrides.

| Evidence | Result | SHA-256 |
| --- | --- | --- |
| `/private/tmp/owner03-segment-next-targeted-unit-r5.log` | Final focused emitter/marshaler tests: 393 passed | `82c79a19d09372f99a031d44a32253d4b28788d48d4c2acd144fe7cfc6c9ead4` |
| `/private/tmp/owner03-segment-next-test-r2.log` | Final `nuke test`: generator 19,103 passed, 2 skipped; analyzer 79 passed; runtime library 924 passed, 1 skipped | `40f6e8912262ec4813fcd0eb92516dfedd73d0b9bbad04695c79773186a99911` |
| `/private/tmp/owner03-segment-next-binding-compile-r1.log` | Fresh BindingTests generation and compile: 1,708 C# files, 3 Swift wrapper files, 0 compile errors | `82a90d1af098015af65c4b1faef3e50f73106b75332433c5ace792d80c717c0a` |
| `/private/tmp/owner03-segment-next-binding-runtime-r1.log` | Full BindingTests simulator run: 4,076 passed, 32 skipped | `2d81d5c22da1bc01d0b0f5bb9d164d62c78b32b6fb36a03595a2a563ca0c0843` |
| `/private/tmp/owner03-segment-next-generate-r3.log` | Fresh official Segment generation, built-in C# compile verification, and wrapper XCFramework build passed | `1060e1bfb170a6c940f73a82578c5116ffe68e8cbb9c1197e143d8d2063909c8` |
| `/private/tmp/owner03-segment-next-managed-build-r1.log` | Independent unmodified generated `Segment.Swift.iOS.csproj` build: 0 errors | `ab738d56cefe4aece39aef1742c3875975eb88189f30e0ae11eebb2ae6a3b0b9` |

Fresh Segment output: `/private/tmp/owner03-segment-next-r3`.

- Wrapper SHA-256: `300da4b8a24d23628c4bd3504869b50b0cbdc50c3eac47dc0d7eb4e87ebc872c`
- Generated project SHA-256: `ed3bdbf2b5536e1ec048b5a286e0015225e27b4dedc532a85c7ba991f449c89f`
- The wrapper contains no `incomingEvent:` entry and no references to `Segment.UserInfo`,
  `Segment.iOSLifecycleEvents`, or `Segment.iOSLifecycleMonitor`.
- Generated C# contains root-qualified `global::System.Exception`,
  `global::System.Action<IntPtr>`, and `global::System.Span<byte>` support references.

The validation baselines record a final unit-test pass floor of 19,103. BindingTests runtime
improved from 4,064 to 4,076 passing cases at the unchanged 32-skip set.

## Final review

Per the batch instruction, the final external review was Grok-only; Claude was never invoked.

- Stable Grok session: `01a0a649-62c0-7d61-b42a-f4dce6ed981e`
- Captured report: `/private/tmp/owner03-segment-grok-review-r2/result.md`
- Report SHA-256: `899bcbf636098f19b7abcc301ac35fde526f4ce3092c9e63baf093415fe5726e`
- Raw response SHA-256: `fcf7fc4fe9555b6bb85e005b02aa50c1ee061f312803955aae79e406328bd343`

Grok reported no Critical, High, or functional correctness defect. It retained one Medium coverage
finding: the new shared predicate's same-module nested-parent and foreign-receiver arms were not
independently pinned. It retained one Low coverage finding: a curated specialization hint could
exercise a distinct resurrection path that the initial tests did not reach. It explicitly dropped
the suggested parent-type method-discovery expansion as pre-existing and outside the Segment
trigger, and dropped concerns about ABI cross-check weakening, the unit-floor arithmetic, frozen
struct behavior, and unrelated scope after inspecting the corresponding code and evidence.

Both retained coverage findings were accepted. The final fix pass added direct same-module nested
internal/SPI and foreign-receiver tests plus a curated-hint internal/SPI resurrection test. The
affected slice passed 393 tests and the final full `nuke test` passed with the results above. Per the
paired-review stopping rule for a Medium/Low-only round, no second review was started.
