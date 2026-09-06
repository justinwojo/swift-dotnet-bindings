// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the direct-CallConvSwift Optional width classifier and the ABI floor built on it.
///
/// <para>The defect these pin: the emitter's preferred route for a wide <c>Optional&lt;T&gt;</c> is
/// a Swift wrapper with an out-buffer, but that route is conditional on the member being
/// wrapper-eligible. When it is not, the emitter falls back to a direct P/Invoke that declares the
/// Optional as a single <c>IntPtr</c> and then copies the type metadata's full size out of that
/// pointer-sized local. For a 16-byte <c>Optional&lt;String&gt;</c> that transfers 8 real bytes and
/// 8 bytes of adjacent stack memory — and because such an Optional carries no separate tag byte,
/// the bytes that were never transferred are exactly the ones deciding nil-ness.</para>
///
/// <para>The classifier is the soundness half of the fix, and its hardest requirement is not
/// catching the wide shapes but <b>leaving the narrow ones alone</b>: the routing predicate that
/// selects the wrapper calls several genuinely one-word Optionals "large", and refusing those would
/// replace working members with throws. Both directions are asserted here.</para>
/// </summary>
public class DirectOptionalAbiTests
{
    #region Classifier — shapes that do NOT fit the single direct slot

    [Fact]
    public void Classify_OptionalString_IsTwoIntegerWords()
    {
        // The reported defect's exact shape. String is two words and has spare bits, so the
        // Optional needs no tag byte and stays at exactly 16 bytes — twice the direct slot.
        var typeDb = CreateTypeDatabase();

        var result = DirectOptionalAbi.Classify(Optional(new NamedTypeSpec("Swift.String")), typeDb);

        Assert.Equal(DirectOptionalAbiWidth.TwoIntegerWords, result);
        Assert.True(DirectOptionalAbi.ExceedsDirectSlot(Optional(new NamedTypeSpec("Swift.String")), typeDb));
    }

    [Theory]
    [InlineData("Swift.Double")]
    [InlineData("Swift.Int")]
    [InlineData("Swift.Int64")]
    [InlineData("Swift.UInt64")]
    [InlineData("CoreGraphics.CGFloat")]
    public void Classify_OptionalWordSizedPayload_IsWordAndTagByte(string innerName)
    {
        // A payload that already fills a word has no spare bits, so the Optional appends a tag byte
        // and spills past the slot: nine bytes, arriving in x0 + w1.
        //
        // All of these share ONE carrier even though Double is floating-point and Int is not,
        // because Swift lowers an enum payload as opaque INTEGER storage. Optional<Double> travels
        // in x0, not d0 — the callee opens with `fmov d0, x0` to move the payload out of the
        // integer register. An earlier version of this classifier called these Unprovable on the
        // stated reasoning that "the payload's register class differs between them", which is the
        // misconception this test now pins the correction to.
        var typeDb = CreateTypeDatabase();

        var result = DirectOptionalAbi.Classify(Optional(new NamedTypeSpec(innerName)), typeDb);

        Assert.Equal(DirectOptionalAbiWidth.WordAndTagByte, result);
    }

    [Fact]
    public void Classify_OptionalUnknownStruct_IsUnprovable()
    {
        // A struct the database carries no layout knowledge of could be resilient (address-only
        // across its module boundary) or arbitrarily wide. Nothing here establishes it fits, so
        // the classifier refuses rather than assuming.
        var typeDb = CreateTypeDatabase();

        var result = DirectOptionalAbi.Classify(Optional(new NamedTypeSpec("Other.MysteryStruct")), typeDb);

        Assert.Equal(DirectOptionalAbiWidth.Unprovable, result);
    }

    [Fact]
    public void Classify_OptionalBareContainer_IsUnprovable()
    {
        // The single-pointer container proof is about a fully applied Array/Dictionary/Set. A
        // bare container name with no element type is not a shape whose layout has been
        // established, so it must not borrow the applied form's answer.
        var typeDb = CreateTypeDatabase();

        var result = DirectOptionalAbi.Classify(Optional(new NamedTypeSpec("Swift.Array")), typeDb);

        Assert.Equal(DirectOptionalAbiWidth.Unprovable, result);
    }

    [Fact]
    public void Classify_OptionalObjCBridgedValueType_IsUnprovable()
    {
        // The reference predicate this classifier consults answers a *bridging* question — does
        // this arrive as a nullable object pointer at a @_cdecl boundary — and so also accepts
        // Swift value types that bridge to an ObjC class (Foundation.URL, Date, IndexPath). There
        // is no bridging on the direct CallConvSwift path: such a payload keeps its native Swift
        // layout, which is wider than a pointer. Answering SingleWord here would be a licence to
        // truncate, so the classifier must decline to prove it.
        var typeDb = CreateTypeDatabase();

        var result = DirectOptionalAbi.Classify(Optional(new NamedTypeSpec("TestModule.BridgedValue")), typeDb);

        Assert.Equal(DirectOptionalAbiWidth.Unprovable, result);
    }

    #endregion

    #region Classifier — shapes that DO fit (the over-broad-gate canaries)

    [Theory]
    [InlineData("Swift.Array")]
    [InlineData("Swift.Dictionary")]
    [InlineData("Swift.Set")]
    public void Classify_OptionalSinglePointerContainer_IsSingleWord(string containerName)
    {
        // THE load-bearing negative control. These are classified "large" by the wrapper-routing
        // predicate, but each is physically one refcounted storage pointer using null as its extra
        // inhabitant, so the existing single-slot direct call is already correct for them. A gate
        // keyed on the routing predicate instead of on real width would tombstone working members
        // here — this test is what catches that.
        var typeDb = CreateTypeDatabase();
        var spec = Optional(Generic(containerName, new NamedTypeSpec("Swift.String")));

        Assert.Equal(DirectOptionalAbiWidth.SingleWord, DirectOptionalAbi.Classify(spec, typeDb));
        Assert.False(DirectOptionalAbi.ExceedsDirectSlot(spec, typeDb));
    }

    [Fact]
    public void Classify_OptionalErrorExistential_IsSingleWord()
    {
        // `any Error` is the one existential Swift represents as a single refcounted box rather
        // than a multi-word container: measured at 8 bytes, where an ordinary `(any P)?` is 40.
        // Every `throws`-shaped Optional-error return depends on this staying live, so it is the
        // load-bearing control against the existential arm refusing a pointer-sized shape.
        var typeDb = CreateTypeDatabase();

        var result = DirectOptionalAbi.Classify(Optional(new NamedTypeSpec("Swift.Error")), typeDb);

        Assert.Equal(DirectOptionalAbiWidth.SingleWord, result);
    }

    [Fact]
    public void Floor_OptionalObjCBridgedValueTypeReturn_Fires()
    {
        // The floor must reach this shape even though the large-Optional routing predicates
        // answer "not large" for it — they early-out on a *bridging* question, and there is no
        // bridging on the direct path. Foundation.URL? measures 16 bytes, so the single-slot
        // call reads one word of a struct and hands it to GetINativeObject as an object pointer,
        // releasing a value that was never a reference.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodReturning(
            "boxedUrl",
            Optional(new NamedTypeSpec("TestModule.BridgedValue")),
            parent,
            moduleDecl);

        Assert.True(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Classify_OptionalClassReference_IsSingleWord()
    {
        // One object pointer, nil as its null extra inhabitant — the case the existing direct
        // emission was designed around and must keep serving.
        var typeDb = CreateTypeDatabase();
        var spec = Optional(new NamedTypeSpec("TestModule.MyClass"));

        Assert.Equal(DirectOptionalAbiWidth.SingleWord, DirectOptionalAbi.Classify(spec, typeDb));
    }

    [Theory]
    [InlineData("Swift.Bool")]
    [InlineData("Swift.Int8")]
    [InlineData("Swift.Int16")]
    [InlineData("Swift.Int32")]
    [InlineData("Swift.UInt32")]
    [InlineData("Swift.Float")]
    public void Classify_OptionalSubWordPrimitive_IsSingleWord(string innerName)
    {
        // Payload plus its appended tag byte still fits inside one word.
        var typeDb = CreateTypeDatabase();

        var result = DirectOptionalAbi.Classify(Optional(new NamedTypeSpec(innerName)), typeDb);

        Assert.Equal(DirectOptionalAbiWidth.SingleWord, result);
    }

    [Fact]
    public void Classify_NonOptional_IsNotOptional()
    {
        var typeDb = CreateTypeDatabase();

        Assert.Equal(
            DirectOptionalAbiWidth.NotOptional,
            DirectOptionalAbi.Classify(new NamedTypeSpec("Swift.String"), typeDb));
        Assert.False(DirectOptionalAbi.ExceedsDirectSlot(new NamedTypeSpec("Swift.String"), typeDb));
    }

    #endregion

    #region ABI floor — return side

    [Fact]
    public void Floor_UnwrappedOptionalStringReturn_DoesNotFire_BecauseACarrierExists()
    {
        // The originally reported shape: no wrapper of any kind assigned, so the emitted call is
        // the direct one. It is still WIDER than the single slot — that has not changed — but the
        // floor no longer refuses it, because a two-word carrier now transports it intact.
        //
        // The two assertions are deliberately separate. Width and refusal used to be the same
        // question, and collapsing them again is how a future width added to the enum would
        // silently fall through into a truncated direct call instead of being refused.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodReturning("label", Optional(new NamedTypeSpec("Swift.String")), parent, moduleDecl);

        Assert.True(DirectOptionalAbi.ExceedsDirectSlot(
            Optional(new NamedTypeSpec("Swift.String")), typeDb));
        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalArrayReturn_DoesNotFire()
    {
        // Same wrapper-less direct path, but the value genuinely fits the slot. Must stay live —
        // this is the assertion that fails if the floor is widened to the routing predicate.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodReturning(
            "names",
            Optional(Generic("Swift.Array", new NamedTypeSpec("Swift.String"))),
            parent,
            moduleDecl);

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalStringReturnWithOptionalPointerWrapper_DoesNotFire()
    {
        // The out-buffer wrapper exists precisely to carry these through memory. When it is
        // assigned, width stops mattering and the member must keep its real body.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodReturning("label", Optional(new NamedTypeSpec("Swift.String")), parent, moduleDecl);
        method.HasOptionalPointerWrapper = true;
        method.UsesWrapperLibrary = true;

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalStringReturnWithCdeclWrapper_DoesNotFire()
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodReturning("label", Optional(new NamedTypeSpec("Swift.String")), parent, moduleDecl);
        method.UsesCdeclMethodWrapper = true;

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    #endregion

    #region ABI floor — parameter side

    [Fact]
    public void Floor_UnwrappedOptionalStringParam_DoesNotFire_BecauseACarrierExists()
    {
        // Swift reads a two-word Optional argument out of two integer registers, and the parameter
        // side is a distinct emission path from the return side — so it needs its own carrier and
        // its own assertion that the floor has stood down for it.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodTaking("width", Optional(new NamedTypeSpec("Swift.String")), parent, moduleDecl);

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalArrayParam_DoesNotFire()
    {
        // Parameter-side counterpart of the Array return control.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodTaking(
            "count",
            Optional(Generic("Swift.Array", new NamedTypeSpec("Swift.String"))),
            parent,
            moduleDecl);

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Theory]
    [InlineData("Swift.Array")]
    [InlineData("Swift.Set")]
    public void Floor_OptionalObjCBridgeableContainerParam_Fires(string containerName)
    {
        // The width is genuinely fine — one refcounted storage pointer, exactly what the slot
        // holds — so the truncation arm above cannot reach this. What goes wrong is the value: the
        // setter conversion for a container whose elements bridge builds an NSArray/NSSet and
        // passes its handle, because it asks what the payload bridges TO without asking whether
        // there is a boundary to bridge AT. A direct CallConvSwift accessor is Swift's own, so
        // there is none, and Swift reads a Foundation object where its native storage belongs.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var spec = Optional(Generic(containerName, new NamedTypeSpec("TestModule.BridgeableElement")));
        var env = new MethodEnvironment(MethodTaking("store", spec, parent, moduleDecl), typeDb);

        Assert.True(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(env));

        // And the width oracle keeps telling the truth about it, so the refusal is recorded as the
        // representation problem it is rather than as a truncation the consumer would go hunting for.
        Assert.Equal(DirectOptionalAbiWidth.SingleWord, DirectOptionalAbi.Classify(spec, typeDb));
        Assert.True(WrapperValidation.HasForeignObjectRenderedDirectDispatch(env));
    }

    [Fact]
    public void Floor_OptionalObjCBridgeableContainerReturn_Fires()
    {
        // Return side of the same shape, and the worse half: the getter reads Swift's own array
        // storage back through ArrayFromHandleFunc as if it were an NSArray, taking ownership of
        // an object that never existed.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodReturning(
            "fetch",
            Optional(Generic("Swift.Array", new NamedTypeSpec("TestModule.BridgeableElement"))),
            parent,
            moduleDecl);

        Assert.True(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Marker_OptionalObjCBridgeableContainerMethod_ExplainsRepresentationNotWidth()
    {
        // The declaration marker and the throw body are chosen by two different functions, so they
        // can disagree about WHY a member is refused even when they agree THAT it is. The width
        // predicate is a superset of this one, so a marker path that asks only the width question
        // stamps a member whose slot is exactly the right size with a sentence about reading past
        // the first word — sending a consumer to look for a truncation that isn't there.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var env = new MethodEnvironment(
            MethodTaking(
                "store",
                Optional(Generic("Swift.Array", new NamedTypeSpec("TestModule.BridgeableElement"))),
                parent,
                moduleDecl),
            typeDb);

        var issue = WrapperValidation.GetNonBlittableCallConvSwiftIssue(env);

        Assert.NotNull(issue);
        Assert.Equal(WrapperValidation.UncallableAbiDiagnosticId, issue!.Value.DiagnosticId);
        Assert.Contains("bridge to Objective-C", issue.Value.Message);
        Assert.DoesNotContain("wider than the single machine word", issue.Value.Message);
    }

    [Fact]
    public void Marker_TrulyWideOptionalMethod_StillExplainsWidth()
    {
        // The negative half of the pair: adding the representation arm ahead of the width arm must
        // not capture the shapes the width arm owns. Optional<OpaquePointer> has no established
        // lowering to build a carrier from, so it stays refused for width and must keep the
        // truncation sentence. Optional<String> would be the wrong control here — it is carried
        // now, so it is not refused at all and has no marker to compare.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var env = new MethodEnvironment(
            MethodTaking("store", Optional(new NamedTypeSpec("Swift.OpaquePointer")), parent, moduleDecl),
            typeDb);

        var issue = WrapperValidation.GetNonBlittableCallConvSwiftIssue(env);

        Assert.NotNull(issue);
        Assert.Equal(WrapperValidation.UncallableAbiDiagnosticId, issue!.Value.DiagnosticId);
        Assert.Contains("wider than the single machine word", issue.Value.Message);
    }

    [Fact]
    public void Tombstone_ForeignObjectRenderedMember_IsRecognisedByTheSharedOracle()
    {
        // Sites that must agree a member's body is a throw without caring which arm decided it —
        // chiefly the failable-factory path, which stamps the declaration separately — read
        // IsAbiFloorTombstoned. An arm missing from it emits an unmarked tombstone.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var env = new MethodEnvironment(
            MethodTaking(
                "store",
                Optional(Generic("Swift.Array", new NamedTypeSpec("TestModule.BridgeableElement"))),
                parent,
                moduleDecl),
            typeDb);

        Assert.True(WrapperValidation.IsAbiFloorTombstoned(env));
    }

    [Fact]
    public void ForeignObjectQuestion_TrulyWideOptional_AnswersNo()
    {
        // The two tombstone messages are chosen by asking these questions in order, so a wide
        // Optional answering yes here would explain a String? truncation as a bridging problem.
        // Optional<String> is refused for width and must stay owned by the width arm.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var env = new MethodEnvironment(
            MethodTaking("store", Optional(new NamedTypeSpec("Swift.String")), parent, moduleDecl), typeDb);

        Assert.False(WrapperValidation.HasForeignObjectRenderedDirectDispatch(env));
    }

    [Fact]
    public void Floor_OptionalNativeElementContainerParam_DoesNotFire()
    {
        // The negative control that keeps the new refusal narrow. A container is refused for the
        // Foundation object its elements bridge to, not for being a container — [String]? renders
        // as a native SwiftArray and must keep binding.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var env = new MethodEnvironment(
            MethodTaking(
                "store",
                Optional(Generic("Swift.Array", new NamedTypeSpec("Swift.String"))),
                parent,
                moduleDecl),
            typeDb);

        Assert.False(WrapperValidation.HasForeignObjectRenderedDirectDispatch(env));
        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(env));
    }

    [Fact]
    public void Floor_OptionalObjCBridgeableContainerWithCdeclWrapper_DoesNotFire()
    {
        // A @_cdecl wrapper is the boundary the rendering assumes: it takes the collection as a
        // nullable object pointer and unwraps it back to the native container on entry. The
        // refusal is about the absence of that wrapper, so its presence must clear it — otherwise
        // every ObjC-bridgeable container property in the corpus tombstones.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodTaking(
            "store",
            Optional(Generic("Swift.Array", new NamedTypeSpec("TestModule.BridgeableElement"))),
            parent,
            moduleDecl);
        method.UsesCdeclMethodWrapper = true;

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Theory]
    [InlineData("Swift.Array")]
    [InlineData("Swift.Set")]
    public void Floor_BareObjCBridgeableContainerParam_Fires(string containerName)
    {
        // The optionality of the container is not what the refusal is about. A bare [URL] is the
        // same one-word native storage, rendered through the same conversion — an NSArray handle
        // where Swift's own array belongs — and it reaches the direct arm through the most
        // ordinary shapes there are: a plain method parameter, a subscript setter's new value.
        // A floor that fires only on the optional spelling leaves those making the live wrong call.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var env = new MethodEnvironment(
            MethodTaking(
                "store",
                Generic(containerName, new NamedTypeSpec("TestModule.BridgeableElement")),
                parent,
                moduleDecl),
            typeDb);

        Assert.True(WrapperValidation.HasForeignObjectRenderedDirectDispatch(env));
        Assert.True(WrapperValidation.IsAbiFloorTombstoned(env));
    }

    [Fact]
    public void Floor_BareObjCBridgeableDictionaryParam_Fires()
    {
        // Dictionary has two type arguments and bridges on its VALUE element; it must be asked
        // the same question as the single-argument containers rather than falling through the
        // walk as "not a container".
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var dictionary = new NamedTypeSpec("Swift.Dictionary");
        dictionary.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictionary.GenericParameters.Add(new NamedTypeSpec("TestModule.BridgeableElement"));
        var env = new MethodEnvironment(MethodTaking("store", dictionary, parent, moduleDecl), typeDb);

        Assert.True(WrapperValidation.HasForeignObjectRenderedDirectDispatch(env));
    }

    [Fact]
    public void Floor_BareObjCBridgeableContainerReturn_Fires()
    {
        // Return side of the bare shape — a subscript getter over [URL] on the direct arm. The
        // getter reads Swift's own array storage back through the NSArray conversion and takes
        // ownership of an object that never existed, exactly like its optional sibling.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var env = new MethodEnvironment(
            MethodReturning(
                "fetch",
                Generic("Swift.Array", new NamedTypeSpec("TestModule.BridgeableElement")),
                parent,
                moduleDecl),
            typeDb);

        Assert.True(WrapperValidation.HasForeignObjectRenderedDirectDispatch(env));
        Assert.True(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(env));
    }

    [Fact]
    public void Floor_BareObjCBridgeableContainerAccessor_TombstonesWithoutMarker()
    {
        // A subscript setter is a synthesized accessor. The floor still replaces its body with a
        // throw, but the declaration marker must stay off the PRIVATE accessor: the public
        // indexer's `set => Subscript_Set(...)` would otherwise call an error-severity member and
        // the generated binding would stop compiling. The public indexer therefore carries no
        // marker at all — the same deferral every accessor-side refusal already observes.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var setter = MethodTaking(
            "subscript_Set",
            Generic("Swift.Array", new NamedTypeSpec("TestModule.BridgeableElement")),
            parent,
            moduleDecl);
        setter.IsAccessor = true;
        var env = new MethodEnvironment(setter, typeDb);

        Assert.True(WrapperValidation.IsAbiFloorTombstoned(env));
        Assert.Null(WrapperValidation.GetNonBlittableCallConvSwiftIssue(env));
    }

    [Fact]
    public void Marker_BareObjCBridgeableContainerMethod_ExplainsRepresentationNotWidth()
    {
        // The bare container's slot is exactly one word wide, so the marker must blame the
        // rendering, not a truncation the consumer would then search for in vain.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var env = new MethodEnvironment(
            MethodTaking(
                "store",
                Generic("Swift.Array", new NamedTypeSpec("TestModule.BridgeableElement")),
                parent,
                moduleDecl),
            typeDb);

        var issue = WrapperValidation.GetNonBlittableCallConvSwiftIssue(env);

        Assert.NotNull(issue);
        Assert.Equal(WrapperValidation.UncallableAbiDiagnosticId, issue!.Value.DiagnosticId);
        Assert.Contains("bridge to Objective-C", issue.Value.Message);
        Assert.DoesNotContain("wider than the single machine word", issue.Value.Message);
    }

    [Fact]
    public void Floor_BareNativeElementContainerParam_DoesNotFire()
    {
        // The negative control for the bare arm: [String] renders as a native SwiftArray, crosses
        // as Swift's own storage, and must keep binding on the direct path. A floor keyed on
        // "is a container" instead of "bridges to a Foundation object" would take it too.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var env = new MethodEnvironment(
            MethodTaking("store", Generic("Swift.Array", new NamedTypeSpec("Swift.String")), parent, moduleDecl),
            typeDb);

        Assert.False(WrapperValidation.HasForeignObjectRenderedDirectDispatch(env));
        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(env));
    }

    [Fact]
    public void Floor_BareObjCBridgeableContainerWithCdeclWrapper_DoesNotFire()
    {
        // The bare shape is the common one in real libraries — `func load(_ urls: [URL])` — and
        // nearly all of them have a @_cdecl wrapper, which is the boundary the NSArray rendering
        // is correct at. The wrapper's presence must clear the refusal, or every such member in
        // the corpus tombstones.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodTaking(
            "store",
            Generic("Swift.Array", new NamedTypeSpec("TestModule.BridgeableElement")),
            parent,
            moduleDecl);
        method.UsesCdeclMethodWrapper = true;

        Assert.False(WrapperValidation.HasForeignObjectRenderedDirectDispatch(
            new MethodEnvironment(method, typeDb)));
        Assert.False(WrapperValidation.IsAbiFloorTombstoned(new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalClosureParam_DoesNotFire()
    {
        // Function-valued Optionals are outside this floor. Width alone would not decide them:
        // an Optional @convention(c) function is one word (8 bytes), a Swift closure's is two
        // (16), and the convention is not decidable from the spec alone — closures parsed from
        // ABI JSON carry no convention attribute. Firing here would tombstone working
        // @convention(c) members over a missing attribute, and Optional closures have their own
        // marshalling path that never reads the value out of a single slot.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty);
        var method = MethodTaking("observe", Optional(closure), parent, moduleDecl);

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalClosureReturn_DoesNotFire()
    {
        // The return-side twin of Floor_OptionalClosureParam_DoesNotFire. The exclusion is applied
        // on both arms, and Classify answers Unprovable for a closure payload (the inner spec is
        // not a NamedTypeSpec), so without the return-arm guard every Optional-closure return on a
        // wrapper-ineligible parent would be tombstoned. One arm being right does not imply the
        // other, so each is pinned separately.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty);
        var method = MethodReturning("makeHandler", Optional(closure), parent, moduleDecl);

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalErrorExistentialReturn_DoesNotFire()
    {
        // Classify_OptionalErrorExistential_IsSingleWord pins the classifier; this pins the floor
        // that consumes it. `any Error` is the one existential Swift represents as a single
        // refcounted box (measured 8 bytes, against 40 for an ordinary `(any P)?`), so it must
        // stay live even though the general existential arm right after it refuses. A future
        // "existentials always exceed the slot" shortcut in the floor would pass the classifier
        // test and fail this one.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodReturning("failure", Optional(new NamedTypeSpec("Swift.Error")), parent, moduleDecl);

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalGenericPayloadParam_DoesNotFire()
    {
        // A generic payload has no static size at the call site, so Swift takes the argument
        // indirectly and the emitter passes the buffer address rather than a value word. A pointer
        // carries the whole value however wide it is, so there is nothing to truncate. Confirmed
        // on the Simulator: a `T?` argument answers correctly for both nil and non-nil, so firing
        // here would destroy working surface.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodTaking("accept", Optional(new NamedTypeSpec("T")), parent, moduleDecl);

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalPointerParam_Fires()
    {
        // The counter-example to "one word means it fits". OpaquePointer? measures 8 bytes — the
        // same as the [String]? argument that round-trips correctly — yet calling it with the
        // parameter arm lifted SIGSEGVs the simulator on the first call. Width is necessary but
        // not sufficient for the direct argument slot, so nullable pointers must stay Unprovable.
        // This test is the guard against "re-classify them SingleWord, the measurement says 8".
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodTaking("accept", Optional(new NamedTypeSpec("Swift.OpaquePointer")), parent, moduleDecl);

        Assert.True(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalProtocolExistentialParam_Fires()
    {
        // The wrapper route selects on IsLargeOptionalParam OR IsLargeOptionalProtocolParam,
        // because the first deliberately returns false for protocol existentials and hands them
        // to the second. A floor consulting only the first would let every `(any P)?` parameter
        // on a wrapper-ineligible member walk past into the truncating call — and an existential
        // container is five words wide, so one slot is nowhere near enough.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodTaking(
            "accept",
            Optional(new NamedTypeSpec("TestModule.MyProtocol")),
            parent,
            moduleDecl);

        Assert.True(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    #endregion

    #region ABI floor — accessors are in scope

    [Fact]
    public void Floor_AccessorWithOptionalStringReturn_Fires()
    {
// The sibling internal-visibility floor excludes accessors, because the advisory marker it
        // drives is never rendered on that path. That exclusion must NOT be inherited here: a
        // `public var url: URL?` getter is unprovable exactly like the equivalent method, and a
        // property is the most ordinary shape a Swift API has. Inheriting the exclusion would leave
        // the defect reachable through properties while the method form was covered.
        //
        // Uses a shape with no carrier on purpose. A carried shape would answer false here for a
        // reason that has nothing to do with accessors, so it could not detect the exclusion
        // leaking back in.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodReturning("get_url", Optional(new NamedTypeSpec("TestModule.BridgedValue")), parent, moduleDecl);
        method.IsAccessor = true;

        Assert.True(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    #endregion

    #region Carrier selection

    [Fact]
    public void Carrier_OptionalString_IsTheTwoWordCarrier()
    {
        var typeDb = CreateTypeDatabase();

        Assert.Equal(
            "global::Swift.Runtime.SwiftOptionalCarrier16",
            DirectOptionalAbi.TryGetCarrierTypeName(Optional(new NamedTypeSpec("Swift.String")), typeDb));
    }

    [Theory]
    [InlineData("Swift.Double")]
    [InlineData("Swift.Int")]
    public void Carrier_OptionalWordSizedPayload_IsTheNineByteCarrier(string innerName)
    {
        // Double and Int select the SAME carrier, and that carrier's fields are integer-typed.
        // This is the assertion a future "Double needs a floating-point carrier" change fails on:
        // Swift lowers the payload as opaque integer storage, so the value arrives in x0 whatever
        // the payload's own type is.
        var typeDb = CreateTypeDatabase();

        Assert.Equal(
            "global::Swift.Runtime.SwiftOptionalCarrier9",
            DirectOptionalAbi.TryGetCarrierTypeName(Optional(new NamedTypeSpec(innerName)), typeDb));
    }

    [Fact]
    public void Carrier_SingleWordOptional_IsNull()
    {
        // A one-word Optional already fits the slot the direct path gives it, so it takes no
        // carrier and its existing emission must be left exactly as it was.
        var typeDb = CreateTypeDatabase();
        var spec = Optional(Generic("Swift.Array", new NamedTypeSpec("Swift.String")));

        Assert.Null(DirectOptionalAbi.TryGetCarrierTypeName(spec, typeDb));
    }

    [Theory]
    [InlineData("TestModule.BridgedValue")]
    [InlineData("Swift.OpaquePointer")]
    [InlineData("TestModule.MyProtocol")]
    public void Carrier_UnprovableOptional_IsNullAndHasNoSoundCallPath(string innerName)
    {
        // No carrier can be built for a lowering that has not been established, so these keep
        // failing closed. The pairing is the point: "no carrier" and "refused" have to stay the
        // same answer, or a shape with no carrier would fall through into a truncated direct call.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var spec = Optional(new NamedTypeSpec(innerName));
        var method = MethodTaking("accept", spec, parent, moduleDecl);

        Assert.Null(DirectOptionalAbi.TryGetCarrierTypeName(spec, typeDb));
        Assert.True(DirectOptionalAbi.HasNoSoundDirectCallPath(method, spec, typeDb));
    }

    #endregion

    #region Carrier oracle — members that carry their values some other way

    [Fact]
    public void DirectCarrier_MemberWithSwiftSideWrapper_TakesNoCarrier()
    {
        // A member whose values already move through memory on the Swift side must keep its
        // existing slot types. Handing it a carrier as well would change a signature that was
        // already correct, and the two sides would disagree about the same parameter.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var spec = Optional(new NamedTypeSpec("Swift.String"));

        foreach (var configure in new Action<MethodDecl>[]
        {
            m => m.UsesCdeclMethodWrapper = true,
            m => m.WrapperStrategy = WrapperStrategy.NativeThunk,
            m => m.UsesFreeFunctionWrapper = true,
            m => m.UsesWrapperLibrary = true,
            m => m.HasOptionalPointerWrapper = true,
            m => m.IsAsync = true,
        })
        {
            var method = MethodTaking("width", spec, parent, moduleDecl);
            configure(method);

            Assert.Null(DirectOptionalAbi.TryGetDirectCarrier(method, spec, typeDb));
        }
    }

    [Fact]
    public void DirectCarrier_UnwrappedMember_TakesTheCarrier()
    {
        // The control for the test above: with none of those flags set, the member is on the
        // direct path and does take the carrier. Without this, that test would still pass if
        // TryGetDirectCarrier returned null unconditionally.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var spec = Optional(new NamedTypeSpec("Swift.String"));
        var method = MethodTaking("width", spec, parent, moduleDecl);

        Assert.Equal(
            "global::Swift.Runtime.SwiftOptionalCarrier16",
            DirectOptionalAbi.TryGetDirectCarrier(method, spec, typeDb));
    }

    [Fact]
    public void DirectCarrier_InOutParam_TakesNoCarrier()
    {
        // A carrier transports the value; `inout` needs the address of the caller's storage.
        // Handing Swift a register pair holding a copy means its write-back lands nowhere and
        // its read of that "address" is really payload data. The same member takes the carrier
        // by value (asserted above), so what is being pinned here is the inout axis alone.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var spec = Optional(new NamedTypeSpec("Swift.String"));
        var method = MethodTaking("swap", spec, parent, moduleDecl);

        Assert.Null(DirectOptionalAbi.TryGetDirectCarrier(method, spec, typeDb, isInOut: true));
    }

    [Fact]
    public void Floor_InOutWideOptionalParam_Fires()
    {
        // Withdrawing the carrier has to land the member back on the refusal path rather than
        // merely change its slot type: with no carrier and a width past the direct slot, there is
        // no sound lowering left, and emitting one anyway is the crash the floor exists to stop.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var spec = Optional(new NamedTypeSpec("Swift.String"));
        var method = MethodTaking("swap", spec, parent, moduleDecl);

        Assert.True(DirectOptionalAbi.HasNoSoundDirectCallPath(method, spec, typeDb, isInOut: true));
        Assert.False(DirectOptionalAbi.HasNoSoundDirectCallPath(method, spec, typeDb));
    }

    [Fact]
    public void Blittability_InOutWideOptionalParam_StaysNonBlittable()
    {
        // The blittability carve-out is keyed on the carrier existing. Once inout withdraws it,
        // the carve-out must withdraw too — otherwise the two floors disagree and the member is
        // declared callable by one while the other has already refused it.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var spec = Optional(new NamedTypeSpec("Swift.String"));
        var method = MethodTaking("swap", spec, parent, moduleDecl);

        Assert.True(WrapperValidation.IsParamPInvokeNonBlittable(
            spec, new MethodEnvironment(method, typeDb), isInOut: true));
    }

    [Fact]
    public void Floor_InOutSingleWordOptionalParam_DoesNotFire()
    {
        // This floor answers the WIDTH question only, and a one-word `[Element]?` never exceeded the
        // direct slot at any ownership. Its inout form is refused one layer over, by the blittability
        // predictor — see Blittability_InOutSingleWordOptionalParam_StaysNonBlittable. Keeping the
        // two separate is what lets the width floor stay a statement about bytes.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var spec = Optional(Generic("Swift.Array", new NamedTypeSpec("Swift.String")));
        var method = MethodTaking("count", spec, parent, moduleDecl);

        Assert.False(DirectOptionalAbi.HasNoSoundDirectCallPath(method, spec, typeDb, isInOut: true));
    }

    #endregion

    #region Blittability predictor agrees with the emitted slot

    [Fact]
    public void Blittability_CarriedOptionalParam_IsBlittable()
    {
        // The predictor and the P/Invoke emitter have to reach the same verdict about the same
        // parameter. The emitter gives a carried Optional a plain struct of integer fields; a
        // predictor still answering "Optional argument, therefore SafeHandle-marshalled" makes the
        // member "uncallable direct dispatch" on an internal parent and replaces its body with a
        // throw — a member whose P/Invoke was by then perfectly well-formed.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var spec = Optional(new NamedTypeSpec("Swift.String"));
        var method = MethodTaking("width", spec, parent, moduleDecl);

        Assert.False(WrapperValidation.IsParamPInvokeNonBlittable(
            spec, new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Blittability_SingleWordOptionalParam_IsBlittable()
    {
        // The regression that this predicate actually shipped. A one-word `[Element]?` argument is
        // passed as its own value in an IntPtr — blittable — but the generic-container rule
        // answered for it on the strength of its outward shape and tombstoned it.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var spec = Optional(Generic("Swift.Array", new NamedTypeSpec("Swift.String")));
        var method = MethodTaking("count", spec, parent, moduleDecl);

        Assert.False(WrapperValidation.IsParamPInvokeNonBlittable(
            spec, new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Blittability_InOutSingleWordOptionalParam_StaysNonBlittable()
    {
        // Width is the wrong axis for an inout argument, and this is the case that proves it. A
        // one-word `[Element]?` fits the direct slot perfectly by value — which is exactly why the
        // carve-out above declares it blittable — but inout does not want the value there. Swift
        // wants the address of the caller's variable so it can write back through it, and the direct
        // path has no `ref` slot to offer. Answering "blittable" on width alone kept the member
        // callable and shipped a call that handed Swift the array's own storage pointer to overwrite.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var spec = Optional(Generic("Swift.Array", new NamedTypeSpec("Swift.String")));
        var method = MethodTaking("swap", spec, parent, moduleDecl);

        Assert.True(WrapperValidation.IsParamPInvokeNonBlittable(
            spec, new MethodEnvironment(method, typeDb), isInOut: true));
        Assert.False(WrapperValidation.IsParamPInvokeNonBlittable(
            spec, new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Blittability_UnprovableOptionalParam_StaysNonBlittable()
    {
        // The carve-out covers only the widths that have been established. An Optional the
        // classifier cannot prove keeps its previous verdict, so nothing was widened past the
        // shapes actually measured.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var spec = Optional(new NamedTypeSpec("TestModule.BridgedValue"));
        var method = MethodTaking("accept", spec, parent, moduleDecl);

        Assert.True(WrapperValidation.IsParamPInvokeNonBlittable(
            spec, new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Blittability_NonOptionalContainerParam_StaysNonBlittable()
    {
        // A bare (non-Optional) generic container is outside the carve-out entirely — the
        // classifier answers NotOptional for it — so the container rule still decides it. This is
        // what pins the carve-out to Optionals rather than to generic containers at large.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var spec = Generic("Swift.Array", new NamedTypeSpec("Swift.String"));
        var method = MethodTaking("accept", spec, parent, moduleDecl);

        Assert.True(WrapperValidation.IsParamPInvokeNonBlittable(
            spec, new MethodEnvironment(method, typeDb)));
    }

    #endregion

    #region Helpers

    private static NamedTypeSpec Optional(TypeSpec inner)
    {
        var spec = new NamedTypeSpec("Swift.Optional");
        spec.GenericParameters.Add(inner);
        return spec;
    }

    private static NamedTypeSpec Generic(string name, TypeSpec arg)
    {
        var spec = new NamedTypeSpec(name);
        spec.GenericParameters.Add(arg);
        return spec;
    }

    private static TypeDatabase CreateTypeDatabase() => CreateEnvironment().typeDb;

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateEnvironment()
    {
        var typeDb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterStruct(swiftModule, "Swift.String", "Swift", "SwiftString", inlineSize: 16);
        RegisterStruct(swiftModule, "Swift.Double", "System", "Double", inlineSize: 8);
        RegisterStruct(swiftModule, "Swift.Int", "System", "IntPtr", inlineSize: 8);
        RegisterStruct(swiftModule, "Swift.Int32", "System", "Int32", inlineSize: 4);
        RegisterStruct(swiftModule, "Swift.Bool", "System", "Boolean", inlineSize: 1);
        typeDb.AddModuleDatabase(swiftModule);

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IMyProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyProtocol"),
                MetadataAccessor = "$s10TestModule10MyProtocolMp",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });

        // A Swift value type that bridges to an ObjC class, in the shape of Foundation.URL:
        // a frozen struct carrying the ObjCBridged flag.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.BridgedValue"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "BridgedValue"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BridgedValue"),
                MetadataAccessor = "$s10TestModule12BridgedValueVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.ObjCBridged,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });

        // The element type that makes a container render as a Foundation collection. Distinct flag
        // from BridgedValue above: ObjCBridged is what the *width* exclusion keys on, ObjCBridgeable
        // is what the container conversion keys on, and the two questions have different answers.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.BridgeableElement"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "BridgeableElement"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BridgeableElement"),
                MetadataAccessor = "$s10TestModule17BridgeableElementVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.ObjCBridgeable,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
        typeDb.AddModuleDatabase(testModule);

        return (moduleDecl, typeDb);
    }

    private static void RegisterStruct(
        ModuleTypeDatabase db, string qualifiedName, string ns, string csName, int inlineSize)
    {
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName);
        db.RegisterType(
            swiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(ns, csName),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = $"$s{swiftTypeName.Name}Ma",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = inlineSize
            });
    }

    private static ClassDecl CreateClass(string name, ModuleDecl moduleDecl)
    {
        var decl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            IsFinal = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static ArgumentDecl Arg(TypeSpec spec, string name, ModuleDecl moduleDecl) =>
        new ArgumentDecl
        {
            SwiftTypeSpec = spec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

    private static MethodDecl MethodReturning(
        string name, TypeSpec returnType, TypeDecl parent, ModuleDecl moduleDecl) =>
        BuildMethod(name, parent, moduleDecl, Arg(returnType, "", moduleDecl));

    private static MethodDecl MethodTaking(
        string name, TypeSpec paramType, TypeDecl parent, ModuleDecl moduleDecl) =>
        BuildMethod(
            name,
            parent,
            moduleDecl,
            Arg(new NamedTypeSpec("Swift.Int32"), "", moduleDecl),
            Arg(paramType, "value", moduleDecl));

    private static MethodDecl BuildMethod(
        string name, TypeDecl parent, ModuleDecl moduleDecl, params ArgumentDecl[] signature) =>
        new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(signature),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

    #endregion
}
