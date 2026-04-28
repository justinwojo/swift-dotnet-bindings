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
        /// Whether this type is decorated with @_spi (System Programming Interface).
        /// @_spi types are only visible to SPI consumers (e.g., other modules in the same
        /// package). Unlike @usableFromInline types, @_spi types should NEVER appear in
        /// generated bindings — they are not part of the public API surface.
        /// </summary>
        public bool IsSpiProtected { get; set; } = false;

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
        /// Whether this type is annotated with a custom global actor (e.g., <c>@ImagePipelineActor</c>),
        /// distinct from <see cref="IsCustomActor"/> which tracks the <c>actor X { }</c> keyword form.
        /// All members on such a type implicitly inherit the actor's isolation unless they
        /// individually opt out with <c>nonisolated</c>. The constructor wrapper hops into the
        /// isolation domain via <c>&lt;Actor&gt;.shared.assumeIsolated { … }</c> when the actor
        /// type is resolvable through <see cref="CustomActorIsolatorName"/>; SWIFTBIND022 is
        /// emitted only as a fallback when the actor itself can't be located in the type model.
        /// </summary>
        public bool IsCustomActorIsolated { get; set; } = false;

        /// <summary>
        /// Short name of the global actor type that isolates this type (e.g., <c>"ImagePipelineActor"</c>
        /// for a class annotated <c>@ImagePipelineActor</c> or <c>@Nuke.ImagePipelineActor</c>).
        /// Populated by the swiftinterface scanner when <see cref="IsCustomActorIsolated"/> is set.
        /// Used by the constructor wrapper emitter to build the <c>actor.shared.assumeIsolated</c>
        /// hop; when the named actor cannot be located in the bound module(s), the emitter falls
        /// back to skipping the constructor with SWIFTBIND022.
        /// </summary>
        public string? CustomActorIsolatorName { get; set; }

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
