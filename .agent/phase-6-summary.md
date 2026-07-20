# Phase 6 — Settled Publication

**Obligation ledger** (`PublicationObligationLedger.cs`) — an honest record, never promotes an unrun verifier to proven. 13 obligations; verdicts ProvenByConstruction / ByVerifier / NotApplicable / Unproven. Verifier→test (`PublicationObligationLedgerTests`, `CSharpVerifyRecoverDriverTests`): ob2 frozen-struct layout (IntPtr fallback; unresolvable→fail-closed); ob3 ABI validation (typed-plan subset + `[LibraryImport]` backstop); ob4 wrapper-symbol integrity gate; ob5/12 in-loop verify-slice compile; ob7 VtableLayout; ob9/11 C# verifier (CSharpVerifiedClean); ob10 leaf/accessor self-containment; ob13 ABI validator. Codex r3–r6 narrowed ob1/2/3/5/12 from overclaims to exactly what each verifier proves.

**Usable-surface (D-R6):** ships iff `EmittedMembers>0 OR (EmittedTypes−silentTombstones)>0`; SWIFTBIND116 fails closed only when zero members AND every type a silent tombstone. All BindingTests/corpus shapes shipped (no 116).

**Parity:** artifact-parity 0 new violations, 206 witness getters identical → converged==recompile.

**Strip stats:** 0 post-processor strips (reconciler retired on loop path); resilience-kitchen withdrew 2 hostile members via verify-recover, C# compiles, no dangling wrapper.

**Asymmetry:** loop/sim→ledger + in-loop verify slice; no-wrapper→wrapper obligations NotApplicable, C# via compile-only leg; post-report fat build + SWIFTBIND115 strip = final slice/strip authority (not folded into ledger; whole-publication atomicity deferred).

**Gates:** unit 15409/0; binding-tests compile-only 0 errors; sim 3242/0/0-crash.
