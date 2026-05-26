# Session 1 — Property-drop bug on PAT-constrained generic parents

**Status: complete — landed in commit `fef9c065`.**

Co-deferred gap 3 from `00-overview.md`. Independent of KeyPath. Smallest unit of work in the subsystem and lands first because it (a) restores diagnostic visibility for a silently-dropped class of properties and (b) is a prerequisite for any property emission on `MusicLibraryRequest<T>` (Session 6).

## Goal

When a Swift type has a PAT-constrained generic parameter (e.g. `struct MusicLibraryRequest<MusicItemType: MusicLibraryRequestable>`) and exposes plain `var` / `let` properties whose accessors transitively inherit the parent's PAT constraints, the property is currently dropped from the generated binding with **no `// SUPPRESSED:` tombstone comment** in the output `.cs` file.

Two outputs from this session:

1. **Diagnostic restoration (must-have)** — every accessor-level skip in `PropertyHandler` emits a tombstone comment in the generated `.cs` so the consumer sees *why* the property is missing. No silent drops.
2. **Root-cause fix (must-have)** — for plain properties whose accessors only inherit parent-PAT constraints (and where the accessor body itself has no method-own generics or PAT receivers), the property emits correctly. Test fixture verifies the three properties `limit`, `offset`, `includeOnlyDownloadedContent`-shaped on a Bag-style fixture pass end-to-end.

## Why first

- Smallest, lowest-risk gap in the subsystem.
- Independent of KeyPath, CSM async, and CSM sync work — can ship in isolation.
- Restoring tombstones costs nothing and pays back during *every* subsequent session: when Session 6 wires MusicLibraryRequest and a property still isn't emitting, the tombstone will tell you which gate suppressed it.
- Codex and Grok both flagged this as the right starting point.

## Dependencies

None. Can run from `main` at any point.

## Suspected silent-drop site (per Codex + Grok convergent investigation)

Primary candidate: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs:461` — the accessor preflight calls `MethodValidationGates.HasUnsupportedProtocolConstraints(accessorEnv)` and on hit calls `SkipProperty(SkipReason.GenericProtocolConstraint, ...)` then returns for the whole property. `SkipProperty` logs via `_logger.LogWarning` but the caller's emission site (`ClassHandler.cs:283`, `NonFrozenStructHandler.cs:222`, `FrozenStructHandler.cs:322`) does **not** emit a `// SUPPRESSED: ...` source comment for properties — only for methods.

Secondary candidates:
- `PropertyHandler.cs:441–509` — preflight loop, any accessor-level rejection (unsatisfied constraint, `ContainsPlaceholder`, no handler) returns early after logging without comment-emit.
- `MemberValidationPipeline.cs:377` — `constrainedExtensionParent.IsGeneric` sibling-count gate for properties on generic parents.
- `MemberEmissionValidator.CanEmitProperty` at `MemberEmissionValidator.cs:67`; `MemberGateEvaluator.EvaluateProperty` at `MemberGateEvaluator.cs:78` — no parent-only PAT fast-path.

Verify the actual site before fixing — the trace should follow the property `limit: Int` on a fixture `struct Request<T: PatProto>` with no method-own generics, no PAT-receiver accessors. The first early-return without a `// SUPPRESSED:` emission is the bug.

## Session 1 work breakdown

### Phase 1.1 — Reproduce + trace

1. Add a minimal fixture `BindingTests/Sources/SwiftBindingsTestLib/Generics/PatParentPlainProperties.swift`:
   ```swift
   public protocol BagItem {
       associatedtype Filter
       associatedtype SortKey
   }

   public struct PlainStringItem: BagItem {
       public struct Filter {}
       public struct SortKey {}
   }

   public struct Bag<Item: BagItem> {
       public var limit: Int = 25
       public var offset: Int = 0
       public var includeArchived: Bool? = nil

       public init() {}
   }
   ```
2. Run `nuke binding-tests --compile-only --permissive` to regen. Inspect the generated `Bag<Item>.cs` (or whichever the emitter writes). Confirm `limit` / `offset` / `includeArchived` are absent with no tombstone.
3. Attach debugger or add `_logger.LogDebug` around `PropertyHandler.cs:438–510` to trace which gate fires. Capture the file:line of the early-return site. *Update this doc with the confirmed site* before moving to phase 1.2.

### Phase 1.2 — Tombstone restoration (must land in same commit as fix)

In every accessor-level early-return inside `PropertyHandler`, emit a tombstone comment in the generated `.cs` *before* returning. Tombstone format:

```csharp
// SUPPRESSED: property "{propertyName}" on {parentTypeName}<...>
// Reason: accessor inherits unsupported protocol constraint ({constraintShape})
```

Pattern to follow: `MethodHandler` already emits `// SUPPRESSED:` comments at every method-skip site. Mirror that pattern in `PropertyHandler`. Tombstone must:
- Include the property name and the parent type's qualified name (with generic params spelled).
- Cite the SkipReason enum value verbatim.
- Live as a leading comment in the place the property would have been emitted (so consumers `grep "SUPPRESSED.*limit"` to find it).

Constraint reference: `.claude/rules/constraints.md` line 18 (`WasEmitted` flag) — make sure the tombstone path doesn't accidentally flip `WasEmitted` true and break `HasPropertyInResolvedAncestors`.

### Phase 1.3 — Root-cause fix

For plain stored properties whose accessors only carry *inherited* parent-PAT constraints (not accessor-own generics, not Self-bound receivers), the property should emit. Concretely, the gate must distinguish:

- **Accessor-own constraint** (the accessor's `MethodDecl.GenericParameters` carries a `T: SomePat` requirement that's local to the accessor) → still skip, tombstone.
- **Inherited parent constraint** (the parent type is `Bag<Item: BagItem>` and the accessor's `accessorEnv` happens to include the parent's `Item: BagItem` requirement) → emit normally; the parent's generic parameter resolves via the closed conformer at CSM time (or via the normal generic-substitution emission for non-CSM contexts).

Likely change: `MethodValidationGates.HasUnsupportedProtocolConstraints` (or a sibling predicate) needs to filter constraints by *origin*. Parent-inherited PAT constraints on a plain stored-property accessor are not a reason to drop — the C# generic parameter (with its own constraint surface) already carries the constraint via the parent type's generic param list. The accessor itself doesn't add a new requirement; the body just reads/writes a stored offset.

Edge case: `var includeArchived: Bool?` — `Bool?` is an `Optional<Bool>`, which the parser should already classify as supported (no PAT involvement). Confirm in the trace.

Edge case: a property whose *type* itself involves the parent's associated type (e.g. `var filter: Item.Filter`). This is harder — it's a parent-associated-type-typed property and *does* need CSM substitution to resolve at the closed-conformer level. **Out of scope for Session 1.** If the fixture exposes such a property, leave it suppressed with a (now-emitted) tombstone, and route the work to a follow-up session.

### Phase 1.4 — BindingTests fixture

Swift side: `BindingTests/Sources/SwiftBindingsTestLib/Generics/PatParentPlainProperties.swift` (above).

C# side: `BindingTests/RuntimeTestsApp/Generics/PatParentPlainPropertiesTests.cs`. Cover:
- Default-value read of `limit`, `offset`, `includeArchived` on a `Bag<PlainStringItem>` instance.
- Mutation: assign new values, observe round-trip.
- `Bool?` Optional roundtrip (set both `null` and `true`).
- A second conformer (`PlainIntItem`) to confirm the property emits per-closed-conformer correctly via CSM specialisation if applicable.

Plus: a *negative* assertion. Add one property to the fixture whose type is `Item.Filter` (parent's associated type). Confirm the tombstone comment is now visible in the generated `.cs` and that the suppression reason is intelligible. This is the regression test for the tombstone restoration, separate from the root-cause fix.

## Validation gates

| Gate | Expected | Notes |
|---|---|---|
| `nuke test` | All unit tests pass; baseline holds | New: tombstone-emission unit test in `PropertyHandlerTests` |
| `nuke binding-tests` (sim) | New `PatParentPlainPropertiesTests` passes | Default Mono JIT run |
| `nuke binding-tests --device` | Same set passes on NativeAOT | Property setters can have NativeAOT-specific bugs; verify |
| `nuke validate` | Cross-validate at least Stripe / BlinkID; baseline holds | Opt-in but recommended — property-handler changes are cross-cutting per `CLAUDE.md` |

`nuke validate` is recommended here even though `CLAUDE.md` lists it as opt-in: this is a property-emission change, and property suppression silently regressing across the validation lib set is the exact failure mode the validate baseline catches. Don't skip it.

## Exit criteria

- Trace site identified, documented in this file (replace the "Suspected silent-drop site" section with confirmed evidence).
- Tombstone comments emit for every accessor-level skip in PropertyHandler.
- Plain stored properties on PAT-constrained generic parents emit correctly.
- BindingTests fixture exercises both the positive (emit) and negative (tombstone visible) paths.
- All four validation gates green.
- Commit message: subject + 1–3 sentences on the *why* (per `feedback_concise_commits.md`). No session/phase numbers in the commit body.

## Risks specific to Session 1

- **Risk: WasEmitted flag flip.** Adding tombstone emission must not flip `WasEmitted` true for the skipped property — that breaks `HasPropertyInResolvedAncestors` (constraint #18). Add a `_logger.LogDebug` confirming `WasEmitted == false` after tombstone emission; assert in a unit test.
- **Risk: Tombstones flood unrelated regen.** Every PropertyHandler skip site getting a tombstone could produce noise on libraries that have always had heavily-suppressed properties. Mitigate: confirm pre-fix vs post-fix diff on each validation lib; if a lib gains >100 lines of new tombstones, audit a sample to ensure they reflect real suppressions, not false positives.
- **Risk: Wrong gate identification.** The first gate that fires may not be the gate that *should* fire — multiple gates can suppress the same property. The trace must continue past the first early-return to confirm no downstream gate would have caught it correctly. Document the full gate chain in this file before changing behavior.
- **Risk: Constraint-of-origin filter regresses a real suppression.** Filtering parent-inherited constraints from the "unsupported" check could accidentally let through a property that *should* be suppressed for some other accessor-level reason. Mitigate: keep all *non-PAT-inheritance* checks in the predicate; only filter `HasUnsupportedProtocolConstraints` against constraints whose origin is the parent type's generic param list, not the accessor's local list or its receiver requirement.

## References

- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs:438–509`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs:283`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs:222`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs:322`
- `src/Swift.Bindings/src/Marshaler/MethodValidationGates.cs` (`HasUnsupportedProtocolConstraints`)
- `src/Swift.Bindings/src/Marshaler/MemberValidationPipeline.cs:377`
- `.claude/rules/constraints.md` line 18 (`WasEmitted` flag) and line 36 (conditional extension constraint gates)
