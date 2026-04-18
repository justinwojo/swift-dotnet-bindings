// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Bridge for methods taking <c>[any Protocol.Type]</c> parameters (arrays of existential
/// protocol metatypes). Emits a Swift <c>@_cdecl</c> wrapper that accepts a C array of
/// metatype pointers + count per such parameter, reconstructs the Swift array via
/// <c>unsafeBitCast</c>, then calls the original Swift method.
///
/// Scope (MVP): free functions only, synchronous, non-throwing, non-generic. Return type
/// must be a plain <c>@_cdecl</c>-compatible scalar, <c>Swift.String</c>, or <c>Void</c>.
/// Instance methods, constructors, and complex return types are deferred.
///
/// User-facing C# API accepts raw <c>IntPtr + nint</c> for each metatype array parameter.
/// Callers pin a Swift metatype pointer array (obtained via
/// <c>TypeMetadata.GetTypeMetadataOrThrow&lt;T&gt;().Handle</c> per known conformer) and
/// pass the pinned pointer + length. A friendlier overload accepting
/// <c>IReadOnlyList&lt;TypeMetadata&gt;</c> is out of scope for the MVP.
/// </summary>
public static class MetatypeArrayBridgeEmitter
{
    /// <summary>
    /// Returns true when the method is eligible for metatype array bridging.
    /// Must be synchronous, non-throwing, non-generic, non-accessor, non-constructor,
    /// not mutating, a free function (ParentDecl is ModuleDecl or null), and have at
    /// least one parameter that is Array&lt;any P.Type&gt; with known hint conformers
    /// allowed for the method's owning module. Module context comes from
    /// <c>methodDecl.ModuleDecl?.Name</c> so scoped hints (e.g. MusicKit-only) fail
    /// closed when the method lives in an unrelated module.
    /// </summary>
    public static bool IsEligible(MethodDecl methodDecl, ILogger? logger = null)
    {
        if (methodDecl.IsAccessor || methodDecl.IsConstructor || methodDecl.IsAsync ||
            methodDecl.Throws || methodDecl.IsGeneric || methodDecl.IsMutating)
            return false;

        // Free functions only for the MVP: ParentDecl is ModuleDecl or null
        if (methodDecl.ParentDecl is TypeDecl)
            return false;

        if (methodDecl.IsModuleInternal)
            return false;

        var moduleFilter = methodDecl.ModuleDecl?.Name;

        // At least one CSSignature arg (skip return at index 0) must be a metatype array
        bool hasMetatypeArray = false;
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            if (BoundGenericsHandler.IsArrayOfExistentialMetatypes(
                methodDecl.CSSignature[i].SwiftTypeSpec, moduleFilter, out _))
            {
                hasMetatypeArray = true;
                break;
            }
        }
        return hasMetatypeArray;
    }

    /// <summary>
    /// Attempts to emit a metatype-array-bridged version of the method.
    /// Returns true on success, false if the method isn't eligible or can't be normalized.
    /// </summary>
    public static bool TryEmit(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ILogger logger,
        ModuleEmissionContext? emissionContext = null)
    {
        var methodDecl = env.MethodDecl;
        if (!IsEligible(methodDecl, logger))
            return false;

        // Build the normalized MethodDecl: each [any P.Type] param becomes
        // two scalar args (UnsafeRawPointer + Int). Pass the method's owning module
        // so scoped hint conformers are only matched within their allow-list.
        var moduleFilter = methodDecl.ModuleDecl?.Name;
        var normalized = NormalizeMethodDecl(methodDecl, moduleFilter);

        var normalizedEnv = new MethodEnvironment(
            normalized,
            env.TypeDatabase,
            env.SiblingPropertyNames,
            env.PInvokeHelperContext,
            env.CompositionCollector);

        // Build cdecl symbol name and set wrapper flags on the cloned decl
        var moduleName = normalized.ParentDecl is ModuleDecl modDecl ? modDecl.Name : "";
        var cdeclSymbol = MethodWrapperEmitter.GetMethodSymbolName(
            moduleName, "Free", normalized.Name, normalized.MangledName);
        normalized.UsesCdeclMethodWrapper = true;
        normalized.UsesFreeFunctionWrapper = true;
        normalized.MangledName = cdeclSymbol;

        // Make sure the normalized signature can be fully marshalled by the pipeline
        var signatureHandler = new SignatureHandler(normalizedEnv);
        if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
        {
            logger.LogDebug(
                "MetatypeArrayBridge: skipping {Name} — normalized signature still contains placeholder",
                methodDecl.Name);
            return false;
        }

        EmitSwiftWrapper(swiftWriter, methodDecl, normalized, cdeclSymbol, moduleFilter);

        TypeDatabaseExtensions.AnyTypeFallbackInfo? fallbackInfo = null;
        foreach (var argument in normalized.CSSignature)
        {
            if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(
                    env.TypeDatabase, env.ClosureHandler, argument.SwiftTypeSpec, out var found))
            {
                fallbackInfo = found;
                break;
            }
        }

        var wrapperEmitter = new WrapperEmitter(normalizedEnv, signatureHandler, fallbackInfo, emissionContext);
        wrapperEmitter.EmitMethod(csWriter, swiftWriter);
        PInvokeEmitter.EmitPInvoke(csWriter, normalizedEnv, signatureHandler);
        return true;
    }

    /// <summary>
    /// Creates a new MethodDecl where each [any P.Type] array parameter is replaced
    /// by two scalar parameters: {name}Ptr: Swift.UnsafeRawPointer and {name}Count: Swift.Int.
    /// <paramref name="moduleFilter"/> scopes the metatype-array detection so hint conformers
    /// restricted to other modules don't trigger normalization here.
    /// </summary>
    internal static MethodDecl NormalizeMethodDecl(MethodDecl original, string? moduleFilter = null)
    {
        // Record `with` clones every field, preserving metadata like IsSpiProtected,
        // IsMainActorIsolated/IsNonisolated, availability annotations, etc. Manual
        // field-by-field copying drops those silently and makes wrapper emission
        // diverge from the original method's visibility/availability intent.
        var normalized = original with
        {
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(original.GenericParameters),
            UsesWrapperLibrary = true,
        };

        // Return type is at index 0 — passed through as-is
        normalized.CSSignature.Add(CloneArg(original.CSSignature[0], normalized));

        for (int i = 1; i < original.CSSignature.Count; i++)
        {
            var arg = original.CSSignature[i];
            if (BoundGenericsHandler.IsArrayOfExistentialMetatypes(arg.SwiftTypeSpec, moduleFilter, out _))
            {
                var baseName = !string.IsNullOrEmpty(arg.Name) ? arg.Name : $"arg{i}";
                var privateBase = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : baseName;

                normalized.CSSignature.Add(new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.UnsafeRawPointer"),
                    Name = baseName + "Ptr",
                    PrivateName = privateBase + "Ptr",
                    IsInOut = false,
                    IsGeneric = false,
                    HasDefaultArg = false,
                    ParentDecl = normalized,
                    ModuleDecl = arg.ModuleDecl,
                });
                normalized.CSSignature.Add(new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = baseName + "Count",
                    PrivateName = privateBase + "Count",
                    IsInOut = false,
                    IsGeneric = false,
                    HasDefaultArg = false,
                    ParentDecl = normalized,
                    ModuleDecl = arg.ModuleDecl,
                });
            }
            else
            {
                normalized.CSSignature.Add(CloneArg(arg, normalized));
            }
        }

        return normalized;
    }

    private static ArgumentDecl CloneArg(ArgumentDecl arg, MethodDecl parent) => new()
    {
        SwiftTypeSpec = arg.SwiftTypeSpec,
        Name = arg.Name,
        PrivateName = arg.PrivateName,
        IsInOut = arg.IsInOut,
        IsGeneric = arg.IsGeneric,
        HasDefaultArg = arg.HasDefaultArg,
        ParentDecl = parent,
        ModuleDecl = arg.ModuleDecl,
    };

    /// <summary>
    /// Emits the Swift <c>@_cdecl</c> wrapper that reconstructs each <c>[any P.Type]</c>
    /// array from a C pointer + count pair, then calls the original Swift function.
    /// <paramref name="moduleFilter"/> scopes the hint-registry lookup so scoped conformers
    /// are only emitted for methods in their allow-listed modules.
    /// </summary>
    private static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodDecl original,
        MethodDecl normalized,
        string wrapperSymbol,
        string? moduleFilter)
    {
        var returnSpec = original.CSSignature[0].SwiftTypeSpec;
        bool isVoid = returnSpec is TupleTypeSpec tup && tup == TupleTypeSpec.Empty;
        bool isStringReturn = !isVoid && returnSpec.ToString() == "Swift.String";
        var returnSwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnSpec);

        var swiftParams = new List<string>();
        var reconstruction = new List<string>();
        var callArgs = new List<string>();

        if (isStringReturn)
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");

        for (int i = 1; i < original.CSSignature.Count; i++)
        {
            var origArg = original.CSSignature[i];
            if (BoundGenericsHandler.IsArrayOfExistentialMetatypes(origArg.SwiftTypeSpec, moduleFilter, out var protocolName))
            {
                var ptrLabel = (!string.IsNullOrEmpty(origArg.PrivateName) ? origArg.PrivateName : origArg.Name) + "Ptr";
                var countLabel = (!string.IsNullOrEmpty(origArg.PrivateName) ? origArg.PrivateName : origArg.Name) + "Count";
                var localVar = (!string.IsNullOrEmpty(origArg.PrivateName) ? origArg.PrivateName : origArg.Name) + "Arr";

                swiftParams.Add($"_ {ptrLabel}: UnsafeRawPointer");
                swiftParams.Add($"_ {countLabel}: Int");

                // `any P.Type` is a 2-word existential (metatype + protocol witness table).
                // The C# caller provides a 1-word raw metatype pointer per element.
                // Match the raw pointer against each hint-registered conformer's metatype
                // and assign the concrete metatype — Swift resolves the witness table statically.
                var anyTypeSpec = $"(any {protocolName!}.Type)";
                var conformers = ConcreteSpecializationEngine
                    .GetHintConformers(protocolName!, moduleFilter)
                    .Where(c => !string.IsNullOrEmpty(c.SwiftQualifiedName))
                    .ToList();

                reconstruction.Add($"var {localVar}: [{anyTypeSpec}] = []");
                reconstruction.Add($"{localVar}.reserveCapacity({countLabel})");
                for (int ci = 0; ci < conformers.Count; ci++)
                {
                    var qName = conformers[ci].SwiftQualifiedName!;
                    reconstruction.Add($"let _ptr{ci} = unsafeBitCast({qName}.self as Any.Type, to: UnsafeRawPointer.self)");
                }
                reconstruction.Add($"for _i in 0..<{countLabel} {{");
                reconstruction.Add($"    let _p = {ptrLabel}.load(fromByteOffset: _i * MemoryLayout<UnsafeRawPointer>.stride, as: UnsafeRawPointer.self)");
                for (int ci = 0; ci < conformers.Count; ci++)
                {
                    var qName = conformers[ci].SwiftQualifiedName!;
                    var keyword = ci == 0 ? "if" : "else if";
                    reconstruction.Add($"    {keyword} _p == _ptr{ci} {{");
                    reconstruction.Add($"        {localVar}.append({qName}.self)");
                    reconstruction.Add("    }");
                }
                reconstruction.Add("    else {");
                reconstruction.Add($"        fatalError(\"MetatypeArrayBridge: unknown conformer of {protocolName}\")");
                reconstruction.Add("    }");
                reconstruction.Add("}");

                var origLabel = BuildArgLabel(origArg);
                callArgs.Add($"{origLabel}{localVar}");
            }
            else
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(origArg.SwiftTypeSpec);
                var label = !string.IsNullOrEmpty(origArg.PrivateName) ? origArg.PrivateName : origArg.Name;
                if (string.IsNullOrEmpty(label))
                    label = $"arg{i}";
                swiftParams.Add($"_ {label}: {swiftType}");

                var origLabel = BuildArgLabel(origArg);
                callArgs.Add($"{origLabel}{label}");
            }
        }

        var paramString = string.Join(", ", swiftParams);
        var callArgString = string.Join(", ", callArgs);

        var moduleName = ArraySliceNormalizationEmitter.UnescapeModuleName(original.ModuleDecl?.Name ?? "");
        var originalMethodName = NameProvider.ParserNameToSwift(original);
        var callExpr = !string.IsNullOrEmpty(moduleName)
            ? $"{moduleName}.{originalMethodName}({callArgString})"
            : $"{originalMethodName}({callArgString})";

        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"@_cdecl(\"{wrapperSymbol}\")");
        var returnClause = (isVoid || isStringReturn) ? "" : $" -> {returnSwiftType}";
        var swiftFuncName = $"_sbw_{originalMethodName}_{ArraySliceNormalizationEmitter.DeterministicHash8(original.MangledName)}";
        swiftWriter.WriteLine($"public func {swiftFuncName}({paramString}){returnClause} {{");
        swiftWriter.Indent++;
        foreach (var line in reconstruction)
            swiftWriter.WriteLine(line);
        if (isStringReturn)
        {
            StringReturnEmitter.EmitReturnBody(swiftWriter, callExpr);
        }
        else
        {
            swiftWriter.WriteLine(isVoid ? callExpr : $"return {callExpr}");
        }
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Builds the Swift call-site label prefix for an argument, matching the convention
    /// used by ArraySliceNormalizationEmitter and ExistentialBypassEmitter.
    /// </summary>
    private static string BuildArgLabel(ArgumentDecl arg) => arg.Name switch
    {
        var n when SwiftBuilder.IsAutoGeneratedArgName(n) => "",
        var n when !string.IsNullOrEmpty(n) && n.StartsWith("_") => $"{n.Substring(1)}: ",
        var n when string.IsNullOrEmpty(n) => "",
        var n => $"{n}: ",
    };
}
