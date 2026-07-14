# Data Pack — Visibility Dual-Oracle Evidence (A8 deep)

**Status**: Evidence package for DA-W5-A8-001/002/003 — still **no production fix**

---

## Mechanism

### PublicMemberNames (SwiftSyntax host)

`MemberCollectionWalker.swift` builds `publicMemberNames` for negative-space internal detection.

Documented gate (file header + body comments):

> After access keyword, modifiers must satisfy per-kind allow-list.  
> **`public nonisolated func` is rejected** (BroadPublicFuncRegex disallows nonisolated).  
> `public required init` may be OK for inits;  
> `public nonisolated class` fails type-name gate.

Also:

- Protocol requirements without explicit `public`/`open` on the requirement line are **not** in the set (implicit public in Swift) — roadmap A8-001.  
- Subscripts contribute to publicMemberNames only under their own rules.

### Consumer: IsInternalFromPublicMemberNames

`SwiftABIParser.cs` (~855–881, 2560, 3140):

- If `PublicMemberNames` non-empty and key **absent** → treat as module-internal (negative space).  
- Used for `IsModuleInternal` on decls.

### Downstream effects of false internal

| Consumer | Effect |
|----------|--------|
| Property emission `CanEmitProperty` | **Drop** public property |
| ParentModuleInternalNoFallback | Drop async/closure/operator on internal parent |
| EveryProtocol / protocol handlers | Mitigations exist for some protocol req paths (`allowAbstract`, ProtocolHandler) |
| KeyPath | allowAbstract workaround for protocol bag |

---

## Confirmed intentional exclusions (code comments)

| Shape | In PublicMemberNames? | Source |
|-------|----------------------|--------|
| `public nonisolated func` | **No** (regex disallow) | MemberCollectionWalker, SignatureFactsWalker |
| `public nonisolated class` | **No** | PublicTypeNamesWalker |
| Protocol requirement without `public` keyword | **No** | Implicit public gap (roadmap) |
| `public required init` | Init path may allow | SignatureFactsWalker note |
| `nonisolated(unsafe)` | Intentionally **dropped** from nonisolatedMembers tracking | ActorIsolationWalker |

ActorIsolationWalker **separately** tracks `nonisolatedMembers` for isolation facts — that set is **not** the same as PublicMemberNames. A member can be known nonisolated for isolation while still missing from PublicMemberNames → false internal.

---

## Why this is high-value for workers

1. **Not theoretical**: host code explicitly documents excluding `public nonisolated func`.  
2. **Modern Swift APIs** increasingly use `nonisolated` on public members (Swift 5.5+ concurrency).  
3. **False ModuleInternal** looks like “expected skip” in triage → **silent undercount**, not Review.  
4. Fix shape is centralized: broaden PublicMemberNames allow-list **or** don’t use negative-space for lines that only fail the allow-list (use UsableFromInline / AccessControl only).

---

## Probe plan (for later workers — not run this pass)

1. Swift fixture: `public struct S { public nonisolated var x: Int; public nonisolated func f() }`  
2. Regen with swiftinterface facts  
3. Assert: not ModuleInternal; members emit  
4. Same for protocol requirement without `public` keyword  
5. Count hits in Apple frameworks: grep `public nonisolated` in SDK swiftinterfaces  

---

## Related dual oracles (same family)

| Oracle | Role |
|--------|------|
| PublicMemberNames negative space | Internal detection |
| UsableFromInline + AccessControl | Positive internal |
| Inlinable without AccessControl | Ambiguous internal |
| nonisolatedMembers (ActorIsolation) | Isolation facts only |

**S1-05 VisibilityClassifier SSOT** from simplification backlog collapses these.
