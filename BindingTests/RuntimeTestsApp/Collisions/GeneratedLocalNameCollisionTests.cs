// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Almost every member here has a parameter spelled exactly like a local the marshalling code mints
/// into the same emitted scope — either a scratch local derived from a SIBLING parameter's name plus
/// a suffix, or one of the fixed spellings the wrapper/factory bodies use. The exception is the
/// reverse-dispatch section, which is a negative control: that lane re-declares requirement
/// parameters positionally, so the spelling never shares a scope with anything and nothing moves.
///
/// <para>The compile gate is half the proof: if the generated local still carried the colliding
/// spelling, the emitted C# would not compile and these call sites would not exist. What a run adds
/// is the other half — that the collision was resolved by moving the GENERATED name aside rather
/// than by rerouting the user's value. Each assertion folds both the driving parameter and the
/// colliding sibling into the answer, so a member that quietly passed the scratch local where the
/// user's argument belonged (or vice versa) produces the wrong number instead of passing.</para>
/// </summary>
public class GeneratedLocalNameCollisionTests : TestBase
{
    public GeneratedLocalNameCollisionTests(TestResults results) : base(results) { }

    // ---------------------------------------------------------------------------------------
    //  Locals derived from a sibling parameter's name
    // ---------------------------------------------------------------------------------------

    public void TestArrayOwnerKeepsBridgedContainerAndSibling()
    {
        using var collider = new DerivedLocalNameCollider();
        var items = new[]
        {
            new Foundation.NSUrl("https://example.com/a"),
            new Foundation.NSUrl("https://example.com/b"),
            new Foundation.NSUrl("https://example.com/c"),
        };

        // 3 * 100 + 7 — the sibling named after the NSArray owner local must not displace the array.
        AssertEqual(307, collider.ArrayOwner(items, 7), "bridged array and its NSArray-named sibling stay distinct");
    }

    public void TestArrayOwnerAcceptsNullContainer()
    {
        using var collider = new DerivedLocalNameCollider();
        AssertEqual(9, collider.ArrayOwner(null, 9), "null optional array still reaches the sibling parameter");
    }

    public void TestArrayBufferKeepsNativeArrayAndSibling()
    {
        using var collider = new DerivedLocalNameCollider();
        // (1+2+3) * 100 + 4
        AssertEqual(604, collider.ArrayBuffer(new[] { 1, 2, 3 }, 4), "native array and its Buffer-named sibling stay distinct");
    }

    public void TestArrayContainerKeepsNativeArrayAndSibling()
    {
        using var collider = new DerivedLocalNameCollider();
        AssertEqual(1005, collider.ArrayContainer(new[] { 4, 6 }, 5), "native array and its Swift-named sibling stay distinct");
    }

    public void TestArrayDisposableKeepsNativeArrayAndSibling()
    {
        using var collider = new DerivedLocalNameCollider();
        AssertEqual(1102, collider.ArrayDisposable(new[] { 5, 6 }, 2), "native array and its Disposable-named sibling stay distinct");
    }

    public void TestArrayConvertedKeepsElementConversionAndSibling()
    {
        using var collider = new DerivedLocalNameCollider();
        AssertEqual(208, collider.ArrayConverted(new[] { "a", "b" }, 8), "converted array and its Converted-named sibling stay distinct");
    }

    public void TestStringSwiftKeepsStringAndSibling()
    {
        using var collider = new DerivedLocalNameCollider();
        // "abcd".count == 4 → 4 * 100 + 3
        AssertEqual(403, collider.StringSwift("abcd", 3), "string and its Swift-named sibling stay distinct");
    }

    public void TestDictOwnerKeepsBridgedDictionaryAndSibling()
    {
        using var collider = new DerivedLocalNameCollider();
        var map = new Dictionary<string, Foundation.NSUrl>
        {
            ["a"] = new Foundation.NSUrl("https://example.com/a"),
            ["b"] = new Foundation.NSUrl("https://example.com/b"),
        };
        AssertEqual(206, collider.DictOwner(map, 6), "bridged dictionary and its NSDict-named sibling stay distinct");
    }

    public void TestDictPairsKeepsBridgedDictionaryAndSibling()
    {
        using var collider = new DerivedLocalNameCollider();
        var entries = new Dictionary<string, Foundation.NSUrl>
        {
            ["x"] = new Foundation.NSUrl("https://example.com/x"),
        };
        AssertEqual(101, collider.DictPairs(entries, 1), "bridged dictionary and its Pairs-named sibling stay distinct");
    }

    public void TestSetOwnerKeepsBridgedSetAndSibling()
    {
        using var collider = new DerivedLocalNameCollider();
        var unique = new HashSet<Foundation.NSUrl>
        {
            new Foundation.NSUrl("https://example.com/1"),
            new Foundation.NSUrl("https://example.com/2"),
        };
        AssertEqual(202, collider.SetOwner(unique, 2), "bridged set and its NSSet-named sibling stay distinct");
    }

    public void TestClosureHandleKeepsCallbackAndSibling()
    {
        using var collider = new DerivedLocalNameCollider();
        // The Swift body calls callback(callbackHandle): the sibling is what reaches the closure.
        AssertEqual(84, collider.ClosureHandle(v => v * 2, 42), "closure and its Handle-named sibling stay distinct");
    }

    // ---------------------------------------------------------------------------------------
    //  Locals with a fixed spelling
    // ---------------------------------------------------------------------------------------

    public void TestExistentialReturnCarriesParameterValue()
    {
        using var collider = new FixedLocalNameCollider();
        var shape = collider.ExistentialReturn(21);
        AssertEqual(21, shape.ShapeValue, "existential return carries the value of the existentialResult-named parameter");
    }

    public void TestResultReturnSuccessCarriesParameterValue()
    {
        using var collider = new FixedLocalNameCollider();
        var result = collider.ResultReturn(5);
        AssertTrue(result.IsSuccess, "non-negative input takes the success arm");
        AssertEqual(10, result.Success, "Result return carries the doubled _swiftResult-named parameter");
    }

    public void TestResultReturnFailureArmReachable()
    {
        using var collider = new FixedLocalNameCollider();
        var result = collider.ResultReturn(-1);
        AssertFalse(result.IsSuccess, "negative input takes the failure arm");
    }

    public void TestIndirectResultPtrCarriesParameterValue()
    {
        using var collider = new FixedLocalNameCollider();
        using var wide = collider.IndirectResultPtr(10);
        // a..d == v, v+1, v+2, v+3 → 4v + 6
        AssertEqual(46L, wide.Total, "indirect return carries the resultPtr-named parameter");
    }

    public void TestIndirectReturnMetadataCarriesParameterValue()
    {
        using var collider = new FixedLocalNameCollider();
        using var wide = collider.IndirectReturnMetadata(20);
        AssertEqual(86L, wide.Total, "indirect return carries the returnMetadata-named parameter");
    }

    public void TestIndirectRegisterCarriesParameterValue()
    {
        using var collider = new FixedLocalNameCollider();
        using var wide = collider.IndirectRegister(30);
        AssertEqual(126L, wide.Total, "indirect return carries the swiftIndirectResult-named parameter");
    }

    public void TestSuccessFlagRoundTripsBothArms()
    {
        using var collider = new FixedLocalNameCollider();
        // Every wrapper body pins the receiver handle through a local spelled `success`.
        AssertEqual(1, collider.SuccessFlag(true), "true reaches the success-named parameter");
        AssertEqual(0, collider.SuccessFlag(false), "false reaches the success-named parameter");
    }

    public void TestSelfRegisterCarriesParameterValue()
    {
        using var collider = new FixedLocalNameCollider();
        // The Swift argument LABEL is `self`; the internal name (and so the C# parameter) is `value`.
        AssertEqual(21, collider.SelfRegister(3), "self-labelled parameter reaches the body");
    }

    public void TestResultLocalCarriesParameterValue()
    {
        using var collider = new FixedLocalNameCollider();
        AssertEqual(33, collider.ResultLocal(3), "result-named parameter is not displaced by the call's own result local");
    }

    // ---------------------------------------------------------------------------------------
    //  The direct return arm's fixed `swiftResult` local
    // ---------------------------------------------------------------------------------------

    public void TestDirectStringReturnCarriesParameterValue()
    {
        using var collider = new DirectReturnLocalNameCollider<int>(1);
        AssertEqual("s12", collider.StringReturn(12), "direct-arm string return carries the swiftResult-named parameter");
    }

    public void TestDirectOptionalReturnCarriesParameterValue()
    {
        using var collider = new DirectReturnLocalNameCollider<int>(1);
        var values = collider.OptionalReturn(4);
        AssertNotNull(values, "non-negative input yields a container");
        AssertEqual(1, values!.Count, "container holds the single element the body builds");
        AssertEqual("o4", values[0], "direct-arm optional return carries the swiftResult-named parameter");
    }

    public void TestDirectOptionalReturnNullArmReachable()
    {
        using var collider = new DirectReturnLocalNameCollider<int>(1);
        AssertNull(collider.OptionalReturn(-1), "negative input yields the nil arm");
    }

    public void TestDirectOptionalExistentialReturnCarriesParameterValue()
    {
        using var collider = new DirectReturnLocalNameCollider<int>(1);
        var shape = collider.OptionalExistentialReturn(15);
        AssertNotNull(shape, "non-negative input yields an existential");
        AssertEqual(15, shape!.ShapeValue, "direct-arm optional existential carries the swiftResult-named parameter");
    }

    public void TestDirectOptionalExistentialReturnNullArmReachable()
    {
        using var collider = new DirectReturnLocalNameCollider<int>(1);
        AssertNull(collider.OptionalExistentialReturn(-2), "negative input yields the nil arm");
    }

    public void TestDirectReturnColliderPreservesItsOwnSeed()
    {
        using var collider = new DirectReturnLocalNameCollider<int>(77);
        AssertEqual(77, collider.Seed, "the receiver's stored property is untouched by the return-local resolution");
    }

    // ---------------------------------------------------------------------------------------
    //  Failable-initializer factory locals
    // ---------------------------------------------------------------------------------------

    public void TestTagLocalColliderFactoryRoundTrips()
    {
        var ok = TagLocalCollider.TryCreate(6, out var value);
        AssertTrue(ok, "non-negative input constructs");
        using (value)
            AssertEqual(12, value.Value, "the tag-named parameter reaches the initializer body");
    }

    public void TestTagLocalColliderFactoryNilArmReachable()
    {
        AssertFalse(TagLocalCollider.TryCreate(-1, out _), "negative input takes the nil arm");
    }

    public void TestOptionalMetadataLocalColliderFactoryRoundTrips()
    {
        var ok = OptionalMetadataLocalCollider.TryCreate(4, out var value);
        AssertTrue(ok, "non-negative input constructs");
        using (value)
            AssertEqual(12, value.Value, "the optionalMetadata-named parameter reaches the initializer body");
    }

    public void TestOptionalMetadataLocalColliderFactoryNilArmReachable()
    {
        AssertFalse(OptionalMetadataLocalCollider.TryCreate(-3, out _), "negative input takes the nil arm");
    }

    public void TestResultBufferLocalColliderFactoryRoundTrips()
    {
        var ok = ResultBufferLocalCollider.TryCreate(5, out var value);
        AssertTrue(ok, "non-negative input constructs");
        using (value)
            AssertEqual(20, value.Value, "the resultBuffer-named parameter reaches the initializer body");
    }

    public void TestResultBufferLocalColliderFactoryNilArmReachable()
    {
        AssertFalse(ResultBufferLocalCollider.TryCreate(-4, out _), "negative input takes the nil arm");
    }

    // ---------------------------------------------------------------------------------------
    //  Reverse dispatch — negative control: the receiver re-declares parameters positionally
    // ---------------------------------------------------------------------------------------

    private sealed class SwiftResultReceiver : ISwiftResultParameterReceiver
    {
        public IReadOnlyList<string> Labels => new[] { "one", "two", "three" };

        public int Compute(int swiftResult) => swiftResult * 3;
    }

    public void TestProxyReceiverIsUnaffectedByARequirementParameterNamedLikeItsLocal()
    {
        // Negative control. The generated receiver re-declares every requirement parameter
        // positionally, so the Swift-authored spelling never shares a scope with the receiver's own
        // locals and nothing has to move. What this pins is that the property still holds.
        var receiver = new SwiftResultReceiver();
        AssertEqual(24, TestLibFunctions.InvokeSwiftResultReceiver(receiver, 8),
            "the requirement's swiftResult-named parameter reaches the C# conformer unchanged");
    }

    public void TestProxyReceiverContainerReturnUsesItsOwnLocal()
    {
        var receiver = new SwiftResultReceiver();
        AssertEqual(3, TestLibFunctions.ReadSwiftResultReceiverLabels(receiver),
            "the container-returning requirement marshals through its own return local");
    }

    // ---------------------------------------------------------------------------------------
    //  Locals minted by the asynchronous lanes
    // ---------------------------------------------------------------------------------------

    public async Task TestAsyncMemberWithCancellationTokenNamedParameter()
    {
        using var collider = new AsyncLocalNameCollider();
        // The emitted signature carries BOTH the user's `cancellationToken` int and the appended
        // token; passing the appended one by name proves they are separate parameters.
        AssertEqual(42, await collider.AwaitTokenAsync(7), "the user's cancellationToken-named parameter reaches the body");
        AssertEqual(60, await collider.AwaitTokenAsync(10, default), "the appended token is a distinct parameter");
    }

    public async Task TestAsyncMemberWithTokenAndItsDerivedLocalSpelled()
    {
        using var collider = new AsyncLocalNameCollider();
        // Three names want one spelling here: the container parameter, the sibling named after the
        // container's scratch local, and the token the emitter appends. Folding the element count
        // and the sibling into one answer catches a body that fed the wrong one to the call.
        AssertEqual(304, await collider.AwaitAliasedTokenAsync(new[] { 1, 2, 3 }, 4),
            "container parameter, its Buffer-named sibling and the appended token stay three distinct things");
        AssertEqual(206, await collider.AwaitAliasedTokenAsync(new[] { 8, 9 }, 6, default),
            "the appended token is still separately passable when both other names are taken");
    }

    public async Task TestAsyncMemberWithCallbackContextHandleNamedParameter()
    {
        using var collider = new AsyncLocalNameCollider();
        // The asynchronous body holds its callback context in a GC handle; a parameter spelled the
        // same way must not be what gets pinned and freed.
        AssertEqual(35, await collider.AwaitHandleAsync(5), "the handle-named parameter reaches the body");
    }

    public void TestCompletionHandlerSyncFormsRoundTrip()
    {
        using var collider = new CompletionHandlerLocalNameCollider();
        var seen = 0;
        collider.RunTcs(3, v => seen = v);
        AssertEqual(6, seen, "tcs-named parameter reaches the callback");
        collider.RunRegistration(3, v => seen = v);
        AssertEqual(9, seen, "registration-named parameter reaches the callback");
        collider.RunResult(3, v => seen = v);
        AssertEqual(12, seen, "result-named parameter reaches the callback");
        collider.RunToken(3, v => seen = v);
        AssertEqual(15, seen, "cancellationToken-named parameter reaches the callback");
    }

    public async Task TestCompletionHandlerTaskOverloadsCarryParameterValues()
    {
        using var collider = new CompletionHandlerLocalNameCollider();
        // Each overload's own locals (completion source, registration, lambda value, token) are
        // spelled like the member's parameter; the awaited value proves the parameter still arrives.
        AssertEqual(6, await collider.RunTcsAsync(3), "task overload forwards the tcs-named parameter");
        AssertEqual(9, await collider.RunRegistrationAsync(3), "task overload forwards the registration-named parameter");
        AssertEqual(12, await collider.RunResultAsync(3), "task overload forwards the result-named parameter");
        AssertEqual(15, await collider.RunTokenAsync(3), "task overload forwards the cancellationToken-named parameter");
    }

    public async Task TestCompletionHandlerTaskOverloadRecordsLastValue()
    {
        using var collider = new CompletionHandlerLocalNameCollider();
        await collider.RunResultAsync(11);
        AssertEqual(11, collider.LastValue, "the Swift body observed the parameter the overload forwarded");
    }

    // ---------------------------------------------------------------------------------------
    //  Forward dispatch through a generated proxy
    // ---------------------------------------------------------------------------------------
    //
    //  The value below comes FROM Swift, so every call goes through the generated proxy's
    //  forwarding member — which keeps the requirement's own parameter names and mints the
    //  dispatch body's scratch locals into the same scope. Reaching Swift at all is the proof
    //  the two families stayed separate.

    public void TestProxyBlittableReturnKeepsResultPointerLocalApart()
    {
        var receiver = TestLibFunctions.MakeProxyLocalNameReceiver(2);
        AssertEqual(209, receiver.BlittableResult(9), "the resultPtr-named parameter reaches the Swift witness");
    }

    public void TestProxyStringReturnKeepsDecodedSliceLocalApart()
    {
        var receiver = TestLibFunctions.MakeProxyLocalNameReceiver(3);
        AssertEqual("t3-4", receiver.TextResult(4), "the slice-named parameter reaches the Swift witness");
    }

    public void TestProxyDispatchKeepsPinnedContainerLocalApart()
    {
        var receiver = TestLibFunctions.MakeProxyLocalNameReceiver(2);
        AssertEqual(405, receiver.ContainerResult(5), "the containerPtr-named parameter reaches the Swift witness");
    }

    public void TestProxyThrowingDispatchKeepsErrorOutLocalApart()
    {
        var receiver = TestLibFunctions.MakeProxyLocalNameReceiver(2);
        AssertEqual(606, receiver.ThrowingResult(6), "the errorOut-named parameter reaches the Swift witness");
    }

    public void TestProxyThrowingDispatchStillPropagatesTheSwiftError()
    {
        var receiver = TestLibFunctions.MakeProxyLocalNameReceiver(2);
        // The out-parameter the dispatch really uses moved aside; the throwing arm proves it is
        // still the one being read, rather than the user's int.
        AssertThrows<Swift.Runtime.SwiftException>(() => receiver.ThrowingResult(-1),
            "a Swift error still surfaces through the moved-aside error out-parameter");
    }

    public void TestProxyStringParameterKeepsItsWireSliceLocalsApart()
    {
        var receiver = TestLibFunctions.MakeProxyLocalNameReceiver(1);
        // Both the pinned UTF-8 bytes and the wire slice built from the first parameter are
        // spelled by parameters of this member; the length folds the string in, the sibling adds.
        AssertEqual(507, receiver.SlicedResult("abcde", 7), "string parameter and both wire-local-named parameters stay distinct");
    }

    public void TestProxyIndirectStructReturnKeepsAllocationLocalsApart()
    {
        var receiver = TestLibFunctions.MakeProxyLocalNameReceiver(4);
        using var wide = receiver.StructResult(10, 20, 30);
        AssertEqual(64L, wide.Total, "the buffer/metadata/indirectResult-named parameters all reach the Swift witness");
    }

    public void TestProxyExistentialCellReturnKeepsContainerLocalApart()
    {
        var receiver = TestLibFunctions.MakeProxyLocalNameReceiver(2);
        var shape = receiver.ExistentialCellResult(11);
        AssertEqual(811, shape.ShapeValue, "the container-named parameter reaches the Swift witness");
    }

    public void TestProxyCollectionReturnKeepsResultPointerLocalApart()
    {
        var receiver = TestLibFunctions.MakeProxyLocalNameReceiver(1);
        var values = receiver.CollectionResult(5, 6);
        AssertEqual(2, values.Count, "the container return carries both elements the body builds");
        AssertEqual("c5", values[0], "the resultPtr-named parameter reaches the Swift witness");
        AssertEqual("e6", values[1], "its sibling reaches the Swift witness too");
    }

    // Enum case construction, case inspection and operators

    public void TestEnumCaseFactoryKeepsItsMetadataNamedAssociatedValue()
    {
        using var value = EnumCaseLocalNameCollider.Metadata(21);
        AssertEqual(42, value.Folded, "the metadata-named associated value reaches Swift, not the metadata local");
    }

    public void TestEnumCaseFactoryKeepsBufferAndIndirectResultNamedValues()
    {
        using var value = EnumCaseLocalNameCollider.Buffer(3, 7);
        AssertEqual(37, value.Folded, "both allocation-named associated values stayed distinct from the body's locals");
    }

    public void TestEnumCaseFactoryKeepsItsResultNamedAssociatedValue()
    {
        using var value = EnumCaseLocalNameCollider.Result(5);
        AssertEqual(15, value.Folded, "the result-named associated value reaches Swift, not the factory's own result local");
    }

    public void TestEnumCaseFactoryWithAStringValueSpellingTheResultPointer()
    {
        using var value = EnumCaseLocalNameCollider.Labelled("abcd", 9);
        AssertEqual(409, value.Folded, "the string value and the appended result pointer stayed apart");
    }

    public void TestEnumCaseFactoryMixingAResultPointerNameWithAStringSibling()
    {
        // The extern's own parameter list carries the sibling's pointer/length pair AND the
        // appended trailing result pointer alongside a value already spelled that way.
        using var value = EnumCaseLocalNameCollider.Mixed(6, "xyz");
        AssertEqual(6003, value.Folded, "the extern bound positionally and every value arrived where Swift expects it");
    }

    public void TestEnumTupleInspectionKeepsItsMetadataNamedElements()
    {
        using var value = TupleCaseLocalNameCollider.Pair(4, 9);
        AssertTrue(value.TryGetPair(out var metadata, out var success), "the pair case is recognized");
        AssertEqual(4, metadata, "the metadata-named element is read out of the payload");
        AssertEqual(9, success, "the success-named element is read out of the payload");
    }

    public void TestEnumTupleInspectionKeepsEveryScratchNamedElement()
    {
        using var value = TupleCaseLocalNameCollider.Triple(1, 2, 3);
        AssertTrue(value.TryGetTriple(out var enumCopy, out var tupleMetadata, out var offset0),
            "the triple case is recognized");
        AssertEqual(1, enumCopy, "the enum-copy-named element survives");
        AssertEqual(2, tupleMetadata, "the tuple-metadata-named element survives");
        AssertEqual(3, offset0, "the per-element-offset-named element survives");
    }

    public void TestEnumTupleInspectionRejectsTheOtherCase()
    {
        var value = TupleCaseLocalNameCollider.None;
        AssertFalse(value.TryGetPair(out _, out _), "a different case still reports no match");
    }

    public void TestEnumStringReturningMemberKeepsItsResultPointerNamedParameter()
    {
        using var value = EnumCaseLocalNameCollider.Metadata(1);
        AssertEqual("d3-4", value.Describe(3, 4), "both wire-local-named parameters reach the Swift member");
    }

    public void TestEnumStringReturningStaticKeepsItsResultPointerNamedParameter()
    {
        AssertEqual("l8-2", EnumCaseLocalNameCollider.Label(8, 2), "the static string return keeps its parameters too");
    }

    public void TestPayloadPlanFactoryKeepsItsMarshalledValueAndTheSiblingNamedAfterItsLocal()
    {
        // The array marshals through locals suffixed onto its own label; the sibling is spelled like
        // one of them. 6 * 10 + 9 — a value routed through the wrong identifier changes the answer.
        using var value = PayloadPlanLocalNameCollider.Packed(new[] { 1, 2, 3 }, 9);
        AssertEqual(69, value.Total, "the array and the sibling named after its marshalling local stay distinct");
    }

    public void TestPayloadPlanFactoryEmptyArmReachable()
    {
        var value = PayloadPlanLocalNameCollider.Empty;
        AssertEqual(-1, value.Total, "the payload-free case is still constructible");
    }

    public void TestOperatorWithOperandsSpellingTheReturnMetadataLocal()
    {
        // The operand identifiers come from the operand TYPE, so a type named after the local is
        // what makes this reachable; the wide return is what mints the local.
        using var left = new ReturnMetadata(11);
        using var right = new ReturnMetadata(31);
        using var sum = left + right;
        AssertEqual(42L, sum.Total, "both operands reached Swift rather than the metadata local");
    }

    // ---------------------------------------------------------------------------------------
    //  Extension members on a type declared outside this module
    // ---------------------------------------------------------------------------------------
    //
    //  These bodies are written by the extension emitters rather than the method pipeline, so
    //  their locals are minted against a scope built from the projected parameter names instead
    //  of a declaration. Each member below spells one of those locals with a parameter, which is
    //  what makes the collision reachable at all; the emitted binding compiling at all is the
    //  first half of the proof. These cases carry the second half — the argument the caller
    //  passed is what Swift actually received, and the value came back through the local the
    //  body declared for it — by folding each parameter into the answer with its own weight.

    public void TestStructReceiverExtensionKeepsItsResultNamedParameter()
    {
        // Frozen-struct receiver: the class return is read back through a local of the emitter's
        // own, declared into the body that already holds `result`.
        var point = new SwiftBindingsTestLibDependency.DependencyPoint(4.0, 1.0);
        using var token = point.TokenScaled(2.0);
        AssertEqual(42, token.Value, "the result-named parameter reaches Swift, not the return holder");
    }

    public void TestClassReceiverExtensionKeepsItsResultNamedParameter()
    {
        // Same shape on the class-receiver path, which is a different emitter.
        using var active = new SwiftBindingsTestLibDependency.DependencyService("s");
        using var positive = active.TokenTagged(17);
        AssertEqual(17, positive.Value, "the result-named parameter reaches Swift on the class path");

        using var inactive = new SwiftBindingsTestLibDependency.DependencyService("s", false);
        using var negated = inactive.TokenTagged(17);
        AssertEqual(-17, negated.Value, "the receiver still selects the arm, so it was not displaced either");
    }

    public void TestForeignReceiverExtensionKeepsItsResultNamedParameter()
    {
        // Foreign (ObjC-imported) receiver — a third emitter, same class-return shape.
        using var obj = new Foundation.NSObject();
        using var receipt = obj.Receipt(14);
        AssertEqual(42, receipt.Code, "the result-named parameter reaches Swift on the foreign path");
    }

    public void TestForeignReceiverExtensionKeepsAllThreeAllocationNamedParameters()
    {
        // A resilient struct return needs three locals of the emitter's own — the metadata, the
        // buffer it sizes, and the indirect-result handle — and all three are spelled here. The
        // score weights each parameter differently, so any two swapped changes the answer.
        using var obj = new Foundation.NSObject();
        using var summary = obj.Summarize(1, 2, 3);
        AssertEqual(123, summary.Score, "each allocation-named parameter arrived in its own position");
        AssertEqual("summary", summary.Label, "the value came back out of the buffer the body allocated");
    }

    public void TestForeignReceiverExtensionKeepsAParameterNamedLikeTheNativeResultRegister()
    {
        // The register handle for a resilient return is a parameter of the native declaration,
        // not a body local, so it shares a list with the caller's arguments rather than with the
        // body's locals — a second place one generated name can land on a projected one. Nothing
        // in the emitted binding names them apart, so the value arriving intact is what says the
        // argument and the register stayed in their own positions.
        using var obj = new Foundation.NSObject();
        using var stamped = obj.Stamped(21);
        AssertEqual(42, stamped.Score, "the argument reached Swift rather than the result register");
        AssertEqual("stamped", stamped.Label, "the struct came back through the register the caller supplied");
    }

    // ---------------------------------------------------------------------------------------
    //  A parameter spelled like the extension's own receiver
    // ---------------------------------------------------------------------------------------
    //
    //  `self` is a legal Swift argument label, and it projects to a C# parameter spelled `self`.
    //  The emitted extension member also takes the receiver as its first parameter, and `self` is
    //  the spelling that reads naturally there — so the two land in one parameter list. The
    //  receiver is the synthesized name, so the receiver is what moves; the user's parameter keeps
    //  the spelling Swift gave it. Each receiver kind below is written by a different emitter.

    public void TestStructReceiverExtensionKeepsAParameterSpelledLikeTheReceiver()
    {
        // x and y take the argument with opposite signs, so a body that passed the receiver's
        // coordinate where the argument belonged could not produce both answers.
        var point = new SwiftBindingsTestLibDependency.DependencyPoint(4.0, 1.0);
        var moved = point.OffsetWithSelfLabel(2.0);
        AssertEqual(6.0, moved.X, "the self-labelled argument reached Swift on the struct-receiver path");
        AssertEqual(-1.0, moved.Y, "and it arrived once, with its own sign");
    }

    public void TestClassReceiverExtensionKeepsAParameterSpelledLikeTheReceiver()
    {
        // The receiver still selects the arm, so it was not displaced by the argument either.
        using var active = new SwiftBindingsTestLibDependency.DependencyService("s");
        AssertEqual(15, active.TagWithSelfLabel(5), "the self-labelled argument reached Swift on the class path");

        using var inactive = new SwiftBindingsTestLibDependency.DependencyService("s", false);
        AssertEqual(-5, inactive.TagWithSelfLabel(5), "and the receiver still chose the arm");
    }

    public void TestForeignReceiverExtensionKeepsAParameterSpelledLikeTheReceiver()
    {
        // Foreign (ObjC-imported) receiver — a third emitter, same shape.
        using var obj = new Foundation.NSObject();
        AssertEqual(16, obj.ScoredWithSelfLabel(3), "the self-labelled argument reached Swift on the foreign path");
    }
}
