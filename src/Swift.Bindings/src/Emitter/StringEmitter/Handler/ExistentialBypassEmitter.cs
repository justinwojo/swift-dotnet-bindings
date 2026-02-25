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

        foreach (var arg in allArgs)
        {
            if (env.BoundGenericsHandler.IsBoundGeneric(arg) &&
                env.BoundGenericsHandler.TryGetFirstExistentialTypeArgument(arg.SwiftTypeSpec, out _))
            {
                existentialArgs.Add(arg);
            }
            else
            {
                passthroughArgs.Add(arg);
            }
        }

        // Must have at least one existential argument
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

        // Build a reduced MethodDecl to check if passthrough args are marshallable
        var reducedSignature = new List<ArgumentDecl>
        {
            methodDecl.CSSignature.First() // return type
        };
        reducedSignature.AddRange(passthroughArgs);

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
        var reducedWrapperSig = reducedSigHandler.GetWrapperSignature();

        if (reducedWrapperSig.ContainsPlaceholder)
        {
            logger.LogDebug("ExistentialBypassEmitter: reduced signature contains placeholder, cannot bypass.");
            return false;
        }

        // Build P/Invoke signature after placeholder check, since GetPInvokeSignature
        // may throw for types not in the database (those would show as placeholders above).
        var reducedPInvokeSig = reducedSigHandler.GetPInvokeSignature();

        // Verify wrapper and P/Invoke signatures produce identical parameter lists.
        // If they differ, the factory would need marshalling setup code we don't emit
        // (e.g., SafeHandle extraction, idiomatic type conversion, indirect results).
        // CallArgumentsString() may reference locals like {name}Handle, {name}Disposable,
        // {name}Swift that WrapperEmitter normally sets up but the bypass factory doesn't.
        if (reducedWrapperSig.ParametersString() != reducedPInvokeSig.PInvokeParametersString())
        {
            logger.LogDebug("ExistentialBypassEmitter: wrapper and P/Invoke parameter signatures differ, cannot bypass.");
            return false;
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
        // Must be a class or struct parent
        if (env.ParentDecl is not TypeDecl parentTypeDecl)
            return false;
        bool isClass = parentTypeDecl is ClassDecl;
        bool isStruct = parentTypeDecl is StructDecl;
        if (!isClass && !isStruct)
            return false;

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
            return false;

        // Only void return for now — non-void returns need result marshalling
        var returnType = methodDecl.CSSignature.First();
        if (returnType.SwiftTypeSpec != TupleTypeSpec.Empty)
            return false;

        // Throwing methods produce different Swift return shapes
        if (methodDecl.Throws)
            return false;

        // Async methods need Task<T> return semantics — bypass emits synchronous void
        if (methodDecl.IsAsync)
            return false;

        // Classify params into existential vs passthrough
        var allArgs = methodDecl.CSSignature.Skip(1).ToList();
        var existentialArgs = new List<ArgumentDecl>();
        var passthroughArgs = new List<ArgumentDecl>();

        foreach (var arg in allArgs)
        {
            bool isExistentialBoundGeneric =
                env.BoundGenericsHandler.IsBoundGeneric(arg) &&
                (env.BoundGenericsHandler.TryGetFirstExistentialTypeArgument(arg.SwiftTypeSpec, out _) ||
                 env.BoundGenericsHandler.TryGetFirstUnsupportedExistentialTypeArgument(arg.SwiftTypeSpec, out _));

            if (isExistentialBoundGeneric)
                existentialArgs.Add(arg);
            else
                passthroughArgs.Add(arg);
        }

        if (existentialArgs.Count == 0)
            return false;

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
        // Use static method type so SignatureHandler doesn't expect self parameter.
        var reducedSignature = new List<ArgumentDecl>
        {
            methodDecl.CSSignature.First() // return type (Void)
        };
        reducedSignature.AddRange(passthroughArgs);

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
        var reducedWrapperSig = reducedSigHandler.GetWrapperSignature();

        if (reducedWrapperSig.ContainsPlaceholder)
        {
            logger.LogDebug("ExistentialBypassEmitter: method bypass - reduced signature contains placeholder.");
            return false;
        }

        var reducedPInvokeSig = reducedSigHandler.GetPInvokeSignature();

        // Verify wrapper and P/Invoke signatures produce identical parameter lists
        if (reducedWrapperSig.ParametersString() != reducedPInvokeSig.PInvokeParametersString())
        {
            logger.LogDebug("ExistentialBypassEmitter: method bypass - wrapper/P/Invoke param signatures differ.");
            return false;
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
            swiftWriter.WriteLine($"__selfTyped.{methodDecl.Name}({callArgString})");
            // Write back for mutating methods
            swiftWriter.WriteLine($"__self.assumingMemoryBound(to: {swiftTypeName}.self).pointee = __selfTyped");
        }
        else
        {
            swiftWriter.WriteLine($"__self.{methodDecl.Name}({callArgString})");
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
            csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
            csWriter.WriteLine($"[LibraryImport(\"{wrapperLibPath}\", EntryPoint = \"{wrapperSymbol}\")]");
            csWriter.WriteLine($"private static partial void {wrapperSymbol}({pInvokeParams});");
            csWriter.WriteLine();
        }

        // Build C# method name
        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(methodDecl);
        var methodName = NameProvider.GetPublicMethodName(methodDecl.Name, methodDecl.IsAsync, hasReturnValue: false, isSelfReturning: isSelfReturning);

        // Emit public method (unsafe needed for class pointer dereference)
        var unsafeModifier = isClass ? "unsafe " : "";
        csWriter.WriteLine($"{accessModifier} {unsafeModifier}void {methodName}({paramString})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Build call arguments: self handle + passthrough args.
        // Classes: _payload buffer contains the object reference at offset 0 — dereference.
        // Non-frozen structs: _payload buffer IS the struct data — pass directly.
        var callArgsList = new List<string>();
        callArgsList.Add(isClass
            ? "*(IntPtr*)_payload.DangerousGetHandle()"
            : "_payload.DangerousGetHandle()");

        var passthroughCallArgs = reducedPInvokeSig.CallArgumentsString();
        if (!string.IsNullOrEmpty(passthroughCallArgs))
            callArgsList.Add(passthroughCallArgs);
        var callArgs = string.Join(", ", callArgsList);

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
            csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
            csWriter.WriteLine($"[LibraryImport(\"{wrapperLibPath}\", EntryPoint = \"{wrapperSymbol}\")]");
            csWriter.WriteLine($"private static partial IntPtr {wrapperSymbol}({pInvokeParams});");
            csWriter.WriteLine();
            csWriter.WriteLine("[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]");
            csWriter.WriteLine($"[LibraryImport(\"{wrapperLibPath}\", EntryPoint = \"{freeSymbol}\")]");
            csWriter.WriteLine($"private static partial void {freeSymbol}(IntPtr ptr);");
            csWriter.WriteLine();
        }

        // Emit factory method
        csWriter.WriteLine($"{accessModifier} static unsafe {typeName} {factoryName}({paramString})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Call arguments use the P/Invoke signature's call format
        var callArgs = reducedPInvokeSig.CallArgumentsString();

        var wrapperCall = env.PInvokeHelperContext != null
            ? $"{env.PInvokeHelperContext.HelperClassName}.{wrapperSymbol}"
            : wrapperSymbol;
        var freeCall = env.PInvokeHelperContext != null
            ? $"{env.PInvokeHelperContext.HelperClassName}.{freeSymbol}"
            : freeSymbol;

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
    /// Renders a TypeSpec as its Swift source representation, including generic arguments.
    /// Strips module prefixes (e.g. "Swift.Array&lt;Swift.Int&gt;" → "Array&lt;Int&gt;").
    /// </summary>
    public static string RenderSwiftTypeSpec(TypeSpec typeSpec)
    {
        switch (typeSpec)
        {
            case NamedTypeSpec namedTypeSpec:
                var name = namedTypeSpec.NameWithoutModule;

                if (namedTypeSpec.GenericParameters.Count > 0)
                {
                    var genericArgs = string.Join(", ", namedTypeSpec.GenericParameters.Select(RenderSwiftTypeSpec));
                    return $"{name}<{genericArgs}>";
                }
                return name;

            case TupleTypeSpec tupleTypeSpec:
                if (tupleTypeSpec == TupleTypeSpec.Empty)
                    return "Void";
                var elements = string.Join(", ", tupleTypeSpec.Elements.Select(RenderSwiftTypeSpec));
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
                    var elems = string.Join(", ", argsTuple.Elements.Select(RenderSwiftTypeSpec));
                    argsRendered = $"({elems})";
                }
                else
                {
                    argsRendered = $"({RenderSwiftTypeSpec(closureTypeSpec.Arguments)})";
                }
                var ret = RenderSwiftTypeSpec(closureTypeSpec.ReturnType);
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
                var protocols = string.Join(" & ", protocolListTypeSpec.Protocols.Keys.Select(p => RenderSwiftTypeSpec(p)));
                return $"any {protocols}";

            default:
                return typeSpec.ToString();
        }
    }
}
