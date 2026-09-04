// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
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
        /// Marks this property as emitted. The single mutation entry point for <see cref="WasEmitted"/>
        /// — every emitter that successfully writes a property stamps it through here rather than
        /// assigning the flag inline, so "an emitter that produced a member stamps it" lives in one
        /// place (pinned by <c>WasEmittedAssignmentCountTests</c>).
        /// </summary>
        public void MarkEmitted() => WasEmitted = true;

        /// <summary>
        /// The disambiguated C# base name chosen for this property when a sibling declares a
        /// Swift name differing from this one only by case, so both project onto one C# identifier
        /// (Swift <c>url</c> + <c>URL</c> → <c>Url</c>, CS0102). Null when no such collision exists,
        /// which is the overwhelmingly common case.
        ///
        /// <para>The decision is stamped on the DECLARATION rather than carried in a name-keyed
        /// rename dictionary because the collapse destroys the key: by the time two properties are
        /// both called <c>Url</c>, no C#-name-keyed map can tell them apart. Every name-prediction
        /// site therefore has to reach the decision through the decl — see
        /// <c>NameProvider.GetPropertyName(PropertyDecl, string?)</c>.</para>
        /// </summary>
        public string? CaseDisambiguatedName { get; private set; }

        /// <summary>
        /// Records the case-only disambiguation decision for this property. The single mutation
        /// entry point for <see cref="CaseDisambiguatedName"/>; called only from the pre-emission
        /// case-only collision pass.
        /// </summary>
        public void MarkCaseDisambiguated(string csharpName) => CaseDisambiguatedName = csharpName;

        /// <summary>
        /// The C# name this property was actually emitted under, stamped at emission time. Mirrors
        /// <see cref="MethodDecl.EmittedCSharpName"/>: it is the only value that has seen every
        /// naming scheme (enclosing-type <c>Value</c> rule, nested-type rename channel, enum-case
        /// channel, case-only disambiguation), so the rename ledger written into the module
        /// database reads it rather than re-deriving a name from inputs it can no longer see.
        /// Null for a property that was never emitted.
        /// </summary>
        public string? EmittedCSharpName { get; private set; }

        /// <summary>
        /// Records the C# name this property was emitted under. The emission-time mutation entry
        /// point for <see cref="EmittedCSharpName"/>.
        /// </summary>
        public void MarkEmittedCSharpName(string csharpName) => EmittedCSharpName = csharpName;

        /// <summary>
        /// Puts <see cref="EmittedCSharpName"/> back to a previously captured value, null included.
        /// Exists for the verify-recover rollback, which has to return the declaration to exactly
        /// the state it held before a render that was then thrown away: a stamp surviving from a
        /// discarded attempt would be published in the module database's rename ledger as a member
        /// the final output never wrote.
        /// </summary>
        public void RestoreEmittedCSharpName(string? csharpName) => EmittedCSharpName = csharpName;

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
        /// Whether this property is `@objc dynamic` — i.e. ABI JSON declAttributes
        /// contain both "ObjC" and "Dynamic". These are the properties that participate
        /// in Foundation's KVO machinery on NSObject subclasses, and the only ones the
        /// generator can wire `observe(_:options:changeHandler:)` extension methods to.
        /// </summary>
        public bool IsObjCDynamic { get; set; } = false;

        /// <summary>
        /// Whether this property is a protocol requirement (protocolReq=true in ABI JSON).
        /// Mirrors <see cref="MethodDecl.IsProtocolRequirement"/>. Required protocol properties
        /// must have a witness in EveryProtocol's conformance — if a required property is
        /// dropped by suppression (SPI, module-internal) or fails to parse, the conformance
        /// itself must be skipped to avoid emitting an unsatisfiable extension.
        /// </summary>
        public bool IsProtocolRequirement { get; set; } = false;

        /// <summary>
        /// Whether this property is defined in a Swift extension (isFromExtension in ABI JSON).
        /// Mirrors <see cref="MethodDecl.IsExtensionMethod"/>. When a property is both
        /// <c>IsFromExtension</c> and not <c>IsProtocolRequirement</c>, it is a protocol-extension
        /// default (often <c>@_alwaysEmitIntoClient</c>) that is inlined at the Swift call site
        /// and is NOT part of the protocol's abstract contract — conforming types must not be
        /// required to implement it.
        /// </summary>
        public bool IsFromExtension { get; set; } = false;

        /// <summary>
        /// Setter-specific availability annotations when the setter is restricted to a
        /// newer platform than the property getter. Read from the ABI JSON's
        /// <c>intro_iOS</c>/<c>intro_Macosx</c>/etc. fields on the set accessor node and
        /// merged with the property-level availability. When null, the setter inherits
        /// <see cref="BaseDecl.AvailabilityAnnotations"/> from the property.
        /// </summary>
        public IReadOnlyList<AvailabilityAnnotation>? SetterAvailabilityAnnotations { get; set; }

        /// <summary>
        /// The reference-ownership qualifier on the property's storage (<c>strong</c> by default,
        /// or <c>weak</c>/<c>unowned</c>/<c>unowned(unsafe)</c>). Read from the ABI JSON Var node's
        /// <c>ownership</c> field, which both ABI producers spell identically.
        /// </summary>
        /// <remarks>
        /// Load-bearing for marshalling a proxied existential INTO the property: a non-retaining
        /// sink stores the value without taking a strong reference, so nothing on the Swift side
        /// keeps the conformer box alive after the setter returns.
        /// </remarks>
        public SwiftReferenceOwnership ReferenceOwnership { get; set; } = SwiftReferenceOwnership.Strong;
    }

    /// <summary>
    /// Reference-ownership qualifier on a Swift stored property, mirroring the Swift compiler's
    /// <c>ReferenceOwnership</c> enum. The ABI JSON carries the raw integer in a Var node's
    /// <c>ownership</c> field and lists <c>ReferenceOwnership</c> in its <c>declAttributes</c>;
    /// a strong property omits both.
    /// </summary>
    public enum SwiftReferenceOwnership
    {
        /// <summary>Default storage: the property retains its value.</summary>
        Strong = 0,

        /// <summary><c>weak var</c> — zeroing, non-retaining. Always Optional in Swift.</summary>
        Weak = 1,

        /// <summary><c>unowned var</c> — non-retaining with a runtime liveness check.</summary>
        Unowned = 2,

        /// <summary><c>unowned(unsafe) var</c> — non-retaining, unchecked.</summary>
        Unmanaged = 3,
    }
}
