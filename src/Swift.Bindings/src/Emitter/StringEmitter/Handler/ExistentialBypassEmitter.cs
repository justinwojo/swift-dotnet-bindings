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
                IsSynthesizedAccessor = methodDecl.IsSynthesizedAccessor
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
        // Emit module-qualified: structDecl always belongs to the bound module, whose import the
        // wrapper always carries, so the qualifier is safe even outside a collision. The single
        // whole-file ModuleEmissionContext.QualifyForWrapperSource pass at the wrapper boundary
        // strips it back down (or preserves it via the nested carve-out) exactly like every other
        // qualified reference in the wrapper — stripping it locally here would bypass that pass
        // and leave a bare self/return type ambiguous against an imported same-named SDK type.
        var swiftTypeName = swiftModuleQualifiedName;

        var mangledHash = EmitterUtility.DeterministicHash8(methodDecl.MangledName);
        // SBSW_ prefix marks Swift-CC wrappers (@_silgen_name) so the P/Invoke calling-convention
        // picker pairs them with CallConvSwift instead of the SBW_ → CallConvCdecl default.
        var wrapperSymbol = $"SBSW_{typeName}_init_{mangledHash}";
        var freeSymbol = $"SBSW_{typeName}_free_{mangledHash}";

        // Determine library path for the wrapper
        var moduleDecl = methodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(methodDecl.ModuleDecl));
        var moduleLibPath = env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
        var wrapperLibPath = env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;

        // Determine if type is frozen value (no memory management)
        var typeRecord = env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
        bool isFrozenValue = MarshallingHelpers.IsTypeFrozen(typeRecord) && !MarshallingHelpers.RequiresMemoryManagement(typeRecord);

        // S5 claim-then-emit guard: route through TryClaimWrapperSymbol BEFORE writing any
        // Swift / C# code. The init claim is the structural gate (sourceKey anchored to the
        // original Swift mangled name) and we stash it on methodDecl.StructuralIdentityKey so
        // a future MethodWrapperEmitter pass on the same Swift declaration computes the SAME
        // (typeName, methodName, sourceKey) triple — typeName uses ModuleQualifiedName to
        // match MWE. On dup the entire constructor pair is a no-op so the standard path or
        // prior emitter is the source of truth. The free claim follows for contract-gate
        // bookkeeping — it lives in a distinct method-name bucket ("free") so it cannot
        // collide with init.
        var initSourceKey = $"existential-bypass-init::{env.MethodDecl.MangledName}";
        env.MethodDecl.StructuralIdentityKey = initSourceKey;
        if (env.EmissionContext != null &&
            !env.EmissionContext.TryClaimWrapperSymbol(swiftModuleQualifiedName, "init",
                initSourceKey, wrapperSymbol, DeclIdFactory.ForMethod(env.MethodDecl)))
        {
            return false;
        }
        env.EmissionContext?.TryClaimWrapperSymbol(swiftModuleQualifiedName, "free",
            $"existential-bypass-free::{env.MethodDecl.MangledName}", freeSymbol, DeclIdFactory.ForMethod(env.MethodDecl));

        // Prefer a hash-free `Create` factory when the name is provably free on this type;
        // fall back to the deterministic Create_{hash} only on a genuine collision. Two sources:
        //  (a) a non-constructor MEMBER of the type already projects to `Create` — a method (rare —
        //      e.g. a Swift `static func create`), a property (`var create`), or a nested type
        //      (`struct Create`). A real constructor projects to a C# constructor, never `Create`,
        //      so ctors are naturally excluded; any of the three non-ctor member kinds would give
        //      CS0102. Be conservative across all of them and keep the hash on any match.
        //  (b) another existential-bypass factory on the same type already reserved the same C#
        //      overload signature (identical parameter TYPES) — two bare `Create` would be CS0111.
        // Without an EmissionContext (isolated unit contexts) there is no reservation table to
        // prove (b), so keep the hash — the bare name is only ever taken when uniqueness is proven.
        var factoryName = $"Create_{mangledHash}";
        bool createNameTakenByMember =
            structDecl.Methods.Any(m =>
                !m.IsConstructor && !m.IsAccessor &&
                NameProvider.ToPascalCase(m.Name) == "Create") ||
            structDecl.Properties.Any(p =>
                NameProvider.ToPascalCase(p.Name) == "Create") ||
            structDecl.Types.Any(t =>
                NameProvider.ToPascalCase(t.SwiftTypeName.Name) == "Create");
        if (!createNameTakenByMember && env.EmissionContext != null)
        {
            var paramTypeSig = string.Join(
                ",", reducedWrapperSig!.Parameters.Select(p => p.modifier + "|" + p.Type));
            if (env.EmissionContext.TryReserveExistentialBypassCreate(swiftModuleQualifiedName, paramTypeSig))
                factoryName = "Create";
        }

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

        // Supported returns: void, or existential (`any P`). Other non-void returns require
        // full-wrapper result marshalling (string, SafeHandle, tuples, optionals, etc.) that
        // the bypass path doesn't implement — fall through for those.
        var returnType = methodDecl.CSSignature.First();
        bool isExistentialReturn = env.ExistentialHandler.IsExistential(returnType.SwiftTypeSpec);
        if (!returnType.SwiftTypeSpec.IsEmptyTuple && !isExistentialReturn)
        {
            logger.LogDebug("ExistentialBypassEmitter: rejected — non-void, non-existential return: {ReturnType}.", returnType.SwiftTypeSpec);
            return false;
        }

        // Existential return must resolve to a protocol list we can wrap (proxy class or well-known type).
        // Unresolved/zero-protocol existentials (`Any`) fall back — the public return would be `object`
        // which the bypass's proxy-wrap path doesn't produce.
        if (isExistentialReturn)
        {
            var retProtoList = env.ExistentialHandler.ToProtocolListTypeSpec(returnType.SwiftTypeSpec);
            if (retProtoList == null || retProtoList.Protocols.Count == 0 ||
                env.ExistentialHandler.GetPublicExistentialType(retProtoList) == "object")
            {
                logger.LogDebug("ExistentialBypassEmitter: rejected — existential return not resolvable to proxy/public type.");
                return false;
            }
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
                IsSynthesizedAccessor = methodDecl.IsSynthesizedAccessor
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
        // Emit module-qualified: see the constructor-bypass twin of this derivation above for why
        // this must not strip locally — the central QualifyForWrapperSource pass owns that.
        var swiftTypeName = swiftModuleQualifiedName;

        var mangledHash = EmitterUtility.DeterministicHash8(methodDecl.MangledName);
        // SBSW_ prefix marks the Swift-CC (@_silgen_name) method wrapper. See EmitSwiftWrapper
        // for the rationale: method-on-class passes `self` as the parent class type, which is
        // not C-representable under @_cdecl when the class isn't @objc.
        var wrapperSymbol = $"SBSW_{typeName}_{methodDecl.Name}_{mangledHash}";

        // Determine library path for the wrapper
        var moduleDecl = methodDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(methodDecl.ModuleDecl));
        var moduleLibPath = env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
        var wrapperLibPath = env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;

        // S5 claim-then-emit guard: route through TryClaimWrapperSymbol BEFORE writing any
        // Swift / C# code. sourceKey anchors to the original Swift mangled name and we stash
        // it on methodDecl.StructuralIdentityKey so a future MethodWrapperEmitter pass on the
        // same Swift declaration computes the SAME (typeName, methodName, sourceKey) triple —
        // typeName uses ModuleQualifiedName to match MWE's `parentTypeDecl?.SwiftTypeName.
        // ModuleQualifiedName ?? parentModuleDecl?.Name`. On dup-claim the bypass is a no-op
        // and the prior emitter (or fallback MWE) is the source of truth.
        var bypassSourceKey = $"existential-bypass-method::{methodDecl.MangledName}";
        methodDecl.StructuralIdentityKey = bypassSourceKey;
        if (env.EmissionContext != null &&
            !env.EmissionContext.TryClaimWrapperSymbol(swiftModuleQualifiedName, methodDecl.Name,
                bypassSourceKey, wrapperSymbol, DeclIdFactory.ForMethod(methodDecl)))
        {
            return false;
        }

        // --- Emit Swift wrapper ---
        EmitMethodSwiftWrapper(swiftWriter, wrapperSymbol, swiftTypeName, isClass,
            passthroughArgs, existentialArgs, env, isExistentialReturn);

        // --- Emit C# method ---
        EmitMethodCSharpBinding(csWriter, env, typeName, wrapperSymbol, wrapperLibPath,
            isClass, reducedWrapperSig, reducedPInvokeSig, isExistentialReturn);

        return true;
    }

    private static void EmitMethodSwiftWrapper(
        SwiftWriter swiftWriter,
        string wrapperSymbol,
        string swiftTypeName,
        bool isClass,
        List<ArgumentDecl> passthroughArgs,
        List<ArgumentDecl> existentialArgs,
        MethodEnvironment env,
        bool isExistentialReturn)
    {
        var methodDecl = env.MethodDecl;
        var returnArg = methodDecl.CSSignature.First();

        // Build Swift parameter list. For existential returns we write the result into a caller-
        // provided buffer (out-parameter style) — resultPtr comes first, matching what the C#
        // P/Invoke passes in. For void returns we keep the legacy shape (self first, no extra).
        var swiftParams = new List<string>();

        if (isExistentialReturn)
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");

        // Self parameter
        if (isClass)
            swiftParams.Add($"_ __self: {swiftTypeName}");
        else
            swiftParams.Add("_ __self: UnsafeMutableRawPointer");

        // Sibling bindings (the params that get a binding are passthroughArgs) so a reserved-name
        // escape also dodges a sibling user binding. The call loop reuses the same set.
        var siblings = CdeclParamMapper.CollectSiblingBindingNames(passthroughArgs);
        // Bare names in this wrapper's signature resolve against every import, so a bound-module
        // type sharing a name with an imported module's type is ambiguous to swiftc.
        // QualificationPolicy compares against NamedTypeSpec.Module, the raw ABI spelling —
        // never backtick-escaped. UnescapeModuleName produces Swift SOURCE syntax (e.g. the
        // C#-safe "_class" becomes "`class`" for emission into text); comparing that against
        // the raw ABI name would never match for a keyword-named module, silently disabling
        // qualification for it. Compare on the raw name instead.
        var boundModuleName = env.MethodDecl.ModuleDecl?.Name ?? "";
        foreach (var arg in passthroughArgs)
        {
            var swiftType = RenderSwiftTypeSpecForWrapperSignature(arg.SwiftTypeSpec, boundModuleName);
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
            // Keyword rename + sanitize + reserved/sibling escape (canonical helper). The call
            // loop derives the matching arg identically.
            var rawLabel = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var label = CdeclParamMapper.BuildSwiftBindingName(rawLabel, siblings);
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

            // Identical to the param-decl loop (same sibling set + BuildSwiftBindingName) so the
            // value references the same binding.
            var rawLabel = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var privateName = CdeclParamMapper.BuildSwiftBindingName(rawLabel, siblings);
            // Provenance-aware call label (canonical builder) — preserves labels that genuinely
            // begin with '_' (e.g. _self) and backtick-escapes keywords. Label and value (the
            // escaped binding) are independent.
            var argStr = $"{CdeclParamMapper.BuildSwiftCallArgLabel(arg)}{privateName}";
            callArgs.Add(argStr);
        }
        var callArgString = string.Join(", ", callArgs);

        // Propagate parent-type availability so the wrapper compiles on device SDKs
        // where the wrapped type may be gated on a newer OS version.
        var methodAvailability = WrapperEmitterHelpers.MergeAvailability(
            methodDecl.AvailabilityAnnotations, env.ParentDecl);

        swiftWriter.WriteLine();
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, methodAvailability);
        // @_silgen_name keeps Swift CC because the method wrapper takes the parent class as
        // self and may pass non-@objc class types in passthroughArgs — neither is C-representable
        // under @_cdecl. Symbol uses the SBSW_ prefix so PInvokeEmitHelper.SelectCallingConvention
        // routes the matching P/Invoke to CallConvSwift instead of throwing on SBW_+Swift.
        swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
        swiftWriter.WriteLine($"public func {wrapperSymbol}({swiftParamString}) {{");
        swiftWriter.Indent++;

        var methodCallName = NameProvider.ParserNameToSwift(methodDecl);
        if (isExistentialReturn)
        {
            // Render the Swift return type — Swift uses this to lay out the existential container
            // in the caller's buffer so C# can read back matching bytes as ExistentialContainerN.
            // Return position, hence the ReturnType helper (strips @escaping, which return types
            // cannot carry) rather than the signature helper used for the passthrough params above.
            var returnSwiftType = RenderSwiftTypeSpecForWrapperReturnType(returnArg.SwiftTypeSpec, boundModuleName);
            if (!isClass)
            {
                swiftWriter.WriteLine($"let __selfTyped = __self.assumingMemoryBound(to: {swiftTypeName}.self).pointee");
                swiftWriter.WriteLine($"let __result: {returnSwiftType} = __selfTyped.{methodCallName}({callArgString})");
            }
            else
            {
                swiftWriter.WriteLine($"let __result: {returnSwiftType} = __self.{methodCallName}({callArgString})");
            }
            swiftWriter.WriteLine($"resultPtr.initializeMemory(as: ({returnSwiftType}).self, repeating: __result, count: 1)");
        }
        else if (!isClass)
        {
            // Non-frozen struct: dereference pointer to get value, call method.
            // Use 'var' to support mutating methods (though most bypass candidates are non-mutating).
            swiftWriter.WriteLine($"var __selfTyped = __self.assumingMemoryBound(to: {swiftTypeName}.self).pointee");
            swiftWriter.WriteLine($"__selfTyped.{methodCallName}({callArgString})");
            // Write back for mutating methods
            swiftWriter.WriteLine($"__self.assumingMemoryBound(to: {swiftTypeName}.self).pointee = __selfTyped");
        }
        else
        {
            swiftWriter.WriteLine($"__self.{methodCallName}({callArgString})");
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
        Signature reducedPInvokeSig,
        bool isExistentialReturn)
    {
        var methodDecl = env.MethodDecl;
        var accessModifier = NameProvider.GetAccessModifier(methodDecl.IsSynthesizedAccessor);
        var returnArg = methodDecl.CSSignature.First();

        // Resolve return-type info up-front: the existential container drives P/Invoke shape + buffer
        // size, and the public/proxy type drives the public method signature + wrap statement.
        string? containerType = null;
        string? publicReturnType = null;
        string? returnWrapExpr = null;
        string? existentialReadExpr = null;
        bool returnProxySuppressed = false;
        string? suppressedReturnProxyName = null;
        if (isExistentialReturn)
        {
            var protocolList = env.ExistentialHandler.ToProtocolListTypeSpec(returnArg.SwiftTypeSpec)!;
            containerType = env.ExistentialHandler.GetCSharpExistentialType(protocolList);
            publicReturnType = env.ExistentialHandler.GetPublicExistentialType(protocolList);
            // A class-bound (single AnyObject-/superclass-constrained) existential is a compact
            // 2-word [classRef][witnessTable] heap cell (16 bytes), not the 5-word opaque container
            // (40 bytes); reading the wider type pulls uninitialized bytes into the unused container
            // fields. The +1 still transfers via the bitwise copy (the buffer free is a plain
            // dealloc, no VWT Destroy), so the proxy's ownsContainer adoption is unchanged — only
            // the read width differs.
            existentialReadExpr = env.ExistentialHandler.IsClassBoundArity1Existential(protocolList)
                ? "Swift.Runtime.ClassExistentialContainer1.ReadHeapCell(__resultPtr)"
                : $"SwiftMarshal.MarshalFromSwift<{containerType}>(__resultPtr)";
            // Mirror WrapperEmitter.Return.cs: well-known (Swift.Error → AnyError), else proxy.
            // Owned return: the proxy adopts the +1 existential and releases it on Dispose.
            // Both single-protocol (EC1) and composition (EC2+) proxies expose the ownership-aware
            // ctor. Gate on the container type, not the protocol count (ObjC filtering can make
            // those diverge).
            var ownsArg = ExistentialHandler.IsOwnedExistentialContainerType(containerType)
                ? ", ownsContainer: true"
                : string.Empty;
            // Well-known protocol types (Swift.Error → AnyError) are never proxy-suppressed, so
            // resolve them first. Otherwise, if the existential's proxy class is unavailable — name-
            // suppressed (its EveryProtocol conformance was not emitted) or a Self/AT protocol for
            // which no proxy class is ever generated (a constrained `any P<X>` keeps its generic-
            // interface public type, so the object-demotion decline above does not catch it) — there
            // is no type to construct: the bypass adapter SKIPS the method when this TryEmit returns
            // false, so declining here would drop the public member. Instead keep the member and emit
            // a throwing body below (retired-CoGater public-member parity), leaving returnWrapExpr
            // null. (The flags half of the oracle needs no context, so no null-context guard here.)
            if (env.ExistentialHandler.TryGetWellKnownProtocolType(protocolList, out var wellKnown))
            {
                returnWrapExpr = $"new {wellKnown}(__existentialResult{ExistentialHandler.WellKnownOwnedTransferArg(wellKnown)})";
            }
            else if (env.ExistentialHandler.IsProxyReferenceUnavailable(protocolList, env.EmissionContext))
            {
                returnProxySuppressed = true;
                suppressedReturnProxyName = env.ExistentialHandler.GetQualifiedProxyClassName(protocolList);
            }
            else
            {
                returnWrapExpr = $"new {env.ExistentialHandler.GetQualifiedProxyClassName(protocolList)}(__existentialResult{ownsArg})";
            }
        }

        // Build the public method parameter list from the reduced wrapper signature
        var paramString = reducedWrapperSig.ParametersString();

        // P/Invoke params. Existential return adds a leading resultPtr (indirect result buffer) so
        // the P/Invoke itself stays void-return — mirrors the @_cdecl+resultPtr pattern used by
        // full wrappers.
        var pInvokeParamsList = new List<string>();
        if (isExistentialReturn)
            pInvokeParamsList.Add("IntPtr resultPtr");
        pInvokeParamsList.Add("IntPtr self");
        var pInvokePassthroughParams = reducedPInvokeSig.PInvokeParametersString();
        if (!string.IsNullOrEmpty(pInvokePassthroughParams))
            pInvokeParamsList.Add(pInvokePassthroughParams);
        var pInvokeParams = string.Join(", ", pInvokeParamsList);

        // (Wrapper symbol was already claimed via TryClaimWrapperSymbol BEFORE Swift emission;
        //  see the claim-then-emit guard at the top of TryEmitMethodBypass.)

        // Emit P/Invoke declaration. SBSW_ wrappers use @_silgen_name (Swift CC) — set
        // CallingConvention explicitly so PInvokeEmitHelper.SelectCallingConvention's safety
        // check doesn't throw on the default Cdecl spec.
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
                MetadataParameters = null,
                CallingConvention = PInvokeCallingConvention.Swift,
                EmissionContext = env.EmissionContext,
                EnforceWrapperContract = env.EmissionContext != null
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
                ParametersString = pInvokeParams,
                CallingConvention = PInvokeCallingConvention.Swift,
                EmissionContext = env.EmissionContext,
                EnforceWrapperContract = env.EmissionContext != null
            });
            csWriter.WriteLine();
        }

        // Emit under the authoritative C# name. env.CSharpMethodName (IEnvironment.cs) is the single
        // source of truth — it folds in the sibling-property rename (Foo->FooMethod), the parent-type
        // collision guard (CS0542), the B15 collision-suffix (CollisionIndex), AND an adopted ancestor-
        // slot name for collision-suffix overrides. Recomputing here via GetPublicMethodName dropped all
        // of those axes, so the bypass could emit `Foo`/`Process` while IHandler stamped
        // EmittedCSharpName = `FooMethod`/`Process2` and reserved the matching dedup key
        // (IMethodBridgeEmitter.GetProjectedCSharpMethodKey threads all of them) -> CS0111 / CS0102 /
        // wrong-slot override. The old recompute used hasReturnValue: isExistentialReturn, which diverges
        // from CSharpMethodName's non-void rule only when parameterCount == 0 (the sole "Get"-prefix
        // gate); the only param-less bypass methods are existential-return (both compute the same), so
        // env.CSharpMethodName is name-preserving for every existing binding.
        var methodName = env.CSharpMethodName;

        // Pre-compute marshalling to determine if unsafe is needed before emitting the method declaration.
        var (marshalledArgs, setupLines, needsUnsafe) = GetBypassMarshalledCallArguments(reducedWrapperSig, reducedPInvokeSig);

        // Existential-return path allocates a native buffer and passes its pointer — forces unsafe.
        // A suppressed return-proxy emits only a throw (no buffer alloc), so it needs no unsafe.
        if (isExistentialReturn && !returnProxySuppressed)
            needsUnsafe = true;

        // Build call arguments: [resultPtr,] self handle, passthrough args.
        // Classes: _handle IS the Swift object pointer (SwiftClassHandle) — pass directly.
        // Non-frozen structs: _payload buffer IS the struct data — pass directly.
        var callArgsList = new List<string>();
        if (isExistentialReturn)
            callArgsList.Add("__resultPtr");
        var classParentDecl = env.ParentDecl as ClassDecl;
        var selfExpr = classParentDecl != null
            ? (classParentDecl.IsObjCRooted ? "Handle" : "_handle.DangerousGetHandle()")
            : "_payload.DangerousGetHandle()";
        callArgsList.Add(selfExpr);
        var passthroughCallArgs = string.Join(", ", marshalledArgs);
        if (!string.IsNullOrEmpty(passthroughCallArgs))
            callArgsList.Add(passthroughCallArgs);
        var callArgs = string.Join(", ", callArgsList);

        // Emit public method (unsafe needed for stackalloc marshalling or resultPtr alloc).
        var unsafeModifier = needsUnsafe ? "unsafe " : "";
        var returnTypeKeyword = isExistentialReturn ? publicReturnType! : "void";
        // Suppressed return-proxy: this member can only ever throw when called. Poison it at compile time
        // (SB0006) ahead of the signature so callers fail to build, not just at runtime. A synthesized
        // (private) accessor is NOT poisoned here — its delegating public getter carries the marker
        // instead (recorded below), mirroring WrapperEmitter's accessor split.
        if (isExistentialReturn && returnProxySuppressed && !env.MethodDecl.IsSynthesizedAccessor)
        {
            WrapperEmitter.EmitSuppressedProxyReadPoison(csWriter);
        }
        csWriter.WriteLine($"{accessModifier} {unsafeModifier}{returnTypeKeyword} {methodName}({paramString})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Suppressed return-proxy: emit a throwing body and skip the buffer alloc / P/Invoke call.
        // The P/Invoke decl and Swift wrapper stay declared but unused (the suppressed-return gate is
        // C#-only and never touches either), keeping the public member + P/Invoke entry-point set unchanged.
        if (isExistentialReturn && returnProxySuppressed)
        {
            csWriter.WriteLine($"throw new NotSupportedException(\"{WrapperEmitter.ProxySuppressedMessage}\");");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            // Persist the PRODUCE degrade: the existential-return bypass member throws because its
            // {Protocol}Proxy was suppressed. Mirrors WrapperEmitter's full-wrapper produce-throw gate.
            SuppressedProxyReporting.Record(methodDecl, SuppressedProxyReporting.Site.ProduceThrow, suppressedReturnProxyName);
            // A synthesized (private) accessor delegates to from a public getter; flag it so that getter
            // is poisoned (SB0006) rather than this private accessor, keeping the generated delegation valid.
            if (env.MethodDecl.IsSynthesizedAccessor)
            {
                env.EmissionContext?.RecordAccessorProduceThrow(env.MethodDecl);
            }
            return;
        }

        // Emit marshalling setup lines (string conversions)
        foreach (var line in setupLines)
            csWriter.WriteLine(line);

        var wrapperCall = env.PInvokeHelperContext != null
            ? $"{env.PInvokeHelperContext.HelperClassName}.{wrapperSymbol}"
            : wrapperSymbol;

        if (!isExistentialReturn)
        {
            csWriter.WriteLine($"{wrapperCall}({callArgs});");
        }
        else
        {
            // Allocate buffer sized to the existential container metadata, hand its pointer to the
            // Swift wrapper (which writes the container bytes), then marshal back and wrap in a proxy.
            csWriter.WriteLine($"var __returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<{containerType}>();");
            csWriter.WriteLine($"void* __resultBuf = NativeMemory.Alloc((nuint)__returnMetadata.Size);");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"var __resultPtr = (IntPtr)__resultBuf;");
            csWriter.WriteLine($"{wrapperCall}({callArgs});");
            csWriter.WriteLine($"var __existentialResult = {existentialReadExpr};");
            csWriter.WriteLine($"return {returnWrapExpr};");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("NativeMemory.Free(__resultBuf);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

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
        // Sibling bindings (the params that get a binding are passthroughArgs) so a reserved-name
        // escape also dodges a sibling user binding. The call loop reuses the same set.
        var siblings = CdeclParamMapper.CollectSiblingBindingNames(passthroughArgs);
        // Bare names in this wrapper's signature resolve against every import, so a bound-module
        // type sharing a name with an imported module's type is ambiguous to swiftc.
        // QualificationPolicy compares against NamedTypeSpec.Module, the raw ABI spelling —
        // never backtick-escaped. UnescapeModuleName produces Swift SOURCE syntax (e.g. the
        // C#-safe "_class" becomes "`class`" for emission into text); comparing that against
        // the raw ABI name would never match for a keyword-named module, silently disabling
        // qualification for it. Compare on the raw name instead.
        var boundModuleName = env.MethodDecl.ModuleDecl?.Name ?? "";
        foreach (var arg in passthroughArgs)
        {
            var swiftType = RenderSwiftTypeSpecForWrapperSignature(arg.SwiftTypeSpec, boundModuleName);
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
            // Keyword rename + sanitize + reserved/sibling escape (canonical helper). The call
            // loop derives the matching arg identically.
            var rawLabel = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var label = CdeclParamMapper.BuildSwiftBindingName(rawLabel, siblings);
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
            // Identical to the param-decl loop (same sibling set + BuildSwiftBindingName) so the
            // value references the same binding.
            var rawLabel = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            var privateName = CdeclParamMapper.BuildSwiftBindingName(rawLabel, siblings);
            // Provenance-aware call label (canonical builder) — preserves labels that genuinely
            // begin with '_' (e.g. _self) and backtick-escapes keywords. Label and value (the
            // escaped binding) are independent.
            var argStr = $"{CdeclParamMapper.BuildSwiftCallArgLabel(arg)}{privateName}";
            callArgs.Add(argStr);
        }
        var callArgString = string.Join(", ", callArgs);

        // Propagate parent-type availability so the wrapper compiles on device SDKs
        // where the wrapped type may be gated on a newer OS version.
        var availability = WrapperEmitterHelpers.MergeAvailability(
            env.MethodDecl.AvailabilityAnnotations, env.ParentDecl);

        swiftWriter.WriteLine();
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);
        // @_silgen_name keeps Swift CC because passthroughArgs may carry non-@objc class
        // types that swiftc rejects under @_cdecl. Symbol uses the SBSW_ prefix so
        // PInvokeEmitHelper.SelectCallingConvention routes the P/Invoke to CallConvSwift.
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
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);
        // Free wrapper takes UnsafeMutableRawPointer — kept on @_silgen_name to share the
        // SBSW_ Swift-CC convention with the init wrapper above.
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
        var accessModifier = NameProvider.GetAccessModifier(env.MethodDecl.IsSynthesizedAccessor);
        // Public factory uses the wrapper (high-level) signature
        var paramString = reducedWrapperSig.ParametersString();

        // P/Invoke extern declarations use the P/Invoke (low-level) signature
        var pInvokeParams = reducedPInvokeSig.PInvokeParametersString();

        // (Wrapper symbols init+free were already claimed via TryClaimWrapperSymbol BEFORE Swift
        //  emission; see the claim-then-emit guard at the top of TryEmitConstructorBypass.)

        // Emit P/Invoke declarations. SBSW_ wrappers use @_silgen_name (Swift CC) — set
        // CallingConvention explicitly so the helper's safety check doesn't throw on
        // the default Cdecl spec.
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
                MetadataParameters = null,
                CallingConvention = PInvokeCallingConvention.Swift,
                EmissionContext = env.EmissionContext,
                EnforceWrapperContract = env.EmissionContext != null
            });
            env.PInvokeHelperContext.AddDeclaration(new PInvokeDeclaration
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = freeSymbol,
                MethodName = freeSymbol,
                ReturnType = "void",
                ParametersString = "IntPtr ptr",
                IsAsync = false,
                MetadataParameters = null,
                CallingConvention = PInvokeCallingConvention.Swift,
                EmissionContext = env.EmissionContext,
                EnforceWrapperContract = env.EmissionContext != null
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
                ParametersString = pInvokeParams,
                CallingConvention = PInvokeCallingConvention.Swift,
                EmissionContext = env.EmissionContext,
                EnforceWrapperContract = env.EmissionContext != null
            });
            csWriter.WriteLine();
            PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = freeSymbol,
                MethodName = freeSymbol,
                ReturnType = "void",
                ParametersString = "IntPtr ptr",
                CallingConvention = PInvokeCallingConvention.Swift,
                EmissionContext = env.EmissionContext,
                EnforceWrapperContract = env.EmissionContext != null
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

                // Derived locals prepend onto the parameter name, so a keyword-escaped name
                // (@abstract) must drop its verbatim prefix first — `@` is only legal at the
                // START of a C# identifier, and `__@abstractStr` does not compile. Value
                // references keep {wParam.Name} verbatim (the escape is valid there).
                var safeName = NameProvider.StripVerbatimPrefix(pParam.Name);

                if (wrapperType == "string?" && pInvokeType == "IntPtr")
                {
                    // string? → IntPtr (large optional buffer for OptionalPointerWrapper)
                    // Must allocate a buffer, write SwiftOptional<SwiftString> into it, pass the pointer.
                    var bufName = $"__{safeName}Buf";
                    setupLines.Add(
                        $"using var __{safeName}Str = {wParam.Name} is {{}} __{safeName}Val ? new SwiftString(__{safeName}Val) : default;");
                    setupLines.Add(
                        $"var __{safeName}Opt = {wParam.Name} is {{}} ? SwiftOptional<SwiftString>.NewSome(__{safeName}Str) : SwiftOptional<SwiftString>.NewNone();");
                    setupLines.Add(
                        $"var __{safeName}Size = (int)TypeMetadata.GetTypeMetadataOrThrow<SwiftOptional<SwiftString>>().Size;");
                    setupLines.Add(
                        $"byte* {bufName} = stackalloc byte[__{safeName}Size];");
                    setupLines.Add(
                        $"var __{safeName}Span = new Span<byte>({bufName}, __{safeName}Size);");
                    setupLines.Add(
                        $"SwiftMarshal.MarshalToSwift(__{safeName}Opt, ref __{safeName}Span);");
                    callArgs.Add($"new IntPtr({bufName})");
                    needsUnsafe = true;
                }
                else if (wrapperType == "string?")
                {
                    // string? → SwiftOptional<SwiftString> (direct)
                    var varName = $"__{safeName}Opt";
                    setupLines.Add(
                        $"using var __{safeName}Str = {wParam.Name} is {{}} __{safeName}Val ? new SwiftString(__{safeName}Val) : default;");
                    setupLines.Add(
                        $"var {varName} = {wParam.Name} is {{}} ? SwiftOptional<SwiftString>.NewSome(__{safeName}Str) : SwiftOptional<SwiftString>.NewNone();");
                    callArgs.Add(varName);
                }
                else
                {
                    // string → SwiftString → SwiftString.Buffer (P/Invoke takes the blittable Buffer type)
                    var strVar = $"__{safeName}Str";
                    var dispVar = $"__{safeName}Disposable";
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
        => RenderSwiftTypeSpecCore(typeSpec, QualificationPolicy.None);

    /// <summary>
    /// Renders a TypeSpec for use in return type position.
    /// Strips @escaping which is only valid in function parameter position.
    /// </summary>
    public static string RenderSwiftTypeSpecForReturnType(TypeSpec typeSpec)
        => RenderSwiftTypeSpec(typeSpec).Replace("@escaping ", "");

    /// <summary>
    /// Renders a TypeSpec with module-qualified names (e.g. "Module.TypeName" instead of "TypeName").
    /// Use this for .load(as:), .initializeMemory(as:), and .assumingMemoryBound(to:) expressions
    /// where unqualified names can be ambiguous (the wrapper imports the module, and the type name
    /// may collide with types from other imported modules).
    /// </summary>
    public static string RenderModuleQualifiedSwiftTypeSpec(TypeSpec typeSpec)
        => RenderSwiftTypeSpecCore(typeSpec, QualificationPolicy.All);

    /// <summary>
    /// Renders a TypeSpec for a generated wrapper function's own signature, qualifying references
    /// to <paramref name="boundModuleName"/>'s types and leaving every other name bare.
    ///
    /// A bare name in a wrapper signature resolves against everything the wrapper imports, so a
    /// type whose name is also declared by an imported module (the bound module's own `Logger`
    /// vs `os.Logger`, `Expression` vs `Foundation.Expression`) makes swiftc reject the signature
    /// as ambiguous. Qualifying the bound module's types resolves that: the wrapper always imports
    /// the module it binds, so the prefix is guaranteed to name something.
    ///
    /// The narrow scope is the point — qualifying everything is not a spelling change and breaks
    /// three ways. Dependency and Apple imports are filtered down to what the interface gives
    /// evidence for, so `Dep.T` can name a module that was never imported even where bare `T`
    /// resolved through a re-export. Downstream `@_cdecl` lowering classifies primitives by
    /// comparing this rendered string to a bare name, so qualifying `Bool` silently drops it out
    /// of its Int8 ABI arm while still compiling. And a dotted spec is not always module-qualified
    /// (an associated-type reference like `S.Element` splits the same way), so a blanket prefix
    /// changes meaning rather than adding precision. Restricting to the bound module keeps all
    /// three out of reach: its import always exists, its name never matches a stdlib primitive's
    /// module, and a generic parameter is never the module being bound.
    /// </summary>
    public static string RenderSwiftTypeSpecForWrapperSignature(TypeSpec typeSpec, string? boundModuleName)
        => RenderSwiftTypeSpecCore(typeSpec, QualificationPolicy.ForBoundModule(boundModuleName));

    /// <summary>
    /// Wrapper-signature rendering (see <see cref="RenderSwiftTypeSpecForWrapperSignature"/>) for
    /// return position, which cannot carry @escaping.
    /// </summary>
    public static string RenderSwiftTypeSpecForWrapperReturnType(TypeSpec typeSpec, string? boundModuleName)
        => RenderSwiftTypeSpecForWrapperSignature(typeSpec, boundModuleName).Replace("@escaping ", "");

    /// <summary>
    /// Decides, per type reference, whether a render writes the module prefix.
    /// </summary>
    private readonly struct QualificationPolicy
    {
        private readonly bool _qualifyAll;
        private readonly string? _onlyModule;

        private QualificationPolicy(bool qualifyAll, string? onlyModule)
        {
            _qualifyAll = qualifyAll;
            _onlyModule = onlyModule;
        }

        /// <summary>Strip every module prefix.</summary>
        internal static QualificationPolicy None => new(false, null);

        /// <summary>Keep every module prefix the spec carries.</summary>
        internal static QualificationPolicy All => new(true, null);

        /// <summary>
        /// Keep the prefix only on types belonging to <paramref name="moduleName"/>. A null or
        /// empty name degrades to <see cref="None"/> rather than qualifying nothing-in-particular.
        /// </summary>
        internal static QualificationPolicy ForBoundModule(string? moduleName)
            => string.IsNullOrEmpty(moduleName) ? None : new(false, moduleName);

        /// <summary>True when this reference is rendered with its module prefix.</summary>
        internal bool Qualifies(NamedTypeSpec spec)
            => _qualifyAll || (_onlyModule is not null && spec.Module == _onlyModule);
    }

    private static string RenderSwiftTypeSpecCore(TypeSpec typeSpec, QualificationPolicy policy)
    {
        switch (typeSpec)
        {
            case NamedTypeSpec namedTypeSpec:
                var qualified = policy.Qualifies(namedTypeSpec);
                var name = qualified ? namedTypeSpec.Name : namedTypeSpec.NameWithoutModule;
                // Rewrite SPI (underscore-prefixed) module prefixes to their public counterpart
                // so the generated Swift doesn't reference types through a non-public module
                // (e.g. "_LocationEssentials.CLLocation" → "CoreLocation.CLLocation").
                if (qualified)
                    name = AppleFrameworkRegistry.RewriteSpiModulePrefix(name);

                var rendered = namedTypeSpec.GenericParameters.Count > 0
                    ? $"{name}<{string.Join(", ", namedTypeSpec.GenericParameters.Select(gp => RenderSwiftTypeSpecCore(gp, policy)))}>"
                    : name;

                // Nested types: TypeSpecParser populates InnerType for dotted names
                // (e.g. StreamOf<E>.Iterator). Render ".{Inner}" recursively — each
                // nesting level carries only its own generic parameters (StreamOf<E>.Iterator,
                // never StreamOf<E>.Iterator<E>). Inner segments' own names are emitted
                // unqualified (outer already carries the module prefix), but their generic
                // arguments must still follow the caller's qualification preference so that
                // e.g. Outer.Inner<Swift.Int> keeps the Swift.Int qualification when requested.
                if (namedTypeSpec.InnerType is not null)
                {
                    var innerRendered = RenderInnerNamedTypeSpec(namedTypeSpec.InnerType, policy);
                    rendered = $"{rendered}.{innerRendered}";
                }
                return rendered;

            case TupleTypeSpec tupleTypeSpec:
                if (tupleTypeSpec.IsEmptyTuple)
                    return "Void";
                var elements = string.Join(", ", tupleTypeSpec.Elements.Select(e =>
                {
                    var rendered = RenderSwiftTypeSpecCore(e, policy);
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
                        var rendered = RenderSwiftTypeSpecCore(e, policy);
                        var inoutPrefix = e.IsInOut ? "inout " : "";
                        var labeled = !string.IsNullOrEmpty(e.TypeLabel) ? $"{e.TypeLabel}: {inoutPrefix}{rendered}" : $"{inoutPrefix}{rendered}";
                        return labeled;
                    }));
                    argsRendered = $"({elems})";
                }
                else
                {
                    var singleInout = closureTypeSpec.Arguments.IsInOut ? "inout " : "";
                    argsRendered = $"({singleInout}{RenderSwiftTypeSpecCore(closureTypeSpec.Arguments, policy)})";
                }
                var ret = RenderSwiftTypeSpecCore(closureTypeSpec.ReturnType, policy);
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
                    p => RenderSwiftTypeSpecCore(p, policy)));
                return $"any {protocols}";

            default:
                return typeSpec.ToString();
        }
    }

    /// <summary>
    /// Renders a nested inner segment: its name is always unqualified (outer already
    /// carries the module prefix), but its own generic arguments and further-nested
    /// inner segments follow the caller's <paramref name="policy"/>
    /// so that e.g. Outer.Inner&lt;Swift.Int&gt; keeps Swift.Int qualification when requested.
    /// </summary>
    private static string RenderInnerNamedTypeSpec(TypeSpec innerSpec, QualificationPolicy policy)
    {
        if (innerSpec is NamedTypeSpec nts)
        {
            var name = nts.NameWithoutModule;
            var rendered = nts.GenericParameters.Count > 0
                ? $"{name}<{string.Join(", ", nts.GenericParameters.Select(gp => RenderSwiftTypeSpecCore(gp, policy)))}>"
                : name;
            if (nts.InnerType is not null)
                rendered = $"{rendered}.{RenderInnerNamedTypeSpec(nts.InnerType, policy)}";
            return rendered;
        }
        return RenderSwiftTypeSpecCore(innerSpec, policy);
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
            IsSynthesizedAccessor = method.IsSynthesizedAccessor
        };
    }
}
