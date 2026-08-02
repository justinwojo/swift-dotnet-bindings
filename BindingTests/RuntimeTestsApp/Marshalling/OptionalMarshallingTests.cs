// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for optional type marshalling: Some/None for blittable/class, struct properties.
/// </summary>
public class OptionalMarshallingTests : TestBase
{
    public OptionalMarshallingTests(TestResults results) : base(results) { }

    #region Tier 1 — Smoke Tests

    public void TestOptionalBlittableReturnSome()
    {
        var index = TestLibFunctions.FindIndex(new[] { 10, 20, 30 }, 20);
        AssertTrue(index.HasValue, "FindIndex found value");
        AssertEqual(1, index!.Value, "FindIndex returns correct index");
        TestLogger.Info($"FindIndex([10,20,30], 20) = {index}");
    }

    public void TestOptionalBlittableReturnNone()
    {
        var index = TestLibFunctions.FindIndex(new[] { 10, 20, 30 }, 99);
        AssertFalse(index.HasValue, "FindIndex returns null for missing value");
        TestLogger.Info("FindIndex returns null for missing value");
    }

    #endregion

    #region Tier 2 — Functional Tests

    public void TestOptionalClassReturnSome()
    {
        var cat = TestLibFunctions.CreateAnimal("Cat", "Meow");
        var dog = TestLibFunctions.CreateAnimal("Dog", "Woof");

        var found = TestLibFunctions.FindAnimalByName(new[] { cat, dog }, "Cat");
        AssertNotNull(found, "FindAnimalByName found Cat");
        TestLogger.Info("FindAnimalByName returned a non-null result");
    }

    public void TestOptionalClassReturnNone()
    {
        var cat = TestLibFunctions.CreateAnimal("Cat", "Meow");

        var found = TestLibFunctions.FindAnimalByName(new[] { cat }, "Parrot");
        AssertNull(found, "FindAnimalByName returns null for missing name");
        TestLogger.Info("FindAnimalByName returns null for missing name");
    }

    public void TestOptionalParameterSome()
    {
        var result = TestLibFunctions.DescribeOptionalInt(42);
        AssertEqual("Value: 42", result, "DescribeOptionalInt with value");
        TestLogger.Info($"DescribeOptionalInt(42) = \"{result}\"");
    }

    public void TestOptionalParameterNone()
    {
        var result = TestLibFunctions.DescribeOptionalInt(null);
        AssertEqual("nil", result, "DescribeOptionalInt with null");
        TestLogger.Info($"DescribeOptionalInt(null) = \"{result}\"");
    }

    public void TestOptionalConfigConstructorWithLabel()
    {
        // Exercises NewSome(SwiftString) through frozen struct constructor
        var config = new OptionalConfig(new SwiftString("Primary"), 10, "Fallback");
        AssertEqual("Primary", config.Label, "Constructor sets String? label");
        AssertEqual(10, config.Count, "Constructor sets Int32? count");
        AssertEqual("Fallback", config.FallbackLabel, "Constructor sets fallbackLabel");
        TestLogger.Info("OptionalConfig constructor with label passed");
    }

    public void TestOptionalConfigConstructorWithoutLabel()
    {
        var config = new OptionalConfig(null, null, "Default");
        AssertNull(config.Label, "Constructor with null label");
        AssertFalse(config.Count.HasValue, "Constructor with null count");
        AssertEqual("Default", config.FallbackLabel, "Constructor sets fallbackLabel");
        TestLogger.Info("OptionalConfig constructor without label passed");
    }

    public void TestOptionalConfigEffectiveLabel()
    {
        var config = new OptionalConfig(new SwiftString("Primary"), 10, "Fallback");
        var label = config.GetEffectiveLabel();
        AssertEqual("Primary", label, "EffectiveLabel with label");

        var configNoLabel = new OptionalConfig(null, null, "Fallback");
        var fallback = configNoLabel.GetEffectiveLabel();
        AssertEqual("Fallback", fallback, "EffectiveLabel without label");
        TestLogger.Info("OptionalConfig.EffectiveLabel tests passed");
    }

    public void TestFindIndexFirstElement()
    {
        var index = TestLibFunctions.FindIndex(new[] { 5, 10, 15 }, 5);
        AssertTrue(index.HasValue, "FindIndex first element");
        AssertEqual(0, index!.Value, "First element index is 0");
        TestLogger.Info("FindIndex first element passed");
    }

    public void TestFindIndexEmptyArray()
    {
        var index = TestLibFunctions.FindIndex(Array.Empty<int>(), 1);
        AssertFalse(index.HasValue, "FindIndex empty array returns null");
        TestLogger.Info("FindIndex empty array passed");
    }

    public void TestOptionalStringPropertySetter()
    {
        var config = new OptionalConfig(null, null, "Fallback");
        config.Label = "Updated";
        AssertEqual("Updated", config.Label, "Label setter with String? Some");

        config.Label = null;
        AssertNull(config.Label, "Label setter with String? None");
        TestLogger.Info("OptionalStringPropertySetter tests passed");
    }

    // Fixed: Optional pointer wrapper passes full 16-byte Optional<String> via UnsafeRawPointer
    public void TestOptionalStringParameterSome()
    {
        var result = TestLibFunctions.DescribeOptionalString("hello");
        AssertEqual("Value: hello", result, "DescribeOptionalString with value");
        TestLogger.Info($"DescribeOptionalString(\"hello\") = \"{result}\"");
    }

    // Fixed: Optional pointer wrapper passes full 16-byte Optional<String> via UnsafeRawPointer
    public void TestOptionalStringParameterNone()
    {
        var result = TestLibFunctions.DescribeOptionalString(null);
        AssertEqual("nil", result, "DescribeOptionalString with null");
        TestLogger.Info($"DescribeOptionalString(null) = \"{result}\"");
    }

    #endregion

    #region Tier 3 — Optbuf ARC Regression Tests

    public void TestOptionalStringReturnLongSome()
    {
        // Regression: optbuf wrapper used copyMemory (raw memcpy) instead of initializeMemory.
        // For heap-allocated strings (>15 UTF-8 bytes), the returned string was freed when
        // the Swift wrapper returned, leaving dangling bytes in the result buffer.
        // This reproduces the heap-string optbuf crash (SIGSEGV in InitializeWithCopy).
        var result = TestLibFunctions.GetLongOptionalString(false);
        AssertNotNull(result, "Long optional string should be Some");
        AssertEqual("This is a long string that exceeds small string optimization", result,
            "Long optional string value matches");
        TestLogger.Info($"GetLongOptionalString(false) = \"{result}\"");
    }

    public void TestOptionalStringReturnLongNone()
    {
        var result = TestLibFunctions.GetLongOptionalString(true);
        AssertNull(result, "Long optional string should be None");
        TestLogger.Info("GetLongOptionalString(true) = null");
    }

    public void TestOptionalStringPropertyOnClass()
    {
        // Exercises optbuf wrapper for class property getter returning Optional<String>
        using var holder = new OptionalStringHolder("A long device name like iPhone 15 Pro Max Extended");
        var name = holder.OptionalName;
        AssertNotNull(name, "OptionalStringHolder.OptionalName should be Some");
        AssertEqual("A long device name like iPhone 15 Pro Max Extended", name,
            "OptionalStringHolder.OptionalName value matches");
        TestLogger.Info($"OptionalStringHolder.OptionalName = \"{name}\"");
    }

    public void TestOptionalStringPropertyOnClassNone()
    {
        using var holder = new OptionalStringHolder(null);
        var name = holder.OptionalName;
        AssertNull(name, "OptionalStringHolder.OptionalName should be None");
        TestLogger.Info("OptionalStringHolder.OptionalName = null");
    }

    #endregion

    #region Optional<typealias-to-primitive>

    public void TestOptionalTimeIntervalParameterSome()
    {
        // Foundation.TimeInterval is a typealias to Double. The C# binding must accept
        // double? (and not Swift.SwiftOptional<IntPtr>) — verifying the projection fallback
        // resolves the alias to its primitive ABI form.
        var result = TestLibFunctions.DescribeOptionalTimeInterval(2.5);
        AssertEqual("Interval: 2.5", result, "DescribeOptionalTimeInterval with value");
        TestLogger.Info($"DescribeOptionalTimeInterval(2.5) = \"{result}\"");
    }

    public void TestOptionalTimeIntervalParameterNone()
    {
        var result = TestLibFunctions.DescribeOptionalTimeInterval(null);
        AssertEqual("nil", result, "DescribeOptionalTimeInterval with null");
        TestLogger.Info($"DescribeOptionalTimeInterval(null) = \"{result}\"");
    }

    public void TestOptionalTimeIntervalReturnSome()
    {
        var result = TestLibFunctions.ComputeOptionalDuration(3.5);
        AssertTrue(result.HasValue, "ComputeOptionalDuration positive returns Some");
        AssertEqual(3.5, result!.Value, "ComputeOptionalDuration value matches");
        TestLogger.Info($"ComputeOptionalDuration(3.5) = {result}");
    }

    public void TestOptionalTimeIntervalReturnNone()
    {
        var result = TestLibFunctions.ComputeOptionalDuration(-1.0);
        AssertFalse(result.HasValue, "ComputeOptionalDuration non-positive returns None");
        TestLogger.Info("ComputeOptionalDuration(-1.0) = null");
    }

    #endregion

    #region Optional<generic-param> on generic struct

    // Round-trips Optional<Value> through OptionalGenericHolder<Value>'s constructor,
    // stored property accessors, and peek() method. Exercises the generic-typed indirect
    // result path: PInvokeSignatureBuilder must emit SwiftIndirectResult and the projection
    // must avoid the inline byte-read path (which assumes a trailing discriminant byte
    // and is wrong for class TValue where Optional<T> reuses the pointer's spare bits).
    //
    // [SkipOnMonoJit]: the getters/peek() path goes through a raw CallConvSwift P/Invoke
    // (no @_cdecl wrapper — Swift forbids @_cdecl on generic-param-bearing functions, and
    // an unconstrained TValue gives no protocol witness to dispatch through). The
    // (SwiftIndirectResult, IntPtr, SwiftSelf) signature trips Mono's JIT first-call
    // !ji->async assertion (upstream Issue 1) on the Simulator and Catalyst (both Mono).
    // macOS (CoreCLR) and NativeAOT (device) handle this signature shape and are the gate for
    // these cases. Constructor uses (SwiftIndirectResult, IntPtr… IntPtr) without SwiftSelf so
    // it does not hit the same path.

    public void TestOptionalGenericHolderStoredSome()
    {
        var animal = TestLibFunctions.CreateAnimal("Lion", "Roar");
        using var holder = new OptionalGenericHolder<Animal>(animal);
        var stored = holder.Stored;
        AssertNotNull(stored, "Stored returns Some(animal)");
        AssertEqual("Lion", stored!.Name, "Stored animal's Name preserved");
        AssertEqual("Roar", stored.Sound, "Stored animal's Sound preserved");
        TestLogger.Info($"OptionalGenericHolder<Animal>(Lion).Stored.Name = \"{stored.Name}\"");
    }

    public void TestOptionalGenericHolderStoredNone()
    {
        using var holder = new OptionalGenericHolder<Animal>(null);
        var stored = holder.Stored;
        AssertNull(stored, "Stored returns None for null-constructed holder");
        TestLogger.Info("OptionalGenericHolder<Animal>(null).Stored = null");
    }

    public void TestOptionalGenericHolderStoredSetter()
    {
        using var holder = new OptionalGenericHolder<Animal>(null);
        AssertNull(holder.Stored, "Initial Stored is None");

        var animal = TestLibFunctions.CreateAnimal("Tiger", "Growl");
        holder.Stored = animal;
        var afterSet = holder.Stored;
        AssertNotNull(afterSet, "Stored is Some after setter");
        AssertEqual("Tiger", afterSet!.Name, "Setter round-trips animal's Name");

        holder.Stored = null;
        AssertNull(holder.Stored, "Stored is None after setting to null");
        TestLogger.Info("OptionalGenericHolder<Animal>.Stored setter round-trip passed");
    }

    public void TestOptionalGenericHolderPeekSome()
    {
        // peek() is the function (vs property accessor) that previously emitted a broken
        // P/Invoke (no SwiftIndirectResult arg) plus an inline byte-read of uninitialized
        // memory. Both fixes — sret signature and SwiftMarshal-based readback — must hold.
        var animal = TestLibFunctions.CreateAnimal("Eagle", "Screech");
        using var holder = new OptionalGenericHolder<Animal>(animal);
        var peeked = holder.GetPeek();
        AssertNotNull(peeked, "GetPeek returns Some(animal)");
        AssertEqual("Eagle", peeked!.Name, "Peeked animal's Name preserved");
        AssertEqual("Screech", peeked.Sound, "Peeked animal's Sound preserved");
        TestLogger.Info($"OptionalGenericHolder<Animal>(Eagle).GetPeek().Name = \"{peeked.Name}\"");
    }

    public void TestOptionalGenericHolderPeekNone()
    {
        using var holder = new OptionalGenericHolder<Animal>(null);
        var peeked = holder.GetPeek();
        AssertNull(peeked, "GetPeek returns None for null-constructed holder");
        TestLogger.Info("OptionalGenericHolder<Animal>(null).GetPeek() = null");
    }

    public void TestOptionalGenericHolderLargeStructStored()
    {
        // 48-byte struct payload — exceeds the previous fixed 16-byte ExistentialContainer1
        // buffer. Stored_Get must allocate a buffer sized to TValue's runtime metadata and
        // place hasValuePtr at offset Size; otherwise the trailing field overflows into the
        // discriminant byte and the read appears to return None.
        // Note: TValue=struct under `where TValue : ISwiftObject` means TValue? collapses to
        // TValue at the C# type level (no Nullable wrapping for unconstrained-T?), so the
        // null-path is not expressible through the public API for value-type T. The Some
        // round-trip is what exercises the buffer-size fix.
        var value = new LargeValueStruct(1, 2, 3, 4, 5, 6);
        using var holder = new OptionalGenericHolder<LargeValueStruct>(value);
        var stored = holder.Stored;
        AssertEqual(1L, stored.A, "field a preserved");
        AssertEqual(2L, stored.B, "field b preserved");
        AssertEqual(3L, stored.C, "field c preserved");
        AssertEqual(4L, stored.D, "field d preserved");
        AssertEqual(5L, stored.E, "field e preserved");
        AssertEqual(6L, stored.F, "field f preserved");

        holder.Stored = new LargeValueStruct(10, 20, 30, 40, 50, 60);
        var after = holder.Stored;
        AssertEqual(10L, after.A, "setter round-trip field a");
        AssertEqual(20L, after.B, "setter round-trip field b");
        AssertEqual(60L, after.F, "setter round-trip field f");
        TestLogger.Info("OptionalGenericHolder<LargeValueStruct> Stored round-trip preserved all 6 Int64 fields");
    }

    // CallConvSwift entry point on this path: $s20SwiftBindingsTestLib21OptionalGenericHolderVMa
    [SkipOnMonoJit("Mono JIT crashes resolving Optional<LargeStruct> generic metadata in GetPeek readback (upstream Issue 1 — !ji->async at jit-info.c:918). The OptionalGenericHolder<T> type-metadata accessor is CallConvSwift (PInvoke_getMetadata: $s20SwiftBindingsTestLib21OptionalGenericHolderVMa), resolved during generic construction. Mono-only (Simulator + Catalyst); runs on macOS (CoreCLR) and under NativeAOT on device. CallConvSwift entry: $s20SwiftBindingsTestLib21OptionalGenericHolderVMa")]
    public void TestOptionalGenericHolderLargeStructPeek()
    {
        // GetPeek hits the SwiftOptional<TValue> readback path for a 48-byte payload —
        // independent of Stored_Get's decomposed buffer, but still must not assume a
        // 16-byte fixed inner size.
        var value = new LargeValueStruct(7, 14, 21, 28, 35, 42);
        using var holder = new OptionalGenericHolder<LargeValueStruct>(value);
        var peeked = holder.GetPeek();
        AssertEqual(7L, peeked.A, "GetPeek field a preserved");
        AssertEqual(14L, peeked.B, "GetPeek field b preserved");
        AssertEqual(42L, peeked.F, "GetPeek field f preserved");
        TestLogger.Info("OptionalGenericHolder<LargeValueStruct>.GetPeek round-trip preserved");
    }

    public void TestFirstNamedAnimalSome()
    {
        // Pins the Optional<(String, Class)> per-element decomposition fix.
        // Tuple stores PInvokeType for each element so the IntPtr field for the
        // class must be lifted via SwiftMarshal.MarshalFromSwiftObject<Animal>,
        // and the SwiftString field via .ToString(). Before the fix the generated
        // code attempted ((string, Animal)?)_swiftOpt.Some which is a CS0030.
        var cat = TestLibFunctions.CreateAnimal("Cat", "Meow");
        var dog = TestLibFunctions.CreateAnimal("Dog", "Woof");
        var result = TestLibFunctions.FirstNamedAnimal(new[] { cat, dog });
        AssertTrue(result.HasValue, "FirstNamedAnimal returns Some for non-empty array");
        var (label, animal) = result!.Value;
        AssertEqual("primary:Cat", label, "Tuple Item1 (String) lifted via .ToString()");
        AssertNotNull(animal, "Tuple Item2 (Animal) lifted via MarshalFromSwiftObject");
        AssertEqual("Cat", animal.Name.ToString(), "Animal class instance round-tripped through tuple field");
        TestLogger.Info($"FirstNamedAnimal Some => label=\"{label}\", animal.Name=\"{animal.Name}\"");
    }

    public void TestFirstNamedAnimalNone()
    {
        var result = TestLibFunctions.FirstNamedAnimal(Array.Empty<Animal>());
        AssertFalse(result.HasValue, "FirstNamedAnimal returns None for empty array");
        TestLogger.Info("FirstNamedAnimal([]) = null");
    }

    #endregion

    #region Tier 3 — Direct-path Optional ABI width

    // Optionals reaching the direct CallConvSwift path on a wrapper-ineligible parent.
    //
    // The emitter's preferred route for a wide Optional is a Swift wrapper with an out-buffer
    // parameter, but that route needs the member to be wrapper-eligible. A generic parent is one
    // of the conditions that makes it ineligible, so GenericOptionalAbiBox<T>'s members take the
    // direct fallback — historically a P/Invoke declaring the Optional as a single IntPtr, whose
    // result was then read back at the type metadata's full width. For a 16-byte
    // Optional<String> that read 8 real bytes plus 8 bytes of adjacent stack memory, and since
    // that Optional carries no separate tag byte, the never-transferred bytes were exactly the
    // ones deciding nil. It produced no error on either runtime — just a nil that decoded as a
    // garbage value, and differently under Mono JIT than under NativeAOT.
    //
    // These members now declare a slot as wide as the value Swift actually passes — a blittable
    // carrier struct collecting every register involved — so the assertions below are that the
    // values arrive INTACT, in both directions and for both Some and None.
    //
    // Asserting the value rather than mere absence of a crash is the whole point: the defect
    // never crashed. It produced a plausible answer built partly from adjacent stack memory, so
    // "it returned something" and "it returned the right thing" are exactly the two outcomes that
    // have to be told apart. None is the load-bearing case for Optional<String>, which carries no
    // tag byte: nil-ness is decided by bytes that a single-slot call never transferred.

    public void TestDirectPathOptionalStringReturnRoundTrips()
    {
        using var some = new GenericOptionalAbiBox<Animal>(7);
        AssertEqual("boxed-7", some.GetLabel(), "Optional<String> Some return arrives intact");

        using var none = new GenericOptionalAbiBox<Animal>(-1);
        AssertNull(none.GetLabel(), "Optional<String> None return reads as null, not as garbage");
        TestLogger.Info("GenericOptionalAbiBox<Animal>.GetLabel round-trips Some and None");
    }

    public void TestDirectPathOptionalStringParamRoundTrips()
    {
        using var box = new GenericOptionalAbiBox<Animal>(1);
        AssertEqual(3, box.Width("abc"),
            "Optional<String> Some parameter reaches Swift with both of its words");
        AssertEqual(-1, box.Width(null),
            "Optional<String> None parameter is seen as nil by Swift");
        TestLogger.Info("GenericOptionalAbiBox<Animal>.Width round-trips Some and None");
    }

    public void TestDirectPathOptionalDoubleReturnRoundTrips()
    {
        // Optional<Double> is 9 bytes — an 8-byte payload plus a separate tag byte, because Double
        // has no spare bits to steal. A single-word return captured the payload and dropped the
        // tag, so None was indistinguishable from Some(0)-shaped garbage.
        //
        // Both words are INTEGER registers even though the payload is floating-point: Swift lowers
        // an enum payload as opaque integer storage, so the value travels in x0 and the callee
        // moves it out with `fmov d0, x0`. The carrier's fields are integer-typed for exactly this
        // reason, and a Some assertion on a value with a fraction is what would catch a carrier
        // that had been "helpfully" redeclared with a double field.
        using var some = new GenericOptionalAbiBox<Animal>(7);
        var timestamp = some.GetTimestamp();
        AssertTrue(timestamp.HasValue, "Optional<Double> Some return is not null");
        AssertApproxEqual(7.25, timestamp!.Value, 0.0001,
            "Optional<Double> Some return arrives with its payload intact");

        using var none = new GenericOptionalAbiBox<Animal>(-1);
        AssertFalse(none.GetTimestamp().HasValue,
            "Optional<Double> None return reads as null, not as a dropped tag byte");
        TestLogger.Info("GenericOptionalAbiBox<Animal>.GetTimestamp round-trips Some and None");
    }

    // The controls. Optional<[String]> is ONE machine word — Array is a single refcounted
    // pointer and nil is its null extra inhabitant — so the single-slot direct call was always
    // correct for it. The routing predicate that selects the wrapper nonetheless calls it
    // "large", so a fail-closed gate keyed on that predicate rather than on real width would
    // tombstone these too. They must keep binding AND keep returning right answers; if either
    // starts throwing, the gate has become over-broad and is destroying working surface.

    public void TestDirectPathSingleWordOptionalReturnSome()
    {
        using var box = new GenericOptionalAbiBox<Animal>(7);
        var names = box.GetNames();
        AssertNotNull(names, "Optional<[String]> return still binds on the direct path");
        AssertEqual(1, names!.Count, "Optional<[String]> Some carries its element");
        AssertEqual("g-7", names[0], "Optional<[String]> element round-trips intact");
        TestLogger.Info($"GenericOptionalAbiBox<Animal>(7).GetNames() = [\"{names[0]}\"]");
    }

    public void TestDirectPathSingleWordOptionalReturnNone()
    {
        // The nil half of the control, and the more important one: nil-ness for a single-word
        // Optional rides on the null extra inhabitant in the one word that IS transferred, so
        // this reads correctly on the direct path and must keep doing so.
        using var box = new GenericOptionalAbiBox<Animal>(-1);
        var names = box.GetNames();
        AssertNull(names, "Optional<[String]> None reads as null on the direct path");
        TestLogger.Info("GenericOptionalAbiBox<Animal>(-1).GetNames() = null");
    }

    public void TestDirectPathSingleWordOptionalParamRoundTrips()
    {
        // The parameter-side half of the over-breadth control. The return side and the parameter
        // side are separate arms of the floor, so each needs its own live canary — and unlike the
        // return side, the parameter side had none. Optional<[String]> is one machine word, so
        // the floor leaves it live; this asserts the surviving call actually answers correctly
        // rather than merely compiling. (Disabling the parameter arm and calling the 16-byte
        // String? sibling SIGSEGVs on the first call, so this arm is load-bearing, not
        // precautionary — which makes a live control on the other side of the split necessary.)
        using var box = new GenericOptionalAbiBox<Animal>(1);
        AssertEqual(2, box.NameCount(new List<string> { "a", "b" }),
            "Optional<[String]> parameter Some carries its element count into Swift");
        AssertEqual(-1, box.NameCount(null),
            "Optional<[String]> parameter None is seen as nil by Swift");
        TestLogger.Info("GenericOptionalAbiBox<Animal>.NameCount round-trips Some and None");
    }

    public void TestDirectPathGenericPayloadOptionalParamRoundTrips()
    {
        // Optional of the parent's own generic parameter. The caller has no static size for it,
        // so Swift takes the argument indirectly and the emitter passes the buffer address — a
        // carrier for the whole value, whatever its width. Nothing is truncated, so the floor
        // must not fire, and this asserts the call both survives and answers correctly. Without
        // this control the floor silently tombstones every `T?` argument on a wrapper-ineligible
        // generic parent, which is working surface.
        using var box = new GenericOptionalAbiBox<Animal>(1);
        AssertFalse(box.TagIsPresent(null), "Optional<T> parameter None is seen as nil by Swift");
        using var cat = TestLibFunctions.CreateAnimal("Cat", "Meow");
        AssertTrue(box.TagIsPresent(cat), "Optional<T> parameter Some is seen as non-nil by Swift");
        TestLogger.Info("GenericOptionalAbiBox<Animal>.TagIsPresent round-trips Some and None");
    }

#pragma warning disable SB0009 // Tombstoned by the ABI floor — throwing is the behavior under test.

    public void TestDirectPathSingleWordPointerOptionalParamIsRefused()
    {
        // The counter-example to "one word means it fits". OpaquePointer? measures 8 bytes, the
        // same as the [String]? argument that round-trips fine, yet calling it with the floor
        // lifted SIGSEGVs on the first call. Width is necessary but not sufficient for the direct
        // argument slot, so this must stay refused; the assertion exists so that a future attempt
        // to classify nullable pointers as SingleWord on the strength of their measured size
        // fails here instead of shipping a crash.
        using var box = new GenericOptionalAbiBox<Animal>(1);
        AssertThrows<NotSupportedException>(
            () => box.OpaqueWidth(null),
            "Optional<OpaquePointer> parameter on the direct path throws instead of crashing");
        TestLogger.Info("GenericOptionalAbiBox<Animal>.OpaqueWidth threw NotSupportedException");
    }

#pragma warning restore SB0009

    public void TestDirectPathOptionalStringPropertyRoundTrips()
    {
        // A `public var x: String?` truncates exactly like the equivalent method — same direct
        // call, same 16-byte return — so the carrier has to reach accessors too. It is also the
        // most ordinary shape a Swift API has, which makes it the likeliest way a consumer meets
        // this defect. The value has to arrive THROUGH the public property: the call lives on the
        // private synthesized accessor, so a property that failed to delegate correctly would
        // still read a truncated value behind an innocent-looking read.
        using var some = new GenericOptionalAbiBox<Animal>(7);
        AssertEqual("prop-7", some.BoxedLabel, "Optional<String> property Some arrives intact");

        using var none = new GenericOptionalAbiBox<Animal>(-1);
        AssertNull(none.BoxedLabel, "Optional<String> property None reads as null");
        TestLogger.Info("GenericOptionalAbiBox<Animal>.BoxedLabel round-trips Some and None");
    }

    public void TestDirectPathSingleWordOptionalPropertyStillWorks()
    {
        // Accessor-side over-breadth control, the property counterpart of GetNames(). A
        // single-word Optional through a property getter is ABI-correct on the direct path and
        // must keep binding — if the accessor arm of the floor ever widens to "any Optional on an
        // accessor", this read is what stops compiling or starts throwing.
        using var box = new GenericOptionalAbiBox<Animal>(7);
        var names = box.BoxedNames;
        AssertNotNull(names, "Optional<[String]> property Some is not null");
        AssertEqual(1, names!.Count, "Optional<[String]> property Some has one element");
        AssertEqual("p-7", names[0], "Optional<[String]> property Some element round-trips");

        using var noneBox = new GenericOptionalAbiBox<Animal>(-1);
        AssertNull(noneBox.BoxedNames, "Optional<[String]> property None reads as null");
        TestLogger.Info("GenericOptionalAbiBox<Animal> BoxedNames round-trips Some and None");
    }

#pragma warning disable SB0009 // Tombstoned by the ABI floor — throwing is the behavior under test.

    public void TestDirectPathBridgedValueTypeOptionalIsRefused()
    {
        // Foundation.URL is a Swift *struct* that bridges to NSURL only at an ObjC boundary. The
        // direct CallConvSwift path has no such boundary, so the value arrives in its native
        // 16-byte Swift layout — but the routing predicates classify it as a reference, which is
        // why this shape slipped past a floor keyed on those predicates instead of on physical
        // width. What used to be emitted was worse than truncation: the first word of a half-read
        // struct handed to GetINativeObject(..., owns: true), i.e. reinterpreted as an ObjC object
        // AND released. This must fail closed on both runtimes.
        using var box = new GenericOptionalAbiBox<Animal>(-1);
        AssertThrows<NotSupportedException>(
            () => { _ = box.GetBoxedUrl(); },
            "Optional<URL> on the direct path throws instead of projecting half a struct as NSURL");
        TestLogger.Info("GenericOptionalAbiBox<Animal>(-1).GetBoxedUrl() threw NotSupportedException");
    }

#pragma warning restore SB0009

    // The internal-parent half of the same story. GenericOptionalAbiBox is wrapper-INELIGIBLE
    // because its parent is generic; InternalOptionalAbiHost is wrapper-IMPOSSIBLE because its
    // parent is `@usableFromInline internal` and a wrapper body could not name it. Both land on
    // the direct call, but only the second has no other route it could ever fall back to, which
    // is what makes it worth exercising separately rather than assuming the first covers it.

    public void TestInternalParentOptionalStringReturnRoundTrips()
    {
        AssertEqual("label-7", InternalOptionalAbiHost.Label(7),
            "Optional<String> Some return arrives intact from an internal parent");
        AssertNull(InternalOptionalAbiHost.Label(-1),
            "Optional<String> None return reads as null from an internal parent");
        TestLogger.Info("InternalOptionalAbiHost.Label round-trips Some and None");
    }

    public void TestInternalParentOptionalStringReturnCoversBothStorageForms()
    {
        // A Swift String stores short contents inline across both words and long contents as a
        // pointer to a heap buffer in one of them. The two forms exercise different halves of the
        // 16 bytes, so a carrier that transferred only one word could still look correct on one
        // form. The empty string is the sharpest case of all: it is a Some that a truncating read
        // reported as nil on one runtime and as "" on the other.
        AssertEqual("", InternalOptionalAbiHost.GetEmptyLabel(),
            "empty-string Some is distinguishable from None");

        var expectedLong = string.Concat(Enumerable.Repeat("swift-optional-abi-", 8));
        AssertEqual(expectedLong, InternalOptionalAbiHost.GetLongLabel(),
            "heap-allocated (non-small-form) String Some arrives intact");
        TestLogger.Info("InternalOptionalAbiHost covers both String storage forms");
    }

    public void TestInternalParentOptionalDoubleReturnRoundTrips()
    {
        var some = InternalOptionalAbiHost.Timestamp(7);
        AssertTrue(some.HasValue, "Optional<Double> Some return from an internal parent is not null");
        AssertApproxEqual(7.5, some!.Value, 0.0001,
            "Optional<Double> Some return carries its payload past the tag byte");

        AssertFalse(InternalOptionalAbiHost.Timestamp(-1).HasValue,
            "Optional<Double> None return reads as null from an internal parent");
        TestLogger.Info("InternalOptionalAbiHost.Timestamp round-trips Some and None");
    }

    public void TestInternalParentOptionalStringParamRoundTrips()
    {
        AssertEqual(5, InternalOptionalAbiHost.LabelWidth("hello"),
            "Optional<String> Some parameter reaches Swift with both of its words");
        AssertEqual(-1, InternalOptionalAbiHost.LabelWidth(null),
            "Optional<String> None parameter is seen as nil by Swift");
        TestLogger.Info("InternalOptionalAbiHost.LabelWidth round-trips Some and None");
    }

    public void TestInternalParentOptionalStringEchoRoundTrips()
    {
        // Both directions in a single call, so a parameter-side and a return-side carrier cannot
        // pass independently while disagreeing with each other.
        AssertEqual("hello!", InternalOptionalAbiHost.EchoLabel("hello"),
            "Optional<String> survives a round trip through both directions at once");
        AssertNull(InternalOptionalAbiHost.EchoLabel(null),
            "Optional<String> None round-trips as None through both directions");
        TestLogger.Info("InternalOptionalAbiHost.EchoLabel round-trips Some and None");
    }

    public void TestInternalParentOptionalDoubleParamRoundTrips()
    {
        AssertEqual(7, InternalOptionalAbiHost.TimestampWhole(7.5),
            "Optional<Double> Some parameter reaches Swift with payload and tag");
        AssertEqual(-1, InternalOptionalAbiHost.TimestampWhole(null),
            "Optional<Double> None parameter is seen as nil by Swift");
        TestLogger.Info("InternalOptionalAbiHost.TimestampWhole round-trips Some and None");
    }

    public void TestInternalParentSingleWordOptionalStillBinds()
    {
        // The over-breadth controls on the internal-parent path, both directions. One machine word
        // fits the slot a direct call already gives it, so neither of the two floors guarding this
        // path may refuse them. The parameter side is the one that regressed: the blittability
        // test behind the second floor counted any Optional argument as a SafeHandle-marshalled
        // container regardless of width, and tombstoned a call that was already well-formed.
        var names = InternalOptionalAbiHost.Names(7);
        AssertNotNull(names, "Optional<[String]> Some return still binds on an internal parent");
        AssertEqual(2, names!.Count, "Optional<[String]> Some return carries its elements");
        AssertEqual("a-7", names[0], "Optional<[String]> element round-trips intact");
        AssertNull(InternalOptionalAbiHost.Names(-1), "Optional<[String]> None return reads as null");

        AssertEqual(2, InternalOptionalAbiHost.NameCount(new List<string> { "a", "b" }),
            "Optional<[String]> Some parameter still binds and carries its element count");
        AssertEqual(-1, InternalOptionalAbiHost.NameCount(null),
            "Optional<[String]> None parameter is seen as nil by Swift");
        TestLogger.Info("InternalOptionalAbiHost single-word Optionals bind in both directions");
    }

    public void TestInternalParentOptionalStringPropertySetterRoundTrips()
    {
        // The setter side of the carrier, which is a separate emission path from everything above:
        // accessor bodies are built without the parameter rewrite method bodies get, so a setter
        // can end up handing Swift a buffer address where the P/Invoke declares two payload words.
        // The property is stored on the Swift side, so reading it back asserts what Swift actually
        // received rather than something the accessor could have reconstructed.
        InternalOptionalAbiHost.StoredLabel = "stored-label";
        AssertEqual("stored-label", InternalOptionalAbiHost.StoredLabel,
            "Optional<String> Some survives a setter/getter round trip on an internal parent");

        InternalOptionalAbiHost.StoredLabel = null;
        AssertNull(InternalOptionalAbiHost.StoredLabel,
            "Optional<String> None survives a setter/getter round trip");

        // Empty-string Some is the discriminating case: with no tag byte, nil-ness is decided by
        // reading all sixteen bytes, so a setter that wrote only one word could leave "" and nil
        // indistinguishable.
        InternalOptionalAbiHost.StoredLabel = "";
        AssertEqual("", InternalOptionalAbiHost.StoredLabel,
            "empty-string Some stays distinct from None through the setter");

        // Every value above is a small-form Swift String — 15 or fewer UTF-8 bytes live inline in
        // the two words and are never heap-allocated, so they exercise no refcounting at all and
        // cannot observe an ownership error. Swift lowers a property setter as
        // `(@owned Optional<String>, ...) -> ()`: the callee takes over a +1. A payload wide enough
        // to be heap-allocated is what makes that observable — if the setter handed Swift a value
        // the C# side still owns and later value-witness-destroys, the stored string would be
        // released twice and the read-back below would see freed memory. Repeating it re-enters the
        // setter against a *previously stored* value, so the assign's release of the old string is
        // exercised too, and draining finalizers forces any pending destroy to run while Swift's
        // global still holds the string.
        const string heapPayload = "stored-label-long-enough-to-be-heap-allocated";
        for (int i = 0; i < 200; i++)
        {
            InternalOptionalAbiHost.StoredLabel = heapPayload + i;
            AssertEqual(heapPayload + i, InternalOptionalAbiHost.StoredLabel,
                "heap-allocated Optional<String> Some survives the setter round trip");
        }

        DrainFinalizers();
        AssertEqual(heapPayload + "199", InternalOptionalAbiHost.StoredLabel,
            "the last stored heap payload is still readable after finalizers run");

        InternalOptionalAbiHost.StoredLabel = null;
        TestLogger.Info("InternalOptionalAbiHost.StoredLabel round-trips through the setter");
    }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

#pragma warning disable SB0009 // Tombstoned by the ABI floor — throwing is the behavior under test.

    public void TestInternalParentInOutOptionalIsRefused()
    {
        // `inout` is passed as the address of the caller's storage and written back through, which
        // is the opposite shape from a carrier holding a copy in registers. Emitting the carrier
        // anyway would send Swift payload bytes to dereference as a pointer and drop its write-back
        // on the floor, so this member has to stay refused even though the very same Optional is
        // callable by value. That distinction is the whole assertion: a future change that decides
        // "a wide Optional has a carrier, therefore the member is callable" fails here rather than
        // shipping a corrupting write.
        string? label = "seed";
        AssertThrows<NotSupportedException>(
            () => InternalOptionalAbiHost.SwapLabel(ref label),
            "inout Optional<String> on the direct path throws instead of passing a copy by value");
        TestLogger.Info("InternalOptionalAbiHost.SwapLabel threw NotSupportedException");
    }

    public void TestInternalParentInOutSingleWordOptionalIsRefused()
    {
        // The one-word sibling of the test above, and the more interesting of the two. A `[String]?`
        // needs no carrier at all — it fits the direct slot by value, and as a by-value argument it
        // is genuinely callable — so a refusal keyed on width alone lets this one through and emits
        // a by-value call for a parameter Swift reads as an address. What arrives is the array's own
        // storage pointer, offered to Swift as the variable to overwrite.
        IReadOnlyList<string>? names = new[] { "seed" };
        AssertThrows<NotSupportedException>(
            () => InternalOptionalAbiHost.SwapNames(ref names),
            "inout Optional<Array> on the direct path throws rather than passing storage by value");
        TestLogger.Info("InternalOptionalAbiHost.SwapNames threw NotSupportedException");
    }

    public void TestDirectPathBridgedElementContainerIsRefused()
    {
        // The refusal that is NOT about width. `[URL]?` is one refcounted storage pointer, so the
        // direct slot is exactly the right size — and that is what makes it dangerous. The C# side
        // renders a container whose elements bridge as an NSArray, because the conversion asks what
        // the payload bridges TO without asking whether there is a boundary to bridge AT; on this
        // path there is none, so Swift would read a Foundation object where its own array storage
        // belongs. The getter is the worse half: it reads Swift's storage pointer back through
        // ArrayFromHandleFunc as an NSArray and takes ownership of an object that never existed.
        //
        // Both accessors are asserted because they fail independently — a floor that reached only
        // the setter would leave a property that reads garbage and releases it.
        using var box = new GenericOptionalAbiBox<Animal>(1);
        AssertThrows<NotSupportedException>(
            () => { var _ = box.BridgedUrls; },
            "Optional<Array<URL>> getter on the direct path throws instead of reading a Foundation object");
        AssertThrows<NotSupportedException>(
            () => box.BridgedUrls = null,
            "Optional<Array<URL>> setter on the direct path throws instead of handing over an NSArray");
        TestLogger.Info("GenericOptionalAbiBox<Animal>.BridgedUrls threw NotSupportedException on both accessors");
    }

    public void TestInternalParentBridgedElementContainerIsRefused()
    {
        // The same refusal on the internal-parent twin. The two parents reach a wrapper-ineligible
        // direct path by different routes — the box because its shape declines the wrapper, the host
        // because an internal parent cannot have one named — so a regression confined to either
        // route would leave the other passing. Static because the host's members are static.
        AssertThrows<NotSupportedException>(
            () => { var _ = InternalOptionalAbiHost.StoredBridgedUrls; },
            "Optional<Array<URL>> getter on an internal parent throws rather than reading a Foundation object");
        AssertThrows<NotSupportedException>(
            () => InternalOptionalAbiHost.StoredBridgedUrls = null,
            "Optional<Array<URL>> setter on an internal parent throws rather than handing over an NSArray");
        TestLogger.Info("InternalOptionalAbiHost.StoredBridgedUrls threw NotSupportedException on both accessors");
    }

#pragma warning restore SB0009

    public void TestInternalParentOptionalObjCClassSetterBalancesOwnership()
    {
        // The internal-parent twin of the ownership fix below. Same reasoning, different emission
        // route into the direct path; asserting only the public-parent shape would let the internal
        // one regress silently back to handing over a borrowed pointer.
        var kept = new Foundation.NSObject();
        for (int i = 0; i < 200; i++)
        {
            InternalOptionalAbiHost.StoredBridgedObject = kept;
        }

        DrainFinalizers();
        AssertFalse(kept.Handle == IntPtr.Zero, "the assigned object is still alive after 200 setter calls");
        AssertNotNull(kept.Description, "the surviving object is still usable, not a freed allocation");

        InternalOptionalAbiHost.StoredBridgedObject = null;
        kept.Dispose();
        TestLogger.Info("InternalOptionalAbiHost.StoredBridgedObject survived 200 setter round-trips");
    }

    public void TestDirectPathOptionalObjCClassSetterBalancesOwnership()
    {
        // The sibling shape that stays callable, and the reason it needed a fix rather than a
        // refusal. For a class reference the object pointer IS Swift's representation, so nothing
        // is mis-read — but the pointer the property conversion produces is borrowed off a managed
        // wrapper that goes on owning the object, while Swift lowers a setter's new value as
        // @owned and releases it. One release too many, from a callee that was never handed the
        // retain its convention says it receives.
        //
        // The imbalance is invisible to a value assertion: nothing reads wrong, the object simply
        // dies early and the fault lands on whoever touches it next — usually a finalizer, in some
        // other test. Holding a reference across the assignments and using it afterwards is what
        // turns that into a local, deterministic failure. NSObject is refcounted for real, so every
        // iteration is an actual ARC operation rather than an inline no-op.
        using var box = new GenericOptionalAbiBox<Animal>(1);
        var kept = new Foundation.NSObject();

        for (int i = 0; i < 200; i++)
            box.BridgedObject = kept;

        DrainFinalizers();

        AssertFalse(kept.Handle == IntPtr.Zero, "the assigned object is still alive after 200 setter calls");
        AssertNotNull(kept.Description, "the surviving object is still usable, not a freed allocation");

        box.BridgedObject = null;
        kept.Dispose();
        TestLogger.Info("GenericOptionalAbiBox<Animal>.BridgedObject balanced ARC across 200 assignments");
    }

    public void TestInternalParentSingleWordOptionalPropertyRoundTrips()
    {
        // A settable one-word Optional takes no carrier, so it reaches the accessor's residual
        // marshalling arm, which passes the Optional's own value. Handing over the address of the
        // C# payload buffer instead is right only where a Swift shim dereferences it. The nil leg
        // is what makes the difference observable: a buffer address is never zero, so a nil written
        // through a pointer-passing setter comes back as a present array whose contents are
        // whatever the buffer happened to hold.
        InternalOptionalAbiHost.StoredNames = null;
        AssertNull(InternalOptionalAbiHost.StoredNames,
            "nil survives the setter instead of arriving as a present value");

        InternalOptionalAbiHost.StoredNames = new[] { "alpha", "beta" };
        var stored = InternalOptionalAbiHost.StoredNames;
        AssertNotNull(stored, "a present Optional<Array> survives the setter");
        AssertEqual(2, stored!.Count, "the stored array keeps its element count");
        AssertEqual("alpha", stored[0], "the stored array keeps its first element");
        AssertEqual("beta", stored[1], "the stored array keeps its second element");

        // Round-trip back to nil so the None case is exercised in both directions, and so the
        // fixture's static storage does not leak its payload into whatever test runs next.
        InternalOptionalAbiHost.StoredNames = null;
        AssertNull(InternalOptionalAbiHost.StoredNames,
            "the property returns to nil after holding a value");
        TestLogger.Info("InternalOptionalAbiHost.StoredNames round-trips through the setter");
    }

    /// <summary>
    /// The ownership half of the residual single-word setter arm. Swift lowers this setter as
    /// <c>(@owned Value, @guaranteed self) -&gt; ()</c>, so the callee releases the array it is
    /// handed; leaving the .NET payload's Destroy in place as well releases the same storage twice.
    /// <para>
    /// The value legs above cannot observe that — a correct read happens before either release, and
    /// the second release lands later on the finalizer thread, so the damage surfaces as a SIGSEGV
    /// inside an unrelated test that happens to drain finalizers next. Repeating the assignment
    /// builds a queue of payloads and draining it here forces the fault into this test instead.
    /// An array's storage is always heap-allocated and refcounted, so unlike a small-form String
    /// every iteration is a real ARC operation.
    /// </para>
    /// </summary>
    public void TestInternalParentSingleWordOptionalSetterBalancesOwnership()
    {
        DrainFinalizers();

        for (int i = 0; i < 200; i++)
        {
            InternalOptionalAbiHost.StoredNames = new[] { "owner-" + i, "second-" + i };
            var round = InternalOptionalAbiHost.StoredNames;
            AssertNotNull(round, "the assigned array survives iteration " + i);
            AssertEqual("owner-" + i, round![0], "the stored array keeps its first element");
        }

        DrainFinalizers();
        AssertEqual("owner-199", InternalOptionalAbiHost.StoredNames![0],
            "the last assignment is still readable after the payloads finalize");

        InternalOptionalAbiHost.StoredNames = null;
        TestLogger.Info("InternalOptionalAbiHost.StoredNames balanced ARC across 200 heap-backed assignments");
    }

    #endregion
}
