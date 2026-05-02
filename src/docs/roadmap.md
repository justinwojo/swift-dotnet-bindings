# Roadmap

This doc covers longer-term themes, blocked items, and lower-priority ideas. Live baseline counts live in `.validation-baseline.json`; per-library status lives with each package.

> **Every skipped test is guilty until proven innocent.** 102/102 tests previously blamed on Mono JIT were proven to be generator/runtime bugs in our code. There are exactly 4 confirmed upstream .NET runtime behaviours — Issue 1 (Mono bug — `!ji->async`), Issue 2 (Mono+NativeAOT feature request — non-blittable `CallConvSwift`), Issue 3 (Mono bug — `Set.insert` DONE_BLOCKING), and the `SwiftSelf<SafeHandle>` async-lifetime tracking-issue comment (no standalone filing); see `Blocked` section below + memory `feedback_mono_jit_blame.md`. If a crash doesn't match one of these, it's our bug.

---

## Theme A: Skip Reduction *(low priority)*

Skip rate is 23.7% (per current `.validation-baseline.json`). The remaining skips are overwhelmingly either correct behavior (private API, synthesized Codable) or architecturally blocked. Consumer-impactful patterns (`Result<T,E>`, common generics, protocol conformances) are already covered. Further reduction has diminishing returns.

| Item | Remaining skips | Effort | Why low priority |
|------|----------------:|--------|-----------------|
| **Unsupported signatures** (associated types, bare generics) | ~611 | Very high | Swift patterns with no C# equivalent |
| **AnyTypeFallback** (post-M3 decomposition, see below) | ~614 | Very high | Surface is fully architecturally-deferred: PAT classification + by-design Swift `Any` + ObjC protocols + cross-library |
| **UnsupportedClosure** (multi-blocker methods) | ~188 | High | Reduced via setter-only closure properties and the async-closure bridge (throwing 0–3 args with primitive returns plus zero-arg `Foundation.Data` return; non-throwing 0–3 args with primitive returns only). Remaining are generic params, nested closures, and async-closure shapes outside the supported arg/return matrix (e.g., arg-bearing `Data` returns, non-throwing `Data` returns). |
| **UnsatisfiedGenericConstraint** (remaining) | ~76 | High | Fundamental type system constraints, not relaxable gates |
| **Result<T,E> parameter direction** | blocked | Medium | Needs native payload synthesis for C#-created instances |
| **Multi-protocol generic compositions** | blocked | High | Needs full existential composition in @_cdecl wrapper |
| **Value-type generic conformers** | blocked | High | Requires non-AnyObject transport through @_cdecl boundary |

### AnyTypeFallback decomposition (M3 Session 4)

`nuke validate`'s 614 AnyTypeFallback hits split cleanly into four categories. The histogram was the M3 Session 4 deliverable; the conclusion is that the surface is fully out-of-scope per existing roadmap exclusions:

| Sub-cause | Hits | Why deferred |
|---|---:|---|
| M9 gate: method generic-arg AnyType in protocol context (PAT) | 399 | RxSwift/GRDB/Swinject PATs — same root as "Generic protocol constraints / PATs" in *Not Worth Addressing*. Architecturally blocked by associated-type erasure. |
| Bare AnyType properties / `[Any]` / `Optional<Any>` / `[Any: T]` | ~119 | Swift `Any` is a runtime existential with no useful C# representation; `object` would silently lose round-trip identity. Same root as "Unsupported existential" in *Not Worth Addressing*. |
| Subscript return / index AnyType (PAT-shaped) | 62 | GRDB.Row, MusicItemCollection<T>, WeatherKit forecasts, RxSwift.Reactive, XMLCoder.KeyedStorage, TipKit.Tips.Event.Donation. PAT-shaped subscripts where the return type is an associated type or generic parameter. |
| Optional existential inner protocol not in TypeDatabase | 18 | 8 are bare `Optional<Any>` (Swinject Storage×5, ObjectMapper Map×2, Mixpanel.MixpanelFlagVariant); 10 are pure-ObjC delegate protocols (Foundation.URLSessionDelegate, UIKit.UITextFieldDelegate, UIKit.UIPopoverPresentationControllerDelegate, UIKit.UIPopoverPresentationControllerSourceItem, PassKit.PKAddPaymentPassViewControllerDelegate). Both ObjC and pure-`Any` are filtered by `GetEffectiveProtocols` → `object` → property skip. ObjC protocol bridging is post-1.0 surface. |

In-scope surface (single-module supplement-resolution gaps, alias gaps): **0 measurable hits**. The original ~303 / ~429 figures cited in the gameplan and earlier roadmap rows pre-date the M9 gate (which surfaced 399 PAT classifications cleanly); the increase is bookkeeping, not regression.

---

## Theme B: Multi-TFM Runtime Coverage

**Exercise non-iOS slices of multi-TFM packages at runtime.** ~35k lines of test code already cover ~39 libraries on iOS sim + device, but every test app targets `net10.0-ios` only. Packages that ship for macOS / Mac Catalyst / tvOS get compile-gated against those TFMs and never run there. The compile gate catches generator/SDK issues; runtime drift (P/Invoke ABI, framework lookup, codesign, lifecycle) goes unnoticed until a consumer reports it.

Multi-TFM packages currently shipping (TFMs untested at runtime in **bold**):

| Package | TFMs | Untested at runtime |
|---------|------|---------------------|
| **CryptoKit, MusicKit, StoreKit2, TipKit, WeatherKit** | iOS + **macOS** + **Catalyst** + **tvOS** | 3 of 4 |
| **LiveCommunicationKit, WorkoutKit** | iOS + **macOS** + **Catalyst** | 2 of 3 |
| **ProximityReader** | iOS + **Catalyst** | 1 of 2 |

8 Apple-framework packages × 1–3 untested platforms each. Third-party libraries currently ship iOS-only — extending them to additional TFMs is a separate, per-library question driven by consumer demand, not part of this theme.

**Approach:**
1. Pick 1–2 packages with the broadest TFM matrix as the pilot (StoreKit2 and TipKit are the natural candidates — both already ship 4 TFMs, both have substantive embedded tests).
2. Multi-target the existing test app so the same C# test surface runs on each TFM the package supports.
3. Wire macOS / Mac Catalyst / tvOS runners into the regression-validation flow next to the existing iOS sim + device legs.
4. Roll the pattern out across the remaining 6 multi-TFM packages.

Once stable, the `nuke binding-tests` flags (`--macos --catalyst --tvos`) that already exist generator-side become first-class regression gates, not just developer probes.

---

## Theme C: Upstream Bug Reports + Repro Repo *(ready to file 1/2/3)*

Filing-ready bug reports for the three confirmed upstream issues live as one file each under `Future/upstream-issue-{01,02,03}-*.md`; see `Future/upstream-issues-README.md` for the filing guide. Repro repo at `/Users/wojo/Dev/swift-interop-repro/` carries one C# class per issue. The 2026-04-30 Codex fact-check + disassembly review surfaced Issue 8 as a wrong-P/Invoke-shape bug on our side rather than a runtime bug; the generator now emits the correct shape for fully bare-generic multi-element tuple returns. Mixed shapes (e.g., `(T, Int)`, `(Array<T>, T)`) still fall through to the legacy path — see *Lower Priority*.

Remaining steps:
1. Publish repro repo to GitHub (`justinwojo/swift-interop-repro` — currently local only, no remote).
2. File Issues 1, 2, 3 on dotnet/runtime; post the `SwiftSelf<SafeHandle>` item as a tracking-issue comment.

---

## RealityKit shipping-plan follow-ups

Surfaced during Session 5 of `realitykit-shipping-plan.md` (commits `3a3748dc` + `c614ae45`). RealityKit dep-gate now at 15/15 after Session 5b (P1 closed); the items below are the remaining cleanup workstream.

**~~P1~~ — Existential return-type suppression for autoBridge-module Swift-only protocols. ✅ Resolved in Session 5b.** Split `IsObjCExistentialBridgedProtocol` off from the broad `IsObjCModuleType` so existential filter / parity-guard sites use the per-module ObjC prefix gate (`AppleFrameworkRegistry.IsObjCBridgedTypeName`), while the synthetic-record `ObjCBridgingStrategy` path stays on the broad helper (umbrella-collapsed types like `RealityKit.Entity` from `RealityFoundation.Entity` must still classify as ObjC). Also threaded `CurrentModuleName` through `ProtocolHandler.GetCSharpTypeName`'s `ProjectionContext` so cross-module existential interface members qualify correctly (the unqualified-vs-qualified mismatch caused CS0246 + CS0738 on `IEntityGestureRecognizer.Entity`). RealityKit `MultipeerConnectivityService.Owner` now emits `RealityFoundation.ISynchronizationPeerID?`. BindingTests fixture: `Protocols/AutoBridgeSwiftOnlyExistentialReturn.swift` + `AutoBridgeSwiftOnlyExistentialTests`. Unit pinning: `IsObjCExistentialBridgedProtocol_PerModulePrefixGate` (10 cases) + `IsObjCModuleType_BroadAutoBridgePreserved` (5 cases) + `IsObjCBridgedTypeName_ReturnsExpected` (17 cases).

**P2 — Wrapper-emission bugs surfaced during ARRaycastQueryTarget Codex review.** Three issues called out by Codex session `019de6ef-c41c-7fb3-a708-cda5cde59cf1` round 2 — recover from that session, reproduce in BindingTests, fix.

**P3 — Existential-resolver cross-module USR-to-printedName reconciliation.** Original suppression scoped during Session 5 work (parser sees module via printedName, TypeRecord via USR). The data fix in `c614ae45` (`apple-frameworks.json` registry entries for ARRaycastQueryTarget) covers the immediate symptoms; reconciliation work would replace the data fix with a code-side fix and generalize to AVFoundation. Lower priority — the data fix is honest and not a hack.

**P4 — Enum TypeRecord synthesis for `c:@E@…` USRs in autoBridge modules.** Companion to P3 — synthesize Enum-kind TypeRecord (not Class) for nested ObjC enum USRs. Authorized but deferred during Session 5 since `c614ae45` lands the data-side fix.

**P5 — Wire `Swift.Runtime` trimmer descriptor for downstream NativeAOT consumers via `buildTransitive`.** Surfaced during Session 6 by Codex review of the tuple-marshalling fix. Embedded `ILLink.Descriptors.xml` in `Swift.Runtime` is honored automatically by the IL trimmer (any consumer that publishes trimmed — `PublishTrimmed=true`, or `IsTrimmable=true` on a referencing library) but **not** by ILC (NativeAOT/device path) when the assembly is referenced transitively — ILC only reads descriptors passed via `--descriptor` IlcArgs. This repo's iOS simulator path runs with `MtouchLink=None` so neither mechanism is active there; the issue surfaces only on NativeAOT consumers. BindingTests works around this by adding both `<TrimmerRootDescriptor>TrimmerRoots.xml</TrimmerRootDescriptor>` and `<IlcArg Include="--descriptor:..." />` directly in `RuntimeTestsApp.csproj`; downstream NuGet consumers don't get this automatically. Fix direction: ship a `buildTransitive/SwiftBindings.Runtime.targets` in the `SwiftBindings.Runtime` NuGet that injects the equivalent IlcArg + TrimmerRootDescriptor whenever `PublishAot=true` is detected. Add a NativeAOT consumer smoke test (e.g., a fresh `dotnet new swift-binding` project consuming the live NuGet) that exercises an `Optional<(T1, T2)>` return path so the gap is detected by CI rather than only by an unrelated regression.

**P6 — Auto-inject cross-framework dep edges for apple-framework mode. ✅** Apple-framework projects (`<SwiftAppleFrameworkTarget>`) now auto-inject inter-package `<PackageReference>` items keyed off the swiftinterface's `import` lines. The SDK target `_DetectAppleFrameworkCrossModuleDeps` (`src/Swift.Bindings.Sdk/Sdk/Sdk.targets`) shells out to the generator's new `--detect-apple-cross-module-deps` subcommand, which parses the swiftinterface, filters marker imports (`Swift`, `_Concurrency`, `_StringProcessing`, `simd`, etc.), resolves each remaining module against `apple-frameworks.json`'s `packageId` field via `AppleFrameworkRegistry`, and emits `MODULE|PACKAGE_ID|VERSION_RANGE` lines. The target injects one `<PackageReference Include="SwiftBindings.Apple.<Module>" Version="[X.Y.Z,X.(Y+1).0)" />` per detected dep, deduping against any user-declared `<PackageReference>` of the same identity (user wins). Runs `BeforeTargets="ResolveProjectReferences;CollectPackageReferences"` so injected refs flow into both `project.assets.json` (restore) and the packed nuspec (pack). Opt-out: `<SwiftAutoDetectAppleFrameworkDependencies>false</SwiftAutoDetectAppleFrameworkDependencies>`. RealityKit's downstream csproj retains its manual `<PackageReference>` for now — dedup makes it harmless — but the block is no longer required and can be removed at any time. See `src/docs/realitykit-shipping-plan.md::Packaging contract` for the updated downstream contract.

---

## Lower Priority

| Item | Notes |
|------|-------|
| **Performance benchmarks** | Baseline P/Invoke overhead measurement. [`Future/interop-performance-validation-plan.md`](Future/interop-performance-validation-plan.md) |
| **API snapshot tooling** | Detect API surface drift between versions. [`Future/api-snapshot-tooling.md`](Future/api-snapshot-tooling.md) |
| **SwiftUI beyond current level** | Wait for consumer feedback before investing further |
| **Property wrappers / KeyPaths** | Low frequency in public API surfaces |
| **Static protocol constructors** | Init witness dispatch needs allocation infrastructure |
| **Weak/unowned references** | 4 test skips. Requires ownership tracking infrastructure |
| **Constrained-generic PWT plumbing for non-accessor P/Invokes** | `EnumHandler.RawRepresentable.cs:146,254` and `OperatorHandler.cs:453,481` still pass bare `GetMetadataArgumentList()`. Not triggered by any current validation library — leave alone until a repro surfaces. |
| **Wrapper-helper path dynamic PWT resolution** | Swift wrapper side still fail-closed for Self-requirement / associated-type protocols. Not triggered by any current validation library. |
| **Multi-PAT existential boxing** | A type conforming to 2+ PAT protocols cannot box through the `object` fallback because the `typeof(object)` dictionary key is ambiguous. Guarded to fail explicitly (`InvalidCastException`) rather than silently select the wrong witness table. Extremely rare in practice. |
| **tvOS device runner** | Requires provisioning profile + physical Apple TV. Generator, SDK, runtime, and build infra already support tvOS; only the `nuke runtime-tests-tvos-device` Nuke target and deployment mechanism are missing. |
| **Mixed-indirect generic tuple returns** | Bare-generic shape (`(T, U)`, `(T, U, V)`) is covered by `IsMultiElementGenericTupleIndirectReturn`. Mixed and bound-generic shapes — `(T, Int)` → `(@out T, Int)`, `(Array<T>, T)` → `(Array<T>, @out T)`, `(UnsafePointer<T>, T)` → `(UnsafePointer<T>, @out T)`, `(Optional<T>, T)` → `(@out Optional<T>, @out T)` — fall through to the legacy `SwiftIndirectResult` path with the wrong shape. Real fix: per-element address-only/direct ABI classifier driving a partial-indirect P/Invoke signature. No active repro from validation libraries; un-block when one surfaces. |
| **Pattern 2 retirement (wrapper-eligibility)** | `SwiftWrapperPostProcessor.Pattern2_SilgenOrCdeclBroken` strips broken `@_silgen_name`/`@_cdecl` wrapper bodies after-the-fact. M3-S3 sub-cause counter showed 99.7% of hits are `Pattern2.InternalType` — wrapper signatures that reach `internalTypeNames`. A naive emission-time gate (`MemberValidationPipeline`) regressed 4 libraries (CryptoSwift/SkeletonView/NVActivityIndicatorView/XMLCoder) because `@usableFromInline internal` types like XMLCoder's `BoolBox`/`FloatBox` are flagged internal yet emitted as public C# classes. Right fix layer is wrapper-eligibility (`MethodWrapperEmitter.ShouldEmitWrapper` / `ConstructorWrapperEmitter` / `PropertyWrapperEmitter`): refuse the `@_cdecl` wrapper when its signature reaches an internal type, then `MethodHandler.cs:928–933` falls back to the original Swift symbol under `CallConvSwift`. Plumbing requires either threading `InternalTypeNames` through `MethodEnvironment` (~13 source-side construction sites + ~44 test sites) or attaching the set to `ModuleDecl`. Proof obligation: BindingTests fixture for an `@usableFromInline internal` type so the runtime path is exercised on iOS sim + device. Not < 1-session scope; deferred. |

---

## Blocked (Confirmed Upstream Only)

These are the **only** confirmed upstream issues. There are exactly 4 (reproduced in standalone repro at `/Users/wojo/Dev/swift-interop-repro/`). If a crash doesn't match one of these, it's our bug. See `feedback_mono_jit_blame.md` for the full investigation checklist.

| Filing | Issue | Blocked By |
|--------|-------|-----------|
| 1 | **Mono: JIT assertion `!ji->async` on CallConvSwift P/Invoke** | Fatal `jit-info.c:918` during stack unwinding through a `wrapper_managed_to_native_*` frame after a native crash in a `CallConvSwift` callee. Workaround: `@_silgen_name` Swift wrappers / avoid native crashes through `CallConvSwift` |
| 2 | **Non-blittable type rejection with CallConvSwift** | .NET runtime design limitation. Workaround: @_cdecl wrappers (already covers 78.5% of P/Invokes) |
| 3 | **Mono: `Cannot transition thread from STARTING with DONE_BLOCKING` on `(Bool, @out via x0)` tuple-return CallConvSwift** | Specific to `Set<T>.insert` ABI shape. `Set.contains` (no `@out`) passes. Workaround: `@_cdecl` Swift wrapper |
| comment | **Mono: SafeHandle async lifetime** (tracking-issue comment, no standalone filing) | GC may collect SafeHandle during async suspension. Workaround: manual ARC retain/release or singleton pattern |

| Other | Status |
|-------|--------|
| **Non-Int32 enum raw values** | Blocked on Swift compiler: `.swiftinterface` strips integer raw values. No workaround. 1 skipped test. |

---

## Not Worth Addressing

| Skip Reason | Count | Why Not |
|-------------|------:|---------|
| @_spi / internal members | ~750 | Correct behavior — private API should not be bound |
| Synthesized Codable | ~730 | .NET consumers use own serialization (`System.Text.Json`, etc.) |
| Generic protocol constraints / PATs | ~453 | Architecturally blocked by associated type erasure |
| SwiftUI/Combine dependencies | ~181 | Framework boundary — consumers use SwiftUI bridge instead (`SwiftUIConstraint` + `SwiftUIView`) |
| Unsupported existential (opaque generics) | ~90 | Fundamental limitation of Swift's type system vs C# generics |

---

## Explicitly Out of Scope

| Item | Reason |
|------|--------|
| Full Swift type graph infrastructure | Over-engineered for current needs |
| Deep generic signature / associated type constraint emission | C# generics can't express Swift's full type system |
| Result builder (`@resultBuilder`) projection | Compile-time Swift feature, no ABI JSON representation |
| `@dynamicMemberLookup` / KeyPath projection | Affects <5 types across 53 validation libraries |
| Composing SwiftUI view trees from C# | Result builders are a compiler feature |
| Structs projected as C# value types | Only safe for frozen+blittable subset; marginal benefit |

