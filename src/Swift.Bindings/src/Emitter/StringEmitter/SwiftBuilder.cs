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
        ["bool"] = "Swift.Bool", ["System.Boolean"] = "Swift.Bool",
        ["sbyte"] = "Swift.Int8", ["System.SByte"] = "Swift.Int8",
        ["byte"] = "Swift.UInt8", ["System.Byte"] = "Swift.UInt8",
        ["short"] = "Swift.Int16", ["System.Int16"] = "Swift.Int16",
        ["ushort"] = "Swift.UInt16", ["System.UInt16"] = "Swift.UInt16",
        ["int"] = "Swift.Int32", ["System.Int32"] = "Swift.Int32",
        ["uint"] = "Swift.UInt32", ["System.UInt32"] = "Swift.UInt32",
        ["long"] = "Swift.Int64", ["System.Int64"] = "Swift.Int64",
        ["ulong"] = "Swift.UInt64", ["System.UInt64"] = "Swift.UInt64",
        ["nint"] = "Swift.Int", ["System.IntPtr"] = "Swift.Int",
        ["nuint"] = "Swift.UInt", ["System.UIntPtr"] = "Swift.UInt",
        ["float"] = "Swift.Float", ["System.Single"] = "Swift.Float",
        ["double"] = "Swift.Double", ["System.Double"] = "Swift.Double",
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
            "Swift.Bool" => "Swift.UInt8",
            "Swift.Int" => "Swift.Int",
            "Swift.UInt" => "Swift.UInt",
            "Swift.Int8" => "Swift.Int8",
            "Swift.UInt8" => "Swift.UInt8",
            "Swift.Int16" => "Swift.Int16",
            "Swift.UInt16" => "Swift.UInt16",
            "Swift.Int32" => "Swift.Int32",
            "Swift.UInt32" => "Swift.UInt32",
            "Swift.Int64" => "Swift.Int64",
            "Swift.UInt64" => "Swift.UInt64",
            "Swift.Float" => "Swift.Float",
            "Swift.Double" => "Swift.Double",
            // Pointer types pass through
            "Swift.UnsafeRawPointer" => "Swift.UnsafeRawPointer",
            "Swift.UnsafeMutableRawPointer" => "Swift.UnsafeMutableRawPointer",
            "Swift.OpaquePointer" => "Swift.OpaquePointer",
            _ => "Swift.UnsafeMutableRawPointer" // Structs, classes, etc.
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

            // Optional<class> uses single-nullable-pointer ABI: UnsafeMutableRawPointer?
            if (closureHandler != null && closureHandler.IsOptionalReferenceArg(named))
            {
                return "Swift.UnsafeMutableRawPointer?";
            }

            // Optional<any Error> — existential uses pointer-to-container ABI; nil-pointer is none.
            // Matches MCB's marshalling path (Swift.Foundation.AnyError? in C#, IntPtr.Zero sentinel).
            if (named.ContainsGenericParameters &&
                named.Name == "Swift.Optional" && named.GenericParameters.Count == 1 &&
                MethodClosureBridge.IsAnyErrorExistential(named.GenericParameters[0]))
            {
                return "Swift.UnsafeMutableRawPointer?";
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
                return "Swift.UnsafeMutableRawPointer?";
            }

            // Optional<Array/Dictionary> callback arguments use nil for .none and otherwise
            // carry the address of a typed temporary containing the unwrapped collection value.
            if (named.ContainsGenericParameters && named.Name == "Swift.Optional" &&
                named.GenericParameters.Count == 1 &&
                named.GenericParameters[0] is NamedTypeSpec { Name: "Swift.Array" or "Swift.Dictionary" })
            {
                return "Swift.UnsafeMutableRawPointer?";
            }

            return GetSwiftCdeclParamType(named);
        }

        if (typeSpec.IsEmptyTuple)
            return "Swift.Void";

        return "Swift.UnsafeMutableRawPointer";
    }

    /// <summary>
    /// Returns the C# callback-delegate parameter type for a closure argument — the C#-side dual of
    /// <see cref="GetSwiftCdeclParamType(TypeSpec, ClosureHandler)"/>. Canonical implementation shared
    /// by <c>MethodClosureBridge</c> and <c>NestedClosureBridge</c>. Bool lowers to <c>byte</c>,
    /// other primitives via <see cref="MarshallingHelpers.MapSwiftPrimitiveToCSharpType"/>, simple enums
    /// to their C# underlying integer, optional-reference args to <c>IntPtr</c> (nullable-pointer ABI),
    /// and bound generics / classes to <c>IntPtr</c>.
    /// </summary>
    public static string GetCSharpCallbackParamType(TypeSpec argType, ClosureHandler closureHandler)
    {
        if (argType is NamedTypeSpec named)
        {
            if (named.Name == "Swift.Bool") return "byte";
            if (MarshallingHelpers.IsSwiftPrimitive(named.Name))
                return MarshallingHelpers.MapSwiftPrimitiveToCSharpType(named.Name);

            // Simple enum: pass raw value via the enum's C# underlying integer type.
            var enumInfo = closureHandler.GetSimpleEnumInfo(argType);
            if (enumInfo != null)
                return enumInfo.Value.csUnderlying;

            // Optional<class>: single-nullable-pointer ABI — IntPtr (Zero = nil).
            if (closureHandler.IsOptionalReferenceArg(argType)) return "IntPtr";
        }

        // Bound generics, classes: IntPtr (pointer ABI)
        return "IntPtr";
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
        c == '<' || c == '>' || c == '[' || c == ']' || c == '(' || c == ')' ||
        c == '?' || c == '!';

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
