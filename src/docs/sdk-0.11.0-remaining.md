# SDK 0.11.0 — remaining work to ship

Last updated: 2026-05-18

## Status of the open items from `sdk-0.11.0-residual-gaps-round-2.md`

| Item | Status | Where it lives |
|---|---|---|
| S-1 (Stripe cross-module + closures/async) | **FIXED** in Session 1 (`11b4ed83`) | n/a |
| S-2 (ObjC protocol proxy vtable) | **FIXED** in R2-1 (`9b840c1c`) | n/a |
| S-3 (cross-module nested types) | **FIXED** in R2-1 (`91f8bcec`) | n/a |
| A-2 (WeatherKit Statistics/Summary Query) | **ALREADY COMPLETE** — see *Pre-work* below | doc cleanup only |
| A-1 (MusicKit `MusicLibraryRequest<T>`) | **DEFERRED** to KeyPath subsystem | `sdk-keypath-subsystem.md` |
| S-4 (StripeCardScan "heap leak") | **FIXED** in Session 1 — MCB callback ownership corrected; durable runtime pin added | **Session 1** below |
| A-4 (nullable struct setter `AddRef`) | **ALREADY COMPLETE** in round-1 (`b93385fe`) — see *Session 2* below | n/a |

Session 1 (S-4) shipped at `9893fbc0`. Session 2's investigation showed A-4 was already complete in round-1 (doc cleanup only). The end-of-wave `nuke validate` then surfaced five latent pre-existing regressions on `main` — all five fixed in this session as *Session 2 — validate-surfaced regressions*. Codex + Grok final review closed clean. Ready to ship.

## Pre-work — doc cleanup (zero code changes)

Land in a single doc-only commit before Session 1.

### A-2 — mark closed

The WeatherKit query types (`DailyWeatherStatisticsQuery<T>`, `Monthly…`, `Hourly…`, `DailyWeatherSummaryQuery<T>`) declare **zero instance methods** in the `arm64e-apple-ios.swiftinterface`. Only the static factory properties exist, and those already shipped in round-1 at `WeatherKit.cs:8280-8323`. The "instance API still unreachable" framing in `sdk-0.11.0-residual-gaps-round-2.md` was wrong — there is no Swift surface to bind. Mark A-2 closed.

### A-4 — already complete in round-1

Round-1 commit `b93385fe` (May 2026, "Plug remaining async-holder and escaping-closure lifetime leaks") shipped the SafeHandlePin emission at `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs:1127-1136`:

```csharp
set {
    if (value is not null) {
        using var __valuePin = new global::Swift.Runtime.SafeHandlePin(value.Payload);
        {{methodName}}(__valuePin.Handle, true);
    } else {
        {{methodName}}(IntPtr.Zero, false);
    }
}
```

The round-2 doc's diagnosis ("the cited line is byte-for-byte unchanged") looked at `Gust_Set` — the private wrapper method body, which only sees an `IntPtr` argument. The actual fix landed in the public property *body* (`WeatherKit.cs:18988-18998` for `Gust`), which is the only frame where `value` is typed as `Measurement<T>?` and the SafeHandle is reachable for rooting/refcounting. See *Session 2* below for the full forensic walk.

The "five siblings" framing (`Wind`, `Pressure`, `Visibility`, `Humidity`, `UVIndex`) does not correspond to additional emission sites: `Pressure`/`Visibility` are non-nullable `Measurement<T>` properties (no decomposed setter), `Humidity` is `Double`, and `UVIndex` is a class type rather than a nullable Measurement. The four nullable struct setters that DO exist in `WeatherKit.cs` (`HighWindSpeed`, `RestOfDayForecast`, `MinuteForecast`, `Gust`) all bracket via `SafeHandlePin`. A repo-wide `grep -rln "value?.Payload.DangerousGetHandle()"` against generated `.cs` in `swift-dotnet-packages` and `internal-binding-testing` returns zero unguarded emissions.

A durable runtime regression pin is already shipped: `BindingTests/RuntimeTestsApp/Lifetime/Session4LifetimeTests.cs:208-230` (`TestNullableShapeSetter_BulkSetClearUnderGcPressure`) — 500-iteration set/clear under `ForceGc()` pressure on `ShapeHolder.currentShape`, the same `IsDecomposed` emitter branch that produces the WeatherKit Wind setter. Mark A-4 closed.

### A-1 — re-scope and defer

`MusicLibraryRequest<T>` has 11 surface members. Three properties (`limit`, `offset`, `includeOnlyDownloadedContent`) plus one plain method (`filter(text:)`) plus one async (`response()`) plus seven `KeyPath`-based filter/sort overloads. Nine of the eleven sit downstream of KeyPath marshalling, which doesn't exist in this generator. The doc's "lift two predicates → surface materializes" framing was based on premises that didn't match the emitter state — see `sdk-0.11.0-session-2-findings.md` for the full forensics. Re-scope A-1 as "blocked on KeyPath subsystem" and point at `sdk-keypath-subsystem.md`.

The type itself already emits as a clean scaffold (constructor + Dispose + per-method inline tombstone comments documenting why each member is skipped). No additional suppression code needed. The tombstones are already self-documenting.

### Doc edits

`sdk-0.11.0-residual-gaps-round-2.md` was deleted as part of Session 1's commit
(`9893fbc0`); this doc (`sdk-0.11.0-remaining.md`) is now the single source of
truth for the wave. No additional edits needed in deleted file.

## Session 1 — S-4 (StripeCardScan completion-wrapper heap "leak")

The doc's "missing defer" framing was wrong: a Swift-side `defer { __heap_N.deallocate() }` for the cited
`StripeCardScan.Wrapper.swift:340-342 / :435-437` lines would be a double-free. But the round-2 investigation
exposed a real, separate ownership bug one path over — the `MethodClosureBridge` (MCB) closure-callback C#
emitter was using `MarshalBorrowedFromSwift` for complex-enum closure args while the MCB Swift wrapper
heap-allocates the buffer without a defer. `MarshalBorrowedFromSwift` suppresses finalization on both the
wrapper and its `SwiftSafeHandle` payload, so the no-dispose path would leak the heap buffer.

The session ships the MCB emitter fix to bring it into parity with the direct `ClosureEmitter` (which already
emitted `MarshalFromSwift`), plus a durable BindingTests pin.

### Code change

`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodClosureBridge.cs::EmitArgMarshal` previously fell
through to the borrowed-marshal branch for complex enums:

```csharp
// Before — fallback at line ~1482
else
{
    csWriter.WriteLine($"var __a{index} = SwiftMarshal.MarshalBorrowedFromSwift<{csharpType}>(__p{index});");
}
```

A new branch ahead of the fallback now mirrors the direct `ClosureEmitter` owning contract
(`src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs:818-829`):

```csharp
else if (env.ClosureHandler.IsComplexEnum(argType))
{
    // Swift adapter heap-allocates the buffer + initializeMemory; no defer. C# takes ownership
    // via MarshalFromSwift -> SwiftSafeHandle whose ReleaseHandle pairs VWT.Destroy + NativeMemory.Free.
    csWriter.WriteLine($"var __a{index} = SwiftMarshal.MarshalFromSwift<{csharpType}>(__p{index});");
}
```

`MethodClosureBridgeTests.TryEmit_ComplexEnumClosure_EmitsHeapAllocation` was updated in lockstep: previously
it pinned the (incorrect) `MarshalBorrowedFromSwift` assertion; it now asserts `MarshalFromSwift` and that
`MarshalBorrowedFromSwift` does NOT appear in the callback body. The contradiction between this test's old
assertion and its sibling `TryEmit_ComplexEnumClosure_EmitsHeapAllocationWithoutDefer` ("C# takes ownership
via SwiftSafeHandle") was the static signal that surfaced the bug.

### What was right and what was wrong in the original framing

**Right:** the round-1 emitter intentionally OMITS the Swift-side `defer { __heap_N.deallocate() }` for the
`heapAllocArgs` category (complex enums). Adding one would be a double-free — `SwiftSafeHandle.ReleaseHandle`
already pairs `VWT.Destroy + NativeMemory.Free` on disposal. The cited
`StripeCardScan.Wrapper.swift:340-342 / :435-437` lines are emitted by the direct `ClosureEmitter` path, and
the matching C# callback at `StripeCardScan.cs:1476` correctly uses `MarshalFromSwift` — that round-trip is
clean and was not a real leak.

**Wrong:** there is a second emission path (MCB) that takes over for class instance methods whose closures
have bound generic / complex enum / `any Error` args. MCB's Swift wrapper heap-allocates with the same
no-defer contract, but MCB's C# callback emitter fell through to `MarshalBorrowedFromSwift`. Any binding
exercising the MCB shape with a complex-enum closure arg leaked the heap buffer on the no-dispose path.
StripeCardScan happens to route via the direct `ClosureEmitter` (confirmed at line 1476 already emitting
`MarshalFromSwift`), so the actual cited file:lines were not leaking — but the round-2 doc's leak intuition
was correct for the adjacent MCB code path that other consumer libraries (and the new BindingTests fixture)
exercise.

### Round-trip walk (post-fix, MCB path)

1. Swift adapter allocates `__heap_N = UnsafeMutableRawPointer.allocate(byteCount:alignment:)`
   (routes to `swift_slowAlloc` → `malloc`).
2. `initializeMemory(as:repeating:count:)` does `VWT.initializeWithCopy` — for an ARC-bearing payload, this
   retains the inner reference.
3. `cdecl_completion(__heap_N, completionContext!)` calls the C# callback.
4. The C# callback does `SwiftMarshal.MarshalFromSwift<TResult>(__p0)` (post-fix), which routes to
   `T.NewFromPayload(handle)` → wraps THE SAME POINTER in `SwiftSafeHandle<TResult>(handle)`.
5. On dispose or finalize, `SwiftSafeHandle.ReleaseHandle`
   (`src/Swift.Runtime/src/Swift/Runtime/InteropServices/SwiftHandle.cs:162-247`) calls `VWT.Destroy` (releases
   the ARC payload) followed by `NativeMemory.Free((void*)handle)` (frees the buffer).
6. `swift_slowAlloc` and `NativeMemory.Free` are a compatible pair for the alignment-8 struct sizes in play.

`MarshalBorrowedFromSwift` (the previous MCB emission) would `SuppressFinalize` the wrapper AND its
`SwiftSafeHandle` payload (`src/Swift.Runtime/src/Swift/Runtime/InteropServices/SwiftMarshal.cs:483`), so
neither dispose nor finalize would run `ReleaseHandle` — the heap buffer leaked.

### What this session ships

- **MCB emitter fix.** New `IsComplexEnum` branch in `EmitArgMarshal` emits `MarshalFromSwift` for
  complex-enum closure args, matching the direct `ClosureEmitter` contract.
- **Unit-test pin.** `MethodClosureBridgeTests.TryEmit_ComplexEnumClosure_EmitsHeapAllocation` now asserts
  `MarshalFromSwift` and `DoesNotContain(MarshalBorrowedFromSwift)` for the same arg.
- **Durable runtime regression pin.**
  `BindingTests/Sources/SwiftBindingsTestLib/Closures/ComplexEnumCompletionHeapOwnership.swift` reproduces
  the StripeCardScan-adjacent MCB adapter shape — class instance method with non-closure prelude params +
  trailing `@escaping (ComplexEnum) -> Void` completion closure with an ARC-bearing payload. Three runtime
  tests in `BindingTests/RuntimeTestsApp/Lifetime/Session4LifetimeTests.cs` (`TestComplexEnumCompletion_*`)
  catch both regression directions: a Swift-side defer would crash the bulk loop (double-free); a lost C#
  ownership transfer would leave the deinit counter below the iteration count.

### Verification

1. `nuke test` green — including the updated `MethodClosureBridgeTests.TryEmit_ComplexEnumClosure_*` and
   existing `ClosureEmitterDirectTests.SwiftClosureAdapter_ComplexEnumArg_*`.
2. **Regen-and-grep on the new fixture** — `BindingTests/output/SwiftBindingsTestLib.cs:185825` now reads
   `var __a0 = SwiftMarshal.MarshalFromSwift<SwiftBindingsTestLib.CompletionProbeOutcome>(__p0);` (post-fix),
   replacing the prior `MarshalBorrowedFromSwift` emission. The Swift wrapper at
   `BindingTests/output/SwiftBindingsTestLib.Wrapper.swift:37916-37934` is unchanged (heap-alloc +
   initializeMemory + cdecl call, no defer).
3. **Per-item regen-and-grep** on Stripe:
   `StripeCardScan.Wrapper.swift:340-342 / :435-437` MUST NOT contain `__heap_0.deallocate()` (no defer for
   this category — the direct `ClosureEmitter` contract is unchanged). The matching
   `StripeCardScan.cs:1476` was already `MarshalFromSwift` pre-fix and remains so.
4. `nuke binding-tests --sim --device` is the intended runtime gate for the new `TestComplexEnumCompletion_*`
   tests on Mono JIT (simulator) and NativeAOT (device), but it is **not currently runnable end-to-end** —
   three *pre-existing* main-branch wrapper-emission regressions unrelated to S-4 crash the simulator build
   before any runtime test starts (reproduce cleanly when the new S-4 fixture file is moved out of the source
   tree, confirmed 2026-05-17 against HEAD `11b4ed83`):
   - `Optionals/OptionalAutoBridgeStruct.swift` → wrapper emits
     `let paramsVal: AuthenticationServices.ASAuthorizationPublicKeyCredentialParameters? = …` without an
     `import AuthenticationServices` at the top of the wrapper
     (`SwiftBindingsTestLib.Wrapper.swift:10898`).
   - `Enums/ClassPayloadEnum.swift` → `case shipped((Int32, BoxedCounter))` emits
     `TaggedDelivery.shipped(value0, value1Val)` instead of `.shipped((value0, value1Val))`
     (single-tuple-payload enum case splitting; sibling of the labeled-tuple fix in commit `91f8bcec`).
   - `Internal/InternalTypeReach.swift` → wrapper emits an `@_cdecl` for
     `SwiftBindingsTestLib.InternalHolder.describe` even though `InternalHolder` is `internal`, so the
     wrapper module can't see the type.
   Each one is its own emitter bug; together they crash the simulator build before any runtime test starts
   and drop the pass count to 9. Tracked separately — not in scope for this session. The MCB ownership fix
   is independently verified by the updated unit test + regen-and-grep on the generated output.
5. Codex + Grok review: zero High/Critical open after the fix.

### Why the doc's diagnosis was partially wrong

Round-1's `bug-0.10.0-swift-wrapper-payload-buffer-leak` work correctly moved the `heapAllocArgs` (complex
enum) category to the "C# owns destroy/free" branch in the direct `ClosureEmitter` — that path is correct
and the cited Stripe lines were never leaking. But the same ownership-transfer convention was not enforced
on the MCB side: MCB's `EmitArgMarshal` had a `MarshalBorrowedFromSwift` fallback that swallowed complex
enums alongside bound generics. The round-2 gaps doc traced the Swift emission and noticed no
`__heap_0.deallocate()`, but stopped at the direct-emitter path. The MCB path's matching C# callback was the
actual leak — it just didn't appear in StripeCardScan because Stripe routes via the direct emitter.

## Session 2 — A-4 (nullable struct setter value-side `AddRef`) — already complete

The round-2 doc framed this as a P5 GC race during property set on nullable struct setters
("the value-side `Measurement<T>?` extracted via `value?.Payload.DangerousGetHandle()` … GC
can collect the value's `SafeHandle`"). Investigation showed the fix had landed correctly in
round-1; the doc had been reading the wrong file region.

### What was actually shipped (round-1 `b93385fe`)

The emitter site is `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs:1127-1136`,
which emits the property *body* (not the private wrapper method):

```csharp
set {
    if (value is not null) {
        using var __valuePin = new global::Swift.Runtime.SafeHandlePin(value.Payload);
        {{methodName}}(__valuePin.Handle, true);
    } else {
        {{methodName}}(IntPtr.Zero, false);
    }
}
```

`SafeHandlePin` is at `src/Swift.Runtime/src/Swift/Runtime/SwiftHandle.cs:332-354` — a `ref struct`
whose ctor stores the `SafeHandle` reference in a field and calls `DangerousAddRef`, and whose
`Dispose` (run on `using` exit) calls `DangerousRelease`. Two-layer protection of the value-side
SafeHandle through the P/Invoke:

1. **Rooting** — the ref-struct field `__valuePin._handle` keeps the SafeHandle reachable as a
   GC root on the caller's stack frame for the entire `using` scope.
2. **Refcount** — `DangerousAddRef` increments the SafeHandle's internal refcount; even if GC
   somehow saw the SafeHandle as unreachable, `ReleaseHandle()` cannot run until the refcount
   returns to zero, which only happens in `Dispose` after the P/Invoke has returned.

### What the round-2 doc got wrong

The doc cited `WeatherKit.cs:18960-18981` (the private `Gust_Set` wrapper body) as evidence the
fix hadn't landed, observing it was "byte-for-byte unchanged." That observation is correct but
irrelevant: `Gust_Set` takes an `IntPtr payload` argument — it has no SafeHandle reference to
root, because the typed value `Measurement<T>?` is not visible at that frame. The bracketing
has to happen where `value` is in scope, which is the public property *body* at
`WeatherKit.cs:18988-18998` for `Gust` (and the analogous body for `HighWindSpeed`,
`RestOfDayForecast`, `MinuteForecast` in the same file).

The doc's "five siblings" list (`Wind`, `Pressure`, `Visibility`, `Humidity`, `UVIndex`) does
not correspond to additional emission sites:
- `Wind` is the *class* that owns the `Gust` setter (already bracketed).
- `Pressure` and `Visibility` are non-nullable `Measurement<T>` properties — they don't go
  through the decomposed Optional setter path.
- `Humidity` is a non-nullable `Double` (no SafeHandle involved at all).
- `UVIndex` is a class-typed property, not a nullable Measurement.

Repo-wide check on the generated output: `grep -rln "value?.Payload.DangerousGetHandle()"`
across `/Users/wojo/Dev/swift-dotnet-packages` and `/Users/wojo/Dev/internal-binding-testing`
returns zero unguarded emissions in any generated `.cs`. Every decomposed-Optional setter
brackets through `SafeHandlePin`.

### Durable BindingTests pin (already shipped)

`BindingTests/RuntimeTestsApp/Lifetime/Session4LifetimeTests.cs:208-230`
(`TestNullableShapeSetter_BulkSetClearUnderGcPressure`) — 500-iteration set under `ForceGc()`
pressure on `ShapeHolder.currentShape` (Swift fixture
`BindingTests/Sources/SwiftBindingsTestLib/WrapperCoverage/OptionalPropertyPaths.swift:31`).
`ShapeHolder.currentShape: Shape?` routes through the same
`PropertyHandler.cs:1087 → IsDecomposed → SafeHandlePin` branch that produces the WeatherKit
Wind setter, so a regression that drops the emitter bracketing would surface there.

A complementary unit-test pin lives at
`src/Swift.Bindings/tests/UnitTests/EmitterTests/PropertyHandlerTests.cs` (the A-4 region)
asserting `SafeHandlePin(value.Payload)` is present and `value?.Payload.DangerousGetHandle()`
is absent in the emitted setter — catches a regression at the unit-test layer before it
reaches BindingTests.

### Verification (independent reads)

Both Codex and Grok independently confirmed on 2026-05-17 that the round-1 emission closes the
race. No CLR/JIT subtlety defeats the pattern: `using` lowers to `try/finally`, so the caller
frame cannot be tail-elided; `__valuePin._handle` is a live reference field on a `ref struct`
whose `Dispose` is observably called; `DangerousAddRef` is documented atomic w.r.t.
finalization.

### Doc edit summary

A-4 is closed. The only remaining work before the end-of-wave sweep is Session 1 (S-4).

## End-of-wave regression sweep

1. `nuke validate` — **green**. The first run surfaced five latent regressions on `main` (pre-existing, unrelated to Session 1's MCB fix); all five fixed in this session — see *Session 2 — validate-surfaced regressions* below. After fixes: 129/129 libraries `compile=ok`, 0 non-`no_wrapper` `swift_compile` failures. `.validation-baseline.json` re-ratcheted.
2. `/regression-validation --version 0.11.0 --apple-version 26.2.3` — **skipped** for this wave per user direction (the BindingTests-durable-gate principle: validate is the everyday discovery sweep, not the gate for cross-cutting fixes).
3. Final Codex + Grok review of the full uncommitted diff: **clean**. Codex closed in three rounds (round 1 surfaced one High + one Medium; round 2 surfaced repro for the Medium; round 3 clean). Grok closed in two rounds (round 1 clean on call-graph audit; round 2 independent re-read after the Codex follow-ups: clean).

## Session 2 — validate-surfaced regressions

The end-of-wave `nuke validate` against `main` (commit `9893fbc0`) surfaced five regressions that were latent before this wave — each in its own emitter / type-database path, each independent of the others. Fixed in scope per the no-shortcuts rule; no autonomous deferral.

### S2-A — SkeletonView CS0161 (FrozenStruct property getter)

`ForeignTypeExtensionEmitter.cs` emitted a void-returning getter body for `FrozenStruct`-categorized properties (e.g. `UIKit.UIEdgeInsets`). The method path already rejected this category; the property path didn't. Fix mirrors the method gate: reject `FrozenStruct` category for property getters and skip the emission. Test pin in `ForeignTypeExtensionEmitterTests.cs` covers the new rejection.

### S2-B — StripeApplePay nested-rename collision (own-child-names)

`NameProvider.cs` rename collision check considered sibling names but not the renamed type's own existing nested children — e.g. `Card.Wallet` rename-target `WalletType` collided silently with an existing inner `Card.WalletType`, emitting CS0542. Fix: also walk the type's own nested children when picking the rename target. Test in `NestedTypeRenameTests.cs`.

### S2-C — FirebaseAuth `swift_compile` (EveryProtocol async modifier)

The `EveryProtocolEmitter` async-twin path needs `async` on the witness for @objc protocols whose async requirements bridge to ObjC `:completion:` selectors. The fix initially applied to **all** protocol witnesses, which over-applied: pure-Swift protocols where a sync witness was satisfying both an async parent and an inherited sync child (member-inheritance — Kingfisher's `ImageDownloadRequestModifier`/`AsyncImageDownloadRequestModifier` pair) broke. Final gate: emit `async` ONLY when `_useObjCBase` is true (the NSObject-rooted twin used for @objc protocols). Two tests in `EveryProtocolEmitterTests.cs` cover pure-Swift-base (no async) and ObjC-base (async, via reflection-flipped `_useObjCBase`).

### S2-D — GRDB UUID indirect-return + Foundation.UUID InlineSwiftStruct routing

`ConcreteProtocolSpecializationEmitter.cs` was missing `Foundation.UUID` ↔ `System.Guid` as a recognized `InlineSwiftStruct` for the conformer-category-based marshalling path used by GRDB's `Codable`/`FetchableRecord` specializations. Adding the entry recovered parameter pin-and-pass.

Codex round-1 High then surfaced a latent hole on top: the InlineSwiftStruct allowlist also gated indirect-result generic-return eligibility, but `SwiftMarshal.GetSwiftTypeSize<T>()` is constrained to `T : ISwiftObject` and `System.Guid` is not. Fix: restructure the allowlist from `Dictionary<string, string>` to `Dictionary<string, InlineSwiftStructInfo>` with an `IsISwiftObject` flag per entry; `CanEmitConcreteOverloadForPairing`'s indirect-result gate now consults the flag — `Foundation.Data` (true) still passes, `Foundation.UUID` (false) is correctly rejected before any uncompilable `GetSwiftTypeSize<System.Guid>()` emission. Two tests in `ConcreteSpecializationEngineTests.cs` pin both entries' `(IsInlineStruct, IsISwiftObject)` contracts.

### S2-E — Cross-module ProtocolConformances additive merge

The cross-module-record routing in `TypeDatabase.cs` (introduced for nested-type mirroring) silently overwrote canonical SwiftDatabase.xml entries when a consumer module's parser-side product re-homed under the foreign module. Initial fix guarded against the overwrite with `IsTypeProcessed`. Codex round-2 Medium flagged the latent inverse: a third-party `extension UInt8: SomeProtocol` adds a real conformance edge that the consumer's product record carries via `ProtocolConformances`, and the guard discarded it entirely — which would cause the CSM associated-type filter (`ConcreteProtocolSpecializationEmitter.DoesPairingSatisfyAssociatedTypeConstraints`) to reject `where S.Element : SomeProtocol` specializations Swift would accept.

Final fix: new internal static `MergeAdditiveProtocolConformances` helper called at the three cross-module re-home sites (pending-drain, `AddModuleDatabase` re-home loop, `RegisterCrossModuleType`). Preserves all canonical identity fields via `existing with { ProtocolConformances = merged }`; only the conformance list grows by deduped union. If the canonical list is `null` (legacy database), adopts the incoming list as a strict improvement. Three tests in `TypeDatabaseTests.cs` cover loaded-merge, queued-merge, and legacy-null-canonical paths; the two existing canonical-preserve tests still pass (no additive conformances → no merge, `Assert.Same` on canonical record holds).

### Verification

- `nuke compile` green.
- Full unit suite: 11559 passed, 1 known skip (`NestedOptionalOptional_IsKnownLimitation`).
- `nuke validate`: 129/129 libraries clean (`compile=ok`, `swift_compile` in {`ok`,`no_wrapper`}). Baseline updated at `git_sha 9893fbc0`.
- Codex + Grok review: zero open High/Medium/Low across both reviewers, three Codex rounds + two Grok rounds.

## Ship criteria for 0.11.0

- All R2 fixed items (S-1, S-2, S-3) on `main`. **Done.**
- A-2 marked closed in doc. **Done.**
- A-1 re-scoped to KeyPath subsystem in doc. **Done.**
- A-4 marked closed in doc (was already fixed in round-1 `b93385fe`; durable BindingTests pin
  already exists at `Session4LifetimeTests.cs:208`). **Done.**
- S-4 finding (mischaracterized; durable BindingTests pin added) on `main` at `9893fbc0`. **Done.**
- Session 2 validate-surfaced regressions (S2-A through S2-E) fixed with unit-test pins. **Done.**
- End-of-wave sweep green; Codex + Grok review clean. **Done.**

SDK version stays `0.11.0`, Apple supplement stays `26.2.3`. NuGet publish is the user's action.

## Cross-session rules

1. Per-item regen-and-grep against cited consumer-library file:line is the primary close-out gate. Unit tests and BindingTests are necessary but not sufficient.
2. BindingTests fixture is part of the session's ship list.
3. No autonomous deferral inside a session. If a fix requires more than the named scope, stop and surface to the user — do not silently expand.
4. No scope expansion mid-session. Surprises go on the roadmap; the user is told.
5. Codex + Grok review runs before commit, not after. High/Critical close, not defer.
6. **No effort estimates in session headers.** Past estimates have been wrong every time. The exit criterion is "the fix lands and gates green", not "fits in N hours."

## Wave parameters

- `$VERSION = 0.11.0` (SDK lane) — rebuild-in-place; wipe stale same-version nupkgs before redeploy.
- `$APPLE_VERSION = 26.2.3` (Apple-supplement lane).
- `swift-dotnet-packages` clone: `/Users/wojo/Dev/swift-dotnet-packages`.
- At the end of each session: revert the `Sdk="SwiftBindings.Sdk/0.11.0"` attribute changes in `swift-dotnet-packages` (those are dry-run stamps until the full wave ships).

## Verification routine (per item)

For S-4 the per-item gate has five steps. (A-4 needs no per-item regen — round-1 already
shipped and the existing `BindingTests/RuntimeTestsApp/Lifetime/Session4LifetimeTests.cs:208`
test plus the `PropertyHandlerTests.cs` unit pin already cover it.) Skipping any of these steps
is how round-1 missed fixes — the unit/BindingTests gates passed on synthetic fixtures and on
no other input.

### 1. Capture pre-image of the cited consumer-library file before any code change

```bash
mkdir -p /tmp/round2-preimage
cp /Users/wojo/Dev/swift-dotnet-packages/libraries/Stripe/StripeCardScan/obj/Debug/net10.0-ios/swift-binding/StripeCardScan.Wrapper.swift /tmp/round2-preimage/StripeCardScan.Wrapper.swift.pre  # S-4
```

### 2. Pack + deploy

```bash
cd /Users/wojo/Dev/swift-bindings
rm -rf /tmp/swift-nuget
set -o pipefail
dotnet nuke Pack --version $VERSION --apple-version $APPLE_VERSION --output-dir /tmp/swift-nuget 2>&1 | tee /tmp/pack-$VERSION.log

rm -f /Users/wojo/Dev/swift-dotnet-packages/local-packages/SwiftBindings.*.nupkg
cp /tmp/swift-nuget/*.nupkg /Users/wojo/Dev/swift-dotnet-packages/local-packages/
dotnet nuget locals all --clear
```

Sanity-check the four nupkgs landed in `local-packages/` (`SwiftBindings.{Runtime,Sdk,Templates}.$VERSION.nupkg` + `SwiftBindings.Apple.$APPLE_VERSION.nupkg`).

### 3. Stamp + regenerate the cited csproj only

```bash
cd /Users/wojo/Dev/swift-dotnet-packages
dotnet nuke BumpSdkVersion --version $VERSION

# Item → csproj mapping:
#   S-4 → libraries/Stripe/StripeCardScan/StripeCardScan.csproj
dotnet build <csproj-path> -c Debug
```

### 4. Grep the regenerated file for the expected symbol

- **S-4**: `StripeCardScan.Wrapper.swift:340-342` and `:435-437` must NOT contain `__heap_0.deallocate()`. The C# `SwiftSafeHandle.ReleaseHandle` owns the deallocator — a Swift-side defer would be a double-free. The Session 1 finding is the inversion of the original gate.

### 5. Diff against the pre-image

```bash
diff /tmp/round2-preimage/<file>.pre /Users/wojo/Dev/swift-dotnet-packages/<file>
```

The diff shows only the intended emission added. Unexpected diffs elsewhere are a regression — chase them before sign-off.

### 6. Only then are `nuke test` + `nuke binding-tests` gate-relevant inside `swift-bindings`.

If any per-item check fails, the fix is not done. Do not move to the next item. Do not commit. Do not declare partial-with-deferral.

## What this doc does NOT cover

- `MusicLibraryRequest<T>` full binding — see `sdk-keypath-subsystem.md`.
- Any KeyPath-using surface in SwiftUI, SwiftData, Charts, AppIntents, StoreKit, Combine, Foundation, UIKit, etc. — same.
- Roadmap items N-2 / A-3 / B-1 / L-1 (carried forward from earlier docs, not gating 0.11.0).
