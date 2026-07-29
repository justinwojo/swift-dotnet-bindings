// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Represents an interface for emitting C# source code.
    /// </summary>
    public interface IEmitter
    {
        /// <summary>
        /// Emits a C# module based on the module declaration.
        /// </summary>
        /// <param name="decl">The module declaration.</param>
        /// <param name="emissionContext">Per-module emission context. When null, this emission gets its own
        /// (see <see cref="ModuleEmissionContext.CreateImplicitFallback"/>) rather than sharing one.</param>
        public void EmitModule(ModuleDecl decl, ModuleEmissionContext? emissionContext = null);
    }
}
