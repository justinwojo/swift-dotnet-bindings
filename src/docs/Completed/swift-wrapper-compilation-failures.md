# Swift Wrapper Compilation Failures

**Status**: 55/56 passing (1 failure — SkeletonView)
**Updated**: 2026-03-28
**Target**: 56/56 (all passing)

## Current State

After the full wrapper compilation fix series (commits `424e2a16` through `3052fe09`), swift wrapper compilation improved from 49/56 to 55/56. Only SkeletonView remains failing (internal type references that can't be resolved without the library's internal dependencies).

## All Fixes Applied

| Commit | Fix | Libraries |
|--------|-----|-----------|
| `424e2a16` | MCB function name dedup, FindBlockEnd multi-line signatures, SwiftResult class extraction | Alamofire, SkeletonView (partial) |
| `a8aadd63` | MCB struct self-reconstruction (`assumingMemoryBound` for value types) | Kingfisher |
| `c6d8fff7` | EveryProtocol: missing requirement detection + `@convention(c)` typealias detection | GRDB |
| `747b522c` | @MainActor annotation gaps in MCB, GenericClosureBridge, EnumCaseWrapper emitters | StripePaymentSheet |
| `3052fe09` | XCTest platform framework search path + module/class collision, VALID_ARCHS override + resolved architecture propagation | Quick, TinyConstraints |

## Remaining Failure

### SkeletonView — Internal Type References

**Errors**: 1 wrapper compilation error from references to internal types that survive post-processor stripping.

SkeletonView has internal types that appear in public API signatures via generic parameters. The post-processor strips wrapper functions referencing internal types, but some references survive in complex generic contexts. This is a genuine limitation — the wrapper can't compile without access to the library's internal types.

**Status**: Low priority. The library works for consumers via the public API; only the EveryProtocol/wrapper compilation path is affected.
