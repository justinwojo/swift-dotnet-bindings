// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Generates Swift wrapper functions that accept Array parameters in place of ArraySlice,
/// converting at the call site with ArraySlice(param). This allows methods blocked by
/// ArraySlice (which has no TypeDatabase registration) to be emitted with Array-based
/// signatures that the existing marshalling pipeline handles.
/// </summary>
public static class ArraySliceNormalizationEmitter
{
    /// <summary>
    /// Returns true if the given NamedTypeSpec represents Swift.ArraySlice.
    /// </summary>
    public static bool IsArraySlice(NamedTypeSpec namedTypeSpec)
    {
        return namedTypeSpec.Name == "Swift.ArraySlice";
    }

    /// <summary>
    /// Returns true if the TypeSpec directly contains ArraySlice at the top level
    /// (NamedTypeSpec name or generic parameters). Returns false for ArraySlice
    /// nested inside closures, tuples, or optionals (scope boundary).
    /// </summary>
    public static bool ContainsArraySlice(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec namedTypeSpec)
        {
            if (IsArraySlice(namedTypeSpec))
                return true;

            // Check if this is Optional wrapping ArraySlice — scope boundary, return false
            if (namedTypeSpec.Name == "Swift.Optional")
                return false;

            // Check generic parameters (e.g., Array<ArraySlice<T>> — though unlikely)
            foreach (var genericParam in namedTypeSpec.GenericParameters)
            {
                if (ContainsArraySlice(genericParam))
                    return true;
            }
        }

        // Closures, tuples — scope boundary, return false
        // (ClosureTypeSpec, TupleTypeSpec, ProtocolListTypeSpec all return false)
        return false;
    }

    /// <summary>
    /// Returns true if any parameter (not return type) in the method signature
    /// contains ArraySlice.
    /// </summary>
    public static bool HasArraySliceInSignature(MethodDecl methodDecl)
    {
        // CSSignature[0] is return type; parameters start at index 1
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            if (ContainsArraySlice(methodDecl.CSSignature[i].SwiftTypeSpec))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if the method has an unsupported shape for ArraySlice normalization.
    /// Returns true (with reason) if normalization should be skipped.
    /// </summary>
    private static bool IsUnsupportedShape(MethodDecl methodDecl, ILogger logger, out string reason)
    {
        if (methodDecl.IsAccessor)
        {
            reason = "property accessor";
            return true;
        }

        if (methodDecl.IsConstructor)
        {
            reason = "constructor";
            return true;
        }

        if (methodDecl.IsMutating && methodDecl.ParentDecl is StructDecl)
        {
            reason = "mutating method on value type";
            return true;
        }

        if (methodDecl.IsGeneric)
        {
            reason = "generic method";
            return true;
        }

        // Check for inout ArraySlice params
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            var arg = methodDecl.CSSignature[i];
            if (arg.IsInOut && ContainsArraySlice(arg.SwiftTypeSpec))
            {
                reason = "inout ArraySlice parameter";
                return true;
            }
        }

        // Check for ArraySlice inside closures or tuples in params
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            var arg = methodDecl.CSSignature[i];
            if (ContainsArraySliceInUnsupportedContext(arg.SwiftTypeSpec))
            {
                reason = "ArraySlice inside closure/tuple/optional";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns true if ArraySlice appears inside a closure, tuple, or optional — contexts
    /// where normalization is not supported.
    /// </summary>
    private static bool ContainsArraySliceInUnsupportedContext(TypeSpec typeSpec)
    {
        if (typeSpec is ClosureTypeSpec closureTypeSpec)
        {
            return ContainsArraySliceDeep(closureTypeSpec.Arguments) ||
                   ContainsArraySliceDeep(closureTypeSpec.ReturnType);
        }

        if (typeSpec is TupleTypeSpec tupleTypeSpec)
        {
            foreach (var element in tupleTypeSpec.Elements)
            {
                if (ContainsArraySliceDeep(element))
                    return true;
            }
            return false;
        }

        if (typeSpec is NamedTypeSpec namedTypeSpec)
        {
            // Optional wrapping ArraySlice
            if (namedTypeSpec.Name == "Swift.Optional")
            {
                foreach (var genericParam in namedTypeSpec.GenericParameters)
                {
                    if (ContainsArraySliceDeep(genericParam))
                        return true;
                }
                return false;
            }

            // Recurse into other NamedTypeSpec generic params
            foreach (var genericParam in namedTypeSpec.GenericParameters)
            {
                if (ContainsArraySliceInUnsupportedContext(genericParam))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deep search for ArraySlice anywhere in a TypeSpec tree (for unsupported context detection).
    /// </summary>
    private static bool ContainsArraySliceDeep(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec namedTypeSpec)
        {
            if (IsArraySlice(namedTypeSpec))
                return true;
            foreach (var genericParam in namedTypeSpec.GenericParameters)
            {
                if (ContainsArraySliceDeep(genericParam))
                    return true;
            }
        }
        else if (typeSpec is TupleTypeSpec tupleTypeSpec)
        {
            foreach (var element in tupleTypeSpec.Elements)
            {
                if (ContainsArraySliceDeep(element))
                    return true;
            }
        }
        else if (typeSpec is ClosureTypeSpec closureTypeSpec)
        {
            return ContainsArraySliceDeep(closureTypeSpec.Arguments) ||
                   ContainsArraySliceDeep(closureTypeSpec.ReturnType);
        }
        return false;
    }

    /// <summary>
    /// Returns true if any parameter contains ArraySlice in an unsupported context
    /// (inside Optional, Closure, or Tuple) — these are not normalizable but should be logged.
    /// </summary>
    private static bool HasArraySliceInUnsupportedContext(MethodDecl methodDecl)
    {
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            if (ContainsArraySliceInUnsupportedContext(methodDecl.CSSignature[i].SwiftTypeSpec))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Attempts to emit a normalized method with ArraySlice parameters replaced by Array.
    /// Emits a Swift wrapper that converts Array→ArraySlice at the call site, and delegates
    /// C# emission to the normal WrapperEmitter + PInvokeEmitter pipeline.
    /// </summary>
    /// <returns>true if normalization was emitted; false to fall through to normal handling.</returns>
    public static bool TryEmitNormalizedMethod(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ILogger logger)
    {
        var methodDecl = env.MethodDecl;

        if (!HasArraySliceInSignature(methodDecl))
        {
            // Log if ArraySlice is present but only in unsupported contexts
            if (HasArraySliceInUnsupportedContext(methodDecl))
            {
                logger.LogDebug("ArraySliceNormalization: skipping {Name} — ArraySlice only in unsupported context (Optional/Closure/Tuple)", methodDecl.Name);
            }
            return false;
        }

        if (IsUnsupportedShape(methodDecl, logger, out var reason))
        {
            logger.LogDebug("ArraySliceNormalization: skipping {Name} — {Reason}", methodDecl.Name, reason);
            return false;
        }

        // Bug #17: Skip @usableFromInline internal methods — the generated Swift wrapper
        // would call an inaccessible member from an extension in a different module.
        if (methodDecl.IsModuleInternal)
        {
            logger.LogDebug("ArraySliceNormalization: skipping {Name} — method is module-internal", methodDecl.Name);
            return false;
        }

        // Bug #15: Skip methods on types that aren't accessible from external code.
        // Internal types with @usableFromInline (e.g., BlockEncryptor, StreamEncryptor)
        // appear in the ABI but can't be extended from a separate module's wrapper file.
        if (methodDecl.ParentDecl is TypeDecl parentTypeDecl &&
            (parentTypeDecl.IsModuleInternal ||
             !env.TypeDatabase.TryGetTypeRecord(parentTypeDecl.SwiftTypeName, out _)))
        {
            logger.LogDebug("ArraySliceNormalization: skipping {Name} — parent type {Type} is module-internal or has no TypeRecord", methodDecl.Name, parentTypeDecl.SwiftTypeName.ModuleQualifiedName);
            return false;
        }

        // Build normalized MethodDecl with ArraySlice → Array replacement
        var normalizedMethodDecl = NormalizeMethodDecl(methodDecl);

        // Note: HasClosureCdeclWrapper is NOT set on cloned normalized decls.
        // ArraySlice wrappers use @_silgen_name to intercept the original Swift symbol,
        // which forces the function type to match the original ABI. Only standalone
        // closure wrappers (emitted at MethodHandler level) can use Cdecl params.

        // Create environment with normalized decl
        var normalizedEnv = new MethodEnvironment(
            normalizedMethodDecl,
            env.TypeDatabase,
            env.SiblingPropertyNames,
            env.PInvokeHelperContext);

        // Check if the normalized signature is fully marshallable
        var signatureHandler = new SignatureHandler(normalizedEnv);
        if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
        {
            logger.LogDebug("ArraySliceNormalization: skipping {Name} — normalized signature still contains placeholder", methodDecl.Name);
            return false;
        }

        // Emit Swift wrapper
        EmitSwiftWrapper(swiftWriter, methodDecl, normalizedMethodDecl, env);

        // Delegate C# emission to normal pipeline
        TypeDatabaseExtensions.AnyTypeFallbackInfo? fallbackInfo = null;
        foreach (var argument in normalizedMethodDecl.CSSignature)
        {
            if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(env.TypeDatabase, env.ClosureHandler, argument.SwiftTypeSpec, out var foundFallbackInfo))
            {
                fallbackInfo = foundFallbackInfo;
                break;
            }
        }

        var wrapperEmitter = new WrapperEmitter(normalizedEnv, signatureHandler, fallbackInfo);
        wrapperEmitter.EmitMethod(csWriter, swiftWriter);
        PInvokeEmitter.EmitPInvoke(csWriter, normalizedEnv, signatureHandler);

        return true;
    }

    /// <summary>
    /// Creates a new MethodDecl with ArraySlice parameters replaced by Array.
    /// Deep-copies CSSignature to avoid mutating the original.
    /// </summary>
    internal static MethodDecl NormalizeMethodDecl(MethodDecl original)
    {
        var wrapperSymbol = BuildWrapperSymbol(original);

        var normalized = new MethodDecl
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

        // Deep-copy arguments, normalizing ArraySlice → Array where applicable
        foreach (var arg in original.CSSignature)
        {
            var normalizedTypeSpec = ContainsArraySlice(arg.SwiftTypeSpec)
                ? NormalizeTypeSpec(arg.SwiftTypeSpec)
                : arg.SwiftTypeSpec;

            normalized.CSSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = normalizedTypeSpec,
                Name = arg.Name,
                PrivateName = arg.PrivateName,
                IsInOut = arg.IsInOut,
                IsGeneric = arg.IsGeneric,
                HasDefaultArg = arg.HasDefaultArg,
                ParentDecl = normalized,
                ModuleDecl = arg.ModuleDecl
            });
        }

        return normalized;
    }

    /// <summary>
    /// Replaces Swift.ArraySlice with Swift.Array in a TypeSpec, preserving generic parameters.
    /// Only handles direct NamedTypeSpec replacement (scope guards ensure no closures/tuples/optionals).
    /// </summary>
    internal static TypeSpec NormalizeTypeSpec(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec namedTypeSpec)
        {
            if (IsArraySlice(namedTypeSpec))
            {
                var normalized = new NamedTypeSpec("Swift.Array");
                foreach (var genericParam in namedTypeSpec.GenericParameters)
                {
                    normalized.GenericParameters.Add(NormalizeTypeSpec(genericParam));
                }
                CopyTypeSpecMetadata(namedTypeSpec, normalized);
                return normalized;
            }

            // Recurse into generic parameters of non-ArraySlice named types
            bool anyChanged = false;
            var newGenericParams = new List<TypeSpec>();
            foreach (var genericParam in namedTypeSpec.GenericParameters)
            {
                var normalizedParam = NormalizeTypeSpec(genericParam);
                newGenericParams.Add(normalizedParam);
                if (!ReferenceEquals(normalizedParam, genericParam))
                    anyChanged = true;
            }

            if (anyChanged)
            {
                var result = new NamedTypeSpec(namedTypeSpec.Name);
                result.GenericParameters.AddRange(newGenericParams);
                CopyTypeSpecMetadata(namedTypeSpec, result);
                result.InnerType = namedTypeSpec.InnerType;
                return result;
            }
        }

        // Non-NamedTypeSpec or no changes needed — return as-is
        return typeSpec;
    }

    /// <summary>
    /// Copies TypeSpec metadata (Attributes, TypeLabel, IsInOut, IsAny, IsVariadic)
    /// from the original to a newly created TypeSpec. Follows the same pattern as
    /// TypeSpec.ReplaceName().
    /// </summary>
    private static void CopyTypeSpecMetadata(TypeSpec source, TypeSpec target)
    {
        target.Attributes.AddRange(source.Attributes);
        target.TypeLabel = source.TypeLabel;
        target.IsInOut = source.IsInOut;
        target.IsAny = source.IsAny;
        target.IsVariadic = source.IsVariadic;
    }

    /// <summary>
    /// Emits a Swift extension method that wraps the original ArraySlice method,
    /// accepting Array parameters and converting to ArraySlice at the call site.
    /// </summary>
    private static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodDecl originalMethodDecl,
        MethodDecl normalizedMethodDecl,
        MethodEnvironment env)
    {
        var wrapperSymbol = normalizedMethodDecl.MangledName;
        var parentTypeDecl = originalMethodDecl.ParentDecl as TypeDecl;
        bool isFreeFunction = parentTypeDecl == null;

        // Build parameter list for the wrapper function
        var swiftParams = new List<string>();
        var derefLines = new List<string>();
        var originalArgs = originalMethodDecl.CSSignature.Skip(1).ToList();
        var normalizedArgs = normalizedMethodDecl.CSSignature.Skip(1).ToList();
        for (int i = 0; i < normalizedArgs.Count; i++)
        {
            var arg = normalizedArgs[i];
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
                swiftParams.Add($"_ {label}: {swiftType}");
            }
        }

        // Check if the return type is a large Optional that needs an out-buffer
        bool hasLargeOptionalReturn = env.BoundGenericsHandler.IsLargeOptionalReturn(normalizedMethodDecl);
        if (hasLargeOptionalReturn)
        {
            swiftParams.Add("_ _resultBuf: UnsafeMutableRawPointer");
        }

        var swiftParamString = string.Join(", ", swiftParams);

        // Build call arguments with ArraySlice conversion
        var callArgs = new List<string>();
        for (int i = 0; i < originalArgs.Count; i++)
        {
            var origArg = originalArgs[i];
            var normArg = normalizedArgs[i];
            var privateName = !string.IsNullOrEmpty(normArg.PrivateName) ? normArg.PrivateName : normArg.Name;

            // Use dereferenced value for large Optional params
            var valueRef = OptionalPointerWrapperEmitter.ShouldWidenParam(normArg, env.BoundGenericsHandler)
                ? $"{privateName}Val" : privateName;

            // Determine label using same convention as ExistentialBypassEmitter
            var argStr = origArg.Name switch
            {
                var n when n.StartsWith("arg") => "",
                var n when n.StartsWith("_") => $"{n.Substring(1)}: ",
                var n when string.IsNullOrEmpty(n) => "",
                var n => $"{n}: "
            };

            // If this param was normalized (ArraySlice → Array), wrap with ArraySlice()
            if (ContainsArraySlice(origArg.SwiftTypeSpec))
            {
                argStr += $"Swift.ArraySlice({valueRef})";
            }
            else
            {
                argStr += valueRef;
            }

            callArgs.Add(argStr);
        }
        var callArgString = string.Join(", ", callArgs);

        // Render return type from original method
        var returnTypeSpec = originalMethodDecl.CSSignature.First().SwiftTypeSpec;
        var returnType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
        bool isVoid = returnTypeSpec is TupleTypeSpec tupleTypeSpec && tupleTypeSpec == TupleTypeSpec.Empty;
        bool throws = originalMethodDecl.Throws;

        var originalMethodName = originalMethodDecl.Name;
        var throwsClause = throws ? " throws" : "";
        var returnClause = (isVoid || hasLargeOptionalReturn) ? "" : $" -> {returnType}";
        var tryPrefix = throws ? "try " : "";
        var swiftFuncName = $"_sbw_{originalMethodName}_{DeterministicHash8(originalMethodDecl.MangledName)}";

        swiftWriter.WriteLine();

        if (isFreeFunction)
        {
            // Free function — emit standalone @_silgen_name function
            // Call the original by its module-qualified name.
            // ModuleDecl.Name may have a _ prefix for C# keyword escaping (e.g., "class" → "_class").
            // For Swift call sites, strip the prefix and backtick-escape the original keyword.
            var moduleName = UnescapeModuleName(originalMethodDecl.ModuleDecl?.Name ?? "");
            var callPrefix = !string.IsNullOrEmpty(moduleName) ? $"{moduleName}." : "";

            swiftWriter.WriteLine($"@_silgen_name(\"{wrapperSymbol}\")");
            swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){throwsClause}{returnClause} {{");
            swiftWriter.Indent++;

            foreach (var line in derefLines)
                swiftWriter.WriteLine(line);

            var callExpr = $"{tryPrefix}{callPrefix}{originalMethodName}({callArgString})";
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
            swiftWriter.WriteLine($"public {staticKeyword}func {swiftFuncName}({swiftParamString}){throwsClause}{returnClause} {{");
            swiftWriter.Indent++;

            foreach (var line in derefLines)
                swiftWriter.WriteLine(line);

            var callExpr = $"{tryPrefix}{selfPrefix}.{originalMethodName}({callArgString})";
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
    /// Builds the wrapper symbol name: SBW_{TypeName}_{MethodName}_{Hash8}
    /// </summary>
    private static string BuildWrapperSymbol(MethodDecl methodDecl)
    {
        var parentDecl = methodDecl.ParentDecl as TypeDecl;
        var typeName = parentDecl?.Name ?? "Global";
        var hash = DeterministicHash8(methodDecl.MangledName);
        return $"SBW_{typeName}_{methodDecl.Name}_{hash}";
    }

    internal static string DeterministicHash8(string input) => EmitterUtility.DeterministicHash8(input);

    /// <summary>
    /// Reverses the C# keyword escaping applied by SwiftABIParser.ExtractUniqueName().
    /// If a module name was escaped (e.g., "class" → "_class"), this strips the prefix
    /// and backtick-wraps it for valid Swift syntax (e.g., "`class`").
    /// </summary>
    internal static string UnescapeModuleName(string name)
    {
        if (name.Length > 1 && name[0] == '_')
        {
            var candidate = name.Substring(1);
            if (SyntaxFacts.GetKeywordKind(candidate) != SyntaxKind.None)
            {
                return $"`{candidate}`";
            }
        }
        return name;
    }
}
