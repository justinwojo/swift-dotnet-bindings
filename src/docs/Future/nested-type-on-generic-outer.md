# Nested-Type-on-Generic-Outer Argument Mis-Placement

Status: design complete, implementation deferred to a separate session.
Surfaced: 2026-04-23 (Round 5 Session 1, StoreKit 2 coverage expansion).
Roadmap entry: `src/docs/roadmap.md` — "Nested-type-on-generic-outer arg mis-placement".
Reproduction fixture: intentionally NOT committed to `BindingTests/Sources/SwiftBindingsTestLib/Enums/GenericPayloadHolder.swift` — it would break `nuke binding-tests --strict` on main via CS0305 + CS0693. The implementation session MUST re-add this Swift source as step 1 before touching the emitter, so every subsequent gate exercises the fix against a live failure:

```swift
/// Nested-type-on-generic-outer fixture: `Failure` is a nested enum inside
/// the generic enum `VerificationOutcome<SignedType>`. The `.unverified` case
/// carries a tuple payload whose second element references `Failure` —
/// which the ABI JSON emits with the outer's generic signature carried in.
public enum VerificationOutcome<SignedType> {
    public enum Failure {
        case expired
        case malformed
        case notAuthorized
    }
    case verified(SignedType)
    case unverified((SignedType, VerificationOutcome<SignedType>.Failure))
}

/// String-specialized factory for `.verified` — exercises the working
/// single-payload path (bare generic-parameter resolution).
public func makeVerifiedOutcomeString(_ value: String) -> VerificationOutcome<String> {
    return .verified(value)
}

/// String-specialized factory for `.unverified` — the tuple payload forces
/// the emitter down the nested-type-with-outer-generic-args path that
/// currently mis-binds args onto the nested segment.
public func makeUnverifiedOutcomeString(_ value: String, reason: VerificationOutcome<String>.Failure) -> VerificationOutcome<String> {
    return .unverified((value, reason))
}
```

## 1. Symptom

Consumer-visible: `StoreKit2.VerificationResult<TSignedType>` ships `TryGetVerified` but NOT `TryGetUnverified` or the `Unverified` case factory. The `.unverified` case is pattern-matchable only via the low-level `Tag`/`Payload` surface — there is no public C# API to either construct it or destructure its `(SignedType, VerificationResult<SignedType>.VerificationError)` payload.

Compiler-level: running the emitter with the reproduction fixture produces two diagnostics against `BindingTests/output/SwiftBindingsTestLib.cs`:

- **CS0693** at the nested-type declaration: `public partial class Failure<TSignedType>` is emitted INSIDE `public partial class VerificationOutcome<TSignedType>`. The C# compiler reports "Type parameter 'TSignedType' has the same name as the type parameter from outer type 'VerificationOutcome<TSignedType>'".
- **CS0305** at the reference site (when the AnyType bail is bypassed): the emitted name is `VerificationOutcome.Failure<Swift.SwiftString>` — outer type arguments placed on the inner segment. C# reports "Using the generic type 'VerificationOutcome<TSignedType>' requires 1 type arguments" against the outer.

In StoreKit 2 today the CS0305 diagnostic does not reach the build because `HasUnsupportedAnyTypeInPayload` (see §2) substring-matches the embedded `Swift.AnyType` and bails the case factory/TryGet before emission. The case silently disappears. CS0693 against the nested-type declaration survives to the build, but survives *quietly*:

- `Directory.Build.props` sets `TreatWarningsAsErrors=True` for in-repo builds (BindingTests, CompileCheck, the main generator csproj) — any real consumer inheriting similar settings would fail.
- The `nuke validate` csproj generator (`BindingProjectEmitter.cs:365`) emits `<NoWarn>CS0169;CA1420</NoWarn>` into a project at `/tmp/binding-validation-<branch>/` that does NOT inherit `Directory.Build.props`. CS0693 is a warning in default MSBuild and is not an error here.
- The packed `StoreKit2.Swift.iOS` NuGet therefore ships with the CS0693 warning unresolved. First consumer with `TreatWarningsAsErrors` fails.

## 2. Root cause

File citations verified against the tree at commit `3d539fc7` (`main`, 2026-04-23). The roadmap entry was written the same day and its line numbers still match.

### 2.a Reference-site: outer generic args appended to nested FQN with empty context

`EnumHandler.CaseConstruction.cs:720-724` (`GetCSharpTypeNameForEnumCase`) handles the bare generic-parameter path (the working single-payload `.verified(SignedType)` case) via `TryGetGenericTypeParameterName`, which accepts multi-character sugared declarator names like `SignedType` when `enumGenericParams` is passed.

`EnumHandler.CaseConstruction.cs:747-750` then handles bound-generic types — which is where `VerificationOutcome<SignedType>.Failure` lands. The typespec parsed from ABI JSON is a `NamedTypeSpec` with `Name = "StoreKit.VerificationResult"`, `GenericParameters = [NamedTypeSpec("SignedType")]`, and `InnerType = NamedTypeSpec("Failure")` (see `TypeSpecParser.cs:162-172` — the `.` between segments yields an `InnerType` chain). `namedTypeSpec.ContainsGenericParameters` is true because the *outer* carries the generic args. The dispatch is:

```csharp
if (typeSpec is NamedTypeSpec namedTypeSpec && namedTypeSpec.ContainsGenericParameters)
{
    return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(typeSpec, GenericContext.Empty);
}
```

Note the hard-coded `GenericContext.Empty`. `enumGenericParams` is in scope at this call site but not threaded through.

`BoundGenericsHandler.cs:649-724` (`TranslateBoundGenericTypeToCSharp`) then:

1. Looks up the type record via `_typeDatabase.GetTypeRecordOrAnyType(namedTypeSpec)`. `TypeDatabaseExtensions.cs:110` resolves the whole `.InnerType` chain via `SwiftTypeName.FromTypeSpec` (`SwiftTypeName.cs:66-80` walks `InnerType` to build `"StoreKit.VerificationResult.Failure"`). The resolved record's FQN is the C# nested form `StoreKit2.VerificationResult.Failure`.
2. Translates each entry in `namedTypeSpec.GenericParameters` via `TranslateTypeSpecToCSharp(param, GenericContext.Empty, moduleDecl: null)` (line 716). The param is `NamedTypeSpec("SignedType")`:
   - `TypeSpecHelpers.IsGenericTypeParameter("SignedType")` is FALSE (length > 3, not in the T/U/V/W/E/K/R/S shortlist — see `TypeSpecHelpers.cs:27-38`).
   - The context-resolve path at `BoundGenericsHandler.cs:658-663` is therefore skipped.
   - Falls through to `GetTypeRecordOrAnyType` — no registered type for `SignedType` → returns `AnyType`.
   - Early-return at line 679-683: `Swift.AnyType`.
3. Calls `QualifyNestedGenericOwners` (line 719, impl at line 876-912). This function WOULD place the outer args on the outer segment — but bails at line 882: `if (moduleDecl == null || !namedTypeSpec.HasModule() || genericContext.IsEmpty) return fullyQualifiedTypeName`. Both `moduleDecl` and `genericContext` are empty at this call site, so it no-ops.
4. Appends the translated params at the end: `StoreKit2.VerificationResult.Failure<Swift.AnyType>`.

### 2.b Downstream bail via substring match on AnyType

Three sites substring-match `Swift.AnyType` in the resolved name to decide whether to skip case emission. They were added to tolerate the pre-existing mis-placement:

- `EnumHandler.CaseInspection.cs:144` — single-payload TryGet: `csharpType.Contains(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)` → skip method.
- `EnumHandler.CaseInspection.cs:314` — tuple-element TryGet: same substring match per element → skip method.
- `EnumHandler.CaseConstruction.cs:42` — case factory: `HasUnsupportedAnyTypeInPayload` (impl at `:1068-1088`) applies the same match (tuple-aware) → skip factory.

Comments at those sites explicitly name the "StoreKit 2 nested-type bug — `VerificationResult.VerificationError<Swift.AnyType>`" as the pattern the substring match was designed to catch. Fixing §2.a makes these bail sites irrelevant for this pattern, but they still cover the legitimate AnyType-fallback cases (unknown types, Lottie's `(Int, UnknownType)` tuples) and must not be removed wholesale.

### 2.c Declaration-site: redundant generic on nested type

Separate from §2.a. The Swift ABI JSON reports `VerificationOutcome<SignedType>.Failure`'s own `GenericSig` with the outer's parameters carried through — this is Swift semantic truth (a nested type under a generic outer is itself generic over the outer's params; `VerificationOutcome<String>.Failure` and `VerificationOutcome<Int>.Failure` are distinct Swift types). `SwiftABIParser.cs:932-934` parses `GenericSig` into the nested `TypeDecl.GenericParameters`. `EnumHandler.cs:152` then calls `GenericTypeEmitter.GetTypeNameWithGenerics(enumDecl, ...)`, which at `GenericTypeEmitter.cs:20-30` produces `Failure<TSignedType>` for the nested declaration.

In C#, a nested class INHERITS access to the outer's generic parameters implicitly — re-declaring them on the nested class is syntactically legal but triggers CS0693 and is semantically redundant. The emitter code path never distinguished "own generic params" from "inherited-from-outer generic params" for TYPE declarations, only for METHODS (see `WrapperEmitter.Signature.cs:111-136`, where the method-side `GetMethodOwnGenericParams` filters the parent type's params out of the method signature exactly to avoid this CS0693 + invalid-generic-constructor pattern).

### 2.d Why the redundant generic wasn't caught sooner

Two reasons:
1. Generic enums with nested generic types + a tuple-payload case that references the nested type are rare. StoreKit 2 is the first validation library to hit it.
2. The `/tmp/binding-validation-<branch>/` csproj doesn't inherit `Directory.Build.props`. CS0693 demotes to warning; `<NoWarn>CS0169;CA1420</NoWarn>` does not include it, but the default `TreatWarningsAsErrors=False` in MSBuild does not promote it. The validation gate passes. Only a consumer with strict settings would trip.

## 3. Reference-site fix (§2.a)

The fix has two independent parts that must both land: pass the enum's generic context into `TranslateBoundGenericTypeToCSharp`, and have `QualifyNestedGenericOwners` place the outer args on the correct segment.

### 3.a Thread the enum's generic context through

At `EnumHandler.CaseConstruction.cs:747-750`, replace the hard-coded `GenericContext.Empty` with a context built from `genericParams`. The helper already exists: `BuildGenericContextFromEnumParams` at `:1110`, used elsewhere in the same file. The call becomes:

```csharp
var genericContext = genericParams != null
    ? BuildGenericContextFromEnumParams(genericParams)
    : GenericContext.Empty;
return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(typeSpec, genericContext);
```

This alone resolves step 2 of the §2.a chain: each outer generic arg (`SignedType`) now resolves via context → `TSignedType` instead of falling through to `AnyType`.

### 3.b Segment-aware placement via `QualifyNestedGenericOwners`

Step 3 and 4 of the chain still place the args at the end. `QualifyNestedGenericOwners` has the correct logic — it walks the TypeDecl chain, identifies which ancestor segments are generic, and distributes args. It needs two changes:

- **Drop the `moduleDecl == null` short-circuit** when a caller passes a non-empty context. The `moduleDecl` is used to locate the nested `TypeDecl` via `FindTypeDecl`. The enum-case emitter has access to `moduleDecl` (it's part of the `EnumHandler` environment) — just thread it through. Alternatively, expose a `BoundGenericsHandler` overload that accepts `moduleDecl` alongside the context.
- **Use `parentTypeDecl` as a fallback when `moduleDecl` is ambiguous**: the `TranslateTypeSpecToCSharp` overload already accepts `parentTypeDecl` (line 741) and threads it into `TranslateBoundGenericTypeToCSharp` (line 773). Extend that to pass through to `QualifyNestedGenericOwners`.

After the fix, the translated type for `VerificationOutcome<SignedType>.Failure` when `genericContext = {SignedType → TSignedType}` becomes: walk the `typeDecl` chain → `[VerificationOutcome, Failure]`, `VerificationOutcome` is generic with param `SignedType` → resolve via context to `TSignedType` → place `<TSignedType>` on the outer segment → final FQN `StoreKit2.VerificationOutcome<TSignedType>.Failure`.

The top-level `translatedGenericParameters` append at line 720-723 still fires — but since `namedTypeSpec.GenericParameters` is ALREADY consumed by the outer segment placement, appending a duplicate `<TSignedType>` on the inner segment must be suppressed. Options:

1. When `QualifyNestedGenericOwners` takes ownership of the outer args, return a flag indicating the params have been placed; skip the final append in the caller.
2. Change `QualifyNestedGenericOwners` to return the fully-formed string (including the inner segment suffix) and have the caller skip both branches.
3. Restructure so the outer generic args live on `namedTypeSpec.GenericParameters` AND the inner nested type is represented via `InnerType` — then the "append at the end" semantic needs to mean "append to the inner segment's args", and the outer segment needs its own explicit arg list. This aligns with the Swift ABI model: the outer carries the args, the inner has none. But it requires broader changes.

Option 2 is the smallest surface area. Recommended.

### 3.c Preserve the single-payload working path

The `.verified(SignedType)` single-payload path works today because the typespec is `NamedTypeSpec("SignedType")` with empty `GenericParameters` — `ContainsGenericParameters` is false, so the `:747-750` branch doesn't fire and `TryGetGenericTypeParameterName` at `:720-724` handles it instead. The fix must NOT broaden the `ContainsGenericParameters` check to also consume bare generic-param typespecs, or the working path regresses. The fixture's `makeVerifiedOutcomeString` covers this; its TryGet emission today is the regression canary.

## 4. Declaration-site options (§2.c)

The redundant `<TSignedType>` on the nested `Failure` inside `VerificationOutcome<TSignedType>` is a C# emission choice. Three options, recommendation at the end.

### Option A — Drop the redundant generic on nested types (recommended)

Change `GenericTypeEmitter.GetTypeNameWithGenerics` (or its callers) so a nested `TypeDecl` under a generic outer emits its OWN generic parameters only, not those inherited from the outer.

The parser propagates inherited parameters into `TypeDecl.GenericParameters` verbatim from the ABI JSON (Swift semantic truth). The fix is at the emission boundary: compute the outer-inherited set at emission time and subtract. The method side already does this: `WrapperEmitter.Signature.cs:118-136` (`GetMethodOwnGenericParams`) subtracts `typeDecl.GenericParameters` from the method's params. The type-declaration side needs the equivalent — subtract each ancestor's params (walking `ParentDecl`) from the nested's list.

Where this changes behavior:

- Type HEADER in `EnumHandler.cs:152`, `NonFrozenStructHandler.cs:106`, `FrozenStructHandler.cs:114`, `ClassHandler.cs:111`: `typeNameWithGenerics` becomes `Failure` (no `<TSignedType>`) for the nested.
- WHERE clause in `GenericTypeEmitter.cs:67` (`GetWhereClause`): must also subtract, or the nested's where clause redeclares outer constraints (CS0692 / duplicate). The outer's constraints are already in scope for the nested; dropping the inherited constraints from the nested's where clause is correct.
- ISwiftObject implementations and metadata-accessor call sites inside the nested use `typeNameWithGenerics` to refer to the nested's own type. `Failure` (non-generic nested) inside `VerificationOutcome<TSignedType>` (generic outer) names a distinct closed generic type PER outer-instantiation — `VerificationOutcome<string>.Failure` and `VerificationOutcome<int>.Failure` are different closed types in the CLR. The emitted metadata accessor `SwiftObjectHelper<VerificationOutcome<TSignedType>.Failure>.GetTypeMetadata()` still resolves to the correct Swift metadata because C# binds `TSignedType` from the outer scope and the CLR interprets the nested type as sharing the outer's type arguments.
- `_payloadSize = TypeMetadata.RegisterAndGetSize(typeof(Failure), ...)` uses `typeof()` on the closed type (outer args implicit) — fine.
- PWT / metadata-accessor P/Invoke arguments (`PInvokeHelperEmitter.cs:439-464`) call `SwiftObjectHelper<T>.GetTypeMetadata().Handle` for each `GenericTypeParameter`. `GenericTypeParameters` is populated from the generating type's own `GenericParameters` — must match what the type declaration actually declares. If the nested drops inherited params from its declaration, the PWT emitter must also skip inherited params when building the nested's argument list (the outer-closed context already has them). The method-side equivalent is `GetMethodOwnGenericParams`; a type-side equivalent is needed.

Commit history check for metadata-lookup justification (the roadmap note: "redundant generic exists for a reason — grep for 'type-metadata lookup'"):

- `git log --all --oneline --grep="metadata lookup" -- src/Swift.Bindings/src/Emitter/` returns no hits for nested-type rationale.
- `Phase 46` (dc1cc750) is where GenericContext + per-method `GetMethodOwnGenericParams` landed; that commit explicitly fixed CS0693 on METHODS/CONSTRUCTORS, leaving the TYPE-declaration case uncovered because no validation library had hit it at the time.
- No surviving comment in the emitter or `GenericTypeEmitter` justifies the redundant generic for nested types on metadata-lookup grounds. The behavior is an emergent consequence of the parser faithfully propagating `GenericSig` from ABI JSON into `TypeDecl.GenericParameters`, combined with the emitter never stripping inherited params from the nested's declaration.

Verdict: the metadata-lookup motivation appears to be folklore, not a load-bearing design. Verify during implementation by running the Option-A variant through the full test matrix (§6) — if any nested-type metadata accessor fails to resolve, the motivation was real and Option C kicks in. Primary risk: libraries that declare method-level generic params with the same NAME as the outer's params in a nested type. The parser's `FromMethodInType` already dedupes; the type-side equivalent must be careful not to mis-subtract when a nested type adds ITS OWN new generic param (e.g. `enum Outer<T> { struct Inner<U> { ... } }` — `Inner` should emit `<TU>` only, not `<T, TU>`).

### Option B — Keep the redundant generic; suppress CS0693

Keep the current emission. Add `CS0693` to `BindingProjectEmitter.cs:365` `<NoWarn>`, and to the SDK's generated csproj `NoWarn` (`src/Swift.Bindings.Sdk/Sdk/Sdk.targets` or wherever the default `NoWarn` lives). Add to CompileCheck's `NoWarn`. Document the redundancy in the wiki's "Known Limitations" page so consumers who enable `TreatWarningsAsErrors` know to include `CS0693` in their own `NoWarn`.

Semantic analysis of CS0693: the C# spec treats the nested param as a *shadowing redeclaration* — `Failure<TSignedType>`'s `TSignedType` is a fresh parameter that is distinct from the outer's `TSignedType`, even though they share the name. If any emitted code inside `Failure` refers to `TSignedType` (e.g. constraints, method signatures, field types), the reference binds to the INNER param. If the outer's `TSignedType` is what was intended, this is a silent correctness bug — not just a cosmetic warning. For a closed-over-outer nested type whose Swift semantic meaning IS the outer's parameter, Option B risks producing subtly wrong metadata bindings. This is why Option B is NOT a clean root-cause fix.

### Option C — Conditional redundant generic

Emit `<TSignedType>` on the nested only when the nested genuinely needs to reference the outer's param in a type-metadata-accessor argument list that the runtime cannot otherwise see (e.g. when the nested has a constrained-generic metadata accessor and the outer's param is NOT closed by the outer's own metadata accessor). Otherwise emit the nested non-generically.

This is defensible if the root-cause analysis in Option A identifies specific metadata-accessor patterns that break without the redundant generic. Defer to implementation: run Option A first; if a specific nested-type metadata case breaks, narrow to Option C for that case.

### Recommendation

**Option A** — drop the redundant generic. Rationale:

1. It is the C#-idiomatic form. Nested classes under generic outers routinely reference outer params without redeclaring them.
2. CS0693 under Option B is not a cosmetic warning — it silently redefines the param inside the nested, which can mask real correctness bugs when the inner's `T` is assumed to bind to the outer's `T`. Suppressing it is symptom-hiding, not root-cause fixing, and violates `CLAUDE.md`'s "no shortcuts" policy.
3. No commit history or surviving comment identifies a metadata-lookup reason for the redundant generic. The folklore can be disproved at implementation time by running the full test matrix.
4. Option A aligns with the already-shipped method-side handling (`GetMethodOwnGenericParams`) — same subtraction pattern, same motivation.

Regression surfaces (to cover in tests):

- Nested type that adds its OWN generic param distinct from the outer's (`Outer<T> { struct Inner<U> { ... } }`). Emission must produce `Inner<TU>`, not `Inner<T, TU>` and not bare `Inner`. This needs a dedicated unit test in `GenericTypeEmitterTests` (create if missing).
- Nested type with a constrained-generic metadata accessor (check `ConcreteProtocolSpecializationEmitter` and its callers from `EnumHandler.cs:515`).
- Doubly-nested generic: `Outer<T> { struct Mid { struct Inner { ... } } }`. `Inner` should emit non-generic, referencing `T` from `Outer` via two-deep implicit inheritance.

## 5. Runtime marshalling verification

The emission fix only produces compilable C#. It does NOT verify that the tuple-with-nested-type factory-thunk marshalling actually works at runtime.

Generic enum cases with associated values route through Swift-compiler-synthesized factory thunks, not `@_cdecl` wrappers. Example mangled symbol (conceptual): `$s...VerificationOutcomeO10unverifiedyACyxG_xt8reason...tcAEmlF`. These thunks take the tuple elements as in-register / indirect args per Swift's calling convention and return the enum's payload buffer by reference.

What is verified today (via `GenericPayloadHolder`'s existing `Holder<T>` / `AppleHolder<SignedType>` fixtures):

- Single-payload `.verified(SignedType)` factory + extraction for both frozen-struct `String` and ARC class `IntBox` payloads.
- Bare generic-parameter resolution through the enum's `genericParams` path.

What is UNVERIFIED:

- Tuple-payload factory for a generic enum.
- Tuple element that is a nested type of the same generic outer.
- Tuple element that is a nested type with its own ABI layout (non-generic nested simple enum — `Failure` in the fixture).
- Extraction (`TryGetUnverified`) mirroring the factory direction.

The factory thunk takes `(SignedType, Failure)` as the payload. `SignedType` is opaque (needs metadata + VWT copy). `Failure` is a simple enum (1-byte tag, no payload). The tuple itself has a well-defined Swift layout (alignment, padding, tag interleaving). Both Mono JIT and NativeAOT have historically diverged on generic-enum tuple payloads — NativeAOT has register-class bugs that don't appear on Mono.

### Proposed runtime test cases (for the implementation session)

Location: `BindingTests/RuntimeTestsApp/Generics/` (likely a new file `VerificationOutcomeTests.cs` or extension of `AppleShapedForecastTests.cs` — check naming conventions at implementation time).

1. **`Unverified_Factory_PreservesStringAndFailureTag`** — call `MakeUnverifiedOutcomeString("payload", VerificationOutcome<String>.Failure.Expired)`; pattern-match; assert the string round-trips and the `Failure` tag equals `Expired`. Runs on both Mono simulator and NativeAOT device.
2. **`TryGetUnverified_DestructuresTupleCorrectly`** — call factory; `TryGetUnverified(out var value)`; assert `value.Item1 == "payload"` and `value.Item2.Tag == CaseTag.Expired`.
3. **`Unverified_RoundTripsAllFailureCases`** — enumerate all three `Failure` cases (Expired, Malformed, NotAuthorized), construct+extract each.
4. **`Unverified_HandlesLargeStringPayload`** — 64KB string payload, stresses VWT copy size handling for the tuple-element-0.
5. **`Verified_StillWorksAfterNestedTypeFix`** — regression canary for §3.c. Run on both runtimes.

### Proposed unit test cases (for the emitter)

Location: `src/Swift.Bindings/tests/UnitTests/EmitterTests/` — add `NestedTypeOnGenericOuterTests.cs` (or extend `BoundGenericsHandlerTests.cs`).

1. Feed a synthesized `TypeDatabase` containing `Outer<T> { enum Inner { case a, b } }`. Assert `TranslateBoundGenericTypeToCSharp` on `Outer<String>.Inner` with a populated `GenericContext` returns `"Outer<string>.Inner"`, NOT `"Outer.Inner<string>"`.
2. Same setup with `GenericContext.Empty` AND `moduleDecl` available — assert it still produces correct placement via the TypeDecl walk (tests the `moduleDecl` fallback path).
3. Doubly-nested: `Outer<T> { struct Mid { struct Inner { ... } } }` — `Outer<String>.Mid.Inner` placement.
4. Nested with its own param: `Outer<T> { struct Inner<U> { ... } }` — `Outer<String>.Inner<int>` placement (outer arg on segment 0, inner arg on segment 1).

## 6. Test matrix

| Layer | What to run | Gate |
|---|---|---|
| Unit (emitter) | `nuke test` with new `NestedTypeOnGenericOuterTests` + existing `GenericTypeEmitterTests` | All pass; no regressions in existing pass count (≥ baseline per `.validation-baseline.json`) |
| BindingTests compile | `nuke binding-tests --strict` | `VerificationOutcome` nested declaration no longer emits redundant generic (no CS0693); reference sites resolve to `VerificationOutcome<TSignedType>.Failure` (no CS0305); `TryGetUnverified` + `Unverified` factory appear in `output/SwiftBindingsTestLib.cs` |
| Runtime sim | `nuke runtime-tests-simulator --skip-regen` | All new `VerificationOutcome` runtime tests pass; no regressions in existing fixtures (especially `Holder<T>`, `AppleHolder<SignedType>`) |
| Runtime device | `nuke runtime-tests-device` | Same tests pass on NativeAOT — generic-enum tuple-payload marshalling differs between runtimes |
| Validation sweep | `nuke validate --filter StoreKit2` | `StoreKit2.VerificationResult<T>` gains `Unverified`/`TryGetUnverified` in the output; `cs_compile` pass count ≥ baseline |
| Validation broad | `nuke validate` (no filter) | `cs_compile` + `swift_compile` ≥ baseline across all libs. Particular attention to generic-enum-heavy libs: `MusicKit`, `AppleMusicServices`, `StoreKit2`. `Directory.Build.props` removal from `/tmp/binding-validation-<branch>/` is a separate cleanup — not required for this fix but surfaces latent CS0693 elsewhere |

Zero-regression policy (`CLAUDE.md`): `.validation-baseline.json` values for `cs_compile` and `swift_compile` must be ≥ baseline. BindingTests pass count and unit test pass count must be ≥ baseline before committing.

## 7. Implementation plan

Ordered steps with size estimates (Claude-session time, not human time) and gates between them. Each gate is a concrete pass/fail check — do not proceed if red.

1. **Thread `enumGenericParams` into the bound-generic dispatch** (small). Change `EnumHandler.CaseConstruction.cs:747-750` to build a `GenericContext` from `genericParams` and pass it into `TranslateBoundGenericTypeToCSharp`. Run `nuke test` — gate: emitter unit tests green, no new failures. Running generator on the `VerificationOutcome` fixture should now produce the outer args resolved to `TSignedType` rather than `Swift.AnyType`, but still mis-placed (expected — step 2 fixes placement).

2. **Plumb `moduleDecl` through the enum-case translation path** (small). Extend `GetCSharpTypeNameForEnumCase` to accept (and thread) `ModuleDecl` from its caller. Callers in `EnumHandler.CaseConstruction` and `EnumHandler.CaseInspection` already have access via the enclosing method's `moduleDecl` parameter. Gate: re-run unit tests — green.

3. **Fix `QualifyNestedGenericOwners` segment placement** (small-medium). Drop the `genericContext.IsEmpty` short-circuit when `moduleDecl` is available; keep the `moduleDecl == null` short-circuit (it's the no-op we can't improve without more plumbing). Have `TranslateBoundGenericTypeToCSharp` suppress the top-level append when `QualifyNestedGenericOwners` placed the args. Gate: add a unit test per §5 unit test case; run; green. Then run `nuke binding-tests --strict` and inspect `output/SwiftBindingsTestLib.cs` — `VerificationOutcome.Failure<Swift.AnyType>` should no longer appear; the reference should read `VerificationOutcome<TSignedType>.Failure`.

4. **Remove the declaration-site redundant generic** (medium). Add `GetTypeDeclOwnGenericParams(TypeDecl)` to `GenericTypeEmitter` mirroring `GetMethodOwnGenericParams`. Use it in the type-name and where-clause emitters (`GetTypeNameWithGenerics`, `GetGenericParameterList`, `GetWhereClause`). Also adjust `PInvokeHelperEmitter.GenericTypeParameters` population for nested types — the P/Invoke metadata-accessor param list must stay consistent with the TypeDecl's declared params. Gate: `nuke test` green; `nuke binding-tests --strict` produces no CS0693 in `output/SwiftBindingsTestLib.cs`.

5. **Remove the AnyType substring bail at the three sites** (small, gated on step 3). Now that the reference site no longer emits `Swift.AnyType` for the nested-type case, the substring bail is only needed for legitimate AnyType fallbacks. Change the check from `csharpType.Contains(anyTypeName)` to `csharpType == anyTypeName` where the whole payload IS AnyType. For tuple elements, keep per-element logic that permits Lottie's `(Int, UnknownType)` — an element that resolves *directly* to `AnyType` remains emittable because `Swift.AnyType` has a `.Payload` property that the factory body uses. Substring matches on "contains AnyType inside generic args" should be rare after step 3 and should produce a gate-warning rather than silent skip. Gate: existing Lottie TryGet emissions are preserved; `nuke validate --filter Lottie` passes.

6. **Add runtime tests for `VerificationOutcome`** (small-medium). Files per §5 runtime proposals. Gate: `nuke runtime-tests-simulator` green; `nuke runtime-tests-device` green.

7. **Full validation sweep** (medium — 2 min of wallclock per sweep, multiple sweeps expected). `nuke validate` → confirm `.validation-baseline.json` pass counts ≥ baseline; update baseline if net-positive. Gate: zero-regression policy holds.

8. **Update roadmap + close** (trivial). Move the entry from `src/docs/roadmap.md`'s remaining-work section to completed. Mention the fixture in `BindingTests` remains a permanent regression canary.

Between steps 3 and 4, decide based on inspection of `output/SwiftBindingsTestLib.cs` whether Option A is tractable or Option C is needed. If a metadata-accessor pattern breaks under Option A that was passing under the redundant-generic form, narrow to Option C for that specific pattern and document the narrowing.

## 8. Don't-break-these constraints

- `.validation-baseline.json` must stay green (`cs_compile`, `swift_compile`).
- `Holder<T>` and `AppleHolder<SignedType>` fixtures' existing runtime tests must continue to pass — the working single-payload path is the regression canary.
- `Lottie.(Int, UnknownType)` tuple-payload TryGet emission must not regress (step 5 edge case).
- `Directory.Build.props` must NOT be modified to add CS0693 suppression. That is symptom-hiding.
- CompileCheck `NoWarn` must NOT be extended to suppress CS0693.
- The validation csproj at `BindingProjectEmitter.cs:365` — leave `<NoWarn>CS0169;CA1420</NoWarn>` as-is. Adding CS0693 there is symptom-hiding.

## 9. Verification of the roadmap entry against current code

The roadmap entry was written 2026-04-23 against the tree at commit `3d539fc7`. All cited line numbers verified in this design doc against the same tree:

- `EnumHandler.CaseConstruction.cs:749` — `GenericContext.Empty` hard-code confirmed.
- `BoundGenericsHandler.cs:649-724` — `TranslateBoundGenericTypeToCSharp` structure confirmed (line 685-723 is the append-outer-args block; line 716 threads `genericContext` into per-arg translation; line 719 is `QualifyNestedGenericOwners` call).
- `BoundGenericsHandler.cs:876` — `QualifyNestedGenericOwners` with the `moduleDecl == null || genericContext.IsEmpty` no-op confirmed at line 882.
- `EnumHandler.CaseInspection.cs:144, :314` — substring bail sites confirmed.
- `EnumHandler.CaseConstruction.cs:42` — `HasUnsupportedAnyTypeInPayload` call confirmed, impl at line 1068.

No drift from the roadmap entry. If future sessions read this doc and the line numbers no longer match, re-grep for `GenericContext.Empty` in `EnumHandler.CaseConstruction.cs` and `QualifyNestedGenericOwners` in `BoundGenericsHandler.cs` — the call structure is stable even if line numbers shift.
