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
        /// Whether this property is actor-isolated via a per-member annotation (e.g., @MainActor or @ProcessingActor).
        /// Both @MainActor and custom global actors set this flag.
        /// </summary>
        public bool IsActorIsolated { get; set; } = false;

        /// <summary>
        /// Whether this property is specifically @MainActor-isolated (per-member annotation).
        /// A subset of IsActorIsolated — true only for @MainActor, false for custom actors.
        /// </summary>
        public bool IsMainActorIsolated { get; set; } = false;

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

        /// <summary>
        /// Whether this is an @objc optional protocol property.
        /// ObjC protocols can declare optional properties that conforming types may omit.
        /// Witness dispatch and EveryProtocol conformance should skip these properties.
        /// </summary>
        public bool IsObjCOptional { get; set; } = false;

        /// <summary>
        /// Whether this property is a protocol requirement (protocolReq=true in ABI JSON).
        /// Mirrors <see cref="MethodDecl.IsProtocolRequirement"/>. Required protocol properties
        /// must have a witness in EveryProtocol's conformance — if a required property is
        /// dropped by suppression (SPI, module-internal) or fails to parse, the conformance
        /// itself must be skipped to avoid emitting an unsatisfiable extension.
        /// </summary>
        public bool IsProtocolRequirement { get; set; } = false;

        /// <summary>
        /// Setter-specific availability annotations when the setter is restricted to a
        /// newer platform than the property getter. Read from the ABI JSON's
        /// <c>intro_iOS</c>/<c>intro_Macosx</c>/etc. fields on the set accessor node and
        /// merged with the property-level availability. When null, the setter inherits
        /// <see cref="BaseDecl.AvailabilityAnnotations"/> from the property.
        /// </summary>
        public IReadOnlyList<AvailabilityAnnotation>? SetterAvailabilityAnnotations { get; set; }
    }
}
