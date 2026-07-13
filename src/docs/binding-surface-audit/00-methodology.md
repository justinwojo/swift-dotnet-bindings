# Binding Surface Audit — Methodology (2026-07-11)

## Intent

Answer three owner questions about the **SwiftBindings generator** and the **bindings that ship from it**:

1. **Core-feature usability** — Against the native Swift/ObjC SDK, are the features that make each library *worth using* actually surfaced and callable from C#? Is the C# structured so a developer can follow native docs with only predictable projection rules?
2. **Functional / configuration defects** — Min OS handling, TFMs, packaging policy, empty modules, wrapper requirements, known correctness bugs.
3. **Other work** that moves the product toward fully functional, well-written consumer bindings.

This is a **static read-and-reason audit** of generated artifacts, package config, tests, and guides — not a full runtime matrix. Suspected runtime bugs are flagged for BindingTests / package test follow-up, not “proven green” here.

## Relationship to prior work

| Document set | Date | Role |
|---|---|---|
| [`src/docs/BindingAudit/`](../BindingAudit/) | 2026-06-27 | Full per-package coverage audit (26 bindings). Still the **baseline per-library depth**. |
| **This folder** (`binding-surface-audit/`) | 2026-07-11 | **Delta re-validation**, packaging/min-OS, C# quality, **internal-binding-testing**, prioritized recommendations. |

Do not re-litigate intentional product decisions (SwiftUI View → bridge, ModuleInternal/`@_spi` pruning, TN2435 framework packaging, AppIntents not shipping).

## Inputs

| Source | Use |
|---|---|
| `swift-dotnet-packages/apple-frameworks/*/obj/**/swift-binding/` | Generated C#, `binding-report.json` |
| Worktree `…/.claude/worktrees/agent-a8e7be4395c90a492/…` | Fresher multi-framework ios26.2 regen when main `obj/` is sparse |
| `swift-dotnet-packages/libraries/*` | Third-party csproj, `library.json`, tests, bin DLLs |
| `swift-dotnet-packages/**/*GUIDE.md`, README | Intended consumer story vs emitted surface |
| `internal-binding-testing/*` | Broader generator stress corpus (Alamofire, RxSwift, …) |
| `src/docs/BindingAudit/*` | June baseline claims to re-check |
| Generator commits since 2026-06-27 | What might have closed findings |

## Approach (what we actually did)

1. **Map surface** — inventory packages, TFMs, test apps, where generated CS lives.
2. **Plan** — tier work so effort matches value (Apple flagship + product third-party + internal canaries + cross-cutting config/quality).
3. **Parallel deep dives**
   - Re-validate BindingAudit top-10 findings on current CS.
   - Project config / min OS / packaging.
   - Internal-binding-testing inventory + 8-library usability deep dives.
   - Cross-cutting C# quality (naming, async, mega-files, ObjC path).
4. **Human reconciliation** — correct agent over-claims with line-level evidence (notably CryptoKit NIST ECDSA CSM overloads).
5. **Synthesize** — executive summary + ranked recommendations.

## Rubric (per binding / theme)

### Coverage & usability

- Headline native workflow constructible from C#? (list key types/methods)
- Skips: intended (ModuleInternal, SwiftUI, Codable synth) vs real consumer gap
- Effective coverage may differ from raw `Emitted/Total` (accessor accounting, availability, open-generic vs CSM closed)

### C# quality

- Naming fidelity vs stutter / hash factories
- Async: `Task` + `CancellationToken`; `AsyncSequence` → `IAsyncEnumerable` where claimed
- Dispose / SafeHandle discipline
- AnyType / Unsupported / Obsolete leakage at the public edge
- Navigability (file size, namespaces, nested facades)

### Config & packaging

- TFM = SDK surface vs deployment min OS (docs accuracy)
- `library.json` minIOS vs csproj vs `[SupportedOSPlatform]`
- Empty transitive packages, `SwiftWrapperRequired`, multi-product deps

### Tests

- Count of meaningful cases; **await** of real async flows vs metadata-only
- Depth: construction/enums (weak) vs value round-trip (medium) vs product workflow (strong)

## Severity

| Tag | Meaning |
|---|---|
| **P0** | Correctness: compiles but wrong at runtime, or silent dead critical path |
| **P1** | Core feature blocked or major consumer footgun |
| **P2** | Inconsistency / secondary gap / packaging clarity |
| **P3** | Polish (naming, docs density, mega-file ergonomics) |

## Explicit non-goals

- Full `nuke RegressionValidate` matrix re-run
- Re-writing BindingAudit per-library files from scratch
- Generator implementation fixes in this pass (audit only)
- Product strategy (Firebase own-vs-contribute) — already documented in packages `BINDING-CANDIDATES.md`
