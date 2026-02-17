// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Provides shared helpers for generating Swift wrapper functions that adapt
/// closure parameters from @convention(c) (Cdecl) to @convention(swift).
/// Used by standalone closure wrappers (MethodHandler), ArraySlice normalization,
/// and default parameter overload emitters.
/// </summary>
public static partial class ClosureEmitter
{
    /// <summary>
    /// Returns the Swift @convention(c) function type string for a closure's callback.
    /// E.g.: "@convention(c) (Int, UInt8, UnsafeMutableRawPointer?) -> UInt8"
    /// Context param is always UnsafeMutableRawPointer? (last position before return).
    /// </summary>
    public static string GetSwiftConventionCType(
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler)
    {
        var paramTypes = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            paramTypes.Add(GetSwiftCdeclParamType(arg));
        }

        // For throwing closures: add error out parameter
        if (closureTypeSpec.Throws)
        {
            paramTypes.Add("UnsafeMutablePointer<UnsafeMutableRawPointer?>?");
        }

        // For indirect return closures: prepend result buffer as first param
        if (closureHandler.RequiresIndirectReturnMarshalling(closureTypeSpec) && !closureTypeSpec.Throws)
        {
            paramTypes.Insert(0, "UnsafeMutableRawPointer");
        }

        // Context param is always last before return
        paramTypes.Add("UnsafeMutableRawPointer?");

        var returnType = GetSwiftCdeclReturnType(closureTypeSpec, closureHandler);
        return $"@convention(c) ({string.Join(", ", paramTypes)}) -> {returnType}";
    }

    /// <summary>
    /// Returns the Swift parameter type for a Cdecl function pointer.
    /// Maps Swift types to their C-compatible equivalents.
    /// </summary>
    private static string GetSwiftCdeclParamType(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec named)
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

        if (typeSpec.IsEmptyTuple)
            return "Void";

        return "UnsafeMutableRawPointer";
    }

    /// <summary>
    /// Returns the Swift return type for a Cdecl function pointer.
    /// </summary>
    private static string GetSwiftCdeclReturnType(
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler)
    {
        // Indirect return uses void (result written to buffer)
        if (closureHandler.RequiresIndirectReturnMarshalling(closureTypeSpec) && !closureTypeSpec.Throws)
            return "Void";

        if (closureTypeSpec.ReturnType.IsEmptyTuple)
            return "Void";

        return GetSwiftCdeclParamType(closureTypeSpec.ReturnType);
    }

    /// <summary>
    /// Generates Swift code to create an adapter closure from a Cdecl function pointer.
    /// The adapter wraps the @convention(c) function pointer in a native Swift closure
    /// that can be passed to the original Swift method.
    /// </summary>
    /// <param name="paramName">The closure parameter name.</param>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <param name="closureHandler">The closure handler.</param>
    /// <param name="isOptional">Whether the closure parameter is optional.</param>
    /// <returns>Lines of Swift code to create the adapter closure.</returns>
    public static List<string> GetSwiftClosureAdapterCode(
        string paramName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        bool isOptional)
    {
        var lines = new List<string>();
        var isThrowing = closureTypeSpec.Throws;
        var isIndirectReturn = closureHandler.RequiresIndirectReturnMarshalling(closureTypeSpec) && !isThrowing;
        var closureSwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(closureTypeSpec);
        var conventionCType = GetSwiftConventionCType(closureTypeSpec, closureHandler);

        // Build the adapter variable name
        var adapterName = $"_adapted_{paramName}";

        // Use param-unique variable name to avoid redeclaration when multiple closures
        var cdeclVarName = $"cdecl_{paramName}";

        if (isOptional)
        {
            // Optional closure: check for null funcPtr
            // Wrap in parens to get Optional<Closure> not Closure-returning-Optional<Void>
            lines.Add($"var {adapterName}: ({closureSwiftType})? = nil");
            lines.Add($"if let {paramName}FuncPtr = {paramName}FuncPtr {{");
            lines.Add($"    let {cdeclVarName} = unsafeBitCast({paramName}FuncPtr, to: ({conventionCType}).self)");
            lines.AddRange(BuildAdapterClosureBody(paramName, cdeclVarName, closureTypeSpec, closureHandler, adapterName, isThrowing, isIndirectReturn, indent: "    ", useLet: false));
            lines.Add("}");
        }
        else
        {
            lines.Add($"let {cdeclVarName} = unsafeBitCast({paramName}FuncPtr!, to: ({conventionCType}).self)");
            lines.AddRange(BuildAdapterClosureBody(paramName, cdeclVarName, closureTypeSpec, closureHandler, adapterName, isThrowing, isIndirectReturn, indent: "", useLet: true));
        }

        return lines;
    }

    /// <summary>
    /// Builds the core adapter closure body that wraps a @convention(c) function pointer
    /// in a native Swift closure.
    /// </summary>
    private static List<string> BuildAdapterClosureBody(
        string paramName,
        string cdeclVarName,
        ClosureTypeSpec closureTypeSpec,
        ClosureHandler closureHandler,
        string adapterName,
        bool isThrowing,
        bool isIndirectReturn,
        string indent,
        bool useLet = false)
    {
        var lines = new List<string>();
        var hasReturn = !closureTypeSpec.ReturnType.IsEmptyTuple;
        var letPrefix = useLet ? "let " : "";

        // Build closure parameter list
        var closureParams = new List<string>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg);
            closureParams.Add($"p{argIndex}: {swiftType}");
            argIndex++;
        }

        // Build cdecl call arguments
        var cdeclArgs = new List<string>();

        // For indirect return, first arg is the result buffer
        if (isIndirectReturn)
        {
            cdeclArgs.Add("resultBuf");
        }

        argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            cdeclArgs.Add(GetSwiftArgConversion(arg, $"p{argIndex}"));
            argIndex++;
        }

        // For throwing, add error out param
        if (isThrowing)
        {
            cdeclArgs.Add("&errorPtr");
        }

        // Context param is always last
        cdeclArgs.Add($"{paramName}Context!");
        var cdeclArgsStr = string.Join(", ", cdeclArgs);

        // Build the closure signature
        var closureParamsStr = string.Join(", ", closureParams.Select(p =>
        {
            var parts = p.Split(": ");
            return $"_ {parts[0]}: {parts[1]}";
        }));

        var throwsStr = isThrowing ? " throws" : "";
        var returnTypeStr = hasReturn && !isIndirectReturn
            ? $" -> {ExistentialBypassEmitter.RenderSwiftTypeSpec(closureTypeSpec.ReturnType)}"
            : "";

        if (isIndirectReturn)
        {
            // Indirect return: closure writes result to buffer, returns void
            var returnSwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(closureTypeSpec.ReturnType);
            lines.Add($"{indent}{letPrefix}{adapterName} = {{ ({closureParamsStr}) -> {returnSwiftType} in");
            lines.Add($"{indent}    let resultBuf = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<{returnSwiftType}>.size, alignment: MemoryLayout<{returnSwiftType}>.alignment)");
            lines.Add($"{indent}    {cdeclVarName}({cdeclArgsStr})");
            lines.Add($"{indent}    let result = resultBuf.load(as: {returnSwiftType}.self)");
            lines.Add($"{indent}    resultBuf.deallocate()");
            lines.Add($"{indent}    return result");
            lines.Add($"{indent}}}");
        }
        else if (isThrowing)
        {
            var returnSwiftType = hasReturn ? ExistentialBypassEmitter.RenderSwiftTypeSpec(closureTypeSpec.ReturnType) : "Void";
            lines.Add($"{indent}{letPrefix}{adapterName} = {{ ({closureParamsStr}) throws{returnTypeStr} in");
            lines.Add($"{indent}    var errorPtr: UnsafeMutableRawPointer? = nil");

            if (hasReturn)
            {
                var returnConversion = GetSwiftReturnConversion(closureTypeSpec.ReturnType, $"{cdeclVarName}({cdeclArgsStr})");
                lines.Add($"{indent}    let rawResult = {returnConversion}");
            }
            else
            {
                lines.Add($"{indent}    {cdeclVarName}({cdeclArgsStr})");
            }

            lines.Add($"{indent}    if let error = errorPtr {{");
            lines.Add($"{indent}        throw unsafeBitCast(error, to: Swift.Error.self)");
            lines.Add($"{indent}    }}");

            if (hasReturn)
            {
                lines.Add($"{indent}    return rawResult");
            }

            lines.Add($"{indent}}}");
        }
        else
        {
            // Regular closure
            lines.Add($"{indent}{letPrefix}{adapterName} = {{ ({closureParamsStr}){returnTypeStr} in");

            if (hasReturn)
            {
                var returnConversion = GetSwiftReturnConversion(closureTypeSpec.ReturnType, $"{cdeclVarName}({cdeclArgsStr})");
                lines.Add($"{indent}    return {returnConversion}");
            }
            else
            {
                lines.Add($"{indent}    {cdeclVarName}({cdeclArgsStr})");
            }

            lines.Add($"{indent}}}");
        }

        return lines;
    }

    /// <summary>
    /// Converts a Swift argument to the form expected by the @convention(c) function.
    /// E.g., Bool → (p0 ? 1 : 0), structs → pointer conversion.
    /// </summary>
    private static string GetSwiftArgConversion(TypeSpec typeSpec, string argExpr)
    {
        if (typeSpec is NamedTypeSpec named)
        {
            if (named.Name == "Swift.Bool")
                return $"({argExpr} ? 1 : 0)";

            // Primitive types pass through directly
            if (IsSwiftPrimitive(named.Name))
                return argExpr;

            // Pointer types pass through
            if (named.Name.Contains("Pointer") || named.Name == "Swift.OpaquePointer")
                return argExpr;
        }

        // For non-primitive types, we'd need pointer conversion
        // but for now the supported closure args are all primitives/bool
        return argExpr;
    }

    /// <summary>
    /// Converts the raw Cdecl return value back to the Swift type.
    /// E.g., UInt8 → (rawResult != 0) for Bool.
    /// </summary>
    private static string GetSwiftReturnConversion(TypeSpec typeSpec, string expr)
    {
        if (typeSpec is NamedTypeSpec named && named.Name == "Swift.Bool")
            return $"({expr}) != 0";

        return expr;
    }

    /// <summary>
    /// Checks if a Swift type name is a primitive that passes directly in @convention(c).
    /// </summary>
    private static bool IsSwiftPrimitive(string swiftTypeName)
    {
        return swiftTypeName switch
        {
            "Swift.Int" or "Swift.UInt" or
            "Swift.Int8" or "Swift.UInt8" or
            "Swift.Int16" or "Swift.UInt16" or
            "Swift.Int32" or "Swift.UInt32" or
            "Swift.Int64" or "Swift.UInt64" or
            "Swift.Float" or "Swift.Double" => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if a type is Cdecl-compatible for Swift wrapper closure adaptation.
    /// Only primitive types (Int, Double, Bool, etc.), Void, and pointer types can be
    /// passed directly through @convention(c) without pointer marshalling.
    /// Complex types (String, classes, non-frozen structs) require full marshalling
    /// which is not yet implemented in the Cdecl wrapper path.
    /// </summary>
    private static bool IsCdeclCompatibleType(TypeSpec typeSpec)
    {
        if (typeSpec.IsEmptyTuple)
            return true;

        if (typeSpec is NamedTypeSpec named)
        {
            if (named.Name == "Swift.Bool")
                return true;
            if (IsSwiftPrimitive(named.Name))
                return true;
            if (named.Name.Contains("Pointer") || named.Name == "Swift.OpaquePointer")
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if all arguments and return type of a closure are Cdecl-compatible,
    /// meaning they can be passed through @convention(c) without pointer marshalling.
    /// </summary>
    public static bool IsClosureCdeclCompatible(ClosureTypeSpec closureTypeSpec, ClosureHandler closureHandler)
    {
        // Check all arguments
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            if (!IsCdeclCompatibleType(arg))
                return false;
        }

        // Check return type (indirect return closures write to buffer — return type must be Cdecl-compatible for load)
        if (!closureTypeSpec.ReturnType.IsEmptyTuple && !IsCdeclCompatibleType(closureTypeSpec.ReturnType))
            return false;

        return true;
    }

    /// <summary>
    /// Emits a standalone Swift wrapper function for a method/constructor whose ONLY
    /// wrapper reason is closure parameters (no ArraySlice, no default params, etc.).
    /// The wrapper is a free function with @_silgen_name that receives Cdecl closure params
    /// and adapts them to native Swift closures before calling the original method.
    /// </summary>
    public static void EmitClosureCdeclSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        TypeDecl? parentDecl)
    {
        var methodDecl = env.MethodDecl;
        var closureHandler = env.ClosureHandler;
        var wrapperSymbol = NameProvider.GetMangledName(methodDecl);

        // Build Swift parameter list
        var swiftParams = new List<string>();
        var callArgs = new List<string>();
        var adapterCode = new List<string>();

        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            var csName = NameProvider.GetCSharpParameterName(arg);
            // Escape Swift keywords with backticks for use in generated Swift code
            var swiftName = NameProvider.EscapeSwiftKeyword(csName);
            var closureTypeSpec = closureHandler.GetClosureTypeSpec(arg);

            if (closureTypeSpec != null &&
                closureHandler.IsSupportedClosure(closureTypeSpec) &&
                closureHandler.RequiresThunk(closureTypeSpec) &&
                !closureHandler.IsAsyncThrowingClosure(closureTypeSpec))
            {
                // Replace closure param with (funcPtr, context) pair
                // Closure-derived names (FuncPtr, Context, _adapted_) use csName suffix which is safe
                swiftParams.Add($"_ {csName}FuncPtr: UnsafeMutableRawPointer?");
                swiftParams.Add($"_ {csName}Context: UnsafeMutableRawPointer?");

                bool isOptional = closureHandler.IsOptionalClosure(arg.SwiftTypeSpec);

                // Generate adapter code
                adapterCode.AddRange(GetSwiftClosureAdapterCode(
                    csName, closureTypeSpec, closureHandler, isOptional));

                // Use adapter in call args
                var adapterName = $"_adapted_{csName}";
                var label = GetSwiftArgLabel(arg);
                callArgs.Add($"{label}{adapterName}");
            }
            else
            {
                // Non-closure param: pass through with original Swift type
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
                swiftParams.Add($"_ {swiftName}: {swiftType}");
                var label = GetSwiftArgLabel(arg);
                callArgs.Add($"{label}{swiftName}");
            }
        }

        // For instance methods, add self as last param
        bool isInstance = methodDecl.MethodType != MethodType.Static && parentDecl != null && !methodDecl.IsConstructor;
        if (isInstance)
        {
            swiftParams.Add("_ _self: UnsafeMutableRawPointer");
        }

        var paramsStr = string.Join(",\n    ", swiftParams);
        var callArgsStr = string.Join(", ", callArgs);

        // Build return type
        var returnTypeSpec = methodDecl.CSSignature[0].SwiftTypeSpec;
        var hasReturn = !returnTypeSpec.IsEmptyTuple;
        var returnTypeStr = hasReturn ? $" -> {ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec)}" : "";
        var throwsStr = methodDecl.Throws ? " throws" : "";

        // Determine how to call the original method
        string callPrefix;
        string selfConversion = "";
        if (methodDecl.IsConstructor)
        {
            // Constructor: call Module.TypeName(...) — needs module-qualified name for nested types
            var typeName = parentDecl?.SwiftTypeName?.ModuleQualifiedName ?? parentDecl?.Name ?? "";
            callPrefix = $"{typeName}(";
        }
        else if (isInstance)
        {
            // Instance method: convert self pointer and call method
            var typeName = parentDecl?.SwiftTypeName?.ModuleQualifiedName ?? parentDecl?.Name ?? "";
            bool isClass = parentDecl is ClassDecl;
            if (isClass)
            {
                selfConversion = $"let __self = unsafeBitCast(OpaquePointer(_self), to: {typeName}.self)";
            }
            else
            {
                selfConversion = $"let __self = _self.assumingMemoryBound(to: {typeName}.self).pointee";
            }
            callPrefix = $"__self.{methodDecl.Name}(";
        }
        else if (parentDecl != null)
        {
            // Static method
            var typeName = parentDecl.SwiftTypeName?.ModuleQualifiedName ?? parentDecl.Name;
            callPrefix = $"{typeName}.{methodDecl.Name}(";
        }
        else
        {
            // Free function
            var moduleName = methodDecl.ModuleDecl?.Name ?? "";
            callPrefix = moduleName.Length > 0 ? $"{moduleName}.{methodDecl.Name}(" : $"{methodDecl.Name}(";
        }

        var callSuffix = methodDecl.IsConstructor ? ")" : ")";
        var tryPrefix = methodDecl.Throws ? "try " : "";

        // Emit the wrapper
        swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
        swiftWriter.WriteLine($"public func {NameProvider.GetPInvokeName(methodDecl)}(");
        swiftWriter.WriteLine($"    {paramsStr}");
        swiftWriter.WriteLine($"){throwsStr}{returnTypeStr} {{");

        // Emit self conversion for instance methods
        if (!string.IsNullOrEmpty(selfConversion))
        {
            swiftWriter.WriteLine($"    {selfConversion}");
        }

        // Emit adapter closure code
        foreach (var line in adapterCode)
        {
            swiftWriter.WriteLine($"    {line}");
        }

        // Emit the call
        var returnPrefix = hasReturn || methodDecl.IsConstructor ? "return " : "";
        swiftWriter.WriteLine($"    {returnPrefix}{tryPrefix}{callPrefix}{callArgsStr}{callSuffix}");
        swiftWriter.WriteLine("}");
        swiftWriter.WriteLine();
    }

    /// <summary>
    /// Gets the Swift argument label for a parameter.
    /// Reconstructs the original Swift label from the argument name.
    /// </summary>
    private static string GetSwiftArgLabel(ArgumentDecl arg)
    {
        var name = arg.Name;
        if (name.StartsWith("arg"))
            return ""; // Unlabeled
        if (name.StartsWith("_"))
            return $"{name.Substring(1)}: "; // Strip leading underscore
        return $"{name}: ";
    }
}
