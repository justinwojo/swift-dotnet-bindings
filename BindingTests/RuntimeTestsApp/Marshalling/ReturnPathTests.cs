// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests return type marshalling through different generator code paths.
/// Exercises WrapperEmitter.Return and PInvokeEmitter return handling.
///
/// Coverage gaps addressed:
/// - Tuple return (WrapperEmitter.Return:279-291, PInvokeEmitter:100-128)
/// - Closure return (WrapperEmitter.Return:250-269, PInvokeEmitter:73-92)
/// - Optional&lt;closure&gt; return (WrapperEmitter.Return:151-175)
/// - DynamicSelf on class (PInvokeEmitter:179-182)
/// - Self return on struct (MethodWrapperEmitter:145-146)
/// - String return via @_cdecl (WrapperEmitter.Return:102-117)
///
/// Note: All return path methods use CallConvCdecl wrappers with indirect result buffers.
/// The crashes are in the marshalling layer (ValueTuple StructLayout.Auto, closure 16-byte
/// struct return), not in CallConvSwift register assignment.
/// </summary>
public class ReturnPathTests : TestBase
{
    public ReturnPathTests(TestResults results) : base(results) { }

    #region PairMaker — Tuple Return

    public void TestPairMakerConstruction()
    {
        var maker = new PairMaker(label: "test");
        AssertNotNull(maker, "PairMaker constructed");
        TestLogger.Info("PairMaker construction passed");
    }

    public void TestPairMakerTupleReturn()
    {
        var maker = new PairMaker(label: "item");
        var pair = maker.MakePair(value: 42);
        AssertEqual(42, pair.Item1, "Tuple.Item1 (Int32)");
        AssertEqual("item:42", pair.Item2, "Tuple.Item2 (String)");
        TestLogger.Info($"PairMaker.MakePair = ({pair.Item1}, {pair.Item2})");
    }

    #endregion

    #region TransformFactory — Closure Return

    public void TestTransformFactoryConstruction()
    {
        var factory = new TransformFactory(multiplier: 3);
        AssertNotNull(factory, "TransformFactory constructed");
        TestLogger.Info("TransformFactory construction passed");
    }

    public void TestTransformFactoryClosureReturn()
    {
        var factory = new TransformFactory(multiplier: 3);
        var transform = factory.MakeTransform();
        AssertNotNull(transform, "MakeTransform returned non-null");
        var result = transform(10);
        AssertEqual(30, result, "transform(10) = 10 * 3");
        TestLogger.Info($"TransformFactory.MakeTransform()(10) = {result}");
    }

    #endregion

    #region StringArgTransformFactory — Closure Return with String arg (invoke-thunk)

    public void TestStringArgTransformFactoryConstruction()
    {
        var factory = new StringArgTransformFactory(factor: 2);
        AssertNotNull(factory, "StringArgTransformFactory constructed");
        TestLogger.Info("StringArgTransformFactory construction passed");
    }

    /// <summary>
    /// Returned closure takes a String arg. The invoke thunk must convert the
    /// incoming string to Swift.SwiftString and marshal it through a retaining
    /// value-witness copy — previously emitted GetTypeMetadataOrThrow&lt;string&gt;()
    /// which throws at runtime. Closure returns utf8-length * factor.
    /// </summary>
    public void TestStringArgTransformFactoryClosureReturn()
    {
        var factory = new StringArgTransformFactory(factor: 2);
        var lengthOf = factory.MakeStringLength();
        AssertNotNull(lengthOf, "MakeStringLength returned non-null");
        var result = lengthOf("hello");
        AssertEqual(10, result, "lengthOf(\"hello\") = 5 * 2");
        TestLogger.Info($"StringArgTransformFactory.MakeStringLength()(\"hello\") = {result}");
    }

    #endregion

    #region DataArgTransformFactory — Closure Return with Data arg (invoke-thunk)

    public void TestDataArgTransformFactoryConstruction()
    {
        var factory = new DataArgTransformFactory(factor: 3);
        AssertNotNull(factory, "DataArgTransformFactory constructed");
        TestLogger.Info("DataArgTransformFactory construction passed");
    }

    /// <summary>
    /// Returned closure takes a Foundation.Data arg. The invoke thunk must
    /// convert the incoming byte[] via Swift.Foundation.Data.FromByteArray —
    /// previously emitted GetTypeMetadataOrThrow&lt;byte[]&gt;() which throws.
    /// Closure returns byte-count * factor.
    /// </summary>
    public void TestDataArgTransformFactoryClosureReturn()
    {
        var factory = new DataArgTransformFactory(factor: 3);
        var byteCount = factory.MakeByteCount();
        AssertNotNull(byteCount, "MakeByteCount returned non-null");
        var result = byteCount(new byte[] { 1, 2, 3, 4 });
        AssertEqual(12, result, "byteCount([4 bytes]) = 4 * 3");
        TestLogger.Info($"DataArgTransformFactory.MakeByteCount()([4]) = {result}");
    }

    #endregion

    #region OptionalHandlerFactory — Optional<Closure> Return

    public void TestOptionalHandlerFactoryConstruction()
    {
        var factory = new OptionalHandlerFactory(enabled: true);
        AssertNotNull(factory, "OptionalHandlerFactory constructed");
        TestLogger.Info("OptionalHandlerFactory construction passed");
    }

    public void TestOptionalHandlerReturnsValue()
    {
        var factory = new OptionalHandlerFactory(enabled: true);
        var handler = factory.MakeHandler();
        AssertNotNull(handler, "MakeHandler should return non-null when enabled");
        var result = handler!(10);
        AssertEqual(20, result, "Handler should double the value");
        TestLogger.Info($"OptionalHandlerFactory.MakeHandler(10) = {result}");
    }

    public void TestOptionalHandlerReturnsNil()
    {
        var factory = new OptionalHandlerFactory(enabled: false);
        var handler = factory.MakeHandler();
        AssertNull(handler, "MakeHandler should return null when disabled");
        TestLogger.Info("OptionalHandlerFactory.MakeHandler null passed");
    }

    #endregion

    #region Buildable — DynamicSelf Return on Class

    public void TestBuildableConstruction()
    {
        var b = new Buildable(tag: 1);
        AssertNotNull(b, "Buildable constructed");
        AssertEqual(1, b.Tag, "Buildable.Tag");
        TestLogger.Info("Buildable construction passed");
    }

    public void TestBuildableDynamicSelfReturn()
    {
        var b = new Buildable(tag: 1);
        var b2 = b.WithTag(42);
        AssertNotNull(b2, "WithTag returned non-null");
        AssertEqual(42, b2.Tag, "New tag value");
        TestLogger.Info("Buildable.WithTag DynamicSelf return passed");
    }

    #endregion

    #region SelfChainingBuilder — DynamicSelf Return through Swift-convention routes

    // Every SelfChainingBuilder member returns `self` (the factory returns a fresh instance). A
    // class `Self` comes back as one +1 reference in the return register, so the binding must
    // take that reference, not read a result buffer the callee never writes.

    public void TestSelfChainingTrimmedRequestOverloadReturnsSameInstance()
    {
        using var builder = new SelfChainingBuilder(tag: 7);
        using var returned = builder.OnRequestCreation(_ => { });
        AssertSameBuilder(builder, returned, 7, "trimmed OnRequestCreation");
        AssertEqual(1, returned.Registrations, "the call registered once on the returned object");
        AssertEqual("com.apple.main-thread", builder.LastQueueLabel, "the trimmed overload passed the main-queue default");
    }

#pragma warning disable SB0001 // The full form calls the library directly; its result convention is what is under test.
    public void TestSelfChainingFullRequestOverloadReturnsSameInstance()
    {
        using var builder = new SelfChainingBuilder(tag: 8);
        using var queue = Swift.DispatchQueue.Global();
        using var returned = builder.OnRequestCreation(queue, _ => { });
        AssertSameBuilder(builder, returned, 8, "full OnRequestCreation");
        AssertEqual(queue.Label, builder.LastQueueLabel, "the full form passed the given queue");
        AssertEqual(1, builder.Registrations, "the call registered once");
    }

    public void TestSelfChainingOptionalSelfOnSwiftConventionRoute()
    {
        using var builder = new SelfChainingBuilder(tag: 9);
        using (var tagged = builder.OnRequestIfTagged(9, _ => { }))
        {
            AssertNotNull(tagged, "OnRequestIfTagged with the matching tag returns the receiver");
            AssertSameBuilder(builder, tagged!, 9, "OnRequestIfTagged");
        }
        AssertNull(builder.OnRequestIfTagged(10, _ => { }), "OnRequestIfTagged with another tag is nil");
        AssertEqual(1, builder.Registrations, "only the matching call registered");
    }

    public void TestSelfChainingClassFactoryOnSwiftConventionRoute()
    {
        using var made = SelfChainingBuilder.MakeForRequests(tag: 12, _ => { });
        AssertNotNull(made, "MakeForRequests returned an instance");
        AssertEqual(12, made.Tag, "factory instance keeps its tag");
        AssertTrue(made.IsSameObject(made), "factory result is usable as a receiver");
        AssertEqual(1, made.Registrations, "factory registered its handler");
    }
#pragma warning restore SB0001

    public void TestSelfChainingClosureRoutesReturnSameInstance()
    {
        using var builder = new SelfChainingBuilder(tag: 3);
        int sum = 0;
        using (var r = builder.OnEvent(v => sum += v)) AssertSameBuilder(builder, r, 3, "trimmed OnEvent");
        using (var r = builder.OnEvent(offset: 5, v => sum += v)) AssertSameBuilder(builder, r, 3, "full OnEvent");
        AssertEqual(5, builder.LastOffset, "full OnEvent passed its offset");
        using (var r = builder.WithHandler(v => sum += v)) AssertSameBuilder(builder, r, 3, "WithHandler");
        using (var r = builder.Touch(10)) AssertSameBuilder(builder, r, 3, "trimmed Touch");
        AssertEqual(13, builder.Registrations, "three closure registrations plus a touch of ten");
        AssertEqual(3, builder.Fire(1), "three integer handlers registered");
        AssertEqual(12, sum, "each handler saw value + tag");

        using (var tagged = builder.SelfIfTagged(3))
            AssertSameBuilder(builder, tagged!, 3, "SelfIfTagged with the matching tag");
        AssertNull(builder.SelfIfTagged(4), "SelfIfTagged with another tag is nil");

        int factoryCalls = 0;
        using var made = SelfChainingBuilder.Make(tag: 11, _ => factoryCalls++);
        AssertEqual(11, made.Tag, "factory instance keeps its tag");
        AssertEqual(1, made.Registrations, "factory registered its handler");
        AssertEqual(1, made.Fire(0), "factory handler is callable");
        AssertEqual(1, factoryCalls, "factory handler ran once");
    }

    public void TestSelfChainingOperatorReturnsFreshInstance()
    {
        using var lhs = new SelfChainingBuilder(tag: 4);
        using var rhs = new SelfChainingBuilder(tag: 5);
        using var sum = lhs + rhs;
        AssertNotNull(sum, "operator + returned an instance");
        AssertEqual(9, sum.Tag, "operator + result reads its own state");
        AssertFalse(sum.IsSameObject(lhs), "operator + returned a new object, not an operand");
        AssertTrue(sum.IsSameObject(sum), "operator + result is usable as a receiver");
        // The operator reads the class metatype; the call must leave the class itself intact, so a
        // fresh instance still constructs through the class's metadata afterwards.
        using var after = new SelfChainingBuilder(tag: 6);
        AssertEqual(6, after.Tag, "the class still constructs after operator +");
    }

    public void TestSelfChainingOptionalSelfOperatorReturnsFreshInstanceOrNull()
    {
        using var lhs = new SelfChainingBuilder(tag: 9);
        using var rhs = new SelfChainingBuilder(tag: 4);
        using (var difference = lhs - rhs)
        {
            AssertNotNull(difference, "operator - with a non-negative difference returned an instance");
            AssertEqual(5, difference!.Tag, "operator - result reads its own state");
            AssertFalse(difference.IsSameObject(lhs), "operator - returned a new object, not an operand");
            AssertTrue(difference.IsSameObject(difference), "operator - result is usable as a receiver");
        }
        AssertNull(rhs - lhs, "operator - with a negative difference is nil");
        // The class metatype the operator reads must survive the call.
        using var after = new SelfChainingBuilder(tag: 6);
        AssertEqual(6, after.Tag, "the class still constructs after operator -");
    }

    public void TestSelfChainingOptionalClassOperatorReturnsFreshInstanceOrNull()
    {
        using var lhs = new SelfChainingBuilder(tag: 3);
        using var rhs = new SelfChainingBuilder(tag: 7);
        using var zero = new SelfChainingBuilder(tag: 0);
        using (var product = lhs * rhs)
        {
            AssertNotNull(product, "operator * with a non-zero product returned an instance");
            AssertEqual(21, product!.Tag, "operator * result reads its own state");
            AssertFalse(product.IsSameObject(lhs), "operator * returned a new object, not an operand");
            AssertTrue(product.IsSameObject(product), "operator * result is usable as a receiver");
        }
        AssertNull(lhs * zero, "operator * with a zero product is nil");
        using var after = new SelfChainingBuilder(tag: 8);
        AssertEqual(8, after.Tag, "the class still constructs after operator *");
    }

    public void TestSelfChainingReturnsDoNotLeak()
    {
        LifetimeTracker.RunWithLeakCheck(() =>
        {
            for (int i = 0; i < 10; i++)
                ChainAndDropSelfReturns(i);
        }, "SelfChainingBuilder Self returns");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ChainAndDropSelfReturns(int tag)
    {
        using var builder = new SelfChainingBuilder(tag: tag);
        using var queue = Swift.DispatchQueue.Global();
        using (var r = builder.OnRequestCreation(_ => { })) AssertSameBuilder(builder, r, tag, "trimmed OnRequestCreation");
#pragma warning disable SB0001
        using (var r = builder.OnRequestCreation(queue, _ => { })) AssertSameBuilder(builder, r, tag, "full OnRequestCreation");
#pragma warning restore SB0001
        using (var r = builder.OnEvent(_ => { })) AssertSameBuilder(builder, r, tag, "trimmed OnEvent");
        using (var r = builder.WithHandler(_ => { })) AssertSameBuilder(builder, r, tag, "WithHandler");
        using (var r = builder.Touch(1)) AssertSameBuilder(builder, r, tag, "trimmed Touch");
        using (var r = builder.SelfIfTagged(tag)) AssertSameBuilder(builder, r!, tag, "SelfIfTagged");
#pragma warning disable SB0001
        using (var r = builder.OnRequestIfTagged(tag, _ => { })) AssertSameBuilder(builder, r!, tag, "OnRequestIfTagged");
        using (var made = SelfChainingBuilder.MakeForRequests(tag: tag + 200, _ => { })) AssertEqual(tag + 200, made.Tag, "request factory tag");
#pragma warning restore SB0001
        using (var made = SelfChainingBuilder.Make(tag: tag + 100, _ => { })) AssertEqual(tag + 100, made.Tag, "factory tag");
        using (var sum = builder + builder) AssertEqual(tag + tag, sum.Tag, "operator + tag");
        using (var larger = new SelfChainingBuilder(tag: tag + 1))
        {
            using (var difference = larger - builder) AssertEqual(1, difference!.Tag, "operator - tag");
            AssertNull(builder - larger, "operator - nil");
        }
        // The product is nil on the first pass (tag 0) and an instance on every other one.
        using (var product = builder * builder)
        {
            if (tag == 0) AssertNull(product, "operator * nil");
            else AssertEqual(tag * tag, product!.Tag, "operator * tag");
        }
    }

    private void AssertSameBuilder(SelfChainingBuilder expected, SelfChainingBuilder actual, int tag, string route)
    {
        AssertNotNull(actual, $"{route} returned an object");
        AssertTrue(expected.IsSameObject(actual), $"{route} returned the receiver itself");
        AssertTrue(actual.IsSameObject(expected), $"{route} result is usable as a receiver");
        AssertEqual(tag, actual.Tag, $"{route} result reads the receiver's state");
    }

    #endregion

    #region CopyableValue — Self Return on Struct

    public void TestCopyableValueConstruction()
    {
        var val = new CopyableValue(value: 10);
        AssertEqual(10, val.Value, "Initial value");
        TestLogger.Info("CopyableValue construction passed");
    }

    public void TestCopyableValueSelfReturn()
    {
        var val = new CopyableValue(value: 10);
        var val2 = val.WithValue(42);
        AssertEqual(42, val2.Value, "New value after WithValue");
        AssertEqual(10, val.Value, "Original unchanged");
        TestLogger.Info("CopyableValue.WithValue passed");
    }

    public void TestCopyableValueDescribe()
    {
        var val = new CopyableValue(value: 99);
        var desc = val.GetDescribe();
        AssertTrue(desc.Contains("99"), "Description contains value");
        TestLogger.Info($"CopyableValue.GetDescribe() = {desc}");
    }

    #endregion

    #region Greeter — String Return via @_cdecl

    public void TestGreeterConstruction()
    {
        var g = new Greeter(name: "World");
        AssertNotNull(g, "Greeter constructed");
        TestLogger.Info("Greeter construction passed");
    }

    public void TestGreeterStringReturn()
    {
        var g = new Greeter(name: "World");
        var greeting = g.Greet(greeting: "Hello");
        AssertEqual("Hello, World!", greeting, "Greeting string");
        TestLogger.Info($"Greeter.Greet = {greeting}");
    }

    public void TestGreeterHeapBackedStringParam()
    {
        // R3-F1 end-to-end sentinel for the @_cdecl String-by-value fast path (EphemeralSwiftString).
        // Every other Greeter test uses ≤15-byte inline small strings; this passes a >15 UTF-8 byte
        // argument, which forces Swift's large/heap-backed String form — the representation whose
        // transient +1 the EphemeralSwiftString ref struct owns and releases on Dispose. A regression
        // that dropped the `using` or copied the owning handle would over-release this heap storage
        // (crash / corruption), so a clean round-trip here affirmatively re-confirms the fast path.
        const string longGreeting = "a thirty-two character greeting!"; // 32 UTF-8 bytes (> 15 → heap-backed)
        var g = new Greeter(name: "Alice");
        var greeting = g.Greet(greeting: longGreeting);
        AssertEqual("a thirty-two character greeting!, Alice!", greeting, "Heap-backed greeting round-trips");
        TestLogger.Info($"Greeter.Greet (heap-backed) = {greeting}");
    }

    #endregion

    #region VoidParamArrayFactory — Void Param + Array Return via @_cdecl

    /// <summary>
    /// Regression: a static method whose only parameter is Void (empty tuple) and
    /// whose Array return routes through the @_cdecl wrapper. The wrapper must
    /// forward the call as `make(first: ())`; dropping the Void argument emitted
    /// invalid Swift ("missing argument for parameter 'first'"). Models TipKit's
    /// `Tips.GroupBuilder.buildPartialBlock(first: Void) -> [any Tip]`.
    /// </summary>
    public void TestVoidParamArrayReturn()
    {
        var result = VoidParamArrayFactory.Make();
        AssertNotNull(result, "VoidParamArrayFactory.Make() returned non-null");
        AssertEqual(3, result.Count, "Array element count");
        AssertEqual(10, result[0], "Element[0]");
        AssertEqual(20, result[1], "Element[1]");
        AssertEqual(30, result[2], "Element[2]");
        TestLogger.Info($"VoidParamArrayFactory.Make() = [{string.Join(", ", result)}]");
    }

    #endregion
}
