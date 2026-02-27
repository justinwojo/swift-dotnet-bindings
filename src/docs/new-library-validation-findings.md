# New Library Validation Findings

**Date:** 2026-02-26
**Libraries tested:** 21 (all new, not in the existing 32-target baseline)
**Results:** 9 pass, 11 fail, 1 no output
**Distinct bugs found:** 10

## Overview

To stress-test the binding generator beyond the existing 32 validation targets, we sourced 21 new Swift libraries across diverse categories — device utilities, logging, networking, UI components, testing, serialization, animation, security, and diffing. Every failure exposed a distinct generator bug.

### Key Learnings on Library Buildability

Most popular Swift packages **cannot** be built as xcframeworks with `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`. Apple's own SPM packages (swift-collections, swift-algorithms, swift-numerics, swift-log, swift-crypto, swift-protobuf, swift-argument-parser) all fail because they use internal types incompatible with library evolution mode. Libraries with `.xcodeproj` files have a much higher success rate.

**Failed to build as xcframework (not generator bugs):**
- swift-collections, swift-algorithms, swift-numerics, swift-log, swift-crypto, swift-protobuf, swift-argument-parser (Apple — library evolution incompatible)
- OpenCombine, Then, SwiftSoup (dependency on swift-atomics or similar)
- SwiftMessages, SwiftEntryKit (swiftinterface typecheck failures)
- Tabman, SwiftDate (build script / compilation failures)
- Nimble, WhatsNewKit (linker failures)
- Eureka (multiple xcodeproj files, ambiguous)
- ViewAnimator (signing requirement)
- Cosmos (Swift 3.0 version mismatch)

---

## Passing Libraries (9)

| Library | Lines | Category | Notes |
|---------|-------|----------|-------|
| [DeviceKit](https://github.com/devicekit/DeviceKit) 5.7.0 | 7,150 | Device/enum | Massive enum with hundreds of device cases |
| [SwiftyBeaver](https://github.com/SwiftyBeaver/SwiftyBeaver) 2.1.1 | 10,976 | Logging | Class hierarchy with virtual dispatch |
| [Reachability](https://github.com/ashleymills/Reachability.swift) v5.2.4 | 1,353 | Networking | Single class with closures and enums |
| [KeychainSwift](https://github.com/evgenyneu/keychain-swift) 9.0.2 | 1,418 | Security | Clean keychain wrapper API |
| [FSPagerView](https://github.com/WenchaoD/FSPagerView) 0.8.3 | 4,869 | UI pager | UICollectionView-based pager |
| [SwiftyGif](https://github.com/kirualex/SwiftyGif) 5.4.5 | 1,175 | Image/GIF | GIF handling extensions |
| [Quick](https://github.com/Quick/Quick) v7.6.2 | 4,441 | Testing | BDD framework with closure DSL |
| [DifferenceKit](https://github.com/ra1028/DifferenceKit) 1.3.0 | 4,545 | Diffing | Generic diffing algorithms |
| [TinyConstraints](https://github.com/roberthein/TinyConstraints) 4.0.2 | 1,804 | Layout | Auto Layout extension methods |

---

## Failing Libraries (11) — Bug Catalog

### Bug 1: Emoji Identifiers Not Sanitized

**Library:** [Valet](https://github.com/square/Valet) 5.1.0 (24 errors, 6,562 lines)

**Symptom:** C# compiler errors on emoji characters in identifiers.
```
error CS1056: Unexpected character '🚫'
error CS1519: Invalid token '🚫' in a member declaration
```

**Root cause:** Valet uses `🚫` in Swift enum case names or identifiers. The generator passes these through to C# without sanitizing non-ASCII/non-identifier characters.

**Fix area:** Identifier sanitization in `SanitizeForCSharp` or equivalent — strip or replace emoji/non-ASCII chars with descriptive names or underscore sequences.

---

### Bug 2: Backtick Identifiers Leaking to C#

**Library:** [BonMot](https://github.com/Rightpoint/BonMot) 6.1.3 (247 errors, 16,113 lines)

**Symptom:** Backtick characters appear in C# identifiers.
```
error CS1056: Unexpected character '`'
error CS1519: Invalid token '`' in a member declaration
```

**Root cause:** Swift uses backticks for escaping keywords as identifiers (e.g., `` `default` ``). The ABI JSON or swiftinterface preserves these backticks, and the generator emits them into C# where they're invalid. C# uses `@` for keyword escaping, not backticks.

**Fix area:** Strip backticks from Swift identifiers during parsing or emission. If the underlying name is a C# keyword, apply the existing `@` prefix logic.

---

### Bug 3: Generic Type Reference Without Type Arguments

**Library:** [Swinject](https://github.com/Swinject/Swinject) v2.10.0 (2 errors, 4,929 lines)

**Symptom:**
```
error CS0305: Using the generic type 'ServiceEntry<TService>' requires 1 type arguments
```

**Root cause:** The generator references `ServiceEntry` as a return type or parameter type without supplying its generic argument `<TService>`. This likely occurs in a method that returns or accepts the raw generic type — the generator needs to propagate generic parameters through the type reference.

**Fix area:** Type resolution in MethodHandler or PropertyHandler — ensure generic types always carry their type arguments when emitted as C# type references.

---

### Bug 4: Optional on UIKit Value-Type Enums

**Libraries:**
- [PhoneNumberKit](https://github.com/marmelroy/PhoneNumberKit) 4.2.7 (2 errors, 15,502 lines)
- [AMPopTip](https://github.com/andreamazz/AMPopTip) 4.7.0 (2 errors, 5,216 lines)
- [AnimatedCollectionViewLayout](https://github.com/KelvinJin/AnimatedCollectionViewLayout) 1.1.0 (2 errors, 2,928 lines)

**Symptom:**
```
error CS0023: Operator '?' cannot be applied to operand of type 'UITableViewStyle'
error CS0023: Operator '?' cannot be applied to operand of type 'UITextFieldDidEndEditingReason'
error CS0023: Operator '?' cannot be applied to operand of type 'UISwipeGestureRecognizerDirection'
error CS0023: Operator '?' cannot be applied to operand of type 'UICollectionViewScrollDirection'
```

**Root cause:** These UIKit enum types (`UITableViewStyle`, `UITextFieldDidEndEditingReason`, `UISwipeGestureRecognizerDirection`, `UICollectionViewScrollDirection`) are C# value types (structs), not reference types. The generator emits `T?` (nullable reference) syntax, but value types need `Nullable<T>` or `T?` only works if `T` is known to be a value type at compile time.

The deeper issue is that these types are in the UIKit type database as ObjC-bridged types but are mapped to .NET value types (enums/structs), not classes. The `Optional<T>` handling assumes reference-type semantics.

**Fix area:** UIKit type database entries for these enum types need a `isValueType` flag, and the Optional emission path needs to use `Nullable<T>` for value types.

**Types to add/fix in UIKitDatabase.xml:**
- `UITableViewStyle`
- `UITextFieldDidEndEditingReason`
- `UISwipeGestureRecognizerDirection`
- `UICollectionViewScrollDirection`

---

### Bug 5: GetNSObject with Non-NSObject UIKit Enum

**Libraries:** AMPopTip, AnimatedCollectionViewLayout (same libraries as Bug 4)

**Symptom:**
```
error CS0315: The type 'UIKit.UISwipeGestureRecognizerDirection' cannot be used as type parameter 'T'
in the generic type or method 'Runtime.GetNSObject<T>(nint)'.
There is no boxing conversion from 'UIKit.UISwipeGestureRecognizerDirection' to 'Foundation.NSObject'.
```

**Root cause:** Same underlying issue as Bug 4. UIKit enum types are treated as ObjC reference types and marshalled via `Runtime.GetNSObject<T>()`, which requires `T : NSObject`. These are actually integer-backed enums in .NET, not NSObject subclasses.

**Fix area:** Same as Bug 4 — fix the type database classification so these types use integer marshalling instead of NSObject marshalling.

---

### Bug 6: Missing CoreAnimation Type

**Library:** [NVActivityIndicatorView](https://github.com/ninjaprox/NVActivityIndicatorView) 5.2.0 (1 error, 8,786 lines)

**Symptom:**
```
error CS0234: The type or namespace name 'CAKeyframeAnimation' does not exist
in the namespace 'CoreAnimation'
```

**Root cause:** `CAKeyframeAnimation` is referenced in the generated bindings but isn't registered in the type database. The generator emits `CoreAnimation.CAKeyframeAnimation` but .NET iOS may expose it under a different namespace or name, or it simply needs to be added to the Apple framework type database.

**Fix area:** Add `CAKeyframeAnimation` entry to the appropriate Apple framework type database (likely `CoreAnimationDatabase.xml` or equivalent). Verify the .NET iOS namespace and class name match.

---

### Bug 7: CGFloat Not Resolved

**Library:** [SwipeCellKit](https://github.com/SwipeCellKit/SwipeCellKit) 2.7.1 (4 errors, 8,767 lines)

**Symptom:**
```
error CS0246: The type or namespace name 'CGFloat' could not be found
```

**Root cause:** Swift's `CGFloat` should be mapped to .NET's `NFloat` (or `nfloat`). The generator is emitting raw `CGFloat` without translation.

**Fix area:** Type mapping — `CoreGraphics.CGFloat` should map to `System.Runtime.InteropServices.NFloat` or the platform-appropriate type. May need an entry in `AppleFrameworkValueTypes` or a dedicated mapping.

---

### Bug 8: Wrong UIKit Type Name

**Library:** SwipeCellKit (same as Bug 7)

**Symptom:**
```
error CS0234: The type or namespace name 'UITableViewCellCellStyle' does not exist
in the namespace 'UIKit'
```

**Root cause:** The generator produces `UITableViewCellCellStyle` but the correct .NET iOS type name is `UITableViewCellStyle`. This is likely a nested-type naming issue — the ABI JSON represents it as `UITableViewCell.CellStyle` and the flattening logic incorrectly concatenates both parts.

**Fix area:** ObjC nested type name flattening in the type database or name resolver. The `CellStyle` nested enum inside `UITableViewCell` should flatten to `UITableViewCellStyle`, not `UITableViewCellCellStyle`.

---

### Bug 9: Default Parameter Overload Collision

**Libraries:**
- [ObjectMapper](https://github.com/tristanhimmelman/ObjectMapper) 4.4.3 (1 error, 5,361 lines)
- [Parchment](https://github.com/rechsteiner/Parchment) v4.1.0 (1 error, 19,044 lines)

**Symptom:**
```
error CS0111: Type 'Mapper<N>' already defines a member called 'ToJSONString'
with the same parameter types

error CS0111: Type 'PageBuilder' already defines a member called 'BuildExpression'
with the same parameter types
```

**Root cause:** When the generator creates overloads by omitting default parameters, two different Swift method signatures can collapse to the same C# signature. The dedup logic (`GetProjectedOverloadKey` / `GetProjectedCSharpMethodKey`) doesn't account for this collision.

**Fix area:** `DefaultParameterOverloadEmitter.GetProjectedOverloadKey` — add collision detection that checks the final projected signature against already-emitted methods. Skip the overload if it would duplicate an existing signature.

---

### Bug 10: Case-Insensitive Enum Case Collision

**Library:** [SVGView](https://github.com/exyte/SVGView) 1.0.6 (21 errors, 12,731 lines)

**Symptom:**
```
error CS0102: The type 'PathSegmentType' already contains a definition for 'M'
error CS0102: The type 'PathSegmentType' already contains a definition for 'L'
error CS0102: The type 'PathSegmentType' already contains a definition for 'C'
... (for M, L, C, Q, A, H, V, S, T, Z)
```

Also:
```
error CS0115: 'SVGColor.WithOpacity(double)': no suitable method found to override
```

**Root cause:** Swift enums are case-sensitive (`M` and `m` are different cases). C# enums are case-insensitive — `M` and `m` collide. The SVG path segment type uses uppercase for absolute and lowercase for relative commands (standard SVG convention), which is valid Swift but invalid C#.

**Fix area:** Enum case emission needs collision detection for case-insensitive duplicates. When detected, suffix the lowercase variant (e.g., `mRelative` / `MAbsolute`) or use a configurable naming strategy.

The `WithOpacity` override error is a separate issue — likely a method override on a class whose parent isn't correctly resolved.

---

## No Output (1)

### XMLCoder

**Library:** [XMLCoder](https://github.com/MaxDesiatov/XMLCoder) 0.17.1

**Symptom:** No `.csproj` generated, validation reports "no output."

**Root cause:** XMLCoder is a pure Swift library with no ObjC interop surface. The generator may not find any ABI JSON or the module has no exportable symbols that pass the current gates. Needs investigation into whether the xcframework contains valid ABI metadata.

---

## Summary: Bugs by Priority

### High Priority (affects multiple libraries, common patterns)
1. **UIKit value-type enum handling** (Bugs 4+5) — 3 libraries affected, common UIKit pattern
2. **Identifier sanitization** (Bugs 1+2) — emoji and backticks, 2 libraries, easy fix
3. **Default parameter overload collision** (Bug 9) — 2 libraries, existing dedup needs extension

### Medium Priority (single library, clear fix)
4. **CGFloat mapping** (Bug 7) — missing type mapping
5. **UIKit type name flattening** (Bug 8) — nested type naming
6. **CoreAnimation type gap** (Bug 6) — missing DB entry
7. **Generic type without args** (Bug 3) — type reference emission

### Lower Priority (edge cases)
8. **Case-insensitive enum collision** (Bug 10) — rare SVG-specific pattern
9. **XMLCoder no output** (investigation needed)

---

## Validation Manifest

All 21 libraries are defined in `validation-libraries.json` (worktree: `library-testing`). To reproduce:

```bash
# Fetch xcframeworks
scripts/fetch-libraries.sh

# Run validation
./validate-libraries.sh --verbose
```

### Libraries in this validation set

| # | Library | Version | Scheme | Status |
|---|---------|---------|--------|--------|
| 1 | DeviceKit | 5.7.0 | DeviceKit | PASS |
| 2 | Swinject | v2.10.0 | Swinject-iOS | FAIL (2) |
| 3 | PhoneNumberKit | 4.2.7 | PhoneNumberKit | FAIL (2) |
| 4 | Valet | 5.1.0 | Valet iOS | FAIL (24) |
| 5 | SwiftyBeaver | 2.1.1 | SwiftyBeaver-Package | PASS |
| 6 | Reachability | v5.2.4 | Reachability | PASS |
| 7 | NVActivityIndicatorView | 5.2.0 | NVActivityIndicatorView-Package | FAIL (1) |
| 8 | ObjectMapper | 4.4.3 | ObjectMapper-iOS | FAIL (1) |
| 9 | BonMot | 6.1.3 | BonMot-iOS | FAIL (247) |
| 10 | Parchment | v4.1.0 | Parchment | FAIL (1) |
| 11 | KeychainSwift | 9.0.2 | KeychainSwift | PASS |
| 12 | SwipeCellKit | 2.7.1 | SwipeCellKit | FAIL (4) |
| 13 | FSPagerView | 0.8.3 | FSPagerView | PASS |
| 14 | SwiftyGif | 5.4.5 | SwiftyGif | PASS |
| 15 | Quick | v7.6.2 | Quick | PASS |
| 16 | DifferenceKit | 1.3.0 | DifferenceKit | PASS |
| 17 | TinyConstraints | 4.0.2 | TinyConstraints | PASS |
| 18 | AMPopTip | 4.7.0 | AMPopTip | FAIL (2) |
| 19 | XMLCoder | 0.17.1 | XMLCoder | NO OUTPUT |
| 20 | AnimatedCollectionViewLayout | 1.1.0 | AnimatedCollectionViewLayout | FAIL (2) |
| 21 | SVGView | 1.0.6 | SVGView | FAIL (21) |
