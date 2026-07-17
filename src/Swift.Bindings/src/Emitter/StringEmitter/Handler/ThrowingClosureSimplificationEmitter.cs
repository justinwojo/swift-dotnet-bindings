// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits simplified convenience overloads for methods with throwing closure parameters.
/// Transforms <c>Func&lt;..., SwiftResult&lt;T, SwiftError&gt;&gt;</c> parameters into
/// <c>Action&lt;...&gt;</c> (void throws) or <c>Func&lt;..., T&gt;</c> (non-void throws).
/// The generated overload wraps the simplified delegate in a try/catch that converts
/// <see cref="Swift.SwiftErrorException"/> to <c>SwiftResult.FromFailure</c>.
/// </summary>
internal static class ThrowingClosureSimplificationEmitter
{
    /// <summary>
    /// Pre-scan: checks whether a method has eligible throwing closure parameters
    /// that will produce a simplified overload. Includes dedup check (Contains, not Add)
    /// to avoid hiding the original when the overload would be blocked by a collision.
    /// Called before WrapperEmitter so the original method can be annotated with
    /// [EditorBrowsable(Never)].
    /// </summary>
    public static bool ShouldSimplify(MethodEnvironment methodEnv)
    {
        if (!PassesGates(methodEnv.MethodDecl))
            return false;

        var throwingClosures = CollectThrowingClosures(methodEnv.MethodDecl.CSSignature);
        if (throwingClosures.Count == 0)
            return false;

        // Check dedup without adding — if key already exists, TryEmitOverload will bail
        if (methodEnv.EmittedProjectedSignatures != null)
        {
            var overloadKey = BuildOverloadKey(methodEnv, throwingClosures);
            if (methodEnv.EmittedProjectedSignatures.Contains(overloadKey))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Tries to emit a simplified overload for a method with throwing closure parameters.
    /// </summary>
    public static void TryEmitOverload(CSharpWriter csWriter, MethodEnvironment methodEnv)
    {
        if (!PassesGates(methodEnv.MethodDecl))
            return;

        var throwingClosures = CollectThrowingClosures(methodEnv.MethodDecl.CSSignature);
        if (throwingClosures.Count == 0)
            return;

        // Dedup: check if this overload signature already exists
        if (methodEnv.EmittedProjectedSignatures != null)
        {
            var overloadKey = BuildOverloadKey(methodEnv, throwingClosures);
            if (!methodEnv.EmittedProjectedSignatures.Add(overloadKey))
                return;
        }

        var csSignature = methodEnv.MethodDecl.CSSignature;

        // Determine method return type. When the return passes straight through, take it from the
        // SAME oracle the primary emits from — this overload's body is a call to the primary, so a
        // second, weaker derivation can only agree or produce a compile error. (The SwiftResult
        // case below is different in kind: there the overload deliberately returns the unwrapped
        // success type, which is not the primary's return type at all.)
        var returnTypeSpec = csSignature[0].SwiftTypeSpec;
        bool hasMethodReturn = !returnTypeSpec.IsEmptyTuple;
        string returnType = hasMethodReturn
            ? new SignatureHandler(methodEnv).GetWrapperSignature().ReturnType
            : "void";

        // If even the primary could not project the return, there is no real type to borrow;
        // the simplified overload is convenience sugar, so drop it rather than fabricate a
        // placeholder spelling that cannot compile. This deliberately does NOT cover the
        // SwiftResult shape below, whose emitted return is the unwrapped success type rather
        // than the primary's return — that path bails on its own when it cannot determine the
        // success type.
        if (hasMethodReturn && !IsSwiftResultReturnType(returnTypeSpec) &&
            returnType.Contains(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName))
            return;

        // Check if the method return is a SwiftResult (throws with non-void return)
        // In this case the overload unwraps the result and throws on failure
        bool methodReturnIsSwiftResult = IsSwiftResultReturnType(returnTypeSpec);
        string? unwrappedReturnType = null;
        if (methodReturnIsSwiftResult)
        {
            unwrappedReturnType = ExtractSwiftResultSuccessType(returnTypeSpec, methodEnv);
            if (unwrappedReturnType == null)
                return; // Can't determine the success type
        }

        var methodName = methodEnv.CSharpMethodName;
        var isStatic = methodEnv.MethodDecl.MethodType == MethodType.Static;
        var staticModifier = isStatic ? "static " : "";

        // Build parameter list and wrapper body
        var paramParts = new List<string>();
        var wrapperSetup = new List<string>();
        var callArgs = new List<string>();
        var closureHandler = methodEnv.ClosureHandler;

        for (int i = 1; i < csSignature.Count; i++)
        {
            var arg = csSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;

            var paramName = NameProvider.GetCSharpParameterName(arg);
            var throwingClosure = throwingClosures.Find(tc => tc.Index == i);

            if (throwingClosure != default)
            {
                // Simplified parameter type — use projected types (IEnumerable<T> not SwiftSet<T>)
                var simplifiedType = GetSimplifiedDelegateType(throwingClosure.ClosureSpec, closureHandler, methodEnv);
                paramParts.Add($"{simplifiedType} {paramName}");

                // Wrapper delegate that converts simplified to SwiftResult-returning.
                // Must use projected types to match the original method's signature
                // (WrapperSignatureBuilder projects closure args via TypeProjectionFactory).
                var originalType = GetProjectedDelegateType(throwingClosure.ClosureSpec, methodEnv)
                    ?? closureHandler.GetCSharpDelegateType(throwingClosure.ClosureSpec);
                var wrapperName = $"_wrapped_{paramName}";
                var wrapperBody = BuildWrapperLambda(throwingClosure, closureHandler, paramName, methodEnv);
                wrapperSetup.Add($"{originalType} {wrapperName} = {wrapperBody};");
                callArgs.Add(wrapperName);
            }
            else
            {
                var typeName = NativeIntOverloadEmitter.ResolveType(arg.SwiftTypeSpec, methodEnv, isParameter: true);
                paramParts.Add($"{typeName} {paramName}");
                callArgs.Add(paramName);
            }
        }

        var paramStr = string.Join(", ", paramParts);
        var argsStr = string.Join(", ", callArgs);

        // Determine the effective return type for the overload
        string effectiveReturnType;
        if (methodReturnIsSwiftResult && unwrappedReturnType != null)
        {
            if (unwrappedReturnType == "Swift.SwiftVoid")
                effectiveReturnType = "void";
            else
                effectiveReturnType = unwrappedReturnType;
        }
        else
        {
            effectiveReturnType = returnType;
        }

        // Inherit availability attributes from the primary throwing method — without
        // these, CA1416 flags the simplified Action/Func forwarder as reachable on
        // OS versions lower than the gated target it delegates to.
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(
            csWriter, methodEnv.MethodDecl, methodEnv.MethodDecl.ParentDecl, emitObsolete: false);

        // Emit the overload
        csWriter.WriteLine($"public {staticModifier}{effectiveReturnType} {methodName}({paramStr})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Emit wrapper setup
        foreach (var setup in wrapperSetup)
        {
            csWriter.WriteLine(setup);
        }

        // Emit the call, with result unwrapping if needed
        if (methodReturnIsSwiftResult && unwrappedReturnType != null)
        {
            if (unwrappedReturnType == "Swift.SwiftVoid")
            {
                csWriter.WriteLine($"var _result = {methodName}({argsStr});");
                csWriter.WriteLine("if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);");
            }
            else
            {
                csWriter.WriteLine($"var _result = {methodName}({argsStr});");
                csWriter.WriteLine("if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);");
                csWriter.WriteLine("return _result.Success;");
            }
        }
        else if (hasMethodReturn)
        {
            csWriter.WriteLine($"return {methodName}({argsStr});");
        }
        else
        {
            csWriter.WriteLine($"{methodName}({argsStr});");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    /// <summary>
    /// Shared gate checks for both ShouldSimplify and TryEmitOverload.
    /// </summary>
    private static bool PassesGates(MethodDecl methodDecl)
    {
        if (methodDecl.IsConstructor || methodDecl.IsAccessor || methodDecl.IsAsync)
            return false;
        if (methodDecl.IsMissingExportedSymbol)
            return false;
        var parentGenericCount = (methodDecl.ParentDecl as TypeDecl)?.GenericParameters?.Count ?? 0;
        var methodGenericCount = methodDecl.GenericParameters?.Count ?? 0;
        if (methodGenericCount > parentGenericCount)
            return false;
        if (methodDecl.CSSignature.Count < 2)
            return false;
        return true;
    }

    /// <summary>
    /// Collects throwing closure parameters from a method's CSSignature.
    /// Shared between ShouldSimplify and TryEmitOverload.
    /// </summary>
    private static List<ThrowingClosureInfo> CollectThrowingClosures(List<ArgumentDecl> csSignature)
    {
        var throwingClosures = new List<ThrowingClosureInfo>();
        for (int i = 1; i < csSignature.Count; i++)
        {
            var arg = csSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            var closureSpec = ExtractThrowingClosureSpec(arg.SwiftTypeSpec);
            if (closureSpec != null)
            {
                throwingClosures.Add(new ThrowingClosureInfo(
                    Index: i,
                    ClosureSpec: closureSpec,
                    HasReturn: !closureSpec.ReturnType.IsEmptyTuple));
            }
        }
        return throwingClosures;
    }

    /// <summary>
    /// Extracts a ClosureTypeSpec from a parameter type, handling Optional wrapping.
    /// Returns null if the parameter is not a (optionally wrapped) throwing closure.
    /// </summary>
    private static ClosureTypeSpec? ExtractThrowingClosureSpec(TypeSpec typeSpec)
    {
        // Direct closure
        if (typeSpec is ClosureTypeSpec closure && closure.Throws && !closure.IsAsync)
            return closure;

        // Optional<Closure>
        if (typeSpec is NamedTypeSpec optSpec &&
            optSpec.Name == "Swift.Optional" &&
            optSpec.GenericParameters.Count == 1 &&
            optSpec.GenericParameters[0] is ClosureTypeSpec optClosure &&
            optClosure.Throws && !optClosure.IsAsync)
        {
            return optClosure;
        }

        return null;
    }

    /// <summary>
    /// Gets the simplified delegate type for a throwing closure.
    /// void throws → Action; T throws → Func&lt;..., T&gt;
    /// </summary>
    private static string GetSimplifiedDelegateType(ClosureTypeSpec closureSpec, ClosureHandler closureHandler, MethodEnvironment? methodEnv = null)
    {
        var argTypes = new List<string>();
        foreach (var arg in closureSpec.EachArgument())
        {
            // Use projected types (e.g., IEnumerable<string> not SwiftSet<string>)
            // to match what WrapperSignatureBuilder emits for the original method.
            if (methodEnv != null)
                argTypes.Add(NativeIntOverloadEmitter.ResolveType(arg, methodEnv, isParameter: true));
            else
                argTypes.Add(closureHandler.TranslateTypeSpecToCSharp(arg));
        }

        bool hasReturn = !closureSpec.ReturnType.IsEmptyTuple;
        if (hasReturn)
        {
            var returnType = methodEnv != null
                ? NativeIntOverloadEmitter.ResolveType(closureSpec.ReturnType, methodEnv, isParameter: false)
                : closureHandler.TranslateTypeSpecToCSharp(closureSpec.ReturnType, isReturnType: true);
            if (argTypes.Count == 0)
                return $"Func<{returnType}>";
            return $"Func<{string.Join(", ", argTypes)}, {returnType}>";
        }
        else
        {
            if (argTypes.Count == 0)
                return "Action";
            return $"Action<{string.Join(", ", argTypes)}>";
        }
    }

    /// <summary>
    /// Gets the projected delegate type for a throwing closure, matching the type used
    /// in the original method's signature (via TypeProjectionFactory).
    /// Returns null if projection fails (falls back to raw type).
    /// </summary>
    private static string? GetProjectedDelegateType(ClosureTypeSpec closureSpec, MethodEnvironment methodEnv)
    {
        var factory = new TypeProjectionFactory();
        var projection = factory.Project(closureSpec, new ProjectionContext
        {
            TypeDatabase = methodEnv.TypeDatabase,
            IsParameter = true
        });
        return projection?.PublicType;
    }

    /// <summary>
    /// Builds the wrapper lambda that converts a simplified delegate to a SwiftResult-returning delegate.
    /// </summary>
    private static string BuildWrapperLambda(ThrowingClosureInfo info, ClosureHandler closureHandler, string paramName, MethodEnvironment? methodEnv = null)
    {
        var closureSpec = info.ClosureSpec;
        var argNames = new List<string>();
        int argIndex = 0;
        foreach (var _ in closureSpec.EachArgument())
        {
            argNames.Add($"_a{argIndex}");
            argIndex++;
        }

        var argList = argNames.Count > 0 ? string.Join(", ", argNames) : "";
        var callArgs = argList;

        // Determine the success type — must use projected types to match the delegate declaration.
        // The wrapper lambda's return type (SwiftResult<T, SwiftError>) must agree with the
        // original method's projected delegate type from GetProjectedDelegateType.
        string successType = info.HasReturn
            ? (methodEnv != null
                ? NativeIntOverloadEmitter.ResolveType(closureSpec.ReturnType, methodEnv, isParameter: false)
                : closureHandler.TranslateTypeSpecToCSharp(closureSpec.ReturnType, isReturnType: true))
            : "Swift.SwiftVoid";
        var resultType = $"Swift.SwiftResult<{successType}, SwiftError>";

        // Build the lambda
        var sb = new System.Text.StringBuilder();
        sb.Append($"({argList}) => {{ ");
        sb.Append("try { ");

        if (info.HasReturn)
        {
            sb.Append($"return {resultType}.FromSuccess({paramName}({callArgs})); ");
        }
        else
        {
            sb.Append($"{paramName}({callArgs}); ");
            sb.Append($"return {resultType}.FromSuccess(Swift.SwiftVoid.Value); ");
        }

        sb.Append("} catch (Swift.SwiftErrorException _ex) { ");
        sb.Append($"return {resultType}.FromFailure(_ex.Error); ");
        sb.Append("} }");

        return sb.ToString();
    }

    /// <summary>
    /// Checks if a return type is a SwiftResult with SwiftError as the failure type.
    /// Only activates the unwrap-and-throw path when the failure type is known to be SwiftError,
    /// preventing type mismatches for Result types with custom error types.
    /// </summary>
    private static bool IsSwiftResultReturnType(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec ns)
            return false;
        if (ns.Name != "Swift.Result" && !ns.Name.EndsWith(".SwiftResult"))
            return false;
        // Require exactly 2 generic params and verify failure type is SwiftError
        if (ns.GenericParameters.Count < 2)
            return false;
        var failureType = ns.GenericParameters[1];
        return failureType is NamedTypeSpec failureNs &&
               (failureNs.Name == "Swift.Error" || failureNs.Name == "SwiftError" ||
                failureNs.Name.EndsWith(".SwiftError"));
    }

    /// <summary>
    /// Extracts the success type from a SwiftResult return type.
    /// </summary>
    private static string? ExtractSwiftResultSuccessType(TypeSpec typeSpec, MethodEnvironment methodEnv)
    {
        if (typeSpec is NamedTypeSpec ns && ns.GenericParameters.Count >= 1)
        {
            return NativeIntOverloadEmitter.ResolveType(ns.GenericParameters[0], methodEnv, isParameter: false);
        }
        return null;
    }

    /// <summary>
    /// Builds a dedup key for the simplified overload.
    /// </summary>
    private static string BuildOverloadKey(MethodEnvironment methodEnv, List<ThrowingClosureInfo> throwingClosures)
    {
        var methodDecl = methodEnv.MethodDecl;
        var methodName = methodEnv.CSharpMethodName;
        var closureHandler = methodEnv.ClosureHandler;
        var visibleGenericNames = BaseHandler.CollectVisibleGenericParamNames(methodDecl);

        var paramTypes = new List<string>();
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            var arg = methodDecl.CSSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;

            var throwingClosure = throwingClosures.Find(tc => tc.Index == i);
            if (throwingClosure != default)
            {
                paramTypes.Add(GetSimplifiedDelegateType(throwingClosure.ClosureSpec, closureHandler, methodEnv));
            }
            else
            {
                var typeSpecForKey = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(
                    arg.SwiftTypeSpec, methodEnv.TypeDatabase, visibleGenericNames);
                var paramType = NativeIntOverloadEmitter.ResolveType(typeSpecForKey, methodEnv, isParameter: true);
                paramType = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(paramType, arg.SwiftTypeSpec, methodEnv.TypeDatabase);
                paramTypes.Add(paramType);
            }
        }

        return $"{methodName}({string.Join(",", paramTypes)})";
    }

    /// <summary>
    /// Info about a throwing closure parameter to be simplified.
    /// </summary>
    private record struct ThrowingClosureInfo(int Index, ClosureTypeSpec ClosureSpec, bool HasReturn);
}
