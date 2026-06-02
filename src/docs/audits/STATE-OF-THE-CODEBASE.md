# State of the Codebase — Audit Capstone Synthesis

> Capstone synthesis of the 14-track read-only deep-dive audit of the Swift→.NET binding generator + runtime. This document is the decision-grade index: every claim traces to one Track report + `file:line`. Read this; open the underlying reports only when you fix the item.

## 1. Executive summary

The audit confirmed a dense band of **silent, reachable memory-safety and ABI-correctness defects** concentrated in the boundary-crossing emitters (closures, async/throws, existentials, struct/VWT marshalling) plus a parallel band of **AI-maintainability hazards** (unguarded identifier emission, emitted-name/dedup-key divergence, stale invariant docs, second-class bridge packaging). The single most important sentence a maintainer needs to hear: **the dominant defect class is a managed exception or marshal failure unwinding through an unguarded `[UnmanagedCallersOnly]` callback into native Swift and aborting the process (SIGABRT) — it recurs across at least three independent emitter families (A4 closures, A7 async, C1 SwiftUI), and it crashes rather than failing gracefully on real, reachable type shapes.**

**Headline counts (floor, not ceiling):**
- **Total confirmed across 14 tracks: ~104** (A1:13 / A2:4 / A3:7¹ / A4:7 / A5:9 / A6:5 / A7:10 / A8:6 / C1:7 / C2:5 / M1:5 / M2:9 / M3:7 / M4:8). ¹A3's table lists 6 rows but counts the non-inline box leak as one of two merged confirmations; its header says 7 — counted as 7 here.
- **P0: ~22** (memory corruption / process crash / silent garbage on reachable shapes).
- **P1: ~70** (the bulk — wrong-ABI, leaks, silent-drop, decl-drop).
- **Tracks rated 5/5 (critical): 5** — A1, A3, A4, A5, A7, plus M2 (so 6 if M2 is counted; A1/A3/A4/A5/A7 are the Tier-1 5/5 set). 
- **Recall caveat:** a single heavy run per track demonstrably recovers only **~40–60%** of the discoverable set. A1 was unioned across **three** independent runs precisely because each found *largely non-overlapping* defects, and even each run's headline P0 was found by exactly one run (Track-A1 §0). **These are floor counts. "Not found" ≠ "not present."** Severity labels are also unstable across runs (A1 §0 records a P0-vs-P2 dispute on the same probe-confirmed bug).

---

## 2. Cross-track risk heatmap

Severity counts are confirmed-findings only (refuted/deferred excluded). "Dominant failure mode" is the characteristic crash/corruption signature of that subsystem.

| Subsystem / hot files | P0 | P1 | Dominant failure mode | Risk |
|---|---|---|---|---|
| **Async / throws emitters** (`AsyncHarnessEmitter`, `AsyncMethodGenericBridgeEmitter`, `WrapperEmitter.Async`) — A7 | 6 | 4 | Managed exception escapes UCO async callback → SIGABRT + Task hang + leak | **5/5** |
| **Closure-bridge emitters** (`ClosureEmitter.*`, `NestedClosureBridge`) — A4 | 5 | 1 | Unguarded UCO callback / NULL-deref before error check / uninitialized indirect buffer | **5/5** |
| **Existential proxies / witness dispatch** (`ProtocolProxyEmitter.*`, `EveryProtocolEmitter`, `ExistentialContainer`) — A5 | 2 | 7 | Owned `any P` return double-release (UAF); optional-existential receiver silently returns nil; wrong array stride | **5/5** |
| **P/Invoke ABI / thunks** (`Arm64ThunkTarget`, `SysVThunkTarget`, `CdeclSignatureContract`, `TypeLowering`) — A1 | 3 | 8 | Throwing-ctor error-register swap; `consuming` double-destroy; x8 sret loss; PWT culture-order | **5/5** |
| **ARC / ownership / lifetime** (`SwiftMarshal`, `Arc`, `ExistentialContainer`) — A3 | 1 | 6 | `swift_retain` no-op on ObjC class; owning SafeHandle over borrowed; existential box +1 leak | **5/5** |
| **Struct layout / VWT** (`TypeLowering`, `FrozenStructHandler`, `SwiftOptional`, `SwiftHandle`) — A2 | 1 | 3 | Eightbyte mis-count → silent garbage return; sub-8B Optional over-read; `Optional<T>` mis-size | **4/5** |
| **SwiftUI bridge** (`SwiftUIBridgeEmitter.*`) — M1 (+ C1 P0) | 2 (+1 from C1) | 3 | ObjC-pointer-as-struct-bytes type confusion; `init(rawValue:)!` trap; Data→NSData UAF; reserved-name dup param | **4/5** |
| **Wrapper / SDK packaging / arch** (`Program.cs`, `SwiftWrapperCompiler`, `Sdk.targets`, `CSharpWrapperCoGater`) — M2 | 1 | 8 | DllImport-shaped P/Invoke invisible to co-gater (dangling); bridge xcframework arm64-only → DllNotFound | **5/5** |
| **Concrete specialization / generics / PAT** (`ConcreteProtocolSpecializationEmitter`, `MethodGenericBridgeEmitter`, `BoundGenericsHandler`) — A6 | 2 (+1 P0/P1) | — | Class-conformer carrier-wrap UAF; fixed-256B result buffer overflow/double-free | **4/5** |
| **TypeDatabase / Apple classification** (`apple-frameworks.json`, `*Database.xml`, `AppleFrameworkRegistry`, `TypeProjectionFactory`) — M3 | 0 | 7 | Wrong objcPrefix → `Optional<T>` dropped; missing rawValueType → 4-vs-8B; NSString-typedef as blittable | **4/5** |
| **Co-gater & emitted-name/key consistency** (`CSharpWrapperCoGater` — M2; `IHandler`, `WrapperEmitter.Signature`, `IEnvironment` — C2) | 1 | 4 | Emitted name diverges from dedup key → `CS0111` / silent override mis-bind; DllImport co-gate gap | **4/5** |
| **Maintainability / identifier emission** (mega-emitters; `ModuleEmissionContext` regex) — C1 | 1 | 6 | User param name collides with synthetic local/param → `CS0136`/`CS0100`; cross-module regex path corruption | **4/5** |
| **Parser / demangler fidelity** (`SwiftABIParser`, `SwiftInterfaceAccessParser`, `Swift5Demangler`, `GenericSignatureParser`) — A8 | 0 | 6 | `where ...: AnyObject` throws → whole decl dropped; `@Sendable`/`YK` demangle gap; paren-in-string EOF-swallow | **3/5** |
| **BindingTests gate / skip taxonomy** (`binding-report.json`, skip attributes, `coverage-report.py`) — M4 | 1 | 7 | Wrong-ABI live API behind `[Skip]`; CallConvCdecl crash misattributed to "upstream Issue 1" | **4/5** |

---

## 3. Cross-track themes (root-cause clusters)

The most valuable view: recurring **root causes** that span tracks. Fix the cluster, not the instance.

### Cluster 1 — Unguarded `[UnmanagedCallersOnly]` callbacks abort on exception unwind (THE headline cluster)
A managed exception (thrown by the user delegate, or by `MarshalFromSwift`/metadata resolution/`PtrToStringUTF8` on a marshal failure) unwinds out of a `[UnmanagedCallersOnly]` Cdecl callback into a native Swift frame → process SIGABRT (exit 134), plus a hung Task and leaked holders/buffers.
- **A4:** main escaping callback `ClosureEmitter.cs:132`; throwing callback only handles cooperative `Failure` `ClosureEmitter.Throwing.cs:86`; indirect-return callback + uninitialized buffer `ClosureEmitter.IndirectReturn.cs:83`; throwing non-primitive return NULL-derefs *before* the error check `ClosureEmitter.SwiftWrapper.cs:492`.
- **A7:** five unguarded success callbacks (`AsyncHarnessEmitter.cs:410/497/539/912/1187`) and the generic-bridge success cb `AsyncMethodGenericBridgeEmitter.cs:824`; typed-error rethrow escapes `AsyncHarnessEmitter.cs:1289/1291`. Only `Array<String>` was hardened — the asymmetry proves the rest were not deliberately exempt.
- **C1 (SwiftUI):** trampolines invoke user delegates inside UCO with no try/catch `SwiftUIBridgeEmitter.cs:3468` (deferred).
- **Fix-shape:** every UCO callback wraps the managed body in try/catch; for throwing/async shapes route to the existing `SwiftError*` / `TrySetException` channel (already present and used on the hardened paths), for non-throwing convert to a defined outcome. Check `errorPtr` *before* consuming any result pointer. **Blast radius: very high** — every async result/error callback and most closure callbacks the generator emits.

### Cluster 2 — Emitted-name / dedup-key divergence (the name the emitter writes is invisible to dedup + override verifier)
The authoritative emitted C# name applies a property-collision rename (`Foo`→`FooMethod`/`WithFoo`) and a numeric suffix (`Foo2`); the dedup keys and the same-module override verifier never see those, so real collisions slip past (`CS0111`) and overrides bind to the wrong slot.
- **C2:** `GetProjectedCSharpMethodKey` omits `propertyNames` `IHandler.cs:528`; authoritative name producer `IEnvironment.cs:149` is the only key participant passing `SiblingPropertyNames`; override verifier `ComputeMethodCSharpName` ignores `EmittedCSharpName` `WrapperEmitter.Signature.cs:570`; the `constraints.md:18` WasEmitted inventory is stale (doc 13/6 vs live 23/12).
- **C1:** the *upstream* shape — user param names projected verbatim while synthetic locals/params are hardcoded with no collision guard, across ≥6 emitter families (`SwiftUIBridgeEmitter.AsyncPattern.cs:906` P0; `MethodClosureBridge.cs:385`; `ConcreteProtocolSpecializationEmitter.cs:1585`; `SwiftUIBridgeEmitter.cs:2776`; `ModuleEmissionContext.cs:82`).
- **M2:** the co-gater is a sibling key-mismatch — it only recognizes `[LibraryImport]`+`partial` and is blind to `[DllImport]`+`static extern` P/Invokes `CSharpWrapperCoGater.cs:51`, leaving dangling references to stripped symbols.
- **Fix-shape:** thread the sibling-property set + `EmittedCSharpName` into every key/verifier in lockstep (the canonical helpers already disagree — `ProtocolSignatureHelper` *does* pass `propertyNames`); broaden the co-gater regex and add a reserved-synthetic-name guard that escapes/renames any user identifier colliding with an emitter local. **Blast radius: high** — any API whose param/member names hit common identifiers (`handle`, `result`, `session`, `userData`), and any stripped DllImport symbol.

### Cluster 3 — ObjC-bridgeable / ownership-contract confusion + force-unwrap traps
Reading a C# object pointer as raw struct bytes, force-unwrapping `init(rawValue:)!`, owning a borrowed handle, and `swift_retain` on an ObjC-backed class.
- **M1 (SwiftUI):** ObjC-bridgeable struct init param read as raw bytes `SwiftUIBridgeEmitter.cs:1316` (P0); `Type(rawValue:)!` trap replicated at 6 sites `SwiftUIBridgeEmitter.cs:1295/1532/1340/1573/2013/843` (P0); Data→NSData UAF on a freshly-bridged temp `SwiftUIBridgeEmitter.cs:3861`.
- **A3:** `Arc.Retain` (= `swift_retain`, no-op on NSObject subclass) gated only on `Kind==Class` `SwiftMarshal.cs:466` + tuple twin `:1466`; owning SafeHandle over a borrowed `+0` handle `MarshalBorrowedFromSwift` `SwiftMarshal.cs:825`.
- **M2:** the SwiftUI **bridge** xcframework is second-class to the wrapper (see Cluster 6).
- **A2/M3 adjacency:** sub-8B Optional over-read `SwiftHandle.cs:295`; NSString-typedef structs registered blittable → no-retain pass-through UAF `QuartzCoreDatabase.xml:22`.
- **Fix-shape:** add the `IsObjCBridgeable` branch (the closure Result path already gets it right at `SwiftUIBridgeEmitter.cs:3854-3873`); replace `init(rawValue:)!` with a `guard let … else` graceful surface uniformly across all 6 sites; construct borrowed payloads with `ownsHandle:false`; use `Arc.UnknownObjectRetain` for class payloads. **Blast radius: high for SwiftUI consumers; medium-high for ObjC-rooted Apple types.**

### Cluster 4 — Classification / data-table drift (registered classification disagrees with real ABI)
A type's registered classification (objcPrefix, rawValueType, blittable-vs-class, value-vs-ObjC) disagrees with its real ABI → wrong marshalling.
- **M3:** CoreNFC `objcPrefixes:"NK"` vs real `NFC*` → `Optional<NFCTag>` degrades `apple-frameworks.json:226`; `UIKeyboardType`/`AVCaptureVideoOrientation` missing `rawValueType` → 4-vs-8B `UIKitDatabase.xml:11`; CM/CT/CS/PDF/WC prefixes absent → members dropped; NSString-typedef structs as blittable `QuartzCoreDatabase.xml:22`; `Foundation.Date` tuple element `double` vs `DateProjection` → `CS0029` `FoundationDatabase.xml:10`; `_LocationEssentials.CLLocationCoordinate2D` forced ObjC-class `AppleFrameworkRegistry.cs:502`.
- **A8 (parser misclassification):** `consuming`/`borrowing func` denied the safe wrapper, degraded to raw `CallConvSwift` `SwiftInterfaceAccessParser.cs:147`; public protocol requirements flagged `IsModuleInternal` `:2795`; typed-throws first-match error type `:3774`.
- **Fix-shape:** correct the data tables (prefix, rawValueType, kind) and add unit `[InlineData]` theories pinning the classifier (currently zero coverage for these). For the parser, widen the modifier alternations and select the *last* `throws(` match. **Blast radius: per-framework / per-API — surgical but each silently wrong on a public Apple surface.**

### Cluster 5 — The end-to-end gate misattributes OUR bugs to "upstream .NET" (process/trust hazard)
M4 shows `[SkipOnSimulator]`/`[Skip]` reasons blaming "upstream Issue 1" for crashes on **pure CallConvCdecl** paths, where Issue 1 (CallConvSwift-only, and per the verified doc only a *secondary* masking assertion after an already-crashed frame) structurally cannot fire.
- **M4:** throwing-closure success tests skipped as Issue 1 on a 100% Cdecl path while the sibling param variant runs unskipped `ClosureEdgeCaseTests.cs:224/234`; the returned-throwing-closure skip masks a self-inflicted generator gap (a working cdecl invoke thunk is emitted but never wired, the live `_invoker` is raw CallConvSwift) `SwiftBindingsTestLib.cs:25006`; a wrong-ABI live public API ships behind `[Skip]` with a factually-wrong "symbol stripped" reason `SwiftBindingsTestLib.cs:169929` (P0); macOS/Catalyst gates skip Mono-only tests on CoreCLR where the limitation cannot fire `Build.RuntimeTests.cs:1849`.
- **Why it matters:** this directly implicates the standing `feedback_mono_jit_blame.md` rule — **only 4 confirmed upstream issues exist; everything else is ours.** Every "Issue 1" skip on a Cdecl path is a generator/runtime bug parked behind a skip, eroding the trust contract the gate exists to enforce.
- **Fix-shape:** add a meta-test invariant — any skip citing "Issue 1"/"!ji->async" must have ≥1 CallConvSwift P/Invoke on its path; remove the others, wire the dead cdecl invoke thunk, and either fix or `[Obsolete]`/suppress the AsyncGenericContainer P0 so no live wrong-ABI method ships. **Blast radius: medium code, high process/trust.**

### Cluster 6 — The bridge xcframework is second-class to the wrapper xcframework
The SwiftUI **bridge** xcframework is never fattened, dropped from pack/consumer-targets, and guarded by stale slice-id defaults — all because the bridge-compile path never threads `--target-architectures`/`WithArchitecture`, and the standalone Apple-direct branch hard-codes the bridge "off" after compiling it.
- **M2:** bridge sim slice arm64-only `Program.cs:1225` / `SwiftWrapperCompiler.cs:987` / `Sdk.targets:2153` → DllNotFound on iossimulator-x64/Rosetta; direct-mode standalone drops bridge from pack + consumer NativeReference `BindingsGeneratorCommand.cs:1316/1044`; Guard 2a (SWIFTBIND031) uses static slice-id defaults `Sdk.targets:2441`.
- **Fix-shape:** add a `targetArchitectures` param to `RunCompileBridgeOnly` threaded through `CompileBridge*`/`WithArchitecture` + lipo fold; add `--target-architectures` to `_CompileSwiftUIBridge`; mirror the xcframework branch's bridge-NativeReference/pack emission in the standalone branch; glob the real `*-simulator` dir in Guard 2a. **Blast radius: every third-party SwiftUI-bridged binding on x64-sim / Rosetta consumers.**

### Cluster 7 — Parallel / duplicated emission paths that have already drifted (maintainability multiplier)
Multiple emitters re-derive the same policy independently and have diverged; the divergence *is* the bug.
- **A7:** a complete dead duplicate async emitter in `WrapperEmitter.Async.cs:1330` has already diverged (3-param vs 4-param `TryGetOptionalMarshalType`); the live/dead boundary between it and `AsyncHarnessEmitter` is unresolved.
- **A4:** four+ parallel closure marshalling engines `ClosureProjection.cs:1` re-derive escaping/CC/leak policy — the confirmed findings *are* the drift.
- **A6/C1:** `MethodGenericBridgeEmitter` shadowed by the CSM emitter but still wired in with an un-ported fixed-256B buffer; skip-ladder triplicated in EveryProtocol with `HasNoncopyableMember` only in one copy `EveryProtocolEmitter.cs:1857` vs `:1455`.
- **Fix-shape:** collapse to one reachable path or add a structural test asserting exactly one live emitter; this is the cross-cutting reason single-site fixes regress. **Blast radius: indirect but compounding.**

---

## 4. Single deduped P0→P2 prioritized backlog

One merged, severity-ordered queue across all 14 tracks. Where a defect appears in multiple tracks it is merged with all sources cited. Reachability is noted; the corrected state is reflected (see §5 notes on M2 arm64e-sim and A6 protocol-composition).

### P0 — memory corruption / process crash / silent garbage on reachable shapes

| ID | `file:line` | Description | Track(s) | Reach / blast | Fix-shape |
|---|---|---|---|---|---|
| P0-01 | `ClosureEmitter.cs:132`, `…Throwing.cs:86`, `…IndirectReturn.cs:83`, `…SwiftWrapper.cs:492` | Unguarded UCO closure callbacks abort on exception unwind; non-primitive throwing return NULL-derefs before the error check; indirect buffer left uninitialized | A4 | Every closure callback; SIGABRT/SIGSEGV | try/catch → `SwiftError*`; check errorPtr first; zero/skip buffer on throw |
| P0-02 | `AsyncHarnessEmitter.cs:410/497/539/912/1187/1289/1291`, `AsyncMethodGenericBridgeEmitter.cs:824/866` | Unguarded UCO async success/error callbacks abort + hang Task + leak on marshal throw | A7 | All async result/error cbs except `Array<String>` | catch → `TrySetException`; free buffers in catch |
| P0-03 | `SwiftUIBridgeEmitter.cs:1295/1340/1532/1573/2013/843` | `Type(rawValue:)!` force-unwrap traps on out-of-range C# enum value, 6 sites | M1 | Any BoundEnum View param; SIGTRAP from valid managed input | `guard let … else` graceful surface, all 6 sites |
| P0-04 | `SwiftUIBridgeEmitter.cs:1316` (+837/1333/1550/1566) | ObjC-bridgeable struct init param: C# passes object pointer, Swift reads as raw struct bytes → type confusion / SIGSEGV | M1 | `Foundation.URL`/`Data` View params | add `IsObjCBridgeable` branch (closure path already correct) |
| P0-05 | `Arm64ThunkTarget.cs:62-67`, `SysVThunkTarget.cs:163-165` | Throwing **class** ctor: errorOut placed first by contract but read as trailing → full register swap, wild store | A1 | Plain `throws` value-arg class ctor; both arches | drive thunk register math from contract phase order |
| P0-06 | `NativeThunkEmitter.cs:614` + `TypeLowering.cs:171-178` | `consuming` non-copyable param forwarded at +0 → Swift consumes, C# `ReleaseHandle` destroys again → double-free (SIGABRT) | A1 | `~Copyable` consuming param | split fast-path predicate to require `!IsIndirect`; model ownership (P1-A1-12) |
| P0-07 | `TypeLowering.cs:228` (`LowerStruct`) | Frozen struct >16B / ≤4 eightbytes / >4 fields mis-marked indirect; thunk tail-calls, never fills `x8` → silent garbage return | A1, A2 | e.g. `{Int8×5,Int64,Int64}`; reachable free func | bucket fields into eightbytes; bridge >16B direct return |
| P0-08 | `Arm64ThunkTarget.cs:88-89` + `ThunkAssemblyEmitter.cs:163-165` | ARM64 thunk drops x8 (cdecl sret) across metadata accessor for static >32B frozen return → SIGBUS | A1 | static func returning >32B frozen struct | `IsIndirect`-aware ARM64 bridge predicate (severity P0/P2 disputed; treat ≥P1) |
| P0-09 | `ProtocolProxyEmitter.InterfaceImpl.cs:1857` + `WitnessDispatchEmitter.cs:1901` | Opaque `any P` member return double-releases (heap-cell free + proxy Dispose both Destroy, no retain-on-read) → UAF/SIGSEGV | A5 | Every opaque single-protocol/composition return | take balancing +1 via existential `InitializeWithCopy` on read |
| P0-10 | `ProtocolProxyEmitter.SwiftObject.cs:92` | Owned opaque proxy finalizer runs direct VWT Destroy on GC finalizer thread, not the `SBW_VWTDestroy` trampoline siblings use | A3, A5 | Owned opaque proxy dropped w/o Dispose; crash *tier* disputed (contract violation undisputed) | route finalizer release through `SBW_VWTDestroy` |
| P0-11 | `ConcreteProtocolSpecializationEmitter.cs:1715` | CSM method returning generic param to a **class** conformer reads carrier address as the object pointer → handle aliases freed buffer (UAF) | A6 | Bare ABI conformance, no hint; shipping path | `MarshalFromSwift<C>(*(IntPtr*)resultPtr)` |
| P0-12 | `MethodGenericBridgeEmitter.cs:733/837/861` | Fixed-256B `AllocHGlobal(256)` result buffer → heap overflow for >256B; double-free + allocator mismatch for non-frozen struct | A6 | Latent (CSM shadows it) but wired in | size via `GetSwiftTypeSize<T>()`; discriminate ownership; scope the free |
| P0-13 | `SwiftUIBridgeEmitter.AsyncPattern.cs:906` (+688/1124) | Async `_Create` appends fixed trailing params after user params, no de-dup → duplicate decl (`CS0100`/Swift redeclaration) | C1 | async View init param named `userData`/`onError`/… | de-dup across all 3 surfaces |
| P0-14 | `CSharpWrapperCoGater.cs:51` | DllImport-shaped P/Invokes invisible to co-gater (only `LibraryImport`+`partial` matched) → dangling reference to stripped symbol | M2 | 4 emitters emit this shape (KeyPath/AppEntity/metadata) | broaden regex + partial-decl finder to DllImport+static-extern |
| P0-15 | `SwiftBindingsTestLib.cs:169929` (`AsyncGenericContainer.ProcessAsync/FetchOrThrowAsync`) | Live non-`[Obsolete]` public method with ABI-mismatched CallConvSwift PInvoke (5 args incl. undeclared TMetadata, self not in SwiftSelf) → SIGSEGV; masked by `[Skip]` with a false reason | M4 | A consumer would ship + hit it | correct metadata-forwarding `@_cdecl` or suppress with `[Obsolete]`/`[UnsupportedSwiftType]` |

### P1 — wrong-ABI / leak / silent-drop / decl-drop (highest-value subset; ~70 total)

| ID | `file:line` | Description | Track(s) | Fix-shape |
|---|---|---|---|---|
| P1-01 | `SwiftMarshal.cs:466` + `:1466` | `Arc.Retain` (`swift_retain`, no-op on NSObject subclass) on `Kind==Class`, no ObjC discrimination → over-release/SIGSEGV | A3 | use `Arc.UnknownObjectRetain` both sites |
| P1-02 | `SwiftMarshal.cs:825` (`MarshalBorrowedFromSwift`) | Owning SafeHandle over a borrowed +0 handle; user `Dispose` double-frees | A3 | construct borrowed payload `ownsHandle:false` |
| P1-03 | `ExistentialContainer.cs:964` (+`:938`) | `swift_allocBox` +1 (and inline ref-backed `InitializeWithCopy` +1) never released; no `swift_deallocBox` anywhere | A3 | conditional VWT Destroy at boxed-payload call sites only |
| P1-04 | `ProtocolProxyEmitter.Receivers.cs:931` | Optional-existential receiver ALWAYS returns `nil` (dead stub wins over later-added working path); silent drop | A5 | delete the early-return |
| P1-05 | `EveryProtocolEmitter.cs:2311/2537/3219` | Value-type getter/subscript/method return leaks the C#-allocated buffer (no `deallocate()`) | A5 | add `resultPtr.deallocate()` all 3 sites |
| P1-06 | `ExistentialContainer.cs:88/155/128` | `Box(int)`/`Box(long)` both → `Swift.Int`; `Unbox` returns `long` → `int` round-trip throws; bare-`Any` param throws on non-primitives | A5 | reject int loudly or preserve origin; emit `CreateAny<T>` fallback |
| P1-07 | `ExistentialProjection.cs:156` | Owned existential collection-element returns (`[any P]`/`[K:any P]`/`Set`) use non-owning ctor → per-element leak | A5 | wire `GetOwnedReturnElementConversion` into Array/Dict/Set projections |
| P1-08 | `ProtocolProxyEmitter.Receivers.cs:1580/1519` | Class-bound `[any P]` getter/param uses 40B `ExistentialContainer1` where Swift reads 16B → wrong stride, over-release | A5 | use `ArrayElementCarrierType` + matching element conversion (both directions) |
| P1-09 | `NativeThunkEmitter.cs:321-323/700-702` | `SmallStructReturnDivergesFromCAbi` false on null `LowerReturnType` → frozen ≤8B mixed/float struct tail-call-thunked, float reads 0 | A1 | route null-layout small returns to `@_cdecl` |
| P1-10 | `CdeclParamMapper.cs:284-289` | `Foundation.Data` `@_cdecl` decomposes to two `Int` words; C# passes one 16B struct → 2nd word lost (AArch64) | A1 | align decomposition between sides |
| P1-11 | `PInvokeEmitter.cs:839` + `MethodMarshalPlanBuilder.cs:162` | Generic PWT params ordered by culture-sensitive comparer; Swift uses Ordinal → witness tables swapped | A1 | `StringComparer.Ordinal`, lockstep |
| P1-12 | `WrapperEmitter.Marshalling.cs:985` | Non-optional `@convention(c)` closure Bool/enum param forwarded raw into idiomatic delegate → `CS1503` | A1 | bridge the non-optional param direction |
| P1-13 | `ArgumentDecl.cs:25` + `SwiftABIParser.cs:2167` | Parser never models `consuming`/`borrowing` (`Owned`/`Shared` collapse to false) — upstream enabler of P0-06 | A1 | carry ownership flag distinct from `IsInOut` |
| P1-14 | `SwiftHandle.cs:295` | `PayloadBuffer<IntPtr>.Buffer` reads 8B for a 1B `Optional<Bool>` payload → 7B over-read (UB) | A2 | pass buffer address or size to actual payload; flip guard |
| P1-15 | `FrozenStructHandler.cs:518-544` | `TryComputeOptionalInlineSize` `innerSize=IntPtr.Size` clamp mis-sizes multi-word RMM `Optional<T>` (under/over) | A2 | persist real per-instantiation Optional size |
| P1-16 | `NestedClosureBridge.cs:668` | Inner-trampoline leaks one `AnyObject` box per invocation (`passRetained` never balanced) | A4 | release once per box at correct lifetime point |
| P1-17 | `WrapperEmitter.Async.cs:413` + `AsyncMethodGenericBridgeEmitter.cs:1244` | Cancellation key is the recycled GCHandle integer → stale unregister evicts a live call; uncancellable | A7 | monotonic per-call token |
| P1-18 | `AsyncClosureHelper.cs:34` | Per-call async-closure GCHandle "intentionally leaked" → strong handle + captured graph leaks per one-shot call | A7 | free the handle after single-resume completion |
| P1-19 | `SwiftUIBridgeEmitter.cs:3861` | ResultClosure ObjC-bridgeable struct passes `passUnretained` over a freshly-bridged temp → UAF (Data/custom) | M1 | bind to a local before `passUnretained` |
| P1-20 | `SwiftUIBridgeEmitter.cs:3315/3869` | Frozen-with-ref-fields closure-arg leaks heap buffer + ARC +1 per call (no Swift defer-deallocate) | M1 | emit `defer { deinitialize; deallocate }` for `FrozenWithMemory` only |
| P1-21 | `IHandler.cs:528` / `IEnvironment.cs:149` / `WrapperEmitter.Signature.cs:570` | Emitted name (property-rename + `Foo2` suffix) invisible to dedup keys + override verifier → `CS0111` / silent override mis-bind | C2 | thread `propertyNames`+`EmittedCSharpName` into all keys/verifier |
| P1-22 | `MethodClosureBridge.cs:385`, `ConcreteProtocolSpecializationEmitter.cs:1585/AsyncGenericParent.cs:895`, `SwiftUIBridgeEmitter.cs:2776`, `ModuleEmissionContext.cs:82`, `EveryProtocolEmitter.cs:1857` | Unguarded-identifier family: user param collides with hardcoded synthetic local/param (`CS0136`/`CS0100`); cross-module regex mid-path corruption; skip-ladder asymmetry | C1 | reserved-name guard; `__`-prefix control locals; anchor regex leading-only; add `HasNoncopyableMember` to prescan |
| P1-23 | `Program.cs:1225` / `SwiftWrapperCompiler.cs:987` / `Sdk.targets:2153` / `BindingsGeneratorCommand.cs:1316/1044` | Bridge xcframework arm64-only + dropped from pack/consumer → DllNotFound on x64-sim/Rosetta (Cluster 6) | M2 | thread `targetArchitectures` through bridge compile; mirror xcframework branch emission |
| P1-24 | `Sdk.targets:2441` | SWIFTBIND031 Guard 2a uses static slice-id defaults; resync is AppleFramework-only → false hard-error on x86_64-only sim | M2 | glob the real `*-simulator` dir |
| P1-25 | `WrapperXCFrameworkMerger.cs:116` | Non-transactional fat-fold: mid-loop throw leaves a fat binary advertised as single-arch → denied slice/DllNotFound | M2 | atomic lipo-overwrite + plist-rewrite |
| P1-26 | `apple-frameworks.json:226`, `UIKitDatabase.xml:11`/`AVFoundationDatabase.xml:11`, CM/CT/CS/PDF/WC, `QuartzCoreDatabase.xml:22`, `FoundationDatabase.xml:10`, `AppleFrameworkRegistry.cs:502` | Classification drift: wrong objcPrefix, missing rawValueType, NSString-typedef as blittable, Date tuple `double`, value struct forced ObjC-class (Cluster 4) | M3 | correct tables + add classifier unit theories |
| P1-27 | `SwiftInterfaceAccessParser.cs:3774`, `Swift5Demangler.cs:552`, `GenericSignatureParser.cs:124`, `SwiftInterfaceAccessParser.cs:2795/3816` | Parser/demangler: typed-throws first-match; `@Sendable`/`YK` demangle gap; `where ...: AnyObject` throws → whole decl dropped; paren-in-string EOF-swallow; public protocol requirement flagged internal | A8 | last-match; handle `Yb`/`YK`; special-case keyword constraints; inString state machine |
| P1-28 | `ConcreteProtocolSpecializationEmitter.cs:1715` class alloc; `MethodGenericBridgeEmitter.cs:837` ARC | (frozen-with-ref variant of P0-11/P0-12) leak +1 / missing `DestroyWireBufferRetains` | A6 | port the CSM `needsResultPtrDestroyWireRetains` discrimination |

### P2 — most important (latent traps a future change would activate)
- `NativeThunkEmitter.cs:525` `ComputeReturnZeroExtension` defaults null `InlineSize` to 1-byte `movzbl` → truncates >256-case enum tag (x86_64) — A1.
- `TypeLowering.cs:205-218` nested/packed struct return loses alignment + per-field positional register slots vs swiftcc eightbyte coalescing — A1, A2.
- `ValueWitnessFlags.AlignmentMask = 0x0000FFFF` vs ABI `0x000000FF` — latent, would corrupt every tuple offset if populated — A2.
- `SwiftUIBridgeEmitter.cs:3468` SwiftUI closure trampolines lack try/catch (Cluster 1, deferred) — C1.
- `BoundGenericsHandler.cs:821-822` nested bound-generic member return drops leaf args → `CS0305` — A6 (inconclusive, real defect at corrected line).
- `coverage-report.py:1037` coverage matrix can't detect missing *runtime* coverage (derives "passing" from Swift-source + generator skips, never reads `RuntimeTestsApp/*.cs`) — M4.
- `TestDiscoveryGenerator.cs:149` `async void` tests run detached, post-await failures unobserved → false PASS — M4.

---

## 5. Top-20 files to touch with care

Ranked by concentrated risk. For each: why it's dangerous, and the **invariant a future AI agent must preserve** (the plausible-but-wrong-change guardrail). Pulls heavily from C1's hazard map cross-referenced with the ABI tracks.

| # | File | Why dangerous | Invariant to preserve |
|---|---|---|---|
| 1 | `Emitter/.../AsyncHarnessEmitter.cs` (1700+ lines) | 6 P0 unguarded UCO callbacks; hand-synced with a dead duplicate + a live Swift-side half | Every UCO callback body is try/catch-wrapped routing to `TrySetException`/`SBW_Free`; **don't** add a new result-type callback without the catch — only `Array<String>` was hardened, not by design (A7). |
| 2 | `Emitter/.../ClosureEmitter.*` (SwiftWrapper/Throwing/IndirectReturn/StructParams) | 5 P0s; 4+ parallel closure engines that have drifted | Check `errorPtr` *before* consuming a result pointer; never call a `delegate* unmanaged[Swift]` from a display-class lambda (the `!ji->async` shape) — route through the cdecl invoke thunk (A4). |
| 3 | `Marshaler/TypeLowering.cs` | P0 eightbyte mis-count → silent garbage; null-layout small-return divergence | `IsIndirect` must be decided on **eightbyte count**, not field count; a >16B *direct*-return struct still needs the `x8` bridge (A1, A2). |
| 4 | `Emitter/.../Arm64ThunkTarget.cs` + `SysVThunkTarget.cs` + `ThunkAssemblyEmitter.cs` | Throwing-ctor register swap; x8 sret loss; `NeedsReturnBridge` conflates two predicates | Drive error/self/sret register placement from `CdeclSignatureContract` phase order, not the trailing-error formula; preserve x8 across the metadata accessor `bl` (A1). |
| 5 | `Emitter/.../SwiftUIBridgeEmitter.cs` (+AsyncPattern/InitAnalyzer, 3962 lines) | P0 reserved-name dup param, P0 enum force-unwrap (6 sites), P0 ObjC-pointer-as-bytes, UAF, leak; **zero** identifier escaping in the whole family | Every BoundEnum conversion guards `init(rawValue:)`; every ObjC-bridgeable param branches on `IsObjCBridgeable`; de-dup trailing synthetic params vs user params (M1, C1). |
| 6 | `Runtime/.../SwiftMarshal.cs` | `swift_retain` no-op on ObjC; owning-over-borrowed SafeHandle; tuple-element twins | Class retain must dispatch on ObjC-vs-Swift (`UnknownObjectRetain`); a borrowed handle must be `ownsHandle:false`; fix `ExtractCopiedValue` and its `:1466` twin together (A3). |
| 7 | `Runtime/.../ExistentialContainer.cs` | Box +1 leak (no `swift_deallocBox`); inline ref-backed +1 leak; int→long drift; bare-Any throw | Any +1 (`allocBox`/`InitializeWithCopy`) needs a matching Destroy — but **conditionally** (the proxy path is correctly balanced; a blanket Destroy over-releases) (A3, A5). |
| 8 | `Emitter/.../ProtocolProxyEmitter.*` (InterfaceImpl/Receivers/SwiftObject) | P0 opaque double-release; P0 finalizer-thread direct VWT; optional-existential nil-stub; wrong array stride | Opaque `any P` read must take a balancing +1 via existential `InitializeWithCopy`; finalizer release routes through `SBW_VWTDestroy`; don't reinstate the dead nil-stub (A5, A3). |
| 9 | `Emitter/.../EveryProtocolEmitter.cs` (5595 lines) | Value-type return leak (3 sites); skip-ladder triplicated + drifted; walker `default:false` polarity traps | Keep the three skip copies in sync (`HasNoncopyableMember` belongs in the prescan too); `deallocate()` every C#-allocated return buffer (A5, C1). |
| 10 | `Marshaler/IHandler.cs` + `IEnvironment.cs` (key generators) | Emitted-name/dedup-key divergence root → `CS0111`/silent mis-bind | The dedup key MUST include the same `propertyNames`/`parentTypeName`/suffix the emitted name uses; the canonical helpers already disagree — fix them in lockstep (C2). |
| 11 | `Emitter/.../WrapperEmitter.Signature.cs` | Same-module override verifier ignores `EmittedCSharpName` → wrong-slot override (compiles clean) | `AncestorCSharpNameMatches` must prefer `ancestorMethod.EmittedCSharpName` (carries the `Foo2` suffix) before recomputing (C2). |
| 12 | `Emitter/.../ConcreteProtocolSpecializationEmitter.cs` | P0 class-conformer carrier-wrap UAF; hardcoded control locals; raw-name parent CS0246 candidates | Read class conformers via `*(IntPtr*)resultPtr`; `__`-prefix control-flow locals; the CSM emitter is the *correct* reference — port its discrimination to the bridge, not vice-versa (A6, C1). |
| 13 | `Emitter/.../MethodGenericBridgeEmitter.cs` | P0 fixed-256B buffer overflow + double-free; shadowed-but-wired-in | Size buffers via `GetSwiftTypeSize<T>()`; discriminate ownership-transfer vs copy-out vs pure-value before freeing (A6). |
| 14 | `Runtime/.../SwiftHandle.cs` + `SwiftOptional.cs` | Sub-8B Optional over-read; `Optional<T>` mis-size; value-type `None` collapses to zero | `PayloadBuffer` read size must match the real payload, not `sizeof(IntPtr)`; the value-type `None`/`default(T)` collapse is a public-API trap (A2). |
| 15 | `Configuration/CSharpWrapperCoGater.cs` | P0 DllImport-shape blind; 3 brace-walkers with no string/comment state | The candidate finder must recognize **both** `[LibraryImport]`+`partial` and `[DllImport]`+`static extern`; brace counting needs string/char/comment awareness (M2). |
| 16 | `Configuration/SwiftWrapperCompiler.cs` + `Program.cs` (bridge compile) | Bridge never fattened; non-transactional merger; `ResolveAutoArchBasis` blanket catch | The bridge compile path must thread `--target-architectures`/`WithArchitecture` exactly like the wrapper; never advertise a fat binary with a single-arch plist (M2). |
| 17 | `Sdk/Sdk.targets` (2461+ lines) | Static slice-id guard; `_CompileSwiftUIBridge` omits `--target-architectures`; AppleFramework-only resync | SwiftFramework and AppleFramework bridge paths must reach arch-parity; fingerprint must include `SwiftTargetArchitectures` so a stale arm64-only bridge isn't reused (M2). |
| 18 | `Parser/SwiftInterfaceAccessParser.cs` (5250 lines) | Negative-space classification flips on brace/paren/string miscount; modifier-alternation gaps drop/degrade members | `HasUnmatchedOpenParen` and `CountBraces` need identical string/escape state; protocol requirements have no `public ` keyword — don't classify them by negative space (A8, C1). |
| 19 | `apple-frameworks.json` + `*Database.xml` | Classification drift silently mis-marshals public Apple APIs; zero unit coverage | objcPrefix must match real class prefix; NSInteger enums carry `rawValueType="Int"`; NSString-typedef newtypes are `kind="class" objcBridged`, not blittable structs (M3). |
| 20 | `Runtime/.../AsyncClosureHelper.cs` + `CancellationTaskEmitter.cs` | Per-call GCHandle leak; GCHandle-integer task identity recycling; `CompleteWithResult` double-resume (inconclusive) | Cancellation identity must be a monotonic token, never a recycled GCHandle integer; dispose the result before/inside a guarded resume so a future gate relaxation can't double-resume (A7). |

---

## 6. What the audit did NOT reach (coverage gaps + residual risk)

**Unaudited-but-suspected surface (deferred-candidate pool):** the per-track verification cap left a large body of credible candidates probed only by source reading. Aggregate deferred counts: **A6:24+, A7:~20, A8:28, C1:~30, C2:19, M1:22, M2:25, M3:25, M4:21**, plus A1's union deferrals, A2:21, A3:22, A4:22, A5:7. **Total deferred candidates ≈ 280+** — this is the approximate size of the suspected-but-unconfirmed surface. Many are the *same shape* as a confirmed finding at another site (e.g. unguarded-identifier family, NSString-typedef family, async-escape family), so the real recall ceiling is materially higher than the ~104 confirmed.

**Entire tracks not run:**
- **Tier-3 L1 (docs-drift), L2 (ObjC interop), L3 (perf) were not run at all.** Docs-drift in particular is partially visible from M4 (README/rules cite deprecated `[MonoJitCrash]`, non-existent scripts, an unproduced `coverage-matrix.json`) and C2 (stale `constraints.md` counts) — a full L1 pass would likely confirm more.

**Verification-method gaps (by design — this was static + compile-probe verification):**
- **No live `dotnet build -r` consumer round-trips** and **no full `nuke binding-tests` gate runs.** Every confirmed crash/corruption rests on `/tmp` standalone probes (swiftc/clang dylibs, CoreCLR-under-Rosetta, host-arm64) that faithfully model the emitted ABI, plus generated-output inspection — but **none was reproduced through the actual generated binding on Mono simulator or NativeAOT device.** A1, A3, A4, A5, A6, A7, M1, M2, M3 all explicitly flag this.
- **Mono vs NativeAOT divergence** is largely unprobed; the two runtimes have different ARC/JIT exposure.
- **Severity is not fully settled** — A1 records a probe-confirmed P0-vs-P2 dispute (x8 sret); A3/A5 record a disputed crash *tier* for the opaque finalizer (contract violation undisputed).
- **Subsystem-internal gaps:** non-frozen struct handler (A2); `ExistentialBypassEmitter` (A5); `GenericClosureBridge` is the least-verified closure engine (A4); the SwiftSyntax host parser walkers (A8); the full `[SkipOnSimulator]` inventory (M4 probed only the closure/optional cluster); `Swift5Demangler` internals (C1/A8).

**Be explicit:** "not found" ≠ "not present." These confirmed counts are floors gathered at ~40–60% per-run recall; the deferred pool and the un-run tracks are where the next round of confirmations will come from.

---

## 7. Recommended next moves

**Fix first — Cluster 1 (the UCO-escape P0 band).** It is the single highest-blast-radius defect class (P0-01, P0-02, plus the deferred SwiftUI trampoline), the fix-shape is uniform (try/catch → existing `SwiftError*`/`TrySetException` channel, check errorPtr first), and it crashes the process on reachable error paths. Resolve the A7 live/dead emitter boundary (`WrapperEmitter.Async` vs `AsyncHarnessEmitter`) *before* patching so the fix lands in the right file.

**Then the Cluster 2/5 governance pair** — wire the dead cdecl invoke thunk (M4 `cs:25006`, A4 1.2), broaden the co-gater (P0-14), and remove/correct every "Issue 1" skip on a Cdecl path while adding the M4 meta-test invariant. This restores the `feedback_mono_jit_blame.md` trust contract and unblocks honest gating.

**Highest-value permanent BindingTests fixtures** (aggregated across reports; these are the durable gates the confirmed P0s currently lack):
1. Throwing/escaping closure where the **C# delegate throws** (non-primitive return, indirect return, async success+error) — assert the Task faults / Swift returns gracefully, no SIGABRT (A4 #1/#4/#5/#6, A7 #1/#2/#3).
2. `consuming` non-copyable param with an observable `deinit` — assert deinit runs **exactly once** (A1 #3).
3. Throwing non-failable **class** ctor with value args (A1 #1) and frozen struct `{Int8×5,Int64,Int64}` returned by value (A1/A2 P0-07).
4. Owned opaque `any P` return dropped **without** Dispose + `GC.Collect`/`WaitForPendingFinalizers` — assert single deinit (A5 #1, A3 #2).
5. SwiftUI View with an out-of-range BoundEnum param and a `Foundation.URL`/`Data` init param (M1 #1/#2).
6. Third-party SwiftUI-bridged binding consumed on `iossimulator-x64` — assert both wrapper and bridge slices list `arm64 x86_64` (M2 #2).
7. ObjC-backed class (`NSObject` subclass) extracted via `Optional`/`Result`/tuple-in-carrier — assert retain-count balance (A3 #1).

**Deferred pools most deserving a second audit run** (highest expected yield): A4 **GenericClosureBridge** (least-verified engine, 4 credible P1s); A7 **WrapperEmitter.Async live/dead boundary**; C1 **SwiftInterfaceAccessParser** brace/scope duplication (largest unverified surface, feeds public/internal classification); M3 **short-prefix + autoBridge-no-prefix** classification family; and a dedicated **L1 docs-drift** pass (M4 + C2 already surface real staleness).

*(Per project policy, upstream-.NET filings are owned by the project owner and are deliberately excluded from this work list.)*

---

*Provenance: synthesized from the 14 Track reports in `src/docs/audits/` (A1–A8, C1–C2, M1–M4). Each report reflects a single heavy run per track (A1 unioned across three) at an estimated ~40–60% per-run recall, so confirmed counts are floors; corrected states (e.g. M2 arm64e-sim → x86_64-only-sim trigger, A6 protocol-composition refutation) are reflected over original claims. Static + compile-probe verification only; no live BindingTests/consumer runs. 2026-06-02.*
