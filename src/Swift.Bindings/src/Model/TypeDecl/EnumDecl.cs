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
        ///
        /// There is deliberately no per-case module-internal flag to go with this:
        /// Swift forbids access-control modifiers on enum cases (a case always has
        /// its enum's access level), so a module-internal case can only exist inside
        /// a module-internal enum — which the enum-level IsModuleInternal gate skips
        /// before any case is considered. @_spi is the one per-case visibility
        /// attribute Swift allows, hence the one flag carried here.
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

        /// <summary>
        /// Whether the case is declared <c>indirect</c>, so its payload lives in a heap box and the
        /// enum stores only the box reference. <see cref="EnumDecl.IsIndirect"/> covers every case of
        /// an enum declared <c>indirect</c> as a whole.
        /// </summary>
        public bool IsIndirect { get; set; }
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
        /// Whether the enum is declared <c>@objc</c>. Such an enum is a C enum of its integer raw
        /// type: stored as the raw value itself, with a fixed layout even when not
        /// <c>@frozen</c>.
        /// </summary>
        public bool IsObjC { get; set; }

        /// <summary>
        /// Whether the enum is declared <c>indirect</c>, which boxes every case's payload: the enum
        /// stores box references, never a payload inline.
        /// </summary>
        public bool IsIndirect { get; set; }

        /// <summary>
        /// Whether the enum raises its own alignment past what its payloads require (Swift's
        /// <c>@_alignment(N)</c>). As on a struct, the ABI descriptor records only that the
        /// attribute is present, never N, so the offset the enum takes inside a containing value
        /// is unknown even when its size is measured.
        /// </summary>
        public bool HasCustomAlignment { get; set; }

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
        /// Whether a value of this integral RawRepresentable enum crosses the native boundary as its
        /// case tag rather than its Swift raw value. True for every such enum that is not
        /// <c>@objc</c>.
        /// <para>
        /// A non-<c>@objc</c> enum's <c>.swiftinterface</c> prints no raw values (<c>case light = 1</c>
        /// prints as <c>case light</c>, <c>@frozen</c> or not), and neither the ABI descriptor nor
        /// swift-api-digester's output, both built from that interface, carries them. Library
        /// evolution lets the author change them without breaking ABI, so no generation-time source
        /// knows them for certain. Its in-memory value is the declaration-order case tag, which is
        /// what the container, Optional and metadata paths read. So the C# constant is that tag on
        /// every crossing, and the raw value is offered only through Swift's own <c>.rawValue</c> and
        /// <c>init(rawValue:)</c> (the generated <c>ToRawValue</c>/<c>FromRawValue</c>).
        /// </para>
        /// <para>
        /// An <c>@objc</c> enum is a C enum: the interface prints its raw values (Swift's implicit
        /// raw values follow the C auto-increment rule) and its in-memory value is the raw value
        /// itself, so it keeps crossing as the raw value.
        /// </para>
        /// </summary>
        public bool CrossesAsCaseTag => IsIntegralRawRepresentable && !IsObjC;

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
        /// Whether the managed identity of each case (the value of its <c>CaseTag</c> constant) is
        /// derived from the case's name (<see cref="GetCaseId"/>) and translated to the running
        /// library's tag when the binding runs, rather than being the declaration-order tag.
        /// <para>
        /// True for every enum that is not <c>@frozen</c> and whose cases all carry a mangled name.
        /// Such an enum may be resilient: the library that defines it owns its case tags, so a newer
        /// library may insert a case anywhere and renumber every case after it (Swift exports each
        /// case's tag as a symbol for exactly this reason). A declaration-order constant then names
        /// another case on an OS other than the one the SDK describes, and differs between the copies
        /// of one package built from different SDKs, while a library compiled against one copy keeps
        /// that copy's constants in an app that runs another. A <c>@frozen</c> enum's tag order is its
        /// ABI, the same in every SDK and on every OS, so it keeps its tag as its constant.
        /// </para>
        /// </summary>
        public bool HasNameKeyedCaseIds => !IsFrozen && Cases.Count > 0 && Cases.All(c => !string.IsNullOrEmpty(c.MangledName));

        /// <summary>
        /// The managed identity of a case: its declaration-order tag (<see cref="GetCaseTag"/>), or, for
        /// an enum with <see cref="HasNameKeyedCaseIds"/>, the value <see cref="AssignNameKeyedIds"/>
        /// derives from the case names, so every binding of the enum, from every SDK, gives the case the
        /// same value.
        /// </summary>
        public uint GetCaseId(EnumCaseDecl caseDecl)
        {
            if (!HasNameKeyedCaseIds)
                return (uint)GetCaseTag(caseDecl);
            return NameKeyedIdOf(caseDecl);
        }

        /// <summary>
        /// Whether this simple enum's C# constants (<see cref="GetCaseMarshalScalar"/>) are keyed by
        /// case name rather than declaration order: every simple enum that crosses by case identity
        /// (no raw value, or an integral one a non-<c>@objc</c> enum's interface does not print), is not
        /// <c>@frozen</c>, and whose C# underlying type is at least 32 bits wide.
        /// <para>
        /// Such an enum may be resilient, so a newer SDK may insert a case anywhere. Its crossings go
        /// through the wrapper's case switch, which names each case, so the constants need not be
        /// tags; a declaration-order constant would still move when a case is inserted, and a library
        /// compiled against one copy of a package holds the old value as a literal in an app that runs
        /// another copy. A name-keyed constant is the same in every copy. A <c>@frozen</c> enum's case
        /// order is its ABI and keeps its declaration order; an <c>@objc</c> enum crosses as its raw
        /// value. An 8- or 16-bit enum keeps declaration order: a name-derived value does not fit its
        /// width without colliding, and the merge's compiled-in value check refuses a copy that
        /// renumbers one.
        /// </para>
        /// </summary>
        public bool HasNameKeyedCaseValues =>
            IsSimpleEnum && !IsFrozen && Cases.Count > 0
            && (!IsIntegralRawRepresentable || CrossesAsCaseTag)
            && EnumHandler.GetCSharpEnumUnderlyingType(RawValueTypeName) is "int" or "uint" or "long" or "ulong";

        // Not cached: a record's fields take part in its equality, and the assignment is linear.
        private uint NameKeyedIdOf(EnumCaseDecl caseDecl)
            => AssignNameKeyedIds(Cases).TryGetValue(caseDecl, out var id)
                ? id
                : throw new ArgumentException($"{caseDecl.Name} is not a case of {Name}.", nameof(caseDecl));

        /// <summary>
        /// The name-keyed identity of each case: 31-bit FNV-1a of its UTF-8 name
        /// (<see cref="NameKeyedCaseId"/>) when no other case's name hashes alike. Cases whose names hash
        /// alike form a collision group; each of its members, in ordinal order of name and mangled name,
        /// takes the first salted hash of its own name (<c>name + "\0" + k</c>, k = 1, 2, …) that is no
        /// case's unsalted hash and no value already taken. A name whose hash is 0 is salted the same
        /// way: 0 is <c>default</c> of the C# enum, which names no case.
        /// <para>
        /// The rule depends only on the colliding names, so a case a newer SDK adds can move an
        /// existing case's value only when its own name hashes exactly like that case's name (or like a
        /// colliding case's salted value): with 31 bits that is about one chance in two billion per
        /// added case and existing case. A value that moves anyway is refused by the merge's
        /// compiled-in value check (SWIFTBIND138), so it never reaches a consumer. Linear probing to the
        /// next free value, the earlier rule, also moved a colliding case when an unrelated added case
        /// hashed to the value it had probed to.
        /// </para>
        /// </summary>
        internal static IReadOnlyDictionary<EnumCaseDecl, uint> AssignNameKeyedIds(IReadOnlyList<EnumCaseDecl> cases)
        {
            var ids = new Dictionary<EnumCaseDecl, uint>(ReferenceEqualityComparer.Instance);
            var bases = cases.ToLookup(c => NameKeyedCaseId(c.Name));
            // 0 is default(E), which must name no case.
            var taken = new HashSet<uint>(bases.Select(g => g.Key)) { 0 };
            foreach (var group in bases.OrderBy(g => g.Key))
            {
                if (group.Count() == 1 && group.Key != 0)
                {
                    ids[group.First()] = group.Key;
                    continue;
                }
                foreach (var c in group.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.MangledName, StringComparer.Ordinal))
                {
                    for (var k = 1; ; k++)
                    {
                        var salted = NameKeyedCaseId($"{c.Name}\0{k.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                        if (taken.Add(salted))
                        {
                            ids[c] = salted;
                            break;
                        }
                    }
                }
            }
            return ids;
        }

        /// <summary>The name-derived case identity before collision resolution: 31-bit FNV-1a of the UTF-8 name.</summary>
        internal static uint NameKeyedCaseId(string caseName)
        {
            var hash = 2166136261u;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(caseName))
                hash = unchecked((hash ^ b) * 16777619u);
            return hash & 0x7FFF_FFFFu;
        }

        /// <summary>
        /// The symbol of a case's "enum case" global (its mangled name with the <c>WC</c> suffix),
        /// which holds the tag the library that defines a resilient enum gives the case.
        /// </summary>
        public static string GetCaseTagSymbol(EnumCaseDecl caseDecl) => caseDecl.MangledName + "WC";

        /// <summary>
        /// Gets the scalar value that both (a) the emitted C# enum member is assigned and
        /// (b) the @_cdecl wrapper uses to marshal this case across the boundary. The two
        /// MUST agree, so a single source computes both.
        ///
        /// For an <c>@objc</c> integral RawRepresentable enum, whose explicit Swift raw values are
        /// known (parsed from the .swiftinterface into <see cref="EnumCaseDecl.RawValue"/>), this
        /// is the Swift source raw value — so <c>(long)MyEnum.Case</c> equals the Swift
        /// <c>.rawValue</c> and matches the bridged ObjC NS_ENUM constant. Cases without an
        /// explicit raw value follow Swift's auto-increment rule (previous case's value + 1,
        /// the first implicit case being 0), exactly as the compiler assigns them. For every
        /// other enum (no raw value, a non-integral raw value such as String, or a
        /// non-<c>@objc</c> integral enum whose raw values no input carries — see
        /// <see cref="CrossesAsCaseTag"/>), this is the
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
            if (HasNameKeyedCaseValues)
                return NameKeyedIdOf(target);
            if (!IsIntegralRawRepresentable || CrossesAsCaseTag || !IntegralRawValuesRepresentableAsInt64())
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
