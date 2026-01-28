# Swift Operator Support Implementation Plan

## Status: COMPLETE (Implemented January 2026)

Operator support has been fully implemented. The `OperatorHandler.cs` generates C# operator overloads from Swift operator definitions. See commit `9317144 Fixing broken logic for operators`.

## Implementation Summary

### Files Created

```
src/Swift.Bindings/src/Emitter/StringEmitter/Handler/OperatorHandler.cs
```

### What Was Implemented

- Binary arithmetic operators: `+`, `-`, `*`, `/`, `%`
- Comparison operators: `==`, `!=`, `<`, `>`, `<=`, `>=`
- Unary operators: `-` (negation), `!` (logical NOT)
- Automatic pairing of `==`/`!=` as required by C#
- Operators are emitted as static operator overloads on the containing type

### How It Works

1. **Parser** - `SwiftABIParser.cs` identifies operator functions by their mangled names
2. **OperatorHandler** - Maps Swift operators to C# operator syntax
3. **TypeHandler** - Calls `OperatorHandler.EmitOperators()` when emitting struct/class members
4. **P/Invoke** - Generated operators call through to Swift via P/Invoke

---

## Original Design Document (for reference)

### Overview

Implement Swift operator support to generate C# operator overloads from Swift operator definitions. Currently, operators are parsed but explicitly skipped in the emitter.

### Current State

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

### Design Decisions

1. **C# Representation**: Map Swift operators to C# `operator` overloads where possible
2. **Static Methods**: C# operators must be static, which aligns with Swift's design
3. **Unsupported Operators**: Swift custom operators (like `<=>`) have no C# equivalent - emit as named static methods

### Swift to C# Operator Mapping

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

### Example Output

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

### Build Commands

```bash
./build.sh                    # Full build
dotnet build src/Swift.Bindings/src/Swift.Bindings.csproj
dotnet test src/Swift.Bindings/tests/UnitTests
dotnet test src/Swift.Bindings/tests/IntegrationTests
```
