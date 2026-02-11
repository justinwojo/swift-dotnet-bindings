# Completed: Binding API Review & Improvements

**Completed**: February 2026
**Source**: `binding-review.md` (12-issue API review) + `binding-api-improvements.md` (implementation tracker)
**Final Baselines**: 1936 unit tests, 185 runtime tests (Tier 2 safe-only), 699 integration tests, 94/94 must-pass features, 4 libraries at 0 binding errors

This document archives all completed binding API improvements. For remaining open items, see `Future/binding-api-future-work.md`.

---

## Background

A senior .NET developer reviewed generated bindings for Nuke, BlinkID, and Lottie from the perspective of consuming them in a .NET iOS application. The review identified 12 issues across 4 priority levels, organized into 4 implementation waves. Additional issues (N1–N6) were discovered during implementation.

**Overall grade improvement**: C+ → estimated B+/A- after all fixes applied.

---

## Wave 1: Type Foundation (P0) — Done

| # | Issue | Fix Summary |
|---|-------|-------------|
| R1 | `Init()` methods instead of constructors | Real C# constructors emitted. Failable `init?` → `TryCreate()`. |
| R2 | `SwiftString` in property return types | Properties return `string`. Type conversion gate removed for accessors. |
| R9 | `Payload` public / `IDisposable` | `Payload` is `internal`. `ISwiftObject : IDisposable` provides transitive `IDisposable`. |

## Wave 2: Type Safety (P1) — Done

| # | Issue | Fix Summary |
|---|-------|-------------|
| R3 | `SwiftOptional<T>` instead of `T?` | Converted in methods, properties, constructors, and subscripts/indexers. |
| R4 | `IntPtr` for integer types | Swift `Int` maps to `nint`. No `System.IntPtr` for non-pointer semantics. |
| R10 | `Equals`/`GetHashCode` throw | Equatable types use `SwiftEquatable.Equals()`. Non-equatable: `GetHashCode() => 0`. |

## Wave 3: API Shape (P2) — Done (except R6 partial)

| # | Issue | Fix Summary |
|---|-------|-------------|
| R5 | Simple enums are classes | `EnumHandler.IsSimpleEnum` emits real C# `enum` types for enums without associated values. |
| R8 | Parameter names (`arg0`, `_for`) | WU4: `GetPublicParameterName()` with type-derived names, `_` stripping, dedup. |

## Wave 4: Polish (P3) — Done

| # | Issue | Fix Summary |
|---|-------|-------------|
| R11 | Property `Value` suffixes | Removed — no `ConfigurationValue`, `CacheValue`, etc. |
| R12 | `ISwift*` interface prefix | Interfaces use `I` + protocol name (`IImageProcessing`, etc.). |

---

## Post-Review Issues — Done

| # | Issue | Fix Summary |
|---|-------|-------------|
| N1 | Method naming (verb prefix + double Async) | WU1: `GetPublicMethodName()` with verb detection, `Get` prefix for noun-only, double Async stripping. 18 unit tests. |
| N2 | Parameter name normalization | WU4: `GetPublicParameterName()` and `GetPublicParameterNames()`. Type-derived naming, operator `left`/`right`, dedup. 12 unit tests. |
| N3 | `unsafe` on public methods | WU5: `unsafe` moved to body-level blocks. Kept only on genuinely-required types (frozen structs, proxy classes). 7 unit tests. |
| N4 | Array element type conversion | WU2: Recursive element conversion in `TypeConversionHandler.GetIdiomaticCSharpType()`. `IReadOnlyList<SwiftString>` → `IReadOnlyList<string>`. 6 unit tests. |

---

## Codex Review Fixes — Done

Post-WU marshalling correctness issues found by Codex review. 17 regression tests.

| # | Severity | Issue | Fix |
|---|----------|-------|-----|
| CR1 | P0 | Protocol proxy getter receivers marshal idiomatic C# types into MarshalToSwiftBuffer (needs Swift ABI types) | `GetParameterConversion` reverse-conversion in getter path |
| CR2 | P0 | Optional<Array<String>> missing `.Select()` element projection | IsSwiftString check on inner array element type |
| CR3 | P1 | Protocol proxy setter missing Optional<Array<String>> handling | Added to `GetReturnConversion` before generic fallback |
| CR4 | P1 | `GetSwiftWrapperType` used `GetElementType()` (eagerly converts SwiftString→string) | Fixed to use `GetRawElementType()` |
| CR5 | P1 | `hasReturnValue` captured after async void→Task conversion | Captured before async type conversion |
| CR6 | — | `GetParameterConversion` for Optional<String> passed raw string to `NewSome()` | Added IsSwiftString inner check for `new SwiftString()` wrap |

---

## AnyType Reduction Pass — Done

Eliminated 7 unique AnyType occurrences across Nuke and Lottie. Nuke AnyType lines: 10 → 4.

| # | Fix | Libraries |
|---|-----|-----------|
| AT1 | Optional existential in protocol interface methods | Nuke |
| AT2 | Foundation.Bundle (NSBundle) TypeDB registration | Lottie |
| AT3 | CoreText.CTFont TypeDB registration | Lottie |
| AT4 | Swift.AnyHashable TypeDB registration + runtime struct | Nuke, Lottie |

---

## Enum Existential Promotion — Done

Enum associated values with protocol-typed parameters now use typed interfaces (`IImageProcessing`, `IImageDecoding`) instead of `ExistentialContainer{N}`. Gated on `AllProtocolsHaveTypeRecords()` — unknown protocols (e.g., `Swift.Error`) correctly keep container types. 7 unit tests.

---

## #nullable enable — Done

All generated C# files (main bindings + SwiftUI bridge) emit `#nullable enable`.

---

## Quality Scorecard at Completion

| Metric | Gate | Status |
|--------|------|--------|
| Public `Init()` instance methods | 0 | Done |
| Public `SwiftString` properties | 0 | Done |
| Public `SwiftOptional<T>` | 0 | Done |
| Public `IntPtr` for non-pointer semantics | 0 | Done |
| `arg0`/`arg1` parameter names | 0 | Done |
| `Equals`/`GetHashCode` that throw | 0 | Done |
| Types declaring `IDisposable` | all | Done |
| Public `Payload` property | 0 | Done |
| Noun-only async methods without verb prefix | 0 | Done |
| Double `Async` prefix+suffix | 0 | Done |
| `IReadOnlyList<SwiftString>` (unconverted elements) | 0 | Done |
| Public methods requiring `unsafe` caller context | 0 | Done |
| Missing `#nullable enable` | 0 | Done |

---

## What Works Well (from original review)

Highlights the reviewer called out as positive:
- **Protocol proxy pattern**: `class MyProcessor : IImageProcessing` + `new ImageProcessingProxy(myProcessor)` — clean and functional
- **Enum associated values (TryGet)**: `TryGetDataLoadingFailed(out var loadError)` mirrors `Dictionary.TryGetValue`
- **Async/await mapping**: Swift async → C# `Task<T>` and `IAsyncEnumerable<T>`
- **Nested type organization**: `ImagePipeline.Error`, `ImageRequest.Priority` mirror Swift structure
- **Operator overloading**: `==`, `!=`, `IEquatable<T>` work as expected
- **OptionSet mapping**: Static properties with `RawValue` constructor
- **Real-world coverage**: Nuke (49 types), BlinkID (96+ types), Lottie (56+ types) all compile and run
