// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Represents a single enum case (element) declaration.
    /// </summary>
    public sealed record EnumCaseDecl : BaseDecl
    {
        /// <summary>
        /// The mangled name for this enum case, used for P/Invoke.
        /// </summary>
        public required string MangledName { get; set; }

        /// <summary>
        /// The associated value types for this enum case, if any.
        /// Empty for simple enum cases without associated values.
        /// </summary>
        public List<TypeSpec> AssociatedValues { get; set; } = new();

        /// <summary>
        /// Whether this case has associated values.
        /// </summary>
        public bool HasAssociatedValues => AssociatedValues.Count > 0;

        /// <summary>
        /// Whether this enum case is marked @_spi (System Programming Interface).
        /// @_spi enum cases are only visible to SPI consumers and should not appear
        /// in generated bindings or Swift wrapper switch statements.
        /// </summary>
        public bool IsSpiProtected { get; set; } = false;

        /// <summary>
        /// The string raw value for this enum case, extracted from .swiftinterface.
        /// Null if the enum is not a string raw value enum or the value was not found.
        /// When null, the case name is used as the raw value (Swift default behavior).
        /// </summary>
        public string? RawValue { get; set; }

        /// <summary>
        /// When the source declaration was <c>case foo(label: (a:, b:, ...))</c> — i.e. a single
        /// associated value that is itself a labeled tuple — TypeSpecParser unwraps the outer
        /// one-element tuple and the ABI parser flattens the inner tuple's elements into
        /// <see cref="AssociatedValues"/>. The outer label survives here so the @_cdecl wrapper
        /// can reconstruct the call as <c>EnumType.foo(label: (a: ..., b: ...))</c> rather than
        /// the malformed <c>EnumType.foo(a: ..., b: ...)</c>. Null when no outer label exists.
        /// </summary>
        public string? OuterTupleLabel { get; set; }

        /// <summary>
        /// When the source declaration was <c>case foo((A, B, ...))</c> — i.e. a single,
        /// UNLABELED associated value that is itself a tuple — the ABI represents the
        /// associated-value list identically to <c>case foo(A, B, ...)</c> (N separate
        /// values): both surface as one Tuple node with N children. Only the enum-case
        /// function type's printedName tells them apart by paren nesting
        /// (<c>((A, B)) -&gt; Enum</c> vs <c>(A, B) -&gt; Enum</c>). The ABI parser flattens
        /// the tuple's elements into <see cref="AssociatedValues"/> either way; this flag
        /// records that they must be re-wrapped into a single tuple so the @_cdecl wrapper
        /// emits <c>EnumType.foo((a, b))</c> rather than the malformed <c>EnumType.foo(a, b)</c>
        /// (which Swift rejects: "enum case 'foo' expects a single parameter of type '(A, B)'").
        /// The labeled counterpart is carried by <see cref="OuterTupleLabel"/> instead.
        /// </summary>
        public bool IsSingleTuplePayload { get; set; }
    }

    /// <summary>
    /// Represents an enum declaration.
    /// </summary>
    public sealed record EnumDecl : TypeDecl
    {
        /// <summary>
        /// The enum cases (elements) declared in this enum.
        /// </summary>
        public List<EnumCaseDecl> Cases { get; set; } = new();

        /// <summary>
        /// Whether this enum has any cases with associated values.
        /// </summary>
        public bool HasAssociatedValueCases => Cases.Any(c => c.HasAssociatedValues);

        /// <summary>
        /// Whether the enum is frozen (has a stable ABI layout).
        /// </summary>
        public required bool IsFrozen { get; set; }

        /// <summary>
        /// Protocol conformances for this enum.
        /// </summary>
        public required List<TypeConformance> Conformances { get; set; }

        /// <summary>
        /// Metadata accessor function name for this enum.
        /// </summary>
        public required string MetadataAccessor { get; set; }

        /// <summary>
        /// The raw value type name for RawRepresentable enums (e.g., "Int", "String").
        /// Null if the enum does not conform to RawRepresentable.
        /// Normalized to the unqualified stdlib spelling on assignment so the bare-only
        /// classification switches below (and downstream consumers) stay correct regardless
        /// of the assignment source — see <see cref="TypeSpecHelpers.NormalizeRawValueTypeName"/>.
        /// </summary>
        public string? RawValueTypeName
        {
            get => _rawValueTypeName;
            set => _rawValueTypeName = TypeSpecHelpers.NormalizeRawValueTypeName(value);
        }
        private string? _rawValueTypeName;

        /// <summary>
        /// Whether this enum conforms to RawRepresentable.
        /// </summary>
        public bool IsRawRepresentable => !string.IsNullOrEmpty(RawValueTypeName);

        /// <summary>
        /// Whether this enum qualifies for emission as a C# enum value type.
        /// Requires: no associated values, non-generic, and either no raw value
        /// or an integral raw value type. Non-frozen enums are included because
        /// no-payload enums are always register-sized and tag values are stable
        /// within a given ABI JSON + compiled wrapper pair.
        /// </summary>
        public bool IsSimpleEnum =>
            !HasAssociatedValueCases &&
            !IsGeneric &&
            (!IsRawRepresentable || IsIntegralRawValue());

        /// <summary>
        /// Checks if the raw value type is an integral type suitable for C# enum underlying type.
        /// </summary>
        private bool IsIntegralRawValue()
        {
            return RawValueTypeName switch
            {
                "Int" or "UInt" or "Int8" or "UInt8" or "Int16" or "UInt16" or
                "Int32" or "UInt32" or "Int64" or "UInt64" => true,
                _ => false
            };
        }

        /// <summary>
        /// Whether the raw value type is String.
        /// </summary>
        public bool IsStringRawValue => RawValueTypeName == "String";

        /// <summary>
        /// Whether this enum qualifies for emission as a C# enum with String raw value conversions.
        /// Requires: no associated values, frozen, non-generic, String raw value.
        /// IsFrozen stays — non-frozen String enums use indirect return patterns requiring SafeHandle.
        /// Member-level gates (properties, static methods, operators) are now handled by
        /// CanSafelyEmitAsSimpleEnum and the extension emission path.
        /// </summary>
        public bool IsStringRawValueSimpleEnum =>
            !HasAssociatedValueCases &&
            IsFrozen &&
            !IsGeneric &&
            IsStringRawValue;

        /// <summary>
        /// Gets the enum cases that have associated values (payload cases).
        /// Swift orders these first in the tag sequence.
        /// </summary>
        public IEnumerable<EnumCaseDecl> PayloadCases => Cases.Where(c => c.HasAssociatedValues);

        /// <summary>
        /// Gets the enum cases that have no associated values (simple cases).
        /// Swift orders these after payload cases in the tag sequence.
        /// </summary>
        public IEnumerable<EnumCaseDecl> NoPayloadCases => Cases.Where(c => !c.HasAssociatedValues);

        /// <summary>
        /// Whether this is a caseless enum (zero cases). In Swift, caseless enums cannot be
        /// instantiated and are used as namespace containers and/or holders of static members.
        /// These should be emitted as static classes rather than ISwiftObject wrappers.
        /// </summary>
        public bool IsNamespaceEnum => Cases.Count == 0;

        /// <summary>
        /// Gets the tag value for a given case declaration.
        /// Swift assigns tags in declaration order, with payload cases first (starting at 0),
        /// followed by no-payload cases.
        /// </summary>
        /// <param name="caseDecl">The enum case to get the tag for.</param>
        /// <returns>The tag value, or -1 if the case is not found in this enum.</returns>
        public int GetCaseTag(EnumCaseDecl caseDecl)
        {
            var payloadList = PayloadCases.ToList();
            int payloadIndex = payloadList.IndexOf(caseDecl);
            if (payloadIndex >= 0)
                return payloadIndex;

            var noPayloadList = NoPayloadCases.ToList();
            int noPayloadIndex = noPayloadList.IndexOf(caseDecl);
            if (noPayloadIndex >= 0)
                return payloadList.Count + noPayloadIndex;

            return -1;
        }
    }
}
