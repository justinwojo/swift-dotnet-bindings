// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace BindingsGeneration;

/// <summary>
/// One case of a simple enum that crosses by case identity: its Swift name (unescaped), the C#
/// constant it carries (<see cref="EnumDecl.GetCaseMarshalScalar"/>), and the <c>#available</c>
/// clause that must hold before Swift may name it (null when the case is as available as its enum).
/// </summary>
public sealed record SimpleEnumCrossingCase(string SwiftName, long Value, string? AvailableClause);

/// <summary>
/// What a crossing site needs to map a simple enum that does not cross as its raw value between its
/// C# constant and its Swift case: the cases by name, the number of C# constant slots the enum
/// reserves, whether the enum is frozen, its own availability, and the bit width of its C# underlying
/// type. <see cref="Cases"/> is null on a record read from a module database that predates the case
/// list, whose values then cross as the in-memory tag (<see cref="LegacyTagCount"/>) as they always did.
/// <see cref="SwiftTypeName"/> is the enum's module-qualified Swift name, which the helpers are spelled
/// with whatever a call site calls the type (an unqualified name can be ambiguous in the wrapper).
/// </summary>
public sealed record SimpleEnumCaseMap(
    IReadOnlyList<SimpleEnumCrossingCase>? Cases,
    int SlotCount,
    bool IsFrozen,
    IReadOnlyList<AvailabilityAnnotation>? Availability,
    int WidthBits,
    int LegacyTagCount,
    string? SwiftTypeName = null);

/// <summary>
/// The one mapping between a simple enum's C# constant and its Swift case, shared by every site
/// that hands such a value across the native boundary in either direction (arguments, returns,
/// properties, subscripts, closures and callbacks, extension trampolines, the SwiftUI bridge, the
/// enum's own members).
/// <para>
/// A value crosses either as Swift's <c>.rawValue</c> / <c>init(rawValue:)</c> or by case identity.
/// Which one is a property of the enum, decided once (<see cref="CrossesAsRawValue"/>): the raw value
/// only where it is known for certain and is the in-memory value too (an <c>@objc</c> enum, an ObjC
/// NS_ENUM, an Apple enum synthesized from the managed surface); case identity for every other simple
/// enum, including a plain Swift <c>enum E: Int</c> whose raw values its interface does not print.
/// </para>
/// <para>
/// <b>Case identity.</b> The C# constant of a case is its declaration-order index in the interface
/// the binding was generated from. Swift's in-memory tag of the same case agrees with it only when
/// the binary has exactly those cases in that order, which a library-evolution module does not
/// promise: an <c>@_spi</c> or otherwise hidden case shifts every later tag, and a newer OS release
/// may insert a case. So the wrapper never loads a constant as enum bytes. Each enum gets one helper
/// pair, emitted once per Swift output file (<see cref="AppendHelperDefinitions"/>) and compiled
/// against the same interface the C# came from: <c>__sbCaseOf_*</c> switches on the constant and
/// names the case, and <c>__sbIndexOf_*</c> switches on the case and returns its constant. A
/// library-evolution client resolves a non-frozen enum's case tags at run time, so both stay right
/// whatever the running binary's layout is.
/// </para>
/// <para>
/// <b>A case the binding does not know</b> (one a newer OS added, or one the interface hides) never
/// becomes a known case. It reaches C# as an unnamed value that round-trips: the bit pattern
/// <c>mask - 1 - rank</c> at the width of the C# enum's underlying type (<c>mask</c> all ones),
/// where <c>rank</c> counts the running binary's unknown cases below it in tag order. The codes
/// count down from the top of the unsigned range and the published constants up from zero, so the
/// two never meet while any unused pattern is left; <c>Enum.IsDefined</c> is false for such a value.
/// Passed back into Swift, it becomes the same case again: the helper reads the enum's runtime
/// metadata (no payload cases, its empty-case count), walks the binary's tags to the unknown case
/// of that rank, and refuses anything else the way the site refuses an invalid constant. The value
/// is meaningful only within one process and binary. The all-ones pattern is a sentinel for an
/// unknown case that cannot be encoded that way (the enum gained a payload case, or no pattern is
/// left) and is refused on the way back.
/// </para>
/// <para>
/// <b>In Swift memory.</b> Where the value is handed over as Swift memory rather than as a scalar
/// (container elements, tuple elements, <c>Optional</c> payloads, async results, reverse-dispatch
/// slots), the wrapper writes and reads Swift's own layout, and the runtime moves the value through
/// the enum's case codec (<see cref="EmitCaseCodec"/>), four <c>@_cdecl</c> functions built on the
/// same helpers that the binding's module initializer registers with the runtime.
/// </para>
/// </summary>
internal static class SimpleEnumCrossing
{
    private const string CaseOfPrefix = "__sbCaseOf_";
    private const string IndexOfPrefix = "__sbIndexOf_";
    private const string KnownIndexOfPrefix = "__sbKnownIndexOf_";
    private const string SharedHelpersToken = "__sbUnknownCase";

    // Content-addressed: an id embeds a hash of its definition, so one id always names one
    // definition and concurrent emissions (or unit tests) cannot disagree about it.
    private static readonly ConcurrentDictionary<string, string> s_definitions = new(StringComparer.Ordinal);

    private static readonly Regex s_helperReference = new(
        @"\b(__sbCaseOf_|__sbIndexOf_|__sbKnownIndexOf_)([A-Za-z0-9_]+)\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether a simple enum's value crosses as Swift's <c>.rawValue</c>. False for an enum with no
    /// C-scalar raw value (no raw value, or a Bool, floating-point or String one) and for an integral
    /// enum whose record says its values cross as the case tag. A record no longer a simple enum (one
    /// demoted to a class projection, which passes <c>.RawValue</c>) crosses as the raw value.
    /// </summary>
    public static bool CrossesAsRawValue(TypeRecord record)
        => CdeclParamMapper.HasCdeclScalarRawValue(record.RawValueTypeName)
           && !(record.RawValueCrossesAsCaseTag && record.Flags.HasFlag(TypeRecordFlags.SimpleEnum));

    /// <summary>
    /// Whether an enum declaration's values cross as its raw value: the decl-side twin of
    /// <see cref="CrossesAsRawValue(TypeRecord)"/>, used where the record is being built.
    /// </summary>
    public static bool CrossesAsRawValue(EnumDecl enumDecl)
        => CdeclParamMapper.HasCdeclScalarRawValue(enumDecl.RawValueTypeName) && !enumDecl.CrossesAsCaseTag;

    /// <summary>
    /// The case list a simple enum crossing by identity records (<see cref="TypeRecord.CrossingCases"/>):
    /// every case Swift can name from the wrapper, with its C# constant and the availability clause a
    /// case floored above the enum needs. An <c>@_spi</c> case is left out (the wrapper cannot name
    /// it), but still holds its constant slot (<see cref="SlotCount(EnumDecl)"/>).
    /// </summary>
    public static IReadOnlyList<SimpleEnumCrossingCase> BuildCases(EnumDecl enumDecl)
    {
        var cases = new List<SimpleEnumCrossingCase>();
        foreach (var caseDecl in enumDecl.Cases)
        {
            if (caseDecl.IsSpiProtected || caseDecl.HasAssociatedValues)
                continue;
            var floor = EnumHandler.GetTighterCaseAvailability(caseDecl, enumDecl);
            cases.Add(new SimpleEnumCrossingCase(
                caseDecl.Name,
                enumDecl.GetCaseMarshalScalar(caseDecl),
                floor.Count > 0 ? EnumHandler.BuildIfAvailableClause(floor) : null));
        }
        return cases;
    }

    /// <summary>The number of C# constant slots an enum reserves: one per declared case.</summary>
    public static int SlotCount(EnumDecl enumDecl) => enumDecl.Cases.Count;

    /// <summary>
    /// The case map of a simple enum that crosses by identity, or null when it crosses as its raw
    /// value (or is not a simple enum).
    /// </summary>
    public static SimpleEnumCaseMap? GetCaseMap(TypeRecord record)
    {
        if (record.Kind != TypeRecordKind.Enum || !record.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            return null;
        if (record.Flags.HasFlag(TypeRecordFlags.OptionSet) || CrossesAsRawValue(record))
            return null;
        return new SimpleEnumCaseMap(
            record.CrossingCases,
            record.CrossingSlotCount,
            record.Flags.HasFlag(TypeRecordFlags.Frozen),
            record.AvailabilityAnnotations,
            UnderlyingWidthBits(EnumHandler.GetCSharpEnumUnderlyingType(record.RawValueTypeName)),
            record.CaseTagCount,
            record.SwiftTypeName.ModuleQualifiedName);
    }

    /// <summary>
    /// The case map of an enum declaration crossing by identity, for the enum's own member wrappers;
    /// null when it crosses as its raw value.
    /// </summary>
    public static SimpleEnumCaseMap? GetCaseMap(EnumDecl enumDecl)
    {
        if (CrossesAsRawValue(enumDecl) || enumDecl.HasAssociatedValueCases)
            return null;
        return new SimpleEnumCaseMap(
            BuildCases(enumDecl),
            SlotCount(enumDecl),
            enumDecl.IsFrozen,
            AvailabilityHelpers.MergeAvailabilityFromAncestors(null, enumDecl),
            UnderlyingWidthBits(EnumHandler.GetCSharpEnumUnderlyingType(enumDecl.RawValueTypeName)),
            enumDecl.Cases.Count,
            enumDecl.SwiftTypeName.ModuleQualifiedName);
    }

    /// <summary>The bit width of a C# enum underlying type name.</summary>
    internal static int UnderlyingWidthBits(string csharpUnderlyingType) => csharpUnderlyingType switch
    {
        "sbyte" or "byte" => 8,
        "short" or "ushort" => 16,
        "long" or "ulong" => 64,
        _ => 32,
    };

    /// <summary>
    /// A Swift expression of type <c>Optional&lt;<paramref name="swiftEnumType"/>&gt;</c>: the case the
    /// integer <paramref name="scalarExpr"/> names, nil for a value that names no case (a constant
    /// out of range, a case floored above the running OS, or a value that is not one Swift handed out
    /// for a case the binding does not know). A null <paramref name="map"/> means the enum crosses as
    /// its raw value, and the expression is Swift's <c>init(rawValue:)</c>.
    /// </summary>
    public static string ScalarToOptionalCase(SimpleEnumCaseMap? map, string swiftEnumType, string scalarExpr)
    {
        if (map is null)
            return $"{swiftEnumType}(rawValue: {scalarExpr})";
        if (map.Cases is null)
            return LegacyScalarToOptionalCase(map.LegacyTagCount, swiftEnumType, scalarExpr);
        var id = Register(map, map.SwiftTypeName ?? swiftEnumType);
        return $"{CaseOfPrefix}{id}(Swift.Int64(truncatingIfNeeded: {scalarExpr}))";
    }

    /// <summary>
    /// A Swift expression of type <paramref name="swiftEnumType"/>: the case <paramref name="scalarExpr"/>
    /// names, trapping with a message naming the enum when it names none. For sites with no channel to
    /// refuse a value (a callback's return, a closure argument): the managed side only produces
    /// published constants and values Swift handed out, so the trap marks a fabricated value.
    /// </summary>
    public static string ScalarToCaseOrTrap(SimpleEnumCaseMap map, string swiftEnumType, string scalarExpr)
    {
        if (map.Cases is null)
            return LegacyTagToCase(swiftEnumType, scalarExpr);
        // The scalar is evaluated once (it may be a call), then named in the trap message.
        var typeName = map.SwiftTypeName ?? swiftEnumType;
        return $"{{ () -> {typeName} in let __sbValue = {scalarExpr}; guard let __sbCase = {ScalarToOptionalCase(map, typeName, "__sbValue")} "
             + $"else {{ Swift.preconditionFailure(\"[SwiftBindings] \\(__sbValue) names no case of {EscapeForSwiftString(typeName)}\") }}; "
             + "return __sbCase }()";
    }

    /// <summary>
    /// A Swift expression of type <paramref name="scalarType"/>: the C# constant of the case
    /// <paramref name="valueExpr"/> (or the unnamed value of a case the binding does not know). A null
    /// <paramref name="map"/> means the enum crosses as its raw value, converted to the scalar.
    /// </summary>
    public static string CaseToScalar(SimpleEnumCaseMap? map, string swiftEnumType, string scalarType, string valueExpr)
    {
        if (map is null)
            return $"{scalarType}(({valueExpr}).rawValue)";
        if (map.Cases is null)
            return LegacyCaseToTag(swiftEnumType, scalarType, valueExpr);
        var id = Register(map, map.SwiftTypeName ?? swiftEnumType);
        return $"{scalarType}(truncatingIfNeeded: {IndexOfPrefix}{id}({valueExpr}))";
    }

    /// <summary>
    /// Whether the enum gets a case codec (<see cref="EmitCaseCodec"/>): every simple enum crossing by
    /// identity whose case list is known.
    /// </summary>
    public static bool HasCaseCodec(SimpleEnumCaseMap? map) => map?.Cases is not null;

    /// <summary>The <c>@_cdecl</c> symbols of an enum's case codec, from its metadata accessor's symbol.</summary>
    public static (string Load, string Store, string LoadOptional, string StoreOptional) CaseCodecSymbols(string metadataSymbol)
    {
        var stem = $"SBEC_{EmitterUtility.DeterministicHash8(metadataSymbol)}";
        return ($"{stem}_load", $"{stem}_store", $"{stem}_loadOptional", $"{stem}_storeOptional");
    }

    /// <summary>
    /// Writes the enum's case codec: four <c>@_cdecl</c> functions that read and write the enum, and
    /// its <c>Optional</c>, in Swift memory by case identity, for the runtime's
    /// <c>ISimpleEnumCaseCodec</c> the module initializer registers. Every site that moves the enum
    /// through Swift memory rather than through a wrapper's parameters and returns (container and
    /// tuple elements, slots, <c>SwiftOptional</c> payloads, async results, reverse-dispatch slots)
    /// goes through them, so each sees the case Swift holds, never its in-memory tag, and an
    /// <c>Optional</c> keeps Swift's own layout whatever cases the binary adds. A value that names no
    /// case is refused (the store functions return 0 and write nothing).
    /// </summary>
    public static void EmitCaseCodec(SwiftWriter swiftWriter, SimpleEnumCaseMap map, string swiftEnumType, string metadataSymbol)
    {
        var typeName = map.SwiftTypeName ?? swiftEnumType;
        var symbols = CaseCodecSymbols(metadataSymbol);
        var availability = new StringBuilder();
        foreach (var key in WrapperEmitterHelpers.CollectStrictestAvailabilityKeys(map.Availability))
            availability.Append($"@available({key}, *)\n");
        var toCase = ScalarToOptionalCase(map, typeName, "value");
        string ToScalar(string valueExpr) => CaseToScalar(map, typeName, "Swift.Int64", valueExpr);
        string Func(string symbol, string parameters, string body) =>
            $"{availability}@_cdecl(\"{symbol}\")\npublic func _sbec_{symbol}({parameters}) -> {(symbol == symbols.Load ? "Swift.Int64" : "Swift.UInt8")} {{\n{body}}}\n";

        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"// Case codec of {typeName} (SimpleEnumCrossing.EmitCaseCodec).");
        swiftWriter.WriteLines(Func(symbols.Load, "_ source: Swift.UnsafeRawPointer",
            $"    return {ToScalar($"source.assumingMemoryBound(to: {typeName}.self).pointee")}\n"));
        swiftWriter.WriteLines(Func(symbols.Store, "_ value: Swift.Int64, _ destination: Swift.UnsafeMutableRawPointer",
            $"    guard let __sbCase = {toCase} else {{ return 0 }}\n"
          + $"    destination.initializeMemory(as: {typeName}.self, repeating: __sbCase, count: 1)\n"
          + "    return 1\n"));
        swiftWriter.WriteLines(Func(symbols.LoadOptional, "_ source: Swift.UnsafeRawPointer, _ value: Swift.UnsafeMutablePointer<Swift.Int64>",
            $"    guard let __sbCase = source.assumingMemoryBound(to: Swift.Optional<{typeName}>.self).pointee else {{ return 0 }}\n"
          + $"    value.pointee = {ToScalar("__sbCase")}\n"
          + "    return 1\n"));
        swiftWriter.WriteLines(Func(symbols.StoreOptional, "_ hasValue: Swift.UInt8, _ value: Swift.Int64, _ destination: Swift.UnsafeMutableRawPointer",
            $"    if hasValue == 0 {{ destination.initializeMemory(as: Swift.Optional<{typeName}>.self, repeating: nil, count: 1); return 1 }}\n"
          + $"    guard let __sbCase = {toCase} else {{ return 0 }}\n"
          + $"    destination.initializeMemory(as: Swift.Optional<{typeName}>.self, repeating: .some(__sbCase), count: 1)\n"
          + "    return 1\n"));
    }

    /// <summary>
    /// The case map of <paramref name="typeSpec"/>'s wrapped enum when it is an <c>Optional</c> of a
    /// simple enum with a case codec (<see cref="HasCaseCodec"/>); null otherwise. Such an Optional
    /// crosses in Swift's own layout, which the managed <c>SwiftOptional</c> reads and writes through
    /// the codec, so a site that hands one over writes the whole Optional, never a C# value's bytes.
    /// </summary>
    public static SimpleEnumCaseMap? GetOptionalCodecMap(TypeSpec? typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is not NamedTypeSpec { Name: "Swift.Optional", GenericParameters.Count: 1 } optional
            || optional.GenericParameters[0] is not NamedTypeSpec inner
            || !typeDatabase.TryGetTypeRecord(inner, out var record) || record is null)
            return null;
        var map = GetCaseMap(record);
        return HasCaseCodec(map) ? map : null;
    }

    /// <summary>
    /// Appends to <paramref name="swiftSource"/> the definition of every enum helper it calls, once
    /// each and in a stable order, plus the shared helpers they use. Run on each Swift output file
    /// as it is written; a file that calls no helper is returned unchanged.
    /// </summary>
    public static string AppendHelperDefinitions(string swiftSource)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in s_helperReference.Matches(swiftSource))
        {
            var id = match.Groups[2].Value;
            if (s_definitions.ContainsKey(id))
                ids.Add(id);
        }
        if (ids.Count == 0)
            return swiftSource;

        var sb = new StringBuilder(swiftSource);
        if (!swiftSource.EndsWith('\n'))
            sb.Append('\n');
        sb.Append('\n');
        sb.Append("// MARK: - Simple-enum case mapping (SimpleEnumCrossing)\n\n");
        if (!swiftSource.Contains($"fileprivate func {SharedHelpersToken}Code<", StringComparison.Ordinal))
            sb.Append(SharedHelpers);
        foreach (var id in ids)
            sb.Append(s_definitions[id]);
        return sb.ToString();
    }

    /// <summary>Registers the helper pair of <paramref name="map"/> spelled with <paramref name="swiftEnumType"/>, returning its id.</summary>
    internal static string Register(SimpleEnumCaseMap map, string swiftEnumType)
    {
        var body = BuildDefinitionBody(map, swiftEnumType, "{ID}");
        var id = $"{SanitizeIdentifier(swiftEnumType)}_{EmitterUtility.DeterministicHash8(body)}";
        s_definitions.TryAdd(id, body.Replace("{ID}", id, StringComparison.Ordinal));
        return id;
    }

    /// <summary>The emitted Swift text of <paramref name="map"/>'s helper pair (tests).</summary>
    internal static string GetDefinition(SimpleEnumCaseMap map, string swiftEnumType)
        => s_definitions[Register(map, swiftEnumType)];

    private static string BuildDefinitionBody(SimpleEnumCaseMap map, string swiftEnumType, string id)
    {
        var cases = map.Cases!;
        var mask = map.WidthBits >= 64 ? ulong.MaxValue : (1UL << map.WidthBits) - 1;
        var maskLiteral = $"0x{mask:X}";
        var availability = new StringBuilder();
        foreach (var key in WrapperEmitterHelpers.CollectStrictestAvailabilityKeys(map.Availability))
            availability.Append($"@available({key}, *)\n");

        var sb = new StringBuilder();
        // Index -> case.
        sb.Append(availability);
        sb.Append($"fileprivate func {CaseOfPrefix}{id}(_ value: Swift.Int64) -> {swiftEnumType}? {{\n");
        sb.Append("    switch value {\n");
        foreach (var c in cases)
        {
            // Wrapped explicitly: in this Optional-returning helper a bare `.none` names Optional's own
            // case, so a case called `none` would cross back as nil. `.some(.x)` resolves `x` against
            // the enum whatever it is called (`none`, `some`, `Type`), which `E.x` cannot for `Type`.
            var caseRef = $".some(.{NameProvider.EscapeSwiftKeyword(c.SwiftName)})";
            sb.Append(c.AvailableClause is null
                ? $"    case {c.Value}: return {caseRef}\n"
                : $"    case {c.Value}: if #available({c.AvailableClause}) {{ return {caseRef} }} else {{ return nil }}\n");
        }
        sb.Append($"    default: return {SharedHelpersToken}(value, slots: {map.SlotCount}, mask: {maskLiteral}, as: {swiftEnumType}.self, knownIndexOf: {KnownIndexOfPrefix}{id})\n");
        sb.Append("    }\n");
        sb.Append("}\n\n");

        // Case -> index of a case the binding names, nil for any other.
        sb.Append(availability);
        sb.Append($"fileprivate func {KnownIndexOfPrefix}{id}(_ value: {swiftEnumType}) -> Swift.Int64? {{\n");
        sb.Append("    switch value {\n");
        foreach (var c in cases)
            sb.Append($"    case .{NameProvider.EscapeSwiftKeyword(c.SwiftName)}: return {c.Value}\n");
        // A frozen enum whose cases are all nameable is exhaustive; one with an @_spi case needs a
        // plain default, and a non-frozen one the @unknown default library evolution asks for.
        if (cases.Count < map.SlotCount)
            sb.Append("    default: return nil\n");
        else if (!map.IsFrozen)
            sb.Append("    @unknown default: return nil\n");
        sb.Append("    }\n");
        sb.Append("}\n\n");

        // Case -> index, or the unnamed value of a case the binding does not name.
        sb.Append(availability);
        sb.Append($"fileprivate func {IndexOfPrefix}{id}(_ value: {swiftEnumType}) -> Swift.Int64 {{\n");
        sb.Append($"    if let index = {KnownIndexOfPrefix}{id}(value) {{ return index }}\n");
        sb.Append($"    return {SharedHelpersToken}Code(value, slots: {map.SlotCount}, mask: {maskLiteral}, knownIndexOf: {KnownIndexOfPrefix}{id})\n");
        sb.Append("}\n\n");
        return sb.ToString();
    }

    /// <summary>
    /// The shared helpers of the unnamed-value encoding (see the class summary): the enum's shape from
    /// its runtime metadata, the code of a case the binding does not know, and the case such a code
    /// names.
    /// </summary>
    private const string SharedHelpers =
        "/// The payload and empty case counts of the enum `type`, read from its nominal type descriptor\n" +
        "/// (enum metadata holds the descriptor one word in; the descriptor holds the counts at 20 and 24).\n" +
        "fileprivate func __sbEnumCaseCounts(_ type: Any.Type) -> (payload: Swift.UInt32, empty: Swift.UInt32) {\n" +
        "    let metadata = Swift.unsafeBitCast(type, to: Swift.UnsafeRawPointer.self)\n" +
        "    let descriptor = metadata.load(fromByteOffset: Swift.MemoryLayout<Swift.Int>.size, as: Swift.UnsafeRawPointer.self)\n" +
        "    let payload = descriptor.load(fromByteOffset: 20, as: Swift.UInt32.self) & 0x00FF_FFFF\n" +
        "    return (payload, descriptor.load(fromByteOffset: 24, as: Swift.UInt32.self))\n" +
        "}\n\n" +
        "/// Whether the unnamed-value encoding applies to `E`: no payload cases and a tag that fits a word.\n" +
        "fileprivate func __sbUnknownCaseEncodable<E>(_ type: E.Type) -> Swift.UInt64? {\n" +
        "    let counts = __sbEnumCaseCounts(E.self)\n" +
        "    guard counts.payload == 0, Swift.MemoryLayout<E>.size <= Swift.MemoryLayout<Swift.UInt64>.size else { return nil }\n" +
        "    return Swift.UInt64(counts.empty)\n" +
        "}\n\n" +
        "/// The case of a payload-free enum whose in-memory tag is `tag` (below its empty-case count).\n" +
        "fileprivate func __sbUnknownCaseAt<E>(_ tag: Swift.UInt64, as type: E.Type) -> E {\n" +
        "    var bits = tag\n" +
        "    return Swift.withUnsafeBytes(of: &bits) { $0.load(as: E.self) }\n" +
        "}\n\n" +
        "/// The unnamed value of a case the binding does not know: `mask - 1 - rank` at the C# enum's width,\n" +
        "/// `rank` the number of unknown cases below it in tag order, above every published constant;\n" +
        "/// `mask` itself when the case cannot be encoded that way.\n" +
        "fileprivate func __sbUnknownCaseCode<E>(_ value: E, slots: Swift.Int64, mask: Swift.UInt64, knownIndexOf: (E) -> Swift.Int64?) -> Swift.Int64 {\n" +
        "    guard let empty = __sbUnknownCaseEncodable(E.self) else { return Swift.Int64(bitPattern: mask) }\n" +
        "    var tag: Swift.UInt64 = 0\n" +
        "    Swift.withUnsafeBytes(of: value) { source in\n" +
        "        Swift.withUnsafeMutableBytes(of: &tag) { $0.copyMemory(from: source) }\n" +
        "    }\n" +
        "    guard tag < empty else { return Swift.Int64(bitPattern: mask) }\n" +
        "    var rank: Swift.UInt64 = 0\n" +
        "    var below: Swift.UInt64 = 0\n" +
        "    while below < tag {\n" +
        "        if knownIndexOf(__sbUnknownCaseAt(below, as: E.self)) == nil { rank += 1 }\n" +
        "        below += 1\n" +
        "    }\n" +
        "    guard Swift.UInt64(slots) < mask, rank < mask - Swift.UInt64(slots) else { return Swift.Int64(bitPattern: mask) }\n" +
        "    return Swift.Int64(bitPattern: mask - 1 - rank)\n" +
        "}\n\n" +
        "/// The case an unnamed value names, or nil: the unknown case of that rank in the running binary.\n" +
        "fileprivate func __sbUnknownCase<E>(_ value: Swift.Int64, slots: Swift.Int64, mask: Swift.UInt64, as type: E.Type, knownIndexOf: (E) -> Swift.Int64?) -> E? {\n" +
        "    let code = Swift.UInt64(bitPattern: value) & mask\n" +
        "    guard code < mask, code >= Swift.UInt64(slots), let empty = __sbUnknownCaseEncodable(E.self) else { return nil }\n" +
        "    let rank = mask - 1 - code\n" +
        "    var seen: Swift.UInt64 = 0\n" +
        "    var tag: Swift.UInt64 = 0\n" +
        "    while tag < empty {\n" +
        "        let candidate = __sbUnknownCaseAt(tag, as: E.self)\n" +
        "        if knownIndexOf(candidate) == nil {\n" +
        "            if seen == rank { return candidate }\n" +
        "            seen += 1\n" +
        "        }\n" +
        "        tag += 1\n" +
        "    }\n" +
        "    return nil\n" +
        "}\n\n";

    private static string SanitizeIdentifier(string swiftType)
    {
        var sb = new StringBuilder(swiftType.Length);
        foreach (var ch in swiftType)
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
        return sb.ToString();
    }

    private static string EscapeForSwiftString(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // -- Legacy in-memory tag crossing: only for a record read from a module database that predates
    //    the case list, which marshalled this way.

    private static string LegacyTagToCase(string swiftEnumType, string scalarExpr)
        => $"{{ () -> {swiftEnumType} in var __sbTag = {scalarExpr}; "
         + $"return Swift.withUnsafeMutablePointer(to: &__sbTag) {{ __sbTagPtr in "
         + $"Swift.UnsafeMutableRawPointer(__sbTagPtr).load(as: {swiftEnumType}.self) }} }}()";

    private static string LegacyScalarToOptionalCase(int caseTagCount, string swiftEnumType, string scalarExpr)
    {
        // A record with no case count (a no-raw-value enum in such a database) was loaded unchecked.
        if (caseTagCount <= 0)
            return $"Swift.Optional<{swiftEnumType}>.some({LegacyTagToCase(swiftEnumType, scalarExpr)})";
        return $"((0 as Swift.Int64)..<{caseTagCount}).contains(Swift.Int64(truncatingIfNeeded: {scalarExpr})) "
             + $"? Swift.Optional<{swiftEnumType}>.some({LegacyTagToCase(swiftEnumType, scalarExpr)}) "
             + $": Swift.Optional<{swiftEnumType}>.none";
    }

    private static string LegacyCaseToTag(string swiftEnumType, string scalarType, string valueExpr)
        => $"{{ () -> {scalarType} in var __sbTag: {scalarType} = 0; var __sbCase: {swiftEnumType} = {valueExpr}; "
         + $"Swift.withUnsafeMutablePointer(to: &__sbTag) {{ __sbDst in Swift.withUnsafePointer(to: &__sbCase) {{ __sbSrc in "
         + $"Swift.UnsafeMutableRawPointer(__sbDst).copyMemory(from: Swift.UnsafeRawPointer(__sbSrc), "
         + $"byteCount: Swift.MemoryLayout<{swiftEnumType}>.size) }} }}; return __sbTag }}()";
}
