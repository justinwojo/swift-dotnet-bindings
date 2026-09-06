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
    /// Provably one 8-byte payload word followed by a separate tag byte — nine bytes total, the
    /// shape of every 8-byte primitive with no spare bit patterns to steal
    /// (<c>Optional&lt;Int&gt;</c>, <c>Optional&lt;Double&gt;</c>, <c>Optional&lt;CGFloat&gt;</c>).
    /// Swift returns it in x0 + w1 and takes it as the same two argument words.
    ///
    /// <para>Both words are <b>integer</b> registers even when the payload is floating-point.
    /// Swift lowers an enum payload as opaque integer storage, so <c>Optional&lt;Double&gt;</c>
    /// travels in x0, not d0 — <c>probe_take_opt_double</c> opens with <c>fmov d0, x0</c>,
    /// moving the payload out of the integer register before using it. A carrier that declares
    /// the payload as <c>double</c> is therefore wrong: .NET would faithfully lower that field
    /// into an FP register and silently disagree with Swift. Carriers for this width must use
    /// integer-typed fields only.</para>
    /// </summary>
    WordAndTagByte,

    /// <summary>
    /// The generator cannot prove the physical lowering: the layout is not statically knowable
    /// at all. Resilient/non-<c>@frozen</c> inner types are address-only across their resilience
    /// boundary regardless of their measured runtime size; generic payloads and existential
    /// containers depend on runtime metadata; and any type whose record the database does not
    /// carry is simply unknown.
    ///
    /// <para>Emitting a direct call for one of these is unsound, and the failure is invisible to
    /// both compilers, so the only honest outcome is to refuse.</para>
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
    /// Primitive inner types that occupy a full machine word and have no spare bit patterns for
    /// Swift to use as an extra inhabitant, so <c>Optional</c> of one of them appends a separate
    /// tag byte and lands at nine bytes.
    ///
    /// <para><c>CGFloat</c> is listed under every spelling the databases use for it. It is a
    /// <c>@frozen</c> single-<c>Double</c> wrapper on 64-bit Apple platforms — the only targets
    /// this generator emits for — so it lowers exactly like <c>Double</c>.</para>
    /// </summary>
    private static readonly HashSet<string> s_wordPlusTagPrimitives = new(StringComparer.Ordinal)
    {
        "Swift.Int", "Int", "Swift.UInt", "UInt",
        "Swift.Int64", "Int64", "Swift.UInt64", "UInt64",
        "Swift.Double", "Double",
        "CoreFoundation.CGFloat", "CoreGraphics.CGFloat", "CGFloat",
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

        // A full-word primitive with no spare bits: payload word plus an appended tag byte.
        if (s_wordPlusTagPrimitives.Contains(inner.Name))
            return DirectOptionalAbiWidth.WordAndTagByte;

        // Everything else — resilient and non-@frozen structs (address-only regardless of their
        // measured size), enums, generic payloads, and any type whose record the database does
        // not carry — is not provable here.
        return DirectOptionalAbiWidth.Unprovable;
    }

    /// <summary>
    /// The unmanaged carrier struct that transports <paramref name="typeSpec"/> across a direct
    /// CallConvSwift P/Invoke, or <see langword="null"/> when the single pointer-sized slot is
    /// already the whole value (<see cref="DirectOptionalAbiWidth.SingleWord"/>) or the lowering
    /// is not provable.
    ///
    /// <para>The carrier is pure transport. It collects every byte Swift actually passes so the
    /// value arrives complete; deciding Some vs None from those bytes stays with the Optional's
    /// value-witness table, which is the only ABI-stable reader of an extra-inhabitant tag.
    /// Nothing here infers a spare-bit encoding.</para>
    /// </summary>
    internal static string? TryGetCarrierTypeName(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => Classify(typeSpec, typeDatabase) switch
        {
            DirectOptionalAbiWidth.WordAndTagByte => "global::Swift.Runtime.SwiftOptionalCarrier9",
            DirectOptionalAbiWidth.TwoIntegerWords => "global::Swift.Runtime.SwiftOptionalCarrier16",
            _ => null,
        };

    /// <summary>
    /// The carrier <paramref name="member"/> will actually emit for <paramref name="typeSpec"/>,
    /// or <see langword="null"/> when this member carries that Optional some other way.
    ///
    /// <para>This is the decision itself, not an ingredient of it: the P/Invoke emitter, the
    /// wrapper's argument marshalling, and the blittability predictor that decides whether the
    /// member is callable at all must all reach the SAME answer for a given member and type, so
    /// they ask here rather than each re-combining <see cref="UsesSwiftSideCarrier"/> with
    /// <see cref="TryGetCarrierTypeName"/>. Recombining is what let them drift once already —
    /// the emitter had learned to emit a blittable carrier parameter while the predictor still
    /// answered "Optional argument, therefore non-blittable", so members whose P/Invoke was by
    /// then perfectly well-formed were tombstoned as uncallable.</para>
    ///
    /// <para><paramref name="isInOut"/> withdraws the carrier entirely. A carrier transports the
    /// value <em>by value</em>, which is precisely the wrong shape for an <c>inout</c> argument:
    /// Swift expects the address of the caller's storage there and writes back through it. Handing
    /// it a register pair holding a copy means the callee's write lands nowhere and, worse, that it
    /// reads an address out of what is actually payload data. Refusing the carrier leaves the
    /// member on the floor's refusal path, which is the honest outcome — an <c>inout</c> wide
    /// Optional has no sound direct lowering here, only an unproven one.</para>
    /// </summary>
    internal static string? TryGetDirectCarrier(
        MethodDecl member, TypeSpec typeSpec, ITypeDatabase typeDatabase, bool isInOut = false)
        => isInOut || UsesSwiftSideCarrier(member) || ReturnsOpaqueExistential(member)
            ? null
            : TryGetCarrierTypeName(typeSpec, typeDatabase);

    /// <summary>
    /// True when the member returns an opaque <c>some P</c> existential, which forces a Swift-side
    /// wrapper to erase the opaque type before the value can cross. Such a member marshals its
    /// arguments through that wrapper rather than through direct register slots.
    ///
    /// <para>Kept in the carrier oracle rather than in <see cref="UsesSwiftSideCarrier"/> because
    /// the two sets answer different questions. <see cref="UsesSwiftSideCarrier"/> names the
    /// members the emission floor need not guard at all; this member still needs guarding — it
    /// takes no carrier, so a wide Optional argument on it would truncate exactly as before — it
    /// simply cannot be rescued by one.</para>
    /// </summary>
    private static bool ReturnsOpaqueExistential(MethodDecl member)
        => member.CSSignature.Count > 0
           && member.CSSignature.First().SwiftTypeSpec is ProtocolListTypeSpec { IsOpaque: true };

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
    /// True when the member moves its values through memory on the Swift side — an
    /// <c>@_cdecl</c> wrapper, a native thunk, a <c>@_silgen_name</c> free function, the
    /// Optional-pointer out-buffer wrapper, or an async completion handler. Width stops mattering
    /// for these, so they must keep their existing slot types and never take a carrier.
    ///
    /// <para>This is the same set the emission floor early-outs on, named once so the floor's
    /// idea of "the direct path" and the emitter's cannot drift apart. If they did, the half that
    /// still believed a shape was direct would either refuse a member the other emits correctly,
    /// or — the dangerous direction — emit a bare slot for one the floor had stopped guarding.</para>
    ///
    /// <para>This answers a WIDTH question only: does the value move through memory, so that its
    /// register footprint stops mattering? It is not an ownership predicate, and reusing it as one
    /// is wrong on exactly one arm. The native assembly thunk moves the value through memory yet
    /// owns nothing — it shifts registers and tail-calls the real accessor — so a callee reached
    /// through it still consumes what a Swift-source wrapper would have borrowed. Ownership is
    /// asked of <see cref="CalleeArgumentOwnership"/> instead.</para>
    /// </summary>
    internal static bool UsesSwiftSideCarrier(MethodDecl methodDecl)
        => methodDecl.UsesCdeclWrapper
           || methodDecl.UsesNativeThunk
           || methodDecl.UsesFreeFunctionWrapper
           || methodDecl.UsesWrapperLibrary
           || methodDecl.HasOptionalPointerWrapper
           || methodDecl.IsAsync;

    /// <summary>
    /// True when <paramref name="typeSpec"/> is an Optional too wide for a single pointer-sized
    /// P/Invoke slot. Such a shape needs a carrier; it is not by itself a reason to refuse — see
    /// <see cref="HasNoSoundDirectCallPath"/> for that question.
    /// </summary>
    internal static bool ExceedsDirectSlot(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => Classify(typeSpec, typeDatabase) is DirectOptionalAbiWidth.WordAndTagByte
                                            or DirectOptionalAbiWidth.TwoIntegerWords
                                            or DirectOptionalAbiWidth.Unprovable;

    /// <summary>
    /// True when <paramref name="typeSpec"/> is an Optional the direct path can carry neither in
    /// its single slot nor in a proven carrier — the condition the emission floor tombstones on.
    ///
    /// <para>Written as "too wide AND no carrier" rather than as a direct test for
    /// <see cref="DirectOptionalAbiWidth.Unprovable"/> so the refusal stays keyed to the absence
    /// of a call path. A width added to the enum without a matching carrier is refused by
    /// construction; it cannot fall through into a truncated direct call.</para>
    /// </summary>
    internal static bool HasNoSoundDirectCallPath(
        MethodDecl member, TypeSpec typeSpec, ITypeDatabase typeDatabase, bool isInOut = false)
        => (ExceedsDirectSlot(typeSpec, typeDatabase)
            && TryGetDirectCarrier(member, typeSpec, typeDatabase, isInOut) is null)
           || RendersAsForeignObject(typeSpec, typeDatabase);

    /// <summary>
    /// True when the direct path's only rendering of <paramref name="typeSpec"/> is an
    /// Objective-C collection object that is not the value Swift reads.
    ///
    /// <para>Width is sound here and that is the trap: <c>[URL]?</c> really is one refcounted
    /// pointer, so the slot is the right size. What crosses it is the wrong object. The C# side
    /// builds an <c>NSArray</c> and passes its handle, because the conversion that renders an
    /// ObjC-bridgeable container asks what the payload bridges TO without asking whether there is
    /// a boundary to bridge AT. A <c>@_cdecl</c> wrapper supplies one and unwraps the collection
    /// back to Swift on entry; a direct <c>CallConvSwift</c> accessor supplies none, so Swift
    /// receives an <c>NSArray</c> pointer where its own native array storage belongs — and for an
    /// element that is a struct, that storage has no representation an ObjC object can inhabit.
    /// The getter is wrong in the same way and worse: it reads Swift's array storage back as an
    /// <c>NSArray</c> and takes ownership of it.</para>
    ///
    /// <para>Sibling to the bridged-value-type exclusion in <see cref="Classify"/>, one level out:
    /// that one covers a payload that bridges (<c>URL?</c>), this one a container whose ELEMENTS
    /// bridge. Kept here rather than in the classifier because it is not a claim about width —
    /// stating it as one would make the classifier answer a layout question with a marshalling
    /// fact. An <c>Optional&lt;ObjC class&gt;</c> deliberately does NOT land here: for a class
    /// reference the object pointer IS Swift's representation, so that shape stays callable and
    /// its ownership is settled at the call instead.</para>
    /// </summary>
    internal static bool RendersAsForeignObject(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => CdeclParamMapper.IsOptionalObjCBridgeableContainer(typeSpec, typeDatabase);
}
