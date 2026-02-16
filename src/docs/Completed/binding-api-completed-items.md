# Completed: Binding API Additional Items

**Completed**: February 2026
**Source**: Consolidated from `Future/binding-api-future-work.md`
**Parent**: `Completed/binding-api-review-and-improvements.md`

---

## R7: AnyType Fallback — Original Type Info

**Completed**: 2026-02-16

`[OriginalSwiftType("CoreText.CTFont")]` attribute emitted on AnyType-fallback parameters and return types. Consumers can see what the Swift API actually expects even when the type can't be resolved.

The AnyType reduction pass eliminated 7 occurrences. Remaining AnyType instances are structural and unlikely to be resolved without architecture changes: ArraySlice in protocol interfaces (15), Protocol Self type (6), Any/Any.Type (3), generic type arguments (4), associated type protocols (2), cross-module nested types (1), closure containing ArraySlice (1).

---

## N6: Property Collision Logic (Value Suffix)

**Completed**: 2026-02-14

Nested type collision resolved by R11 (Wave 4 Polish): when a property collides with a nested type, the **nested type** is renamed with "Info" suffix (e.g., `Cache` → `CacheInfo`), leaving the property with its natural PascalCase name. The only remaining "Value" suffix is for CS0542 (property name == enclosing type name, e.g., class `Animation` with property `animation` → `AnimationValue`), which is a mandatory C# compiler error and cannot be removed. Verified across 25 libraries.

---

## Default Parameters / Overloads

**Completed**: 2026-02-14

Covers all emission-eligible methods (non-accessor, non-internal, non-generic-parent, non-placeholder). Intentional skip cases: property accessors, module-internal methods, internal/unregistered parent types, generic parent types (Swift extension syntax can't express type parameters), placeholder/AnyType signatures, and signature collisions. Remaining gap: generic parent types — zero affected methods across 25 validated libraries.

---

## Collection Interfaces

**Completed**: 2026-02-14

`SwiftArray<T>` now implements `IReadOnlyList<T>` and `IList<T>` with lazy indexed access. Constructors from `T[]` and `IEnumerable<T>`, implicit conversion from `T[]`, bounds-checked indexer, and `AsProjected<TResult>()` for zero-copy string array returns. See roadmap Session 5.
