// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("Swift.Bindings.Unit.Tests")]

namespace BindingsGeneration
{
    /// <summary>
    /// Bundles all parameters needed by any bridge adapter in the dispatch table.
    /// </summary>
    internal record BridgeEmitterContext(
        CSharpWriter CsWriter,
        SwiftWriter SwiftWriter,
        MethodEnvironment MethodEnv,
        ILogger Logger,
        ModuleEmissionContext? EmissionContext,
        bool HasExistentialArg = false,
        string? FirstExistentialType = null)
    {
        public TypeDecl? ParentDecl => MethodEnv.ParentDecl as TypeDecl;
    }

    /// <summary>
    /// Result of a bridge adapter's TryEmit call.
    /// Non-null means the method was handled (emitted or explicitly skipped).
    /// Null means the method is not eligible for this bridge.
    /// </summary>
    internal record BridgeEmitResult(string BridgeName, string Description, bool WasEmitted = true)
    {
        /// <summary>
        /// The method was handled (skipped/failed bypass) but NOT emitted.
        /// Adapter already called RecordMemberSkipped internally.
        /// </summary>
        public static BridgeEmitResult CreateSkipped() => new("_Skipped", "", WasEmitted: false);
    }

    /// <summary>
    /// Interface for bridge adapters in the method dispatch table.
    /// Each adapter wraps one bridge emitter and guards eligibility.
    /// </summary>
    internal interface IMethodBridgeEmitter
    {
        /// <summary>
        /// Attempts to emit a bridged version of the method.
        /// Returns non-null if the method was handled (emitted or explicitly skipped);
        /// null if the method is not eligible for this bridge.
        /// </summary>
        BridgeEmitResult? TryEmit(BridgeEmitterContext context);
    }

    /// <summary>
    /// Bridge adapter for existential bypass — handles methods with existential type arguments.
    /// Must be first in the dispatch table: existential-blocked methods must be handled/skipped
    /// before any other bridge sees them.
    /// </summary>
    internal sealed class ExistentialBypassBridgeAdapter : IMethodBridgeEmitter
    {
        public BridgeEmitResult? TryEmit(BridgeEmitterContext context)
        {
            if (!context.HasExistentialArg)
                return null;

            if (ExistentialBypassEmitter.TryEmitMethodBypass(
                context.CsWriter, context.SwiftWriter, context.MethodEnv, context.Logger))
            {
                return new BridgeEmitResult("ExistentialBypass",
                    "Existential parameter(s) omitted; Swift defaults used.");
            }

            // Bypass failed — skip method entirely
            context.Logger.LogWarning(
                $"Skipping method {context.MethodEnv.MethodDecl.Name}: bound generic contains unsupported existential type argument '{context.FirstExistentialType}'.");
            ReportCollector.RecordMemberSkipped(
                BindingItemKind.Method,
                context.MethodEnv.MethodDecl.Name,
                context.MethodEnv.MethodDecl.ParentDecl,
                SkipReason.UnsupportedExistential,
                $"Bound generic contains existential type argument '{context.FirstExistentialType}'.");
            return BridgeEmitResult.CreateSkipped();
        }
    }

    /// <summary>
    /// Bridge adapter for <c>[any Protocol.Type]</c> metatype-array parameters.
    /// Fires before ArraySliceBridgeAdapter so the metatype-array rewrite runs first
    /// on methods that would otherwise look like plain arrays.
    /// </summary>
    internal sealed class MetatypeArrayBridgeAdapter : IMethodBridgeEmitter
    {
        public BridgeEmitResult? TryEmit(BridgeEmitterContext context)
        {
            if (MetatypeArrayBridgeEmitter.TryEmit(
                context.CsWriter, context.SwiftWriter, context.MethodEnv,
                context.Logger, context.EmissionContext))
            {
                return new BridgeEmitResult("MetatypeArrayBridge",
                    "Existential-metatype-array parameter bridged via @_cdecl pointer+count wrapper.");
            }
            return null;
        }
    }

    /// <summary>
    /// Bridge adapter for ArraySlice normalization.
    /// </summary>
    internal sealed class ArraySliceBridgeAdapter : IMethodBridgeEmitter
    {
        public BridgeEmitResult? TryEmit(BridgeEmitterContext context)
        {
            if (ArraySliceNormalizationEmitter.TryEmitNormalizedMethod(
                context.CsWriter, context.SwiftWriter, context.MethodEnv,
                context.Logger, context.EmissionContext))
            {
                return new BridgeEmitResult("ArraySliceNormalization",
                    "ArraySlice parameters normalized to Array via Swift wrapper.");
            }
            return null;
        }
    }

    /// <summary>
    /// Bridge adapter for generic closure bridge — monomorphized Swift wrapper + C# for methods
    /// with generic closure parameters.
    /// </summary>
    internal sealed class GenericClosureBridgeAdapter : IMethodBridgeEmitter
    {
        public BridgeEmitResult? TryEmit(BridgeEmitterContext context)
        {
            if (GenericClosureBridgeEmitter.TryEmit(
                context.CsWriter, context.SwiftWriter, context.MethodEnv,
                context.ParentDecl, ctx: context.EmissionContext))
            {
                return new BridgeEmitResult("GenericClosureBridge",
                    "Generic closure parameter bridged via monomorphized Swift wrapper.");
            }
            return null;
        }
    }

    /// <summary>
    /// Bridge adapter for protocol extension closure bridge.
    /// Must be before MethodClosureBridgeAdapter (invariant #2).
    /// </summary>
    internal sealed class ProtocolExtensionClosureBridgeAdapter : IMethodBridgeEmitter
    {
        public BridgeEmitResult? TryEmit(BridgeEmitterContext context)
        {
            if (ProtocolExtensionClosureBridge.TryEmit(
                context.CsWriter, context.SwiftWriter, context.MethodEnv,
                context.ParentDecl))
            {
                return new BridgeEmitResult("ProtocolExtensionClosureBridge",
                    "Protocol extension closure parameter bridged via @_silgen_name wrapper.");
            }
            return null;
        }
    }

    /// <summary>
    /// Bridge adapter for method closure bridge — handles regular methods with closure parameters
    /// containing bound generic types.
    /// </summary>
    internal sealed class MethodClosureBridgeAdapter : IMethodBridgeEmitter
    {
        public BridgeEmitResult? TryEmit(BridgeEmitterContext context)
        {
            if (MethodClosureBridge.TryEmit(
                context.CsWriter, context.SwiftWriter, context.MethodEnv,
                context.ParentDecl, context.EmissionContext))
            {
                return new BridgeEmitResult("MethodClosureBridge",
                    "Closure parameter with bound generic args bridged via @_silgen_name wrapper.");
            }
            return null;
        }
    }

    /// <summary>
    /// Bridge adapter for nested closure bridge — two-level trampoline for closure-in-closure params.
    /// </summary>
    internal sealed class NestedClosureBridgeAdapter : IMethodBridgeEmitter
    {
        public BridgeEmitResult? TryEmit(BridgeEmitterContext context)
        {
            if (NestedClosureBridge.TryEmit(
                context.CsWriter, context.SwiftWriter, context.MethodEnv,
                context.ParentDecl, context.EmissionContext))
            {
                return new BridgeEmitResult("NestedClosureBridge",
                    "Nested closure parameter bridged via two-level trampoline.");
            }
            return null;
        }
    }

    /// <summary>
    /// Bridge adapter for method-level generic parameters — uses Swift 5.7+ implicit existential
    /// opening to bridge methods with single protocol-constrained generic type parameters.
    /// </summary>
    internal sealed class MethodGenericBridgeAdapter : IMethodBridgeEmitter
    {
        public BridgeEmitResult? TryEmit(BridgeEmitterContext context)
        {
            if (MethodGenericBridgeEmitter.TryEmit(
                context.CsWriter, context.SwiftWriter, context.MethodEnv,
                context.ParentDecl, ctx: context.EmissionContext))
            {
                return new BridgeEmitResult("MethodGenericBridge",
                    "Method-level generic parameter bridged via existential opening.");
            }
            return null;
        }
    }

    /// <summary>
    /// Bridge adapter for Optional&lt;Closure&gt;+default bypass — omits unsupported optional closure
    /// params, letting Swift fill nil. Must be last in the dispatch table (narrowest scope).
    /// </summary>
    internal sealed class OptionalClosureBypassAdapter : IMethodBridgeEmitter
    {
        public BridgeEmitResult? TryEmit(BridgeEmitterContext context)
        {
            if (!ExistentialBypassEmitter.HasOptionalClosureWithDefault(
                context.MethodEnv.MethodDecl, context.MethodEnv.TypeDatabase))
            {
                return null;
            }

            // Reduced-signature dedup — bypass strips params, check reduced projected key
            var reducedMethodDecl = ExistentialBypassEmitter.BuildReducedMethodDecl(
                context.MethodEnv.MethodDecl, context.MethodEnv.TypeDatabase);
            string? reducedMethodKey = null;
            if (reducedMethodDecl != null && context.MethodEnv.EmittedProjectedSignatures != null)
            {
                reducedMethodKey = BaseHandler.GetProjectedCSharpMethodKey(
                    reducedMethodDecl, context.MethodEnv.TypeDatabase, context.Logger);
                // Apply collision suffix so disambiguated methods check the correct key
                if (context.MethodEnv.CollisionIndex > 0)
                    reducedMethodKey = BaseHandler.ApplyCollisionSuffixToKey(reducedMethodKey, context.MethodEnv.CollisionIndex);
                if (context.MethodEnv.EmittedProjectedSignatures.Contains(reducedMethodKey))
                {
                    context.Logger.LogDebug(
                        $"Skipping method {context.MethodEnv.MethodDecl.Name}: optional closure bypass reduced signature collides: {reducedMethodKey}");
                    ReportCollector.RecordMemberSkipped(
                        BindingItemKind.Method,
                        context.MethodEnv.MethodDecl.Name,
                        context.MethodEnv.MethodDecl.ParentDecl,
                        SkipReason.DuplicateSignature,
                        $"Optional closure bypass reduced C# signature collides: {reducedMethodKey}");
                    return BridgeEmitResult.CreateSkipped();
                }
            }

            if (ExistentialBypassEmitter.TryEmitMethodBypass(
                context.CsWriter, context.SwiftWriter, context.MethodEnv, context.Logger))
            {
                // Reserve the reduced key now that emission succeeded
                if (reducedMethodKey != null)
                    context.MethodEnv.EmittedProjectedSignatures?.Add(reducedMethodKey);
                return new BridgeEmitResult("OptionalClosureBypass",
                    "Optional closure parameter(s) with defaults omitted; Swift fills nil.");
            }

            // Explicit fallback skip — bypass failed (async/throws/static/non-void)
            context.Logger.LogWarning(
                $"Skipping method {context.MethodEnv.MethodDecl.Name}: optional closure params with defaults but bypass not applicable.");
            ReportCollector.RecordMemberSkipped(
                BindingItemKind.Method,
                context.MethodEnv.MethodDecl.Name,
                context.MethodEnv.MethodDecl.ParentDecl,
                SkipReason.UnsupportedClosure,
                "Optional closure parameter(s) with defaults, but method shape incompatible with bypass (async/throws/static/non-void).");
            return BridgeEmitResult.CreateSkipped();
        }
    }
}
