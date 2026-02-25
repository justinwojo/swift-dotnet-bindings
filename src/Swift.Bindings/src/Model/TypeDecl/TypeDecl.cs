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

        /// <summary>
        /// Whether this type is internal to its module but ABI-visible (has @usableFromInline).
        /// Types with this flag cannot be extended from external modules, so Swift wrapper
        /// extensions (e.g., ArraySlice normalization) should not be emitted for them.
        /// </summary>
        public bool IsModuleInternal { get; set; } = false;

        /// <summary>
        /// Whether this type is annotated with @MainActor.
        /// When true, generated Swift wrapper functions must include @MainActor annotation.
        /// </summary>
        public bool IsMainActorIsolated { get; set; } = false;

        /// <summary>
        /// Whether this type is declared with the 'actor' keyword (custom actor).
        /// Custom actors dispatch to their own executor — wrappers do NOT get @MainActor,
        /// but the existing async wrapper pattern (Task {}) already handles dispatch.
        /// </summary>
        public bool IsCustomActor { get; set; } = false;

        /// <summary>
        /// Whether this type has a singleton pattern (static 'shared' property returning Self).
        /// Used for async method workarounds where passing self doesn't work correctly.
        /// </summary>
        public bool HasSingletonPattern => Properties.Any(p =>
            p.IsStatic &&
            p.Name == "shared" &&
            p.SwiftTypeSpec is NamedTypeSpec namedType &&
            namedType.Name.EndsWith(Name));
    }
}
