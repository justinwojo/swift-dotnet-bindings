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
    // These members are now refused at emission rather than emitted as a call that cannot work,
    // so the assertions below are that the refusal is real and reaches the caller as an
    // exception. A member that throws is a bug report; a member that answers with stack garbage
    // is a data-corruption incident that reaches production looking like it worked.
    //
    // The nil-seed argument on each is deliberate: nil is the case the truncation decided
    // incorrectly, so it is the case worth naming even though the call no longer reaches Swift.

#pragma warning disable SB0009 // Tombstoned by the ABI floor — throwing is the behavior under test.

    public void TestDirectPathOptionalStringReturnIsRefused()
    {
        using var box = new GenericOptionalAbiBox<Animal>(-1);
        AssertThrows<NotSupportedException>(
            () => box.GetLabel(),
            "Optional<String> return on the direct path throws instead of truncating");
        TestLogger.Info("GenericOptionalAbiBox<Animal>(-1).GetLabel() threw NotSupportedException");
    }

    public void TestDirectPathOptionalStringParamIsRefused()
    {
        using var box = new GenericOptionalAbiBox<Animal>(1);
        AssertThrows<NotSupportedException>(
            () => box.Width(null),
            "Optional<String> parameter on the direct path throws instead of truncating");
        TestLogger.Info("GenericOptionalAbiBox<Animal>(1).Width(null) threw NotSupportedException");
    }

    public void TestDirectPathOptionalDoubleReturnIsRefused()
    {
        // Optional<Double> is 9 bytes — an 8-byte payload plus a tag byte, because Double has no
        // spare bits to steal. A single-word return captured the payload and dropped the tag.
        using var box = new GenericOptionalAbiBox<Animal>(-1);
        AssertThrows<NotSupportedException>(
            () => box.GetTimestamp(),
            "Optional<Double> return on the direct path throws instead of truncating");
        TestLogger.Info("GenericOptionalAbiBox<Animal>(-1).GetTimestamp() threw NotSupportedException");
    }

#pragma warning restore SB0009

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

    public void TestDirectPathOptionalStringPropertyIsRefused()
    {
        // A `public var x: String?` truncates exactly like the equivalent method — same direct
        // call, same 16-byte return — so the floor covers accessors too. It is also the most
        // ordinary shape a Swift API has, which makes it the likeliest way a consumer meets this
        // defect. The refusal has to reach the caller THROUGH the public property: the throwing
        // body lives on the private synthesized accessor, and a property that swallowed it or
        // never delegated would leave the truncation reachable behind an innocent-looking read.
        using var box = new GenericOptionalAbiBox<Animal>(-1);
        AssertThrows<NotSupportedException>(
            () => { _ = box.BoxedLabel; },
            "Optional<String> property getter on the direct path throws instead of truncating");
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

    #endregion
}
