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
        // This reproduces the DeviceKit Device.name crash (SIGSEGV in InitializeWithCopy).
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

    #region Bug 15a — Optional<typealias-to-primitive>

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

    #region Bug 15b — Optional<generic-param> on generic struct

    // Round-trips Optional<Value> through OptionalGenericHolder<Value>'s constructor,
    // stored property accessors, and peek() method. Exercises the generic-typed indirect
    // result path: PInvokeSignatureBuilder must emit SwiftIndirectResult and the projection
    // must avoid the inline byte-read path (which assumes a trailing discriminant byte
    // and is wrong for class TValue where Optional<T> reuses the pointer's spare bits).
    //
    // [SkipOnSimulator]: the getters/peek() path goes through a raw CallConvSwift P/Invoke
    // (no @_cdecl wrapper — Swift forbids @_cdecl on generic-param-bearing functions, and
    // an unconstrained TValue gives no protocol witness to dispatch through). The
    // (SwiftIndirectResult, IntPtr, SwiftSelf) signature trips Mono's JIT first-call
    // !ji->async assertion (upstream Issue 1). NativeAOT (device) handles this signature
    // shape and is the gate for these cases. Constructor uses (SwiftIndirectResult, IntPtr…
    // IntPtr) without SwiftSelf so it does not hit the same path.

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

    [SkipOnSimulator("Mono JIT crashes resolving Optional<LargeStruct> generic metadata in GetPeek readback (upstream Issue 1 — !ji->async at jit-info.c:918)")]
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

    #endregion
}
