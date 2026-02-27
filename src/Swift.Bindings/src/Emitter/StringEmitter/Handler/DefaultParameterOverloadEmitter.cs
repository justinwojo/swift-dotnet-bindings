// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits C# overloads for methods with trailing default parameters.
/// For each overload, generates a Swift wrapper that calls the original method
/// with fewer arguments (letting Swift supply the defaults) and a corresponding
/// C# method + P/Invoke pointing to the wrapper.
/// </summary>
public static class DefaultParameterOverloadEmitter
{
    /// <summary>
    /// Maximum number of overloads to generate per method.
    /// Limits combinatorial explosion for methods with many defaults.
    /// </summary>
    private const int MaxOverloads = 4;

    /// <summary>
    /// Emits overloads for a method with trailing default parameters.
    /// Called after the primary method has already been emitted.
    /// </summary>
    public static void TryEmitOverloads(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ILogger logger,
        ModuleEmissionContext? emissionContext = null)
    {
        var methodDecl = env.MethodDecl;

        // Skip property accessors
        if (methodDecl.IsAccessor)
            return;

        // Skip module-internal methods
        if (methodDecl.IsModuleInternal)
            return;

        // Skip methods on internal parent types
        if (methodDecl.ParentDecl is TypeDecl parentTypeDecl &&
            (parentTypeDecl.IsModuleInternal ||
             !env.TypeDatabase.TryGetTypeRecord(parentTypeDecl.SwiftTypeName, out _)))
            return;

        // Skip methods on generic parent types — Swift extension syntax can't express
        // the generic parameters (e.g., `extension Keyframe` instead of `extension Keyframe<T>`),
        // and generic type params (τ_0_0) in parameter types aren't valid Swift identifiers.
        if (methodDecl.ParentDecl is TypeDecl parentType && parentType.IsGeneric)
            return;

        var trailingDefaultCount = CountTrailingDefaults(methodDecl);
        if (trailingDefaultCount == 0)
            return;

        // Limit overloads
        var overloadCount = Math.Min(trailingDefaultCount, MaxOverloads);

        // Generate overloads from most-trimmed to least-trimmed
        // (fewest params first in source output)
        for (int trim = overloadCount; trim >= 1; trim--)
        {
            var overloadDecl = BuildOverloadDecl(methodDecl, trim);

            // Note: HasClosureCdeclWrapper is NOT set on cloned overload decls.
            // DefaultParam wrappers use @_silgen_name to intercept the original Swift symbol,
            // which forces the function type to match the original ABI. Closure params must
            // remain native Swift types in the wrapper. Only standalone closure wrappers
            // (emitted at MethodHandler level with their own unique symbol) can use Cdecl params.

            // Create environment with overload decl
            var overloadEnv = new MethodEnvironment(
                overloadDecl,
                env.TypeDatabase,
                env.SiblingPropertyNames,
                env.PInvokeHelperContext,
                env.CompositionCollector);

            // Check if the overload signature is fully marshallable
            var signatureHandler = new SignatureHandler(overloadEnv);
            if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
            {
                logger.LogDebug("DefaultParameterOverload: skipping overload (trim {Trim}) for {Name} — signature contains placeholder", trim, methodDecl.Name);
                continue;
            }

            // Check for collision with existing methods/ctors that have same name and param count
            if (HasSignatureCollision(overloadDecl))
            {
                logger.LogDebug("DefaultParameterOverload: skipping overload (trim {Trim}) for {Name} — collides with existing method", trim, methodDecl.Name);
                continue;
            }

            // C6/C7: Check projected C# signature against already-emitted methods from the main pass
            // Different Swift overloads can produce identical C# signatures after normalization
            if (env.EmittedProjectedSignatures != null)
            {
                var projectedKey = GetProjectedOverloadKey(overloadDecl, env.TypeDatabase);
                if (!env.EmittedProjectedSignatures.Add(projectedKey))
                {
                    logger.LogDebug("DefaultParameterOverload: skipping overload (trim {Trim}) for {Name} — projected signature collides: {Key}", trim, methodDecl.Name, projectedKey);
                    continue;
                }
            }

            // Emit Swift wrapper
            EmitSwiftWrapper(swiftWriter, methodDecl, overloadDecl, env);

            // Delegate C# emission to normal pipeline
            TypeDatabaseExtensions.AnyTypeFallbackInfo? fallbackInfo = null;
            foreach (var argument in overloadDecl.CSSignature)
            {
                if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(env.TypeDatabase, env.ClosureHandler, argument.SwiftTypeSpec, out var foundFallbackInfo))
                {
                    fallbackInfo = foundFallbackInfo;
                    break;
                }
            }

            var wrapperEmitter = new WrapperEmitter(overloadEnv, signatureHandler, fallbackInfo, emissionContext);
            if (overloadDecl.IsConstructor && !overloadDecl.IsFailable && !overloadDecl.IsAsync)
            {
                wrapperEmitter.EmitConstructor(csWriter);
            }
            else if (overloadDecl.IsConstructor && overloadDecl.IsFailable)
            {
                wrapperEmitter.EmitFailableFactory(csWriter);
            }
            else
            {
                wrapperEmitter.EmitMethod(csWriter, swiftWriter);
            }
            PInvokeEmitter.EmitPInvoke(csWriter, overloadEnv, signatureHandler);
        }
    }

    /// <summary>
    /// Counts the number of consecutive trailing parameters with HasDefaultArg.
    /// </summary>
    internal static int CountTrailingDefaults(MethodDecl methodDecl)
    {
        var args = methodDecl.CSSignature.Skip(1).ToList(); // skip return type
        if (args.Count == 0) return 0;

        int count = 0;
        for (int i = args.Count - 1; i >= 0; i--)
        {
            if (IsDebugParameter(args[i]))
                continue; // debug params are always omitted; don't count them
            if (args[i].HasDefaultArg)
                count++;
            else
                break;
        }
        return count;
    }

    /// <summary>
    /// Creates a new MethodDecl with the last 'trimCount' parameters removed.
    /// The MangledName points to the Swift wrapper symbol.
    /// </summary>
    internal static MethodDecl BuildOverloadDecl(MethodDecl original, int trimCount)
    {
        var wrapperSymbol = BuildWrapperSymbol(original, trimCount);

        var overload = new MethodDecl
        {
            Name = original.Name,
            MangledName = wrapperSymbol,
            MethodType = original.MethodType,
            IsConstructor = original.IsConstructor,
            IsFailable = original.IsFailable,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(original.GenericParameters),
            ParentDecl = original.ParentDecl,
            ModuleDecl = original.ModuleDecl,
            Throws = original.Throws,
            IsAsync = original.IsAsync,
            Visibility = original.Visibility,
            IsAccessor = original.IsAccessor,
            IsMutating = original.IsMutating,
            UsesWrapperLibrary = true,
        };

        // Copy return type
        var returnArg = original.CSSignature[0];
        overload.CSSignature.Add(new ArgumentDecl
        {
            SwiftTypeSpec = returnArg.SwiftTypeSpec,
            Name = returnArg.Name,
            PrivateName = returnArg.PrivateName,
            IsInOut = returnArg.IsInOut,
            IsGeneric = returnArg.IsGeneric,
            HasDefaultArg = returnArg.HasDefaultArg,
            ParentDecl = overload,
            ModuleDecl = returnArg.ModuleDecl
        });

        // Copy non-trimmed parameters
        var args = original.CSSignature.Skip(1).ToList();
        var keepCount = args.Count - trimCount;
        for (int i = 0; i < keepCount; i++)
        {
            var arg = args[i];
            overload.CSSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = arg.SwiftTypeSpec,
                Name = arg.Name,
                PrivateName = arg.PrivateName,
                IsInOut = arg.IsInOut,
                IsGeneric = arg.IsGeneric,
                HasDefaultArg = arg.HasDefaultArg,
                ParentDecl = overload,
                ModuleDecl = arg.ModuleDecl
            });
        }

        return overload;
    }

    /// <summary>
    /// Emits a Swift wrapper function that calls the original method
    /// with fewer arguments, letting Swift fill in the defaults.
    /// </summary>
    private static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodDecl originalMethodDecl,
        MethodDecl overloadDecl,
        MethodEnvironment env)
    {
        var wrapperSymbol = overloadDecl.MangledName;
        var parentTypeDecl = originalMethodDecl.ParentDecl as TypeDecl;
        bool isFreeFunction = parentTypeDecl == null;

        // Build parameter list for the wrapper function (only kept params)
        var swiftParams = new List<string>();
        var derefLines = new List<string>();
        var keptArgs = overloadDecl.CSSignature.Skip(1).ToList();
        for (int i = 0; i < keptArgs.Count; i++)
        {
            var arg = keptArgs[i];
            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;

            // Large Optional params: accept UnsafeRawPointer, dereference in body
            if (OptionalPointerWrapperEmitter.ShouldWidenParam(arg, env.BoundGenericsHandler))
            {
                swiftParams.Add($"_ {label}: UnsafeRawPointer");
                derefLines.Add(OptionalPointerWrapperEmitter.GetDerefCode(arg, label, label));
            }
            else
            {
                // Render param as native Swift type — @_silgen_name forces original function type
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
                // Wrapper functions always need @escaping on closure parameters because
                // the closure is passed to the original method which may require it.
                // Also add @Sendable for async closures (required in Swift 5.5+ concurrency).
                if (arg.SwiftTypeSpec is ClosureTypeSpec closureSpec)
                {
                    if (!swiftType.StartsWith("@escaping"))
                    {
                        if (closureSpec.IsAsync && !swiftType.Contains("@Sendable"))
                            swiftType = $"@escaping @Sendable {swiftType}";
                        else
                            swiftType = $"@escaping {swiftType}";
                    }
                    else if (closureSpec.IsAsync && !swiftType.Contains("@Sendable"))
                    {
                        swiftType = swiftType.Replace("@escaping ", "@escaping @Sendable ");
                    }
                }
                swiftParams.Add($"_ {label}: {swiftType}");
            }
        }

        // Check if the return type is a large Optional that needs an out-buffer
        bool hasLargeOptionalReturn = env.BoundGenericsHandler.IsLargeOptionalReturn(overloadDecl);
        if (hasLargeOptionalReturn)
        {
            swiftParams.Add("_ _resultBuf: UnsafeMutableRawPointer");
        }

        var swiftParamString = string.Join(", ", swiftParams);

        // Build call arguments — use kept params with their original labels
        var callArgs = new List<string>();
        for (int i = 0; i < keptArgs.Count; i++)
        {
            var arg = keptArgs[i];
            var privateName = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;

            // Use dereferenced value for large Optional params
            var valueRef = OptionalPointerWrapperEmitter.ShouldWidenParam(arg, env.BoundGenericsHandler)
                ? $"{privateName}Val" : privateName;

            // Reconstruct Swift argument label from external name
            var argStr = arg.Name switch
            {
                var n when n.StartsWith("arg") => "",
                var n when n.StartsWith("_") => $"{n.Substring(1)}: ",
                var n when string.IsNullOrEmpty(n) => "",
                var n => $"{n}: "
            };

            // Call args use native param names — @_silgen_name preserves original ABI
            callArgs.Add(argStr + valueRef);
        }
        var callArgString = string.Join(", ", callArgs);

        // Render return type from original method
        var returnTypeSpec = originalMethodDecl.CSSignature.First().SwiftTypeSpec;
        var returnType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
        bool isVoid = returnTypeSpec is TupleTypeSpec tupleTypeSpec && tupleTypeSpec == TupleTypeSpec.Empty;
        bool throws = originalMethodDecl.Throws;

        var originalMethodName = NameProvider.ParserNameToSwift(originalMethodDecl);
        // For the wrapper function name, use the raw (unescaped) Swift name.
        // `_dbw_init_HASH_N` is a valid identifier — backtick escaping from
        // ParserNameToSwift would produce `_dbw_`init`_HASH_N` (invalid syntax).
        var rawMethodName = originalMethodDecl.GetSwiftName();
        var asyncKeyword = originalMethodDecl.IsAsync ? " async" : "";
        var awaitPrefix = originalMethodDecl.IsAsync ? "await " : "";
        var throwsClause = throws ? " throws" : "";
        var returnClause = (isVoid || hasLargeOptionalReturn) ? "" : $" -> {returnType}";
        var tryPrefix = throws ? "try " : "";
        var trimCount = originalMethodDecl.CSSignature.Count - overloadDecl.CSSignature.Count;
        var swiftFuncName = $"_dbw_{rawMethodName}_{DeterministicHash8(originalMethodDecl.MangledName)}_{trimCount}";

        swiftWriter.WriteLine();

        if (isFreeFunction)
        {
            // Free function — emit standalone @_silgen_name function
            var moduleName = ArraySliceNormalizationEmitter.UnescapeModuleName(originalMethodDecl.ModuleDecl?.Name ?? "");
            var callPrefix = !string.IsNullOrEmpty(moduleName) ? $"{moduleName}." : "";

            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){asyncKeyword}{throwsClause}{returnClause} {{");
            swiftWriter.Indent++;

            foreach (var line in derefLines)
                swiftWriter.WriteLine(line);

            var callExpr = $"{tryPrefix}{awaitPrefix}{callPrefix}{originalMethodName}({callArgString})";
            if (hasLargeOptionalReturn)
            {
                var bufferLines = OptionalPointerWrapperEmitter.GetReturnBufferCode(callExpr, returnType);
                foreach (var bufLine in bufferLines)
                    swiftWriter.WriteLine(bufLine);
            }
            else
            {
                swiftWriter.WriteLine(isVoid ? callExpr : $"return {callExpr}");
            }

            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
        else if (originalMethodDecl.IsConstructor)
        {
            // Constructor — emit as static factory function in extension
            var swiftModuleQualifiedName = parentTypeDecl!.SwiftTypeName.ModuleQualifiedName;

            // Constructor return type is the parent type itself
            var ctorReturnType = swiftModuleQualifiedName;
            var ctorReturnClause = originalMethodDecl.IsFailable
                ? $" -> {ctorReturnType}?"
                : $" -> {ctorReturnType}";

            swiftWriter.WriteLine($"extension {swiftModuleQualifiedName} {{");
            swiftWriter.Indent++;

            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public static func {swiftFuncName}({swiftParamString}){asyncKeyword}{throwsClause}{ctorReturnClause} {{");
            swiftWriter.Indent++;

            foreach (var line in derefLines)
                swiftWriter.WriteLine(line);

            var callExpr = $"{tryPrefix}{awaitPrefix}{swiftModuleQualifiedName}({callArgString})";
            swiftWriter.WriteLine($"return {callExpr}");

            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
        else
        {
            // Type method — emit as extension
            var swiftModuleQualifiedName = parentTypeDecl!.SwiftTypeName.ModuleQualifiedName;
            bool isStatic = originalMethodDecl.MethodType == MethodType.Static;
            var staticKeyword = isStatic ? "static " : "";
            var selfPrefix = isStatic ? "Self" : "self";

            swiftWriter.WriteLine($"extension {swiftModuleQualifiedName} {{");
            swiftWriter.Indent++;

            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public {staticKeyword}func {swiftFuncName}({swiftParamString}){asyncKeyword}{throwsClause}{returnClause} {{");
            swiftWriter.Indent++;

            foreach (var line in derefLines)
                swiftWriter.WriteLine(line);

            var callExpr = $"{tryPrefix}{awaitPrefix}{selfPrefix}.{originalMethodName}({callArgString})";
            if (hasLargeOptionalReturn)
            {
                var bufferLines = OptionalPointerWrapperEmitter.GetReturnBufferCode(callExpr, returnType);
                foreach (var bufLine in bufferLines)
                    swiftWriter.WriteLine(bufLine);
            }
            else
            {
                swiftWriter.WriteLine(isVoid ? callExpr : $"return {callExpr}");
            }

            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
    }

    /// <summary>
    /// Creates a projected C# method key for an overload, matching the format used by
    /// HandleBaseDecl's GetProjectedCSharpMethodKey. (C6/C7)
    /// </summary>
    private static string GetProjectedOverloadKey(MethodDecl overloadDecl, ITypeDatabase typeDatabase)
    {
        var returnTypeSpec = overloadDecl.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        bool hasReturnValue = returnTypeSpec != null && !returnTypeSpec.IsEmptyTuple;
        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(overloadDecl);
        var methodName = overloadDecl.IsConstructor
            ? "ctor"
            : NameProvider.GetPublicMethodName(overloadDecl.Name, overloadDecl.IsAsync, hasReturnValue: hasReturnValue, isSelfReturning: isSelfReturning, parentTypeName: (overloadDecl.ParentDecl as TypeDecl)?.Name,
                parameterCount: overloadDecl.CSSignature.Skip(1).Count(a => !IsDebugParameter(a)));

        var paramTypes = new List<string>();
        for (int i = 1; i < overloadDecl.CSSignature.Count; i++)
        {
            var arg = overloadDecl.CSSignature[i];
            // Debug params (#file, #line, etc.) are stripped from the public signature
            if (IsDebugParameter(arg))
                continue;
            // P1: Unwrap Optional<Closure> to bare Closure, matching the main pass (C11 in IHandler.cs).
            // Nullable reference types don't affect C# overload resolution — Action<T>? and Action<T>
            // are the same signature. Without this, cross-pass dedup misses collisions.
            var typeSpecForKey = arg.SwiftTypeSpec;
            if (typeSpecForKey is NamedTypeSpec optionalClosureSpec &&
                optionalClosureSpec.Name == "Swift.Optional" &&
                optionalClosureSpec.GenericParameters.Count == 1 &&
                optionalClosureSpec.GenericParameters[0] is ClosureTypeSpec)
            {
                typeSpecForKey = optionalClosureSpec.GenericParameters[0];
            }
            var factory = new TypeProjectionFactory();
            var projection = factory.Project(typeSpecForKey, new ProjectionContext
            {
                TypeDatabase = typeDatabase,
                IsParameter = true
            });
            string paramType;
            if (projection != null)
            {
                paramType = projection.PublicType;
            }
            else
            {
                try
                {
                    // Normalize container types whose element projection failed
                    // (e.g., Array<τ_0_0> where τ_0_0 can't be resolved without GenericContext).
                    // Must match IHandler.GetProjectedCSharpMethodKey normalization.
                    // This handles both generic containers AND other unresolved types.
                    paramType = BaseHandler.NormalizeContainerForOverloadKey(typeSpecForKey, typeDatabase);
                }
                catch
                {
                    paramType = typeSpecForKey?.ToString() ?? "unknown";
                }
            }
            // Normalize nullable reference types: mirrors IHandler.GetProjectedCSharpMethodKey.
            paramType = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(paramType, arg.SwiftTypeSpec, typeDatabase);
            paramTypes.Add(paramType);
        }
        // Mirror IHandler.GetProjectedCSharpMethodKey: async methods get CancellationToken at emission time.
        if (overloadDecl.IsAsync)
        {
            paramTypes.Add("System.Threading.CancellationToken");
        }

        return $"{methodName}({string.Join(",", paramTypes)})";
    }

    /// <summary>
    /// Checks if a trimmed overload would collide with an existing method on the same type.
    /// Uses parameter count as a conservative heuristic: if any sibling method has the same
    /// name and parameter count, skip the overload to avoid CS0111 duplicate declarations.
    /// </summary>
    private static bool HasSignatureCollision(MethodDecl overloadDecl)
    {
        int overloadParamCount = overloadDecl.CSSignature.Count - 1; // exclude return type

        IEnumerable<MethodDecl>? siblingMethods = overloadDecl.ParentDecl switch
        {
            TypeDecl typeDecl => typeDecl.Methods,
            _ => overloadDecl.ModuleDecl?.Methods
        };

        if (siblingMethods == null)
            return false;

        return siblingMethods.Any(m =>
            m.Name == overloadDecl.Name &&
            m.IsConstructor == overloadDecl.IsConstructor &&
            m.CSSignature.Count - 1 == overloadParamCount &&
            m.MangledName != overloadDecl.MangledName); // don't compare against self
    }

    /// <summary>
    /// Builds the wrapper symbol name: DBW_{TypeName}_{MethodName}_{Hash8}_{TrimCount}
    /// </summary>
    private static string BuildWrapperSymbol(MethodDecl methodDecl, int trimCount)
    {
        var parentDecl = methodDecl.ParentDecl as TypeDecl;
        var typeName = parentDecl?.Name ?? "Global";
        var hash = DeterministicHash8(methodDecl.MangledName);
        return $"DBW_{typeName}_{methodDecl.Name}_{hash}_{trimCount}";
    }

    /// <summary>
    /// Returns true if the method has any debug parameters (#file, #line, etc.).
    /// </summary>
    internal static bool HasDebugParameters(MethodDecl methodDecl)
        => methodDecl.CSSignature.Skip(1).Any(IsDebugParameter);

    /// <summary>
    /// Emits a Swift @_silgen_name wrapper that strips debug params from a method,
    /// letting Swift supply the defaults. Updates the MethodDecl's MangledName to
    /// point to the wrapper symbol so the P/Invoke targets it instead of the original.
    /// </summary>
    internal static void EmitDebugParamWrapper(SwiftWriter swiftWriter, MethodEnvironment env)
    {
        var methodDecl = env.MethodDecl;
        var parentTypeDecl = methodDecl.ParentDecl as TypeDecl;
        bool isFreeFunction = parentTypeDecl == null;

        // Build wrapper symbol
        var hash = DeterministicHash8(methodDecl.MangledName);
        var typeName = parentTypeDecl?.Name ?? "Global";
        var wrapperSymbol = $"DBG_{typeName}_{methodDecl.Name}_{hash}";

        // Gather kept (non-debug) params
        var keptArgs = methodDecl.CSSignature.Skip(1).Where(a => !IsDebugParameter(a)).ToList();

        // Build Swift parameter list for the wrapper
        var swiftParams = new List<string>();
        var derefLines = new List<string>();
        foreach (var arg in keptArgs)
        {
            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            if (OptionalPointerWrapperEmitter.ShouldWidenParam(arg, env.BoundGenericsHandler))
            {
                swiftParams.Add($"_ {label}: UnsafeRawPointer");
                derefLines.Add(OptionalPointerWrapperEmitter.GetDerefCode(arg, label, label));
            }
            else
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
                if (arg.SwiftTypeSpec is ClosureTypeSpec closureSpec)
                {
                    if (!swiftType.StartsWith("@escaping"))
                    {
                        if (closureSpec.IsAsync && !swiftType.Contains("@Sendable"))
                            swiftType = $"@escaping @Sendable {swiftType}";
                        else
                            swiftType = $"@escaping {swiftType}";
                    }
                    else if (closureSpec.IsAsync && !swiftType.Contains("@Sendable"))
                    {
                        swiftType = swiftType.Replace("@escaping ", "@escaping @Sendable ");
                    }
                }
                swiftParams.Add($"_ {label}: {swiftType}");
            }
        }

        // Large optional return buffer
        bool hasLargeOptionalReturn = env.BoundGenericsHandler.IsLargeOptionalReturn(methodDecl);
        if (hasLargeOptionalReturn)
            swiftParams.Add("_ _resultBuf: UnsafeMutableRawPointer");

        var swiftParamString = string.Join(", ", swiftParams);

        // Build call arguments
        var callArgs = new List<string>();
        foreach (var arg in keptArgs)
        {
            var privateName = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var valueRef = OptionalPointerWrapperEmitter.ShouldWidenParam(arg, env.BoundGenericsHandler)
                ? $"{privateName}Val" : privateName;
            var argStr = arg.Name switch
            {
                var n when n.StartsWith("arg") => "",
                var n when n.StartsWith("_") => $"{n.Substring(1)}: ",
                var n when string.IsNullOrEmpty(n) => "",
                var n => $"{n}: "
            };
            callArgs.Add(argStr + valueRef);
        }
        var callArgString = string.Join(", ", callArgs);

        // Return type
        var returnTypeSpec = methodDecl.CSSignature.First().SwiftTypeSpec;
        var returnType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
        bool isVoid = returnTypeSpec is TupleTypeSpec tupleTypeSpec && tupleTypeSpec == TupleTypeSpec.Empty;
        bool throws = methodDecl.Throws;
        var asyncKeyword = methodDecl.IsAsync ? " async" : "";
        var awaitPrefix = methodDecl.IsAsync ? "await " : "";
        var throwsClause = throws ? " throws" : "";
        var returnClause = (isVoid || hasLargeOptionalReturn) ? "" : $" -> {returnType}";
        var tryPrefix = throws ? "try " : "";

        var swiftFuncName = $"_dbg_{methodDecl.Name}_{hash}";

        swiftWriter.WriteLine();

        if (isFreeFunction)
        {
            var moduleName = ArraySliceNormalizationEmitter.UnescapeModuleName(methodDecl.ModuleDecl?.Name ?? "");
            var callPrefix = !string.IsNullOrEmpty(moduleName) ? $"{moduleName}." : "";
            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){asyncKeyword}{throwsClause}{returnClause} {{");
            swiftWriter.Indent++;
            foreach (var line in derefLines) swiftWriter.WriteLine(line);
            var escapedMethodName = NameProvider.ParserNameToSwift(methodDecl);
            var callExpr = $"{tryPrefix}{awaitPrefix}{callPrefix}{escapedMethodName}({callArgString})";
            if (hasLargeOptionalReturn)
                foreach (var bufLine in OptionalPointerWrapperEmitter.GetReturnBufferCode(callExpr, returnType))
                    swiftWriter.WriteLine(bufLine);
            else
                swiftWriter.WriteLine(isVoid ? callExpr : $"return {callExpr}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
        else if (methodDecl.IsConstructor)
        {
            // Constructor — emit as static factory function in extension
            // (mirrors EmitSwiftOverloadWrapper constructor branch)
            var swiftModuleQualifiedName = parentTypeDecl!.SwiftTypeName.ModuleQualifiedName;
            var ctorReturnType = swiftModuleQualifiedName;
            var ctorReturnClause = methodDecl.IsFailable
                ? $" -> {ctorReturnType}?"
                : $" -> {ctorReturnType}";
            swiftWriter.WriteLine($"extension {swiftModuleQualifiedName} {{");
            swiftWriter.Indent++;
            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public static func {swiftFuncName}({swiftParamString}){asyncKeyword}{throwsClause}{ctorReturnClause} {{");
            swiftWriter.Indent++;
            foreach (var line in derefLines) swiftWriter.WriteLine(line);
            var callExpr = $"{tryPrefix}{awaitPrefix}{swiftModuleQualifiedName}({callArgString})";
            swiftWriter.WriteLine($"return {callExpr}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
        else
        {
            var swiftModuleQualifiedName = parentTypeDecl!.SwiftTypeName.ModuleQualifiedName;
            bool isStatic = methodDecl.MethodType == MethodType.Static;
            var staticKeyword = isStatic ? "static " : "";
            var selfPrefix = isStatic ? "Self" : "self";
            swiftWriter.WriteLine($"extension {swiftModuleQualifiedName} {{");
            swiftWriter.Indent++;
            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public {staticKeyword}func {swiftFuncName}({swiftParamString}){asyncKeyword}{throwsClause}{returnClause} {{");
            swiftWriter.Indent++;
            foreach (var line in derefLines) swiftWriter.WriteLine(line);
            var callExpr = $"{tryPrefix}{awaitPrefix}{selfPrefix}.{NameProvider.ParserNameToSwift(methodDecl)}({callArgString})";
            if (hasLargeOptionalReturn)
                foreach (var bufLine in OptionalPointerWrapperEmitter.GetReturnBufferCode(callExpr, returnType))
                    swiftWriter.WriteLine(bufLine);
            else
                swiftWriter.WriteLine(isVoid ? callExpr : $"return {callExpr}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }

        // Update the method's mangled name to target the wrapper
        methodDecl.MangledName = wrapperSymbol;
        methodDecl.UsesWrapperLibrary = true;

        // Remove debug params from CSSignature so downstream iterators
        // (marshalling, SafeHandle, closure callbacks) never see them.
        methodDecl.CSSignature = methodDecl.CSSignature
            .Where((a, i) => i == 0 || !IsDebugParameter(a))
            .ToList();
    }

    internal static string DeterministicHash8(string input) => EmitterUtility.DeterministicHash8(input);

    /// <summary>
    /// Detects Swift compiler-injected debug parameters (#file, #line, #column, #function).
    /// These always have HasDefaultArg=true and use specific type+name combinations:
    ///   - file/_file with StaticString (NOT String — real file params use String)
    ///   - line/_line with UInt (NOT Int — #line produces UInt)
    ///   - column/_column with UInt
    ///   - function/_function with StaticString
    /// </summary>
    internal static bool IsDebugParameter(ArgumentDecl arg)
    {
        if (!arg.HasDefaultArg)
            return false;

        var name = arg.Name;
        // Strip leading underscore for matching (Swift convention for hiding labels)
        var baseName = name.StartsWith("_") ? name.Substring(1) : name;

        if (arg.SwiftTypeSpec is not NamedTypeSpec namedType)
            return false;

        var typeName = namedType.Name;

        return baseName switch
        {
            "file" or "filePath" => typeName == "Swift.StaticString",
            "line" or "column" => typeName == "Swift.UInt",
            "function" => typeName == "Swift.StaticString",
            _ => false
        };
    }
}
