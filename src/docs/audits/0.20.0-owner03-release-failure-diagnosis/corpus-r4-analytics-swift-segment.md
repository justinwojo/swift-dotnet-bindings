# OWNER-03 analytics-swift / Segment compile repair

Date: 2026-09-15

Base: `0f9b4e11426d7c7230301f45817a38a6b1d0b0f5`

Runtime: macOS (`Darwin`)

Disposition: **the two selected Segment owner defects are repaired and practically
requalified; the historical corpus-r4 publication verdict remains BLOCKED**

This is the next sequential OWNER-03 root-cause batch after the SwiftSoup repair.
It does not rerun or reinterpret the full corpus. It does not waive a gate, widen a
timeout, or claim that unrelated Segment defects are repaired.

## Input authority and reproduction

The practical canary used the official analytics-swift 1.7.3 release artifacts in
fresh `/private/tmp` directories. It did not read the stale Trash corpus or the
shared checkout.

| Input | SHA-256 |
|---|---|
| `Segment.zip` | `a98e6f2d59959e27eb6c85914ccb9a50c187275bb6b4ebfd98eb72cb5fa3dbab` |
| `JSONSafeEncoding.zip` | `bbd4870c3435a7315d2ca494957a22d6c2b69c55f836349da3bd54ec014a0a11` |
| `Sovran.zip` | `98ea04b522e7605943a5c79fc4b8aa3a2e4fc4aa2eef8993d783652b99d3118e` |

The baseline generator output reproduced both historical failures:

1. `Analytics.pendingUploads` (`[URL]?`) emitted an `IntPtr` accessor helper that
   allocated a `SwiftOptional<SwiftArray<IntPtr>>` buffer and returned that value,
   while its P/Invoke still had the direct one-pointer signature. This is the
   historical optional-array-to-`nint` `CS0029` shape.
2. The unsupported closure-initializer tombstone on ObjC-rooted
   `ObjCBlockPlugin : ObjCEventPlugin` emitted
   `base(default(SwiftInheritanceChain))`. The available base constructor plane
   accepts `NativeHandle`/`SwiftHandle`, producing the historical `CS1503`.

Baseline generator log:
`/private/tmp/owner03-segment-baseline-closed-generate.log`, SHA-256
`1304ab6b9a82b4bf9a9af76509d91b6c47ec994981a33dd510663da68c7a44ee`.

## Root causes and repairs

### Optional ObjC-bridgeable containers

The shared Optional queries inspected the native `Swift.Array` representation
instead of the selected wrapper transport. The frozen stdlib container consequently
fell through to the large-Optional value-carrier path, and the bound-generic query
likewise called the value wide. The established `@_cdecl` wrapper contract for
`Optional<Array/Dictionary/Set>` with ObjC-bridgeable leaves is instead one nullable
retained `NSArray`/`NSDictionary`/`NSSet` pointer.

`OptionalMarshalClassifier` and `BoundGenericsHandler` now ask the existing
`CdeclParamMapper.IsOptionalObjCBridgeableContainer` transport predicate before
selecting a decomposed or large Optional. Managed and Swift wrapper sides therefore
agree on one pointer word. The temporary defensive rejections in method and generic
property wrapper eligibility are removed now that their stated disagreement no
longer exists.

The pre-existing BindingTests property
`GenericOptionalAbiBox.BridgedUrls` consequently retargets from the incompatible
direct Swift getter/setter symbols to the generated `SBW_Get_...`/`SBW_Set_...`
pair. The ABI-manifest gate identified exactly this one intentional retarget. Its
single baseline entry is updated as the gate requires; no signature, skip, or
threshold changed.

### ObjC-rooted constructor tombstones

`ClosureParamTombstoneEmitter` previously selected the pure-Swift inheritance
sentinel solely because a superclass existed. It now distinguishes the inheritance
family: ObjC-rooted derived classes chain through
`global::ObjCRuntime.NativeHandle`, while pure-Swift derived classes retain
`SwiftInheritanceChain`. `NativeHandle` is the protected constructor contract also
available when the emitted base comes from another binding assembly.

## Focused regression coverage

- `PropertyHandlerTests` models `Analytics.pendingUploads` and pins the nullable
  `IntPtr` P/Invoke/helper, nullable `IReadOnlyList<NSUrl>` projection, retained
  `NSArray` conversion, Swift nullable-pointer return, and absence of an Optional
  result buffer.
- `ClosureParamTombstoneEmitterTests` models the Segment class hierarchy and pins
  the ObjC-rooted `NativeHandle` base chain while rejecting the pure-Swift sentinel.
- Wrapper eligibility tests now pin acceptance of optional ObjC-bridgeable
  container returns/properties after the shared classifier repair. They also pin
  the emitted method return and generic property getter/setter as direct nullable
  pointers, including retained getter ownership and borrowed setter reconstruction.
- The existing `GenericOptionalAbiBox.BridgedUrls` BindingTests fixture exercises
  the integrated generated C#/Swift route with Some/None getter and setter calls;
  its internal-parent twin remains the direct-path refusal control. The manifest
  baseline records the corrected target pair.

## Segment practical requalification

Fresh generation of Segment plus its JSONSafeEncoding and Sovran dependencies
completed. Segment generation used `--skip-wrapper-compilation` because the
third-party module also exposes a separate pre-existing native-wrapper label error;
this was canary isolation, not a repository gate skip. Generated target files were:

| Generated file | SHA-256 |
|---|---|
| `Segment.Types.Analytics.cs` | `7e9ee5f57013a7e8adea55c584aa1ffc014bcfa5a7b1c55d5b8e3cefdaa8bbc3` |
| `Segment.Types.ObjCBlockPlugin.cs` | `2e0e6d718a3c43a1137b33d9f9014373b5b39756ad07ab04cc36dbb485c0c347` |

The regenerated `pendingUploads` helper and P/Invoke both return direct `IntPtr`,
with no Optional buffer, and the regenerated tombstone chains through
`global::ObjCRuntime.NativeHandle`. Neither historical `CS0029` nor `CS1503`
appears when the complete generated managed project is built.

That unmodified managed build advances to three separate `CS0426` errors because
the emitted Segment type named `System` shadows unqualified `System.Exception` and
`System.Action` in module support code. A validation-only copy under `/private/tmp`
qualified those support-code BCL references and the subsequently exposed
`System.Span`; the complete generated Segment project then built with zero errors.
No repository source was changed for that out-of-scope root.

| Practical check | Result | Log SHA-256 |
|---|---|---|
| fresh Segment generation | PASS | `/private/tmp/owner03-segment-fixed-generate.log` — `f433109a4abcd86bc7dc1808e409cbace397d8d9358a83b55bb93f876e1659cc` |
| unmodified generated managed build | target `CS0029`/`CS1503` absent; BLOCKED by three unrelated `CS0426` diagnostics | `/private/tmp/owner03-segment-fixed-compile.log` — `9e321f5fa38db6d1ef78340e06f9d256c89d6562a7c9d25b3ede17efba000ed4` |
| validation-only BCL-shadow normalization and complete managed rebuild | PASS: 0 errors, 29 warnings | `/private/tmp/owner03-segment-target-isolated-compile.log` — `b1120603bc46f34d1b95409126a295a3dfb0c542e2afed7fa91c2b654d29b7a7` |

## Repository validation

| Gate | Result | Log SHA-256 |
|---|---|---|
| focused emitter/unit tests | PASS: 606/606 | `/private/tmp/owner03-segment-focused-unit.log` — `b03836f82255b2f011848e3968ee277a4be798c87432af21f4df422e0054d153` |
| `nuke binding-tests --compile-only` after the intended one-entry manifest update | PASS in 4:30: 1,708 C# files; C#/Swift compile, ABI-manifest, parity, resilience, ingestion, and overload-name gates green | `/private/tmp/owner03-segment-binding-compile-rerun.log` — `9df9585fe01aa167967bb4054e7ab778e119ccdc81fda3e7cf0a7ed5c4bc1a0f` |

The first compile-only gate run stopped on the one expected retarget and prescribed
the baseline update; the successful rerun is the authoritative gate result. No
full corpus, merge, push, publication, shared-checkout operation, or Trash operation
was performed. Rounded free space after validation remained approximately 22 GiB,
above the required 12 GiB reserve.

## Review

The required final review was Grok-only by explicit owner direction; Claude was not
run. Grok session `01a0a5fb-20aa-78f2-a4b8-662311a1612d` produced the round-one
report at `/private/tmp/owner03-segment-grok-review-round1/result.md` (SHA-256
`894d9c068075c3987a88c8a7b47ca2204aefe2c6b1dd347dd383e603f777c6ff`).

All four findings were accepted:

| Severity | Finding | Disposition |
|---|---|---|
| High | the live `GenericOptionalAbiBox.BridgedUrls` wrapper contradicted a runtime test still requiring both accessors to throw | fixed: the public wrapper now proves Some/None getter and setter calls; the internal-parent twin retains the refusal floor |
| Medium | flipped eligibility tests did not pin the newly admitted method and generic property ABI | fixed: method and generic getter/setter emissions now assert nullable-pointer shape, ownership conversion, and no result/has-value buffers |
| Low | classifier comment incorrectly described frozen stdlib containers as non-frozen/decomposed | fixed: comment names the actual competing large-Optional transport |
| Low | `MarshallingHelpers` comment still said wrapper routes declined the shape | fixed: comment now describes the three aligned predicates |

The post-review validation was:

| Gate | Result | Log SHA-256 |
|---|---|---|
| focused emitter/unit tests | PASS: 606/606 | `/private/tmp/owner03-segment-post-grok-focused-unit.log` — `048e474bbc29006bd2a773292e395bd138fbb9e4dc6c1eba24800f3c47df0555` |
| `nuke binding-tests --class-filter OptionalMarshallingTests` | PASS in 5:30 after normal regeneration/build: 57 passed, 1 known skip; live public wrapper and internal refusal control both pass | `/private/tmp/owner03-segment-post-grok-binding-runtime.log` — `76a0761b1c9fb6b6901b14f2af702da6c6231aa28187e060a42e709c27170269` |

An attempted command containing `--skip-regen` was rejected before process start
under the no-skips guardrail. The successful authoritative simulator command above
ran with `Skip regeneration: False` and `Skip build: False`. Rounded free space
after the post-review runtime gate was approximately 18 GiB, above the required
12 GiB reserve.

Because the accepted High finding changed the candidate, a focused follow-up was
sent to the same Grok session after affected validation. The follow-up report is
`/private/tmp/owner03-segment-grok-review-followup/result.md`, SHA-256
`6ae1c6ed66ce9fe3ad1fd59fc3a0203f514091a229f9bd7335195c4160ce4357`.
It resolved all four findings, filed no new finding, and returned **Ready to land**.
Its only residual notes were that the setter fixture proves callable Some/None
marshalling rather than stored-value observation, and that device/NativeAOT was not
rerun; neither is a blocker for this focused simulator repair.
