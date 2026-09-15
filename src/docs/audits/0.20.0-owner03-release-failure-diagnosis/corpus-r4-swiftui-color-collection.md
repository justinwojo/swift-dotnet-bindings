# OWNER-03 corpus-r4 swiftui-charts Color collection repair

Date: 2026-09-15

Base: `acf95718a573425ad534f2a3399a68720b77a208`

Runtime: macOS (`Darwin`)

Disposition: **the selected `swiftui-charts` generator root is repaired and
validated; the historical corpus-r4 publication verdict remains BLOCKED**

This is a bounded diagnosis and repair receipt, not a corpus rerun and not a
waiver. The preserved corpus under
`/Users/wojo/.Trash/swift-bindings-corpus-q-r4-stale-20260914` was used strictly
read-only and was never moved, restored, modified, or deleted.

## Authoritative failure and preserved evidence

The authoritative `swiftui-charts` result identifies version `1.1.0`, product
and module `Charts`, successful generation, publication authorization, and a
failed managed compile. The sole error was:

```text
Charts.Types.StackedAreaChartStyle.cs(153,89): error CS1503:
Argument 1: cannot convert from
'System.Collections.Generic.IEnumerable<SwiftUI.Color>' to
'System.Collections.Generic.IEnumerable<SwiftUI.Color.Buffer>'
```

| Evidence | SHA-256 |
|---|---|
| authoritative `.../library-artifacts/swiftui-charts/result.json` | `0497851ff616abc9ac79d9d2b3bfb9a62cf206d7b9a9913f06e1595f0ea3f7c8` |
| authoritative `.../library-artifacts/swiftui-charts/Charts/compile.log` | `5da4900f61cacd9a5b4b844ac9133feaecc0d3a3bf51a2e446d3c80e065ce59d` |
| preserved `Charts.Types.StackedAreaChartStyle.cs` | `11bb145f6862326fcc689a43f9e719120f4c0c9245bc44e5b1d7d197d7eee6ee` |
| preserved arm64 public `Charts.swiftinterface` | `074d8c765f7a9d3d61e425503e45bae212d46c6d0dc40868fdf32b899e361a69` |
| preserved arm64 `Charts` ABI JSON | `c02413937bfa504067606fdfa396d638c8e14b0c75c14880a80a0ad1eeda35b6` |

The authoritative files are rooted at
`/Users/wojo/Dev/swift-bindings/src/docs/sessions/0.20.0/execution/P8/corpus-r4/corpus-q-r4/receipts`.
The three preserved files are respectively rooted at
`sweep/output/swiftui-charts/Charts`,
`sweep/xcframeworks/swiftui-charts/Charts.xcframework/ios-arm64/Charts.framework/Modules/Charts.swiftmodule`,
and
`sweep/output/swiftui-charts/Charts/pack-staging/ios-arm64/Charts.xcframework/ios-arm64/Charts.framework/Modules/Charts.swiftmodule`
under the read-only Trash corpus.

The exact Swift declaration is:

```swift
public init(_ lineType: Charts.LineType = .quadCurve,
            colors: [SwiftUICore.Color] = [.red, .orange, .yellow,
                                            .green, .blue, .purple])
```

The ABI JSON independently describes the second parameter as printed
`[SwiftUI.Color]`, with element `SwiftUI.Color`, and freezes the constructor
symbol as
`$s6Charts21StackedAreaChartStyleV_6colorsAcA8LineTypeO_Say7SwiftUI5ColorVGtcfc`.
The preserved generated public C# signature was correctly
`StackedAreaChartStyle(LineType, IEnumerable<SwiftUI.Color>)`, but its body
incorrectly called:

```csharp
SwiftArray<SwiftUI.Color.Buffer>.FromEnumerable(colors)
```

## Root cause and ABI/ownership contract

`FrozenWithMemoryProjection` conflated two distinct carriers. A bare frozen,
reference-bearing Swift value crosses its direct P/Invoke boundary through its
lowered nested `.Buffer`; therefore `ContainerTypeName`/`PInvokeType` correctly
remain `SwiftUI.Color.Buffer`. A Swift generic container, however, is
instantiated with the metadata-bearing Swift type itself. Its managed generic
must therefore be `SwiftArray<SwiftUI.Color>`, not
`SwiftArray<SwiftUI.Color.Buffer>`.

This is both a C# type contract and an ABI/ownership contract. Runtime
`SwiftArray<T>.FromEnumerable(IEnumerable<T>)` obtains metadata for `T`, and its
append/write paths call `SwiftMarshal.MarshalToSwift(item, ...)`. Runtime
`SwiftUI.Color` implements `ISwiftObject.MarshalToSwift`, copying the value via
Color metadata/value witnesses. A raw nested Buffer is only storage layout; it
does not provide the Color metadata or wrapper-owned value lifecycle. At the
initial generator repair, the relevant runtime contract evidence hashes were:

| Runtime contract file | Initial SHA-256 (before the ownership follow-up below) |
|---|---|
| `src/Swift.Runtime/src/Swift/SwiftUI/Color.cs` | `1c2f6f5c883b72d615f46e673ef4a08eba58c606b5678fff9e115e9cce4fc795` |
| `src/Swift.Runtime/src/Swift/SwiftArray.cs` | `3105223c79c4f9a419fc538dbeb406f7f534bf4db2e3e924edcb83d7b10dc2f9` |
| `src/Swift.Runtime/src/Swift/SwiftUIDatabase.xml` | `8820d6fa620f8e03320b79e17b2ec44187db2bd1ef9dcef165768cf88d557cff` |

The upstream fix changes only
`FrozenWithMemoryProjection.SwiftContainerGenericType` to the metadata-bearing
wrapper name. This shared property feeds Array, Set, Dictionary, Result and
reverse-receiver generic shapes. Direct bare-value lowering still uses
`.Buffer`. Optional already deliberately used the metadata-bearing
`MarshalFromSwiftType`; adjacent comments were corrected so they no longer
encode the defective asymmetry.

The adjacent bounded inspection found the same invalid generic argument could
reach Set, Dictionary and nested Result shapes. Unit coverage now pins all four
generic families. No distinct downstream patch, Charts signature guess, skip,
waiver, or timeout change was introduced.

## Accepted ownership follow-up (focused Grok High/Medium)

The accepted High finding exposed a second, runtime-side consequence of making
metadata-bearing hand-written wrappers legal generic elements. Swift's
non-mutating Array subscript initializes an **owned** element in the indirect
result slot. The old `SwiftArray` indexer and `ExtractRange` recognized true
Swift classes and non-POD `ISwiftStruct` wrappers, but `SwiftUI.Color` is neither:
it is a managed reference type implementing `ISwiftObject`, with Swift metadata
kind `Struct`, `Adopt` payload construction, and no `ISwiftStruct`. It therefore
fell through to `MarshalFromSwift`, adopted the temporary allocation, and then
the Array reader freed that allocation raw. The returned Color immediately
referenced freed storage; later disposal could destroy/free it again.

Both Array read paths now share `ReadOwnedElement`, which delegates the complete
owned-slot carrier table to the established
`SwiftMarshal.MarshalMovedValueFromSlot<T>` seam already used by Set and
Dictionary:

- a true class slot is dereferenced and its `+1` transfers to the wrapper; the
  temporary allocation is raw-freed, with no VWT destroy;
- an Adopt/Copy non-POD wrapper (including bare SwiftUI projections) is copied
  independently with `InitializeWithCopy`, then the source slot is VWT-destroyed
  only after successful construction, and the temporary is raw-freed;
- an Adopt POD wrapper receives independent storage, while the POD source needs
  no destroy before raw free;
- Move/Inline carriers, primitives, direct bare values and existential
  containers transfer/read their value without a source destroy, then the
  temporary storage is raw-freed;
- after the native getter initializes the slot it is marked live. If marshaling
  throws before consuming it, `finally` VWT-destroys the intact slot and always
  frees the allocation. A successful helper call clears that live flag, so no
  second destroy occurs.

`ExtractRange` holds the Array SafeHandle once and calls the same helper for each
element, eliminating its divergent classifier. A whole-runtime search of direct
`MarshalFromSwift<T>` slot reads found one bounded sibling: `SwiftClosedRange`
returned its borrowed interior Bound slots through the adopting entry point.
Both bounds now use `MarshalCopiedValueFromSlot<T>`, which creates an independent
projection while leaving the range-owned slots intact. Optional, Result,
AsyncStream, Set, Dictionary, callback and existential paths already route
through the appropriate copied/moved ownership helpers; no other coherent
instance remained in the bounded search.

The accepted Medium finding is covered by a Swift `[SwiftUI.Color]` return on the
existing probe. Its C# test reads the generated owning projection by index and
enumeration, directly exercises `SwiftArray<Color>.ToArray()`/`ExtractRange`,
disposes every source owner before validation, then checks count, order, Swift
value equality, and deterministic disposal of all returned Colors. Synthetic
runtime tests model a bare, non-POD, Adopt `ISwiftObject` and pin both owned-slot
copy-then-consume and borrowed-slot copy-with-source-intact behavior, including
VWT copy/destroy counts and independent storage.

Current follow-up source/evidence hashes are:

| Path | SHA-256 |
|---|---|
| `src/Swift.Runtime/src/Swift/SwiftArray.cs` | `cb1a9f2e1a4bd36c961aa9f7d806bfca4ff5e569873137a13c4dde660ad7e925` |
| `src/Swift.Runtime/src/Swift/SwiftClosedRange.cs` | `05eab0b10d5ebab3d5e4b26fe5f94e1efe8f193534ce9eff3240135955753a0c` |
| `src/Swift.Runtime/tests/MetadataTests/MovedSlotPodAdoptTests.cs` | `35fea00d03ec9d2dfc06b6e6dbdcb0d217b738d58506d8fd135923758ed8ed1f` |
| `BindingTests/Sources/SwiftBindingsTestLib/SwiftUI/ValueTypeParameters.swift` | `ffd9e1f52146035d36d0fbdd914a0e0031f473a5d06814ae4d2de4d62608beec` |
| `BindingTests/RuntimeTestsApp/Types/SwiftUIValueRoundTripTests.cs` | `cef1628723c43c5c70062315ccfe121ff7c083d9af277970a9cad38f337230d1` |
| regenerated `SwiftBindingsTestLib.Types.SwiftUIColorCollectionProbe.cs` (ignored validation output) | `8b5d8ddfba4330afa7ad6c0cfa0a2658d7aca3889a7d138e3d7f00bfc29eb813` |

## Regression coverage and validation

The unit reproduction constructs the exact `SwiftUI.Color` projection and pins
both the public enumerable and the generated
`SwiftArray<SwiftUI.Color>.FromEnumerable(colors)` statement. The BindingTests
fixture mirrors the third-party constructor shape: a defaulted leading value
followed by a defaulted `[SwiftUI.Color]`. Its simulator test constructs two
managed Colors, passes them as the enumerable, and checks Swift-observed count,
order and value equality.

The refreshed generated probe retained
`IEnumerable<SwiftUI.Color>` and emitted
`SwiftArray<SwiftUI.Color>.FromEnumerable(colors)`. Its generated C# SHA-256 is
`e0cdecf3cc7dbb6279010a1f94e567e143ef26138bc325b863a66e552b2437c4`.

| Gate | Result | Log SHA-256 |
|---|---|---|
| `dotnet test ... --filter "FullyQualifiedName~ComplexProjectionTests\|FullyQualifiedName~TypeProjectionFactoryTests" --no-restore` | PASS: 183/183 | `/private/tmp/owner03-swiftui-color-collection-focused-unit.log` — `81983f388449ec9cd679c809a375403892a8f1ccb41a503965676e7f7c1e4930` |
| bounded projection/receiver unit filter | PASS: 513 passed, one existing known skip | `/private/tmp/owner03-swiftui-color-collection-projection-unit.log` — `c29afb67a73e633b7f53e211fab81df8cb948c8ea0fbcbf810dd71ffa59c7f3b` |
| explicit Debug generator refresh | PASS: zero warnings/errors | `/private/tmp/owner03-swiftui-color-collection-generator-build.log` — `341a5284733639799feba5e63a5b8a4c618a198349698dc00820e8b680ac325d` |
| `nuke binding-tests --compile-only` | PASS in 4:45: generated C#, Swift wrapper/bridge, parity, manifest and bounded resilience/ingestion gates green; 1,708 C# files generated | `/private/tmp/owner03-swiftui-color-collection-binding-compile.log` — `e5bdde50a22237a7edc98a30b2b0b807875065fdfd5749bd59f8763978c2eb62` |
| `nuke binding-tests --skip-regen --class-filter SwiftUIValueRoundTripTests` | PASS: 10/10 simulator tests, including the new Color-array ABI test | `/private/tmp/owner03-swiftui-color-collection-binding-runtime.log` — `8bd2f221947d4bd9197d791cfeb91e293c75be6279390e96cdf5efb0b45a1baa` |

Accepted-finding follow-up validation (after the runtime ownership repair):

| Gate | Result | Log SHA-256 |
|---|---|---|
| `dotnet test src/Swift.Runtime/tests/Swift.Runtime.Tests.csproj --filter "FullyQualifiedName~MovedSlotPodAdoptTests" --no-restore` | PASS: 4/4; bare Adopt owned/borrowed contracts and prior POD cases | `/private/tmp/owner03-swiftui-color-collection-followup-helper-unit.log` — `5a22b84192a7d2f983656759abcc755e227682214d61a5f6b59daece8183dd66` |
| `dotnet test src/Swift.Runtime/tests/Swift.Runtime.Tests.csproj --filter "FullyQualifiedName~BindingsGeneration.Tests.SwiftArrayTests" --no-restore` | PASS: 46/46 | `/private/tmp/owner03-swiftui-color-collection-followup-array-unit.log` — `8b737f19486cbdc3296b7731fc521271313e2df3b3bc3cd84c4de9d5725f1df8` |
| `nuke binding-tests --compile-only` | PASS in 4:44; generated C# compiled with zero errors and the bounded wrapper/parity/manifest/kitchen gates passed | `/private/tmp/owner03-swiftui-color-collection-followup-compile.log` — `ae2a3f342ac149cdbd85189c54394b920c905b907d3810c6db9c9a352e04f4a4` |
| `nuke binding-tests --skip-regen --class-filter SwiftUIValueRoundTripTests` | PASS: 11/11 simulator tests; new Color return/index/enumeration/ExtractRange/lifetime test passed | `/private/tmp/owner03-swiftui-color-collection-followup-runtime.log` — `2a072a1ae0e8aa9c9d29ebc37bb1cf45fca9e1b9f88fa920319daf4436dec46d` |

No full corpus, `nuke validate`, full unit suite, or device lane was run. A
single-library corpus canary was not generated: the exact-shape fixture exercised
the repaired production generator, generated-C# compile, wrapper/bridge compile,
and live simulator ABI/ownership path without allocating another corpus tree.

## Capacity and remaining blockers

Exact free-space observations (`df -k`, 1024-byte blocks):

- start: `16,083,604 KiB`;
- immediately before BindingTests: `16,073,660 KiB`;
- during the compile gate: `16,062,756 KiB`;
- after the simulator app build: `11,945,540 KiB`, temporarily below the binary
  12 GiB reserve (`12,582,912 KiB`);
- after stopping further validation and deleting only generated
  `BindingTests/RuntimeTestsApp/bin` and `obj`: `15,514,056 KiB`.

The transient shortfall is recorded, not hidden. It occurred only after the
requested narrow runtime gate had completed successfully. No source, receipt,
authoritative evidence, or preserved corpus content was deleted.

For the accepted-finding follow-up, the minimum observed free space before and
during its gates was `15,490,984 KiB`, above the binary 12 GiB reserve. Before
the new simulator gate, only reproducible task outputs were pruned (the prior
runtime-attempt directory, regenerated symbol graph/parser build, bounded
kitchen artifacts and test binaries), and the prior generated simulator app was
uninstalled. Free space was `16,636,904 KiB` immediately before that gate,
`20,763,328 KiB` immediately after it (APFS reclaimed previously deleted blocks
asynchronously), and `23,779,536 KiB` after deleting the newly generated app
`bin`/`obj`, attempt directory, and simulator install. The stale Trash corpus
was never changed. No full corpus, full validation, device lane, skip, waiver,
timeout widening, or special-case gate was used.

The frozen corpus-r4 authority remains 120 candidates: 84 available, 60
successful, 24 nonzero, and 36 absent. This batch locally repairs the selected
`swiftui-charts` row; the preceding `corpus-r4-swiftsoup-arrayslice.md` receipt
locally repairs the SwiftSoup row. The other 22 historical nonzero candidates
remain unrequalified here: Euclid, SwiftDraw, epoxy-ios, Auth0.swift, SwiftOTP,
CoreStore, SwiftLocation, SwiftRichString, Time, ReactorKit,
swift-dependencies, swift-identified-collections, Moya, SocketIO,
swift-protobuf, Yams, analytics-swift, RevenueCat, TelemetryDeck,
swift-clocks, swift-numerics, and swift-system. The exact 36 absent names and
their fixed-input caveat remain in `corpus-r4-swiftsoup-arrayslice.md`.

Only a future authoritative corpus rerun can change those published outcomes.
The current host's 12 GiB reserve and the 36 unavailable fixed inputs remain
independent rerun blockers; neither changes this repaired generator diagnosis.
