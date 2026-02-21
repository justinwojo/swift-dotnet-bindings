// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared validation checks used by MethodHandler, ConstructorHandler, and PropertyHandler
/// to determine whether a method/accessor can be emitted.
/// </summary>
internal static class MethodValidationGates
{
    /// <summary>
    /// Checks if the method has constraints on protocols with associated types.
    /// Such protocols generate generic C# interfaces which can't be used as constraints without type arguments.
    /// Used by MethodHandler.Emit, ConstructorHandler.Emit, and PropertyHandler preflight.
    /// </summary>
    public static bool HasUnsupportedProtocolConstraints(MethodEnvironment methodEnv)
    {
        if (!methodEnv.MethodDecl.IsGeneric)
            return false;

        foreach (var param in methodEnv.MethodDecl.GenericParameters)
        {
            foreach (var conformance in param.GenericConformances)
            {
                if (conformance.Kind != ConformanceKind.Protocol)
                    continue;

                if (methodEnv.TypeDatabase.TryGetTypeRecord(conformance.ConformanceTarget, out var record) &&
                    record.Kind == TypeRecordKind.Protocol &&
                    record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
