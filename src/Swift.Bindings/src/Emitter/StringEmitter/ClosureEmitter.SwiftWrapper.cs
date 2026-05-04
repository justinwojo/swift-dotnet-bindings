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
            paramTypes.AddRange(GetSwiftCdeclParamTypesForArg(arg, closureHandler));
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
    /// Delegates to the canonical implementation in SwiftBuilder.
    /// </summary>
    private static string GetSwiftCdeclParamType(TypeSpec typeSpec, ClosureHandler? closureHandler = null)
        => SwiftBuilder.GetSwiftCdeclParamType(typeSpec, closureHandler);

    /// <summary>
    /// Returns the Swift @convention(c) parameter types for a closure argument, expanding
    /// buffer-pointer types into a (pointer, count) pair. UnsafeRawBufferPointer is a 16-byte
    /// struct (baseAddress + count) that cannot cross the @convention(c) boundary intact —
    /// the callback callee must split it into two separate scalar parameters. All other
    /// types map 1:1 to a single cdecl param via <see cref="GetSwiftCdeclParamType"/>.
    /// </summary>
    private static IEnumerable<string> GetSwiftCdeclParamTypesForArg(TypeSpec arg, ClosureHandler closureHandler)
    {
        if (MarshallingHelpers.IsAnyUnsafeRawBufferPointer(arg) && arg is NamedTypeSpec named)
        {
            var ptrType = named.Name == "Swift.UnsafeMutableRawBufferPointer"
                ? "UnsafeMutableRawPointer?"
                : "UnsafeRawPointer?";
            yield return ptrType;
            yield return "Int";
            yield break;
        }
        yield return GetSwiftCdeclParamType(arg, closureHandler);
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

        // CanUseDirectCallbackReturn is now primitives-only (frozen structs go through
        // indirect return because @convention(c) can't return Swift struct types).
        // For primitives, use the actual Swift scalar type. Bool is excluded because it
        // uses special byte<->Bool marshalling (UInt8 in @convention(c), != 0 conversion).
        if (!MarshallingHelpers.IsBoolType(closureTypeSpec.ReturnType) &&
            closureHandler.CanUseDirectCallbackReturn(closureTypeSpec.ReturnType))
            return ExistentialBypassEmitter.RenderSwiftTypeSpec(closureTypeSpec.ReturnType);

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
        // Non-frozen structs transfer ownership of the heap-allocated copy to the C#
        // callback (MarshalFromSwift<T> → SwiftSafeHandle.ReleaseHandle). Tracked
        // separately from `heapAllocArgs` so no Swift-side defer is emitted for this
        // path — the wrapper is allowed to escape, and C# owns destroy/free.
        var nonFrozenHeapArgs = new List<(int index, string swiftType)>();
        var nilForNoneArgs = new List<(int index, string innerSwiftType)>(); // Optional<Bool/SimpleEnum>: nil-for-none pointer ABI
        var existentialArgs = new List<(int index, string swiftType)>(); // `any Protocol`: heap-allocated ExistentialContainer pointer
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            // Use module-qualified names to avoid ambiguity when the wrapper imports multiple modules
            // (e.g., SwiftUI.Color vs SwiftBindingsTestLib.Color)
            var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(arg);
            // Single-protocol existentials arrive as NamedTypeSpec { IsAny = true } from the parser;
            // RenderModuleQualifiedSwiftTypeSpec drops the `any` keyword, which Swift 6 requires.
            // ProtocolListTypeSpec already renders with the `any` prefix.
            if (arg is NamedTypeSpec { IsAny: true })
                swiftType = $"any {swiftType}";
            closureParams.Add($"p{argIndex}: {swiftType}");

            // D1: Complex enums and custom frozen structs use heap allocation — track for cdecl arg substitution.
            // Exclude types that pass directly through @convention(c): primitives, Bool, pointers,
            // classes, ObjC-bridged, and simple enums (these are handled by GetSwiftArgConversion).
            if (closureHandler != null)
            {
                if (closureHandler.IsComplexEnum(arg))
                    heapAllocArgs.Add((argIndex, swiftType));
                else if (arg is NamedTypeSpec frozenNamed && closureHandler.IsFrozenStruct(frozenNamed) &&
                         !IsSwiftPrimitive(frozenNamed.Name) && frozenNamed.Name != "Swift.Bool" &&
                         !frozenNamed.Name.Contains("Pointer") && frozenNamed.Name != "Swift.OpaquePointer" &&
                         !closureHandler.IsClassType(frozenNamed) && !closureHandler.IsObjCBridgedClass(frozenNamed) &&
                         !closureHandler.IsSimpleEnum(frozenNamed))
                    heapAllocArgs.Add((argIndex, swiftType));
                // Non-frozen structs: heap-alloc shape, ownership transferred to C# (no
                // Swift-side defer). `initializeMemory(as:repeating:)` VWT-copies the value
                // into the buffer; MarshalFromSwift<T> on the C# side owns destroy/free.
                else if (arg is NamedTypeSpec nfsNamed && closureHandler.IsNonFrozenStruct(nfsNamed))
                    nonFrozenHeapArgs.Add((argIndex, swiftType));
                // Optional<NumericPrimitive>: full Optional on heap (tag-byte layout)
                // MarshalOptionalFromSwift handles tag-byte reading for primitives
                else if (arg is NamedTypeSpec optNamed && optNamed.Name == "Swift.Optional" &&
                         optNamed.ContainsGenericParameters && optNamed.GenericParameters.Count == 1 &&
                         optNamed.GenericParameters[0] is NamedTypeSpec optInner &&
                         IsSwiftNumericPrimitive(optInner.Name))
                    heapAllocArgs.Add((argIndex, swiftType));
                // Optional<Bool/SimpleEnum/FrozenStruct>: nil-for-none pointer ABI
                // Swift unwraps the optional, passes inner value pointer (nil for .none).
                // Avoids extra-inhabitant encoding which MarshalOptionalFromSwift can't handle
                // for enums/frozen structs; C# reads inner value directly from the pointer.
                else if (arg is NamedTypeSpec optNamed2 && optNamed2.Name == "Swift.Optional" &&
                         optNamed2.ContainsGenericParameters && optNamed2.GenericParameters.Count == 1 &&
                         optNamed2.GenericParameters[0] is NamedTypeSpec optInner2 &&
                         (optInner2.Name == "Swift.Bool" || closureHandler.IsSimpleEnum(optInner2) ||
                          (closureHandler.IsFrozenStruct(optInner2) &&
                           !IsSwiftPrimitive(optInner2.Name) && optInner2.Name != "Swift.Bool" &&
                           !optInner2.Name.Contains("Pointer") && optInner2.Name != "Swift.OpaquePointer" &&
                           !closureHandler.IsClassType(optInner2) && !closureHandler.IsObjCBridgedClass(optInner2))))
                {
                    var innerType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(optInner2);
                    nilForNoneArgs.Add((argIndex, innerType));
                }
                // `any Protocol` existential: heap-allocate the container and pass a pointer.
                // IsCdeclCompatibleType already excluded `any Error` (MCB) and unsupported shapes.
                // Both forms reach here: ProtocolListTypeSpec (multi-proto) and
                // NamedTypeSpec { IsAny = true } (single-proto).
                else if (arg is ProtocolListTypeSpec || arg is NamedTypeSpec { IsAny: true })
                {
                    existentialArgs.Add((argIndex, swiftType));
                }
            }

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
            var nonFrozenHeapArg = nonFrozenHeapArgs.FirstOrDefault(h => h.index == argIndex);
            var nilForNoneArg = nilForNoneArgs.FirstOrDefault(h => h.index == argIndex);
            var existentialArg = existentialArgs.FirstOrDefault(h => h.index == argIndex);
            if (heapArg != default)
            {
                // Complex enum/frozen struct/Optional<Primitive>: use heap pointer
                cdeclArgs.Add($"__heap_{argIndex}");
            }
            else if (nonFrozenHeapArg != default)
            {
                // Non-frozen struct: VWT-managed heap pointer, ownership transferred to C#.
                cdeclArgs.Add($"__heap_{argIndex}");
            }
            else if (nilForNoneArg != default)
            {
                // Optional<Bool/SimpleEnum>: nil-for-none nullable pointer
                cdeclArgs.Add($"__heap_{argIndex}");
            }
            else if (existentialArg != default)
            {
                // `any Protocol`: heap-allocated ExistentialContainer pointer
                cdeclArgs.Add($"__heap_{argIndex}");
            }
            else if (MarshallingHelpers.IsAnyUnsafeRawBufferPointer(arg))
            {
                // UnsafeRawBufferPointer / UnsafeMutableRawBufferPointer: 16-byte struct
                // (baseAddress + count) decomposed at the @convention(c) boundary.
                cdeclArgs.Add($"p{argIndex}.baseAddress");
                cdeclArgs.Add($"p{argIndex}.count");
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
            ? $" -> {ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(closureTypeSpec.ReturnType)}"
            : "";

        // D1: Generate heap allocation lines for complex enum args.
        // No defer — C# takes ownership of the heap memory via SwiftSafeHandle
        // (VWT Destroy + NativeMemory.Free on disposal). Deallocating here would
        // cause use-after-free because MarshalFromSwift wraps the pointer without copying.
        var heapAllocLines = new List<string>();
        foreach (var (idx, swiftType) in heapAllocArgs)
        {
            heapAllocLines.Add($"{indent}    let __heap_{idx} = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<{swiftType}>.size, alignment: MemoryLayout<{swiftType}>.alignment)");
            heapAllocLines.Add($"{indent}    __heap_{idx}.initializeMemory(as: {swiftType}.self, repeating: p{idx}, count: 1)");
        }
        // Nil-for-none allocation: unwrap Optional, pass inner value pointer or nil
        foreach (var (idx, innerType) in nilForNoneArgs)
        {
            heapAllocLines.Add($"{indent}    var __heap_{idx}: UnsafeMutableRawPointer? = nil");
            heapAllocLines.Add($"{indent}    if let __unwrapped_{idx} = p{idx} {{");
            heapAllocLines.Add($"{indent}        __heap_{idx} = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<{innerType}>.size, alignment: MemoryLayout<{innerType}>.alignment)");
            heapAllocLines.Add($"{indent}        __heap_{idx}!.initializeMemory(as: {innerType}.self, repeating: __unwrapped_{idx}, count: 1)");
            heapAllocLines.Add($"{indent}    }}");
            heapAllocLines.Add($"{indent}    defer {{ if let ptr = __heap_{idx} {{ ptr.assumingMemoryBound(to: {innerType}.self).deinitialize(count: 1); ptr.deallocate() }} }}");
        }
        // Existential allocation: store `any Protocol` value in heap buffer, pass pointer.
        // Parens around swiftType because `any Foo.self` parses as `any (Foo.self)`.
        // C# dereferences the pointer into an ExistentialContainer{N} value inside the callback;
        // the defer reclaims the buffer after the cdecl call returns.
        foreach (var (idx, swiftType) in existentialArgs)
        {
            heapAllocLines.Add($"{indent}    let __heap_{idx} = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<{swiftType}>.size, alignment: MemoryLayout<{swiftType}>.alignment)");
            heapAllocLines.Add($"{indent}    __heap_{idx}.initializeMemory(as: ({swiftType}).self, repeating: p{idx}, count: 1)");
            heapAllocLines.Add($"{indent}    defer {{ __heap_{idx}.assumingMemoryBound(to: ({swiftType}).self).deinitialize(count: 1); __heap_{idx}.deallocate() }}");
        }
        // Non-frozen struct allocation: VWT-managed copy of the struct value transferred to C#.
        // `initializeMemory(as:repeating:count:)` invokes VWT.initializeWithCopy which retains
        // any ARC-owning payload inside the non-frozen struct. Ownership is transferred to
        // C# — no defer here. The C# callback wraps the pointer with MarshalFromSwift<T>,
        // whose SwiftSafeHandle.ReleaseHandle pairs VWT.Destroy + NativeMemory.Free on
        // dispose/finalize. UnsafeMutableRawPointer.allocate routes to swift_slowAlloc
        // (malloc on Darwin), so NativeMemory.Free is a safe paired deallocator.
        foreach (var (idx, swiftType) in nonFrozenHeapArgs)
        {
            heapAllocLines.Add($"{indent}    let __heap_{idx} = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<{swiftType}>.size, alignment: MemoryLayout<{swiftType}>.alignment)");
            heapAllocLines.Add($"{indent}    __heap_{idx}.initializeMemory(as: {swiftType}.self, repeating: p{idx}, count: 1)");
        }

        if (isIndirectReturn)
        {
            // Indirect return: closure writes result to buffer, returns void
            var returnSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(closureTypeSpec.ReturnType);
            lines.Add($"{indent}{letPrefix}{adapterName} = {{ ({closureParamsStr}) -> {returnSwiftType} in");
            lines.AddRange(heapAllocLines);
            lines.Add($"{indent}    let resultBuf = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<{returnSwiftType}>.size, alignment: MemoryLayout<{returnSwiftType}>.alignment)");
            lines.Add($"{indent}    {cdeclVarName}({cdeclArgsStr})");
            lines.Add($"{indent}    let result = resultBuf.assumingMemoryBound(to: {returnSwiftType}.self).move()");
            lines.Add($"{indent}    resultBuf.deallocate()");
            lines.Add($"{indent}    return result");
            lines.Add($"{indent}}}");
        }
        else if (isThrowing)
        {
            var returnSwiftType = hasReturn ? ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(closureTypeSpec.ReturnType) : "Void";
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
                // Native Swift classes: convert to raw pointer via Unmanaged
                if (closureHandler.IsClassType(named))
                    return $"Unmanaged.passUnretained({argExpr}).toOpaque()";

                // ObjC-bridged struct types (e.g., IndexPath → NSIndexPath):
                // Bridge to AnyObject first since Unmanaged requires a class type
                if (closureHandler.IsObjCBridgedClass(named))
                    return $"Unmanaged.passUnretained({argExpr} as AnyObject).toOpaque()";

                // Simple enums: convert to underlying integer. unsafeBitCast is unsafe
                // because Swift enums may have different MemoryLayout.size than their
                // raw value type (e.g., a 4-case enum is 1 byte, Int32 is 4 bytes).
                var enumInfo = closureHandler.GetSimpleEnumInfo(named);
                if (enumInfo != null)
                {
                    if (enumInfo.Value.hasRawValue)
                        // Wrap in explicit swiftScalar cast: .rawValue may return a
                        // different Swift type (e.g., Int) than the callback's scalar
                        // type (e.g., Int64). Swift treats Int and Int64 as distinct types.
                        return $"{enumInfo.Value.swiftScalar}({argExpr}.rawValue)";
                    // Tag-only enum: extract tag via safe memory load
                    return $"{{ var __s: {enumInfo.Value.swiftScalar} = 0; var __e = {argExpr}; withUnsafeMutablePointer(to: &__s) {{ dst in withUnsafePointer(to: &__e) {{ src in UnsafeMutableRawPointer(dst).copyMemory(from: UnsafeRawPointer(src), byteCount: MemoryLayout<{ExistentialBypassEmitter.RenderSwiftTypeSpec(named)}>.size) }} }}; return __s }}()";
                }

                // Optional<Class/ObjC>: map to Optional raw pointer, nil maps to nil
                if (named.ContainsGenericParameters && named.Name == "Swift.Optional" &&
                    named.GenericParameters.Count == 1 &&
                    closureHandler.IsReferenceType(named.GenericParameters[0]))
                {
                    var innerSpec = named.GenericParameters[0];
                    // ObjC-bridged structs need `as AnyObject` before Unmanaged
                    var bridgeSuffix = (innerSpec is NamedTypeSpec innerNamed &&
                                       !closureHandler.IsClassType(innerNamed) &&
                                       closureHandler.IsObjCBridgedClass(innerNamed))
                        ? " as AnyObject" : "";
                    return $"{argExpr}.map {{ Unmanaged.passUnretained($0{bridgeSuffix}).toOpaque() }}";
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
                // Native Swift classes: raw pointer → Unmanaged.fromOpaque.takeUnretainedValue
                if (closureHandler.IsClassType(named))
                {
                    var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(named);
                    return $"Unmanaged<{swiftType}>.fromOpaque({expr}).takeUnretainedValue()";
                }

                // ObjC-bridged struct types: use AnyObject bridge for Unmanaged, then cast back
                if (closureHandler.IsObjCBridgedClass(named))
                {
                    var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(named);
                    return $"(Unmanaged<AnyObject>.fromOpaque({expr}).takeUnretainedValue() as! {swiftType})";
                }

                // Simple enums: construct from underlying integer. unsafeBitCast is unsafe
                // because Swift enums may have different MemoryLayout.size than their
                // raw value type (e.g., a 4-case enum is 1 byte, Int32 is 4 bytes).
                var enumInfo = closureHandler.GetSimpleEnumInfo(named);
                if (enumInfo != null)
                {
                    var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(named);
                    if (enumInfo.Value.hasRawValue)
                    {
                        // Wrap in explicit raw type cast: the callback scalar (e.g., Int64)
                        // may differ from the enum's actual rawValue type (e.g., Int).
                        // Swift treats Int and Int64 as distinct types.
                        var rawCast = enumInfo.Value.swiftRawType != null &&
                                      enumInfo.Value.swiftRawType != enumInfo.Value.swiftScalar
                            ? $"{enumInfo.Value.swiftRawType}({expr})"
                            : expr;
                        return $"{swiftType}(rawValue: {rawCast})!";
                    }
                    // Tag-only enum: load from low bytes via safe memory load
                    return $"{{ var __raw = {expr}; return withUnsafeMutablePointer(to: &__raw) {{ UnsafeMutableRawPointer($0).load(as: {swiftType}.self) }} }}()";
                }

                // Optional<Class/ObjC>: map raw pointer back to typed optional
                if (named.ContainsGenericParameters && named.Name == "Swift.Optional" &&
                    named.GenericParameters.Count == 1 &&
                    closureHandler.IsReferenceType(named.GenericParameters[0]))
                {
                    var innerSpec = named.GenericParameters[0];
                    var innerType = ExistentialBypassEmitter.RenderSwiftTypeSpec(innerSpec);
                    // ObjC-bridged structs need AnyObject bridge for Unmanaged
                    if (innerSpec is NamedTypeSpec innerNamed &&
                        !closureHandler.IsClassType(innerNamed) &&
                        closureHandler.IsObjCBridgedClass(innerNamed))
                    {
                        return $"({expr}).map {{ (Unmanaged<AnyObject>.fromOpaque($0).takeUnretainedValue() as! {innerType}) }}";
                    }
                    return $"({expr}).map {{ Unmanaged<{innerType}>.fromOpaque($0).takeUnretainedValue() }}";
                }

                // Non-primitive struct types (including String): the cdecl returns
                // UnsafeMutableRawPointer (C# allocated via NativeMemory.Alloc).
                // Load the value from the pointer and deallocate the buffer.
                // This handles throwing closure returns where the indirect return buffer
                // path is not available (RequiresIndirectReturnMarshalling excludes throwing closures).
                if (!IsSwiftPrimitive(named.Name) && !named.Name.Contains("Pointer") &&
                    named.Name != "Swift.OpaquePointer")
                {
                    var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(named);
                    return $"{{ let __typed = {expr}.assumingMemoryBound(to: {swiftType}.self); let __val = __typed.move(); __typed.deallocate(); return __val }}()";
                }
            }
        }

        return expr;
    }

    /// <summary>
    /// Checks if a Swift type name is a primitive that passes directly in @convention(c).
    /// Includes Bool. Callers that handle Bool separately (byte conversion) may check Bool
    /// before calling this — adding Bool here is safe because their early check takes precedence.
    /// </summary>
    internal static bool IsSwiftPrimitive(string swiftTypeName)
    {
        return swiftTypeName switch
        {
            "Swift.Int" or "Swift.UInt" or
            "Swift.Int8" or "Swift.UInt8" or
            "Swift.Int16" or "Swift.UInt16" or
            "Swift.Int32" or "Swift.UInt32" or
            "Swift.Int64" or "Swift.UInt64" or
            "Swift.Float" or "Swift.Double" or
            "Swift.Bool" => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if a Swift type name is a numeric primitive (excludes Bool).
    /// Used for Optional&lt;T&gt; acceptance: numeric primitives use tag-byte layout for optionals,
    /// but Bool uses extra inhabitant encoding (value &gt; 1 for None) which our runtime can't
    /// handle yet in MarshalOptionalFromSwift.
    /// </summary>
    internal static bool IsSwiftNumericPrimitive(string swiftTypeName)
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
    /// ObjC-bridged types, Optional&lt;Class/ObjC&gt; (nil-pointer ABI), and
    /// Optional&lt;NumericPrimitive/Bool/SimpleEnum/FrozenStruct&gt; (nil-for-none or heap-allocated
    /// pointer ABI depending on inner type).
    /// Complex types (String, non-frozen structs, complex enums) require full marshalling
    /// which is not yet implemented in the Cdecl wrapper path.
    /// </summary>
    internal static bool IsCdeclCompatibleType(TypeSpec typeSpec, ClosureHandler closureHandler)
    {
        if (typeSpec.IsEmptyTuple)
            return true;

        // `inout` closure args require the adapter closure to take `inout p0: T` and write
        // back to the caller's storage. The @convention(c) bridge can't plumb an inout across
        // a C function pointer — the C# side receives a borrowed pointer and has no way to
        // signal mutation back. Reject early so higher-level gates fall back to the
        // non-Cdecl path (which may or may not support inout, but at least won't mis-compile).
        // Surfaces e.g. `Nuke.ImagePipeline.init(delegate:_:)` whose trailing closure is
        // `(inout ImagePipeline.Configuration) -> Void`.
        if (typeSpec.IsInOut)
            return false;

        // `any Error` stays on MCB (pointer-wraps the 5-word container via its own
        // IsEligible path). Non-Error existentials use the heap-allocated pointer
        // bridge below — Swift adapter allocates an ExistentialContainer{N},
        // passes UnsafeMutableRawPointer, C# dereferences and wraps with the proxy.
        // Both existential forms arrive here: ProtocolListTypeSpec (multi-proto) and
        // NamedTypeSpec { IsAny = true } (single-proto, the common parser output).
        if (typeSpec is ProtocolListTypeSpec || typeSpec is NamedTypeSpec { IsAny: true })
        {
            if (closureHandler.NeedsWellKnownProtocolWrapping(typeSpec, out _))
                return false;
            return closureHandler.NeedsProxyWrapping(typeSpec, out _)
                || closureHandler.IsExistentialParam(typeSpec);
        }

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

            // Frozen structs: passed via UnsafeMutableRawPointer heap allocation in adapter closure.
            // The C# callback receives struct via stackalloc + MarshalToSwift (Layer 1 already handles this).
            if (closureHandler.IsFrozenStruct(named))
                return true;

            // Non-frozen structs: heap-allocate via initializeMemory (VWT initializeWithCopy)
            // and pass the pointer to the cdecl callback. Ownership of the heap buffer transfers
            // to C#; the callback wraps it with MarshalFromSwift<T> and SwiftSafeHandle.ReleaseHandle
            // pairs VWT.Destroy + NativeMemory.Free on dispose/finalize. No Swift-side defer —
            // the wrapper is allowed to escape the callback.
            if (closureHandler.IsNonFrozenStruct(named))
                return true;

            // Complex enums: passed via UnsafeMutableRawPointer heap allocation in adapter closure.
            // C# callback and Swift adapter heap allocation code are already written.
            // Note: complex enum RETURNS are blocked by Layer 1 (IsSupportedClosureReturnType),
            // so this gate only effectively enables complex enum parameters.
            if (closureHandler.IsComplexEnum(named))
                return true;

            // Optional<Class/ObjC> uses nil-pointer ABI (pointer-sized)
            // Optional<NumericPrimitive/Bool/SimpleEnum> uses heap-allocated pointer ABI
            // NumericPrimitive: full Optional on heap (tag-byte layout), C# reads tag byte
            // Bool/SimpleEnum: full Optional on heap (extra-inhabitant encoding), C# MarshalOptionalFromSwift handles it
            if (named.ContainsGenericParameters && named.Name == "Swift.Optional" &&
                named.GenericParameters.Count == 1)
            {
                var inner = named.GenericParameters[0];
                // Optional<Class> and Optional<ObjC-bridged> — nil-pointer ABI
                if (closureHandler.IsReferenceType(inner))
                    return true;
                // Optional<NumericPrimitive/Bool/SimpleEnum/FrozenStruct> — heap-allocated pointer ABI
                if (inner is NamedTypeSpec innerNamed)
                {
                    if (IsSwiftNumericPrimitive(innerNamed.Name))
                        return true;
                    if (innerNamed.Name == "Swift.Bool")
                        return true;
                    if (closureHandler.IsSimpleEnum(innerNamed))
                        return true;
                    // Optional<FrozenStruct> — nil-for-none pointer ABI.
                    // Swift unwraps the Optional, allocates inner value (or passes nil),
                    // C# reads via MarshalBorrowedFromSwift<T> on non-null.
                    if (closureHandler.IsFrozenStruct(innerNamed) &&
                        !IsSwiftPrimitive(innerNamed.Name) && innerNamed.Name != "Swift.Bool" &&
                        !innerNamed.Name.Contains("Pointer") && innerNamed.Name != "Swift.OpaquePointer" &&
                        !closureHandler.IsClassType(innerNamed) && !closureHandler.IsObjCBridgedClass(innerNamed))
                        return true;
                }
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

        // Existential returns are not yet supported by the cdecl bridge (Fix 11A covers
        // parameters only). Block them here so IsCdeclCompatibleType can stay symmetric.
        if (closureTypeSpec.ReturnType is ProtocolListTypeSpec ||
            closureTypeSpec.ReturnType is NamedTypeSpec { IsAny: true })
            return false;

        // Check return type — skip for closures using indirect return marshalling (non-throwing),
        // because the @convention(c) return type is Void (result written to buffer via first param).
        // The Swift adapter allocates a buffer, passes it to the C# callback, then loads the result.
        var usesIndirectReturn = closureHandler.RequiresIndirectReturnMarshalling(closureTypeSpec) && !closureTypeSpec.Throws;
        if (!closureTypeSpec.ReturnType.IsEmptyTuple && !usesIndirectReturn && !IsCdeclCompatibleType(closureTypeSpec.ReturnType, closureHandler))
            return false;

        // Throwing closures can't use indirect return (buffer), so Optional<non-reference> returns
        // go through GetSwiftReturnConversion which only handles Optional<Class/ObjC>.
        // Reject Optional<value-type> returns for throwing closures to prevent miscompilation.
        if (closureTypeSpec.Throws && closureTypeSpec.ReturnType is NamedTypeSpec retNamed &&
            retNamed.Name == "Swift.Optional" && retNamed.ContainsGenericParameters &&
            retNamed.GenericParameters.Count == 1 &&
            !closureHandler.IsReferenceType(retNamed.GenericParameters[0]))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if a method with closure wrapper can be converted to @_cdecl.
    /// Uses shared function-level gates plus per-param checks on non-closure, non-large params.
    /// </summary>
    public static bool CanConvertToCdecl(MethodEnvironment env)
    {
        if (!MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env))
            return false;
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            // Metatype check runs BEFORE closure/large-optional skips so AnyClass.Type? doesn't
            // slip through under either bypass — same boundary as the primary wrapper gate.
            if (WrapperValidation.IsMetatypeTypeIncludingOptional(arg.SwiftTypeSpec))
                return false;
            // Closure params are already C-compatible (funcPtr + context)
            if (env.ClosureHandler.IsClosure(arg))
                continue;
            // Large optional params are already UnsafeRawPointer
            if (OptionalPointerWrapperEmitter.ShouldWidenParam(arg, env.BoundGenericsHandler))
                continue;
            if (arg.IsGeneric) return false;
            if (CdeclParamMapper.IsProtocolExistentialType(arg.SwiftTypeSpec, env.TypeDatabase))
                return false;
            if (MethodWrapperEmitter.IsNestedFrozenStructParam(arg, env.TypeDatabase))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Emits a standalone Swift wrapper function for a method/constructor whose ONLY
    /// wrapper reason is closure parameters (no ArraySlice, no default params, etc.).
    /// When useCdecl=true, emits @_cdecl with C-compatible non-closure params.
    /// When useCdecl=false, emits @_silgen_name with native Swift types.
    /// </summary>
    public static void EmitClosureCdeclSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        bool useCdecl = false,
        ModuleEmissionContext? emissionContext = null)
    {
        var methodDecl = env.MethodDecl;
        var closureHandler = env.ClosureHandler;
        var wrapperSymbol = NameProvider.GetMangledName(methodDecl);

        // Build Swift parameter list
        var swiftParams = new List<string>();
        var callArgs = new List<string>();
        var adapterCode = new List<string>();
        var closureParamCount = methodDecl.CSSignature.Skip(1).Count(closureHandler.IsClosure);

        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            var csName = NameProvider.StripVerbatimPrefix(NameProvider.GetCSharpParameterName(arg));
            // Escape Swift keywords with backticks for use in generated Swift code
            var swiftName = NameProvider.EscapeSwiftKeyword(csName);
            var closureTypeSpec = closureHandler.GetClosureTypeSpec(arg);

            if (closureTypeSpec != null &&
                closureHandler.IsSupportedClosure(closureTypeSpec) &&
                closureHandler.RequiresThunk(closureTypeSpec, methodDecl.MangledName, closureParamCount) &&
                !closureHandler.IsAsyncClosure(closureTypeSpec))
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
                // @autoclosure parameters: call the adapted closure to forward the value
                var autoClosureSuffix = closureTypeSpec.IsAutoClosure ? "()" : "";
                callArgs.Add($"{label}{adapterName}{autoClosureSuffix}");
            }
            else if (OptionalPointerWrapperEmitter.ShouldWidenParam(arg, env.BoundGenericsHandler))
            {
                // Large Optional param: accept UnsafeRawPointer, dereference in body
                swiftParams.Add($"_ {swiftName}: UnsafeRawPointer");
                adapterCode.Add(OptionalPointerWrapperEmitter.GetDerefCode(arg, csName, swiftName, env.TypeDatabase));
                var label = GetSwiftArgLabel(arg);
                callArgs.Add($"{label}{csName}Val");
            }
            else if (useCdecl)
            {
                // Non-closure, non-large param in @_cdecl mode: convert to C-compatible type
                var label_ = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
                var (cdeclParam, reconstruction, callArg) =
                    CdeclParamMapper.Map(arg, label_, env, omitLabels: true);
                swiftParams.Add(cdeclParam);
                if (reconstruction != null) adapterCode.Add(reconstruction);
                var swiftArgLabel = GetSwiftArgLabel(arg);
                var valueRef = reconstruction != null ? $"{label_}Val" : csName;
                callArgs.Add($"{swiftArgLabel}{valueRef}");
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

        var callArgsStr = string.Join(", ", callArgs);

        // Build return type
        var returnTypeSpec = methodDecl.CSSignature[0].SwiftTypeSpec;
        var hasReturn = !returnTypeSpec.IsEmptyTuple && !hasLargeOptionalReturn;
        var returnSwiftTypeName = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
        string returnTypeStr;
        string throwsStr;
        bool cdeclNeedsResultPtr = false;
        bool cdeclIsStringReturn = false;
        CdeclReturnMapping? cdeclReturnMapping = null;
        if (useCdecl && hasReturn && !hasLargeOptionalReturn)
        {
            var (returnMapping, needsResultPtr) = CdeclReturnMapping.Classify(returnTypeSpec, env.TypeDatabase);
            cdeclReturnMapping = returnMapping;
            cdeclIsStringReturn = WitnessDispatchEmitter.IsStringType(returnTypeSpec);
            if (cdeclIsStringReturn) needsResultPtr = true;
            cdeclNeedsResultPtr = needsResultPtr;
            returnTypeStr = needsResultPtr ? "" : $" -> {returnMapping.CdeclReturnType}";
            if (needsResultPtr)
            {
                // ResultPtr must be FIRST per CdeclSignatureContract:
                // [ResultPtr?] [Arguments?] [Metadata] [Self?] [ErrorOut?]
                swiftParams.Insert(0, "_ resultPtr: UnsafeMutableRawPointer");
                if (cdeclIsStringReturn)
                    Utf8SliceEmitter.EmitIfNeeded(swiftWriter, emissionContext);
            }
        }
        else
        {
            returnTypeStr = hasReturn ? $" -> {returnSwiftTypeName}" : "";
        }
        throwsStr = (useCdecl && methodDecl.Throws) ? "" : (methodDecl.Throws ? " throws" : "");
        if (useCdecl && methodDecl.Throws)
        {
            swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
        }

        // Build paramsStr AFTER all params (resultPtr, errorOut) have been added
        var paramsStr = string.Join(",\n    ", swiftParams);

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

        // Emit the wrapper — add @MainActor only for @MainActor isolation (not custom actors).
        // @_silgen_name and @_cdecl wrappers are top-level Swift functions and do NOT inherit
        // their parent type's @available; both must be re-applied or the wrapper compiles
        // unconditionally and crashes on devices below the wrapped API's introduced version.
        var availability = WrapperEmitterHelpers.MergeAvailability(methodDecl.AvailabilityAnnotations, parentDecl);
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);
        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            parentDecl, methodDecl.IsMainActorIsolated, methodDecl.IsNonisolated);
        if (needsMainActor)
            swiftWriter.WriteLine("@MainActor");
        var annotation = useCdecl ? "@_cdecl" : "@_silgen_name";
        swiftWriter.WriteLine($"{annotation}(\"{wrapperSymbol}\")");
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

        // Emit the call — with error handling for @_cdecl throwing methods
        if (useCdecl && methodDecl.Throws)
        {
            swiftWriter.WriteLine("    do {");
            var throwCallExpr = $"try {callPrefix}{callArgsStr}{callSuffix}";
            if (hasLargeOptionalReturn)
            {
                var bufferLines = OptionalPointerWrapperEmitter.GetReturnBufferCode(throwCallExpr, returnSwiftTypeName);
                foreach (var bufLine in bufferLines)
                    swiftWriter.WriteLine($"        {bufLine}");
            }
            else if (cdeclIsStringReturn)
            {
                OptionalPointerWrapperEmitter.EmitStringReturnBody(swiftWriter, throwCallExpr, indent: "        ");
            }
            else if (cdeclNeedsResultPtr)
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
                swiftWriter.WriteLine($"        let result = {throwCallExpr}");
                swiftWriter.WriteLine($"        resultPtr.initializeMemory(as: {swiftType}.self, repeating: result, count: 1)");
            }
            else if (hasReturn || methodDecl.IsConstructor)
            {
                OptionalPointerWrapperEmitter.EmitCdeclDirectReturn(swiftWriter, throwCallExpr, returnTypeSpec, env.TypeDatabase, cdeclReturnMapping, indent: "        ");
            }
            else
            {
                swiftWriter.WriteLine($"        {throwCallExpr}");
            }
            swiftWriter.WriteLines("""
                    } catch {
                        errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                    """);
            if (hasReturn && !cdeclNeedsResultPtr && !hasLargeOptionalReturn)
                OptionalPointerWrapperEmitter.EmitCdeclSentinelReturn(swiftWriter, cdeclReturnMapping, indent: "        ");
            swiftWriter.WriteLine("    }");
        }
        else if (hasLargeOptionalReturn)
        {
            var callExpr = $"{tryPrefix}{callPrefix}{callArgsStr}{callSuffix}";
            var bufferLines = OptionalPointerWrapperEmitter.GetReturnBufferCode(callExpr, returnSwiftTypeName);
            foreach (var bufLine in bufferLines)
                swiftWriter.WriteLine($"    {bufLine}");
        }
        else if (cdeclIsStringReturn)
        {
            OptionalPointerWrapperEmitter.EmitStringReturnBody(swiftWriter, $"{tryPrefix}{callPrefix}{callArgsStr}{callSuffix}", indent: "    ");
        }
        else if (cdeclNeedsResultPtr)
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
            swiftWriter.WriteLine($"    let result = {tryPrefix}{callPrefix}{callArgsStr}{callSuffix}");
            swiftWriter.WriteLine($"    resultPtr.initializeMemory(as: {swiftType}.self, repeating: result, count: 1)");
        }
        else if (useCdecl && hasReturn)
        {
            OptionalPointerWrapperEmitter.EmitCdeclDirectReturn(swiftWriter, $"{tryPrefix}{callPrefix}{callArgsStr}{callSuffix}", returnTypeSpec, env.TypeDatabase, cdeclReturnMapping, indent: "    ");
        }
        else
        {
            var returnPrefix = hasReturn || methodDecl.IsConstructor ? "return " : "";
            swiftWriter.WriteLine($"    {returnPrefix}{tryPrefix}{callPrefix}{callArgsStr}{callSuffix}");
        }
        swiftWriter.WriteLine("}");
        swiftWriter.WriteLine();

        // Emit invoke thunk for closure returns — a separate @_cdecl function that C# calls
        // via delegate* unmanaged[Cdecl] to invoke the returned closure, avoiding delegate* unmanaged[Swift].
        if (useCdecl && closureHandler != null)
        {
            ClosureTypeSpec? closureReturnSpec = returnTypeSpec as ClosureTypeSpec;
            if (closureReturnSpec == null && closureHandler.IsOptionalClosure(returnTypeSpec))
            {
                if (returnTypeSpec is NamedTypeSpec optNts && optNts.GenericParameters.Count == 1)
                    closureReturnSpec = optNts.GenericParameters[0] as ClosureTypeSpec;
            }
            if (cdeclNeedsResultPtr && closureReturnSpec != null
                && closureHandler.IsSupportedClosure(closureReturnSpec)
                && CanUseInvokeThunk(closureReturnSpec, closureHandler))
            {
                var thunkEntryPoint = GetInvokeThunkEntryPoint(methodDecl.MangledName);
                var thunkFuncName = $"_sbw_inv_closure_{EmitterUtility.DeterministicHash8(thunkEntryPoint)}";
                EmitSwiftInvokeThunk(swiftWriter, closureReturnSpec, closureHandler,
                    thunkEntryPoint, thunkFuncName);
            }
        }
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
        if (SwiftBuilder.IsAutoGeneratedArgName(name))
            return ""; // Unlabeled
        if (name.StartsWith("_"))
            return $"{name.Substring(1)}: "; // Strip leading underscore
        return $"{name}: ";
    }

    /// <summary>
    /// Checks if a method has non-async escaping closures that need Cdecl wrapper adaptation.
    /// Used by MethodHandler and wrapper generators to set HasClosureCdeclWrapper before
    /// PInvokeSignatureBuilder runs.
    /// Returns false for async methods, opaque return methods, and methods with async-throwing closures.
    /// </summary>
    public static bool NeedsClosureCdeclWrapper(MethodDecl methodDecl, ClosureHandler closureHandler)
    {
        if (methodDecl.IsAsync) return false;

        // Property accessors (getters/setters) pass closure values directly, not as
        // callback function pointers. The Cdecl wrapper pattern doesn't apply.
        if (methodDecl.IsAccessor) return false;

        // Opaque return methods use EmitOpaqueReturnWrapper() which passes closures as
        // native Swift types. Combined closure+opaque wrapper not yet implemented.
        if (methodDecl.CSSignature.Count > 0 &&
            methodDecl.CSSignature[0].SwiftTypeSpec is ProtocolListTypeSpec { IsOpaque: true })
            return false;

        // @convention(c) closures are passed as raw C function pointers, not Swift closures.
        // The ABI JSON doesn't include convention attributes, but the mangled name encodes
        // @convention(c) as 'XC'. If present, our adapter closure (a regular Swift closure)
        // can't be passed where a @convention(c) pointer is expected.
        if (HasConventionCInMangledName(methodDecl.MangledName))
            return false;

        // If the method has ANY async closures (throwing or non-throwing baseline),
        // don't use Cdecl wrapper. Async closures use a specialized P/Invoke pattern
        // that is incompatible with the standalone Swift wrapper.
        var hasAsyncClosure = methodDecl.CSSignature.Skip(1)
            .Where(closureHandler.IsClosure)
            .Any(arg =>
            {
                var spec = closureHandler.GetClosureTypeSpec(arg);
                return spec != null && closureHandler.IsAsyncClosure(spec);
            });
        if (hasAsyncClosure) return false;

        // Find all escaping closures that need thunks (candidates for Cdecl wrapping)
        var closureParamCount = methodDecl.CSSignature.Skip(1).Count(closureHandler.IsClosure);
        var thunkClosures = methodDecl.CSSignature.Skip(1)
            .Where(closureHandler.IsClosure)
            .Where(arg =>
            {
                var spec = closureHandler.GetClosureTypeSpec(arg);
                return spec != null
                    && closureHandler.IsSupportedClosure(spec)
                    && closureHandler.RequiresThunk(spec, methodDecl.MangledName, closureParamCount)
                    && !closureHandler.IsAsyncClosure(spec);
            })
            .ToList();

        // Must have at least one thunk closure, and ALL must be Cdecl-compatible.
        return thunkClosures.Count > 0
            && thunkClosures.All(arg =>
                IsClosureCdeclCompatible(
                    closureHandler.GetClosureTypeSpec(arg)!, closureHandler));
    }

    /// <summary>
    /// Checks if a method's mangled name contains the 'XC' marker indicating a
    /// @convention(c) closure parameter. The ABI JSON doesn't include convention
    /// attributes on ClosureTypeSpec, so this is the reliable detection path.
    /// </summary>
    internal static bool HasConventionCInMangledName(string mangledName)
    {
        return mangledName.Contains("XC", StringComparison.Ordinal);
    }
}
