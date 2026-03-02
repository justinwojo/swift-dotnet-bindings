// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Bundles all parameters needed by post-processor adapters.
    /// Follows the <see cref="BridgeEmitterContext"/> pattern.
    /// </summary>
    internal record PostProcessorContext(
        CSharpWriter CsWriter,
        SwiftWriter SwiftWriter,
        MethodEnvironment MethodEnv,
        ILogger Logger,
        ModuleEmissionContext? EmissionContext,
        Dictionary<string, List<string>>? MarkerProtocolConformances = null);

    /// <summary>
    /// Controls which method types a post-processor runs on.
    /// </summary>
    internal enum PostProcessorScope
    {
        /// <summary>Only non-accessor methods (not constructors, not accessors).</summary>
        MethodsOnly,

        /// <summary>All methods including constructors.</summary>
        All
    }

    /// <summary>
    /// Interface for post-processor adapters that run after normal method emission.
    /// Each adapter wraps one post-processing step (overload generation, etc.).
    /// </summary>
    internal interface IMethodPostProcessor
    {
        /// <summary>
        /// Controls which method types this post-processor runs on.
        /// </summary>
        PostProcessorScope Scope { get; }

        /// <summary>
        /// Attempts to run the post-processing step on the emitted method.
        /// </summary>
        void TryPostProcess(PostProcessorContext context);
    }

    /// <summary>
    /// Post-processor for default parameter overloads (trailing default params → shorter overloads).
    /// Runs on all methods including constructors.
    /// </summary>
    internal sealed class DefaultParameterOverloadPostProcessor : IMethodPostProcessor
    {
        public PostProcessorScope Scope => PostProcessorScope.All;

        public void TryPostProcess(PostProcessorContext context)
        {
            DefaultParameterOverloadEmitter.TryEmitOverloads(
                context.CsWriter, context.SwiftWriter, context.MethodEnv,
                context.Logger, context.EmissionContext);
        }
    }

    /// <summary>
    /// Post-processor for Task-returning overloads of completion-handler methods (WU8).
    /// Methods only — not constructors or accessors.
    /// </summary>
    internal sealed class CompletionHandlerPostProcessor : IMethodPostProcessor
    {
        public PostProcessorScope Scope => PostProcessorScope.MethodsOnly;

        public void TryPostProcess(PostProcessorContext context)
        {
            MethodHandler.TryEmitCompletionHandlerOverload(context.CsWriter, context.MethodEnv);
        }
    }

    /// <summary>
    /// Post-processor for typed convenience overloads for marker protocol parameters.
    /// Methods only — not constructors or accessors.
    /// </summary>
    internal sealed class MarkerProtocolOverloadPostProcessor : IMethodPostProcessor
    {
        public PostProcessorScope Scope => PostProcessorScope.MethodsOnly;

        public void TryPostProcess(PostProcessorContext context)
        {
            if (context.MarkerProtocolConformances != null)
            {
                MarkerProtocolOverloadEmitter.EmitOverloads(
                    context.CsWriter, context.SwiftWriter, context.MethodEnv.MethodDecl,
                    context.MethodEnv, context.MethodEnv.ParentDecl as TypeDecl,
                    context.MarkerProtocolConformances);
            }
        }
    }

    /// <summary>
    /// Post-processor for int/uint convenience overloads for nint/nuint parameters.
    /// Methods only — not constructors or accessors.
    /// </summary>
    internal sealed class NativeIntOverloadPostProcessor : IMethodPostProcessor
    {
        public PostProcessorScope Scope => PostProcessorScope.MethodsOnly;

        public void TryPostProcess(PostProcessorContext context)
        {
            NativeIntOverloadEmitter.TryEmitOverload(context.CsWriter, context.MethodEnv);
        }
    }

    /// <summary>
    /// Post-processor for simplified Action/Func overloads for throwing closure parameters.
    /// Methods only — not constructors or accessors.
    /// </summary>
    internal sealed class ThrowingClosureSimplificationPostProcessor : IMethodPostProcessor
    {
        public PostProcessorScope Scope => PostProcessorScope.MethodsOnly;

        public void TryPostProcess(PostProcessorContext context)
        {
            ThrowingClosureSimplificationEmitter.TryEmitOverload(context.CsWriter, context.MethodEnv);
        }
    }
}
