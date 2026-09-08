// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Replays only native-backed default overloads whose producer completed in a previous render.
/// Managed forwarding postprocessors still leave with the managed declaration they call.
/// </summary>
internal static class NativeDefaultOverloadRecovery
{
    internal static void TryEmit(
        MethodDecl source,
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        ITypeDatabase typeDatabase,
        TypeHandlerContext context,
        IReadOnlySet<string>? siblingPropertyNames,
        HashSet<string> emittedProjectedSignatures,
        Dictionary<string, int> reservedOverloadShapes,
        ILogger logger)
    {
        var sourceId = DeclIdFactory.ForMethod(source);
        if (!EmissionAttempt.TryGetFault(sourceId, out var fault)
            || fault.Origin is not (EmitterFaultOrigin.RecoveryWithdrawal
                or EmitterFaultOrigin.CSharpRecoveryWithdrawal
                or EmitterFaultOrigin.BisectionIsolatedWithdrawal))
            return;

        // Bisection probes and settled withdrawals use the same explicit origin. Preserve
        // independently owned defaults while testing the original: otherwise withdrawing a
        // healthy original also hides a bad trim and changes which candidate appears causal.
        var emissionContext = context.GetEmissionContext();
        if (!emissionContext.TryGetDefaultOverloadRecipe(sourceId, out _))
            return;

        // The caller is inside the surviving parent and has already recorded the original's
        // refusal. Do not dispatch its handler or reserve its full signature. The producer restores
        // its captured names, normalized signature and symbol into a fresh environment, and checks
        // each independently owned candidate against the current denial and reservation sets.
        var env = new MethodEnvironment(source, typeDatabase, siblingPropertyNames,
            context.PInvokeHelperContext, context.CompositionCollector)
        {
            SourceDeclId = sourceId,
            EmissionContext = emissionContext,
            EmittedProjectedSignatures = emittedProjectedSignatures,
            ReservedOverloadShapes = reservedOverloadShapes,
        };
        DefaultParameterOverloadEmitter.TryEmitOverloads(
            csWriter, swiftWriter, env, logger, emissionContext);
    }
}
