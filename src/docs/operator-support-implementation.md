# Swift Operator Support Implementation Plan

## Overview

Implement Swift operator support to generate C# operator overloads from Swift operator definitions. Currently, operators are parsed but explicitly skipped in the emitter.

## Current State

**Parser (`SwiftABIParser.cs:72-77`)** - Operator list already defined:
```csharp
private static readonly HashSet<string> _operators = new()
{
    // Arithmetic
    "+", "-", "*", "/", "%",
    // Relational
    "<", ">", "<=", ">=", "==", "!=",
    // Bitwise, etc.
};
```

**Parser (`SwiftABIParser.cs:224-225`)** - Operators explicitly skipped:
```csharp
case "Function":
case "Constructor":
    // TODO: Implement operator overloading
    result = IsOperator(node.Name) ? null : CreateMethodDecl(node, parentDecl, moduleDecl);
    break;
```

**TypeHandler.cs** - Already generates `==` and `!=` for Equatable protocol conformance, showing the pattern works.

## Design Decisions

1. **C# Representation**: Map Swift operators to C# `operator` overloads where possible
2. **Static Methods**: C# operators must be static, which aligns with Swift's design
3. **Unsupported Operators**: Swift custom operators (like `<=>`) have no C# equivalent - emit as named static methods

## Swift to C# Operator Mapping

| Swift | C# | Notes |
|-------|-----|-------|
| `+` | `operator +` | Binary and unary |
| `-` | `operator -` | Binary and unary |
| `*` | `operator *` | |
| `/` | `operator /` | |
| `%` | `operator %` | |
| `==` | `operator ==` | Requires `!=` pair |
| `!=` | `operator !=` | Requires `==` pair |
| `<` | `operator <` | |
| `>` | `operator >` | |
| `<=` | `operator <=` | |
| `>=` | `operator >=` | |
| `&` | `operator &` | Bitwise AND |
| `\|` | `operator \|` | Bitwise OR |
| `^` | `operator ^` | Bitwise XOR |
| `~` | `operator ~` | Bitwise NOT (unary) |
| `<<` | `operator <<` | Left shift |
| `>>` | `operator >>` | Right shift |
| `!` | `operator !` | Logical NOT (unary) |
| `&&` | N/A | C# doesn't allow overloading |
| `\|\|` | N/A | C# doesn't allow overloading |
| Custom | Static method | e.g., `<=>` → `Compare()` |

## Implementation

### Phase 1: Create OperatorDecl Model

**File**: `src/Swift.Bindings/src/Model/TypeDecl/OperatorDecl.cs`

```csharp
public record OperatorDecl : BaseDecl
{
    public required string OperatorSymbol { get; set; }
    public required bool IsUnary { get; set; }  // prefix/postfix vs infix
    public required bool IsPrefix { get; set; } // for unary: prefix vs postfix
    public required MethodDecl UnderlyingMethod { get; set; }
}
```

### Phase 2: Update Parser to Create OperatorDecl

**File**: `src/Swift.Bindings/src/Parser/SwiftABIParser.cs`

Change line 225 from:
```csharp
result = IsOperator(node.Name) ? null : CreateMethodDecl(node, parentDecl, moduleDecl);
```

To:
```csharp
if (IsOperator(node.Name))
    result = CreateOperatorDecl(node, parentDecl, moduleDecl);
else
    result = CreateMethodDecl(node, parentDecl, moduleDecl);
```

Add new method:
```csharp
private OperatorDecl? CreateOperatorDecl(JsonNode node, BaseDecl parentDecl, ModuleDecl moduleDecl)
{
    var methodDecl = CreateMethodDecl(node, parentDecl, moduleDecl);
    if (methodDecl == null) return null;

    var opSymbol = node.Name;
    var isUnary = methodDecl.CSSignature.Count == 2; // return + 1 arg
    var isPrefix = /* determine from Swift ABI */;

    return new OperatorDecl
    {
        Name = opSymbol,
        OperatorSymbol = opSymbol,
        IsUnary = isUnary,
        IsPrefix = isPrefix,
        UnderlyingMethod = methodDecl,
        ParentDecl = parentDecl,
        ModuleDecl = moduleDecl
    };
}
```

### Phase 3: Create OperatorHandler

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/OperatorHandler.cs`

```csharp
public class OperatorHandler
{
    private static readonly Dictionary<string, string> _operatorMethodNames = new()
    {
        { "+", "op_Addition" },
        { "-", "op_Subtraction" },
        { "*", "op_Multiply" },
        { "/", "op_Division" },
        // etc.
    };

    public bool IsSupportedOperator(string opSymbol) => _operatorMethodNames.ContainsKey(opSymbol);

    public string GetCSharpOperatorName(string opSymbol) =>
        _operatorMethodNames.TryGetValue(opSymbol, out var name) ? name : null;

    public void EmitOperator(OperatorDecl operatorDecl, StringBuilder sb)
    {
        // Generate: public static ReturnType operator +(TypeName left, TypeName right) { ... }
    }
}
```

### Phase 4: Update TypeHandler to Emit Operators

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs`

In the type emission loop, add handling for `OperatorDecl`:
```csharp
foreach (var member in typeDecl.Members)
{
    switch (member)
    {
        case MethodDecl methodDecl:
            // existing handling
            break;
        case OperatorDecl operatorDecl:
            _operatorHandler.EmitOperator(operatorDecl, sb);
            break;
        // etc.
    }
}
```

### Phase 5: Handle Paired Operators

C# requires `==`/`!=` and `<`/`>` to be defined as pairs. Add validation:

```csharp
public void ValidateOperatorPairs(TypeDecl typeDecl)
{
    var operators = typeDecl.Members.OfType<OperatorDecl>().ToList();

    // Check for == without !=
    if (operators.Any(o => o.OperatorSymbol == "==") &&
        !operators.Any(o => o.OperatorSymbol == "!="))
    {
        // Generate default != that negates ==
    }

    // Similar for < and >
}
```

### Phase 6: Add Unit Tests

**File**: `src/Swift.Bindings/tests/UnitTests/EmitterTests/OperatorHandlerTests.cs`

Test cases:
- `IsSupportedOperator_WithPlusOperator_ReturnsTrue`
- `IsSupportedOperator_WithCustomOperator_ReturnsFalse`
- `EmitOperator_BinaryPlus_GeneratesCorrectSignature`
- `EmitOperator_UnaryMinus_GeneratesCorrectSignature`
- `EmitOperator_EqualityWithoutInequality_GeneratesBoth`

## Files to Modify/Create

| File | Action |
|------|--------|
| `src/Swift.Bindings/src/Model/TypeDecl/OperatorDecl.cs` | **NEW** |
| `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` | Modify to create OperatorDecl |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/OperatorHandler.cs` | **NEW** |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs` | Add operator emission |
| `src/Swift.Bindings/tests/UnitTests/EmitterTests/OperatorHandlerTests.cs` | **NEW** |

## Phase 1 Scope (MVP)

- Binary arithmetic operators: `+`, `-`, `*`, `/`, `%`
- Comparison operators: `==`, `!=`, `<`, `>`, `<=`, `>=`
- Only operators where both operands are the same type (simplest case)

## Future Phases

- **Phase 2**: Unary operators (`-`, `!`, `~`)
- **Phase 2**: Bitwise operators (`&`, `|`, `^`, `<<`, `>>`)
- **Phase 3**: Mixed-type operators (e.g., `Vector * scalar`)
- **Phase 3**: Custom Swift operators → named static methods

## Verification

1. Build the project: `dotnet build src/Swift.Bindings/src`
2. Run unit tests: `dotnet test src/Swift.Bindings/tests/UnitTests`
3. Test with a Swift library containing operators (requires macOS)

## Example Output

Given Swift:
```swift
struct Point {
    var x: Int
    var y: Int

    static func + (left: Point, right: Point) -> Point {
        return Point(x: left.x + right.x, y: left.y + right.y)
    }
}
```

Generate C#:
```csharp
public struct Point
{
    public long X { get; set; }
    public long Y { get; set; }

    public static Point operator +(Point left, Point right)
    {
        return PInvoke_op_Addition(left, right);
    }

    [DllImport(...)]
    private static extern Point PInvoke_op_Addition(Point left, Point right);
}
```

## Research Needed

Before implementation, investigate:
1. How do operators appear in Swift ABI JSON? (check existing ABI files)
2. How does Swift encode prefix vs postfix unary operators?
3. Are there any operator overloads in the StoreKit bindings we can examine?
