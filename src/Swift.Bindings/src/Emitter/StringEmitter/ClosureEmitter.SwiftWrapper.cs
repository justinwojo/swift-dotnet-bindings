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
            paramTypes.Add(GetSwiftCdeclParamType(arg, closureHandler));
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
    private static string GetSwiftCdeclParamType(TypeSpec typeSpec, ClosureHandler? closureHandler = null)
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

        return GetSwiftCdeclParamType(closureTypeSpec.ReturnType, closureHandler);
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

        // Build closure parameter list and identify complex enum args needing heap allocation
        var closureParams = new List<string>();
        var heapAllocArgs = new List<(int index, string swiftType)>();
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg);
            closureParams.Add($"p{argIndex}: {swiftType}");

            // D1: Complex enums use heap allocation — track for cdecl arg substitution
            if (closureHandler != null && closureHandler.IsComplexEnum(arg))
                heapAllocArgs.Add((argIndex, swiftType));

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
            var heapArg = heapAllocArgs.FirstOrDefault(h => h.index == argIndex);
            if (heapArg != default)
            {
                // Complex enum: use heap pointer (allocation emitted before cdecl call)
                cdeclArgs.Add($"__heap_{argIndex}");
            }
            else
            {
                cdeclArgs.Add(GetSwiftArgConversion(arg, $"p{argIndex}", closureHandler));
            }
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

        // D1: Generate heap allocation lines for complex enum args
        var heapAllocLines = new List<string>();
        foreach (var (idx, swiftType) in heapAllocArgs)
        {
            heapAllocLines.Add($"{indent}    let __heap_{idx} = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<{swiftType}>.size, alignment: MemoryLayout<{swiftType}>.alignment)");
            heapAllocLines.Add($"{indent}    __heap_{idx}.initializeMemory(as: {swiftType}.self, repeating: p{idx}, count: 1)");
        }

        if (isIndirectReturn)
        {
            // Indirect return: closure writes result to buffer, returns void
            var returnSwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(closureTypeSpec.ReturnType);
            lines.Add($"{indent}{letPrefix}{adapterName} = {{ ({closureParamsStr}) -> {returnSwiftType} in");
            lines.AddRange(heapAllocLines);
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
            lines.AddRange(heapAllocLines);
            lines.Add($"{indent}    var errorPtr: UnsafeMutableRawPointer? = nil");

            if (hasReturn)
            {
                var returnConversion = GetSwiftReturnConversion(closureTypeSpec.ReturnType, $"{cdeclVarName}({cdeclArgsStr})", closureHandler);
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
            lines.AddRange(heapAllocLines);

            if (hasReturn)
            {
                var returnConversion = GetSwiftReturnConversion(closureTypeSpec.ReturnType, $"{cdeclVarName}({cdeclArgsStr})", closureHandler);
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
    /// E.g., Bool → (p0 ? 1 : 0), classes → Unmanaged.passUnretained, enums → unsafeBitCast.
    /// </summary>
    private static string GetSwiftArgConversion(TypeSpec typeSpec, string argExpr, ClosureHandler? closureHandler = null)
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

            if (closureHandler != null)
            {
                // Classes and ObjC-bridged: convert to raw pointer via Unmanaged
                if (closureHandler.IsClassType(named) || closureHandler.IsObjCBridgedClass(named))
                    return $"Unmanaged.passUnretained({argExpr}).toOpaque()";

                // Simple enums: bitcast to underlying integer type
                var enumInfo = closureHandler.GetSimpleEnumInfo(named);
                if (enumInfo != null)
                    return $"unsafeBitCast({argExpr}, to: {enumInfo.Value.swiftScalar}.self)";

                // Optional<Class/ObjC>: map to Optional raw pointer, nil maps to nil
                if (named.ContainsGenericParameters && named.Name == "Swift.Optional" &&
                    named.GenericParameters.Count == 1 &&
                    closureHandler.IsReferenceType(named.GenericParameters[0]))
                {
                    return $"{argExpr}.map {{ Unmanaged.passUnretained($0).toOpaque() }}";
                }
            }
        }

        return argExpr;
    }

    /// <summary>
    /// Converts the raw Cdecl return value back to the Swift type.
    /// E.g., UInt8 → (rawResult != 0) for Bool, class pointer → Unmanaged.fromOpaque.
    /// </summary>
    private static string GetSwiftReturnConversion(TypeSpec typeSpec, string expr, ClosureHandler? closureHandler = null)
    {
        if (typeSpec is NamedTypeSpec named)
        {
            if (named.Name == "Swift.Bool")
                return $"({expr}) != 0";

            if (closureHandler != null)
            {
                // Classes: raw pointer → Unmanaged.fromOpaque.takeUnretainedValue
                if (closureHandler.IsClassType(named))
                {
                    var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(named);
                    return $"Unmanaged<{swiftType}>.fromOpaque({expr}).takeUnretainedValue()";
                }

                // ObjC-bridged: same pattern as classes
                if (closureHandler.IsObjCBridgedClass(named))
                {
                    var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(named);
                    return $"Unmanaged<{swiftType}>.fromOpaque({expr}).takeUnretainedValue()";
                }

                // Simple enums: bitcast from underlying integer
                var enumInfo = closureHandler.GetSimpleEnumInfo(named);
                if (enumInfo != null)
                {
                    var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(named);
                    return $"unsafeBitCast({expr}, to: {swiftType}.self)";
                }

                // Optional<Class/ObjC>: map raw pointer back to typed optional
                if (named.ContainsGenericParameters && named.Name == "Swift.Optional" &&
                    named.GenericParameters.Count == 1 &&
                    closureHandler.IsReferenceType(named.GenericParameters[0]))
                {
                    var innerType = ExistentialBypassEmitter.RenderSwiftTypeSpec(named.GenericParameters[0]);
                    return $"({expr}).map {{ Unmanaged<{innerType}>.fromOpaque($0).takeUnretainedValue() }}";
                }
            }
        }

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
    /// Supported types: primitives, Bool, Void, pointer types, classes, simple enums,
    /// ObjC-bridged types, and Optional&lt;Class/ObjC&gt; (nil-pointer ABI).
    /// Complex types (String, non-frozen structs, complex enums) require full marshalling
    /// which is not yet implemented in the Cdecl wrapper path.
    /// </summary>
    internal static bool IsCdeclCompatibleType(TypeSpec typeSpec, ClosureHandler closureHandler)
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

            // Classes and ObjC-bridged types pass as UnsafeMutableRawPointer (pointer ABI)
            if (closureHandler.IsClassType(named))
                return true;
            if (closureHandler.IsObjCBridgedClass(named))
                return true;

            // Simple enums pass as their underlying integer type (value ABI)
            if (closureHandler.IsSimpleEnum(named))
                return true;

            // Optional<Class/ObjC> uses nil-pointer ABI (pointer-sized)
            if (named.ContainsGenericParameters && named.Name == "Swift.Optional" &&
                named.GenericParameters.Count == 1)
            {
                var inner = named.GenericParameters[0];
                // Only Optional<Class> and Optional<ObjC-bridged> — nil-pointer ABI
                if (closureHandler.IsReferenceType(inner))
                    return true;
                // Optional<Primitive> and Optional<SimpleEnum> have different ABI — not supported here
                return false;
            }
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
            if (!IsCdeclCompatibleType(arg, closureHandler))
                return false;
        }

        // Check return type (indirect return closures write to buffer — return type must be Cdecl-compatible for load)
        if (!closureTypeSpec.ReturnType.IsEmptyTuple && !IsCdeclCompatibleType(closureTypeSpec.ReturnType, closureHandler))
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
            var csName = NameProvider.StripVerbatimPrefix(NameProvider.GetCSharpParameterName(arg));
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
            else if (OptionalPointerWrapperEmitter.ShouldWidenParam(arg, env.BoundGenericsHandler))
            {
                // Large Optional param: accept UnsafeRawPointer, dereference in body
                swiftParams.Add($"_ {swiftName}: UnsafeRawPointer");
                adapterCode.Add(OptionalPointerWrapperEmitter.GetDerefCode(arg, csName, swiftName));
                var label = GetSwiftArgLabel(arg);
                callArgs.Add($"{label}{csName}Val");
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

        // Check if the return type is a large Optional that needs an out-buffer
        bool hasLargeOptionalReturn = env.BoundGenericsHandler.IsLargeOptionalReturn(methodDecl);

        // Add result buffer parameter before self (if large Optional return)
        if (hasLargeOptionalReturn)
        {
            swiftParams.Add("_ _resultBuf: UnsafeMutableRawPointer");
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
        var hasReturn = !returnTypeSpec.IsEmptyTuple && !hasLargeOptionalReturn;
        var returnSwiftTypeName = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
        var returnTypeStr = hasReturn ? $" -> {returnSwiftTypeName}" : "";
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
            callPrefix = $"__self.{NameProvider.ParserNameToSwift(methodDecl)}(";
        }
        else if (parentDecl != null)
        {
            // Static method
            var typeName = parentDecl.SwiftTypeName?.ModuleQualifiedName ?? parentDecl.Name;
            callPrefix = $"{typeName}.{NameProvider.ParserNameToSwift(methodDecl)}(";
        }
        else
        {
            // Free function
            var moduleName = methodDecl.ModuleDecl?.Name ?? "";
            var escapedName = NameProvider.ParserNameToSwift(methodDecl);
            callPrefix = moduleName.Length > 0 ? $"{moduleName}.{escapedName}(" : $"{escapedName}(";
        }

        var callSuffix = methodDecl.IsConstructor ? ")" : ")";
        var tryPrefix = methodDecl.Throws ? "try " : "";

        // Emit the wrapper — add @MainActor if the parent type is actor-isolated and method is not nonisolated
        bool needsMainActor = (parentDecl?.IsMainActorIsolated == true
            || methodDecl.IsActorIsolated)
            && !methodDecl.IsNonisolated;
        if (needsMainActor)
            swiftWriter.WriteLine("@MainActor");
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
        if (hasLargeOptionalReturn)
        {
            var callExpr = $"{tryPrefix}{callPrefix}{callArgsStr}{callSuffix}";
            var bufferLines = OptionalPointerWrapperEmitter.GetReturnBufferCode(callExpr, returnSwiftTypeName);
            foreach (var bufLine in bufferLines)
                swiftWriter.WriteLine($"    {bufLine}");
        }
        else
        {
            var returnPrefix = hasReturn || methodDecl.IsConstructor ? "return " : "";
            swiftWriter.WriteLine($"    {returnPrefix}{tryPrefix}{callPrefix}{callArgsStr}{callSuffix}");
        }
        swiftWriter.WriteLine("}");
        swiftWriter.WriteLine();
    }

    /// <summary>
    /// Gets the Swift argument label for a parameter.
    /// Reconstructs the original Swift label from the argument name.
    /// Used by MethodWrapperEmitter and ConstructorWrapperEmitter for closure call args.
    /// </summary>
    internal static string GetSwiftArgLabelForCdecl(ArgumentDecl arg)
    {
        return GetSwiftArgLabel(arg);
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
