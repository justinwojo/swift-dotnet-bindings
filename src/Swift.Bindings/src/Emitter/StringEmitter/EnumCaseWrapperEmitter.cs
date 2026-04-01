// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits per-enum-case @_cdecl Swift wrappers for complex enum case constructors
/// (cases with associated values). Routes the P/Invoke through C calling convention
/// to eliminate CallConvSwift ABI mismatches on NativeAOT/ARM64.
///
/// For each complex case, generates a @_cdecl free function that:
/// - Receives C-compatible parameters for each associated value
/// - Constructs the enum case value
/// - Writes the result to a result pointer via initializeMemory(as:)
///
/// Associated value param mapping reuses CdeclParamMapper.Map().
/// State tracked on <see cref="ModuleEmissionContext"/>.
/// </summary>
public static class EnumCaseWrapperEmitter
{
    /// <summary>
    /// Pure query: determines whether an enum case factory should use a @_cdecl wrapper.
    /// </summary>
    public static bool ShouldEmitCaseFactoryWrapper(
        EnumDecl enumDecl,
        EnumCaseDecl caseDecl,
        ITypeDatabase typeDatabase)
    {
        // 1. xcframework mode required (wrapper library must exist)
        if (string.IsNullOrEmpty(typeDatabase.AsyncLibraryName))
            return false;

        // 2. Skip generic enums — type metadata routing not yet supported for enum case factories
        if (enumDecl.IsGeneric)
            return false;

        // 3. Each associated value must be mappable to @_cdecl params
        foreach (var assocValue in caseDecl.AssociatedValues)
        {
            if (!IsSupportedAssociatedValueType(assocValue, typeDatabase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true if an associated value type can be mapped to @_cdecl params.
    /// Supported: primitives, strings, classes, enums, structs, protocol existentials,
    /// generic containers (Optional, Array, Dictionary, Set), closures (not supported).
    /// </summary>
    private static bool IsSupportedAssociatedValueType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        // Closures can't be passed as @_cdecl params easily
        if (typeSpec is ClosureTypeSpec)
            return false;

        // Tuples: each element must be supported AND ABI-compatible for pointer transport.
        // The @_cdecl wrapper receives the tuple as UnsafeRawPointer and does .load(as: SwiftTuple.self),
        // so the C# memory layout must exactly match the Swift tuple layout. Types whose C# P/Invoke
        // representation differs in size from the Swift type (strings, existentials, generic containers,
        // non-frozen structs) would cause a layout mismatch. Those tuples fall back to CallConvSwift.
        if (typeSpec is TupleTypeSpec tuple)
        {
            foreach (var elem in tuple.Elements)
            {
                if (!IsSupportedAssociatedValueType(elem, typeDatabase))
                    return false;
                if (!IsTupleElementAbiCompatible(elem, typeDatabase))
                    return false;
            }
            return true;
        }

        // Generic type parameters (from generic enums) — blocked by IsGeneric check above
        if (typeSpec is NamedTypeSpec named && TypeSpecHelpers.IsGenericTypeParameter(named.Name))
            return false;

        // Everything else: primitives, strings, classes, enums, structs, containers,
        // protocol existentials — all handled by GetCdeclParamMapping
        return true;
    }

    /// <summary>
    /// Returns true if a tuple element type has a C# P/Invoke representation whose memory
    /// layout matches the Swift type, making it safe for pointer-based tuple transport
    /// in @_cdecl wrappers. Types with mismatched sizes (e.g., String is 16 bytes in Swift
    /// but IntPtr/8 bytes in C#) would cause Swift's .load(as:) to read incorrect data.
    /// </summary>
    private static bool IsTupleElementAbiCompatible(TypeSpec element, ITypeDatabase typeDatabase)
    {
        // String: C# P/Invoke uses IntPtr (8 bytes) but Swift String is 16 bytes
        if (element is NamedTypeSpec named && named.Name == "Swift.String")
            return false;

        // Foundation.Data: similar size mismatch
        if (element is NamedTypeSpec dataSpec && dataSpec.Name == "Foundation.Data")
            return false;

        // Protocol existentials: ExistentialContainer layout may not match tuple element layout
        if (element is ProtocolListTypeSpec)
            return false;

        // Generic containers (Optional<T>, Array<T>, etc.): C# uses lowered IntPtr
        if (CdeclParamMapper.IsGenericContainerType(element))
            return false;

        // Nested tuples: would need recursive layout matching
        if (element is TupleTypeSpec)
            return false;

        // Closures: not C-representable
        if (element is ClosureTypeSpec)
            return false;

        // Named types: check against type database
        if (element is NamedTypeSpec namedElement)
        {
            // Primitives (Int, Bool, Float, etc.) are always bit-for-bit ABI-identical
            if (CdeclParamMapper.IsCdeclPrimitive(element))
                return true;

            // Non-primitive named types must be in the database to verify their kind
            if (!typeDatabase.TryGetTypeRecord(namedElement, out var record))
                return false; // Unknown type — can't verify layout safety

            // Classes: C# tuple stores managed object reference, but Swift expects a native
            // object pointer (Unmanaged.fromOpaque). Taking &valueTuple doesn't rewrite
            // managed references into native handles.
            if (record.Kind == TypeRecordKind.Class)
                return false;

            // ObjC-bridged/rooted types: same issue as classes — managed wrapper vs native pointer
            if (MarshallingHelpers.IsObjCBridged(record) || MarshallingHelpers.IsObjCRooted(record))
                return false;

            // Enums: the non-tuple @_cdecl path reconstructs enums via init(rawValue:) or
            // unsafe memory load to handle widened C# representation vs compact Swift storage.
            // Tuple pointer transport skips that per-element reconstruction.
            if (record.Kind == TypeRecordKind.Enum)
                return false;

            // Non-frozen structs: C# uses IntPtr (SafeHandle) but Swift struct has unknown size
            if (record.Kind == TypeRecordKind.Struct && !MarshallingHelpers.IsTypeFrozen(record))
                return false;

            // Frozen structs with memory management (ref-type fields): C# IntPtr vs Swift struct size
            if (record.Kind == TypeRecordKind.Struct && MarshallingHelpers.RequiresMemoryManagement(record))
                return false;

            // Frozen blittable structs: same field layout, same padding — ABI-identical
            return true;
        }

        // Non-named types that weren't caught above: not safe
        return false;
    }

    /// <summary>
    /// Gets the @_cdecl symbol name for an enum case factory wrapper.
    /// </summary>
    public static string GetCaseFactorySymbolName(string moduleName, string enumTypeName, string caseName, string mangledName)
    {
        var hash = EmitterUtility.DeterministicHash8(mangledName);
        var safeTypeName = enumTypeName.Replace(".", "_");
        return $"SBW_{moduleName}_{safeTypeName}_{caseName}_{hash}";
    }

    /// <summary>
    /// Emits a @_cdecl Swift wrapper function for an enum case constructor.
    /// The wrapper receives C-compatible associated value parameters,
    /// constructs the enum case, and writes the result to a result pointer.
    /// </summary>
    public static void EmitSwiftCaseFactoryWrapper(
        SwiftWriter swiftWriter,
        EnumDecl enumDecl,
        EnumCaseDecl caseDecl,
        string symbolName,
        MethodEnvironment env,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;

        if (!ctx.TryAddConstructorWrapperSymbol(symbolName))
            return; // Already emitted

        var moduleDecl = enumDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(enumDecl.ModuleDecl));
        var enumModuleQualifiedName = enumDecl.SwiftTypeName?.ModuleQualifiedName;
        if (enumModuleQualifiedName == null) return;

        // Build parameter list
        var swiftParams = new List<string>();
        var reconstructionLines = new List<string>();
        var callArgs = new List<string>();

        // Associated value parameters
        for (int i = 0; i < caseDecl.AssociatedValues.Count; i++)
        {
            var assocValue = caseDecl.AssociatedValues[i];
            var label = !string.IsNullOrEmpty(assocValue.TypeLabel) ? assocValue.TypeLabel : $"value{i}";

            // Create a synthetic ArgumentDecl for GetCdeclParamMapping
            var argDecl = new ArgumentDecl
            {
                SwiftTypeSpec = assocValue,
                Name = assocValue.TypeLabel ?? "_",
                PrivateName = label,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl
            };

            var (cdeclParam, reconstruction, callArg) = CdeclParamMapper.Map(
                argDecl, label, env, omitLabels: false, useUtf8Strings: true);

            swiftParams.Add(cdeclParam);
            if (reconstruction != null)
                reconstructionLines.Add(reconstruction);
            callArgs.Add(callArg);
        }

        // Result pointer (last param — receives the constructed enum value)
        swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");

        var swiftParamString = string.Join(", ", swiftParams);
        var swiftFuncName = $"_sbw_case_{caseDecl.Name}_{EmitterUtility.DeterministicHash8(symbolName)}";

        // Build the enum case construction expression
        var caseExpr = BuildCaseConstructionExpr(enumModuleQualifiedName, caseDecl, callArgs);

        // Emit the @_cdecl function
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Enum case factory @_cdecl wrapper for {{enumModuleQualifiedName}}.{{caseDecl.Name}}.
            // Routes through C calling convention to avoid CallConvSwift crash on NativeAOT.
            """);

        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            enumDecl, memberIsMainActorIsolated: false);
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActor,
            WrapperEmitterHelpers.MergeAvailability(null, enumDecl));
        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}) {{");
        swiftWriter.Indent++;

        // Emit parameter reconstruction lines
        foreach (var line in reconstructionLines)
            swiftWriter.WriteLine(line);

        // Construct enum case and write to result pointer
        swiftWriter.WriteLine($"let result = {caseExpr}");
        swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {enumModuleQualifiedName}.self, repeating: result, count: 1)");

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Builds the Swift expression for constructing an enum case with associated values.
    /// Example: Module.EnumType.caseName(label1: val1, label2: val2)
    /// </summary>
    private static string BuildCaseConstructionExpr(string enumQualifiedName, EnumCaseDecl caseDecl, List<string> callArgs)
    {
        // When a single associated value is a tuple with multiple elements (e.g.,
        // case fixed(width: CGFloat, height: CGFloat) → ABI stores as one tuple),
        // the @_cdecl wrapper loads the entire tuple value from a pointer. The enum
        // case constructor expects individual arguments, so destructure by field access.
        if (caseDecl.AssociatedValues.Count == 1 &&
            caseDecl.AssociatedValues[0] is TupleTypeSpec tuple &&
            tuple.Elements.Count > 1)
        {
            // Extract the value variable name from the callArg (e.g., "width: widthVal" → "widthVal")
            var singleCallArg = callArgs[0];
            var colonIdx = singleCallArg.IndexOf(": ");
            var valName = colonIdx >= 0 ? singleCallArg.Substring(colonIdx + 2) : singleCallArg;

            // Build destructured args: "width: valName.width, height: valName.height"
            var destructuredArgs = new List<string>();
            for (int i = 0; i < tuple.Elements.Count; i++)
            {
                var element = tuple.Elements[i];
                var elemAccessor = !string.IsNullOrEmpty(element.TypeLabel)
                    ? element.TypeLabel
                    : $"{i}";
                var elemLabel = !string.IsNullOrEmpty(element.TypeLabel)
                    ? $"{element.TypeLabel}: "
                    : "";
                destructuredArgs.Add($"{elemLabel}{valName}.{elemAccessor}");
            }
            return $"{enumQualifiedName}.{caseDecl.Name}({string.Join(", ", destructuredArgs)})";
        }

        // Standard path: callArgs from GetCdeclParamMapping already include labels
        var argsString = string.Join(", ", callArgs);
        return $"{enumQualifiedName}.{caseDecl.Name}({argsString})";
    }
}
