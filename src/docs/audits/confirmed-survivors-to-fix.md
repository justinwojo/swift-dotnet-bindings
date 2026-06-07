# Confirmed Audit Survivors — Actionable Fix Backlog

**Date captured:** 2026-06-07
**Status:** Open. Not yet fixed. Captured for a future session.
**Provenance:** Code-trace verification (Claude, read-only) of the High-impact "still latent" items in `grok-phase2-remaining-hardening-candidates.md` (see its §0 Verification Log for the full verdict table) and `grok-audit.md` (see its top "Verification status" callout).

---

## How to pick this up cold

The two Grok docs (`grok-audit.md`, `grok-phase2-remaining-hardening-candidates.md`) synthesized the original 14-track audit's *deferred* pool and flagged ~dozens of "still latent" candidates. A follow-up verification pass re-checked the 8 highest-impact High clusters against current source. **Most were false-positive / already-mitigated / latent-but-unreachable** (Apple short-prefixes, enum width-truncation, co-gater brace-walker, parser comment/string blindness, SwiftUI reserved-name collisions, SwiftUI ObjC-closure UAF/leak, demangler Ya/Yb/YK) — those are annotated in the Grok docs and should **not** be re-chased without new evidence.

**This doc is the residue: the 3 reachable bugs worth fixing, plus minor deferred items.** None is a launch-blocking process crash; two (#1, #2) are correctness/ABI issues on common real-binding shapes.

> ⚠️ **Line numbers below are as-of 2026-06-07 and will drift.** Grep/re-confirm before editing. These are *code-trace* verdicts, **not** compile/runtime repros — per the repo's "verify before fixing — no patch-on-suspicion" rule, **reproduce each with a red fixture first**, then fix, then confirm green.

---

## Priority summary

| # | Bug | Symptom | Fix size | Recommended order |
|---|---|---|---|---|
| 1 | `ProtocolExtensionEmitter` hand-rolled overload key | **CS0111** — consumer can't build the binding | route through canonical key builder | 2nd |
| 2 | `consuming`/`borrowing` missing from public-func regexes | public noncopyable methods degrade to `[Obsolete]` SB0001 raw `CallConvSwift` (ABI risk) | add 2 keywords to ~6 regexes | **1st** (cheapest, already degrading output) |
| 3 | Collection-element ObjC fallback (Foundation+UIKit only) | **silent** member *drop* for `Array<ObjC-class>` from other modules | widen a module set (data) | 3rd |

Suggested sequence: **#2 → #1 → #3** (cheapest/highest-confidence first).

---

## Survivor #1 — `ProtocolExtensionEmitter` hand-rolled overload key → CS0111

**Where:** `src/Swift.Bindings/src/.../ProtocolExtensionEmitter.cs` — `TryInjectMethod`, the key construction at **`:300-314`**, compared against the conforming class's keys built at **`:323`** via `IHandler.GetProjectedCSharpMethodKey(...)`.

**Root cause:** The extension's collision-check key is built by hand:
```csharp
var csMethodName = NameProvider.GetPublicMethodName(extMethod.MethodName, isAsync: false,
    hasReturnValue: hasReturnValue, parameterCount: parameters.Count);   // no propertyNames, no isSelfReturning
projParamTypes.Add(projection?.PublicType ?? paramTypeSpec.ToString());  // raw type
var projectedKey = $"{csMethodName}({string.Join(",", projParamTypes)})";
```
This **skips** `NormalizeParamTypeForOverloadIdentity` and `StripOptionalClassLikeForOverloadIdentity` (and `propertyNames`) that `IHandler.GetProjectedCSharpMethodKey` applies. So the two keys for the *same member* can diverge:

- **`Optional<class>` param (primary):** class-method key strips the trailing `?` for reference types → `Transform(UIImage)`; the extension key keeps it → `Transform(UIImage?)`. Keys don't match → the extension default is injected *alongside* the existing class method → **CS0111 duplicate member** at the consumer's `dotnet build`.
- **propertyNames miss (secondary):** if the method name collides with a property (property-collision rename), the extension key won't apply the rename → potential **CS0102** (property and method share a name).

**Repro shape (Kingfisher `ImageTransformable`):**
```swift
public protocol ImageTransformable { func transform(source: UIImage?) -> UIImage }
public extension ImageTransformable {
    func transform(source: UIImage?) -> UIImage { /* default */ }
}
public class ImageProcessor: ImageTransformable {
    public func transform(source: UIImage?) -> UIImage { /* override */ }
}
```
Generated C# ends up with two `UIImage Transform(UIImage? source)` declarations → CS0111.

**Reachability constraint:** Requires `.swiftinterface`-mode consumption (so the protocol-extension-injection pipeline runs) + a protocol-extension default whose param is `Optional<class>` + a conforming class implementing the same method. Not universal, but Kingfisher-shaped libraries hit it. Libraries with no protocol-extension defaults are unaffected.

**Fix:** Make the extension's collision-check key use the canonical `IHandler.GetProjectedCSharpMethodKey` (or thread the same `NormalizeParamTypeForOverloadIdentity` + `StripOptionalClassLikeForOverloadIdentity` + `propertyNames` so the two keys are computed identically). This is an instance of the audit's **Cluster 2 (emitted-name / dedup-key divergence)** root cause — a hand-rolled key path that should route through the shared helper.

**Tests:**
- BindingTests fixture: protocol + extension default + `Optional<class>` param + conforming class → assert the generated binding **compiles** (no CS0111). Use a maximum-case fixture (see memory `feedback_tdd_for_regression_fixes.md`).
- Unit test: `ProtocolExtensionEmitter` key parity vs `IHandler.GetProjectedCSharpMethodKey` for the `Optional<class>` and property-collision axes.
- This path needs the `.swiftinterface` route to be exercised — confirm the fixture actually triggers extension injection (not the ABI-JSON-only path).

**Narrow sibling (DEFER, log only):** `ProtocolProxyEmitter.Receivers.cs:491` — `EmitDispatchableClosureReturningMethodReceiver` uses `NameProvider.GetMethodName(method.Name, propertyNames: null)`, so a zero-arg `() -> Void`-returning method that PascalCase-collides with a property would emit a receiver calling a non-existent interface method (CS1061). Very narrow combination; not worth fixing unless it surfaces.

---

## Survivor #2 — `consuming`/`borrowing` missing from public-func regexes → SB0001 degradation

**Where:** `src/Swift.Bindings/src/.../SwiftInterfaceAccessParser.cs` — `BroadPublicFuncRegex` at **`:158-160`**:
```csharp
@"(?:^|\s)(?:public|open)\s+(?:(?:final|static|class|mutating|nonmutating|override)\s+)*func\s+(\w+)\s*(?:<[^>]*>\s*)?\("
```
The modifier alternation is `final|static|class|mutating|nonmutating|override` — **`consuming` and `borrowing` are absent.** The same gap must be fixed in lockstep across the sibling regexes: `InternalFuncRegex`, `PublicFuncRegex`, `BareFuncRegex`, `ExtensionFuncRegex`, `AnyFuncRegex` (any regex that recognizes a method-modifier position).

**Trace (why it degrades):**
1. `public consuming func consume() -> Swift.Int32` → `BroadPublicFuncRegex` does **not** match → `"…consume()"` never added to `publicMemberNames`.
2. `IsInternalFromPublicMemberNames(...)` → key absent while other members present → returns `true` → `methodDecl.IsModuleInternal = true`.
3. `MemberValidationPipeline` → `CanEmitMember(isModuleInternal: true)` → `false` → `WrapperDecision.CannotWrap`.
4. Emits a direct `CallConvSwift` P/Invoke with `[Obsolete("No @_cdecl wrapper…", DiagnosticId = "SB0001")]` — an **ABI-risky degraded member** instead of a proper `@_cdecl` wrapper.

**Observable today (already happening):** `BindingTests/output/SwiftBindingsTestLib.cs:~80230` shows `Consume()` emitted with the SB0001 `[Obsolete]` + raw `CallConvSwift` P/Invoke (`$s…UniqueResourceV7consume…`).

**Scope:** 6 methods in BindingTests (`UniqueResource`, `FileHandle`, `TrackedResource`); 48 occurrences in `Swift.swiftmodule`, 13 in `Synchronization.swiftmodule`, 4 in `Swift.System.swiftmodule`; present in `Testing.framework`. Noncopyable types (`consuming`/`borrowing`) are a growing modern-Swift surface.

**NOT bugs (already verified):** `override` IS in the alternation (refuted). Leading `nonisolated` is stripped before the regex (`:2838`, mitigated). The `IsModuleInternal` public-overload false positive is already guarded (`SwiftABIParser.cs:687`). Don't touch these.

**Fix:** Add `consuming` and `borrowing` to the modifier alternation group in all the func regexes listed above. Trivial and low-risk.

**Tests:**
- Unit theory feeding `public consuming func consume()` / `public borrowing func inspect()` → assert recognized as public (`!IsModuleInternal`) and a cdecl wrapper is chosen (not SB0001).
- The 6 existing BindingTests methods become the durable runtime gate: after the fix they should get real `@_cdecl` wrappers. **This is a calling-convention change** (raw `CallConvSwift` → `@_cdecl` wrapper) — run `nuke binding-tests --device` (NativeAOT) in addition to sim, per CLAUDE.md guidance for CC/marshalling changes.
- ⚠️ After editing the generator, **rebuild the Debug binary** (`dotnet build src/Swift.Bindings/src -c Debug`) before regen — `nuke binding-tests`/`validate` run the generator from `bin/Debug/` and won't rebuild a stale dll (memory `feedback_stale_release_binary_masks_regen.md`).

---

## Survivor #3 — Collection-element ObjC fallback gap → silent member drop

**Where:** `src/Swift.Bindings/src/.../TypeProjectionFactory.cs` — `TryProjectObjCElement` at **`:584-604`** gates on `AppleFrameworkRegistry.IsKnownModuleForElements(elemNamed.Module)`, which is **true only for Foundation and UIKit**. Compare to the Optional fallback (`IsOptionalFallbackModule`, ~62 modules). 60 modules are in the Optional fallback set but **not** in the collection-element set.

**Root cause / symptom:** When projecting `Array<T>`, if `Project(elementType)` returns null **and** `TryProjectObjCElement` returns null (element's module not in the 2-module set), the whole `Array<T>` projection returns null → the **enclosing method is silently dropped** (not emitted). Symptom is a **missing member**, not corruption or a crash — but it's silent (no warning).

**Concrete case:** a Swift API returning `[AVFoundation.AVAsset]` where `AVAsset` is an ObjC class from AVFoundation (autoBridge, prefix `AV`, not Foundation/UIKit) **and** has no `*Database.xml` entry → method dropped. Note: if the element type *has* a DB entry, `Project(elementType)` succeeds via `CreateProjectionForTypeRecord` and this path never triggers — so it only bites **unregistered** ObjC class element types from non-Foundation/UIKit modules.

**Fix:** Widen `_knownModulesForElements` to match `_optionalFallbackModules` (data change in `src/Swift.Bindings/src/Data/apple-frameworks.json` + `AppleFrameworkRegistry`). The verifier judged this **safe** because `TryProjectObjCElement` carries the same `HasObjCClassPrefix` guard that prevents value-type misclassification — so widening doesn't open a wrong-ARC hole. Confirm that guard still holds when implementing.

**Tests:** generation + binding-report check that an `Array<T>` from a non-Foundation/UIKit optionalFallback module with an unregistered ObjC class element now emits the method; BindingTests fixture if a clean reproducer is available.

---

## Deferred / minor (capture only — not worth a dedicated pass)

- **`DefaultIndicies` typo** — `Swift5Demangler.cs:740` emits `"DefaultIndicies"` (should be `"DefaultIndices"`). **Zero runtime impact**: the demangled name is discarded; only `IsAsync` / variadic shape are read from the reduction, never the type name. Fix opportunistically (1-char) if touching that file.
- **`FrozenWithMemory` (ClassWithBufferStruct) closure arg** — SwiftUI bridge emission is code-correct (`defer { deinitialize + deallocate }`) but has **no BindingTests runtime coverage**. Test-coverage gap, not a defect. Add a fixture when convenient.
- **Apple value-type vs ObjC-bridged data-completeness** — `valueTypes` (in `apple-frameworks.json`) and `*Database.xml` must stay in sync. E.g. Metal structs like `MTLTextureSwizzleChannels` / `MTLPackedFloat3` are not in `valueTypes`; if one appeared as `Optional<T>` in a bound API it would misclassify as ObjC-bridged (wrong ARC). **No current binding hits it.** Maintenance/backlog; add entries as real bindings need them.
- **Apple SwiftUI bridge 2nd-slice atomicity** (`Sdk.targets` `_CompileAppleFrameworkSecondBridgeSlice` / `_AFB_*` staging) — **not re-verified**. It's an interrupt/partial-failure *recovery* gap (half-written bridge on a failed/interrupted build), not a clean-build correctness defect. Lower urgency; would want the same transactional treatment `WrapperXCFrameworkMerger` already has.
- **Co-gater brace-walker no string/comment state** (`CSharpWrapperCoGater.cs` `FindBlockEnd`/`BuildLineToTypeMap`/`FindEnclosingClassStart`) — real code smell, but **unreachable**: generated C# never carries unbalanced braces inside string/comment literals (it wouldn't compile). Worth a one-line fragility comment so a future emitter author keeps brace-bearing strings balanced within a line.

---

## Working rules for whoever picks this up

- **Verify before fixing.** Reproduce each survivor with a *red* fixture first (no patch-on-suspicion). These are code-trace verdicts, not runtime repros.
- **Maximum-case fixtures**, not minimum-repro — round-1 minimal fixtures have masked the real surface before (memory `feedback_tdd_for_regression_fixes.md`).
- **New work ships with tests** at the right layer: generator/emitter/parser logic → unit tests; ABI/marshalling/CC → BindingTests (the durable gate).
- **Gates:** `nuke test` + `nuke binding-tests` (sim). Add `nuke binding-tests --device` for **#2** (calling-convention change). After any generator edit, rebuild `src/Swift.Bindings/src -c Debug` before regen (stale Debug dll masks the change).
- **If a #1 fixture is a reverse-dispatch (EveryProtocol) protocol**, its protocol must be added to `PreservedProtocols` in `build/Helpers/SwiftSourceStripper.cs` or the harness strips the witness-table getter → `EntryPointNotFoundException` (memory `feedback_new_reverse_dispatch_test_preserved_protocols.md`). Protocol *extension* defaults may not need this — check.
- **Zero-regression:** BindingTests + unit pass counts ≥ baseline before committing.

---

## References

- `grok-phase2-remaining-hardening-candidates.md` §0 — full verdict table (all 8 verified clusters, false-positives included).
- `grok-audit.md` "Verification status" callout — top-level summary + survivor list.
- `STATE-OF-THE-CODEBASE.md` — Cluster 2 (emitted-name / dedup-key divergence) is the root-cause family for #1.
