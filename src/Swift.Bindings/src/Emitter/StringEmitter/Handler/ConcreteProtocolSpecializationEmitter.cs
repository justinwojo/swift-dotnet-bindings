// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits concrete C# overloads for methods with protocol-constrained generic parameters.
///
/// When a Swift method has a generic parameter constrained to a protocol with associated types
/// or Self requirements (e.g., <c>func hash&lt;D: DataProtocol&gt;(data: D)</c>), C# cannot express
/// that constraint. This emitter generates one concrete C# overload per known conformer:
///
///   SHA256.Hash(Data data) → calls hash with D = Foundation.Data
///   SHA256.Hash(byte[] data) → calls hash with D = [UInt8]
///
/// Each overload gets its own Swift @_cdecl wrapper that calls the generic method with the
/// concrete type, eliminating the need for generic dispatch or witness table passing.
///
/// For generic constructors, emits static factory methods since C# cannot have generic constructors.
/// </summary>
public static partial class ConcreteProtocolSpecializationEmitter
{
    /// <summary>
    /// Scans a type's methods for specializable protocol-constrained generics and emits
    /// concrete C# overloads for each known conformer.
    /// </summary>
    public static void EmitConcreteSpecializations(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        TypeDecl typeDecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ConcreteSpecializationEngine engine,
        ILogger logger)
    {
        var specializableMethods = engine.FindSpecializableMethods(typeDecl);
        if (specializableMethods.Count == 0) return;

        var moduleName = typeDecl.SwiftTypeName.Module;
        var wrapperLibPath = typeDatabase.AsyncLibraryName ?? "libSwiftBindings";
        // Track emitted C# method signatures to prevent CS0111 duplicate member errors.
        // Sync path keeps its local dedup set; async path uses the shared
        // ModuleEmissionContext claim so it agrees with the Phase-4a predicate.
        var emittedSignatures = new HashSet<string>(StringComparer.Ordinal);

        foreach (var spec in specializableMethods)
        {
            var method = spec.Method;

            // Accessors and mutating methods stay gated: they use different emission
            // paths (property accessors / inout self). Constructors likewise — Phase A
            // only covers plain instance/static async methods.
            if (method.IsAccessor || method.IsMutating) continue;

            // Sync throws without async is also out of scope for CSM v1.
            if (!method.IsAsync && method.Throws) continue;

            // Skip if parent is a generic type (double generic context is complex)
            if (typeDecl.IsGeneric) continue;

            // Verify xcframework mode
            if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) continue;

            // Sync multi-param not yet supported — only async path handles cartesian product.
            if (!method.IsAsync && spec.SpecializableParams.Count != 1) continue;

            if (spec.SpecializableParams.Count == 1)
            {
                var param = spec.SpecializableParams[0];
                foreach (var conformer in param.Conformers)
                {
                    if (method.IsAsync)
                    {
                        TryEmitConcreteOverloadAsync(
                            csWriter, swiftWriter, method, typeDecl,
                            new[] { (param, conformer) },
                            moduleName, typeDatabase, emissionContext, logger);
                    }
                    else
                    {
                        TryEmitConcreteOverload(
                            csWriter, swiftWriter, method, typeDecl, param, conformer,
                            moduleName, wrapperLibPath, typeDatabase, emissionContext, emittedSignatures, logger);
                    }
                }
            }
            else if (method.IsAsync)
            {
                // Multi-param cartesian product: enumerate all combinations of conformers,
                // filter pairs whose cross-parameter same-type constraints (e.g., S.Element == T)
                // are not satisfied. Only emit the surviving substitution pairs.
                foreach (var pairing in CartesianPairings(spec.SpecializableParams))
                {
                    if (!ConformerPairingSatisfiesCoupling(pairing))
                    {
                        logger.LogDebug(
                            "CSM-async: Skipping {Method} multi-param pairing — cross-param same-type constraint not satisfied.",
                            method.Name);
                        continue;
                    }

                    TryEmitConcreteOverloadAsync(
                        csWriter, swiftWriter, method, typeDecl,
                        pairing,
                        moduleName, typeDatabase, emissionContext, logger);
                }
            }
        }
    }

    /// <summary>
    /// Enumerates the cartesian product of conformers across each specializable param,
    /// yielding one pairing per combination.
    /// </summary>
    private static IEnumerable<(ConcreteSpecializationEngine.SpecializableParam, ConcreteSpecializationEngine.ConcreteConformer)[]>
        CartesianPairings(IReadOnlyList<ConcreteSpecializationEngine.SpecializableParam> specParams)
    {
        var indices = new int[specParams.Count];
        while (true)
        {
            var pairing = new (ConcreteSpecializationEngine.SpecializableParam, ConcreteSpecializationEngine.ConcreteConformer)[specParams.Count];
            for (int i = 0; i < specParams.Count; i++)
            {
                pairing[i] = (specParams[i], specParams[i].Conformers[indices[i]]);
            }
            yield return pairing;

            // Advance indices (odometer-style).
            int pos = specParams.Count - 1;
            while (pos >= 0)
            {
                indices[pos]++;
                if (indices[pos] < specParams[pos].Conformers.Count) break;
                indices[pos] = 0;
                pos--;
            }
            if (pos < 0) yield break;
        }
    }

    /// <summary>
    /// Checks cross-param same-type constraints (e.g., S.Element == T) captured on
    /// <see cref="ConcreteSpecializationEngine.SpecializableParam.CouplingConstraints"/>.
    /// Each coupling on S reads: "S.conformer.AssociatedTypes[AssocName] must equal the
    /// chosen conformer Swift type of OtherParamName." Concrete (non-coupling) assoc-type
    /// constraints are already validated at conformer-filter time in the engine.
    /// </summary>
    private static bool ConformerPairingSatisfiesCoupling(
        (ConcreteSpecializationEngine.SpecializableParam Param, ConcreteSpecializationEngine.ConcreteConformer Conformer)[] pairing)
    {
        var paramTypeByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (p, c) in pairing)
        {
            paramTypeByName[p.GenericParam.TypeName] = c.SwiftQualifiedName;
        }

        foreach (var (param, conformer) in pairing)
        {
            if (param.CouplingConstraints is null) continue;
            foreach (var (assocName, otherParamName) in param.CouplingConstraints)
            {
                if (conformer.AssociatedTypes is null) return false;
                if (!conformer.AssociatedTypes.TryGetValue(assocName, out var declared))
                    return false;
                if (!paramTypeByName.TryGetValue(otherParamName, out var otherConformerType))
                    return false;
                if (!string.Equals(declared, otherConformerType, StringComparison.Ordinal))
                    return false;
            }
        }

        return true;
    }

    private static bool TryEmitConcreteOverload(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodDecl method,
        TypeDecl parentTypeDecl,
        ConcreteSpecializationEngine.SpecializableParam specParam,
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        string moduleName,
        string wrapperLibPath,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        HashSet<string> emittedSignatures,
        ILogger logger)
    {
        bool isConstructor = method.IsConstructor;
        bool isStatic = method.MethodType == MethodType.Static || isConstructor;
        bool isClass = parentTypeDecl is ClassDecl;

        // Skip nested type conformers — their C# names may differ from Swift names
        // (e.g., Words → WordsType to avoid property name collisions).
        // Detected by checking if ModuleQualifiedName has >2 dot segments (Module.Parent.Nested).
        if (conformer.SwiftType != null &&
            conformer.SwiftType.ModuleQualifiedName.Split('.').Length > 2)
        {
            logger.LogDebug(
                "ConcreteSpecializationEmitter: Skipping {Method} for {Conformer} — nested type conformer.",
                method.Name, conformer.SwiftQualifiedName);
            return false;
        }

        // Skip conformers whose C# managed type is not an ISwiftObject-backed class with
        // a SafeHandle Payload. Covers:
        //   • NativeTypeName remaps (Foundation.Data → NSData)
        //   • objcBridged records (Foundation.NSLocale, UIKit.UIImage, …) whose managed
        //     counterpart is an NSObject binding without .Payload
        //   • objcRooted Swift classes inheriting NSObject (same reason)
        // The generic-param marshalling emits `{name}.Payload.DangerousGetHandle()`, which
        // fails to compile for any of the above.
        if (conformer.SwiftType != null &&
            typeDatabase.TryGetTypeRecord(conformer.SwiftType, out var conformerRecord) &&
            (conformerRecord.NativeTypeName != null
                || MarshallingHelpers.IsObjCBridged(conformerRecord)
                || MarshallingHelpers.IsObjCRooted(conformerRecord)))
        {
            logger.LogDebug(
                "ConcreteSpecializationEmitter: Skipping {Method} for {Conformer} — ObjC/native-bridged conformer lacks Payload accessor.",
                method.Name, conformer.SwiftQualifiedName);
            return false;
        }

        // Build symbol name
        var safeConformerName = SanitizeTypeName(conformer.SwiftQualifiedName);
        var methodName = isConstructor ? "init" : method.Name;
        var mangledHash = EmitterUtility.DeterministicHash8(method.MangledName + conformer.SwiftQualifiedName);
        var cdeclSymbol = $"SBW_CSM_{moduleName}_{parentTypeDecl.Name}_{safeConformerName}_{methodName}_{mangledHash}";

        // Dedup guard
        if (!emissionContext.TryAddMethodWrapperSymbol(cdeclSymbol))
            return false;

        // Classify return type
        var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
        bool isVoidReturn = returnTypeSpec.IsEmptyTuple;
        var genericParamName = specParam.GenericParam.TypeName;
        bool returnsGenericParam = !isVoidReturn &&
            (IsGenericParamType(returnTypeSpec, genericParamName) ||
             IsGenericParamType(returnTypeSpec, GetAlternateDepthName(genericParamName)));
        bool isStringReturn = !isVoidReturn && !returnsGenericParam && WitnessDispatchEmitter.IsStringType(returnTypeSpec);

        // Skip methods returning Self — @_cdecl global functions can't return Self
        if (!isVoidReturn && !isConstructor && IsSelfReturn(returnTypeSpec))
        {
            logger.LogDebug(
                "ConcreteSpecializationEmitter: Skipping {Method} for {Conformer} — returns Self.",
                method.Name, conformer.SwiftQualifiedName);
            return false;
        }

        // Skip failable constructors — init?() returns Optional which we can't handle in @_cdecl
        if (isConstructor && IsOptionalReturn(returnTypeSpec))
        {
            logger.LogDebug(
                "ConcreteSpecializationEmitter: Skipping {Method} for {Conformer} — failable initializer.",
                method.Name, conformer.SwiftQualifiedName);
            return false;
        }

        // Skip if return type contains ANY unresolved generic param (τ_X_Y or associated types).
        // This catches: Container<T>, Container<T.Element>, and return types using related
        // generic params (e.g., τ_0_1 for associated types resolved by the conformance graph).
        var altGenericName = GetAlternateDepthName(genericParamName);
        if (!isVoidReturn && !returnsGenericParam && !isStringReturn && !isConstructor &&
            ContainsAnyGenericParam(returnTypeSpec))
        {
            logger.LogDebug(
                "ConcreteSpecializationEmitter: Skipping {Method} for {Conformer} — return type contains unresolved generic param.",
                method.Name, conformer.SwiftQualifiedName);
            return false;
        }

        // Skip non-constructor methods returning Optional — the CSM emitter doesn't have
        // proper Optional<T> type argument resolution or unwrapping logic yet.
        if (!isVoidReturn && !returnsGenericParam && !isStringReturn && !isConstructor &&
            IsOptionalReturn(returnTypeSpec))
        {
            logger.LogDebug(
                "ConcreteSpecializationEmitter: Skipping {Method} for {Conformer} — Optional return type not yet supported.",
                method.Name, conformer.SwiftQualifiedName);
            return false;
        }

        // For constructors, return type is the parent type
        if (isConstructor)
        {
            returnsGenericParam = false;
            isStringReturn = false;
            isVoidReturn = false; // Constructor returns self
        }

        // Skip methods whose indirect-result return type is not ISwiftObject.
        // GetSwiftTypeSize<T>() requires T : ISwiftObject; emitting it for tuples, closures,
        // frozen blittable structs, or IntPtr would produce uncompilable C#.
        if (!isVoidReturn && !isStringReturn)
        {
            bool needsIndirectResult = false;
            bool indirectReturnIsSwiftObject = true;

            if (isConstructor && !isClass)
            {
                needsIndirectResult = true;
                var parentTypeName = parentTypeDecl.SwiftTypeName;
                indirectReturnIsSwiftObject = parentTypeName != null
                    && typeDatabase.TryGetTypeRecord(parentTypeName, out var ctorRecord)
                    && ctorRecord.Kind == TypeRecordKind.Struct
                    && (!ctorRecord.Flags.HasFlag(TypeRecordFlags.Frozen)
                        || ctorRecord.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement));
            }
            else if (returnsGenericParam)
            {
                needsIndirectResult = true;
                var category = ClassifyConformerForCSharp(conformer, typeDatabase);
                indirectReturnIsSwiftObject = category is ConformerCategory.NonFrozenStruct or ConformerCategory.Class;
            }
            else if (!isConstructor)
            {
                var (mapping, _) = CdeclReturnMapping.Classify(returnTypeSpec, typeDatabase);
                if (mapping.Kind == CdeclReturnKind.IndirectResult)
                {
                    needsIndirectResult = true;
                    indirectReturnIsSwiftObject = returnTypeSpec is NamedTypeSpec irNamed
                        && typeDatabase.TryGetTypeRecord(irNamed, out var irRecord)
                        && (irRecord.Kind == TypeRecordKind.Class
                            || (irRecord.Kind == TypeRecordKind.Struct
                                && (!irRecord.Flags.HasFlag(TypeRecordFlags.Frozen)
                                    || irRecord.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement))));
                }
            }

            if (needsIndirectResult && !indirectReturnIsSwiftObject)
            {
                logger.LogDebug(
                    "ConcreteSpecializationEmitter: Skipping {Method} for {Conformer} — indirect result return type is not ISwiftObject.",
                    method.Name, conformer.SwiftQualifiedName);
                return false;
            }
        }

        // Verify all non-generic params are passable and don't reference the generic param
        if (!AreNonGenericParamsCompatible(method, specParam, typeDatabase))
        {
            logger.LogDebug(
                "ConcreteSpecializationEmitter: Skipping {Method} for {Conformer} — incompatible non-generic params.",
                method.Name, conformer.SwiftQualifiedName);
            return false;
        }

        // Skip methods where non-generic params reference the generic param in complex types
        // (e.g., DataResponse<τ_0_0, Error>). We can't substitute these without full type rewriting.
        if (HasNonGenericParamReferencingGeneric(method, specParam))
        {
            logger.LogDebug(
                "ConcreteSpecializationEmitter: Skipping {Method} for {Conformer} — non-generic param references generic type.",
                method.Name, conformer.SwiftQualifiedName);
            return false;
        }

        // Compute C# method signature key for dedup — prevents CS0111 when multiple conformers
        // produce the same visible method signature (name + parameter types).
        var csMethodName = isConstructor
            ? $"From{SanitizeTypeName(conformer.CSharpType)}"
            : NameProvider.ToPascalCase(method.Name);
        var sigKey = BuildCSharpSignatureKey(csMethodName, method, specParam, conformer, typeDatabase);
        if (!emittedSignatures.Add(sigKey))
        {
            logger.LogDebug(
                "ConcreteSpecializationEmitter: Skipping {Method} for {Conformer} — duplicate C# signature: {Sig}.",
                method.Name, conformer.SwiftQualifiedName, sigKey);
            return false;
        }

        // Merge availability (method + parent + conformer) once — both Swift and C#
        // sides need the same floor so generated code and callers agree.
        var mergedAvailability = WrapperEmitterHelpers.MergeAvailability(method.AvailabilityAnnotations, parentTypeDecl);
        if (conformer.AvailabilityAnnotations is { Count: > 0 } conformerAvailability)
        {
            var combined = mergedAvailability is null
                ? new List<AvailabilityAnnotation>()
                : new List<AvailabilityAnnotation>(mergedAvailability);
            combined.AddRange(conformerAvailability);
            mergedAvailability = combined;
        }

        // --- Emit Swift @_cdecl wrapper ---
        EmitSwiftWrapper(
            swiftWriter, method, parentTypeDecl, specParam, conformer,
            cdeclSymbol, moduleName, isClass, isConstructor, typeDatabase, emissionContext,
            mergedAvailability);

        // --- Emit C# method ---
        EmitCSharpMethod(
            csWriter, method, parentTypeDecl, specParam, conformer,
            cdeclSymbol, wrapperLibPath, isConstructor, isStatic, isClass,
            isVoidReturn, isStringReturn, returnsGenericParam, typeDatabase,
            mergedAvailability);

        logger.LogInformation(
            "Emitted concrete specialization: {Type}.{Method}<{Conformer}>",
            parentTypeDecl.Name, method.Name, conformer.SwiftQualifiedName);

        return true;
    }

    // ─── Swift Wrapper Generation ────────────────────────────────────

    private static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodDecl method,
        TypeDecl parentTypeDecl,
        ConcreteSpecializationEngine.SpecializableParam specParam,
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        string cdeclSymbol,
        string moduleName,
        bool isClass,
        bool isConstructor,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        IReadOnlyList<AvailabilityAnnotation>? mergedAvailability)
    {
        var parentSwiftName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var concreteSwiftType = conformer.SwiftLiteral ?? conformer.SwiftQualifiedName;
        bool isInstance = method.MethodType == MethodType.Instance && !isConstructor;

        // Classify return
        var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
        bool isVoidReturn = returnTypeSpec.IsEmptyTuple && !isConstructor;
        bool returnsGenericParam = !isVoidReturn && !isConstructor &&
            IsGenericParamType(returnTypeSpec, specParam.GenericParam.TypeName);
        bool isStringReturn = !isVoidReturn && !returnsGenericParam && !isConstructor &&
            WitnessDispatchEmitter.IsStringType(returnTypeSpec);

        // For string returns, ensure Utf8Slice helper
        if (isStringReturn)
        {
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, emissionContext);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, emissionContext);
        }

        // Build Swift parameter list
        var swiftParams = new List<string>();
        var callArgs = new List<string>();

        // Result pointer for indirect returns
        bool needsResultPtr = false;
        if (!isVoidReturn && !isStringReturn && !isConstructor)
        {
            if (returnsGenericParam)
            {
                // The concrete return type may need indirect return
                needsResultPtr = true;
            }
            else
            {
                var (mapping, _) = CdeclReturnMapping.Classify(returnTypeSpec, typeDatabase);
                needsResultPtr = mapping.Kind == CdeclReturnKind.IndirectResult;
            }
        }

        // Struct constructors return via result pointer (class constructors use Unmanaged)
        if (isConstructor && !isClass)
            needsResultPtr = true;

        if (needsResultPtr || isStringReturn)
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");

        // Regular parameters
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;

            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var argLabel = ClosureEmitter.GetSwiftArgLabelForCdecl(arg);

            var swiftGenericName = specParam.GenericParam.TypeName;
            var swiftAltGenericName = GetAlternateDepthName(swiftGenericName);
            if (IsGenericParamType(arg.SwiftTypeSpec, swiftGenericName) ||
                IsGenericParamType(arg.SwiftTypeSpec, swiftAltGenericName))
            {
                // Generic param → receive concrete type directly
                // For non-frozen struct conformers, receive as UnsafeRawPointer
                // For frozen/class conformers, receive directly
                var category = ClassifyConformerForSwiftParam(conformer, typeDatabase);
                switch (category)
                {
                    case ConformerCategory.Class:
                        swiftParams.Add($"_ _{label}: UnsafeMutableRawPointer");
                        callArgs.Add($"{argLabel}unsafeBitCast(OpaquePointer(_{label}), to: {concreteSwiftType}.self)");
                        break;
                    default:
                        // Frozen and non-frozen structs: pass as pointer, load value.
                        // Even frozen structs use pointer indirection because their C# binding
                        // is a class with SafeHandle, not a blittable C# struct.
                        swiftParams.Add($"_ _{label}: UnsafeRawPointer");
                        callArgs.Add($"{argLabel}_{label}.assumingMemoryBound(to: {concreteSwiftType}.self).pointee");
                        break;
                }
            }
            else if (arg.HasDefaultArg)
            {
                continue; // Swift fills the default
            }
            else
            {
                // Non-generic param — classify and map directly
                var abiCategory = MethodClosureBridge.ClassifyParam(arg, typeDatabase);
                switch (abiCategory)
                {
                    case MethodClosureBridge.ParamAbiCategory.Primitive:
                        swiftParams.Add($"_ _{label}: {ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec)}");
                        callArgs.Add($"{argLabel}_{label}");
                        break;
                    case MethodClosureBridge.ParamAbiCategory.ObjCHandle:
                    case MethodClosureBridge.ParamAbiCategory.PayloadHandle:
                        swiftParams.Add($"_ _{label}: UnsafeRawPointer");
                        var swiftTypeName = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
                        callArgs.Add($"{argLabel}unsafeBitCast(OpaquePointer(_{label}), to: {swiftTypeName}.self)");
                        break;
                    default:
                        swiftParams.Add($"_ _{label}: UnsafeRawPointer");
                        callArgs.Add($"{argLabel}_{label}");
                        break;
                }
            }
        }

        // Self parameter for instance methods
        if (isInstance)
        {
            if (isClass)
                swiftParams.Add("_ self_: UnsafeMutableRawPointer");
            else
                swiftParams.Add("_ self_: UnsafeRawPointer");
        }

        // Build self conversion
        string selfConversion = "";
        if (isInstance)
        {
            selfConversion = isClass
                ? $"let __self = unsafeBitCast(OpaquePointer(self_), to: {parentSwiftName}.self)"
                : $"let __self = self_.assumingMemoryBound(to: {parentSwiftName}.self).pointee";
        }

        // Build method call
        string callTarget = isInstance ? "__self" : parentSwiftName;
        string callExpr;
        if (isConstructor)
        {
            callExpr = $"{parentSwiftName}({string.Join(", ", callArgs)})";
        }
        else
        {
            callExpr = $"{callTarget}.{NameProvider.ParserNameToSwift(method)}({string.Join(", ", callArgs)})";
        }

        // Return type
        string swiftReturnType;
        if (isConstructor)
            swiftReturnType = isClass ? " -> UnsafeMutableRawPointer" : "";
        else if (isVoidReturn || isStringReturn || needsResultPtr)
            swiftReturnType = "";
        else if (returnsGenericParam)
            swiftReturnType = $" -> {concreteSwiftType}";
        else
            swiftReturnType = $" -> {ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec)}";

        // Emit
        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            parentTypeDecl, method.IsMainActorIsolated, method.IsNonisolated);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($"// Concrete specialization: {parentSwiftName}.{method.Name}<{concreteSwiftType}>");

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, cdeclSymbol, needsMainActor, mergedAvailability);
        swiftWriter.WriteLine($"public func {cdeclSymbol}(");
        swiftWriter.WriteLine($"    {string.Join(",\n    ", swiftParams)}");
        swiftWriter.WriteLine($"){swiftReturnType} {{");

        if (!string.IsNullOrEmpty(selfConversion))
            swiftWriter.WriteLine($"    {selfConversion}");

        if (isConstructor)
        {
            if (isClass)
            {
                swiftWriter.WriteLine($"    let _result = {callExpr}");
                swiftWriter.WriteLine($"    return Unmanaged.passRetained(_result as AnyObject).toOpaque()");
            }
            else
            {
                // Struct constructor: return via initializeMemory through result pointer
                swiftWriter.WriteLine($"    let _result = {callExpr}");
                swiftWriter.WriteLine($"    resultPtr.initializeMemory(as: ({parentSwiftName}).self, repeating: _result, count: 1)");
            }
        }
        else if (isVoidReturn)
        {
            swiftWriter.WriteLine($"    {callExpr}");
        }
        else if (isStringReturn)
        {
            OptionalPointerWrapperEmitter.EmitStringReturnBody(swiftWriter, callExpr, "    ");
        }
        else if (needsResultPtr)
        {
            var returnTypeStr = returnsGenericParam ? concreteSwiftType : ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnTypeSpec);
            swiftWriter.WriteLine($"    let _result = {callExpr}");
            swiftWriter.WriteLine($"    resultPtr.initializeMemory(as: ({returnTypeStr}).self, repeating: _result, count: 1)");
        }
        else
        {
            swiftWriter.WriteLine($"    return {callExpr}");
        }

        swiftWriter.WriteLine("}");
    }

    // ─── C# Code Generation ──────────────────────────────────────────

    private static void EmitCSharpMethod(
        CSharpWriter csWriter,
        MethodDecl method,
        TypeDecl parentTypeDecl,
        ConcreteSpecializationEngine.SpecializableParam specParam,
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        string cdeclSymbol,
        string wrapperLibPath,
        bool isConstructor,
        bool isStatic,
        bool isClass,
        bool isVoidReturn,
        bool isStringReturn,
        bool returnsGenericParam,
        ITypeDatabase typeDatabase,
        IReadOnlyList<AvailabilityAnnotation>? mergedAvailability)
    {
        var methodName = NameProvider.ToPascalCase(method.Name);
        if (isConstructor)
            methodName = $"From{SanitizeTypeName(conformer.CSharpType)}";

        // Build public parameter list and P/Invoke parameter list
        var publicParams = new List<string>();
        var pinvokeParams = new List<string>();
        var callArgs = new List<string>();

        bool needsResultPtr = false;
        if (isConstructor && !isClass)
        {
            // Struct constructors always return via result pointer
            needsResultPtr = true;
        }
        else if (!isVoidReturn && !isConstructor)
        {
            var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
            if (returnsGenericParam)
                needsResultPtr = true;
            else if (!isStringReturn)
            {
                var (mapping, _) = CdeclReturnMapping.Classify(returnTypeSpec, typeDatabase);
                needsResultPtr = mapping.Kind == CdeclReturnKind.IndirectResult;
            }
        }

        if (needsResultPtr || isStringReturn)
            pinvokeParams.Add("IntPtr resultPtr");

        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.HasDefaultArg) continue;

            var csName = NameProvider.GetCSharpParameterName(arg);

            var csGenericName = specParam.GenericParam.TypeName;
            var csAltGenericName = GetAlternateDepthName(csGenericName);
            if (IsGenericParamType(arg.SwiftTypeSpec, csGenericName) ||
                IsGenericParamType(arg.SwiftTypeSpec, csAltGenericName))
            {
                // Generic param → concrete type
                var category = ClassifyConformerForCSharp(conformer, typeDatabase);
                switch (category)
                {
                    case ConformerCategory.Class:
                        publicParams.Add($"{conformer.CSharpType} {csName}");
                        pinvokeParams.Add($"IntPtr {csName}");
                        callArgs.Add($"{csName}.Payload.DangerousGetHandle()");
                        break;
                    default:
                        // Frozen and non-frozen structs: pass via IntPtr.
                        // Even frozen structs are C# classes with SafeHandle, not blittable structs.
                        publicParams.Add($"{conformer.CSharpType} {csName}");
                        pinvokeParams.Add($"IntPtr {csName}");
                        callArgs.Add($"{csName}.Payload.DangerousGetHandle()");
                        break;
                }
            }
            else
            {
                // Non-generic param
                var abiCategory = MethodClosureBridge.ClassifyParam(arg, typeDatabase);
                switch (abiCategory)
                {
                    case MethodClosureBridge.ParamAbiCategory.Primitive:
                        if (MarshallingHelpers.IsBoolType(arg.SwiftTypeSpec))
                        {
                            publicParams.Add($"bool {csName}");
                            pinvokeParams.Add($"[MarshalAs(UnmanagedType.U1)] bool {csName}");
                        }
                        else
                        {
                            var primType = MethodClosureBridge.GetPInvokePrimitiveType(arg.SwiftTypeSpec);
                            publicParams.Add($"{primType} {csName}");
                            pinvokeParams.Add($"{primType} {csName}");
                        }
                        callArgs.Add(csName);
                        break;
                    case MethodClosureBridge.ParamAbiCategory.ObjCHandle:
                    {
                        // ObjC-bridged/rooted types (UIKit.UIImage, Foundation.NSLocale, …)
                        // are .NET iOS bindings around NSObject and don't implement
                        // ISwiftObject. Pass the native NSObject handle instead.
                        var csType = ResolvePublicCSharpType(arg.SwiftTypeSpec, typeDatabase);
                        publicParams.Add($"{csType} {csName}");
                        pinvokeParams.Add($"IntPtr {csName}");
                        callArgs.Add($"{csName}.Handle");
                        break;
                    }
                    case MethodClosureBridge.ParamAbiCategory.PayloadHandle:
                    {
                        var csType = ResolvePublicCSharpType(arg.SwiftTypeSpec, typeDatabase);
                        publicParams.Add($"{csType} {csName}");
                        pinvokeParams.Add($"IntPtr {csName}");
                        // SwiftHandle is an explicit interface impl on generated ISwiftObject
                        // types, so access via an ISwiftObject cast.
                        callArgs.Add($"((global::Swift.Runtime.ISwiftObject){csName}).SwiftHandle");
                        break;
                    }
                    default:
                        return; // Unsupported param type
                }
            }
        }

        // Self parameter
        if (!isStatic)
        {
            pinvokeParams.Add("IntPtr self_");
            var selfExpr = isClass ? "_handle.DangerousGetHandle()" : "_payload.DangerousGetHandle()";
            callArgs.Add(selfExpr);
        }

        // Determine C# return type
        string csReturnType = "void";
        if (isConstructor)
        {
            csReturnType = parentTypeDecl.Name;
        }
        else if (!isVoidReturn)
        {
            if (returnsGenericParam)
                csReturnType = conformer.CSharpType;
            else if (isStringReturn)
                csReturnType = "string";
            else
            {
                var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
                csReturnType = ResolvePublicCSharpType(returnTypeSpec, typeDatabase);
            }
        }

        string pinvokeReturn;
        if (isConstructor)
        {
            pinvokeReturn = isClass ? "IntPtr" : "void"; // struct ctors use result pointer
        }
        else if (isVoidReturn || isStringReturn || needsResultPtr)
            pinvokeReturn = "void";
        else if (returnsGenericParam)
        {
            // All conformers return via IntPtr — even frozen structs are C# classes with SafeHandle
            pinvokeReturn = "IntPtr";
        }
        else
        {
            // bool returns need a dedicated P/Invoke signature so PInvokeEmitHelper
            // emits `[return: MarshalAs(UnmanagedType.U1)]` and the public method's
            // `return` statement doesn't have to coerce an IntPtr to bool.
            var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
            pinvokeReturn = MarshallingHelpers.IsBoolType(returnTypeSpec) ? "bool" : "IntPtr";
        }

        // --- Emit P/Invoke ---
        csWriter.WriteLine();
        // P/Invoke needs the same OS guard as the public caller so CA1416 matches up when
        // the wrapper is stripped into a platform-specific assembly.
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, mergedAvailability, parentTypeDecl.AvailabilityAnnotations);
        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = cdeclSymbol,
            MethodName = cdeclSymbol,
            ReturnType = pinvokeReturn,
            ParametersString = string.Join(", ", pinvokeParams),
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Internal
        });

        // --- Emit public method ---
        csWriter.WriteLine();
        var staticStr = isStatic ? "static " : "";
        csWriter.WriteLine($"/// <summary>Concrete specialization for {conformer.CSharpType}.</summary>");
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, mergedAvailability, parentTypeDecl.AvailabilityAnnotations);
        csWriter.WriteLine($"public {staticStr}{csReturnType} {methodName}({string.Join(", ", publicParams)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Build P/Invoke call
        var pinvokeCallArgs = new List<string>();
        if (needsResultPtr || isStringReturn)
            pinvokeCallArgs.Add("resultPtr");
        pinvokeCallArgs.AddRange(callArgs);

        string pinvokeCall = $"{cdeclSymbol}({string.Join(", ", pinvokeCallArgs)})";

        if (needsResultPtr || isStringReturn)
        {
            if (isStringReturn)
            {
                // SBW_Utf8Slice is exactly 2 machine words
                csWriter.WriteLine("IntPtr resultPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(nint.Size * 2);");
            }
            else
            {
                // Non-ISwiftObject indirect results are filtered out by the skip guard in
                // EmitSpecializedMethod, so csReturnType is always an ISwiftObject class here.
                csWriter.WriteLine($"IntPtr resultPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(SwiftMarshal.GetSwiftTypeSize<{csReturnType}>());");
            }
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        if (isConstructor)
        {
            if (isClass)
            {
                csWriter.WriteLine($"return new {csReturnType}(new Swift.Runtime.SwiftHandle({pinvokeCall}));");
            }
            else
            {
                // Struct constructor: call writes into resultPtr, then marshal back
                csWriter.WriteLine($"{pinvokeCall};");
                csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{csReturnType}>(resultPtr);");
            }
        }
        else if (isVoidReturn)
        {
            csWriter.WriteLine($"{pinvokeCall};");
        }
        else if (isStringReturn)
        {
            csWriter.WriteLine($"{pinvokeCall};");
            csWriter.WriteLine("return SwiftMarshal.ReadUtf8Slice(resultPtr);");
        }
        else if (needsResultPtr)
        {
            csWriter.WriteLine($"{pinvokeCall};");
            csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{csReturnType}>(resultPtr);");
        }
        else if (returnsGenericParam)
        {
            csWriter.WriteLine($"return {pinvokeCall};");
        }
        else
        {
            csWriter.WriteLine($"return {pinvokeCall};");
        }

        if (needsResultPtr || isStringReturn)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(resultPtr); }");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");

        method.WasEmitted = true;
    }

    // ─── Classification helpers ──────────────────────────────────────

    private enum ConformerCategory
    {
        FrozenStruct,
        NonFrozenStruct,
        Class
    }

    private static ConformerCategory ClassifyConformerForSwiftParam(
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        ITypeDatabase typeDatabase)
    {
        if (conformer.SwiftType == null) return ConformerCategory.FrozenStruct; // Hint-based, assume primitive

        if (typeDatabase.TryGetTypeRecord(conformer.SwiftType, out var record))
        {
            if (record.Kind == TypeRecordKind.Class) return ConformerCategory.Class;
            if (record.Flags.HasFlag(TypeRecordFlags.Frozen)) return ConformerCategory.FrozenStruct;
            return ConformerCategory.NonFrozenStruct;
        }

        // ABI-indexed conformers without type records: use pointer indirection as safe default.
        // Direct passing can fail if the type isn't @_cdecl-compatible (e.g., non-ObjC enums).
        return ConformerCategory.NonFrozenStruct;
    }

    private static ConformerCategory ClassifyConformerForCSharp(
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        ITypeDatabase typeDatabase) =>
        ClassifyConformerForSwiftParam(conformer, typeDatabase);

    private static bool AreNonGenericParamsCompatible(
        MethodDecl method,
        ConcreteSpecializationEngine.SpecializableParam specParam,
        ITypeDatabase typeDatabase)
    {
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.HasDefaultArg) continue;
            if (IsGenericParamType(arg.SwiftTypeSpec, specParam.GenericParam.TypeName)) continue;
            if (IsGenericParamType(arg.SwiftTypeSpec, GetAlternateDepthName(specParam.GenericParam.TypeName))) continue;

            var category = MethodClosureBridge.ClassifyParam(arg, typeDatabase);
            if (category is not (MethodClosureBridge.ParamAbiCategory.Primitive
                or MethodClosureBridge.ParamAbiCategory.ObjCHandle
                or MethodClosureBridge.ParamAbiCategory.PayloadHandle))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsGenericParamType(TypeSpec typeSpec, string genericParamName)
    {
        return typeSpec is NamedTypeSpec named && named.Name == genericParamName;
    }

    /// <summary>
    /// Returns the alternate depth variant of a generic param name.
    /// Method-level generics can appear as τ_0_0 (on non-generic parents) or τ_1_0
    /// (on generic parents). We need to check both variants.
    /// </summary>
    private static string GetAlternateDepthName(string genericParamName)
    {
        if (genericParamName.StartsWith("τ_1_"))
            return "τ_0_" + genericParamName.Substring(4);
        if (genericParamName.StartsWith("τ_0_"))
            return "τ_1_" + genericParamName.Substring(4);
        return genericParamName;
    }

    private static bool IsSelfReturn(TypeSpec returnTypeSpec)
    {
        return returnTypeSpec is NamedTypeSpec named &&
               (named.Name == "Self" || named.Name.EndsWith(".Self"));
    }

    private static bool IsOptionalReturn(TypeSpec returnTypeSpec)
    {
        return returnTypeSpec is NamedTypeSpec named &&
               (named.Name == "Swift.Optional" || named.Name == "Optional");
    }

    /// <summary>
    /// Checks if any non-generic parameter references the generic type parameter in a complex
    /// type position (e.g., DataResponse&lt;τ_0_0, Error&gt;). These can't be simply substituted.
    /// Also checks the return type for any generic param reference at any depth.
    /// </summary>
    private static bool HasNonGenericParamReferencingGeneric(
        MethodDecl method,
        ConcreteSpecializationEngine.SpecializableParam specParam)
    {
        var genericName = specParam.GenericParam.TypeName;
        // Also check the depth-0 variant if the param is depth-1, and vice versa
        var altName = genericName.StartsWith("τ_1_")
            ? "τ_0_" + genericName.Substring(4)
            : genericName.StartsWith("τ_0_")
                ? "τ_1_" + genericName.Substring(4)
                : null;

        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.HasDefaultArg) continue;
            if (IsGenericParamType(arg.SwiftTypeSpec, genericName)) continue;
            if (altName != null && IsGenericParamType(arg.SwiftTypeSpec, altName)) continue;

            // Check if this non-generic param contains the generic param anywhere
            if (ContainsGenericParam(arg.SwiftTypeSpec, genericName)) return true;
            if (altName != null && ContainsGenericParam(arg.SwiftTypeSpec, altName)) return true;
        }

        // Also check return type with alternate depth
        var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
        if (!returnTypeSpec.IsEmptyTuple && altName != null)
        {
            if (ContainsGenericParam(returnTypeSpec, altName)) return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a TypeSpec contains references to a generic parameter anywhere in its tree.
    /// Used to detect complex return types like Container&lt;T&gt; that can't be trivially substituted.
    /// </summary>
    private static bool ContainsGenericParam(TypeSpec typeSpec, string genericParamName)
    {
        if (typeSpec is AssociatedTypeReferenceSpec assocRef)
        {
            // e.g., τ_0_0.SerializedObject — base type references the generic param
            return assocRef.BaseType == genericParamName;
        }
        else if (typeSpec is NamedTypeSpec named)
        {
            // Match exact name (τ_0_0) and associated type references (τ_0_0.SerializedObject)
            if (named.Name == genericParamName || named.Name.StartsWith(genericParamName + ".")) return true;
            return named.GenericParameters.Any(gp => ContainsGenericParam(gp, genericParamName));
        }
        else if (typeSpec is TupleTypeSpec tuple)
        {
            return tuple.Elements.Any(e => ContainsGenericParam(e, genericParamName));
        }
        else if (typeSpec is ClosureTypeSpec closure)
        {
            return ContainsGenericParam(closure.ReturnType, genericParamName) ||
                   ContainsGenericParam(closure.Arguments, genericParamName);
        }
        // Check generic parameters on any TypeSpec (they're defined on the base class)
        return typeSpec.GenericParameters.Any(gp => ContainsGenericParam(gp, genericParamName));
    }

    /// <summary>
    /// Builds a key representing the C# method signature (name + parameter types)
    /// to prevent emitting duplicate overloads.
    /// </summary>
    private static string BuildCSharpSignatureKey(
        string methodName,
        MethodDecl method,
        ConcreteSpecializationEngine.SpecializableParam specParam,
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        ITypeDatabase typeDatabase)
    {
        var parts = new List<string> { methodName };
        var genericName = specParam.GenericParam.TypeName;
        var altGenericName = GetAlternateDepthName(genericName);

        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.HasDefaultArg) continue;

            if (IsGenericParamType(arg.SwiftTypeSpec, genericName) ||
                IsGenericParamType(arg.SwiftTypeSpec, altGenericName))
            {
                parts.Add(conformer.CSharpType);
            }
            else
            {
                parts.Add(ResolvePublicCSharpType(arg.SwiftTypeSpec, typeDatabase));
            }
        }
        return string.Join("|", parts);
    }

    /// <summary>
    /// Checks if a TypeSpec contains any unresolved generic parameter (τ_X_Y pattern)
    /// or associated type reference anywhere in its tree.
    /// </summary>
    private static bool ContainsAnyGenericParam(TypeSpec typeSpec)
    {
        if (typeSpec is AssociatedTypeReferenceSpec)
            return true;
        if (typeSpec is NamedTypeSpec named)
        {
            if (named.Name.StartsWith("τ_")) return true;
            return named.GenericParameters.Any(ContainsAnyGenericParam);
        }
        else if (typeSpec is TupleTypeSpec tuple)
        {
            return tuple.Elements.Any(ContainsAnyGenericParam);
        }
        else if (typeSpec is ClosureTypeSpec closure)
        {
            return ContainsAnyGenericParam(closure.ReturnType) ||
                   ContainsAnyGenericParam(closure.Arguments);
        }
        return typeSpec.GenericParameters.Any(ContainsAnyGenericParam);
    }

    private static string ResolvePublicCSharpType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is NamedTypeSpec named)
        {
            try
            {
                var typeName = SwiftTypeName.FromModuleQualifiedName(named.Name);
                if (typeDatabase.TryGetTypeRecord(typeName, out var record))
                    return record.CSharpTypeName.FullyQualifiedName;
            }
            catch (ArgumentException) { }
            return named.Name.Split('.').Last();
        }
        return "IntPtr";
    }

    private static string SanitizeTypeName(string name)
    {
        return name.Replace(".", "_").Replace("<", "_").Replace(">", "")
                   .Replace(",", "_").Replace(" ", "").Replace("[", "Arr_").Replace("]", "");
    }
}
