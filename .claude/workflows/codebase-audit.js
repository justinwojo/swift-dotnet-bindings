export const meta = {
  name: 'codebase-audit',
  description: 'Read-only deep-dive audit: per-track finders -> adversarial compile-probe verify -> written report in src/docs/audits/. Parameterized by args.tracks.',
  phases: [
    { title: 'Find' },
    { title: 'Verify' },
    { title: 'Report' },
  ],
}

// ===========================================================================
// Shared codebase-audit workflow for the Swift->C# binding generator.
// Run a slice per session:  Workflow({ scriptPath, args: { tracks: ['A1','A2','A3'], intensity: 'heavy' } })
// On re-run after a session cut, pass ONLY the tracks whose report file
// doesn't exist yet in src/docs/audits/.  Plan + track defs: src/docs/audits/README.md
// ===========================================================================

const REPO = '/Users/wojo/Dev/swift-bindings'

const TRACKS = {
  // ---- TIER 1: Headline A — ABI / Marshalling cluster ----
  A1: {
    report: 'Track-A1_PInvoke-ABI-Contract.md',
    title: 'P/Invoke ABI contract + x64 thunks',
    targets: 'Emitter/StringEmitter/Handler/PInvokeEmitter.cs, WrapperEmitter.Marshalling.cs, WrapperEmitter.Return.cs; Emitter/StringEmitter/CdeclParamMapper.cs; Marshaler/Projection/MethodMarshalPlanBuilder.cs; Emitter/ThunkEmitter/* (Arm64ThunkTarget, SysVThunkTarget, ThunkAssemblyEmitter, TypeLowering); generated BindingTests/output/*.cs + *.swift',
    hunt: 'CallConvSwift vs CallConvCdecl mismatch; missing/extra params; wrong SwiftSelf placement; wrong indirect-result (sret) / error-register modeling; bool marshalling; generic metadata/PWT ordering; x86_64 thunk register/return shape drift. Method: sample ~30 generated wrappers across sync/async/throwing/mutating/generic/property/subscript and mechanically compare each Swift @_cdecl signature to its C# P/Invoke declaration.',
  },
  A2: {
    report: 'Track-A2_Struct-Layout-VWT.md',
    title: 'Struct layout / register passing / value witness',
    targets: 'Emitter/StringEmitter/Handler/FrozenStructHandler.cs, NonFrozenStructHandler.cs; Swift.Runtime ValueWitnessTable.cs, SwiftMarshal.cs, SwiftOptional.cs; tests/UnitTests/EmitterTests/TypeLoweringTests.cs; src/docs/Design/binding-structs.md, binding-value-witness-table.md',
    hunt: '@frozen vs resilient misclassification; frozen-but-non-POD copy errors; optional tag / extra-inhabitant mistakes; mixed direct/indirect tuple returns; inout writeback gaps; class-with-buffer vs opaque-payload inconsistency. Build a struct/tuple/optional shape taxonomy and check each branch has a runtime representative.',
  },
  A3: {
    report: 'Track-A3_ARC-Ownership-Lifetime.md',
    title: 'ARC / ownership / lifetime / memory safety',
    targets: 'Swift.Runtime Arc.cs, SwiftHandle.cs, ProxyLifetimeTracker.cs, ExistentialContainer.cs, InteropServices/SwiftMarshal.cs; Emitter ClosureEmitter.cs, EveryProtocolEmitter.cs; BindingTests/RuntimeTestsApp/Lifetime + MemoryManagement; src/docs/Design/memory-management.md',
    hunt: 'leaked passRetained; double release after takeRetainedValue; missing Arc.Retain on borrowed/non-mutating-ref returns; async SafeHandle lifetime loss; finalizer-only correctness assumptions; VWT misuse on resilient vs @frozen; sim/device divergence. Require each passRetained/Retain to have an explicit owner and release path.',
  },
  A4: {
    report: 'Track-A4_Closures-Reabstraction.md',
    title: 'Closures / optional-closure / reabstraction',
    targets: 'Marshaler/ClosureHandler.cs, Marshaler/Projection/ClosureProjection.cs, OptionalProjection.cs; Emitter ClosureEmitter.cs, ClosureEmitter.SwiftWrapper.cs; Emitter/StringEmitter/Handler/MethodClosureBridge.cs, NestedClosureBridge.cs, GenericClosureBridgeEmitter.cs; BindingTests Closures domain',
    hunt: 'optional closures not treated as escaping; reabstraction-thunk traps ($sIeg_ytIegr_TR-style) leading to SIGSEGV; wrong GCHandle lifetime; unsupported closure shape emitted instead of skipped; return-marshalling drift; throwing-closure error leak. Compare the Layer-1 method emission vs the Layer-2 cdecl wrapper emission for every closure gate.',
  },
  A5: {
    report: 'Track-A5_Existentials-Witness-Dispatch.md',
    title: 'Existentials / protocol proxies / witness dispatch',
    targets: 'Emitter EveryProtocolEmitter.cs, ProtocolProxyEmitter.InterfaceImpl.cs, ProtocolProxyEmitter.Receivers.cs, WitnessDispatchEmitter.cs; Emitter/StringEmitter/Handler/ExistentialBypassEmitter.cs; Marshaler/ExistentialHandler.cs; Swift.Runtime ExistentialContainer.cs',
    hunt: 'class-bound existential layout mistakes; mixed protocol-composition size/order mismatch; missing protocol witness tables (PWTs); wrong witness default selection; dead receiver impls returning invalid carriers; Any / Optional<Any> unsafe fallbacks (load(as:) vs Unmanaged.fromOpaque). Classify layouts: opaque any P, class-bound, Any, compositions, optional existentials, returns vs params.',
  },
  A6: {
    report: 'Track-A6_Concrete-Specialization-PAT.md',
    title: 'Concrete specialization (CSM) / generics / PAT',
    targets: 'Marshaler/ConcreteSpecializationEngine.cs; Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs, GenericTypeEmitter.cs; Marshaler/BoundGenericsHandler.cs; TypeDatabase/ConformanceGraph.cs; Emitter/StringEmitter/PInvokeHelperEmitter.cs; src/docs/roadmap.md (CSM "trigger to revisit" items)',
    hunt: 'wrong generic arity; Self not substituted (CS0246/CS0305); SameType sugar mismatch; protocol-composition constraints treated as opaque; associated-type constraints skipped; value conformer passed to ISwiftObject APIs; PWT metadata ordering; CSM result-pointer alloc/free antipatterns; multi-PAT boxing. Seed hypotheses from roadmap open items, then verify each against current code + tests.',
  },
  A7: {
    report: 'Track-A7_Async-Throws-Error-Carrier.md',
    title: 'Async / throws / error-carrier paths',
    targets: 'Emitter/StringEmitter/Handler/WrapperEmitter.Async.cs, AsyncMethodGenericBridgeEmitter.cs, AsyncHarnessEmitter.cs; Swift.Runtime SwiftResult.cs, AsyncClosureHelper.cs',
    hunt: 'callback GCHandle leaks; Swift error-pointer ownership errors; async opaque-existential owned-return mismatch; cancellation vs error path asymmetry; the multiple async emitter paths diverging. Trace success, failure, cancellation, and thrown-error paths separately and compare sync vs async ownership contracts.',
  },
  A8: {
    report: 'Track-A8_Parser-Demangler-Fidelity.md',
    title: 'Parser / ABI-ingestion / demangler fidelity',
    targets: 'Parser/SwiftABIParser.cs; Parser/Producers/* (SwiftSyntaxInterfaceFactsProducer + InterfaceFactsAggregator) and the Swift host tools/SwiftInterfaceParser/Sources/SwiftInterfaceParser/*Walker.swift (esp. MemberCollectionWalker public/internal-member detection); Demangler/Swift5Demangler.cs; Parser/GenericSignatureParser.cs; tests/UnitTests/ParserTests/*, DemanglerTests/*',
    hunt: 'public protocol requirements misclassified internal (and vice-versa); @usableFromInline internal reqs missed; mangling divergence (async, typed throws, InlineArray); ABI-JSON shape drift (Swift 6+); incorrect IsMutating/funcSelfKind/availability; ObjC nested-enum naming; ProtocolComposition printedName fallback; dependent-member parse loss. Roundtrip real .abi.json -> parsed Model -> emitted surface.',
  },

  // ---- TIER 1: Headline C — Architecture / Maintainability cluster ----
  C1: {
    report: 'Track-C1_Maintainability-Hazard-Map.md',
    title: 'AI-maintainability & hotspot hazard-map',
    targets: 'Mega-files: Emitter/StringEmitter/EveryProtocolEmitter.cs (5595), Emitter/StringEmitter/SwiftUIBridgeEmitter.cs (3962), Parser/SwiftABIParser.cs (3481), Demangler/Swift5Demangler.cs (3239); + top churn hotspots (ModuleEmissionContext, ConcreteProtocolSpecializationEmitter, ModuleHandler, WrapperEmitter.Async, MethodClosureBridge, ClassHandler)',
    hunt: 'duplicated decision logic; undocumented invariants; switch fallbacks returning null; generated-local name collisions (hardcoded tag/resultPtr/swiftResult shadowing projected params); hidden ordering constraints; tests asserting implementation rather than behavior. Produce a "future-AI hazard map": where an agent is likely to make a locally-plausible-but-globally-wrong change.',
  },
  C2: {
    report: 'Track-C2_Invariant-Drift-Dedup.md',
    title: 'Invariant-drift / dedup / key-consistency',
    targets: '.claude/rules/*.md (constraints.md, emitter.md, parser-marshaler.md, bindingtests.md, csharp-files.md, swiftui-bridge.md); the 22 WasEmitted sites; Emitter/StringEmitter/ModuleEmissionContext.cs; Marshaler/NameProvider.cs; overload/subscript key sites (IHandler.GetProjectedCSharpMethodKey); DefaultParameterOverloadEmitter.cs; ProtocolSignatureHelper.cs',
    hunt: 'doc<->code drift (spot-check every constraints.md trap against current code); generated-local shadowing; WasEmitted drift on newly-added emission paths (ancestor checks silently fail); EveryProtocol cross-extension dedup collapse on overloaded subscripts; dedup-key mismatch between protocol witness and concrete specialization. Build a site inventory with last-changed commit + a "audit-on-every-PR" checklist.',
  },

  // ---- TIER 2: Medium ----
  M1: {
    report: 'Track-M1_SwiftUI-Bridge.md',
    title: 'SwiftUI bridge support matrix',
    targets: 'Emitter SwiftUIBridgeEmitter.cs, SwiftUIBridgeEmitter.AsyncPattern.cs, SwiftUIBridgeEmitter.InitAnalyzer.cs, SwiftUIViewDetector.cs; BindingTests/RuntimeTestsApp/SwiftUIBridge; .claude/rules/swiftui-bridge.md',
    hunt: 'platform gating mistakes (UIKit vs macOS); async inference picking wrong constructor; retained controller/session lifetime bugs; optional bound-type/enum conversion errors; unsupported params silently bridged; two-path suppression divergence (TypeDatabaseExtensions vs MemberEmissionValidator).',
  },
  M2: {
    report: 'Track-M2_Wrapper-SDK-Packaging.md',
    title: 'Wrapper compilation / SDK / packaging / arch',
    targets: 'Program.cs, BindingsGeneratorCommand.cs; Configuration/SwiftWrapperCompiler.cs, CSharpWrapperCoGater.cs; Emitter/StringEmitter/ConsumerTargetsEmitter.cs; Swift.Bindings.Sdk/Sdk/Sdk.props + Sdk.targets',
    hunt: 'stale wrapper fingerprints; missing NativeReference; arch option ignored in one code path; Apple-framework-direct vs third-party-source flow divergence; CompileWrapperForArchitectures try/catch/finally + primary-restore correctness; Windows MAX_PATH / package regressions.',
  },
  M3: {
    report: 'Track-M3_TypeDatabase-Projection-Parity.md',
    title: 'TypeDatabase / projection parity',
    targets: 'TypeDatabase/TypeDatabase.cs, TypeDatabaseExtensions.cs; Marshaler/Projection/TypeProjectionFactory.cs + all ITypeProjection impls; src/Swift.Bindings/src/Data XML DBs; AppleFrameworkRegistry',
    hunt: 'Apple-framework heuristic drift; optional ObjC-bridged parity gaps (IsOptionalObjCBridged); XML kind mistakes; AnyTypeFallback where a known type exists; registry/XML/projection-factory disagreement. Use binding-report.json skip reasons as leads.',
  },
  M4: {
    report: 'Track-M4_BindingTests-Coverage-Matrix.md',
    title: 'BindingTests skip-taxonomy & coverage matrix',
    targets: 'BindingTests/README.md, BindingTests/output/binding-report.json; BindingTests/RuntimeTestsApp/**; BindingTests/Sources/SwiftBindingsTestLib/**; .claude/rules/bindingtests.md',
    hunt: 'skipped tests hiding project bugs; stale upstream classifications (only 4 are confirmed-upstream); compile-only coverage not backed by runtime assertions; missing coverage for validate-discovered bugs. Inventory [Skip]/NativeAOT-skips and bucket each as legitimate-limitation / needs-audit / likely-bug / needs-test-only-repro. Build the Swift-feature x coverage matrix.',
  },

  // ---- TIER 3: Low ----
  L1: {
    report: 'Track-L1_Docs-Roadmap-Drift.md',
    title: 'Documentation / roadmap consistency',
    targets: 'CLAUDE.md, src/docs/roadmap.md, src/docs/Design/**, .claude/rules/**',
    hunt: 'stale "known issue" statements; conflicts between rules and docs; retired-campaign references; validation-guidance drift. Cross-check docs against recent git history and current code.',
  },
  L2: {
    report: 'Track-L2_ObjC-Interop.md',
    title: 'ObjC interop pipeline',
    targets: 'src/Swift.Bindings/src/ObjC/**; ObjC unit tests; Apple supplement paths',
    hunt: 'ObjC import blind spots; availability projection drift; name collisions; Swift-overlay/Foundation routing mistakes.',
  },
  L3: {
    report: 'Track-L3_Performance-API-Drift.md',
    title: 'Performance / API-drift readiness',
    targets: 'src/docs/regression-matrix-performance.md; src/docs/Future/interop-performance-validation-plan.md, api-snapshot-tooling.md',
    hunt: 'missing benchmark hooks; no API-surface ratchet; expensive wrappers hidden behind idiomatic APIs. Identify where future instrumentation would pay off (non-blocking recommendations only).',
  },
}

// ---- fan-out config -------------------------------------------------------
// The harness may deliver `args` as a JSON STRING rather than a parsed object;
// normalize both shapes so args.tracks is always reachable. Without this, a
// stringified args silently falls through to ['A1'] (confirmed via args-probe).
const ARGS = (typeof args === 'string')
  ? (() => { try { return JSON.parse(args) } catch (e) { return {} } })()
  : (args || {})
const requested = (ARGS && ARGS.tracks) || ['A1']
const intensity = (ARGS && ARGS.intensity) || 'heavy'
const verifierModel = (ARGS && ARGS.verifierModel) || undefined // undefined = inherit session model

const FINDERS = intensity === 'heavy' ? 3 : 1
const VERIFIERS = intensity === 'heavy' ? 2 : 1
const BASE_ROUNDS = intensity === 'heavy' ? 2 : 1
const MAX_ROUNDS = intensity === 'heavy' ? 4 : 1
const VERIFY_CAP = 12          // verify at most top-N findings per track (by severity)
const BUDGET_FLOOR = 60000     // stop spawning extra finder rounds below this many remaining tokens

const tracks = requested.map(id => (TRACKS[id] ? { id, ...TRACKS[id] } : null)).filter(Boolean)
if (!tracks.length) {
  log('No valid track ids. Valid ids: ' + Object.keys(TRACKS).join(', '))
  return { error: 'no-valid-tracks', requested }
}
log(`Audit | tracks=[${tracks.map(t => t.id).join(', ')}] intensity=${intensity} finders/round=${FINDERS} verifiers/finding=${VERIFIERS} verifyCap=${VERIFY_CAP}`)

const FINDINGS_SCHEMA = {
  type: 'object',
  properties: {
    findings: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          title: { type: 'string' },
          file: { type: 'string' },
          line: { type: 'integer' },
          severity: { type: 'string', enum: ['P0', 'P1', 'P2'] },
          claim: { type: 'string' },
          evidence: { type: 'string' },
          probeIdea: { type: 'string' },
        },
        required: ['title', 'file', 'severity', 'claim', 'evidence'],
      },
    },
  },
  required: ['findings'],
}

const VERDICT_SCHEMA = {
  type: 'object',
  properties: {
    verdict: { type: 'string', enum: ['confirmed', 'refuted', 'inconclusive'] },
    confidence: { type: 'string', enum: ['high', 'medium', 'low'] },
    method: { type: 'string' },
    result: { type: 'string' },
  },
  required: ['verdict', 'confidence', 'result'],
}

const READONLY =
  'STRICT READ-ONLY on the repository: do NOT edit, create, or delete any repo file except (for the reporter) the single assigned report. Do all compile-probe work in a fresh /tmp directory. The repo root is ' + REPO + '. ' +
  'Target files are named by basename or partial path; the generator source lives under src/Swift.Bindings/src/, the runtime under src/Swift.Runtime/src/, unit tests under src/Swift.Bindings/tests/, and end-to-end tests under BindingTests/. If a path does not resolve directly, locate the file by basename with Grep/Glob first — ALWAYS open and read the actual source file before reporting on it. '

// Systemic error patterns exposed when the 2026-07 deep audit was independently
// re-verified (backup: /Users/wojo/Dev/SB-Backup-Docs/2026-07-deep-audit/, §9).
const LESSONS =
  'Known audit failure modes — actively guard against each: ' +
  '(1) REACHABILITY: never validate a parser/walker-shape claim against hand-written Swift source syntax — swiftc canonicalizes modifier order and shape in generated .swiftinterface, which is the only input the pipeline consumes; grep the actual generated-interface corpus for the REJECTED shape before claiming a gate is reachable. ' +
  '(2) SEVERITY INFLATION: severity must come from the concrete finding evidence, not the narrative — when summarizing, restate the raw Severity/Status/Reachability facts rather than escalating them. ' +
  '(3) DEAD-CODE CLAIMS: any "unused / low-risk delete" claim requires a fresh whole-repo grep for callers at claim time (two such rows in the last audit had live production callers). ' +
  '(4) ALREADY-FIXED: before reporting a missing safeguard, grep for an existing mechanism that already covers it (e.g. a compile-time poison attribute) — the last audit recommended building a weaker version of a guard that already shipped. '

const rank = { P0: 0, P1: 1, P2: 2 }
const keyOf = (f) => `${(f.file || '?').toLowerCase()}::${(f.title || '').toLowerCase().slice(0, 80)}`

const results = await pipeline(
  tracks,

  // STAGE 1 — FIND (budget-aware multi-round, each round seeks NEW defects)
  async (track) => {
    const seen = new Set()
    const all = []
    let round = 0
    while (round < MAX_ROUNDS) {
      const known = all.length ? all.map(f => `- ${f.title} (${f.file}:${f.line || '?'})`).join('\n') : '(none yet)'
      const finders = Array.from({ length: FINDERS }, (_, i) => () =>
        agent(
          `${READONLY}\n${LESSONS}\nYou audit the "${track.title}" track of a Swift->C# binding generator. READ-ONLY.\n` +
          `TARGET FILES (relative to repo root): ${track.targets}\n` +
          `HUNT FOR: ${track.hunt}\n` +
          `You are finder ${i + 1}/${FINDERS}, round ${round + 1}. Findings already reported — do NOT repeat these, find DIFFERENT/additional defects:\n${known}\n` +
          `Locate and read the target files thoroughly (Grep/Glob to find them by basename, then Read the real source; git log -S on key symbols if useful). For each suspected defect, return: a precise title; file + line; severity (P0 crash/silent-wrong-ABI, P1 leak/correctness, P2 smell/maintainability); a CLAIM of exactly what is wrong; EVIDENCE (code excerpt + reasoning); and a PROBE IDEA (smallest Swift/C# program that would confirm or refute it). Surface every genuine concern as a candidate finding, including lower-confidence ones you would want a probe to check — the downstream verifier stage compiles a probe to refute anything that does not hold up, so UNDER-reporting here is worse than over-reporting. Do not fabricate baseless findings, but a real code path you are unsure about IS worth flagging. For a subsystem this complex you should rarely return zero findings; if you genuinely find nothing, return a single P2 finding titled "No defects found" whose evidence lists exactly which files and functions you inspected and why each looked correct.`,
          { label: `find:${track.id}:r${round + 1}:${i + 1}`, phase: 'Find', schema: FINDINGS_SCHEMA }
        )
      )
      const batch = (await parallel(finders)).filter(Boolean).flatMap(r => r.findings || [])
      let added = 0
      for (const f of batch) { const k = keyOf(f); if (!seen.has(k)) { seen.add(k); all.push(f); added++ } }
      round++
      log(`[${track.id}] find round ${round}: +${added} (total ${all.length})`)
      if (round >= BASE_ROUNDS) {
        if (added === 0) break
        if (budget.total) { if (budget.remaining() < BUDGET_FLOOR) break } else break
      }
    }
    return { track, findings: all }
  },

  // STAGE 2 — VERIFY (adversarial compile-probe, top-N by severity)
  async ({ track, findings }) => {
    if (!findings.length) return { track, verified: [], deferred: [] }
    const sorted = findings.slice().sort((a, b) => (rank[a.severity] ?? 9) - (rank[b.severity] ?? 9))
    const toVerify = sorted.slice(0, VERIFY_CAP)
    const deferred = sorted.slice(VERIFY_CAP)
    if (deferred.length) log(`[${track.id}] verifying top ${toVerify.length}/${sorted.length}; ${deferred.length} deferred (listed unverified in report)`)
    const verified = await parallel(toVerify.map(f => () =>
      parallel(Array.from({ length: VERIFIERS }, (_, k) => () =>
        agent(
          `${READONLY}\n${LESSONS}\nAdversarially VERIFY a suspected defect in a Swift->C# binding generator — try to REFUTE it.\n` +
          `TRACK: ${track.title}\nCLAIM: ${f.claim}\nFILE: ${f.file}:${f.line || '?'}\nEVIDENCE: ${f.evidence}\nPROBE IDEA: ${f.probeIdea || '(devise one)'}\n` +
          `Construct the smallest probe in /tmp and compile/inspect it (swiftc, swiftc -emit-sil, swiftc -emit-assembly, swift-demangle, nm on the built dylib, or a dotnet build of a generated binding) to see what the ABI/SIL/compiler ACTUALLY produces. ` +
          `Verdict 'confirmed' ONLY if the probe demonstrates the defect; 'refuted' if it shows the code is correct; 'inconclusive' if you cannot build a decisive probe. Default to 'inconclusive' over 'confirmed' when unsure. Report the exact probe command(s) and what they showed.`,
          { label: `verify:${track.id}:${(f.file || '').split('/').pop()}:${k + 1}`, phase: 'Verify', schema: VERDICT_SCHEMA, model: verifierModel }
        )
      )).then(votes => {
        const v = votes.filter(Boolean)
        const conf = v.filter(x => x.verdict === 'confirmed').length
        const ref = v.filter(x => x.verdict === 'refuted').length
        const status = conf > ref ? 'confirmed' : (ref > conf ? 'refuted' : 'inconclusive')
        return { ...f, status, votes: v }
      })
    ))
    return { track, verified: verified.filter(Boolean), deferred }
  },

  // STAGE 3 — REPORT (one agent writes the markdown file)
  async ({ track, verified, deferred }) => {
    const confirmed = verified.filter(v => v.status === 'confirmed')
    const inconclusive = verified.filter(v => v.status === 'inconclusive')
    const refuted = verified.filter(v => v.status === 'refuted')
    const payload = JSON.stringify({ confirmed, inconclusive, refuted, deferred_unverified: deferred }, null, 1)
    await agent(
      `${READONLY}\nWrite ONE markdown audit report via the Write tool to: ${REPO}/src/docs/audits/${track.report}\n` +
      `Track: "${track.title}". Files audited: ${track.targets}\n` +
      `Verified findings as JSON (confirmed = probe-demonstrated; inconclusive = no decisive probe; refuted = checked & code is correct; deferred_unverified = real candidates not yet probed due to the per-track cap):\n\`\`\`json\n${payload}\n\`\`\`\n` +
      `Report structure: (1) H1 title + one-line scope + overall risk rating 1-5 with a confidence note; (2) "Confirmed findings" — a table (file:line | severity | claim | what the probe showed) plus a short paragraph per item; (3) "Inconclusive / needs deeper probe"; (4) "Deferred (candidate, unverified)" — list them so nothing is silently dropped; (5) "Checked & refuted" — brief, so future agents don't re-chase; (6) "Coverage gaps" — what this track did NOT reach; (7) "Recommended BindingTests fixtures" — describe the Swift shape that would lock down each confirmed defect (do NOT propose code fixes). Use file:line references throughout. Write ONLY this one file, then return a 3-sentence summary (risk rating + #confirmed + the headline issue).`,
      { label: `report:${track.id}`, phase: 'Report' }
    )
    return { track: track.id, report: track.report, confirmed: confirmed.length, inconclusive: inconclusive.length, refuted: refuted.length, deferred: deferred.length }
  }
)

const summary = results.filter(Boolean)
log('Audit complete: ' + summary.map(r => `${r.track}(${r.confirmed} confirmed / ${r.inconclusive} incon / ${r.deferred} deferred)`).join('  '))
return { tracks: summary }
