# OWNER-03 swift-numerics protocol-ownership repair

Date: 2026-09-15

Base: `fddbca49640f0c87295e580ffd8a4a0344457f03`

Runtime: macOS (`Darwin`)

Disposition: **the selected swift-numerics root cause is repaired and the isolated
ComplexModule, RealModule, and Numerics products requalify; the historical corpus-r4
aggregate remains blocked by unrelated rows**

## Authority and reproduction

The historical row is recorded in
`corpus-r4-swiftsoup-arrayslice.md`: ComplexModule failed generated C# compilation on
unresolved `IAdditiveArithmetic` and `INumeric` (`CS0246`), RealModule passed, and the
aggregate Numerics product consequently lacked ComplexModule.

This batch used the frozen swift-numerics 1.1.1 XCFrameworks at
`/Users/wojo/Dev/internal-binding-testing/corpus-sweep/xcframeworks/swift-numerics`.
The conversion receipt is schema 1, run
`61e1bae79761455283e89ff4621c4eed`, Swift 6.2.4, status `success`, with all expected
products produced and no missing inputs or failures. The inputs and candidate file
were copied into a fresh isolated `/private/tmp` sweep root and `diff -qr` matched the
authoritative XCFramework tree exactly. Conversion was skipped; there was no package
rebuild or input substitution.

The unchanged harness was
`/Users/wojo/Dev/internal-binding-testing/corpus-sweep/scripts/run_library.py` at
internal-binding-testing commit `69f80c4996160f952408e882b3b8396398de92b1`, SHA-256
`40acd5aac9a29e1b9488291c4f347009782e872149e84d58d94d00314a941282`.
The copied candidates file SHA-256 was
`4ae6935e0ae6f80bd9abdc43f738ab6e911a39d42aa30114257bdc0a94b518d3`.

The clean pre-fix reproduction used generation attempt
`1789498973653758000-73391`; its `result.json` SHA-256 was
`36c29d8ec854227fff5f692b61f6185ecabe14c116bdf06cb2f8de52a2416e46`.
RealModule was `real_verdict_pass`. ComplexModule failed generation verification and
managed compilation at `ComplexModule.Types.Complex.cs:805` and `:809` with the two
expected `CS0246` diagnostics. Numerics was correctly withheld as
`named_missing_input` because ComplexModule did not publish.

## Root cause and repair

The ABI preserves the source identities `Swift.AdditiveArithmetic` and
`Swift.Numeric`, but RealModule physically emits their generated C# interfaces as
`RealModule.IAdditiveArithmetic` and `RealModule.INumeric`. `ModuleProcessor` registered
those retained foreign protocols using the source Swift module for both the managed
namespace and interface naming. `RealModuleDatabase.xml` therefore advertised both
interfaces in namespace `Swift`, while the declarations were in `RealModule`.

Downstream conformance emission trusted that database record. The Swift namespace is
implicitly imported, so ComplexModule emitted bare `typeof(IAdditiveArithmetic)` and
`typeof(INumeric)`, which do not exist. This was a protocol registration ownership
error, not a ComplexModule conformance-emitter or serializer defect.

`ModuleProcessor.RegisterProtocolType` now retains the original `SwiftTypeName` lookup
identity but derives the managed namespace and interface naming from the module being
generated—the same module that owns the emitted C# interface. The post-fix database
therefore records `RealModule.IAdditiveArithmetic` and `RealModule.INumeric`, and
ComplexModule emits those fully qualified names without any output rewriting or
manual normalization.

The focused theory covers both failed protocol names, a non-default namespace pattern,
the retained Swift lookup identity, and the corrected current-module C# owner. The
unit-test pass floor increased from 19,103 to 19,105 for the two new cases.

## Isolated downstream requalification

The post-fix run used fresh isolated root
`/private/tmp/owner03-numerics-postfix-RD9NVu` and generation attempt
`1789499265921485000-74399`. Its `result.json` SHA-256 is
`fda5058197d6a1ce4deccfb619200d9cd84e4f16920c77ccb613aa18e1fa9d67`.

| Product | Generation | Managed compile/package | Verdict |
|---|---:|---:|---|
| RealModule | exit 0 | exit 0 | `real_verdict_pass` |
| ComplexModule | exit 0 | exit 0 | `real_verdict_pass` |
| Numerics | exit 0 | exit 0 | `real_verdict_pass` |

The overall harness status is `ok`. ComplexModule imports RealModule and now contains
`typeof(RealModule.IAdditiveArithmetic)` and `typeof(RealModule.INumeric)` at lines 805
and 809. Numerics imports both successful siblings. No product was skipped, no
downstream output was edited, and nothing was published.

## Validation and review

| Gate | Result |
|---|---|
| focused `ModuleProcessorProtocolOwnershipTests` | PASS: 2/2 |
| `nuke test` | PASS in 2:08: 19,105 generator tests (2 known skips), 79 analyzer tests, 924 runtime tests (1 known skip), 65 withdrawal assertions; log SHA-256 `e635fb246d0af6ef4f60b450efef2891307b810e548094eff287479bb448454e` |
| `nuke binding-tests --compile-only` | PASS in 4:37: 1,708 C# files and 3 Swift wrapper files; generated C#, Swift wrapper, bridge, parity, manifest, recovery, ingestion, overload-name, and closure-delegate gates green; log SHA-256 `7248614a6d0a4d3560bca18ad140b91b73bdaf57674db81f1779637696840f94` |
| isolated swift-numerics harness | PASS: all three named products generated, compiled, and packed |

An earlier BindingTests invocation reached successful C# and Swift compilation but
was intentionally rejected by its final provenance check because a source comment
changed during that invocation. That result was discarded. The table records the
subsequent frozen-source rerun, whose current-invocation provenance passed.

### Grok-only final review

Grok session `01a0a68a-900b-7541-b7e0-a192da9b1c34` completed against the frozen
four-path staged batch. The report SHA-256 is
`d881411e60b206dc5017c3e7b8c2ab1847ea6099e7c20e77061b4f8750169cb4`.
It reported no Critical, High, or Medium findings and found no current release blocker.

The sole Low finding is accepted as non-blocking post-release test backlog: the unit
theory pins the two Numerics failures, custom namespace mapping, and retained Swift
identity, but does not separately pin a local-protocol control, runtime `ISwift*`
naming, or an XML load round trip. The fresh real Numerics pipeline exercises the
default namespace, database serialization/load, downstream `typeof` qualification,
and compilation, so this gap does not weaken the repaired release path. It is not
expanded in this batch.

Codex adjudicated the remaining review candidates as follows:

- Dropped a runtime-protocol remap concern: the protocol emitter already names from
  the current `ModuleDecl`, and canonical Swift runtime records are protected by
  `KeepExisting` when overlay records are rehomed.
- Dropped a missing cross-module registration concern: `AddModuleDatabase` already
  rehomes records whose Swift identity belongs to another module; the post-fix
  ComplexModule lookup proves this path.
- Dropped use of `ResolveEmittingModuleName` for protocols: that helper intentionally
  leaves top-level foreign struct/class re-exports foreign and would recreate this
  protocol bug.
- Consolidated the suggested local/XML coverage additions into the accepted Low
  backlog above. No High or Medium candidate remained inconclusive.

No full corpus, merge, push, or package publication was run. The dirty external
checkout and preserved stale corpus/Trash outputs were read only. The task stayed
above the required binary 12 GiB free-space reserve.
