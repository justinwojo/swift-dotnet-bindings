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
        /// Requires: no associated values, frozen, non-generic, and either no raw value
        /// or an integral raw value type.
        /// </summary>
        public bool IsSimpleEnum =>
            !HasAssociatedValueCases &&
            IsFrozen &&
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
        /// Requires: no associated values, frozen, non-generic, String raw value,
        /// and no methods/properties/operators (pure discriminated-value enums only).
        /// Enums with methods keep the class-based emission to support method wrappers.
        /// </summary>
        public bool IsStringRawValueSimpleEnum =>
            !HasAssociatedValueCases &&
            IsFrozen &&
            !IsGeneric &&
            IsStringRawValue &&
            Methods.Count(m => !m.IsConstructor) == 0 &&
            Properties.Count == 0 &&
            Operators.Count == 0;

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
