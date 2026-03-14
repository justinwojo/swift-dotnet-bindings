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
        /// </summary>
        public string? RawValueTypeName { get; set; }

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
