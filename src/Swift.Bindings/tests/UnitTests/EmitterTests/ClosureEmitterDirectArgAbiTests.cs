// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The register schema of a direct (<c>CallConvSwift</c>) closure callback's arguments.
///
/// Swift hands a LOADABLE closure argument to the callback by value — the value is exploded into
/// scalar leaves that arrive in registers — while an address-only argument arrives as a pointer.
/// The trampoline used to declare every non-blittable argument as one <c>void*</c> and read it as
/// the value's ADDRESS, which is right only where the register already holds a pointer. For an
/// array, a string or a <c>Result</c> it dereferenced the value's first word, which compiles and
/// then reads unrelated memory.
///
/// These tests pin the classification (which shapes are exploded, how many registers each occupies,
/// which stay indirect, which are refused), the emitted trampoline that follows from it, and the
/// fail-closed skip for a loadable shape whose explosion is not modelled.
/// </summary>
public class ClosureEmitterDirectArgAbiTests
{
    #region Argument classification

    [Fact]
    public void ClassifyDirectClosureArg_Array_IsOneExplodedWord()
    {
        // An array is one refcounted buffer pointer, passed by value. One register — but the
        // register is the VALUE, so the callback must marshal from the parameter's own address.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(ArrayOfDouble());

        Assert.Equal(DirectClosureArgAbi.ExplodedWords, lowering.Abi);
        Assert.Equal(1, lowering.WordCount);
        Assert.Empty(lowering.ExtraWordTypes);
    }

    [Fact]
    public void ClassifyDirectClosureArg_OptionalArray_IsOneExplodedWord()
    {
        // Optional<Array> reuses the buffer pointer's null as the .none extra inhabitant, so it
        // stays a single register.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(Optional(ArrayOfDouble()));

        Assert.Equal(DirectClosureArgAbi.ExplodedWords, lowering.Abi);
        Assert.Equal(1, lowering.WordCount);
    }

    [Fact]
    public void ClassifyDirectClosureArg_String_IsTwoExplodedWords()
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(new NamedTypeSpec("Swift.String"));

        Assert.Equal(DirectClosureArgAbi.ExplodedWords, lowering.Abi);
        Assert.Equal(2, lowering.WordCount);
        Assert.Equal(16, lowering.BufferBytes);
    }

    [Fact]
    public void ClassifyDirectClosureArg_OptionalString_IsTwoExplodedWords()
    {
        // Optional<String> borrows String's own extra inhabitants rather than adding a tag word:
        // the absent case is an all-zero image, which is why the SECOND word is what separates
        // nil from the empty string.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(Optional(new NamedTypeSpec("Swift.String")));

        Assert.Equal(DirectClosureArgAbi.ExplodedWords, lowering.Abi);
        Assert.Equal(2, lowering.WordCount);
    }

    [Fact]
    public void ClassifyDirectClosureArg_ResultOverOptionalClass_IsPayloadWordPlusTagByte()
    {
        // Result<T, any Error> is a two-case enum: the payload occupies its own words and the case
        // tag is a trailing byte. Enum lowering is word-based, so a one-word payload gives
        // (word, tag) regardless of what the payload's fields are.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            ResultOverAnyError(Optional(new NamedTypeSpec("TestModule.Loader"))));

        Assert.Equal(DirectClosureArgAbi.ExplodedWords, lowering.Abi);
        Assert.Equal(2, lowering.WordCount);
        Assert.Equal(new[] { "byte" }, lowering.ExtraWordTypes.ToArray());
    }

    [Fact]
    public void ClassifyDirectClosureArg_ResultOverString_IsTwoPayloadWordsPlusTagByte()
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            ResultOverAnyError(new NamedTypeSpec("Swift.String")));

        Assert.Equal(DirectClosureArgAbi.ExplodedWords, lowering.Abi);
        Assert.Equal(3, lowering.WordCount);
        Assert.Equal(new[] { "void*", "byte" }, lowering.ExtraWordTypes.ToArray());
    }

    [Fact]
    public void ClassifyDirectClosureArg_ResultOverOpaqueExistential_StaysIndirect()
    {
        // An address-only payload makes the whole enum address-only: Swift passes a pointer, which
        // is exactly what the historical model already assumed. Nothing about this shape moves.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            ResultOverAnyError(Optional(new NamedTypeSpec("Swift.Any") { IsAny = true })));

        Assert.Equal(DirectClosureArgAbi.Indirect, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_Class_IsReferenceWord()
    {
        // The register IS the object pointer, so address and value coincide and the historical
        // model was already correct — this arm must not move.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(new NamedTypeSpec("TestModule.Loader"));

        Assert.Equal(DirectClosureArgAbi.ReferenceWord, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_NonFrozenStruct_StaysIndirect()
    {
        // A resilient struct has no layout the caller can see, so Swift passes it @in_guaranteed.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(new NamedTypeSpec("TestModule.Settings"));

        Assert.Equal(DirectClosureArgAbi.Indirect, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_FrozenStruct_IsUnmodelled()
    {
        // A frozen struct is exploded FIELD-wise, and each chunk lands in an integer or a
        // floating-point register depending on the field it holds — a two-Double struct arrives in
        // d0/d1, not x0/x1 — so the word-image model cannot reproduce it.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(new NamedTypeSpec("TestModule.Point"));

        Assert.Equal(DirectClosureArgAbi.Unmodelled, lowering.Abi);
        Assert.Contains("TestModule.Point", lowering.Shape);
    }

    [Fact]
    public void ClassifyDirectClosureArg_OptionalFrozenStruct_IsUnmodelled()
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(Optional(new NamedTypeSpec("TestModule.Point")));

        Assert.Equal(DirectClosureArgAbi.Unmodelled, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_ArraySlice_IsUnmodelled()
    {
        // A slice is four registers (buffer, start, count, owner).
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.Double")));

        Assert.Equal(DirectClosureArgAbi.Unmodelled, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_OptionalInt_IsPayloadWordPlusTagByte()
    {
        // Int fills its register completely, so the tag has no spare bits to hide in and becomes a
        // byte after the payload word — the same image the one-word Result arm builds.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(Optional(new NamedTypeSpec("Swift.Int")));

        Assert.Equal(DirectClosureArgAbi.ExplodedWords, lowering.Abi);
        Assert.Equal(2, lowering.WordCount);
        Assert.Equal(new[] { "byte" }, lowering.ExtraWordTypes.ToArray());
    }

    [Fact]
    public void ClassifyDirectClosureArg_OptionalDouble_IsPayloadWordPlusTagByte()
    {
        // Enum lowering is word-based rather than field-based, so the Double payload rides an
        // INTEGER register and the schema is the same as Optional<Int>'s — not d0 plus a tag.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(Optional(new NamedTypeSpec("Swift.Double")));

        Assert.Equal(DirectClosureArgAbi.ExplodedWords, lowering.Abi);
        Assert.Equal(2, lowering.WordCount);
        Assert.Equal(new[] { "byte" }, lowering.ExtraWordTypes.ToArray());
    }

    [Fact]
    public void ClassifyDirectClosureArg_OptionalNarrowScalar_IsUnmodelled()
    {
        // The counterpart, and the reason the modelled set is enumerated by WIDTH rather than by
        // "is a primitive": a scalar narrower than a register leaves spare bits, the tag packs into
        // them, and the whole value stays a SINGLE register with no trailing byte. Reusing the
        // 8-byte schema here would declare a word that Swift never passes.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(Optional(new NamedTypeSpec("Swift.Int32")));

        Assert.Equal(DirectClosureArgAbi.Unmodelled, lowering.Abi);
        Assert.Contains("Swift.Int32", lowering.Shape);
    }

    [Fact]
    public void ClassifyDirectClosureArg_OptionalBool_IsUnmodelled()
    {
        // Bool is the extreme of the same rule: Optional<Bool> is one BYTE, not a word plus a tag.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(Optional(new NamedTypeSpec("Swift.Bool")));

        Assert.Equal(DirectClosureArgAbi.Unmodelled, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_OptionalResultOverAnyError_IsUnmodelled()
    {
        // Optional over a loadable enum is loadable, and its tag negotiates with the payload enum's
        // OWN tag byte rather than adding one — so its register image is neither the payload's nor
        // the payload's plus a word. The bare Result stays modelled; wrapping it is refused rather
        // than assumed to inherit the payload's schema.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            Optional(ResultOverAnyError(new NamedTypeSpec("Swift.Int"))));

        Assert.Equal(DirectClosureArgAbi.Unmodelled, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_OptionalResultOverResilientPayload_StaysIndirect()
    {
        // The companion to the case above. A Result whose success payload is address-only is itself
        // address-only, and so is an Optional over it — the historical address model is right there,
        // and refusing it would drop a member that binds correctly today.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            Optional(ResultOverAnyError(new NamedTypeSpec("TestModule.Settings"))));

        Assert.Equal(DirectClosureArgAbi.Indirect, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_OptionalOptionalResilientStruct_StaysIndirect()
    {
        // Address-only-ness passes through any depth of Optional: a resilient payload keeps every
        // Optional above it address-only, so the address model stays correct and the shape must not
        // be refused just because the payload is not existential at the first level.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            Optional(Optional(new NamedTypeSpec("TestModule.Settings"))));

        Assert.Equal(DirectClosureArgAbi.Indirect, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_OptionalOptionalInt_IsUnmodelled()
    {
        // A nested Optional widens the tag instead of stacking a second byte, so the payload's own
        // schema does not carry over.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            Optional(Optional(new NamedTypeSpec("Swift.Int"))));

        Assert.Equal(DirectClosureArgAbi.Unmodelled, lowering.Abi);
    }

    #endregion

    #region Emitted trampoline

    [Fact]
    public void EmitEscapingClosureCallback_SwiftMode_ArrayThenOptionalString_DeclaresTheExtraWord()
    {
        // ([Double], String?) -> Void: the array is one register, the optional string two. The
        // second string word must be a declared parameter — dropping it leaves the callback and
        // the function-pointer type disagreeing on arity, and the delivered value truncated.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var closureTypeSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { ArrayOfDouble(), Optional(new NamedTypeSpec("Swift.String")) }),
            TupleTypeSpec.Empty);

        var output = new StringWriter();
        ClosureEmitter.EmitEscapingClosureCallback(
            new CSharpWriter(output), "deliver", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule7deliveryyF", useCdecl: false);

        var result = output.ToString();
        Assert.Contains("CallConvSwift", result);
        Assert.Contains("arg1_w1", result);
        // Two-word value: its image is rebuilt in a stack buffer and marshalled from there.
        Assert.Contains("stackalloc byte[16]", result);
        Assert.Contains("new IntPtr(__arg1)", result);
        // One-word value: the parameter's own storage IS the value, so its address is taken
        // directly instead of being dereferenced.
        Assert.Contains("new IntPtr(&arg0)", result);
    }

    [Fact]
    public void EmitEscapingClosureCallback_SwiftMode_ClassArg_KeepsTheRegisterAsTheAddress()
    {
        // The reference arm must be untouched: the register already holds the object pointer.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.Loader"), TupleTypeSpec.Empty);

        var output = new StringWriter();
        ClosureEmitter.EmitEscapingClosureCallback(
            new CSharpWriter(output), "deliver", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule7deliveryyF", useCdecl: false);

        var result = output.ToString();
        Assert.DoesNotContain("arg0_w1", result);
        Assert.DoesNotContain("stackalloc byte[", result);
    }

    [Fact]
    public void EmitEscapingClosureCallback_SwiftMode_OptionalArrayArg_TestsTheWordBeforeMarshalling()
    {
        // Optional<Array> spends the buffer reference's zero as its .none inhabitant, so the
        // absent case arrives as a zero register. Marshalling it unconditionally builds a live
        // wrapper over a null buffer, which the caller reads as "an empty collection was
        // delivered" rather than "nothing was". An empty array is a non-zero shared singleton,
        // so the zero test separates the two.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var closureTypeSpec = new ClosureTypeSpec(Optional(ArrayOfDouble()), TupleTypeSpec.Empty);

        var output = new StringWriter();
        ClosureEmitter.EmitEscapingClosureCallback(
            new CSharpWriter(output), "deliver", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule7deliveryyF", useCdecl: false);

        var result = output.ToString();
        Assert.Contains("arg0 != null", result);
        Assert.Contains(": null", result);
    }

    [Fact]
    public void EmitEscapingClosureCallback_SwiftMode_NonOptionalArrayArg_HasNoNoneTest()
    {
        // The positive control: a non-optional array has no .none to test for, and a zero word
        // there would be a bug on the Swift side rather than an absent value.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var closureTypeSpec = new ClosureTypeSpec(ArrayOfDouble(), TupleTypeSpec.Empty);

        var output = new StringWriter();
        ClosureEmitter.EmitEscapingClosureCallback(
            new CSharpWriter(output), "deliver", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule7deliveryyF", useCdecl: false);

        Assert.DoesNotContain("arg0 != null", output.ToString());
    }

    [Fact]
    public void EmitEscapingClosureCallback_SwiftMode_OptionalIntArg_MarshalsFromTheRebuiltBuffer()
    {
        // (Int?) -> Void. The Optional's memory image is the payload word followed by a tag byte,
        // and on this lane both arrive in registers — so the Optional marshalling must read the
        // rebuilt buffer. Reading the first parameter as an address would dereference the payload's
        // own bits, which for a small Int is an unmapped address and for a large one is a wild read.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var closureTypeSpec = new ClosureTypeSpec(
            Optional(new NamedTypeSpec("Swift.Int")), TupleTypeSpec.Empty);

        var output = new StringWriter();
        ClosureEmitter.EmitEscapingClosureCallback(
            new CSharpWriter(output), "deliver", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule7deliveryyF", useCdecl: false);

        var result = output.ToString();
        Assert.Contains("arg0_w1", result);
        Assert.Contains("stackalloc byte[16]", result);
        Assert.Contains("MarshalOptionalFromSwift", result);
        Assert.Contains("new IntPtr(__arg0)", result);
        Assert.DoesNotContain("MarshalOptionalFromSwift<nint>(new IntPtr(arg0))", result);
    }

    [Fact]
    public void EmitEscapingClosureCallback_CdeclMode_OptionalIntArg_KeepsTheHeapAddress()
    {
        // The counterpart: the @_cdecl adapter allocates the Optional and hands over its address, so
        // there is no extra register and the parameter itself is the address to marshal from.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var closureTypeSpec = new ClosureTypeSpec(
            Optional(new NamedTypeSpec("Swift.Int")), TupleTypeSpec.Empty);

        var output = new StringWriter();
        ClosureEmitter.EmitEscapingClosureCallback(
            new CSharpWriter(output), "deliver", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule7deliveryyF", useCdecl: true);

        var result = output.ToString();
        Assert.DoesNotContain("arg0_w1", result);
        Assert.DoesNotContain("stackalloc byte[", result);
        Assert.Contains("MarshalOptionalFromSwift", result);
        Assert.Contains("new IntPtr(arg0)", result);
    }

    [Fact]
    public void EmitEscapingClosureCallback_CdeclMode_StringArg_DeclaresNoExtraWord()
    {
        // The @_cdecl lane's Swift-side adapter hands over a pointer it made itself, so the
        // by-value schema does not apply there.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.String"), TupleTypeSpec.Empty);

        var output = new StringWriter();
        ClosureEmitter.EmitEscapingClosureCallback(
            new CSharpWriter(output), "deliver", "callback", closureTypeSpec, closureHandler,
            "$s10TestModule7deliveryyF", useCdecl: true);

        var result = output.ToString();
        Assert.Contains("CallConvCdecl", result);
        Assert.DoesNotContain("arg0_w1", result);
        Assert.DoesNotContain("stackalloc byte[", result);
    }

    #endregion

    #region Fail-closed skip

    [Fact]
    public void ShouldSkipMethodEmission_DirectLaneFrozenStructClosureArg_SkipsNamingTheShape()
    {
        // ([Double], Point) -> Void in constructor position: the array keeps the closure off the
        // @convention(c) adapter and off the method-closure bridge (neither claims initializers),
        // so it lands on the direct trampoline — where the frozen struct's explosion is not
        // modelled. Emitting it would COMPILE and then read the wrong registers, so the member is
        // refused instead, which is what makes this a soundness gate rather than a predictor of a
        // compile error.
        var typeDatabase = CreateTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { ArrayOfDouble(), new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var method = CreateClosureConstructor(closureSpec);

        var reason = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var details);

        Assert.Equal(SkipReason.UnsupportedClosure, reason);
        Assert.NotNull(details);
        Assert.Contains("TestModule.Point", details!);
        Assert.Contains("by value in registers", details!);
    }

    [Fact]
    public void ClassifyDirectClosureArg_FrozenPayloadEnum_IsUnmodelled()
    {
        // A @frozen enum with payloads is loadable: Swift explodes it, and the explosion depends on
        // the payload layouts and the spare bits the tag borrows from them. Neither is derivable
        // here, so the shape is refused rather than read through a pointer that Swift never passed.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(new NamedTypeSpec("TestModule.Outcome"));

        Assert.Equal(DirectClosureArgAbi.Unmodelled, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_ResilientPayloadEnum_StaysIndirect()
    {
        // The counterpart: a non-@frozen enum has no layout the caller can see, so Swift passes its
        // address. The historical indirect model is correct and must not be swept into the refusal.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(new NamedTypeSpec("TestModule.Phase"));

        Assert.Equal(DirectClosureArgAbi.Indirect, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_FrozenSimpleEnum_IsUnmodelled()
    {
        // A @frozen no-payload enum IS loadable, but what Swift loads is the declaration-order tag
        // sized to the case count — a one-byte 1 for the second case of a three-case Int32-raw enum —
        // while the P/Invoke translation declares the raw integer and the emitted C# enum spells its
        // members with the Swift source raw values. Loadable is not the same as matching, so the
        // by-value model does not rescue it either and the member is failed closed.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(new NamedTypeSpec("TestModule.Mode"));

        Assert.Equal(DirectClosureArgAbi.Unmodelled, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_NonFrozenSimpleEnum_IsUnmodelled()
    {
        // Its resilient twin is @in_guaranteed — Swift hands over an address — while the P/Invoke
        // translation declares the same raw integer, so the callback would read a truncated pointer
        // as a case value. Neither model can express that, so the member is failed closed.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(new NamedTypeSpec("TestModule.Stage"));

        Assert.Equal(DirectClosureArgAbi.Unmodelled, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_OptionalSimpleEnum_IsUnmodelled()
    {
        // An Optional does not launder the mismatch. The emitter's nil-for-none reader casts the
        // argument straight to the enum's declared raw integer, so the payload is still read at the
        // width and the values the declaration names rather than the tag the register carries.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            Optional(new NamedTypeSpec("TestModule.Stage")));

        Assert.Equal(DirectClosureArgAbi.Unmodelled, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_ResultOverSimpleEnum_IsUnmodelled()
    {
        // As a Result success payload the enum would be rebuilt out of the stack buffer as its
        // declared raw integer, which is the same disagreement one layer in.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            ResultOverAnyError(new NamedTypeSpec("TestModule.Mode")));

        Assert.Equal(DirectClosureArgAbi.Unmodelled, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_TupleContainingSimpleEnum_IsUnmodelled()
    {
        // A tuple is declared field-wise, so an enum element keeps its own mis-declared integer
        // field; the tuple as a whole would otherwise pass through on the strength of its blittable
        // declaration.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            new TupleTypeSpec(new TypeSpec[]
            {
                new NamedTypeSpec("TestModule.Mode"),
                new NamedTypeSpec("Swift.Int32"),
            }));

        Assert.Equal(DirectClosureArgAbi.Unmodelled, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_TupleOfPrimitives_StaysPassThrough()
    {
        // The control for the tuple walk: without an enum element the tuple is still declared
        // exactly as it arrives, so the recursion must not reject tuples wholesale.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            new TupleTypeSpec(new TypeSpec[]
            {
                new NamedTypeSpec("Swift.Int32"),
                new NamedTypeSpec("Swift.Double"),
            }));

        Assert.Equal(DirectClosureArgAbi.PassThrough, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_ArrayOfSimpleEnum_StaysExplodedWords()
    {
        // The boundary of the walk. An Array's elements never reach a register — the container
        // arrives as one buffer reference and the element conversion happens in the container
        // marshalling — so recursing into generic arguments would refuse a shape this lane carries
        // correctly.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("TestModule.Mode")));

        Assert.Equal(DirectClosureArgAbi.ExplodedWords, lowering.Abi);
        Assert.Equal(1, lowering.WordCount);
    }

    [Fact]
    public void ClassifyDirectClosureArg_ResultOverResilientPayloadEnum_StaysIndirect()
    {
        // A Result inherits the address model from an address-only success payload, exactly as it
        // does from a resilient struct. Refusing it instead would skip a member the address model
        // already binds correctly.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            ResultOverAnyError(new NamedTypeSpec("TestModule.Phase")));

        Assert.Equal(DirectClosureArgAbi.Indirect, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_NestedResultOverResilientStruct_StaysIndirect()
    {
        // The payload question recurses through Result as it does through Optional, so a Result whose
        // success payload is itself a Result over an address-only type stays on the address model.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            ResultOverAnyError(ResultOverAnyError(new NamedTypeSpec("TestModule.Settings"))));

        Assert.Equal(DirectClosureArgAbi.Indirect, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_OptionalFrozenPayloadEnum_IsUnmodelled()
    {
        // Optional over an unmodelled loadable payload is itself unmodelled: the Optional is loadable
        // exactly when its payload is, and its register count is the payload's plus whatever the tag
        // negotiates with the payload's spare bits. Falling back to the address model here would be
        // the same wild read as the bare enum.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("TestModule.Outcome")));

        Assert.Equal(DirectClosureArgAbi.Unmodelled, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_OptionalResilientPayloadEnum_StaysIndirect()
    {
        // The counterpart: the payload is address-only, so the Optional is too and the historical
        // indirect model is the correct one. The refusal must not widen to swallow it.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("TestModule.Phase")));

        Assert.Equal(DirectClosureArgAbi.Indirect, lowering.Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_OptionalArraySlice_IsUnmodelled()
    {
        // The Optional is loadable exactly when its payload is, and a slice payload explodes into
        // four registers the schema does not model — so the Optional inherits the payload's verdict
        // instead of falling through to the address model.
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var lowering = closureHandler.ClassifyDirectClosureArg(
            Optional(new NamedTypeSpec("Swift.ArraySlice", new NamedTypeSpec("Swift.Double"))));

        Assert.Equal(DirectClosureArgAbi.Unmodelled, lowering.Abi);
    }

    [Fact]
    public void ShouldSkipMethodEmission_XCFrameworkClassCtorWithCdeclShapedClosureArg_IsNotSkipped()
    {
        // (Point) -> Void is @convention(c)-compatible in shape, so NeedsClosureCdeclWrapper holds —
        // and that is precisely the predicate the constructor @_cdecl wrapper consults before it
        // accepts a closure. In xcframework mode such an initializer therefore lands on the wrapper
        // lane, where the frozen struct is heap-adapted and no register explosion happens. Refusing
        // it here would drop a member that binds correctly.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var method = CreateClosureConstructor(closureSpec);

        var reason = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(reason);
    }

    [Fact]
    public void ShouldSkipMethodEmission_DirectModeClassCtorWithCdeclShapedClosureArg_IsSkipped()
    {
        // The counterpart of the test above, and the reason the lane exemption is conditioned on the
        // generation mode: every @_cdecl and bridge lane emits its adapter into a companion wrapper
        // library that exists only in xcframework mode. Without one the same initializer falls back
        // to the direct trampoline, where the frozen struct really does arrive in registers.
        var typeDatabase = CreateTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var method = CreateClosureConstructor(closureSpec);

        var reason = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var details);

        Assert.Equal(SkipReason.UnsupportedClosure, reason);
        Assert.NotNull(details);
        Assert.Contains("TestModule.Point", details!);
    }

    [Fact]
    public void ValidatePropertyEmission_ClosureSetterWithUnmodelledArg_IsSkipped()
    {
        // Property emission validates through the pipeline and then emits its accessors directly, so
        // the gate has to be stated here or a writable `([Double], Point) -> Void` property reaches
        // the direct trampoline with the address model. The property @_cdecl wrapper refuses a bare
        // closure setter outright, so there is no lane to fall back on.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { ArrayOfDouble(), new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var property = CreateClosureProperty(closureSpec, withSetter: true);

        var result = new MemberValidationPipeline(typeDatabase).ValidatePropertyEmission(property, null);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.UnsupportedClosure, result.Reason);
        Assert.Contains("TestModule.Point", result.Details!);
    }

    [Fact]
    public void ValidatePropertyEmission_ClosureSetterWithModelledArg_IsNotSkipped()
    {
        // The positive control: the same settable closure property with shapes whose explosion IS
        // modelled must keep binding.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { ArrayOfDouble(), Optional(new NamedTypeSpec("Swift.String")) }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var property = CreateClosureProperty(closureSpec, withSetter: true);

        var result = new MemberValidationPipeline(typeDatabase).ValidatePropertyEmission(property, null);

        Assert.True(result.ShouldEmit);
    }

    [Fact]
    public void ValidatePropertyEmission_ReadOnlyClosurePropertyWithUnmodelledArg_IsNotSkipped()
    {
        // A getter hands the Swift closure BACK to C#, which invokes it through the Swift-side
        // invoke thunk — a different convention with its own argument transport. Only the setter
        // builds the reverse trampoline this schema governs, so a read-only property must not be
        // caught by the gate.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { ArrayOfDouble(), new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var property = CreateClosureProperty(closureSpec, withSetter: false);

        var result = new MemberValidationPipeline(typeDatabase).ValidatePropertyEmission(property, null);

        Assert.True(result.ShouldEmit);
    }

    [Fact]
    public void ValidateSubscriptEmission_ClosureIndexWithUnmodelledArg_IsSkipped()
    {
        // A closure index parameter travels into Swift on BOTH accessors, and the subscript path
        // P/Invokes the raw dispatch thunk with no @_cdecl transport of its own — so its callback
        // arguments are always the direct trampoline's.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { ArrayOfDouble(), new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var subscriptDecl = CreateClosureIndexSubscript(closureSpec);

        var result = new MemberValidationPipeline(typeDatabase).ValidateSubscriptEmission(subscriptDecl, null);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.UnsupportedClosure, result.Reason);
        Assert.Contains("TestModule.Point", result.Details!);
    }

    [Fact]
    public void ValidateSubscriptEmission_ClosureIndexWithModelledArg_IsNotSkipped()
    {
        // The subscript positive control.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { ArrayOfDouble(), Optional(new NamedTypeSpec("Swift.String")) }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var subscriptDecl = CreateClosureIndexSubscript(closureSpec);

        var result = new MemberValidationPipeline(typeDatabase).ValidateSubscriptEmission(subscriptDecl, null);

        Assert.True(result.ShouldEmit);
    }

    [Fact]
    public void ShouldSkipMethodEmission_DirectLaneModelledClosureArgs_IsNotSkipped()
    {
        // The positive control for the gate above: the same lane, the same constructor position,
        // and shapes whose explosion IS modelled. A gate that also rejects these would silently
        // drop members that bind correctly today.
        var typeDatabase = CreateTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { ArrayOfDouble(), Optional(new NamedTypeSpec("Swift.String")) }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var method = CreateClosureConstructor(closureSpec);

        var reason = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(reason);
    }

    [Fact]
    public void ShouldSkipMethodEmission_XCFrameworkFailableResilientStructCtor_IsSkipped()
    {
        // The lane exemption trusts NeedsClosureCdeclWrapper to stand for "some @_cdecl lane claims
        // this member", but the constructor wrapper independently refuses a failable initializer on a
        // resilient struct, and the standalone closure wrapper only takes non-failable frozen-struct
        // initializers. No wrapper claims it, so it lands on the direct trampoline after all — even in
        // xcframework mode, and even though the closure's shape is @convention(c)-compatible.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var method = CreateFailableResilientStructClosureConstructor(closureSpec);

        var reason = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var details);

        Assert.Equal(SkipReason.UnsupportedClosure, reason);
        Assert.NotNull(details);
        Assert.Contains("TestModule.Point", details!);
    }

    [Fact]
    public void ShouldSkipMethodEmission_XCFrameworkNonFailableResilientStructCtor_IsNotSkipped()
    {
        // The control that makes the test above discriminating: the SAME resilient struct and the
        // SAME closure, only non-failable. The constructor wrapper's refusal is keyed on failability,
        // so this initializer really does take the wrapper lane and must keep binding — the skip
        // above therefore cannot be coming from the parent's struct-ness.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var method = CreateFailableResilientStructClosureConstructor(closureSpec) with { IsFailable = false };

        var reason = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(reason);
    }

    [Fact]
    public void ShouldSkipMethodEmission_XCFrameworkCtorWithMetatypeSibling_IsSkipped()
    {
        // Failability is not the only wrapper refusal a closure shape cannot express. A sibling
        // parameter with no C representation — a metatype here — makes BOTH wrapper lanes decline the
        // member, and the standalone closure wrapper takes only frozen-struct initializers, so this
        // class initializer reaches the direct trampoline with its unmodelled callback argument.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var method = WithMetatypeParameter(CreateClosureConstructor(closureSpec));

        var reason = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out var details);

        Assert.Equal(SkipReason.UnsupportedClosure, reason);
        Assert.NotNull(details);
        Assert.Contains("TestModule.Point", details!);
    }

    [Fact]
    public void ShouldSkipMethodEmission_XCFrameworkMethodWithMetatypeSibling_IsNotSkipped()
    {
        // The bound on the refusal above: an ordinary METHOD that needs a closure wrapper always gets
        // one, because the standalone closure wrapper claims it on the closure predicate alone
        // whatever the method @_cdecl wrapper made of the rest of the signature. The same metatype
        // sibling that leaves a class initializer on the direct trampoline therefore does not leave a
        // method there, and skipping it would drop a member that binds.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var method = WithMetatypeParameter(CreateClosureMethod(closureSpec));

        var reason = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(reason);
    }

    [Fact]
    public void ShouldSkipMethodEmission_XCFrameworkFrozenStructCtorWithMetatypeSibling_IsNotSkipped()
    {
        // The initializer-side bound. The standalone closure wrapper's constructor arm takes a
        // non-failable initializer on a frozen struct, so that one keeps its @_cdecl callback even
        // when the constructor wrapper declines the signature — only initializers OUTSIDE that arm
        // fall through to the direct trampoline.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var ctor = CreateFailableResilientStructClosureConstructor(closureSpec) with { IsFailable = false };
        var frozenParent = ((StructDecl)ctor.ParentDecl!) with { IsFrozen = true };
        var method = WithMetatypeParameter(ctor with { ParentDecl = frozenParent });

        var reason = MemberEmissionValidator.ShouldSkipMethodEmission(method, typeDatabase, out _);

        Assert.Null(reason);
    }

    [Fact]
    public void ValidatePropertyEmission_GenericStructParentClosureSetter_IsSkipped()
    {
        // The property @_cdecl wrapper defers a concrete-typed property on a generic struct that is
        // not a Collection conformer, after which the accessor falls back to the direct P/Invoke. The
        // closure shape alone therefore cannot decide the exemption: an Optional-wrapped
        // cdecl-compatible closure that IS carried on a non-generic parent is still the trampoline's
        // problem here.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var property = OnGenericStructParent(CreateOptionalClosureProperty(closureSpec));

        var result = new MemberValidationPipeline(typeDatabase).ValidatePropertyEmission(property, null);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.UnsupportedClosure, result.Reason);
        Assert.Contains("TestModule.Point", result.Details!);
    }

    [Fact]
    public void ValidatePropertyEmission_InheritedGenericContextClassParentClosureSetter_IsSkipped()
    {
        // The generic-class exemption has a second bound: a nested class that inherits its generic
        // parameters from an enclosing generic type cannot be extended to carry the adapter, so the
        // property wrapper declines it even though the class arm would otherwise take it, and the
        // accessor lands on the direct P/Invoke.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var property = OnNestedGenericClassParent(CreateOptionalClosureProperty(closureSpec));

        var result = new MemberValidationPipeline(typeDatabase).ValidatePropertyEmission(property, null);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.UnsupportedClosure, result.Reason);
        Assert.Contains("TestModule.Point", result.Details!);
    }

    [Fact]
    public void ValidatePropertyEmission_GenericClassParentClosureSetter_IsNotSkipped()
    {
        // The control that keeps the generic-parent narrowing honest. The wrapper does NOT refuse
        // every generic parent: a concrete-typed instance property on a generic CLASS goes through
        // instance dispatch and is wrapped, so the same property that is refused on a generic struct
        // must keep binding here. Restating the wrapper's rule instead of consulting it is what
        // turned this into an over-skip.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var property = OnGenericClassParent(CreateOptionalClosureProperty(closureSpec));

        var result = new MemberValidationPipeline(typeDatabase).ValidatePropertyEmission(property, null);

        Assert.True(result.ShouldEmit);
    }

    [Fact]
    public void ValidatePropertyEmission_ModuleInternalParentClosureSetter_IsSkipped()
    {
        // A public property on a @usableFromInline internal parent slips every member-keyed guard,
        // but its @_cdecl wrapper body would have to name the internal parent from the separate
        // wrapper-compilation module, so the wrapper is refused and the accessors bind as direct
        // P/Invokes — putting the frozen struct back into a register the trampoline mis-reads.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var property = CreateOptionalClosureProperty(closureSpec);
        var internalParent = ((ClassDecl)property.ParentDecl!) with { IsModuleInternal = true };

        var result = new MemberValidationPipeline(typeDatabase)
            .ValidatePropertyEmission(property with { ParentDecl = internalParent }, null);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.UnsupportedClosure, result.Reason);
        Assert.Contains("TestModule.Point", result.Details!);
    }

    [Fact]
    public void ValidatePropertyEmission_SpiProtectedClosureSetter_IsSkipped()
    {
        // The @_spi sibling of the same rule: the wrapper module imports the binding target without
        // the @_spi group, so it cannot name the property either and the accessors fall back to the
        // direct trampoline.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var property = CreateOptionalClosureProperty(closureSpec);
        property.IsSpiProtected = true;

        var result = new MemberValidationPipeline(typeDatabase).ValidatePropertyEmission(property, null);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.UnsupportedClosure, result.Reason);
        Assert.Contains("TestModule.Point", result.Details!);
    }

    [Fact]
    public void ValidatePropertyEmission_CustomActorIsolatedClosureSetter_IsSkipped()
    {
        // Isolation is the fourth decl-visible wrapper refusal. A member isolated to a custom global
        // actor can only be entered through an async hop, which a synchronous @_cdecl adapter cannot
        // perform, so the wrapper declines and the accessors bind as direct P/Invokes — putting the
        // frozen struct back into a register the trampoline mis-reads.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var property = CreateOptionalClosureProperty(closureSpec);
        property.IsActorIsolated = true;

        var result = new MemberValidationPipeline(typeDatabase).ValidatePropertyEmission(property, null);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.UnsupportedClosure, result.Reason);
        Assert.Contains("TestModule.Point", result.Details!);
    }

    [Fact]
    public void ValidatePropertyEmission_MainActorIsolatedClosureSetter_IsNotSkipped()
    {
        // The control that keeps the isolation arm from over-skipping. @MainActor is the deliberate
        // exception in the wrapper's own isolation rule — it is exposed synchronously — so a
        // @MainActor closure setter still belongs to the wrapper lane and must keep binding.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var property = CreateOptionalClosureProperty(closureSpec);
        property.IsActorIsolated = true;
        property.IsMainActorIsolated = true;

        var result = new MemberValidationPipeline(typeDatabase).ValidatePropertyEmission(property, null);

        Assert.True(result.ShouldEmit);
    }

    [Fact]
    public void ValidatePropertyEmission_XCFrameworkOptionalClosureSetterWithCdeclShape_IsNotSkipped()
    {
        // The exemption's positive control. The property wrapper refuses a BARE closure setter but
        // accepts an Optional-wrapped one whose closure is cdecl-compatible, so in xcframework mode
        // this property is the wrapper lane's concern and the frozen struct never reaches a register.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var property = CreateOptionalClosureProperty(closureSpec);

        var result = new MemberValidationPipeline(typeDatabase).ValidatePropertyEmission(property, null);

        Assert.True(result.ShouldEmit);
    }

    [Fact]
    public void ValidatePropertyEmission_DirectModeOptionalClosureSetterWithCdeclShape_IsSkipped()
    {
        // The counterpart that pins the mode conditioning on the property path: with no companion
        // wrapper library there is no lane to hand the setter to, so the same property falls back to
        // the direct trampoline and the unmodelled shape is refused.
        var typeDatabase = CreateTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var property = CreateOptionalClosureProperty(closureSpec);

        var result = new MemberValidationPipeline(typeDatabase).ValidatePropertyEmission(property, null);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.UnsupportedClosure, result.Reason);
        Assert.Contains("TestModule.Point", result.Details!);
    }

    [Fact]
    public void ValidateSubscriptEmission_ClosureElementWithUnmodelledArg_IsSkipped()
    {
        // The subscript's ELEMENT type travels into Swift on the setter, so a closure-typed element
        // builds the same reverse trampoline an index parameter does.
        var typeDatabase = CreateXCFrameworkTypeDatabase();
        var closureSpec = new ClosureTypeSpec(
            new TupleTypeSpec(new TypeSpec[] { ArrayOfDouble(), new NamedTypeSpec("TestModule.Point") }),
            TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var subscriptDecl = CreateClosureElementSubscript(closureSpec);

        var result = new MemberValidationPipeline(typeDatabase).ValidateSubscriptEmission(subscriptDecl, null);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.UnsupportedClosure, result.Reason);
        Assert.Contains("TestModule.Point", result.Details!);
    }

    #endregion

    #region Fixtures

    private static NamedTypeSpec ArrayOfDouble() =>
        new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Double"));

    private static NamedTypeSpec Optional(TypeSpec inner) =>
        new NamedTypeSpec("Swift.Optional", inner);

    private static NamedTypeSpec ResultOverAnyError(TypeSpec success) =>
        new NamedTypeSpec("Swift.Result", success, new NamedTypeSpec("Swift.Error") { IsAny = true });

    /// <summary>
    /// A constructor taking one escaping closure. Constructor position is what keeps the closure on
    /// the direct trampoline in these tests: the method-closure bridge refuses initializers.
    /// </summary>
    private static MethodDecl CreateClosureConstructor(ClosureTypeSpec closureSpec)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Types = new List<TypeDecl>(),
            Protocols = new List<ProtocolDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Dependencies = new List<string>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = new ClassDecl
        {
            Name = "Host",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Host"),
            MangledName = "$s10TestModule4HostC",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Operators = new List<OperatorDecl>(),
            Types = new List<TypeDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                Name = "_return",
                PrivateName = "_return",
                SwiftTypeSpec = new NamedTypeSpec("TestModule.Host"),
                IsGeneric = false,
                IsInOut = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl
            },
            new ArgumentDecl
            {
                Name = "completion",
                PrivateName = "completion",
                SwiftTypeSpec = closureSpec,
                IsGeneric = false,
                IsInOut = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl
            }
        };

        return new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule4HostC10completionACySdGSaySdG_SSSgtcyc_tcfc",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
    }

    /// <summary>
    /// A closure-typed property on the same host class, with or without a setter. The property
    /// <c>@_cdecl</c> wrapper rejects a bare closure setter, so its accessors reach the direct
    /// trampoline the same way a plain method's closure parameter does.
    /// </summary>
    private static PropertyDecl CreateClosureProperty(ClosureTypeSpec closureSpec, bool withSetter)
    {
        var ctor = CreateClosureConstructor(closureSpec);

        var accessors = new List<AccessorDecl>
        {
            new GetAccessorDecl
            {
                Method = CreateAccessorMethod(
                    ctor, "get_handler", "$s10TestModule4HostC7handlerySaySdG_AA5PointVtcvg", closureSpec)
            }
        };
        if (withSetter)
        {
            accessors.Add(new SetAccessorDecl
            {
                Method = CreateAccessorMethod(
                    ctor, "set_handler", "$s10TestModule4HostC7handlerySaySdG_AA5PointVtcvs", closureSpec)
            });
        }

        return new PropertyDecl
        {
            Name = "handler",
            SwiftTypeSpec = closureSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = accessors,
            ParentDecl = ctor.ParentDecl,
            ModuleDecl = ctor.ModuleDecl
        };
    }

    /// <summary>
    /// A plain instance method carrying the same closure parameter — the method wrapper lane's twin
    /// of <see cref="CreateClosureConstructor"/>.
    /// </summary>
    private static MethodDecl CreateClosureMethod(ClosureTypeSpec closureSpec)
    {
        var ctor = CreateClosureConstructor(closureSpec);
        var csSignature = new List<ArgumentDecl>
        {
            ctor.CSSignature[0] with { SwiftTypeSpec = new NamedTypeSpec("()") },
            ctor.CSSignature[1]
        };

        return ctor with
        {
            Name = "deliver",
            MangledName = "$s10TestModule4HostC7deliver10completionyySaySdG_AA5PointVtc_tF",
            IsConstructor = false,
            CSSignature = csSignature
        };
    }

    /// <summary>
    /// Appends a metatype parameter — a shape neither wrapper lane can render in the C ABI, and one
    /// the closure's own type says nothing about.
    /// </summary>
    private static MethodDecl WithMetatypeParameter(MethodDecl method)
    {
        var csSignature = new List<ArgumentDecl>(method.CSSignature)
        {
            method.CSSignature[1] with
            {
                Name = "kind",
                PrivateName = "kind",
                SwiftTypeSpec = new NamedTypeSpec("TestModule.Point.Type")
            }
        };

        return method with { CSSignature = csSignature };
    }

    /// <summary>
    /// The same constructor re-parented onto a resilient struct and made failable — the one shape the
    /// constructor <c>@_cdecl</c> wrapper refuses outright.
    /// </summary>
    private static MethodDecl CreateFailableResilientStructClosureConstructor(ClosureTypeSpec closureSpec)
    {
        var ctor = CreateClosureConstructor(closureSpec);
        var structParent = new StructDecl
        {
            Name = "Box",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
            MangledName = "$s10TestModule3BoxV",
            IsFrozen = false,
            MetadataAccessor = "$s10TestModule3BoxVMa",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Operators = new List<OperatorDecl>(),
            Types = new List<TypeDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = ctor.ModuleDecl
        };

        var csSignature = new List<ArgumentDecl>
        {
            ctor.CSSignature[0] with { SwiftTypeSpec = new NamedTypeSpec("TestModule.Box") },
            ctor.CSSignature[1]
        };

        return ctor with { IsFailable = true, CSSignature = csSignature, ParentDecl = structParent };
    }

    /// <summary>
    /// A settable property whose declared type is <c>Optional&lt;closure&gt;</c> — the one closure
    /// property shape the property <c>@_cdecl</c> wrapper is willing to carry.
    /// </summary>
    private static PropertyDecl CreateOptionalClosureProperty(ClosureTypeSpec closureSpec)
    {
        var property = CreateClosureProperty(closureSpec, withSetter: true);
        return property with { SwiftTypeSpec = new NamedTypeSpec("Swift.Optional", closureSpec) };
    }

    /// <summary>
    /// Re-parents a property onto a generic version of the same host CLASS — the generic parent the
    /// property <c>@_cdecl</c> wrapper still carries, because a concrete-typed instance property
    /// reaches its storage through ordinary instance dispatch.
    /// </summary>
    private static PropertyDecl OnGenericClassParent(PropertyDecl property)
    {
        var genericParent = ((ClassDecl)property.ParentDecl!) with
        {
            GenericParameters = new List<GenericArgumentDecl> { OneTypeParameter() }
        };

        return property with { ParentDecl = genericParent };
    }

    /// <summary>
    /// Re-parents a property onto a generic class NESTED in a generic class that declares the same
    /// type parameter — the inherited-generic-context shape the property wrapper declines because the
    /// conformance extension it would need cannot name the outer type's unresolved parameter.
    /// </summary>
    private static PropertyDecl OnNestedGenericClassParent(PropertyDecl property)
    {
        var inner = (ClassDecl)OnGenericClassParent(property).ParentDecl!;
        var outer = inner with
        {
            Name = "Outer",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer"),
            MangledName = "$s10TestModule5OuterC"
        };

        return property with { ParentDecl = inner with { ParentDecl = outer } };
    }

    /// <summary>
    /// Re-parents a property onto a generic STRUCT that conforms to nothing — the generic parent the
    /// property <c>@_cdecl</c> wrapper defers, leaving the accessor on the direct P/Invoke.
    /// </summary>
    private static PropertyDecl OnGenericStructParent(PropertyDecl property)
    {
        var structParent = new StructDecl
        {
            Name = "Box",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
            MangledName = "$s10TestModule3BoxV",
            IsFrozen = false,
            MetadataAccessor = "$s10TestModule3BoxVMa",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Operators = new List<OperatorDecl>(),
            Types = new List<TypeDecl>(),
            GenericParameters = new List<GenericArgumentDecl> { OneTypeParameter() },
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = property.ModuleDecl
        };

        return property with { ParentDecl = structParent };
    }

    private static GenericArgumentDecl OneTypeParameter() =>
        new GenericArgumentDecl("τ_0_0", "T", new List<GenericParameterConformance>(),
            new List<GenericParameterConformance>());

    /// <summary>
    /// A read-write subscript whose ELEMENT type is the closure under test — the setter hands it to
    /// Swift exactly as an index parameter would.
    /// </summary>
    private static SubscriptDecl CreateClosureElementSubscript(ClosureTypeSpec closureSpec)
    {
        var subscriptDecl = CreateClosureIndexSubscript(closureSpec);
        var indexParam = subscriptDecl.IndexParameters[0] with
        {
            Name = "index",
            PrivateName = "index",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int")
        };

        return subscriptDecl with
        {
            ReturnTypeSpec = closureSpec,
            IndexParameters = new List<ArgumentDecl> { indexParam }
        };
    }

    /// <summary>
    /// A read-write subscript whose single index parameter is the closure under test.
    /// </summary>
    private static SubscriptDecl CreateClosureIndexSubscript(ClosureTypeSpec closureSpec)
    {
        var ctor = CreateClosureConstructor(closureSpec);
        var elementSpec = new NamedTypeSpec("Swift.Int");

        var accessors = new List<AccessorDecl>
        {
            new GetAccessorDecl
            {
                Method = CreateAccessorMethod(
                    ctor, "get_subscript", "$s10TestModule4HostCySiySaySdG_AA5PointVtccig", closureSpec)
            },
            new SetAccessorDecl
            {
                Method = CreateAccessorMethod(
                    ctor, "set_subscript", "$s10TestModule4HostCySiySaySdG_AA5PointVtccis", closureSpec)
            }
        };

        return new SubscriptDecl
        {
            Name = "subscript",
            ReturnTypeSpec = elementSpec,
            IndexParameters = new List<ArgumentDecl> { ctor.CSSignature[1] },
            IsStatic = false,
            Accessors = accessors,
            MangledName = "$s10TestModule4HostCySiySaySdG_AA5PointVtcci",
            ParentDecl = ctor.ParentDecl,
            ModuleDecl = ctor.ModuleDecl
        };
    }

    private static MethodDecl CreateAccessorMethod(
        MethodDecl ctor, string name, string mangledName, ClosureTypeSpec closureSpec)
    {
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                Name = "_return",
                PrivateName = "_return",
                SwiftTypeSpec = new NamedTypeSpec("Swift.Void"),
                IsGeneric = false,
                IsInOut = false,
                ParentDecl = null,
                ModuleDecl = ctor.ModuleDecl
            },
            ctor.CSSignature[1]
        };

        return new MethodDecl
        {
            Name = name,
            MangledName = mangledName,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = ctor.ParentDecl,
            ModuleDecl = ctor.ModuleDecl
        };
    }

    /// <summary>
    /// The same registry configured for xcframework mode, where a companion wrapper library exists
    /// and the <c>@_cdecl</c> / bridge lanes can actually claim a member.
    /// </summary>
    private static TypeDatabase CreateXCFrameworkTypeDatabase()
    {
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestBindings";
        return typeDatabase;
    }

    /// <summary>
    /// Swift primitives and containers plus one class, one frozen struct and one resilient struct —
    /// the four register schemas the classifier distinguishes.
    /// </summary>
    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
                MetadataAccessor = "$sSdMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "$ss5Int32VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "$sSaMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.ArraySlice"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArraySlice"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.ArraySlice"),
                MetadataAccessor = "$ss10ArraySliceVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Result"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftResult"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Result"),
                MetadataAccessor = "$ss6ResultOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Enum
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                MetadataAccessor = "$ss5ErrorPMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Loader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                MetadataAccessor = "$s10TestModule6LoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        // Frozen struct of two Doubles — the field-wise/FPR explosion the model refuses.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                MetadataAccessor = "$s10TestModule5PointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        // Resilient struct — no visible layout, so Swift passes it indirectly.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Settings"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Settings"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Settings"),
                MetadataAccessor = "$s10TestModule8SettingsVMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        // @frozen enum carrying payloads — loadable, so Swift explodes it into registers.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Outcome"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Outcome"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outcome"),
                MetadataAccessor = "$s10TestModule7OutcomeOMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        // Resilient payload enum — address-only, so the indirect model is the correct one.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Phase"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Phase"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Phase"),
                MetadataAccessor = "$s10TestModule5PhaseOMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Enum
            });
        // @frozen no-payload enum — fixed layout, so Swift passes it in a register, but what it puts
        // there is the declaration-order tag sized to the case count, not the raw value the P/Invoke
        // translation declares.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Mode"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Mode"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Mode"),
                MetadataAccessor = "$s10TestModule4ModeOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum
            });
        // Resilient no-payload enum — the case count can grow, so Swift passes its address while the
        // P/Invoke translation still declares an integer.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Stage"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Stage"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Stage"),
                MetadataAccessor = "$s10TestModule5StageOMa",
                Flags = TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    #endregion
}
