// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace BindingsGeneration
{
    /// <summary>
    /// Places the static extension classes a type emits after its body (constrained-extension
    /// specializations, generic-parent CSM, KeyPath trampolines and the like) at namespace scope.
    /// </summary>
    /// <remarks>
    /// C# accepts extension methods only in a top-level static class (CS1109), but a nested type's
    /// handler writes after its own closing brace, which is still inside the enclosing type. So a
    /// nested type queues the emission on <see cref="TypeHandlerContext.DeferredNamespaceScopeEmissions"/>
    /// and the outermost type runs the queue after its own body, inside its file span. Each queued
    /// emission reopens the nested type's fragment scopes on both planes, so a compile failure in it
    /// still attributes to the nested type it came from. A top-level type, or a context that carries
    /// no queue, emits in place exactly as before.
    /// </remarks>
    internal static class NamespaceScopeEmission
    {
        public static void EmitOrDefer(CSharpWriter csWriter, SwiftWriter swiftWriter, TypeDecl typeDecl,
            TypeHandlerContext context, Action<CSharpWriter> emit)
        {
            if (typeDecl.ParentDecl is TypeDecl && context.DeferredNamespaceScopeEmissions is { } queue)
            {
                var csOwner = FragmentOwners.ForDecl(typeDecl);
                var swiftOwner = FragmentOwners.ForDeclWrapper(typeDecl);
                queue.Add(writer =>
                {
                    using var csScope = writer.BeginFragment(csOwner);
                    using var swiftScope = swiftWriter.BeginFragment(swiftOwner);
                    emit(writer);
                });
                return;
            }
            emit(csWriter);
        }

        /// <summary>Runs the queued emissions of nested types once the outermost type has closed.</summary>
        public static void DrainAfterOutermost(CSharpWriter csWriter, TypeDecl typeDecl, TypeHandlerContext context)
        {
            if (typeDecl.ParentDecl is TypeDecl)
                return;
            Drain(csWriter, context);
        }

        /// <summary>
        /// Runs whatever is still queued once every type of the module is written, at namespace
        /// scope. An outermost type that leaves its handler early (a cross-module extension, a
        /// skipped type) never reaches its own drain, so its nested types' emissions wait here.
        /// </summary>
        public static void DrainAtModuleEnd(CSharpWriter csWriter, TypeHandlerContext context) =>
            Drain(csWriter, context);

        private static void Drain(CSharpWriter csWriter, TypeHandlerContext context)
        {
            if (context.DeferredNamespaceScopeEmissions is not { Count: > 0 } queue)
                return;
            // A queued emission never queues another (it runs at namespace scope), but copy first
            // so the list is not enumerated while anything could add to it.
            var pending = new List<Action<CSharpWriter>>(queue);
            queue.Clear();
            foreach (var emission in pending)
                emission(csWriter);
        }
    }
}
