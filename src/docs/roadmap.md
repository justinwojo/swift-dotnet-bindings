# Roadmap

**Created**: February 2026
**Status**: Active — single source of truth for work items

For completed work (Phases A–G), see `CompletedPhases/phases-a-through-g.md`.
For detailed gap descriptions and contract matrix, see `testing-gaps.md`.
For deferred/aspirational work, see `Future/`.

---

## Current Baselines

| Metric | Value |
|--------|-------|
| Unit tests | 1665 passing |
| Integration tests | 699 passing (11 skipped, pre-existing) |
| TestFramework must-pass | 94/94 passing, 0 degraded |

| Library | Binding Errors | Test App Errors | Notes |
|---------|---------------|-----------------|-------|
| **BlinkID** | 0 | N/A | Clean |
| **Nuke** | 1 | 0 | |
| **CryptoSwift** | 3 | 0 | |
| **Lottie** | 8 | N/A | 2 distinct bug patterns |

---

## Phase H: Unit Test Gaps + Remaining Library Errors

**Status**: H1 Done, H2 Not Started
**Priority**: High — eliminate remaining library errors
**Effort**: Medium (1-2 sessions)

### H1: Unit Test Coverage Gaps (Phase G fixes)

Phase G fixed 8 generator bugs but 3 fixes lack targeted unit tests. Add tests to prevent regressions.

| Fix | Gap | What to Add |
|-----|-----|-------------|
| **G2** (Optional existential property pass-through) | Weak — existential detection tested, pass-through logic not | PropertyHandlerTests: optional existential property emits `get => MethodName();` / `set => MethodName(value);` pass-through instead of TypeConversionHandler |
| **G5** (Zero-protocol existential return guard) | Weak — Any detection tested, wrapper return not | WrapperEmitter test: method returning `Any` (zero-protocol existential) emits `return result;` instead of proxy wrapping |
| **G6** (Proxy dedup key unification) | Minimal — only 1 test | ProtocolProxyEmitterTests: protocol with closure param + array param (same ProtocolSignatureHelper key, different GetMethodKey) emits only one interface method |

Secondary gaps (moderate coverage exists, explicit tests would strengthen):

| Fix | Gap | What to Add |
|-----|-----|-------------|
| **G1** (Generic type params in properties) | Moderate — GenericContext tested, property integration not | PropertyHandlerTests: property on generic type `Container<T>` emits `T0` not `AnyType` |
| **G7** (IntPtr fallback in GetBufferType) | Partial — AnyType fallback tested, IntPtr not explicit | BoundGenericsHandlerTests: `GetBufferType()` for unmapped bound generic returns `IntPtr` |

### H2: Remaining Library Errors (6 distinct bugs, 12 total errors)

#### Bug 1: Optional tuple property → AnyType? cast (CryptoSwift, 1 error)
- **Error**: CS0030 at `Swift.CryptoSwift.cs:566` — Cannot convert `SwiftOptional<(BigUInt p, BigUInt q)>` to `AnyType?`
- **Root cause**: Optional tuple property type not resolved — falls through to AnyType cast
- **Affected type**: `RSA.Primes`
- **Fix area**: PropertyHandler or TypeConversionHandler — optional tuple type resolution

#### Bug 2: C# enum `.Payload` access in enum case factory (CryptoSwift, 1 error)
- **Error**: CS1061 at `Swift.CryptoSwift.cs:4645` — `SHA2.VariantInfo` does not contain `.Payload`
- **Root cause**: `VariantInfo` is emitted as C# `enum` (simple enum), but factory method tries `.Payload` (class enum pattern)
- **Fix area**: Enum case factory emission — detect simple enum and use direct value instead of `.Payload`

#### Bug 3: Receiver dispatch closure parameter mismatch (CryptoSwift, 1 error)
- **Error**: CS1503 at `Swift.CryptoSwift.cs:7664` — Cannot convert `Action<SwiftArray<byte>>` to `AnyType`
- **Root cause**: Receiver dispatch method parameter type doesn't match interface declaration for closure parameters
- **Fix area**: ProtocolProxyEmitter receiver parameter resolution for closure types

#### Bug 4: AnyType fallback in existential method body (Nuke, 1 error)
- **Error**: CS1503 at `Swift.Nuke.cs:11157` — Cannot convert `AnyType` to `IImageDecoding`
- **Root cause**: Method body uses AnyType where existential protocol interface type expected
- **Fix area**: Method body emission — existential return type should use interface, not AnyType

#### Bug 5: Existential `.Payload` on interface types (Lottie, 3 errors)
- **Error**: CS1061 at lines 5824, 6078, 25675 — `ILottieURLSession` does not contain `.Payload`
- **Root cause**: Generated code accesses `.Payload` on protocol interface types, but interfaces don't have nested `.Payload`
- **Fix area**: Existential payload access emission — detect interface types and use proper extraction

#### Bug 6: Metatype `TypeProxy` not emitted (Lottie, 5 errors)
- **Error**: CS0246 at lines 8100, 11476, 21564, 23654, 26299 — `TypeProxy` not found
- **Root cause**: `Any.Type` metatype existential references `TypeProxy` class which is never generated
- **Fix area**: Metatype existential handling — either emit `TypeProxy` or map to appropriate existing type

---

## Phase I: Additional Library Validation

**Status**: Not Started
**Priority**: Medium
**Effort**: Medium (2-3 sessions)
**Depends on**: Phase H (validate with clean error baseline)

### I1. Select and bind a new library
Candidates (pick 1):
- **Alamofire** — networking, heavy closure/async patterns
- **Kingfisher** — image loading, different patterns from Nuke
- **SwiftProtobuf** — value types, generics, enums heavy

### I2. Process
1. Build xcframework for the library
2. Run generator, check binding report
3. Compare member coverage to existing libraries (target: 90%+)
4. Verify golden scenario compiles without interop types
5. Fix any new generator bugs found
6. Add to `BindingTesting/` with build/validate scripts

### I3. Document findings
- Update `CURRENT-STATUS.md` with new library stats
- Add any new skip reasons to `testing-gaps.md`

---

## Future Work

Once Phases H and I are complete:
- Must-pass features at 94+ (currently 94, up from 61 pre-Phase B)
- Runtime test coverage covers most of the contract matrix
- Generated API is idiomatic C# — no interop types in public surface
- 5-6 real-world libraries validated
- Quality scorecard metrics all at gate values
- Test pipeline catches regressions automatically

Next priorities:

- **API Documentation Generation** — Extract Swift doc comments via `swift-symbolgraph-extract` and emit as C# XML doc comments (`/// <summary>`, `/// <param>`, etc.) on generated bindings. Every `.framework`/`.xcframework` ships `.swiftdoc` files that the tool reads — no source code needed. Join key: `usr` field shared between symbol graph JSON and ABI JSON. Steps: (1) run `swift-symbolgraph-extract` in build pipeline, (2) parse `docComment.lines` from symbol graph JSON, (3) add `Documentation` property to `BaseDecl` model, (4) emit XML doc comments in emitter. Tested coverage: Nuke 87%, BlinkID 50%, StoreKit 54%, SwiftBindingsTestLib 96%.
- **`@_cdecl` wrapper generation** for all methods (bypasses Mono JIT bugs #18, #19 for runtime)
- **MSBuild SDK + project templates** — Phase 3 DX work from `north-star.md`
- **Optional string properties** — `Swift.Optional<Swift.String>` → `string?` (extend TypeConversionHandler to unwrap optional strings)
- **Cross-module protocol interface coverage** — Expand `_runtimeProtocols` for stdlib protocols used as existentials (Comparable, Sendable, CodingKey, etc.)
- **Remaining testing gaps** — P3/P4 items from `testing-gaps.md` (PInvokeEmitter tests, golden snapshots, CI)
- **Deferred work** in `Future/` (NativeAOT validation, Roslyn analyzer, existential analysis, performance benchmarks)

### Known Runtime Blockers (Upstream)
- **Mono JIT assertion (jit-info.c:918)**: Kills process on closure P/Invoke + SwiftString via CallConvSwift
- **SafeHandle in async P/Invoke**: Not preserved through async continuation
- **Non-blittable CallConvSwift**: Mono rejects non-blittable types with Swift calling convention
- See `known-issues-workarounds.md` for details
