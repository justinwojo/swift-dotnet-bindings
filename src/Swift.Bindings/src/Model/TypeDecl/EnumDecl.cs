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
        /// The explicit raw value for this enum case, extracted from .swiftinterface and
        /// stored verbatim as a string: the unquoted content for a String-raw-value enum
        /// (<c>case x = "foo"</c>), or the base-10 spelling for an integer-raw-value enum
        /// (<c>case x = 17009</c>; hex/octal/binary/underscored/negative source forms are
        /// already normalized to decimal by the .swiftinterface parser). Null when no
        /// explicit raw value was declared for this case — a String enum then falls back to
        /// the case name, and an integer enum to Swift's auto-increment rule (see
        /// <see cref="EnumDecl.GetCaseMarshalScalar"/>).
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
        /// Whether this enum is RawRepresentable with an integral (Int-family) raw value
        /// type. These are the enums whose explicit Swift source raw values (e.g.
        /// <c>case x = 17009</c>) should surface as the emitted C# enum member value AND
        /// as the @_cdecl marshalling scalar — see <see cref="GetCaseMarshalScalar"/>.
        /// </summary>
        public bool IsIntegralRawRepresentable => IsRawRepresentable && IsIntegralRawValue();

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

        /// <summary>
        /// Gets the scalar value that both (a) the emitted C# enum member is assigned and
        /// (b) the @_cdecl wrapper uses to marshal this case across the boundary. The two
        /// MUST agree, so a single source computes both.
        ///
        /// For an integral RawRepresentable enum whose explicit Swift raw values are known
        /// (parsed from the .swiftinterface into <see cref="EnumCaseDecl.RawValue"/>), this
        /// is the Swift source raw value — so <c>(long)MyEnum.Case</c> equals the Swift
        /// <c>.rawValue</c> and matches the bridged ObjC NS_ENUM constant. Cases without an
        /// explicit raw value follow Swift's auto-increment rule (previous case's value + 1,
        /// the first implicit case being 0), exactly as the compiler assigns them. For every
        /// other enum (no raw value, or a non-integral raw value such as String), this is the
        /// declaration-order tag from <see cref="GetCaseTag"/> — the in-memory discriminant
        /// the marshalling switch keys on.
        ///
        /// The raw-value scalar is a signed 64-bit value. If any case's value — an explicit
        /// raw value, OR the implicit auto-increment between cases — falls outside that range
        /// (a UInt64/UInt case above <see cref="long.MaxValue"/>, or an implicit case that would
        /// auto-increment past it), the value cannot be carried faithfully as a <c>long</c>, so
        /// the WHOLE enum degrades to declaration-order tags rather than emit a wrapped (negative)
        /// scalar — keeping the emitted C# member and the @_cdecl switch consistent by construction
        /// and never producing a negative literal a ulong-backed C# enum cannot hold. (Full
        /// unsigned-64-bit raw values are not yet supported end-to-end.)
        /// </summary>
        public long GetCaseMarshalScalar(EnumCaseDecl target)
        {
            if (!IsIntegralRawRepresentable || !IntegralRawValuesRepresentableAsInt64())
                return GetCaseTag(target);

            // Walk cases in declaration order, mirroring Swift's raw-value assignment: an
            // explicit `= N` resets the running value; an implicit case takes prev + 1 (the
            // first implicit case is 0). @_spi cases still consume a slot, so they are not
            // skipped here even though they are omitted from emission.
            long running = 0;
            bool first = true;
            foreach (var c in Cases)
            {
                if (c.RawValue is string s &&
                    long.TryParse(s, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var explicitVal))
                {
                    running = explicitVal;
                }
                else if (!first)
                {
                    running += 1;
                }

                if (ReferenceEquals(c, target))
                    return running;
                first = false;
            }

            // Target not among this enum's cases (shouldn't happen) — fall back to the tag.
            return GetCaseTag(target);
        }

        /// <summary>
        /// Whether this integral enum's per-case scalars all fit the signed 64-bit range
        /// <see cref="GetCaseMarshalScalar"/> marshals through. Simulates the same declaration-order
        /// walk (explicit <c>= N</c> resets the running value; an implicit case is previous + 1,
        /// the first implicit being 0) and returns false if either an explicit raw value cannot be
        /// parsed as an <see cref="long"/> (a UInt64/UInt value above <see cref="long.MaxValue"/>)
        /// OR an implicit case would auto-increment past <see cref="long.MaxValue"/>. Either makes
        /// the whole enum degrade to tags, so no case ever emits a wrapped (negative) scalar.
        /// </summary>
        private bool IntegralRawValuesRepresentableAsInt64()
        {
            long running = 0;
            bool first = true;
            foreach (var c in Cases)
            {
                if (c.RawValue is string s)
                {
                    if (!long.TryParse(s, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var explicitVal))
                    {
                        return false;
                    }
                    running = explicitVal;
                }
                else if (!first)
                {
                    if (running == long.MaxValue)
                        return false;
                    running += 1;
                }

                first = false;
            }
            return true;
        }
    }
}
