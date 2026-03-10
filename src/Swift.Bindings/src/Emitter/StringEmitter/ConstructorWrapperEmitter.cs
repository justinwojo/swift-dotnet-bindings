// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits per-constructor @_cdecl Swift wrappers that route constructor P/Invokes
/// through C calling convention, eliminating CallConvSwift ABI mismatches on NativeAOT/ARM64.
///
/// For each constructor, generates a @_cdecl free function in the wrapper library that:
/// - Receives C-compatible parameters (primitives pass through, structs/classes as UnsafeRawPointer)
/// - Calls the actual Swift init
/// - Returns the result via C ABI (class → retained pointer, struct → writes to result buffer)
///
/// Handles failable (init?), throwing (init() throws), and combined (init?() throws) constructors.
/// Follows the DestroyWrapperEmitter pattern. State tracked on <see cref="ModuleEmissionContext"/>.
/// </summary>
public static class ConstructorWrapperEmitter
{
    /// <summary>
    /// Pure query: determines whether a constructor should use a @_cdecl wrapper.
    /// Guards: xcframework mode (wrapper lib exists), non-generic parent type,
    /// no closure parameters (deferred to follow-up).
    /// </summary>
    public static bool ShouldEmitWrapper(MethodEnvironment env)
    {
        if (!env.MethodDecl.IsConstructor)
            return false;

        // Only in xcframework mode where the wrapper library exists
        if (string.IsNullOrEmpty(env.TypeDatabase.AsyncLibraryName))
            return false;

        // Skip generic parent types — Swift can't express generic params in @_cdecl free functions
        if (env.ParentDecl is TypeDecl typeDecl && typeDecl.IsGeneric)
            return false;

        // Skip constructors with closure parameters (deferred complexity)
        if (env.MethodDecl.CSSignature.Skip(1).Any(env.ClosureHandler.IsClosure))
            return false;

        // Skip async constructors (async uses its own wrapper pattern)
        if (env.MethodDecl.IsAsync)
            return false;

        return true;
    }

    /// <summary>
    /// Gets the @_cdecl symbol name for a constructor wrapper.
    /// Pure function — no side effects, safe to call before emission.
    /// </summary>
    /// <param name="moduleName">The Swift module name (e.g., "Lottie").</param>
    /// <param name="typeName">The Swift type name (e.g., "LottieAnimationView").</param>
    /// <param name="originalMangledName">The original mangled name to hash for uniqueness.</param>
    public static string GetConstructorSymbolName(string moduleName, string typeName, string originalMangledName)
    {
        var hash = EmitterUtility.DeterministicHash8(originalMangledName);
        var safeTypeName = typeName.Replace(".", "_");
        return $"SBW_{moduleName}_{safeTypeName}_init_{hash}";
    }

    /// <summary>
    /// Emits a Swift @_cdecl wrapper function for a constructor.
    /// The wrapper receives C-compatible parameters, calls the Swift init,
    /// and returns the result via C ABI.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer for the wrapper .swift file.</param>
    /// <param name="env">The method environment with constructor info.</param>
    /// <param name="ctx">The per-module emission context for dedup tracking.</param>
    /// <param name="silgenTarget">Optional @_silgen_name symbol to call instead of direct init (for default param overloads).</param>
    public static void EmitSwiftConstructorWrapper(
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
        if (!ctx.TryAddConstructorWrapperSymbol(symbolName))
            return; // Already emitted

        var moduleName = parentTypeDecl.SwiftTypeName.Module;
        var moduleQualifiedSwiftName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;

        bool isClass = env.ParentDecl is ClassDecl;
        bool isFailable = methodDecl.IsFailable;
        bool throws = methodDecl.Throws;
        bool requiresIndirectResult = !isClass || isFailable; // Structs always, failable always

        // For classes: non-failable returns pointer, failable still uses indirect for Optional<Self>
        // Actually: failable class returns UnsafeMutableRawPointer? (nullable pointer)
        // Failable struct writes Optional<Self> to result buffer
        bool isFailableClass = isFailable && isClass;
        bool isFailableStruct = isFailable && !isClass;
        bool needsResultBuffer = !isClass || isFailableStruct;
        // Non-failable class: returns pointer directly (no result buffer)
        // Failable class: returns nullable pointer (no result buffer)
        // Non-failable struct: writes to result buffer
        // Failable struct: writes Optional<Self> to result buffer
        needsResultBuffer = !isClass;

        // Build Swift parameter list for the @_cdecl wrapper
        var swiftParams = new List<string>();

        // Result buffer parameter (first, for struct constructors)
        if (needsResultBuffer)
        {
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");
        }

        // Error out-pointer parameter (for throwing constructors)
        if (throws)
        {
            swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
        }

        // Build parameter reconstruction lines and @_cdecl params
        var reconstructionLines = new List<string>();
        var callArgs = new List<string>();
        var keptArgs = methodDecl.CSSignature.Skip(1).ToList();

        for (int i = 0; i < keptArgs.Count; i++)
        {
            var arg = keptArgs[i];
            // Skip debug params and empty tuples (already stripped by this point)
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var (cdeclParam, reconstruction, callArg) = GetCdeclParamMapping(arg, label, env);
            swiftParams.Add(cdeclParam);
            if (reconstruction != null)
                reconstructionLines.Add(reconstruction);
            callArgs.Add(callArg);
        }

        var swiftParamString = string.Join(", ", swiftParams);

        // Build return type
        string returnClause;
        if (isClass && !isFailable)
            returnClause = " -> UnsafeMutableRawPointer";
        else if (isFailableClass)
            returnClause = " -> UnsafeMutableRawPointer?";
        else
            returnClause = ""; // void (writes to resultPtr)

        // Build the Swift function name (internal, doesn't need to be pretty)
        var swiftFuncName = $"_sbw_init_{EmitterUtility.DeterministicHash8(symbolName)}";

        // Build call arguments string
        var callArgString = string.Join(", ", callArgs);

        // Build the call expression
        string callExpr;
        if (silgenTarget != null)
        {
            // Default param overload: call the @_silgen_name wrapper via its extension method
            // The @_silgen_name wrapper is a static factory on the type
            callExpr = $"{moduleQualifiedSwiftName}.{silgenTarget}({callArgString})";
        }
        else
        {
            callExpr = $"{moduleQualifiedSwiftName}({callArgString})";
        }

        // Emit the @_cdecl function
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Constructor @_cdecl wrapper for {{moduleQualifiedSwiftName}}.
            // Routes constructor through C calling convention to avoid CallConvSwift crash on NativeAOT.
            @_cdecl("{{symbolName}}")
            """);

        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){returnClause} {{");
        swiftWriter.Indent++;

        // Emit parameter reconstruction lines
        foreach (var line in reconstructionLines)
        {
            swiftWriter.WriteLine(line);
        }

        // Emit the body based on constructor type
        if (throws && isClass && !isFailable)
        {
            // Throwing class constructor
            EmitThrowingClassBody(swiftWriter, callExpr);
        }
        else if (throws && isFailableClass)
        {
            // Failable + throwing class constructor
            EmitFailableThrowingClassBody(swiftWriter, callExpr);
        }
        else if (throws && !isClass)
        {
            // Throwing struct constructor
            EmitThrowingStructBody(swiftWriter, callExpr, moduleQualifiedSwiftName, isFailable);
        }
        else if (isFailableClass)
        {
            // Failable class constructor (non-throwing)
            swiftWriter.WriteLine($"guard let result = {callExpr} else {{ return nil }}");
            swiftWriter.WriteLine("return Unmanaged.passRetained(result).toOpaque()");
        }
        else if (isClass)
        {
            // Non-failable, non-throwing class constructor
            swiftWriter.WriteLine($"let result = {callExpr}");
            swiftWriter.WriteLine("return Unmanaged.passRetained(result).toOpaque()");
        }
        else if (isFailableStruct)
        {
            // Failable struct constructor (non-throwing)
            swiftWriter.WriteLine($"let result: {moduleQualifiedSwiftName}? = {callExpr}");
            swiftWriter.WriteLine($"resultPtr.initializeMemory(as: Optional<{moduleQualifiedSwiftName}>.self, repeating: result, count: 1)");
        }
        else
        {
            // Non-failable, non-throwing struct constructor
            swiftWriter.WriteLine($"let result = {callExpr}");
            swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {moduleQualifiedSwiftName}.self, repeating: result, count: 1)");
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Maps a constructor parameter to its @_cdecl-compatible Swift type, reconstruction code,
    /// and call argument expression.
    /// </summary>
    private static (string cdeclParam, string? reconstruction, string callArg) GetCdeclParamMapping(
        ArgumentDecl arg, string label, MethodEnvironment env)
    {
        var swiftTypeSpec = arg.SwiftTypeSpec;

        // Determine the Swift argument label for the init call
        var argLabel = arg.Name switch
        {
            var n when n.StartsWith("arg") => "",
            var n when n.StartsWith("_") => $"{n.Substring(1)}: ",
            var n when string.IsNullOrEmpty(n) => "",
            var n => $"{n}: "
        };

        // Primitives pass through directly
        if (IsCdeclPrimitive(swiftTypeSpec))
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);

            // Bool: Swift @_cdecl receives Int8, needs != 0 conversion
            if (MarshallingHelpers.IsBoolType(swiftType) || swiftType == "Bool")
            {
                return ($"_ {label}: Int8",
                        $"let {label}Val = {label} != 0",
                        $"{argLabel}{label}Val");
            }

            return ($"_ {label}: {swiftType}", null, $"{argLabel}{label}");
        }

        // Classes: receive as UnsafeMutableRawPointer, reconstruct via Unmanaged
        if (env.TypeDatabase.TryGetTypeRecord(swiftTypeSpec, out var typeRecord))
        {
            if (typeRecord.Kind == TypeRecordKind.Class ||
                MarshallingHelpers.IsObjCBridged(typeRecord) ||
                MarshallingHelpers.IsObjCRooted(typeRecord))
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeMutableRawPointer",
                        $"let {label}Val = Unmanaged<{swiftType}>.fromOpaque({label}).takeUnretainedValue()",
                        $"{argLabel}{label}Val");
            }

            // Simple enums: pass raw value directly
            if (typeRecord.Kind == TypeRecordKind.Enum && typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);
                var rawType = GetSwiftRawValueType(typeRecord.RawValueTypeName);
                return ($"_ {label}: {rawType}",
                        $"let {label}Val = {swiftType}(rawValue: {label})!",
                        $"{argLabel}{label}Val");
            }

            // Complex enums: pass as pointer
            if (typeRecord.Kind == TypeRecordKind.Enum)
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeRawPointer",
                        $"let {label}Val = {label}.load(as: {swiftType}.self)",
                        $"{argLabel}{label}Val");
            }

            // Non-frozen structs: pass as pointer, load value
            if (!MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeRawPointer",
                        $"let {label}Val = {label}.load(as: {swiftType}.self)",
                        $"{argLabel}{label}Val");
            }

            // Frozen structs with memory management: pass as pointer
            if (MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeRawPointer",
                        $"let {label}Val = {label}.load(as: {swiftType}.self)",
                        $"{argLabel}{label}Val");
            }

            // Frozen structs (blittable): pass as pointer, load value
            if (MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeRawPointer",
                        $"let {label}Val = {label}.load(as: {swiftType}.self)",
                        $"{argLabel}{label}Val");
            }
        }

        // Fallback: pass as UnsafeRawPointer
        var fallbackSwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);
        return ($"_ {label}: UnsafeRawPointer",
                $"let {label}Val = {label}.load(as: {fallbackSwiftType}.self)",
                $"{argLabel}{label}Val");
    }

    /// <summary>
    /// Returns true for types that can be passed directly through the C ABI
    /// without pointer wrapping (integers, floats, etc.).
    /// </summary>
    private static bool IsCdeclPrimitive(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec named)
            return false;

        return named.Name switch
        {
            "Swift.Int" or "Swift.UInt" or "Swift.Int8" or "Swift.UInt8" or
            "Swift.Int16" or "Swift.UInt16" or "Swift.Int32" or "Swift.UInt32" or
            "Swift.Int64" or "Swift.UInt64" or
            "Swift.Float" or "Swift.Double" or "Swift.Bool" or
            "CoreFoundation.CGFloat" => true,
            _ => false
        };
    }

    /// <summary>
    /// Maps C# enum underlying type names to Swift raw value type names.
    /// </summary>
    private static string GetSwiftRawValueType(string? rawValueTypeName) => rawValueTypeName switch
    {
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
        "Swift.String" => "String",
        _ => "Int" // fallback
    };

    /// <summary>
    /// Emits the body of a throwing class constructor wrapper.
    /// </summary>
    private static void EmitThrowingClassBody(SwiftWriter sw, string callExpr)
    {
        sw.WriteLines($$"""
            do {
                let result = try {{callExpr}}
                return Unmanaged.passRetained(result).toOpaque()
            } catch {
                errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                return UnsafeMutableRawPointer(bitPattern: 1)!
            }
            """);
    }

    /// <summary>
    /// Emits the body of a failable+throwing class constructor wrapper.
    /// </summary>
    private static void EmitFailableThrowingClassBody(SwiftWriter sw, string callExpr)
    {
        sw.WriteLines($$"""
            do {
                guard let result = try {{callExpr}} else { return nil }
                return Unmanaged.passRetained(result).toOpaque()
            } catch {
                errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                return nil
            }
            """);
    }

    /// <summary>
    /// Emits the body of a throwing struct constructor wrapper.
    /// </summary>
    private static void EmitThrowingStructBody(SwiftWriter sw, string callExpr, string swiftTypeName, bool isFailable)
    {
        if (isFailable)
        {
            sw.WriteLines($$"""
                do {
                    let result: {{swiftTypeName}}? = try {{callExpr}}
                    resultPtr.initializeMemory(as: Optional<{{swiftTypeName}}>.self, repeating: result, count: 1)
                } catch {
                    errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                }
                """);
        }
        else
        {
            sw.WriteLines($$"""
                do {
                    let result = try {{callExpr}}
                    resultPtr.initializeMemory(as: {{swiftTypeName}}.self, repeating: result, count: 1)
                } catch {
                    errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                }
                """);
        }
    }
}
