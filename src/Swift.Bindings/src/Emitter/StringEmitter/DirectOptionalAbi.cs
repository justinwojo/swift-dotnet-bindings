// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// How wide an <c>Optional&lt;T&gt;</c> actually is once Swift has physically lowered it, as
/// seen by a <b>direct CallConvSwift P/Invoke</b> — one with no Swift-side wrapper to widen the
/// value into a pointer or an out-buffer.
///
/// <para>This is deliberately NOT the same question as
/// <c>BoundGenericsHandler.IsLargeOptionalParam</c>. That predicate asks "should this Optional
/// prefer the pointer-wrapper route?" and answers yes for anything that is not a reference, a
/// protocol existential, or a sub-word primitive. It is a routing *preference*, and it is
/// deliberately over-broad: routing a genuinely one-word Optional through a pointer wrapper is
/// merely wasteful, not wrong, so nothing forced it to be precise.
///
/// This enum asks the *soundness* question instead — how many machine words does the value
/// occupy, and can the generator prove it? — because the direct path has exactly one
/// pointer-sized slot to put the value in. An Optional that does not fit in that slot is not
/// slower, it is silently truncated: the bytes past the first word are never transferred, and
/// for an extra-inhabitant Optional (one with no separate tag byte) those missing bytes are
/// precisely what decides Some vs None.</para>
/// </summary>
internal enum DirectOptionalAbiWidth
{
    /// <summary>The type is not an <c>Optional&lt;T&gt;</c>; the question does not apply.</summary>
    NotOptional,

    /// <summary>
    /// Provably one machine word. The single pointer-sized P/Invoke slot the direct path gives
    /// it is the whole value, so the existing direct emission is already ABI-correct and must be
    /// left alone.
    /// </summary>
    SingleWord,

    /// <summary>
    /// Provably two integer machine words with no separate tag byte — the extra-inhabitant
    /// shape, of which concrete <c>Optional&lt;String&gt;</c> is the case that actually occurs.
    /// Swift returns it in two integer registers and takes it as two integer argument words.
    /// The direct path's one slot carries only the first of the two, so this does NOT fit
    /// today; it is called out separately from <see cref="Unprovable"/> because, unlike the
    /// unprovable shapes, its lowering IS statically known and it can therefore be carried
    /// correctly by a two-word blittable carrier rather than refused.
    /// </summary>
    TwoIntegerWords,

    /// <summary>
    /// The generator cannot prove the physical lowering. Either the value is known to be wider
    /// than one word without its register classes being pinned down (a payload-plus-tag-byte
    /// Optional such as <c>Optional&lt;Double&gt;</c>, whose payload lands in a floating-point
    /// register while its tag is an integer byte), or the layout is not statically knowable at
    /// all (resilient/non-<c>@frozen</c> inner types, which are address-only across their
    /// resilience boundary; generic payloads whose layout depends on runtime metadata).
    ///
    /// <para>Emitting a direct single-slot call for one of these is unsound, and the failure is
    /// invisible to both compilers, so the only honest outcome is to refuse.</para>
    /// </summary>
    Unprovable,
}

/// <summary>
/// Single source of truth for the physical width of an <c>Optional&lt;T&gt;</c> on the direct
/// CallConvSwift path.
///
/// <para>Deliberately conservative in one direction only: a shape is classified
/// <see cref="DirectOptionalAbiWidth.SingleWord"/> or
/// <see cref="DirectOptionalAbiWidth.TwoIntegerWords"/> only when its lowering is *positively*
/// established, and everything else falls to <see cref="DirectOptionalAbiWidth.Unprovable"/>.
/// The asymmetry is intentional: mistaking a wide Optional for a narrow one silently corrupts
/// values at runtime, whereas mistaking a narrow one for unprovable costs surface but cannot
/// produce a wrong answer.</para>
/// </summary>
internal static class DirectOptionalAbi
{
    /// <summary>
    /// Swift standard-library containers whose entire representation is a single refcounted
    /// pointer to their backing storage. Because that pointer can never legitimately be null,
    /// <c>Optional</c> of one of them uses the null value as its extra inhabitant and stays at
    /// exactly one machine word.
    /// </summary>
    private static readonly HashSet<string> s_singlePointerContainers = new(StringComparer.Ordinal)
    {
        "Swift.Array", "Array",
        "Swift.Dictionary", "Dictionary",
        "Swift.Set", "Set",
    };

    /// <summary>
    /// Spellings of the Swift error existential, whose representation is a single refcounted
    /// error box rather than the multi-word container every other existential uses. Measured at
    /// 8 bytes for both <c>any Error</c> and <c>(any Error)?</c>, against 40 for an ordinary
    /// <c>(any P)?</c>.
    /// </summary>
    private static readonly HashSet<string> s_errorExistentials = new(StringComparer.Ordinal)
    {
        "Swift.Error", "Error", "Foundation.AnyError", "AnyError",
    };

    /// <summary>
    /// Primitive inner types small enough that the payload plus its appended tag byte still fit
    /// inside one machine word. These are the types
    /// <c>BoundGenericsHandler.IsLargeOptionalParam</c> already declines to call "large", listed
    /// again here so this classifier states its own reasoning rather than inheriting a routing
    /// preference as if it were a layout fact.
    /// </summary>
    private static readonly HashSet<string> s_subWordPrimitives = new(StringComparer.Ordinal)
    {
        "Swift.Bool", "Bool",
        "Swift.Int8", "Int8", "Swift.UInt8", "UInt8",
        "Swift.Int16", "Int16", "Swift.UInt16", "UInt16",
        "Swift.Int32", "Int32", "Swift.UInt32", "UInt32",
        "Swift.Float", "Float",
    };

    /// <summary>
    /// Classifies how wide <paramref name="typeSpec"/> is when carried by a direct CallConvSwift
    /// P/Invoke. Returns <see cref="DirectOptionalAbiWidth.NotOptional"/> for anything that is
    /// not an <c>Optional&lt;T&gt;</c>.
    /// </summary>
    internal static DirectOptionalAbiWidth Classify(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (!WrapperValidation.IsOptionalType(typeSpec))
            return DirectOptionalAbiWidth.NotOptional;

        // A malformed or non-nominal payload (tuple, closure, nested generic spec) is not
        // something this classifier reasons about — refuse rather than guess.
        if (typeSpec is not NamedTypeSpec namedType ||
            namedType.GenericParameters.Count != 1 ||
            namedType.GenericParameters[0] is not NamedTypeSpec inner)
            return DirectOptionalAbiWidth.Unprovable;

        // A class or ObjC-rooted payload is a single pointer, and nil is its null extra inhabitant.
        // This is the case the existing direct emission was designed around.
        //
        // The reference predicate is deliberately not trusted on its own here. It answers a
        // *bridging* question — "does this arrive as a nullable object pointer at a @_cdecl
        // boundary?" — and so also accepts Swift value types that bridge to an ObjC object
        // (Foundation.URL, Date, and friends). There is no bridging on the direct CallConvSwift
        // path: such a payload keeps its native Swift layout, which is two words for URL? and
        // nine bytes for Date?, not a nullable pointer. Those fall through to Unprovable, which
        // is the safe answer.
        //
        // This exclusion is load-bearing, not defensive: the emission floor calls this classifier
        // for every Optional it sees, with no routing predicate in front of it, so dropping the
        // exclusion re-opens the URL?/Date? case directly. What that case emitted before the floor
        // was worse than a truncated read — the first word of a half-copied struct handed to
        // GetINativeObject(..., owns: true), reinterpreted as an ObjC object and then released.
        if (WrapperValidation.IsOptionalWithReferenceInner(typeSpec, typeDatabase)
            && !IsBridgedValueTypePayload(inner, typeDatabase))
            return DirectOptionalAbiWidth.SingleWord;

        // An @objc protocol existential has AnyObject's representation — one object pointer,
        // no witness table — so it is a single word like a class reference.
        if (ExistentialHandler.IsObjCProtocolExistentialSpec(typeSpec, typeDatabase))
            return DirectOptionalAbiWidth.SingleWord;

        // `any Error` is the one existential Swift does not represent as a multi-word container:
        // it is a single refcounted error box, so the Optional is one word and nil is its null
        // extra inhabitant. Measured at 8 bytes for both `any Error` and `(any Error)?`, against
        // 40 for an ordinary `(any P)?`. This must be tested BEFORE the general existential arm
        // below, which would otherwise refuse a shape that is genuinely pointer-sized and strip
        // working surface (every `throws`-shaped Optional-error return lands here).
        if (s_errorExistentials.Contains(inner.Name))
            return DirectOptionalAbiWidth.SingleWord;

        // A non-@objc protocol existential is a multi-word container, not a pointer.
        if (CdeclParamMapper.IsProtocolExistentialType(typeSpec, typeDatabase))
            return DirectOptionalAbiWidth.Unprovable;

        if (s_subWordPrimitives.Contains(inner.Name))
            return DirectOptionalAbiWidth.SingleWord;

        // Array/Dictionary/Set: one refcounted storage pointer, null as the extra inhabitant.
        // Guard on the payload being fully applied — a bare `Array` with no element type is not
        // something whose layout has been established here.
        if (s_singlePointerContainers.Contains(inner.Name) && inner.GenericParameters.Count > 0)
            return DirectOptionalAbiWidth.SingleWord;

        // String is @frozen and two words (count/flags + object pointer), and it has spare bits,
        // so Optional<String> needs no tag byte and stays at exactly two integer words. This is
        // the one wider-than-a-word shape whose lowering is statically pinned.
        if (inner.Name is "Swift.String" or "String")
            return DirectOptionalAbiWidth.TwoIntegerWords;

        // Everything else — Int/Double/CGFloat (payload plus a tag byte, mixed register classes),
        // resilient and non-@frozen structs (address-only), enums, generic payloads, and any type
        // whose record the database does not carry — is not provable here.
        return DirectOptionalAbiWidth.Unprovable;
    }

    /// <summary>
    /// True when the payload is a Swift <em>value</em> type that the reference predicate accepts
    /// only because it bridges to an ObjC object at a <c>@_cdecl</c> boundary. Such a payload is
    /// not a nullable pointer on the direct CallConvSwift path, so its width is not established
    /// by the reference test. A genuine class or ObjC-rooted payload has a Class-shaped record
    /// and is unaffected; a payload with no record at all is left to the reference predicate,
    /// which only accepts it on evidence this classifier has no better view of.
    /// </summary>
    private static bool IsBridgedValueTypePayload(NamedTypeSpec inner, ITypeDatabase typeDatabase)
        => typeDatabase.TryGetTypeRecord(inner, out var record)
           && record.Kind is TypeRecordKind.Struct or TypeRecordKind.Enum;

    /// <summary>
    /// True when <paramref name="typeSpec"/> is an Optional that cannot be carried correctly by
    /// the direct path's single pointer-sized slot — i.e. anything this classifier could not
    /// prove fits in one word.
    /// </summary>
    internal static bool ExceedsDirectSlot(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => Classify(typeSpec, typeDatabase) is DirectOptionalAbiWidth.TwoIntegerWords
                                            or DirectOptionalAbiWidth.Unprovable;
}
