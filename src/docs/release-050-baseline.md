# Release 0.5.0 Baseline

**Established**: March 30, 2026
**Git SHA**: 192976a3 (post Sessions 1-3)

---

## Compile Gate

90 validation targets across 46 libraries (Tier 1 + Tier 2).

| Metric | Count |
|--------|-------|
| CS compile pass | 89/90 |
| CS compile fail | 1 (SkeletonView — known: internal types in wrapper) |
| Swift wrapper pass | 54/56 |
| Swift wrapper fail | 2 (GRDB, Kingfisher — known wrapper compilation issues) |
| ObjC / no wrapper | 34 |

## Skip Metrics

Across all 90 validation targets:

| Metric | Value |
|--------|-------|
| Emitted members | 9,953 |
| Skipped members | 1,920 |
| Skip rate | 16.2% |

### Skip Reasons (full validation, 90 targets)

| Reason | Count | % of skips |
|--------|------:|----------:|
| ModuleInternal | 709 | 36.9% |
| UnsupportedSignature | 354 | 18.4% |
| AnyTypeFallback | 298 | 15.5% |
| SynthesizedCodable | 155 | 8.1% |
| EveryProtocolConformanceSkipped | 149 | 7.8% |
| UnsupportedClosure | 149 | 7.8% |
| UnsatisfiedGenericConstraint | 97 | 5.1% |
| UnsupportedType | 81 | 4.2% |
| GenericProtocolConstraint | 67 | 3.5% |
| UnsupportedExistential | 41 | 2.1% |
| SwiftUIConstraint | 33 | 1.7% |
| StaticProtocolMember | 21 | 1.1% |
| SwiftUIView | 19 | 1.0% |
| GenericTypeCallback | 14 | 0.7% |
| DuplicateSignature | 3 | 0.2% |
| UnderscorePrefixInternal | 2 | 0.1% |
| ActorIsolatedAsyncStream | 1 | 0.1% |

### Sim-validation skip metrics (15 libraries)

Higher-fidelity numbers from the 15 sim-validation libraries with runtime test coverage:

| Metric | Value |
|--------|-------|
| Emitted members | 2,850 |
| Skipped members | 806 |
| Skip rate | 22.0% |

The higher skip rate for sim-validation reflects that these are larger, more complex libraries. The full validation set includes many smaller ObjC/Firebase libraries with near-zero skips.

## Unit Tests

| Suite | Passed | Skipped | Total |
|-------|-------:|--------:|------:|
| Swift.Bindings.Unit.Tests | 9,174 | 1 | 9,175 |
| Swift.Analyzers.Tests | 20 | 0 | 20 |
| Swift.Runtime.Tests | 510 | 1 | 511 |
| **Total** | **9,704** | **2** | **9,706** |

## BindingTests Runtime (iOS Simulator)

| Metric | Value |
|--------|-------|
| Passed | 1,260 |
| Skipped | 9 |
| Failed | 0 |

## Downstream: sim-validation (15 libraries)

All 15 libraries pass on iOS Simulator with 0 failures:

| Library | Pass |
|---------|-----:|
| Alamofire | 42 |
| Kingfisher | 38 |
| BonMot | 38 |
| RxSwift | 36 |
| KeychainAccess | 34 |
| SwiftyBeaver | 33 |
| Starscream | 30 |
| PhoneNumberKit | 30 |
| Swinject | 28 |
| DeviceKit | 26 |
| SnapKit | 24 |
| CryptoSwift | 24 |
| ObjectMapper | 23 |
| Reachability | 15 |
| XMLCoder | 13 |
| **Total** | **434** |

## Downstream: swift-dotnet-packages (5 libraries)

| Library | Pass | Skip |
|---------|-----:|-----:|
| Lottie | 85 | 4 |
| Nuke | 75 | 2 |
| BlinkID | 20 | 0 |
| BlinkIDUX | 11 | 2 |
| Stripe | 5 | 5 |
| **Total** | **196** | **13** |

## Tooling

- **skip-metrics.py** (`build/scripts/skip-metrics.py`): Aggregates binding-report.json files into structured skip metrics. Supports `--input`, `--output`, `--baseline`, `--json`.
- **.validation-baseline.json**: Now includes `skip_metrics` section with emitted/skipped counts and per-reason breakdown. Updated automatically by `nuke validate` on full runs.
