// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Centralized Swift code generation helpers — type maps, identifier sanitization,
/// and scope-managed blocks for emitting well-formed Swift source code.
/// </summary>
public static class SwiftBuilder
{
    // ═══════════════════════════════════════════════════════════════════════
    // 1A. Centralized type maps
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Maps Swift type names to C# type names for resolving types without the type database.
    /// Canonical source — WitnessDispatchEmitter delegates to this.
    /// </summary>
    public static readonly Dictionary<string, string> SwiftToCSharpType = new()
    {
        ["Swift.Int"] = "nint", ["Swift.UInt"] = "nuint",
        ["Swift.Int8"] = "sbyte", ["Swift.UInt8"] = "byte",
        ["Swift.Int16"] = "short", ["Swift.UInt16"] = "ushort",
        ["Swift.Int32"] = "int", ["Swift.UInt32"] = "uint",
        ["Swift.Int64"] = "long", ["Swift.UInt64"] = "ulong",
        ["Swift.Float"] = "float", ["Swift.Double"] = "double",
        ["Swift.Bool"] = "bool",
    };

    /// <summary>
    /// Maps C# type names to Swift type names for use in generated Swift code.
    /// Canonical source — WitnessDispatchEmitter delegates to this.
    /// </summary>
    public static readonly Dictionary<string, string> CSharpToSwiftType = new()
    {
        ["bool"] = "Bool", ["System.Boolean"] = "Bool",
        ["sbyte"] = "Int8", ["System.SByte"] = "Int8",
        ["byte"] = "UInt8", ["System.Byte"] = "UInt8",
        ["short"] = "Int16", ["System.Int16"] = "Int16",
        ["ushort"] = "UInt16", ["System.UInt16"] = "UInt16",
        ["int"] = "Int32", ["System.Int32"] = "Int32",
        ["uint"] = "UInt32", ["System.UInt32"] = "UInt32",
        ["long"] = "Int64", ["System.Int64"] = "Int64",
        ["ulong"] = "UInt64", ["System.UInt64"] = "UInt64",
        ["nint"] = "Int", ["System.IntPtr"] = "Int",
        ["nuint"] = "UInt", ["System.UIntPtr"] = "UInt",
        ["float"] = "Float", ["System.Single"] = "Float",
        ["double"] = "Double", ["System.Double"] = "Double",
    };

    /// <summary>
    /// Returns the Swift @convention(c) parameter type for a given NamedTypeSpec.
    /// Handles primitive types, pointer types, and falls back to UnsafeMutableRawPointer
    /// for structs, classes, and other non-C-representable types.
    /// For the full version that also handles enums, optional references, and tuples,
    /// use the overload that takes a TypeSpec and ClosureHandler.
    /// </summary>
    public static string GetSwiftCdeclParamType(NamedTypeSpec named)
    {
        return named.Name switch
        {
            "Swift.Bool" => "UInt8",
            "Swift.Int" => "Int",
            "Swift.UInt" => "UInt",
            "Swift.Int8" => "Int8",
            "Swift.UInt8" => "UInt8",
            "Swift.Int16" => "Int16",
            "Swift.UInt16" => "UInt16",
            "Swift.Int32" => "Int32",
            "Swift.UInt32" => "UInt32",
            "Swift.Int64" => "Int64",
            "Swift.UInt64" => "UInt64",
            "Swift.Float" => "Float",
            "Swift.Double" => "Double",
            // Pointer types pass through
            "Swift.UnsafeRawPointer" => "UnsafeRawPointer",
            "Swift.UnsafeMutableRawPointer" => "UnsafeMutableRawPointer",
            "Swift.OpaquePointer" => "OpaquePointer",
            _ => "UnsafeMutableRawPointer" // Structs, classes, etc.
        };
    }

    /// <summary>
    /// Returns the Swift @convention(c) parameter type for an arbitrary TypeSpec.
    /// Extends the NamedTypeSpec overload with enum resolution, optional-reference
    /// nil-pointer ABI, and empty-tuple handling. Canonical implementation —
    /// ClosureEmitter, MethodClosureBridge, and NestedClosureBridge all delegate here.
    /// </summary>
    public static string GetSwiftCdeclParamType(TypeSpec typeSpec, ClosureHandler? closureHandler = null)
    {
        if (typeSpec is NamedTypeSpec named)
        {
            // Simple enums pass as their underlying integer type
            if (closureHandler != null)
            {
                var enumInfo = closureHandler.GetSimpleEnumInfo(named);
                if (enumInfo != null)
                    return enumInfo.Value.swiftScalar;
            }

            // Optional<Class/ObjC> uses nil-pointer ABI: UnsafeMutableRawPointer?
            if (closureHandler != null && named.ContainsGenericParameters &&
                named.Name == "Swift.Optional" && named.GenericParameters.Count == 1 &&
                closureHandler.IsReferenceType(named.GenericParameters[0]))
            {
                return "UnsafeMutableRawPointer?";
            }

            // Optional<any Error> — existential uses pointer-to-container ABI; nil-pointer is none.
            // Matches MCB's marshalling path (Swift.AnyError? in C#, IntPtr.Zero sentinel).
            if (named.ContainsGenericParameters &&
                named.Name == "Swift.Optional" && named.GenericParameters.Count == 1 &&
                MethodClosureBridge.IsAnyErrorExistential(named.GenericParameters[0]))
            {
                return "UnsafeMutableRawPointer?";
            }

            // Optional<Bool/SimpleEnum/FrozenStruct (non-primitive)> uses nil-for-none pointer ABI: UnsafeMutableRawPointer?
            // Swift unwraps the optional, passes inner value pointer (nil for .none).
            // Primitives (Int32, Double, etc.) are frozen structs in stdlib but use the
            // heap-allocated full-Optional path instead — exclude them here.
            if (closureHandler != null && named.ContainsGenericParameters &&
                named.Name == "Swift.Optional" && named.GenericParameters.Count == 1 &&
                named.GenericParameters[0] is NamedTypeSpec optInner &&
                (optInner.Name == "Swift.Bool" || closureHandler.IsSimpleEnum(optInner) ||
                 (closureHandler.IsFrozenStruct(optInner) &&
                  !MarshallingHelpers.IsSwiftPrimitive(optInner.Name) &&
                  !optInner.Name.Contains("Pointer") && optInner.Name != "Swift.OpaquePointer" &&
                  !closureHandler.IsClassType(optInner) && !closureHandler.IsObjCBridgedClass(optInner))))
            {
                return "UnsafeMutableRawPointer?";
            }

            return GetSwiftCdeclParamType(named);
        }

        if (typeSpec.IsEmptyTuple)
            return "Void";

        return "UnsafeMutableRawPointer";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 1B. Argument name helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks if a parameter name is an auto-generated "argN" name (e.g., arg0, arg1).
    /// Must NOT match real parameter names like "arguments", "args", etc.
    /// </summary>
    public static bool IsAutoGeneratedArgName(string? name) =>
        name != null && name.StartsWith("arg") && name.Length > 3 &&
        name.Substring(3).All(char.IsDigit);

    // ═══════════════════════════════════════════════════════════════════════
    // 1C. Identifier sanitization
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Strips brackets, parens, angle brackets, and other type-syntax characters
    /// from a name to produce a valid Swift/C# identifier.
    /// Unlike <see cref="NameProvider.SanitizeIdentifierChars"/> which replaces with underscores,
    /// this removes the characters entirely — producing cleaner generated identifiers.
    /// </summary>
    public static string SanitizeIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        bool needsSanitization = false;
        foreach (var c in name)
        {
            if (IsTypeSyntaxChar(c))
            {
                needsSanitization = true;
                break;
            }
        }

        if (!needsSanitization)
            return name;

        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (!IsTypeSyntaxChar(c))
                sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool IsTypeSyntaxChar(char c) =>
        c == '<' || c == '>' || c == '[' || c == ']' || c == '(' || c == ')';

    // ═══════════════════════════════════════════════════════════════════════
    // 1C. Scope-managed blocks
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes a Swift function signature with an opening brace, increments indent,
    /// and returns an IDisposable that decrements indent and writes the closing brace on dispose.
    /// </summary>
    public static IDisposable FunctionBlock(SwiftWriter w, string signature, string? attribute = null)
    {
        if (attribute != null)
            w.WriteLine(attribute);
        w.WriteLine($"{signature} {{");
        w.Indent++;
        return new BlockScope(w);
    }

    /// <summary>
    /// Writes a Swift extension block opening, increments indent,
    /// and returns an IDisposable that writes the closing brace on dispose.
    /// </summary>
    public static IDisposable ExtensionBlock(SwiftWriter w, string typeName)
    {
        w.WriteLine($"extension {typeName} {{");
        w.Indent++;
        return new BlockScope(w);
    }

    /// <summary>
    /// Writes a Swift if-block opening, increments indent,
    /// and returns an IDisposable that writes the closing brace on dispose.
    /// </summary>
    public static IDisposable IfBlock(SwiftWriter w, string condition)
    {
        w.WriteLine($"if {condition} {{");
        w.Indent++;
        return new BlockScope(w);
    }

    /// <summary>
    /// IDisposable that decrements writer indent and emits a closing brace on dispose.
    /// </summary>
    private sealed class BlockScope : IDisposable
    {
        private readonly SwiftWriter _writer;
        private bool _disposed;

        public BlockScope(SwiftWriter writer) => _writer = writer;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Indent--;
            _writer.WriteLine("}");
        }
    }
}
