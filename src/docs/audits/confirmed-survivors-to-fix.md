# Confirmed Audit Survivors — Actionable Fix Backlog

**Date captured:** 2026-06-07
**Status:** Survivors #1, #2, #3 FIXED. One residual item remains: **#2b** (noncopyable `consuming`/`borrowing` *self* wrapper), split out from #2 after tracing the original "durable gate" to a different root cause. See the priority table.
**Provenance:** Code-trace verification (Claude, read-only) of the High-impact "still latent" items in `grok-phase2-remaining-hardening-candidates.md` (see its §0 Verification Log for the full verdict table) and `grok-audit.md` (see its top "Verification status" callout).

---

## How to pick this up cold

The two Grok docs (`grok-audit.md`, `grok-phase2-remaining-hardening-candidates.md`) synthesized the original 14-track audit's *deferred* pool and flagged ~dozens of "still latent" candidates. A follow-up verification pass re-checked the 8 highest-impact High clusters against current source. **Most were false-positive / already-mitigated / latent-but-unreachable** (Apple short-prefixes, enum width-truncation, co-gater brace-walker, parser comment/string blindness, SwiftUI reserved-name collisions, SwiftUI ObjC-closure UAF/leak, demangler Ya/Yb/YK) — those are annotated in the Grok docs and should **not** be re-chased without new evidence.

**This doc is the residue: the 3 reachable bugs worth fixing, plus minor deferred items.** None is a launch-blocking process crash; two (#1, #2) are correctness/ABI issues on common real-binding shapes.

> ⚠️ **Line numbers below are as-of 2026-06-07 and will drift.** Grep/re-confirm before editing. These are *code-trace* verdicts, **not** compile/runtime repros — per the repo's "verify before fixing — no patch-on-suspicion" rule, **reproduce each with a red fixture first**, then fix, then confirm green.

---

## Priority summary

| # | Bug | Symptom | Fix size | Status |
|---|---|---|---|---|
| 1 | `ProtocolExtensionEmitter` hand-rolled overload key | **CS0111** — consumer can't build the binding | route through canonical key builder | **FIXED** (commit `313b2a2d`) |
| 2 | `consuming`/`borrowing` missing from public-func regexes | non-`public`-keyword noncopyable methods (`@inlinable internal`, bare protocol reqs, protocol-extension defaults) mis-classified module-internal → SB0001 raw `CallConvSwift` | add 2 keywords to 8 regexes (2 files) | **FIXED** (parser + unit tests) |
| 2b | Noncopyable `consuming`/`borrowing` **self** wrapper not emitted | the 6 BindingTests noncopyable instance methods degrade to SB0001 `CallConvSwift` (works today, but ABI-risky) | parser + model + emitter feature | **OPEN** (see below) |
| 3 | Collection-element ObjC fallback (Foundation+UIKit only) | **silent** member *drop* for `Array<ObjC-class>` from other modules | widen a module set (data) | **FIXED** (commit `76608c2a`) |

The original three are done; #2 split into a parser fix (#2, fixed) and a separately-rooted emitter feature (#2b, open) once the durable-gate methods were traced to a different cause — see the §"Correction" note under Survivor #2.

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

## Survivor #2 — `consuming`/`borrowing` missing from public-func regexes → SB0001 degradation  ✅ FIXED

**Where:** `src/Swift.Bindings/src/Parser/SwiftInterfaceAccessParser.cs` — `BroadPublicFuncRegex` plus the sibling func regexes (`InternalFuncRegex`, `PublicFuncRegex`, `BareFuncRegex`, `ExtensionFuncRegex`, `AnyFuncRegex`), **and** `SwiftInterfaceContextTracker.cs` (`PublicFuncRegex`, `ProtocolFuncRegex` — found by the cross-file grep, not in the original doc). The modifier alternation was `final|static|class|mutating|nonmutating|override` — **`consuming` and `borrowing` were absent** from the modifier slot in all 8 regexes.

**Fix applied:** added `consuming`/`borrowing` to the modifier alternation in all 8 regexes. Covered by a `[Theory]` suite in `SwiftInterfaceAccessParserTests.cs` (`GetInternalMembers_*OwnershipModifier*`) exercising public-struct, open-static, bare-protocol-requirement, and extension shapes for both modifiers — verified red before the fix, green after. `nuke test` and the `nuke binding-tests --compile-only` gate pass.

**Trace (why it degraded):** `public consuming func consume()` failed `BroadPublicFuncRegex` → never added to `publicMemberNames` → `IsInternalFromPublicMemberNames(...)` returned `true` → `IsModuleInternal = true` → `CanEmitMember(isModuleInternal: true)` = `false` → degraded SB0001 `CallConvSwift`.

**Real reach of this fix:** the negative-space (swiftinterface-text) internal detector only flips members that **lack an explicit access keyword** — `@inlinable internal consuming func`, bare protocol requirements, and protocol-extension defaults. Methods written `public consuming func …` carry `declAttributes: ["…","AccessControl"]` in the ABI JSON and are classified public by the ABI-JSON path regardless of the regex, so the regex fix does **not** change their output. Scope of the *latent* bug it does fix: 48 occurrences in `Swift.swiftmodule`, 13 in `Synchronization.swiftmodule`, 4 in `Swift.System.swiftmodule`, plus `Testing.framework` — wherever an ownership-modified func appears without a leading `public`/`open` keyword.

**NOT bugs (already verified):** `override` IS in the alternation. Leading `nonisolated` is stripped before the regex (mitigated). The `IsModuleInternal` public-overload false positive is already guarded (`SwiftABIParser.cs`). Don't touch these.

### Correction — the doc's original "durable gate" was mis-attributed

The original write-up claimed the 6 BindingTests methods (`UniqueResource.consume/inspect`, `FileHandle.close/getDescriptor/isOpen`, `TrackedResource.peek`) were the runtime gate for this regex and "after the fix should get real `@_cdecl` wrappers." **That is false.** Those 6 are all written `public consuming/borrowing func`, so (per the paragraph above) the regex never gated them — their SB0001 has a *separate* root cause, captured as **Survivor #2b** below. The `~80230` `Consume()` SB0001 observation in the original doc is real but is a #2b symptom, not a #2 one.

---

## Survivor #2b — noncopyable `consuming`/`borrowing` **self** instance methods don't get a `@_cdecl` wrapper  (OPEN)

**Symptom:** the 6 noncopyable instance methods above emit an `[Obsolete(… SB0001)]` + raw `CallConvSwift` P/Invoke to the mangled Swift symbol instead of a `CallConvCdecl` `SBW_…` wrapper. They **work today** at runtime via `CallConvSwift` (their `OwnershipTests`/`NegativePathTests` runtime cases are not skipped) — so this is ABI *hardening*, not a live crash. But it carries real double-free risk if fixed naively (these are `~Copyable` types with `deinit`).

**Verified root cause (code-traced 2026-06-07):**
1. **Ownership is dropped at parse time.** `SwiftABIParser.cs:2001` stores `IsMutating = node.funcSelfKind == "Mutating"` only. `funcSelfKind: "Consuming"` / `"Borrowing"` are discarded — `MethodDecl` has no `IsConsuming`/`IsBorrowing`. Confirmed in the ABI JSON: the `consume` node carries `"funcSelfKind": "Consuming"`.
2. **Self-reconstruction copies a `~Copyable` value.** `MethodWrapperEmitter.cs:500-501`: for a noncopyable parent, `selfRef = self_.assumingMemoryBound(to: T.self).pointee`. `.pointee` is a borrow; calling a **`consuming`** method on it requires *ownership*, so Swift rejects the wrapper (illegal consume of a borrow). `ShouldEmitWrapper` returns true and a wrapper IS emitted, but it fails Swift compilation and is removed by the build's give-up/strip loop (compile log: "Compilation attempt 1 failed — stripping… N total stripped"); the C# then degrades to `CallConvSwift`. (`borrowing` self may compile via `.pointee`, but rides the same stale-strip path.)

**Fix shape (when picked up):**
- Add `IsConsuming`/`IsBorrowing` to `MethodDecl`; parse them from `funcSelfKind` in `SwiftABIParser.cs` (and the accessor path at `:2470` if relevant). Keep the two readers of any new flag in sync.
- In `MethodWrapperEmitter` self-reconstruction: **consuming self** → take ownership out of the buffer (`…assumingMemoryBound(to:).move()`), then **mark the C# `SwiftSafeHandle` consumed** so a later `Dispose()` is a no-op — exactly the handle-consumed contract the `TrackedResource` *parameter* path already implements (see `Noncopyable.swift` P0-06 comment). **borrowing self** → a true borrow through the pointer with no copy.
- Verify the exact Swift form via SIL (memory `feedback_verify_swift_abi_sil.md`) and an independent CLI consult before committing — ownership errors here are double-frees.
- **Calling-convention change** → gate on `nuke binding-tests --device` (NativeAOT) in addition to sim, per CLAUDE.md.
- ⚠️ Rebuild the Debug generator binary (`dotnet build src/Swift.Bindings/src -c Debug`) before regen — the gates run from `bin/Debug/` and won't rebuild a stale dll (memory `feedback_stale_release_binary_masks_regen.md`).

**Durable gate:** the existing `OwnershipTests`/`NegativePathTests` cases that call `GetInspect()`/`Consume()` on `UniqueResource`/`FileHandle`/`TrackedResource` — assert they round-trip AND that the generated C# uses `CallConvCdecl` (no SB0001) after the fix. Add a `deinit`-runs-exactly-once probe for `consuming` self (mirror the `TrackedResource` parameter probe) to catch the double-free.

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
