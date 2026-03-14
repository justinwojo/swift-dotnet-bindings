// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits per-property @_cdecl Swift wrappers that route property getter/setter P/Invokes
/// through C calling convention, eliminating CallConvSwift ABI mismatches on NativeAOT/ARM64.
///
/// For each property accessor, generates a @_cdecl free function in the wrapper library that:
/// - Receives C-compatible parameters (self as pointer, newValue for setters)
/// - Calls the actual Swift property getter/setter
/// - Returns the result via C ABI (appropriate type mapping per category)
///
/// String properties use the proven SBW_Utf8Slice pattern (UTF-8 bytes + length).
/// Follows the ConstructorWrapperEmitter pattern. State tracked on <see cref="ModuleEmissionContext"/>.
/// </summary>
public static class PropertyWrapperEmitter
{
    /// <summary>
    /// Pure query: determines whether a property should use @_cdecl wrappers for its accessors.
    /// Guards: xcframework mode, generic parents (allowed for non-final classes with concrete types),
    /// no closures, no async, no non-copyable structs, no nested types,
    /// no unsupported generic containers, no ObjC-bridged Optional setters.
    /// </summary>
    public static bool ShouldEmitWrapper(PropertyDecl propertyDecl, MethodEnvironment accessorEnv)
    {
        // 1. xcframework mode required (wrapper library must exist)
        if (!WrapperValidation.IsXCFrameworkMode(accessorEnv.TypeDatabase))
            return false;

        // 2. Generic parent type — allow non-final class instance properties with concrete types
        if (accessorEnv.ParentDecl is TypeDecl td && td.IsGeneric)
        {
            if (!CanEmitGenericClassPropertyWrapper(propertyDecl, td))
                return false;
        }

        // 2b. Skip internal properties — not accessible from the wrapper module
        if (propertyDecl.IsModuleInternal)
            return false;

        // 2c. Skip @_spi protected properties — wrapper can't access them without @_spi import
        if (propertyDecl.IsSpiProtected)
            return false;

        // 2d. Skip metatype properties (Any.Type, T.Type) — not C-representable
        if (WrapperValidation.IsMetatypeType(propertyDecl.SwiftTypeSpec))
            return false;

        // 3. Skip closure properties
        if (accessorEnv.ClosureHandler.IsClosure(propertyDecl))
            return false;

        // 4. Skip async properties (own wrapper pattern)
        if (propertyDecl.Accessors.Any(a => a.Method.IsAsync))
            return false;

        // 4b. Skip actor-isolated properties — @_cdecl wrappers are synchronous nonisolated,
        // and protocol conformance for generic class type erasure crosses actor boundaries
        if (WrapperValidation.IsActorIsolatedMember(accessorEnv.ParentDecl, propertyDecl.IsActorIsolated))
            return false;

        // 6. Skip non-copyable (~Copyable) struct parents
        if (WrapperValidation.IsNonCopyableStructParent(accessorEnv.ParentDecl))
            return false;

        // 8. Skip nested type properties — @_cdecl can't represent nested Swift types
        //    (e.g., OuterType.InnerType) as parameters. C-compatible structs (CGSize, UIEdgeInsets)
        //    work fine, but pure Swift nested types fail at compilation.
        if (WrapperValidation.IsNestedType(propertyDecl.SwiftTypeSpec))
            return false;

        // 9. Skip unsupported generic container properties (Result<T,E>, Optional<existential>).
        //    Optional<value-type> allowed (IndirectResult). Array/Dictionary/Set allowed (UnsafeRawPointer transport).
        if (WrapperValidation.IsUnsupportedGenericContainer(propertyDecl.SwiftTypeSpec, accessorEnv.TypeDatabase))
            return false;

        // 9b. ObjC-bridged Optional accessor setter: C# aliases IntPtr directly, incompatible
        //     with @_cdecl reconstruction. Getter is fine — PropertyHandler's ObjC conversion
        //     is calling-convention agnostic.
        if (WrapperValidation.IsOptionalType(propertyDecl.SwiftTypeSpec) &&
            MarshallingHelpers.IsOptionalObjCBridged(propertyDecl.SwiftTypeSpec, accessorEnv.TypeDatabase) &&
            propertyDecl.Accessors.OfType<SetAccessorDecl>().Any())
            return false;

        // 10. Skip properties with raw ABI generic type params (τ_0_0) in the property type.
        // These leak from parent type generics and cause Swift compilation failures.
        if (propertyDecl.SwiftTypeSpec != null && WrapperValidation.ContainsRawGenericTypeParam(propertyDecl.SwiftTypeSpec))
            return false;

        return true;
    }

    /// <summary>
    /// Returns a human-readable skip reason if the property wrapper would be rejected, or null if it passes all gates.
    /// Mirrors the guard order in <see cref="ShouldEmitWrapper"/> for emission report diagnostics.
    /// </summary>
    public static string? GetRejectionReason(PropertyDecl propertyDecl, MethodEnvironment accessorEnv)
    {
        if (!WrapperValidation.IsXCFrameworkMode(accessorEnv.TypeDatabase))
            return null; // not in xcframework mode — not a skip, just N/A

        if (accessorEnv.ParentDecl is TypeDecl td && td.IsGeneric &&
            !CanEmitGenericClassPropertyWrapper(propertyDecl, td))
            return "generic_parent_type";
        if (propertyDecl.IsModuleInternal)
            return "internal_property";
        if (propertyDecl.IsSpiProtected)
            return "spi_protected";
        if (WrapperValidation.IsMetatypeType(propertyDecl.SwiftTypeSpec))
            return "metatype_property";
        if (accessorEnv.ClosureHandler.IsClosure(propertyDecl))
            return "closure_property";
        if (propertyDecl.Accessors.Any(a => a.Method.IsAsync))
            return "async_property";
        if (WrapperValidation.IsActorIsolatedMember(accessorEnv.ParentDecl, propertyDecl.IsActorIsolated))
            return "actor_isolated_property";
        if (WrapperValidation.IsNonCopyableStructParent(accessorEnv.ParentDecl))
            return "non_copyable_struct_parent";
        if (WrapperValidation.IsNestedType(propertyDecl.SwiftTypeSpec))
            return "nested_type_property";
        if (WrapperValidation.IsUnsupportedGenericContainer(propertyDecl.SwiftTypeSpec, accessorEnv.TypeDatabase))
            return "unsupported_generic_container";
        if (WrapperValidation.IsOptionalType(propertyDecl.SwiftTypeSpec) &&
            MarshallingHelpers.IsOptionalObjCBridged(propertyDecl.SwiftTypeSpec, accessorEnv.TypeDatabase) &&
            propertyDecl.Accessors.OfType<SetAccessorDecl>().Any())
            return "objc_bridged_optional_setter";
        if (propertyDecl.SwiftTypeSpec != null && WrapperValidation.ContainsRawGenericTypeParam(propertyDecl.SwiftTypeSpec))
            return "raw_generic_type_params";

        return null;
    }

    /// <summary>
    /// Gets the @_cdecl symbol name for a property accessor wrapper.
    /// </summary>
    public static string GetAccessorSymbolName(string moduleName, string typeName, string propertyName, bool isGetter)
    {
        var safeTypeName = typeName.Replace(".", "_");
        var prefix = isGetter ? "Get" : "Set";
        return $"SBW_{prefix}_{moduleName}_{safeTypeName}_{propertyName}";
    }

    /// <summary>
    /// Emits a @_cdecl Swift wrapper for a property getter.
    /// </summary>
    public static void EmitSwiftGetterWrapper(
        SwiftWriter swiftWriter,
        PropertyDecl propertyDecl,
        string symbolName,
        MethodEnvironment env,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;

        if (!ctx.TryAddPropertyWrapperSymbol(symbolName))
            return; // Already emitted

        var parentTypeDecl = env.ParentDecl as TypeDecl;
        if (parentTypeDecl?.SwiftTypeName == null) return;
        var moduleQualifiedName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var moduleName = parentTypeDecl.SwiftTypeName.Module;
        var propertySwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(propertyDecl.SwiftTypeSpec);
        bool isClass = env.ParentDecl is ClassDecl;
        bool isStatic = propertyDecl.IsStatic;
        bool isString = WitnessDispatchEmitter.IsStringType(propertyDecl.SwiftTypeSpec);

        // Ensure SBW_Utf8Slice infrastructure is emitted for string properties
        if (isString)
        {
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, ctx);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, ctx);
        }

        // Check if return needs indirect result (non-frozen struct/complex enum)
        var (returnMapping, needsResultPtr) = GetCdeclReturnMapping(propertyDecl.SwiftTypeSpec, env.TypeDatabase);

        // Build parameter list — phase ordering from CdeclSignatureContract.
        // ResultPtr is handled outside the loop using the emitter's own needsResultPtr logic.
        var swiftParams = new List<string>();
        bool isGenericParent = MethodWrapperEmitter.IsGenericClassParent(env.ParentDecl);

        if (needsResultPtr)
        {
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");
        }

        var order = CdeclSignatureContract.DetermineParameterOrder(env,
            overrideNeedsResultPtr: needsResultPtr, overrideNeedsSelf: !isStatic);
        foreach (var phase in order.Phases)
        {
            switch (phase)
            {
                case CdeclPhase.ResultPtr:
                    break; // Already handled above
                case CdeclPhase.Self:
                    if (isClass)
                        swiftParams.Add($"_ self_: UnsafeMutableRawPointer");
                    else
                        swiftParams.Add($"_ self_: UnsafeRawPointer");
                    break;
                case CdeclPhase.Metadata:
                    if (isGenericParent && parentTypeDecl != null)
                    {
                        for (int i = 0; i < parentTypeDecl.GenericParameters.Count; i++)
                        {
                            swiftParams.Add($"_ _metadata{i}: UnsafeRawPointer");
                        }
                    }
                    break;
            }
        }

        var swiftParamString = string.Join(", ", swiftParams);

        // Return clause
        string returnClause = needsResultPtr ? "" : $" -> {returnMapping.cdeclReturnType}";

        var swiftFuncName = $"_sbw_get_{propertyDecl.Name}_{EmitterUtility.DeterministicHash8(symbolName)}";

        // For generic parent class types, emit protocol + conformance for type erasure
        string? protocolName = null;
        if (isGenericParent)
        {
            protocolName = EmitGetterProtocolAndConformance(
                swiftWriter, propertyDecl, symbolName, moduleQualifiedName);
        }

        // Emit the @_cdecl function
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Property getter @_cdecl wrapper for {{moduleQualifiedName}}.{{propertyDecl.Name}}.
            // Routes through C calling convention to avoid CallConvSwift crash on NativeAOT.
            """);

        // @MainActor intentionally omitted — see MethodWrapperEmitter comment.
        swiftWriter.WriteLines($$"""
            @_cdecl("{{symbolName}}")
            """);
        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){returnClause} {{");
        swiftWriter.Indent++;

        // Reconstruct self
        if (!isStatic)
        {
            if (isGenericParent && protocolName != null)
            {
                swiftWriter.WriteLine($"let obj = Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! any {protocolName}");
            }
            else
            {
                EmitSelfReconstruction(swiftWriter, isClass, moduleQualifiedName, isMutable: false);
            }
        }

        // Get property value
        string propAccess = isStatic ? $"{moduleQualifiedName}.{propertyDecl.Name}" : $"obj.{propertyDecl.Name}";

        // Emit return based on type category
        if (isString)
        {
            EmitStringGetterBody(swiftWriter, propAccess);
        }
        else if (needsResultPtr)
        {
            // Non-frozen struct or complex enum: write to result buffer
            var qualifiedPropertyType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(propertyDecl.SwiftTypeSpec);
            var metatype = qualifiedPropertyType.StartsWith("any ") ? $"({qualifiedPropertyType}).self" : $"{qualifiedPropertyType}.self";
            swiftWriter.WriteLine($"let result = {propAccess}");
            swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {metatype}, repeating: result, count: 1)");
        }
        else
        {
            EmitDirectGetterReturn(swiftWriter, propAccess, propertyDecl.SwiftTypeSpec, env.TypeDatabase, returnMapping);
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits a @_cdecl Swift wrapper for a property setter.
    /// Parameter order: [resultPtr if needed], newValue params, self (matches C# P/Invoke layout).
    /// </summary>
    public static void EmitSwiftSetterWrapper(
        SwiftWriter swiftWriter,
        PropertyDecl propertyDecl,
        string symbolName,
        MethodEnvironment env,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;

        if (!ctx.TryAddPropertyWrapperSymbol(symbolName))
            return; // Already emitted

        var parentTypeDecl = env.ParentDecl as TypeDecl;
        if (parentTypeDecl?.SwiftTypeName == null) return;
        var moduleQualifiedName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var moduleName = parentTypeDecl.SwiftTypeName.Module;
        bool isClass = env.ParentDecl is ClassDecl;
        bool isStatic = propertyDecl.IsStatic;
        bool isString = WitnessDispatchEmitter.IsStringType(propertyDecl.SwiftTypeSpec);

        // Build parameter list — phase ordering from CdeclSignatureContract.
        var swiftParams = new List<string>();
        var reconstructionLines = new List<string>();
        bool isGenericParent = MethodWrapperEmitter.IsGenericClassParent(env.ParentDecl);

        var order = CdeclSignatureContract.DetermineParameterOrder(env,
            overrideNeedsResultPtr: false, overrideHasArguments: true, overrideNeedsSelf: !isStatic);
        foreach (var phase in order.Phases)
        {
            switch (phase)
            {
                case CdeclPhase.Arguments:
                    // NewValue parameter(s)
                    if (isString)
                    {
                        swiftParams.Add("_ utf8Ptr: UnsafePointer<UInt8>");
                        swiftParams.Add("_ utf8Len: Int");
                        reconstructionLines.Add("let newValue = String(bytes: UnsafeBufferPointer(start: utf8Ptr, count: utf8Len), encoding: .utf8)!");
                    }
                    else
                    {
                        var newValueArg = new ArgumentDecl
                        {
                            SwiftTypeSpec = propertyDecl.SwiftTypeSpec,
                            Name = "newValue",
                            PrivateName = "newValue",
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = null
                        };
                        // omitLabels: false — property setters always need .load(as:) reconstruction
                        // for large Optionals. omitLabels: true would trigger ShouldWidenParam bypass.
                        var (cdeclParam, reconstruction, _) = ConstructorWrapperEmitter.GetCdeclParamMapping(
                            newValueArg, "newValue", env, omitLabels: false);
                        swiftParams.Add(cdeclParam);
                        if (reconstruction != null)
                        {
                            reconstructionLines.Add(reconstruction);
                        }
                    }
                    break;

                case CdeclPhase.Metadata:
                    if (isGenericParent && parentTypeDecl != null)
                    {
                        for (int i = 0; i < parentTypeDecl.GenericParameters.Count; i++)
                        {
                            swiftParams.Add($"_ _metadata{i}: UnsafeRawPointer");
                        }
                    }
                    break;

                case CdeclPhase.Self:
                    // Both class and struct setters use mutable self
                    swiftParams.Add($"_ self_: UnsafeMutableRawPointer");
                    break;
            }
        }

        var swiftParamString = string.Join(", ", swiftParams);
        var swiftFuncName = $"_sbw_set_{propertyDecl.Name}_{EmitterUtility.DeterministicHash8(symbolName)}";

        // For generic parent class types, emit protocol + conformance for type erasure
        string? protocolName = null;
        if (isGenericParent)
        {
            protocolName = EmitSetterProtocolAndConformance(
                swiftWriter, propertyDecl, symbolName, moduleQualifiedName);
        }

        // Emit the @_cdecl function
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Property setter @_cdecl wrapper for {{moduleQualifiedName}}.{{propertyDecl.Name}}.
            // Routes through C calling convention to avoid CallConvSwift crash on NativeAOT.
            """);

        // @MainActor intentionally omitted — see MethodWrapperEmitter comment.
        swiftWriter.WriteLines($$"""
            @_cdecl("{{symbolName}}")
            """);
        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}) {{");
        swiftWriter.Indent++;

        // Emit reconstruction lines
        foreach (var line in reconstructionLines)
        {
            swiftWriter.WriteLine(line);
        }

        // Get the value expression (may be reconstructed with suffix "Val" for non-string types)
        string valueExpr = isString ? "newValue" :
            (reconstructionLines.Count > 0 ? "newValueVal" : "newValue");

        // Emit assignment
        if (isStatic)
        {
            swiftWriter.WriteLine($"{moduleQualifiedName}.{propertyDecl.Name} = {valueExpr}");
        }
        else if (isGenericParent && protocolName != null)
        {
            // Generic class: use protocol-based type erasure
            swiftWriter.WriteLine($"var obj = Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! any {protocolName}");
            swiftWriter.WriteLine($"obj.{propertyDecl.Name} = {valueExpr}");
        }
        else if (isClass)
        {
            // Class: reconstruct from Unmanaged, assign property
            swiftWriter.WriteLine($"let obj = Unmanaged<{moduleQualifiedName}>.fromOpaque(self_).takeUnretainedValue()");
            swiftWriter.WriteLine($"obj.{propertyDecl.Name} = {valueExpr}");
        }
        else
        {
            // Struct: mutate through pointer
            swiftWriter.WriteLine($"self_.assumingMemoryBound(to: {moduleQualifiedName}.self).pointee.{propertyDecl.Name} = {valueExpr}");
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Maps a property type to its @_cdecl-compatible return type and whether it needs a result pointer.
    /// </summary>
    internal static (CdeclReturnMapping mapping, bool needsResultPtr) GetCdeclReturnMapping(
        TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        // DynamicSelf (Self): resolves to parent class type at call site.
        // Return as class pointer (Unmanaged.passRetained().toOpaque()).
        if (typeSpec.IsDynamicSelf)
            return (new CdeclReturnMapping("UnsafeMutableRawPointer", CdeclReturnKind.ClassPointer), false);

        // Tuple returns: route through indirect result (resultPtr buffer).
        // initializeMemory(as: (T1, T2).self) handles all tuple element types.
        if (typeSpec is TupleTypeSpec tts && !tts.IsEmptyTuple)
            return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);

        // Primitives: pass through directly
        if (ConstructorWrapperEmitter.IsCdeclPrimitive(typeSpec))
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);
            if (MarshallingHelpers.IsBoolType(swiftType) || swiftType == "Bool")
                return (new CdeclReturnMapping("Int8", CdeclReturnKind.Bool), false);
            return (new CdeclReturnMapping(swiftType, CdeclReturnKind.Direct), false);
        }

        // String: SBW_Utf8Slice via result pointer (@_cdecl can't return Swift structs)
        if (typeSpec is NamedTypeSpec strNamed && strNamed.Name == "Swift.String")
            return (new CdeclReturnMapping("SBW_Utf8Slice", CdeclReturnKind.String), true);

        // AnyObject: IS a class reference by definition — use Unmanaged.passRetained().toOpaque().
        // AnyObject may appear as ProtocolListTypeSpec (from existential parsing) or as plain
        // NamedTypeSpec (from TypeSpecParser). Without this gate, it falls through to IndirectResult
        // and emits `any AnyObject.self` which is not valid Swift.
        if (ConstructorWrapperEmitter.IsAnyObjectType(typeSpec))
            return (new CdeclReturnMapping("UnsafeMutableRawPointer", CdeclReturnKind.ClassPointer), false);

        // Closure returns: write to resultPtr buffer
        if (typeSpec is ClosureTypeSpec)
            return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);

        // Optional<reference type>: nullable pointer ABI (no result buffer needed)
        if (MethodWrapperEmitter.IsOptionalWithReferenceInner(typeSpec, typeDatabase))
            return (new CdeclReturnMapping("UnsafeMutableRawPointer?", CdeclReturnKind.OptionalClassPointer), false);

        // Generic containers (Optional, Array, etc.): need result pointer
        if (ConstructorWrapperEmitter.IsGenericContainerType(typeSpec))
            return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);

        // Protocol existentials: need result pointer (not C-representable in @_cdecl)
        if (ConstructorWrapperEmitter.IsProtocolExistentialType(typeSpec, typeDatabase))
            return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);

        // Try TypeRecord-based mapping
        if (typeDatabase.TryGetTypeRecord(typeSpec, out var typeRecord))
        {
            // NSString typedef structs (e.g., CALayerContentsGravity, CATransitionType) are ObjC-bridged
            // in the type database but are Swift structs wrapping NSString — not class instances.
            // Unmanaged.passRetained() requires a class, so these must NOT use ClassPointer.
            // Route through indirect result like other structs.
            if (MarshallingHelpers.IsObjCBridged(typeRecord) &&
                typeSpec is NamedTypeSpec nsTypedef &&
                AppleFrameworkRegistry.TryGetNetTypeName(nsTypedef.Name, out var remapped) &&
                remapped == "Foundation.NSString")
                return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);

            // Classes and ObjC-bridged: return as retained pointer.
            // Guard: Unmanaged.passRetained() requires a class type — ObjC-rooted/bridged struct
            // types (e.g., PHPickerResult) must fall through to IndirectResult instead.
            if (typeRecord.Kind == TypeRecordKind.Class ||
                ((MarshallingHelpers.IsObjCBridged(typeRecord) || MarshallingHelpers.IsObjCRooted(typeRecord))
                 && typeRecord.Kind != TypeRecordKind.Struct))
                return (new CdeclReturnMapping("UnsafeMutableRawPointer", CdeclReturnKind.ClassPointer), false);

            // Simple enums: return raw value type
            if (typeRecord.Kind == TypeRecordKind.Enum && typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            {
                var rawType = ConstructorWrapperEmitter.GetSwiftRawValueType(typeRecord.RawValueTypeName);
                return (new CdeclReturnMapping(rawType, CdeclReturnKind.SimpleEnum), false);
            }

            // Complex enums: need result pointer
            if (typeRecord.Kind == TypeRecordKind.Enum)
                return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);

            // All structs (frozen and non-frozen): need result pointer.
            // @_cdecl can't return Swift structs — even @frozen ones fail with
            // "result type cannot be represented in Objective-C".
            return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);
        }

        // Fallback: indirect result
        return (new CdeclReturnMapping("Void", CdeclReturnKind.IndirectResult), true);
    }

    /// <summary>
    /// Emits the self reconstruction line for the getter/setter body.
    /// </summary>
    private static void EmitSelfReconstruction(SwiftWriter swiftWriter, bool isClass, string moduleQualifiedName, bool isMutable)
    {
        if (isClass)
        {
            swiftWriter.WriteLine($"let obj = Unmanaged<{moduleQualifiedName}>.fromOpaque(self_).takeUnretainedValue()");
        }
        else
        {
            swiftWriter.WriteLine($"let obj = self_.assumingMemoryBound(to: {moduleQualifiedName}.self).pointee");
        }
    }

    /// <summary>
    /// Emits the string getter body using SBW_Utf8Slice pattern.
    /// Writes result to resultPtr because @_cdecl can't return Swift structs.
    /// </summary>
    private static void EmitStringGetterBody(SwiftWriter swiftWriter, string propAccess)
    {
        swiftWriter.WriteLines($$"""
            let result = {{propAccess}}
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
    /// Emits the direct return for non-string, non-indirect-result getter returns.
    /// </summary>
    private static void EmitDirectGetterReturn(SwiftWriter swiftWriter, string propAccess,
        TypeSpec typeSpec, ITypeDatabase typeDatabase, CdeclReturnMapping mapping)
    {
        switch (mapping.Kind)
        {
            case CdeclReturnKind.Bool:
                swiftWriter.WriteLine($"return {propAccess} ? 1 : 0");
                break;

            case CdeclReturnKind.SimpleEnum:
                // Check if it has a raw value type for safe conversion
                if (typeDatabase.TryGetTypeRecord(typeSpec, out var enumRecord) &&
                    !string.IsNullOrEmpty(enumRecord.RawValueTypeName))
                {
                    swiftWriter.WriteLine($"return {mapping.cdeclReturnType}({propAccess}.rawValue)");
                }
                else
                {
                    // Tag-only enum: use withUnsafePointer to extract tag bits
                    var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);
                    swiftWriter.WriteLine($"var result = {propAccess}");
                    swiftWriter.WriteLine($"return withUnsafePointer(to: &result) {{ UnsafeRawPointer($0).load(as: {mapping.cdeclReturnType}.self) }}");
                }
                break;

            case CdeclReturnKind.ClassPointer:
                swiftWriter.WriteLine($"return Unmanaged.passRetained({propAccess}).toOpaque()");
                break;

            case CdeclReturnKind.OptionalClassPointer:
                swiftWriter.WriteLine($"return ({propAccess}).map {{ Unmanaged.passRetained($0).toOpaque() }}");
                break;

            case CdeclReturnKind.Direct:
            default:
                swiftWriter.WriteLine($"return {propAccess}");
                break;
        }
    }

    /// <summary>
    /// Checks if a parent decl is a non-copyable struct.
    /// </summary>
    private static bool IsNonCopyableStruct(BaseDecl? parentDecl)
        => WrapperValidation.IsNonCopyableStructParent(parentDecl);

    // ═══════════════════════════════════════════════════════════════════════
    // Generic parent class support — protocol-based type erasure
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when a property on a generic parent type can be wrapped via @_cdecl
    /// using protocol-based type erasure.
    /// </summary>
    private static bool CanEmitGenericClassPropertyWrapper(
        PropertyDecl propertyDecl, TypeDecl parentTypeDecl)
    {
        // Only class types — protocol dispatch via existential cast
        if (parentTypeDecl is not ClassDecl)
            return false;

        // Static properties don't need self-based erasure, but static dispatch
        // uses wrong metadata for generic types — skip for now
        if (propertyDecl.IsStatic)
            return false;

        // Property type must not reference the parent's generic type parameters
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();
        if (MethodWrapperEmitter.TypeSpecReferencesGenericParam(propertyDecl.SwiftTypeSpec, genericParamNames))
            return false;

        return true;
    }

    /// <summary>
    /// Emits protocol declaration and conformance for a property getter on a generic class type.
    /// </summary>
    private static string EmitGetterProtocolAndConformance(
        SwiftWriter swiftWriter, PropertyDecl propertyDecl, string symbolName, string moduleQualifiedName)
    {
        var protocolName = $"_SBW_PG_{EmitterUtility.DeterministicHash8(symbolName)}";
        var propertySwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(propertyDecl.SwiftTypeSpec);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            private protocol {{protocolName}} {
                var {{propertyDecl.Name}}: {{propertySwiftType}} { get }
            }
            extension {{moduleQualifiedName}}: {{protocolName}} {}
            """);

        return protocolName;
    }

    /// <summary>
    /// Emits protocol declaration and conformance for a property setter on a generic class type.
    /// </summary>
    private static string EmitSetterProtocolAndConformance(
        SwiftWriter swiftWriter, PropertyDecl propertyDecl, string symbolName, string moduleQualifiedName)
    {
        var protocolName = $"_SBW_PS_{EmitterUtility.DeterministicHash8(symbolName)}";
        var propertySwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(propertyDecl.SwiftTypeSpec);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            private protocol {{protocolName}} {
                var {{propertyDecl.Name}}: {{propertySwiftType}} { get set }
            }
            extension {{moduleQualifiedName}}: {{protocolName}} {}
            """);

        return protocolName;
    }

    /// <summary>
    /// Describes the @_cdecl return type mapping for a property getter.
    /// </summary>
    internal record CdeclReturnMapping(string cdeclReturnType, CdeclReturnKind Kind);

    /// <summary>
    /// Categories of @_cdecl return type handling.
    /// </summary>
    internal enum CdeclReturnKind
    {
        Direct,               // Primitive, frozen struct — return by value
        Bool,                 // Bool → Int8 conversion
        String,               // String → SBW_Utf8Slice
        SimpleEnum,           // Enum → raw value type
        ClassPointer,         // Class → Unmanaged.passRetained().toOpaque()
        OptionalClassPointer, // Optional<Class> → result.map { Unmanaged.passRetained($0).toOpaque() }
        IndirectResult        // Non-frozen struct, complex enum → writes to resultPtr
    }
}
