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
        /// The original Swift identifier name, set only when the parser's ExtractUniqueName
        /// modified <see cref="Name"/> (e.g., added a "_" prefix for C# keyword escaping).
        /// When null, <see cref="Name"/> is the original Swift name.
        /// Use <see cref="GetSwiftName"/> to get the correct name for Swift code emission.
        /// </summary>
        public string? OriginalSwiftName { get; set; }

        /// <summary>
        /// Returns the original Swift identifier for use in generated Swift code.
        /// Falls back to <see cref="Name"/> when the parser did not modify the name.
        /// </summary>
        public string GetSwiftName() => OriginalSwiftName ?? Name;

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
