// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Detects completion handler closure parameters and determines the callback shape
/// for generating Task-returning overloads.
/// </summary>
/// <remarks>
/// A completion handler is identified by:
/// 1. Trailing closure parameter (last parameter)
/// 2. Closure returns Void
/// 3. Method itself is NOT already async
/// 4. Method returns Void
/// 5. Single closure parameter (v1 limitation)
/// 6. Recognized callback shape (see <see cref="CallbackShape"/>)
/// </remarks>
public static class CompletionHandlerDetector
{
    /// <summary>
    /// Describes the shape of a completion handler closure.
    /// </summary>
    public enum CallbackShape
    {
        /// <summary>() -> Void — completion with no result</summary>
        VoidResult,
        /// <summary>(T) -> Void — completion with a single result</summary>
        SingleResult,
        /// <summary>(T?, Error?) -> Void — completion with result and optional error</summary>
        ResultWithError,
        /// <summary>(Error?) -> Void — completion with optional error only</summary>
        ErrorOnly,
        /// <summary>Unrecognized closure shape — no Task overload generated</summary>
        Unsupported
    }

    /// <summary>
    /// Determines whether the given closure parameter is a completion handler
    /// eligible for Task-returning overload generation.
    /// </summary>
    /// <param name="methodDecl">The method containing the closure parameter.</param>
    /// <param name="closureParam">The closure parameter to check.</param>
    /// <param name="closureHandler">The closure handler for type inspection.</param>
    /// <returns>True if this is a completion handler eligible for a Task overload.</returns>
    public static bool IsCompletionHandler(MethodDecl methodDecl, ArgumentDecl closureParam, ClosureHandler closureHandler)
    {
        // Method must not be async (avoid double-wrapping Swift async methods)
        if (methodDecl.IsAsync)
            return false;

        // Method must return Void
        var returnType = methodDecl.CSSignature.FirstOrDefault();
        if (returnType == null || !returnType.SwiftTypeSpec.IsEmptyTuple)
            return false;

        // Must be a closure
        if (!closureHandler.IsClosure(closureParam))
            return false;

        var closureSpec = closureHandler.GetClosureTypeSpec(closureParam);
        if (closureSpec == null)
            return false;

        // Closure must return Void
        if (closureSpec.HasReturn())
            return false;

        // Must be trailing (last parameter)
        var parameters = methodDecl.CSSignature.Skip(1).ToList();
        if (parameters.Count == 0)
            return false;
        if (parameters.Last().Name != closureParam.Name)
            return false;

        // Single closure parameter only (v1 limitation)
        int closureCount = parameters.Count(p => closureHandler.IsClosure(p));
        if (closureCount > 1)
            return false;

        // Must have a recognized callback shape
        return GetCallbackShape(closureSpec) != CallbackShape.Unsupported;
    }

    /// <summary>
    /// Determines the callback shape of a completion handler closure.
    /// </summary>
    /// <param name="closureSpec">The closure type specification.</param>
    /// <returns>The callback shape, or <see cref="CallbackShape.Unsupported"/> if unrecognized.</returns>
    public static CallbackShape GetCallbackShape(ClosureTypeSpec closureSpec)
    {
        // Closure must return Void
        if (closureSpec.HasReturn())
            return CallbackShape.Unsupported;

        int argCount = closureSpec.ArgumentCount();
        if (!closureSpec.HasArguments())
        {
            // () -> Void
            return CallbackShape.VoidResult;
        }

        if (argCount == 1)
        {
            var arg = closureSpec.GetArgument(0);

            // Check for (Error?) -> Void pattern
            if (IsOptionalErrorType(arg))
                return CallbackShape.ErrorOnly;

            // (T) -> Void — single result (T may be optional)
            return CallbackShape.SingleResult;
        }

        if (argCount == 2)
        {
            var arg0 = closureSpec.GetArgument(0);
            var arg1 = closureSpec.GetArgument(1);

            // (T?, Error?) -> Void pattern
            if (IsOptionalType(arg0) && IsOptionalErrorType(arg1))
                return CallbackShape.ResultWithError;

            return CallbackShape.Unsupported;
        }

        // 3+ params — unsupported
        return CallbackShape.Unsupported;
    }

    /// <summary>
    /// Extracts the C# result type from a completion handler closure.
    /// For VoidResult, returns null. For SingleResult, returns the parameter type.
    /// For ResultWithError, returns the first parameter type (nullable).
    /// For ErrorOnly, returns null.
    /// </summary>
    /// <param name="closureSpec">The closure type specification.</param>
    /// <param name="shape">The callback shape.</param>
    /// <param name="typeDatabase">The type database for type resolution.</param>
    /// <param name="typeConversionHandler">The type conversion handler for idiomatic type names.</param>
    /// <returns>The C# result type name, or null for void-returning overloads.</returns>
    public static string? GetResultTypeName(
        ClosureTypeSpec closureSpec,
        CallbackShape shape,
        ITypeDatabase typeDatabase,
        TypeConversionHandler typeConversionHandler)
    {
        switch (shape)
        {
            case CallbackShape.VoidResult:
            case CallbackShape.ErrorOnly:
                return null;

            case CallbackShape.SingleResult:
            {
                var argType = closureSpec.GetArgument(0);
                return ResolveTypeName(argType, typeDatabase, typeConversionHandler);
            }

            case CallbackShape.ResultWithError:
            {
                // Keep the full Optional<T> type — the callback parameter is T? and Swift APIs
                // can legitimately return nil result + nil error. Task<T?> is the correct mapping.
                var argType = closureSpec.GetArgument(0);
                return ResolveTypeName(argType, typeDatabase, typeConversionHandler);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Resolves a Swift TypeSpec to a C# type name string.
    /// For bound generic types (e.g., Result&lt;T, Error&gt;), resolves all generic arguments recursively.
    /// Returns null if the type or any generic argument cannot be resolved.
    /// </summary>
    private static string? ResolveTypeName(TypeSpec typeSpec, ITypeDatabase typeDatabase, TypeConversionHandler typeConversionHandler)
    {
        // Try idiomatic conversion first (SwiftString → string, etc.)
        var idiomaticType = typeConversionHandler.GetIdiomaticCSharpType(typeSpec, isParameter: true);
        if (idiomaticType != null)
            return idiomaticType;

        // Fall back to type database lookup
        if (typeDatabase.TryGetTypeRecord(typeSpec, out var record))
        {
            // Protocol types used as generic args need existential container handling
            // which isn't available here — return null to skip the overload
            if (record.Kind == TypeRecordKind.Protocol)
                return null;

            var baseName = record.CSharpTypeName.FullyQualifiedName;

            // Handle bound generic types — resolve all generic arguments
            if (typeSpec is NamedTypeSpec namedSpec && namedSpec.GenericParameters.Count > 0)
            {
                var genericArgs = new List<string>();
                foreach (var genParam in namedSpec.GenericParameters)
                {
                    var resolvedArg = ResolveTypeName(genParam, typeDatabase, typeConversionHandler);
                    if (resolvedArg == null)
                        return null; // Can't resolve a generic arg — skip overload
                    genericArgs.Add(resolvedArg);
                }
                return $"{baseName}<{string.Join(", ", genericArgs)}>";
            }

            return baseName;
        }

        return null;
    }

    /// <summary>
    /// Checks if a TypeSpec is an Optional type (Swift.Optional).
    /// </summary>
    private static bool IsOptionalType(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;
        var typeName = SwiftTypeName.FromTypeSpec(namedType);
        return typeName.ModuleQualifiedName == "Swift.Optional";
    }

    /// <summary>
    /// Checks if a TypeSpec is an Optional Error type (Swift.Optional&lt;Swift.Error&gt;).
    /// </summary>
    private static bool IsOptionalErrorType(TypeSpec typeSpec)
    {
        if (!IsOptionalType(typeSpec))
            return false;

        var namedType = (NamedTypeSpec)typeSpec;
        if (namedType.GenericParameters.Count != 1)
            return false;

        var innerType = namedType.GenericParameters[0];
        return innerType.ToString() == "Swift.Error";
    }
}
