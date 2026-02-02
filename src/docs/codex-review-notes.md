# Codex Review Notes (2026-02-02)

This file lists design, architecture, and direction issues/improvements identified during a review of the Swift/.NET bindings repo. It is intended for follow‑up discussion and roadmap decisions, not as a task list.

## Priority Findings (ordered by severity)

1) **Public API stability risk: placeholder namespace mapping**
   - Current registrations use `Swift.{Module}` plus hand‑built type identifiers.
   - When “real” mapping lands, the generated C# API surface will change (breaking).
   - Suggestion: lock a mapping scheme now via config (defaults + per‑module overrides), and keep `Swift.{Module}` only as a fallback.
   - References: `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` (RegisterStructType/RegisterEnumType/RegisterClassType/RegisterProtocolType)

2) **Type system cohesion: TypeDatabase mixes nominal and non‑nominal types**
   - TODO notes that tuples/closures should not live in the TypeDatabase.
   - This makes “processed” semantics ambiguous and increases AnyType fallbacks.
   - Suggestion: introduce a `TypeGraph`/`CompositeTypeFactory` layer responsible for building complex types from nominal types, keeping the TypeDatabase nominal-only.
   - References: `src/Swift.Bindings/src/TypeDatabase/TypeDatabaseExtensions.cs`

3) **Cross‑module resolution is ad‑hoc and fragile**
   - `_outOfModuleTypes` and `_moduleAliases` are temporary but already in active use (closed generics, CoreFoundation/CoreGraphics).
   - As the library surface grows, this will become a policy problem.
   - Suggestion: formalize a “type origin + resolution policy” with explicit config and diagnostics.
   - References: `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs`

4) **AnyType fallback hides binding gaps**
   - Bound generics and unsupported closures map to `AnyType` or `object`, which compiles but silently degrades API correctness.
   - Suggestion: add an explicit “UnsupportedType” placeholder in generated code, and/or elevate diagnostics to summary output so gaps are visible to users.
   - References: `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs`, `src/Swift.Bindings/src/TypeDatabase/TypeDatabaseExtensions.cs`

5) **Async properties are not fully blocked**
   - There is a TODO to detect/skip async properties; currently nothing prevents partial emission.
   - Suggestion: pre‑scan properties for async accessors and skip with explicit warning.
   - References: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs`

6) **Protocol proxy architecture blocks PATs & Self requirements**
   - Proxy emitter hard‑skips these cases due to generic constraints and `[UnmanagedCallersOnly]`.
   - Suggestion: extract a `ProtocolProxyStrategy` abstraction so a future strategy can be swapped without touching emitter logic (e.g., non‑generic base + per‑instantiation non‑generic helper types).
   - References: `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs`

7) **Type metadata cache lacks built‑in entries**
   - Built‑in metadata cache is empty per TODO.
   - Suggestion: pre‑seed common scalar/string metadata for determinism and perf.
   - References: `src/Swift.Runtime/src/Swift/Runtime/TypeMetadata.cs`

## Additional Architectural Suggestions

- **Surface a “binding completeness report”.**
  Emit a structured summary (JSON + console) of skipped members/types with reason codes (e.g., UnsupportedType, AnyTypeFallback, AsyncProperty).

- **Tighten public API contract around fallbacks.**
  Consider explicit opt‑in flags for “allow AnyType/object fallbacks” so SDK consumers can choose strictness.

- **Configuration versioning.**
  As namespace mapping and resolution policies solidify, add a versioned config schema and include the config hash in generated output for traceability.

- **Testing strategy for cross‑module and alias behavior.**
  Add tests to explicitly cover module aliasing and out‑of‑module resolution so refactors don’t regress.

## Open Questions

- Should `Swift.{Module}` remain the default namespace, or should a more .NET‑idiomatic mapping be adopted now to avoid churn?
- Is “AnyType fallback” acceptable in shipping bindings, or should it always be a warning/error?
- Do you want formal config for module aliasing and cross‑module generics, or a hard‑coded resolver?

