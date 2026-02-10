// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Represents a base declaration.
    /// </summary>
    public record BaseDecl
    {
        /// <summary>
        /// Name of the declaration.
        /// </summary>
        public required string Name { get; set; } //TODO: Hide or remove this property. This might not contain a correct name.

        /// <summary>
        /// The parent declaration.
        /// </summary>
        public required BaseDecl? ParentDecl { get; set; }

        /// <summary>
        /// The module declaration.
        /// </summary>
        public required ModuleDecl? ModuleDecl { get; set; }

        /// <summary>
        /// Documentation from Swift symbol graph (null when --symbolgraph is not provided).
        /// </summary>
        public DocComment? Documentation { get; set; }
    }
}
