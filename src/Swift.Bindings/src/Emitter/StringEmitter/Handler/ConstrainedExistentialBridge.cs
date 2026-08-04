// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration;

/// <summary>
/// Generates a @_cdecl Swift wrapper + C# constructor + P/Invoke for constructors
/// blocked by constrained existential parameters (e.g., any CameraFrameAnalyzer&lt;CameraFrame, UIEvent&gt;).
///
/// The Swift wrapper takes opaque UnsafeMutableRawPointer for constrained existential params
/// and reconstructs them via Unmanaged&lt;AnyObject&gt;.fromOpaque(...).takeUnretainedValue() as! any Protocol&lt;A, B&gt;.
/// The constructed class is returned via Unmanaged.passRetained(result).toOpaque().
///
/// SAFETY: The Unmanaged&lt;AnyObject&gt;.fromOpaque() cast requires the pointer to be a Swift object
/// reference (class instance). This is correct for class conformers (where SwiftHandle returns the
/// retained object pointer) but NOT for non-frozen struct conformers (where SwiftHandle returns a
/// pointer to heap-allocated struct storage, which is not a Swift object). Passing a struct conformer
/// as a constrained existential parameter will crash at runtime. The C# parameter type is ISwiftObject
/// (broadest common type) because we cannot statically distinguish class vs struct conformers.
///
/// Scope: Class parent constructors only. Non-failable, non-throwing, non-async.
/// </summary>
public static class ConstrainedExistentialBridge
{
    /// <summary>
    /// Minimum OS versions required for parameterized protocol types (any Protocol&lt;T, U&gt;).
    /// Swift 5.7 / Xcode 14 feature — runtime support requires these platform versions.
    /// </summary>
    private static readonly IReadOnlyList<AvailabilityAnnotation> ParameterizedExistentialFloor = new[]
    {
        new AvailabilityAnnotation("iOS", "16.0", null, null, false, false, null, null),
        new AvailabilityAnnotation("macOS", "13.0", null, null, false, false, null, null),
        new AvailabilityAnnotation("macCatalyst", "16.0", null, null, false, false, null, null),
        new AvailabilityAnnotation("tvOS", "16.0", null, null, false, false, null, null),
        new AvailabilityAnnotation("watchOS", "9.0", null, null, false, false, null, null),
        new AvailabilityAnnotation("visionOS", "1.0", null, null, false, false, null, null),
    };
    /// <summary>
    /// Classification of a parameter for the constrained existential bridge.
    /// </summary>
    private enum BridgeParamKind
    {
        /// <summary>
        /// Constrained existential — ISwiftObject param, .SwiftHandle passes pointer to Swift wrapper
        /// which casts via Unmanaged&lt;AnyObject&gt;.fromOpaque() as! any Protocol&lt;A, B&gt;.
        /// Only safe for class conformers (SwiftHandle = retained object pointer).
        /// Struct conformers would pass heap storage pointer, crashing the AnyObject cast.
        /// </summary>
        ConstrainedExistential,
        /// <summary>Swift primitive — passed by value.</summary>
        Primitive,
        /// <summary>Swift class or non-frozen struct — ISwiftObject/.SwiftHandle, Swift unwraps pointer.</summary>
        PayloadHandle,
    }

    private record BridgeParam(
        ArgumentDecl Arg,
        BridgeParamKind Kind,
        string CSharpType,
        string SwiftParamType,
        string SwiftUnmarshalExpr,
        int Index);

    /// <summary>
    /// Attempts to emit a bridged constructor for a class with constrained existential parameters.
    /// Returns true if emission succeeded; false to fall back to normal emission or skip.
    /// </summary>
    public static bool TryEmitConstructor(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ILogger logger,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.CreateImplicitFallback();

        // Gate: class parent only (struct constructors need different return pattern)
        if (env.ParentDecl is not ClassDecl classDecl)
            return false;

        var methodDecl = env.MethodDecl;

        // Gate: non-failable, non-throwing, non-async
        if (methodDecl.IsFailable || methodDecl.Throws || methodDecl.IsAsync)
            return false;

        // Gate: non-generic class — if the class has type parameters (e.g., SomeClass<T, U>),
        // the constructor call can't specify them, causing "generic parameter could not be inferred".
        if (classDecl.IsGeneric)
            return false;

        // Classify all params (first element in CSSignature is the return type)
        var allArgs = methodDecl.CSSignature.Skip(1).ToList();
        bool hasConstrainedExistential = false;
        var bridgeParams = new List<BridgeParam>();
        var neededImports = new HashSet<string>();
        int paramIndex = 0;

        foreach (var arg in allArgs)
        {
            if (ExistentialHandler.IsConstrainedExistential(arg.SwiftTypeSpec, env.TypeDatabase))
            {
                hasConstrainedExistential = true;
                var swiftType = RenderConstrainedExistentialSwiftType(arg.SwiftTypeSpec);
                var idx = paramIndex++;
                bridgeParams.Add(new BridgeParam(
                    arg,
                    BridgeParamKind.ConstrainedExistential,
                    ISwiftObjectInterfaceName,
                    "UnsafeMutableRawPointer",
                    $"Unmanaged<AnyObject>.fromOpaque({GetSwiftParamName(arg, idx)}).takeUnretainedValue() as! {swiftType}",
                    idx));
                // Collect constraint type modules for imports (emitted by EmitSwiftWrapper)
                var protocolSpec = GetConstrainedProtocolSpec(arg.SwiftTypeSpec);
                if (protocolSpec != null)
                {
                    foreach (var gp in protocolSpec.GenericParameters)
                    {
                        if (gp is NamedTypeSpec n)
                        {
                            var module = n.Module;
                            if (!string.IsNullOrEmpty(module))
                                neededImports.Add(module);
                        }
                    }
                    var protoModule = protocolSpec.Module;
                    if (!string.IsNullOrEmpty(protoModule))
                        neededImports.Add(protoModule);
                }
                continue;
            }

            // Classify non-existential params
            var category = MethodClosureBridge.ClassifyParam(arg, env.TypeDatabase);
            switch (category)
            {
                case MethodClosureBridge.ParamAbiCategory.Primitive:
                {
                    var idx = paramIndex++;
                    var primitiveType = MarshallingHelpers.MapSwiftPrimitiveToCSharpType(((NamedTypeSpec)arg.SwiftTypeSpec).Name);
                    bridgeParams.Add(new BridgeParam(arg, BridgeParamKind.Primitive, primitiveType, GetSwiftPrimitiveType(arg), "", idx));
                    break;
                }

                case MethodClosureBridge.ParamAbiCategory.PayloadHandle:
                {
                    var idx = paramIndex++;
                    var named = (NamedTypeSpec)arg.SwiftTypeSpec;
                    var record = GetTypeRecord(named, env.TypeDatabase);
                    if (record == null) return false;
                    var csType = record.CSharpTypeName.FullyQualifiedName;
                    if (record.Kind == TypeRecordKind.Class)
                    {
                        bridgeParams.Add(new BridgeParam(
                            arg, BridgeParamKind.PayloadHandle, csType, "UnsafeMutableRawPointer",
                            $"Unmanaged<{named.Name}>.fromOpaque({GetSwiftParamName(arg, idx)}).takeUnretainedValue()",
                            idx));
                    }
                    else // struct (non-frozen)
                    {
                        bridgeParams.Add(new BridgeParam(
                            arg, BridgeParamKind.PayloadHandle, csType, "UnsafeMutableRawPointer",
                            $"{GetSwiftParamName(arg, idx)}.assumingMemoryBound(to: {named.Name}.self).pointee",
                            idx));
                    }
                    break;
                }

                case MethodClosureBridge.ParamAbiCategory.Utf8Slice:
                    // Explicit exclusion: Swift.String is canonically passable on
                    // MethodClosureBridge.IsAbiCategoryPassable, but the ConstrainedExistentialBridge
                    // body shape (BridgeParam/BridgeParamKind + Swift wrapper synthesis below) does
                    // not yet carry a Utf8Slice kind, so admitting it here would either miss
                    // dispatch or emit an ABI-mismatched IntPtr pair. Reject locally with reasoning
                    // until the bridge body grows a Utf8Slice ptr+len marshalling case
                    // (parallel to the parent-only CSM bridges' Utf8Slice support).
                    if (arg.HasDefaultArg)
                        continue;
                    logger.LogDebug($"ConstrainedExistentialBridge: rejecting Utf8Slice param '{arg.Name}' — body emission not yet implemented");
                    return false;

                default:
                    // ObjC, NativeRemapped, FrozenStruct, Pointer, Unsupported — reject
                    if (arg.HasDefaultArg)
                        continue; // Skip params with defaults that we can't pass — Swift fills the default
                    logger.LogDebug($"ConstrainedExistentialBridge: rejecting param '{arg.Name}' with category {category}");
                    return false;
            }
        }

        if (!hasConstrainedExistential)
            return false;

        // Compute symbols
        var className = classDecl.Name;
        var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(classDecl);
        var mangledHash = EmitterUtility.DeterministicHash8(methodDecl.MangledName);
        var wrapperSymbol = $"SBW_{className}_init_{mangledHash}";
        var swiftTypeName = classDecl.SwiftTypeName.ModuleQualifiedName;

        // Determine library path
        var moduleDecl = methodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(methodDecl.ModuleDecl));
        var moduleLibPath = env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
        var wrapperLibPath = env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;

        // Compute merged availability: parameterized existential floor + member/parent annotations.
        // Keeps the strictest version per platform so the wrapper isn't exposed too early.
        var mergedAvailability = MergeParameterizedExistentialAvailability(
            methodDecl.AvailabilityAnnotations, classDecl);

        // --- Emit Swift wrapper ---
        EmitSwiftWrapper(swiftWriter, wrapperSymbol, swiftTypeName, bridgeParams, methodDecl, neededImports,
            classDecl.IsMainActorIsolated, mergedAvailability);

        // --- Emit C# constructor ---
        EmitCSharpConstructor(csWriter, env, classDecl, typeNameWithGenerics, wrapperSymbol, wrapperLibPath,
            bridgeParams, ctx, mergedAvailability);

        return true;
    }

    private static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        string wrapperSymbol,
        string swiftTypeName,
        List<BridgeParam> bridgeParams,
        MethodDecl methodDecl,
        HashSet<string> neededImports,
        bool isMainActorIsolated = false,
        IReadOnlyList<AvailabilityAnnotation>? availability = null)
    {
        // Build Swift parameter list
        var swiftParams = new List<string>();
        foreach (var bp in bridgeParams)
        {
            swiftParams.Add($"_ {GetSwiftParamName(bp)}: {bp.SwiftParamType}");
        }
        var swiftParamString = string.Join(", ", swiftParams);

        // Build constructor call arguments using original ABI labels
        var callArgs = new List<string>();
        var allArgs = methodDecl.CSSignature.Skip(1).ToList();
        int bridgeIdx = 0;
        foreach (var arg in allArgs)
        {
            // Find this arg in bridgeParams
            var bp = bridgeIdx < bridgeParams.Count && bridgeParams[bridgeIdx].Arg == arg
                ? bridgeParams[bridgeIdx++]
                : null;

            if (bp == null)
            {
                // Param was skipped (has default arg, unsupported category) — Swift fills default
                continue;
            }

            // Compute the expression to pass to the constructor
            var valueExpr = bp.Kind switch
            {
                BridgeParamKind.ConstrainedExistential => bp.SwiftUnmarshalExpr,
                BridgeParamKind.PayloadHandle => bp.SwiftUnmarshalExpr,
                BridgeParamKind.Primitive => GetSwiftParamName(bp),
                _ => GetSwiftParamName(bp),
            };

            // Apply label
            var label = GetSwiftCallLabel(arg);
            callArgs.Add(string.IsNullOrEmpty(label) ? valueExpr : $"{label}: {valueExpr}");
        }
        var callArgString = string.Join(", ", callArgs);

        // Emit import statements for constraint type modules
        foreach (var import in neededImports.OrderBy(i => i))
        {
            swiftWriter.WriteLine($"import {import}");
        }

        swiftWriter.WriteLine();
        // Parameterized protocol types (any Protocol<T, U>) require iOS 16+ / macOS 13+ / etc.
        // The merged availability includes both the parameterized existential floor and any
        // member/parent availability, using the strictest version per platform.
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);
        if (isMainActorIsolated)
            swiftWriter.WriteLine("@MainActor");
        // SBW_ prefix encodes @_cdecl — must match the C# P/Invoke side, which
        // PInvokeEmitHelper.SelectCallingConvention pins to CallConvCdecl for SBW_.
        // Bridge params are Primitive (numeric) or IntPtr/UnsafeRawPointer, all C-representable.
        swiftWriter.WriteLine($"@_cdecl(\"{wrapperSymbol}\")");
        swiftWriter.WriteLine($"public func {wrapperSymbol}({swiftParamString}) -> UnsafeMutableRawPointer {{");
        swiftWriter.Indent++;

        // Emit local variables for unmarshal expressions that are multi-step
        foreach (var bp in bridgeParams)
        {
            if (bp.Kind == BridgeParamKind.ConstrainedExistential || bp.Kind == BridgeParamKind.PayloadHandle)
            {
                var localName = $"__{GetSwiftParamName(bp)}";
                swiftWriter.WriteLine($"let {localName} = {bp.SwiftUnmarshalExpr}");
            }
        }

        // Update callArgs to use local variable names for non-primitive params
        var updatedCallArgs = new List<string>();
        bridgeIdx = 0;
        foreach (var arg in allArgs)
        {
            var bp = bridgeIdx < bridgeParams.Count && bridgeParams[bridgeIdx].Arg == arg
                ? bridgeParams[bridgeIdx++]
                : null;

            if (bp == null) continue;

            var valueExpr = bp.Kind switch
            {
                BridgeParamKind.ConstrainedExistential => $"__{GetSwiftParamName(bp)}",
                BridgeParamKind.PayloadHandle => $"__{GetSwiftParamName(bp)}",
                BridgeParamKind.Primitive => GetSwiftParamName(bp),
                _ => GetSwiftParamName(bp),
            };

            var label = GetSwiftCallLabel(arg);
            updatedCallArgs.Add(string.IsNullOrEmpty(label) ? valueExpr : $"{label}: {valueExpr}");
        }

        swiftWriter.WriteLine($"let result = {swiftTypeName}({string.Join(", ", updatedCallArgs)})");
        // Use `as AnyObject` for safety — handles both true classes and ObjC-bridged structs.
        swiftWriter.WriteLine("return Unmanaged.passRetained(result as AnyObject).toOpaque()");
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static void EmitCSharpConstructor(
        CSharpWriter csWriter,
        MethodEnvironment env,
        ClassDecl classDecl,
        string typeNameWithGenerics,
        string wrapperSymbol,
        string wrapperLibPath,
        List<BridgeParam> bridgeParams,
        ModuleEmissionContext ctx,
        IReadOnlyList<AvailabilityAnnotation>? availability = null)
    {
        var accessModifier = NameProvider.GetAccessModifier(env.MethodDecl.IsSynthesizedAccessor);
        var constructorName = typeNameWithGenerics.Contains('<')
            ? typeNameWithGenerics.Substring(0, typeNameWithGenerics.IndexOf('<'))
            : typeNameWithGenerics;

        // Build C# parameter list
        var csParams = new List<string>();
        foreach (var bp in bridgeParams)
        {
            var paramName = NameProvider.GetCSharpParameterName(bp.Arg);
            csParams.Add($"{bp.CSharpType} {paramName}");
        }
        var paramString = string.Join(", ", csParams);

        // Neither half of this declaration matches the declared signature: a constructor is written
        // under the type's own name, the constrained existential parameters are erased to
        // ISwiftObject, and any parameter the classifier could not pass was dropped (Swift supplies
        // its default). Record what is about to be written so the API manifest names a callable
        // member instead of the un-emitted declared form.
        ctx.RecordEmittedApiShape(
            env.MethodDecl,
            csharpName: constructorName,
            parameterPortion: ModuleEmissionContext.FormatParameterPortion(
                bridgeParams.Select(bp => bp.CSharpType)));

        // Build P/Invoke call arguments
        var pInvokeArgs = new List<string>();
        foreach (var bp in bridgeParams)
        {
            var paramName = NameProvider.GetCSharpParameterName(bp.Arg);
            pInvokeArgs.Add(bp.Kind switch
            {
                BridgeParamKind.ConstrainedExistential => $"(({ISwiftObjectInterfaceName}){paramName}).SwiftHandle",
                BridgeParamKind.PayloadHandle => $"(({ISwiftObjectInterfaceName}){paramName}).SwiftHandle",
                BridgeParamKind.Primitive => paramName,
                _ => paramName,
            });
        }

        // Build P/Invoke parameter string
        var pInvokeParamDecls = new List<string>();
        foreach (var bp in bridgeParams)
        {
            pInvokeParamDecls.Add(bp.Kind switch
            {
                BridgeParamKind.Primitive when MarshallingHelpers.IsBoolType(bp.CSharpType)
                    => $"{MarshallingHelpers.BoolPInvokeParamAttribute} {bp.CSharpType} {GetSwiftParamName(bp)}",
                BridgeParamKind.Primitive => $"{bp.CSharpType} {GetSwiftParamName(bp)}",
                _ => $"IntPtr {GetSwiftParamName(bp)}",
            });
        }
        var pInvokeParamString = string.Join(", ", pInvokeParamDecls);

        // Emit P/Invoke declaration
        if (env.PInvokeHelperContext != null)
        {
            env.PInvokeHelperContext.AddDeclaration(new PInvokeDeclaration
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = wrapperSymbol,
                MethodName = wrapperSymbol,
                ReturnType = "IntPtr",
                ParametersString = pInvokeParamString,
                IsAsync = false,
                MetadataParameters = null
            });
        }
        else
        {
            PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = wrapperSymbol,
                MethodName = wrapperSymbol,
                ReturnType = "IntPtr",
                ParametersString = pInvokeParamString
            });
            csWriter.WriteLine();
        }

        var pInvokeCall = env.PInvokeHelperContext != null
            ? $"{env.PInvokeHelperContext.HelperClassName}.{wrapperSymbol}"
            : wrapperSymbol;

        // Emit [SupportedOSPlatform] attributes so C# consumers get a CA1416 warning
        // when targeting an OS below the parameterized existential (or wrapped API) floor.
        EmitCSharpAvailability(csWriter, availability);

        // Emit constructor
        csWriter.WriteLine($"{accessModifier} unsafe {constructorName}({paramString})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // The parameterized-existential bridge wrapper is availability-gated; on an OS below the
        // existential's floor its body dereferences a weak-linked, null gated symbol (uncatchable
        // SIGSEGV). Throw a catchable exception before the P/Invoke.
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(csWriter, availability, constructorName);

        var callArgString = string.Join(", ", pInvokeArgs);
        csWriter.WriteLine($"var resultPtr = {pInvokeCall}({callArgString});");

        // Class return unmarshal: wrap pointer directly in SwiftClassHandle.
        // No buffer allocation needed — SwiftClassHandle IS the Swift object pointer.
        var handleType = ClassISwiftObjectMethodWriter.GetRootBaseTypeNameWithGenerics(classDecl, env.TypeDatabase);
        csWriter.WriteLine($"_handle = new SwiftClassHandle<{handleType}>(resultPtr);");
        csWriter.WriteLine("Swift.Runtime.SwiftDisposeScope.TryRegister(this);");

        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    /// <summary>
    /// Renders the full Swift type string for a constrained existential, e.g.,
    /// "any Module.Protocol&lt;Module.TypeArg1, Module.TypeArg2&gt;"
    /// Module qualifications are preserved to avoid ambiguity with same-named types in
    /// other imported modules. The import statements ensure
    /// module-qualified names resolve correctly.
    /// </summary>
    internal static string RenderConstrainedExistentialSwiftType(TypeSpec typeSpec)
    {
        var protocolSpec = GetConstrainedProtocolSpec(typeSpec);
        if (protocolSpec == null)
            throw new ArgumentException($"TypeSpec is not a constrained existential: {typeSpec}");
        var genericArgs = protocolSpec.GenericParameters
            .Select(gp => gp is NamedTypeSpec named ? named.Name : gp.ToString())
            .ToList();
        return $"any {protocolSpec.Name}<{string.Join(", ", genericArgs)}>";
    }

    /// <summary>
    /// Extracts the protocol NamedTypeSpec from either a ProtocolListTypeSpec or a NamedTypeSpec
    /// that represents a constrained existential.
    /// </summary>
    private static NamedTypeSpec? GetConstrainedProtocolSpec(TypeSpec? typeSpec)
    {
        if (typeSpec is ProtocolListTypeSpec protocolList && protocolList.Protocols.Count == 1)
            return protocolList.Protocols.Keys[0];
        if (typeSpec is NamedTypeSpec named && named.GenericParameters.Count > 0)
            return named;
        return null;
    }

    private static string GetSwiftParamName(ArgumentDecl arg, int index = 0)
    {
        var name = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
        return string.IsNullOrEmpty(name) ? $"arg{index}" : name;
    }

    private static string GetSwiftParamName(BridgeParam bp) => GetSwiftParamName(bp.Arg, bp.Index);

    private static string GetSwiftCallLabel(ArgumentDecl arg)
    {
        // Follow ExistentialBypassEmitter convention:
        // "argX" prefix → unlabeled
        // other → the Swift label. Provenance-aware: prefer the parser-captured OriginalSwiftName
        // so a label that genuinely begins with '_' (e.g. _self) is not corrupted by the legacy
        // underscore strip.
        return arg.Name switch
        {
            var n when string.IsNullOrEmpty(n) => "",
            var n when SwiftBuilder.IsAutoGeneratedArgName(n) => "",
            var n => arg.OriginalSwiftName ?? (n.StartsWith("_") ? n.Substring(1) : n),
        };
    }

    private static string GetSwiftPrimitiveType(ArgumentDecl arg)
    {
        if (arg.SwiftTypeSpec is NamedTypeSpec named)
            return named.Name; // e.g., "Swift.Int"
        return "Swift.Int";
    }

    private const string ISwiftObjectInterfaceName = "ISwiftObject";

    private static TypeRecord? GetTypeRecord(NamedTypeSpec named, ITypeDatabase typeDatabase)
    {
        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(named.Name);
            if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
                return record;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Merges the parameterized existential floor with member/parent availability annotations.
    /// Keeps the strictest (highest) version per platform so the wrapper isn't exposed too early.
    /// For example, if the parent class requires iOS 17.0 and the existential floor is iOS 16.0,
    /// the result contains iOS 17.0 (not both).
    /// </summary>
    private static IReadOnlyList<AvailabilityAnnotation> MergeParameterizedExistentialAvailability(
        IReadOnlyList<AvailabilityAnnotation>? memberAnnotations,
        ClassDecl classDecl)
    {
        // Start with the parameterized existential floor
        var byPlatform = new Dictionary<string, AvailabilityAnnotation>();
        foreach (var ann in ParameterizedExistentialFloor)
            byPlatform[ann.Platform!] = ann;

        // Merge member/parent annotations, keeping the strictest version per platform
        var apiAnnotations = WrapperEmitterHelpers.MergeAvailability(memberAnnotations, classDecl);
        if (apiAnnotations != null)
        {
            foreach (var ann in apiAnnotations)
            {
                if (ann.Platform == null || ann.IntroducedVersion == null)
                    continue;
                if (byPlatform.TryGetValue(ann.Platform, out var existing))
                {
                    if (CompareVersions(ann.IntroducedVersion, existing.IntroducedVersion!) > 0)
                        byPlatform[ann.Platform] = ann;
                }
                else
                {
                    byPlatform[ann.Platform] = ann;
                }
            }
        }

        return byPlatform.Values.ToList();
    }

    /// <summary>
    /// Compares two dotted version strings (e.g., "16.0" vs "17.4"). Returns positive if a &gt; b.
    /// </summary>
    private static int CompareVersions(string a, string b)
    {
        var aParts = a.Split('.');
        var bParts = b.Split('.');
        for (int i = 0; i < Math.Max(aParts.Length, bParts.Length); i++)
        {
            int av = i < aParts.Length && int.TryParse(aParts[i], out var ai) ? ai : 0;
            int bv = i < bParts.Length && int.TryParse(bParts[i], out var bi) ? bi : 0;
            if (av != bv) return av - bv;
        }
        return 0;
    }

    /// <summary>
    /// Emits C# [SupportedOSPlatform] attributes matching the merged availability. Routed through
    /// the shared <see cref="AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations"/>
    /// so the iOS→macCatalyst floor lift and strictest-per-platform dedup match every other C#
    /// availability site. The merged availability is already one entry per platform, so the dedup
    /// is a no-op here; the lift is what keeps a gated existential's Catalyst surface from
    /// advertising a floor below the one the @_cdecl wrapper is exported at (orphaned symbol).
    /// </summary>
    private static void EmitCSharpAvailability(CSharpWriter csWriter, IReadOnlyList<AvailabilityAnnotation>? annotations)
    {
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(csWriter, annotations);
    }
}
