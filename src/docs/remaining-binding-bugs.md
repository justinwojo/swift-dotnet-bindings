# Remaining binding bugs (Session 10)

One session remaining after Session 9 closed Bug 16. See [`realitykit-binding-bugs.md`](realitykit-binding-bugs.md) for historical context — Bug 1–16 narratives, ABI investigation, Sessions 1–7 commit refs.

Bugs covered here: **15c**, plus a possible Bug 3 init-site residual reassessment.

---

## Session 8 — Bugs 15a + 15b: `Optional<T>` resolution gaps — DONE

Closed the two distinct cases where `Optional<T>` dropped to `Swift.SwiftOptional<IntPtr>` instead of resolving to a typed C# nullable.

**Fix sites.**

- **15a** (`Optional<primitive>`): `TypeProjectionFactory.Project` resolves Foundation typealiases (`Foundation.TimeInterval` → `Swift.Double`) via `MarshallingHelpers.TypeAliasToCSPrimitive` before the database lookup, so `OptionalProjection(BlittableProjection("double"))` forms correctly. `TypeDatabaseExtensions.IsObjCModuleType` and `IsObjCClassSwiftType` exclude these aliases so they don't get synthetic `ObjCBridged` (Kind=Class) records that would route Optional through `IsOptionalWithReferenceInner`. A new helper `TryResolvePrimitiveTypeAlias` resolves the alias to the underlying primitive's `TypeRecord` from `TryGetTypeRecord`, `IsTypeProcessed`, `TryGetAnyTypeFallbackInfo`, and `GetTypeRecordOrAnyType` so accessor preflight (`MemberEmissionValidator.GetWrapperSignature`) sees `Swift.SwiftOptional<System.Double>` instead of `Swift.SwiftOptional<Swift.AnyType>`.
- **15b** (`Optional<generic-param>`): `TypeProjectionFactory.Project` trusts `GenericContext` over `IsGenericTypeParameter`'s shape check (parity with `BoundGenericsHandler.TranslateBoundGenericTypeToCSharp`), so sugared parameter names like `Value` resolve to the C# type parameter when the surrounding generic context maps them.

**Test coverage.** Unit tests in `TypeProjectionFactoryTests` exercise both paths with mock `TypeDatabase`. BindingTests fixture (`Optionals/OptionalTypes.swift` + `OptionalMarshallingTests.cs`) round-trips synthetic `Optional<Double>` and `Optional<TValue>` end-to-end.

**Validation gates.** `nuke test` (10093 + 20 + 598 passed) + `nuke validate` (MusicKit 4-platform IMPROVED `fail(28)→ok(0)`, baseline updated, 0 regressions) + `nuke binding-tests --skip-regen` (1732 pass, baseline +4).

**Bug 3 follow-up.** Regenerate `RealityFoundation.Wrapper.swift` and re-survey. If the 2+2 `SampledAnimation` same-type constraint errors persist, they're a Session 11 candidate alongside the 4 init errors (see Bug 3 reassessment below).

---

## Session 9 — Bug 16: required-but-suppressed protocol member gate — DONE

Stopped emitting `EveryProtocol` conformances that are missing required members because the parser stamped those members as `@_spi`-suppressed.

**Fix sites.**

- `src/Swift.Bindings/src/Model/TypeDecl/PropertyDecl.cs` — added `IsProtocolRequirement` (mirrors the existing `MethodDecl` field) so the gate can distinguish required Vars from extension defaults.
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` — populates `IsProtocolRequirement` from the ABI JSON `protocolReq` flag in `CreatePropertyDecl`; the `HasMissingRequirements` counter now also tallies `Kind == "Var"` so failed-to-parse Var requirements still flip the existing pre-scan flag.
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` — `HasSuppressedRequiredMember` helper checks `IsProtocolRequirement && IsSpiProtected` over both `Properties` and `Methods`; both `WillSkipConformance` (pre-scan) and `EmitProtocolConformance` (emission-time, with `RecordSkip("RequiredMemberSuppressed")`) consult it. **`IsModuleInternal` is intentionally NOT consulted** — the parser's negative-space heuristic flags public protocol-requirement Vars as internal because the swiftinterface body lists them without a leading `public ` keyword (e.g. SnapKit `ConstraintPriorityTarget.constraintPriorityTargetValue`), and treating those as suppressed regresses conformances that already emit working witnesses on baseline.

**Test coverage.**

- Unit tests at `src/Swift.Bindings/tests/UnitTests/EmitterTests/EveryProtocolEmitterTests.cs` (`WillSkipConformance_RequiredSpiProperty_ReturnsTrue`, `WillSkipConformance_RequiredSpiMethod_ReturnsTrue`, `WillSkipConformance_RequiredModuleInternalProperty_DoesNotSkip`, `WillSkipConformance_NonRequiredSpiProperty_StillEmits`) and `ProtocolParserTests.cs` (`PropertyDecl_IsProtocolRequirement_DefaultFalse`/`CanBeSet`).
- BindingTests fixture: `BindingTests/Sources/SwiftBindingsTestLib/Protocols/SpiRequirementProtocolSkipping.swift` (`Bug16SpiRequirementProtocol` with public `publicLabel` getter + `@_spi(Internal) var __linkSPI` requirement and an `@_spi(Internal) extension` providing the default impl, plus a public conformer + consumer) + `BindingTests/RuntimeTestsApp/Protocols/SpiRequirementProtocolSkipTests.cs` (round-trip + proxy-suppression invariant).

**Validation gates.** `nuke test` (10099 + 20 + 598 passed) + `nuke validate` (zero metric drift, only SHA bumped) + `nuke binding-tests --skip-regen` (1739 → 1743 simulator pass count from the four new fixtures, 0 fail / 0 crash, baseline matches). The RealityFoundation `MaterialFunction __linkSPI` error is gone from the wrapper-failure list.

---

## Session 10 — Bug 15c: umbrella re-export struct/enum registration — DONE (Step 1)

**Original hypothesis (refuted by trace).** The doc speculated the bug was either flat-vs-chained TypeSpec representation in `TypeProjectionFactory` or umbrella-probe path drop-out. The Step 1 trace at `RealityKit.TextureResource.Semantic` showed neither: TypeSpec arrives **flat** (single `Name = "RealityKit.TextureResource.Semantic"`, no `InnerType` chain), and the umbrella probe correctly rewrites the lookup to `RealityFoundation.TextureResource.Semantic`. The lookup miss happened because **the rewritten key was never registered** in the RealityFoundation `ModuleTypeDatabase`.

**Real root cause.** `SwiftABIParser.CreateStructDecl` and `CreateEnumDecl` eagerly call `_demangledTbd.GetMetadataAccessor(swiftTypeName)`, which throws when the type's metadata accessor symbol isn't in the current module's TBD. For Apple frameworks that umbrella-re-export types (RealityFoundation re-exports RealityKit's `TextureResource`), the metadata accessor lives in the source framework's TBD (`RealityKit.tbd`) — not in the umbrella module's TBD. The exception propagated to `HandleNode`'s catch-all and the nested type was silently dropped. `CreateClassDecl` was unaffected because it stores the raw mangled name and `RegisterClassType` derives `{Mangled}Ma` later — exactly the convention we now use as a fallback.

**Fix sites.**

- `src/Swift.Bindings/src/Demangler/DemanglingResults.cs` — added `TryGetMetadataAccessor(SwiftTypeName, out string)` non-throwing variant.
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` — `CreateStructDecl` and `CreateEnumDecl` delegate to a new `ResolveMetadataAccessor` helper that falls back to `$"{node.MangledName}Ma"` only when `node.ModuleName` matches the module being parsed (i.e., the umbrella case where RealityFoundation parses RealityKit-mangled types as its own). Cross-module Apple extension types (e.g., a `SwiftUI.Label` extension surfaced inside FamilyControls's ABI) keep the original throw-and-drop behavior so they aren't mistakenly registered into the wrong module's database.

**Cross-module guard rationale.** A naïve "always fall back" landed first and regressed FamilyControls: the `SwiftUI.Label` extension surfaced as a `Label` struct with `node.ModuleName = "SwiftUI"` and got registered into FamilyControls's DB, which let the SwiftUI bridge resolve `ManagedSettings.Token<T>` to a non-generic `Swift.ManagedSettings.Token` and fail to compile. Gating on `node.ModuleName == moduleDecl.Name` preserves the umbrella win without leaking external Apple types.

**Test coverage.**

- `src/Swift.Bindings/tests/UnitTests/ParserTests/UmbrellaReExportTests.cs` — 5 unit tests: nested struct, nested enum, struct+enum+class siblings on a class with empty `DemanglingResults` (simulating the umbrella case), `TryGetMetadataAccessor` smoke test, and a cross-module guard test asserting `SwiftUI.Label` is NOT registered when seen during parsing of another module.

**Impact on RealityFoundation** (measured against the SDK build path's emitted `apple-frameworks/RealityFoundation/obj/.../RealityFoundation.cs`). Module type-record count: 438 → 565 (+127 newly registered umbrella re-exports — `TextureResource.{Semantic, MipmapsMode, CreateOptions, Compression, Format, Contents, Drawable.Descriptor, Format.ColorSpace, Format.NormalEncoding, Compression.ASTCBlockSize, Compression.ASTCQuality, Contents.MipmapLevel, Contents.Slice, ...}` and similar nested struct/enum chains beneath other re-exported classes). Residual `Swift.SwiftOptional<IntPtr>` count: 219 → 117 (−102). The remaining 117 are cross-module sites that v2's `node.ModuleName` guard correctly leaves alone (e.g., Metal `MTLCullMode`, SwiftUI bindings, etc.) — those are Step 2 / supplement candidates, not umbrella misregistrations. RF.cs grew from 135,882 → 170,483 lines (+34,601) as previously dropped types now flow through to typed properties/accessors.

**Validation gates.** `nuke test` (10104 + 20 + 598 passed; +11 from the 5 new umbrella/cross-module tests and existing harness growth) + `nuke validate` (127/127 overall, 113/113 standalone, no regressions, baseline SHA-only diff) + `nuke binding-tests --skip-regen` (1743 pass / 0 fail / 0 crash, baseline match).

**Step 2 (carry-over) — broader cross-module residuals.**

The remaining 117 `SwiftOptional<IntPtr>` sites are cross-module references that need targeted registration work — Step 1's umbrella fallback intentionally doesn't touch them. Highest-frequency clusters from the regenerated RF.cs:

- `Foundation.AttributedString`: hand-roll a Foundation supplement parallel to existing `Data.cs` / `URL.cs` patterns in `Swift.Bindings.Apple/Sources/Foundation/`. Closes the `TextComponent.text` site. **(landed — see "Step 2 progress" below.)**
- `simd.simd_quatf`: register as a frozen blittable native-remap (16-byte float quaternion). Either a new `simdDatabase.xml` or an extension of existing native-remap config. **(landed — see "Step 2 progress" below.)**
- `MTLCullMode` / Metal cross-module enums: the `cullMode` and similar Metal-typed parameters on `RealityFoundation.DirectionalLightComponent.Shadow`, etc.
- SwiftUI binding-target types referenced by RealityKit View modifiers (`bindTarget`, `id`).

**Implementation guidance** (still applies):

- **Registration first, wrapper API last.** Don't ship `SwiftOpaqueHandle<T>` or synthetic class wrappers in this session.
- Public opaque-handle API is hard to unwind. Defer until the post-registration residual is known.

### Step 2 progress (Foundation.AttributedString + simd.simd_quatf)

**simd.simd_quatf — frozen blittable native-remap.** Single-precision quaternion (`simd_float4` payload, 16 bytes; imaginary lanes `xi/yj/zk` at indices 0–2, real lane `w` at 3) is bit-compatible with `System.Numerics.Quaternion`'s `(X, Y, Z, W)` field order, so it ships as a `managedNameSpace="System.Numerics" managedTypeName="Quaternion"` projection in `src/Swift.Runtime/src/Swift/SimdDatabase.xml` (alongside `simd_float4x4` and friends). `src/Swift.Bindings/src/Data/apple-frameworks.json` adds `simd_quatf` to the `simd` module's `valueTypes` list so `AppleFrameworkRegistry.IsKnownValueType("simd.simd_quatf")` returns `true`. Test coverage: `TypeDatabaseTests.cs` adds an `[InlineData("simd.simd_quatf", Struct, frozen=true, blittable=true, size=16)]` row to the existing `SimdDatabase_StructTypes_ResolvesCorrectly` theory plus a new `SimdDatabase_Quatf_ProjectsAsSystemNumericsQuaternion` fact pinning the namespace/type-name projection.

**Foundation.AttributedString — Apple supplement (manifest-driven).** Added to `src/Swift.Bindings.Sdk/tools/apple-types-manifest/include-types.json` and `manifest.json` modeled exactly on the existing `Foundation.PersonNameComponents` entry: VWT-opaque storage strategy, no sequential-layout whitelisting, metadata accessor `$s10Foundation16AttributedStringVMa` from `Foundation`, availability floors `ios15.0 / maccatalyst15.0 / tvos15.0 / macos12.0` (matches the SwiftBindings.Apple supplement floor, so `weak_link=false`). `AppleTypesCsEmitter` auto-emits the `sealed partial class : ISwiftObject, ISwiftStruct, IDisposable` to `src/Swift.Bindings.Apple/obj/Debug/net10.0-{tfm}/AppleTypes/Foundation/AttributedString.cs` — no hand-rolled file required (the auto-generated layout is structurally identical to `URL.cs` / `Data.cs` / `PersonNameComponents.cs`). After the build, the supplement now exports 15 Apple-types files (was 14).

**Impact on RealityFoundation** (measured by regenerating directly via `swift-api-digester` against `iPhoneSimulator26.2.sdk` + `dotnet run --project src/Swift.Bindings/src` into `/tmp/step2-rf-regen/`).

| Metric | Step 1 | Step 2 | Δ |
| --- | --- | --- | --- |
| `SwiftOptional<IntPtr>` residuals | 158 | 129 | −29 |
| `simd_quatf` occurrences | 39 | 6 | −33 (the 6 left are cosmetic — generic-instantiation extension class names like `SampledAnimationsimd_quatfExtensions`, parameter names auto-derived from the Swift call site, and `So10simd_quatfa` mangled-symbol fragments inside `EntryPoint=` strings; the actual *types* all project as `System.Numerics.Quaternion`) |
| `Foundation.AttributedString` references | 2 (broken — emitted as Microsoft.iOS `Foundation.AttributedString`, which doesn't exist; only `NSAttributedString` does) | 6 (all routed to `Swift.Foundation.AttributedString` — `TextComponent.Text_Get/Set` is the headline site, `MeshResource(extruding:)` constructors close the rest) | wired |
| `System.Numerics.Quaternion` references in RF.cs | 0 | 82 | newly typed |

(Step 1 baseline above is `/private/tmp/trace15c-rf/RealityFoundation.cs` — the residuals number quoted as 117 in the Step 1 narrative was measured against the SDK-build pipeline's emitted file, which slightly differs in line count from the direct-regen path used for Step 2's measurement; the 158 → 129 delta is apples-to-apples within the same direct-regen output, and the wins are visible regardless of which baseline you measure against.)

**Validation gates.** `nuke test` (10106 + 20 + 598 passed — +2 from the new `SimdDatabase_*` rows) + `nuke validate` (127/127 overall, baseline updated, Swift wrapper 90/93 unchanged) + `nuke binding-tests --skip-regen` (1743 pass / 0 fail / 0 crash, baseline match — initial run with regen tripped a known-flaky `OptionalMarshallingTests.TestOptionalGenericHolderLargeStructPeek` Mono GC heap-corruption SIGABRT that does not touch `simd_quatf` or `AttributedString`; the deterministic re-run cleared at 1743/1743).

---

## Bug 3 init-site residual (reassessment, not a session)

After Session 8 lands and `RealityFoundation.Wrapper.swift` is regenerated, check the residual error set:

- **2+2 `SampledAnimation` same-type constraint errors** → expected to vanish (15b in disguise — `Optional<Value>` of a parent generic slot failing to specialize).
- **4 `no exact matches in call to initializer` errors** → likely persist; if so, separate constraint-filter gap in `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs:1576, 2293–2310` (`EmitConcreteSpecializationsForGenericParent` constructor sub-path; `DoesPairingSatisfyAssociatedTypeConstraints` deeper bilateral check).

If the 4 init errors persist, file as **Session 11** with narrow constraint-filter scope (≤10 lines).

---

## Suggested order

1. Session 8 (15a + 15b) — small surface, biggest occurrence count, low risk.
2. Session 9 (16) — generalized suppression gate, riding the Bug-5 pattern.
3. Session 10 (15c) — TypeSpec trace gates everything else; registration over wrapper API.
4. Bug 3 reassessment after Session 8.
5. Possible Session 11 only if 15c trace surprises us with a real residual or the 4 Bug 3 init errors persist.

Three sessions are the realistic count. The 15c work could expand if registration turns up unexpected scope, but the upper bound is bounded by the four sub-categories — not open-ended.
