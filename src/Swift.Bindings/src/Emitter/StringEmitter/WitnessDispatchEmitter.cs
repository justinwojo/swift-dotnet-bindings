// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Generates Swift @_silgen_name accessor functions that reconstruct existential containers
/// and dispatch through the protocol witness table. These accessors enable C# code to call
/// protocol members on Swift-backed existential containers via P/Invoke.
///
/// Phase A scope: blittable property getters, non-mutating methods returning blittable types,
/// non-mutating void methods with blittable parameters.
/// Phase B scope: String property getters/setters, String method params/returns,
/// blittable property setters.
/// </summary>
public class WitnessDispatchEmitter
{
    private readonly ITypeDatabase _typeDatabase;
    private readonly ILogger _logger;
    private readonly string _moduleName;

    /// <summary>
    /// Set of C# type names that are blittable and can be safely marshalled via Unsafe.Read/Write.
    /// </summary>
    private static readonly HashSet<string> BlittablePrimitiveTypes = new()
    {
        "bool", "System.Boolean",
        "sbyte", "System.SByte",
        "byte", "System.Byte",
        "short", "System.Int16",
        "ushort", "System.UInt16",
        "int", "System.Int32",
        "uint", "System.UInt32",
        "long", "System.Int64",
        "ulong", "System.UInt64",
        "nint", "System.IntPtr",
        "nuint", "System.UIntPtr",
        "float", "System.Single",
        "double", "System.Double",
    };

    /// <summary>
    /// Set of Swift type names that are known blittable primitives.
    /// Used as a fast path before falling back to TypeDatabase lookups.
    /// </summary>
    private static readonly HashSet<string> BlittableSwiftTypes = new()
    {
        "Swift.Int", "Swift.UInt",
        "Swift.Int8", "Swift.UInt8",
        "Swift.Int16", "Swift.UInt16",
        "Swift.Int32", "Swift.UInt32",
        "Swift.Int64", "Swift.UInt64",
        "Swift.Float", "Swift.Double",
        "Swift.Bool",
    };

    /// <summary>
    /// Maps Swift type names to C# type names for resolving types without the type database.
    /// </summary>
    private static readonly Dictionary<string, string> SwiftToCSharpPrimitiveMap = new()
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
    /// </summary>
    private static readonly Dictionary<string, string> CSharpToSwiftTypeMap = new()
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

    public WitnessDispatchEmitter(ITypeDatabase typeDatabase, ILogger logger, string moduleName)
    {
        _typeDatabase = typeDatabase;
        _logger = logger;
        _moduleName = moduleName;
    }

    /// <summary>
    /// Emits all witness dispatch accessor functions for a protocol.
    /// These are Swift functions that reconstruct the existential and dispatch through the witness table.
    /// </summary>
    public void EmitWitnessDispatchFunctions(SwiftWriter writer, ProtocolDecl protocolDecl)
    {
        var protocolName = protocolDecl.Name;
        var moduleQualifiedName = protocolDecl.SwiftTypeName.ModuleQualifiedName;

        // Track method indices for overload disambiguation (matching ProtocolProxyEmitter pattern)
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();

        bool anyEmitted = false;
        bool utf8SliceEmitted = false;

        // Property getters (skip static properties - not part of witness table)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
            if (hasGetter && IsPropertyGetterDispatchable(property))
            {
                if (!anyEmitted)
                {
                    writer.WriteLine($"// Witness dispatch accessors for {protocolName}");
                    anyEmitted = true;
                }
                if (!utf8SliceEmitted && NeedsUtf8Slice(protocolDecl))
                {
                    EmitUtf8SliceStruct(writer);
                    utf8SliceEmitted = true;
                }
                EmitPropertyGetterAccessor(writer, property, protocolDecl, moduleQualifiedName);
            }
        }

        // Property setters (skip static properties - not part of witness table)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
            if (hasSetter && IsPropertySetterDispatchable(property))
            {
                if (!anyEmitted)
                {
                    writer.WriteLine($"// Witness dispatch accessors for {protocolName}");
                    anyEmitted = true;
                }
                if (!utf8SliceEmitted && NeedsUtf8Slice(protocolDecl))
                {
                    EmitUtf8SliceStruct(writer);
                    utf8SliceEmitted = true;
                }
                EmitPropertySetterAccessor(writer, property, protocolDecl, moduleQualifiedName);
            }
        }

        // Methods
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;

            var methodKey = GetMethodKey(method);
            if (methodIndices.ContainsKey(methodKey))
                continue;

            var idx = methodIndex++;
            methodIndices[methodKey] = idx;

            if (IsMethodDispatchable(method))
            {
                if (!anyEmitted)
                {
                    writer.WriteLine($"// Witness dispatch accessors for {protocolName}");
                    anyEmitted = true;
                }
                if (!utf8SliceEmitted && NeedsUtf8Slice(protocolDecl))
                {
                    EmitUtf8SliceStruct(writer);
                    utf8SliceEmitted = true;
                }
                EmitMethodAccessor(writer, method, protocolDecl, moduleQualifiedName, idx);
            }
        }

        if (anyEmitted)
            writer.WriteLine();
    }

    /// <summary>
    /// Determines if a property getter can be dispatched via witness table.
    /// A getter is dispatchable if its return type is blittable or String.
    /// </summary>
    public bool IsPropertyGetterDispatchable(PropertyDecl property)
    {
        return IsTypeDispatchable(property.SwiftTypeSpec);
    }

    /// <summary>
    /// Determines if a property setter can be dispatched via witness table.
    /// A setter is dispatchable if its type is blittable or String.
    /// </summary>
    public bool IsPropertySetterDispatchable(PropertyDecl property)
    {
        return IsTypeDispatchable(property.SwiftTypeSpec);
    }

    /// <summary>
    /// Determines if a method can be dispatched via witness table.
    /// A method is dispatchable if all parameter types and the return type (if any) are blittable or String.
    /// Throwing and async methods are not yet dispatchable.
    /// </summary>
    public bool IsMethodDispatchable(MethodDecl method)
    {
        // Throwing and async methods require special Swift accessor
        // signatures (try/await) that we don't generate yet
        if (method.Throws || method.IsAsync)
            return false;

        // Check return type
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        if (hasReturn && !IsTypeDispatchable(returnType!))
            return false;

        // Check all parameters
        foreach (var param in method.CSSignature.Skip(1))
        {
            if (!IsTypeDispatchable(param.SwiftTypeSpec))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if a TypeSpec represents Swift.String.
    /// Used by ProtocolProxyEmitter to branch on String-specific marshalling.
    /// </summary>
    public static bool IsStringType(TypeSpec? typeSpec)
    {
        return typeSpec is NamedTypeSpec namedType && namedType.Name == "Swift.String";
    }

    /// <summary>
    /// Checks if a type can be dispatched through witness accessors.
    /// This includes blittable primitives and Swift.String (via UTF-8 bridge).
    /// </summary>
    public bool IsTypeDispatchable(TypeSpec? typeSpec)
    {
        return IsTypeBlittable(typeSpec) || IsStringType(typeSpec);
    }

    /// <summary>
    /// Checks if a TypeSpec represents a String dispatch type.
    /// Public for ProtocolProxyEmitter to branch on String vs blittable marshalling.
    /// </summary>
    public static bool IsStringDispatchType(TypeSpec? typeSpec)
    {
        return IsStringType(typeSpec);
    }

    /// <summary>
    /// Gets the @_silgen_name symbol for an accessor function.
    /// Format: SBW_{Protocol}_{kind}_{name}_{index}
    /// </summary>
    public static string GetAccessorSymbol(string protocolName, string kind, string memberName, int index)
    {
        return $"SBW_{protocolName}_{kind}_{memberName}_{index}";
    }

    /// <summary>
    /// Gets the @_silgen_name symbol for a free function.
    /// Format: SBW_{Protocol}_free_{kind}_{name}_{index}
    /// </summary>
    public static string GetFreeSymbol(string protocolName, string kind, string memberName, int index)
    {
        return $"SBW_{protocolName}_free_{kind}_{memberName}_{index}";
    }

    /// <summary>
    /// Checks if a C# type name represents a blittable primitive.
    /// </summary>
    public static bool IsBlittablePrimitive(string csharpTypeName)
    {
        return BlittablePrimitiveTypes.Contains(csharpTypeName);
    }

    /// <summary>
    /// Returns the canonical blittable C# type name for a TypeSpec.
    /// Uses the Swift-name fast-path first, then falls back to the type database.
    /// This must be used for MarshalFromSwift/MarshalToSwift type parameters
    /// to ensure the marshal type matches the dispatch gate decision.
    /// Returns null if the type is not blittable.
    /// </summary>
    public string? GetBlittableCSharpType(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        // Fast path: map known Swift primitives directly
        if (typeSpec is NamedTypeSpec namedType && SwiftToCSharpPrimitiveMap.TryGetValue(namedType.Name, out var csharpType))
            return csharpType;

        // Slow path: fall back to type database
        try
        {
            var record = _typeDatabase.GetTypeRecordOrAnyType(typeSpec);
            var fqn = record.CSharpTypeName.FullyQualifiedName;
            return IsBlittablePrimitive(fqn) ? fqn : null;
        }
        catch
        {
            return null;
        }
    }

    #region Private Helpers

    /// <summary>
    /// Checks whether a protocol has any dispatchable members that use String types,
    /// which requires the SBW_Utf8Slice struct to be emitted.
    /// </summary>
    private bool NeedsUtf8Slice(ProtocolDecl protocolDecl)
    {
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            if (IsStringType(property.SwiftTypeSpec))
                return true;
        }
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;
            if (method.Throws || method.IsAsync)
                continue;
            var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            if (returnType != null && !returnType.IsEmptyTuple && IsStringType(returnType))
                return true;
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (IsStringType(param.SwiftTypeSpec))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Emits the @frozen SBW_Utf8Slice struct used to transfer UTF-8 string data
    /// across the Swift/C# boundary.
    /// </summary>
    private static void EmitUtf8SliceStruct(SwiftWriter writer)
    {
        writer.WriteLines("""
            @frozen
            public struct SBW_Utf8Slice {
                public var ptr: UnsafeMutablePointer<UInt8>
                public var len: Int
            }

            """);
    }

    private bool IsTypeBlittable(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return false;

        // Fast path: check Swift type name directly against known primitives
        if (typeSpec is NamedTypeSpec namedType && BlittableSwiftTypes.Contains(namedType.Name))
            return true;

        // Slow path: fall back to type database
        try
        {
            var record = _typeDatabase.GetTypeRecordOrAnyType(typeSpec);
            return IsBlittablePrimitive(record.CSharpTypeName.FullyQualifiedName);
        }
        catch
        {
            return false;
        }
    }

    private string GetCSharpTypeName(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return "object";

        // Fast path: map known Swift primitives directly
        if (typeSpec is NamedTypeSpec namedType && SwiftToCSharpPrimitiveMap.TryGetValue(namedType.Name, out var csharpType))
            return csharpType;

        try
        {
            var record = _typeDatabase.GetTypeRecordOrAnyType(typeSpec);
            return record.CSharpTypeName.FullyQualifiedName;
        }
        catch
        {
            return "object";
        }
    }

    private static string GetSwiftPrimitiveType(string csharpTypeName)
    {
        return CSharpToSwiftTypeMap.TryGetValue(csharpTypeName, out var swiftType)
            ? swiftType
            : "Any";
    }

    private void EmitPropertyGetterAccessor(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string moduleQualifiedName)
    {
        var protocolName = protocolDecl.Name;
        var accessorSymbol = GetAccessorSymbol(protocolName, "get", property.Name, 0);
        var freeSymbol = GetFreeSymbol(protocolName, "get", property.Name, 0);

        if (IsStringType(property.SwiftTypeSpec))
        {
            // String getter: convert Swift String to UTF-8 bytes via SBW_Utf8Slice
            writer.WriteLines($$"""
                @_silgen_name("{{accessorSymbol}}")
                public func {{accessorSymbol}}(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
                    let existential = containerPtr.load(as: (any {{moduleQualifiedName}}).self)
                    let result: String = existential.{{property.Name}}
                    let utf8 = Array(result.utf8)
                    let bufferPtr = UnsafeMutablePointer<UInt8>.allocate(capacity: max(utf8.count, 1))
                    if !utf8.isEmpty {
                        utf8.withUnsafeBufferPointer { src in
                            bufferPtr.initialize(from: src.baseAddress!, count: src.count)
                        }
                    }
                    let slicePtr = UnsafeMutablePointer<SBW_Utf8Slice>.allocate(capacity: 1)
                    slicePtr.initialize(to: SBW_Utf8Slice(ptr: bufferPtr, len: utf8.count))
                    return UnsafeMutableRawPointer(slicePtr)
                }

                @_silgen_name("{{freeSymbol}}")
                public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                    let slicePtr = ptr.assumingMemoryBound(to: SBW_Utf8Slice.self)
                    slicePtr.pointee.ptr.deallocate()
                    slicePtr.deinitialize(count: 1)
                    slicePtr.deallocate()
                }

                """);
        }
        else
        {
            // Blittable getter: direct pointer allocation
            var csharpReturnType = GetCSharpTypeName(property.SwiftTypeSpec);
            var swiftReturnType = GetSwiftPrimitiveType(csharpReturnType);

            writer.WriteLines($$"""
                @_silgen_name("{{accessorSymbol}}")
                public func {{accessorSymbol}}(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
                    let existential = containerPtr.load(as: (any {{moduleQualifiedName}}).self)
                    let result = existential.{{property.Name}}
                    let ptr = UnsafeMutablePointer<{{swiftReturnType}}>.allocate(capacity: 1)
                    ptr.initialize(to: result)
                    return UnsafeMutableRawPointer(ptr)
                }

                @_silgen_name("{{freeSymbol}}")
                public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                    ptr.assumingMemoryBound(to: {{swiftReturnType}}.self).deinitialize(count: 1)
                    ptr.deallocate()
                }

                """);
        }
    }

    private void EmitPropertySetterAccessor(SwiftWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string moduleQualifiedName)
    {
        var protocolName = protocolDecl.Name;
        var accessorSymbol = GetAccessorSymbol(protocolName, "set", property.Name, 0);

        if (IsStringType(property.SwiftTypeSpec))
        {
            // String setter: decode SBW_Utf8Slice → String, then assign via typed pointee
            writer.WriteLines($$"""
                @_silgen_name("{{accessorSymbol}}")
                public func {{accessorSymbol}}(_ containerPtr: UnsafeMutableRawPointer, _ valuePtr: UnsafeRawPointer) {
                    let typedPtr = containerPtr.assumingMemoryBound(to: (any {{moduleQualifiedName}}).self)
                    var existential = typedPtr.pointee
                    let slice = valuePtr.load(as: SBW_Utf8Slice.self)
                    let str: String
                    if slice.len > 0 {
                        str = String(unsafeUninitializedCapacity: slice.len) { buf in
                            UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: slice.ptr, byteCount: slice.len)
                            return slice.len
                        }
                    } else {
                        str = ""
                    }
                    existential.{{property.Name}} = str
                    typedPtr.pointee = existential
                }

                """);
        }
        else
        {
            // Blittable setter: typed pointee assignment
            var csharpType = GetCSharpTypeName(property.SwiftTypeSpec);
            var swiftType = GetSwiftPrimitiveType(csharpType);

            writer.WriteLines($$"""
                @_silgen_name("{{accessorSymbol}}")
                public func {{accessorSymbol}}(_ containerPtr: UnsafeMutableRawPointer, _ valuePtr: UnsafeRawPointer) {
                    let typedPtr = containerPtr.assumingMemoryBound(to: (any {{moduleQualifiedName}}).self)
                    var existential = typedPtr.pointee
                    existential.{{property.Name}} = valuePtr.load(as: {{swiftType}}.self)
                    typedPtr.pointee = existential
                }

                """);
        }
    }

    private void EmitMethodAccessor(SwiftWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string moduleQualifiedName, int index)
    {
        var protocolName = protocolDecl.Name;
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var isStringReturn = hasReturn && IsStringType(returnType!);

        var accessorSymbol = GetAccessorSymbol(protocolName, "method", method.Name, index);

        // Build Swift parameter list: containerPtr + one UnsafeRawPointer per param
        var swiftParams = new List<string> { "_ containerPtr: UnsafeRawPointer" };
        for (int i = 0; i < method.CSSignature.Count - 1; i++)
        {
            swiftParams.Add($"_ arg{i}Ptr: UnsafeRawPointer");
        }
        var swiftParamsString = string.Join(", ", swiftParams);

        // Build Swift return type
        var swiftReturnDecl = hasReturn ? " -> UnsafeMutableRawPointer" : "";

        writer.WriteLine($"@_silgen_name(\"{accessorSymbol}\")");
        writer.WriteLine($"public func {accessorSymbol}({swiftParamsString}){swiftReturnDecl} {{");
        writer.Indent++;

        // Load existential — use var for methods that may be mutating in the future
        writer.WriteLine($"let existential = containerPtr.load(as: (any {moduleQualifiedName}).self)");

        // Unmarshal parameters — per-parameter branching for String vs blittable
        var callArgs = new List<string>();
        int argIdx = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            if (IsStringType(param.SwiftTypeSpec))
            {
                // String parameter: decode SBW_Utf8Slice → Swift String
                writer.WriteLine($"let arg{argIdx}Slice = arg{argIdx}Ptr.load(as: SBW_Utf8Slice.self)");
                writer.WriteLine($"let arg{argIdx}: String");
                writer.WriteLine($"if arg{argIdx}Slice.len > 0 {{");
                writer.Indent++;
                writer.WriteLine($"arg{argIdx} = String(unsafeUninitializedCapacity: arg{argIdx}Slice.len) {{ buf in");
                writer.Indent++;
                writer.WriteLine($"UnsafeMutableRawPointer(buf.baseAddress!).copyMemory(from: arg{argIdx}Slice.ptr, byteCount: arg{argIdx}Slice.len)");
                writer.WriteLine($"return arg{argIdx}Slice.len");
                writer.Indent--;
                writer.WriteLine("}");
                writer.Indent--;
                writer.WriteLine("} else {");
                writer.Indent++;
                writer.WriteLine($"arg{argIdx} = \"\"");
                writer.Indent--;
                writer.WriteLine("}");
            }
            else
            {
                // Blittable parameter: direct load
                var csharpType = GetCSharpTypeName(param.SwiftTypeSpec);
                var swiftType = GetSwiftPrimitiveType(csharpType);
                writer.WriteLine($"let arg{argIdx} = arg{argIdx}Ptr.load(as: {swiftType}.self)");
            }
            callArgs.Add($"arg{argIdx}");
            argIdx++;
        }

        // Build method call with Swift parameter labels
        var labeledArgs = new List<string>();
        argIdx = 0;
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var param = method.CSSignature[i];
            var label = GetSwiftParameterLabel(param);
            var argRef = callArgs[argIdx];
            labeledArgs.Add(label == "_" ? argRef : $"{label}: {argRef}");
            argIdx++;
        }
        var callArgsString = string.Join(", ", labeledArgs);

        if (hasReturn)
        {
            if (isStringReturn)
            {
                // String return: convert to UTF-8 bytes via SBW_Utf8Slice
                writer.WriteLine($"let result: String = existential.{method.Name}({callArgsString})");
                writer.WriteLine("let utf8 = Array(result.utf8)");
                writer.WriteLine("let bufferPtr = UnsafeMutablePointer<UInt8>.allocate(capacity: max(utf8.count, 1))");
                writer.WriteLine("if !utf8.isEmpty {");
                writer.Indent++;
                writer.WriteLine("utf8.withUnsafeBufferPointer { src in");
                writer.Indent++;
                writer.WriteLine("bufferPtr.initialize(from: src.baseAddress!, count: src.count)");
                writer.Indent--;
                writer.WriteLine("}");
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine("let slicePtr = UnsafeMutablePointer<SBW_Utf8Slice>.allocate(capacity: 1)");
                writer.WriteLine("slicePtr.initialize(to: SBW_Utf8Slice(ptr: bufferPtr, len: utf8.count))");
                writer.WriteLine("return UnsafeMutableRawPointer(slicePtr)");
            }
            else
            {
                // Blittable return: direct pointer allocation
                var csharpReturnType = GetCSharpTypeName(returnType!);
                var swiftReturnType = GetSwiftPrimitiveType(csharpReturnType);
                writer.WriteLine($"let result = existential.{method.Name}({callArgsString})");
                writer.WriteLine($"let ptr = UnsafeMutablePointer<{swiftReturnType}>.allocate(capacity: 1)");
                writer.WriteLine("ptr.initialize(to: result)");
                writer.WriteLine("return UnsafeMutableRawPointer(ptr)");
            }
        }
        else
        {
            writer.WriteLine($"existential.{method.Name}({callArgsString})");
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();

        // Emit free function only for methods with return values
        if (hasReturn)
        {
            var freeSymbol = GetFreeSymbol(protocolName, "method", method.Name, index);

            if (isStringReturn)
            {
                // String return: free SBW_Utf8Slice + buffer
                writer.WriteLines($$"""
                    @_silgen_name("{{freeSymbol}}")
                    public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                        let slicePtr = ptr.assumingMemoryBound(to: SBW_Utf8Slice.self)
                        slicePtr.pointee.ptr.deallocate()
                        slicePtr.deinitialize(count: 1)
                        slicePtr.deallocate()
                    }

                    """);
            }
            else
            {
                // Blittable return: simple dealloc
                var csharpReturnType = GetCSharpTypeName(returnType!);
                var swiftReturnType = GetSwiftPrimitiveType(csharpReturnType);

                writer.WriteLines($$"""
                    @_silgen_name("{{freeSymbol}}")
                    public func {{freeSymbol}}(_ ptr: UnsafeMutableRawPointer) {
                        ptr.assumingMemoryBound(to: {{swiftReturnType}}.self).deinitialize(count: 1)
                        ptr.deallocate()
                    }

                    """);
            }
        }
    }

    /// <summary>
    /// Gets the Swift parameter label for a method argument.
    /// Mirrors EveryProtocolEmitter.GetSwiftParameterLabel logic.
    /// </summary>
    private static string GetSwiftParameterLabel(ArgumentDecl param)
    {
        if (string.IsNullOrEmpty(param.Name) || param.Name == "_" || IsGeneratedArgName(param.Name))
            return "_";

        // Strip C# keyword prefix
        if (param.Name.Length > 1 && param.Name[0] == '_')
        {
            var possibleKeyword = param.Name.Substring(1);
            if (CSharpKeywords.Contains(possibleKeyword))
                return possibleKeyword;
        }

        return param.Name;
    }

    private static bool IsGeneratedArgName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith("arg"))
            return false;
        return name.Length > 3 && name.Substring(3).All(char.IsDigit);
    }

    private static readonly HashSet<string> CSharpKeywords = new()
    {
        "for", "in", "is", "as", "if", "else", "do", "while", "return",
        "break", "continue", "switch", "case", "default", "try", "catch",
        "throw", "new", "this", "base", "null", "true", "false", "class",
        "struct", "enum", "interface", "public", "private", "protected",
        "internal", "static", "readonly", "const", "override", "virtual",
        "abstract", "sealed", "async", "await", "var", "object", "string",
        "int", "long", "float", "double", "bool", "void", "ref", "out",
        "params", "event", "delegate", "operator", "implicit", "explicit",
        "where", "get", "set", "value", "partial", "using", "namespace"
    };

    private static string GetMethodKey(MethodDecl method)
    {
        return method.Name + "(" + string.Join(",", method.CSSignature.Skip(1).Select(p => p.SwiftTypeSpec?.ToString() ?? "")) + ")";
    }

    #endregion
}
