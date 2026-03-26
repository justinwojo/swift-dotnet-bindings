# Remaining Hardening Work

**Created**: March 26, 2026
**Source**: Items not delivered during the hardening sessions (`src/docs/Completed/hardening-sessions.md`).

Two sessions. Independent — either order works. Session 1 is lower risk (DX/tooling, no core emit pipeline changes). Session 2 touches marshalling and may cause regressions.

---

## Session 1: DX & Tooling Polish ✅ `4323f7be`

**Scope**: 5 items — MSBuild warnings, bulk retain/release, pack-all.sh, static protocol constructors, bridge CLI
**Risk**: Low — no core generator/emit pipeline changes
**Validation**: `run-tests.sh` after each item. `validate-libraries.sh` at end only if Item 4 changes generated output.

### Item 1: Binding report as MSBuild warnings

**What**: Surface skip counts from `binding-report.json` as MSBuild warnings so binding authors see gaps in build output without consulting the JSON file.

**Where to start**:
- `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` — existing SWIFTBIND warning codes at lines 16, 38, 43, 283, 745 etc. Follow the same `<Warning Code="SWIFTBINDxxx" Text="..."/>` pattern.
- `src/Swift.Bindings/src/Reporting/BindingReport.cs` — report structure: `TotalTypes`, `EmittedTypes`, `SkippedTypes`, `TotalMembers`, `EmittedMembers`, `SkippedMembers`, `SkippedMembersByKind` (Dictionary<BindingItemKind, int>).
- `src/Swift.Bindings/src/Reporting/ReportEmitter.cs` — writes `binding-report.json` via `JsonConvert.SerializeObject()` at line 27.

**Implementation**: Add a target in `Sdk.targets` (after `_SwiftBindingsGenerate`) that reads `binding-report.json` from the output directory, parses the skip counts, and emits one `<Warning>` per skip category with a count. Use a new code range (e.g., SWIFTBIND060-069).

**Test**: Run the generator on a test library via `dotnet build` with the SDK and verify warnings appear in build output.

### Item 2: Bulk retain/release helpers

**What**: Add batch retain/release methods to `Arc` for performance with large Swift collections.

**Where to start**:
- `src/Swift.Runtime/src/Swift/Runtime/Arc.cs` — existing `swift_retain` (line 23), `swift_release` (line 68), `Retain()` (line 33), `Release()` (line 78). Note: `swift_retain` has `[SuppressGCTransition]`, `swift_release` does NOT (deinit can trigger managed callbacks).

**Implementation**: Add `RetainMultiple(ReadOnlySpan<IntPtr> pointers)` and `ReleaseMultiple(ReadOnlySpan<IntPtr> pointers)` that loop over the span calling the individual P/Invokes. The retain loop can use `[SuppressGCTransition]` per-call (already validated safe). The release loop must NOT suppress GC transition per existing guidance.

**Test**: Unit test in `src/Swift.Runtime/tests/` — create multiple `IntPtr` handles, call bulk retain, verify retain counts, call bulk release.

### Item 3: `pack-all.sh` orchestration

**What**: Script to build and pack all three NuGet packages in dependency order.

**Where to start**:
- `validate-libraries.sh` lines 257-266 (dependency extraction), 378-398 (dependency mapping), 867-901 (`compute_dependent_closure()` transitive closure).
- CLAUDE.md "Building local packages" section (lines 186-211) — lists the 6 version locations and the 3 pack commands in order.

**Implementation**: Create `pack-all.sh` at repo root. Arguments: `--version <semver>` (required), `--output <dir>` (default `/tmp/swift-nuget/`). Steps:
1. Validate version argument
2. Update version in all 6 locations (csproj PackageVersion + Sdk.props versions + template csproj)
3. Build packages: Runtime → Sdk (via `build-sdk.sh`) → Templates
4. Revert version changes (restore original files)
5. Print summary of generated `.nupkg` files

**Test**: Run `./pack-all.sh --version 0.0.1-test --output /tmp/test-nuget/` and verify 3 packages produced.

### Item 4: Static protocol constructors

**What**: When a Swift protocol declares `init(...)`, emit a static `Create(...)` factory method on conforming C# types.

**Where to start**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ProtocolHandler.cs` lines 260-264 — constructors are currently skipped with `SkipReason.StaticProtocolMember` and message "Protocol constructor requirements cannot be declared in C# interfaces."
- Lines 88-92 — member count explicitly excludes constructors with comment "Excludes: constructors (need factory synthesis)".

**Implementation**:
1. In `ProtocolHandler`, when encountering `IsConstructor` methods, instead of skipping, collect them.
2. For each conforming type that implements the protocol, emit a `public static T Create(...)` method that calls the witness table's init entry point.
3. The witness table init entry is accessed the same way as other witness dispatch — via `ProtocolWitnessTable` + offset. Look at how `WitnessDispatchEmitter` handles method dispatch for the pattern.
4. The factory method's return type should be the conforming type, constructed via the init witness.

**Risk**: Medium — needs correct witness table offset for init entries. If init witness dispatch doesn't follow the same pattern as method dispatch, this may need more investigation. Budget 45 min, skip if blocked.

**Test**: Unit test verifying the factory method is emitted. Runtime test in BindingTests if time permits.

### Item 5: BindingTests bridge via `--compile-bridge-only`

**What**: Replace the `build-bridge.sh` shell script with the generator's `--compile-bridge-only` CLI flag.

**Where to start**:
- `src/Swift.Bindings/src/CliOptions.cs` lines 127-131 — flag already exists: `--compile-bridge-only` "skips all parsing and C# generation, compiles existing .SwiftUIBridge.swift files from the output directory into a {Module}Bridge.xcframework".
- `BindingTests/build-bridge.sh` — current script: compiles bridge Swift files with `xcrun swiftc` (lines 159-166), creates Info.plist for framework bundle (lines 169-194), runs symbol smoke check (lines 125-139).
- `BindingTests/RuntimeTestsApp/RuntimeTestsApp.csproj` lines 55-84 — `<NativeReference>` entries with `<Kind>Framework</Kind>`.

**Implementation**:
1. Replace `build-bridge.sh` invocation in `BindingTests/build-and-test.sh` with `dotnet run --project src/Swift.Bindings/src -- --compile-bridge-only --xcframework ... -o ...`
2. Handle `SwiftUIBridgeTestHelpers.swift` — this file contains test-only `@_cdecl` helpers not generated by the bridge emitter. Either: (a) pass it as an extra source file to the CLI, or (b) keep a small compile step just for test helpers.
3. Update `RuntimeTestsApp.csproj` NativeReference if the output format changes from `.framework` to `.xcframework`.
4. Update DllImport library name if it changes.

**Risk**: Medium — the CLI flag exists but may not handle the test helpers file. If the CLI needs modification to accept extra Swift source files, scope expands. Budget 45 min.

**Test**: `cd BindingTests && ./build-and-test.sh` — full pipeline must still work.

### Definition of Done
- Each item: working and tested
- No regressions in `run-tests.sh`
- **Minimum acceptable**: Items 1-3 (MSBuild warnings + bulk retain/release + pack-all.sh)

---

## Session 2: Bug Fixes & Marshalling ✅ `18258b98`

**Scope**: 4 bugs with known root causes + 1 investigation-first item
**Risk**: Medium-High — items 1-3 touch marshalling/emit pipeline, regressions possible
**Validation**: `run-tests.sh` after each fix. `validate-libraries.sh` + `run-runtime-tests.sh --timeout 90` at end.

### Item 1: Non-Int32 enum raw values (investigation-first)

**What**: Enums with non-sequential integer raw values (e.g., `none=0, read=1, write=2, execute=4`) emit sequential ordinals (0,1,2,3) instead of the Swift values.

**Skipped test**: `BindingTests/RuntimeTestsApp/Marshalling/NonStandardEnumTests.cs` line 51 — `TestPermissionCaseValues` expects `Execute` = 4 but gets 3.

**Where to start**:
- `src/Swift.Bindings/src/Parser/SwiftInterfaceAccessParser.cs` line 2111 — `GetEnumRawValues()` already exists and parses `.swiftinterface` files, but the doc comment says "Only extracts string raw values. Integer raw values are already correctly inferred from case order." This is the bug — integer raw values are NOT correctly inferred when there are gaps.
- The `.swiftinterface` file for the test library should contain `case execute = 4` — verify this first.

**Implementation**:
1. Check if the test library's `.swiftinterface` contains integer raw value assignments.
2. If yes: extend `GetEnumRawValues()` to also parse `case name = <integer>` lines (currently only handles `case name = "string"`).
3. Wire the parsed integer raw values into the enum emitter — check how string raw values from this method are consumed downstream.
4. If the `.swiftinterface` doesn't contain integer raw values, this is truly blocked — update the skip reason and move on.

**Do this first** — it's investigation-heavy and may turn out blocked, saving time for the other items.

### Item 2: Variadic init data retention

**What**: `VariadicHolder(values: [10, 20, 30]).Sum()` returns 0 instead of 60. The array data isn't retained.

**Skipped test**: `BindingTests/RuntimeTestsApp/Marshalling/WrapperStrippingTests.cs` line 105.

**Where to start**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/ConstructorWrapperEmitter.cs` lines 91-105 — variadic constructors are currently skipped entirely (`HasVariadicParameter` returns false from wrapper emission). The constructor falls back to direct `CallConvSwift` dispatch.
- The root cause: the IEnumerable<int> → Swift Array conversion creates a temporary that isn't retained. When passed via `CallConvSwift` with `@owned` semantics, Swift expects to take ownership, but the temporary may be released before the init body runs.

**Implementation**: The fix path is adding an explicit `swift_retain` on the array handle before the `CallConvSwift` P/Invoke call for `@owned` array parameters. Look at how other `@owned` parameters are handled in the C# marshalling — there may be a `MarshalToSwift` path that already handles ownership transfer for non-variadic cases.

**Test**: Unskip the test, verify `Sum()` returns 60.

### Item 3: Existential container ref param marshalling

**What**: Passing an existential container by reference causes SIGKILL on device (container layout or calling convention mismatch).

**Skipped tests**: `BindingTests/RuntimeTestsApp/Protocols/ExistentialBoxingTests.cs` lines 249, 259 — `[SkipOnDevice]` for `TestRunModeConsumerWithSimpleMode` and similar.

**Where to start**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs` lines 401-423 — existential parameter handling. When `UsesCdeclWrapper` is true, uses `MarshalledType.CdeclExistential` (ref container); otherwise `MarshalledType.Existential` (by-value).
- `src/Swift.Bindings/src/Marshaler/MarshalledType.cs` lines 19-22 — `CdeclExistential` is documented as "ref (pointer)" but need to verify the actual P/Invoke parameter is emitted as `ref ExistentialContainer` vs `IntPtr`.
- Compare the generated C# P/Invoke signature with the Swift @_cdecl wrapper signature for the test functions. The mismatch is likely in parameter count or types.

**Implementation**: Audit the generated P/Invoke for `RunModeConsumer` against its @_cdecl wrapper. Fix whichever side has the wrong signature. Common issues: extra metadata parameters the wrapper doesn't expect, or `ref` vs pointer mismatch.

**Test**: Remove `[SkipOnDevice]`, run `run-runtime-tests.sh`.

### Item 4: SwiftString.Buffer ABI decomposition

**What**: Constructors taking 4+ `SwiftString.Buffer` parameters crash because the 4th struct overflows from GPR registers to stack, but the @_cdecl wrapper and C# P/Invoke disagree on the stack layout.

**Skipped test**: `BindingTests/RuntimeTestsApp/EdgeCases/EdgeCaseTests.cs` line 66 — `TestKeywordTestCreation` with 4 string params.

**Where to start**:
- `src/Swift.Runtime/src/Swift/SwiftString.cs` lines 25-30 — `Buffer` struct contains a single `Data` field (16 bytes: 2 nint fields).
- The ABI issue: AAPCS64 allows 8 GPR slots (x0-x7, 64 bytes). 4 `Buffer` structs = 4 × 16 bytes = 64 bytes exactly. The 4th struct should fit in x6-x7, but if the runtime or @_cdecl wrapper rounds differently, the 4th overflows to stack.

**Implementation**: Decompose `Buffer` into two explicit `nint` fields in the P/Invoke declaration instead of passing the struct. This makes the register assignment explicit and avoids ABI ambiguity. This is a significant marshalling change — affects how `SwiftString.Buffer` is projected everywhere.

**Risk**: High — this changes how a fundamental type is marshalled. Skip if it takes >45 min or causes regressions elsewhere.

**Test**: Unskip the test, verify `KeywordTest("evt", "del", "op", "cls")` constructs correctly.

### Definition of Done
- Each fixed bug: `[Skip]`/`[SkipOnDevice]` removed, test passes
- Bugs that couldn't be fixed: skip reason updated with findings
- No regressions in `run-tests.sh` or `validate-libraries.sh`
- **Minimum acceptable**: Items 1-2 resolved (enum raw values investigated + variadic init fixed)

---

## Blocked (not scheduled)

| Item | Blocker | Skipped Tests |
|------|---------|---------------|
| Protocol descriptor pointers for `SwiftArray<ExistentialContainer>` | Requires new runtime infrastructure to resolve Swift protocol descriptors and pass them when constructing existential containers inside arrays. Not a bug fix — needs architectural design. | 5 tests across ConstructorCollectionTests.cs and ClosureTests.cs |

---

## Dropped

These were investigated and determined to be resolved or not worth pursuing:

| Item | Resolution |
|------|-----------|
| Cross-framework `using` directives | Not needed — generator already uses fully-qualified names for cross-module type references. |
| Presentation helper tests | Low value — would need real UIViewController, only verifies P/Invoke is callable. |
| Multi-level protocol hierarchy BindingTests source | Adequately covered by real-world library validation (Alamofire, Kingfisher, GRDB all use protocol hierarchies). |
