# Item 7: Nested Nullability — Implementation Plan

> Phases 7a (block nullability) and 7b (generic arg nullability) in one session.

## The Bug

`StripNullability` (ObjCTypeRefParser.cs:246) uses `string.Replace` to strip ALL nullability annotations from the entire qualType string BEFORE any structural parsing. This causes two classes of bugs:

### Bug 1: Wrong annotation picked (different inner/outer)

```
Input:  void (^ _Nullable)(NSString * _Nonnull)
Step 1: StripNullability finds _Nonnull first (if/else chain) → nullability = Nonnull
        But _Nonnull is on the PARAM, not the block!
Result: Block.Nullability = Nonnull ← WRONG (should be Nullable)
        Param.Nullability = Unspecified ← WRONG (should be Nonnull, _Nonnull was consumed)
```

Same for generics:
```
Input:  NSArray<NSString * _Nonnull> * _Nullable
Step 1: StripNullability finds _Nonnull first → nullability = Nonnull
Result: NSArray.Nullability = Nonnull ← WRONG (should be Nullable)
```

### Bug 2: Inner annotation lost (same annotation on both)

```
Input:  void (^ _Nullable)(NSString * _Nullable)
Step 1: StripNullability → Replace("_Nullable", "") strips BOTH occurrences
Result: Block.Nullability = Nullable ← correct
        Param.Nullability = Unspecified ← WRONG (should be Nullable)
```

---

## Root Cause

`StripNullability` is position-unaware — it treats the qualType as a flat string instead of a structured expression with nested scopes.

## Fix Strategy

**No model changes needed.** `ObjCTypeRef` already has recursive structure:
- `BlockParams` / `BlockReturnType` → each is an `ObjCTypeRef` with its own `Nullability`
- `GenericArgs` → each is an `ObjCTypeRef` with its own `Nullability`

The fix is entirely in the parser: stop flattening nullability before structural parsing.

---

## Parser Changes (`ObjCTypeRefParser.cs`)

### 1. New helper: `FindAtDepthZero`

Finds a token in a string only when it's NOT inside `()` or `<>` brackets.

```csharp
private static int FindAtDepthZero(string s, string token)
{
    var depth = 0;
    for (var i = 0; i <= s.Length - token.Length; i++)
    {
        switch (s[i])
        {
            case '(' or '<': depth++; break;
            case ')' or '>': if (depth > 0) depth--; break;
        }
        if (depth == 0 && s.AsSpan(i).StartsWith(token))
            return i;
    }
    return -1;
}
```

### 2. New helper: depth-aware `StripNullability`

Replace the existing `StripNullability` with a version that only strips depth-0 annotations. Inner annotations (inside parens/angle brackets) are preserved for recursive `Parse` calls.

```csharp
private static string StripNullability(string s, ref ObjCNullability nullability)
{
    // Priority-ordered: _Nullable_result before _Nullable (prefix overlap)
    (string Token, ObjCNullability Value)[] annotations = [
        ("_Nullable_result", ObjCNullability.Nullable),
        ("_Nonnull", ObjCNullability.Nonnull),
        ("__nonnull", ObjCNullability.Nonnull),
        ("_Nullable", ObjCNullability.Nullable),
        ("__nullable", ObjCNullability.Nullable),
    ];

    foreach (var (token, value) in annotations)
    {
        var idx = FindAtDepthZero(s, token);
        if (idx >= 0)
        {
            nullability = value;
            s = (s[..idx] + s[(idx + token.Length)..]);
            break;
        }
    }

    // Strip _Null_unspecified at depth 0 (no semantic impact)
    while (true)
    {
        var idx = FindAtDepthZero(s, "_Null_unspecified");
        if (idx < 0) break;
        s = (s[..idx] + s[(idx + "_Null_unspecified".Length)..]);
    }

    while (s.Contains("  "))
        s = s.Replace("  ", " ");

    return s.Trim();
}
```

### 3. New helper: `ExtractNullability`

Simple string-contains check for extracting nullability from small string fragments (like the caret group content `^ _Nullable`).

```csharp
private static ObjCNullability ExtractNullability(string s)
{
    if (s.Contains("_Nonnull") || s.Contains("__nonnull"))
        return ObjCNullability.Nonnull;
    if (s.Contains("_Nullable_result"))
        return ObjCNullability.Nullable;
    if (s.Contains("_Nullable") || s.Contains("__nullable"))
        return ObjCNullability.Nullable;
    return ObjCNullability.Unspecified;
}
```

### 4. Modified `Parse()`: block short-circuit

For block types (detected by `(^`), skip the global `StripNullability` call. All nullability handling is deferred to `TryParseBlock`, which parses structurally.

```csharp
public static ObjCTypeRef Parse(string qualType)
{
    var raw = qualType;
    var s = qualType.Trim();
    s = StripAttributes(s);
    s = StripObjCMacros(s);

    // ... anonymous record check (unchanged) ...

    var nullability = ObjCNullability.Unspecified;
    var isBlockType = s.Contains("(^");

    if (!isBlockType)
        s = StripNullability(s, ref nullability);  // depth-aware

    // ... function pointer check (unchanged) ...

    // Block: s still has all nullability annotations intact
    if (TryParseBlock(s, nullability, raw, out var blockRef))
        return blockRef;

    // Fallback: if (^ was present but TryParseBlock failed, strip now
    if (isBlockType)
        s = StripNullability(s, ref nullability);

    // ... rest unchanged (id<Proto>, double pointer, generics, single pointer) ...
}
```

### 5. Modified `TryParseBlock()`: structural nullability extraction

Instead of trusting the pre-stripped outer `nullability`, extract the block's own nullability from the `(^ ...)` caret group. Return type and parameter nullabilities are handled by recursive `Parse` calls on the raw substrings.

Key changes:
- Extract nullability from between `^` and `)` in the caret group
- Return type string (before `(^`) still contains its own annotations → recursive `Parse` handles them
- Param strings still contain their own annotations → recursive `Parse` handles them

```csharp
private static bool TryParseBlock(string s, ObjCNullability outerNullability, string raw, out ObjCTypeRef result)
{
    result = null!;

    var caretIdx = s.IndexOf("(^");
    if (caretIdx < 0) return false;

    var caretClose = FindMatchingParen(s, caretIdx);
    if (caretClose < 0) return false;

    // Extract block-level nullability from caret group: (^ _Nullable)
    var caretContent = s[(caretIdx + 2)..caretClose].Trim();
    var blockNullability = ExtractNullability(caretContent);
    // Fall back to outer nullability only if caret group had none
    if (blockNullability == ObjCNullability.Unspecified)
        blockNullability = outerNullability;

    if (caretClose + 1 >= s.Length || s[caretClose + 1] != '(') return false;
    var paramsOpen = caretClose + 1;
    var paramsClose = FindMatchingParen(s, paramsOpen);
    if (paramsClose < 0) return false;

    var returnTypeStr = s[..caretIdx].Trim();
    if (string.IsNullOrEmpty(returnTypeStr)) returnTypeStr = "void";

    var paramsStr = s[(paramsOpen + 1)..paramsClose].Trim();
    var blockParams = new List<ObjCTypeRef>();
    if (!string.IsNullOrEmpty(paramsStr) && paramsStr != "void")
    {
        foreach (var param in SplitBlockParams(paramsStr))
        {
            var trimmed = param.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                blockParams.Add(Parse(trimmed));  // recursive Parse handles each param's nullability
        }
    }

    result = new ObjCTypeRef
    {
        Name = "Block",
        IsBlock = true,
        Nullability = blockNullability,
        BlockReturnType = Parse(returnTypeStr),  // recursive Parse handles return type nullability
        BlockParams = blockParams,
        RawQualType = raw
    };
    return true;
}
```

---

## Model Changes (`ObjCTypeRef.cs`)

**None.** The existing record already supports per-element nullability on `BlockParams`, `BlockReturnType`, and `GenericArgs`.

---

## Emitter Changes

### `ObjCTypeMapper.FormatGenericTypeHint` — nullability in hints

Include nullability info in generic type hint comments:

```
// Before: "Element type: string"
// After:  "Element type: string (nullable)"
```

This is a small additive change — append `" (nullable)"` or `" (nonnull)"` to the mapped type string in the hint when the generic arg has explicit nullability.

### No other emitter changes needed

- `IsNullableAttribute(typeRef)` already checks the top-level `typeRef.Nullability` — with the parser fix, blocks now get the correct top-level nullability, so `[NullAllowed]` is emitted correctly
- `MapBlockType` converts blocks to `Action<T>`/`Func<T>` — C# delegates don't carry per-parameter nullability, so inner block param nullability is captured in the model but not emittable (correct design)
- Struct/constant emitters don't deal with blocks or generics at this level

---

## Verification Traces

### 7a: `void (^ _Nonnull)(NSString * _Nullable)`

```
Parse("void (^ _Nonnull)(NSString * _Nullable)")
  StripAttributes/Macros → no change
  Contains("(^") → true → skip StripNullability
  TryParseBlock:
    caretContent = "_Nonnull" → blockNullability = Nonnull
    returnTypeStr = "void" → Parse("void") → {void, Unspecified}
    paramsStr = "NSString * _Nullable"
      Parse("NSString * _Nullable") → depth-0 _Nullable → {NSString, ptr, Nullable}
  Result: {Block, Nonnull, ret=void, params=[{NSString, Nullable}]} ✓
```

### 7a: `NSString * _Nullable (^ _Nonnull)(void)`

```
  Contains("(^") → true → skip StripNullability
  TryParseBlock:
    caretContent = "_Nonnull" → blockNullability = Nonnull
    returnTypeStr = "NSString * _Nullable"
      Parse("NSString * _Nullable") → {NSString, ptr, Nullable}
    paramsStr = "void" → empty
  Result: {Block, Nonnull, ret={NSString, Nullable}, params=[]} ✓
```

### 7b: `NSArray<NSString * _Nullable> * _Nonnull`

```
Parse("NSArray<NSString * _Nullable> * _Nonnull")
  No "(^" → depth-aware StripNullability:
    _Nonnull at depth 0 (after "> *") → stripped, nullability = Nonnull
    _Nullable at depth 1 (inside <>) → preserved
    s = "NSArray<NSString * _Nullable> *"
  TryParseGeneric:
    argsStr = "NSString * _Nullable"
    Parse("NSString * _Nullable") → {NSString, ptr, Nullable}
  Result: {NSArray, ptr, Nonnull, GenericArgs=[{NSString, Nullable}]} ✓
```

### 7b: `NSArray<NSString * _Nullable> * _Nullable` (same annotation)

```
  depth-aware StripNullability:
    First _Nullable is at depth 1 → skipped
    Second _Nullable is at depth 0 → stripped, nullability = Nullable
    s = "NSArray<NSString * _Nullable> *"
  TryParseGeneric:
    Parse("NSString * _Nullable") → {NSString, ptr, Nullable}
  Result: {NSArray, ptr, Nullable, GenericArgs=[{NSString, Nullable}]} ✓
```

---

## Test Plan

### Parser unit tests (ObjCTypeRefParserTests.cs)

**7a — Block nullability:**

| # | Input | Block Nullability | Return Nullability | Param Nullabilities |
|---|-------|------|--------|------|
| 1 | `void (^ _Nonnull)(NSString * _Nullable)` | Nonnull | Unspecified | [Nullable] |
| 2 | `void (^ _Nullable)(NSString * _Nonnull)` | Nullable | Unspecified | [Nonnull] |
| 3 | `void (^ _Nullable)(NSString * _Nullable)` | Nullable | Unspecified | [Nullable] |
| 4 | `NSString * _Nullable (^ _Nonnull)(void)` | Nonnull | Nullable | [] |
| 5 | `void (^ _Nonnull)(NSString * _Nullable, NSNumber * _Nonnull)` | Nonnull | — | [Nullable, Nonnull] |
| 6 | `void (^ _Nullable)(void (^ _Nonnull)(NSString * _Nullable))` | Nullable | — | inner block: [Nonnull, params=[Nullable]] |
| 7 | `void (^)(NSString *)` (no annotations — unchanged) | Unspecified | Unspecified | [Unspecified] |

**7b — Generic arg nullability:**

| # | Input | Outer Nullability | Arg Nullabilities |
|---|-------|------|--------|
| 8 | `NSArray<NSString * _Nullable> * _Nonnull` | Nonnull | [Nullable] |
| 9 | `NSArray<NSString * _Nonnull> * _Nullable` | Nullable | [Nonnull] |
| 10 | `NSArray<NSString * _Nullable> * _Nullable` | Nullable | [Nullable] |
| 11 | `NSDictionary<NSString * _Nonnull, NSNumber * _Nullable> *` | Unspecified | [Nonnull, Nullable] |

**Regression (ensure existing tests still pass):**

All 40+ existing `ObjCTypeRefParserTests` must continue passing — the depth-aware approach must not break any non-nested case.

### End-to-end emission tests

| # | Scenario | Assertion |
|---|----------|-----------|
| 12 | Method param is `void (^ _Nullable)(NSString *)` | `[NullAllowed]` on parameter |
| 13 | Method param is `void (^ _Nonnull)(NSString *)` | No `[NullAllowed]` on parameter |
| 14 | Property type `NSArray<NSString * _Nullable> * _Nonnull` | No `[NullAllowed]` (nonnull outer) + hint comment includes "(nullable)" |

---

## Scope / What We're NOT Doing

- **Full recursive type tree**: Not restructuring `ObjCTypeRef` into a tree. The flat model with recursive fields (`BlockParams`, `GenericArgs`) already works.
- **Double-pointer inner nullability**: `NSError * _Nullable *` strips inner annotation — pre-existing, low impact, separate fix if ever needed.
- **Emitting block param nullability in C#**: `Action<T>`/`Func<T>` can't carry it. Captured in model only.
- **P3 items**: ClangAstParser/ObjCTypeMapper extensibility — deferred.

---

## Implementation Order

1. Add `FindAtDepthZero` helper
2. Replace `StripNullability` with depth-aware version
3. Add `ExtractNullability` helper
4. Modify `Parse()` to skip stripping for blocks
5. Modify `TryParseBlock()` to extract block nullability from caret group
6. Add all parser unit tests (tests 1-11)
7. Enhance `FormatGenericTypeHint` with nullability info
8. Add end-to-end emission tests (tests 12-14)
9. Run full test suite + validation
