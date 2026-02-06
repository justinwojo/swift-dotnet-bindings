# Remaining Work

Consolidated backlog of generator gaps, runtime issues, and infrastructure work. Ordered by priority.

**Date**: February 2026
**Current**: Phase 61 complete, 112 must-pass features (57 passing, 0 degraded, 51 missing from disabled dirs)
**Completed items**: See `CompletedPhases/completed-backlog-items.md` (Phases 45-61)

---

## Real-World Binding Status

Reassessed February 2026 after Phase 60 (async complex type marshalling).

| Metric | BlinkID | Nuke | Lottie |
|--------|---------|------|--------|
| Types emitted | 116/119 (97.5%) | 60/68 (88.2%) | 79/93 (84.9%) |
| Members emitted | 567/572 (99.1%) | 323/342 (94.4%) | 387/428 (90.4%) |
| Members skipped | 5 | 19 | 41 |
| Runtime validated | Yes (**18/18 tests**) | Yes | Yes (9/9 tests) |
| Compile errors | **0** | **0** | 1* |

*\* Lottie has 1 compile error (CS0315) due to `ExistentialContainer0` not implementing `ISwiftObject` constraint — a pre-existing generic constraint issue unrelated to protocol conformance.*

---

## Priority Items

| # | Area | Description | Impact | Priority |
|---|------|-------------|--------|----------|
| 1 | Tooling | Roslyn analyzer for undisposed Swift objects | Compile-time safety | P3 |
| 2 | Research | NativeAOT hands-on validation | Desk research done, testing remaining | P4 |

---

## 1. Roslyn Analyzer for Undisposed Swift Objects

**Priority**: P3
**Area**: Tooling

A Roslyn analyzer can warn at compile time when Swift objects implementing `IDisposable` are created without `using` or explicit `Dispose()`.

**What's needed**:
1. Create analyzer project targeting `ISwiftObject` / `SwiftSafeHandle<T>` types
2. Warn on: local variables without `using`, field assignments without dispose in containing type
3. Package as NuGet alongside `Swift.Runtime`

**Acceptance criteria**:
- [ ] Analyzer warns on undisposed `SwiftSafeHandle<T>` locals
- [ ] Analyzer packaged and included in `Swift.Runtime` NuGet
- [ ] No false positives on properly disposed objects

---

## 2. NativeAOT Hands-On Validation

**Priority**: P4
**Area**: Research
**Details**: `src/docs/nativeaot-investigation.md`

Desk research complete. Findings:

| Blocker | NativeAOT Impact | Confidence |
|---------|-----------------|------------|
| #1 `!ji->async` JIT assertion crash | **Bypassed** (no JIT in NativeAOT) | High |
| #2 Non-blittable types with `CallConvSwift` | **Likely persists** | Medium |
| #3 SafeHandle across async P/Invoke | **Uncertain** | Low |

**Remaining work**:
1. Build a minimal NativeAOT iOS test app reproducing the three blockers
2. Test `[LibraryImport]` + `CustomMarshaller` for `SwiftOptional<T>` blittable lowering
3. Run matrix tests with `[DllImport]` vs `[LibraryImport]` under NativeAOT

**Acceptance criteria**:
- [ ] Hands-on validation with NativeAOT iOS test app
- [ ] Document which workarounds apply under NativeAOT

---

## Observations: UnsupportedExistential (26 skips)

Not a standalone work item — these are existential type arguments in bound generics where the parameter does **not** have a default value. Phase 51's `ExistentialBypassEmitter` handles the default-arg case. The non-default-arg case requires:

- Constructor/method signatures that accept `ExistentialContainer{N}` as a bound generic type argument
- C# callers to box their protocol-conforming object into a container
- Runtime support for existential container construction from C#

| Library | Existential Types | Count |
|---------|------------------|-------|
| Nuke | ImagePipelineDelegate, ImageProcessing, ImageDecoding, Error, anonymous | 10 |
| Lottie | AnimationImageProvider, AnyValueProvider, AnimationCacheProvider, DotLottieCacheProvider, Error, anonymous | 16 |

Most are library-specific provider/delegate protocols. Addressing these would require the generator to emit methods that accept `ExistentialContainer` parameters in bound generic positions — a significant extension of the current existential support.

---

## Verification

After completing any generator task:

```bash
# Layer 1: Generator coverage
cd TestFramework
./build-and-test.sh
./generate-coverage-report.sh

python3 -c "
import json
with open('output/coverage-matrix.json') as f:
    d = json.load(f)
mp = d['summary']['must_pass']
print(f'Must-pass: {mp[\"passing\"]}/{mp[\"total\"]} passing, {mp[\"degraded\"]} degraded')
for f in d['features']:
    if f.get('test_status') == 'degraded':
        print(f'  {f[\"name\"]}: {len(f.get(\"binding_skips\",[]))} skips')
"

# Layer 2: Runtime ABI/marshalling tests
./run-runtime-tests.sh --tier 2
```

Layer 2 runtime tests validate that generated bindings actually work at runtime (marshalling round-trips, SafeHandle lifecycle, memory management). Use `--tier 2` for merge-gate validation, `--tier 3` for nightly runs with flake detection. See `TestFramework/README.md` for details.

After completing any real-world binding task, regenerate and compare:

```bash
# BlinkID
cd BindingTesting/BlinkID && ./regenerate-bindings.sh

# Nuke
cd BindingTesting/Nuke && ./regenerate-bindings.sh

# Lottie
cd BindingTesting/Lottie && ./regenerate-bindings.sh

# Quick skip count comparison
for lib in BlinkID/output-ios Nuke/output-ios Lottie/output-ios; do
  echo "=== $lib ==="
  python3 -c "
import json
with open('BindingTesting/$lib/binding-report.json') as f:
    d = json.load(f)
print(f'Types: {d[\"EmittedTypeCount\"]}/{d[\"TotalTypeCount\"]}')
print(f'Members: {d[\"EmittedMemberCount\"]}/{d[\"TotalMemberCount\"]}')
print(f'Skipped: {d[\"SkippedMemberCount\"]}')
"
done
```
