# Audit — Resume Here (hand this to a fresh session)

**To continue the read-only codebase audit, paste this to a fresh Claude:**

> Read `src/docs/audits/RESUME.md` and continue the audit. Run the next track(s) in the priority order, then update this file and the README report index.

This file is self-contained: a fresh session has no memory of the prior audit conversation, so everything needed is here. Read it top to bottom before launching anything.

---

## What this is

A **read-only** deep-dive audit of the Swift→.NET binding generator + runtime. **No code changes.** The only output is written markdown reports in `src/docs/audits/`. Fixes are explicitly out of scope — findings are recorded with `file:line` + a compile-probe verdict, and left for follow-on implementation work.

The full plan, three-way planner cross-check (Claude/Codex/Grok), and track definitions live in **`src/docs/audits/README.md`**. Read its §4 (prioritized master plan) for what each track hunts for.

---

## Status (update this section as tracks land)

| Track | Title | Status | Report | Confirmed |
|---|---|---|---|---|
| **A1** | P/Invoke ABI contract + thunks | ✅ done (3 runs unioned) | `Track-A1_PInvoke-ABI-Contract.md` (+ `Track-A1_run-reports/`) | 13 |
| **A3** | ARC / ownership / lifetime / memory safety | ✅ done | `Track-A3_ARC-Ownership-Lifetime.md` | 7 |
| **C1** | AI-maintainability hazard-map | ✅ done | `Track-C1_Maintainability-Hazard-Map.md` | 7 (1 P0, 6 P1) |
| **A2** | Struct layout / register / VWT | ✅ done | `Track-A2_Struct-Layout-VWT.md` | 4 (1 P0, 3 P1) |
| A4 | Closures / optional-closure / reabstraction | ✅ done | `Track-A4_Closures-Reabstraction.md` | 7 (5 P0, 1 P1; **5/5 Critical**) |
| A5 | Existentials / protocol proxies / witness dispatch | ✅ done | `Track-A5_Existentials-Witness-Dispatch.md` | 9 (2 P0, 7 P1; **5/5 Critical**) |
| A6 | Concrete specialization / generics / PAT | ✅ done | `Track-A6_Concrete-Specialization-PAT.md` | 5 (2 P0 families) |
| A7 | Async / throws / error-carrier | 🔄 running (parallel) | `Track-A7_Async-Throws-Error-Carrier.md` | — |
| A8 | Parser / ABI-ingestion / demangler | ⬜ | `Track-A8_Parser-Demangler-Fidelity.md` | — |
| C2 | Invariant-drift / dedup / key-consistency | ⬜ | `Track-C2_Invariant-Drift-Dedup.md` | — |
| M1–M4 | Tier-2 (SwiftUI bridge / wrapper-SDK / TypeDatabase parity / BindingTests coverage matrix) | ⬜ | per-track | — |
| L1–L3 | Tier-3 (docs drift / ObjC / perf) — lowest priority | ⬜ | per-track | — |
| — | **Synthesis** → `STATE-OF-THE-CODEBASE.md` (risk heatmap + prioritized backlog + "top-20 files to touch with care") | ⬜ run last | `STATE-OF-THE-CODEBASE.md` | — |

**Recommended next order:** `C1` (headline architecture track — directly serves "help future AI agents not make plausible-but-wrong changes") → `A2` (struct/VWT, the last untouched Tier-1 ABI core) → `A4`→`A5`→`A6`→`A7`→`A8` → `C2` → Tier-2 → Tier-3 → synthesis.

**Headline priority is A + C (Tier-1).** Tier-2/3 are "if budget remains." Don't skip A2/C2 to do M/L tracks.

---

## How to run a track

The orchestration is a dynamic Workflow script already on disk and already debugged:
`.claude/workflows/codebase-audit.js`

**Launch one track (heavy intensity) like this:**

```
Workflow({
  scriptPath: '/Users/wojo/Dev/swift-bindings/.claude/workflows/codebase-audit.js',
  args: { tracks: ['C1'], intensity: 'heavy' }
})
```

The workflow runs in the background and notifies you on completion. Pipeline per track:
**Find** (3 finders, budget-aware multi-round, seeking NEW defects) → **Verify** (adversarial compile-probe; majority of votes; defaults to `inconclusive`) → **Report** (one agent Writes the `TrackXX_*.md`). Track keys: `A1`–`A8`, `C1`, `C2`, `M1`–`M4`, `L1`–`L3`.

---

## ⚠️ Hard-won lessons — DO NOT skip these

1. **VERIFY TRACK ROUTING within seconds of launch.** A prior run silently re-audited A1 four times (~10M wasted tokens) because the harness delivers the `args` tool parameter to the script as a **JSON string**, not a parsed object. The script now normalizes it (`JSON.parse`), but *always confirm anyway*. Right after launch, find the workflow transcript dir (printed in the launch result, under `.../subagents/workflows/wf_*/`), and grep a finder agent's `.jsonl` for the track's keywords:
   ```bash
   d=<transcript-dir-from-launch-result>
   grep -o -i -E "<track keyword>|A1|PInvoke" "$d"/agent-*.jsonl | sort | uniq -c | sort -rn | head
   ```
   The track's own keywords must dominate (e.g. for C1: `maintainability`, `hazard`, `god-class`, `invariant`; for A2: `frozen`, `value witness`, `extra-inhabitant`, `layout`). If you see A1/PInvoke dominating instead, **stop the run immediately** (`TaskStop`) — routing is broken.

2. **Budget: one heavy track ≈ ~31 agents ≈ ~3.2M subagent tokens ≈ ~20% of a fresh 5-hour window.** The verifier fan-out (up to 12 findings × 2 votes) dominates the cost, so cost is roughly flat per track regardless of intensity-of-findings. Practical ceiling: **~4 heavy tracks per 5-hour window** with safety margin. Don't run a multi-track heavy invocation expecting ~20% total — it's ~20% **per track**, and the ~10-concurrent-agent cap serializes them anyway. To cover several tracks, launch them as **separate sequential runs** and watch usage between each. Stop with margin; don't let a run hit the wall mid-flight.

3. **Single heavy run ≈ 40–60% recall.** A1's three independent runs had low overlap and unstable severity (that's why A1 was unioned across 3 and has a cross-run reliability section). For the remaining tracks a single heavy run is a solid *first pass*, not exhaustive — an accepted tradeoff for breadth. If a track later proves critical, re-run it and union (see how `Track-A1_run-reports/` + the §0 table in the A1 report were assembled).

4. **Read-only invariant — verify it held after every run.** Agents may write ONLY their one report and must do all compile-probe work in `/tmp`. After each run, confirm nothing leaked:
   ```bash
   cd /Users/wojo/Dev/swift-bindings
   git status --short                                   # expect: no modified tracked files; only ?? .claude/workflows/, src/docs/audits/
   git ls-files --others --exclude-standard | grep -vE "^src/docs/"   # expect: only .claude/workflows/codebase-audit.js
   find . -name "probe*.swift" -o -name "*.swift" -newer README.md | grep -v /tmp/   # expect: empty
   ```
   A prior run let a verifier's `Program.cs` compile-probe escape into the repo; it was deleted. Catch and delete any such stray immediately. **Probe diagnostics referencing `/tmp/.../probe*.swift` in `<new-diagnostics>` are normal** — those files live in `/tmp` and are fine.

5. **Verification = static + compile probes** (swiftc / SIL dump / `swift-demangle` / `nm` / `otool` / `nuke compile`). No full `nuke validate` / `nuke binding-tests` gates by default — this is an audit, not a fix cycle. The repo's standing rules still apply: **verify the Swift ABI via SIL before blaming the runtime** (`feedback_verify_swift_abi_sil.md`); **check `@frozen` before blaming register placement** (`feedback_swift_frozen_first.md`); **all runtime crashes are OUR bugs until proven** — only the 4 confirmed upstream .NET issues in `feedback_mono_jit_blame.md` are exempt.

6. **Nothing has been committed, and nothing should be** unless the user explicitly asks. **Never `git stash`.** The audit artifacts (`src/docs/audits/`, `.claude/workflows/codebase-audit.js`) are untracked and staying that way until the user decides.

---

## After each run

1. Verify routing (lesson 1) and read-only invariant (lesson 4).
2. `Read` the new `TrackXX_*.md` and surface its confirmed findings to the user verbatim-ish (headline P0/P1s + counts).
3. Update the **Status table above** (✅ + report name + confirmed count) and check the box in **`README.md` §6**.
4. Report remaining budget and ask before the next run if it would risk crossing the limit.

## When all (or enough) tracks are done

Run the **synthesis pass** last: one agent reads every `TrackXX_*.md` and writes `STATE-OF-THE-CODEBASE.md` — a cross-track risk heatmap, a single P0→P2 prioritized backlog (deduped across tracks), and a "top-20 files to touch with care" list for future AI-maintainability. This is the payoff document; don't skip it.
