# Swift Wrapper Compilation Errors

Comprehensive analysis of Swift wrapper compilation failures caught by `validate-libraries.sh`. All 90 targets pass C# compilation; these are errors in the generated `.swift` wrapper that gets compiled into a `.framework` binary.

**Baseline**: 27/56 passing (34 ObjC/no wrapper), 29 failing.

**Previous baselines**: 24/56 (session 4), 21/56 (session 3), 16/56 (`6bf59eab`).

## Session History

**Session 5** (4 libraries improved: StripeIdentity, RxSwift, AMPopTip, Swinject):
- `spi_group_names` ABI JSON field detection → StripeIdentity passes; StripeConnect 51 → 2 errors
- `IsModuleInternal` gating in `MethodWrapperEmitter`, `WrapperValidation`, `ConstructorWrapperEmitter`, and optional-pointer/closure wrapper paths → internal method/constructor wrappers no longer emitted
- `@usableFromInline internal` and `@inlinable` free functions filtered from module-level emission
- `_`-prefixed method/property suppression: methods without explicit `AccessControl` attribute treated as internal
- `SBW_Utf8Slice` dedup fix: emission context properly threaded (eliminates Kingfisher duplicate declaration)
- Variadic expansion pattern detection: constructors with N unnamed protocol params + trailing Array skipped
- Raw generic param (`τ_0_0`) gate added to optional-pointer wrapper emission → AMPopTip passes
- `inout` parameter support in EveryProtocol conformance and WitnessDispatch emission (with caller buffer writeback) → Swinject passes

**Session 4** (4 libraries improved: Lottie, BlinkID, BlinkIDUX, SwipeCellKit):
- `@autoclosure` parameter detection from `.swiftinterface`
- Custom actor isolation gating (`@ProcessingActor`)
- ObjC-bridged struct `as AnyObject` in closure params
- Module/type name collision post-processor

**Session 3** (6 libraries improved: SnapKit, KeychainAccess, Starscream, Nuke x3):
- Post-processor extension self fix, dictionary enum fix, malformed parameter names

## Current Error Categories (post-processed, ranked by impact)

Error counts below reflect what remains **after** post-processor stripping. The post-processor already eliminates hundreds of blocks referencing internal types, raw generic params, and EveryProtocol patterns.

### 1. Protocol Extension Associated Type Leaks (GRDB, ~262 errors)

Protocol extension wrappers use bare `Element`, `Base` types outside their protocol context. Accounts for 81% of GRDB's errors.

**Fix**: Carry forward generic constraints and resolve associated types in protocol extension wrapper signatures. Architectural — requires type graph changes.

### 2. EveryProtocol Conformance Gaps (multiple libraries)

EveryProtocol can't satisfy all protocol requirements:
- Associated type / static Self requirements (Alamofire, GRDB, Kingfisher)
- NSObjectProtocol inheritance (SkeletonView — UICollectionViewDataSource/UITableViewDataSource)
- Missing inherited protocol methods

**Fix**: Detect unsatisfiable protocol requirements and skip EveryProtocol conformance for those protocols.

### 3. `.swiftinterface` Import Failures (4 libraries)

Reachability, KeychainSwift, NVActivityIndicatorView, AnimatedCollectionViewLayout — module/type name collision resolved by post-processor, but `swiftc -emit-library` fails on self-referential `.swiftinterface` imports. **Swift compiler limitation, not fixable from generated code.**

### 4. Struct Treated as Class — `Unmanaged<ValueType>` (3 libraries, ~8 errors)

| Library | Errors | Types |
|---------|--------|-------|
| StripeConnect | 2 | `UIFont.Weight` |
| Kingfisher | 3 | `PHPickerResult` |
| SkeletonView | 2 | `TimeInterval` (Double) |

**Fix**: Use pointer-based marshalling for value types instead of `Unmanaged`.

### 5. Stripe3DS2 Dependency (5 libraries)

StripeCryptoOnramp, StripeIssuing, StripePayments, StripePaymentSheet, StripePaymentsUI fail because `Stripe3DS2` is not provided via `-F` during wrapper compilation.

**Fix**: Wire Stripe3DS2 into the dependency graph.

### 6. Internal Member Access (CryptoSwift, SkeletonView, XMLCoder)

Remaining internal members not caught by `IsModuleInternal` or `_`-prefix suppression:
- CryptoSwift: protocol composition `.self` metatype (4), `@_cdecl`-incompatible types (2)
- SkeletonView: internal singletons/initializers (4)
- XMLCoder: internal instance methods on public types (`isEmpty`, `toXML`) (4), malformed `_optbuf` block (1)

**Fix**: Cross-reference with `.swiftinterface` for member-level access control verification.

### 7. Remaining Single-Library Issues

| Library | Errors | Category |
|---------|--------|----------|
| Alamofire | 2 | SecTrust type projection + ambiguous `encode` overload |
| PhoneNumberKit | ~10 | `@MainActor` isolation + missing constructor args |
| Kingfisher | ~8 | `@MainActor` (1), `@autoclosure` (1), `UInt64→UInt` (2), struct-as-class (3), `ContentMode` ambiguity (1) |
| ObjectMapper | ~3 | Wrong subscript labels + `required init` |
| Parchment | ~4 | Incomplete EveryProtocol + wrong arg labels + `@MainActor` |
| BonMot | ~10 | Ambiguous type lookup (`StringStyle`) |
| Quick | N/A | XCTest dependency — inherently unsupported (test framework) |
| TinyConstraints | N/A | x86_64-only xcframework — stale build, not a generator bug |

## Libraries Fixed by Session

| Session | Libraries | Count |
|---------|-----------|-------|
| 3 | SnapKit, KeychainAccess, Starscream, Nuke, Nuke@macos, Nuke@tvos | 6 |
| 4 | Lottie, BlinkID, BlinkIDUX, SwipeCellKit | 4 |
| 5 | StripeIdentity, RxSwift, AMPopTip, Swinject | 4 |
| Pre-existing | BRLMPrinterKit, MicroblinkPlatform, SmartCardIO, SwiftyGif, DifferenceKit, CocoaLumberjackSwift, DeviceKit, Stripe*, StripeCore, StripeApplePay, StripeCameraCore, StripeCardScan, StripeFinancialConnections, StripeUICore | 13 |
