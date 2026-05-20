// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits @_cdecl Swift wrappers for methods with method-level generic parameters
/// constrained to protocols without Self or associated type requirements.
///
/// Uses Swift 5.7+ implicit existential opening: the wrapper receives a pointer to
/// the existential container, loads it as <c>(any Protocol).self</c>, and passes
/// it to the original generic method. Swift automatically opens the existential
/// to recover the concrete type.
///
/// Only handles the simplest pattern:
/// - Single method-own generic parameter with at least one protocol conformance
/// - No Self or associated type requirements on the constraint protocol
/// - Generic param only in direct parameter positions (not in return type or containers)
/// - All non-generic parameters are @_cdecl compatible
/// </summary>
public static class MethodGenericBridgeEmitter
{
    /// <summary>
    /// Information about an eligible method-level generic parameter.
    /// </summary>
    internal record GenericParamInfo(
        GenericArgumentDecl Param,
        string SwiftParamName,
        SwiftTypeName ConstraintProtocol,
        string ConstraintProtocolSwiftName);

    /// <summary>
    /// Attempts to emit a method-generic bridge for the given method.
    /// Returns true if the method was handled (caller should skip normal emission).
    /// Returns false if the method is not eligible (caller proceeds normally).
    /// </summary>
    public static bool TryEmit(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        var methodDecl = env.MethodDecl;

        if (methodDecl.IsConstructor) return false;
        if (methodDecl.IsAccessor) return false;
        if (methodDecl.IsAsync) return false;
        if (methodDecl.Throws) return false; // @_cdecl can't throw; skip for v1
        if (methodDecl.UsesWrapperLibrary) return false;
        if (methodDecl.IsProtocolExtensionMethod) return false;

        // Must have method-own generic params
        if (!WrapperValidation.HasMethodOwnGenericParameters(methodDecl))
            return false;

        // Must be on a type (not free function, for simplicity)
        if (parentDecl == null)
            return false;

        // Skip generic parent types (double generic context is complex)
        if (parentDecl.IsGeneric)
            return false;

        // Find the eligible generic parameter
        var genericInfo = FindEligibleGenericParam(methodDecl, env.TypeDatabase);
        if (genericInfo == null)
            return false;

        // Verify all non-generic params are @_cdecl compatible
        if (!AreNonGenericParamsCompatible(methodDecl, genericInfo, env.TypeDatabase))
            return false;

        // Verify the return type does not contain the generic param
        if (ReturnContainsGenericParam(methodDecl, genericInfo.Param.TypeName))
            return false;

        // Reject metatype-shaped returns (bare or Optional<Metatype>) — the indirect
        // return buffer would render through ExistentialBypassEmitter and emit a bare
        // "Type" token in the wrapper. AreNonGenericParamsCompatible covers params.
        if (WrapperValidation.IsMetatypeTypeIncludingOptional(methodDecl.CSSignature[0].SwiftTypeSpec))
            return false;

        // Ensure xcframework mode (needed for wrapper library)
        if (!WrapperValidation.IsXCFrameworkMode(env.TypeDatabase))
            return false;

        // --- Eligible! Generate the bridge. ---

        var moduleName = parentDecl.SwiftTypeName.Module;
        var typeName = parentDecl.Name;
        var mangledHash = EmitterUtility.DeterministicHash8(methodDecl.MangledName);
        var cdeclSymbol = $"SBW_{moduleName}_{typeName.Replace(".", "_")}_{methodDecl.Name}_{mangledHash}_XM";

        // Emit Swift wrapper
        EmitSwiftWrapper(swiftWriter, env, parentDecl, genericInfo, cdeclSymbol, ctx);

        // Set method flags so PInvokeEmitter routes to wrapper library
        methodDecl.UsesWrapperLibrary = true;
        methodDecl.UsesFreeFunctionWrapper = true;
        methodDecl.HasGenericClosureBridge = true; // Reuse flag to signal bridge emission
        methodDecl.MangledName = cdeclSymbol;

        // Emit C# code (P/Invoke + public method)
        EmitCSharp(csWriter, env, parentDecl, genericInfo, cdeclSymbol);

        methodDecl.WasEmitted = true;
        return true;
    }

    /// <summary>
    /// Checks if a method is eligible for the method-generic bridge pattern.
    /// Used by MemberEmissionValidator to allow eligible methods through the placeholder gate.
    /// </summary>
    public static bool IsEligible(MethodDecl method, ITypeDatabase typeDatabase)
    {
        if (method.IsConstructor || method.IsAccessor || method.IsAsync || method.Throws)
            return false;
        if (method.UsesWrapperLibrary || method.IsProtocolExtensionMethod)
            return false;
        if (!WrapperValidation.HasMethodOwnGenericParameters(method))
            return false;
        if (method.ParentDecl is not TypeDecl parentDecl || parentDecl.IsGeneric)
            return false;
        if (!WrapperValidation.IsXCFrameworkMode(typeDatabase))
            return false;

        var genericInfo = FindEligibleGenericParam(method, typeDatabase);
        if (genericInfo == null)
            return false;
        if (!AreNonGenericParamsCompatible(method, genericInfo, typeDatabase))
            return false;
        if (ReturnContainsGenericParam(method, genericInfo.Param.TypeName))
            return false;
        if (WrapperValidation.IsMetatypeTypeIncludingOptional(method.CSSignature[0].SwiftTypeSpec))
            return false;

        return true;
    }

    // ─── Eligibility Helpers ─────────────────────────────────────────

    /// <summary>
    /// Finds the single method-own generic parameter with a simple protocol constraint.
    /// Returns null if the method has multiple own params or no eligible constraint.
    /// </summary>
    internal static GenericParamInfo? FindEligibleGenericParam(MethodDecl methodDecl, ITypeDatabase typeDatabase)
    {
        // Collect parent type generic parameter names
        var parentTypeParamNames = methodDecl.ParentDecl is TypeDecl td && td.IsGeneric
            ? new HashSet<string>(td.GenericParameters.Select(p => p.TypeName))
            : new HashSet<string>();

        // Find method-own generic params
        var ownParams = methodDecl.GenericParameters
            .Where(p => !parentTypeParamNames.Contains(p.TypeName))
            .ToList();

        // Must have exactly one own param for v1
        if (ownParams.Count != 1)
            return null;

        var param = ownParams[0];

        // Must be class-bound — the bridge uses Unmanaged<AnyObject>.fromOpaque()
        // which is only valid for heap-allocated Swift objects. Struct/enum conformers
        // would crash at runtime. Class-boundedness comes from either:
        //   (a) explicit AnyObject conformance on the generic param, or
        //   (b) a protocol conformance whose target protocol is itself class-bound
        //       (e.g. `protocol P: AnyObject` — the ABI JSON only lists P on the
        //       generic param, not AnyObject, so we must look up P's record).
        var hasExplicitAnyObject = param.GenericConformances
            .Any(c => c.Kind == ConformanceKind.Protocol &&
                       (c.ConformanceTarget.Name == "AnyObject" ||
                        c.ConformanceTarget.ModuleQualifiedName == "Swift.AnyObject"));

        // Must have exactly one non-AnyObject protocol conformance — multi-protocol
        // compositions (e.g. T: P & Q & AnyObject) are unsound because the wrapper
        // casts to `any <first protocol>` only, losing the second constraint's witness table.
        var protocolConformances = param.GenericConformances
            .Where(c => c.Kind == ConformanceKind.Protocol &&
                        c.ConformanceTarget.Name != "AnyObject" &&
                        c.ConformanceTarget.ModuleQualifiedName != "Swift.AnyObject")
            .ToList();
        if (protocolConformances.Count != 1)
            return null;

        var protocolConformance = protocolConformances[0];
        var protocolName = protocolConformance.ConformanceTarget;

        var hasTransitiveClassBound = !hasExplicitAnyObject &&
            typeDatabase.TryGetTypeRecord(protocolName, out var protocolRecord) &&
            protocolRecord.Kind == TypeRecordKind.Protocol &&
            (protocolRecord.Flags & TypeRecordFlags.ClassBound) != 0;

        if (!hasExplicitAnyObject && !hasTransitiveClassBound)
            return null;

        // Check if the protocol is known to have Self requirements that prevent
        // existential opening (Equatable, Hashable, Comparable, etc.)
        if (HasSelfOrAssociatedTypeRequirements(param, protocolName, typeDatabase))
            return null;

        // Verify the generic param only appears in direct parameter positions
        // (not nested inside containers like Array<T>, Optional<T>, closures, etc.)
        if (!GenericParamOnlyInDirectPositions(methodDecl, param.TypeName))
            return null;

        return new GenericParamInfo(
            param,
            param.TypeName,
            protocolName,
            protocolName.ModuleQualifiedName);
    }

    /// <summary>
    /// Checks if a protocol has Self or associated type requirements that prevent
    /// implicit existential opening when passing <c>any Protocol</c> to <c>&lt;T: Protocol&gt;</c>.
    /// </summary>
    private static bool HasSelfOrAssociatedTypeRequirements(
        GenericArgumentDecl param, SwiftTypeName protocolName, ITypeDatabase typeDatabase)
    {
        // Check the generic parameter's own associated type conformances
        if (param.AssosiatedTypeConformances.Count > 0)
            return true;

        // Well-known protocols with Self requirements (can't use implicit existential opening)
        var name = protocolName.ModuleQualifiedName;
        if (name is "Swift.Equatable" or "Swift.Hashable" or "Swift.Comparable"
            or "Swift.Identifiable" or "Swift.RawRepresentable"
            or "Swift.Strideable" or "Swift.Numeric" or "Swift.SignedNumeric"
            or "Swift.BinaryInteger" or "Swift.FloatingPoint"
            or "Swift.FixedWidthInteger" or "Swift.UnsignedInteger" or "Swift.SignedInteger"
            or "Swift.StringProtocol" or "Swift.CodingKey"
            or "Swift.Encodable" or "Swift.Decodable" or "Swift.Codable"
            or "Swift.Sequence" or "Swift.Collection" or "Swift.MutableCollection"
            or "Swift.BidirectionalCollection" or "Swift.RandomAccessCollection"
            or "Swift.RangeReplaceableCollection" or "Swift.LazySequenceProtocol"
            or "Swift.IteratorProtocol" or "Swift.SetAlgebra"
            or "Swift.AdditiveArithmetic" or "Swift.ExpressibleByIntegerLiteral"
            or "Swift.ExpressibleByFloatLiteral" or "Swift.ExpressibleByStringLiteral"
            or "Swift.ExpressibleByBooleanLiteral" or "Swift.ExpressibleByNilLiteral"
            or "Swift.ExpressibleByArrayLiteral" or "Swift.ExpressibleByDictionaryLiteral"
            or "Foundation.NSObjectProtocol")
        {
            return true;
        }

        // For library-defined protocols, check if the type database knows about PAT/Self requirements
        // via the MethodValidationGates check (which looks at the protocol's interface declaration)
        if (MethodValidationGates.IsUnsupportedProtocolConstraint(protocolName, typeDatabase))
            return true;

        return false;
    }

    /// <summary>
    /// Checks that the generic param only appears as a direct NamedTypeSpec parameter,
    /// not nested inside containers (Array&lt;T&gt;, Optional&lt;T&gt;), closures, or tuples.
    /// </summary>
    private static bool GenericParamOnlyInDirectPositions(MethodDecl methodDecl, string genericParamName)
    {
        foreach (var arg in methodDecl.CSSignature.Skip(1)) // Skip return type
        {
            if (!ContainsGenericParam(arg.SwiftTypeSpec, genericParamName))
                continue;

            // Must be a direct NamedTypeSpec matching the generic param name
            if (arg.SwiftTypeSpec is not NamedTypeSpec named || named.Name != genericParamName)
                return false;

            // inout generic params can't be bridged (existential cast produces immutable value)
            if (arg.IsInOut)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Returns true if the return type contains the generic parameter.
    /// </summary>
    private static bool ReturnContainsGenericParam(MethodDecl methodDecl, string genericParamName)
    {
        if (methodDecl.CSSignature.Count == 0) return false;
        var returnArg = methodDecl.CSSignature[0];
        return ContainsGenericParam(returnArg.SwiftTypeSpec, genericParamName);
    }

    /// <summary>
    /// Recursively checks if a TypeSpec contains a reference to the given generic parameter.
    /// </summary>
    private static bool ContainsGenericParam(TypeSpec typeSpec, string paramName)
    {
        switch (typeSpec)
        {
            case AssociatedTypeReferenceSpec assocRef:
                return assocRef.BaseType == paramName;
            case NamedTypeSpec named:
                if (named.Name == paramName) return true;
                // Associated types parsed from printedName (e.g. "τ_0_0.SerializedObject")
                // appear as NamedTypeSpec with the dot in the name, not AssociatedTypeReferenceSpec
                if (named.Name.StartsWith(paramName + ".")) return true;
                return named.GenericParameters.Any(gp => ContainsGenericParam(gp, paramName));
            case TupleTypeSpec tuple:
                return tuple.Elements.Any(e => ContainsGenericParam(e, paramName));
            case ClosureTypeSpec closure:
                return ContainsGenericParam(closure.Arguments, paramName) ||
                       ContainsGenericParam(closure.ReturnType, paramName);
            default:
                return false;
        }
    }

    /// <summary>
    /// Checks that all non-generic parameters are @_cdecl compatible.
    /// </summary>
    private static bool AreNonGenericParamsCompatible(
        MethodDecl method, GenericParamInfo genericInfo, ITypeDatabase typeDatabase)
    {
        foreach (var arg in method.CSSignature.Skip(1))
        {
            // Skip generic params (they're handled by the bridge)
            if (arg.SwiftTypeSpec is NamedTypeSpec named && named.Name == genericInfo.Param.TypeName)
                continue;

            // Skip defaulted params (Swift fills them)
            if (arg.HasDefaultArg) continue;

            // ABI passability allowlist is canonical on MethodClosureBridge.IsAbiCategoryPassable.
            if (!MethodClosureBridge.IsAbiCategoryPassable(
                    MethodClosureBridge.ClassifyParam(arg, typeDatabase)))
            {
                return false;
            }
        }
        return true;
    }

    // ─── Swift Wrapper Generation ────────────────────────────────────

    private static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        TypeDecl parentDecl,
        GenericParamInfo genericInfo,
        string cdeclSymbol,
        ModuleEmissionContext ctx)
    {
        // S5 audited (Tier B): the `_XM` suffix namespaces this symbol away from the
        // plain method wrapper (`SBW_{module}_{type}_{method}_{hash}`) and from the async
        // generic bridge (`_XMA`). Collision is impossible by suffix convention even for
        // the same method.
        if (!ctx.TryAddMethodWrapperSymbol(cdeclSymbol))
            return; // Already emitted

        var methodDecl = env.MethodDecl;
        bool isClass = parentDecl is ClassDecl;
        bool isInstance = methodDecl.MethodType != MethodType.Static;
        var moduleQualifiedName = parentDecl.SwiftTypeName.ModuleQualifiedName;
        // Determine return mapping
        var returnTypeSpec = methodDecl.CSSignature.First().SwiftTypeSpec;
        bool isVoidReturn = returnTypeSpec.IsEmptyTuple;
        bool isStringReturn = !isVoidReturn && WitnessDispatchEmitter.IsStringType(returnTypeSpec);

        // Ensure Utf8Slice infrastructure for string returns
        if (isStringReturn)
        {
            var moduleName = parentDecl.SwiftTypeName.Module;
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, ctx);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, ctx);
        }

        // Build Swift parameters
        var swiftParams = new List<string>();
        var callArgs = new List<string>();
        var reconstructions = new List<string>();

        // Result buffer for indirect returns
        bool needsResultPtr = !isVoidReturn && !isStringReturn &&
            CdeclReturnMapping.Classify(returnTypeSpec, env.TypeDatabase).needsResultPtr;
        if (needsResultPtr || isStringReturn)
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");

        // Regular parameters (with existential loading for generic params)
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;

            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;

            if (arg.SwiftTypeSpec is NamedTypeSpec named && named.Name == genericInfo.Param.TypeName)
            {
                // Generic parameter → receive class handle as UnsafeRawPointer,
                // recover object and cast to protocol existential for implicit opening
                swiftParams.Add($"_ _{label}: UnsafeRawPointer");
                var argLabel = GetSwiftArgLabel(arg);
                callArgs.Add($"{argLabel}(Unmanaged<AnyObject>.fromOpaque(_{label}).takeUnretainedValue() as! any {genericInfo.ConstraintProtocolSwiftName})");
            }
            else if (arg.HasDefaultArg)
            {
                // Defaulted param — omit from wrapper, Swift fills the default
                continue;
            }
            else
            {
                // Non-generic param — use standard CdeclParamMapper (preserve labels for method calls).
                // `useUtf8Strings: true` emits Swift.String as a (UInt8 ptr, Int len) pair plus a
                // reconstruction `let {label}Val = String(...)`. The matching C# side (pinvoke /
                // public / callArgs switches below) carries a Utf8Slice case so the ABI pairs
                // up. Mirrors the MethodClosureBridge.cs Utf8Slice marshalling shape.
                var (cdeclParam, reconstruction, callArg) = CdeclParamMapper.Map(arg, label, env, omitLabels: false, useUtf8Strings: true);
                swiftParams.Add(cdeclParam);
                callArgs.Add(callArg);
                if (!string.IsNullOrEmpty(reconstruction))
                    reconstructions.Add(reconstruction);
            }
        }

        // Self parameter (last for instance methods)
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
                ? $"let __self = unsafeBitCast(OpaquePointer(self_), to: {moduleQualifiedName}.self)"
                : $"let __self = self_.assumingMemoryBound(to: {moduleQualifiedName}.self).pointee";
        }

        string callTarget = isInstance ? "__self" : moduleQualifiedName;
        string methodCall = $"{callTarget}.{NameProvider.ParserNameToSwift(methodDecl)}({string.Join(", ", callArgs)})";
        // Note: throwing methods are excluded at TryEmit entry (v1 limitation)

        // Determine return type for @_cdecl function
        var returnKind = CdeclReturnKind.Direct;
        if (!isVoidReturn && !isStringReturn)
        {
            var (mapping, _) = CdeclReturnMapping.Classify(returnTypeSpec, env.TypeDatabase);
            returnKind = mapping.Kind;
        }
        bool isClassPointerReturn = returnKind is CdeclReturnKind.ClassPointer or CdeclReturnKind.OptionalClassPointer;
        string cdeclReturnType;
        if (isVoidReturn || isStringReturn || needsResultPtr) cdeclReturnType = "";
        else if (isClassPointerReturn)
            cdeclReturnType = returnKind == CdeclReturnKind.OptionalClassPointer
                ? " -> UnsafeMutableRawPointer?" : " -> UnsafeMutableRawPointer";
        else if (returnKind == CdeclReturnKind.Direct)
            cdeclReturnType = $" -> {ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec)}";
        else cdeclReturnType = "";

        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            parentDecl, methodDecl.IsMainActorIsolated, methodDecl.IsNonisolated);

        // Emit availability annotations from the method and ancestor chain.
        // @_cdecl wrappers are top-level functions and don't inherit enclosing type availability.
        var availability = WrapperEmitterHelpers.MergeAvailability(methodDecl.AvailabilityAnnotations, parentDecl);
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);

        // Emit the wrapper function
        if (needsMainActor)
            swiftWriter.WriteLine("@MainActor");
        swiftWriter.WriteLine($"@_cdecl(\"{cdeclSymbol}\")");
        swiftWriter.WriteLine($"public func {cdeclSymbol}(");
        swiftWriter.WriteLine($"    {string.Join(",\n    ", swiftParams)}");
        swiftWriter.WriteLine($"){cdeclReturnType} {{");

        if (!string.IsNullOrEmpty(selfConversion))
            swiftWriter.WriteLine($"    {selfConversion}");

        // Emit parameter reconstructions (e.g., pointer-to-value conversions for non-blittable types)
        foreach (var reconstruction in reconstructions)
            swiftWriter.WriteLine($"    {reconstruction}");

        // Call the method and handle return
        if (isVoidReturn)
        {
            swiftWriter.WriteLine($"    {methodCall}");
        }
        else if (isStringReturn)
        {
            OptionalPointerWrapperEmitter.EmitStringReturnBody(swiftWriter, methodCall, "    ");
        }
        else if (needsResultPtr)
        {
            var renderedReturn = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnTypeSpec);
            swiftWriter.WriteLine($"    let _result = {methodCall}");
            swiftWriter.WriteLine($"    resultPtr.initializeMemory(as: ({renderedReturn}).self, repeating: _result, count: 1)");
        }
        else if (isClassPointerReturn)
        {
            swiftWriter.WriteLine($"    return Unmanaged.passRetained({methodCall} as AnyObject).toOpaque()");
        }
        else
        {
            swiftWriter.WriteLine($"    return {methodCall}");
        }

        swiftWriter.WriteLine("}");
        swiftWriter.WriteLine();
    }

    // ─── C# Code Generation ──────────────────────────────────────────

    private static void EmitCSharp(
        CSharpWriter csWriter,
        MethodEnvironment env,
        TypeDecl parentDecl,
        GenericParamInfo genericInfo,
        string cdeclSymbol)
    {
        var methodDecl = env.MethodDecl;
        var asyncLibName = env.TypeDatabase.AsyncLibraryName ?? "SwiftBindings";
        var methodName = NameProvider.ToPascalCase(methodDecl.Name);
        var pInvokeName = NameProvider.GetPInvokeName(methodDecl);

        // Determine return type
        var returnTypeSpec = methodDecl.CSSignature.First().SwiftTypeSpec;
        bool isVoidReturn = returnTypeSpec.IsEmptyTuple;
        bool isStringReturn = !isVoidReturn && WitnessDispatchEmitter.IsStringType(returnTypeSpec);

        bool needsResultPtr = !isVoidReturn && !isStringReturn &&
            CdeclReturnMapping.Classify(returnTypeSpec, env.TypeDatabase).needsResultPtr;

        string csReturnType = "void";
        if (!isVoidReturn)
        {
            if (isStringReturn) csReturnType = "string";
            else
            {
                // Try to resolve the return type from the type database
                var factory = new TypeProjectionFactory();
                var projection = factory.Project(returnTypeSpec, new ProjectionContext
                {
                    TypeDatabase = env.TypeDatabase,
                    IsParameter = false,
                    ParentTypeDecl = parentDecl
                });
                csReturnType = projection?.PublicType ?? "IntPtr";
            }
        }

        // --- P/Invoke declaration ---
        EmitPInvoke(csWriter, env, genericInfo, cdeclSymbol, pInvokeName, asyncLibName,
            isVoidReturn, isStringReturn, needsResultPtr);

        // --- Public method ---
        EmitPublicMethod(csWriter, env, parentDecl, genericInfo, methodName, pInvokeName,
            csReturnType, isVoidReturn, isStringReturn, needsResultPtr);
    }

    private static void EmitPInvoke(
        CSharpWriter csWriter,
        MethodEnvironment env,
        GenericParamInfo genericInfo,
        string cdeclSymbol,
        string pInvokeName,
        string asyncLibName,
        bool isVoidReturn,
        bool isStringReturn,
        bool needsResultPtr)
    {
        var methodDecl = env.MethodDecl;
        var pinvokeParams = new List<string>();

        // Result buffer
        if (needsResultPtr || isStringReturn)
            pinvokeParams.Add("IntPtr resultPtr");

        // Regular parameters
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.HasDefaultArg) continue;

            var csName = NameProvider.GetCSharpParameterName(arg);

            if (arg.SwiftTypeSpec is NamedTypeSpec named && named.Name == genericInfo.Param.TypeName)
            {
                // Generic param → IntPtr (existential container pointer)
                pinvokeParams.Add($"IntPtr {csName}");
            }
            else
            {
                // Non-generic param
                var category = MethodClosureBridge.ClassifyParam(arg, env.TypeDatabase);
                switch (category)
                {
                    case MethodClosureBridge.ParamAbiCategory.Primitive:
                        if (MarshallingHelpers.IsBoolType(arg.SwiftTypeSpec))
                            pinvokeParams.Add($"[MarshalAs(UnmanagedType.U1)] bool {csName}");
                        else
                            pinvokeParams.Add($"{MethodClosureBridge.GetPInvokePrimitiveType(arg.SwiftTypeSpec)} {csName}");
                        break;
                    case MethodClosureBridge.ParamAbiCategory.Utf8Slice:
                        // Swift.String ABI: ptr+len pair matching the Swift @_silgen_name
                        // wrapper's `_ {label}Utf8Ptr: UnsafePointer<UInt8>, _ {label}Utf8Len: Int`.
                        pinvokeParams.Add($"IntPtr {csName}Utf8Ptr");
                        pinvokeParams.Add($"nint {csName}Utf8Len");
                        break;
                    default:
                        pinvokeParams.Add($"IntPtr {csName}");
                        break;
                }
            }
        }

        // Self parameter
        if (methodDecl.MethodType == MethodType.Instance)
            pinvokeParams.Add("IntPtr self_");

        // P/Invoke return type — string returns use indirect via resultPtr (void)
        string pinvokeReturn = "void";
        if (!isVoidReturn && !isStringReturn && !needsResultPtr)
        {
            pinvokeReturn = "IntPtr"; // Direct return
        }

        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = asyncLibName,
            EntryPoint = cdeclSymbol,
            MethodName = $"{pInvokeName}_XM",
            ReturnType = pinvokeReturn,
            ParametersString = string.Join(", ", pinvokeParams),
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Internal
        });
        csWriter.WriteLine();
    }

    private static void EmitPublicMethod(
        CSharpWriter csWriter,
        MethodEnvironment env,
        TypeDecl parentDecl,
        GenericParamInfo genericInfo,
        string methodName,
        string pInvokeName,
        string csReturnType,
        bool isVoidReturn,
        bool isStringReturn,
        bool needsResultPtr)
    {
        var methodDecl = env.MethodDecl;
        bool isClass = parentDecl is ClassDecl;
        bool isStatic = methodDecl.MethodType == MethodType.Static;

        // Build public parameter list
        var publicParams = new List<string>();
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.HasDefaultArg) continue;

            var csName = NameProvider.GetCSharpParameterName(arg);

            if (arg.SwiftTypeSpec is NamedTypeSpec named && named.Name == genericInfo.Param.TypeName)
            {
                // Generic param → ISwiftObject (any Swift object with a handle)
                publicParams.Add($"ISwiftObject {csName}");
            }
            else
            {
                // Non-generic param — use the same type as P/Invoke for simplicity
                var category = MethodClosureBridge.ClassifyParam(arg, env.TypeDatabase);
                switch (category)
                {
                    case MethodClosureBridge.ParamAbiCategory.Primitive:
                        if (MarshallingHelpers.IsBoolType(arg.SwiftTypeSpec))
                            publicParams.Add($"bool {csName}");
                        else
                            publicParams.Add($"{MethodClosureBridge.GetPInvokePrimitiveType(arg.SwiftTypeSpec)} {csName}");
                        break;
                    case MethodClosureBridge.ParamAbiCategory.ObjCHandle:
                    case MethodClosureBridge.ParamAbiCategory.PayloadHandle:
                        publicParams.Add($"ISwiftObject {csName}");
                        break;
                    case MethodClosureBridge.ParamAbiCategory.Utf8Slice:
                        publicParams.Add($"string {csName}");
                        break;
                    default:
                        publicParams.Add($"IntPtr {csName}");
                        break;
                }
            }
        }

        // Emit XML doc comment
        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl);

        // Detect Utf8Slice (Swift.String) params — they need `unsafe` for the
        // `fixed (byte* …Ptr = …Utf8)` pin that brackets the P/Invoke call below.
        bool hasUtf8SliceParam = false;
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.HasDefaultArg) continue;
            if (arg.SwiftTypeSpec is NamedTypeSpec nm && nm.Name == genericInfo.Param.TypeName) continue;
            if (MethodClosureBridge.ClassifyParam(arg, env.TypeDatabase) == MethodClosureBridge.ParamAbiCategory.Utf8Slice)
            {
                hasUtf8SliceParam = true;
                break;
            }
        }

        string staticStr = isStatic ? "static " : "";
        string unsafeStr = hasUtf8SliceParam ? "unsafe " : "";
        string returnStr = isVoidReturn ? "void" : csReturnType;
        csWriter.WriteLine($"public {unsafeStr}{staticStr}{returnStr} {methodName}({string.Join(", ", publicParams)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Build P/Invoke call arguments
        var callArgs = new List<string>();
        // Track Utf8Slice params so we can emit byte[] prelude declarations once before
        // the fixed-block stack opens (mirrors MethodClosureBridge.cs ~1283-1292).
        var utf8SliceLocals = new List<(string csName, string bareName)>();

        // Result buffer (for indirect returns)
        if (needsResultPtr)
        {
            csWriter.WriteLine("IntPtr resultPtr = Marshal.AllocHGlobal(256);");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            callArgs.Add("resultPtr");
        }
        else if (isStringReturn)
        {
            csWriter.WriteLine("IntPtr resultPtr = Marshal.AllocHGlobal(256);");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            callArgs.Add("resultPtr");
        }

        // Regular parameters
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.HasDefaultArg) continue;

            var csName = NameProvider.GetCSharpParameterName(arg);

            if (arg.SwiftTypeSpec is NamedTypeSpec named && named.Name == genericInfo.Param.TypeName)
            {
                // For ISwiftObject, get the handle. Protocol proxies have _swiftContainer,
                // classes have Payload. Use the Payload SafeHandle for class-like objects.
                callArgs.Add($"{csName}.SwiftHandle");
            }
            else
            {
                var category = MethodClosureBridge.ClassifyParam(arg, env.TypeDatabase);
                switch (category)
                {
                    case MethodClosureBridge.ParamAbiCategory.Primitive:
                        callArgs.Add(csName);
                        break;
                    case MethodClosureBridge.ParamAbiCategory.ObjCHandle:
                        callArgs.Add($"{csName}.SwiftHandle");
                        break;
                    case MethodClosureBridge.ParamAbiCategory.PayloadHandle:
                        callArgs.Add($"{csName}.SwiftHandle");
                        break;
                    case MethodClosureBridge.ParamAbiCategory.Utf8Slice:
                    {
                        // Track the local; the byte[] prelude + fixed pin are emitted around
                        // the P/Invoke call below.
                        var bareName = NameProvider.StripVerbatimPrefix(csName);
                        utf8SliceLocals.Add((csName, bareName));
                        callArgs.Add($"(IntPtr)__{bareName}Ptr");
                        callArgs.Add($"(nint)__{bareName}Utf8.Length");
                        break;
                    }
                    default:
                        callArgs.Add(csName);
                        break;
                }
            }
        }

        // Self parameter
        if (!isStatic)
        {
            var selfExpr = isClass
                ? (parentDecl is ClassDecl cd && cd.IsObjCRooted ? "Handle" : "_handle.DangerousGetHandle()")
                : "_payload.DangerousGetHandle()";
            callArgs.Add(selfExpr);
        }

        // Utf8Slice prelude: allocate UTF-8 bytes for each Swift.String param BEFORE the
        // fixed-block stack opens so the `fixed (... = __{bareName}Utf8)` source binding
        // resolves. Mirrors MethodClosureBridge.cs ~1283-1292 verbatim.
        foreach (var (csName, bareName) in utf8SliceLocals)
            csWriter.WriteLine($"var __{bareName}Utf8 = System.Text.Encoding.UTF8.GetBytes({csName});");

        // Open fixed blocks pinning each UTF-8 byte[] so the P/Invoke + Swift @_silgen_name
        // wrapper sees a stable byte pointer for the entire call duration.
        foreach (var (_, bareName) in utf8SliceLocals)
        {
            csWriter.WriteLine($"fixed (byte* __{bareName}Ptr = __{bareName}Utf8)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        // Emit P/Invoke call
        string callExpr = $"{pInvokeName}_XM({string.Join(", ", callArgs)})";
        // Check if the return is a class pointer (Unmanaged.passRetained on Swift side)
        var returnTypeSpec = methodDecl.CSSignature.First().SwiftTypeSpec;
        var returnMapping = CdeclReturnMapping.Classify(returnTypeSpec, env.TypeDatabase);
        bool isClassPointerReturn = returnMapping.mapping.Kind is CdeclReturnKind.ClassPointer
            or CdeclReturnKind.OptionalClassPointer;
        if (isVoidReturn)
        {
            csWriter.WriteLine($"{callExpr};");
        }
        else if (isStringReturn)
        {
            csWriter.WriteLine($"{callExpr};");
            csWriter.WriteLine("return SwiftMarshal.ReadUtf8Slice(resultPtr);");
        }
        else if (needsResultPtr)
        {
            csWriter.WriteLine($"{callExpr};");
            csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{csReturnType}>(resultPtr);");
        }
        else if (isClassPointerReturn)
        {
            // Class pointer: Swift returns Unmanaged.passRetained().toOpaque(), wrap into C# object
            csWriter.WriteLine($"return new {csReturnType}(new Swift.Runtime.SwiftHandle({callExpr}));");
        }
        else
        {
            csWriter.WriteLine($"return {callExpr};");
        }

        // Close fixed blocks (in reverse order — innermost first)
        for (int i = 0; i < utf8SliceLocals.Count; i++)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        // Close try blocks for indirect returns
        if (needsResultPtr || isStringReturn)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally { Marshal.FreeHGlobal(resultPtr); }");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static string GetSwiftArgLabel(ArgumentDecl arg)
        => ClosureEmitter.GetSwiftArgLabelForCdecl(arg);
}
