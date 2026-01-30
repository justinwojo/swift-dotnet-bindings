// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Represents a type declaration.
    /// </summary>
    public record TypeDecl : BaseDecl
    {
        /// <summary>
        /// Swift type name.
        /// </summary>
        public required SwiftTypeName SwiftTypeName { get; set; }

        /// <summary>
        /// Mangled name of the declaration.
        /// </summary>
        public required string MangledName { get; set; }

        /// <summary>
        /// Type properties.
        /// </summary>
        public required List<PropertyDecl> Properties { get; set; }

        /// <summary>
        /// Methods within the base declaration.
        /// </summary>
        public required List<MethodDecl> Methods { get; set; }

        /// <summary>
        /// Types declarations within the base declaration.
        /// </summary>
        public required List<TypeDecl> Types { get; set; }

        /// <summary>
        /// Operator declarations within the type.
        /// </summary>
        public required List<OperatorDecl> Operators { get; set; }

        /// <summary>
        /// Subscript declarations within the type.
        /// </summary>
        public List<SubscriptDecl> Subscripts { get; set; } = new();

        /// <summary>
        /// Generic type parameters for this type declaration.
        /// Empty for non-generic types.
        /// </summary>
        public List<GenericArgumentDecl> GenericParameters { get; set; } = new();

        /// <summary>
        /// Whether this type is generic (has type parameters).
        /// </summary>
        public bool IsGeneric => GenericParameters.Count > 0;
    }
}
