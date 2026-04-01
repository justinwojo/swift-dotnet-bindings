// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Generates Swift wrapper + C# bindings for members blocked by existential-in-bound-generic
/// when all existential params have HasDefaultArg == true. The Swift wrapper omits the existential
/// params (letting Swift fill in defaults).
/// Supports: struct constructors (returns heap-allocated instance pointer) and
/// class/struct instance methods (void return, non-throwing).
/// </summary>
public static class ExistentialBypassEmitter
{
    /// <summary>
    /// Attempts to emit a bypass wrapper for a constructor that has existential type arguments
    /// in bound generic parameters. Only succeeds if all existential params have HasDefaultArg == true
    /// and the remaining non-existential params are fully marshallable.
    /// </summary>
    /// <returns>true if the bypass was emitted; false to fall back to skip.</returns>
    public static bool TryEmitConstructorBypass(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ILogger logger)
    {
        // Must be a struct constructor
        if (env.ParentDecl is not StructDecl structDecl)
            return false;

        var methodDecl = env.MethodDecl;

        // Failable (init?) and throwing (init throws) constructors produce different
        // Swift return shapes (Optional / throws). The bypass wrapper emits a plain
        // `let result = Type(...)` which is only valid for non-failable, non-throwing inits.
        if (methodDecl.IsFailable || methodDecl.Throws)
            return false;

        // Classify params: first element in CSSignature is the return type
        var allArgs = methodDecl.CSSignature.Skip(1).ToList();
        var existentialArgs = new List<ArgumentDecl>();
        var passthroughArgs = new List<ArgumentDecl>();
        var closureHandler = new ClosureHandler(env.TypeDatabase);

        foreach (var arg in allArgs)
        {
            // Two-tier existential check matching MethodHandler + BuildReducedMethodDecl:
            // 1. Unsupported existentials are always omittable
            // 2. Supported existentials only if NOT in a container with dedicated handling
            //    (Array<any P>, Dict<K, any P>, Optional<any P> go through normal emission)
            if (env.BoundGenericsHandler.IsBoundGeneric(arg))
            {
                if (env.BoundGenericsHandler.TryGetFirstUnsupportedExistentialTypeArgument(arg.SwiftTypeSpec, out _))
                {
                    existentialArgs.Add(arg);
                    continue;
                }
                if (env.BoundGenericsHandler.TryGetFirstExistentialTypeArgument(arg.SwiftTypeSpec, out _) &&
                    !env.BoundGenericsHandler.IsContainerWithSupportedDirectExistential(arg.SwiftTypeSpec))
                {
                    existentialArgs.Add(arg);
                    continue;
                }
            }

            if (closureHandler.IsOptionalClosure(arg.SwiftTypeSpec) && arg.HasDefaultArg)
            {
                var innerClosure = closureHandler.GetClosureTypeSpec(arg);
                if (innerClosure != null && !closureHandler.IsSupportedClosure(innerClosure))
                {
                    existentialArgs.Add(arg); // Omit — Swift fills nil
                }
                else
                {
                    passthroughArgs.Add(arg);
                }
            }
            else
            {
                passthroughArgs.Add(arg);
            }
        }

        // Must have at least one omittable argument (existential or optional closure with default)
        if (existentialArgs.Count == 0)
            return false;

        // All existential args must have HasDefaultArg
        foreach (var arg in existentialArgs)
        {
            if (!arg.HasDefaultArg)
            {
                logger.LogDebug("ExistentialBypassEmitter: param '{Name}' lacks HasDefaultArg, cannot bypass.", arg.Name);
                return false;
            }
        }

        // Reject passthrough args that are generic type parameters (e.g., T).
        // The reduced method has empty GenericParameters, so GenericTypeMapping
        // would not contain entries for these, causing SignatureHandler to crash.
        foreach (var arg in passthroughArgs)
        {
            if (arg.IsGeneric)
            {
                logger.LogDebug("ExistentialBypassEmitter: passthrough param '{Name}' is a generic type parameter, cannot bypass.", arg.Name);
                return false;
            }
        }

        // Build a reduced MethodDecl to check if passthrough args are marshallable.
        // If the full passthrough set fails signature validation, progressively remove
        // passthrough args that have HasDefaultArg until we find a compatible subset.
        var candidatePassthrough = new List<ArgumentDecl>(passthroughArgs);
        Signature? reducedWrapperSig = null;
        Signature? reducedPInvokeSig = null;

        // Use void return type for the probe MethodDecl. The bypass factory emits its own
        // P/Invoke with IntPtr return (heap pointer), so we only validate parameter compatibility.
        // Using the real return type (non-frozen struct) would inject SwiftIndirectResult into the
        // P/Invoke parameter list, falsely failing the sig match.
        var voidReturnArg = new ArgumentDecl
        {
            SwiftTypeSpec = TupleTypeSpec.Empty,
            Name = "",
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = methodDecl.ParentDecl,
            ModuleDecl = methodDecl.ModuleDecl
        };

        while (true)
        {
            var reducedSignature = new List<ArgumentDecl> { voidReturnArg };
            reducedSignature.AddRange(candidatePassthrough);

            var reducedMethodDecl = new MethodDecl
            {
                Name = methodDecl.Name,
                MangledName = methodDecl.MangledName,
                MethodType = MethodType.Static,
                IsConstructor = false, // Treat as static factory for signature building
                CSSignature = reducedSignature,
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = methodDecl.ParentDecl,
                ModuleDecl = methodDecl.ModuleDecl,
                Throws = false,
                IsAsync = false,
                Visibility = methodDecl.Visibility
            };

            var reducedEnv = new MethodEnvironment(reducedMethodDecl, env.TypeDatabase, compositionCollector: env.CompositionCollector);
            var reducedSigHandler = new SignatureHandler(reducedEnv);
            var candidateWrapperSig = reducedSigHandler.GetWrapperSignature();

            if (candidateWrapperSig.ContainsPlaceholder)
            {
                // Try removing a passthrough arg with default, starting from the end
                var removable = candidatePassthrough.FindLastIndex(a => a.HasDefaultArg);
                if (removable >= 0)
                {
                    var removed = candidatePassthrough[removable];
                    existentialArgs.Add(removed); // treat as omitted (Swift fills default)
                    candidatePassthrough.RemoveAt(removable);
                    logger.LogDebug("ExistentialBypassEmitter: removing passthrough param '{Name}' (has default) due to placeholder.", removed.Name);
                    continue;
                }
                logger.LogDebug("ExistentialBypassEmitter: reduced signature contains placeholder, cannot bypass.");
                return false;
            }

            // Build P/Invoke signature after placeholder check, since GetPInvokeSignature
            // may throw for types not in the database (those would show as placeholders above).
            Signature candidatePInvokeSig;
            try
            {
                candidatePInvokeSig = reducedSigHandler.GetPInvokeSignature();
            }
            catch
            {
                var removable = candidatePassthrough.FindLastIndex(a => a.HasDefaultArg);
                if (removable >= 0)
                {
                    var removed = candidatePassthrough[removable];
                    existentialArgs.Add(removed);
                    candidatePassthrough.RemoveAt(removable);
                    logger.LogDebug("ExistentialBypassEmitter: removing passthrough param '{Name}' (has default) due to P/Invoke failure.", removed.Name);
                    continue;
                }
                logger.LogDebug("ExistentialBypassEmitter: reduced P/Invoke signature failed, cannot bypass.");
                return false;
            }

            // Verify each passthrough parameter is compatible between wrapper and P/Invoke.
            // For simple types (int, IntPtr, etc.) the signatures must match exactly.
            // For SafeHandle-based types (non-frozen structs, classes), the wrapper uses the
            // specific class name while P/Invoke uses SafeHandle — these are compatible because
            // the C# class inherits from SafeHandle and the runtime marshals automatically.
            bool hasIncompatibleParam = false;
            if (candidateWrapperSig.Parameters.Count == candidatePInvokeSig.Parameters.Count)
            {
                for (int i = 0; i < candidateWrapperSig.Parameters.Count; i++)
                {
                    var wp = candidateWrapperSig.Parameters[i];
                    var pp = candidatePInvokeSig.Parameters[i];
                    if (!IsParamCompatibleForBypass(wp, pp))
                    {
                        hasIncompatibleParam = true;
                        break;
                    }
                }
            }
            else
            {
                hasIncompatibleParam = true;
            }

            if (hasIncompatibleParam)
            {
                // Try removing a passthrough arg with default, starting from the end
                var removable = candidatePassthrough.FindLastIndex(a => a.HasDefaultArg);
                if (removable >= 0)
                {
                    var removed = candidatePassthrough[removable];
                    existentialArgs.Add(removed); // treat as omitted (Swift fills default)
                    candidatePassthrough.RemoveAt(removable);
                    logger.LogDebug("ExistentialBypassEmitter: removing passthrough param '{Name}' (has default) due to sig incompatibility.", removed.Name);
                    continue;
                }
                logger.LogDebug("ExistentialBypassEmitter: wrapper and P/Invoke parameter signatures differ, cannot bypass. Wrapper='{Wrapper}', PInvoke='{PInvoke}'.",
                    candidateWrapperSig.ParametersString(), candidatePInvokeSig.PInvokeParametersString());
                return false;
            }

            reducedWrapperSig = candidateWrapperSig;
            reducedPInvokeSig = candidatePInvokeSig;
            passthroughArgs = candidatePassthrough;
            break;
        }

        // Everything checks out — emit the bypass

        var typeName = structDecl.Name;
        var swiftModuleQualifiedName = structDecl.SwiftTypeName.ModuleQualifiedName;
        var swiftTypeName = swiftModuleQualifiedName.Contains('.')
            ? swiftModuleQualifiedName.Substring(swiftModuleQualifiedName.IndexOf('.') + 1)
            : swiftModuleQualifiedName;

        var mangledHash = EmitterUtility.DeterministicHash8(methodDecl.MangledName);
        var wrapperSymbol = $"SBW_{typeName}_init_{mangledHash}";
        var freeSymbol = $"SBW_{typeName}_free_{mangledHash}";
        var factoryName = $"Create_{mangledHash}";

        // Determine library path for the wrapper
        var moduleDecl = methodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(methodDecl.ModuleDecl));
        var moduleLibPath = env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
        var wrapperLibPath = env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;

        // Determine if type is frozen value (no memory management)
        var typeRecord = env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
        bool isFrozenValue = MarshallingHelpers.IsTypeFrozen(typeRecord) && !MarshallingHelpers.RequiresMemoryManagement(typeRecord);

        // --- Emit Swift wrapper ---
        EmitSwiftWrapper(swiftWriter, wrapperSymbol, freeSymbol, swiftTypeName, passthroughArgs, existentialArgs, env);

        // --- Emit C# factory ---
        EmitCSharpFactory(csWriter, env, typeName, factoryName, wrapperSymbol, freeSymbol,
            wrapperLibPath, reducedWrapperSig, reducedPInvokeSig, isFrozenValue);

        return true;
    }

    /// <summary>
    /// Attempts to emit a bypass wrapper for an instance method that has existential type arguments
    /// in bound generic parameters. Only succeeds if: all existential params have HasDefaultArg == true,
    /// the method is a void-returning, non-throwing instance method on a class or struct,
    /// and the remaining non-existential params are fully marshallable.
    /// </summary>
    /// <returns>true if the bypass was emitted; false to fall back to skip.</returns>
    public static bool TryEmitMethodBypass(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ILogger logger)
    {
        logger.LogDebug("ExistentialBypassEmitter: TryEmitMethodBypass called for '{Name}' on '{Parent}'.",
            env.MethodDecl.Name, env.ParentDecl?.Name ?? "null");

        // Must be a class or struct parent
        if (env.ParentDecl is not TypeDecl parentTypeDecl)
        {
            logger.LogDebug("ExistentialBypassEmitter: rejected — parent is not TypeDecl.");
            return false;
        }
        bool isClass = parentTypeDecl is ClassDecl;
        bool isStruct = parentTypeDecl is StructDecl;
        if (!isClass && !isStruct)
        {
            logger.LogDebug("ExistentialBypassEmitter: rejected — parent is neither class nor struct ({Type}).", parentTypeDecl.GetType().Name);
            return false;
        }

        // For structs, only non-frozen structs are supported (they have _payload like classes).
        // Frozen structs are C# value types — passing 'this' as IntPtr requires pinning,
        // which is not yet implemented for the bypass path.
        if (isStruct)
        {
            var structRecord = env.TypeDatabase.TryGetTypeRecord(parentTypeDecl.SwiftTypeName, out var sr) ? sr : null;
            if (structRecord != null && MarshallingHelpers.IsTypeFrozen(structRecord) && !MarshallingHelpers.RequiresMemoryManagement(structRecord))
            {
                logger.LogDebug("ExistentialBypassEmitter: method bypass - frozen value struct not supported.");
                return false;
            }
        }

        var methodDecl = env.MethodDecl;

        // Only handle instance methods (not constructors, not static)
        if (methodDecl.IsConstructor || methodDecl.MethodType == MethodType.Static)
        {
            logger.LogDebug("ExistentialBypassEmitter: rejected — constructor={Ctor} static={Static}.", methodDecl.IsConstructor, methodDecl.MethodType == MethodType.Static);
            return false;
        }

        // Only void return for now — non-void returns need result marshalling
        var returnType = methodDecl.CSSignature.First();
        if (!returnType.SwiftTypeSpec.IsEmptyTuple)
        {
            logger.LogDebug("ExistentialBypassEmitter: rejected — non-void return: {ReturnType}.", returnType.SwiftTypeSpec);
            return false;
        }

        // Throwing methods produce different Swift return shapes
        if (methodDecl.Throws)
        {
            logger.LogDebug("ExistentialBypassEmitter: rejected — throws.");
            return false;
        }

        // Async methods need Task<T> return semantics — bypass emits synchronous void
        if (methodDecl.IsAsync)
        {
            logger.LogDebug("ExistentialBypassEmitter: rejected — async.");
            return false;
        }

        // Classify params into existential/omittable vs passthrough
        var allArgs = methodDecl.CSSignature.Skip(1).ToList();
        var existentialArgs = new List<ArgumentDecl>();
        var passthroughArgs = new List<ArgumentDecl>();
        var closureHandler = new ClosureHandler(env.TypeDatabase);

        foreach (var arg in allArgs)
        {
            // Two-tier existential check matching MethodHandler + BuildReducedMethodDecl:
            // 1. Unsupported existentials are always omittable
            // 2. Supported existentials only if NOT in a container with dedicated handling
            //    (Array<any P>, Dict<K, any P>, Optional<any P> go through normal emission)
            bool isOmittableExistential = false;
            if (env.BoundGenericsHandler.IsBoundGeneric(arg))
            {
                if (env.BoundGenericsHandler.TryGetFirstUnsupportedExistentialTypeArgument(arg.SwiftTypeSpec, out _))
                {
                    isOmittableExistential = true;
                }
                else if (env.BoundGenericsHandler.TryGetFirstExistentialTypeArgument(arg.SwiftTypeSpec, out _) &&
                         !env.BoundGenericsHandler.IsContainerWithSupportedDirectExistential(arg.SwiftTypeSpec))
                {
                    isOmittableExistential = true;
                }
            }

            if (isOmittableExistential)
            {
                existentialArgs.Add(arg);
            }
            else if (closureHandler.IsOptionalClosure(arg.SwiftTypeSpec) && arg.HasDefaultArg)
            {
                var innerClosure = closureHandler.GetClosureTypeSpec(arg);
                if (innerClosure != null && !closureHandler.IsSupportedClosure(innerClosure))
                {
                    existentialArgs.Add(arg); // Omit — Swift fills nil
                }
                else
                {
                    passthroughArgs.Add(arg);
                }
            }
            else
            {
                passthroughArgs.Add(arg);
            }
        }

        logger.LogDebug("ExistentialBypassEmitter: method '{Name}': {ExCount} omittable, {PassCount} passthrough args.",
            methodDecl.Name, existentialArgs.Count, passthroughArgs.Count);

        if (existentialArgs.Count == 0)
        {
            logger.LogDebug("ExistentialBypassEmitter: rejected — no omittable args found.");
            return false;
        }

        // ALL existential args must have HasDefaultArg
        foreach (var arg in existentialArgs)
        {
            if (!arg.HasDefaultArg)
            {
                logger.LogDebug("ExistentialBypassEmitter: method bypass - param '{Name}' lacks HasDefaultArg.", arg.Name);
                return false;
            }
        }

        // Reject passthrough args that are generic type parameters
        foreach (var arg in passthroughArgs)
        {
            if (arg.IsGeneric)
            {
                logger.LogDebug("ExistentialBypassEmitter: method bypass - passthrough param '{Name}' is generic.", arg.Name);
                return false;
            }
        }

        // Build a reduced MethodDecl to check if passthrough args are marshallable.
        // If the full passthrough set fails signature validation, progressively remove
        // passthrough args that have HasDefaultArg until we find a compatible subset.
        var candidatePassthrough = new List<ArgumentDecl>(passthroughArgs);
        Signature? reducedWrapperSig = null;
        Signature? reducedPInvokeSig = null;

        while (true)
        {
            var reducedSignature = new List<ArgumentDecl>
            {
                methodDecl.CSSignature.First() // return type (Void)
            };
            reducedSignature.AddRange(candidatePassthrough);

            var reducedMethodDecl = new MethodDecl
            {
                Name = methodDecl.Name,
                MangledName = methodDecl.MangledName,
                MethodType = MethodType.Static,
                IsConstructor = false,
                CSSignature = reducedSignature,
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = methodDecl.ParentDecl,
                ModuleDecl = methodDecl.ModuleDecl,
                Throws = false,
                IsAsync = false,
                Visibility = methodDecl.Visibility
            };

            var reducedEnv = new MethodEnvironment(reducedMethodDecl, env.TypeDatabase, compositionCollector: env.CompositionCollector);
            var reducedSigHandler = new SignatureHandler(reducedEnv);
            var candidateWrapperSig = reducedSigHandler.GetWrapperSignature();

            if (candidateWrapperSig.ContainsPlaceholder)
            {
                logger.LogDebug("ExistentialBypassEmitter: method '{Name}': wrapper sig contains placeholder.", methodDecl.Name);
                var removable = candidatePassthrough.FindLastIndex(a => a.HasDefaultArg);
                if (removable >= 0)
                {
                    var removed = candidatePassthrough[removable];
                    existentialArgs.Add(removed);
                    candidatePassthrough.RemoveAt(removable);
                    continue;
                }
                logger.LogDebug("ExistentialBypassEmitter: method bypass - reduced signature contains placeholder, no removable params.");
                return false;
            }

            Signature candidatePInvokeSig;
            try
            {
                candidatePInvokeSig = reducedSigHandler.GetPInvokeSignature();
            }
            catch (Exception ex)
            {
                logger.LogDebug("ExistentialBypassEmitter: method '{Name}': P/Invoke sig failed: {Ex}.", methodDecl.Name, ex.Message);
                var removable = candidatePassthrough.FindLastIndex(a => a.HasDefaultArg);
                if (removable >= 0)
                {
                    var removed = candidatePassthrough[removable];
                    existentialArgs.Add(removed);
                    candidatePassthrough.RemoveAt(removable);
                    continue;
                }
                logger.LogDebug("ExistentialBypassEmitter: method bypass - reduced P/Invoke signature failed.");
                return false;
            }

            bool hasIncompatible = false;
            if (candidateWrapperSig.Parameters.Count == candidatePInvokeSig.Parameters.Count)
            {
                for (int i = 0; i < candidateWrapperSig.Parameters.Count; i++)
                {
                    if (!IsParamCompatibleForBypass(candidateWrapperSig.Parameters[i], candidatePInvokeSig.Parameters[i]))
                    {
                        hasIncompatible = true;
                        break;
                    }
                }
            }
            else
            {
                hasIncompatible = true;
            }

            if (hasIncompatible)
            {
                logger.LogDebug("ExistentialBypassEmitter: method '{Name}': params incompatible. W='{Wrapper}', P='{PInvoke}'.",
                    methodDecl.Name, candidateWrapperSig.ParametersString(), candidatePInvokeSig.PInvokeParametersString());
                var removable = candidatePassthrough.FindLastIndex(a => a.HasDefaultArg);
                if (removable >= 0)
                {
                    var removed = candidatePassthrough[removable];
                    existentialArgs.Add(removed);
                    candidatePassthrough.RemoveAt(removable);
                    continue;
                }
                logger.LogDebug("ExistentialBypassEmitter: method bypass - wrapper/P/Invoke param signatures differ, no removable params.");
                return false;
            }

            logger.LogDebug("ExistentialBypassEmitter: method '{Name}': signature check passed.", methodDecl.Name);
            reducedWrapperSig = candidateWrapperSig;
            reducedPInvokeSig = candidatePInvokeSig;
            passthroughArgs = candidatePassthrough;
            break;
        }

        // Everything checks out — emit the bypass

        var typeName = parentTypeDecl.Name;
        var swiftModuleQualifiedName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var swiftTypeName = swiftModuleQualifiedName.Contains('.')
            ? swiftModuleQualifiedName.Substring(swiftModuleQualifiedName.IndexOf('.') + 1)
            : swiftModuleQualifiedName;

        var mangledHash = EmitterUtility.DeterministicHash8(methodDecl.MangledName);
        var wrapperSymbol = $"SBW_{typeName}_{methodDecl.Name}_{mangledHash}";

        // Determine library path for the wrapper
        var moduleDecl = methodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(methodDecl.ModuleDecl));
        var moduleLibPath = env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
        var wrapperLibPath = env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;

        // --- Emit Swift wrapper ---
        EmitMethodSwiftWrapper(swiftWriter, wrapperSymbol, swiftTypeName, isClass,
            passthroughArgs, existentialArgs, env);

        // --- Emit C# method ---
        EmitMethodCSharpBinding(csWriter, env, typeName, wrapperSymbol, wrapperLibPath,
            isClass, reducedWrapperSig, reducedPInvokeSig);

        return true;
    }

    private static void EmitMethodSwiftWrapper(
        SwiftWriter swiftWriter,
        string wrapperSymbol,
        string swiftTypeName,
        bool isClass,
        List<ArgumentDecl> passthroughArgs,
        List<ArgumentDecl> existentialArgs,
        MethodEnvironment env)
    {
        var methodDecl = env.MethodDecl;

        // Build Swift parameter list: self first, then passthrough args
        var swiftParams = new List<string>();

        // Self parameter
        if (isClass)
            swiftParams.Add($"_ __self: {swiftTypeName}");
        else
            swiftParams.Add("_ __self: UnsafeMutableRawPointer");

        foreach (var arg in passthroughArgs)
        {
            var swiftType = RenderSwiftTypeSpec(arg.SwiftTypeSpec);
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
            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            swiftParams.Add($"_ {label}: {swiftType}");
        }
        var swiftParamString = string.Join(", ", swiftParams);

        // Build Swift call arguments
        var callArgs = new List<string>();
        var allArgs = methodDecl.CSSignature.Skip(1).ToList();
        foreach (var arg in allArgs)
        {
            if (existentialArgs.Contains(arg))
                continue; // Omitted — Swift uses default value

            var privateName = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var argStr = arg.Name switch
            {
                var n when n.StartsWith("arg") => privateName,
                var n when n.StartsWith("_") => $"{n.Substring(1)}: {privateName}",
                var n when string.IsNullOrEmpty(n) => privateName,
                var n => $"{n}: {privateName}"
            };
            callArgs.Add(argStr);
        }
        var callArgString = string.Join(", ", callArgs);

        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
        swiftWriter.WriteLine($"public func {wrapperSymbol}({swiftParamString}) {{");
        swiftWriter.Indent++;

        // Convert self and call the method
        if (!isClass)
        {
            // Non-frozen struct: dereference pointer to get value, call method.
            // Use 'var' to support mutating methods (though most bypass candidates are non-mutating).
            swiftWriter.WriteLine($"var __selfTyped = __self.assumingMemoryBound(to: {swiftTypeName}.self).pointee");
            swiftWriter.WriteLine($"__selfTyped.{NameProvider.ParserNameToSwift(methodDecl)}({callArgString})");
            // Write back for mutating methods
            swiftWriter.WriteLine($"__self.assumingMemoryBound(to: {swiftTypeName}.self).pointee = __selfTyped");
        }
        else
        {
            swiftWriter.WriteLine($"__self.{NameProvider.ParserNameToSwift(methodDecl)}({callArgString})");
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static void EmitMethodCSharpBinding(
        CSharpWriter csWriter,
        MethodEnvironment env,
        string typeName,
        string wrapperSymbol,
        string wrapperLibPath,
        bool isClass,
        Signature reducedWrapperSig,
        Signature reducedPInvokeSig)
    {
        var methodDecl = env.MethodDecl;
        var accessModifier = NameProvider.GetAccessModifier(methodDecl.Visibility);

        // Build the public method parameter list from the reduced wrapper signature
        var paramString = reducedWrapperSig.ParametersString();

        // P/Invoke params: self (IntPtr) + passthrough args
        var pInvokeParamsList = new List<string> { "IntPtr self" };
        var pInvokePassthroughParams = reducedPInvokeSig.PInvokeParametersString();
        if (!string.IsNullOrEmpty(pInvokePassthroughParams))
            pInvokeParamsList.Add(pInvokePassthroughParams);
        var pInvokeParams = string.Join(", ", pInvokeParamsList);

        // Emit P/Invoke declaration
        if (env.PInvokeHelperContext != null)
        {
            env.PInvokeHelperContext.AddDeclaration(new PInvokeDeclaration
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = wrapperSymbol,
                MethodName = wrapperSymbol,
                ReturnType = "void",
                ParametersString = pInvokeParams,
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
                ReturnType = "void",
                ParametersString = pInvokeParams
            });
            csWriter.WriteLine();
        }

        // Build C# method name
        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(methodDecl);
        var methodName = NameProvider.GetPublicMethodName(methodDecl.Name, methodDecl.IsAsync, hasReturnValue: false, isSelfReturning: isSelfReturning,
            parameterCount: methodDecl.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

        // Pre-compute marshalling to determine if unsafe is needed before emitting the method declaration.
        var (marshalledArgs, setupLines, needsUnsafe) = GetBypassMarshalledCallArguments(reducedWrapperSig, reducedPInvokeSig);

        // Build call arguments: self handle + passthrough args.
        // Classes: _handle IS the Swift object pointer (SwiftClassHandle) — pass directly.
        // Non-frozen structs: _payload buffer IS the struct data — pass directly.
        var callArgsList = new List<string>();
        var classParentDecl = env.ParentDecl as ClassDecl;
        var selfExpr = classParentDecl != null
            ? (classParentDecl.IsObjCRooted ? "Handle" : "_handle.DangerousGetHandle()")
            : "_payload.DangerousGetHandle()";
        callArgsList.Add(selfExpr);
        var passthroughCallArgs = string.Join(", ", marshalledArgs);
        if (!string.IsNullOrEmpty(passthroughCallArgs))
            callArgsList.Add(passthroughCallArgs);
        var callArgs = string.Join(", ", callArgsList);

        // Emit public method (unsafe needed for stackalloc marshalling)
        var unsafeModifier = needsUnsafe ? "unsafe " : "";
        csWriter.WriteLine($"{accessModifier} {unsafeModifier}void {methodName}({paramString})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Emit marshalling setup lines (string conversions)
        foreach (var line in setupLines)
            csWriter.WriteLine(line);

        var wrapperCall = env.PInvokeHelperContext != null
            ? $"{env.PInvokeHelperContext.HelperClassName}.{wrapperSymbol}"
            : wrapperSymbol;

        csWriter.WriteLine($"{wrapperCall}({callArgs});");

        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    private static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        string wrapperSymbol,
        string freeSymbol,
        string swiftTypeName,
        List<ArgumentDecl> passthroughArgs,
        List<ArgumentDecl> existentialArgs,
        MethodEnvironment env)
    {
        // Build Swift parameter list for passthrough args
        var swiftParams = new List<string>();
        foreach (var arg in passthroughArgs)
        {
            var swiftType = RenderSwiftTypeSpec(arg.SwiftTypeSpec);
            // Wrapper functions always need @escaping on closure parameters because
            // the closure is passed to the original method which may require it.
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
            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            swiftParams.Add($"_ {label}: {swiftType}");
        }
        var swiftParamString = string.Join(", ", swiftParams);

        // Build Swift call arguments using the same label convention as async wrappers:
        // - "argX" prefix → unlabeled (no label)
        // - "_foo" prefix → label "foo:" (strip underscore)
        // - other → label "name:" (use Name as label)
        var callArgs = new List<string>();
        var allArgs = env.MethodDecl.CSSignature.Skip(1).ToList();
        foreach (var arg in allArgs)
        {
            if (existentialArgs.Contains(arg))
            {
                // Omitted — Swift uses default value
                continue;
            }
            var privateName = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var argStr = arg.Name switch
            {
                var n when n.StartsWith("arg") => privateName,
                var n when n.StartsWith("_") => $"{n.Substring(1)}: {privateName}",
                var n when string.IsNullOrEmpty(n) => privateName,
                var n => $"{n}: {privateName}"
            };
            callArgs.Add(argStr);
        }
        var callArgString = string.Join(", ", callArgs);

        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
        swiftWriter.WriteLine($"public func {wrapperSymbol}({swiftParamString}) -> UnsafeMutableRawPointer {{");
        swiftWriter.Indent++;
        swiftWriter.WriteLine($"let result = {swiftTypeName}({callArgString})");
        swiftWriter.WriteLine($"let ptr = UnsafeMutablePointer<{swiftTypeName}>.allocate(capacity: 1)");
        swiftWriter.WriteLine("ptr.initialize(to: result)");
        swiftWriter.WriteLine("return UnsafeMutableRawPointer(ptr)");
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"@_silgen_name(\"{freeSymbol}\")");
        swiftWriter.WriteLine($"public func {freeSymbol}(_ ptr: UnsafeMutableRawPointer) {{");
        swiftWriter.Indent++;
        swiftWriter.WriteLine($"let typedPtr = ptr.assumingMemoryBound(to: {swiftTypeName}.self)");
        swiftWriter.WriteLine("typedPtr.deinitialize(count: 1)");
        swiftWriter.WriteLine("typedPtr.deallocate()");
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static void EmitCSharpFactory(
        CSharpWriter csWriter,
        MethodEnvironment env,
        string typeName,
        string factoryName,
        string wrapperSymbol,
        string freeSymbol,
        string wrapperLibPath,
        Signature reducedWrapperSig,
        Signature reducedPInvokeSig,
        bool isFrozenValue)
    {
        var accessModifier = NameProvider.GetAccessModifier(env.MethodDecl.Visibility);
        // Public factory uses the wrapper (high-level) signature
        var paramString = reducedWrapperSig.ParametersString();

        // P/Invoke extern declarations use the P/Invoke (low-level) signature
        var pInvokeParams = reducedPInvokeSig.PInvokeParametersString();

        // Emit P/Invoke declarations
        if (env.PInvokeHelperContext != null)
        {
            // Generic type: route through PInvokeHelperContext.
            // Bypass wrappers are plain functions — they do NOT take metadata params.
            env.PInvokeHelperContext.AddDeclaration(new PInvokeDeclaration
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = wrapperSymbol,
                MethodName = wrapperSymbol,
                ReturnType = "IntPtr",
                ParametersString = pInvokeParams,
                IsAsync = false,
                MetadataParameters = null
            });
            env.PInvokeHelperContext.AddDeclaration(new PInvokeDeclaration
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = freeSymbol,
                MethodName = freeSymbol,
                ReturnType = "void",
                ParametersString = "IntPtr ptr",
                IsAsync = false,
                MetadataParameters = null
            });
        }
        else
        {
            // Non-generic type: emit inline
            PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = wrapperSymbol,
                MethodName = wrapperSymbol,
                ReturnType = "IntPtr",
                ParametersString = pInvokeParams
            });
            csWriter.WriteLine();
            PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = freeSymbol,
                MethodName = freeSymbol,
                ReturnType = "void",
                ParametersString = "IntPtr ptr"
            });
            csWriter.WriteLine();
        }

        // Emit factory method
        csWriter.WriteLine($"{accessModifier} static unsafe {typeName} {factoryName}({paramString})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Generate marshalling code for string params if needed
        var (marshalledArgs, setupLines, _) = GetBypassMarshalledCallArguments(reducedWrapperSig, reducedPInvokeSig);
        var callArgs = string.Join(", ", marshalledArgs);

        var wrapperCall = env.PInvokeHelperContext != null
            ? $"{env.PInvokeHelperContext.HelperClassName}.{wrapperSymbol}"
            : wrapperSymbol;
        var freeCall = env.PInvokeHelperContext != null
            ? $"{env.PInvokeHelperContext.HelperClassName}.{freeSymbol}"
            : freeSymbol;

        // Emit marshalling setup lines (string conversions)
        foreach (var line in setupLines)
            csWriter.WriteLine(line);

        csWriter.WriteLine("IntPtr swiftPtr = IntPtr.Zero;");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"swiftPtr = {wrapperCall}({callArgs});");

        if (isFrozenValue)
        {
            // Frozen value type: copy directly from the pointer
            csWriter.WriteLine($"return *({typeName}*)swiftPtr;");
        }
        else
        {
            // Non-frozen or frozen-with-memory-management: copy via metadata
            csWriter.WriteLine($"var metadata = TypeMetadata.GetTypeMetadataOrThrow<{typeName}>();");
            csWriter.WriteLine("IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("metadata.ValueWitnessTable->InitializeWithCopy((void*)buffer, (void*)swiftPtr, metadata);");
            csWriter.WriteLine($"return new {typeName}(buffer);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("catch");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("NativeMemory.Free((void*)buffer);");
            csWriter.WriteLine("throw;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("finally");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("if (swiftPtr != IntPtr.Zero)");
        csWriter.Indent++;
        csWriter.WriteLine($"{freeCall}(swiftPtr);");
        csWriter.Indent--;
        csWriter.Indent--;
        csWriter.WriteLine("}");

        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    /// <summary>
    /// Generates call arguments for the bypass factory/method. Unlike CallArgumentsString()
    /// which generates complex conversions (e.g., .Payload for SafeHandle), this generates
    /// simple pass-through arguments suitable for the bypass pattern where the P/Invoke
    /// declaration uses SafeHandle and the runtime handles marshalling.
    /// </summary>
    private static string GetBypassCallArguments(Signature pInvokeSig)
    {
        var args = new List<string>();
        foreach (var p in pInvokeSig.Parameters)
        {
            // Preserve ref/out modifiers at the call site
            var prefix = string.IsNullOrEmpty(p.modifier) ? "" : $"{p.modifier} ";
            args.Add($"{prefix}{p.Name}");
        }
        return string.Join(", ", args);
    }

    /// <summary>
    /// Checks whether a wrapper parameter and its corresponding P/Invoke parameter are
    /// compatible for the bypass factory. Compatible means the factory can pass the wrapper
    /// value directly to the P/Invoke (possibly with string marshalling code).
    /// </summary>
    private static bool IsParamCompatibleForBypass(Parameter wrapperParam, Parameter pInvokeParam)
    {
        // Exact string match — trivially compatible
        if (wrapperParam.SignatureString() == pInvokeParam.PInvokeSignatureString())
            return true;

        // SafeHandle-based types: the wrapper uses the specific C# class name (e.g., "URLRequest")
        // while P/Invoke uses "SafeHandle". These are compatible because the C# class inherits
        // from SafeHandle and the runtime marshals automatically.
        if (pInvokeParam.Type is MarshalledType.NonFrozenSafeHandleType or MarshalledType.NativeRemappedNonFrozenType)
            return true;

        // String marshalling: wrapper uses "string" but P/Invoke uses SwiftString (or similar).
        // The bypass emitter handles the conversion.
        if (IsStringMarshallingNeeded(wrapperParam, pInvokeParam))
            return true;

        return false;
    }

    /// <summary>
    /// Returns true if the wrapper param is a string/string? type and the P/Invoke param
    /// differs, requiring marshalling code in the bypass.
    /// </summary>
    private static bool IsStringMarshallingNeeded(Parameter wrapperParam, Parameter pInvokeParam)
    {
        var wrapperSig = wrapperParam.SignatureString().Trim();
        var pInvokeSig = pInvokeParam.PInvokeSignatureString().Trim();

        var wrapperType = wrapperSig.Replace(wrapperParam.Name, "").Trim();
        var pInvokeType = pInvokeSig.Replace(pInvokeParam.Name, "").Trim();

        // string → SwiftString.Buffer or similar Swift string type
        if (wrapperType == "string" &&
            (pInvokeType.Contains("SwiftString") || pInvokeType.Contains("Swift.SwiftString")))
            return true;

        // string? → IntPtr (large optional buffer) or SwiftOptional<SwiftString>
        if (wrapperType == "string?" && wrapperType != pInvokeType)
            return true;

        return false;
    }

    /// <summary>
    /// Emits marshalling setup code for bypass parameters that need string conversion.
    /// Returns the list of variable names to use in the P/Invoke call (replacing param names
    /// where marshalling is needed) and disposal lines for cleanup.
    /// Also returns whether the call must be wrapped in an unsafe block.
    /// </summary>
    private static (List<string> callArgs, List<string> setupLines, bool needsUnsafe) GetBypassMarshalledCallArguments(
        Signature wrapperSig, Signature pInvokeSig)
    {
        var callArgs = new List<string>();
        var setupLines = new List<string>();
        bool needsUnsafe = false;

        for (int i = 0; i < pInvokeSig.Parameters.Count; i++)
        {
            var wParam = i < wrapperSig.Parameters.Count ? wrapperSig.Parameters[i] : null;
            var pParam = pInvokeSig.Parameters[i];

            if (wParam != null && IsStringMarshallingNeeded(wParam, pParam))
            {
                var wrapperType = wParam.SignatureString().Replace(wParam.Name, "").Trim();
                var pInvokeType = pParam.PInvokeSignatureString().Replace(pParam.Name, "").Trim();

                if (wrapperType == "string?" && pInvokeType == "IntPtr")
                {
                    // string? → IntPtr (large optional buffer for OptionalPointerWrapper)
                    // Must allocate a buffer, write SwiftOptional<SwiftString> into it, pass the pointer.
                    var bufName = $"__{pParam.Name}Buf";
                    setupLines.Add(
                        $"using var __{pParam.Name}Str = {wParam.Name} is {{}} __{pParam.Name}Val ? new SwiftString(__{pParam.Name}Val) : default;");
                    setupLines.Add(
                        $"var __{pParam.Name}Opt = {wParam.Name} is {{}} ? SwiftOptional<SwiftString>.NewSome(__{pParam.Name}Str) : SwiftOptional<SwiftString>.NewNone();");
                    setupLines.Add(
                        $"var __{pParam.Name}Size = (int)TypeMetadata.GetTypeMetadataOrThrow<SwiftOptional<SwiftString>>().Size;");
                    setupLines.Add(
                        $"byte* {bufName} = stackalloc byte[__{pParam.Name}Size];");
                    setupLines.Add(
                        $"var __{pParam.Name}Span = new Span<byte>({bufName}, __{pParam.Name}Size);");
                    setupLines.Add(
                        $"SwiftMarshal.MarshalToSwift(__{pParam.Name}Opt, ref __{pParam.Name}Span);");
                    callArgs.Add($"new IntPtr({bufName})");
                    needsUnsafe = true;
                }
                else if (wrapperType == "string?")
                {
                    // string? → SwiftOptional<SwiftString> (direct)
                    var varName = $"__{pParam.Name}Opt";
                    setupLines.Add(
                        $"using var __{pParam.Name}Str = {wParam.Name} is {{}} __{pParam.Name}Val ? new SwiftString(__{pParam.Name}Val) : default;");
                    setupLines.Add(
                        $"var {varName} = {wParam.Name} is {{}} ? SwiftOptional<SwiftString>.NewSome(__{pParam.Name}Str) : SwiftOptional<SwiftString>.NewNone();");
                    callArgs.Add(varName);
                }
                else
                {
                    // string → SwiftString → SwiftString.Buffer (P/Invoke takes the blittable Buffer type)
                    var strVar = $"__{pParam.Name}Str";
                    var dispVar = $"__{pParam.Name}Disposable";
                    setupLines.Add($"using var {strVar} = new SwiftString({wParam.Name});");
                    setupLines.Add($"using var {dispVar} = {strVar}.PayloadBuffer;");
                    callArgs.Add($"{dispVar}.Buffer");
                }
            }
            else if (pParam.Type is MarshalledType.NonFrozenSafeHandleType or MarshalledType.NativeRemappedNonFrozenType)
            {
                // Non-frozen struct/class: P/Invoke takes SafeHandle but wrapper takes the specific type.
                // Extract the .Payload SafeHandle from the ISwiftObject.
                var prefix = string.IsNullOrEmpty(pParam.modifier) ? "" : $"{pParam.modifier} ";
                callArgs.Add($"{prefix}{pParam.Name}.Payload");
            }
            else
            {
                // Preserve ref/out modifiers at the call site
                var prefix = string.IsNullOrEmpty(pParam.modifier) ? "" : $"{pParam.modifier} ";
                callArgs.Add($"{prefix}{pParam.Name}");
            }
        }

        return (callArgs, setupLines, needsUnsafe);
    }

    /// <summary>
    /// Renders a TypeSpec as its Swift source representation, including generic arguments.
    /// Strips module prefixes (e.g. "Swift.Array&lt;Swift.Int&gt;" → "Array&lt;Int&gt;").
    /// </summary>
    public static string RenderSwiftTypeSpec(TypeSpec typeSpec)
        => RenderSwiftTypeSpecCore(typeSpec, moduleQualified: false);

    /// <summary>
    /// Renders a TypeSpec for use in return type position.
    /// Strips @escaping which is only valid in function parameter position.
    /// </summary>
    public static string RenderSwiftTypeSpecForReturnType(TypeSpec typeSpec)
        => RenderSwiftTypeSpec(typeSpec).Replace("@escaping ", "");

    /// <summary>
    /// Renders a TypeSpec with module-qualified names (e.g. "BonMot.StringStyle" instead of "StringStyle").
    /// Use this for .load(as:), .initializeMemory(as:), and .assumingMemoryBound(to:) expressions
    /// where unqualified names can be ambiguous (the wrapper imports the module, and the type name
    /// may collide with types from other imported modules).
    /// </summary>
    public static string RenderModuleQualifiedSwiftTypeSpec(TypeSpec typeSpec)
        => RenderSwiftTypeSpecCore(typeSpec, moduleQualified: true);

    private static string RenderSwiftTypeSpecCore(TypeSpec typeSpec, bool moduleQualified)
    {
        switch (typeSpec)
        {
            case NamedTypeSpec namedTypeSpec:
                var name = moduleQualified ? namedTypeSpec.Name : namedTypeSpec.NameWithoutModule;

                if (namedTypeSpec.GenericParameters.Count > 0)
                {
                    var genericArgs = string.Join(", ", namedTypeSpec.GenericParameters.Select(
                        gp => RenderSwiftTypeSpecCore(gp, moduleQualified)));
                    return $"{name}<{genericArgs}>";
                }
                return name;

            case TupleTypeSpec tupleTypeSpec:
                if (tupleTypeSpec == TupleTypeSpec.Empty)
                    return "Void";
                var elements = string.Join(", ", tupleTypeSpec.Elements.Select(e =>
                {
                    var rendered = RenderSwiftTypeSpecCore(e, moduleQualified);
                    return !string.IsNullOrEmpty(e.TypeLabel) ? $"{e.TypeLabel}: {rendered}" : rendered;
                }));
                return $"({elements})";

            case ClosureTypeSpec closureTypeSpec:
                // Render closure arguments without double-wrapping tuples.
                // Closure args: () for no args, (Arg) for single, (A, B) for multiple.
                string argsRendered;
                if (!closureTypeSpec.HasArguments())
                {
                    argsRendered = "()";
                }
                else if (closureTypeSpec.Arguments is TupleTypeSpec argsTuple)
                {
                    var elems = string.Join(", ", argsTuple.Elements.Select(e =>
                    {
                        var rendered = RenderSwiftTypeSpecCore(e, moduleQualified);
                        var inoutPrefix = e.IsInOut ? "inout " : "";
                        var labeled = !string.IsNullOrEmpty(e.TypeLabel) ? $"{e.TypeLabel}: {inoutPrefix}{rendered}" : $"{inoutPrefix}{rendered}";
                        return labeled;
                    }));
                    argsRendered = $"({elems})";
                }
                else
                {
                    var singleInout = closureTypeSpec.Arguments.IsInOut ? "inout " : "";
                    argsRendered = $"({singleInout}{RenderSwiftTypeSpecCore(closureTypeSpec.Arguments, moduleQualified)})";
                }
                var ret = RenderSwiftTypeSpecCore(closureTypeSpec.ReturnType, moduleQualified);
                var throwsKeyword = closureTypeSpec.Throws ? " throws" : "";
                var asyncKeyword = closureTypeSpec.IsAsync ? " async" : "";
                // @escaping and @Sendable from parsed attributes
                var prefix = "";
                if (closureTypeSpec.IsEscaping)
                    prefix += "@escaping ";
                if (closureTypeSpec.HasAttributes && closureTypeSpec.Attributes.Exists(attr =>
                    attr.Name == "Sendable" || attr.Name == "Swift.Sendable" || attr.Name == "_Concurrency.Sendable"))
                    prefix += "@Sendable ";
                return $"{prefix}{argsRendered}{asyncKeyword}{throwsKeyword} -> {ret}";

            case ProtocolListTypeSpec protocolListTypeSpec:
                if (protocolListTypeSpec.Protocols.Count == 0)
                    return "Any";
                var protocols = string.Join(" & ", protocolListTypeSpec.Protocols.Keys.Select(
                    p => RenderSwiftTypeSpecCore(p, moduleQualified)));
                return $"any {protocols}";

            default:
                return typeSpec.ToString();
        }
    }

    /// <summary>
    /// Checks whether a method has any Optional&lt;Closure&gt; parameters with HasDefaultArg
    /// where the closure is unsupported. Used by MethodHandler to decide whether to attempt bypass.
    /// </summary>
    public static bool HasOptionalClosureWithDefault(MethodDecl method, ITypeDatabase typeDatabase)
    {
        var closureHandler = new ClosureHandler(typeDatabase);
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (closureHandler.IsOptionalClosure(arg.SwiftTypeSpec) && arg.HasDefaultArg)
            {
                var innerClosure = closureHandler.GetClosureTypeSpec(arg);
                if (innerClosure != null && !closureHandler.IsSupportedClosure(innerClosure))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Builds a reduced MethodDecl with omittable params (existential bound generics and
    /// unsupported Optional&lt;Closure&gt;+default) stripped. Used by MethodHandler for
    /// reduced-signature dedup before calling TryEmitMethodBypass/TryEmitConstructorBypass.
    /// Returns null if no params were omitted.
    /// </summary>
    public static MethodDecl? BuildReducedMethodDecl(MethodDecl method, ITypeDatabase typeDatabase)
    {
        var closureHandler = new ClosureHandler(typeDatabase);
        var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

        var passthroughArgs = new List<ArgumentDecl>();
        bool hasOmitted = false;

        foreach (var arg in method.CSSignature.Skip(1))
        {
            // Check existential bound generic — mirrors MethodHandler's two-tier check:
            // 1. Unsupported existentials are always omittable
            // 2. Supported existentials only if NOT in a container with dedicated handling
            //    (Array<any P>, Dict<K, any P>, Optional<any P> go through normal emission)
            if (boundGenericsHandler.IsBoundGeneric(arg))
            {
                if (boundGenericsHandler.TryGetFirstUnsupportedExistentialTypeArgument(arg.SwiftTypeSpec, out _))
                {
                    hasOmitted = true;
                    continue;
                }
                if (boundGenericsHandler.TryGetFirstExistentialTypeArgument(arg.SwiftTypeSpec, out _) &&
                    !boundGenericsHandler.IsContainerWithSupportedDirectExistential(arg.SwiftTypeSpec))
                {
                    hasOmitted = true;
                    continue;
                }
            }

            // Check unsupported Optional<Closure> with default
            if (closureHandler.IsOptionalClosure(arg.SwiftTypeSpec) && arg.HasDefaultArg)
            {
                var innerClosure = closureHandler.GetClosureTypeSpec(arg);
                if (innerClosure != null && !closureHandler.IsSupportedClosure(innerClosure))
                {
                    hasOmitted = true;
                    continue;
                }
            }

            passthroughArgs.Add(arg);
        }

        if (!hasOmitted)
            return null;

        var reducedSignature = new List<ArgumentDecl>
        {
            method.CSSignature.First() // return type
        };
        reducedSignature.AddRange(passthroughArgs);

        return new MethodDecl
        {
            Name = method.Name,
            MangledName = method.MangledName,
            MethodType = method.MethodType,
            IsConstructor = method.IsConstructor,
            CSSignature = reducedSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = method.ParentDecl,
            ModuleDecl = method.ModuleDecl,
            Throws = method.Throws,
            IsAsync = method.IsAsync,
            Visibility = method.Visibility
        };
    }
}
