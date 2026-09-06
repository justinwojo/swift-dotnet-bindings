// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// How Swift delivers ONE argument to a direct (<c>CallConvSwift</c>) closure callback — the
/// reverse trampoline C# hands to Swift as a thick closure's function pointer.
/// </summary>
/// <remarks>
/// The direct bridge historically declared every non-blittable argument as <c>void*</c> and read it
/// as the ADDRESS of the value. That model is only correct for two of the cases below. Swift's native
/// convention passes a LOADABLE argument by value: the value is exploded into its scalar leaves and
/// those arrive in registers, so the trampoline receives the value's words, not a pointer to them.
/// Reading a word as an address dereferences whatever the value's first word happens to be — an
/// array's storage header, a string's small-form character bytes — which is a wild read.
/// </remarks>
public enum DirectClosureArgAbi
{
    /// <summary>
    /// The already-declared P/Invoke parameter type matches the register content exactly:
    /// blittable primitives, <c>Bool</c>, tuples of those, existential containers. A no-payload
    /// enum does NOT qualify — its register holds a declaration-order tag, not the raw value the
    /// declaration names — so it is refused wherever it sits inline in the argument.
    /// </summary>
    PassThrough,

    /// <summary>
    /// One register whose content IS the value: a class reference, <c>Optional&lt;class&gt;</c>,
    /// an ObjC-bridged class, or a raw pointer type. Reading it as an address is correct here
    /// because the address and the value coincide.
    /// </summary>
    ReferenceWord,

    /// <summary>
    /// One register holding the ADDRESS of the value. Swift passes address-only values
    /// (<c>@in_guaranteed</c>) indirectly: opaque existentials, resilient (non-<c>@frozen</c>)
    /// structs from a library-evolution module such as <c>Foundation.Date</c> / <c>Foundation.URL</c>,
    /// and any enum whose payload is itself address-only.
    /// </summary>
    Indirect,

    /// <summary>
    /// The value is loadable and Swift explodes it across several integer registers whose
    /// concatenation, at 8-byte offsets, reproduces the value's memory image. The trampoline
    /// declares the extra words as additional parameters, rebuilds the image in a stack buffer,
    /// and hands that buffer's address to the existing address-based marshalling.
    /// </summary>
    ExplodedWords,

    /// <summary>
    /// Loadable and exploded, but this generator does not model the explosion — the register
    /// classes or the word count depend on layout facts the generator does not have. Emitting
    /// the address model here would compile and then read the wrong memory, so the member is
    /// failed closed instead.
    /// </summary>
    Unmodelled,
}

/// <summary>
/// The register schema for one direct-lane closure callback argument.
/// </summary>
/// <param name="Abi">How the argument arrives.</param>
/// <param name="ExtraWordTypes">
/// Native parameter types for the words AFTER the first. Word 0 keeps its existing <c>void*</c>
/// declaration, so this list is empty for every single-register shape. Word <c>k</c> sits at byte
/// offset <c>8 * k</c> of the value's memory image.
/// </param>
/// <param name="Shape">A human-readable spelling of the argument type, for skip diagnostics.</param>
public sealed record DirectClosureArgLowering(
    DirectClosureArgAbi Abi,
    IReadOnlyList<string> ExtraWordTypes,
    string Shape)
{
    /// <summary>Total number of registers the argument occupies.</summary>
    public int WordCount => ExtraWordTypes.Count + 1;

    /// <summary>Stack-buffer size needed to rebuild the value's memory image (one 8-byte slot per word).</summary>
    public int BufferBytes => 8 * WordCount;
}

public partial class ClosureHandler
{
    private static readonly DirectClosureArgLowering PassThroughLowering =
        new(DirectClosureArgAbi.PassThrough, Array.Empty<string>(), string.Empty);

    private static readonly DirectClosureArgLowering ReferenceWordLowering =
        new(DirectClosureArgAbi.ReferenceWord, Array.Empty<string>(), string.Empty);

    private static readonly DirectClosureArgLowering IndirectLowering =
        new(DirectClosureArgAbi.Indirect, Array.Empty<string>(), string.Empty);

    /// <summary>
    /// Container carriers whose Swift representation is a single refcounted buffer pointer, so the
    /// whole value rides in ONE register. <c>Optional</c> of one of these is also one register: the
    /// null pointer is the extra inhabitant Swift uses for <c>.none</c>, and every non-nil value
    /// (including an empty collection, which points at a shared empty singleton) is non-null.
    /// </summary>
    private static bool IsSingleWordContainerName(string name) =>
        name is "Swift.Array" or "Swift.ContiguousArray" or "Swift.Dictionary" or "Swift.Set";

    /// <summary>
    /// True for <c>Optional&lt;Array/ContiguousArray/Dictionary/Set&gt;</c>, whose one-word register
    /// image is the container's buffer reference with a zero word standing for <c>.none</c>. An empty
    /// container is a non-zero shared singleton, so the zero test cannot confuse the two.
    /// </summary>
    public bool IsOptionalSingleWordContainerArg(TypeSpec typeSpec)
        => typeSpec is NamedTypeSpec named
           && TryGetOptionalInner(named, out var inner)
           && inner is NamedTypeSpec innerNamed
           && innerNamed.ContainsGenericParameters
           && IsSingleWordContainerName(innerNamed.Name);

    /// <summary>
    /// Classifies how Swift delivers <paramref name="typeSpec"/> to a direct-lane closure callback.
    /// Only meaningful on the direct (<c>CallConvSwift</c>) bridge — the <c>@_cdecl</c> lanes receive
    /// what their Swift-side adapter chooses to hand over, which is a different contract.
    /// </summary>
    public DirectClosureArgLowering ClassifyDirectClosureArg(TypeSpec typeSpec)
    {
        // A no-payload enum is unreachable on this lane whether or not it is @frozen, and for a
        // different reason on each side. Resilient: the case count can grow, so the caller sees no
        // fixed size and Swift hands over a pointer (SIL renders the callback parameter
        // @in_guaranteed, against a plain by-value parameter for the @frozen twin). @frozen: Swift
        // passes the declaration-order tag, sized to the case count rather than to any raw type —
        // `@frozen enum Code: Int32 { case ok = 10, failed = 20, other = 30 }` reaches the callback
        // as a one-byte `1` for `.failed`. The P/Invoke translation declares the enum's raw integer
        // in both cases, and the emitted C# enum spells its members with the Swift source raw values,
        // so the declaration disagrees with the register on width AND on value. The @_cdecl lanes do
        // not hit this because their Swift adapter converts through `.rawValue` (or copies the tag
        // out at the enum's real size) before the callback sees it; the direct lane has no adapter to
        // convert in. Neither model reaches it — the by-value model cannot fix a declaration this
        // lane does not own, and the address model still carries the integer declaration — so the
        // member is failed closed instead.
        //
        // The refusal follows the enum wherever it sits INLINE in the argument, because every inline
        // position hands the same mis-declared integer to the same C# cast: a tuple element is
        // declared as its own field of the emitted tuple struct, an Optional payload is read through
        // the nil-for-none pointer cast, and a Result success payload is rebuilt word-wise out of the
        // stack buffer. Container generic arguments are deliberately NOT walked — an Array's or a
        // Dictionary's elements never reach a register, they are unpacked later by the container
        // marshalling, which owns its own conversion and is unaffected by this lane's declaration.
        if (ContainsInlineSimpleEnum(typeSpec))
            return new DirectClosureArgLowering(
                DirectClosureArgAbi.Unmodelled, Array.Empty<string>(), typeSpec.ToString());

        // Anything the P/Invoke translation already declares as a concrete blittable type
        // (primitive, Bool, tuple, supported existential container) is passed in the shape the
        // declaration says, so the register content already matches. No-payload enums are the one
        // family whose declaration does NOT match, and the arm above has already removed them.
        if (TranslateTypeSpecToPInvokeType(typeSpec) != "void*")
            return PassThroughLowering;

        if (typeSpec is not NamedTypeSpec named)
            return IndirectLowering;

        // Unsupported existentials fall to void* above but are still address-only opaque containers.
        if (_existentialHandler.IsExistential(typeSpec))
            return IndirectLowering;

        // The register IS the value for these: no dereference happens, so the historical
        // address model coincides with the by-value model.
        if (TypeDatabaseExtensions.IsPointerType(named) || IsReferenceType(named) || IsOptionalReferenceArg(named))
            return ReferenceWordLowering;

        // Loadable shapes whose explosion is the value's memory image in word order.
        if (TryGetValueWordTypes(named, out var valueWords))
            return new DirectClosureArgLowering(
                DirectClosureArgAbi.ExplodedWords, valueWords.Skip(1).ToArray(), named.ToString());

        // Result<T, any Error>: a two-case enum whose failure payload is a one-word boxed error, so
        // the payload area is always at least one word and the case tag is a byte at 8 * max(1, words(T)).
        // Enum lowering is word-based rather than field-based — a Double payload arrives in an
        // integer register, not a floating-point one — so the schema depends only on the payload's
        // word count, never on its field types.
        if (IsSwiftResultOverAnyError(named, out var successPayload))
        {
            // An address-only payload makes the whole enum address-only; the historical model is right.
            if (IsAddressOnlyValue(successPayload))
                return IndirectLowering;

            if (TryGetValueWordTypes(successPayload, out var payloadWords))
            {
                var words = new List<string>(payloadWords) { "byte" };
                return new DirectClosureArgLowering(
                    DirectClosureArgAbi.ExplodedWords, words.Skip(1).ToArray(), named.ToString());
            }

            return new DirectClosureArgLowering(
                DirectClosureArgAbi.Unmodelled, Array.Empty<string>(), named.ToString());
        }

        // A resilient (non-@frozen) struct from a library-evolution module has no layout the caller
        // can see, so Swift passes it indirectly — the address model is correct.
        if (IsNonFrozenStruct(named))
            return IndirectLowering;

        // Optional over an 8-byte scalar: the payload fills its register completely, leaving no spare
        // bits for the tag, so the tag becomes a separate byte after the payload word — the same
        // word-plus-tag-byte image the one-word Result arm builds. Narrower scalars behave
        // differently (the tag packs INTO the payload's spare bits and the whole value stays a single
        // register), which is why the set is enumerated by width rather than by "is a primitive".
        if (IsOptionalOverEightByteScalar(named))
            return new DirectClosureArgLowering(
                DirectClosureArgAbi.ExplodedWords, new[] { "byte" }, named.ToString());

        // Every remaining Optional. An Optional is loadable exactly when its payload is, and its
        // register image is whatever the tag negotiates with the payload's spare bits — which varies
        // per payload with no rule to generalise from: Optional<Data> and Optional<ArraySlice> are
        // their payload's words with no tag byte at all, Optional<Int> gains one, Optional<Bool> and
        // Optional<Int32> shrink into a single register. Only a genuinely address-only payload makes
        // the Optional address-only too, and there the historical model is right; everything else is
        // refused rather than guessed.
        if (TryGetOptionalInner(named, out var optionalPayload))
            return IsAddressOnlyValue(optionalPayload)
                ? IndirectLowering
                : new DirectClosureArgLowering(
                    DirectClosureArgAbi.Unmodelled, Array.Empty<string>(), named.ToString());

        // Verified loadable and exploded, deliberately not modelled:
        //  - a frozen struct is exploded field-wise, and each 8-byte chunk lands in an integer OR a
        //    floating-point register depending on the fields it holds (a two-Double struct arrives in
        //    d0/d1, not x0/x1), so reproducing it needs the full aggregate-classification table;
        //  - ArraySlice is a four-register value (base, start, count, owner);
        //  - a @frozen payload-carrying enum is loadable, and its explosion depends on the payload
        //    layouts and the spare-bit packing the tag negotiates with them.
        if (IsFrozenStruct(named) || IsFrozenStructWithRefFields(named) ||
            IsSliceContainer(named) || IsFrozenPayloadEnum(named))
            return new DirectClosureArgLowering(
                DirectClosureArgAbi.Unmodelled, Array.Empty<string>(), named.ToString());

        // Everything else keeps the historical address model. This is deliberately conservative:
        // shapes whose lowering has not been established either way are left exactly as they were
        // rather than being newly rejected on suspicion.
        return IndirectLowering;
    }

    /// <summary>
    /// Word schema for a loadable value whose exploded registers reproduce its memory image at
    /// 8-byte offsets. Returns the native parameter type for EVERY word, word 0 included.
    /// </summary>
    private bool TryGetValueWordTypes(TypeSpec typeSpec, out List<string> words)
    {
        words = null!;
        if (typeSpec is not NamedTypeSpec named)
            return false;

        // One refcounted pointer: a class, an ObjC-bridged class, or a raw pointer.
        if (TypeDatabaseExtensions.IsPointerType(named) || IsReferenceType(named))
        {
            words = new List<string> { "void*" };
            return true;
        }

        // Two words: the String struct is (rawBits, discriminatorAndCount) and Foundation.Data is a
        // @frozen two-word representation. Optional<String> reuses String's own extra inhabitants,
        // so it stays two words with an all-zero image for .none.
        if (WitnessDispatchEmitter.IsStringType(named) || named.Name == "Foundation.Data")
        {
            words = new List<string> { "void*", "void*" };
            return true;
        }

        if (named.ContainsGenericParameters && IsSingleWordContainerName(named.Name))
        {
            words = new List<string> { "void*" };
            return true;
        }

        if (TryGetOptionalInner(named, out var inner))
        {
            if (inner is NamedTypeSpec innerNamed)
            {
                if (IsReferenceType(innerNamed))
                {
                    words = new List<string> { "void*" };
                    return true;
                }

                if (WitnessDispatchEmitter.IsStringType(innerNamed))
                {
                    words = new List<string> { "void*", "void*" };
                    return true;
                }

                if (innerNamed.ContainsGenericParameters && IsSingleWordContainerName(innerNamed.Name))
                {
                    words = new List<string> { "void*" };
                    return true;
                }
            }

            return false;
        }

        // A blittable primitive is at most one word wide. This only matters as a Result payload — on
        // its own such a type never reaches here, because the P/Invoke translation declares it
        // directly and it classifies as PassThrough above. A no-payload enum is deliberately NOT
        // admitted alongside it: its register carries the declaration-order tag rather than the raw
        // value the declaration names, so a word schema built for it would rebuild the wrong integer.
        // Such an argument is refused before it can reach this helper, and leaving the case out keeps
        // the refusal fail-closed if that guard is ever narrowed.
        if (!named.ContainsGenericParameters && GetBlittablePrimitiveType(named.Name) != null)
        {
            words = new List<string> { "void*" };
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="typeSpec"/> is <c>Swift.Optional&lt;T&gt;</c>, yielding <c>T</c>.
    /// </summary>
    private static bool TryGetOptionalInner(NamedTypeSpec named, out TypeSpec inner)
    {
        if (named.ContainsGenericParameters && named.Name == "Swift.Optional" && named.GenericParameters.Count == 1)
        {
            inner = named.GenericParameters[0];
            return true;
        }

        inner = null!;
        return false;
    }

    /// <summary>
    /// True when <paramref name="typeSpec"/> is <c>Swift.Result&lt;T, any Swift.Error&gt;</c>.
    /// </summary>
    private static bool IsSwiftResultOverAnyError(NamedTypeSpec named, out TypeSpec successPayload)
    {
        successPayload = null!;
        if (named.Name != "Swift.Result" || named.GenericParameters.Count != 2)
            return false;
        if (!MethodClosureBridge.IsAnyErrorExistential(named.GenericParameters[1]))
            return false;
        successPayload = named.GenericParameters[0];
        return true;
    }

    /// <summary>
    /// True for <c>Optional</c> over a scalar that fills a whole register — <c>Int</c>, <c>UInt</c>,
    /// <c>Int64</c>, <c>UInt64</c>, <c>Double</c>. With no spare bits left in the payload the tag
    /// cannot pack inside it, so the value lowers to the payload word followed by a tag byte.
    /// </summary>
    private static bool IsOptionalOverEightByteScalar(NamedTypeSpec named) =>
        TryGetOptionalInner(named, out var inner) &&
        inner is NamedTypeSpec { ContainsGenericParameters: false } innerNamed &&
        innerNamed.Name is "Swift.Int" or "Swift.UInt" or "Swift.Int64" or "Swift.UInt64" or "Swift.Double";

    /// <summary>
    /// True when a no-payload enum sits INLINE anywhere in <paramref name="typeSpec"/> — the type
    /// itself, a tuple element at any depth, an <c>Optional</c> payload, or a
    /// <c>Result&lt;T, any Error&gt;</c> success payload. Every one of those positions reaches C#
    /// through this lane's own declaration of the enum's raw integer, which disagrees with the
    /// register on both width and value, so all of them are refused together rather than one shape
    /// at a time. Generic arguments of a container (<c>Array</c>, <c>Dictionary</c>, <c>Set</c>) are
    /// NOT inline: the container arrives as one buffer reference and its elements are converted by
    /// the container marshalling, which does not read them out of a register.
    /// </summary>
    private bool ContainsInlineSimpleEnum(TypeSpec typeSpec)
    {
        if (typeSpec is TupleTypeSpec tuple)
            return tuple.Elements.Any(ContainsInlineSimpleEnum);

        if (typeSpec is not NamedTypeSpec named)
            return false;

        if (IsSimpleEnum(named))
            return true;

        if (TryGetOptionalInner(named, out var optionalPayload))
            return ContainsInlineSimpleEnum(optionalPayload);
        if (IsSwiftResultOverAnyError(named, out var successPayload))
            return ContainsInlineSimpleEnum(successPayload);

        return false;
    }

    /// <summary>
    /// True for a value Swift can only pass behind an address: an existential or <c>Any</c>, a
    /// resilient struct, or a resilient enum (payload-carrying or not — a growable case count hides
    /// the size just as an unknown field list does). A container inherits the model from what it
    /// holds, so an <c>Optional</c> or a <c>Result</c> is address-only exactly when its payload is;
    /// each step strips one layer, so the recursion terminates on the type tree. Answering only for
    /// the outermost shape would call <c>Optional&lt;Optional&lt;ResilientStruct&gt;&gt;</c> loadable
    /// and refuse a shape the address model already carries correctly.
    /// </summary>
    private bool IsAddressOnlyValue(TypeSpec typeSpec)
    {
        if (_existentialHandler.IsExistential(typeSpec) || typeSpec is ProtocolListTypeSpec)
            return true;

        if (typeSpec is not NamedTypeSpec named)
            return false;
        if (named.IsAny)
            return true;

        if (TryGetOptionalInner(named, out var optionalPayload))
            return IsAddressOnlyValue(optionalPayload);
        if (IsSwiftResultOverAnyError(named, out var successPayload))
            return IsAddressOnlyValue(successPayload);

        if (IsNonFrozenStruct(named))
            return true;

        if (named.ContainsGenericParameters)
            return false;
        if (IsGenericTypeParameter(named.Name) || !named.HasModule())
            return false;

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(named.Name);
        if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            return false;

        return typeRecord.Kind == TypeRecordKind.Enum &&
               (typeRecord.Flags & TypeRecordFlags.Frozen) == 0;
    }

    /// <summary>
    /// True for a <c>@frozen</c> enum that carries payloads. Such an enum is loadable, so Swift
    /// explodes it into registers rather than passing its address; a resilient (non-<c>@frozen</c>)
    /// enum stays address-only and keeps the indirect model. A payload-free enum never reaches this
    /// check — it is declared directly by the P/Invoke translation or resolved as a one-word value.
    /// </summary>
    private bool IsFrozenPayloadEnum(NamedTypeSpec named)
    {
        if (named.ContainsGenericParameters)
            return false;
        if (IsGenericTypeParameter(named.Name) || !named.HasModule())
            return false;

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(named.Name);
        if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
            return false;

        return typeRecord.Kind == TypeRecordKind.Enum &&
               (typeRecord.Flags & TypeRecordFlags.Frozen) != 0 &&
               (typeRecord.Flags & TypeRecordFlags.SimpleEnum) == 0;
    }

    /// <summary>
    /// True for <c>ArraySlice</c>/<c>Slice</c>, which Swift passes as four registers
    /// (buffer, start index, count, owner).
    /// </summary>
    private static bool IsSliceContainer(NamedTypeSpec named) =>
        named.ContainsGenericParameters && named.Name is "Swift.ArraySlice" or "Swift.Slice";
}
