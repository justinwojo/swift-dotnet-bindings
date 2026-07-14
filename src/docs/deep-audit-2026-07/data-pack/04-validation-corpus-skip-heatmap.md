# Data Pack — Validation Corpus Skip Heatmap

**Source**: `build/baselines/validation-baseline.json` → `skip_metrics`  
**Note**: This is a **snapshot of a prior validate run** (git_sha in file), not re-run this session. Still the best multi-library skip histogram in-tree.

---

## Headline totals

| Metric | Value |
|--------|------:|
| total_emitted_members | **25,314** |
| total_skipped_members | **7,356** |
| skip_rate_pct | **22.5%** |

### Post-processor sub-causes (residual strip, not emission SkipReason)

| Sub-cause | Count |
|-----------|------:|
| InternalType | 83 |
| NSInvocation | 1 |
| Other | 0 |

---

## Skip reasons across validation libraries (descending)

| Count | Reason | Share of skips | Worker note |
|------:|--------|---------------:|-------------|
| **1420** | UnsupportedSignature | 19.3% | Largest “could bind?” gap |
| **971** | SynthesizedCodable | 13.2% | Expected structural — don’t “fix” |
| **874** | ModuleInternal | 11.9% | Expected non-public |
| **780** | NetUnavailableType | 10.6% | Foundation/OS type missing in .NET |
| **600** | UnsupportedClosure | 8.2% | Closure matrix remainder |
| **450** | DuplicateSignature | 6.1% | Label/projection collapse residual |
| **420** | AnyTypeFallback | 5.7% | Open generic / Any — CSM may recover |
| **392** | GenericProtocolConstraint | 5.3% | PAT / generic protocol |
| **372** | UnsupportedType | 5.1% | |
| **356** | EveryProtocolConformanceSkipped | 4.8% | Reverse-dispatch dead class |
| **272** | UnsatisfiedGenericConstraint | 3.7% | |
| **216** | Pattern2InternalTypeReach | 2.9% | Expected structural |
| **128** | GenericTypeCallback | 1.7% | |
| **124** | SwiftUIConstraint | 1.7% | |
| **95** | UnsupportedExistential | 1.3% | |
| **67** | StaticProtocolMember | 0.9% | Expected structural |
| **64** | **MissingWrapperSymbol** | **0.9%** | **Review-tier integrity residual** |
| **53** | SwiftUIView | 0.7% | Bridge path |
| **19** | NonBlittableCallConvSwift | 0.3% | |
| **12** | OwnedByAppleSupplement | 0.2% | |
| **9** | IndeterminatePwtShape | 0.1% | |
| **7** | ParentModuleInternalNoFallback | 0.1% | |
| **4** | UnderscorePrefixInternal | — | |
| **4** | UnsupportedAsyncStream | — | |

**Not present / zero in this snapshot:** SuppressedProxyMemberDegraded (may be version lag — BindingTests has 63; validate baseline may predate full reporting or libs differ).

---

## Expected vs actionable (rough split)

Using disposition classifier intent:

| Bucket | Approx count | Action |
|--------|-------------:|--------|
| Expected (Codable + ModuleInternal + Pattern2 + Static + Underscore + OwnedByApple + SwiftUIView structural) | ~2,200+ | Do not treat as bugs |
| **KnownLimitation bulk** (signature, closure, NetUnavailable, generics, AnyType, existential, EP skipped, …) | ~4,500+ | Capacity / product roadmap |
| **Review-ish** MissingWrapperSymbol | **64** | Integrity / strip residual — G1-005 |

---

## Compare: BindingTests vs Validate corpus

| Axis | BindingTests | Validate baseline |
|------|-------------:|------------------:|
| Skipped rows | 312 | 7356 members |
| Top reason | SuppressedProxyMemberDegraded | UnsupportedSignature |
| Review | 1 | (MissingWrapperSymbol 64) |
| Strip residual InternalType | 0 (current) | 83 post-processor |

**Insight:** Test-lib is reverse-dispatch heavy; real libs are **signature/closure/internal/codable** heavy. Workers need **both** corpora — G1 kitchen must include UnsupportedSignature + UnsupportedClosure, not only produce-throw.

---

## Compile gate status (same baseline file)

`compile_gate.libraries` samples all show `"compile": "ok"` for listed Apple frameworks (AppIntents, CryptoKit multi-platform, etc.). Snapshot claims a green compile surface at that git_sha — re-validate before trusting for current HEAD.

---

## Worker priority from this heatmap

1. **UnsupportedSignature** (1420) — largest “is this skip honest?” investigation set  
2. **NetUnavailableType** (780) — product/docs (what .NET has) not generator bugs  
3. **UnsupportedClosure** (600) — known matrix remainder  
4. **MissingWrapperSymbol** (64) — integrity Review  
5. **EveryProtocolConformanceSkipped** (356) — reverse-dispatch product  
