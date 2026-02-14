// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

internal static class UnsupportedSwiftTypeSupport
{
    public static bool TryFindFallbackInfo(
        ITypeDatabase typeDatabase,
        ClosureHandler closureHandler,
        TypeSpec typeSpec,
        out TypeDatabaseExtensions.AnyTypeFallbackInfo fallbackInfo)
    {
        if (typeDatabase.TryGetAnyTypeFallbackInfo(typeSpec, out var directFallback))
        {
            fallbackInfo = directFallback.Value;
            return true;
        }

        if (typeSpec is ClosureTypeSpec closureTypeSpec && !closureHandler.IsSupportedClosure(closureTypeSpec))
        {
            fallbackInfo = new TypeDatabaseExtensions.AnyTypeFallbackInfo(
                "Unsupported closure fallback",
                closureTypeSpec.ToString());
            return true;
        }

        switch (typeSpec)
        {
            case NamedTypeSpec namedTypeSpec:
                foreach (var genericParameter in namedTypeSpec.GenericParameters)
                {
                    if (TryFindFallbackInfo(typeDatabase, closureHandler, genericParameter, out fallbackInfo))
                    {
                        return true;
                    }
                }
                break;
            case TupleTypeSpec tupleTypeSpec:
                foreach (var element in tupleTypeSpec.Elements)
                {
                    if (TryFindFallbackInfo(typeDatabase, closureHandler, element, out fallbackInfo))
                    {
                        return true;
                    }
                }
                break;
            case ClosureTypeSpec nestedClosureTypeSpec:
                if (TryFindFallbackInfo(typeDatabase, closureHandler, nestedClosureTypeSpec.Arguments, out fallbackInfo))
                {
                    return true;
                }

                if (TryFindFallbackInfo(typeDatabase, closureHandler, nestedClosureTypeSpec.ReturnType, out fallbackInfo))
                {
                    return true;
                }
                break;
        }

        fallbackInfo = default;
        return false;
    }

    public static void EmitAttribute(CSharpWriter csWriter, TypeDatabaseExtensions.AnyTypeFallbackInfo fallbackInfo)
    {
        csWriter.WriteLine(
            $"[global::Swift.UnsupportedSwiftType(\"{EscapeStringLiteral(fallbackInfo.Reason)}\", \"{EscapeStringLiteral(fallbackInfo.SwiftType)}\")]");
    }

    internal static string EscapeStringLiteral(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
