# Binding Audit — Methodology & Rubric

This folder holds a correctness/quality/coverage audit of every generated binding shipped from
`/Users/wojo/Dev/swift-dotnet-packages` (third-party libs under `libraries/`, Apple frameworks under
`apple-frameworks/`). One markdown file per library. This file is the shared rubric every per-library
audit follows so the docs are comparable.

## What we are auditing (and what we are NOT)

We audit **the generated C# binding that ships to consumers** — the `swift-binding/*.cs` produced under each
library's `obj/.../swift-binding/` at the current `main` HEAD, plus the tests that guard it. This is a
*static* read-and-reason audit. It is **not** a runtime correctness gate — that is what BindingTests and the
runtime test apps prove. When we suspect a runtime/ABI bug we flag it for a BindingTests repro, we don't try
to prove it here.

We respect deliberate project decisions (do not relitigate them):
- SwiftUI `View` types are intentionally excluded from the direct binding; a SwiftUI **bridge** is generated
  instead. A skipped `View` with `BridgeStatus: Generated` is *covered*, not a gap.
- Module-internal / synthesized-`Codable` / implicit-override members are intentionally pruned.
- The post-processor and the harness-stripper spine are kept by design.
- Async Swift methods are surfaced with C# `Task`/`async` affordances — that is wanted, not drift.
- The native runtime ships as a framework, never a loose dylib (TN2435).

## Inputs per library (all under `<lib>/obj/<cfg>/<tfm>/swift-binding/`)

| Artifact | Use |
|---|---|
| `binding-report.json` | **Primary coverage source.** Totals + per-item skip reasons/workarounds. |
| `<Module>.cs` (+ `.SwiftUIBridge.cs`) | The shipped C# surface — read for quality/correctness. |
| `<Module>.api-manifest.json` | Generator's parsed view of the Swift public API (`members` list). |
| `<Module>.Wrapper.swift` | Generated Swift `@_cdecl` wrapper — shows what got wrapped. |
| `symbolgraph/` | Swift symbol graph (authoritative API surface). |
| xcframework `*.swiftinterface` (source/zip libs) | Canonical native public API for the 1:1 comparison. |
| `../../../tests/Program.cs` (+ domain files) | The end-to-end tests guarding this binding. |
| `library.json`, `README.md`, `*.csproj` | Version, mode, products, packaging. |

For Apple frameworks the `.swiftinterface` lives in the SDK/Xcode framework path, not the repo; the
`api-manifest.json` + `symbolgraph/` are the practical ground truth.

`binding-report.json` field reference: `TotalTypes/EmittedTypes/SkippedTypes`,
`TotalMembers/EmittedMembers/SkippedMembers/SynthesizedMembers`, `EmittedMembersByKind`,
`SkippedMembersByKind`, `SkippedItems[]` (`Kind,Name,ContainingType,Reason,Details,RecommendedWorkaround`),
`WrappedItems[]`, `BridgedViews[]`, `BridgeSummary`, `UnsupportedCommentDrops[]`, `ObjCPrefixBridges[]`,
`ObjectDegradations[]`.

## The three dimensions

### 1. Coverage — what we surface vs. what we skip
- Report emitted/total for types and members (with %). Note `SynthesizedMembers` (generator-added, e.g.
  Codable helpers, factory ctors) so the "emitted" number isn't misread.
- Group `SkippedItems` by `Reason`. For each reason bucket, classify as **(a) correctly excluded**
  (SwiftUI view w/ bridge, module-internal, synthesized Codable, deprecated/obsoleted) or **(b) a real gap**
  (a useful public API a C# dev would expect, dropped for a generator limitation we could lift).
- For every (b) gap, name the concrete Swift API, the skip `Reason`/`Details`, and a one-line judgment of
  **whether it's worth a generator fix** and roughly what capability is missing (e.g. "existential `any Error`
  in return position", "operator on generic type needs buffer marshalling", "resilient struct property").
- Surface the highest-value generator unlocks as a short prioritized list (value × tractability).

### 2. C# correctness & consumption quality
Read the generated surface and judge whether a C# dev can actually use it idiomatically. Concretely check:
- **Naming/shape**: PascalCase, no leaked Swift mangling, sensible namespaces, enums map cleanly, nested
  types reasonable. Flag anything that reads as machine-vomit a consumer can't navigate.
- **Async**: Swift `async` funcs surface as `Task`/`async`-friendly C# (not blocking-only). Note any async
  API surfaced only as a blocking call (acceptable fallback, but call it out).
- **Nullability**: optionals → nullable C# (`?`); flag missing/contradictory nullable annotations.
- **Lifetime**: `IDisposable` present where the Swift type owns native memory; obvious leak/ownership smells.
- **Ergonomic gaps where a thin C# affordance is in-scope** (overloads, `string`↔`SwiftString`, collection
  conversion, enum convenience) — only where it does NOT steer away from what native docs teach. We aim for a
  1:1 binding with minimum cleanup-to-.NET-standards; do not invent a re-imagined API.
- Anything outright broken/unusable (a public type with no usable ctor, a method whose only param type is
  itself unbindable, a property typed as an opaque blob).

Anchor every finding with `Module.cs:LINE` and the Swift API it corresponds to.

### 3. Test coverage — do the tests prove the binding end-to-end?
- Count distinct test cases (the `results.Pass/Fail/Skip("Name", …)` calls or equivalent) and what surface
  they touch. Tests run on Simulator (Mono JIT) and/or device (NativeAOT).
- Judge **depth**: do they round-trip real values / call real methods (strong), or only poke type metadata
  and sizes (weak)? Metadata-only "tests" do not prove ABI.
- Map tests to the surfaced API: which significant emitted types/members have **zero** coverage? Call out the
  most important untested surface.
- Note legitimate `Skip`s (document why) vs. silent gaps. Recommend specific high-value tests to add (name the
  type/member and what to assert), matched to the right layer.

## Output format (every per-library file)

```
# <Library> — Binding Audit

- **Package**: SwiftBindings.<X> vX.Y.Z   **Mode**: source|zip|apple   **TFM(s)**: …
- **Native**: <repo/framework> <version>
- **Audited at**: main <short-sha>, generated <date from binding-report>

## Verdict
<2–4 sentences: overall health, headline coverage %, is it shippable/usable, biggest risk.>

## 1. Coverage
<emitted/total table; skip-reason breakdown; (a) correctly-excluded vs (b) real gaps; prioritized generator unlocks.>

## 2. C# Quality
<findings with Module.cs:line; async/nullability/lifetime/ergonomics; what's broken or awkward.>

## 3. Test Coverage
<case count + depth; untested surface; concrete tests to add.>

## Action Items
| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
… (omit the table and say "No material findings" if genuinely clean — still write the file.)
```

Keep claims evidence-backed (file:line, concrete API names, real numbers from the report). Calibrate to the
project's deliberate decisions above. If a library is genuinely clean, say so briefly — still produce the file.
