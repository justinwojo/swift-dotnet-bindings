# Pattern 2 InternalType residue inventory

Companion to `pattern2-retirement-plan.md`. Captures the post-`Pattern2InternalTypeReach`
residue in the post-processor, classified by why each strip happened.

## Headline numbers (full `nuke validate`, baseline at git_sha `e158f2a3`)

| Bucket                        |  Count  |
|-------------------------------|--------:|
| Pattern2InternalTypeReach (emission-time skips) | 234 |
| Post-processor `InternalType` strips (residue)  | 294 |
| Post-processor `NSInvocation` strips            | (separate sub-cause; not inventoried here) |
| Post-processor `Other` strips                   | (safety-net; not inventoried here) |

The 294 `InternalType` strips break down as:

| Class             | Count | What it is |
|-------------------|------:|------------|
| body-reference    |   291 | Wrapper signature is opaque pointers (`UnsafeRawPointer`); the internal type only appears in the body via `assumingMemoryBound(to: Module.X.self)` or `Unmanaged<Module.X>.fromOpaque(...)`. Legitimate residue per Findings 6 — the emission-time signature walker cannot see this. |
| header-mention    |     3 | `extension Module.InternalType: _SBW_…` blocks (XMLCoder dispatch-protocol pattern). The block's *header line* names the internal type. |
| signature-reach   |     0 | None. Confirms the new emission-time walker catches every case where the internal type appears in the C# binding's TypeSpec. |

## Per-library distribution

```
Library                                   header  sig-reach  body-ref  total
XMLCoder                                       3          0       110    113
SkeletonView                                   0          0       102    102
NVActivityIndicatorView                        0          0        57     57
StripePaymentSheet                             0          0        13     13
SwiftyBeaver                                   0          0         5      5
CryptoSwift                                    0          0         1      1
StripeCryptoOnramp                             0          0         1      1
StripePayments                                 0          0         1      1
StripeUICore                                   0          0         1      1
                                          ------     ------    ------  ------
                                               3          0       291    294
```

## body-reference — what these look like

Property getter/setter and method wrapper bodies on internal receivers. The
signature uses opaque pointers (so the emission-time walker sees only
`UnsafeRawPointer`/`UnsafeMutableRawPointer`), but the body name-binds the
internal type to dereference the receiver.

Representative samples (drawn from `$TMPDIR/binding-validation-main/`):

```swift
// XMLCoder — internal type "BoolBox"
@_cdecl("SBW_Get_XMLCoder_BoolBox_unboxed")
public func _sbw_get_unboxed_62980D19(_ self_: UnsafeRawPointer) -> Int8 {
    let obj = self_.assumingMemoryBound(to: XMLCoder.BoolBox.self).pointee
    return obj.unboxed ? 1 : 0
}
```

```swift
// SkeletonView — internal type "SkeletonLayerBuilder"
@_cdecl("SBW_Get_SkeletonView_SkeletonLayerBuilder_skeletonType")
public func _sbw_get_skeletonType_180669F5(_ resultPtr: UnsafeMutableRawPointer, _ self_: UnsafeMutableRawPointer) {
    let obj = Unmanaged<SkeletonView.SkeletonLayerBuilder>.fromOpaque(self_).takeUnretainedValue()
    let result = obj.skeletonType
    resultPtr.initializeMemory(as: Swift.Optional<SkeletonView.SkeletonType>.self, repeating: result, count: 1)
}
```

```swift
// NVActivityIndicatorView — internal type "NVActivityIndicatorAnimationAudioEqualizer"
@_cdecl("SBW_NVActivityIndicatorView_NVActivityIndicatorAnimationAudioEqualizer_setUpAnimation_F2F5A9BC")
public func _sbw_method_6037E34D(_ _in: UnsafeMutableRawPointer, _ size: CGSize, _ color: UnsafeMutableRawPointer, _ self_: UnsafeMutableRawPointer) {
    // ...
    let obj = Unmanaged<NVActivityIndicatorAnimationAudioEqualizer>.fromOpaque(self_).takeUnretainedValue()
    obj.setUpAnimation(in: _inVal, size: size, color: colorVal)
}
```

These are emitted because the C# binding includes a property/method on the
internal type, but the wrapper signature opaqueifies the receiver. Three things
can make the residue go away in a future workstream:

1. **Receiver-aware emission gate.** Extend `Pattern2InternalTypeReach` (or a
   sibling skip reason) to look at the *containing type* of any
   property/method/subscript binding, not only its TypeSpec parameters. If the
   containing type is internal, skip emission. This would zero out almost all
   291 strips.
2. **Move internal-type filtering up to the type level.** Don't emit a binding
   class at all for `@usableFromInline internal` types unless they're explicitly
   re-exported. Less precise (some types may have ABI-public surface worth
   keeping), but blunt and effective.
3. **Accept this as post-processing scope.** Per Findings 6 in
   `pattern2-retirement-plan.md`, this is exactly the case the post-processor is
   designed to backstop. If the residue stays at this level, no further work is
   strictly required.

The retirement plan defers this decision to a separate followup; this inventory
documents the cases so that followup can choose between (1)–(3) with data.

## header-mention — what these look like

Three blocks in XMLCoder. Same shape: dispatch-protocol extensions on
`XMLCoder.SharedBox` (which is internal). These come from
`GenericClosureBridgeEmitter`'s private `_SBW_<hash>` dispatch protocol pattern.

```swift
extension XMLCoder.SharedBox: _SBW_GSPG_88DBC5BE {
    static func _sbw_get_88DBC5BE(resultPtr: UnsafeMutableRawPointer, selfPtr: UnsafeMutableRawPointer) {
        let obj = Unmanaged<AnyObject>.fromOpaque(selfPtr).takeUnretainedValue() as! Self
        let result = obj.unboxed
        resultPtr.initializeMemory(as: Unboxed.self, repeating: result, count: 1)
        // ...
    }
}

extension XMLCoder.SharedBox: _SBW_PG_86F4050C {}
```

These differ from body-reference because the *extension header itself* names
the internal type. They aren't picked up by the new walker because the walker
inspects member-binding TypeSpecs, not the extension target. The dispatch
protocol approach is itself a workaround for generics, so a fix here is
entangled with that approach.

Three instances total — too small to justify a dedicated emission gate. Tracking
in the retirement followup as the same workstream as body-reference.

## signature-reach — none

Zero. The `Pattern2InternalTypeReach` walker catches every case where an
internal type appears in the binding's TypeSpec signature. This is the success
criterion for Sessions 1–2: the dominant signature-reach case is fully gated at
emission time.

## Out of scope here

- `NSInvocation` sub-cause: tracked separately in the post-processor; needs its
  own short audit before retirement (see Finding 7 in the retirement plan).
- `Other` sub-cause: safety-net catches (`EveryProtocol()` placeholder,
  `.load(as: @escaping)`). They shouldn't fire in normal operation. The
  aggregate validation guard in `Build.Validation.cs` warns on any increase
  for this bucket; only the `.load(as: @escaping)` case additionally invokes
  the per-block `onSafetyNetWarning` callback at strip time, so don't rely on
  the callback alone to surface a regression.
- Full Pattern 2 retirement: still deferred per the retirement plan.

## Reproducing this inventory

The post-processor only counts strips, it doesn't emit per-block diagnostics.
For inventory purposes this doc was produced by re-walking each library's
on-disk pre-strip `<Module>.Wrapper.swift` against the corresponding
`wrapper-context.json` `internalTypeNames` array, mirroring the strip patterns
in `SwiftWrapperPostProcessor.Process`. Inventory script lives in `/tmp/` and
is not checked in — the per-library counts above are the durable record. If
this inventory ever needs to be redone, add a `IReadOnlyList<StripSample>`
diagnostic field to `PostProcessingResult` rather than reimplementing the strip
walk in another language.
