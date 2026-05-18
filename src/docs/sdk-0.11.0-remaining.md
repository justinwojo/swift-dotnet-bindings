# SDK 0.11.0 — remaining work to ship

Last updated: 2026-05-17

## Status of the open items from `sdk-0.11.0-residual-gaps-round-2.md`

| Item | Status | Where it lives |
|---|---|---|
| S-1 (Stripe cross-module + closures/async) | **FIXED** in Session 1 (`11b4ed83`) | n/a |
| S-2 (ObjC protocol proxy vtable) | **FIXED** in R2-1 (`9b840c1c`) | n/a |
| S-3 (cross-module nested types) | **FIXED** in R2-1 (`91f8bcec`) | n/a |
| A-2 (WeatherKit Statistics/Summary Query) | **ALREADY COMPLETE** — see *Pre-work* below | doc cleanup only |
| A-1 (MusicKit `MusicLibraryRequest<T>`) | **DEFERRED** to KeyPath subsystem | `sdk-keypath-subsystem.md` |
| S-4 (StripeCardScan "heap leak") | **FIXED** in Session 1 — MCB callback ownership corrected; durable runtime pin added | **Session 1** below |
| A-4 (nullable struct setter `AddRef`) | NOT FIXED | **Session 2** below |

Two real bug-fix sessions remain, plus an end-of-wave regression sweep, plus a doc-only pre-work pass. No new architecture in this scope.

## Pre-work — doc cleanup (zero code changes)

Land in a single doc-only commit before Session 1.

### A-2 — mark closed

The WeatherKit query types (`DailyWeatherStatisticsQuery<T>`, `Monthly…`, `Hourly…`, `DailyWeatherSummaryQuery<T>`) declare **zero instance methods** in the `arm64e-apple-ios.swiftinterface`. Only the static factory properties exist, and those already shipped in round-1 at `WeatherKit.cs:8280-8323`. The "instance API still unreachable" framing in `sdk-0.11.0-residual-gaps-round-2.md` was wrong — there is no Swift surface to bind. Mark A-2 closed.

### A-1 — re-scope and defer

`MusicLibraryRequest<T>` has 11 surface members. Three properties (`limit`, `offset`, `includeOnlyDownloadedContent`) plus one plain method (`filter(text:)`) plus one async (`response()`) plus seven `KeyPath`-based filter/sort overloads. Nine of the eleven sit downstream of KeyPath marshalling, which doesn't exist in this generator. The doc's "lift two predicates → surface materializes" framing was based on premises that didn't match the emitter state — see `sdk-0.11.0-session-2-findings.md` for the full forensics. Re-scope A-1 as "blocked on KeyPath subsystem" and point at `sdk-keypath-subsystem.md`.

The type itself already emits as a clean scaffold (constructor + Dispose + per-method inline tombstone comments documenting why each member is skipped). No additional suppression code needed. The tombstones are already self-documenting.

### Doc edits

In `sdk-0.11.0-residual-gaps-round-2.md`:
- Move A-1 out of the "Still-open must-fix items" section into a new "Deferred to KeyPath subsystem" section that links to `sdk-keypath-subsystem.md`.
- Move A-2 into the "Confirmed FIXED" section with a one-sentence note that the doc's instance-surface claim was vestigial (no Swift declarations exist).
- Remove the Session 2 plan; renumber Session 3 → Session 1 (S-4) and Session 4 → Session 2 (A-4) so the doc and this doc agree on numbering, or just delete the old session plan and link here.
- Add a pointer at the top to `sdk-0.11.0-remaining.md` as the operational plan.

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

## Session 2 — A-4 (nullable struct setter value-side `AddRef`)

P5 — GC race during property set on nullable struct setters.

### Diagnosis

`WeatherKit.cs:18960-18981` (`Gust_Set`):

```csharp
private void Gust_Set(IntPtr payload, bool hasValue) {
    unsafe {
        var success = false;
        _payload.DangerousAddRef(ref success);        // receiver bracketed ✓
        try {
            PInvoke_gust_Set_2506D7D4(payload, hasValue, _payload.DangerousGetHandle());
            return;
        }
        finally {
            if (success) _payload.DangerousRelease();
        }
    }
}
```

The `payload` parameter is the value-side `Measurement<T>?` extracted via `value?.Payload.DangerousGetHandle()` in the property body. Between that extraction and the PInvoke return, GC can collect the value's `SafeHandle`. Round-1's A-4 work claimed value-side bracketing but the cited line is byte-for-byte unchanged.

Same shape across nullable `Measurement<T>?` setters in `Wind`, `Pressure`, `Visibility`, `Humidity`, `UVIndex`.

### Investigation required before scoping

1. Locate the property-setter emitter site producing `WeatherKit.cs:18960` (the `private void Gust_Set(IntPtr payload, bool hasValue)` shape). Likely `PropertyHandler` or a nullable-struct-setter variant.
2. Identify why round-1's fix landed on a different emission path. The fix is presumably at the site that emits `Gust_Set` (the wrapper method called from the property setter), not the property-body emitter.
3. Confirm the correct pinning shape: value-side `DangerousAddRef`/`Release` bracketing matching the receiver pattern, OR a `fixed` block over the value's payload buffer. The fix should mirror whichever pattern the rest of the codebase uses for similar setter shapes.

### Ships

- Value-parameter pinning at the emitter site producing `WeatherKit.cs:18960-18981` (`Gust_Set`) and the five siblings (`Wind`, `Pressure`, `Visibility`, `Humidity`, `UVIndex`).
- BindingTests fixture: a nullable `Measurement`-shaped struct property setter exercised under GC pressure. The fixture should be capable of triggering the race in the unfixed version (so it acts as a regression gate going forward) — likely via repeated set under `GC.Collect()` calls in a tight loop.

### Validation gates

1. `nuke test` green.
2. `nuke binding-tests --skip-regen --sim --device` green. Device required: GC behavior differs across Mono and NativeAOT.
3. **Per-item regen-and-grep** — primary close-out gate:
   - `WeatherKit.cs` `Gust_Set` body brackets `payload` through the PInvoke (specific pattern to match determined after investigation).
   - `Wind_Set`, `Pressure_Set`, `Visibility_Set`, `Humidity_Set`, `UVIndex_Set` same.
   - No regression elsewhere in `WeatherKit.cs` or any other consumer-library `*.cs`.
4. Codex + Grok review of the diff: zero High/Critical open.

### Exit

Commit on `main` (or PR per user preference). All four gates green. Same hard stop as Session 1 — if the trace reveals broader emitter work, stop and surface.

## End-of-wave regression sweep

After Session 2 lands.

1. `nuke validate` green; `.validation-baseline.json` (`cs_compile` + `swift_compile`) ≥ baseline.
2. `/regression-validation --version 0.11.0 --apple-version 26.2.3` — full Mono sim + NativeAOT device sweep across both `swift-dotnet-packages` and `internal-binding-testing`. Zero non-pass results per the no-expected-failures policy.
3. Final Codex + Grok review of the full 0.11.0 diff vs `main` at the start of the wave: zero High/Critical open.

## Ship criteria for 0.11.0

- All R2 fixed items (S-1, S-2, S-3) on `main`. **Done.**
- A-2 marked closed in doc. **Pre-work.**
- A-1 re-scoped to KeyPath subsystem in doc. **Pre-work.**
- S-4 finding (mischaracterized; durable BindingTests pin added) on `main`. **Session 1.**
- A-4 fix on `main` with regen-and-grep verified. **Session 2.**
- End-of-wave sweep green.

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

For S-4 and A-4, the per-item gate has five steps. Skipping any of them is how round-1 missed fixes — the unit/BindingTests gates passed on synthetic fixtures and on no other input.

### 1. Capture pre-image of the cited consumer-library file before any code change

```bash
mkdir -p /tmp/round2-preimage
cp /Users/wojo/Dev/swift-dotnet-packages/libraries/Stripe/StripeCardScan/obj/Debug/net10.0-ios/swift-binding/StripeCardScan.Wrapper.swift /tmp/round2-preimage/StripeCardScan.Wrapper.swift.pre  # S-4
cp /Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs /tmp/round2-preimage/WeatherKit.cs.pre                                # A-4
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
#   A-4 → apple-frameworks/WeatherKit/WeatherKit.csproj
dotnet build <csproj-path> -c Debug
```

### 4. Grep the regenerated file for the expected symbol

- **S-4**: `StripeCardScan.Wrapper.swift:340-342` and `:435-437` must NOT contain `__heap_0.deallocate()`. The C# `SwiftSafeHandle.ReleaseHandle` owns the deallocator — a Swift-side defer would be a double-free. The Session 1 finding is the inversion of the original gate.
- **A-4**: `Gust_Set` body in `WeatherKit.cs` brackets the `payload` parameter through the PInvoke with `DangerousAddRef`/`Release` or a `fixed` block. Same for `Wind_Set`, `Pressure_Set`, `Visibility_Set`, `Humidity_Set`, `UVIndex_Set`.

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
