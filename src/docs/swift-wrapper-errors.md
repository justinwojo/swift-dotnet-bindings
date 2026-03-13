# Swift Wrapper Compilation Errors

Comprehensive analysis of the 40 Swift wrapper compilation failures caught by the `validate-libraries.sh` swift compile gate. All 90 targets pass C# compilation; these are errors in the generated `.swift` wrapper that gets compiled into a `.framework` binary.

**Baseline**: 16/56 passing (34 ObjC/no wrapper), 40 failing — as of `6bf59eab`.

## Error Categories (ranked by impact)

### 1. Same-Name Module/Type Collision (11 libraries, ~600+ errors)

When a library has a public type with the same name as the module (e.g., module `Reachability` contains class `Reachability`), the wrapper uses `Reachability.Reachability` which triggers: `'Reachability' is not a member type of class 'Reachability.Reachability'`. The compiler resolves the first `Reachability` as the class, not the module.

| Library | Errors | Colliding Type |
|---------|--------|----------------|
| SVGView | 159 | struct `SVGView` |
| SwiftyBeaver | 154 | class `SwiftyBeaver` |
| Valet | 100 | class `Valet` |
| Mixpanel | 85 | class `Mixpanel` |
| FSPagerView | 34 | class `FSPagerView` |
| Reachability | 32 | class `Reachability` |
| AnimatedCollectionViewLayout | 18 | class `AnimatedCollectionViewLayout` |
| NVActivityIndicatorView | 9 | class `NVActivityIndicatorView` |
| KeychainSwift | 7 | class `KeychainSwift` |
| CryptoSwift | 4 | protocol `Updatable` (self type reference) |
| Mappedin | 2 | method `self()` collision |

**Fix**: Detect same-name collisions in the wrapper emitter and use a local typealias (`private typealias _Reachability = Reachability.Reachability`) or `import struct/class/enum Module.TypeName`. **Highest-impact fix — would fully resolve 4 libraries** (Reachability, KeychainSwift, NVActivityIndicatorView, AnimatedCollectionViewLayout).

### 2. Malformed Parameter Names (4 libraries, ~220 errors)

The wrapper emitter uses closure signatures or array type names as Swift parameter names, producing invalid syntax like `sQLSelectable]` or `element) throws -> Result`.

| Library | Errors | Pattern |
|---------|--------|---------|
| GRDB | 168 | Array parameter names contain `]` |
| Kingfisher | 30 | Closure parameter names contain `->` and `)` |
| RxSwift | 21 | Closure signatures used as parameter names |
| Starscream | 1 | Closure parameter issue |

**Fix**: Sanitize parameter names in the wrapper emitter — strip brackets, parentheses, and other type-syntax characters from identifiers before emission.

### 3. Internal/Non-Public Type References (6 libraries, ~500+ errors)

The generator emits wrapper code referencing types that exist in the ABI JSON but are `internal` (not `public`) in the framework.

| Library | Errors | Example Types |
|---------|--------|---------------|
| XMLCoder | 264 | XMLCoderElement, XMLEncoderImplementation, ChoiceBox, DateBox |
| SkeletonView | 195 | ViewAssociatedKeys, RecoverableViewState, SkeletonLayer |
| GRDB | 21 | RowKey, RowDecodingContext, Configuration |
| CryptoSwift | 12 | StreamEncryptor, StreamDecryptor, BlockEncryptor |
| Alamofire | 4 | JSONDecoder, PropertyListDecoder (typealiases) |
| RxSwift | 4 | JSONDecoder, PropertyListDecoder |

**Fix**: The post-processor already has `internalTypeNames` support. Ensure the parser correctly identifies all internal types and passes them to the post-processor. For Alamofire/RxSwift, `JSONDecoder`/`PropertyListDecoder` are likely typealiases that resolve differently than expected.

### 4. `UnsafeMutableRawPointer` → `UnsafeRawPointer` Type Mismatch (6 libraries, ~13 errors)

The wrapper emits `errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()` in `throws` wrappers, but `errorOut` is `UnsafeMutablePointer<UnsafeRawPointer?>` and `toOpaque()` returns `UnsafeMutableRawPointer`. Swift does not implicitly convert mutable to immutable raw pointers.

| Library | Errors |
|---------|--------|
| CryptoSwift | 5 |
| GRDB | 3 |
| Alamofire | 2 |
| Nuke | 1 |
| Nuke@macos | 1 |
| Nuke@tvos | 1 |

**Fix**: Change the error output parameter type to `UnsafeMutablePointer<UnsafeMutableRawPointer?>`, or add explicit cast. **Would fully fix all 3 Nuke variants.**

### 5. Actor Isolation Violations (4 libraries, ~18 errors)

The wrapper calls actor-isolated methods from a synchronous `@_cdecl` function context. Swift 6 strict concurrency rejects this.

| Library | Errors | Actor Type |
|---------|--------|------------|
| BlinkID | 7 | Custom `ProcessingActor` |
| PhoneNumberKit | 7 | `@MainActor` |
| Kingfisher | 3 | `@MainActor` |
| BlinkIDUX | 1 | Actor-isolated property |

**Fix**: Gate actor-isolated members during emission (suppress wrapper generation for actor-isolated APIs), or wrap calls in `Task { @MainActor in ... }`. **Would fully fix BlinkID and BlinkIDUX.**

### 6. `Unmanaged<NonClassType>` (4 libraries, ~16 errors)

The wrapper uses `Unmanaged.passUnretained(value)` for struct types, but `Unmanaged` requires `AnyObject` (class types).

| Library | Errors | Offending Types |
|---------|--------|-----------------|
| SkeletonView | 6 | Various structs |
| StripeConnect | 4 | `UIFont.Weight` |
| SwipeCellKit | 3 | `IndexPath` |
| Kingfisher | 3 | `PHPickerResult` |

**Fix**: For struct types in closure parameters, use pointer-based marshalling (`UnsafeMutableRawPointer.allocate` + `initializeMemory`) instead of `Unmanaged`. **Would fully fix SwipeCellKit.**

### 7. `@_spi` Protected Members (2 libraries, ~43 errors)

The generator emits wrappers for `@_spi`-annotated members that are not accessible without `@_spi(GroupName) import Module`.

| Library | Errors |
|---------|--------|
| StripeConnect | 42 |
| StripeIdentity | 1 |

**Fix**: Gate `@_spi` members during emission (check ABI JSON for SPI annotations), or add `@_spi` import to the wrapper.

### 8. `@autoclosure` Parameter Forwarding (2 libraries, ~5 errors)

Protocol proxy dispatch functions forward `@autoclosure` parameters without adding `()` to evaluate them.

| Library | Errors |
|---------|--------|
| Lottie | 4 |
| Kingfisher | 1 |

**Fix**: When emitting protocol proxy dispatch for `@autoclosure` parameters, add `()` in the forwarding call. **Would fully fix Lottie.**

### 9. Generic Constraint Propagation (GRDB, ~72 errors)

Generic wrapper functions for protocol extensions emit `<Base>` without proper conformance constraints (e.g., `where Base: Cursor`).

**Fix**: Carry forward `where` clause constraints from the original generic declarations to wrapper function signatures.

### 10. Stripe3DS2 Dependency (5 libraries, ~5 errors)

StripeCryptoOnramp, StripeIssuing, StripePayments, StripePaymentSheet, StripePaymentsUI fail because `Stripe3DS2` is a manual (non-fetchable) framework not provided via `-F` during wrapper compilation.

**Fix**: Wire Stripe3DS2 into the dependency graph for wrapper compilation, or gate members that reference unavailable dependency types.

### 11. Ambiguous Type Lookup (3 libraries, ~10 errors)

BonMot has `StringStyle` creating ambiguity between module-level and nested type references.

### 12. Missing Arguments / Wrong Signatures (3 libraries)

PhoneNumberKit has constructor calls with missing arguments (API changes or default parameters not handled). ObjectMapper has subscript calls with wrong argument labels.

### 13. Miscellaneous One-Off Issues

- `cannot find 'rotateLeft'` in CryptoSwift (4): Global function from protocol extension not accessible
- `initializer requirement 'init()' can only be satisfied by 'required' initializer` in ObjectMapper (2), Kingfisher (1)
- `@_cdecl` incompatible parameter type in CryptoSwift (2)
- `method does not override` in SVGView (12), SwiftyBeaver (10): Cascaded from same-name collision
- `Int64` to `Int` narrowing in Kingfisher (2), PhoneNumberKit (1)
- `mutating member on immutable value` in CryptoSwift (3), GRDB (2), ObjectMapper (1)

## Quick Wins: Libraries Fully Fixed by Single Fix

| Fix | Libraries Fully Resolved |
|-----|--------------------------|
| Same-name collision (#1) | Reachability, KeychainSwift, NVActivityIndicatorView, AnimatedCollectionViewLayout |
| Pointer type mismatch (#4) | Nuke, Nuke@macos, Nuke@tvos |
| Actor isolation gate (#5) | BlinkID, BlinkIDUX |
| `@autoclosure` forwarding (#8) | Lottie |
| `Unmanaged<struct>` (#6) | SwipeCellKit |

**11 libraries fixable with targeted single-category fixes → would bring passing rate from 16/56 to 27/56.**

## Priority Order for Maximum Impact

1. **Same-name collision** — 11 libraries, 4 fully fixed
2. **Malformed parameter names** — 4 libraries, reduces errors significantly in GRDB/Kingfisher/RxSwift
3. **Pointer type mismatch** — 3 Nuke variants fully fixed
4. **Actor isolation gate** — 2 libraries fully fixed
5. **Internal type gating** — 6 libraries, large error reduction
6. **`@autoclosure` forwarding** — 1 library fully fixed (Lottie)
7. **`Unmanaged<struct>`** — 1 library fully fixed (SwipeCellKit)
8. **`@_spi` gating** — reduces StripeConnect errors
9. **Generic constraints** — large error reduction in GRDB
10. **Stripe3DS2 dependency** — 5 Stripe libraries
