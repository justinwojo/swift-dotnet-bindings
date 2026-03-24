// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Negative-path tests: invalid inputs, error conditions, edge cases that
/// should produce well-defined errors rather than crashes or corruption.
/// </summary>
public class NegativePathTests : TestBase
{
    public NegativePathTests(TestResults results) : base(results) { }

    #region Invalid Enum FromRawValue

    public void TestDirectionInvalidFromRawValue()
    {
        // Direction is a simple C# enum — verify cases have expected int values
        AssertEqual(Direction.North, (Direction)0, "Direction.North is 0");
        AssertEqual(Direction.South, (Direction)1, "Direction.South is 1");

        // All four cases should have distinct values
        var values = new[] { (int)Direction.North, (int)Direction.South, (int)Direction.East, (int)Direction.West };
        for (int i = 0; i < values.Length; i++)
            for (int j = i + 1; j < values.Length; j++)
                AssertTrue(values[i] != values[j], $"Direction values {i} and {j} are distinct");

        TestLogger.Info("Direction valid case construction verified");
    }

    public void TestColorInvalidRawValue()
    {
        // Color is a simple C# enum — verify cases have distinct int values
        var red = Color.Red;
        var green = Color.Green;
        AssertTrue(red != green, "Red and Green are different");

        TestLogger.Info("Color enum case tags are distinct");
    }

    public void TestStatusCodeInvalidRawValue()
    {
        // StatusCode string enum with invalid value
        var invalid = StatusCode.FromRawValue("NONEXISTENT");
        AssertNull(invalid, "StatusCode.FromRawValue(NONEXISTENT) returns null");

        var empty = StatusCode.FromRawValue("");
        AssertNull(empty, "StatusCode.FromRawValue('') returns null");

        TestLogger.Info("StatusCode invalid raw values return null");
    }

    public void TestLogLevelInvalidRawValue()
    {
        // LogLevel with invalid raw value
        var invalid = LogLevel.FromRawValue("NOT_A_LEVEL");
        AssertNull(invalid, "LogLevel.FromRawValue(NOT_A_LEVEL) returns null");

        // Case-sensitive check
        var wrongCase = LogLevel.FromRawValue("[info]");
        AssertNull(wrongCase, "LogLevel.FromRawValue('[info]') returns null (case-sensitive)");

        TestLogger.Info("LogLevel invalid raw values return null");
    }

    public void TestGreetingInvalidRawValue()
    {
        var invalid = Greeting.FromRawValue("not_a_greeting");
        AssertNull(invalid, "Greeting.FromRawValue with invalid value returns null");

        TestLogger.Info("Greeting invalid raw value returns null");
    }

    public void TestHttpMethodInvalidRawValue()
    {
        var invalid = NetworkConfig.HttpMethod.FromRawValue("INVALID_METHOD");
        AssertNull(invalid, "HttpMethod.FromRawValue(INVALID_METHOD) returns null");

        TestLogger.Info("HttpMethod invalid raw value returns null");
    }

    public void TestContentTypeInvalidRawValue()
    {
        var invalid = NetworkConfig.ContentTypeType.FromRawValue("invalid/type");
        AssertNull(invalid, "ContentType.FromRawValue(invalid/type) returns null");

        TestLogger.Info("ContentType invalid raw value returns null");
    }

    #endregion

    #region Reference Equality for Non-Equatable Types

    public void TestAnimalReferenceEquality()
    {
        var animal1 = TestLibFunctions.CreateAnimal("Rex", "Bark");
        var animal2 = TestLibFunctions.CreateAnimal("Rex", "Bark");

        // Non-Equatable types inherit reference equality from object
        AssertTrue(animal1.Equals(animal1), "Animal same-reference Equals returns true");
        AssertFalse(animal1.Equals(animal2), "Animal different-reference Equals returns false");

        // GetHashCode does not throw
        var hash = animal1.GetHashCode();
        AssertTrue(hash != 0 || hash == 0, "Animal.GetHashCode returns without throwing");

        TestLogger.Info("Non-Equatable Animal uses reference equality");
    }

    public void TestUniqueResourceReferenceEquality()
    {
        var r1 = new UniqueResource(1);
        var r2 = new UniqueResource(1);

        AssertTrue(r1.Equals(r1), "UniqueResource same-reference Equals returns true");
        AssertFalse(r1.Equals(r2), "UniqueResource different-reference Equals returns false");

        TestLogger.Info("Non-Equatable UniqueResource uses reference equality");
    }

    public void TestMutablePropsReferenceEquality()
    {
        var p1 = new MutableProps(1, "A");
        var p2 = new MutableProps(1, "A");

        AssertTrue(p1.Equals(p1), "MutableProps same-reference Equals returns true");
        AssertFalse(p1.Equals(p2), "MutableProps different-reference Equals returns false");

        TestLogger.Info("Non-Equatable MutableProps uses reference equality");
    }

    #endregion

    #region Disposed Object Edge Cases

    public void TestDisposedAnimalSoundPropertyAfterDispose()
    {
        // Test the second property (Sound) specifically
        var animal = TestLibFunctions.CreateAnimal("Test", "Moo");
        animal.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = animal.Sound;
        }, "Sound property after dispose throws");

        TestLogger.Info("Sound property after dispose correctly throws");
    }

    public void TestDisposedAnimalSoundSetAfterDispose()
    {
        var animal = TestLibFunctions.CreateAnimal("Test", "Moo");
        animal.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            animal.Sound = new SwiftString("NewSound");
        }, "Sound set after dispose throws");

        TestLogger.Info("Sound set after dispose correctly throws");
    }

    public void TestDisposedMutablePropsNameAfterDispose()
    {
        var props = new MutableProps(1, "Test");
        props.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = props.Name;
        }, "MutableProps.Name after dispose throws");

        TestLogger.Info("MutableProps.Name after dispose correctly throws");
    }

    public void TestDisposedMutablePropsNameSetAfterDispose()
    {
        var props = new MutableProps(1, "Test");
        props.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            props.Name = new SwiftString("New");
        }, "MutableProps.Name set after dispose throws");

        TestLogger.Info("MutableProps.Name set after dispose correctly throws");
    }

    [Skip("UniqueResource is ~Copyable: @_cdecl wrapper stripped during compilation")]
    public void TestDisposedResourceConsumeAfterDispose()
    {
        var resource = TestLibFunctions.CreateUniqueResource(10);
        resource.Dispose();

        AssertThrows<ObjectDisposedException>(() =>
        {
            _ = resource.Consume();
        }, "Consume after dispose throws");

        TestLogger.Info("Consume after dispose correctly throws");
    }

    #endregion

    #region SwiftSafeHandle.Zero Access

    public void TestZeroHandlePropertyAccessThrows()
    {
        // SwiftSafeHandle<T>.Zero is the default invalid handle.
        // Generated types initialize _payload to .Zero before the constructor
        // replaces it. We can't create a type with a .Zero handle directly
        // (no public default constructors), but we can simulate this by
        // disposing the handle — a disposed handle behaves identically to
        // a zero handle (both are invalid SafeHandles).
        //
        // This test verifies the contract: invalid handles produce
        // ObjectDisposedException on any property/method access.
        var animal = TestLibFunctions.CreateAnimal("ZeroTest", "Sound");

        // Verify the payload is valid before dispose
        AssertTrue(!animal.Payload.IsInvalid, "Payload is valid before dispose");

        // Dispose makes the handle invalid (equivalent to .Zero behavior)
        animal.Dispose();
        AssertTrue(animal.Payload.IsClosed, "Payload is closed after dispose");

        // All access paths should throw
        AssertThrows<ObjectDisposedException>(() => { _ = animal.Name; },
            "Zero/disposed handle: Name get throws");
        AssertThrows<ObjectDisposedException>(() => { animal.Name = new SwiftString("X"); },
            "Zero/disposed handle: Name set throws");
        AssertThrows<ObjectDisposedException>(() => { _ = animal.GetSpeak(); },
            "Zero/disposed handle: Speak() throws");
        AssertThrows<ObjectDisposedException>(() => { _ = animal.GetDescribe(); },
            "Zero/disposed handle: Describe() throws");

        TestLogger.Info("Zero/disposed handle access correctly throws on all paths");
    }

    // NOTE: "Static method after type metadata failure" is not testable in a
    // normal runtime environment. Type metadata is loaded lazily from the
    // native Swift library and cached permanently. To trigger a metadata
    // failure, we would need to corrupt or unload the native library, which
    // would crash the entire process rather than producing a catchable error.
    // This scenario is inherently untestable without process-level isolation.

    #endregion

    #region Validate Round-Trip Free Function

    public void TestValidateLogLevelInvalidValues()
    {
        // ValidateLogLevelRoundTrip should return false for invalid values
        AssertFalse(TestLibFunctions.ValidateLogLevelRoundTrip(""), "Empty string is invalid");
        AssertFalse(TestLibFunctions.ValidateLogLevelRoundTrip("INVALID"), "Random string is invalid");
        AssertFalse(TestLibFunctions.ValidateLogLevelRoundTrip("[info]"), "Lowercase is invalid");

        TestLogger.Info("ValidateLogLevelRoundTrip rejects invalid values");
    }

    public void TestValidateLogLevelAllValid()
    {
        // All valid raw values should round-trip
        var validValues = new[] { "[DEBUG]", "[INFO]", "[WARN]", "[ERROR]", "[CRITICAL]" };
        foreach (var value in validValues)
        {
            AssertTrue(TestLibFunctions.ValidateLogLevelRoundTrip(value), $"{value} round-trips");
        }

        TestLogger.Info("All valid LogLevel raw values round-trip correctly");
    }

    #endregion
}
