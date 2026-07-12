# Phase 10 — Emission Structure

**Done.** Split the single `{Module}.cs` mega-file into one file per top-level C# type
(Swift-mode only), a pure repackaging with zero public-API change.

- **Split mechanism:** emitter records each top-level type's char-span + namespace boundary
  offsets (new `CurrentOffset`, same buffer read as trusted `Checkpoint`); `ModuleFileSplitter`
  slices the pre-qualify string into prelude `{ns}.cs` + `{ns}.Types.{Leaf}.cs` per type.
  Case-insensitive (APFS) filename disambiguation. `QualifyNamespaceReferences` applied per file.
- **Downstream:** standalone csproj glob (`BindingProjectEmitter`), SDK `*.cs` glob (already safe),
  all 5 BindingTests csprojs, parity gate, skip-surface path normalization, dep-module move.
- **Deliverable 3 (indent-collapse):** shipped in Phase A (committed `3bf250d7`).
- **Deliverable 4 (`// Unsupported:`):** leave inline — no code change; documented in design doc.

**Gates:** unit 14434/0 (floor bumped); compile-only (compile+parity+api-manifest) green;
sim runtime 3192/0; determinism byte-identical across 2 regens (1258 type files);
10 new `ModuleFileSplitter` tests.

**Review:** Grok caught 3 platform csprojs (Mac/tvOS/Catalyst) missing the `.Types.*.cs` glob
— fixed + compile-verified. Other findings were false positives (contingent on offset
inexactness, disproven). Codex CLI absent on host.
