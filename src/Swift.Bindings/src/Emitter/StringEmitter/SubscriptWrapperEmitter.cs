// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits per-subscript @_cdecl Swift wrappers that route subscript accessor P/Invokes
/// through C calling convention, eliminating CallConvSwift ABI mismatches on NativeAOT/ARM64.
///
/// For each subscript accessor, generates a @_cdecl free function in the wrapper library that:
/// - Receives C-compatible parameters (self as pointer, index params via GetCdeclParamMapping, newValue for setters)
/// - Reconstructs self and index params, accesses subscript via bracket syntax
/// - Returns the result via C ABI (appropriate type mapping per GetCdeclReturnMapping)
///
/// Modeled after PropertyWrapperEmitter but with index parameter support.
/// State tracked on <see cref="ModuleEmissionContext"/>.
/// </summary>
public static class SubscriptWrapperEmitter
{
    /// <summary>
    /// Pure query: determines whether a subscript accessor should use a @_cdecl wrapper.
    /// Checked per-accessor (getter/setter signatures differ due to newValue).
    /// </summary>
    public static bool ShouldEmitSubscriptWrapper(SubscriptDecl subscriptDecl, AccessorDecl accessor, MethodEnvironment env)
    {
        // 1. xcframework mode required (wrapper library must exist)
        if (!WrapperValidation.IsXCFrameworkMode(env.TypeDatabase))
            return false;

        // 2. Generic parent type — allow non-final class instance subscripts with concrete signatures
        if (env.ParentDecl is TypeDecl td && td.IsGeneric)
        {
            if (!CanEmitGenericClassSubscriptWrapper(subscriptDecl, td))
                return false;
        }

        // 3. Not static (static subscripts aren't C# indexers)
        if (subscriptDecl.IsStatic)
            return false;

        // 4. No closure index params
        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (env.ClosureHandler.IsClosure(param))
                return false;
        }

        // 5. No async accessors
        if (accessor.Method.IsAsync)
            return false;

        // 6. No non-copyable struct parent
        if (WrapperValidation.IsNonCopyableStructParent(env.ParentDecl))
            return false;

        // 6b. Actor isolation: actor types, actor-isolated accessors, @MainActor parent types
        if (WrapperValidation.IsActorIsolatedMember(env.ParentDecl, accessor.Method.IsActorIsolated))
            return false;

        // 7. No opaque return type (some Protocol)
        if (subscriptDecl.ReturnTypeSpec is ProtocolListTypeSpec { IsOpaque: true })
            return false;

        // 8. No unsupported generic container params/returns (Result<T,E>, Optional<existential>).
        //    Optional<value-type> allowed (IndirectResult). Array/Dictionary/Set allowed (UnsafeRawPointer transport).
        if (WrapperValidation.IsUnsupportedGenericContainer(subscriptDecl.ReturnTypeSpec, env.TypeDatabase))
            return false;

        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (WrapperValidation.IsUnsupportedGenericContainer(param.SwiftTypeSpec, env.TypeDatabase))
                return false;
        }

        // 9. No closure return types
        if (subscriptDecl.ReturnTypeSpec is ClosureTypeSpec)
            return false;

        // 10. Tuple return types: allowed — routed through IndirectResult (resultPtr buffer).

        // 11. No nested type returns
        if (WrapperValidation.IsNestedType(subscriptDecl.ReturnTypeSpec))
            return false;

        // 12. No nested frozen struct index parameters
        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (param.SwiftTypeSpec is not NamedTypeSpec namedSpec)
                continue;
            if (!env.TypeDatabase.TryGetTypeRecord(namedSpec, out var typeRecord))
                continue;
            if (typeRecord.Kind != TypeRecordKind.Struct || !MarshallingHelpers.IsTypeFrozen(typeRecord))
                continue;
            var name = namedSpec.Name;
            var dotIndex = name.IndexOf('.');
            if (dotIndex >= 0 && name.Substring(dotIndex + 1).Contains('.'))
                return false;
        }

        // 13. No non-primitive frozen struct index parameters
        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (ConstructorWrapperEmitter.IsCdeclPrimitive(param.SwiftTypeSpec))
                continue;
            if (param.SwiftTypeSpec is NamedTypeSpec strNamed && strNamed.Name == "Swift.String")
                continue;
            if (env.TypeDatabase.TryGetTypeRecord(param.SwiftTypeSpec, out var typeRecord) &&
                typeRecord.Kind == TypeRecordKind.Struct &&
                MarshallingHelpers.IsTypeFrozen(typeRecord))
                return false;
        }

        // 14. Skip subscripts with raw ABI generic type params (τ_0_0) in return type or index params.
        // These leak from parent type generics and cause Swift compilation failures.
        if (subscriptDecl.ReturnTypeSpec != null && WrapperValidation.ContainsRawGenericTypeParam(subscriptDecl.ReturnTypeSpec))
            return false;
        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (param.SwiftTypeSpec != null && WrapperValidation.ContainsRawGenericTypeParam(param.SwiftTypeSpec))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns a human-readable skip reason if the subscript wrapper would be rejected, or null if it passes all gates.
    /// Mirrors the guard order in <see cref="ShouldEmitSubscriptWrapper"/> for emission report diagnostics.
    /// </summary>
    public static string? GetRejectionReason(SubscriptDecl subscriptDecl, AccessorDecl accessor, MethodEnvironment env)
    {
        if (!WrapperValidation.IsXCFrameworkMode(env.TypeDatabase))
            return null; // not in xcframework mode — not a skip, just N/A

        if (env.ParentDecl is TypeDecl td && td.IsGeneric &&
            !CanEmitGenericClassSubscriptWrapper(subscriptDecl, td))
            return "generic_parent_type";
        if (subscriptDecl.IsStatic)
            return "static_subscript";
        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (env.ClosureHandler.IsClosure(param))
                return "closure_index_param";
        }
        if (accessor.Method.IsAsync)
            return "async_accessor";
        if (WrapperValidation.IsNonCopyableStructParent(env.ParentDecl))
            return "non_copyable_struct_parent";
        if (WrapperValidation.IsActorIsolatedMember(env.ParentDecl, accessor.Method.IsActorIsolated))
            return "actor_isolated_subscript";
        if (subscriptDecl.ReturnTypeSpec is ProtocolListTypeSpec { IsOpaque: true })
            return "opaque_return_type";
        if (WrapperValidation.IsUnsupportedGenericContainer(subscriptDecl.ReturnTypeSpec, env.TypeDatabase))
            return "unsupported_generic_container";
        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (WrapperValidation.IsUnsupportedGenericContainer(param.SwiftTypeSpec, env.TypeDatabase))
                return "unsupported_generic_container_param";
        }
        if (subscriptDecl.ReturnTypeSpec is ClosureTypeSpec)
            return "closure_return_type";
        if (WrapperValidation.IsNestedType(subscriptDecl.ReturnTypeSpec))
            return "nested_type_return";
        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (param.SwiftTypeSpec is not NamedTypeSpec namedSpec) continue;
            if (!env.TypeDatabase.TryGetTypeRecord(namedSpec, out var typeRecord)) continue;
            if (typeRecord.Kind != TypeRecordKind.Struct || !MarshallingHelpers.IsTypeFrozen(typeRecord)) continue;
            var name = namedSpec.Name;
            var dotIndex = name.IndexOf('.');
            if (dotIndex >= 0 && name.Substring(dotIndex + 1).Contains('.'))
                return "nested_frozen_struct_index_param";
        }
        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (ConstructorWrapperEmitter.IsCdeclPrimitive(param.SwiftTypeSpec)) continue;
            if (param.SwiftTypeSpec is NamedTypeSpec strNamed && strNamed.Name == "Swift.String") continue;
            if (env.TypeDatabase.TryGetTypeRecord(param.SwiftTypeSpec, out var typeRecord) &&
                typeRecord.Kind == TypeRecordKind.Struct &&
                MarshallingHelpers.IsTypeFrozen(typeRecord))
                return "non_primitive_frozen_struct_index_param";
        }
        if (subscriptDecl.ReturnTypeSpec != null && WrapperValidation.ContainsRawGenericTypeParam(subscriptDecl.ReturnTypeSpec))
            return "raw_generic_type_params";
        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (param.SwiftTypeSpec != null && WrapperValidation.ContainsRawGenericTypeParam(param.SwiftTypeSpec))
                return "raw_generic_type_params";
        }

        return null;
    }

    /// <summary>
    /// Gets the @_cdecl symbol name for a subscript accessor wrapper.
    /// </summary>
    public static string GetSubscriptAccessorSymbolName(string moduleName, string typeName, string mangledName, bool isGetter)
    {
        var hash = EmitterUtility.DeterministicHash8(mangledName);
        var safeTypeName = typeName.Replace(".", "_");
        var prefix = isGetter ? "SubGet" : "SubSet";
        return $"SBW_{prefix}_{moduleName}_{safeTypeName}_{hash}";
    }

    /// <summary>
    /// Emits a @_cdecl Swift wrapper for a subscript getter.
    /// Params: [resultPtr if needed] → [index params via GetCdeclParamMapping] → self_
    /// Body: reconstruct self, reconstruct index params, let result = obj[key1, key2, ...]
    /// </summary>
    public static void EmitSwiftSubscriptGetterWrapper(
        SwiftWriter swiftWriter,
        SubscriptDecl subscriptDecl,
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
        bool isString = WitnessDispatchEmitter.IsStringType(subscriptDecl.ReturnTypeSpec);

        // Determine return mapping
        var (returnMapping, needsResultPtr) = isString
            ? (new PropertyWrapperEmitter.CdeclReturnMapping("SBW_Utf8Slice", PropertyWrapperEmitter.CdeclReturnKind.String), true)
            : PropertyWrapperEmitter.GetCdeclReturnMapping(subscriptDecl.ReturnTypeSpec, env.TypeDatabase);

        if (isString)
            needsResultPtr = true;

        // Ensure SBW_Utf8Slice infrastructure is emitted for string returns
        if (isString)
        {
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, ctx);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, ctx);
        }

        // Build Swift parameter list — phase ordering from CdeclSignatureContract.
        // ResultPtr is handled outside the loop using the emitter's own needsResultPtr logic.
        var swiftParams = new List<string>();
        var reconstructionLines = new List<string>();
        var callArgs = new List<string>();
        bool isGenericParent = MethodWrapperEmitter.IsGenericClassParent(env.ParentDecl);

        if (needsResultPtr)
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");

        var order = CdeclSignatureContract.DetermineParameterOrder(env, overrideNeedsResultPtr: needsResultPtr);
        foreach (var phase in order.Phases)
        {
            switch (phase)
            {
                case CdeclPhase.ResultPtr:
                    break; // Already handled above

                case CdeclPhase.Arguments:
                    foreach (var param in subscriptDecl.IndexParameters)
                    {
                        if (param.SwiftTypeSpec.IsEmptyTuple)
                            continue;

                        var label = !string.IsNullOrEmpty(param.PrivateName) ? param.PrivateName : param.Name;
                        var (cdeclParam, reconstruction, callArg) = ConstructorWrapperEmitter.GetCdeclParamMapping(
                            param, label, env, omitLabels: false);
                        swiftParams.Add(cdeclParam);
                        if (reconstruction != null)
                            reconstructionLines.Add(reconstruction);
                        callArgs.Add(callArg);
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
                    if (isClass)
                        swiftParams.Add("_ self_: UnsafeMutableRawPointer");
                    else
                        swiftParams.Add("_ self_: UnsafeRawPointer");
                    break;
            }
        }

        var swiftParamString = string.Join(", ", swiftParams);

        // Return clause
        string returnClause = needsResultPtr ? "" : $" -> {returnMapping.cdeclReturnType}";

        var swiftFuncName = $"_sbw_subget_{EmitterUtility.DeterministicHash8(symbolName)}";

        // Build bracket access expression
        var subscriptAccess = BuildSubscriptAccessExpr("obj", callArgs);

        // For generic parent class types, emit protocol + conformance for type erasure
        string? protocolName = null;
        if (isGenericParent)
        {
            protocolName = EmitGetterProtocolAndConformance(
                swiftWriter, subscriptDecl, symbolName, moduleQualifiedName);
        }

        // Emit the @_cdecl function
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Subscript getter @_cdecl wrapper for {{moduleQualifiedName}}.subscript.
            // Routes through C calling convention to avoid CallConvSwift crash on NativeAOT.
            """);

        if (parentTypeDecl?.IsMainActorIsolated == true)
            swiftWriter.WriteLine("@MainActor");

        swiftWriter.WriteLines($$"""
            @_cdecl("{{symbolName}}")
            """);
        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){returnClause} {{");
        swiftWriter.Indent++;

        // Emit parameter reconstruction lines
        foreach (var line in reconstructionLines)
            swiftWriter.WriteLine(line);

        // Reconstruct self
        if (isGenericParent && protocolName != null)
        {
            swiftWriter.WriteLine($"let obj = Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! any {protocolName}");
        }
        else
        {
            EmitSelfReconstruction(swiftWriter, isClass, moduleQualifiedName);
        }

        // Emit return based on type category
        if (isString)
        {
            EmitStringGetterBody(swiftWriter, subscriptAccess);
        }
        else if (needsResultPtr)
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(subscriptDecl.ReturnTypeSpec);
            swiftWriter.WriteLine($"let result = {subscriptAccess}");
            swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {swiftType}.self, repeating: result, count: 1)");
        }
        else
        {
            EmitDirectReturn(swiftWriter, subscriptAccess, subscriptDecl.ReturnTypeSpec, env.TypeDatabase, returnMapping);
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits a @_cdecl Swift wrapper for a subscript setter.
    /// Params: [newValue params via GetCdeclParamMapping] → [index params] → self_ (mutable)
    /// Body: reconstruct self/params, obj[key1, key2, ...] = newValueVal
    /// </summary>
    public static void EmitSwiftSubscriptSetterWrapper(
        SwiftWriter swiftWriter,
        SubscriptDecl subscriptDecl,
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
        bool isClass = env.ParentDecl is ClassDecl;
        bool isString = WitnessDispatchEmitter.IsStringType(subscriptDecl.ReturnTypeSpec);

        // Build parameter list — phase ordering from CdeclSignatureContract.
        var swiftParams = new List<string>();
        var reconstructionLines = new List<string>();
        var callArgs = new List<string>();
        bool isGenericParent = MethodWrapperEmitter.IsGenericClassParent(env.ParentDecl);

        var order = CdeclSignatureContract.DetermineParameterOrder(env,
            overrideNeedsResultPtr: false, overrideHasArguments: true);
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
                            SwiftTypeSpec = subscriptDecl.ReturnTypeSpec,
                            Name = "newValue",
                            PrivateName = "newValue",
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = null
                        };
                        // omitLabels: false — setters always need .load(as:) reconstruction for large Optionals.
                        var (cdeclParam, reconstruction, _) = ConstructorWrapperEmitter.GetCdeclParamMapping(
                            newValueArg, "newValue", env, omitLabels: false);
                        swiftParams.Add(cdeclParam);
                        if (reconstruction != null)
                            reconstructionLines.Add(reconstruction);
                    }

                    // Index parameters
                    foreach (var param in subscriptDecl.IndexParameters)
                    {
                        if (param.SwiftTypeSpec.IsEmptyTuple)
                            continue;

                        var label = !string.IsNullOrEmpty(param.PrivateName) ? param.PrivateName : param.Name;
                        var (cdeclParam, reconstruction, callArg) = ConstructorWrapperEmitter.GetCdeclParamMapping(
                            param, label, env, omitLabels: false);
                        swiftParams.Add(cdeclParam);
                        if (reconstruction != null)
                            reconstructionLines.Add(reconstruction);
                        callArgs.Add(callArg);
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
                    // Always mutable for setters
                    swiftParams.Add("_ self_: UnsafeMutableRawPointer");
                    break;
            }
        }

        var swiftParamString = string.Join(", ", swiftParams);
        var swiftFuncName = $"_sbw_subset_{EmitterUtility.DeterministicHash8(symbolName)}";

        // For generic parent class types, emit protocol + conformance for type erasure
        string? protocolName = null;
        if (isGenericParent)
        {
            protocolName = EmitSetterProtocolAndConformance(
                swiftWriter, subscriptDecl, symbolName, moduleQualifiedName);
        }

        // Emit the @_cdecl function
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Subscript setter @_cdecl wrapper for {{moduleQualifiedName}}.subscript.
            // Routes through C calling convention to avoid CallConvSwift crash on NativeAOT.
            """);

        if (parentTypeDecl?.IsMainActorIsolated == true)
            swiftWriter.WriteLine("@MainActor");

        swiftWriter.WriteLines($$"""
            @_cdecl("{{symbolName}}")
            """);
        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}) {{");
        swiftWriter.Indent++;

        // Emit reconstruction lines
        foreach (var line in reconstructionLines)
            swiftWriter.WriteLine(line);

        // Get the value expression
        string valueExpr = isString ? "newValue" :
            (reconstructionLines.Any(l => l.Contains("newValueVal")) ? "newValueVal" : "newValue");

        // Build bracket access and emit assignment
        var subscriptAccess = BuildSubscriptAccessExpr(
            isClass ? "obj" : $"self_.assumingMemoryBound(to: {moduleQualifiedName}.self).pointee",
            callArgs);

        if (isGenericParent && protocolName != null)
        {
            swiftWriter.WriteLine($"var obj = Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! any {protocolName}");
            swiftWriter.WriteLine($"obj[{string.Join(", ", callArgs.Select(StripArgLabel))}] = {valueExpr}");
        }
        else if (isClass)
        {
            swiftWriter.WriteLine($"let obj = Unmanaged<{moduleQualifiedName}>.fromOpaque(self_).takeUnretainedValue()");
            swiftWriter.WriteLine($"obj[{string.Join(", ", callArgs.Select(StripArgLabel))}] = {valueExpr}");
        }
        else
        {
            swiftWriter.WriteLine($"self_.assumingMemoryBound(to: {moduleQualifiedName}.self).pointee[{string.Join(", ", callArgs.Select(StripArgLabel))}] = {valueExpr}");
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Builds a subscript access expression: obj[arg1, arg2, ...]
    /// </summary>
    private static string BuildSubscriptAccessExpr(string selfExpr, List<string> callArgs)
    {
        var indexArgs = string.Join(", ", callArgs.Select(StripArgLabel));
        return $"{selfExpr}[{indexArgs}]";
    }

    /// <summary>
    /// Strips argument labels from call arguments for bracket syntax.
    /// "key: keyVal" → "keyVal"
    /// </summary>
    private static string StripArgLabel(string callArg)
    {
        var colonIdx = callArg.IndexOf(':');
        if (colonIdx >= 0)
            return callArg.Substring(colonIdx + 1).Trim();
        return callArg;
    }

    private static void EmitSelfReconstruction(SwiftWriter swiftWriter, bool isClass, string moduleQualifiedName)
    {
        if (isClass)
            swiftWriter.WriteLine($"let obj = Unmanaged<{moduleQualifiedName}>.fromOpaque(self_).takeUnretainedValue()");
        else
            swiftWriter.WriteLine($"let obj = self_.assumingMemoryBound(to: {moduleQualifiedName}.self).pointee");
    }

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

    private static void EmitDirectReturn(SwiftWriter swiftWriter, string expr,
        TypeSpec typeSpec, ITypeDatabase typeDatabase, PropertyWrapperEmitter.CdeclReturnMapping mapping)
    {
        switch (mapping.Kind)
        {
            case PropertyWrapperEmitter.CdeclReturnKind.Bool:
                swiftWriter.WriteLine($"return ({expr}) ? 1 : 0");
                break;

            case PropertyWrapperEmitter.CdeclReturnKind.SimpleEnum:
                if (typeDatabase.TryGetTypeRecord(typeSpec, out var enumRecord) &&
                    !string.IsNullOrEmpty(enumRecord.RawValueTypeName))
                {
                    swiftWriter.WriteLine($"return {mapping.cdeclReturnType}(({expr}).rawValue)");
                }
                else
                {
                    swiftWriter.WriteLine($"var result = {expr}");
                    swiftWriter.WriteLine($"return withUnsafePointer(to: &result) {{ UnsafeRawPointer($0).load(as: {mapping.cdeclReturnType}.self) }}");
                }
                break;

            case PropertyWrapperEmitter.CdeclReturnKind.ClassPointer:
                swiftWriter.WriteLine($"return Unmanaged.passRetained({expr}).toOpaque()");
                break;

            case PropertyWrapperEmitter.CdeclReturnKind.OptionalClassPointer:
                swiftWriter.WriteLine($"return ({expr}).map {{ Unmanaged.passRetained($0).toOpaque() }}");
                break;

            case PropertyWrapperEmitter.CdeclReturnKind.Direct:
            default:
                swiftWriter.WriteLine($"return {expr}");
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Generic parent class support — protocol-based type erasure
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when a subscript on a generic parent type can be wrapped via @_cdecl
    /// using protocol-based type erasure.
    /// </summary>
    private static bool CanEmitGenericClassSubscriptWrapper(
        SubscriptDecl subscriptDecl, TypeDecl parentTypeDecl)
    {
        // Only class types — protocol dispatch via existential cast
        if (parentTypeDecl is not ClassDecl)
            return false;

        // Static subscripts don't use self-based erasure
        if (subscriptDecl.IsStatic)
            return false;

        // Return type and all index param types must not reference parent's generic type params
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();

        if (MethodWrapperEmitter.TypeSpecReferencesGenericParam(subscriptDecl.ReturnTypeSpec, genericParamNames))
            return false;

        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (MethodWrapperEmitter.TypeSpecReferencesGenericParam(param.SwiftTypeSpec, genericParamNames))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Emits protocol declaration and conformance for a subscript getter on a generic class type.
    /// </summary>
    private static string EmitGetterProtocolAndConformance(
        SwiftWriter swiftWriter, SubscriptDecl subscriptDecl, string symbolName,
        string moduleQualifiedName)
    {
        var protocolName = $"_SBW_SG_{EmitterUtility.DeterministicHash8(symbolName)}";
        var returnSwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(subscriptDecl.ReturnTypeSpec);

        // Build subscript signature for protocol
        var indexParams = new List<string>();
        foreach (var param in subscriptDecl.IndexParameters)
        {
            var label = !string.IsNullOrEmpty(param.Name) ? param.Name : "_";
            var paramType = ExistentialBypassEmitter.RenderSwiftTypeSpec(param.SwiftTypeSpec);
            indexParams.Add($"{label}: {paramType}");
        }
        var indexParamString = string.Join(", ", indexParams);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            private protocol {{protocolName}} {
                subscript({{indexParamString}}) -> {{returnSwiftType}} { get }
            }
            extension {{moduleQualifiedName}}: {{protocolName}} {}
            """);

        return protocolName;
    }

    /// <summary>
    /// Emits protocol declaration and conformance for a subscript setter on a generic class type.
    /// </summary>
    private static string EmitSetterProtocolAndConformance(
        SwiftWriter swiftWriter, SubscriptDecl subscriptDecl, string symbolName,
        string moduleQualifiedName)
    {
        var protocolName = $"_SBW_SS_{EmitterUtility.DeterministicHash8(symbolName)}";
        var returnSwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(subscriptDecl.ReturnTypeSpec);

        // Build subscript signature for protocol
        var indexParams = new List<string>();
        foreach (var param in subscriptDecl.IndexParameters)
        {
            var label = !string.IsNullOrEmpty(param.Name) ? param.Name : "_";
            var paramType = ExistentialBypassEmitter.RenderSwiftTypeSpec(param.SwiftTypeSpec);
            indexParams.Add($"{label}: {paramType}");
        }
        var indexParamString = string.Join(", ", indexParams);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            private protocol {{protocolName}} {
                subscript({{indexParamString}}) -> {{returnSwiftType}} { get set }
            }
            extension {{moduleQualifiedName}}: {{protocolName}} {}
            """);

        return protocolName;
    }
}
