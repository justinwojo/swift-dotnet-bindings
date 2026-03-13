// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Represents a property declaration.
    /// </summary>
    public record PropertyDecl : BaseDecl
    {
        /// <summary>
        /// The TypeSpec of the declaration
        /// <summary>
        public required TypeSpec SwiftTypeSpec { get; set; }

        /// <summary>
        /// Indicates if the property has a backing field
        /// </summary>
        public required bool HasStorage { get; set; }

        /// <summary>
        /// Indicates if the declaration is static.
        /// </summary>
        public required bool IsStatic { get; set; }

        /// <summary>
        /// The accessors available for this field.
        /// </summary>
        public required IReadOnlyList<AccessorDecl> Accessors { get; init; }

        /// <summary>
        /// Whether this property overrides a superclass property.
        /// Parsed from the ABI JSON Var node's 'overriding' field or 'Override' in declAttributes.
        /// </summary>
        public bool IsOverride { get; set; } = false;

        /// <summary>
        /// Whether this property is declared as 'final'.
        /// Final properties cannot be overridden in subclasses.
        /// </summary>
        public bool IsFinal { get; set; } = false;

        /// <summary>
        /// Set to true during emission when this property passes all validation gates and is
        /// actually written to the C# output. Used by override resolution to verify that a
        /// base class property exists in the emitted C# hierarchy (not just the parsed model).
        /// </summary>
        public bool WasEmitted { get; set; } = false;

        /// <summary>
        /// Whether this property is marked @_spi (System Programming Interface).
        /// @_spi members on public types are only visible to SPI consumers and should not
        /// appear in generated bindings.
        /// </summary>
        public bool IsSpiProtected { get; set; } = false;

        /// <summary>
        /// Whether this property is @MainActor-isolated (individually annotated, not inherited from type).
        /// </summary>
        public bool IsActorIsolated { get; set; } = false;

        /// <summary>
        /// Whether this property is declared nonisolated (opts out of containing type's isolation).
        /// </summary>
        public bool IsNonisolated { get; set; } = false;

        /// <summary>
        /// Whether this property has internal (non-public) access level.
        /// Set from ABI JSON's IsInternal flag and swiftinterface cross-reference.
        /// Internal properties are not accessible from the wrapper module and must be
        /// excluded from @_cdecl wrapper generation.
        /// </summary>
        public bool IsModuleInternal { get; set; } = false;
    }
}
