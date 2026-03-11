// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits per-method @_cdecl Swift wrappers that route instance/static method P/Invokes
/// through C calling convention, eliminating CallConvSwift ABI mismatches on NativeAOT/ARM64.
///
/// For each method, generates a @_cdecl free function in the wrapper library that:
/// - Receives C-compatible parameters (primitives pass through, structs/classes as pointers)
/// - Reconstructs self for instance methods (class: Unmanaged, struct: load/pointer)
/// - Calls the actual Swift method
/// - Returns the result via C ABI (class → retained pointer, struct → writes to result buffer)
///
/// Handles throwing methods, mutating struct methods, string returns (SBW_Utf8Slice),
/// and static methods. Follows the ConstructorWrapperEmitter/PropertyWrapperEmitter pattern.
/// State tracked on <see cref="ModuleEmissionContext"/>.
/// </summary>
public static class MethodWrapperEmitter
{
    /// <summary>
    /// Pure query: determines whether a method should use a @_cdecl wrapper.
    /// Guards: xcframework mode, non-generic parent, non-constructor, non-accessor,
    /// no closures, no protocol existentials, no async, etc.
    /// </summary>
    public static bool ShouldEmitWrapper(MethodEnvironment env)
    {
        // 1. Must NOT be a constructor (constructors handled by ConstructorWrapperEmitter)
        if (env.MethodDecl.IsConstructor)
            return false;

        // 2. Must NOT be an accessor (property accessors handled by PropertyWrapperEmitter; subscripts deferred)
        if (env.MethodDecl.IsAccessor)
            return false;

        // 3. Must NOT already have a cdecl property wrapper
        if (env.MethodDecl.UsesCdeclPropertyWrapper)
            return false;

        // 4. xcframework mode required (wrapper library must exist)
        if (string.IsNullOrEmpty(env.TypeDatabase.AsyncLibraryName))
            return false;

        // 5. Must be on a type (not a free function on ModuleDecl)
        if (env.ParentDecl is not TypeDecl typeDecl)
            return false;

        // 5b. Non-generic parent type — @_cdecl can't express type parameters
        if (typeDecl.IsGeneric)
            return false;

        // 6. No method-level generics
        if (env.MethodDecl.IsGeneric)
            return false;

        // 6b. Actor types — actor-isolated methods require async context, @_cdecl is sync
        if (typeDecl is ClassDecl { IsActor: true })
            return false;

        // 7. Not async (async uses its own wrapper pattern)
        if (env.MethodDecl.IsAsync)
            return false;

        // 8. No closure parameters
        if (env.MethodDecl.CSSignature.Skip(1).Any(env.ClosureHandler.IsClosure))
            return false;

        // 9. No protocol existential parameters
        if (HasProtocolExistentialParameter(env))
            return false;

        // 10. No protocol existential return type
        if (ConstructorWrapperEmitter.IsProtocolExistentialType(
                env.MethodDecl.CSSignature.First().SwiftTypeSpec, env.TypeDatabase))
            return false;

        // 11. Non-copyable struct guards
        if (IsNonCopyableStructParent(env.ParentDecl))
            return false;

        // 12. No nested frozen struct parameters
        if (HasNestedFrozenStructParameter(env))
            return false;

        // 12b. No non-primitive frozen struct parameters — @_cdecl rejects "Swift structs
        // cannot be represented in Objective-C" for custom frozen struct types.
        // Primitives (Int, Float, Bool, CGFloat) and String are handled via GetCdeclParamMapping.
        if (HasNonPrimitiveFrozenStructParameter(env))
            return false;

        // 13. Not already using wrapper library (DebugParam, ArraySlice, etc. own the wrapper)
        if (env.MethodDecl.UsesWrapperLibrary)
            return false;

        // 14. No generic container params/returns
        if (HasGenericContainerParamsOrReturn(env))
            return false;

        var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;

        // 15. No opaque return types (some Protocol)
        if (returnSpec is ProtocolListTypeSpec { IsOpaque: true })
            return false;

        // 15b. No closure return types — closures can't be @_cdecl result types
        if (returnSpec is ClosureTypeSpec)
            return false;

        // 15c. No non-empty tuple return types — tuples have their own marshalling
        if (returnSpec is TupleTypeSpec trs && !trs.IsEmptyTuple)
            return false;

        // 15d. No DynamicSelf return
        if (returnSpec.IsDynamicSelf)
            return false;

        // 16. No large Optional params/returns
        if (env.BoundGenericsHandler.HasLargeOptionalParams(env.MethodDecl) ||
            env.BoundGenericsHandler.IsLargeOptionalReturn(env.MethodDecl))
            return false;

        // 17. No nested type returns
        if (returnSpec is NamedTypeSpec retNamed &&
            retNamed.HasModule() &&
            AppleFrameworkRegistry.IsNestedType(retNamed.Name))
            return false;

        return true;
    }

    /// <summary>
    /// Gets the @_cdecl symbol name for a method wrapper.
    /// Pure function — no side effects, safe to call before emission.
    /// </summary>
    public static string GetMethodSymbolName(string moduleName, string typeName, string methodName, string originalMangledName)
    {
        var hash = EmitterUtility.DeterministicHash8(originalMangledName);
        var safeTypeName = typeName.Replace(".", "_");
        return $"SBW_{moduleName}_{safeTypeName}_{methodName}_{hash}";
    }

    /// <summary>
    /// Emits a Swift @_cdecl wrapper function for a method.
    /// The wrapper receives C-compatible parameters, reconstructs self for instance methods,
    /// calls the Swift method, and returns the result via C ABI.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer for the wrapper .swift file.</param>
    /// <param name="env">The method environment with method info.</param>
    /// <param name="ctx">The per-module emission context for dedup tracking.</param>
    /// <param name="silgenTarget">Optional @_silgen_name symbol to call instead of direct method (for default param overloads).</param>
    public static void EmitSwiftMethodWrapper(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ModuleEmissionContext? ctx = null,
        string? silgenTarget = null)
    {
        ctx ??= ModuleEmissionContext.Default;

        var methodDecl = env.MethodDecl;
        var parentTypeDecl = env.ParentDecl as TypeDecl;
        if (parentTypeDecl == null) return;

        var symbolName = methodDecl.MangledName; // Already set to cdecl symbol by caller
        if (!ctx.TryAddMethodWrapperSymbol(symbolName))
            return; // Already emitted

        var moduleName = parentTypeDecl.SwiftTypeName.Module;
        var moduleQualifiedSwiftName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;

        bool isClass = env.ParentDecl is ClassDecl;
        bool isStatic = methodDecl.MethodType == MethodType.Static;
        bool isMutating = methodDecl.IsMutating;
        bool throws = methodDecl.Throws;

        // Determine return mapping
        var returnTypeSpec = methodDecl.CSSignature.First().SwiftTypeSpec;
        bool isVoidReturn = returnTypeSpec.IsEmptyTuple;
        bool isString = !isVoidReturn && WitnessDispatchEmitter.IsStringType(returnTypeSpec);

        var (returnMapping, needsResultPtr) = isVoidReturn
            ? (new PropertyWrapperEmitter.CdeclReturnMapping("Void", PropertyWrapperEmitter.CdeclReturnKind.Direct), false)
            : PropertyWrapperEmitter.GetCdeclReturnMapping(returnTypeSpec, env.TypeDatabase);

        // String returns always need result ptr (SBW_Utf8Slice)
        if (isString)
            needsResultPtr = true;

        // Ensure SBW_Utf8Slice infrastructure is emitted for string returns
        if (isString)
        {
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, ctx);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, ctx);
        }

        // Build Swift parameter list for the @_cdecl wrapper
        var swiftParams = new List<string>();

        // Result buffer parameter (first, for indirect results and string returns)
        if (needsResultPtr)
        {
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");
        }

        // Error out-pointer parameter (for throwing methods)
        if (throws)
        {
            swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
        }

        // Build parameter reconstruction lines and @_cdecl params
        var reconstructionLines = new List<string>();
        var callArgs = new List<string>();
        var keptArgs = methodDecl.CSSignature.Skip(1).ToList();

        // When calling a silgen target, all parameters use _ (no external labels).
        bool omitLabels = silgenTarget != null;

        for (int i = 0; i < keptArgs.Count; i++)
        {
            var arg = keptArgs[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var (cdeclParam, reconstruction, callArg) = ConstructorWrapperEmitter.GetCdeclParamMapping(arg, label, env, omitLabels);
            swiftParams.Add(cdeclParam);
            if (reconstruction != null)
                reconstructionLines.Add(reconstruction);
            callArgs.Add(callArg);
        }

        // Self parameter (instance methods only, last position)
        if (!isStatic)
        {
            if (isClass)
                swiftParams.Add("_ self_: UnsafeMutableRawPointer");
            else if (isMutating)
                swiftParams.Add("_ self_: UnsafeMutableRawPointer");
            else
                swiftParams.Add("_ self_: UnsafeRawPointer");
        }

        var swiftParamString = string.Join(", ", swiftParams);

        // Build return clause
        string returnClause;
        if (isVoidReturn || needsResultPtr)
            returnClause = "";
        else
            returnClause = $" -> {returnMapping.cdeclReturnType}";

        // Build the Swift function name
        var swiftFuncName = $"_sbw_method_{EmitterUtility.DeterministicHash8(symbolName)}";

        // Build call arguments string
        var callArgString = string.Join(", ", callArgs);

        // Build the call expression
        // For mutating methods, use through-pointer access so mutations write back.
        string selfRef;
        if (isStatic)
            selfRef = moduleQualifiedSwiftName;
        else if (isMutating && !isClass)
            selfRef = $"self_.assumingMemoryBound(to: {moduleQualifiedSwiftName}.self).pointee";
        else
            selfRef = "obj";

        string callExpr;
        if (silgenTarget != null)
        {
            // Default param overload: call the @_silgen_name wrapper via extension method
            callExpr = $"{selfRef}.{silgenTarget}({callArgString})";
        }
        else
        {
            callExpr = $"{selfRef}.{NameProvider.ParserNameToSwift(methodDecl)}({callArgString})";
        }

        // Emit the @_cdecl function
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Method @_cdecl wrapper for {{moduleQualifiedSwiftName}}.{{methodDecl.Name}}.
            // Routes method through C calling convention to avoid CallConvSwift crash on NativeAOT.
            """);

        // Add @MainActor annotation when the parent type or the method itself is @MainActor-isolated.
        // Note: IsActorIsolated specifically tracks @MainActor member annotations (not custom actors).
        // Custom actors are excluded by the IsActor guard in ShouldEmitWrapper.
        if (parentTypeDecl.IsMainActorIsolated || methodDecl.IsActorIsolated)
        {
            swiftWriter.WriteLine("@MainActor");
        }

        swiftWriter.WriteLines($$"""
            @_cdecl("{{symbolName}}")
            """);

        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){returnClause} {{");
        swiftWriter.Indent++;

        // Emit parameter reconstruction lines
        foreach (var line in reconstructionLines)
        {
            swiftWriter.WriteLine(line);
        }

        // Reconstruct self for instance methods
        if (!isStatic)
        {
            EmitSelfReconstruction(swiftWriter, isClass, isMutating, moduleQualifiedSwiftName);
        }

        // Emit the body based on method characteristics
        if (throws)
        {
            EmitThrowingMethodBody(swiftWriter, callExpr, returnTypeSpec, returnMapping,
                needsResultPtr, isVoidReturn, isString, env.TypeDatabase);
        }
        else if (isVoidReturn)
        {
            swiftWriter.WriteLine(callExpr);
        }
        else if (isString)
        {
            EmitStringReturnBody(swiftWriter, callExpr);
        }
        else if (needsResultPtr)
        {
            // Non-frozen struct or complex enum: write to result buffer
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
            swiftWriter.WriteLine($"let result = {callExpr}");
            swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {swiftType}.self, repeating: result, count: 1)");
        }
        else
        {
            EmitDirectReturn(swiftWriter, callExpr, returnTypeSpec, env.TypeDatabase, returnMapping);
        }

        // For mutating struct methods, write back the mutated value
        if (!isStatic && isMutating && !isClass)
        {
            // The struct was loaded from self_ pointer; mutations happened on obj.
            // Write back the mutated value.
            // (Handled inline by using through-pointer access for mutating methods)
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits self reconstruction for instance methods.
    /// </summary>
    private static void EmitSelfReconstruction(SwiftWriter swiftWriter, bool isClass, bool isMutating, string moduleQualifiedSwiftName)
    {
        if (isClass)
        {
            swiftWriter.WriteLine($"let obj = Unmanaged<{moduleQualifiedSwiftName}>.fromOpaque(self_).takeUnretainedValue()");
        }
        else if (isMutating)
        {
            // Mutating method: use through-pointer access (self_.assumingMemoryBound(...).pointee)
            // so mutations write back. No separate obj variable needed — callExpr uses pointer directly.
        }
        else
        {
            swiftWriter.WriteLine($"let obj = self_.load(as: {moduleQualifiedSwiftName}.self)");
        }
    }

    /// <summary>
    /// Emits the body of a throwing method wrapper.
    /// </summary>
    private static void EmitThrowingMethodBody(
        SwiftWriter swiftWriter,
        string callExpr,
        TypeSpec returnTypeSpec,
        PropertyWrapperEmitter.CdeclReturnMapping returnMapping,
        bool needsResultPtr,
        bool isVoidReturn,
        bool isString,
        ITypeDatabase typeDatabase)
    {
        swiftWriter.WriteLine("do {");
        swiftWriter.Indent++;

        if (isVoidReturn)
        {
            swiftWriter.WriteLine($"try {callExpr}");
        }
        else if (isString)
        {
            EmitStringReturnBody(swiftWriter, $"try {callExpr}");
        }
        else if (needsResultPtr)
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
            swiftWriter.WriteLine($"let result = try {callExpr}");
            swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {swiftType}.self, repeating: result, count: 1)");
        }
        else
        {
            EmitDirectReturn(swiftWriter, $"try {callExpr}", returnTypeSpec, typeDatabase, returnMapping);
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLines("""
            } catch {
                errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
            """);

        // For non-void direct returns (not via resultPtr), we need a dummy return value.
        // For resultPtr and void, Swift is happy with just the error assignment.
        if (!isVoidReturn && !needsResultPtr)
        {
            // Return a sentinel value matching the return type
            switch (returnMapping.Kind)
            {
                case PropertyWrapperEmitter.CdeclReturnKind.Bool:
                    swiftWriter.WriteLine("    return 0");
                    break;
                case PropertyWrapperEmitter.CdeclReturnKind.SimpleEnum:
                    swiftWriter.WriteLine("    return 0");
                    break;
                case PropertyWrapperEmitter.CdeclReturnKind.ClassPointer:
                    swiftWriter.WriteLine("    return UnsafeMutableRawPointer(bitPattern: 1)!");
                    break;
                case PropertyWrapperEmitter.CdeclReturnKind.Direct:
                    swiftWriter.WriteLine("    return 0");
                    break;
            }
        }

        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits the string return body using SBW_Utf8Slice pattern.
    /// Writes result to resultPtr because @_cdecl can't return Swift structs.
    /// </summary>
    private static void EmitStringReturnBody(SwiftWriter swiftWriter, string callExpr)
    {
        swiftWriter.WriteLines($$"""
            let result = {{callExpr}}
            let utf8 = Array(result.utf8)
            if utf8.isEmpty {
                resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: &_sbw_emptyBuffer, len: 0), as: SBW_Utf8Slice.self)
                return
            }
            let ptr = UnsafeMutablePointer<UInt8>.allocate(capacity: utf8.count)
            ptr.initialize(from: utf8, count: utf8.count)
            resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: ptr, len: utf8.count), as: SBW_Utf8Slice.self)
            """);
    }

    /// <summary>
    /// Emits a direct return statement for non-string, non-indirect-result returns.
    /// </summary>
    private static void EmitDirectReturn(SwiftWriter swiftWriter, string callExpr,
        TypeSpec typeSpec, ITypeDatabase typeDatabase, PropertyWrapperEmitter.CdeclReturnMapping mapping)
    {
        switch (mapping.Kind)
        {
            case PropertyWrapperEmitter.CdeclReturnKind.Bool:
                swiftWriter.WriteLine($"return ({callExpr}) ? 1 : 0");
                break;

            case PropertyWrapperEmitter.CdeclReturnKind.SimpleEnum:
                if (typeDatabase.TryGetTypeRecord(typeSpec, out var enumRecord) &&
                    !string.IsNullOrEmpty(enumRecord.RawValueTypeName))
                {
                    swiftWriter.WriteLine($"return {mapping.cdeclReturnType}(({callExpr}).rawValue)");
                }
                else
                {
                    swiftWriter.WriteLine($"var result = {callExpr}");
                    swiftWriter.WriteLine($"return withUnsafePointer(to: &result) {{ UnsafeRawPointer($0).load(as: {mapping.cdeclReturnType}.self) }}");
                }
                break;

            case PropertyWrapperEmitter.CdeclReturnKind.ClassPointer:
                swiftWriter.WriteLine($"return Unmanaged.passRetained({callExpr}).toOpaque()");
                break;

            case PropertyWrapperEmitter.CdeclReturnKind.Direct:
            default:
                swiftWriter.WriteLine($"return {callExpr}");
                break;
        }
    }

    /// <summary>
    /// Checks whether any method parameter is a protocol existential type.
    /// </summary>
    private static bool HasProtocolExistentialParameter(MethodEnvironment env)
    {
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (env.ExistentialHandler.IsExistential(arg.SwiftTypeSpec))
                return true;

            if (arg.SwiftTypeSpec is NamedTypeSpec namedSpec &&
                env.TypeDatabase.TryGetTypeRecord(namedSpec, out var typeRecord) &&
                (typeRecord.Kind == TypeRecordKind.Protocol || typeRecord.Kind == TypeRecordKind.Existential))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if a parent decl is a non-copyable struct.
    /// </summary>
    private static bool IsNonCopyableStructParent(BaseDecl? parentDecl)
    {
        if (parentDecl is StructDecl structDecl)
        {
            return structDecl.Conformances.Any(c => c.Protocol.ToString() == "Swift.Escapable") &&
                   !structDecl.Conformances.Any(c => c.Protocol.ToString() == "Swift.Copyable");
        }
        return false;
    }

    /// <summary>
    /// Checks whether any parameter is a nested frozen struct type.
    /// </summary>
    private static bool HasNestedFrozenStructParameter(MethodEnvironment env)
    {
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec is not NamedTypeSpec namedSpec)
                continue;

            if (!env.TypeDatabase.TryGetTypeRecord(namedSpec, out var typeRecord))
                continue;

            if (typeRecord.Kind != TypeRecordKind.Struct)
                continue;

            if (!MarshallingHelpers.IsTypeFrozen(typeRecord))
                continue;

            var name = namedSpec.Name;
            var dotIndex = name.IndexOf('.');
            if (dotIndex >= 0)
            {
                var afterModule = name.Substring(dotIndex + 1);
                if (afterModule.Contains('.'))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks whether any parameter is a non-primitive frozen struct type.
    /// @_cdecl rejects Swift struct types that aren't C-representable.
    /// Primitives (Int, Float, Bool, etc.) and String pass through GetCdeclParamMapping fine.
    /// Classes, enums, non-frozen structs are marshalled as pointers.
    /// Only frozen non-primitive structs trigger the "cannot be represented in Objective-C" error.
    /// </summary>
    private static bool HasNonPrimitiveFrozenStructParameter(MethodEnvironment env)
    {
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            var spec = arg.SwiftTypeSpec;

            // Primitives are fine
            if (ConstructorWrapperEmitter.IsCdeclPrimitive(spec))
                continue;

            // String is handled specially (two Int words)
            if (spec is NamedTypeSpec strNamed && strNamed.Name == "Swift.String")
                continue;

            // Check if it's a frozen struct
            if (env.TypeDatabase.TryGetTypeRecord(spec, out var typeRecord) &&
                typeRecord.Kind == TypeRecordKind.Struct &&
                MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                // This is a frozen struct parameter that isn't primitive or String —
                // @_cdecl can't represent it
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks whether any parameter or the return type is a generic container type.
    /// </summary>
    private static bool HasGenericContainerParamsOrReturn(MethodEnvironment env)
    {
        // Check return type
        var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;
        if (ConstructorWrapperEmitter.IsGenericContainerType(returnSpec))
            return true;

        // Check parameters
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (ConstructorWrapperEmitter.IsGenericContainerType(arg.SwiftTypeSpec))
                return true;
        }
        return false;
    }
}
