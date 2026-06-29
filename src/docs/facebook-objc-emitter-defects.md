# Facebook binding — remaining ObjC/Swift emitter defects (2026-06-29)

> **Status: triaged, not started.** Discovered while validating the issue-#40 graceful-degradation
> fix (ObjC Session 3 / B3) against the real Facebook iOS SDK. B3 itself is **done and proven** — see
> "What already landed" below. Everything in this doc is a **separate, pre-existing defect class**
> orthogonal to B3; it blocks `nuke BuildLibrary --library Facebook --all-products` from compiling
> clean. Filed as its own initiative rather than bundled into B3 (owner decision, 2026-06-29).

## How to reproduce

Worktree: `swift-dotnet-packages` Facebook fixture (binary mode, facebook-ios-sdk 18.1.0, minIOS 15.0;
products in dep order: `FBSDKCoreKit_Basics → FBAEMKit → FBSDKCoreKit → FBSDKLoginKit → FBSDKShareKit`).

```bash
# from swift-bindings: pack the local SDK (carries the generator) into the worktree feed
nuke pack --version <ver> --apple-version 26.2.8 --skip-apple
cp $TMPDIR/swift-nuget/SwiftBindings.{Runtime,Sdk,Templates}.<ver>.nupkg <worktree>/local-packages/
rm -rf ~/.nuget/packages/swiftbindings.*/<ver>
# clean Facebook product obj/bin to force a full regen (incremental skips the generator)
find <worktree>/libraries/Facebook -type d \( -name obj -o -name bin \) -exec rm -rf {} +
cd <worktree> && dotnet nuke BuildLibrary --library Facebook --all-products
```

The build hard-fails at **FBAEMKit (product #2)**, so FBSDKShareKit (#5) never reaches its compile pass
— but every product's `.cs` is generated under `…/<Product>/obj/Debug/net10.0-ios/swift-binding/`.
Mixed (ObjC+Swift) products emit `ApiDefinition.cs` + `StructsAndEnums.cs` + `BgenDelegates.cs` (ObjC
side) **and** `<Product>.cs` (Swift side); Swift-only products emit just `<Product>.cs`.

## What already landed (do not re-open)

- **B3 / issue #40 graceful degradation — proven on FBSDKShareKit.** Its `binding-emission-report.json`
  shows `degradedReverseDispatchReceivers: ["Sharing.shareContent setter", "SharingButton.shareContent
  setter"]` — the `any SharingContent` suppressed-proxy setters degrade to FailFast stubs instead of
  aborting the whole module, so FBSDKShareKit now binds (295 members, 122 skipped) and emits a full
  `.cs`. Before B3, `SuppressedProxyReferenceException` at those receivers killed the module.
- **ARC ownership-qualifier parser fix.** `ObjCTypeRefParser.StripObjCMacros` now strips
  `__strong`/`__weak`/`__unsafe_unretained`/`__autoreleasing` from pointer qualTypes. Without it, block
  typedef params like `NSData * __strong` left the trailing `*` unrecognized, so the pointer was never
  mapped and the literal ObjC text leaked into a C# delegate in `BgenDelegates.cs` (CS1003/CS1001).
  This is what previously blocked FBSDKCoreKit_Basics; it now emits valid C#.

## Defect classes (all pre-existing, orthogonal to B3)

### Class 1 — ObjC ApiDefinition emitter emits each type twice (structural)

Highest-volume class. The ObjC ApiDefinition emitter generates a **second** `partial interface` block
for some types, and that second block lists the type **itself** in its inheritance list.

Evidence — `FBSDKCoreKit/.../swift-binding/ApiDefinition.cs`, type `FBSDKBridgeAPIRequest`:

- `1265:  partial interface FBSDKBridgeAPIRequest : INSCopying`              ← first (correct) decl
- `4086:  partial interface FBSDKBridgeAPIRequest : INSCopying, FBSDKBridgeAPIRequest`  ← second decl
  lists itself → `CS0529` "causes a cycle in the interface hierarchy of itself"
- `4085:  error CS0579: Duplicate 'BaseType' attribute`  (the second block re-emits `[BaseType]`)
- `4101/4119:  CS0111/CS0102` — the second block re-defines members `RequestURL` / `Scheme`

Symptom totals across Core/CoreKit_Basics: **CS0102 ×18, CS0111 ×7, CS0529 ×11, CS0579** on
`FBSDKAppEventsConfiguration`, `FBSDKBridgeAPIRequest`, `FBSDKGraphRequest`, `FBSDKInternalUtility`,
`FBSDKErrorConfiguration`, `FBSDKKeychainStore`, several `*Factory` protocols, etc. Root cause: a
de-dup gap where a type reachable via two emission paths (e.g. plain interface **and** a
protocol/category/static-members path) is emitted as two full declarations, and the second
incorrectly re-lists the host type as a base. Fixing the de-dup (one declaration per type; merge
static/category members into it; never self-reference in the inheritance list) should clear all four
error codes at once.

### Class 2 — Swift-binding emitter naming/variance bugs (FBAEMKit.cs)

Two distinct bugs in the **Swift** binding output; these are what currently hard-fail the build.

- **`Handle` vs `NSObject.Handle` collision (`CS0428` ×3 + `CS0108`).**
  `FBAEMKit.cs:4013  warning CS0108: 'AEMReporter.Handle(NSUrl?)' hides inherited member
  'NSObject.Handle'` then `FBAEMKit.cs:3819/3820/3868  error CS0428: Cannot convert method group
  'Handle' to non-delegate type 'nint'`. A Swift method projected to C# `Handle(...)` shadows the
  `NSObject.Handle` (`NativeHandle`/`nint`) property on an NSObject-derived class; later code that
  reads the `Handle` property instead resolves the method group. Fix: collision-rename a projected
  member that would shadow a base-class property (the name-shaping already handles sibling
  collisions; this is the inherited-NSObject-member axis).

- **Nested `IReadOnlyDictionary` invariance (`CS0266`).**
  `FBAEMKit.cs:2926  Cannot implicitly convert IReadOnlyDictionary<string, Dictionary<string,object>>
  to IReadOnlyDictionary<string, IReadOnlyDictionary<string,object>>`. The inner dictionary value is
  projected as the concrete `Dictionary<…>` while the outer expects the `IReadOnlyDictionary<…>`
  element type. `IReadOnlyDictionary` is invariant in its value, so the element needs an explicit cast
  (cf. the "IReadOnlyDictionary invariance" architectural note — element conversions in containers need
  an explicit cast, unlike covariant `IReadOnlyList<T>`). The existing rule likely doesn't recurse into
  a dictionary-of-dictionaries value.

### Class 3 — cross-framework / system type resolution (`CS0246`)

`ApiDefinition.cs:2223  SKPaymentTransaction could not be found`; also `FBSDKAppLink` and
`ISKProductsRequestDelegate`. These are StoreKit / cross-product types the binding references but
doesn't resolve (no using/assembly reference, or a dependency product not surfaced to the consuming
compile). Likely overlaps the absent-framework-type handling (C1 / SWIFTBIND049) but on the ObjC
ApiDefinition path and across product boundaries; needs its own triage to decide drop-vs-resolve.

## Suggested sequencing

Class 1 is the dominant compile-error source and a single de-dup root — fix first; it should unblock
Core/CoreKit_Basics. Class 2's two bugs are small and self-contained (Swift-binding emitter, closest to
the current generator work). Class 3 (cross-framework resolution) is the murkiest and should be scoped
last. Each wants a faithful BindingTests reproduction (mixed-binding duplicate-emission shape;
NSObject-`Handle`-shadow shape; dictionary-of-dictionaries projection) so the fixes are permanently
covered, per the project's BindingTests-as-durable-gate policy.
