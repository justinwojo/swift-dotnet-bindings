# Structural resilience design for the Swift → C# bindings generator

## Executive position

The proposed spine is directionally correct, but its unit of recovery is wrong. “Member” is an emission convenience, not a soundness boundary.

The architecture should be:

1. Build an immutable, typed binding plan from the parsed Swift model.
2. Attach a stable identity to every declaration, generated artifact, ABI obligation, and capability.
3. Render C# and Swift from independently removable artifact fragments.
4. Verify compilation, symbol closure, layout parity, and ABI contracts.
5. When verification identifies a bad fragment, disable its statically declared recovery unit and propagate removals through a dependency graph.
6. Re-render from the plan, never edit already-emitted text as the primary recovery mechanism.
7. Publish output only when every remaining artifact has discharged its soundness obligations.

The principal recovery units should be:

- Leaf API
- Type representation
- Protocol forward-view capability
- Managed reverse-conformance capability
- Conformance edge
- Shared helper bundle
- Whole module, only for genuinely global failures

A method often is a leaf API. A frozen struct field, protocol requirement, metadata accessor, vtable slot, or shared marshalling helper often is not.

Two hard conclusions follow.

First, compiler success cannot prove ABI correctness. `swiftc` and Roslyn are syntax, type-system, and linkage oracles; neither proves that the two sides agree on calling convention, register classification, ownership, field offsets, or witness-table layout. Recovery must therefore be governed by a typed ABI model, with compilers used as additional oracles.

Second, “always return a binding” is achievable for localized declaration failures only if the system is permitted to escalate conservatively—sometimes from member to conformance or whole type. It is not achievable for corrupt inputs, missing mandatory dependencies, unsupported toolchains, resource exhaustion, or an invalid module root without either lying about success or emitting an empty artifact. The honest product contract should be:

> Every localized failure produces a sound degraded binding. A successful generation never contains an unverified or known-unsafe surface. Non-local input or environment failures remain explicit failures.

That preserves the intended product outcome without weakening the soundness constraint.

---

## 1. Critique of the proposed spine

### Provenance map: right, but insufficient by itself

A direct mapping from generated lines to Swift declarations is foundational. The current system has pieces of this idea, but not the complete relation needed for recovery:

- The file splitter records top-level type character spans, demonstrating that generation-time offsets are already practical in the string emitter ([ModuleEmissionContext.cs:38](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmissionContext.cs:38)).
- Binding reports preserve best-effort swiftinterface source positions, but those locate the input declaration, not the generated C#/Swift artifacts derived from it ([BindingReport.cs:294](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Reporting/BindingReport.cs:294)).
- Post-reconciliation public identities are explicitly heuristic because the text pass cannot recover the original declaration relation ([StrippedSymbolCSharpReconciler.cs:2394](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Configuration/StrippedSymbolCSharpReconciler.cs:2394)).

The proposed map must therefore be more than:

```text
generated span → Swift member
```

It should be an artifact graph:

```text
DeclId
  ├── provides: public API capability
  ├── emits: C# public member fragment
  ├── emits: C# P/Invoke fragment
  ├── emits: Swift wrapper fragment
  ├── emits: callback/helper fragments
  ├── requires: type representation facts
  ├── requires: native symbol
  ├── requires: protocol/conformance capability
  └── recovery unit: leaf / type / conformance / shared bundle
```

One Swift declaration can generate multiple C# and Swift artifacts, and one shared artifact can serve many declarations. A line map alone cannot express either fan-out or fan-in.

### Verify-then-recover loops: correct backstop, but verification is not failure-only

The Swift wrapper is already compiled, so adding recovery after a failed `swiftc` invocation adds almost no cost on healthy inputs. The compiler currently preserves full stderr and throws after a failed invocation ([SwiftWrapperCompiler.cs:1915](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs:1915)). That is a natural recovery seam.

The C# side is different. There is no way to trigger recovery “only on failure” without first compiling or semantically analyzing the output. A C# compile probe is itself healthy-path work. The realistic objective is:

> Healthy bindings pay one verification pass and zero recovery passes.

That is still a good trade, but it is not zero cost.

The loop should also batch failures rather than remove one member per compile:

1. Compile every requested Swift slice.
2. Collect all attributable diagnostics.
3. Union their root recovery units.
4. Apply dependency and escalation closure.
5. Re-render all slices from the same disabled-unit set.
6. Repeat only if new root errors remain.

All target slices must converge on one source surface. If a wrapper is valid on the simulator but invalid on the device, the default safe policy is to remove that API from the binding globally unless platform-conditional API emission is deliberately modeled. Stripping it only from the device wrapper would leave one C# surface with inconsistent native availability.

Recovery must make monotonic progress: every iteration permanently disables at least one new recovery unit. This gives a finite bound of at most the number of units, although normal behavior should remove many units per pass.

### Per-member emission transactions: desirable, but the current checkpoint is not a transaction

The existing `CSharpWriter` checkpoint restores only buffer length and indentation ([IndentedTextWriter.cs:43](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/TextWriter/IndentedTextWriter.cs:43)). The method rollback path checkpoints C#, emits the method and Swift wrapper, then catches only `WrapperSymbolContractException` and rolls back the C# buffer ([MethodHandler.cs:1718](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs:1718)).

That is a useful local repair, but it does not roll back:

- Swift output
- Wrapper-symbol registrations
- Dedup reservations
- Callback/helper collections
- Emission counters
- ReportCollector events
- `MethodDecl.WasEmitted` or related model stamps
- Deferred helper fragments
- `TypeDatabase.ApplyEmissionResult`
- Conformance decisions
- Assembly thunk builders

`ModuleEmissionContext` contains many independent mutable registries. Wrapper-symbol registration alone mutates both a unified set and a per-kind set ([ModuleEmissionContext.cs:1133](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmissionContext.cs:1133)); it also carries mutable conformance decisions and proxy/vtable capability facts ([ModuleEmissionContext.cs:1742](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmissionContext.cs:1742)).

A true transaction cannot be implemented by adding more writer checkpoints. It requires isolated fragments and a journal of side effects, or a clean module re-emission after an aborted attempt.

### Soundness guard: this is the core, not the final polish

The brief calls for a soundness guard but illustrates it with “method safe, stored field unsafe.” That is too shallow. Soundness is not a property of declaration syntax; it is a property of capabilities and ABI footprints.

The guard must understand at least:

- Value representation and layout
- Calling convention and register classes
- Native symbol ownership and availability
- Wrapper/P/Invoke pairing
- Metadata and witness-table dependencies
- Protocol vtable position and width
- Managed interface/conformance obligations
- Ownership and lifetime helpers
- Target-slice consistency

The current `AbiContractChecker` cannot serve as that authority. Its own comments describe approximately 83% precision ([AbiContractChecker.cs:14](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/AbiContractChecker.cs:14)), and its unknown-type fallback assumes a custom type is blittable ([AbiContractChecker.cs:578](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/AbiContractChecker.cs:578)). That is useful linting, not a proof of safety.

Its result is also discarded at the module call site ([ModuleEmitter.cs:131](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmitter.cs:131)). The first immediate correction is to make detected violations actionable, but the longer-term correction is to validate typed lowering plans rather than reverse-engineering ABI facts from C# text.

### Degradation report: right direction, but add cause ownership and cascade structure

The repository already has a strong reporting base:

- Skip rows include reason, details, workaround, and source position ([BindingReport.cs:268](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Reporting/BindingReport.cs:268)).
- Skip triage separates expected structural losses, known limitations, and unexplained review items ([SkipDisposition.cs:16](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Reporting/SkipDisposition.cs:16)).
- Unsupported comments are surfaced as loud degradation events rather than remaining source-only annotations ([UnsupportedCommentEmitter.cs:15](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/UnsupportedCommentEmitter.cs:15)).

What is missing is orthogonal cause ownership. “Known limitation” and “Review” answer whether someone should investigate; they do not answer who can fix it.

Add fields such as:

```text
CauseOwner:
  InputConfiguration
  LibraryAuthor
  Generator
  SwiftToolchain
  DotNetToolchain
  Environment
  Unknown

RecoveryStage:
  Parse
  Plan
  Emit
  SwiftCompile
  CSharpCompile
  AbiValidation
  SymbolValidation

RootCauseId
CascadeFrom
RecoveryScope
CompilerDiagnostic
AffectedArtifacts
Confidence
```

An unanticipated compiler-attributed recovery should default to `Generator` or `Unknown`, never automatically become an accepted known limitation. Missing-module diagnostics can be classified as `InputConfiguration` with the existing actionable dependency guidance.

The report should distinguish one root failure from the 40 members removed because they depend on the failed type. Otherwise a dependency cascade looks like 40 unrelated generator bugs.

---

## 2. The soundness model: safe to drop versus mandatory escalation

## The governing abstraction

Each planned surface must declare two things:

1. Its ABI footprint: what representation, slot, symbol, metadata, or ownership state it contributes.
2. Its consumer capability: what the generated binding promises the consumer can do.

A removal is safe only if:

```text
removing the unit does not alter any retained ABI footprint
and
does not leave any retained capability with an unsatisfied obligation
```

This should be computed statically over a recovery graph, not guessed after a compiler error.

Suggested recovery scopes:

```text
LeafApi
AccessorGroup
TypeSurface
TypeRepresentation
ForwardProtocolView
ManagedProtocolConformance
ConformanceEdge
SharedHelperBundle
Module
```

Every artifact has a declared escalation parent. Recovery begins at the smallest attributable scope and walks upward until all remaining obligations are closed.

## Safe leaf drops

The following are normally safe to remove as a complete logical bundle:

- Free function
- Ordinary instance or static method
- Constructor or failable factory
- Extension method
- Operator
- Convenience overload
- Non-contractual property or subscript accessor group
- Enum case construction or inspection helper
- Wrapper-local helper owned exclusively by one leaf API

“Complete bundle” means removing all generated artifacts belonging to that API:

- Public C# declaration
- P/Invoke
- Swift wrapper
- Callback fields/methods
- Default-parameter overloads
- Narrowing overloads
- Error extractors owned exclusively by it
- Manifest and report entries
- Native thunk, if exclusive

Removing only the public method while leaving a P/Invoke is untidy but normally harmless. Removing only the wrapper while leaving a reachable P/Invoke is unsound.

## Stored properties: separate access surface from representation

A stored property of a frozen struct has two distinct roles:

- It may produce a public getter/setter.
- It contributes bytes to the struct representation.

Dropping its accessors can be safe. Removing or guessing its backing storage is not.

The current frozen-struct handler explicitly emits stored fields to match Swift memory layout and warns that they are layout-only, not value-access fields ([FrozenStructHandler.cs:227](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs:227)). It also treats an indeterminate stored-field size as an invariant violation because guessing would corrupt memory ([FrozenStructHandler.cs:249](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs:249)).

Therefore:

- Failure in property getter/setter lowering → drop the accessor group; retain the storage cell.
- Failure in determining the storage cell’s size, alignment, or carrier type → drop `TypeRepresentation`.
- Dropping `TypeRepresentation` propagates to every API passing, returning, embedding, allocating, or marshalling that type.
- A comment-only tombstone may remain, but no usable C# type may claim the unsafe representation.

This generalizes to enum payload layout, tuple layout, optional layout, and generic value-type instantiations.

The existing `TypeSkipConditions` already models this conservative type-level outcome for indeterminate and mismatched frozen layouts ([TypeSkipConditions.cs:112](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/TypeSkipConditions.cs:112)). That is the correct pattern.

## Classes and opaque/non-frozen value types

Ordinary class member removal is usually representation-neutral, but some generated members are type infrastructure:

- Retain/release
- Payload construction
- Metadata access
- Dynamic type registration
- Base-class initialization
- Object factory
- Existential boxing
- Disposal/lifetime support

Failure in an ordinary method is a leaf drop. Failure in mandatory type infrastructure escalates to `TypeSurface` or `TypeRepresentation`.

A type can remain as an opaque shell only if every retained use is sound. In particular:

- A reference-like opaque handle may still be safely transported if construction, ownership, and destruction remain valid.
- A by-value opaque type cannot be exposed when size or register classification is unknown.
- A tombstone type must not satisfy a generic or conformance constraint that implies unsupported operations.
- Callers returning a tombstoned type must either be removed or explicitly modeled as returning a safe opaque handle.

The current silent-tombstone prepass recognizes that tombstone registration and actual type emission must remain consistent, and it hard-checks that invariant later ([SilentTombstoneRegistrar.cs:17](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/SilentTombstoneRegistrar.cs:17)). The new model should make this relationship structural rather than mirrored by prepasses.

## Protocols: split forward view from reverse conformance

This is the crux.

There are two fundamentally different protocol capabilities:

1. Forward view: C# receives a Swift value that already conforms to `P` and invokes supported requirements through Swift’s real witness table.
2. Reverse conformance: a C# implementation is wrapped in a generated Swift carrier, a witness table/vtable is installed, and Swift calls back into C#.

A missing forward member can often be omitted safely. The actual Swift object still has a valid native conformance; the C# binding merely exposes a subset.

A missing reverse witness cannot be omitted while still advertising managed conformance. Swift’s conformance is all-or-nothing. Every required witness and every positional vtable obligation must remain valid.

The codebase already contains the conceptual distinction. It documents that a read-only proxy can consume a Swift existential without the generated EveryProtocol carrier, while a full reverse-dispatch proxy requires that carrier ([ModuleEmissionContext.cs:1748](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmissionContext.cs:1748)).

The safe policy is:

- If one protocol requirement cannot be bound in the forward direction, remove that member from the forward interface/view.
- If one required reverse-dispatch witness cannot be generated soundly, disable `ManagedProtocolConformance<P>` as a whole.
- Retain the forward view if it remains valid.
- Remove or suppress all APIs that accept an arbitrary C# implementation of `P`.
- Retain APIs that accept or return Swift-vended conformers, if their marshalling path does not depend on the generated reverse proxy.
- Remove the EveryProtocol conformance extension, vtable setter, managed-conformer factory, witness getter associated with the synthetic carrier, and reverse callback registrations as one capability bundle.
- Report “forward-only protocol binding” explicitly.

If the current public `IFoo` type simultaneously represents both forward viewing and implementability, that API conflation must be resolved. The robust design is to represent implementability as a separately generated capability—whether by a distinct interface, marker, factory availability, or metadata attribute. A consumer should not infer that implementing the forward-view interface is sufficient to pass a managed object back to Swift.

## Protocol vtable slots are layout, not members

A requirement excluded from C# surface can still affect positional ABI.

The canonical `VtableLayout` documents three different outcomes:

- Pre-skipped, consumes no index
- Skip-but-consume, consumes an index even though it emits no field
- Included, consumes and emits a slot

For methods, constructors/static/ObjC-optional requirements consume no index, while most other distinct requirements consume an index even when excluded ([VtableLayout.cs:58](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/VtableLayout.cs:58)). The builder implements that exact distinction ([VtableLayout.cs:180](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/VtableLayout.cs:180)).

The C# vtable emitter deliberately ignores member fillability when rendering layout, because shrinking the C# struct would shift later fields relative to Swift ([ProtocolProxyEmitter.Vtables.cs:34](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.Vtables.cs:34)).

Therefore a recovery pass must never physically “delete protocol member X” from the layout model. It may:

- Remove X from the public forward interface.
- Leave a reserved/empty position when the ABI model says the index is consumed.
- Disable the whole reverse-conformance capability so the empty position is never installed or called.

It may not retain a usable reverse conformance with a null or trapping required witness. A deliberate trap is better than memory corruption for debugging, but it still violates the stated “never crash at runtime” product constraint.

## Managed interface and conformance obligations

The current C# stripped-symbol reconciler detects interface implementations and exempts the relevant P/Invoke from stripping so the containing type continues compiling ([StrippedSymbolCSharpReconciler.cs:101](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Configuration/StrippedSymbolCSharpReconciler.cs:101)). `FindExemptedPInvokes` explicitly treats interface members as non-strippable ([StrippedSymbolCSharpReconciler.cs:548](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Configuration/StrippedSymbolCSharpReconciler.cs:548)).

That is compile-preserving but not soundness-preserving. If the Swift symbol was stripped, preserving its P/Invoke can leave a runtime `EntryPointNotFoundException`.

The new rule should be:

- Never preserve a dead native call solely to satisfy a managed interface.
- If the member is required by a generated managed conformance, remove or degrade the conformance capability.
- If the interface is merely a forward view of a real Swift conformance, remove the failed member consistently from the interface and all generated implementers.
- If neither transformation can be proven safe, drop the enclosing protocol/type surface.

The repository’s managed interface should be derived from the settled recovery plan, not treated as an immutable contract that forces unsafe native calls to remain.

## Conformance edges

A concrete Swift type’s native conformance and a generated C# implementation relation are different facts.

Safe cases:

- A real Swift type conforms to `P`; C# retains a forward `P` view with a supported subset.
- A managed class no longer declares a generated interface because the projection is incomplete; the underlying Swift object remains usable through its concrete type.

Escalation cases:

- A generated C# type promises `: IFoo` but can no longer implement IFoo’s settled member set.
- A generated managed conformer can be passed into Swift, but a required witness or carrier helper is missing.
- A generic constraint is emitted based on a conformance whose witness-table or metadata path is unavailable.
- A conformance supplies associated-type or PWT information used by retained call signatures.

In those cases remove the conformance edge and propagate the loss to all APIs requiring it. If the edge is inherent to the type’s only valid representation, drop the type.

## Shared helpers

If a failing diagnostic lands in a shared helper, do not choose an arbitrary nearby member. The helper must declare its owners.

- One owner → drop that leaf bundle.
- Several independent owners → drop all owners or split the helper.
- Mandatory module/type helper → escalate to its owning type or module capability.
- Unknown owners → no safe localized recovery; re-run from a clean plan with the owning higher-level unit disabled.

Examples include UTF-8 helpers, error registries, metadata aggregators, EveryProtocol carriers, closure context helpers, and NativeAOT registration code.

## Static proof obligations before publication

A successful degraded binding should satisfy all of these:

1. Every retained type has a proven representation category.
2. Every by-value type has known size, alignment, field offsets, and relevant register classification.
3. Every retained P/Invoke has a typed calling-convention plan.
4. Every wrapper-targeting P/Invoke has exactly one retained wrapper definition.
5. Every retained wrapper definition is present in every promised native slice.
6. Every direct native P/Invoke targets an exported symbol in every promised slice.
7. Every protocol vtable pair derives from the same canonical layout object.
8. Every advertised managed reverse conformance has all required witnesses and infrastructure.
9. Every generated C# interface implementation is complete under the settled member set.
10. Every retained dependency edge points to a retained artifact or an explicitly provided external artifact.
11. C# compiles with the actual reference set and source generators.
12. Swift wrappers compile and link for all promised targets.
13. No ABI-contract violation remains.

If an obligation cannot be proved, escalation is mandatory. “The compiler accepted it” is not a substitute.

---

## 3. Attribution strategy: precise provenance first, bounded isolation fallback

## Recommendation: hybrid, with provenance as the normal path

Use precise provenance for ordinary recovery and bounded delta debugging only when compiler diagnostics cannot be assigned confidently.

Bisection should not be the primary strategy because:

- Compiler errors are not reliably monotonic. Removing one declaration can expose a different overload ambiguity or missing reference.
- Generated fragments have dependencies; half-module removal often creates artificial failures.
- A maximal compiling subset is not unique.
- Compile success says nothing about ABI safety.
- O(log n) assumes a single independent offender. Real compiler output often contains several roots and many cascades.

## Stable identities

Assign each parsed declaration a deterministic `DeclId`, preferably derived from stable ABI facts:

```text
module
decl kind
fully qualified Swift path
mangled symbol or USR
accessor kind
parameter labels/types
generic context
```

Synthesized artifacts receive stable IDs derived from their owner and role:

```text
DeclId / csharp-public
DeclId / pinvoke
DeclId / swift-wrapper
DeclId / callback-0
ProtocolId / reverse-vtable
TypeId / metadata-helper
ModuleId / initializer
```

Do not use current line numbers or emission order as identity.

## Immutable fragments and fresh maps per render

Each output attempt should be rendered from an immutable set of fragments. A fragment records:

```text
ArtifactId
RecoveryUnitId
Owners
Requires
Provides
Text producer
Generated file
Current start/end offsets
```

After disabling units, re-render the file from retained fragments and rebuild the interval map. Line drift then becomes irrelevant: diagnostics are always interpreted against the map for the exact source version that produced them.

The recovery system should not repeatedly edit a file in place and try to adjust old spans.

Marker comments are still valuable as an audit and fallback:

```swift
// swift-bindings-origin: <ArtifactId> begin
...
// swift-bindings-origin: <ArtifactId> end
```

```csharp
// swift-bindings-origin: <ArtifactId> begin
...
// swift-bindings-origin: <ArtifactId> end
```

They help diagnose saved artifacts and survive external build tools, but the in-memory interval map remains authoritative.

`#line` or Swift `#sourceLocation` directives could encode logical IDs directly into diagnostics, but they also perturb user-facing debugging and compiler behavior. I would not make them the primary mechanism. They can be enabled in a diagnostic-only probe render if needed.

## Diagnostic attribution algorithm

For each diagnostic:

1. Resolve its file and line/column against the exact render map.
2. If it falls inside one fragment, attribute it to that artifact.
3. If it falls on a fragment boundary, examine the syntax node or token span and adjacent artifacts.
4. Translate the artifact to its declared recovery unit.
5. Classify whether the error is:
   - Local declaration failure
   - Unsatisfied dependency caused by an already-disabled unit
   - Shared-helper failure
   - Global module/dependency/toolchain failure
6. Batch all confident root units.
7. Apply the recovery dependency/escalation closure.
8. Re-render and retry.

For Roslyn, use structured `Diagnostic.Location.SourceSpan` and the syntax tree rather than parsing console text. If using an MSBuild/Csc invocation for exact project fidelity, emit SARIF/error-log output and map its locations.

For `swiftc`, parse the standard `file:line:column` diagnostics, but retain the full raw stderr. Linker diagnostics without a source location should be mapped by symbol to artifact ownership. The wrapper compiler already extracts missing modules separately and emits dependency guidance ([SwiftWrapperCompiler.cs:1947](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs:1947)); those should be classified as global or dependency-graph failures rather than blindly attributed to the last source line.

## Fallback isolation

Use bounded delta debugging only when:

- The diagnostic has no usable source location.
- It points into a shared compiler-generated construct with uncertain ownership.
- The compiler crashes without a diagnostic.
- A source-level attribution repeatedly makes no progress.

Run isolation over recovery units, not text lines. Preserve all mandatory dependencies when constructing a candidate subset. Once a minimal failing set is found:

- If it contains one leaf unit, drop it.
- If it contains an inseparable group, drop their nearest common soundness scope.
- If it contains a shared capability, escalate to that capability.
- If no stable attribution emerges, fail honestly rather than emit an unverified artifact.

After a clean build, optionally try to re-enable units removed only by uncertain fallback isolation. This avoids making a compiler cascade permanently over-strip the surface.

---

## 4. Exception containment with shared mutable emitter state

## Current evidence

The parser already contains many declaration-local exceptions. `HandleNode` catches arbitrary exceptions, records the declaration as dropped, and continues ([SwiftABIParser.cs:1101](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Parser/SwiftABIParser.cs:1101)). That is useful evidence that declaration-level degradation is workable.

Its limitation is granularity. If constructing a `TypeDecl` throws before child collection, the whole type is lost. If a later pass throws after shared state has been mutated, the outer generation catch aborts the module ([Program.cs:751](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Program.cs:751)).

The throwing and nonthrowing type-name APIs illustrate the input-boundary problem:

- `FromModuleQualifiedName` throws on several invalid shapes and does not reject a generic placeholder root ([SwiftTypeName.cs:44](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Model/TypeNames/SwiftTypeName.cs:44)).
- `TryFromModuleQualifiedName` rejects placeholder-rooted identities and is explicitly intended for untrusted ABI spellings ([SwiftTypeName.cs:63](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Model/TypeNames/SwiftTypeName.cs:63)).
- One careful consumer already uses the nonthrowing path to prevent one generic View parameter from aborting generation ([SwiftUIBridgeEmitter.InitAnalyzer.cs:427](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/SwiftUIBridgeEmitter.InitAnalyzer.cs:427)).

The right fix is not scattered `try/catch` blocks around `FromModuleQualifiedName`. All ABI-derived names should pass through a nonthrowing parse/result boundary, while throwing factories should be reserved for generator-owned constants and proven invariants.

## Is true per-member transactional emission feasible?

Yes, but not by snapshotting the current live emitter.

A feasible transaction has:

```text
EmissionTransaction
  CSharpFragments
  SwiftFragments
  NativeThunkFragments
  SideEffectJournal
  ReportEvents
  TypeEmissionFacts
  SymbolClaims
  HelperClaims
  DependencyEdges
```

The transaction performs planning and rendering against read-only state plus a transaction-local overlay. Commit atomically merges its fragments and facts. Rollback discards them.

Shared claims such as symbol names and dedup keys need reservation semantics:

1. Plan candidates deterministically.
2. Resolve claim conflicts before rendering, or reserve against a transaction overlay.
3. Commit claims only with the owning artifact.
4. If an artifact is later disabled, its claims are removed by rebuilding the settled plan rather than mutating global sets ad hoc.

`TypeDatabase` should be immutable during emission. The repository already nearly has this boundary: structural writes are frozen, and only `ApplyEmissionResult` is permitted after freeze ([ITypeDatabase.cs:96](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/TypeDatabase/ITypeDatabase.cs:96)). Those emission results should become transaction-local facts and be applied only after the type or module settles.

Likewise, `ReportCollector` events should be journaled and committed with the artifact. Otherwise a rolled-back member may remain counted as emitted or skipped for the wrong reason.

## Practical first implementation: clean-attempt restart

Retrofitting every emitter with a journal is a large change. The safest initial containment mechanism is attempt-level restart:

1. Establish an `EmissionScope` containing the current `ArtifactId` and recovery unit.
2. Begin a complete module emission attempt with a fresh:
   - `ModuleEmissionContext`
   - Report session
   - Output buffers
   - Emission-result overlay
3. If an unanticipated exception escapes a scoped unit:
   - Record its exception and recovery unit.
   - Abort the entire attempt.
   - Disable that recovery unit using the soundness policy.
   - Re-run module emission from the immutable plan with fresh state.
4. Publish only a settled attempt.

This is slower on failures but avoids continuing from tainted shared state. It also provides resilience before every emitter has been converted to fragment-local transactions.

The base type database must not retain stamps from the failed attempt. Either:

- Move all `ApplyEmissionResult` facts into a disposable overlay, or
- Rebuild/clone the emission database for each attempt.

The first is the cleaner long-term design.

Once restart-based containment is working, migrate hot leaf emitters to real local transactions for performance.

## Which exceptions are recoverable?

Catch arbitrary exceptions only inside an isolation boundary that guarantees no state was committed.

Do not convert these into member skips:

- `OutOfMemoryException`
- `StackOverflowException`
- Process cancellation
- Filesystem/output corruption
- Toolchain executable absence
- Invalid module root
- Failure to load mandatory reference assemblies
- Invariants whose owner cannot be determined
- Failures after partial external publication

An invariant exception inside a scoped fragment can trigger a clean-attempt restart and conservative escalation. An invariant exception after live shared state mutation cannot safely be swallowed in place.

---

## 5. Is there a fundamentally better architecture?

## Emit everything, then tree-shake

Not as the primary design.

Advantages:

- Simple optimistic first pass
- Compiler sees the whole generated program
- Potentially high API retention

Fatal weaknesses:

- Text does not encode ABI obligations.
- Removing one declaration can require nonlocal changes to interfaces, vtables, metadata registration, overloads, and reports.
- Compiler success cannot establish ABI correctness.
- The existing C# reconciler’s complexity—multiple transitive passes over fields, callers, forwarders, module initialization, and facades—is evidence of how quickly text tree-shaking becomes a second fragile compiler ([StrippedSymbolCSharpReconciler.cs:136](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Configuration/StrippedSymbolCSharpReconciler.cs:136)).
- Heuristic preservation of interface implementations can retain runtime-dead calls.

Keep text reconciliation only as temporary compatibility machinery while moving to artifact fragments.

## Probe-build to discover a maximal compiling subset

Also not the primary design.

A maximal compiling subset is not necessarily ABI-safe, and “maximum” subset selection is combinatorial and policy-dependent. Interacting declarations can make compilation nonmonotonic. The compiler may also report cascades that cause unnecessary loss.

Probe-build isolation is valuable as a fallback attribution mechanism, not as the generator’s semantic architecture.

## Compiler-as-oracle from the start

Better for syntax and semantic validity, but insufficient alone.

Roslyn can authoritatively answer whether C# compiles, and `swiftc` can authoritatively answer whether the wrapper source type-checks and links. They cannot determine whether:

- A C# struct matches Swift field offsets
- CallConvSwift carriers use correct registers
- Ownership conventions match
- A witness-table slot width is correct
- A wrapper and P/Invoke agree semantically despite compatible surface types

The compiler must be one oracle among several.

## Recommended architecture: proof-carrying binding plan plus optimistic verification

The fundamentally better design is not “predict versus verify.” It is:

```text
Parse
  ↓
Immutable semantic binding plan
  ↓
Typed lowering plans with explicit proof obligations
  ↓
Artifact/capability dependency graph
  ↓
Optimistic full render
  ↓
Swift + C# compiler verification
  ↓
Static ABI/symbol/layout validation
  ↓
Policy-driven recovery and re-render on failure
  ↓
Settled artifact publication
```

Prediction gates remain useful as cheap plan construction. They should stop being an ever-growing set of unrelated booleans and instead select among typed lowering outcomes:

```text
DirectSwiftCallPlan
CdeclWrapperPlan
NativeThunkPlan
OpaqueHandlePlan
ForwardProtocolViewPlan
ManagedConformancePlan
UnsupportedPlan
```

A plan constructor succeeds only when it has the facts needed for its ABI contract. Unsupported or indeterminate facts produce an explicit non-emitting plan.

The current `MemberValidationPipeline` is already an orchestration layer separating emission and wrapper eligibility ([MemberValidationPipeline.cs:6](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/MemberValidationPipeline.cs:6)). It should evolve into plan selection rather than be discarded.

Likewise, the canonical `VtableLayout` is an excellent example of the target architecture: one typed model owns membership, index, and width, and both Swift and C# render from it ([VtableLayout.cs:51](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/VtableLayout.cs:51)).

The compiler recovery loop then handles the unavoidable unmodeled tail without becoming responsible for ABI semantics.

---

## 6. Concrete staged implementation path

## Stage 0: eliminate known unsound “successful” outcomes

This is the highest immediate risk reduction.

1. Stop discarding `AbiContractChecker.Validate` results.
   - Initially, any violation should prevent publication or remove its attributable API.
   - Do not call it a soundness proof until it operates over typed plans.

2. Remove the SDK-mode policy that treats a missing wrapper as a valid binding.
   - `EffectiveOutcome` currently downgrades fatal wrapper failure to warning in SDK mode ([SwiftWrapperCompiler.cs:94](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs:94)).
   - The resulting diagnostic explicitly says wrapper-dependent methods will throw `DllNotFoundException` ([Program.cs:2350](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Program.cs:2350)).
   - That directly violates the new soundness constraint.
   - Until recovery exists, wrapper failure must fail publication.

3. Remove the reconciler exemption that retains a dead P/Invoke to preserve an interface implementation.
   - If safe conformance/interface rewriting is unavailable, fail closed at the enclosing conformance/type.

4. Make wrapper-symbol integrity failures recovery inputs rather than terminal module failures.
   - The current gate correctly detects dangling wrapper references and hard-fails ([WrapperSymbolIntegrityGate.cs:59](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Emitter/StringEmitter/WrapperSymbolIntegrityGate.cs:59)).
   - Preserve its fail-closed behavior until precise recovery is installed.

This stage may temporarily increase hard failures. That is preferable to producing compile-successful runtime faults.

## Stage 1: stable identities, capability graph, and recovery policy

Build the foundation before writing another text stripper.

Deliver:

- Stable `DeclId` and `ArtifactId`
- `RecoveryUnit`
- `RecoveryScope`
- `Requires`/`Provides` graph
- ABI footprint classification
- Escalation parent
- Root/cascade reporting
- Explicit protocol capabilities:
  - Forward view
  - Managed reverse conformance
- Type representation capability
- Shared-helper ownership

Implement the safe-drop policy as code with exhaustive tests. Adding a new artifact/recovery kind without a declared escalation rule should fail tests and default to conservative escalation.

This stage does not need compiler recovery yet. It makes existing prediction skips and reports use the same semantic units future recovery will use.

## Stage 2: immutable fragment rendering and clean-attempt restart

Refactor output construction so C# and Swift are collections of owned fragments.

Start with:

- Ordinary methods
- Constructors
- Properties/subscripts
- Their P/Invokes and Swift wrappers
- Exclusive callbacks/helpers

Keep module/type preludes as higher-level fragments.

Add generated interval provenance for every render.

Introduce scoped exception attribution and whole-attempt restart:

- Fresh emission context
- Fresh report session
- Fresh output buffers
- Emission-result overlay
- Disabled recovery-unit set

On an exception, abandon the attempt, escalate, and restart. This gives correct exception containment before fine-grained transaction journaling is complete.

Also enforce the untrusted-name boundary:

- ABI-derived strings use `TryFromModuleQualifiedName` or a richer parse-result type.
- Throwing factories are limited to generator-owned identities and asserted invariants.
- Parsing failure returns a typed unsupported reason attached to the current declaration.

## Stage 3: Swift wrapper diagnostic recovery

This should be the first compiler recovery loop because wrapper compilation already exists and the current failure family is concrete.

Implement:

1. Preserve fragment maps for the cleaned Swift source.
2. Run every promised slice and collect diagnostics before changing the disabled set.
3. Attribute source diagnostics by interval.
4. Attribute linker diagnostics by emitted symbols and artifact ownership.
5. Classify missing modules and global link dependencies separately.
6. Disable attributable recovery units.
7. Apply soundness escalation.
8. Re-render all slices from one settled source set.
9. Recompile until clean or no progress.

Retire known-pattern stripping gradually:

- Keep `SwiftWrapperPostProcessor` as a pre-verification optimization at first.
- Record every pattern strip through the same recovery-unit machinery.
- Once compiler-driven recovery has corpus coverage, remove pattern-specific logic whose only role is predicting compilation failure.

Do not retain regex text stripping as an independent authority.

## Stage 4: C# semantic and compile recovery

Use Roslyn in process for structured diagnostics and speed. The generator already references `Microsoft.CodeAnalysis.CSharp` ([Swift.Bindings.csproj:22](/Users/wojo/Dev/swift-bindings/src/Swift.Bindings/src/Swift.Bindings.csproj:22)).

The probe must include:

- Actual generated files
- Correct target framework reference assemblies
- Swift.Runtime reference
- Apple platform references
- Dependency binding references
- Unsafe/nullable/language options
- LibraryImport source generator and relevant analyzers
- The same conditional symbols as the generated project

If exact parity with the generated MSBuild project cannot be maintained, use the real project build as the publication gate and consume structured SARIF diagnostics. An approximate Roslyn probe may accelerate recovery, but it cannot be the sole “compiles” guarantee.

Recovery follows the same artifact graph:

- Error in leaf member → drop leaf bundle.
- Error caused by a dropped type → dependency closure, not a second unrelated root.
- Interface obligation error → recompute interface/conformance capability.
- Error in type infrastructure → escalate to type.
- Error in shared module initializer → remove only an independently owned statement if modeled; otherwise escalate to the shared bundle.

The existing reconciler can then be retired incrementally.

## Stage 5: typed ABI contracts

Replace text extraction in `AbiContractChecker` with validation over `AbiCallPlan`.

Each retained native call plan should contain:

```text
Swift lowered parameter/return carriers
C# lowered parameter/return carriers
Calling convention
Indirect-result convention
Self convention
Ownership convention
Library
Entry point
Wrapper ArtifactId, if any
Per-target symbol availability
Size/alignment/register facts
```

Wrapper plans should expose matching descriptors. Validation compares descriptors, not rendered source.

Similarly, type representation plans should carry layout evidence, and protocol plans should carry the shared `VtableLayout`.

Only after these typed validators pass should the generated text be considered eligible for publication.

Keep text scanning as defense-in-depth for a transition period, but disagreement between typed facts and text should be a generator invariant failure.

## Stage 6: settled publication and reporting

Do not write canonical output piecemeal during attempts.

Write attempts to staging storage, then atomically promote only after:

- Swift compilation succeeds for all promised slices
- C# compilation succeeds
- Symbol closure succeeds
- ABI validators are clean
- Layout/conformance invariants are clean
- Reports and manifests reflect the settled disabled set

Generate:

- `binding-report.json`
- Human-readable API/degradation report
- Root-cause and cascade grouping
- Owner classification
- Exact recovery scope
- Compiler diagnostic excerpts
- Suggested user action
- Generator defect fingerprint for deduplication

A dropped whole type should still produce a report/tombstone artifact, but not a fake usable C# type if its representation is unsafe.

## Stage 7: bounded isolation fallback and optimization

After the main system is sound:

- Add dependency-aware delta debugging for unattributed compiler crashes.
- Cache healthy verification by input/toolchain/plan fingerprint.
- Batch diagnostics to minimize recompiles.
- Convert high-volume leaf emitters from whole-attempt restart to local fragment transactions.
- Freeze expansion of hand-coded prediction gates unless a gate:
  - Avoids expensive known failures
  - Encodes ABI knowledge unavailable to the compiler
  - Improves the diagnostic reason
  - Selects a different valid lowering plan

A new compiler-only shape should normally be handled by the verification backstop and then promoted into a predictor only if it is common enough to justify the maintenance cost.

---

## Final recommendation

Adopt the proposed spine, but redefine it as follows:

- Provenance map → stable artifact/capability graph
- Member stripping → policy-driven recovery-unit disabling
- Text mutation → immutable fragment re-rendering
- Writer checkpoint → isolated attempt or journaled transaction
- Compiler success → one necessary proof obligation
- Protocol member drop → forward-view reduction or whole reverse-conformance removal
- ABI checker warnings → typed fail-closed ABI validation
- Degradation list → root/cascade report with owner and confidence

The most important invariant is:

> No recovery operation directly edits the retained ABI model. It disables a declared capability, then the generator recomputes every artifact and obligation from the settled plan.

That makes localized unanticipated failures containable without allowing “compiles but crashes” degradation. It also aligns with the strongest parts already present in the repository: centralized type skip conditions, parser node reconciliation, canonical vtable layout, wrapper-symbol registries, and structured skip reporting.

No files were modified.