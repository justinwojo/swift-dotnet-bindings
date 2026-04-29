# Phase 0 — Evidencing the `binding-report.json` staleness bug

**Captured**: 2026-04-28
**Companion**: `phase0-baselines.md`
**Fixes in**: Milestone 1 — *Trust the output* (gameplan §M1).

## Why this doc exists

The architecture gameplan opens M1 by asserting that `binding-report.json` "derives from mid-pipeline state, so it diverges from what consumers actually receive after `CSharpWrapperCoGater` and post-emission steps run." Phase 0's job is to evidence that gap **before** any M1 fix touches the pipeline, so the manifest-driven report M1 introduces can be diff'd against the snapshots captured here.

Two evidence paths were considered:

1. **Instrumented dual-emit**: dump `binding-report.json` once at its current write site, then a second time after every post-emission pass completes. This requires touching the generator pipeline — which is M1's actual scope, so it would creep Phase 0 into M1.
2. **Read-only divergence proof**: capture the report as it's written today, then show on-disk reality contradicts it via a different artifact the generator already emits (`binding-emission-report.json`) and via direct content comparison.

We took path (2), with explicit lead approval to fall back from the dual-emit form when capturing path-(1) cleanly would require the M1 manifest infrastructure itself (see the team-lead's Phase 0 instructions). The evidence below is sufficient to confirm the bug exists, identify where it is introduced in the pipeline, and show representative impact on two validation libraries (CryptoKit, GRDB). It does **not** quantify the full scope of divergence across every report field on every module — that's a verification artifact M1 will produce when it diffs the manifest-derived report against these snapshots.

## Pipeline ordering — where the report is captured vs. where the truth changes

`Program.cs` lines below are the linearization of one binding-generation invocation. Identified via the Phase 0 Explore subagent pass.

```
Program.cs:440  ReportCollector.Start(decl)              // begin collecting skip/emit signals
Program.cs:507  StringEmitter.EmitModule(...)            // *.cs files written to disk
Program.cs:510  report = ReportCollector.Complete()      // snapshot of in-memory model
Program.cs:513  ReportEmitter.Emit(report, ...)          // ← binding-report.json IS WRITTEN HERE
Program.cs:515  ReportCollector.Reset()
Program.cs:521  if (emissionContext.SuppressedProxyClassNames.Count > 0)
Program.cs:523    CSharpWrapperCoGater.ProcessSuppressedProxyReferencesInDirectory(...)
                                                         // ← MUTATES *.cs files on disk
Program.cs:531  EmissionReportEmitter.Emit(...)          // binding-emission-report.json
```

A second pass runs in the wrapper-compile phase invoked by `RunCompileWrapperOnly`:

```
SwiftWrapperCompiler.cs:202,606
                SwiftWrapperPostProcessor.Process(...)   // strips emitted EveryProtocol conformances etc.
SwiftWrapperCompiler.cs:246,255
                SimulatorOnlyMemberDetector.Detect/ApplySimulatorGuards
Program.cs:758  CSharpWrapperCoGater.ProcessDirectory(...) // SECOND .cs disk-mutation pass
                                                         // strips P/Invokes for stripped wrapper symbols
```

`CSharpWrapperCoGater` reads `.cs` files via `File.ReadAllText`, runs regex-level stripping, and writes back via `File.WriteAllText` (CSharpWrapperCoGater.cs:162–166). It has no feedback loop into `ReportCollector`. By the time the cogater runs, `binding-report.json` has already been written and `ReportCollector` has been `Reset()`. There is **no CLI flag that produces a post-cogating snapshot of the report** — confirmed by reading every option emitted by `dotnet run --project src/Swift.Bindings/src -- --help`.

## Smoking gun #1 — `binding-emission-report.json` admits the divergence

The generator already writes a *second* report (`binding-emission-report.json`) after `EmitModule` but tracks the post-processor independently. For the GRDB validation library on `1.0-milestones@d5eccf2f`:

```json
"conformanceDecisions": {
  "emittedInSource": 18,
  "skippedAtEmission": 17,
  "note": "Emitted conformances are stripped by post-processor Pattern 1 (unconditional EveryProtocol removal)"
}
```

Source: `phase0-artifacts/grdb-binding-emission-report.json`.

The note is plain-English acknowledgement that the C# emitter believes it emitted 18 EveryProtocol conformances, but the Swift wrapper post-processor unconditionally removes all of them — and `binding-report.json` still counts those 18 as members of `EmittedMembers`. The CryptoKit `binding-emission-report.json` shows the same conformance-stripping pattern at smaller scale (`emittedInSource: 1, skippedAtEmission: 1`); the rest of CryptoKit's divergence story is different from GRDB's (see the next section).

This is the structural divergence M1 is fixing: the consumer sees what's left after the post-processor, but `binding-report.json` reflects the pre-post-processor view.

## Smoking gun #2 — proxy-type entries are present in the report whether or not the cogater actually fires

`binding-report.json` for GRDB contains 36 `SkippedItems` whose `Reason` is `EveryProtocolConformanceSkipped`, e.g.:

```json
{
  "Kind": "Type",
  "Name": "RowAdapterProxy",
  "ContainingType": "GRDB.RowAdapter",
  "Reason": "EveryProtocolConformanceSkipped",
  "Details": "Protocol proxy skipped: EveryProtocol conformance was not emitted (MissingRequirements)."
}
```

Reading the same proxy types out of `binding-report.json` and grepping the corresponding emitted `GRDB.cs` from the `nuke validate` output directory shows zero references to most of them — confirming the cogater path *does* fire and *does* delete content from disk for GRDB. CryptoKit, in contrast, registers 12 of these `EveryProtocolConformanceSkipped` proxies in its report yet shows no log line from `CSharpWrapperCoGater.ProcessSuppressedProxyReferencesInDirectory` because `emissionContext.SuppressedProxyClassNames.Count == 0` for that module — the suppressed-proxy code path is gated on a state the report itself doesn't surface. The point isn't that every `SkippedItems` `Type` entry is stale — for CryptoKit the entries are arguably accurate as records of an early skip decision — but that the **report mixes intermediate decisions and final-output facts in the same shape**, and a consumer reading it can't tell which is which without re-running the pipeline. M1's manifest-derived report fixes this by writing once after every mutation has settled.

Source: `phase0-artifacts/grdb-binding-report.json`, `phase0-artifacts/cryptokit-binding-report.json`.

## What this means for M1

M1 introduces a `BindingArtifactManifest` written *after* wrapper compilation, `CSharpWrapperCoGater`, and bridge compilation, with `binding-report.json` derived from the manifest rather than mid-pipeline state. The two evidence paths above give M1 a concrete acceptance test:

1. **Conformance counts**: M1's manifest-derived report on GRDB must show `emittedInSource - skippedAtEmission` consistent with the post-processor's actual final state (i.e., 1, not 18, for that field if the new report keeps the same field shape; or zero entries for stripped conformances if M1 reorganises the schema).
2. **Type-skip entries**: every `SkippedItems` entry whose disposition depends on cogating must reflect the post-cogating reality, not the emitter's mid-pipeline guess.

The four JSON files in `phase0-artifacts/` are the pre-fix baseline. Re-running the same generator invocation after M1 lands and diffing the new outputs against these snapshots is the verification.

## Notes for the M1 implementation

- `RecordMemberSkipped` in `Reporting/ReportCollector.cs:161` and `RuntimeLimitations.Limitation` in `Swift.Runtime/.../RuntimeLimitations.cs:18` are the secondary M1 targets called out in the gameplan; they are **not** part of the staleness bug per se but share the report subsystem.
- Avoid blowing away `binding-emission-report.json` when introducing the manifest — it's currently the only honest record of post-processor stripping decisions and remains useful for debugging until the manifest fully subsumes it.
