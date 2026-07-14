# Open Questions — Owner Decisions

These are **not** bugs. They are product/policy choices the audit cannot make for you.

---

## Q1 — Wrapper failure default

When Swift wrapper compilation fails after successful C# generation:

| Option | Tradeoff |
|--------|----------|
| **A. Keep `SwiftWrapperRequired=true`** (today) | Maximum “honest hard fail”; worst day-1 for hard libraries |
| **B. Default soft / partial package** | Sharpie-like; risk silent DllNotFound on wrapper-only APIs unless loud UX |
| **C. Soft only via explicit flag + docs** (short-term) | Safe; discoverability of flag is the tax |

**Audit recommendation:** C immediately; design B with Obsolete/EditorBrowsable on wrapper-dependent APIs before flipping default.

---

## Q2 — Produce-throw reverse-dispatch surface

When EveryProtocol/proxy cannot be filled:

| Option | Tradeoff |
|--------|----------|
| **A. Public member that throws** (today often) | Discoverable; looks like API; causes “bug reports” |
| **B. Omit from public API + report** | Cleaner; harder to discover missing capability |
| **C. EditorBrowsable(Never) + Obsolete** | Compromise |

**Audit recommendation:** B or C for 0.18 UX (G1-003).

---

## Q3 — Mixed ObjC systemic parse failure

| Option | Tradeoff |
|--------|----------|
| **A. Abort entire binding** (today) | No false Mixed claims |
| **B. Opt-in Swift-only continue** | Day-1 salvage; must stamp degraded metadata |

**Audit recommendation:** B as opt-in (G1-002), never silent.

---

## Q4 — baselines.json dead keys

| Option | Tradeoff |
|--------|----------|
| **A. Wire enforcement** for must_pass_* / known_unsupported_total | Real gate cost |
| **B. Delete dead keys** | Honesty |
| **C. Leave** | Continues theater |

**Audit recommendation:** A or B; not C (T4-001).

---

## Q5 — How hard to push dual-oracle simplification pre-0.18

| Option | Tradeoff |
|--------|----------|
| **A. Only byte-identical deletes** (S1-13, docs) | Near-zero risk |
| **B. Behavior-preserving consolidations** (TypeSkip, vtable field) | Needs fixtures |
| **C. Full mega-file splits** | High churn, post-0.18 |

**Audit recommendation:** A now; B in dedicated sessions; C later.

---

## Q6 — Execute vs archive

This audit produced a backlog. Do you want:

1. **Archive only** — pull items as real bugs surface  
2. **Stream A (day-1)** implementation next session  
3. **Stream B (visibility)** first  
4. **Docs-drift-only** pass (constraints.md / roadmap stale rows)  

No default assumed.
